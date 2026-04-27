using System;
using System.Collections.Generic;

namespace HIDMaestro;

/// <summary>
/// Fluent builder for constructing valid HID report descriptors from semantic
/// building blocks. The user never touches hex — they describe what they want
/// (sticks, buttons, hat, triggers) and the builder emits the correct HID
/// descriptor bytes.
///
/// <para>Example — a 5-button gamepad with two sticks and a hat:</para>
/// <code>
/// byte[] desc = new HidDescriptorBuilder()
///     .Gamepad()
///     .AddStick("Left", bits: 16)
///     .AddStick("Right", bits: 16)
///     .AddButtons(5)
///     .AddHat()
///     .Build();
/// </code>
///
/// <para>The output can be passed to <see cref="HMProfileBuilder.Descriptor(byte[])"/>
/// to create a fully custom virtual controller.</para>
/// </summary>
public sealed class HidDescriptorBuilder
{
    private readonly List<byte> _bytes = new();
    private bool _collectionOpen;
    private int _totalInputBits;

    /// <summary>Begin a Gamepad application collection (Usage Page 0x01, Usage 0x05).</summary>
    public HidDescriptorBuilder Gamepad()
    {
        _bytes.AddRange(new byte[] { 0x05, 0x01 });       // Usage Page (Generic Desktop)
        _bytes.AddRange(new byte[] { 0x09, 0x05 });       // Usage (Game Pad)
        _bytes.AddRange(new byte[] { 0xA1, 0x01 });       // Collection (Application)
        _collectionOpen = true;
        return this;
    }

    /// <summary>Begin a Joystick application collection (Usage Page 0x01, Usage 0x04).</summary>
    public HidDescriptorBuilder Joystick()
    {
        _bytes.AddRange(new byte[] { 0x05, 0x01 });       // Usage Page (Generic Desktop)
        _bytes.AddRange(new byte[] { 0x09, 0x04 });       // Usage (Joystick)
        _bytes.AddRange(new byte[] { 0xA1, 0x01 });       // Collection (Application)
        _collectionOpen = true;
        return this;
    }

    /// <summary>Add a 2-axis stick (X+Y or Rx+Ry) inside a Physical collection.</summary>
    /// <param name="name">"Left" maps to X/Y (usages 0x30/0x31), "Right" maps to Rx/Ry (0x33/0x34).</param>
    /// <param name="bits">Axis resolution: 8 for [0..255], 16 for [0..65535].</param>
    public HidDescriptorBuilder AddStick(string name, int bits = 16)
    {
        byte usage1, usage2;
        if (name.Equals("Left", StringComparison.OrdinalIgnoreCase)
            || name.Equals("L", StringComparison.OrdinalIgnoreCase))
        {
            usage1 = 0x30; usage2 = 0x31; // X, Y
        }
        else
        {
            usage1 = 0x33; usage2 = 0x34; // Rx, Ry
        }

        int logMax = (1 << bits) - 1;

        _bytes.AddRange(new byte[] { 0xA1, 0x00 });       // Collection (Physical)
        _bytes.AddRange(new byte[] { 0x09, usage1 });      // Usage (X or Rx)
        _bytes.AddRange(new byte[] { 0x09, usage2 });      // Usage (Y or Ry)
        _bytes.AddRange(new byte[] { 0x15, 0x00 });        // Logical Minimum (0)
        if (bits <= 8)
        {
            _bytes.AddRange(new byte[] { 0x26, (byte)(logMax & 0xFF), (byte)(logMax >> 8) });
        }
        else
        {
            _bytes.AddRange(new byte[] { 0x27, (byte)(logMax & 0xFF), (byte)((logMax >> 8) & 0xFF),
                                                (byte)((logMax >> 16) & 0xFF), (byte)(logMax >> 24) });
        }
        _bytes.AddRange(new byte[] { 0x95, 0x02 });        // Report Count (2)
        _bytes.AddRange(new byte[] { 0x75, (byte)bits });   // Report Size (bits)
        _bytes.AddRange(new byte[] { 0x81, 0x02 });        // Input (Data,Var,Abs)
        _bytes.Add(0xC0);                                   // End Collection

        _totalInputBits += bits * 2;
        return this;
    }

