using System;
using System.Collections.Generic;

namespace HIDMaestro.Internal;

/// <summary>
/// Walks a HID report descriptor to find which Report IDs carry the
/// canonical HID PID 1.0 Pool / State / Block Load / Set Effect / Block
/// Free / Device Control reports. This lets the driver serve dinput8's
/// FFB enumeration on profiles whose firmware uses non-canonical Report
/// IDs (e.g. Microsoft SideWinder Force Feedback 2 puts Pool at Feature
/// RID 0x03, Block Load at Feature RID 0x02, Set Effect at Output RID
/// 0x01: all far from the canonical 0x13 / 0x12 / 0x11).
///
/// Detection key: each PID 1.0 report wraps its value items in a
/// Logical Collection (0xA1 0x02) whose Usage is a fixed PID Page
/// (0x0F) marker:
///     0x21  Set Effect Report             (Output)
///     0x5A  Set Envelope Report           (Output)
///     0x5F  Set Condition Report          (Output)
///     0x66  Download Force Sample Report  (Output)
///     0x6E  Set Periodic Report           (Output)
///     0x73  Set Custom Force Report       (Output)
///     0x74  Custom Force Data Report      (Output)
///     0x77  Effect Operation Report       (Output)
///     0x7D  Set Gain Report               (Output)
///     0x90  PID Block Free Report         (Output)
///     0x96  PID Device Control Report     (Output)
///     0x7F  PID Pool Report               (Feature)
///     0x88  Create New Effect Report      (Feature, bidirectional)
///     0x89  PID Block Load Report         (Feature)
///     0x92  PID State Report              (Feature)
///
/// We consume only the six the driver dispatches against (Pool, State,
/// Block Load, Create New Effect, Block Free, Device Control). Returns
/// canonical defaults (0x13/0x14/0x12/0x11/0x1B/0x1C) when a report
/// kind isn't found in the descriptor: back-compat for builder-emitted
/// AddPidFfbBlock descriptors that already use the canonical IDs.
///
/// Background: pre-v1.3.7 the driver hardcoded the canonical IDs in
/// IOCTL_UMDF_HID_GET_FEATURE / IOCTL_UMDF_HID_SET_FEATURE, so dinput8's
/// HidD_GetFeature(0x03) on a SideWinder virtual hit the "unknown RID"
/// fallthrough and returned STATUS_NOT_SUPPORTED. dinput8 found no Pool
/// data and refused to enumerate FFB. Issue: SideWinder FFB regression
/// reported during v1.3.6 PadForge testing.
/// </summary>
public static class PidReportIdExtractor
{
    public sealed class PidReportIds
    {
        public byte PoolReportId            = 0x13;
        public byte StateReportId           = 0x14;
        public byte BlockLoadReportId       = 0x12;
        public byte CreateNewEffectReportId = 0x11;
        public byte BlockFreeReportId       = 0x1B;
        public byte DeviceControlReportId   = 0x1C;

        /// <summary>True when the descriptor declared at least one PID 1.0
        /// report at a non-canonical Report ID. Pure diagnostic: driver
        /// behavior is identical regardless because zero defaults to
        /// canonical and a found mapping overrides.</summary>
        public bool AnyOverride;

        public override string ToString() =>
            $"Pool=0x{PoolReportId:X2} State=0x{StateReportId:X2} BlockLoad=0x{BlockLoadReportId:X2} " +
            $"CreateNewEffect=0x{CreateNewEffectReportId:X2} BlockFree=0x{BlockFreeReportId:X2} " +
            $"DeviceControl=0x{DeviceControlReportId:X2}";
    }

    // Direction of a Main item inside an Input(0x81) / Output(0x91) /
    // Feature(0xB1) statement.
    private enum Direction { Input, Output, Feature }

