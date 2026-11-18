// The XUSB-companion family registers as exactly ONE WGI gamepad (issue #59).
//
// A virtual Xbox 360 registered as TWO gamepads in Windows.Gaming.Input, so
// shell surfaces that aggregate gamepads read every press twice. The xinputhid
// tripwire (UpperFilters="xinputhid" on the main HID parent, a pure string
// check inside WGI's OnPnpDeviceAdded) was stamped in creation step 5, two
// steps after the devnode went live, and WGI usually classified the HID child
// first: absent-at-arrival in 5 of 5 measured creates, doubled WGI in 4 of 5.
//
// Fixing the stamp's timing turned out not to be enough, and this probe is
// how that was learned: with the value written right after registration, and
// again with it in the SetupDi property state before the devnode existed,
// WGI still admitted the HID child at FIRST arrival, 2 gamepads in 3 of 3
// cycles each way. Only a RE-arrival is gated - disabling and re-enabling
// the live HID child collapsed 2 to 1 every time, the same mechanism as the
// reporter's clear/restore/restart toggle. The shipped fix is therefore the
// pair: SPDRP_UPPERFILTERS in the pre-registration property state (so the
// value is present from birth with no sweep race), plus one deliberate
// restart of the main devnode in step 5, after the XUSB companion exists,
// with the HID child re-waited before the controller is handed over.
//
// Each create cycle asserts four things:
//
//   1. The tripwire is on the main devnode.
//   2. WGI shows exactly ONE gamepad for the virtual (Gamepad and
//      RawGameController both, matched by VID/PID, less any 045E:028E entry
//      WGI listed before the first create, so a real pad or another
//      program's virtual one cannot contaminate the count). The {EC87F1E3}
//      XUSB interfaces present are printed.
//   3. XInput still sees the controller.
//   4. The HID child still reports exactly 10 buttons. The main parent is
//      Class HIDClass, so a filter value present at start could in
//      principle load xinputhid.sys and synthesize its 15-button Xbox
//      One-style descriptor, the trap that rules out driverMode=xinputhid
//      for the 360 family. Measured: it does not bind here, and 10 buttons
//      survive. If this assertion ever fails, the fix traded a doubled
//      gamepad for a wrong descriptor and must be pulled.
//
// Three cycles, because the race this guards against was probabilistic.
//
// On the USB/IP backend the controller is the XUSB function: a USB device
// xusb22 binds, which publishes the XUSB interface and makes the HID child
// itself. There is no HIDMaestro devnode to carry the tripwire, so check 1
// becomes what that backend presents in its place: the device's service is
// xusb22, no xinputhid filter sits on it or on its HID child, and exactly
// one XUSB interface is the device's own. Check 4 counts the buttons of
// the HID child xusb22 makes. Checks 2 and 3 are the same on both.
//
// Arguments: [--backend umdf2|usbip] [--profiles id,id,...|family]. With
// none, xbox-360-wired on the backend Auto resolves to, as the battery
// runs it. "family" names the ten profiles that reach XInput through the
// XUSB companion.
//
// Then thirty quick cycles under one identity key, as a consumer creates
// them, each disposing as soon as XInput lists the pad and sampling the
// pad's own slot every 2 ms after Dispose returns: it may not read connected
// again once it has read empty. The SDK's device-removal wait used to fail to register (a
// misdeclared CM_NOTIFY_FILTER), so Dispose returned while the XUSB
// companion was still leaving, and XInput read the slot connected again 8 to
// 24 ms later for about 15 ms. That build failed 4 of 30 such cycles here,
// and 12 of 30 in a process that does not listen to WGI. The sampling runs
// to 100 ms after Dispose, four times the latest of those, and to a second
// when the slot has not read empty by then.
//
// Requires admin. Exit 0 PASS, 1 FAIL.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32;
using Windows.Gaming.Input;

static class XusbWgiSingleCheck
{
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    /// <summary>One bit per XInput slot that reads connected.</summary>
    static int XInputMask()
    {
        int m = 0;
        for (uint s = 0; s < 4; s++) if (XInputGetState(s, out _) == 0) m |= 1 << (int)s;
        return m;
    }

