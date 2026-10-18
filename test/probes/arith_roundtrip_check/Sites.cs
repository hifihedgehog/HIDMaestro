// The conversions the one arithmetic covers (design note
// docs/design/data-driven-devices.md, decision (a)), each with the formula
// 9bd6279 used and the SDK's own code today.
//
// "Old" is each site as 9bd6279 computes it, transliterated line for line
// from the code it names. "New" is the SDK's WireArith, the function every
// one of these sites calls now, bound by name (NewArith) so the probe also
// builds and runs its round trip on a tree from before it. "Tree" runs the
// SDK's own code path for the site end to end, so the round trip proves the
// site calls the arithmetic the way the formula says.
using System;
using System.Collections.Generic;
using HIDMaestro;
using HIDMaestro.Internal;

/// <summary>The SDK's WireArith and the XUSB slice's trigger, bound by
/// name. Absent on a tree from before the one arithmetic, where only the
/// round trip runs.</summary>
internal static class NewArith
{
    const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static
        | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;

    static readonly Func<double, int, int, bool, double, int>? s_linear =
        typeof(HMController).Assembly.GetType("HIDMaestro.Internal.WireArith")?.GetMethod("Linear", Any)
            ?.CreateDelegate<Func<double, int, int, bool, double, int>>();
    static readonly Func<double, ushort>? s_xusbTrigger =
        typeof(HMController).GetMethod("XusbTrigger", Any)?.CreateDelegate<Func<double, ushort>>();
    static readonly Func<float, double, double, short>? s_scaled =
        typeof(HMController).Assembly.GetType("HIDMaestro.Internal.WireArith")?.GetMethod("Scaled", Any)
            ?.CreateDelegate<Func<float, double, double, short>>();

    static readonly Func<double, int, int, int, double, double, bool, int>? s_centered =
        typeof(HMController).Assembly.GetType("HIDMaestro.Internal.WireArith")?.GetMethod("Centered", Any)
            ?.CreateDelegate<Func<double, int, int, int, double, double, bool, int>>();
    static readonly Func<int, int, int, (double, double)>? s_slopes =
        typeof(HMController).Assembly.GetType("HIDMaestro.Internal.WireArith")?.GetMethod("Slopes", Any)
            ?.CreateDelegate<Func<int, int, int, (double, double)>>();

    public static bool Present => s_linear != null && s_xusbTrigger != null && s_scaled != null;
    public static bool CenteredPresent => s_centered != null && s_slopes != null;

    /// <summary>WireArith.Centered with the field's own slopes, for a double
    /// input the SDK's float axes cannot carry.</summary>
    public static int Centered(double v, int min, int center, int max)
    {
        var (low, step) = s_slopes!(min, center, max);
        return s_centered!(v, min, center, max, low, step, false);
    }

    // The round trip sends no NaN, so the rest value never applies.
    public static int Linear(double v, int min, int max, bool invert) => s_linear!(v, min, max, invert, 0.5);
    public static int XusbTrigger(double v) => s_xusbTrigger!(v);
    public static short Scaled(float v, double n, double d) => s_scaled!(v, n, d);
}

internal static class Old
{
    // HidReportBuilder.WriteField at 9bd6279 (HidReportBuilder.cs:798):
    // truncation of a double.
    public static int Descriptor(float v, int min, int max)
    {
        double n = Math.Clamp(v, 0f, 1f);
        int raw = (int)(n * (max - min) + min);
        return Math.Clamp(raw, min, max);
    }

    // VendorBlobCodec at 9bd6279: float products (VendorBlobCodec.cs:115-119,
    // 131, 152-153, 271-274, 360, 836).
    public static int U8Axis(float v, int center, bool invert)
    {
        if (invert) v = 1f - Math.Clamp(v, 0f, 1f);
        int raw = (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255);
        if (center != 128)
            raw = center + (int)Math.Round((Math.Clamp(v, 0f, 1f) - 0.5f) * 254);
        return Math.Clamp(raw, 0, 255);
    }