    /// <summary>
    /// Parse <paramref name="descriptor"/> and return the Report IDs that
    /// carry each PID 1.0 report kind. Missing kinds default to the
    /// canonical IDs so callers can pass the result to driver state
    /// unconditionally.
    /// </summary>
    public static PidReportIds Extract(byte[]? descriptor)
    {
        var ids = new PidReportIds();
        if (descriptor == null || descriptor.Length == 0) return ids;

        // Map: (collectionUsage, direction) -> Report ID.
        // First Main item under a given LC wins; subsequent Main items
        // under the same LC don't overwrite (descriptors typically
        // declare multiple value items inside one LC, all sharing the
        // same Report ID).
        var found = new Dictionary<(byte lcUsage, Direction dir), byte>();

        // Parser state. Usage Page and Report ID are global items, which
        // Push saves and Pop restores (HID 1.11, 6.2.2.7).
        ushort usagePage = 0;
        byte reportId = 0;
        var globals = new Stack<(ushort UsagePage, byte ReportId)>();
        // Local usages with their pages (page << 16 | usage), and each
        // item's data size: a 4-byte usage names its own page.
        var usages = new List<uint>();
        var usageSizes = new List<int>();
        var collectionStack = new List<(byte type, uint usage)>();
        // We need the most recent Usage Local item that *would* attach to
        // the next Main item (Collection or Input/Output/Feature). HID
        // local items reset on each Main. Track separately so collections
        // don't steal usages from data items.

        int i = 0;
        while (i < descriptor.Length)
        {
            byte head = descriptor[i];
            if (head == 0xFE)
            {
                if (i + 1 >= descriptor.Length) break;
                int longSize = descriptor[i + 1];
                i += 3 + longSize;
                continue;
            }

            int bSize = head & 0x03;
            if (bSize == 3) bSize = 4;
            int bType = (head >> 2) & 0x03;
            int bTag = (head >> 4) & 0x0F;

            int value = 0;
            for (int j = 0; j < bSize && i + 1 + j < descriptor.Length; j++)
                value |= descriptor[i + 1 + j] << (8 * j);

            switch (bType)
            {
                case 0: // Main
                    ConcatenateLastUsagePage(usages, usageSizes, usagePage);
                    switch (bTag)
                    {
                        case 9: // Output
                            RecordMain(found, collectionStack, reportId, Direction.Output);
                            break;
                        case 10: // Begin Collection
                            // Collection type byte follows in `value`. The
                            // most recent Local Usage attaches as the
                            // collection's usage (HID §6.2.2.6).
                            uint collUsage = usages.Count > 0 ? usages[0] : 0u;
                            collectionStack.Add(((byte)value, collUsage));
                            break;
                        case 11: // Feature
                            RecordMain(found, collectionStack, reportId, Direction.Feature);
                            break;
                        case 12: // End Collection
                            if (collectionStack.Count > 0)
                                collectionStack.RemoveAt(collectionStack.Count - 1);
                            break;
                    }
                    // Locals reset after every main item.
                    usages.Clear();
                    usageSizes.Clear();
                    break;

                case 1: // Global
                    switch (bTag)
                    {
                        case 0: usagePage = (ushort)value; break;
                        case 8: reportId = (byte)value; break; // Report ID
                        case 10: globals.Push((usagePage, reportId)); break; // Push
                        case 11: // Pop
                            if (globals.Count > 0) (usagePage, reportId) = globals.Pop();
                            break;
                        // Other globals (logical/physical min/max, report
                        // size/count, unit, etc.) don't affect classification.
                    }
                    break;

                case 2: // Local
                    switch (bTag)
                    {
                        case 0:
                            usages.Add(bSize == 4 ? (uint)value : ((uint)usagePage << 16) | (ushort)value);
                            usageSizes.Add(bSize);
                            break;
                        // Usage Min / Usage Max etc. don't matter for LC
                        // identification: we only key on the first usage
                        // that becomes the Collection or Main item's usage.
                    }
                    break;
            }

            i += 1 + bSize;
        }

        Apply(found, ids);
        return ids;
    }

