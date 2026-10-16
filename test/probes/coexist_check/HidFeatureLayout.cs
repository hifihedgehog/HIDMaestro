// The feature reports a HID report descriptor declares, read from the
// descriptor bytes the driver serves (HKLM\SOFTWARE\HIDMaestro\
// Controller<N>\ReportDescriptor, driver.c ReadConfigFromRegistry). The
// preparsed data Windows builds lists no constant padding, so a report's
// length comes from the descriptor itself.
using System.Collections.Generic;

internal static class HidFeatureLayout
{
    /// <summary>Each feature report id the descriptor declares, with the
    /// buffer HidD_GetFeature needs for it: the report's bytes plus the
    /// report id byte, which is 0 for a descriptor that declares none.
    /// Report Size, Report Count and Report ID are global items, so Push
    /// and Pop carry them (HID 1.11 section 6.2.2.7).</summary>
    public static SortedDictionary<byte, int> Lengths(byte[] descriptor)
    {
        var bits = new SortedDictionary<byte, int>();
        int id = 0, size = 0, count = 0;
        var stack = new Stack<(int Id, int Size, int Count)>();
        int i = 0;
        while (i < descriptor.Length)
        {
            byte prefix = descriptor[i];
            if (prefix == 0xFE)
            {
                // A long item: its data size is the next byte, then a tag
                // byte, then the data (HID 1.11 section 6.2.2.3).
                if (i + 1 >= descriptor.Length) break;
                i += 3 + descriptor[i + 1];
                continue;
            }
            int n = prefix & 3;
            if (n == 3) n = 4;
            uint value = 0;
            for (int k = 0; k < n && i + 1 + k < descriptor.Length; k++)
                value |= (uint)descriptor[i + 1 + k] << (8 * k);
            i += 1 + n;
            switch (prefix & 0xFC)
            {
                case 0x84: id = (int)value; break;            // Report ID
                case 0x74: size = (int)value; break;          // Report Size
                case 0x94: count = (int)value; break;         // Report Count
                case 0xA4: stack.Push((id, size, count)); break;
                case 0xB4: if (stack.Count > 0) (id, size, count) = stack.Pop(); break;
                case 0xB0:                                    // Feature
                    bits[(byte)id] = (bits.TryGetValue((byte)id, out int b) ? b : 0) + size * count;
                    break;
            }
        }
        var lengths = new SortedDictionary<byte, int>();
        foreach (var (rid, b) in bits) lengths[rid] = (b + 7) / 8 + 1;
        return lengths;
    }
}
