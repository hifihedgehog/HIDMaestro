// live <backend>: the HID interface each Bluetooth identity with a USB form
// presents on <backend>, with dualsense-composite on the USB/IP backend as
// the positive control in the same run.

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
    static void Live(HMBackend backend)
    {
        using var ctx = new HMContext(backend);
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        Console.WriteLine($"  context backend {backend}, machine backend {HMContext.MachineBackend}");

        foreach (var (btId, formId) in Forms)
            LiveOne(ctx, ctx.GetProfile(btId)!, backend);
        // The positive control: a USB persona the USB/IP backend has always
        // presented, in the same run.
        LiveOne(ctx, ctx.GetProfile("dualsense-composite")!, HMBackend.Usbip);
    }

    static void LiveOne(HMContext ctx, HMProfile profile, HMBackend backend)
    {
        Console.WriteLine($"\n-- {profile.Id} on {backend} --");
        using var ctrl = ctx.CreateController(profile, null, backend);
        Check("the controller runs on the backend asked for", ctrl.Backend == backend, ctrl.Backend.ToString());
        var form = profile.FormOn(ctrl.Backend);
        Console.WriteLine($"  presents {form.Id}");

        var decoded = new List<IReadOnlyDictionary<string, object>>();
        ctrl.OutputDecoded += (_, e) => { lock (decoded) decoded.Add(e.Fields); };

        string serial = SerialOf(ctrl);
        HMHidDeviceInfo? info = null;
        var sw = Stopwatch.StartNew();
        while (info == null && sw.ElapsedMilliseconds < 15000)
        {
            // The presented form's PID: the Genesis pad carries another on USB.
            info = HMDeviceExtractor.ListDevices().FirstOrDefault(d => d.VendorId == form.VendorId
                && d.ProductId == form.ProductId && d.SerialNumberString == serial);
            if (info == null) Thread.Sleep(50);
        }
        Check($"the HID interface enumerates (serial {serial})", info != null, info?.DevicePath ?? "");
        if (info == null) return;

        // The bus hidapi reads: the compatible IDs of the HID devnode's
        // parent, USB tested first (SDL 3.4.18 hid.c:759-793).
        var compat = ParentCompatibleIds(info.DevicePath);
        string bus = compat.Any(c => c.Contains("USB", StringComparison.OrdinalIgnoreCase)) ? "usb"
            : compat.Any(c => c.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) || c.Contains("BTHLEDEVICE", StringComparison.OrdinalIgnoreCase)) ? "bluetooth" : "unknown";
        bool wantBt = ctrl.Backend == HMBackend.Umdf2 && string.Equals(profile.Connection, "bluetooth", StringComparison.OrdinalIgnoreCase);
        Check($"hidapi reads the bus as {(wantBt ? "bluetooth" : "usb")}", bus == (wantBt ? "bluetooth" : "usb"),
            $"parent compatible IDs: {string.Join(" | ", compat)}");

        using var h = OpenHid(info.DevicePath);
        Check("the HID interface opens read/write", !h.IsInvalid, $"error {Marshal.GetLastWin32Error()}");
        if (h.IsInvalid) return;
        var caps = Caps(h);
        var map = form.Inner.GetOrBuildReportMap();
        int wantIn = map.Reports.Where(r => r.Kind == HidReportKind.Input).Max(r => r.FrameBytes);
        int wantOut = map.Reports.Where(r => r.Kind == HidReportKind.Output).Select(r => r.FrameBytes).DefaultIfEmpty(0).Max();
        Check($"input report length is {form.Id}'s", caps.In == wantIn, $"{caps.In}, want {wantIn}");
        Check($"output report length is {form.Id}'s", caps.Out == wantOut, $"{caps.Out}, want {wantOut}");
        ushort wantVersion = form.Inner.VersionNumber ?? 0x0100;
        Check($"version is {form.Id}'s", info.VersionNumber == wantVersion, $"0x{info.VersionNumber:X4}, want 0x{wantVersion:X4}");
        Check("the product string is the profile's", info.ProductString == profile.ProductString, $"'{info.ProductString}'");

        // A feature report of the form: the calibration SDL reads first, 0x05
        // on a DualSense and on a DualShock 4 over Bluetooth, 0x02 on a
        // DualShock 4 over USB (SDL_hidapi_ps4.c:494-510).
        var feature = new byte[] { 0x05, 0x02 }.Select(id => map.Reports.FirstOrDefault(r => r.Kind == HidReportKind.Feature && r.ReportId == id))
            .FirstOrDefault(r => r != null);
        if (feature != null && caps.Feature > 0)
        {
            var buf = new byte[caps.Feature];
            buf[0] = feature.ReportId;
            bool got = HidD_GetFeature(h, buf, buf.Length);
            Check($"feature report 0x{feature.ReportId:X2} of {form.Id} answers", got, got ? Convert.ToHexString(buf, 0, Math.Min(16, buf.Length)) : $"error {Marshal.GetLastWin32Error()}");
        }

        // An output report of the form reaches OutputDecoded with the values
        // it carries.
        byte[]? output = OutputOf(form);
        if (output != null)
        {
            var wire = new byte[caps.Out];
            Array.Copy(output, wire, Math.Min(output.Length, wire.Length));
            bool wrote = WriteFile(h, wire, (uint)wire.Length, out uint n, IntPtr.Zero);
            Check($"output report 0x{wire[0]:X2} of {form.Id} writes", wrote && n == wire.Length, wrote ? "" : $"error {Marshal.GetLastWin32Error()}");
            // The values the report carries, each motor on its own: the
            // Sony report sets right 200 and left 100, the Switch report
            // the left motor's top amplitude and the right neutral.
            var (wantLeft, wantRight) = form.HasExtendedOutput ? (100, 200) : (255, 0);
            bool seen = Wait(() => { lock (decoded) return decoded.Any(f => Num(f, "leftMotor") == wantLeft && Num(f, "rightMotor") == wantRight); }, 5000);
            string last;
            lock (decoded) last = decoded.Count == 0 ? "none" : string.Join(" ", decoded[^1].Where(k => k.Key.EndsWith("Motor")).Select(k => $"{k.Key}={k.Value}"));
            Check($"OutputDecoded raises leftMotor {wantLeft} and rightMotor {wantRight}", seen, last);
        }

        // Raw frames follow the presented form.
        bool other = !ReferenceEquals(form, profile);
        if (profile.Inner.ExtendedReport is { } ext && profile.Inner.StateBlock == null && !ReferenceEquals(form.Inner.ExtendedReport, null))
        {
            var btFrame = new byte[ext.Size];
            btFrame[0] = ext.ReportIdByte;
            Exception? thrown = null;
            try { ctrl.SubmitRawExtendedReport(btFrame); } catch (Exception e) { thrown = e; }
            if (other)
                Check($"a {ext.Size}-byte Bluetooth-form raw frame is refused with the form named",
                    thrown is ArgumentException a && a.Message.Contains(form.Id), thrown?.Message ?? "accepted");
            else
                Check($"a {ext.Size}-byte raw frame of its own form is taken", thrown == null, thrown?.Message ?? "");
            var own = new byte[form.Inner.ExtendedReport!.Size];
            own[0] = form.Inner.ExtendedReport.ReportIdByte;
            thrown = null;
            try { ctrl.SubmitRawExtendedReport(own); } catch (Exception e) { thrown = e; }
            Check($"a {own.Length}-byte raw frame of {form.Id} is taken", thrown == null, thrown?.Message ?? "");
        }
        if (profile.Inner.StateBlock is { } block)
        {
            Exception? thrown = null;
            try { ctrl.SubmitRawReport(new byte[block.Size]); } catch (Exception e) { thrown = e; }
            Check($"a {block.Size}-byte state block, the same in both forms, is taken", thrown == null, thrown?.Message ?? "");
        }
        Exception? submit = null;
        try { ctrl.SubmitState(new HMGamepadState { Buttons = (HMButton)1 }); } catch (Exception e) { submit = e; }
        Check("SubmitState is taken", submit == null, submit?.Message ?? "");
    }

    /// <summary>An output report of <paramref name="form"/> that sets the
    /// motors: its extendedOutputReport encoded, or the Switch rumble report
    /// 0x10 the decoder reads.</summary>
    static byte[]? OutputOf(HMProfile form)
    {
        if (form.HasExtendedOutput)
            return HMOutputEncoder.Encode(form, new Dictionary<string, object>
            {
                ["validFlag0"] = (byte)0x03, ["rightMotor"] = (byte)200, ["leftMotor"] = (byte)100,
            });
        if (form.Inner.GetOrCompileModel().Decoders.Any(d => d.Reports.Contains((byte)0x10)))
        {
            // [id, counter, left 4 bytes, right 4 bytes]: left at the high
            // band's top amplitude 0xC8, right neutral 00 01 40 40
            // (SDL_hidapi_switch.c EncodeRumbleHighAmplitude, SetNeutralRumble).
            return new byte[] { 0x10, 0x01, 0x00, 0xC8, 0x60, 0x00, 0x00, 0x01, 0x40, 0x40 };
        }
        return null;
    }

    static int Num(IReadOnlyDictionary<string, object> f, string key) =>
        f.TryGetValue(key, out var v) && v is IConvertible c ? c.ToInt32(null) : 0;

    static bool Wait(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(10); }
        return cond();
    }

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
    /// interface path, the property hidapi reads for the bus.</summary>
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
