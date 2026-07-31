using System;
using System.Collections.Generic;
using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>
/// A controller profile — the description of a real-world controller that a
/// virtual device can masquerade as. Profiles are immutable, identified by a
/// stable string ID slug like "xbox-360-wired" or "dualsense".
///
/// <para>Get built-in instances via <see cref="HMContext.GetProfile(string)"/>
/// or <see cref="HMContext.AllProfiles"/>. Create custom profiles from scratch
/// via <see cref="HMProfileBuilder"/>.</para>
///
/// <para>All profile characteristics — VID/PID, descriptor bytes, axis layout,
/// button count, connection type — are publicly accessible for inspection and
/// for building modified variants.</para>
/// </summary>
public sealed class HMProfile
{
    internal ControllerProfile Inner { get; }

    internal HMProfile(ControllerProfile inner) { Inner = inner; }

    // ── Identity ─────────────────────────────────────────────────────────

    /// <summary>Stable identifier slug, e.g. "xbox-series-xs-bt".</summary>
    public string Id => Inner.Id;

    /// <summary>Human-readable name shown in UIs, e.g. "Xbox Series X|S Controller (Bluetooth)".</summary>
    public string Name => Inner.Name;

    /// <summary>Vendor name, e.g. "Microsoft", "Sony", "Logitech".</summary>
    public string Vendor => Inner.Vendor;

    /// <summary>USB Vendor ID as a 16-bit integer.</summary>
    public ushort VendorId => Inner.VendorId;

    /// <summary>USB Product ID as a 16-bit integer.</summary>
    public ushort ProductId => Inner.ProductId;

    /// <summary>The product string the device reports to the OS, e.g. "Wireless Controller".</summary>
    public string ProductString => Inner.ProductString;

    /// <summary>The manufacturer string the device reports, e.g. "Microsoft".</summary>
    public string ManufacturerString => Inner.ManufacturerString ?? Inner.Vendor ?? "";

    /// <summary>Device Manager display name. Falls back to <see cref="ProductString"/>.</summary>
    public string DisplayName => Inner.DisplayName;

    /// <summary>Controller category — "gamepad", "wheel", "joystick", "arcade", etc.</summary>
    public string Type => Inner.Type;

    // ── Connection + driver characteristics ───────────────────────────────

    /// <summary>Connection type: "usb", "bluetooth", or "wireless-adapter".</summary>
    public string Connection => Inner.Connection ?? "usb";

    /// <summary>Driver mode: "xinputhid" for Xbox BT controllers that bind
    /// Microsoft's xinputhid.sys, or null for standard HID profiles.</summary>
    public string? DriverMode => Inner.DriverMode;

    /// <summary>Trigger axis layout: "combined" (Xbox 360 shared Z axis),
    /// "separate" (independent LT/RT), or null (non-gamepad).</summary>
    public string? TriggerMode => Inner.TriggerMode;

    /// <summary>Which instantiation path this profile needs: "umdf2" for
    /// the single-HID-device path most profiles use, or "usbip" for a
    /// composite USB persona whose audio interfaces need the bundled USB
    /// transport (issue #39).</summary>
    public string Backend => Inner.Backend ?? "umdf2";

    /// <summary>True when this profile is a composite USB persona, which
    /// rides the USB transport bundled inside HIDMaestro.Core.dll rather
    /// than the UMDF2 driver.
    ///
    /// <para>This is NOT a "can I create it" gate: the transport deploys
    /// itself on first use, so <see cref="HMContext.CreateController"/>
    /// works regardless. Read it when a picker wants to mark which
    /// entries bring controller audio, or to warn that the first such
    /// controller triggers a one-time driver install (issue #39).</para></summary>
    public bool RequiresUsbipBackend => Inner.RequiresUsbipBackend;

    // ── HID descriptor ───────────────────────────────────────────────────

    /// <summary>True if this profile has a HID descriptor and can be deployed
    /// as a virtual controller. Some catalog entries are placeholders.</summary>
    public bool IsDeployable => Inner.HasDescriptor;

    /// <summary>Input report size in bytes (including Report ID byte if any).
    /// Returns 0 if not specified in the profile.</summary>
    public int InputReportSize => Inner.InputReportSize ?? 0;

    /// <summary>The raw HID report descriptor bytes. Returns a copy — modifying
    /// the returned array does not affect the profile. Returns null if the
    /// profile has no descriptor (not deployable).</summary>
    public byte[]? GetDescriptorBytes()
    {
        var src = Inner.GetDescriptorBytes();
        if (src == null) return null;
        var copy = new byte[src.Length];
        Array.Copy(src, copy, src.Length);
        return copy;
    }

