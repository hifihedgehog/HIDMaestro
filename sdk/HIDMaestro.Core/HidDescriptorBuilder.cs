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
    /// <param name="bits">Axis resolution: 8 for [0..255], 10 for [0..1023].</param>
    public HidDescriptorBuilder AddTrigger(string name, int bits = 8)
    {
        byte usage = name.Equals("Left", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("L", StringComparison.OrdinalIgnoreCase)
            ? (byte)0x32 : (byte)0x35;

        int logMax = (1 << bits) - 1;

        _bytes.AddRange(new byte[] { 0x05, 0x01 });        // Usage Page (Generic Desktop)
        _bytes.AddRange(new byte[] { 0x09, usage });        // Usage (Z or Rz)
        _bytes.AddRange(new byte[] { 0x15, 0x00 });        // Logical Minimum (0)
        _bytes.AddRange(new byte[] { 0x26, (byte)(logMax & 0xFF), (byte)(logMax >> 8) });
        _bytes.AddRange(new byte[] { 0x95, 0x01 });        // Report Count (1)
        _bytes.AddRange(new byte[] { 0x75, (byte)bits });   // Report Size (bits)
        _bytes.AddRange(new byte[] { 0x81, 0x02 });        // Input (Data,Var,Abs)

        _totalInputBits += bits;

        // Pad to byte boundary if not aligned — on a vendor-defined page so
        // browsers/OS parsers don't surface the pad as a phantom axis. See
        // issue #6 for the symptom (Const Generic-Desktop pad items appeared
        // as extra axes in Chrome's Gamepad API).
        EmitPadding();

        return this;
    }

    /// <summary>Add N buttons (Button Page, Usage 1..N, 1 bit each, auto-padded to byte boundary).</summary>
    public HidDescriptorBuilder AddButtons(int count)
    {
        _bytes.AddRange(new byte[] { 0x05, 0x09 });        // Usage Page (Button)
        _bytes.AddRange(new byte[] { 0x19, 0x01 });        // Usage Minimum (1)
        _bytes.AddRange(new byte[] { 0x29, (byte)count });  // Usage Maximum (count)
        _bytes.AddRange(new byte[] { 0x15, 0x00 });        // Logical Minimum (0) — explicit
        _bytes.AddRange(new byte[] { 0x25, 0x01 });        // Logical Maximum (1) — explicit
        _bytes.AddRange(new byte[] { 0x95, (byte)count });  // Report Count (count)
        _bytes.AddRange(new byte[] { 0x75, 0x01 });        // Report Size (1)
        _bytes.AddRange(new byte[] { 0x81, 0x02 });        // Input (Data,Var,Abs)

        _totalInputBits += count;

        EmitPadding();

        return this;
    }

    /// <summary>Add a 4-bit hat switch (D-pad), auto-padded to byte boundary.</summary>
    public HidDescriptorBuilder AddHat()
    {
        _bytes.AddRange(new byte[] { 0x05, 0x01 });        // Usage Page (Generic Desktop)
        _bytes.AddRange(new byte[] { 0x09, 0x39 });        // Usage (Hat switch)
        _bytes.AddRange(new byte[] { 0x15, 0x01 });        // Logical Minimum (1)
        _bytes.AddRange(new byte[] { 0x25, 0x08 });        // Logical Maximum (8)
        _bytes.AddRange(new byte[] { 0x35, 0x00 });        // Physical Minimum (0)
        _bytes.AddRange(new byte[] { 0x46, 0x3B, 0x01 });  // Physical Maximum (315)
        _bytes.AddRange(new byte[] { 0x66, 0x14, 0x00 });  // Unit (Degrees)
        _bytes.AddRange(new byte[] { 0x75, 0x04 });        // Report Size (4)
        _bytes.AddRange(new byte[] { 0x95, 0x01 });        // Report Count (1)
        _bytes.AddRange(new byte[] { 0x81, 0x42 });        // Input (Data,Var,Abs,Null)

        _totalInputBits += 4;

        // Reset physical max and unit so they don't bleed into subsequent items.
        _bytes.AddRange(new byte[] { 0x45, 0x00 });        // Physical Maximum (0)
        _bytes.AddRange(new byte[] { 0x65, 0x00 });        // Unit (None)

        EmitPadding();

        return this;
    }

    /// <summary>Emit a Const Input item that fills up to the next byte boundary,
    /// on Usage Page 0xFF00 (vendor-defined) with no local usage. Vendor-defined
    /// pages are universally understood by HID parsers as non-gameplay data, so
    /// the pad does not surface as a phantom axis or button in browsers or
    /// games. Resets Logical Min/Max afterward so the next AddX call starts
    /// from a known global state. No-op if the descriptor is already byte
    /// aligned.</summary>
    private void EmitPadding()
    {
        int pad = (8 - (_totalInputBits % 8)) % 8;
        if (pad == 0) return;
        _bytes.AddRange(new byte[] { 0x06, 0x00, 0xFF }); // Usage Page (Vendor-Defined 0xFF00)
        _bytes.AddRange(new byte[] { 0x15, 0x00 });        // Logical Minimum (0)
        _bytes.AddRange(new byte[] { 0x25, 0x00 });        // Logical Maximum (0)
        _bytes.AddRange(new byte[] { 0x75, (byte)pad });   // Report Size (pad bits)
        _bytes.AddRange(new byte[] { 0x95, 0x01 });        // Report Count (1)
        _bytes.AddRange(new byte[] { 0x81, 0x03 });        // Input (Const,Var,Abs)
        _totalInputBits += pad;
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
