// first_create_check: what a create hands back stays as it is.
//
// Windows starts a HID collection raw and installs it afterward, and an
// install it could not finish before that start restarts the collection.
// It happens to the first game controller a Windows ever installs once that
// Windows carries hidgamepad.inf, an extension INF (driver version
// 10.0.26100.9549) that puts the hidgamepad filter on every HID game
// controller: the filter's service does not exist yet, Windows logs the
// collection as requiring further installation, and the install that
// follows creates the service and restarts the collection. Every later game
// controller on that Windows is configured before its start and starts
// once. Measured 2026-11-19 in Windows Sandbox on 26100.9711: sc qc
// hidgamepad answered 1060 before the first controller and the service
// after it, a create that returned before the restart handed its caller a
// collection that went away 25 ms later and came back, and a Bluetooth Xbox
// pad on USB/IP came back without xinputhid's name and its XInput slot.
//
// So one profile is the first game controller of a Windows only once, and
// the place to run this is a Windows that never had one. usbip_install_check's
// part F runs it in Windows Sandbox, a fresh sandbox for each profile that
// goes first. On a machine that had a game controller before, it checks the
// same things for a device Windows starts once.
//
//   live <backend> [--first] [ids]
//                          each profile created on umdf2 or usbip, one at a
//                          time, while another thread reads XInput's four
//                          slots as a running game does. From the create's
//                          return and for four seconds: the devnodes under
//                          the device are the same ones and stay started,
//                          the handles opened on its HID collections at the
//                          return keep answering, a pad that is no XInput
//                          device keeps delivering reports to the read
//                          opened at the return, and an XInput pad holds one
//                          slot at the return and the slot answers every
//                          read. Then a button reaches XInput. Elevated.
//
// Each profile's notes say whether Windows started one of its collections
// before it could install it (Kernel-PnP configuration event 430), the case
// this check exists for. With --first the first profile named has to be
// that case on a Windows that carries hidgamepad.inf, so a run that was
// meant to be a Windows' first game controller and was not fails, where it
// would otherwise pass without having tested anything.
//
// With no ids the backend's own list runs. It leaves out the profiles whose
// descriptors carry a keyboard or mouse collection. Part F names one of
// those as well, since a sandbox is nobody's desktop.
//
// Exit 0 PASS, 1 FAIL, 2 bad arguments or skipped, 3 not elevated.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

using HIDMaestro;
using HIDMaestro.Internal;

