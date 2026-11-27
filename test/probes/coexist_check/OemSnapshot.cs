// The joy.cpl OEM keys a v1.11.0 program deletes. Through v1.11.0 the SDK's
// cleanup deleted every key under the Microsoft, Sony and VID 0000 prefixes
// of HKLM's and HKCU's Joystick\OEM, a real pad's calibration and names
// among them (v1.12.0's RegistryLedger.cs, foundation-sunshine #1138).
// Sections E4 and M1 run such a program, so before each one the keys under
// those prefixes are copied, whole, and after it they are put back as they
// were: a key the copy holds is written back from it, and a key under the
// prefixes that the copy does not hold, which the section's programs made,
// goes. The copy is kept under HKLM\SOFTWARE\HIDMaestroProbeBackup, where
// the hygiene check finds a copy a stopped run left, and the next run puts
// that one back before anything else (Program.Main).
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

internal static class OemSnapshot
{
    const string OemRel = @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";
    const string BackupArea = @"SOFTWARE\HIDMaestroProbeBackup";
    const string BackupRel = BackupArea + @"\coexist-oem";
    // Written last by Take: a copy without it was cut short before any key
    // was deleted, and is dropped rather than written back.
    const string WholeValue = "Whole";
    static readonly string[] Prefixes = { "VID_045E", "VID_054C", "VID_0000" };

    static bool Swept(string name) => Prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    static (string Label, RegistryKey Hive)[] Hives() =>
        new[] { ("HKLM", Registry.LocalMachine), ("HKCU", Registry.CurrentUser) };

    /// <summary>Copy <paramref name="src"/>'s values, each with its kind and
    /// unexpanded, and its subkeys, whole, into <paramref name="dst"/>.
    /// RegCopyTree is not used: on the desktop PC it refused the copy of a
    /// key Administrators may read in full (error 5).</summary>
    static void CopyTree(RegistryKey src, RegistryKey dst)
    {
        foreach (string v in src.GetValueNames())
        {
            var kind = src.GetValueKind(v);
            if (kind == RegistryValueKind.Unknown)
                throw new InvalidOperationException($"{src.Name}: the value {v} has a kind the copy cannot write");
            dst.SetValue(v, src.GetValue(v, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, kind);
        }
        foreach (string s in src.GetSubKeyNames())
        {
            using var from = src.OpenSubKey(s) ?? throw new InvalidOperationException($@"{src.Name}\{s} cannot be opened");
            using var to = dst.CreateSubKey(s);
            CopyTree(from, to);
        }
    }

    /// <summary>Copy every key under the swept prefixes of both hives. A
    /// copy a stopped run left is put back first.</summary>
    internal static void Take()
    {
        RestoreLeft();
        using (var backup = Registry.LocalMachine.CreateSubKey(BackupRel))
        {
            foreach (var (label, hive) in Hives())
            {
                using var dest = backup.CreateSubKey(label);
                using var oem = hive.OpenSubKey(OemRel);
                if (oem == null) continue;
                foreach (string name in oem.GetSubKeyNames().Where(Swept))
                {
                    using var from = oem.OpenSubKey(name) ?? throw new InvalidOperationException($"{label} {name} cannot be opened");
                    using var d = dest.CreateSubKey(name);
                    CopyTree(from, d);
                }
            }
            backup.SetValue(WholeValue, 1, RegistryValueKind.DWord);
        }
    }

    /// <summary>Put the keys back as the copy holds them, and drop the
    /// copy. Returns what changed, empty when nothing did.</summary>
    internal static string Restore()
    {
        var changed = new List<string>();
        using (var backup = Registry.LocalMachine.OpenSubKey(BackupRel))
        {
            if (backup == null) return "";
            if (backup.GetValue(WholeValue) is int)
                foreach (var (label, hive) in Hives())
                {
                    using var saved = backup.OpenSubKey(label);
                    var names = new HashSet<string>(saved?.GetSubKeyNames() ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                    using var oem = names.Count > 0 ? hive.CreateSubKey(OemRel) : hive.OpenSubKey(OemRel, writable: true);
                    if (oem == null) continue;
                    foreach (string name in oem.GetSubKeyNames().Where(Swept).Where(n => !names.Contains(n)))
                    {
                        oem.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                        changed.Add($"{label} {name} made during the section, removed");
                    }
                    foreach (string name in names)
                    {
                        bool had = oem.GetSubKeyNames().Contains(name, StringComparer.OrdinalIgnoreCase);
                        oem.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
                        using var from = saved!.OpenSubKey(name) ?? throw new InvalidOperationException($"the copy of {label} {name} cannot be opened");
                        using var d = oem.CreateSubKey(name);
                        CopyTree(from, d);
                        if (!had) changed.Add($"{label} {name} put back");
                    }
                }
        }
        Registry.LocalMachine.DeleteSubKeyTree(BackupRel, throwOnMissingSubKey: false);
        using (var area = Registry.LocalMachine.OpenSubKey(BackupArea))
            if (area != null && area.SubKeyCount == 0 && area.ValueCount == 0)
            {
                area.Close();
                Registry.LocalMachine.DeleteSubKey(BackupArea, throwOnMissingSubKey: false);
            }
        return string.Join("; ", changed);
    }

    /// <summary>Put back the copy a stopped run left, if there is one.</summary>
    internal static void RestoreLeft()
    {
        string changed = Restore();
        if (changed.Length > 0) Console.WriteLine($"  [INFO] a stopped run's copy of the joy.cpl keys was put back: {changed}");
    }

    /// <summary>Copy the keys now (<see cref="Take"/>), and put them back
    /// when the result is disposed (<see cref="Restore"/>), after the
    /// v1.11.0 program a <c>using</c> inside it ends.</summary>
    internal static IDisposable Kept(string section)
    {
        Take();
        return new Back(section);
    }

    sealed class Back : IDisposable
    {
        readonly string _section;
        bool _done;
        internal Back(string section) => _section = section;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            string changed = Restore();
            Console.WriteLine($"  [INFO] {_section}: the joy.cpl keys after the v1.11.0 program: {(changed.Length > 0 ? changed : "as before")}");
        }
    }
}
