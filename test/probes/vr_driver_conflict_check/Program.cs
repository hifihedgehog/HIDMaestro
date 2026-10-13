// Another OpenVR driver registered under HIDMaestro's driver name.
//
// OpenVR requires a driver's manifest name to be unique. A derivative that
// keeps "hidmaestro" competes with HIDMaestro's own driver for the same
// name, resource prefix and named objects. HMVR.FindDriverNameConflicts
// reads the user's OpenVR path registry and SteamVR's drivers folder and
// lists every such folder other than HIDMaestro's own.
//
// The probe points VR_PATHREG_OVERRIDE (honored by OpenVR's
// GetVRPathRegistryFilename) at a registry file of its own, so the real one
// is never read or written. No elevation, no SteamVR needed. Exit 0 PASS / 1
// FAIL.

using System;
using System.IO;
using System.Linq;
using HIDMaestro;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static string Driver(string root, string folder, string name)
    {
        string dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "driver.vrdrivermanifest"),
            "{\n\t\"alwaysActivate\": true,\n\t\"name\" : \"" + name + "\",\n\t\"directory\" : \"\"\n}\n");
        return dir;
    }

    static int Main()
    {
        Console.WriteLine("=== OpenVR driver name conflicts ===");
        Check("HIDMaestro's driver is named hidmaestro", HMVR.DriverName == "hidmaestro", HMVR.DriverName);

        string root = Path.Combine(Path.GetTempPath(), "hm-vr-conflict-" + Guid.NewGuid().ToString("N"));
        string? saved = Environment.GetEnvironmentVariable("VR_PATHREG_OVERRIDE");
        try
        {
            string foreign = Driver(root, "hidmaestro", "hidmaestro");
            string other = Driver(root, "otherdriver", "otherdriver");
            string upper = Driver(root, "kitsune", "HIDMaestro");
            string ours = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                       "HIDMaestro", "openvr", "hidmaestro");
            string registry = Path.Combine(root, "openvrpaths.vrpath");
            string Esc(string p) => p.Replace("\\", "\\\\");
            File.WriteAllText(registry,
                "{\n\t\"external_drivers\" : \n\t[\n" +
                $"\t\t\"{Esc(foreign)}\",\n\t\t\"{Esc(other)}\",\n\t\t\"{Esc(upper)}\",\n\t\t\"{Esc(ours)}\",\n\t\t\"{Esc(Path.Combine(root, "missing"))}\"\n" +
                "\t],\n\t\"jsonid\" : \"vrpathreg\",\n\t\"version\" : 1\n}\n");
            Environment.SetEnvironmentVariable("VR_PATHREG_OVERRIDE", registry);

            var found = HMVR.FindDriverNameConflicts();
            Console.WriteLine($"  found: {(found.Count == 0 ? "(none)" : string.Join(", ", found))}");
            Check("a foreign folder named hidmaestro is reported", found.Any(f => f.Equals(foreign, StringComparison.OrdinalIgnoreCase)));
            Check("so is one whose manifest says HIDMaestro in another case", found.Any(f => f.Equals(upper, StringComparison.OrdinalIgnoreCase)));
            Check("a driver with another name is not", !found.Any(f => f.Equals(other, StringComparison.OrdinalIgnoreCase)));
            Check("HIDMaestro's own folder is not", !found.Any(f => f.Equals(ours, StringComparison.OrdinalIgnoreCase)));
            Check("a registered folder that no longer exists is skipped", found.Count == 2, $"{found.Count} reported");

            File.WriteAllText(registry, "{\n\t\"external_drivers\" : [],\n\t\"version\" : 1\n}\n");
            Check("an empty registry reports nothing beyond SteamVR's own drivers folder",
                  HMVR.FindDriverNameConflicts().All(f => !f.StartsWith(root, StringComparison.OrdinalIgnoreCase)));

            File.WriteAllText(registry, "not json");
            Check("an unreadable registry reports nothing and does not throw",
                  HMVR.FindDriverNameConflicts().All(f => !f.StartsWith(root, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("VR_PATHREG_OVERRIDE", saved);
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        var live = HMVR.FindDriverNameConflicts();
        Console.WriteLine($"  this machine's own registry: {(live.Count == 0 ? "no conflict" : string.Join(", ", live))}");

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
