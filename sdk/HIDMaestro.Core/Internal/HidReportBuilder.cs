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

    // Issue #58. The layout report is chosen by position, which is right
    // for every descriptor whose gamepad report comes first and wrong for
    // the one that puts a lizard-mode mouse ahead of its controller
    // state. A profile that declares which report carries its input is
    // the authority on that question; this is how it says so.
    private byte _preferredReportId;
    public int InputReportBitSize { get; private set; }
    public int InputReportByteSize => (InputReportBitSize + 7) / 8 + (InputReportId != 0 ? 1 : 0);
    public List<InputField> InputFields { get; } = new();

    /// <summary>Optional axis semantic override. When set, applied after
    /// ResolveSemantics to correct axis assignments for profiles where the
    /// default heuristic is wrong (e.g. Sony uses Z/Rz for right stick,
    /// Rx/Ry for triggers — opposite of the Xbox convention).</summary>
    // Canonical trigger positions for the combined-Z synthesis, resolved
    // lazily on first BuildReportInto (issue #34). Every repo path assigns
    // AxisMap during Parse, before any frame is built, but the setter
    // still invalidates the cache (audit of #34) so a late assignment can
    // never leave stale canonicals behind.
    private Dictionary<string, string>? _axisMap;
    private bool _canonicalTriggersResolved;
    private HMAxis _canonicalLtCached;
    private HMAxis _canonicalRtCached;

    public Dictionary<string, string>? AxisMap
    {
        get => _axisMap;
        set { _axisMap = value; _canonicalTriggersResolved = false; }
    }

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
    /// <summary>Third paired stick (v1.3.15, #124). Surfaced when a
    /// builder-emitted descriptor calls AddStick three times; the SDK's
    /// slot-pool allocator routes stick 3 onto Rx (0x33) + Ry (0x34) once
    /// X/Y and Z/Rz are claimed. Real-hardware profiles populate this via
    /// JSON layout when applicable.</summary>
    public InputField? ThirdStickX { get; private set; }
    /// <summary>Third paired stick Y axis. See <see cref="ThirdStickX"/>.</summary>
    public InputField? ThirdStickY { get; private set; }
    /// <summary>Fourth paired stick (v1.3.15, #124). Routes onto Slider
    /// (0x36) + Dial (0x37). joy.cpl labels them by HUT 1.5 Section 4.3
    /// names ("Slider" / "Dial"); consumer-side UIs may overlay a generic
    /// "Stick 4 X/Y" label and address the wire bytes via
    /// HMAxis.Slider / HMAxis.Dial.</summary>
    public InputField? FourthStickX { get; private set; }
    /// <summary>Fourth paired stick Y axis. See <see cref="FourthStickX"/>.</summary>
    public InputField? FourthStickY { get; private set; }
    public InputField? LeftTrigger { get; private set; }
    public InputField? RightTrigger { get; private set; }
    public InputField? CombinedTrigger { get; private set; } // Z axis for DI combined trigger
    public InputField? HatSwitch { get; private set; }
    public List<InputField> Buttons { get; } = new();

    /// <summary>Vendor-page 1-bit input fields that directly continue the
    /// button array on the wire (issue #48). The DualSense family declares
    /// 15 buttons and then a 13x1-bit vendor field (usage 0xFF00/0x21); the
    /// Edge's back paddles and Fn buttons live in that field's bits, NOT in
    /// declared buttons. A real Edge's extras are invisible to DirectInput
    /// and are read from the raw report by SDL, Steam Input, DS4Windows and
    /// dualsense-tester. <see cref="ButtonMap"/> values at or above
    /// <c>Buttons.Count</c> address this run (value N = bit N - Count), so a
    /// profile can place those buttons exactly where real firmware puts them
    /// without widening its descriptor away from the real device's shape.
    /// Only explicit map values reach it: identity mapping and out-of-range
    /// map indices never spill into vendor bits. Empty when the descriptor
    /// has no such contiguous run (DS4's 6-bit sequence counter and the Sony
    /// BT legacy 6-bit pad are single multi-bit fields and do not qualify).</summary>
    public List<InputField> VendorButtonBits { get; } = new();

    /// <summary>System Control / System Main Menu bit. On Xbox Series / Xbox
    /// One controllers the Guide (Xbox) button lives here, not in the regular
    /// gamepad button array. xinputhid parses this bit and exposes it via
    /// XInputGetStateEx (ordinal 100) as XINPUT_GAMEPAD_GUIDE. When this field
    /// is present, <see cref="BuildReport"/> routes <c>HMButton.Guide</c> to
    /// it; otherwise Guide falls back to the normal button-array path so
    /// profiles where Guide is a regular button (Xbox 360, Sony, etc.) still
    /// work via <see cref="ButtonMap"/>.</summary>
    public InputField? SystemMainMenu { get; private set; }

    /// <summary>Every analog input field whose HID usage maps to a known
    /// <see cref="HMAxis"/>. Populated during ResolveSemantics from BOTH
    /// Generic Desktop (page 0x01) and Simulation Controls (page 0x02).
    /// Keyed by the encoded (page &lt;&lt; 8 | usage) pair so consumers can
    /// drive any descriptor-declared axis via
    /// <see cref="HMGamepadState.ExtraAxes"/> regardless of whether it
    /// also lands in a semantic slot. First field wins on duplicate
    /// usages — matches the <c>??=</c> behavior of the semantic
    /// classifier.</summary>
    public Dictionary<HMAxis, InputField> AxisFields { get; } = new();

    /// <param name="preferredReportId">The report the profile itself
    /// declares as its input report, or 0 to select by position. Only a
    /// report the descriptor actually carries an Input item for is
    /// honored, so a stale declaration falls back rather than naming a
    /// report that does not exist (issue #58).</param>
    public static HidReportBuilder Parse(byte[] descriptor, Dictionary<string, string>? axisMap = null,
                                         byte preferredReportId = 0)
    {
        var builder = new HidReportBuilder();
        builder._preferredReportId = preferredReportId;
        builder.ParseDescriptor(descriptor);
        builder.ResolveSemantics();
        if (axisMap != null)
        {
            builder.AxisMap = axisMap;
            builder.ApplyAxisMap(axisMap);
        }
        return builder;
    }

    /// <summary>v1.3.9 — apply role-tagged Layout overrides on top of the
    /// classifier's resolved semantic slots. Profiles whose JSON authors a
    /// <c>layout</c> block deterministically map their axes into the
    /// classic 4-slot stick + 2-slot trigger framework based on the
    /// layout's role tags, so consumers reading <see cref="HMProfile.StickCount"/>
    /// / <see cref="HMProfile.TriggerCount"/> see the authored truth
    /// rather than the descriptor-shape heuristic.
    ///
    /// <para>Mapping rules per kind:
    /// <list type="bullet">
    /// <item>gamepad — leave classifier output alone (already author-aligned)</item>
    /// <item>joystick / flight_stick — stick.x/y → LeftStick, throttle.axis → LeftTrigger,
    ///       rudder.axis → RightTrigger; clear right stick</item>
    /// <item>hotas — stick.x/y → LeftStick, throttle_primary.axis → LeftTrigger,
    ///       stick_rudder.axis → RightTrigger; clear right stick</item>
    /// <item>wheel — wheel.axis → LeftStickX, pedal[role=clutch] → LeftStickY,
    ///       pedal[role=accelerator|throttle] → LeftTrigger,
    ///       pedal[role=brake] → RightTrigger; clear right stick</item>
    /// <item>pedals — pedal[role=accelerator|throttle] → LeftTrigger,
    ///       pedal[role=brake] → RightTrigger; clear sticks</item>
    /// <item>handbrake / single_axis_accessory — axis → LeftTrigger; clear
    ///       sticks and right trigger</item>
    /// <item>arcade_stick / dance_pad / guitar / motion_wand / remote /
    ///       controller_adapter / shifter — clear sticks and triggers
    ///       (these devices don't fit the simple-slot framework)</item>
    /// </list></para>
    ///
    /// <para>Buttons are left untouched — descriptor's button array is the
    /// truth and ButtonMap remaps as before. Layout's button-role
    /// information is exposed via <see cref="HMProfile.AsGamepad"/> et al.
    /// without affecting the encoder.</para></summary>
    public void ApplyLayoutSemantics(HMLayout layout)
    {
        if (layout is HMUnspecifiedLayout) return;

        InputField? FindByAxis(HMAxis axis) =>
            AxisFields.TryGetValue(axis, out var f) ? f : null;

        InputField? FindPedal(IReadOnlyList<HMPedal> pedals, params HMAxisRole[] roles)
        {
            foreach (var p in pedals)
                if (Array.IndexOf(roles, p.Role) >= 0)
                    return FindByAxis(p.Axis);
            return null;
        }

        switch (layout)
        {
            case HMGamepadLayout:
                // Author-aligned; classifier already correct.
                return;

            case HMJoystickLayout j:
                LeftStickX  = FindByAxis(j.Stick.XAxis);
                LeftStickY  = FindByAxis(j.Stick.YAxis);
                RightStickX = null; RightStickY = null;
                LeftTrigger  = j.Throttle is { } jt ? FindByAxis(jt.Axis) : null;
                RightTrigger = j.Rudder   is { } jr ? FindByAxis(jr.Axis) : null;
                CombinedTrigger = null;
                break;

            case HMFlightStickLayout f:
                LeftStickX  = FindByAxis(f.Stick.XAxis);
                LeftStickY  = FindByAxis(f.Stick.YAxis);
                RightStickX = null; RightStickY = null;
                LeftTrigger  = f.Throttle is { } ft ? FindByAxis(ft.Axis) : null;
                RightTrigger = f.Rudder   is { } fr ? FindByAxis(fr.Axis) : null;
                CombinedTrigger = null;
                break;

            case HMHotasLayout h:
                LeftStickX  = FindByAxis(h.Stick.XAxis);
                LeftStickY  = FindByAxis(h.Stick.YAxis);
                RightStickX = null; RightStickY = null;
                LeftTrigger  = h.ThrottlePrimary is { } hp ? FindByAxis(hp.Axis) : null;
                RightTrigger = h.StickRudder    is { } hr ? FindByAxis(hr.Axis) : null;
                CombinedTrigger = null;
                break;

            case HMWheelLayout w:
                LeftStickX  = FindByAxis(w.Wheel.Axis);
                LeftStickY  = FindPedal(w.Pedals, HMAxisRole.Clutch);
                RightStickX = null; RightStickY = null;
                LeftTrigger  = FindPedal(w.Pedals, HMAxisRole.Accelerator, HMAxisRole.Throttle);
                RightTrigger = FindPedal(w.Pedals, HMAxisRole.Brake);
                CombinedTrigger = null;
                break;

            case HMPedalsLayout p:
                LeftStickX = null; LeftStickY = null;
                RightStickX = null; RightStickY = null;
                LeftTrigger  = FindPedal(p.Pedals, HMAxisRole.Accelerator, HMAxisRole.Throttle);
                RightTrigger = FindPedal(p.Pedals, HMAxisRole.Brake);
                CombinedTrigger = null;
                break;

            case HMHandbrakeLayout hb:
                LeftStickX = null; LeftStickY = null;
                RightStickX = null; RightStickY = null;
                LeftTrigger  = FindByAxis(hb.Axis);
                RightTrigger = null;
                CombinedTrigger = null;
                break;

            case HMSingleAxisAccessoryLayout sa:
                LeftStickX = null; LeftStickY = null;
                RightStickX = null; RightStickY = null;
                LeftTrigger  = FindByAxis(sa.Axis);
                RightTrigger = null;
                CombinedTrigger = null;
                break;

            case HMArcadeStickLayout:
            case HMDancePadLayout:
            case HMGuitarLayout:
            case HMMotionWandLayout:
            case HMRemoteLayout:
            case HMShifterLayout:
            case HMControllerAdapterLayout:
                // None of these map onto the simple stick/trigger framework.
                // Consumers that need to drive them read AvailableAxes /
                // ExtraAxes / per-kind layout accessors directly.
                LeftStickX = null; LeftStickY = null;
                RightStickX = null; RightStickY = null;
                LeftTrigger = null; RightTrigger = null;
                CombinedTrigger = null;
                break;
        }
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

        // v1.3.15 (#124): collect every InputField that the axisMap is about
        // to reassign, then scrub each of them out of ALL semantic slots
        // before any assignment runs. ResolveSemantics may have cascaded
        // those fields into ThirdStickX/Y / FourthStickX/Y (e.g. Sony BT Rx
        // at ReportCount==2 gets claimed as ThirdStickX before axisMap
        // overrides it to leftTrigger), and the prior clear-slot loop only
        // covered the 6 target-named slots — the cascade ghosts survived and
        // bloated Profile.Sticks.Count.
        var assignedFields = new HashSet<InputField>();
        foreach (var kvp in map)
        {
            ushort usage = Convert.ToUInt16(kvp.Key, 16);
            if (fieldByUsage.TryGetValue(usage, out var f))
                assignedFields.Add(f);
        }
        if (LeftStickX  != null && assignedFields.Contains(LeftStickX))  LeftStickX  = null;
        if (LeftStickY  != null && assignedFields.Contains(LeftStickY))  LeftStickY  = null;
        if (RightStickX != null && assignedFields.Contains(RightStickX)) RightStickX = null;
        if (RightStickY != null && assignedFields.Contains(RightStickY)) RightStickY = null;
        if (ThirdStickX != null && assignedFields.Contains(ThirdStickX)) ThirdStickX = null;
        if (ThirdStickY != null && assignedFields.Contains(ThirdStickY)) ThirdStickY = null;
        if (FourthStickX != null && assignedFields.Contains(FourthStickX)) FourthStickX = null;
        if (FourthStickY != null && assignedFields.Contains(FourthStickY)) FourthStickY = null;
        if (LeftTrigger  != null && assignedFields.Contains(LeftTrigger))  LeftTrigger  = null;
        if (RightTrigger != null && assignedFields.Contains(RightTrigger)) RightTrigger = null;

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
        int collectionDepth = 0;

        // Fields + running bit offset per input report ID, in encounter
        // order. The parser historically kept only the FIRST input report,
        // which held for every profile because their gamepad report came
        // first. The real Switch Pro BT descriptor (issue #37) declares
        // its vendor-blob inputs (0x21/0x30/0x31-0x33, page 0xFF01)
        // BEFORE the parseable gamepad report 0x3F, so collect every
        // input report and select afterward.
        var fieldsByReport = new Dictionary<byte, List<InputField>>();
        var bitOffsetByReport = new Dictionary<byte, int>();
        var reportOrder = new List<byte>();

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
                            if (!fieldsByReport.TryGetValue(reportId, out var fields))
                            {
                                fields = new List<InputField>();
                                fieldsByReport[reportId] = fields;
                                bitOffsetByReport[reportId] = 0;
                                reportOrder.Add(reportId);
                            }
                            int bitOffset = bitOffsetByReport[reportId];
                            if (usageMin != 0 && usageMax != 0)
                            {
                                // Button range
                                for (int b = 0; b < reportCount; b++)
                                {
                                    ushort u = (ushort)(usageMin + b);
                                    if (u > usageMax) u = usageMax;
                                    fields.Add(new InputField(usagePage, u,
                                        bitOffset + b * reportSize, reportSize,
                                        logicalMin, logicalMax, isConstant, reportCount));
                                }
                            }
                            else
                            {
                                for (int c = 0; c < reportCount; c++)
                                {
                                    ushort u = c < usages.Count ? usages[c] : (ushort)0;
                                    fields.Add(new InputField(usagePage, u,
                                        bitOffset + c * reportSize, reportSize,
                                        logicalMin, logicalMax, isConstant, reportCount));
                                }
                            }
                            bitOffsetByReport[reportId] = bitOffset + reportSize * reportCount;
                            usages.Clear();
                            usageMin = usageMax = 0;
                            break;
                        case 9: // Output (different report direction), skip
                        case 11: // Feature, skip
                            usages.Clear();
                            usageMin = usageMax = 0;
                            break;
                        case 10: // Collection. A usage before it is the collection's, not the input's.
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
                        case 8: reportId = (byte)value; break;            // Report ID
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

        // Select the layout report: the first input report carrying a
        // non-constant field on a gamepad-parseable page (Generic
        // Desktop 0x01 for axes/hats, Simulation 0x02 for pedals,
        // Button 0x09). Identical to the old first-report rule for
        // every descriptor whose first input report is the gamepad
        // report; picks 0x3F on the Switch-family BT descriptors where
        // the vendor blobs come first. Falls back to the plain first
        // input report when no report qualifies (e.g. a vendor channel
        // plus a Consumer-page media-key report), preserving the old
        // rule exactly for descriptors with no gamepad report at all.
        byte chosenId = 0;
        bool chosen = false;
        // The profile's own declaration outranks position, but only for a
        // report the descriptor really declares Input items for.
        if (_preferredReportId != 0 && reportOrder.Contains(_preferredReportId))
        {
            chosenId = _preferredReportId;
            chosen = true;
        }
        foreach (var id in chosen ? Array.Empty<byte>() : (IEnumerable<byte>)reportOrder)
        {
            foreach (var f in fieldsByReport[id])
            {
                if (!f.IsConstant &&
                    (f.UsagePage == 0x01 || f.UsagePage == 0x02 || f.UsagePage == 0x09))
                {
                    chosenId = id;
                    chosen = true;
                    break;
                }
            }
            if (chosen) break;
        }
        if (!chosen && reportOrder.Count > 0)
        {
            chosenId = reportOrder[0];
            chosen = true;
        }
        if (chosen)
        {
            InputReportId = chosenId;
            InputFields.AddRange(fieldsByReport[chosenId]);
            InputReportBitSize = bitOffsetByReport[chosenId];
        }
    }

    void ResolveSemantics()
    {
        // Pre-scan for two right-stick patterns that fall outside the default
        // "Rx/Ry = right stick, Z/Rz = triggers" Xbox 360 wire convention:
        //
        //   (a) fourAxisDInput — no Rx/Ry at all, Z+Rz both present (WebKit/
        //       Chromium's "standard gamepad", Logitech F310 DInput mode).
        //       Z/Rz are the right stick.
        //   (b) zRzAreSticksByCount — Z or Rz declared with Report Count >= 2,
        //       i.e. paired-axis stick shape rather than single-axis trigger
        //       shape. The Rx/Ry-as-triggers convention HidDescriptorBuilder
        //       emits since v1.3.14 (issue #27: pre-2005 DInput games like
        //       Jedi Outcast / Serious Sam TFE bind right-stick from
        //       DIJOYSTATE.lZ/lRz and miss Rx/Ry-only virtuals).
        //
        // Z OR Rz alone (no pair, no wide count) means a single trigger axis,
        // not half a stick. See issues #5, #22, #27.
        bool hasRxOrRy = false;
        bool hasZ = false;
        bool hasRz = false;
        bool zRzAreSticksByCount = false;
        foreach (var f in InputFields)
        {
            if (f.IsConstant || f.UsagePage != 0x01) continue;
            if (f.Usage == 0x33 || f.Usage == 0x34) hasRxOrRy = true;
            else if (f.Usage == 0x32) { hasZ = true; if (f.ReportCount >= 2) zRzAreSticksByCount = true; }
            else if (f.Usage == 0x35) { hasRz = true; if (f.ReportCount >= 2) zRzAreSticksByCount = true; }
        }
        bool fourAxisDInput = !hasRxOrRy && hasZ && hasRz;
        bool zRzAreSticks = fourAxisDInput || zRzAreSticksByCount;

        // A Generic Desktop field declared with Report Count == 1 and unsigned
        // range starting at 0 is unambiguously a trigger (matches what
        // HidDescriptorBuilder.AddTrigger emits). This wins over the
        // right-stick fallback so a (1 stick, 1 trigger) or (2 sticks, 1
        // trigger) Custom layout doesn't get its lone trigger silently
        // claimed as right-stick X. See issue #22.
        static bool LooksLikeTrigger(InputField f) =>
            f.ReportCount == 1 && f.LogicalMin == 0;

        // Cascade claim helpers (v1.3.15, #124). Stick-shaped axes flow
        // RightStickX → ThirdStickX → FourthStickX in field-encounter order;
        // trigger-shaped axes flow LeftTrigger → RightTrigger. The builder's
        // slot-pool allocator emits sticks 1-4 onto X+Y / Z+Rz / Rx+Ry /
        // Slider+Dial, and these cascades land each declared pair into the
        // next free semantic slot regardless of which Usage code it sits on.
        void ClaimRightStickX(InputField f)
        {
            if (RightStickX == null) RightStickX = f;
            else if (ThirdStickX == null) ThirdStickX = f;
            else if (FourthStickX == null) FourthStickX = f;
        }
        void ClaimRightStickY(InputField f)
        {
            if (RightStickY == null) RightStickY = f;
            else if (ThirdStickY == null) ThirdStickY = f;
            else if (FourthStickY == null) FourthStickY = f;
        }
        // Trigger-claim with preference: case 0x32/0x33/0x36 (Z, Rx, Slider)
        // prefer LeftTrigger; case 0x34/0x35/0x37 (Ry, Rz, Dial) prefer
        // RightTrigger. Cascade to the other slot when the preferred one is
        // already taken. Preserves the v1.3.14 (LT=Rx, RT=Ry) layout for the
        // 2-stick + 2-trigger common case while letting Slider/Dial cascade
        // naturally for 3-stick + 2-trigger configs (#124).
        void ClaimLeftTrigger(InputField f)
        {
            if (LeftTrigger == null) LeftTrigger = f;
            else if (RightTrigger == null) RightTrigger = f;
        }
        void ClaimRightTrigger(InputField f)
        {
            if (RightTrigger == null) RightTrigger = f;
            else if (LeftTrigger == null) LeftTrigger = f;
        }

        // Map HID usages to semantic gamepad axes/buttons
        // This works for any standard gamepad descriptor
        foreach (var f in InputFields)
        {
            if (f.IsConstant) continue;

            // Catalog every recognized ANALOG usage by HMAxis. Skip Hat
            // Switch (0x39) — Hat is its own input shape with the
            // descriptor's HatSwitch field handling, not part of the
            // analog-axis encoder pass. HMAxis.Hat exists for layout
            // references (HMHatBinding.axis) but doesn't get registered
            // into AxisFields.
            if ((f.UsagePage == 0x01 || f.UsagePage == 0x02)
                && !(f.UsagePage == 0x01 && f.Usage == 0x39))
            {
                var key = (HMAxis)((f.UsagePage << 8) | f.Usage);
                if (Enum.IsDefined(typeof(HMAxis), key) && !AxisFields.ContainsKey(key))
                    AxisFields[key] = f;
            }

            if (f.UsagePage == 0x01) // Generic Desktop
            {
                switch (f.Usage)
                {
                    case 0x30: LeftStickX ??= f; break;    // X
                    case 0x31: LeftStickY ??= f; break;    // Y
                    case 0x32:                               // Z
                        if (LooksLikeTrigger(f))
                            ClaimLeftTrigger(f);
                        else if (zRzAreSticks)
                            ClaimRightStickX(f);
                        else
                            ClaimLeftTrigger(f);
                        break;
                    case 0x33:                               // Rx
                        if (LooksLikeTrigger(f))
                            ClaimLeftTrigger(f);
                        else
                            ClaimRightStickX(f);
                        break;
                    case 0x34:                               // Ry
                        if (LooksLikeTrigger(f))
                            ClaimRightTrigger(f);
                        else
                            ClaimRightStickY(f);
                        break;
                    case 0x35:                               // Rz
                        if (LooksLikeTrigger(f))
                            ClaimRightTrigger(f);
                        else if (zRzAreSticks)
                            ClaimRightStickY(f);
                        else
                            ClaimRightTrigger(f);
                        break;
                    case 0x36:                               // Slider — also stick 4 X under v1.3.15 pool
                        if (LooksLikeTrigger(f))
                            ClaimLeftTrigger(f);
                        else
                            ClaimRightStickX(f);
                        break;
                    case 0x37:                               // Dial — also stick 4 Y under v1.3.15 pool
                        if (LooksLikeTrigger(f))
                            ClaimRightTrigger(f);
                        else
                            ClaimRightStickY(f);
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

        // Collect the vendor 1-bit run that continues the button array
        // (issue #48). Strictly contiguous from the last declared button:
        // each qualifying bit extends the run by one, anything else ends
        // it. InputFields is in declaration order, so a single ordered
        // pass suffices.
        if (Buttons.Count > 0)
        {
            int runEnd = Buttons[^1].BitOffset + Buttons[^1].BitSize;
            foreach (var f in InputFields)
            {
                if (f.UsagePage < 0xFF00 || f.IsConstant || f.BitSize != 1)
                    continue;
                if (f.BitOffset != runEnd) continue;
                VendorButtonBits.Add(f);
                runEnd += 1;
            }
        }
    }

    /// <summary>v1.3.9 — single-source axis-dict encoder. Caller writes
    /// state.Axes keyed by HID usage; the encoder iterates every declared
    /// analog input field in the descriptor, reads the dict, and writes
    /// the report. Axes the consumer doesn't write default to centered
    /// (signed axes) or released (unsigned axes) automatically.
    /// All values are <c>[0.0..1.0]</c> normalized.</summary>
    /// <summary>v1.3.9 — convenience: build an axes dict from the canonical
    /// 6-slot convention (leftStickX/Y, rightStickX/Y, leftTrigger,
    /// rightTrigger) using the classifier-resolved semantic slots
    /// (<see cref="LeftStickX"/>, etc.) as keys. Mainly for tests and one-shot
    /// frame submissions; production hot paths should pre-populate the dict
    /// once and update entries by HMAxis directly.</summary>
    public Dictionary<HMAxis, float> StandardAxes(
        double leftX = 0.5, double leftY = 0.5,
        double rightX = 0.5, double rightY = 0.5,
        double leftTrigger = 0.0, double rightTrigger = 0.0)
    {
        var d = new Dictionary<HMAxis, float>();
        static HMAxis Key(InputField f) => (HMAxis)((f.UsagePage << 8) | f.Usage);
        if (LeftStickX  != null) d[Key(LeftStickX)]  = (float)leftX;
        if (LeftStickY  != null) d[Key(LeftStickY)]  = (float)leftY;
        if (RightStickX != null) d[Key(RightStickX)] = (float)rightX;
        if (RightStickY != null) d[Key(RightStickY)] = (float)rightY;
        if (LeftTrigger != null) d[Key(LeftTrigger)] = (float)leftTrigger;
        if (RightTrigger!= null) d[Key(RightTrigger)]= (float)rightTrigger;
        return d;
    }

    public byte[] BuildReport(
        IReadOnlyDictionary<HMAxis, float>? axes = null,
        int hatValue = 0,
        uint buttonMask = 0,
        float? hatDegrees = null,
        int? hatHundredths = null,
        ushort? hatRaw = null)
    {
        byte[] report = new byte[InputReportByteSize];
        BuildReportInto(report, axes, hatValue, buttonMask, hatDegrees, hatHundredths, hatRaw);
        return report;
    }

    /// <summary>v1.3.9 — buffer-reuse encoder. Caller supplies a byte[]
    /// of length <see cref="InputReportByteSize"/>; we zero it and pack
    /// the report into it. Avoids the per-frame byte[] alloc on the
    /// SubmitState hot path.
    ///
    /// <para>The encoder iterates every analog input field declared in
    /// the descriptor and reads its value from <paramref name="axes"/>:
    /// signed-centered axes default to 0.5 (centered) when not in the
    /// dict; unsigned axes default to 0.0 (released). Combined-Z trigger
    /// synthesis, hat priority chain (HatDegrees &gt; HatHundredths &gt;
    /// HatRaw &gt; Hat), trigger-to-button derivation, Guide routing, and
    /// button packing all run as before — only the axis-write path
    /// changed from named slots to dict-driven.</para></summary>
    public void BuildReportInto(byte[] report,
        IReadOnlyDictionary<HMAxis, float>? axes,
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

        double GetAxisValue(HMAxis key, double def)
        {
            if (axes != null && axes.TryGetValue(key, out var v))
                return Math.Clamp(v, 0f, 1f);
            return def;
        }

        // Per-axis pass: write every declared analog input from the dict.
        // Auto-default: signed-centered axes (LogicalMin < 0) → 0.5 (centered);
        // unsigned axes → 0.0 (released).
        foreach (var (key, field) in AxisFields)
        {
            double def = field.LogicalMin < 0 ? 0.5 : 0.0;
            WriteField(field, GetAxisValue(key, def));
        }

        // Combined-Z trigger synthesis (Xbox 360 wired with Vx/Vy hidden pair):
        // when CombinedTrigger (Z) is declared alongside Vx/Vy as separate
        // triggers, dinput sees combined Z; XInput / WGI see the separate
        // Vx/Vy. PadForge#130 / v1.3.17: the v1.3.9 state.Axes refactor sourced
        // lt / rt from state.Axes[LeftTrigger.Usage] / state.Axes[RightTrigger.Usage]
        // — i.e. state.Axes[Vx] / state.Axes[Vy]. Consumers (PadForge's
        // ResolveAxisByRole, every HMGamepadStateHelpers caller) write
        // state.Axes[HMAxis.Z] = leftTrigger and state.Axes[HMAxis.Rz] =
        // rightTrigger when the profile has no axisMap, so Vx / Vy stayed at
        // their seed values and the synthesis always landed on combined =
        // 0.5. All Xbox 360 profiles silently lost their split-trigger
        // workaround. The fix: read trigger values from the CANONICAL
        // user-facing positions (axisMap-declared or HMAxis.Z / HMAxis.Rz
        // default), then write both the combined Z byte AND the separate
        // Vx / Vy bytes so dinput sees the combined value and WGI sees the
        // raw triggers.
        if (CombinedTrigger != null && LeftTrigger != null && RightTrigger != null)
        {
            // Canonical trigger positions, resolved once per builder
            // (issue #34). The builder instance is per-profile cached
            // (GetOrBuildReportBuilder) and AxisMap is fixed after
            // construction, so the axisMap walk (hex parse + role
            // compares) that used to run per frame is a constant. Shares
            // HMController.ResolveCanonicalAxis so both lanes resolve
            // identically (the v1.3.17 lesson: fix all readers in
            // lockstep).
            if (!_canonicalTriggersResolved)
            {
                _canonicalLtCached = HMController.ResolveCanonicalAxis(AxisMap, "lefttrigger", HMAxis.Z);
                _canonicalRtCached = HMController.ResolveCanonicalAxis(AxisMap, "righttrigger", HMAxis.Rz);
                _canonicalTriggersResolved = true;
            }
            HMAxis canonicalLt = _canonicalLtCached;
            HMAxis canonicalRt = _canonicalRtCached;
            var leftFieldKey  = (HMAxis)((LeftTrigger.UsagePage  << 8) | LeftTrigger.Usage);
            var rightFieldKey = (HMAxis)((RightTrigger.UsagePage << 8) | RightTrigger.Usage);
            // Source the trigger values from the canonical (user-facing)
            // position first — PadForge / HMGamepadStateHelpers write here.
            // Fall back to the wire field's own Usage so the test-fixture
            // path (HidReportBuilder.StandardAxes writes to LeftTrigger.Usage
            // = Vx) keeps working without a canonical entry.
            double GetTrigger(HMAxis canonical, HMAxis fieldKey, double def)
            {
                if (axes != null)
                {
                    if (axes.TryGetValue(canonical, out var vCanon))
                        return Math.Clamp(vCanon, 0f, 1f);
                    if (axes.TryGetValue(fieldKey, out var vField))
                        return Math.Clamp(vField, 0f, 1f);
                }
                return def;
            }
            double lt = GetTrigger(canonicalLt, leftFieldKey,  0.0);
            double rt = GetTrigger(canonicalRt, rightFieldKey, 0.0);
            double combined = Math.Clamp(0.5 + (rt - lt) * 0.5, 0.0, 1.0);
            WriteField(CombinedTrigger, combined);
            WriteField(LeftTrigger,  lt);
            WriteField(RightTrigger, rt);
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
        if (TriggerButtons != null && TriggerButtons.Length >= 2 && LeftTrigger != null && RightTrigger != null)
        {
            var ltKey = (HMAxis)((LeftTrigger.UsagePage << 8) | LeftTrigger.Usage);
            var rtKey = (HMAxis)((RightTrigger.UsagePage << 8) | RightTrigger.Usage);
            double lt = GetAxisValue(ltKey, 0.0);
            double rt = GetAxisValue(rtKey, 0.0);
            if (lt > 0.0 && TriggerButtons[0] >= 0 && TriggerButtons[0] < Buttons.Count)
                WriteBits(report, Buttons[TriggerButtons[0]].BitOffset + idOffset,
                          Buttons[TriggerButtons[0]].BitSize, 1);
            if (rt > 0.0 && TriggerButtons[1] >= 0 && TriggerButtons[1] < Buttons.Count)
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
            // A -1 map entry is the explicit "this profile does not carry
            // this button" sentinel (issue #48): it fails both range checks
            // below and the bit is dropped. Sony maps use it for Share and,
            // on non-Edge pads, the paddles, so those HMButton bits no
            // longer alias onto PS / Touchpad / Mute by identity fallback.
            bool mapped = ButtonMap is { } map && b < map.Length;
            int descBtn = mapped ? ButtonMap![b] : b;
            if ((uint)descBtn < (uint)Buttons.Count)
                WriteBits(report, Buttons[descBtn].BitOffset + idOffset,
                          Buttons[descBtn].BitSize, 1);
            else if (mapped && (uint)(descBtn - Buttons.Count) < (uint)VendorButtonBits.Count)
            {
                // Explicit map values past the declared buttons address the
                // contiguous vendor-bit run (DualSense Edge paddles / Fn,
                // see VendorButtonBits). Identity-mapped bits never land
                // here: only a profile that opts in via buttonMap can write
                // vendor bits.
                var vb = VendorButtonBits[descBtn - Buttons.Count];
                WriteBits(report, vb.BitOffset + idOffset, vb.BitSize, 1);
            }
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
