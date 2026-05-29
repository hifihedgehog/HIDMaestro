// Real-input passthrough neutralization check.
//
// Reads a real controller through XInput, mirrors that state into virtual
// HIDMaestro controllers, and verifies the virtual HID input report:
//   - Neutralized=false: virtual axes/buttons match the sampled real state.
//   - Neutralized=true: virtual stays neutral while the real input is still
//     being submitted.
//
// Profiles covered: DS4 USB, DualSense USB, Switch Pro.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal sealed class Program
{
    private const uint ERROR_SUCCESS = 0;
    private static int s_total;
    private static int s_failures;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("hid.dll", SetLastError = true)]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES attr);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetInputReport(IntPtr h, byte[] buf, int len);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr devInfoData, ref Guid classGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr h, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, out uint required, IntPtr devInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr h, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, IntPtr required, IntPtr devInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string fn, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    private sealed record RealSample(int Slot, XINPUT_STATE State, HMGamepadState HMState);

    private static void Check(string label, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    private static string FormatReal(in XINPUT_STATE s)
    {
        var g = s.Gamepad;
        return $"pkt={s.dwPacketNumber} btn=0x{g.wButtons:X4} LT={g.bLeftTrigger} RT={g.bRightTrigger} " +
               $"LX={g.sThumbLX} LY={g.sThumbLY} RX={g.sThumbRX} RY={g.sThumbRY}";
    }

    private static float Axis(short value) => ((int)value + 32768) / 65535f;
    private static float Trigger(byte value) => value / 255f;

    private static bool RealIsActive(in XINPUT_STATE s)
    {
        var g = s.Gamepad;
        return g.wButtons != 0
            || g.bLeftTrigger > 8
            || g.bRightTrigger > 8
            || Math.Abs((int)g.sThumbLX) > 4000
            || Math.Abs((int)g.sThumbLY) > 4000
            || Math.Abs((int)g.sThumbRX) > 4000
            || Math.Abs((int)g.sThumbRY) > 4000;
    }

    private static HMButton ConvertButtons(ushort x)
    {
        HMButton b = HMButton.None;
        if ((x & 0x1000) != 0) b |= HMButton.A;
        if ((x & 0x2000) != 0) b |= HMButton.B;
        if ((x & 0x4000) != 0) b |= HMButton.X;
        if ((x & 0x8000) != 0) b |= HMButton.Y;
        if ((x & 0x0100) != 0) b |= HMButton.LeftBumper;
        if ((x & 0x0200) != 0) b |= HMButton.RightBumper;
        if ((x & 0x0020) != 0) b |= HMButton.Back;
        if ((x & 0x0010) != 0) b |= HMButton.Start;
        if ((x & 0x0040) != 0) b |= HMButton.LeftStick;
        if ((x & 0x0080) != 0) b |= HMButton.RightStick;
        if ((x & 0x0400) != 0) b |= HMButton.Guide;
        return b;
    }

    private static HMHat ConvertHat(ushort x)
    {
        bool up = (x & 0x0001) != 0, down = (x & 0x0002) != 0;
        bool left = (x & 0x0004) != 0, right = (x & 0x0008) != 0;
        if (up && right) return HMHat.NorthEast;
        if (down && right) return HMHat.SouthEast;
        if (down && left) return HMHat.SouthWest;
        if (up && left) return HMHat.NorthWest;
        if (up) return HMHat.North;
        if (right) return HMHat.East;
        if (down) return HMHat.South;
        if (left) return HMHat.West;
        return HMHat.None;
    }

    private static HMGamepadState ConvertRealToHM(HMProfile targetProfile, in XINPUT_STATE real)
    {
        var g = real.Gamepad;
        return new HMGamepadState
        {
            Axes = HMGamepadStateHelpers.StandardAxes(targetProfile,
                leftStickX: Axis(g.sThumbLX),
                leftStickY: Axis(g.sThumbLY),
                rightStickX: Axis(g.sThumbRX),
                rightStickY: Axis(g.sThumbRY),
                leftTrigger: Trigger(g.bLeftTrigger),
                rightTrigger: Trigger(g.bRightTrigger)),
            Buttons = ConvertButtons(g.wButtons),
            Hat = ConvertHat(g.wButtons),
        };
    }

    private static RealSample WaitForRealActive(HMProfile targetProfile, int timeoutMs)
    {
        Console.WriteLine();
        Console.WriteLine("  ===============================================================");
        Console.WriteLine("  ESPERANDO INPUT DO CONTROLE REAL");
        Console.WriteLine("  Mexa um analógico ou segure qualquer botão agora.");
        Console.WriteLine("  O teste continua automaticamente quando detectar input ativo.");
        Console.WriteLine("  ===============================================================");
        Console.WriteLine();
        try { Console.Beep(880, 180); Console.Beep(660, 180); } catch { }
        var sw = Stopwatch.StartNew();
        XINPUT_STATE last = default;
        int lastSlot = -1;
        long nextStatusMs = 0;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                if (XInputGetState((uint)slot, out var state) != ERROR_SUCCESS) continue;
                last = state;
                lastSlot = slot;
                if (!RealIsActive(in state)) continue;
                Console.WriteLine($"  Input real detectado no slot {slot}: {FormatReal(in state)}");
                return new RealSample(slot, state, ConvertRealToHM(targetProfile, in state));
            }
            if (sw.ElapsedMilliseconds >= nextStatusMs)
            {
                int remaining = Math.Max(0, (timeoutMs - (int)sw.ElapsedMilliseconds) / 1000);
                Console.WriteLine($"  ...ainda esperando input real ({remaining}s restantes). Último slot={lastSlot}, {FormatReal(in last)}");
                nextStatusMs = sw.ElapsedMilliseconds + 1000;
            }
            Thread.Sleep(50);
        }
        throw new InvalidOperationException($"No active real XInput input observed within {timeoutMs} ms. Last slot={lastSlot}, state={FormatReal(in last)}");
    }

    private static string? FindHidDevicePath(ushort vid, ushort pid)
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

    private static byte[]? GetCurrentInputReport(string path, int reportLen, byte reportId)
    {
        IntPtr fh = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (fh == INVALID_HANDLE_VALUE) return null;
        try
        {
            byte[] buf = new byte[reportLen];
            buf[0] = reportId;
            return HidD_GetInputReport(fh, buf, buf.Length) ? buf : null;
        }
        finally { CloseHandle(fh); }
    }

    private static int ReadField(byte[] report, HidReportBuilder.InputField field, byte reportId)
    {
        int idOffset = reportId != 0 ? 8 : 0;
        int bit = field.BitOffset + idOffset;
        long value = 0;
        for (int i = 0; i < field.BitSize; i++)
        {
            int b = (bit + i) / 8;
            int sh = (bit + i) % 8;
            if ((report[b] & (1 << sh)) != 0) value |= 1L << i;
        }
        return (int)value;
    }

    private static int ExpectedAxisRaw(HidReportBuilder.InputField field, float normalized)
    {
        double n = Math.Clamp(normalized, 0f, 1f);
        return (int)Math.Round(field.LogicalMin + n * (field.LogicalMax - field.LogicalMin));
    }

    private static bool AxisMatches(byte[] report, byte reportId, HidReportBuilder.InputField? field, float expected, out string detail)
    {
        detail = "";
        if (field == null) return true;
        int got = ReadField(report, field, reportId);
        int want = ExpectedAxisRaw(field, expected);
        int tolerance = Math.Max(3, (field.LogicalMax - field.LogicalMin) / 128);
        bool ok = Math.Abs(got - want) <= tolerance;
        detail = $"usage=0x{field.UsagePage:X2}:0x{field.Usage:X2} got={got} want~={want} tol={tolerance}";
        return ok;
    }

    private static bool FieldIsNeutral(byte[] report, byte reportId, HidReportBuilder.InputField? field, bool released, out string detail)
    {
        detail = "";
        if (field == null) return true;
        int got = ReadField(report, field, reportId);
        int want = released ? field.LogicalMin : ExpectedAxisRaw(field, 0.5f);
        int tolerance = Math.Max(3, (field.LogicalMax - field.LogicalMin) / 128);
        bool ok = Math.Abs(got - want) <= tolerance;
        detail = $"usage=0x{field.UsagePage:X2}:0x{field.Usage:X2} got={got} neutral~={want} tol={tolerance}";
        return ok;
    }

    private static bool ButtonMatches(byte[] report, byte reportId, HidReportBuilder rb, HMButton expected, out string detail)
    {
        var problems = new List<string>();
        for (int bit = 0; bit < 13; bit++)
        {
            int desc = rb.ButtonMap != null && bit < rb.ButtonMap.Length ? rb.ButtonMap[bit] : bit;
            if ((uint)desc >= (uint)rb.Buttons.Count) continue;
            int got = ReadField(report, rb.Buttons[desc], reportId);
            int want = (((uint)expected & (1u << bit)) != 0) ? 1 : 0;
            if ((got != 0 ? 1 : 0) != want)
                problems.Add($"HMButton[{bit}] desc={desc} got={got} want={want}");
        }
        detail = string.Join("; ", problems);
        return problems.Count == 0;
    }

    private static bool ButtonsNeutral(byte[] report, byte reportId, HidReportBuilder rb, out string detail)
    {
        var pressed = new List<string>();
        for (int i = 0; i < rb.Buttons.Count; i++)
        {
            int got = ReadField(report, rb.Buttons[i], reportId);
            if (got != 0) pressed.Add($"descButton[{i}]={got}");
        }
        detail = string.Join("; ", pressed);
        return pressed.Count == 0;
    }

    private static bool HatNeutral(byte[] report, byte reportId, HidReportBuilder rb, out string detail)
    {
        detail = "";
        if (rb.HatSwitch == null) return true;
        int got = ReadField(report, rb.HatSwitch, reportId);
        int neutral = rb.HatSwitch.LogicalMin == 0 ? rb.HatSwitch.LogicalMax + 1 : 0;
        bool ok = got == neutral;
        detail = $"hat got={got} neutral={neutral}";
        return ok;
    }

    private static bool MirrorMatchesReal(HMProfile profile, HidReportBuilder rb, byte[] report, RealSample sample, out string detail)
    {
        var axes = sample.HMState.Axes!;
        bool ok = true;
        var details = new List<string>();
        void Axis(string name, HidReportBuilder.InputField? field, HMAxis axis)
        {
            if (field == null || axis == HMAxis.None) return;
            float expected = axes.TryGetValue(axis, out var v) ? v : (field.LogicalMin < 0 ? 0.5f : 0f);
            if (!AxisMatches(report, rb.InputReportId, field, expected, out string d))
            {
                ok = false;
                details.Add($"{name}: {d}");
            }
        }

        var sticks = profile.Sticks;
        var triggers = profile.Triggers;
        Axis("LSX", rb.LeftStickX, sticks.Count > 0 ? sticks[0].XAxis : HMAxis.None);
        Axis("LSY", rb.LeftStickY, sticks.Count > 0 ? sticks[0].YAxis : HMAxis.None);
        Axis("RSX", rb.RightStickX, sticks.Count > 1 ? sticks[1].XAxis : HMAxis.None);
        Axis("RSY", rb.RightStickY, sticks.Count > 1 ? sticks[1].YAxis : HMAxis.None);
        Axis("LT", rb.LeftTrigger, triggers.Count > 0 ? triggers[0].Axis : HMAxis.None);
        Axis("RT", rb.RightTrigger, triggers.Count > 1 ? triggers[1].Axis : HMAxis.None);

        // Axis fidelity is the key passthrough invariant here. Button maps are
        // profile-specific (Sony L2/R2 can be derived from analog triggers), so
        // keep button comparison best-effort and only assert it when the source
        // has explicit digital buttons pressed.
        if (sample.HMState.Buttons != HMButton.None
            && !ButtonMatches(report, rb.InputReportId, rb, sample.HMState.Buttons, out string btnDetail))
        {
            ok = false;
            details.Add("buttons: " + btnDetail);
        }

        detail = string.Join(" | ", details);
        return ok;
    }

    private static bool VerifyMirrorsReal(HMProfile profile, HidReportBuilder rb, byte[] report, RealSample sample, string label)
    {
        bool ok = MirrorMatchesReal(profile, rb, report, sample, out string detail);
        var real = sample.State;
        Check(label, ok, ok ? FormatReal(in real) : detail);
        return ok;
    }

    private static bool VerifyNeutral(HidReportBuilder rb, byte[] report, string label)
    {
        bool ok = true;
        var details = new List<string>();
        void NeutralAxis(string name, HidReportBuilder.InputField? field, bool released)
        {
            if (!FieldIsNeutral(report, rb.InputReportId, field, released, out string d))
            {
                ok = false;
                details.Add($"{name}: {d}");
            }
        }

        NeutralAxis("LSX", rb.LeftStickX, released: false);
        NeutralAxis("LSY", rb.LeftStickY, released: false);
        NeutralAxis("RSX", rb.RightStickX, released: false);
        NeutralAxis("RSY", rb.RightStickY, released: false);
        NeutralAxis("LT", rb.LeftTrigger, released: true);
        NeutralAxis("RT", rb.RightTrigger, released: true);
        if (!ButtonsNeutral(report, rb.InputReportId, rb, out string btnDetail))
        {
            ok = false;
            details.Add("buttons: " + btnDetail);
        }
        if (!HatNeutral(report, rb.InputReportId, rb, out string hatDetail))
        {
            ok = false;
            details.Add(hatDetail);
        }

        Check(label, ok, ok ? "wire report is neutral" : string.Join(" | ", details));
        return ok;
    }

    private static bool WaitForMirror(HMController ctrl, HMProfile profile, HidReportBuilder rb,
        string path, int reportSize, RealSample sample, string label, int timeoutMs = 1500)
    {
        var sw = Stopwatch.StartNew();
        byte[]? last = null;
        var state = sample.HMState;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ctrl.SubmitState(in state);
            Thread.Sleep(35);
            last = GetCurrentInputReport(path, reportSize, rb.InputReportId);
            if (last == null) continue;

            if (MirrorMatchesReal(profile, rb, last, sample, out _))
                return VerifyMirrorsReal(profile, rb, last, sample, label);
        }

        if (last == null)
        {
            Check(label, false, "GetInputReport returned null until timeout");
            return false;
        }
        return VerifyMirrorsReal(profile, rb, last, sample, label);
    }

    private static void TestProfile(HMContext ctx, string profileId)
    {
        var profile = ctx.GetProfile(profileId) ?? throw new Exception($"missing profile: {profileId}");
        Console.WriteLine($"\n=== Profile {profile.Id} ({profile.Name}) VID={profile.VendorId:X4} PID={profile.ProductId:X4} ===");

        var descriptor = profile.GetDescriptorBytes() ?? throw new Exception($"profile {profile.Id} has no descriptor");
        var rb = HidReportBuilder.Parse(descriptor, profile.AxisMap);
        rb.ButtonMap = profile.ButtonMap;
        if (profile.Layout != null) rb.ApplyLayoutSemantics(profile.Layout);
        RealSample sample = WaitForRealActive(profile, timeoutMs: 120_000);
        var realStateForLog = sample.State;
        Console.WriteLine($"  real source slot={sample.Slot}: {FormatReal(in realStateForLog)}");

        using var ctrl = ctx.CreateController(profile);
        Thread.Sleep(800);
        string? path = FindHidDevicePath(profile.VendorId, profile.ProductId);
        Check($"{profile.Id}: virtual HID path resolved", path != null, path ?? "");
        if (path == null) return;

        ctrl.Neutralized = false;
        var sampleState = sample.HMState;
        ctrl.SubmitState(in sampleState);
        Thread.Sleep(60);
        int reportSize = profile.InputReportSize > 0 ? profile.InputReportSize : rb.InputReportByteSize;
        byte[]? report = GetCurrentInputReport(path, reportSize, rb.InputReportId);
        Check($"{profile.Id}: GetInputReport while neutral off", report != null);
        if (report != null)
            VerifyMirrorsReal(profile, rb, report, sample, $"{profile.Id}: neutral off mirrors sampled real input");

        ctrl.Neutralized = true;
        // Keep feeding the active real sample while neutralized.
        for (int i = 0; i < 5; i++)
        {
            ctrl.SubmitState(in sampleState);
            Thread.Sleep(25);
        }
        report = GetCurrentInputReport(path, reportSize, rb.InputReportId);
        Check($"{profile.Id}: GetInputReport while neutral on", report != null);
        if (report != null)
            VerifyNeutral(rb, report, $"{profile.Id}: neutral on suppresses real input");

        ctrl.Neutralized = false;
        WaitForMirror(ctrl, profile, rb, path, reportSize, sample,
            $"{profile.Id}: neutral off restores real input");
    }

    public static int Main(string[] args)
    {
        Console.WriteLine("=== HIDMaestro real-input passthrough neutral regression ===");
        Console.WriteLine("Move/press your real XInput gamepad when prompted so the test has non-neutral source data.");

        string[] profiles = args.Length > 0
            ? args
            : new[] { "dualshock-4-v2", "dualsense", "switch-pro" };

        try { HMContext.RemoveAllVirtualControllers(); } catch { }
        Thread.Sleep(500);

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        Console.Write("Installing driver... ");
        ctx.InstallDriver();
        Console.WriteLine("OK");

        foreach (string profile in profiles)
        {
            TestProfile(ctx, profile);
        }

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} PASS ===");
        return s_failures == 0 ? 0 : 1;
    }
}