    /// <summary>The HID report descriptor as a hex string (same format as the
    /// profile JSON's "descriptor" field). Null if no descriptor.</summary>
    public string? DescriptorHex => Inner.Descriptor;

    // ── Parsed descriptor layout ─────────────────────────────────────────

    /// <summary>Number of buttons declared in the HID descriptor.</summary>
    public int ButtonCount => GetLayout()?.Buttons.Count ?? 0;

    /// <summary>Number of analog axes declared in the descriptor — counts
    /// every Generic Desktop / Simulation Controls input axis the parser
    /// recognizes (sticks, triggers, sliders, throttles, rudders, pedals,
    /// etc.). Use <see cref="AvailableAxes"/> to enumerate them by HID
    /// usage.</summary>
    public int AxisCount => GetLayout()?.AxisFields.Count ?? 0;


    /// <summary>Every analog axis the descriptor declares, addressable by
    /// HID usage via <see cref="HMGamepadState.ExtraAxes"/>. Empty list
    /// when the profile has no descriptor or no recognized axes. Stable
    /// across repeated calls — the underlying layout is parsed once and
    /// cached.</summary>
    public IReadOnlyList<HMAxis> AvailableAxes
    {
        get
        {
            var l = GetLayout();
            if (l == null) return Array.Empty<HMAxis>();
            var arr = new HMAxis[l.AxisFields.Count];
            int i = 0;
            foreach (var k in l.AxisFields.Keys) arr[i++] = k;
            return arr;
        }
    }

    /// <summary>True if the descriptor includes a hat switch (D-pad).</summary>
    public bool HasHat => GetLayout()?.HatSwitch != null;

    /// <summary>The descriptor's Hat Switch LogicalMin, or null if the profile
    /// has no hat usage. Use with <see cref="HMGamepadState.HatRaw"/> when
    /// you need bit-exact descriptor values.</summary>
    public int? HatLogicalMin => GetLayout()?.HatSwitch?.LogicalMin;

    /// <summary>The descriptor's Hat Switch LogicalMax, or null if the profile
    /// has no hat usage. Together with <see cref="HatLogicalMin"/>, the count
    /// of distinct hat positions is <c>HatLogicalMax - HatLogicalMin + 1</c>
    /// (typically 8 for octant hats, 16 for 22.5° hats, more for HOTAS).</summary>
    public int? HatLogicalMax => GetLayout()?.HatSwitch?.LogicalMax;

    /// <summary>Bit size of each stick axis (typically 8 or 16).</summary>
    public int StickBits => GetLayout()?.LeftStickX?.BitSize ?? 0;

    /// <summary>Bit size of each trigger axis (typically 8 or 10).</summary>
    public int TriggerBits => GetLayout()?.LeftTrigger?.BitSize ?? 0;

    /// <summary>Notes from the profile JSON (descriptor provenance, quirks, etc.).</summary>
    public string? Notes => Inner.Notes;

    /// <summary>Button remapping table. Maps HMButton bit positions (index) to
    /// descriptor button indices (value). Null means identity mapping (Xbox layout).
    /// Sony profiles remap so HMButton.A → Cross, HMButton.X → Square, etc.</summary>
    public int[]? ButtonMap => Inner.ButtonMap;

    /// <summary>Axis semantic override map. Keys are hex HID usage codes (e.g.
    /// "0x32" for Z), values are semantic names (leftStickX, rightStickY,
    /// leftTrigger, etc.). Sony profiles override Z/Rz→rightStick and
    /// Rx/Ry→triggers. Null means default heuristic mapping.</summary>
    public Dictionary<string, string>? AxisMap => Inner.AxisMap;

    /// <summary>v1.3.5 — vendor-blob input-report spec, or null. When set,
    /// HMController.SubmitState emits this report ID via the data-driven
    /// codec instead of the descriptor-based encoder. Profile-level metadata
    /// exposed for inspection by consumers and regression probes; field-level
    /// access goes through <c>Fields</c> on the spec.</summary>
    public ExtendedReportSpec? ExtendedReport => Inner.ExtendedReport;

    /// <summary>v1.3.5 — vendor-blob output-report spec, or null. When set,
    /// <see cref="HMController.OutputDecoded"/> surfaces parsed-field events
    /// for matching inbound report IDs and <see cref="HMOutputEncoder.Encode"/>
    /// can produce wire-format bytes from parsed-field dictionaries.</summary>
    public ExtendedReportSpec? ExtendedOutputReport => Inner.ExtendedOutputReport;

