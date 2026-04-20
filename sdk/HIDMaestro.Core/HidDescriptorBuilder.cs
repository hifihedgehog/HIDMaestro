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
