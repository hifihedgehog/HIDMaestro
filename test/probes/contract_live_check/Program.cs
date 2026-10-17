// contract_live_check: the version contract and FeatureAnswers on the live
// UMDF2 driver (design note, decision (c) and mechanism 4). Needs
// elevation. Creates its controllers one at a time and disposes each.
//
//   identity     The driver reads its instance ID through
//                WdfFdoInitQueryPropertyEx before WdfDeviceCreate, takes
//                its token, and reads the index the token's identity entry
//                names before the device node's first start. A generic pad
//                at index 0 first. Then a fresh-token dualsense at index 1
//                on the ROOT path and a fresh-token xbox-one-s at index 2
//                on the SWD xinputhid path. For each, the driver's half of
//                the chosen index's version section reads protocols 4 to 4,
//                which it writes only into the section of the index it
//                chose. The dualsense answers report 0x09 with its MAC's
//                last byte 1, from the FeatureAnswers of index 1, and the
//                xbox-one-s's input reaches XInput. If any of these fails,
//                the later lanes stop until the design names another
//                source for the index.
//   unnumbered   A profile without report IDs that carries the Deck's
//                message entries: HidD_SetFeature of the caller's leading 0
//                and the message 0x83, then HidD_GetFeature, which must
//                answer 0x83's attributes at the caller's byte 1, and the
//                string request for index 1, answered with the index at the
//                caller's byte 3. This tests that HID class hands the
//                driver an unnumbered report without the caller's 0 in both
//                directions, as Wine's HID class does for both and
//                vhidmini2 says for a GET (R3-N9).
//   extract      Each UMDF2 catalog profile with a model is created, read
//                back with HMDeviceExtractor.Extract, and the extracted
//                profile must name the model its identity gives it, with
//                the answers the device gave.
//   check        the three, the default.
//   selftest     the version-section watch on a section this check makes,
//                no device and no elevation.
// Each create that throws is a named failure with the exception, the
// driver's half of its index's version section and the problem code
// Windows gave the device, and the checks after it go on. A create whose
// device misbehaves names the device's problem code beside the check. The
// controllers each lane creates are disposed whatever happens, and the
// context's disposal at the end takes what is left. If the driver cannot
// read its instance ID before its device exists, every create here fails
// its start, and identity's first check names that.
// Exit 0 PASS, 1 FAIL, 3 not elevated (nothing created).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    static int s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetState(uint user, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LX, LY, RX, RY;
    }

    /// <summary>The HID device the driver serves for an index: its serial
    /// is HM-CTL- and the index (driver.c InitInstancePaths).</summary>
    static HMHidDeviceInfo? Find(int index)
    {
        string serial = $"HM-CTL-{index:D4}";
        for (int i = 0; i < 50; i++)
        {
            var d = HMDeviceExtractor.ListDevices().FirstOrDefault(x => x.SerialNumberString == serial);
            if (d != null) return d;
            Thread.Sleep(100);
        }
        return null;
    }

    static SafeFileHandle? Open(HMHidDeviceInfo? d)
    {
        if (d == null) return null;
        var h = CreateFileW(d.DevicePath, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        return h.IsInvalid ? null : h;
    }

    static byte[]? Get(SafeFileHandle h, byte id, int length)
    {
        var buf = new byte[length];
        buf[0] = id;
        return HidD_GetFeature(h, buf, buf.Length) ? buf : null;
    }

    static string Key() => "contract-live-" + Guid.NewGuid().ToString("N");

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMappingW(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr sec, uint protect, uint sizeHigh, uint sizeLow, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offHigh, uint offLow, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    /// <summary>The version section of one index, held open by this check
    /// from the moment the SDK makes it. A failed create tears its section
    /// down (HMContext's cleanup), and the section lives while any handle
    /// does, so the driver's half is still readable after the failure.</summary>
    sealed class VersionWatch : IDisposable
    {
        const uint FILE_MAP_READ = 4;
        readonly string _name;
        readonly Thread _thread;
        volatile bool _stop;
        volatile bool _holding;
        IntPtr _handle, _view;

        /// <summary>The watch holds the section.</summary>
        public bool Holding => _holding;

        public VersionWatch(string name)
        {
            _name = name;
            _thread = new Thread(Run) { IsBackground = true, Name = "version-watch" };
            _thread.Start();
        }

        void Run()
        {
            while (!_stop)
            {
                IntPtr h = OpenFileMappingW(FILE_MAP_READ, false, _name);
                if (h != IntPtr.Zero)
                {
                    IntPtr v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, (UIntPtr)SharedMemoryIO.VERSION_INFO_SIZE);
                    if (v != IntPtr.Zero) { _handle = h; _view = v; _holding = true; return; }
                    CloseHandle(h);
                }
                Thread.Sleep(2);
            }
        }

        /// <summary>The driver's half as SharedMemoryIO.DriverHalf reads
        /// it, or why there is none.</summary>
        public string Read()
        {
            _stop = true;
            _thread.Join();
            if (_view == IntPtr.Zero) return "no version section seen";
            int error = Marshal.ReadInt32(_view, SharedMemoryIO.VERSION_OFFSET_DRIVER_CONFIG_ERROR);
            int protocol = Marshal.ReadInt32(_view, SharedMemoryIO.VERSION_OFFSET_DRIVER_PROTOCOL);
            int min = Marshal.ReadInt32(_view, SharedMemoryIO.VERSION_OFFSET_DRIVER_PROTOCOL_MIN);
            var version = $"{(ushort)Marshal.ReadInt16(_view, SharedMemoryIO.VERSION_OFFSET_DRIVER_VERSION)}." +
                          $"{(ushort)Marshal.ReadInt16(_view, SharedMemoryIO.VERSION_OFFSET_DRIVER_VERSION + 2)}." +
                          $"{(ushort)Marshal.ReadInt16(_view, SharedMemoryIO.VERSION_OFFSET_DRIVER_VERSION + 4)}";
            return protocol == 0 && error == 0 ? "the driver wrote nothing"
                : $"protocols {min} to {protocol}, version {version}, error word {error}";
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join();
            if (_view != IntPtr.Zero) { UnmapViewOfFile(_view); _view = IntPtr.Zero; }
            if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
        }
    }

    /// <summary>A create, or a named failure: the exception, the driver's
    /// half of the index's version section, and what the index's device
    /// reported. The section is watched from before the create, since the
    /// failed create's cleanup releases the SDK's own view of it.</summary>
    static HMController? Create(HMContext ctx, int index, HMProfile profile, string what)
    {
        using var watch = new VersionWatch($@"Global\HIDMaestroVersion{index}");
        try { return ctx.CreateControllerAt(index, profile, Key()); }
        catch (Exception e)
        {
            Check($"{what}: the create", false, $"{e.GetType().Name}: {e.Message} | driver's half: {watch.Read()}");
            return null;
        }
    }

    /// <summary>The watch reads a section that outlived its maker: a
    /// section this check makes and fills as a driver would, closed before
    /// the read. Needs no device and no elevation.</summary>
    static void Selftest()
    {
        string name = $@"Local\HMContractLiveSelftest{Environment.ProcessId}";
        using var watch = new VersionWatch(name);
        IntPtr h = CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, 4 /* PAGE_READWRITE */, 0, (uint)SharedMemoryIO.VERSION_INFO_SIZE, name);
        IntPtr v = MapViewOfFile(h, 2 /* FILE_MAP_WRITE */, 0, 0, (UIntPtr)SharedMemoryIO.VERSION_INFO_SIZE);
        Marshal.WriteInt32(v, SharedMemoryIO.VERSION_OFFSET_DRIVER_PROTOCOL, 4);
        Marshal.WriteInt32(v, SharedMemoryIO.VERSION_OFFSET_DRIVER_PROTOCOL_MIN, 4);
        Marshal.WriteInt16(v, SharedMemoryIO.VERSION_OFFSET_DRIVER_VERSION, 2);
        Marshal.WriteInt32(v, SharedMemoryIO.VERSION_OFFSET_DRIVER_CONFIG_ERROR, 1);
        for (int i = 0; i < 500 && !watch.Holding; i++) Thread.Sleep(2);
        UnmapViewOfFile(v);
        CloseHandle(h);
        string read = watch.Read();
        Check("selftest: the watch reads a version section its maker closed", read == "protocols 4 to 4, version 2.0.0, error word 1", read);
    }

    /// <summary>The device a controller made, as Windows reports it.</summary>
    static string Node(HMController c)
    {
        if (c.InstanceId is not { } id) return "no device";
        var (present, status, problem) = DeviceOrchestrator.DevnodeStatus(id);
        return present ? $"{id} status 0x{status:X} problem {problem}" : $"{id} not present";
    }

    /// <summary>A lane runs to its end: an exception it did not expect is a
    /// named failure, and its usings dispose what it created.</summary>
    static void Lane(string name, Action body)
    {
        try { body(); }
        catch (Exception e) { Check($"{name}: runs to its end", false, $"{e.GetType().Name}: {e.Message}"); }
    }

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "selftest")
        {
            Selftest();
            Console.WriteLine($"contract_live_check selftest: {(s_failures == 0 ? "PASS" : $"FAIL, {s_failures}")}");
            return s_failures == 0 ? 0 : 1;
        }
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.WriteLine("contract_live_check: needs elevation, nothing created");
            return 3;
        }
        string mode = args.Length > 0 ? args[0] : "check";
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        if (mode is "identity" or "check") Lane("identity", () => Identity(ctx));
        if (mode is "unnumbered" or "check") Lane("unnumbered", () => Unnumbered(ctx));
        if (mode is "extract" or "check") Lane("extract", () => Extract(ctx));
        Console.WriteLine($"contract_live_check {mode}: {(s_failures == 0 ? "PASS" : $"FAIL, {s_failures}")}");
        return s_failures == 0 ? 0 : 1;
    }

    static void Identity(HMContext ctx)
    {
        var pad = new HMProfileBuilder().Id("contract-live-pad").Vid(0xBEEF).Pid(0xF0C0)
            .DescriptorHex(ctx.GetProfile("heusinkveld-ultimate-pedals")!.DescriptorHex!).Build();
        // The first create names the instance-ID read: a driver that cannot
        // read it before its device exists has no token, matches no section
        // and fails the start of every create here.
        using (var c0 = Create(ctx, 0, pad, "a generic pad at index 0, whose driver must read its instance ID before its device exists"))
        {
            if (c0 != null)
                Check("a generic pad at index 0 starts on a driver of protocols 4 to 4",
                      SharedMemoryIO.DriverHalf(0) is { Protocol: 4, ProtocolMin: 4 }, $"{SharedMemoryIO.DriverHalf(0)} | {Node(c0)}");
        }

        using (var c1 = Create(ctx, 1, ctx.GetProfile("dualsense")!, "a fresh-token dualsense at index 1 on the ROOT path"))
        {
            if (c1 != null)
            {
                var half = SharedMemoryIO.DriverHalf(1);
                Check("a fresh-token dualsense at index 1 on the ROOT path: its driver wrote protocols 4 to 4 into index 1's section at its first add",
                      half is { Protocol: 4, ProtocolMin: 4, ConfigError: 0 }, $"{half} | {Node(c1)}");
                using var h = Open(Find(1));
                var r = h == null ? null : Get(h, 0x09, 20);
                Check("its report 0x09 carries index 1 in its MAC, from index 1's FeatureAnswers",
                      r != null && r[6] == 1, r == null ? $"no answer | {Node(c1)}" : Convert.ToHexString(r));
            }
        }

        using (var c2 = Create(ctx, 2, ctx.GetProfile("xbox-one-s")!, "a fresh-token xbox-one-s at index 2 on the SWD xinputhid path"))
        {
            if (c2 == null) return;
            var half = SharedMemoryIO.DriverHalf(2);
            Check("a fresh-token xbox-one-s at index 2 on the SWD xinputhid path: its driver wrote protocols 4 to 4 into index 2's section at its first add",
                  half is { Protocol: 4, ProtocolMin: 4, ConfigError: 0 }, $"{half} | {Node(c2)}");
            var state = new HMGamepadState { Buttons = HMButton.A | HMButton.Y };
            var sw = Stopwatch.StartNew();
            bool seen = false;
            while (!seen && sw.ElapsedMilliseconds < 3000)
            {
                c2.SubmitState(state);
                for (uint s = 0; s < 4 && !seen; s++)
                    seen = XInputGetState(s, out var st) == 0 && (st.Buttons & 0x9000) == 0x9000;
                Thread.Sleep(20);
            }
            Check("its input reaches XInput, which reads index 2's section", seen, $"{sw.ElapsedMilliseconds} ms | {Node(c2)}");
        }
    }

    static void Unnumbered(HMContext ctx)
    {
        var deck = ctx.GetProfile("steam-deck-composite")!;
        var p = new HMProfileBuilder().Id("contract-live-unnumbered").Vid(0x28DE).Pid(0x1205)
            .DescriptorHex(deck.DescriptorHex!).FeatureAnswers(System.Text.Json.JsonSerializer.Serialize(deck.Inner.FeatureStubs!, HMJsonContext.Default.FeatureStubSpec)).Build();
        using var c = Create(ctx, 3, p, "the unnumbered profile at index 3");
        if (c == null) return;
        using var h = Open(Find(3));
        if (h == null) { Check("the unnumbered profile's HID device opens", false, Node(c)); return; }
        // The caller writes report 0's leading 0 as SDL does, and HID class
        // leaves it out of the minidriver's buffer in both directions (Wine
        // 2df1ee2 hidclass.sys device.c:485, vhidmini2 197ba21
        // vhidmini.c:765-767): the driver keeps [83 00 ...], matches the
        // message at byte 0, and its answer lands at the caller's byte 1.
        // The string request [00 AE 14 01] reaches it as [AE 14 01], index 1
        // at byte 2, and its answer echoes the index at the caller's byte 3.
        (bool Sent, bool Got, byte[] Get, int Error) Ask(params byte[] request)
        {
            var set = new byte[65];
            request.CopyTo(set, 0);
            bool sent = HidD_SetFeature(h, set, set.Length);
            int error = sent ? 0 : Marshal.GetLastPInvokeError();
            var get = new byte[65];
            bool got = HidD_GetFeature(h, get, get.Length);
            return (sent, got, get, error != 0 ? error : got ? 0 : Marshal.GetLastPInvokeError());
        }
        var a = Ask(0x00, 0x83);
        Check("HidD_SetFeature of message 0x83 after the caller's leading 0, then HidD_GetFeature, answers 0x83's attributes at the caller's byte 1",
              a.Sent && a.Got && a.Get[1] == 0x83 && a.Get[2] == 0x2D,
              $"set {a.Sent}, get {a.Got}, error {a.Error}: {Convert.ToHexString(a.Get, 0, 8)}");
        var b = Ask(0x00, 0xAE, 0x14, 0x01);
        Check("the string request for index 1 answers index 1's string, the index at the caller's byte 3",
              b.Sent && b.Got && b.Get[1] == 0xAE && b.Get[3] == 0x01,
              $"set {b.Sent}, get {b.Got}, error {b.Error}: {Convert.ToHexString(b.Get, 0, 8)}");
    }

    static void Extract(HMContext ctx)
    {
        foreach (var p in ctx.AllProfiles.Where(x => !x.Inner.RequiresUsbipBackend && x.HasDeviceModel && x.Inner.Resolved.Model.FeatureStubs != null)
                     .OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            using var c = Create(ctx, 0, p, p.Id);
            if (c == null) continue;
            var d = Find(0);
            if (d == null) { Check($"{p.Id}: its HID device is listed", false, Node(c)); continue; }
            var x = HMDeviceExtractor.Extract(d);
            string? named = x.Inner.DeviceModelFrom;
            var model = x.Inner.Resolved.Model.FeatureStubs;
            Check($"{p.Id}: the extracted profile names the model its identity gives it", named != null && x.Inner.Resolved.IdentityModel == named,
                  $"{named ?? "none"} (identity gives {x.Inner.Resolved.IdentityModel ?? "none"})");
            using var h = Open(d);
            var table = FeatureStubTable.From(x.Inner, 0, d.SerialNumberString);
            var differ = new List<string>();
            foreach (var r in p.Inner.GetOrBuildReportMap().Reports.Where(r => r.Kind == HidReportKind.Feature).Select(r => r.ReportId).Distinct())
            {
                var live = h == null ? null : Get(h, r, 65);
                int n = table?.Lookup(r, 65, out _) ?? -1;
                byte[]? want = n < 0 ? null : table!.Answer(n).ToArray();
                if (live == null && want == null) continue;
                if (live == null || want == null || !live.AsSpan(0, want.Length).SequenceEqual(want))
                    differ.Add($"0x{r:X2}");
            }
            Check($"{p.Id}: the extracted profile's answers are the ones the device gave", differ.Count == 0, string.Join(" ", differ));
        }
    }
}
