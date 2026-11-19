// R8: the HID device xusb22 makes on the USB/IP backend against the
// profile's own descriptor, the one UMDF2 presents. Each layout is written
// the same way, from the descriptor's input items and from the HID class's
// capabilities: the input report's length, the button ranges, and each
// value's usage page, usage and size. The two must be equal unless the
// profile's notes state the difference, and the table below holds each
// stated one with the layout xusb22 gives and the sentence that states it.

using System;
using System.Collections.Generic;
using System.Linq;

internal static unsafe partial class Program
{
    /// <summary>The profiles whose USB/IP HID device differs from their
    /// descriptor: the layout xusb22 makes for them, and the words of the
    /// profile's notes that state it. xusb22 lays a pad out by its subtype
    /// alone, in the layout its Parameters key selects for the whole machine
    /// (GamepadTriggerUsage, GamepadStickUsage, WheelPedalUsage, the values
    /// ControllersInfo's xusb_gamepad_hid_separate_triggers.reg sets), so no
    /// byte of the device can give these profiles their own.</summary>
    static readonly Dictionary<string, (string Layout, string Note)> R8Stated = new(StringComparer.Ordinal)
    {
        ["xbox-360-wired"] = (GamepadLayout, R8GamepadNote),
        ["xbox-360-type2"] = (GamepadLayout, R8GamepadNote),
        ["xbox-360-wireless"] = (GamepadLayout, R8GamepadNote),
        ["xbox-360-wheel-v1"] = ("in 9 buttons 09:1-10 values 01:30/16 01:32/16 01:39/4", "On the USB/IP backend xusb22 lays the wheel out as xbox-360-wheel-v2's descriptor does"),
    };

    const string GamepadLayout = "in 15 buttons 09:1-10 values 01:30/16 01:31/16 01:32/16 01:33/16 01:34/16 01:39/4";
    const string R8GamepadNote = "On the USB/IP backend xusb22 makes the HID device, with no Vx and Vy pair";

    /// <summary>The layout of a report descriptor's input items, with no
    /// report ID: the HID class counts the ID byte the report starts with
    /// in its input length either way.</summary>
    static string DescriptorLayout(byte[] d)
    {
        int page = 0, size = 0, count = 0, bits = 0;
        var usages = new List<int>();
        int min = -1, max = -1;
        var buttons = new List<string>();
        var values = new List<string>();
        for (int i = 0; i < d.Length;)
        {
            byte b = d[i];
            int n = (b & 3) == 3 ? 4 : b & 3;
            uint v = 0;
            for (int k = 0; k < n; k++) v |= (uint)d[i + 1 + k] << (8 * k);
            int tag = b & 0xFC;
            i += 1 + n;
            switch (tag)
            {
                case 0x04: page = (int)v; break;                // Usage Page
                case 0x74: size = (int)v; break;                // Report Size
                case 0x94: count = (int)v; break;               // Report Count
                case 0x08: usages.Add((int)v); break;           // Usage
                case 0x18: min = (int)v; break;                 // Usage Minimum
                case 0x28: max = (int)v; break;                 // Usage Maximum
                case 0x80:                                      // Input
                    bits += size * count;
                    if ((v & 1) == 0)
                    {
                        if ((v & 2) == 0) { }                   // an array: none here
                        else if (size == 1 && min >= 0) buttons.Add($"{page:X2}:{min}-{max}");
                        else
                            for (int k = 0; k < count; k++)
                                values.Add($"{page:X2}:{(k < usages.Count ? usages[k] : usages.LastOrDefault()):X2}/{size}");
                    }
                    usages.Clear(); min = max = -1;
                    break;
                case 0xA0: case 0xC0: case 0x90: case 0xB0: usages.Clear(); min = max = -1; break;
            }
        }
        values.Sort(StringComparer.Ordinal);
        return $"in {bits / 8 + 1} buttons {string.Join(",", buttons)} values {string.Join(" ", values)}";
    }

    /// <summary>The same layout from the HID class's capabilities of
    /// <paramref name="c"/>.</summary>
    static string CollectionLayout(Collection c)
    {
        var buttons = new List<string>();
        for (int i = 0; i + 72 <= c.InputButtons.Length; i += 72)
        {
            var b = c.InputButtons.AsSpan(i, 72);
            int page = BitConverter.ToUInt16(b);
            buttons.Add(b[12] != 0 ? $"{page:X2}:{BitConverter.ToUInt16(b[56..])}-{BitConverter.ToUInt16(b[58..])}" : $"{page:X2}:{BitConverter.ToUInt16(b[56..])}");
        }
        var values = new List<string>();
        for (int i = 0; i + 72 <= c.InputValues.Length; i += 72)
        {
            var v = c.InputValues.AsSpan(i, 72);
            values.Add($"{BitConverter.ToUInt16(v):X2}:{BitConverter.ToUInt16(v[56..]):X2}/{BitConverter.ToUInt16(v[18..])}");
        }
        values.Sort(StringComparer.Ordinal);
        return $"in {c.InputLength} buttons {string.Join(",", buttons)} values {string.Join(" ", values)}";
    }

    /// <summary>R8 for one profile: xusb22's layout on USB/IP,
    /// <paramref name="usbip"/>, against the profile's descriptor.</summary>
    static void R8(HIDMaestro.HMProfile p, string usbip)
    {
        string own = p.GetDescriptorBytes() is { } d ? DescriptorLayout(d) : "no descriptor";
        Console.WriteLine($"  [R8] the profile's descriptor: {own}");
        Console.WriteLine($"  [R8] xusb22's HID device:     {usbip}");
        if (own == usbip)
        {
            Check($"{p.Id}: R8, xusb22's HID device on USB/IP has the layout of the profile's descriptor", !R8Stated.ContainsKey(p.Id),
                  R8Stated.ContainsKey(p.Id) ? "the table states a difference that is gone" : "");
            return;
        }
        bool stated = R8Stated.TryGetValue(p.Id, out var s);
        bool inNotes = stated && (p.Notes ?? "").Contains(s.Note, StringComparison.Ordinal);
        Check($"{p.Id}: R8, xusb22's HID device on USB/IP differs from the profile's descriptor as the profile's notes state",
              stated && s.Layout == usbip && inNotes,
              !stated ? "a difference the notes do not state" : s.Layout != usbip ? $"the table's layout is {s.Layout}" : inNotes ? "" : $"the notes lack \"{s.Note}\"");
    }
}