    public static int U8Trigger(float v) => Math.Clamp((int)Math.Round(Math.Clamp(v, 0f, 1f) * 255), 0, 255);
    public static int Stick12(float v) => Math.Clamp((int)Math.Round(Math.Clamp(v, 0f, 1f) * 4095), 0, 4095);

    public static int I16Axis(float v, bool negate)
    {
        float centered = Math.Clamp(v, 0f, 1f) - 0.5f;
        if (negate) centered = -centered;
        return Math.Clamp((int)Math.Round(centered * 2f * 32767f), -32767, 32767);
    }

    public static int U16Trigger(float v) => (int)Math.Round(Math.Clamp(v, 0f, 1f) * 32767f);
    public static bool Digital(float v) => (int)Math.Round(Math.Clamp(v, 0f, 1f) * 255) > 0;

    // HMController's XUSB slice at 9bd6279 (HMController.cs:875-880).
    public static int GipStick(float v) => (ushort)((double)Math.Clamp(v, 0f, 1f) * 65535);
    public static int GipTrigger(float v) => (ushort)((double)Math.Clamp(v, 0f, 1f) * 1023);

    // SwitchProPacker.StickRaw at 9bd6279 (SwitchProPacker.cs:139-145).
    public static int SwitchStick(float v, bool invert)
    {
        double centered = (Math.Clamp((double)v, 0.0, 1.0) * 2.0) - 1.0;
        if (invert) centered = -centered;
        int raw = 0x800 + (int)(centered * 0x600);
        return Math.Clamp(raw, 0, 0xFFF);
    }

    // Switch2ProPacker.StickRaw and TriggerByte at 9bd6279: double, ties to
    // even already.
    public static int Switch2Stick(float v, bool invert)
    {
        double c = Math.Clamp((double)v, 0.0, 1.0);
        if (invert) c = 1.0 - c;
        return Math.Clamp((int)Math.Round(c * 4095.0), 0, 4095);
    }

    public static bool Switch2Digital(float v) => (int)Math.Round(Math.Clamp((double)v, 0.0, 1.0) * 255) > 0;

    // Both packers' ImuRaw at 9bd6279: a float product.
    public static short Imu(float value, float scale)
        => (short)Math.Clamp((int)MathF.Round(value * scale), short.MinValue, short.MaxValue);
}

/// <summary>companion.c's reading of the XUSB slice (DecodeGipToXInput).</summary>
internal static class Companion
{
    public static int StickX(int g) => (short)(g - 32768);
    public static int StickY(int g) => (short)(32767 - g);
    public static int Trigger(int g) => (g & 0x3FF) * 255 / 1023;
}

/// <summary>One conversion: its old and new arithmetic over a float input,
/// the raw integer domain a consumer can pass back, how a raw value is
/// normalized, and how a wire value reads back.</summary>
internal sealed record Site(
    string Key,
    Func<float, int> Old,
    Func<float, int> New,
    int RawLo, int RawHi,
    // raw value -> the count k it sits at above its own zero, and the span.
    // A consumer normalizes it as k / span.
    Func<int, (int K, int Range)> Normal,
    Func<int, int> ReadBack)
{
    /// <summary>The SDK's own code path for this site, from a float input
    /// to the value a consumer reads back. Null for a site the round trip
    /// reaches only through New.</summary>
    public Func<float, int>? Tree { get; init; }
}

internal static class Sites
{
    static int Id(int x) => x;

    public static Site Descriptor(int min, int max) => new(
        $"hid-descriptor[{min}..{max}]",
        v => Old.Descriptor(v, min, max),
        v => NewArith.Linear(v, min, max, false),
        min, max, r => (r - min, max - min), Id);