internal static partial class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static void Note(string text) => Console.WriteLine($"  [NOTE] {text}");

    /// <summary>How long a device is watched after its create returned. The
    /// restart this looks for came 190 ms after the collection appeared in
    /// the measured run.</summary>
    const int WatchMs = 4000;

    // One of each way a profile reaches Windows: xusb22 and its companion,
    // xinputhid for a wired pad and for a Bluetooth one, Windows' GIP
    // driver, a plain HID gamepad on USB and with a Bluetooth identity, a
    // pad that answers a host's commands, a wheel and a stick.
    static readonly string[] Umdf2Subjects =
        { "xbox-360-wired", "xbox-series-xs", "xbox-series-xs-bt", "dualsense", "switch-pro", "logitech-g29", "thrustmaster-t16000m" };

    static readonly string[] UsbipSubjects =
        { "xbox-360-wired", "xbox-series-xs", "xbox-series-xs-bt", "xbox-one-s-bt", "dualsense", "dualsense-bt", "switch-pro", "logitech-g29" };

    static int Main(string[] args)
    {
        if (args.Length < 2 || args[0] != "live" || !Enum.TryParse<HMBackend>(args[1], ignoreCase: true, out var backend)
            || backend is not (HMBackend.Umdf2 or HMBackend.Usbip))
        {
            Console.WriteLine("usage: FirstCreateCheck live <umdf2|usbip> [--first] [profile ids]");
            return 2;
        }
        Console.WriteLine($"=== first creates on {backend}: what a create hands back stays as it is ===");
        using (var who = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(who).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  refused: live needs elevation");
                return 3;
            }
        if (SdkConsumers.Find() is { } other)
        {
            Console.WriteLine($"  SKIP: {other} holds HIDMaestro state on this machine, and two programs that embed HIDMaestro never test at once.");
            return 2;
        }
        Console.WriteLine($"  Windows {Environment.OSVersion.Version}, {RuntimeInformation.OSArchitecture}, session {Process.GetCurrentProcess().SessionId}");
        bool first = args.Length > 2 && args[2] == "--first";
        string[] named = args[(first ? 3 : 2)..];
        string[] ids = named.Length > 0 ? named : backend == HMBackend.Usbip ? UsbipSubjects : Umdf2Subjects;

        // A running game reads every slot all the time, so XInput opens a
        // pad's collection the moment it appears.
        bool stop = false;
        var game = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                for (uint i = 0; i < 4; i++) XInputGetState(i, out _);
                Thread.Sleep(1);
            }
        }) { IsBackground = true };
        game.Start();
        try
        {
            using var ctx = new HMContext(backend);
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            for (int i = 0; i < ids.Length; i++) One(ctx, ids[i], backend, mustBeFirstInstall: first && i == 0);
        }
        finally { Volatile.Write(ref stop, true); }

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static void One(HMContext ctx, string id, HMBackend backend, bool mustBeFirstInstall)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {id} on {backend} ---");
        if (ctx.GetProfile(id) is not { } profile) { Check($"{id}: the catalog has the profile", false); return; }
        bool xinput = XInputPresentation.Resolve(profile.Inner).Spec != null;
        // Another device's install must not be read as this one's.
        if (!InstallsSettle(30_000)) Note($"{id}: device installs were still pending 30 s before the create");
        uint before = ConnectedSlots();

        DateTime began = DateTime.UtcNow.AddSeconds(-1);
        var clock = Stopwatch.StartNew();
        HMController c;
        try { c = ctx.CreateController(profile, null, backend); }
        catch (Exception e)
        {
            Check($"{id}: the create returns a controller", false, $"{e.GetType().Name}: {e.Message}");
            return;
        }
        long createMs = clock.ElapsedMilliseconds;
        var handles = new List<(string Path, SafeFileHandle Handle)>();
        Reader? reader = null;
        try
        {
            // What the create handed back.
            bool pendingAtReturn = !InstallsSettle(0);
            uint slotsAtReturn = ConnectedSlots() & ~before;
            // The USB/IP device's own devnode, or the UMDF2 device's.
            string root = (c.Backend == HMBackend.Usbip ? c.UsbipHandle?.Device.Descriptors.UsbInstanceId : c.InstanceId) ?? "";
            if (root.Length == 0 || !Hid.Status(root).Present)
            {
                Check($"{id}: the controller's device is present at the create's return", false, root.Length == 0 ? "the controller names no device" : root);
                return;
            }
            var tree = Tree(root);
            var collections = Hid.Read(root, out var unread);
            foreach (string child in Hid.Descendants(root))
                foreach (string path in Hid.Interfaces(child))
                    handles.Add((path, Hid.Open(path, 0)));
            Note($"{id}: the create took {createMs} ms, {(pendingAtReturn ? "a device install was pending" : "no device install was pending")} at its return, " +
                 $"{tree.Count} devnode(s), {handles.Count} HID collection(s)");
            foreach (var n in tree) Note($"{id}:   {n.Id}{(n.Started ? "" : $" not started, problem {n.Problem}")}");

            Check($"{id}: the device has a HID collection at the create's return and each one opens", handles.Count > 0 && handles.All(h => !h.Handle.IsInvalid) && unread.Count == 0,
                  handles.Count == 0 ? $"none under {root}" : string.Join(", ", unread));
            Check($"{id}: every devnode of the device is started at the create's return", tree.Count > 0 && tree.All(n => n.Started),
                  string.Join(", ", tree.Where(n => !n.Started).Select(n => $"{n.Id} problem {n.Problem}")));
            uint slot = 4;
            if (xinput)
            {
                for (uint i = 0; i < 4; i++) if ((slotsAtReturn & (1u << (int)i)) != 0) { slot = i; break; }
                Check($"{id}: the pad holds one XInput slot at the create's return", BitCount(slotsAtReturn) == 1,
                      $"slots before {Slots(before)}, at the return {Slots(ConnectedSlots())}");
            }
            else
            {
                Check($"{id}: the pad, no XInput device, takes no XInput slot", slotsAtReturn == 0, $"slots before {Slots(before)}, at the return {Slots(ConnectedSlots())}");
                var game = collections.FirstOrDefault(k => k.UsagePage == 1 && k.Usage is 4 or 5 && k.InputLength > 0);
                if (game == null) Note($"{id}: no joystick or gamepad collection to read");
                else reader = Reader.Start(game.Path, game.InputLength);
                if (game != null) Check($"{id}: the game controller collection opens for reading at the create's return", reader != null);
            }

            // The watch.
            string? change = null;
            long changeMs = 0, slotMissMs = 0, handleMs = 0;
            int slotMisses = 0, slotReads = 0;
            string? handleFault = null;
            var down = new HMGamepadState { Buttons = HMButton.B };
            var up = new HMGamepadState();
            var watch = Stopwatch.StartNew();
            long nextTree = 0, nextHandles = 0;
            int tick = 0;
            while (watch.ElapsedMilliseconds < WatchMs)
            {
                // Traffic, so a pad that reports on change has reports to deliver.
                if ((tick++ / 8) % 2 == 0) c.SubmitState(in down); else c.SubmitState(in up);
                long at = watch.ElapsedMilliseconds;
                if (slot < 4)
                {
                    slotReads++;
                    if (XInputGetState(slot, out _) != 0 && slotMisses++ == 0) slotMissMs = at;
                }
                if (at >= nextTree)
                {
                    nextTree = at + 20;
                    if (change == null && Changed(tree, Tree(root)) is { } what) { change = what; changeMs = at; }
                }
                if (at >= nextHandles)
                {
                    nextHandles = at + 50;
                    if (handleFault == null)
                        foreach (var (path, handle) in handles)
                        {
                            if (handle.IsInvalid) continue;
                            var attr = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                            if (HidD_GetAttributes(handle, ref attr)) continue;
                            handleFault = $"{path}: Win32 {Marshal.GetLastPInvokeError()}";
                            handleMs = at;
                            break;
                        }
                }
                Thread.Sleep(2);
            }
            c.SubmitState(in up);

            Check($"{id}: the devnodes under the device are the same ones and started for {WatchMs / 1000} s after the create returned", change == null,
                  change == null ? "" : $"{changeMs} ms after the return: {change}");
            Check($"{id}: the handles opened on its HID collections at the return answer for {WatchMs / 1000} s", handleFault == null,
                  handleFault == null ? "" : $"{handleMs} ms after the return: {handleFault}");
            if (slot < 4)
                Check($"{id}: XInput slot {slot} answers every read for {WatchMs / 1000} s after the create returned", slotMisses == 0,
                      slotMisses == 0 ? $"{slotReads} reads" : $"{slotMisses} of {slotReads} reads found no pad, the first {slotMissMs} ms after the return");
            if (reader != null)
            {
                var (reports, fault) = reader.Stop();
                Check($"{id}: the read opened at the return delivered reports for {WatchMs / 1000} s and never failed", fault == null && reports > 0,
                      fault ?? $"{reports} reports");
            }

            // Whether this was the case the check exists for.
            var collectionIds = tree.Where(n => n.Id.StartsWith(@"HID\", StringComparison.Ordinal)).Select(n => n.Id).ToList();
            var startedRaw = FurtherInstalls(began, collectionIds);
            bool carriesFilter = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "hidgamepad.inf"));
            if (startedRaw == null) Note($"{id}: the Kernel-PnP configuration log could not be read");
            else if (startedRaw.Count > 0) Note($"{id}: Windows started {string.Join(" and ", startedRaw)} before it could install it, a first install");
            else Note($"{id}: Windows configured each collection before its start");
            if (mustBeFirstInstall)
            {
                if (!carriesFilter) Note($"{id}: this Windows carries no hidgamepad.inf, so no install restarts its first game controller");
                else Check($"{id}: the profile was this Windows' first game controller, so its create ran through a first install", startedRaw is { Count: > 0 },
                           startedRaw is { Count: > 0 } ? "" : startedRaw == null ? "the Kernel-PnP configuration log could not be read"
                           : "Windows logged no collection of the device as requiring further installation: a game controller was installed here before");
            }

            bool idle = InstallsSettle(30_000);
            var end = Tree(root);
            Check($"{id}: with Windows' device installs done the device is as the create returned it", idle && Changed(tree, end) == null,
                  !idle ? "installs still pending after 30 s" : Changed(tree, end) ?? "");

            if (xinput)
            {
                uint now = ConnectedSlots() & ~before;
                uint at = 4;
                for (uint i = 0; i < 4; i++) if ((now & (1u << (int)i)) != 0) { at = i; break; }
                bool pressed = at < 4 && WaitUntil(() => { c.SubmitState(in down); return XInputGetState(at, out var st) == 0 && (st.Gamepad.wButtons & 0x2000) != 0; }, 1_500);
                bool released = at < 4 && WaitUntil(() => { c.SubmitState(in up); return XInputGetState(at, out var st) == 0 && (st.Gamepad.wButtons & 0x2000) == 0; }, 1_500);
                Check($"{id}: B pressed and released reaches XInput after the watch", pressed && released,
                      at >= 4 ? $"no slot, slots {Slots(ConnectedSlots())}" : $"pressed {pressed}, released {released}");
            }
        }
        finally
        {
            reader?.Stop();
            foreach (var (_, handle) in handles) handle.Dispose();
            c.Dispose();
        }
        if (!WaitUntil(() => ConnectedSlots() == before, 10_000)) Note($"{id}: XInput slots {Slots(ConnectedSlots())} ten seconds after the dispose, {Slots(before)} before the create");
    }

    // ── The device tree ─────────────────────────────────────────────────

    sealed record Node(string Id, bool Started, uint Problem);

    /// <summary>The device's own devnode and every one under it, present
    /// ones only.</summary>
    static List<Node> Tree(string root)
    {
        var list = new List<Node>();
        if (root.Length == 0) return list;
        foreach (string id in new[] { root }.Concat(Hid.Descendants(root)))
        {
            var (present, started, problem) = Hid.Status(id);
            if (present) list.Add(new Node(id.ToUpperInvariant(), started, problem));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    /// <summary>What differs between the tree at the create's return and a
    /// later one, or null when nothing does.</summary>
    static string? Changed(List<Node> at, List<Node> now)
    {
        var parts = new List<string>();
        var gone = at.Where(a => now.All(n => n.Id != a.Id)).Select(a => a.Id).ToList();
        var came = now.Where(n => at.All(a => a.Id != n.Id)).Select(n => n.Id).ToList();
        var stopped = now.Where(n => !n.Started && at.Any(a => a.Id == n.Id && a.Started)).Select(n => $"{n.Id} (problem {n.Problem})").ToList();
        var began = now.Where(n => n.Started && at.Any(a => a.Id == n.Id && !a.Started)).Select(n => n.Id).ToList();
        if (gone.Count > 0) parts.Add("gone: " + string.Join(" ", gone));
        if (came.Count > 0) parts.Add("new: " + string.Join(" ", came));
        if (stopped.Count > 0) parts.Add("no longer started: " + string.Join(" ", stopped));
        if (began.Count > 0) parts.Add("started after the return: " + string.Join(" ", began));
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    // ── A read on a HID collection ──────────────────────────────────────

    /// <summary>Reads one collection's input reports, one report a read,
    /// from when it is started until it is stopped.</summary>
    sealed class Reader
    {
        readonly FileStream _stream;
        readonly CancellationTokenSource _cancel = new();
        readonly Task _task;
        int _reports;
        string? _fault;

        Reader(FileStream stream, int length)
        {
            _stream = stream;
            _task = Task.Run(async () =>
            {
                // A buffer of the report's length: a longer read can return several reports.
                var buffer = new byte[length];
                try
                {
                    while (true)
                    {
                        int n = await _stream.ReadAsync(buffer.AsMemory(0, length), _cancel.Token).ConfigureAwait(false);
                        if (n <= 0) { _fault = "a read returned no bytes"; return; }
                        Interlocked.Increment(ref _reports);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception e) { _fault = $"{e.GetType().Name}: {e.Message}"; }
            });
        }

        public static Reader? Start(string path, int length)
        {
            var handle = Hid.Open(path, 0x80000000 /* GENERIC_READ */);
            if (handle.IsInvalid) { handle.Dispose(); return null; }
            return new Reader(new FileStream(handle, FileAccess.Read, bufferSize: 0, isAsync: true), length);
        }

        bool _stopped;

        public (int Reports, string? Fault) Stop()
        {
            if (!_stopped)
            {
                _stopped = true;
                _cancel.Cancel();
                try { _task.Wait(2_000); } catch (AggregateException) { }
                _stream.Dispose();
            }
            return (Volatile.Read(ref _reports), _fault);
        }
    }

    // ── Windows' own record of the install ──────────────────────────────

    /// <summary>Which of <paramref name="collections"/> Windows logged as
    /// requiring further installation since <paramref name="sinceUtc"/>
    /// (Kernel-PnP configuration event 430): the ones it started before it
    /// could install them. Read with wevtutil, which opens no window. Null
    /// when the log could not be read.</summary>
    static List<string>? FurtherInstalls(DateTime sinceUtc, IReadOnlyCollection<string> collections)
    {
        var start = new ProcessStartInfo("wevtutil.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("qe");
        start.ArgumentList.Add("Microsoft-Windows-Kernel-PnP/Configuration");
        start.ArgumentList.Add($"/q:*[System[(EventID=430) and TimeCreated[@SystemTime>='{sinceUtc:yyyy-MM-ddTHH:mm:ss.fffZ}']]]");
        start.ArgumentList.Add("/f:text");
        try
        {
            using var p = Process.Start(start);
            if (p == null) return null;
            string text = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30_000) || p.ExitCode != 0) return null;
            return collections.Where(c => text.Contains(c, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }

    // ── Windows ─────────────────────────────────────────────────────────

    static bool WaitUntil(Func<bool> done, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (done()) return true;
            Thread.Sleep(5);
        }
        return done();
    }

    /// <summary>The XInput slots that answer, as a mask.</summary>
    static uint ConnectedSlots()
    {
        uint mask = 0;
        for (uint i = 0; i < 4; i++) if (XInputGetState(i, out _) == 0) mask |= 1u << (int)i;
        return mask;
    }

    static string Slots(uint mask) => mask == 0 ? "none" : string.Join(",", Enumerable.Range(0, 4).Where(i => (mask & (1u << i)) != 0));

    static int BitCount(uint v) => System.Numerics.BitOperations.PopCount(v);

    /// <summary>Waits up to <paramref name="timeoutMs"/> for Windows to have
    /// no device installation pending. False when one still is.</summary>
    static bool InstallsSettle(uint timeoutMs) => CMP_WaitNoPendingInstallEvents(timeoutMs) != 0x102 /* WAIT_TIMEOUT */;

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

    [LibraryImport("xinput1_4.dll")]
    private static partial uint XInputGetState(uint index, out XINPUT_STATE state);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public uint Size; public ushort VendorID, ProductID, VersionNumber; }

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES a);

    // cfgmgr32.h: #define CM_WaitNoPendingInstallEvents CMP_WaitNoPendingInstallEvents.
    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CMP_WaitNoPendingInstallEvents(uint timeoutMs);
}
