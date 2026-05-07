using System;
using System.Collections.Generic;

namespace HIDMaestro.Internal;

/// <summary>
/// Parses a HID report descriptor and builds input reports with correct bit packing.
/// This is fully data-driven — works with ANY HID descriptor, no controller-specific code.
/// </summary>
public class HidReportBuilder
{
    public record InputField(
        ushort UsagePage, ushort Usage,
        int BitOffset, int BitSize,
        int LogicalMin, int LogicalMax,
        bool IsConstant,
        int ReportCount = 1);

    public byte InputReportId { get; private set; }
    public int InputReportBitSize { get; private set; }
    public int InputReportByteSize => (InputReportBitSize + 7) / 8 + (InputReportId != 0 ? 1 : 0);
    public List<InputField> InputFields { get; } = new();

    /// <summary>Optional axis semantic override. When set, applied after
    /// ResolveSemantics to correct axis assignments for profiles where the
    /// default heuristic is wrong (e.g. Sony uses Z/Rz for right stick,
    /// Rx/Ry for triggers — opposite of the Xbox convention).</summary>
    public Dictionary<string, string>? AxisMap { get; set; }

    /// <summary>Optional button remapping table. Maps HMButton bit positions
    /// (index) to descriptor button indices (value). When set, BuildReport
    /// uses this to place semantic buttons at the correct descriptor positions
    /// for the profile's controller family. When null, identity mapping is
    /// assumed (bit N → descriptor button N).</summary>
    public int[]? ButtonMap { get; set; }

    /// <summary>Optional trigger-to-button derivation. When set, BuildReport
    /// automatically sets the specified descriptor buttons when the corresponding
    /// trigger axis is nonzero. Array of two: [LT_button_index, RT_button_index].
    /// DS4/DualSense hardware reports L2/R2 as both analog axis AND digital button;
    /// this field replicates that behavior so consumers see both.</summary>
    public int[]? TriggerButtons { get; set; }

    // Semantic axis mapping (resolved after parsing)
    public InputField? LeftStickX { get; private set; }
    public InputField? LeftStickY { get; private set; }
    public InputField? RightStickX { get; private set; }
    public InputField? RightStickY { get; private set; }
    public InputField? LeftTrigger { get; private set; }
    public InputField? RightTrigger { get; private set; }
    public InputField? CombinedTrigger { get; private set; } // Z axis for DI combined trigger
    public InputField? HatSwitch { get; private set; }
    public List<InputField> Buttons { get; } = new();

    /// <summary>System Control / System Main Menu bit. On Xbox Series / Xbox
    /// One controllers the Guide (Xbox) button lives here, not in the regular
    /// gamepad button array. xinputhid parses this bit and exposes it via
    /// XInputGetStateEx (ordinal 100) as XINPUT_GAMEPAD_GUIDE. When this field
    /// is present, <see cref="BuildReport"/> routes <c>HMButton.Guide</c> to
    /// it; otherwise Guide falls back to the normal button-array path so
    /// profiles where Guide is a regular button (Xbox 360, Sony, etc.) still
    /// work via <see cref="ButtonMap"/>.</summary>
    public InputField? SystemMainMenu { get; private set; }

    public static HidReportBuilder Parse(byte[] descriptor, Dictionary<string, string>? axisMap = null)
    {
        var builder = new HidReportBuilder();
        builder.ParseDescriptor(descriptor);
        builder.ResolveSemantics();
        if (axisMap != null)
        {
            builder.AxisMap = axisMap;
            builder.ApplyAxisMap(axisMap);
        }
        return builder;
    }