    /// <summary>A descriptor field its profile centers (axisCenters).</summary>
    public static Site DescriptorCentered(int min, int center, int max) => new(
        $"hid-descriptor[{min}..{max}] centered {center}",
        v => Old.Descriptor(v, min, max),
        v => NewArith.Centered(v, min, center, max),
        min, max, r => (r - min, max - min), Id);

    public static Site U8Axis(int center, bool invert) => new(
        $"codec uint8-axis center {center}{(invert ? " invert" : "")}",
        v => Old.U8Axis(v, center, invert),
        v => center == 128 ? NewArith.Linear(v, 0, 255, invert) : Math.Clamp(NewArith.Linear(v, center - 127, center + 127, invert), 0, 255),
        center == 128 ? 0 : Math.Max(0, center - 127), center == 128 ? 255 : Math.Min(255, center + 127),
        r =>
        {
            int lo = center == 128 ? 0 : center - 127, hi = center == 128 ? 255 : center + 127;
            return (invert ? hi - r : r - lo, hi - lo);
        }, Id);

    public static Site U8Trigger() => new(
        "codec uint8-trigger", Old.U8Trigger, v => NewArith.Linear(v, 0, 255, false),
        0, 255, r => (r, 255), Id);

    public static Site Stick12() => new(
        "codec stick12-pair", Old.Stick12, v => NewArith.Linear(v, 0, 4095, false),
        0, 4095, r => (r, 4095), Id);

    public static Site I16Axis(bool negate) => new(
        $"codec int16-axis{(negate ? " Y (negated)" : "")}",
        v => Old.I16Axis(v, negate), v => NewArith.Linear(v, -32767, 32767, negate),
        -32767, 32767, r => (negate ? 32767 - r : r + 32767, 65534), Id);

    public static Site U16Trigger() => new(
        "codec uint16-trigger", Old.U16Trigger, v => NewArith.Linear(v, 0, 32767, false),
        0, 32767, r => (r, 32767), Id);

    public static Site Digital() => new(
        "codec LT_DIGITAL / RT_DIGITAL",
        v => Old.Digital(v) ? 1 : 0, v => NewArith.Linear(v, 0, 255, false) > 0 ? 1 : 0,
        0, 1, r => (r == 0 ? 0 : 255, 255), Id);

    // The slice carries g, and the consumer reads companion.c's decode of it.
    public static Site GipStickX() => new(
        "xusb slice stick X",
        v => Companion.StickX(Old.GipStick(v)), v => Companion.StickX(NewArith.Linear(v, 0, 65535, false)),
        -32768, 32767, r => (r + 32768, 65535), Id);

    // XInput's sThumbY grows upward, so the rule treats it as an inverted
    // field: sThumbY = Linear(v, -32768, 32767, invert).
    public static Site GipStickY() => new(
        "xusb slice stick Y",
        v => Companion.StickY(Old.GipStick(v)), v => NewArith.Linear(v, -32768, 32767, true),
        -32768, 32767, r => (32767 - r, 65535), Id);

    public static Site GipTrigger() => new(
        "xusb slice trigger",
        v => Companion.Trigger(Old.GipTrigger(v)), v => Companion.Trigger(NewArith.XusbTrigger(v)),
        0, 255, r => (r, 255), Id);

    public static Site SwitchStick(bool invert) => new(
        $"switch-pro stick {(invert ? "Y" : "X")}",
        v => Old.SwitchStick(v, invert), v => NewArith.Linear(v, 0x200, 0xE00, invert),
        0x200, 0xE00, r => (invert ? 0xE00 - r : r - 0x200, 0xC00), Id);

    public static Site Switch2Stick(bool invert) => new(
        $"switch2 composite stick {(invert ? "Y" : "X")}",
        v => Old.Switch2Stick(v, invert), v => NewArith.Linear(v, 0, 4095, invert),
        0, 4095, r => (invert ? 4095 - r : r, 4095), Id);

    public static Site Switch2Digital() => new(
        "switch2 composite ZL/ZR",
        v => Old.Switch2Digital(v) ? 1 : 0, v => NewArith.Linear(v, 0, 255, false) > 0 ? 1 : 0,
        0, 1, r => (r == 0 ? 0 : 255, 255), Id);
}

