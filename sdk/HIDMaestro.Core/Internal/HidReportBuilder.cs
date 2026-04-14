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
        bool IsConstant);

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
                                            logicalMin, logicalMax, isConstant));
                                    }
                                }
                                else
                                {
                                    for (int c = 0; c < reportCount; c++)
                                    {
                                        ushort u = c < usages.Count ? usages[c] : (ushort)0;
                                        InputFields.Add(new InputField(usagePage, u,
                                            bitOffset + c * reportSize, reportSize,
                                            logicalMin, logicalMax, isConstant));
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
                        // Z could be right stick or trigger — distinguish by bit size
                        if (f.BitSize > 8) // 10+ bit = trigger, 16-bit = stick
                        {
                            if (f.BitSize >= 16 && RightStickX == null)
                                RightStickX = f;
                            else
                                LeftTrigger ??= f;
                        }
                        else
                            LeftTrigger ??= f;
                        break;
                    case 0x33: RightStickX ??= f; break;   // Rx
                    case 0x34: RightStickY ??= f; break;   // Ry
                    case 0x35:                               // Rz
                        if (f.BitSize >= 16 && RightStickY == null)
                            RightStickY = f;
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
        uint buttonMask = 0) // Bit 0 = button 1, etc.
    {
        byte[] report = new byte[InputReportByteSize];
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
            // Combined trigger (Xbox 360 style): single Z axis
            // Center = 0.5 (both released). LT pulls toward 0, RT pulls toward 1.
            double combined = 0.5 + (rightTrigger - leftTrigger) * 0.5;
            WriteField(LeftTrigger, Math.Clamp(combined, 0.0, 1.0));
        }

        if (HatSwitch != null)
        {
            int hatRaw;
            if (hatValue == 0)
            {
                // Neutral: write null state (value outside logical range).
                // LogMin=1,Max=8: null=0. LogMin=0,Max=7: null=Max+1.
                hatRaw = HatSwitch.LogicalMin == 0
                    ? HatSwitch.LogicalMax + 1
                    : 0;
            }
            else
            {
                // hatValue 1-8 (N,NE,E,SE,S,SW,W,NW). Shift to descriptor's range.
                hatRaw = hatValue + HatSwitch.LogicalMin - 1;
            }
            WriteBits(report, HatSwitch.BitOffset + idOffset, HatSwitch.BitSize, hatRaw);
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
        for (int b = 0; b < 32; b++)
        {
            if (((buttonMask >> b) & 1) == 0) continue;
            if (b == GUIDE_BIT && guideRoutedToSysMenu) continue;
            int descBtn = (ButtonMap != null && b < ButtonMap.Length)
                ? ButtonMap[b] : b;
            if (descBtn >= 0 && descBtn < Buttons.Count)
                WriteBits(report, Buttons[descBtn].BitOffset + idOffset,
                          Buttons[descBtn].BitSize, 1);
        }

        return report;
    }

    static void WriteBits(byte[] buffer, int bitOffset, int bitSize, int value)
    {
        for (int b = 0; b < bitSize; b++)
        {
            int bit = (value >> b) & 1;
            int byteIdx = (bitOffset + b) / 8;
            int bitIdx = (bitOffset + b) % 8;
            if (byteIdx < buffer.Length)
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