    /// <summary>Override semantic axis assignments from an explicit map.
    /// Keys are hex usage codes (e.g. "0x32"), values are semantic names
    /// (leftStickX, leftStickY, rightStickX, rightStickY, leftTrigger,
    /// rightTrigger). Clears the affected slots before reassigning so
    /// there are no duplicates.</summary>
    void ApplyAxisMap(Dictionary<string, string> map)
    {
        // Build a lookup from usage code → InputField
        var fieldByUsage = new Dictionary<ushort, InputField>();
        foreach (var f in InputFields)
        {
            if (f.IsConstant || f.UsagePage != 0x01) continue;
            if (!fieldByUsage.ContainsKey(f.Usage))
                fieldByUsage[f.Usage] = f;
        }

        // Clear all slots that will be reassigned
        foreach (var kvp in map)
        {
            switch (kvp.Value.ToLowerInvariant())
            {
                case "leftstickx":  LeftStickX = null; break;
                case "leftsticky":  LeftStickY = null; break;
                case "rightstickx": RightStickX = null; break;
                case "rightsticky": RightStickY = null; break;
                case "lefttrigger": LeftTrigger = null; break;
                case "righttrigger": RightTrigger = null; break;
            }
        }

        // Apply the overrides
        foreach (var kvp in map)
        {
            ushort usage = Convert.ToUInt16(kvp.Key, 16);
            if (!fieldByUsage.TryGetValue(usage, out var field)) continue;
            switch (kvp.Value.ToLowerInvariant())
            {
                case "leftstickx":  LeftStickX = field; break;
                case "leftsticky":  LeftStickY = field; break;
                case "rightstickx": RightStickX = field; break;
                case "rightsticky": RightStickY = field; break;
                case "lefttrigger": LeftTrigger = field; break;
                case "righttrigger": RightTrigger = field; break;
            }
        }
    }

    void ParseDescriptor(byte[] desc)
    {
        // HID descriptor parser state
        ushort usagePage = 0;
        var usages = new List<ushort>();
        ushort usageMin = 0, usageMax = 0;
        int reportSize = 0, reportCount = 0;
        int logicalMin = 0, logicalMax = 0;
        byte reportId = 0;
        int bitOffset = 0;
        bool firstInputReportId = true;
        int collectionDepth = 0;

        for (int i = 0; i < desc.Length;)
        {
            byte prefix = desc[i];
            if (prefix == 0xFE) { i += 3; continue; } // Long item (skip)

            int bSize = prefix & 0x03;
            if (bSize == 3) bSize = 4;
            int bType = (prefix >> 2) & 0x03;
            int bTag = (prefix >> 4) & 0x0F;

            int value = 0;
            if (i + bSize < desc.Length)
            {
                for (int j = 0; j < bSize; j++)
                    value |= desc[i + 1 + j] << (8 * j);
            }
            // Sign-extend for signed items (Logical Min, etc.)
            int signedValue = value;
            if (bSize > 0 && bSize < 4 && (value & (1 << (bSize * 8 - 1))) != 0)
                signedValue |= unchecked((int)(0xFFFFFFFF << (bSize * 8)));

            switch (bType)
            {
                case 0: // Main
                    switch (bTag)
                    {
                        case 8: // Input
                            bool isConstant = (value & 0x01) != 0;
                            if (reportId != 0 && firstInputReportId)
                            {
                                InputReportId = reportId;
                                firstInputReportId = false;
                            }
                            // Only process first input report ID
                            if (reportId == InputReportId || (reportId == 0 && InputReportId == 0))
                            {
                                if (usageMin != 0 && usageMax != 0)
                                {
                                    // Button range
                                    for (int b = 0; b < reportCount; b++)
                                    {
                                        ushort u = (ushort)(usageMin + b);
                                        if (u > usageMax) u = usageMax;
                                        InputFields.Add(new InputField(usagePage, u,
                                            bitOffset + b * reportSize, reportSize,
                                            logicalMin, logicalMax, isConstant, reportCount));
                                    }
                                }
                                else
                                {
                                    for (int c = 0; c < reportCount; c++)
                                    {
                                        ushort u = c < usages.Count ? usages[c] : (ushort)0;
                                        InputFields.Add(new InputField(usagePage, u,
                                            bitOffset + c * reportSize, reportSize,
                                            logicalMin, logicalMax, isConstant, reportCount));
                                    }
                                }
                                bitOffset += reportSize * reportCount;
                            }
                            usages.Clear();
                            usageMin = usageMax = 0;
                            break;
                        case 9: // Output — skip (different report)
                        case 11: // Feature — skip
                            usages.Clear();
                            usageMin = usageMax = 0;
                            break;
                        case 10: // Collection — usage before collection is the collection's, not input's
                            collectionDepth++;
                            usages.Clear();
                            usageMin = usageMax = 0;
                            break;
                        case 12: // End Collection
                            collectionDepth--;
                            break;
                    }
                    break;

                case 1: // Global
                    switch (bTag)
                    {
                        case 0: usagePage = (ushort)value; break;         // Usage Page
                        case 1: logicalMin = signedValue; break;          // Logical Min
                        case 2: logicalMax = (logicalMin >= 0 && signedValue < 0) ? value : signedValue; break; // Logical Max (unsigned if min>=0)
                        case 7: reportSize = value; break;                // Report Size
                        case 8: // Report ID
                            reportId = (byte)value;
                            if (firstInputReportId)
                            {
                                // First Report ID we encounter — reset for this report
                                bitOffset = 0;
                            }
                            break;
                        case 9: reportCount = value; break;               // Report Count
                    }
                    break;

                case 2: // Local
                    switch (bTag)
                    {
                        case 0: // Usage
                            if (bSize == 4)
                            {
                                // Extended usage: low 16 = usage ID, high 16 = usage page
                                usagePage = (ushort)(value >> 16);
                                usages.Add((ushort)(value & 0xFFFF));
                            }
                            else
                            {
                                usages.Add((ushort)value);
                            }
                            break;
                        case 1: usageMin = (ushort)value; break;          // Usage Minimum
                        case 2: usageMax = (ushort)value; break;          // Usage Maximum
                    }
                    break;
            }

            i += 1 + bSize;
        }

        InputReportBitSize = bitOffset;
    }