/// <summary>The motion scales: counts per g or per degree per second, the
/// float scale 9bd6279 multiplied by, and the ratio the SDK now divides
/// by.</summary>
internal sealed record ImuScale(string Key, float OldScale, double Numerator, double Denominator)
{
    public static readonly ImuScale[] All =
    {
        new("switch-pro gyro 13371/936", 13371.0f / 936.0f, 13371, 936),
        new("switch-pro accel 4096", 4096.0f, 4096, 1),
        new("switch2 accel 32767/8", 32767.0f / 8.0f, 32767, 8),
        new("switch2 gyro (pi/180)*32767/34.8", (float)(Math.PI / 180.0 * 32767.0 / 34.8), Math.PI / 180.0 * 32767.0 / 34.8, 1),
    };

    public double Scale => Numerator / Denominator;
}

/// <summary>Every conversion site of every catalog profile, by the SDK's own
/// reading of each profile.</summary>
internal static class Inventory
{
    public sealed record Use(string Profile, string Path, string SiteKey, string Where);

    public static readonly List<Use> Uses = new();
    public static readonly SortedDictionary<string, Site> SiteByKey = new(StringComparer.Ordinal);

    static void Add(string profile, string path, Site s, string where)
    {
        SiteByKey.TryAdd(s.Key, s);
        Uses.Add(new Use(profile, path, s.Key, where));
    }

    public static void Build()
    {
        if (Uses.Count > 0) return;
        foreach (var p in ProfileDatabase.LoadEmbedded().All)
        {
            var ext = p.ExtendedReport;
            bool alwaysArmed = ext?.AlwaysArmed == true;
            bool armOn = ext?.ArmOn is { Count: > 0 };
            bool ports = ext?.Ports != null;
            // A device that builds its reports from a state block: the Switch
            // Pro, whose stateBlock's 12-bit sticks scale as SwitchProPacker's
            // did, and the composite, whose int16-axis sticks scale as
            // Switch2ProPacker's did (v2.0.0 moved both bodies into the
            // profiles).
            if (!p.RequiresUsbipBackend && p.StateBlockFields?.Exists(f => f.Type == "stick12-pair") == true)
            {
                Add(p.Id, "packer", Sites.SwitchStick(false), "0x30 bytes 6-8 and 9-11 (X)");
                Add(p.Id, "packer", Sites.SwitchStick(true), "0x30 bytes 6-8 and 9-11 (Y)");
                continue;
            }
            if (p.RequiresUsbipBackend && p.BuildsItsOwnReports)
            {
                Add(p.Id, "packer", Sites.Switch2Stick(false), "0x09 bytes 6-11, 0x05 bytes 0x0B-0x10 (X)");
                Add(p.Id, "packer", Sites.Switch2Stick(true), "0x09 bytes 6-11, 0x05 bytes 0x0B-0x10 (Y)");
                Add(p.Id, "packer", Sites.Switch2Digital(), "0x09 byte 4 bit 5 and byte 5 bit 1 (ZR, ZL)");
                continue;
            }
            if (p.RequiresXusbCompanion)
            {
                Add(p.Id, "xusb", Sites.GipStickX(), "XInput sThumbLX/RX");
                Add(p.Id, "xusb", Sites.GipStickY(), "XInput sThumbLY/RY");
                Add(p.Id, "xusb", Sites.GipTrigger(), "XInput bLeftTrigger/bRightTrigger");
            }
            if (ext != null && (alwaysArmed || armOn || ports))
            {
                if (ports)
                {
                    var ps = ext.Ports!;
                    var blockSpec = new ExtendedReportSpec { ReportId = ext.ReportId, Size = ps.Stride, Fields = ps.Fields };
                    foreach (var f in VendorBlobProgram.Get(blockSpec).Fields)
                        Codec(p.Id, f, $"report {ext.ReportId} port block byte {f.B}");
                }
                else
                {
                    foreach (var f in VendorBlobProgram.Get(ext).Fields)
                        Codec(p.Id, f, $"report {ext.ReportId} byte {f.B}");
                }
            }
            if (!alwaysArmed && !ports && p.HasDescriptor)
            {
                var b = p.GetOrBuildReportBuilder();
                int idOff = b.InputReportId != 0 ? 8 : 0;
                // Read by name, so the inventory also runs on a tree from
                // before axisCenters.
                var centers = typeof(ControllerProfile).GetProperty("AxisCenters")?.GetValue(p) as Dictionary<string, int>;
                var seen = new HashSet<HidReportBuilder.InputField>(ReferenceEqualityComparer.Instance);
                string path = armOn ? "descriptor (until armed)" : "descriptor";
                foreach (var (key, f) in b.AxisFields)
                    if (seen.Add(f))
                        Add(p.Id, path,
                            centers != null && centers.TryGetValue(key.ToString(), out int c)
                                ? Sites.DescriptorCentered(f.LogicalMin, c, f.LogicalMax)
                                : Sites.Descriptor(f.LogicalMin, f.LogicalMax),
                            $"report {b.InputReportId:X2} byte {(f.BitOffset + idOff) / 8} bits {f.BitSize} ({key})");
                foreach (var f in new[] { b.CombinedTrigger, b.LeftTrigger, b.RightTrigger })
                    if (f != null && seen.Add(f))
                        Add(p.Id, path, Sites.Descriptor(f.LogicalMin, f.LogicalMax),
                            $"report {b.InputReportId:X2} byte {(f.BitOffset + idOff) / 8} bits {f.BitSize} (trigger field)");
            }
        }
    }

