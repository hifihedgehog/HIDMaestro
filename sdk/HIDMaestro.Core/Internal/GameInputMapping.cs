using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// The GameInput mapping key Windows.Gaming.Input reads to turn a HID
/// game pad into a <c>Gamepad</c>:
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\GameInput\Devices\{VID}{PID}{UsagePage}{Usage}</c>.
/// The key is named by VID and PID alone, so a real pad with the same
/// identity reads through whatever sits there.
///
/// <para>Through v1.11.0 the SDK wrote one fixed table there for every
/// UMDF2 profile and never removed it. The table addresses the axes and
/// buttons by index, and WGI numbers them by the descriptor's structure,
/// so the table was right for one shape only: the HidDescriptorBuilder
/// game pad of two sticks, two triggers, a hat and ten or more buttons.
/// Measured on Windows 11 26200: a resting DualSense or DualShock 4 read
/// RightThumbstickY 1.0 and RightTrigger 0.5, with Cross on B and the stick
/// clicks unmapped (foundation-sunshine #1056). Windows' own mapping for a
/// pad (the <c>SOFTWARE\Microsoft\Windows\CurrentVersion\GameInput</c>
/// database) loses to the SYSTEM key, so the table also hid correct
/// mappings, and a profile with four axes stopped being a Gamepad at
/// all.</para>
///
/// <para>From v1.12.0 a catalog or JSON profile gets no mapping: it reads
/// through WGI exactly as the real device does. The table is written only
/// for a profile built in code whose descriptor has the shape it was
/// measured on, only where no other mapping exists, and only while a
/// virtual holds it (see <see cref="RegistryLedger"/>).</para>
/// </summary>
internal static class GameInputMapping
{
    internal const string SystemRoot = @"SYSTEM\CurrentControlSet\Control\GameInput\Devices";
    internal const string WindowsRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameInput\Devices";

    internal static string KeyName(ushort vid, ushort pid) => $"{vid:X4}{pid:X4}00010005";

    /// <summary>True when the profile is one the table is right for: built
    /// in code rather than loaded from JSON, with a descriptor of the
    /// measured shape.</summary>
    internal static bool Applies(ControllerProfile profile)
    {
        if (!profile.BuiltInCode) return false;
        var bytes = profile.GetDescriptorBytes();
        return bytes != null && HasMeasuredShape(bytes);
    }

    /// <summary>One application collection, Generic Desktop / Game Pad, at
    /// most one report ID, and these input value items in this order:
    /// X+Y, Z+Rz, Rx, Ry, Hat switch. Ten or more buttons, anywhere. No
    /// other input field. That is what
    /// <c>Gamepad().AddStick("Left").AddStick("Right").AddTrigger("Left").AddTrigger("Right").AddHat().AddButtons(n)</c>
    /// emits, and the order WGI numbered its axes in was X, Y, Z, Rz, Rx,
    /// Ry: indices 0 to 5 of the table. A descriptor that lists the same
    /// usages in one item (DualSense: X, Y, Z, Rz then Rx, Ry) came out as
    /// X, Y, Z, Rx, Ry, Rz instead, so no other shape qualifies.</summary>
    internal static bool HasMeasuredShape(byte[] d)
    {
        var expected = new[]
        {
            new uint[] { 0x30, 0x31 },
            new uint[] { 0x32, 0x35 },
            new uint[] { 0x33 },
            new uint[] { 0x34 },
            new uint[] { 0x39 },
        };
        var values = new List<uint[]>();
        var reportIds = new HashSet<uint>();
        var usages = new List<uint>();
        uint usagePage = 0, usageMin = 0, reportCount = 0;
        bool haveMin = false;
        int applications = 0, buttons = 0;
        bool tlcIsGamePad = false;

        int i = 0;
        while (i < d.Length)
        {
            byte prefix = d[i];
            if (prefix == 0xFE) return false;   // long items: not a builder descriptor
            int size = prefix & 0x03;
            if (size == 3) size = 4;
            if (i + 1 + size > d.Length) return false;
            uint data = 0;
            for (int b = 0; b < size; b++) data |= (uint)d[i + 1 + b] << (8 * b);
            int type = (prefix >> 2) & 0x03;
            int tag = prefix >> 4;
            i += 1 + size;

            switch (type)
            {
                case 0:   // Main
                    if (tag == 0x0A)   // Collection
                    {
                        if (data == 0x01)
                        {
                            applications++;
                            if (applications == 1)
                                tlcIsGamePad = usagePage == 0x01 && usages.Count > 0 && usages[0] == 0x05;
                        }
                    }
                    else if (tag == 0x08)   // Input
                    {
                        bool constant = (data & 0x01) != 0;
                        if (!constant)
                        {
                            if (usagePage == 0x09)
                                buttons += (int)reportCount;
                            else if (usagePage == 0x01 && usages.Count > 0)
                                values.Add(usages.ToArray());
                            else
                                return false;
                        }
                    }
                    usages.Clear();
                    haveMin = false;
                    break;
                case 1:   // Global
                    if (tag == 0x00) usagePage = data;
                    else if (tag == 0x08) reportIds.Add(data);
                    else if (tag == 0x09) reportCount = data;
                    break;
                case 2:   // Local
                    if (tag == 0x00) usages.Add(size == 4 ? data & 0xFFFF : data);
                    else if (tag == 0x01) { usageMin = data; haveMin = true; }
                    else if (tag == 0x02 && haveMin)
                        for (uint u = usageMin; u <= data && u - usageMin < 256; u++) usages.Add(u);
                    break;
            }
        }

        if (applications != 1 || !tlcIsGamePad || reportIds.Count > 1 || buttons < 10) return false;
        if (values.Count != expected.Length) return false;
        for (int k = 0; k < expected.Length; k++)
            if (!values[k].SequenceEqual(expected[k])) return false;
        return true;
    }