    void ResolveSemantics()
    {
        // Pre-scan: does this descriptor have dedicated Rx (0x33) or Ry (0x34)
        // usages? If so, Z (0x32) and Rz (0x35) default to trigger semantics
        // (Xbox-style 6-axis). If NOT, and Z+Rz BOTH appear, they're the
        // right stick — the 4-axis DirectInput layout (the usage pattern
        // WebKit/Chromium call the "standard gamepad"). Z OR Rz alone (no
        // pair) means a single trigger axis, not half a stick. See issues #5
        // and #22.
        bool hasRxOrRy = false;
        bool hasZ = false;
        bool hasRz = false;
        foreach (var f in InputFields)
        {
            if (f.IsConstant || f.UsagePage != 0x01) continue;
            if (f.Usage == 0x33 || f.Usage == 0x34) hasRxOrRy = true;
            else if (f.Usage == 0x32) hasZ = true;
            else if (f.Usage == 0x35) hasRz = true;
        }
        bool fourAxisDInput = !hasRxOrRy && hasZ && hasRz;

        // A Z (0x32) or Rz (0x35) field declared with Report Count == 1 and
        // unsigned range starting at 0 is unambiguously a trigger (matches
        // what HidDescriptorBuilder.AddTrigger emits). This wins over the
        // fourAxisDInput heuristic so a (1 stick, 1 trigger) or (2 sticks,
        // 1 trigger) Custom layout doesn't get its lone trigger silently
        // claimed as right-stick X. See issue #22.
        static bool LooksLikeTrigger(InputField f) =>
            f.ReportCount == 1 && f.LogicalMin == 0;

        // Map HID usages to semantic gamepad axes/buttons
        // This works for any standard gamepad descriptor
        foreach (var f in InputFields)
        {
            if (f.IsConstant) continue;

            if (f.UsagePage == 0x01) // Generic Desktop
            {
                switch (f.Usage)
                {
                    case 0x30: LeftStickX ??= f; break;    // X
                    case 0x31: LeftStickY ??= f; break;    // Y
                    case 0x32:                               // Z
                        if (LooksLikeTrigger(f))
                            LeftTrigger ??= f;
                        else if (fourAxisDInput)
                            RightStickX ??= f;
                        else
                            LeftTrigger ??= f;
                        break;
                    case 0x33: RightStickX ??= f; break;   // Rx
                    case 0x34: RightStickY ??= f; break;   // Ry
                    case 0x35:                               // Rz
                        if (LooksLikeTrigger(f))
                            RightTrigger ??= f;
                        else if (fourAxisDInput)
                            RightStickY ??= f;
                        else
                            RightTrigger ??= f;
                        break;
                    case 0x39: HatSwitch ??= f; break;     // Hat Switch
                    case 0x85: SystemMainMenu ??= f; break; // System Main Menu (Xbox Guide)
                    case 0x40:                               // Vx — hidden separate LT for WGI
                        CombinedTrigger ??= LeftTrigger;     // Save Z as combined before override
                        LeftTrigger = f; break;
                    case 0x41:                               // Vy — hidden separate RT for WGI
                        RightTrigger = f; break;
                }
            }
            else if (f.UsagePage == 0x02) // Simulation
            {
                switch (f.Usage)
                {
                    case 0xC4: RightTrigger ??= f; break;  // Accelerator
                    case 0xC5: LeftTrigger ??= f; break;    // Brake
                    case 0xBB: LeftTrigger ??= f; break;    // Throttle
                    case 0xBA: RightTrigger ??= f; break;   // Rudder
                }
            }
            else if (f.UsagePage == 0x09) // Button
            {
                Buttons.Add(f);
            }
            else if (f.UsagePage == 0x0C) // Consumer
            {
                Buttons.Add(f); // Consumer buttons (e.g., Share/Record)
            }
        }
    }

