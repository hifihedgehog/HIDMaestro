// The 2026 Steam Controller persona against Steam's own firmware updater
// (issue #62). Needs elevation and a Steam install.
//
// Steam decides a controller wants a firmware update by running
// bin\hardwareupdater\hardwareupdater.exe --check-for-updates and posting a
// notification for every device the JSON lists. The updater lists a device
// whose build stamp differs from the one its hardwareupdater.cfg names,
// older or newer. v1.9.0's persona answered with a stamp captured from one
// real unit, so Steam offered it an update it could never take, every time
// Steam started. This scenario asks the updater itself:
//
//   1. --show-all-devices lists the persona, under the serial the persona
//      serves, with the build Steam's config names. That proves the
//      updater found the device and read our stamp, so the next result is
//      not silence from an updater that saw nothing.
//   2. --check-for-updates does not list it.
//   3. Control: the same persona with its stamp left at the capture, which
//      is v1.9.0's answer, IS listed by --check-for-updates. Skipped only
//      if Steam's current build happens to equal the capture.
//   4. After teardown the updater no longer sees the persona.
//
// Only the updater's two read-only queries run here. Its --update-*,
// --prep-* and --reboot-* options flash firmware and are never passed.
//
// Exit 0 PASS, 1 FAIL, 2 SKIP (no Steam updater on this machine).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static class Program
{
    const string ProfileId = "steam-controller-2";
    const string StampKey = "TRITON_FW_TS";
    const string UpdaterExe = "hardwareupdater.exe";
    const uint CapturedBuild = 0x6A18D057;

    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static int Main()
    {
        Console.WriteLine("=== Triton persona against Steam's firmware updater (issue #62) ===");
        // The updater's folder and config, found as the persona's own value
        // source finds them (valueSources.steamUpdater).
        var source = ProfileDatabase.LoadEmbedded().All.First(p => p.Id == ProfileId).ValueSources!["steamUpdater"];
        var (_, cfg) = ValueSources.Locate(source);
        string? dir = cfg == null ? null : Path.GetDirectoryName(cfg);
        string? exe = dir == null ? null : Path.Combine(dir, UpdaterExe);
        if (exe == null || !File.Exists(exe))
        {
            Console.WriteLine("[SKIP] no Steam hardware updater on this machine.");
            return 2;
        }
        uint? steamBuild = ValueSources.ReadFile(source) is string text ? ValueSources.ParseKeyedHex(text, StampKey) : null;
        if (steamBuild == null)
        {
            Console.WriteLine($"[SKIP] Steam's {Path.GetFileName(cfg)} names no {StampKey}.");
            return 2;
        }
        Console.WriteLine($"  updater: {exe}");
        Console.WriteLine($"  Steam ships Triton build 0x{steamBuild:X8}");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var profile = ctx.GetProfile(ProfileId);
        Check("the persona is in the catalog", profile != null);
        if (profile == null) return Finish();

        // The serial the updater reports is the unit serial the persona
        // serves at string index 1.
        var served = FeatureStubTable.From(profile.Inner, 0, null, _ => null)?.Lookup(0xAE, 1, 64);
        string serial = served == null ? "" : Ascii(served, 4);
        Check("the persona serves a unit serial", serial.Length > 0, serial);
        if (serial.Length == 0) return Finish();

        Console.WriteLine("\n-- The persona as shipped --");
        using (var ctrl = ctx.CreateController(profile))
        {
            var seen = WaitForListing(exe, dir!, "--show-all-devices", serial);
            Check("--show-all-devices lists the persona", seen != null,
                  seen == null ? "not found within 30 s" : $"serial {serial}");
            if (seen is JsonElement s)
                Check("with the build Steam's config names",
                      Stamp(s, "current_ts") == steamBuild,
                      $"current_ts 0x{Stamp(s, "current_ts"):X8}");

            var (rc, pending) = Updater(exe, dir!, "--check-for-updates");
            Check("--check-for-updates answers", rc == 0 && pending != null, $"exit {rc}");
            var mine = Find(pending, serial);
            Check("and does not offer the persona an update", pending != null && mine == null,
                  mine is JsonElement m ? $"offered 0x{Stamp(m, "update_ts"):X8} over 0x{Stamp(m, "current_ts"):X8}" : "");
        }
        Thread.Sleep(3000);

        Console.WriteLine("\n-- Control: the same persona answering with the capture, as v1.9.0 did --");
        if (steamBuild == CapturedBuild)
        {
            Console.WriteLine($"  [SKIP] Steam's build equals the capture, so the capture cannot be told apart.");
        }
        else
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"hmfw{Environment.ProcessId}");
            try
            {
                var stale = StaleProfile(ctx, tmp);
                Check("the variant without the tracked stamp loads", stale != null);
                if (stale != null)
                {
                    using var ctrl = ctx.CreateController(stale);
                    var seen = WaitForListing(exe, dir!, "--check-for-updates", serial);
                    Check("--check-for-updates offers it an update", seen != null,
                          seen is JsonElement o ? $"0x{Stamp(o, "current_ts"):X8} -> 0x{Stamp(o, "update_ts"):X8}" : "not listed within 30 s");
                    if (seen is JsonElement c)
                        Check("because it answers with the capture",
                              Stamp(c, "current_ts") == CapturedBuild,
                              $"current_ts 0x{Stamp(c, "current_ts"):X8}");
                }
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
            Thread.Sleep(3000);
        }

        Console.WriteLine("\n-- Teardown --");
        var (rcAll, all) = Updater(exe, dir!, "--show-all-devices");
        Check("the updater no longer sees the persona",
              rcAll == 0 && all != null && all.All(e => Serial(e) != serial), $"exit {rcAll}");

        return Finish();
    }

    static int Finish()
    {
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} {(s_failures == 0 ? "PASS" : "FAIL")} ===");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>The persona's own profile with its tracked stamp removed,
    /// loaded from the copy embedded in the SDK, so it answers with the
    /// captured build. It takes its own id, because a context keeps the
    /// first profile it loads under an id.</summary>
    static HMProfile? StaleProfile(HMContext ctx, string tmp)
    {
        const string variantId = ProfileId + "-capture";
        var asm = typeof(HMContext).Assembly;
        string? name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(ProfileId + ".json", StringComparison.OrdinalIgnoreCase));
        if (name == null) return null;
        using var src = asm.GetManifestResourceStream(name)!;
        var root = JsonNode.Parse(src)!;
        root["id"] = variantId;
        foreach (var r in root["featureStubs"]!["reports"]!.AsArray())
            r!.AsObject().Remove("patch");

        Directory.CreateDirectory(tmp);
        File.WriteAllText(Path.Combine(tmp, variantId + ".json"), root.ToJsonString());
        ctx.LoadProfilesFromDirectory(tmp);
        return ctx.GetProfile(variantId);
    }

    /// <summary>Re-run a query until it lists the serial, for up to 30 s:
    /// a new device takes a moment to reach the updater's enumeration.</summary>
    static JsonElement? WaitForListing(string exe, string dir, string query, string serial)
    {
        for (int i = 0; i < 15; i++)
        {
            var (rc, list) = Updater(exe, dir, query);
            if (rc == 0 && Find(list, serial) is JsonElement hit) return hit;
            Thread.Sleep(2000);
        }
        return null;
    }

    /// <summary>The entry for a serial, or null. A plain FirstOrDefault
    /// cannot say "none" here: JsonElement is a struct, and its default is
    /// an empty element rather than null.</summary>
    static JsonElement? Find(List<JsonElement>? list, string serial) =>
        list?.Where(e => Serial(e) == serial).Select(e => (JsonElement?)e).FirstOrDefault();

    /// <summary>Run one read-only query and return its device list. The
    /// updater reads its config relative to the working directory, so it
    /// starts from its own folder, as Steam starts it.</summary>
    static (int Rc, List<JsonElement>? List) Updater(string exe, string dir, string query)
    {
        var psi = new ProcessStartInfo(exe, query)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        // Both pipes read asynchronously: a synchronous ReadToEnd would
        // block until the updater exits, and the timeout below would never
        // fire. The battery runs a probe with no timeout of its own. The
        // updater is a PyInstaller bundle whose bootloader runs a child,
        // so a timeout kills the whole tree.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(60_000))
        {
            try { p.Kill(true); } catch { }
            Console.WriteLine("    updater timed out after 60 s");
            return (-999, null);
        }
        Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 10_000);
        string stdout = stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : "";
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            return (p.ExitCode, doc.RootElement.GetProperty("updates_available")
                .EnumerateArray().Select(e => e.Clone()).ToList());
        }
        catch (JsonException)
        {
            Console.WriteLine($"    updater printed: {stdout.Trim()}");
            return (p.ExitCode, null);
        }
    }

    static string Serial(JsonElement e) =>
        e.TryGetProperty("serial_number", out var s) ? s.GetString() ?? "" : "";

    static uint Stamp(JsonElement e, string field)
    {
        if (!e.TryGetProperty(field, out var v) || v.GetString() is not string hex) return 0;
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return uint.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint u) ? u : 0;
    }

    static string Ascii(byte[] b, int off)
    {
        int end = off;
        while (end < b.Length && b[end] != 0) end++;
        return Encoding.ASCII.GetString(b, off, end - off);
    }
}