    /// <summary>HID 1.11, 6.2.2.8, as hid-core.c applies it
    /// (hid_concatenate_last_usage_page) and HidReportMap does: at a main
    /// item the last Usage Page completes each short usage, walking back
    /// from the last one until one already has it.</summary>
    private static void ConcatenateLastUsagePage(List<uint> usages, List<int> sizes, ushort page)
    {
        for (int i = usages.Count - 1; i >= 0; i--)
        {
            if (sizes[i] == 4) continue;
            if (HidField.PageOf(usages[i]) == page) break;
            usages[i] = ((uint)page << 16) | (usages[i] & 0xFFFF);
        }
    }

    private static void RecordMain(
        Dictionary<(byte, Direction), byte> found,
        List<(byte type, uint usage)> stack,
        byte rid,
        Direction dir)
    {
        if (rid == 0) return;
        // Walk collection stack from innermost to outermost looking for the
        // first Logical Collection (type=0x02) whose usage we recognize as a
        // PID report kind. Inner Logical Collections take priority over
        // outer ones (e.g. a Set Effect Report (0x21) that contains a
        // sub-collection isn't reclassified by the outer Application
        // Collection (0x01)).
        for (int k = stack.Count - 1; k >= 0; k--)
        {
            var (type, usage) = stack[k];
            if (type != 0x02) continue; // we only key on Logical Collections
            // Only canonical PID LC usages we dispatch on, and only on the
            // PID page: usage 0x21 of a vendor page is no Set Effect Report.
            if (HidField.PageOf(usage) != 0x0F) continue;
            ushort id = HidField.IdOf(usage);
            if (id > 0xFF) continue;
            byte u = (byte)id;
            switch (u)
            {
                case 0x21: case 0x88:                          // Set / New Effect
                case 0x90: case 0x96: case 0x7F:               // Block Free / Device Control / Pool
                case 0x89: case 0x92:                          // Block Load / State
                    found.TryAdd((u, dir), rid);
                    return;
            }
        }
    }

    private static void Apply(
        Dictionary<(byte lcUsage, Direction dir), byte> found,
        PidReportIds ids)
    {
        // Pool: Feature, LC Usage 0x7F.
        if (found.TryGetValue((0x7F, Direction.Feature), out var poolRid))
        { ids.PoolReportId = poolRid; ids.AnyOverride |= poolRid != 0x13; }

        // State: Feature, LC Usage 0x92.
        if (found.TryGetValue((0x92, Direction.Feature), out var stateRid))
        { ids.StateReportId = stateRid; ids.AnyOverride |= stateRid != 0x14; }

        // Block Load: Feature, LC Usage 0x89.
        if (found.TryGetValue((0x89, Direction.Feature), out var blRid))
        { ids.BlockLoadReportId = blRid; ids.AnyOverride |= blRid != 0x12; }

        // Create New Effect: Feature is the canonical direction (the
        // host SETs it, dinput8 dispatches via HidD_SetFeature). LC
        // Usage 0x88 in the canonical PID descriptor; some firmwares
        // use Output LC 0x21 (Set Effect Report) as the create trigger
        // instead. Try Feature first, fall back to Output.
        if (found.TryGetValue((0x88, Direction.Feature), out var newFeat))
        { ids.CreateNewEffectReportId = newFeat; ids.AnyOverride |= newFeat != 0x11; }
        else if (found.TryGetValue((0x21, Direction.Output), out var newOut))
        { ids.CreateNewEffectReportId = newOut; ids.AnyOverride |= newOut != 0x11; }

        // Block Free: Output, LC Usage 0x90.
        if (found.TryGetValue((0x90, Direction.Output), out var bfRid))
        { ids.BlockFreeReportId = bfRid; ids.AnyOverride |= bfRid != 0x1B; }

        // Device Control: Output, LC Usage 0x96.
        if (found.TryGetValue((0x96, Direction.Output), out var dcRid))
        { ids.DeviceControlReportId = dcRid; ids.AnyOverride |= dcRid != 0x1C; }
    }
}