    /// <summary>
    /// Build an input report from normalized gamepad values.
    /// All values are 0.0-1.0 range (sticks: 0.5 = center, triggers: 0.0 = released).
    /// </summary>
    public byte[] BuildReport(
        double leftX = 0.5, double leftY = 0.5,
        double rightX = 0.5, double rightY = 0.5,
        double leftTrigger = 0.0, double rightTrigger = 0.0,
        int hatValue = 0, // 0=neutral, 1-8=directions
        uint buttonMask = 0, // Bit 0 = button 1, etc.
        float? hatDegrees = null,
        int? hatHundredths = null,
        ushort? hatRaw = null)
    {
        byte[] report = new byte[InputReportByteSize];
        BuildReportInto(report, leftX, leftY, rightX, rightY, leftTrigger, rightTrigger,
                        hatValue, buttonMask, hatDegrees, hatHundredths, hatRaw);
        return report;
    }

    /// <summary>v1.3.0 — buffer-reuse overload. Caller supplies a byte[]
    /// of length <see cref="InputReportByteSize"/>; we zero it and pack
    /// the report into it. Avoids the 1500 alloc/sec churn at default
    /// SubmitState rate × multi-controller, which translates to less GC
    /// pressure and tighter cache behavior on slow hw.
    /// v1.3.4 — added hatDegrees/hatHundredths/hatRaw nullable parameters
    /// for high-resolution hat sources (HOTAS, flight sticks). Priority
    /// chain in the encoder block below: hatDegrees > hatHundredths >
    /// hatRaw > hatValue (octant) > null state.</summary>
    public void BuildReportInto(byte[] report,
        double leftX = 0.5, double leftY = 0.5,
        double rightX = 0.5, double rightY = 0.5,
        double leftTrigger = 0.0, double rightTrigger = 0.0,
        int hatValue = 0,
        uint buttonMask = 0,
        float? hatDegrees = null,
        int? hatHundredths = null,
        ushort? hatRaw = null)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        if (report.Length < InputReportByteSize)
            throw new ArgumentException(
                $"BuildReportInto: caller buffer is {report.Length} bytes, "
              + $"need {InputReportByteSize}.", nameof(report));