    /// <summary>Windows ships its own mapping for this identity.</summary>
    internal static bool WindowsHasMapping(ushort vid, ushort pid)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{WindowsRoot}\{KeyName(vid, pid)}");
            return k != null;
        }
        catch { return false; }
    }

    // ── The table ──────────────────────────────────────────────────────

    /// <summary>The table, as the canonical dump <see cref="Dump"/> makes,
    /// for separate triggers (indices 4 and 5) or the combined-trigger
    /// variant the Xbox 360 profiles got through v1.11.0 (5 and 6).</summary>
    internal static string TableDump(bool combinedTriggers)
    {
        var sb = new StringBuilder();
        // Subkeys sort by name, the order Dump walks them in.
        var entries = new SortedDictionary<string, (string Name, RegistryValueKind Kind, string Data)[]>(StringComparer.OrdinalIgnoreCase);
        void Axis(string n, int index, bool invert = false) =>
            entries[n] = invert
                ? new[] { ("AxisIndex", RegistryValueKind.DWord, index.ToString()), ("Invert", RegistryValueKind.DWord, "1") }
                : new[] { ("AxisIndex", RegistryValueKind.DWord, index.ToString()) };
        void Button(string n, int index) =>
            entries[n] = new[] { ("ButtonIndex", RegistryValueKind.DWord, index.ToString()) };
        void DPad(string n, string position) =>
            entries[n] = new[]
            {
                ("IncludeAdjacent", RegistryValueKind.DWord, "1"),
                ("SwitchIndex", RegistryValueKind.DWord, "0"),
                ("SwitchPosition", RegistryValueKind.String, position),
            };
        Axis("LeftThumbstickX", 0); Axis("LeftThumbstickY", 1, invert: true);
        Axis("RightThumbstickX", 2); Axis("RightThumbstickY", 3, invert: true);
        Axis("LeftTrigger", combinedTriggers ? 5 : 4); Axis("RightTrigger", combinedTriggers ? 6 : 5);
        Button("A", 0); Button("B", 1); Button("X", 2); Button("Y", 3);
        Button("LeftShoulder", 4); Button("RightShoulder", 5);
        Button("View", 6); Button("Menu", 7);
        Button("LeftThumbstickButton", 8); Button("RightThumbstickButton", 9);
        DPad("DPadUp", "Up"); DPad("DPadDown", "Down"); DPad("DPadLeft", "Left"); DPad("DPadRight", "Right");

        sb.Append("[Gamepad]\n");
        foreach (var (name, values) in entries)
        {
            sb.Append("  [").Append(name).Append("]\n");
            foreach (var v in values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                sb.Append("    ").Append(v.Name).Append(" (").Append(v.Kind).Append(") = ").Append(v.Data).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Write the separate-trigger table under the identity's key.
    /// The caller has established that the key is free.</summary>
    internal static void WriteTable(ushort vid, ushort pid)
    {
        string path = $@"{SystemRoot}\{KeyName(vid, pid)}";
        using var gp = Registry.LocalMachine.CreateSubKey($@"{path}\Gamepad");
        void Axis(string n, int index, bool invert = false)
        {
            using var k = gp.CreateSubKey(n);
            k.SetValue("AxisIndex", index, RegistryValueKind.DWord);
            if (invert) k.SetValue("Invert", 1, RegistryValueKind.DWord);
        }
        void Button(string n, int index)
        {
            using var k = gp.CreateSubKey(n);
            k.SetValue("ButtonIndex", index, RegistryValueKind.DWord);
        }
        void DPad(string n, string position)
        {
            using var k = gp.CreateSubKey(n);
            k.SetValue("SwitchIndex", 0, RegistryValueKind.DWord);
            k.SetValue("SwitchPosition", position, RegistryValueKind.String);
            k.SetValue("IncludeAdjacent", 1, RegistryValueKind.DWord);
        }
        Axis("LeftThumbstickX", 0); Axis("LeftThumbstickY", 1, invert: true);
        Axis("RightThumbstickX", 2); Axis("RightThumbstickY", 3, invert: true);
        Axis("LeftTrigger", 4); Axis("RightTrigger", 5);
        Button("A", 0); Button("B", 1); Button("X", 2); Button("Y", 3);
        Button("LeftShoulder", 4); Button("RightShoulder", 5);
        Button("View", 6); Button("Menu", 7);
        Button("LeftThumbstickButton", 8); Button("RightThumbstickButton", 9);
        DPad("DPadUp", "Up"); DPad("DPadDown", "Down"); DPad("DPadLeft", "Left"); DPad("DPadRight", "Right");
    }

    /// <summary>A key tree as text: every value's name, kind and data, every
    /// subkey, sorted. Null when the key does not exist.</summary>
    internal static string? Dump(string systemRelativePath)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(systemRelativePath);
            if (k == null) return null;
            var sb = new StringBuilder();
            DumpInto(k, "", sb);
            return sb.ToString();
        }
        catch { return null; }
    }

    private static void DumpInto(RegistryKey k, string indent, StringBuilder sb)
    {
        foreach (var name in k.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var kind = k.GetValueKind(name);
            object? v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            string data = v switch
            {
                byte[] b => Convert.ToHexString(b),
                string[] a => string.Join("|", a),
                null => "",
                _ => v.ToString() ?? "",
            };
            sb.Append(indent).Append(name).Append(" (").Append(kind).Append(") = ").Append(data).Append('\n');
        }
        foreach (var sub in k.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(indent).Append('[').Append(sub).Append("]\n");
            using var sk = k.OpenSubKey(sub);
            if (sk != null) DumpInto(sk, indent + "  ", sb);
        }
    }

    /// <summary>The key holds exactly a table HIDMaestro wrote through
    /// v1.11.0 (either trigger variant), and nothing else.</summary>
    internal static bool IsLegacyTable(string keyName)
    {
        string? dump = Dump($@"{SystemRoot}\{keyName}");
        return dump != null && (dump == TableDump(false) || dump == TableDump(true));
    }

    /// <summary>Delete every key that holds exactly the table HIDMaestro
    /// wrote through v1.11.0 and that the ledger does not hold for a live
    /// virtual. Those keys were never removed, and they misread every real
    /// pad of the same identity that is not shaped like the table. Any key
    /// whose content differs from the table is someone else's and stays.
    /// Returns the number removed.</summary>
    internal static int RemoveLegacyTables(ISet<string> heldKeys)
    {
        int removed = 0;
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(SystemRoot, writable: true);
            if (root == null) return 0;
            foreach (var name in root.GetSubKeyNames())
            {
                if (heldKeys.Contains(name)) continue;
                if (!IsLegacyTable(name)) continue;
                try { root.DeleteSubKeyTree(name, throwOnMissingSubKey: false); removed++; } catch { }
            }
        }
        catch { }
        return removed;
    }
}
