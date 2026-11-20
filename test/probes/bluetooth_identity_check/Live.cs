// live <backend> [ids]: each Bluetooth profile but the Xbox pads on
// <backend>, with dualsense on <backend> and dualsense-composite on USB/IP as
// the USB controls of the same run.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;

using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    static void Live(HMBackend backend, string[] ids)
    {
        using var ctx = new HMContext(backend);
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        Console.WriteLine($"  context backend {backend}, machine backend {HMContext.MachineBackend}");

        foreach (string id in ids.Length > 0 ? ids : Bluetooth.Select(b => b.Id).ToArray())
        {
            var p = ctx.GetProfile(id);
            if (p == null) { Check($"{id} is in the catalog", false); continue; }
            // A pad that reaches XInput through xinputhid is read through
            // xinputhid: bluetooth_hid_check has those.
            if (p.Inner.UsesXinputhid) continue;
            LiveOne(ctx, p, backend);
        }
        if (ids.Length > 0) return;
        // The USB controls: the same pad's USB profile on this backend, and
        // a USB persona the USB/IP backend has always presented.
        LiveOne(ctx, ctx.GetProfile("dualsense")!, backend);
        LiveOne(ctx, ctx.GetProfile("dualsense-composite")!, HMBackend.Usbip);
    }

    /// <summary>The bus hidapi reads from a node's compatible IDs: each ID
    /// in order, upper-cased, and in each the tests in hidapi's order (SDL
    /// 3.4.18 src/hidapi/windows/hid.c:769-807). The first ID that passes
    /// one decides.</summary>
    internal static string HidapiBus(IEnumerable<string> compatibleIds)
    {
        foreach (string id in compatibleIds)
        {
            string up = id.ToUpperInvariant();
            if (up.Contains("USB", StringComparison.Ordinal)) return "usb";
            if (up.Contains("BTHENUM", StringComparison.Ordinal)) return "bluetooth";
            if (up.Contains("BTHLEDEVICE", StringComparison.Ordinal)) return "bluetooth";
            if (up.Contains("PNP0C50", StringComparison.Ordinal)) return "i2c";
            if (up.Contains("PNP0C51", StringComparison.Ordinal)) return "spi";
        }
        return "unknown";
    }

    static void LiveOne(HMContext ctx, HMProfile profile, HMBackend backend)
    {
        Console.WriteLine($"\n-- {profile.Id} on {backend} --");
        using var ctrl = ctx.CreateController(profile, null, backend);
        Check("the controller runs on the backend asked for", ctrl.Backend == backend, ctrl.Backend.ToString());
        Check("it presents its own form", ReferenceEquals(profile.FormOn(ctrl.Backend), profile), profile.FormOn(ctrl.Backend).Id);

        var decoded = new List<IReadOnlyDictionary<string, object>>();
        ctrl.OutputDecoded += (_, e) => { lock (decoded) decoded.Add(e.Fields); };

        string serial = SerialOf(ctrl);
        var all = new List<HMHidDeviceInfo>();
        var sw = Stopwatch.StartNew();
        while (all.Count == 0 && sw.ElapsedMilliseconds < 15000)
        {
            all = Collections(profile, serial);
            if (all.Count == 0) Thread.Sleep(50);
        }
        Check($"its HID collections enumerate (serial {serial})", all.Count > 0, $"{all.Count}");
        if (all.Count == 0) return;

        // The bus hidapi reads. A Bluetooth profile reads as Bluetooth on
        // either backend. A USB control reads as USB on USB/IP, and on UMDF2,
        // where its root-enumerated node carries no compatible IDs, as no
        // bus at all, which SDL takes as not Bluetooth
        // (SDL_hidapijoystick.c:937).
        bool wantBt = string.Equals(profile.Connection, "bluetooth", StringComparison.OrdinalIgnoreCase);
        var buses = all.Select(c => (c.DevicePath, Ids: ParentCompatibleIds(c.DevicePath))).ToList();
        var read = buses.Select(b => HidapiBus(b.Ids)).Distinct().ToArray();
        Check(wantBt ? $"hidapi reads the bus as Bluetooth for each of its {all.Count} collection(s)"
                     : $"hidapi reads no Bluetooth bus for its {all.Count} collection(s), the USB control",
            read.All(r => (r == "bluetooth") == wantBt), $"read {string.Join(", ", read)}. Parent compatible IDs: {string.Join(" | ", buses[0].Ids)}");

        ushort wantVersion = profile.Inner.VersionNumber ?? 0x0100;
        var offVersion = all.Where(c => c.VersionNumber != wantVersion).Select(c => $"0x{c.VersionNumber:X4}").Distinct().ToArray();
        Check($"the version is the profile's 0x{wantVersion:X4}", offVersion.Length == 0, string.Join(", ", offVersion));
        var offProduct = all.Where(c => c.ProductString != profile.ProductString).Select(c => $"'{c.ProductString}'").Distinct().ToArray();
        Check($"the product string is '{profile.ProductString}'", offProduct.Length == 0, string.Join(", ", offProduct));

        // The rest reads a pad's one collection. Windows holds a keyboard's
        // and a mouse's for itself, and S107 compares every collection's caps.
        static bool HeldByWindows(HMHidDeviceInfo c) => c.TopLevelUsagePage == 0x01 && c.TopLevelUsage is 0x02 or 0x06 or 0x07;
        if (all.Count != 1 || HeldByWindows(all[0])) return;
        var info = all[0];
        using var h = OpenHid(info.DevicePath);
        Check("the HID collection opens read/write", !h.IsInvalid, $"error {Marshal.GetLastWin32Error()}");
        if (h.IsInvalid) return;
        var caps = Caps(h);
        var map = profile.Inner.GetOrBuildReportMap();
        // Windows counts a report ID byte in each length, with or without
        // report IDs in the descriptor.
        int idByte = map.UsesReportIds ? 0 : 1;
        int wantIn = map.Reports.Where(r => r.Kind == HidReportKind.Input).Max(r => r.FrameBytes) + idByte;
        int wantOut = map.Reports.Where(r => r.Kind == HidReportKind.Output).Select(r => r.FrameBytes + idByte).DefaultIfEmpty(0).Max();
        Check("the input report length is the profile's", caps.In == wantIn, $"{caps.In}, want {wantIn}");
        Check("the output report length is the profile's", caps.Out == wantOut, $"{caps.Out}, want {wantOut}");
        if (profile.Id == "joycon-l") JoyConArrows(ctrl, info.DevicePath);

        // A feature report: the calibration SDL reads first, 0x05 on a
        // DualSense and on a DualShock 4 over Bluetooth, 0x02 on a
        // DualShock 4 over USB (SDL_hidapi_ps4.c:494-510).
        var feature = new byte[] { 0x05, 0x02 }.Select(id => map.Reports.FirstOrDefault(r => r.Kind == HidReportKind.Feature && r.ReportId == id))
            .FirstOrDefault(r => r != null);
        if (feature != null && caps.Feature > 0)
        {
            var buf = new byte[caps.Feature];
            buf[0] = feature.ReportId;
            bool got = HidD_GetFeature(h, buf, buf.Length);
            Check($"feature report 0x{feature.ReportId:X2} answers", got, got ? Convert.ToHexString(buf, 0, Math.Min(16, buf.Length)) : $"error {Marshal.GetLastWin32Error()}");
        }

        // An output report reaches OutputDecoded with the values it carries.
        byte[]? output = OutputOf(profile);
        if (output != null)
        {
            var wire = new byte[caps.Out];
            Array.Copy(output, wire, Math.Min(output.Length, wire.Length));
            bool wrote = WriteFile(h, wire, (uint)wire.Length, out uint n, IntPtr.Zero);
            Check($"output report 0x{wire[0]:X2} writes", wrote && n == wire.Length, wrote ? "" : $"error {Marshal.GetLastWin32Error()}");
            // The values the report carries, each motor on its own: the
            // Sony report sets right 200 and left 100, the Switch report
            // the left motor's top amplitude and the right neutral.
            var (wantLeft, wantRight) = profile.HasExtendedOutput ? (100, 200) : DecodesLeft(profile) ? (255, 0) : (0, 255);
            bool seen = Wait(() => { lock (decoded) return decoded.Any(f => Num(f, "leftMotor") == wantLeft && Num(f, "rightMotor") == wantRight); }, 5000);
            string last;
            lock (decoded) last = decoded.Count == 0 ? "none" : string.Join(" ", decoded[^1].Where(k => k.Key.EndsWith("Motor")).Select(k => $"{k.Key}={k.Value}"));
            Check($"OutputDecoded raises leftMotor {wantLeft} and rightMotor {wantRight}", seen, last);
        }

        // Raw frames are the profile's own on either backend.
        if (profile.Inner.ExtendedReport is { } ext && profile.Inner.StateBlock == null)
        {
            var frame = new byte[ext.Size];
            frame[0] = ext.ReportIdByte;
            Exception? thrown = null;
            try { ctrl.SubmitRawExtendedReport(frame); } catch (Exception e) { thrown = e; }
            Check($"a {ext.Size}-byte raw frame of its extended report is taken", thrown == null, thrown?.Message ?? "");
        }
        if (profile.Inner.StateBlock is { } block)
        {
            Exception? thrown = null;
            try { ctrl.SubmitRawReport(new byte[block.Size]); } catch (Exception e) { thrown = e; }
            Check($"a {block.Size}-byte state block is taken", thrown == null, thrown?.Message ?? "");
        }
        Exception? submit = null;
        try { ctrl.SubmitState(new HMGamepadState { Buttons = (HMButton)1 }); } catch (Exception e) { submit = e; }
        Check("SubmitState is taken", submit == null, submit?.Message ?? "");
    }

    /// <summary>An output report of <paramref name="profile"/> that sets
    /// the motors: its extendedOutputReport encoded, or the Switch rumble
    /// report 0x10 the decoder reads.</summary>
    static byte[]? OutputOf(HMProfile profile)
    {
        if (profile.HasExtendedOutput)
            return HMOutputEncoder.Encode(profile, new Dictionary<string, object>
            {
                ["validFlag0"] = (byte)0x03, ["rightMotor"] = (byte)200, ["leftMotor"] = (byte)100,
            });
        if (profile.Inner.GetOrCompileModel().Decoders.Any(d => d.Reports.Contains((byte)0x10)))
        {
            // [id, counter, left 4 bytes, right 4 bytes]: left at the high
            // band's top amplitude 0xC8, right neutral 00 01 40 40
            // (SDL_hidapi_switch.c EncodeRumbleHighAmplitude, SetNeutralRumble).
            // A pad whose one motor reads the right half (joycon-r) gets the
            // two halves the other way round.
            return DecodesLeft(profile)
                ? new byte[] { 0x10, 0x01, 0x00, 0xC8, 0x60, 0x00, 0x00, 0x01, 0x40, 0x40 }
                : new byte[] { 0x10, 0x01, 0x00, 0x01, 0x40, 0x40, 0x00, 0xC8, 0x60, 0x00 };
        }
        return null;
    }

    static bool DecodesLeft(HMProfile profile) =>
        profile.Inner.GetOrCompileModel().Decoders.Any(d => d.Semantics.Contains("leftMotor"));

    static int Num(IReadOnlyDictionary<string, object> f, string key) =>
        f.TryGetValue(key, out var v) && v is IConvertible c ? c.ToInt32(null) : 0;

    static bool Wait(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(10); }
        return cond();
    }

    /// <summary>Every HID collection of the device with
    /// <paramref name="profile"/>'s VID and PID and this serial.</summary>
    internal static List<HMHidDeviceInfo> Collections(HMProfile profile, string serial) =>
        HMDeviceExtractor.ListDevices().Where(d => d.VendorId == profile.VendorId
            && d.ProductId == profile.ProductId && d.SerialNumberString == serial).ToList();

    /// <summary>The HID interface of <paramref name="profile"/> that carries
    /// its reports: the one interface of a single-HID device, or on the
    /// charging grip the docked pad's, whose path names its interface
    /// number (mi_01 for the left slot). Of several collections, the game
    /// controller's is taken: Windows lists a device's collections in no set
    /// order, and on the ARM64 test system (2026-11-20) amazon-luna-ble's
    /// vendor collection, col02, came first, which SDL lists as no
    /// joystick.</summary>
    internal static HMHidDeviceInfo? OwnInterface(HMProfile profile, string serial)
    {
        var all = Collections(profile, serial);
        if (all.Count <= 1) return all.FirstOrDefault();
        var hid = profile.Inner.UsbConfiguration?.Interfaces?.FirstOrDefault(i => i.Function == "hid");
        string? mi = hid == null ? null : $"&mi_{hid.InterfaceNumber:D2}";
        var candidates = mi == null ? all : all.Where(d => d.DevicePath.Contains(mi, StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.FirstOrDefault(IsGameController) ?? candidates.FirstOrDefault();
    }

    /// <summary>A Generic Desktop joystick, gamepad or multi-axis controller
    /// collection (HID Usage Tables, Generic Desktop Page 0x01, usages 0x04,
    /// 0x05 and 0x08): the collections SDL opens as joysticks.</summary>
    static bool IsGameController(HMHidDeviceInfo d) =>
        d.TopLevelUsagePage == 0x01 && d.TopLevelUsage is 0x04 or 0x05 or 0x08;

    /// <summary>The serial the controller's HID device serves.</summary>
    internal static string SerialOf(HMController c) =>
        c.Backend == HMBackend.Usbip ? c.UsbipHandle!.Device.Descriptors.SerialString! : DeviceOrchestrator.DriverSerial(c.TransportIndex);

    // ── Win32 ────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, byte[] buf, uint n, out uint written, IntPtr ov);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    internal static SafeFileHandle OpenHid(string path) =>
        CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);

    internal static (int In, int Out, int Feature) Caps(SafeFileHandle h)
    {
        if (!HidD_GetPreparsedData(h, out var pp)) return (0, 0, 0);
        try
        {
            HidP_GetCaps(pp, out var caps);
            return (caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength);
        }
        finally { HidD_FreePreparsedData(pp); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_PropertyW(string path, ref DEVPROPKEY key, out uint type, byte[]? buf, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint dn, string id, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint parent, uint dn, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint dn, ref DEVPROPKEY key, out uint type, byte[]? buf, ref uint size, uint flags);

    static readonly DEVPROPKEY s_instanceId = new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
    static readonly DEVPROPKEY s_compatibleIds = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 4 };

    /// <summary>The compatible IDs of the parent of the devnode behind a HID
    /// interface path, the property hidapi reads for the bus, in the order
    /// Windows holds them.</summary>
    internal static string[] ParentCompatibleIds(string path)
    {
        var key = s_instanceId;
        uint size = 0;
        CM_Get_Device_Interface_PropertyW(path, ref key, out _, null, ref size, 0);
        if (size == 0) return Array.Empty<string>();
        var buf = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(path, ref key, out _, buf, ref size, 0) != 0) return Array.Empty<string>();
        string id = System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
        if (CM_Locate_DevNodeW(out uint dn, id, 0) != 0 || CM_Get_Parent(out uint parent, dn, 0) != 0) return new[] { "no parent of " + id };
        key = s_compatibleIds;
        size = 0;
        CM_Get_DevNode_PropertyW(parent, ref key, out _, null, ref size, 0);
        if (size == 0) return Array.Empty<string>();
        buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(parent, ref key, out _, buf, ref size, 0) != 0) return Array.Empty<string>();
        return System.Text.Encoding.Unicode.GetString(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