        Array.Clear(report, 0, InputReportByteSize);
        if (InputReportId != 0)
            report[0] = InputReportId;

        int idOffset = InputReportId != 0 ? 8 : 0; // Bit offset for Report ID byte

        void WriteField(InputField? field, double normalized)
        {
            if (field == null) return;
            int rawValue = (int)(normalized * (field.LogicalMax - field.LogicalMin) + field.LogicalMin);
            rawValue = Math.Clamp(rawValue, field.LogicalMin, field.LogicalMax);
            WriteBits(report, field.BitOffset + idOffset, field.BitSize, rawValue);
        }

        WriteField(LeftStickX, leftX);
        WriteField(LeftStickY, leftY);
        WriteField(RightStickX, rightX);
        WriteField(RightStickY, rightY);
        if (CombinedTrigger != null && RightTrigger != null)
        {
            // Dual mode: combined Z for DI + separate Vx/Vy for WGI
            double combined = 0.5 + (rightTrigger - leftTrigger) * 0.5;
            WriteField(CombinedTrigger, Math.Clamp(combined, 0.0, 1.0));
            WriteField(LeftTrigger, leftTrigger);
            WriteField(RightTrigger, rightTrigger);
        }
        else if (RightTrigger != null)
        {
            // Separate triggers: write each independently
            WriteField(LeftTrigger, leftTrigger);
            WriteField(RightTrigger, rightTrigger);
        }
        else if (LeftTrigger != null)
        {
            // Lone trigger axis: write the LT value directly. The Xbox-360
            // combined-Z synthesis only fires when both CombinedTrigger and
            // RightTrigger are declared (the first branch above) — that's
            // keyed on the explicit Vx/Vy hidden-pair the descriptor
            // declares. Single-trigger descriptors (Custom-Extended, wheels
            // with one pedal, lightguns, etc.) get their value through
            // unchanged. See issue #22.
            WriteField(LeftTrigger, leftTrigger);
        }

        if (HatSwitch != null)
        {
            // Priority chain: hatDegrees > hatHundredths > hatRaw > hatValue > null.
            // First non-null wins; remaining inputs ignored for this frame.
            int range = HatSwitch.LogicalMax - HatSwitch.LogicalMin + 1;
            int hatRawWritten;
            if (hatDegrees.HasValue)
            {
                // Normalize to [0, 360). Snap to nearest descriptor position.
                // The trailing % range handles the wrap-around case where the
                // angle rounds up to range (e.g. 350° on an 8-position hat
                // would otherwise round to idx=8 = LogicalMax+1).
                double a = ((hatDegrees.Value % 360.0) + 360.0) % 360.0;
                int idx = (int)Math.Round(a / 360.0 * range) % range;
                hatRawWritten = HatSwitch.LogicalMin + idx;
            }
            else if (hatHundredths.HasValue)
            {
                // Integer-only path. Truncates rather than rounds (matches
                // vJoy's wire-format convention).
                int v = ((hatHundredths.Value % 36000) + 36000) % 36000;
                int idx = (int)((long)v * range / 36000);
                hatRawWritten = HatSwitch.LogicalMin + idx;
            }
            else if (hatRaw.HasValue)
            {
                // Bit-exact: clamp into descriptor's range silently.
                hatRawWritten = Math.Clamp((int)hatRaw.Value,
                                           HatSwitch.LogicalMin,
                                           HatSwitch.LogicalMax);
            }
            else if (hatValue == 0)
            {
                // Neutral: write null state (value outside logical range).
                // LogMin=1,Max=8: null=0. LogMin=0,Max=7: null=Max+1.
                hatRawWritten = HatSwitch.LogicalMin == 0
                    ? HatSwitch.LogicalMax + 1
                    : 0;
            }
            else
            {
                // Octant 1-8 (N,NE,E,SE,S,SW,W,NW). Scale into the
                // descriptor's range so high-res hats place octants at
                // the matching 45° positions instead of crowding into
                // the first 8 indices. For range=8 this collapses to
                // (hatValue-1) — backwards-compatible with the legacy
                // 8-position behavior. For range=16: NE → idx 2,
                // E → idx 4, SE → idx 6, etc. Truncating int division
                // matches the descriptor's quantization.
                int octantIdx = (hatValue - 1) * range / 8;
                hatRawWritten = HatSwitch.LogicalMin + octantIdx;
            }
            WriteBits(report, HatSwitch.BitOffset + idOffset, HatSwitch.BitSize, hatRawWritten);
        }

