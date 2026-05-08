// Issue #22 — END-TO-END trigger classifier check.
//
// Creates a real virtual controller via HMContext.CreateController with a
// PadForge-style custom layout (configurable sticks/triggers), submits state
// via SubmitState, opens the device's HID input report via Win32, and reads
// back the wire bytes to verify the trigger axis encodes correctly across
// the full SubmitState → BuildReportInto → shared memory → driver →
// HidClass round trip. The unit-only TriggerClassifierCheck probe verifies
// BuildReportInto in isolation; this probe verifies the live wire.
//
// Layouts exercised (matching PadForge's variable-layout custom profile):
//   - (2 sticks, 1 trigger, hat, 11 buttons, FFB)  → issue #22 case 2 shape
//   - (1 stick, 1 trigger, hat, 11 buttons, FFB)   → issue #22 case 1 shape
//   - (2 sticks, 2 triggers, hat, 11 buttons, FFB) → PadForge default custom
//
// For each layout: submit LT=0.0, 0.5, 1.0; open HID interface; assert the
// LeftTrigger field on the wire matches the expected wire value to within
// rounding. Same for RT when present.
//
// Requires admin (driver install + virtual creation).
// Exit 0 on PASS, 1 on FAIL.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal sealed class Program
{
    static int s_total = 0;
    static int s_failures = 0;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES attr);

    [DllImport("hid.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool HidD_GetProductString(IntPtr h, [MarshalAs(UnmanagedType.LPArray)] byte[] buf, int len);

    [DllImport("hid.dll", SetLastError = true)]
    static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetInputReport(IntPtr h, byte[] buf, int len);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr devInfoData, ref Guid classGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr h, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, out uint required, IntPtr devInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr h, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, IntPtr required, IntPtr devInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string fn, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    const uint DIGCF_PRESENT = 0x02;
    const uint DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 0x01;
    const uint FILE_SHARE_WRITE = 0x02;
    const uint OPEN_EXISTING = 3;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    static string? FindHidDevicePath(ushort vid, ushort pid)
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr h = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (h == INVALID_HANDLE_VALUE) return null;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(h, IntPtr.Zero, ref hidGuid, i, ref data)) break;
                SetupDiGetDeviceInterfaceDetailW(h, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(h, ref data, buf, required, IntPtr.Zero, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(IntPtr.Add(buf, 4))!;
                    IntPtr fh = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                    if (fh == INVALID_HANDLE_VALUE) continue;
                    try
                    {
                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (HidD_GetAttributes(fh, ref attr) && attr.VendorID == vid && attr.ProductID == pid)
                            return path;
                    }
                    finally { CloseHandle(fh); }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
        return null;
    }

    static byte[]? GetCurrentInputReport(string path, int reportLen, byte reportId)
    {
        IntPtr fh = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (fh == INVALID_HANDLE_VALUE) return null;
        try
        {
            byte[] buf = new byte[reportLen];
            buf[0] = reportId;
            if (!HidD_GetInputReport(fh, buf, buf.Length)) return null;
            return buf;
        }
        finally { CloseHandle(fh); }
    }

    static int ReadField(byte[] report, HidReportBuilder.InputField field, byte reportId)
    {
        // The HID report read via HidD_GetInputReport has the Report ID at
        // index 0 when InputReportId != 0. Field bit offsets are descriptor-
        // relative (no Report ID byte). Add 8 if the wire has the RID prefix.
        int idOffset = reportId != 0 ? 8 : 0;
        int bit = field.BitOffset + idOffset;
        int size = field.BitSize;
        long value = 0;
        for (int i = 0; i < size; i++)
        {
            int b = (bit + i) / 8;
            int sh = (bit + i) % 8;
            if ((report[b] & (1 << sh)) != 0)
                value |= 1L << i;
        }
        return (int)value;
    }

    static void TestLayout(HMContext ctx, int sticks, int triggers, int povs, int buttons, bool ffb)
    {
        string label = $"({sticks}s,{triggers}t,{(povs > 0 ? "hat" : "nohat")},{buttons}btn,{(ffb ? "ffb" : "noffb")})";
        Console.WriteLine($"\n--- Layout {label} ---");

        // Build profile via HidDescriptorBuilder + HMProfileBuilder, exactly
        // like PadForge.HMaestroProfileCatalog.BuildCustomProfile and the
        // variable-layout path in InputManager.Step5.VirtualDevices.cs.
        var d = new HidDescriptorBuilder().Joystick();
        for (int s = 0; s < sticks; s++) d.AddStick(s == 0 ? "Left" : "Right", 16);
        for (int t = 0; t < triggers; t++) d.AddTrigger(t == 0 ? "Left" : "Right", 16);
        if (povs > 0) d.AddHat();
        if (buttons > 0) d.AddButtons(buttons);
        if (ffb) d.AddPidFfbBlock();

        ushort vid = 0xBEEF;
        ushort pid = (ushort)(0xF000 + (sticks << 4) + triggers);
        var profile = new HMProfileBuilder()
            .Id($"trigger-live-{sticks}s-{triggers}t-{(ffb ? "ffb" : "noffb")}")
            .Name($"Trigger Live Check {label}")
            .Vendor("Custom")
            .Vid(vid).Pid(pid)
            .ProductString($"TriggerLive-{sticks}s{triggers}t")
            .ManufacturerString("HIDMaestro")
            .Type("gamepad")
            .Connection("usb")
            .FromDescriptorBuilder(d)
            .Build();

        using var ctrl = ctx.CreateController(profile);

        // Settle. Driver needs a moment to finalize the HID interface.
        Thread.Sleep(800);

        string? path = FindHidDevicePath(vid, pid);
        Check($"{label} HID device path resolved", path != null);
        if (path == null) return;

        var rb = profile.Inner.GetOrBuildReportBuilder();
        int reportLen = profile.InputReportSize;

        // Sweep LT.
        if (rb.LeftTrigger != null)
        {
            (double input, int expected)[] sweeps =
            {
                (0.0, 0),
                (0.5, rb.LeftTrigger.LogicalMax / 2),
                (1.0, rb.LeftTrigger.LogicalMax),
            };
            foreach (var (input, expected) in sweeps)
            {
                ctrl.SubmitState(new HMGamepadState
                {
                    Axes = HMGamepadStateHelpers.StandardAxes(ctrl.Profile, leftTrigger: (float)input)
                });
                Thread.Sleep(40);
                byte[]? wire = GetCurrentInputReport(path, reportLen, rb.InputReportId);
                if (wire == null) { Check($"{label} LT={input}: GetInputReport", false); continue; }
                int got = ReadField(wire, rb.LeftTrigger, rb.InputReportId);
                Check($"{label} LT={input:F1} → wire {got} ~= {expected}",
                      Math.Abs(got - expected) <= 2,
                      $"got {got}, expected ~{expected}");
            }
        }
        // Sweep RT if declared.
        if (rb.RightTrigger != null)
        {
            (double input, int expected)[] sweeps =
            {
                (0.0, 0),
                (1.0, rb.RightTrigger.LogicalMax),
            };
            foreach (var (input, expected) in sweeps)
            {
                ctrl.SubmitState(new HMGamepadState
                {
                    Axes = HMGamepadStateHelpers.StandardAxes(ctrl.Profile, rightTrigger: (float)input)
                });
                Thread.Sleep(40);
                byte[]? wire = GetCurrentInputReport(path, reportLen, rb.InputReportId);
                if (wire == null) { Check($"{label} RT={input}: GetInputReport", false); continue; }
                int got = ReadField(wire, rb.RightTrigger, rb.InputReportId);
                Check($"{label} RT={input:F1} → wire {got} ~= {expected}",
                      Math.Abs(got - expected) <= 2,
                      $"got {got}, expected ~{expected}");
            }
        }
        // Stick mid + max checks (regression on builder-built sticks).
        if (rb.LeftStickX != null)
        {
            // v1.3.9 — sticks are uniformly [0..1] (1.0 = full right / max).
            ctrl.SubmitState(new HMGamepadState
            {
                Axes = HMGamepadStateHelpers.StandardAxes(ctrl.Profile, leftStickX: 1.0f)
            });
            Thread.Sleep(40);
            byte[]? wire = GetCurrentInputReport(path, reportLen, rb.InputReportId);
            if (wire != null)
            {
                int got = ReadField(wire, rb.LeftStickX, rb.InputReportId);
                Check($"{label} LSX=1.0 → wire {got} = {rb.LeftStickX.LogicalMax}",
                      got == rb.LeftStickX.LogicalMax,
                      $"got {got}, expected {rb.LeftStickX.LogicalMax}");
            }
        }
    }

    public static int Main()
    {
        Console.WriteLine("=== Issue #22 trigger live-wire check ===");
        Console.WriteLine("Creates real virtual controllers via HMContext.CreateController.");
        Console.WriteLine("Reads HID input reports via HidD_GetInputReport.");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        // The three layouts the user reported broken: PadForge's standard
        // (2,2) custom + the (2,1) and (1,1) Custom-Extended variants.
        TestLayout(ctx, 2, 2, 1, 11, true);
        TestLayout(ctx, 2, 1, 1, 11, true);
        TestLayout(ctx, 1, 1, 1, 11, true);

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} PASS ===");
        return s_failures == 0 ? 0 : 1;
    }
}