    /// <summary>Add a single trigger axis.</summary>
    /// <param name="name">"Left" maps to Z (0x32), "Right" maps to Rz (0x35).</param>
    /// <param name="bits">Axis resolution: 8 for [0..255], 16 for [0..65535].
    /// Must be a multiple of 8 to keep the report byte-aligned. 10-bit or
    /// other non-aligned sizes would force a Const pad item that Chromium's
    /// RawInput parser surfaces as a phantom axis (see issue #6).</param>
    public HidDescriptorBuilder AddTrigger(string name, int bits = 8)
    {
        if (bits % 8 != 0)
            throw new ArgumentException(
                $"AddTrigger bits must be a multiple of 8 (got {bits}). " +
                "Non-aligned sizes introduce phantom axes in Chromium's Gamepad API.",
                nameof(bits));

        byte usage = name.Equals("Left", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("L", StringComparison.OrdinalIgnoreCase)
            ? (byte)0x32 : (byte)0x35;

        int logMax = (1 << bits) - 1;

        _bytes.AddRange(new byte[] { 0x05, 0x01 });        // Usage Page (Generic Desktop)
        _bytes.AddRange(new byte[] { 0x09, usage });        // Usage (Z or Rz)
        _bytes.AddRange(new byte[] { 0x15, 0x00 });        // Logical Minimum (0)
        if (bits <= 8)
            _bytes.AddRange(new byte[] { 0x26, (byte)(logMax & 0xFF), (byte)(logMax >> 8) });
        else
            _bytes.AddRange(new byte[] { 0x27, (byte)(logMax & 0xFF), (byte)((logMax >> 8) & 0xFF),
                                                (byte)((logMax >> 16) & 0xFF), (byte)(logMax >> 24) });
        _bytes.AddRange(new byte[] { 0x95, 0x01 });        // Report Count (1)
        _bytes.AddRange(new byte[] { 0x75, (byte)bits });   // Report Size (bits)
        _bytes.AddRange(new byte[] { 0x81, 0x02 });        // Input (Data,Var,Abs)

        _totalInputBits += bits;

        return this;
    }

    /// <summary>Add N buttons (Button Page, Usage 1..N, 1 bit each). The
    /// declared Report Count is rounded UP to the next multiple of 8 (with
    /// extra Usage Max bump so the round-up bits are "dummy" buttons the
    /// caller never sets). No Const pad item follows — Chromium's RawInput
    /// parser surfaces any trailing Const Input item as a phantom axis,
    /// even on the Vendor-Defined Usage Page. Absorbing the pad as
    /// additional buttons keeps the report byte-aligned without introducing
    /// a Const item. See issue #6 — the round-up approach eliminates the
    /// "AXIS 9 = 1227133568" phantom seen in Chrome's Gamepad API.</summary>
    public HidDescriptorBuilder AddButtons(int count)
    {
        // Round the total bits after this block up to the next byte boundary
        // by declaring extra "dummy" buttons. User still wires `count` real
        // buttons; the extras stay zero.
        int bitsBefore = _totalInputBits;
        int declaredCount = count;
        int total = bitsBefore + count;
        int pad = (8 - (total % 8)) % 8;
        declaredCount += pad;

        _bytes.AddRange(new byte[] { 0x05, 0x09 });        // Usage Page (Button)
        _bytes.AddRange(new byte[] { 0x19, 0x01 });        // Usage Minimum (1)
        _bytes.AddRange(new byte[] { 0x29, (byte)declaredCount }); // Usage Maximum (declaredCount)
        _bytes.AddRange(new byte[] { 0x15, 0x00 });        // Logical Minimum (0)
        _bytes.AddRange(new byte[] { 0x25, 0x01 });        // Logical Maximum (1)
        _bytes.AddRange(new byte[] { 0x95, (byte)declaredCount }); // Report Count
        _bytes.AddRange(new byte[] { 0x75, 0x01 });        // Report Size (1)
        _bytes.AddRange(new byte[] { 0x81, 0x02 });        // Input (Data,Var,Abs)

        _totalInputBits += declaredCount;

        return this;
    }

    /// <summary>Add an 8-bit hat switch (D-pad). Uses Report Size 8 instead
    /// of 4 so the hat absorbs its own byte rather than requiring a Const
    /// pad item after it. Hat values 1-8 encode the 8 directions; 0 and
    /// 9-255 are null (via the Null-state flag), identical to a 4-bit hat
    /// with Logical Max 8 semantically but byte-aligned on the wire. No
    /// following Const pad item — see AddButtons docs for rationale.</summary>
    public HidDescriptorBuilder AddHat()
    {
        _bytes.AddRange(new byte[] { 0x05, 0x01 });        // Usage Page (Generic Desktop)
        _bytes.AddRange(new byte[] { 0x09, 0x39 });        // Usage (Hat switch)
        _bytes.AddRange(new byte[] { 0x15, 0x00 });        // Logical Minimum (0)
        _bytes.AddRange(new byte[] { 0x25, 0x08 });        // Logical Maximum (8)
        _bytes.AddRange(new byte[] { 0x35, 0x00 });        // Physical Minimum (0)
        _bytes.AddRange(new byte[] { 0x46, 0x3B, 0x01 });  // Physical Maximum (315)
        _bytes.AddRange(new byte[] { 0x66, 0x14, 0x00 });  // Unit (Degrees)
        _bytes.AddRange(new byte[] { 0x75, 0x08 });        // Report Size (8) — byte-aligned
        _bytes.AddRange(new byte[] { 0x95, 0x01 });        // Report Count (1)
        _bytes.AddRange(new byte[] { 0x81, 0x42 });        // Input (Data,Var,Abs,Null)

        _totalInputBits += 8;

        // Reset physical max and unit so they don't bleed into subsequent items.
        _bytes.AddRange(new byte[] { 0x45, 0x00 });        // Physical Maximum (0)
        _bytes.AddRange(new byte[] { 0x65, 0x00 });        // Unit (None)

        return this;
    }

    /// <summary>Add raw descriptor bytes. For advanced use — appends arbitrary
    /// HID descriptor items without validation.</summary>
    public HidDescriptorBuilder AddRaw(byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    /// <summary>Append the HID PID 1.0 force-feedback report block to the
    /// descriptor. Emits the full Output-report set the DirectInput PID
    /// mapper (<c>pid.dll</c>) drives — Set Effect (0x11), Set Envelope
    /// (0x12), Set Condition (0x13), Set Periodic (0x14), Set Constant
    /// Force (0x15), Set Ramp Force (0x16), Custom Force Data (0x17),
    /// Download Force Sample (0x18), Effect Operation (0x1A), Block Free
    /// (0x1B), Device Control (0x1C), Device Gain (0x1D), Set Custom
    /// Force (0x1E) — plus the single Feature report Create New Effect
    /// (0x11) used for effect allocation.
    ///
    /// <para><b>Why only one Feature report?</b> The vJoy / vJoy-Brunner
    /// reference descriptor (<c>hidReportDescFfb.h</c>) declares four
    /// sibling Feature reports inside the Joystick TLC: Create New
    /// Effect (0x11), Block Load (0x12), PID Pool (0x13), PID State
    /// (0x14). On Windows 11 Build 26100 with HIDMaestro's UMDF2
    /// shared-section transport, that four-feature variant causes a
    /// <c>0xC0000005</c> AV inside <c>pid!PID_EffectOperation+0x52</c>
    /// the first time the consumer calls <c>CreateEffect</c> via
    /// DirectInput8 / SharpDX (issue #16). The crash reproduces with
    /// the exact bytes vJoy ships and is independent of the SDK side
    /// — it's a pid.dll preparsed-data interaction we can't fix from
    /// user mode. The block emitted here drops 0x12, 0x13, 0x14 from
    /// the Feature side and serves them via shared-section
    /// <c>HidD_GetFeature</c> handling in the driver instead, which
    /// is the only configuration that does not AV.</para>
    ///
    /// <para><b>Don't add additional Feature reports inside the same
    /// Application Collection.</b> If you need extra metadata
    /// reachable via <c>HidD_GetFeature</c>, expose it through
    /// <see cref="HMController.PublishPidPool"/>,
    /// <see cref="HMController.PublishPidBlockLoad"/>, or
    /// <see cref="HMController.PublishPidState"/> — those are served
    /// by the driver from a separate shared-section path that doesn't
    /// touch pid.dll's preparsed-data parser.</para>
    ///
    /// <para>This block expects a Joystick or Gamepad TLC already
    /// opened by <see cref="Joystick"/> / <see cref="Gamepad"/>. Call
    /// after the input report (sticks, buttons, hat) is fully declared.</para>
    /// </summary>
    public HidDescriptorBuilder AddPidFfbBlock()
    {
        _bytes.AddRange(MinimumViablePidFfbBlock);
        return this;
    }

    /// <summary>The exact descriptor bytes <see cref="AddPidFfbBlock"/>
    /// appends. Exposed for probe and test code that needs to verify
    /// the canonical block byte-for-byte; consumers should call the
    /// fluent <see cref="AddPidFfbBlock"/> method instead.</summary>
    public static byte[] MinimumViablePidFfbBlock { get; } = BuildMinimumViablePidFfbBlock();

    private static byte[] BuildMinimumViablePidFfbBlock()
    {
        var d = new List<byte>(640);
        d.AddRange(new byte[] { 0x05, 0x0F });                 // Usage Page Physical Interface

        // Set Effect Report (Output, ID 0x11)
        d.AddRange(new byte[] { 0x09, 0x21, 0xA1, 0x02, 0x85, 0x11 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x25, 0xA1, 0x02 });
        d.AddRange(new byte[] {
            0x09, 0x26, 0x09, 0x27, 0x09, 0x30, 0x09, 0x31, 0x09, 0x32,
            0x09, 0x33, 0x09, 0x34, 0x09, 0x40, 0x09, 0x41, 0x09, 0x42,
            0x09, 0x43, 0x09, 0x29
        });
        d.AddRange(new byte[] { 0x25, 0x0C, 0x15, 0x01, 0x35, 0x01, 0x45, 0x0C });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x00 });
        d.Add(0xC0);
        d.AddRange(new byte[] { 0x09, 0x50, 0x09, 0x54, 0x09, 0x51, 0x09, 0xA7 });
        d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x7F });
        d.AddRange(new byte[] { 0x66, 0x03, 0x10, 0x55, 0xFD });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x04, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x55, 0x00, 0x66, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x09, 0x52 });
        d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x53 });
        d.AddRange(new byte[] { 0x15, 0x01, 0x25, 0x08, 0x35, 0x01, 0x45, 0x08 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x55, 0xA1, 0x02 });
        d.AddRange(new byte[] { 0x05, 0x01 });
        d.AddRange(new byte[] { 0x09, 0x30, 0x09, 0x31 });
        d.AddRange(new byte[] { 0x15, 0x00, 0x25, 0x01 });
        d.AddRange(new byte[] { 0x75, 0x01, 0x95, 0x02, 0x91, 0x02 });
        d.Add(0xC0);
        d.AddRange(new byte[] { 0x05, 0x0F });
        d.AddRange(new byte[] { 0x09, 0x56, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x95, 0x05, 0x91, 0x03 });
        d.AddRange(new byte[] { 0x09, 0x57, 0xA1, 0x02 });
        d.AddRange(new byte[] { 0x0B, 0x01, 0x00, 0x0A, 0x00 });
        d.AddRange(new byte[] { 0x0B, 0x02, 0x00, 0x0A, 0x00 });
        d.AddRange(new byte[] { 0x66, 0x14, 0x00, 0x55, 0xFE });
        d.AddRange(new byte[] { 0x15, 0x00, 0x27, 0xFF, 0x7F, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x35, 0x00, 0x47, 0xA0, 0x8C, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x66, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x55, 0x00, 0x66, 0x00, 0x00 });
        d.Add(0xC0);
        d.AddRange(new byte[] { 0x05, 0x0F, 0x09, 0x58, 0xA1, 0x02 });
        d.AddRange(new byte[] { 0x0B, 0x01, 0x00, 0x0A, 0x00 });
        d.AddRange(new byte[] { 0x0B, 0x02, 0x00, 0x0A, 0x00 });
        d.AddRange(new byte[] { 0x26, 0xFD, 0x7F, 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
        d.Add(0xC0);
        d.Add(0xC0);

        // Set Envelope (Output, ID 0x12)
        d.AddRange(new byte[] { 0x09, 0x5A, 0xA1, 0x02, 0x85, 0x12 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x5B, 0x09, 0x5D });
        d.AddRange(new byte[] { 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x5C, 0x09, 0x5E });
        d.AddRange(new byte[] { 0x66, 0x03, 0x10, 0x55, 0xFD });
        d.AddRange(new byte[] { 0x27, 0xFF, 0x7F, 0x00, 0x00, 0x47, 0xFF, 0x7F, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x20, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x45, 0x00, 0x66, 0x00, 0x00, 0x55, 0x00 });
        d.Add(0xC0);

        // Set Condition (Output, ID 0x13)
        d.AddRange(new byte[] { 0x09, 0x5F, 0xA1, 0x02, 0x85, 0x13 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x23, 0x15, 0x00, 0x25, 0x03, 0x35, 0x00, 0x45, 0x03 });
        d.AddRange(new byte[] { 0x75, 0x04, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x58, 0xA1, 0x02 });
        d.AddRange(new byte[] { 0x0B, 0x01, 0x00, 0x0A, 0x00, 0x0B, 0x02, 0x00, 0x0A, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x02, 0x95, 0x02, 0x91, 0x02 });
        d.Add(0xC0);
        d.AddRange(new byte[] { 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x09, 0x60, 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x09, 0x61, 0x09, 0x62, 0x95, 0x02, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x09, 0x63, 0x09, 0x64, 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x65 });
        d.AddRange(new byte[] { 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x95, 0x01, 0x91, 0x02 });
        d.Add(0xC0);

        // Set Periodic (Output, ID 0x14)
        d.AddRange(new byte[] { 0x09, 0x6E, 0xA1, 0x02, 0x85, 0x14 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x70, 0x16, 0x00, 0x00, 0x26, 0x10, 0x27, 0x36, 0x00, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x6F, 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x95, 0x01, 0x75, 0x10, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x71, 0x66, 0x14, 0x00, 0x55, 0xFE });
        d.AddRange(new byte[] { 0x15, 0x00, 0x27, 0x9F, 0x8C, 0x00, 0x00, 0x35, 0x00, 0x47, 0x9F, 0x8C, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x72, 0x15, 0x00, 0x27, 0xFF, 0x7F, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x35, 0x00, 0x47, 0xFF, 0x7F, 0x00, 0x00 });
        d.AddRange(new byte[] { 0x66, 0x03, 0x10, 0x55, 0xFD });
        d.AddRange(new byte[] { 0x75, 0x20, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x66, 0x00, 0x00, 0x55, 0x00 });
        d.Add(0xC0);

        // Set Constant Force (Output, ID 0x15)
        d.AddRange(new byte[] { 0x09, 0x73, 0xA1, 0x02, 0x85, 0x15 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x70 });
        d.AddRange(new byte[] { 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
        d.Add(0xC0);

        // Set Ramp Force (Output, ID 0x16)
        d.AddRange(new byte[] { 0x09, 0x74, 0xA1, 0x02, 0x85, 0x16 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x75, 0x09, 0x76 });
        d.AddRange(new byte[] { 0x16, 0xF0, 0xD8, 0x26, 0x10, 0x27, 0x36, 0xF0, 0xD8, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x02, 0x91, 0x02 });
        d.Add(0xC0);

        // Custom Force Data (Output, ID 0x17)
        d.AddRange(new byte[] { 0x09, 0x68, 0xA1, 0x02, 0x85, 0x17 });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x6C, 0x15, 0x00, 0x26, 0x10, 0x27, 0x35, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x69, 0x15, 0x81, 0x25, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x0C, 0x92, 0x02, 0x01 });
        d.Add(0xC0);

        // Download Force Sample (Output, ID 0x18)
        d.AddRange(new byte[] { 0x09, 0x66, 0xA1, 0x02, 0x85, 0x18 });
        d.AddRange(new byte[] { 0x05, 0x01, 0x09, 0x30, 0x09, 0x31 });
        d.AddRange(new byte[] { 0x15, 0x81, 0x25, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x02, 0x91, 0x02 });
        d.Add(0xC0);

        // Effect Operation (Output, ID 0x1A)
        d.AddRange(new byte[] { 0x05, 0x0F });
        d.AddRange(new byte[] { 0x09, 0x77, 0xA1, 0x02, 0x85, 0x1A });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x78, 0xA1, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x79, 0x09, 0x7A, 0x09, 0x7B });
        d.AddRange(new byte[] { 0x15, 0x01, 0x25, 0x03, 0x75, 0x08, 0x95, 0x01, 0x91, 0x00 });
        d.Add(0xC0);
        d.AddRange(new byte[] { 0x09, 0x7C });
        d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0xFF, 0x00 });
        d.AddRange(new byte[] { 0x91, 0x02 });
        d.Add(0xC0);

        // PID Block Free (Output, ID 0x1B)
        d.AddRange(new byte[] { 0x09, 0x90, 0xA1, 0x02, 0x85, 0x1B });
        d.AddRange(new byte[] { 0x09, 0x22, 0x25, 0x28, 0x15, 0x01, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.Add(0xC0);

        // PID Device Control (Output, ID 0x1C)
        d.AddRange(new byte[] { 0x09, 0x96, 0xA1, 0x02, 0x85, 0x1C });
        d.AddRange(new byte[] { 0x09, 0x97, 0x09, 0x98, 0x09, 0x99, 0x09, 0x9A, 0x09, 0x9B, 0x09, 0x9C });
        d.AddRange(new byte[] { 0x15, 0x01, 0x25, 0x06, 0x75, 0x08, 0x95, 0x01, 0x91, 0x00 });
        d.Add(0xC0);

        // Device Gain (Output, ID 0x1D)
        d.AddRange(new byte[] { 0x09, 0x7D, 0xA1, 0x02, 0x85, 0x1D });
        d.AddRange(new byte[] { 0x09, 0x7E, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0x10, 0x27 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.Add(0xC0);

        // Set Custom Force (Output, ID 0x1E)
        d.AddRange(new byte[] { 0x09, 0x6B, 0xA1, 0x02, 0x85, 0x1E });
        d.AddRange(new byte[] { 0x09, 0x22, 0x15, 0x01, 0x25, 0x28, 0x35, 0x01, 0x45, 0x28 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x6D, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x35, 0x00, 0x46, 0xFF, 0x00 });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x09, 0x51, 0x66, 0x03, 0x10, 0x55, 0xFD });
        d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x7F, 0x35, 0x00, 0x46, 0xFF, 0x7F });
        d.AddRange(new byte[] { 0x75, 0x10, 0x95, 0x01, 0x91, 0x02 });
        d.AddRange(new byte[] { 0x55, 0x00, 0x66, 0x00, 0x00 });
        d.Add(0xC0);

        // Create New Effect (Feature, ID 0x11) — the ONLY Feature report
        // declared inside the TLC. See AddPidFfbBlock summary for why
        // adding 0x12 / 0x13 / 0x14 Feature reports here AVs pid.dll.
        d.AddRange(new byte[] { 0x09, 0xAB, 0xA1, 0x02, 0x85, 0x11 });
        d.AddRange(new byte[] { 0x09, 0x25, 0xA1, 0x02 });
        d.AddRange(new byte[] {
            0x09, 0x26, 0x09, 0x27, 0x09, 0x30, 0x09, 0x31, 0x09, 0x32,
            0x09, 0x33, 0x09, 0x34, 0x09, 0x40, 0x09, 0x41, 0x09, 0x42,
            0x09, 0x43, 0x09, 0x29
        });
        d.AddRange(new byte[] { 0x25, 0x0C, 0x15, 0x01, 0x35, 0x01, 0x45, 0x0C });
        d.AddRange(new byte[] { 0x75, 0x08, 0x95, 0x01, 0xB1, 0x00 });
        d.Add(0xC0);
        d.AddRange(new byte[] { 0x05, 0x01, 0x09, 0x3B });
        d.AddRange(new byte[] { 0x15, 0x00, 0x26, 0xFF, 0x01, 0x35, 0x00, 0x46, 0xFF, 0x01 });
        d.AddRange(new byte[] { 0x75, 0x0A, 0x95, 0x01, 0xB1, 0x02 });
        d.AddRange(new byte[] { 0x75, 0x06, 0xB1, 0x01 });
        d.Add(0xC0);

        return d.ToArray();
    }

    /// <summary>Build the descriptor. Closes the Application Collection if open
    /// and returns the raw byte array suitable for <see cref="HMProfileBuilder.Descriptor(byte[])"/>.</summary>
    public byte[] Build()
    {
        var result = new List<byte>(_bytes);
        if (_collectionOpen)
            result.Add(0xC0); // End Collection (Application)
        return result.ToArray();
    }

    /// <summary>The total number of input bits declared so far (for computing report size).</summary>
    public int TotalInputBits => _totalInputBits;

    /// <summary>The input report size in bytes (rounded up from bits).</summary>
    public int InputReportByteSize => (_totalInputBits + 7) / 8;
}