        // Trigger-to-button derivation: DS4/DualSense hardware reports L2/R2
        // as both analog axes AND digital buttons (buttons 7/8 in the DS4
        // descriptor). When TriggerButtons is set, any nonzero trigger value
        // automatically engages the corresponding descriptor button.
        if (TriggerButtons != null && TriggerButtons.Length >= 2)
        {
            if (leftTrigger > 0.0 && TriggerButtons[0] >= 0 && TriggerButtons[0] < Buttons.Count)
                WriteBits(report, Buttons[TriggerButtons[0]].BitOffset + idOffset,
                          Buttons[TriggerButtons[0]].BitSize, 1);
            if (rightTrigger > 0.0 && TriggerButtons[1] >= 0 && TriggerButtons[1] < Buttons.Count)
                WriteBits(report, Buttons[TriggerButtons[1]].BitOffset + idOffset,
                          Buttons[TriggerButtons[1]].BitSize, 1);
        }

        // Guide (bit 10) routing: on descriptors where the Xbox Guide button
        // lives in the System Control collection (Xbox Series / Xbox One BT
        // family), write the dedicated System Main Menu 1-bit field. The
        // regular button-array path below will skip bit 10 in that case so
        // we don't double-write. On descriptors where Guide is a regular
        // button (Xbox 360, PS4/PS5 PS Home button via buttonMap), the
        // regular path handles it.
        const int GUIDE_BIT = 10;
        bool guideRoutedToSysMenu = false;
        if (SystemMainMenu != null && ((buttonMask >> GUIDE_BIT) & 1) != 0)
        {
            WriteBits(report, SystemMainMenu.BitOffset + idOffset,
                      SystemMainMenu.BitSize, 1);
            guideRoutedToSysMenu = true;
        }