    const uint GENERIC_READ = 0x80000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
    static readonly IntPtr INVALID = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string p, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("hid.dll")]
    static extern int HidP_GetButtonCaps(int reportType, [Out] byte[] caps, ref ushort len, IntPtr pp);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr h, uint f);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i,
                                                   ref SP_DEVICE_INTERFACE_DATA a);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref SP_DEVICE_INTERFACE_DATA a,
        IntPtr d, uint size, out uint need, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize; public Guid Class; public uint Flags; public IntPtr Reserved;
    }

    // The identity of the profile a cycle runs, and the product ID WGI
    // reports for it: the HID child's, or 0 for a pad behind the receiver
    // on USB/IP, which WGI names by the capabilities xusb22 reports, PID 0,
    // as PlasticBand's captures of retail wireless pads read.
    static ushort Vid = 0x045E, Pid = 0x028E, WgiPid = 0x028E;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid g,
        string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid g, string? deviceId,
        char[] buffer, uint bufferLen, uint flags);

    /// <summary>Every PRESENT {EC87F1E3} XUSB device interface. Which side
    /// of the doubling this run is on: two of these means two XUSB
    /// providers; one means the extra WGI entry is HID-backed.</summary>
    static List<string> XusbInterfaces()
    {
        var xusb = new Guid("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
        var found = new List<string>();
        if (CM_Get_Device_Interface_List_SizeW(out uint len, ref xusb, null, 0) != 0 || len <= 1)
            return found;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref xusb, null, buf, len, 0) != 0)
            return found;
        int i = 0;
        while (i < buf.Length)
        {
            int e = Array.IndexOf(buf, '\0', i);
            if (e < 0 || e == i) break;
            found.Add(new string(buf, i, e - i));
            i = e + 1;
        }
        return found;
    }

    static int s_fail;

    /// <summary>The instance id an interface path names:
    /// <c>\\?\usb#vid_045e&amp;pid_028e#serial#{guid}</c> is
    /// <c>usb\vid_045e&amp;pid_028e\serial</c>.</summary>
    static string InstanceOf(string path)
    {
        string s = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        int g = s.LastIndexOf("#{", StringComparison.Ordinal);
        return (g > 0 ? s[..g] : s).Replace('#', '\\');
    }

    /// <summary>How long a cycle holds after WGI first lists the virtual,
    /// for a second registration. With the create's restart of the main
    /// devnode taken out, the doubling this guards against, WGI listed the
    /// second RawGameController at the hold's first look, 50 to 65 ms after
    /// the first, in 30 of 30 fresh creates. Four times the latest.</summary>
    const int SecondRegistrationMs = 250;

    static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) s_fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static IEnumerable<string> HidPaths()
    {
        HidD_GetHidGuid(out Guid hid);
        IntPtr set = SetupDiGetClassDevsW(ref hid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == INVALID) yield break;
        try
        {
            var d = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref d); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref d, IntPtr.Zero, 0, out uint need, IntPtr.Zero);
                if (need == 0) continue;
                IntPtr buf = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref d, buf, need, out _, IntPtr.Zero))
                        yield return Marshal.PtrToStringUni(buf + 4) ?? "";
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>Total input-button count on the virtual 360's HID child,
    /// summed across HIDP_BUTTON_CAPS entries (each is 72 bytes; a range
    /// entry spans UsageMin..UsageMax at offsets 56/58, IsRange is at 4).
    /// Returns -1 when the interface is not found.</summary>
    static int ButtonCount()
    {
        foreach (var p in HidPaths())
        {
            if (p.IndexOf($"vid_{Vid:x4}", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (p.IndexOf($"pid_{Pid:x4}", StringComparison.OrdinalIgnoreCase) < 0) continue;
            IntPtr h = CreateFileW(p, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID) continue;
            try
            {
                if (!HidD_GetPreparsedData(h, out IntPtr pp)) continue;
                try
                {
                    var caps = new byte[64];
                    if (HidP_GetCaps(pp, caps) != 0x00110000) continue;
                    ushort nBtnCaps = BitConverter.ToUInt16(caps, 46); // NumberInputButtonCaps
                    if (nBtnCaps == 0) return 0;
                    const int CapSize = 72;
                    var raw = new byte[nBtnCaps * CapSize];
                    ushort len = nBtnCaps;
                    if (HidP_GetButtonCaps(0 /* HidP_Input */, raw, ref len, pp) != 0x00110000)
                        continue;
                    int total = 0;
                    for (int i = 0; i < len; i++)
                    {
                        int o = i * CapSize;
                        bool isRange = raw[o + 4] != 0;
                        if (isRange)
                        {
                            ushort lo = BitConverter.ToUInt16(raw, o + 56);
                            ushort hi = BitConverter.ToUInt16(raw, o + 58);
                            total += hi - lo + 1;
                        }
                        else total += 1;
                    }
                    return total;
                }
                finally { HidD_FreePreparsedData(pp); }
            }
            finally { CloseHandle(h); }
        }
        return -1;
    }

    /// <summary>WGI's view of the virtual: Gamepads and RawGameControllers
    /// whose VID/PID match, less those <paramref name="skip"/> names by
    /// NonRoamableId. A real Xbox 360 pad or another program's virtual one
    /// has the same VID/PID.</summary>
    static (int Gamepads, int Raw) WgiCounts(HashSet<string> skip)
    {
        int gp = 0;
        foreach (var g in WgiPins.Pads())
        {
            var r = WgiPins.RawOf(g);
            if (r != null && r.HardwareVendorId == Vid && r.HardwareProductId == WgiPid && !skip.Contains(r.NonRoamableId)) gp++;
        }
        int raw = WgiPins.Raws()
            .Count(r => r.HardwareVendorId == Vid && r.HardwareProductId == WgiPid && !skip.Contains(r.NonRoamableId));
        return (gp, raw);
    }

    /// <summary>The NonRoamableId of every RawGameController of the
    /// identity WGI lists now.</summary>
    static HashSet<string> WgiIds() =>
        WgiPins.Raws().Where(r => r.HardwareVendorId == Vid && r.HardwareProductId == WgiPid)
            .Select(r => r.NonRoamableId).ToHashSet(StringComparer.Ordinal);

    /// <summary>UpperFilters on the main devnode, found by ControllerIndex
    /// under the &IG_00 enumerator forms the orchestrator uses.</summary>
    static string[] MainUpperFilters()
    {
        foreach (var enumRoot in new[] { "ROOT", "SWD" })
        {
            using var rootKey = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{enumRoot}");
            if (rootKey == null) continue;
            foreach (var sub in rootKey.GetSubKeyNames())
            {
                if (sub.IndexOf($"VID_{Vid:X4}&PID_{Pid:X4}&IG_00",
                                StringComparison.OrdinalIgnoreCase) < 0) continue;
                using var subKey = rootKey.OpenSubKey(sub);
                if (subKey == null) continue;
                foreach (var inst in subKey.GetSubKeyNames())
                {
                    using var k = subKey.OpenSubKey(inst);
                    if (k?.GetValue("UpperFilters") is string[] v) return v;
                }
            }
        }
        return Array.Empty<string>();
    }

    static string? FindDevnode(string pattern)
    {
        foreach (var root in new[] { "HID", "SWD", "ROOT" })
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{root}");
            if (k == null) continue;
            foreach (var sub in k.GetSubKeyNames())
            {
                if (sub.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0) continue;
                using var s = k.OpenSubKey(sub);
                foreach (var inst in s!.GetSubKeyNames())
                    return $@"{root}\{sub}\{inst}";
            }
        }
        return null;
    }

    static void Pnputil(string verb, string id)
    {
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "pnputil.exe",
            Arguments = $"/{verb}-device \"{id}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        p.WaitForExit(15000);
    }

    /// <summary>Hold one doubled controller and name each WGI entry's backing
    /// device by disabling candidates one at a time and watching which entry
    /// vanishes. Diagnosis only, never part of the battery.</summary>
    static int Discriminate()
    {
        Console.WriteLine("=== which two devices back the doubled WGI entries ===");
        WgiPins.Listen();
        Thread.Sleep(1000);

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var prof = ctx.GetProfile("xbox-360-wired")!;
        using var c = ctx.CreateController(prof);

        (int Gamepads, int Raw) counts = (0, 0);
        var none = new HashSet<string>();
        for (int i = 0; i < 40; i++) { Thread.Sleep(500); counts = WgiCounts(none); if (counts.Raw >= 2) break; }
        Console.WriteLine($"  baseline: gamepads={counts.Gamepads} raw={counts.Raw}");

        string? hidChild = FindDevnode("VID_045E&PID_028E");
        string? swdComp = FindDevnode("HIDMAESTRO");
        Console.WriteLine($"  hid child: {hidChild ?? "(none)"}");
        Console.WriteLine($"  companion: {swdComp ?? "(none)"}");

        foreach (var (label, id) in new[] { ("HID child", hidChild), ("SWD companion", swdComp) })
        {
            if (id == null) continue;
            Pnputil("disable", id);
            Thread.Sleep(4000);
            var after = WgiCounts(none);
            Console.WriteLine($"  {label} DISABLED -> gamepads={after.Gamepads} raw={after.Raw}" +
                              $"   (delta raw {after.Raw - counts.Raw})");
            Pnputil("enable", id);
            Thread.Sleep(4000);
            var back = WgiCounts(none);
            Console.WriteLine($"  {label} re-enabled -> gamepads={back.Gamepads} raw={back.Raw}");
            counts = back;
        }
        return 0;
    }

    static readonly string[] Family =
    {
        "xbox-360-arcade-stick", "xbox-360-dance-pad", "xbox-360-flight-stick", "xbox-360-guitar-v1", "xbox-360-guitar-v2",
        "xbox-360-type2", "xbox-360-wheel-v1", "xbox-360-wheel-v2", "xbox-360-wired", "xbox-360-wireless",
    };

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--discriminate") return Discriminate();
        Console.WriteLine("=== XUSB-companion virtuals are ONE WGI gamepad (issue #59) ===");
        var backend = HMBackend.Auto;
        var ids = new List<string> { "xbox-360-wired" };
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            if (args[i] == "--backend") backend = args[i + 1] == "usbip" ? HMBackend.Usbip : args[i + 1] == "umdf2" ? HMBackend.Umdf2 : HMBackend.Auto;
            else if (args[i] == "--profiles") ids = args[i + 1] == "family" ? Family.ToList() : args[i + 1].Split(',').ToList();
        }

        // Event subscription first: WGI's broker only tracks controllers for
        // processes it believes are listening.
        WgiPins.Listen();
        Thread.Sleep(1000);

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        Console.WriteLine($"  backend {backend}, resolving to {(backend == HMBackend.Auto ? HMContext.MachineBackend : backend)} for a profile both backends present");
        HMProfile? last = null;
        foreach (var id in ids)
        {
            var p = ctx.GetProfile(id);
            if (p == null) { Check($"{id} is in the catalog", false); continue; }
            last = p;
            Cycles(ctx, p, backend);
        }
        if (last == null) return 1;
        var prof = ctx.GetProfile("xbox-360-wired") ?? last;
        Vid = prof.VendorId;
        Pid = prof.ProductId;
        WgiPid = Pid;

        Console.WriteLine();
        Console.WriteLine($"-- Dispose leaves no XInput slot behind: {prof.Id} --");
        const int ComeBackWindowMs = 100;
        int cameBack = 0, notEmpty = 0;
        for (int i = 0; i < 30; i++)
        {
            // Only the slot the pad takes is watched: another controller can
            // take or leave a slot of its own while this runs.
            int others = XInputMask(), own = 0;
            using (ctx.CreateController(prof, "xusb-wgi-quick", backend))
            {
                var up = System.Diagnostics.Stopwatch.StartNew();
                while ((own = XInputMask() & ~others) == 0 && up.ElapsedMilliseconds < 5000) Thread.Sleep(1);
            }
            var t = System.Diagnostics.Stopwatch.StartNew();
            bool empty = false, back = false;
            long backAt = -1;
            while (t.ElapsedMilliseconds < 1000)
            {
                if ((XInputMask() & own) == 0) empty = true;
                else if (empty && !back) { back = true; backAt = t.ElapsedMilliseconds; }
                if (empty && t.ElapsedMilliseconds >= ComeBackWindowMs) break;
                Thread.Sleep(2);
            }
            if (back) Console.WriteLine($"     cycle {i + 1}: the slot read connected again {backAt} ms after Dispose returned");
            if (back) cameBack++;
            if (!empty || (XInputMask() & own) != 0) notEmpty++;
        }
        Check("no XInput slot reads connected again after Dispose returns", cameBack == 0, $"{cameBack} of 30 quick cycles");
        Check("the slot reads empty within a second of Dispose", notEmpty == 0, $"{notEmpty} of 30");

        Console.WriteLine();
        Console.WriteLine(s_fail == 0
            ? "=== ONE WGI GAMEPAD, ITS BUTTONS, EVERY CYCLE ==="
            : $"=== {s_fail} FAILED ===");
        return s_fail == 0 ? 0 : 1;
    }

    /// <summary>Three fresh creates of <paramref name="prof"/> on
    /// <paramref name="backend"/>, each held to the four checks.</summary>
    static void Cycles(HMContext ctx, HMProfile prof, HMBackend backend)
    {
        Vid = prof.VendorId;
        Pid = prof.ProductId;
        bool onUsbip = backend == HMBackend.Usbip || (backend == HMBackend.Auto && HMContext.MachineBackend == HMBackend.Usbip);
        WgiPid = onUsbip && prof.Connection == "wireless-adapter" ? (ushort)0 : Pid;
        Console.WriteLine();
        Console.WriteLine($"== {prof.Id}, {Vid:X4}:{Pid:X4}, WGI {Vid:X4}:{WgiPid:X4} ==");
        // A device of the identity already here is not the probe's, and the
        // counts below leave it out.
        var bench = WgiIds();
        Console.WriteLine($"  {Vid:X4}:{Pid:X4} devices WGI lists before the first create, left out of the counts: {bench.Count}");
        // The buttons the HID child reports: on UMDF2 the profile's own
        // descriptor, which is 10 for xbox-360-wired; on USB/IP the HID child
        // xusb22 makes. Read on the first cycle, held on the next two.
        int firstButtons = -2;

        for (int cycle = 1; cycle <= 3; cycle++)
        {
            Console.WriteLine();
            Console.WriteLine($"-- {prof.Id} cycle {cycle}: fresh create --");
            HMController? c = null;
            var xusbBefore = XusbInterfaces().ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                c = ctx.CreateController(prof, null, backend);
                Console.WriteLine($"  on {c.Backend}");

                if (c.Backend == HMBackend.Umdf2)
                {
                    var filters = MainUpperFilters();
                    Check("the xinputhid tripwire is on the main devnode",
                          filters.Any(f => string.Equals(f, "xinputhid", StringComparison.OrdinalIgnoreCase)),
                          filters.Length > 0 ? string.Join(",", filters) : "(absent)");
                }
                else
                {
                    // In the tripwire's place: the device is xusb22's, no
                    // xinputhid filter on it, and one XUSB interface its own.
                    // The device is the one whose XUSB interface arrived with
                    // this create: its path names its instance.
                    string? path = null;
                    var arrival = System.Diagnostics.Stopwatch.StartNew();
                    // A wireless pad's device is the receiver, whose PID is
                    // not the pad's: any USB device's interface that arrived.
                    while ((path = XusbInterfaces().FirstOrDefault(s => !xusbBefore.Contains(s)
                                && s.Contains($"usb#vid_{Vid:x4}&pid_", StringComparison.OrdinalIgnoreCase))) == null
                           && arrival.ElapsedMilliseconds < 10_000) Thread.Sleep(25);
                    string usb = path == null ? "" : InstanceOf(path);
                    using var k = usb.Length == 0 ? null : Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{usb}");
                    string service = k?.GetValue("Service") as string ?? "(none)";
                    string[] upper = k?.GetValue("UpperFilters") as string[] ?? Array.Empty<string>();
                    Check("the USB device's service is xusb22, with no xinputhid filter", string.Equals(service, "xusb22", StringComparison.OrdinalIgnoreCase)
                          && !upper.Any(f => f.Equals("xinputhid", StringComparison.OrdinalIgnoreCase)), $"{usb}: service {service}, upper filters {string.Join(",", upper)}");
                    string hash = usb.Replace('\\', '#');
                    int own = usb.Length == 0 ? 0 : XusbInterfaces().Count(s => s.Contains(hash, StringComparison.OrdinalIgnoreCase));
                    Check("exactly one XUSB interface is the device's own", own == 1, $"{own}");
                }


                // WGI settles asynchronously; poll until the virtual shows up,
                // then hold for a moment so a late second registration would
                // still be caught rather than raced past.
                (int Gamepads, int Raw) counts = (0, 0);
                var seen = System.Diagnostics.Stopwatch.StartNew();
                while (seen.ElapsedMilliseconds < 20000)
                {
                    Thread.Sleep(50);
                    counts = WgiCounts(bench);
                    if (counts.Gamepads >= 1 && counts.Raw >= 1) break;
                }
                var hold = System.Diagnostics.Stopwatch.StartNew();
                while (hold.ElapsedMilliseconds < SecondRegistrationMs)
                {
                    Thread.Sleep(50);
                    var c2 = WgiCounts(bench);
                    if (c2.Gamepads > 1 || c2.Raw > 1) break;
                }
                counts = WgiCounts(bench);
                // A Gamepad only for a pad WGI classes as one: Windows lists
                // an XUSB wheel as a RawGameController alone, as it lists
                // the retail wheel. Never two.
                Check(c.Backend == HMBackend.Usbip ? "WGI shows at most ONE Gamepad for the virtual" : "WGI shows exactly ONE Gamepad for the virtual",
                      c.Backend == HMBackend.Usbip ? counts.Gamepads <= 1 : counts.Gamepads == 1,
                      $"gamepads={counts.Gamepads}");
                Check("WGI shows exactly ONE RawGameController", counts.Raw == 1,
                      $"raw={counts.Raw}");
                var xusbIfs = XusbInterfaces();
                Console.WriteLine($"     XUSB interfaces present: {xusbIfs.Count}");
                foreach (var s in xusbIfs) Console.WriteLine($"       {s}");
                if (counts.Raw != 1)
                {
                    foreach (var r in WgiPins.Raws())
                        Console.WriteLine($"       every raw: {r.HardwareVendorId:X4}:{r.HardwareProductId:X4} '{r.DisplayName}' id={r.NonRoamableId}");
                    // Name the impostors: NonRoamableId embeds the backing
                    // device interface path, which says WHICH two devices
                    // WGI is holding, instead of leaving that to argument.
                    foreach (var r in WgiPins.Raws())
                        if (r.HardwareVendorId == Vid && r.HardwareProductId == WgiPid && !bench.Contains(r.NonRoamableId))
                            Console.WriteLine($"       raw: '{r.DisplayName}' id={r.NonRoamableId}");
                }

                // Polled: XInput may list the pad a moment after WGI does.
                var look = System.Diagnostics.Stopwatch.StartNew();
                bool xinputSeen;
                while (!(xinputSeen = XInputMask() != 0) && look.ElapsedMilliseconds < 5000) Thread.Sleep(10);
                Check("XInput still sees the controller", xinputSeen);

                int buttons = ButtonCount();
                if (firstButtons == -2) firstButtons = buttons;
                if (c.Backend == HMBackend.Umdf2 && prof.Id == "xbox-360-wired")
                    Check("the HID child still reports exactly 10 buttons", buttons == 10, $"buttons={buttons}");
                else
                    Check("the HID child reports its buttons, the same count every cycle", buttons > 0 && buttons == firstButtons,
                          $"buttons={buttons}, first cycle {firstButtons}");
            }
            catch (Exception ex)
            {
                Check("create cycle runs without throwing", false, ex.Message);
            }
            finally
            {
                try { c?.Dispose(); } catch { }
                // XUSB-companion teardown runs several seconds; wait until
                // WGI has let go so the next cycle starts from zero.
                var gone = System.Diagnostics.Stopwatch.StartNew();
                while (gone.ElapsedMilliseconds < 30000)
                {
                    Thread.Sleep(100);
                    if (WgiCounts(bench) is { Gamepads: 0, Raw: 0 }) break;
                }
            }
        }
    }

}