    /// <summary>True if the profile declares a vendor-blob input report
    /// (e.g. Sony BT Report 0x31).</summary>
    public bool HasExtendedInput => Inner.ExtendedReport != null;

    /// <summary>True if the profile declares a vendor-blob output report
    /// for parsed-field decoding.</summary>
    public bool HasExtendedOutput => Inner.ExtendedOutputReport != null;

    // ── Structured physical-design layout (v1.3.9) ──────────────────────

    /// <summary>v1.3.9 — structured physical-design declaration. When the
    /// profile JSON authors a <c>layout</c> block, this returns the typed
    /// record (<see cref="HMGamepadLayout"/>, <see cref="HMWheelLayout"/>,
    /// <see cref="HMHotasLayout"/>, etc., one per
    /// <see cref="HMLayoutKind"/>). When the JSON has no <c>layout</c>
    /// block, returns null and consumers fall back to classifier-derived
    /// views (<see cref="StickCount"/>, <see cref="TriggerCount"/>,
    /// <see cref="AvailableAxes"/>) — backward compatible with v1.3.8 and
    /// earlier.
    ///
    /// <para>Use the <c>As*</c> accessors below for typed access:
    /// <c>profile.AsWheel()</c>, <c>profile.AsHotas()</c>,
    /// <c>profile.AsJoystick()</c>, etc.</para></summary>
    public HMLayout? Layout => Inner.Layout;

    public HMGamepadLayout?             AsGamepad()              => Layout as HMGamepadLayout;
    public HMJoystickLayout?            AsJoystick()             => Layout as HMJoystickLayout;
    public HMFlightStickLayout?         AsFlightStick()          => Layout as HMFlightStickLayout;
    public HMHotasLayout?               AsHotas()                => Layout as HMHotasLayout;
    public HMWheelLayout?               AsWheel()                => Layout as HMWheelLayout;
    public HMPedalsLayout?              AsPedals()               => Layout as HMPedalsLayout;
    public HMShifterLayout?             AsShifter()              => Layout as HMShifterLayout;
    public HMHandbrakeLayout?           AsHandbrake()            => Layout as HMHandbrakeLayout;
    public HMSingleAxisAccessoryLayout? AsSingleAxisAccessory()  => Layout as HMSingleAxisAccessoryLayout;
    public HMArcadeStickLayout?         AsArcadeStick()          => Layout as HMArcadeStickLayout;
    public HMDancePadLayout?            AsDancePad()             => Layout as HMDancePadLayout;
    public HMGuitarLayout?              AsGuitar()               => Layout as HMGuitarLayout;
    public HMMotionWandLayout?          AsMotionWand()           => Layout as HMMotionWandLayout;
    public HMRemoteLayout?              AsRemote()               => Layout as HMRemoteLayout;
    public HMControllerAdapterLayout?   AsControllerAdapter()    => Layout as HMControllerAdapterLayout;

    // ── Simple-view derived lists (variable counts, the "I just want
    //    sticks and triggers" surface PadForge reads to render a per-axis
    //    binding UI). Layout-derived when authored, classifier-derived
    //    when not. ─────────────────────────────────────────────────────

    /// <summary>Every analog 2-axis stick the profile exposes, in order.
    /// Variable count: typical gamepad has 2; flight stick / wheel /
    /// HOTAS has 1; pedals-only device has 0.</summary>
    public IReadOnlyList<HMSimpleStick> Sticks => GetSimpleSticks();

    /// <summary>Every analog trigger-shaped axis the profile exposes,
    /// in order. Variable count: 3-pedal sim set has 3, gamepad has 2,
    /// handbrake has 1, stick-only device has 0. The first two entries
    /// are also written through the encoder via the descriptor's standard
    /// trigger fields; additional triggers are encoder-reachable only
    /// via <see cref="HMGamepadState.Axes"/> indexed by the entry's
    /// <c>Axis</c>.</summary>
    public IReadOnlyList<HMSimpleTrigger> Triggers => GetSimpleTriggers();

    /// <summary>Number of analog 2-axis sticks the profile exposes (alias
    /// for <c>Sticks.Count</c>).</summary>
    public int StickCount => Sticks.Count;

    /// <summary>Number of analog trigger-shaped axes the profile exposes
    /// (alias for <c>Triggers.Count</c>). Variable: 0 for stick-only
    /// devices, 1 for handbrake, 2 for gamepad, 3 for 3-pedal sim sets.</summary>
    public int TriggerCount => Triggers.Count;

