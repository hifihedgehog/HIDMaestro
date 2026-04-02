using System;
using System.Collections.Generic;

namespace HIDMaestroTest;

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

    // Semantic axis mapping (resolved after parsing)
    public InputField? LeftStickX { get; private set; }
    public InputField? LeftStickY { get; private set; }
    public InputField? RightStickX { get; private set; }
    public InputField? RightStickY { get; private set; }
    public InputField? LeftTrigger { get; private set; }
    public InputField? RightTrigger { get; private set; }
    public InputField? HatSwitch { get; private set; }
    public List<InputField> Buttons { get; } = new();

    public static HidReportBuilder Parse(byte[] descriptor)
    {
        var builder = new HidReportBuilder();
        builder.ParseDescriptor(descriptor);
        builder.ResolveSemantics();
        return builder;
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
                        case 2: logicalMax = signedValue; break;          // Logical Max
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
        WriteField(LeftTrigger, leftTrigger);
        WriteField(RightTrigger, rightTrigger);

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

        for (int b = 0; b < Buttons.Count && b < 32; b++)
        {
            int val = ((buttonMask >> b) & 1) != 0 ? 1 : 0;
            WriteBits(report, Buttons[b].BitOffset + idOffset, Buttons[b].BitSize, val);
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
        Console.WriteLine($"    Buttons:  {Buttons.Count}");
    }
}