    /// <summary>True on a tree whose codec reads min and max, where Y
    /// inverts only by the profile's invert.</summary>
    static readonly bool MinMaxKnown = typeof(FieldSpec).GetProperty("Max") != null;

    static void Codec(string id, VendorBlobProgram.CompiledField f, string where)
    {
        // Until v2.0.0 the codec negated a stick's Y by its semantic and a
        // pad's always. From v2.0.0 the profile says so with invert.
        bool yStick = f.Invert || (f.Source is VendorBlobProgram.SrcOp.LeftStickY or VendorBlobProgram.SrcOp.RightStickY && f.Src.Invert == null && !MinMaxKnown);
        bool yPad = f.Invert || (f.Source is VendorBlobProgram.SrcOp.LeftPadY or VendorBlobProgram.SrcOp.RightPadY && f.Src.Invert == null && !MinMaxKnown);
        switch (f.Op)
        {
            case VendorBlobProgram.FieldOp.U8Axis: Add(id, "codec", Sites.U8Axis(f.Center, f.Invert), where); break;
            case VendorBlobProgram.FieldOp.U8Trigger: Add(id, "codec", Sites.U8Trigger(), where); break;
            case VendorBlobProgram.FieldOp.Stick12Pair: Add(id, "codec", Sites.Stick12(), where); break;
            case VendorBlobProgram.FieldOp.I16Axis: Add(id, "codec", Sites.I16Axis(yStick), where); break;
            case VendorBlobProgram.FieldOp.I16PadOrStick: Add(id, "codec", Sites.I16Axis(yPad), where + " (stick part)"); break;
            case VendorBlobProgram.FieldOp.U16Trigger: Add(id, "codec", Sites.U16Trigger(), where); break;
            case VendorBlobProgram.FieldOp.ButtonMask:
                if (f.ButtonBits != null && Array.Exists(f.ButtonBits, x => x == VendorBlobProgram.ButtonLtDigital || x == VendorBlobProgram.ButtonRtDigital))
                    Add(id, "codec", Sites.Digital(), where + " (LT_DIGITAL/RT_DIGITAL bit)");
                break;
        }
    }
}