        // Button packing with optional remapping. When ButtonMap is set,
        // HMButton bit positions are translated to descriptor button indices
        // so that semantic names (A, B, LB, Start, etc.) land at the correct
        // positions for the profile's controller family.
        // T31-2 — bit-pop instead of full 32-bit scan. With BitOperations
        // we extract the lowest set bit, process, clear it, and loop only
        // while bits remain. For a typical state with 0–4 buttons held,
        // this is 0–4 iterations vs the full 32. Saves ~28 branches per
        // frame at default state. No-op cost when no buttons held.
        uint mask = buttonMask;
        if (guideRoutedToSysMenu) mask &= ~(1u << GUIDE_BIT);
        while (mask != 0)
        {
            int b = System.Numerics.BitOperations.TrailingZeroCount(mask);
            mask &= mask - 1;  // clear lowest set bit
            int descBtn = (ButtonMap != null && b < ButtonMap.Length)
                ? ButtonMap[b] : b;
            if ((uint)descBtn < (uint)Buttons.Count)
                WriteBits(report, Buttons[descBtn].BitOffset + idOffset,
                          Buttons[descBtn].BitSize, 1);
        }
    }

    static void WriteBits(byte[] buffer, int bitOffset, int bitSize, int value)
    {
        // T27-2 — fast path for byte-aligned, byte-multiple fields. The vast
        // majority of HID descriptor fields fit this case: 8-bit triggers/
        // hat (single byte), 16-bit sticks (two bytes), 32-bit composite
        // axes (four bytes). Bit-by-bit fallback is only needed for
        // odd-sized button bitmaps, mid-byte alignment etc. Writing whole
        // bytes drops per-field cost from ~16-32 ops to ~2-4 ops on the
        // common case — the dominant gain in SubmitState's hot path on
        // descriptor-heavy profiles like DualSense.
        if ((bitOffset & 7) == 0 && (bitSize & 7) == 0)
        {
            int byteIdx = bitOffset >> 3;
            int byteCnt = bitSize >> 3;
            uint v = (uint)value;
            for (int i = 0; i < byteCnt; i++)
            {
                if ((uint)(byteIdx + i) >= (uint)buffer.Length) break;
                buffer[byteIdx + i] = (byte)(v & 0xFF);
                v >>= 8;
            }
            return;
        }

        // Fall-through: arbitrary alignment / non-byte-multiple size. Used
        // for HID button bitmaps that pack 1 bit per button starting at
        // mid-byte offsets and similar oddly-aligned fields.
        for (int b = 0; b < bitSize; b++)
        {
            int bit = (value >> b) & 1;
            int byteIdx = (bitOffset + b) >> 3;
            int bitIdx = (bitOffset + b) & 7;
            if ((uint)byteIdx < (uint)buffer.Length)
            {
                if (bit != 0)
                    buffer[byteIdx] |= (byte)(1 << bitIdx);
                else
                    buffer[byteIdx] &= (byte)~(1 << bitIdx);
            }
        }
    }

    public void PrintLayout()
    {
        Console.WriteLine($"  Input Report: ID=0x{InputReportId:X2}, {InputReportByteSize} bytes ({InputReportBitSize} bits)");
        if (LeftStickX != null) Console.WriteLine($"    Left X:   bit {LeftStickX.BitOffset}, {LeftStickX.BitSize}b, range [{LeftStickX.LogicalMin}..{LeftStickX.LogicalMax}]");
        if (LeftStickY != null) Console.WriteLine($"    Left Y:   bit {LeftStickY.BitOffset}, {LeftStickY.BitSize}b, range [{LeftStickY.LogicalMin}..{LeftStickY.LogicalMax}]");
        if (RightStickX != null) Console.WriteLine($"    Right X:  bit {RightStickX.BitOffset}, {RightStickX.BitSize}b, range [{RightStickX.LogicalMin}..{RightStickX.LogicalMax}]");
        if (RightStickY != null) Console.WriteLine($"    Right Y:  bit {RightStickY.BitOffset}, {RightStickY.BitSize}b, range [{RightStickY.LogicalMin}..{RightStickY.LogicalMax}]");
        if (LeftTrigger != null) Console.WriteLine($"    LTrigger: bit {LeftTrigger.BitOffset}, {LeftTrigger.BitSize}b, range [{LeftTrigger.LogicalMin}..{LeftTrigger.LogicalMax}]");
        if (RightTrigger != null) Console.WriteLine($"    RTrigger: bit {RightTrigger.BitOffset}, {RightTrigger.BitSize}b, range [{RightTrigger.LogicalMin}..{RightTrigger.LogicalMax}]");
        if (HatSwitch != null) Console.WriteLine($"    Hat:      bit {HatSwitch.BitOffset}, {HatSwitch.BitSize}b");
        if (SystemMainMenu != null) Console.WriteLine($"    SysMenu:  bit {SystemMainMenu.BitOffset}, {SystemMainMenu.BitSize}b (Xbox Guide)");
        Console.WriteLine($"    Buttons:  {Buttons.Count}");
    }
}
