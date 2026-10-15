// Y. The driver's named events. A thread of this probe impersonates the
//    user's medium-integrity token (explorer's, duplicated), so the program
//    that is not elevated is a thread, never a process.
//
//    Y1. On a controller of this SDK, the user cannot open
//        Global\HIDMaestroStopEvent<N> to signal it, or
//        Global\HIDMaestroOutputEvent<N> to wait on it or reset it, and each
//        event's DACL admits SYSTEM, Administrators and LocalService only.
//        The program, and a v1.12.0 program, still open both the way their
//        SDKs do. A v1.12.0 program's controller moved to the installed
//        driver gets the same StopEvent. Its OutputEvent stays the object
//        the earlier driver made, with no DACL, for as long as the program
//        holds it, and the new driver does not take that object. Both
//        programs' input keeps flowing.
//    Y2. The user creates both names, the StopEvent signaled, after a create
//        of an Xbox 360 controller took its transport index and before its
//        device exists. The controller's driver takes neither: the StopEvent
//        stays signaled, and rumble sent through XInput never signals the
//        user's OutputEvent. XInput still reads the controller's input.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

internal static partial class Program
{
    static void SectionY()
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", "v1.12.0", "CoexistPeer.exe")))
        {
            Check("Y: the v1.12.0 peer is built", false, "build CoexistCheck.csproj");
            return;
        }
        IntPtr user = Y.UserToken(out string why);
        Check("Y: the user's medium-integrity token is at hand", user != IntPtr.Zero, why);
        if (user == IntPtr.Zero) return;
        try { SectionYBody(user); }
        finally { Y.CloseHandle(user); }
    }

    /// <summary>Y1 and Y2.</summary>
    static void SectionYBody(IntPtr user)
    {
        // A v1.12.0 program takes the lowest index of its own, so it starts
        // first.
        using var old = PeerProcess.Start("Y-v1.12.0", "v1.12.0");
        string co = old.Send("create pad F1F0 coexist-y-old");
        if (!Ok(co)) { Check("Y: the v1.12.0 program creates", false, co); return; }
        old.Send("stream 3 0.25 125 0");
        string instOld = PeerProcess.Field(co, "instance")!;
        int tOld = int.Parse(PeerProcess.Field(co, "transport")!);

        using var live = PeerProcess.Start("Y-live");
        string cl = live.Send("create pad F1F1 coexist-y-live");
        if (!Ok(cl)) { Check("Y: the program on this SDK creates", false, cl); return; }
        live.Send("stream 4 0.75 125 0");
        string instLive = PeerProcess.Field(cl, "instance")!;
        int tLive = int.Parse(PeerProcess.Field(cl, "transport")!);
        string? installed = Native.DriverVersionOf(instLive);

        // A v1.12.0 program installs its own package on each device it makes
        // (UpdateDriverForPlugAndPlayDevices with its INF). The best driver
        // in the store goes on that device alone, as this SDK's create
        // installs one (DeviceNodeCreator.InstallBestStoreDriver).
        bool moved = Y.InstallBestStoreDriver(instOld)
                     && WaitFor(() => Native.DriverVersionOf(instOld) == installed && Native.IsStarted(instOld), 20_000);
        Check("Y: the v1.12.0 program's controller moves to the installed driver", moved,
              $"{Native.DriverVersionOf(instOld)}, installed {installed}");
        if (!moved) return;

        var pathOld = WaitPaths(0xBEEF, 0xF1F0, 1, 10_000);
        var pathLive = WaitPaths(0xBEEF, 0xF1F1, 1, 10_000);
        using var capOld = pathOld.Count == 1 ? HidCapture.Open(pathOld[0]) : null;
        using var capLive = pathLive.Count == 1 ? HidCapture.Open(pathLive[0]) : null;
        if (capOld == null || capLive == null) { Check("Y: both programs' devices open", false); return; }

        // Y1.
        {
            string stop = $@"Global\HIDMaestroStopEvent{tLive}", output = $@"Global\HIDMaestroOutputEvent{tLive}";
            var asUser = Y.AsUser(user, () => new[]
            {
                Y.OpenError(Y.EVENT_MODIFY_STATE, stop),
                Y.OpenError(Y.SYNCHRONIZE, output),
                Y.OpenError(Y.EVENT_MODIFY_STATE, output),
            });
            Check("Y1: the user cannot open the controller's StopEvent to signal it", asUser[0] == Y.ERROR_ACCESS_DENIED, Y.Win32(asUser[0]));
            Check("Y1: the user cannot open its OutputEvent to wait on it", asUser[1] == Y.ERROR_ACCESS_DENIED, Y.Win32(asUser[1]));
            Check("Y1: the user cannot open its OutputEvent to set or reset it", asUser[2] == Y.ERROR_ACCESS_DENIED, Y.Win32(asUser[2]));
            foreach (string name in new[] { stop, output })
            {
                string? sddl = Y.EventSddl(name);
                Check($"Y1: {name}'s DACL admits SYSTEM, Administrators and LocalService only", Y.HostOnlyDacl(sddl), sddl ?? "cannot read it");
            }
            foreach (var (who, peer) in new[] { ("its program", live), ("a v1.12.0 program", old) })
            {
                string own = peer.Send($"sdkevents {tLive}");
                Check($"Y1: {who} opens and waits on the OutputEvent and signals the StopEvent as its SDK does",
                      PeerProcess.Field(own, "output") == "1" && PeerProcess.Field(own, "wait") is "0" or "258"
                      && PeerProcess.Field(own, "stop") == "1", own);
            }

            string oldStop = $@"Global\HIDMaestroStopEvent{tOld}";
            int oldErr = Y.AsUser(user, () => Y.OpenError(Y.EVENT_MODIFY_STATE, oldStop));
            Check("Y1: the user cannot open the moved v1.12.0 controller's StopEvent to signal it", oldErr == Y.ERROR_ACCESS_DENIED, Y.Win32(oldErr));
            string? oldSddl = Y.EventSddl(oldStop);
            Check($"Y1: {oldStop}'s DACL admits SYSTEM, Administrators and LocalService only", Y.HostOnlyDacl(oldSddl), oldSddl ?? "cannot read it");
            string oldOwn = old.Send($"sdkevents {tOld}");
            Check("Y1: the v1.12.0 program still opens and waits on its OutputEvent and signals its StopEvent",
                  PeerProcess.Field(oldOwn, "output") == "1" && PeerProcess.Field(oldOwn, "wait") is "0" or "258"
                  && PeerProcess.Field(oldOwn, "stop") == "1", oldOwn);
            Check("Y1: the v1.12.0 program's input keeps flowing", !capOld.Broken && Flows(capOld), capOld.Broken ? $"read error {capOld.ReadError}" : "");
            Check("Y1: this SDK's program's input keeps flowing", !capLive.Broken && Flows(capLive), capLive.Broken ? $"read error {capLive.ReadError}" : "");
        }

        SectionYSquat(user);
    }

    /// <summary>Y2. Both event names, made by the user before a create.</summary>
    static void SectionYSquat(IntPtr user)
    {
        // The transport allocator skips an index whose events a program
        // that is not elevated made (SharedMemoryIO.ForeignObjectAt), so the
        // user creates both names once the create holds its index, before
        // the device exists. The StopEvent is made signaled.
        IntPtr stopSquat = IntPtr.Zero, outputSquat = IntPtr.Zero;
        try
        {
            using var p = PeerProcess.Start("Y-squat");
            Check("Y2: the next create is set to wait once it holds its index", Ok(p.Send("pauseindex")));
            p.Post("create xbox - coexist-y-squat");
            string paused = p.Receive("create xbox - coexist-y-squat (waiting)", 60_000);
            if (!paused.StartsWith("OK paused index=", StringComparison.Ordinal))
            {
                Check("Y2: the create waits once it holds its index", false, paused);
                return;
            }
            int t = int.Parse(PeerProcess.Field(paused, "index")!);
            bool made = Y.AsUser(user, () =>
            {
                stopSquat = Y.CreateOpenEvent($@"Global\HIDMaestroStopEvent{t}", manualReset: true, signaled: true, out bool sNew);
                outputSquat = Y.CreateOpenEvent($@"Global\HIDMaestroOutputEvent{t}", manualReset: false, signaled: false, out bool oNew);
                return sNew && oNew;
            });
            Check($"Y2: the user creates both names for transport index {t}", made);
            string cx = p.Send("go");
            if (!Ok(cx)) { Check("Y2: the Xbox 360 controller creates beside the user's names", false, cx); return; }
            if (!made) return;
            p.Send("stream 0 0.95 125 0");
            int slot = -1;
            bool reads = WaitFor(() =>
            {
                for (uint s = 0; s < 4; s++)
                    if (Native.XInputGetState(s, out var st) == 0 && st.Gamepad.sThumbLX > 25000 && (st.Gamepad.wButtons & 0x1000) != 0)
                    { slot = (int)s; return true; }
                return false;
            }, 15_000);
            Check("Y2: XInput reads the controller's input", reads, reads ? $"slot {slot}" : "no XInput slot carries it");
            Check("Y2: the driver did not take the user's StopEvent, which is still signaled",
                  Y.WaitForSingleObject(stopSquat, 0) == Y.WAIT_OBJECT_0);
            if (!reads) return;

            // Rumble through XInput: the XUSB companion publishes each packet
            // and signals the output doorbell it holds.
            int wakes = 0;
            var waiter = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2500)
                    if (Y.WaitForSingleObject(outputSquat, 50) == Y.WAIT_OBJECT_0) wakes++;
            });
            waiter.Start();
            for (int i = 0; i < 40; i++)
            {
                var vib = new Y.XINPUT_VIBRATION { wLeftMotorSpeed = (ushort)(i % 2 == 0 ? 0x4000 : 0), wRightMotorSpeed = (ushort)(i % 2 == 0 ? 0 : 0x4000) };
                Y.XInputSetState((uint)slot, ref vib);
                Thread.Sleep(40);
            }
            var stopVib = new Y.XINPUT_VIBRATION();
            Y.XInputSetState((uint)slot, ref stopVib);
            waiter.Join();
            Check("Y2: rumble never signals the user's OutputEvent", wakes == 0, $"{wakes} wakes");
            p.Send("dispose");
        }
        finally
        {
            if (stopSquat != IntPtr.Zero) Y.CloseHandle(stopSquat);
            if (outputSquat != IntPtr.Zero) Y.CloseHandle(outputSquat);
        }
    }

    /// <summary>Section Y's helpers and the Win32 calls they make.</summary>
    static class Y
    {
        /// <summary>DiInstallDevice with no driver named on one devnode: the
        /// best match the driver store holds, on that device alone.</summary>
        internal static bool InstallBestStoreDriver(string instanceId)
        {
            IntPtr dis = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (dis == new IntPtr(-1)) return false;
            byte[] buf = new byte[IntPtr.Size == 8 ? 32 : 28];
            BitConverter.GetBytes(buf.Length).CopyTo(buf, 0);
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                if (!SetupDiOpenDeviceInfoW(dis, instanceId, IntPtr.Zero, 0, pin.AddrOfPinnedObject())) return false;
                return DiInstallDevice(IntPtr.Zero, dis, pin.AddrOfPinnedObject(), IntPtr.Zero, 0x2 /*DIIDFLAG_NOFINISHINSTALLUI*/, out _);
            }
            finally
            {
                pin.Free();
                SetupDiDestroyDeviceInfoList(dis);
            }
        }

        // ── The user's token, on one thread ────────────────────────────────

        /// <summary>A duplicate of the user's medium-integrity token, taken from
        /// explorer.exe in this session, for impersonation. Zero when there is
        /// none, with the reason.</summary>
        internal static IntPtr UserToken(out string why)
        {
            why = "no explorer.exe at medium integrity in this session";
            int session = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                using (p)
                {
                    if (p.SessionId != session) continue;
                    IntPtr hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (hp == IntPtr.Zero) continue;
                    try
                    {
                        if (!OpenProcessToken(hp, TOKEN_DUPLICATE | TOKEN_QUERY, out IntPtr pt)) continue;
                        try
                        {
                            if (!DuplicateTokenEx(pt, TOKEN_IMPERSONATE | TOKEN_QUERY, IntPtr.Zero, 2 /*SecurityImpersonation*/, 2 /*TokenImpersonation*/, out IntPtr dup))
                                continue;
                            if (IntegrityRid(dup) == 0x2000 && !Elevated(dup))
                            {
                                why = $"explorer.exe pid {p.Id}";
                                return dup;
                            }
                            CloseHandle(dup);
                        }
                        finally { CloseHandle(pt); }
                    }
                    finally { CloseHandle(hp); }
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>Run <paramref name="body"/> on a new thread that impersonates
        /// <paramref name="token"/>, and revert before the thread ends. Handles
        /// it opens stay open: they belong to the process.</summary>
        internal static T AsUser<T>(IntPtr token, Func<T> body)
        {
            T result = default!;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                if (!SetThreadToken(IntPtr.Zero, token)) { error = new Win32Exception(Marshal.GetLastWin32Error(), "SetThreadToken"); return; }
                try { result = body(); }
                catch (Exception e) { error = e; }
                finally { RevertToSelf(); }
            });
            thread.Start();
            thread.Join();
            if (error != null) throw new InvalidOperationException("the impersonating thread failed", error);
            return result;
        }

        /// <summary>The error an OpenEvent with <paramref name="access"/> ends
        /// with, 0 when it opens.</summary>
        internal static int OpenError(uint access, string name)
        {
            IntPtr h = OpenEventW(access, false, name);
            int err = h == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (h != IntPtr.Zero) CloseHandle(h);
            return err;
        }

        internal static string Win32(int err) => err == 0 ? "it opened" : $"Win32 error {err}";

        /// <summary>An event with a NULL DACL, as the driver made its own before
        /// this release. <paramref name="created"/> is false when the name
        /// existed.</summary>
        internal static IntPtr CreateOpenEvent(string name, bool manualReset, bool signaled, out bool created)
        {
            var sd = Marshal.AllocHGlobal(64);
            try
            {
                InitializeSecurityDescriptor(sd, 1);
                SetSecurityDescriptorDacl(sd, true, IntPtr.Zero, false);
                var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
                IntPtr h = CreateEventW(ref sa, manualReset, signaled, name);
                created = h != IntPtr.Zero && Marshal.GetLastWin32Error() != ERROR_ALREADY_EXISTS;
                return h;
            }
            finally { Marshal.FreeHGlobal(sd); }
        }

        /// <summary>The owner and DACL of a named event in SDDL, read from an
        /// elevated handle, or null.</summary>
        internal static string? EventSddl(string name)
        {
            IntPtr h = OpenEventW(READ_CONTROL, false, name);
            if (h == IntPtr.Zero) return null;
            try
            {
                if (GetSecurityInfo(h, 6 /*SE_KERNEL_OBJECT*/, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                                    out _, out _, out _, out _, out IntPtr sd) != 0)
                    return null;
                try
                {
                    if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, 1, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, out IntPtr s, out _))
                        return null;
                    try { return Marshal.PtrToStringUni(s); }
                    finally { LocalFree(s); }
                }
                finally { LocalFree(sd); }
            }
            finally { CloseHandle(h); }
        }

        /// <summary>A present DACL whose every ACE allows SYSTEM, Administrators
        /// or LocalService, each of the three once.</summary>
        internal static bool HostOnlyDacl(string? sddl)
        {
            if (sddl == null) return false;
            int d = sddl.IndexOf("D:", StringComparison.Ordinal);
            if (d < 0 || sddl.Contains("NO_ACCESS_CONTROL", StringComparison.Ordinal)) return false;
            var sids = Regex.Matches(sddl.Substring(d), @"\(([^;]*);[^;]*;[^;]*;[^;]*;[^;]*;([^)]*)\)")
                .Select(m => (Type: m.Groups[1].Value, Sid: m.Groups[2].Value)).ToList();
            return sids.Count == 3 && sids.All(a => a.Type == "A")
                   && sids.Select(a => a.Sid).OrderBy(s => s).SequenceEqual(new[] { "BA", "LS", "SY" });
        }

        static int IntegrityRid(IntPtr token)
        {
            GetTokenInformation(token, 25 /*TokenIntegrityLevel*/, IntPtr.Zero, 0, out int len);
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(token, 25, buf, len, out _)) return -1;
                IntPtr sid = Marshal.ReadIntPtr(buf);
                byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static bool Elevated(IntPtr token)
        {
            IntPtr buf = Marshal.AllocHGlobal(4);
            try { return GetTokenInformation(token, 20 /*TokenElevation*/, buf, 4, out _) && Marshal.ReadInt32(buf) != 0; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        internal const uint EVENT_MODIFY_STATE = 0x0002, SYNCHRONIZE = 0x00100000, READ_CONTROL = 0x00020000;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint TOKEN_DUPLICATE = 0x0002, TOKEN_IMPERSONATE = 0x0004, TOKEN_QUERY = 0x0008;
        const uint OWNER_SECURITY_INFORMATION = 0x1, DACL_SECURITY_INFORMATION = 0x4;
        internal const int ERROR_ACCESS_DENIED = 5, ERROR_ALREADY_EXISTS = 183;
        internal const uint WAIT_OBJECT_0 = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed, wRightMotorSpeed; }

        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwnd);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwnd, uint flags, IntPtr data);
        [DllImport("newdev.dll", SetLastError = true)]
        static extern bool DiInstallDevice(IntPtr hwnd, IntPtr set, IntPtr data, IntPtr driver, uint flags, out bool needReboot);
        [DllImport("xinput1_4.dll")] internal static extern uint XInputSetState(uint idx, ref XINPUT_VIBRATION v);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenEventW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES sa, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr sa, int level, int type, out IntPtr dup);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetThreadToken(IntPtr thread, IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool RevertToSelf();
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int returned);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint n);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool InitializeSecurityDescriptor(IntPtr sd, uint revision);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetSecurityDescriptorDacl(IntPtr sd, bool present, IntPtr dacl, bool defaulted);
        [DllImport("advapi32.dll")]
        static extern uint GetSecurityInfo(IntPtr handle, int type, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr sd);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr sd, uint revision, uint info, out IntPtr sddl, out uint len);
    }
}