    private List<HMSimpleStick> GetSimpleSticks()
    {
        var l = GetLayout();
        if (l == null) return new List<HMSimpleStick>();
        var sticks = new List<HMSimpleStick>();
        if (l.LeftStickX != null && l.LeftStickY != null)
        {
            sticks.Add(new HMSimpleStick {
                XAxis = (HMAxis)((l.LeftStickX.UsagePage << 8) | l.LeftStickX.Usage),
                YAxis = (HMAxis)((l.LeftStickY.UsagePage << 8) | l.LeftStickY.Usage),
                Label = "Left stick"
            });
        }
        if (l.RightStickX != null && l.RightStickY != null)
        {
            sticks.Add(new HMSimpleStick {
                XAxis = (HMAxis)((l.RightStickX.UsagePage << 8) | l.RightStickX.Usage),
                YAxis = (HMAxis)((l.RightStickY.UsagePage << 8) | l.RightStickY.Usage),
                Label = "Right stick"
            });
        }
        // v1.3.15 (#124): surface stick 3 and stick 4 when the descriptor
        // declares them. HidDescriptorBuilder's slot-pool allocator routes
        // AddStick 3 → Rx/Ry and AddStick 4 → Slider/Dial; the runtime
        // classifier in ResolveSemantics cascades them into ThirdStick /
        // FourthStick. Consumers (PadForge Extended 3/4-stick configs) read
        // Profile.Sticks.Count to drive a per-slot write loop, so surfacing
        // the extra entries is what makes wire-level 8-axis configurations
        // observable end-to-end.
        if (l.ThirdStickX != null && l.ThirdStickY != null)
        {
            sticks.Add(new HMSimpleStick {
                XAxis = (HMAxis)((l.ThirdStickX.UsagePage << 8) | l.ThirdStickX.Usage),
                YAxis = (HMAxis)((l.ThirdStickY.UsagePage << 8) | l.ThirdStickY.Usage),
                Label = "Third stick"
            });
        }
        if (l.FourthStickX != null && l.FourthStickY != null)
        {
            sticks.Add(new HMSimpleStick {
                XAxis = (HMAxis)((l.FourthStickX.UsagePage << 8) | l.FourthStickX.Usage),
                YAxis = (HMAxis)((l.FourthStickY.UsagePage << 8) | l.FourthStickY.Usage),
                Label = "Fourth stick"
            });
        }
        return sticks;
    }

    private List<HMSimpleTrigger> GetSimpleTriggers()
    {
        var l = GetLayout();
        if (l == null) return new List<HMSimpleTrigger>();
        var triggers = new List<HMSimpleTrigger>();
        if (l.LeftTrigger != null)
            triggers.Add(new HMSimpleTrigger {
                Axis = (HMAxis)((l.LeftTrigger.UsagePage << 8) | l.LeftTrigger.Usage),
                Role = HMAxisRole.LeftTrigger,
                Label = "Left trigger"
            });
        if (l.RightTrigger != null)
            triggers.Add(new HMSimpleTrigger {
                Axis = (HMAxis)((l.RightTrigger.UsagePage << 8) | l.RightTrigger.Usage),
                Role = HMAxisRole.RightTrigger,
                Label = "Right trigger"
            });
        return triggers;
    }

    public override string ToString() => $"{Id} ({Name})";

    // Simple-slot resolver. v1.3.13 (#23) — previously this did a bare
    // HidReportBuilder.Parse(bytes) with no axisMap and no layout
    // semantics, so GetSimpleSticks/GetSimpleTriggers saw only the raw
    // HID-usage-code heuristic. For Sony-convention controllers (Z/Rz =
    // right stick, Rx/Ry = triggers) the heuristic takes the XInput
    // branch and classifies the axes backwards; the profile JSON's
    // axisMap / layout that declare the truth were ignored. DualSense /
    // DualShock 4 Bluetooth profiles emitted right-stick and trigger
    // axes swapped as a result.
    //
    // Fix: resolve through Inner.GetOrBuildReportBuilder() — the exact
    // builder the encoder consumes. It runs the heuristic, then applies
    // the profile's axisMap overrides, then ApplyLayoutSemantics. The
    // discovery surface (Profile.Sticks/Triggers) and the encode path
    // now read identical role assignments by construction, so they can
    // never disagree. Profiles with no axisMap and no layout fall
    // through to the unchanged heuristic.
    private HidReportBuilder? _layout;
    private HidReportBuilder? GetLayout()
    {
        if (_layout != null) return _layout;
        if (Inner.GetDescriptorBytes() == null) return null;
        _layout = Inner.GetOrBuildReportBuilder();
        return _layout;
    }
}
