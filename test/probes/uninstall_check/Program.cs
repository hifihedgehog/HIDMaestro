// HIDMaestro's removal: HIDMaestroUninstall.exe and HMUninstall.
//
//   preview  On this machine: a snapshot of what HIDMaestro leaves (devices,
//            driver packages, the certificate, registry keys, files), the
//            tool's --preview --json and HMUninstall.Preview in this
//            process, then the snapshot again. Nothing may change. The
//            preview must list every HIDMaestro package the snapshot finds.
//   sandbox  In Windows Sandbox started without a window, as SYSTEM: the
//            driver installs, a program creates a plain HID controller, an
//            Xbox 360 controller with its XUSB companion and an xinputhid
//            controller, sets an OEM name override and registers the
//            OpenVR driver with a stand-in SteamVR. While it runs the tool
//            refuses (exit 2) and changes nothing. Once it is killed the
//            tool removes everything, and the machine is read again: no
//            HIDMaestro device record, package, certificate, registry key or
//            folder is left, the OEM targets hold what they held before, the
//            OpenVR registration is gone, and no USB root hub arrived again.
//            Programs that start while the tool removes HIDMaestro, one
//            installing and one creating from two threads, wait at the gate
//            while the probe holds the removal at the deploy lock, do not
//            block it, and then install again and keep their controllers. A
//            running program that only set an OEM name override blocks a
//            removal. A junction at
//            %ProgramData%\HIDMaestro goes as a link with its target intact,
//            and usbip-win2's link on the all-users desktop is removed.
//            Then a program in this process installs, creates, and calls
//            HMUninstall.Run itself, as a program's uninstaller would, and
//            installs and creates again in the same process afterward.
//   fixture  The real removal on this machine, then a reinstall and one
//            controller created and disposed, so the machine stays a working
//            test fixture. Only with HIDMAESTRO_UNINSTALL_FIXTURE=1 and only
//            where Windows Sandbox is absent: on a machine with Windows
//            Sandbox the real removal runs only inside it.
//
// No argument runs preview, then sandbox where Windows Sandbox exists, else
// fixture where it is allowed. The battery's S101 runs preview and fixture
// in its slot and sandbox beside the scenarios before it.
//
// The sandbox run creates a USB/IP composite too. The SDK installs
// usbip-win2's host controller from its own package, with no root-hub
// filter, so the sandbox has no filter before or after.
//
// Requires elevation. Exit 0 PASS, 1 FAIL, 2 when the part asked for cannot
// run on this machine.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32;

internal static class Program
{
    // dualshock-4-v2's identity, so the override and the controller's own
    // joy.cpl key meet, as they do in a program that names its controller.
    const ushort OemVid = 0x054C, OemPid = 0x09CC;
    const string OemLabel = "HIDMaestro removal check";

    static int s_total, s_failures;

    delegate void CheckFn(string name, bool ok, string detail = "");

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--in-sandbox") return InSandbox(args[1]);
        if (args.Length >= 2 && args[0] == "--hold") return Hold(args[1]);
        if (args.Length >= 2 && args[0] == "--hold-one") return Hold(args[1], one: true);
        if (args.Length >= 4 && args[0] == "--late") return Late(args[1], args[2], args[3]);
        if (args.Length >= 3 && args[0] == "--only") return OnlyOne(args[1], args[2]);
        string part = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
        int code;
        switch (part)
        {
            case "preview": code = PreviewPart(); break;
            case "sandbox": code = SandboxPart(); break;
            case "fixture": code = FixturePart(); break;
            case "all":
                code = PreviewPart();
                int real = FindOnPath("wsb.exe") != null ? SandboxPart() : FixturePart();
                if (real == 1) code = 1;
                break;
            default:
                Console.WriteLine("Usage: UninstallCheck.exe [preview|sandbox|fixture]");
                return 1;
        }
        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return code == 2 && s_total == 0 ? 2 : s_failures == 0 ? 0 : 1;
    }

    static string ToolPath => Path.Combine(AppContext.BaseDirectory, "tool", "HIDMaestroUninstall.exe");

    // ── preview ────────────────────────────────────────────────────────

    static int PreviewPart()
    {
        Console.WriteLine("=== A preview changes nothing ===");
        if (!File.Exists(ToolPath)) { Check("the tool was built beside the probe", false, ToolPath); return 1; }
        // The battery sets HIDMAESTRO_DIAG=1, and with it any SDK call may
        // append to %TEMP%\HIDMaestro\teardown_diag.log, the log a user asks
        // for. The previews run without it, in this process and the tool's.
        Environment.SetEnvironmentVariable("HIDMAESTRO_DIAG", null);
        Environment.SetEnvironmentVariable("HIDMAESTRO_TIMING", null);
        // The positive control: a folder of a shape the SDK through v1.12.0
        // left in %TEMP%, made by this probe. The previews must list it, and
        // the snapshot must see it go once the probe deletes it.
        string control = Path.Combine(Path.GetTempPath(), "HIDMaestro_00000000c0ffee00");
        Directory.CreateDirectory(control);
        File.WriteAllText(Path.Combine(control, "control.txt"), "uninstall_check");
        var before = Inventory.Take();

        var (exit, output) = Run(ToolPath, 600_000, "--preview", "--json");
        var tool = Report.Parse(output);
        Check("the tool's --preview --json writes a report", tool != null, tool == null ? Tail(output) : "");
        if (tool != null)
        {
            Check("the report says it is a preview", tool.Preview);
            Check("its exit code matches its status", exit == tool.ExitCode && exit == (tool.Status == "Blocked" ? 2 : 0),
                  $"exit {exit}, status {tool.Status}, exitCode {tool.ExitCode}");
            Check("a preview has no Removed, RemovedAtRestart or Failed entry",
                  tool.Entries.All(e => e.Outcome is "WouldRemove" or "Kept"),
                  string.Join("; ", tool.Entries.Where(e => e.Outcome is not ("WouldRemove" or "Kept")).Take(5)));
            // The positive control: each HIDMaestro package the snapshot finds
            // through the driver database is in the preview.
            var published = before.Packages.Select(p => p[(p.IndexOf('|') + 1)..]).ToList();
            var listed = published.Where(oem => tool.Entries.Any(e => e.Kind == "driver package"
                && e.Location.StartsWith(oem + " (", StringComparison.OrdinalIgnoreCase))).ToList();
            Check("the preview lists every HIDMaestro driver package the driver database holds",
                  listed.Count == published.Count, $"{listed.Count} of {published.Count}: {string.Join(", ", published)}");
            Check("the preview lists the probe's control folder in %TEMP% (positive control)",
                  tool.Entries.Any(e => e.Kind == "folder" && e.Outcome == "WouldRemove" && e.Location.Equals(control, StringComparison.OrdinalIgnoreCase)), control);
            if (Directory.Exists(Inventory.ProgramFilesFolder))
                Check("the preview lists %ProgramFiles%\\HIDMaestro",
                      tool.Entries.Any(e => e.Kind == "folder" && e.Location.StartsWith(Inventory.ProgramFilesFolder, StringComparison.OrdinalIgnoreCase)));
            Console.WriteLine($"  [NOTE] the tool lists {tool.Entries.Count(e => e.Outcome == "WouldRemove")} item(s) to remove and keeps {tool.Entries.Count(e => e.Outcome == "Kept")}");
        }

        HMUninstallReport? api = null;
        try { api = HMUninstall.Preview(); }
        catch (Exception ex) { Check("HMUninstall.Preview runs in this process", false, $"{ex.GetType().Name}: {ex.Message}"); }
        if (api != null && tool != null)
        {
            Check("HMUninstall.Preview says it is a preview", api.IsPreview && api.Status is HMUninstallStatus.Previewed or HMUninstallStatus.Blocked,
                  api.Status.ToString());
            var a = api.Entries.Select(e => $"{e.Kind}|{e.Location}|{e.Outcome}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            var t = tool.Entries.Select(e => $"{e.Kind}|{e.Location}|{e.Outcome}").ToHashSet(StringComparer.OrdinalIgnoreCase);
            Check("HMUninstall.Preview and the tool list the same items", a.SetEquals(t),
                  $"{a.Except(t).Count()} only in the API's, {t.Except(a).Count()} only in the tool's");
        }

        var after = Inventory.Take();
        var diff = before.Diff(after).ToList();
        Check("nothing changed: devices, packages, certificates, registry keys and files read the same after both previews",
              diff.Count == 0, string.Join("; ", diff.Take(8)));
        try { Directory.Delete(control, recursive: true); } catch { }
        var gone = after.Diff(Inventory.Take()).ToList();
        Check("the snapshot sees a change: the control folder's deletion shows (positive control)",
              gone.Any(d => d.Contains(control, StringComparison.OrdinalIgnoreCase)), $"{gone.Count} difference(s)");
        return s_failures == 0 ? 0 : 1;
    }

    // ── fixture ────────────────────────────────────────────────────────

    static int FixturePart()
    {
        Console.WriteLine();
        Console.WriteLine("=== The real removal on this machine, then a reinstall ===");
        if (FindOnPath("wsb.exe") != null)
        {
            Console.WriteLine("  [SKIP] Windows Sandbox is available here, so the real removal runs only inside it.");
            return 2;
        }
        if (Environment.GetEnvironmentVariable("HIDMAESTRO_UNINSTALL_FIXTURE") != "1")
        {
            Console.WriteLine("  [SKIP] HIDMAESTRO_UNINSTALL_FIXTURE is not 1 on this machine.");
            return 2;
        }
        if (!File.Exists(ToolPath)) { Check("the tool was built beside the probe", false, ToolPath); return 1; }

        var before = Inventory.Take();
        Check("the machine holds HIDMaestro's driver before the removal (positive control)", before.Packages.Count > 0,
              $"{before.Packages.Count} package(s)");
        var hubs = Inventory.RootHubs();
        var (exit, output) = Run(ToolPath, 1_800_000, "--yes", "--json");
        var report = Report.Parse(output);
        RemovalChecks("fixture", exit, report, output, hubs, clean: false, Console.WriteLine, Check);

        // Leave the fixture working: the driver installs again and a
        // controller comes and goes.
        try
        {
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            using (var c = ctx.CreateController(ctx.GetProfile("dualshock-4-v2")!, "uninstall-fixture"))
                Check("after the removal the driver installs again and a controller creates", c != null);
            Check("the driver packages are back", Inventory.Take().Packages.Count > 0);
        }
        catch (Exception ex) { Check("after the removal the driver installs again and a controller creates", false, $"{ex.GetType().Name}: {ex.Message}"); }
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>The checks after a real removal, from the tool's report and
    /// from the machine read again directly.</summary>
    static void RemovalChecks(string p, int exit, Report? report, string output, SortedDictionary<string, long> hubsBefore,
                              bool clean, Action<string> note, CheckFn check)
    {
        check($"{p}: the tool writes a report", report != null, report == null ? Tail(output) : "");
        if (report == null) return;
        check($"{p}: the tool removed HIDMaestro (exit 0, or 3 with items left to the next start)",
              exit is 0 or 3 && report.Status is "Removed" or "RemovedRestartPending", $"exit {exit}, status {report.Status}");
        var failed = report.Entries.Where(e => e.Outcome == "Failed").ToList();
        check($"{p}: no item failed", failed.Count == 0, string.Join("; ", failed.Take(6)));
        foreach (var g in report.Entries.GroupBy(e => e.Outcome))
            note($"{p}: {g.Count()} {g.Key}");
        foreach (var e in report.Entries.Where(e => clean || e.Outcome is "Kept" or "RemovedAtRestart"))
            note($"{p}: {e}");

        var after = Inventory.Take();
        var devnodes = after.HidMaestroDevnodes();
        check($"{p}: no HIDMaestro device record is left, present or not", devnodes.Count == 0, string.Join(", ", devnodes.Take(8)));
        if (clean)
        {
            var hid = after.HidMaestroShapedHidRecords();
            check($"{p}: no HID record of HIDMaestro's shapes is left", hid.Count == 0, string.Join(", ", hid.Take(8)));
        }
        check($"{p}: no HIDMaestro driver package is left", after.Packages.Count == 0 || KeptPackages(report, after),
              string.Join(", ", after.Packages));
        check($"{p}: the certificate is gone from My, Root and TrustedPublisher", after.Certificates.Count == 0,
              string.Join(", ", after.Certificates));
        foreach (string key in Inventory.WatchedKeys.Take(5).Concat(Inventory.WatchedKeys.Skip(6).Take(2)))
        {
            bool kept = report.Entries.Any(e => e.Outcome == "Kept" && e.Location.Equals(key, StringComparison.OrdinalIgnoreCase));
            check($"{p}: {key} is gone", !Inventory.KeyExists(key) || kept, kept ? "kept, with the reason in the report" : "");
        }
        if (clean)
        {
            string overrides = Inventory.Dump(@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceOverrides");
            check($"{p}: no root#VID_ DeviceOverrides key is left", !overrides.Contains(@"\root#VID_", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            var left = report.Entries.Where(e => e.Kind == "DeviceOverrides key" && e.Outcome == "Removed" && Inventory.KeyExists(e.Location)).ToList();
            check($"{p}: every DeviceOverrides key the report removed is gone", left.Count == 0, string.Join(", ", left.Take(5)));
        }
        var pending = Inventory.PendingDeletes();
        foreach (string folder in new[] { Inventory.ProgramFilesFolder, Inventory.ProgramDataFolder })
        {
            bool kept = report.Entries.Any(e => e.Outcome == "Kept" && e.Location.Equals(folder, StringComparison.OrdinalIgnoreCase));
            var under = Directory.Exists(folder)
                ? Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Prepend(folder)
                    .Where(x => !pending.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList()
                : new List<string>();
            check($"{p}: {folder} is gone or left only to the next start", under.Count == 0 || kept, string.Join(", ", under.Take(5)));
        }
        var temps = Inventory.TempRoots().SelectMany(t =>
        {
            try { return Directory.EnumerateDirectories(t, "HIDMaestro*").Where(d => IsLegacyTemp(Path.GetFileName(d))).ToList(); }
            catch { return new List<string>(); }
        }).ToList();
        check($"{p}: no legacy HIDMaestro folder is left in any %TEMP%", temps.Count == 0, string.Join(", ", temps.Take(5)));
        foreach (string f in Inventory.OpenVrFiles())
            if (File.Exists(f))
                check($"{p}: {f} no longer names HIDMaestro's OpenVR driver",
                      !File.ReadAllText(f).Contains(@"HIDMaestro\\openvr\\hidmaestro", StringComparison.OrdinalIgnoreCase));
        var hubsAfter = Inventory.RootHubs();
        var restarted = hubsBefore.Where(h => hubsAfter.TryGetValue(h.Key, out long t) && t != h.Value).Select(h => h.Key).ToList();
        var gone = hubsBefore.Keys.Where(h => !hubsAfter.ContainsKey(h)).ToList();
        note($"{p}: {hubsBefore.Count} USB root hub(s) present before the removal");
        check($"{p}: no USB root hub arrived again", restarted.Count == 0, string.Join(", ", restarted));
        // usbip-win2's own virtual hub goes with its host controller when
        // usbip-win2 is removed, so a hub that is gone is noted, not judged.
        foreach (string h in gone) note($"{p}: root hub {h} is no longer present");

        HMUninstallReport? again = null;
        try { again = HMUninstall.Preview(); } catch (Exception ex) { note($"{p}: the preview after the removal threw {ex.Message}"); }
        var still = again?.Entries.Where(e => e.Outcome != HMUninstallOutcome.Kept).ToList() ?? new List<HMUninstallEntry>();
        check($"{p}: a preview after the removal finds nothing but kept items", again != null && still.Count == 0,
              string.Join("; ", still.Take(6)));
    }

    /// <summary>A package left after the removal is accepted only when the
    /// report kept it with a reason: a WinUSB package a device record still
    /// names.</summary>
    static bool KeptPackages(Report report, Inventory after) =>
        after.Packages.All(pkg => report.Entries.Any(e => e.Kind == "driver package" && e.Outcome == "Kept"
            && e.Location.StartsWith(pkg[(pkg.IndexOf('|') + 1)..] + " (", StringComparison.OrdinalIgnoreCase)));

    static bool IsLegacyTemp(string name) =>
        name.Equals("HIDMaestro", StringComparison.OrdinalIgnoreCase)
        || System.Text.RegularExpressions.Regex.IsMatch(name, @"^HIDMaestro_([0-9A-Fa-f-]{8,}|usbip_[0-9.]+)$")
        || System.Text.RegularExpressions.Regex.IsMatch(name, @"^HIDMaestroEmulate-[0-9A-Fa-f-]{8,}$");

    // ── sandbox: host ──────────────────────────────────────────────────

    static readonly string SandboxIdFile = Path.Combine(Path.GetTempPath(), "HIDMaestro_uninstall_check.sandbox");
    static readonly string UsbipInstallCheckIdFile = Path.Combine(Path.GetTempPath(), "HIDMaestro", "usbip_install_check.sandbox");

    static int SandboxPart()
    {
        Console.WriteLine();
        Console.WriteLine("=== The removal in Windows Sandbox ===");
        string? wsb = FindOnPath("wsb.exe");
        if (wsb == null)
        {
            Console.WriteLine("  [SKIP] Windows Sandbox (wsb.exe) is not available on this machine.");
            return 2;
        }
        string publish = Path.Combine(AppContext.BaseDirectory, "sandbox");
        string toolDir = Path.Combine(AppContext.BaseDirectory, "tool");
        if (!File.Exists(Path.Combine(publish, "UninstallCheck.exe")) || !File.Exists(Path.Combine(toolDir, "HIDMaestroUninstall.exe")))
        {
            Check("the sandbox copy and the tool were built beside the probe", false, $"{publish}, {toolDir}");
            return 1;
        }
        string results = Path.Combine(Path.GetTempPath(), $"HIDMaestro_uninstall_{Guid.NewGuid():N}");
        Directory.CreateDirectory(results);
        string? id = null;
        var clock = Stopwatch.StartNew();
        try
        {
            string config =
                "<Configuration><Networking>Disable</Networking><MappedFolders>" +
                $"<MappedFolder><HostFolder>{Xml(publish)}</HostFolder><SandboxFolder>C:\\probe</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>" +
                $"<MappedFolder><HostFolder>{Xml(toolDir)}</HostFolder><SandboxFolder>C:\\tool</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>" +
                $"<MappedFolder><HostFolder>{Xml(results)}</HostFolder><SandboxFolder>C:\\results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>" +
                "</MappedFolders></Configuration>";
            // One sandbox at a time: a run killed before its finally leaves
            // its sandbox running, and the next start fails. Each run stops
            // the one an earlier run left: this probe's, or
            // usbip_install_check's (S73), which the battery collects before
            // this one starts, so a recorded one is left over. No other
            // sandbox is touched.
            foreach (string idFile in new[] { SandboxIdFile, UsbipInstallCheckIdFile })
            {
                if (!File.Exists(idFile)) continue;
                string left = File.ReadAllText(idFile).Trim();
                if (left.Length > 0) Run(wsb, 120_000, "stop", "--id", left);
                File.Delete(idFile);
            }
            var started = Run(wsb, 120_000, "start", "--config", config, "--raw");
            id = JsonString(started.Output, "Id");
            if (id == null) { Check("a sandbox starts", false, started.Output.Trim()); return 1; }
            File.WriteAllText(SandboxIdFile, id);
            var sw = Stopwatch.StartNew();
            bool ready = false;
            while (!ready && sw.ElapsedMilliseconds < 180_000)
            {
                var probe = Run(wsb, 60_000, "exec", "--id", id, "-c", "cmd.exe /c exit 0", "-r", "System", "--raw");
                ready = JsonInt(probe.Output, "ExitCode") == 0;
                if (!ready) Thread.Sleep(250);
            }
            if (!ready) { Check("the sandbox accepts a command", false); return 1; }
            var ran = Run(wsb, 2_400_000, "exec", "--id", id, "-c", @"C:\probe\UninstallCheck.exe --in-sandbox C:\results", "-r", "System", "--raw");
            string file = Path.Combine(results, "results.txt");
            if (!File.Exists(file)) { Check("the run inside the sandbox reports back", false, ran.Output.Trim()); return 1; }
            var lines = File.ReadAllLines(file);
            foreach (string line in lines)
            {
                if (line == "DONE") continue;
                int sp = line.IndexOf(' ');
                if (sp < 0) continue;
                string verdict = line[..sp], rest = line[(sp + 1)..];
                if (verdict == "NOTE") { Console.WriteLine($"  [NOTE] {rest}"); continue; }
                int bar = rest.IndexOf(" | ", StringComparison.Ordinal);
                Check(bar < 0 ? rest : rest[..bar], verdict == "PASS", bar < 0 ? "" : rest[(bar + 3)..]);
            }
            if (!lines.Contains("DONE")) Check("the run inside the sandbox finished", false, ran.Output.Trim());
            // The battery runs this part beside the scenarios after S73, whose
            // probes skip while another program has HIDMaestro.Core.dll loaded,
            // so the host side never loads it.
            Check("the host part ran without loading HIDMaestro.Core.dll",
                  !AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "HIDMaestro.Core"));
        }
        finally
        {
            if (id != null) Run(wsb, 120_000, "stop", "--id", id);
            try { File.Delete(SandboxIdFile); } catch { }
            Console.WriteLine(FormattableString.Invariant($"  [NOTE] the sandbox part took {clock.Elapsed.TotalSeconds:F1} s"));
            try { Directory.Delete(results, true); } catch { }
        }
        return s_failures == 0 ? 0 : 1;
    }

    // ── sandbox: inside ────────────────────────────────────────────────

    static readonly List<string> s_lines = new();

    static void Note(string text) => s_lines.Add("NOTE " + text.Replace('\r', ' ').Replace('\n', ' '));

    static void Record(string name, bool ok, string detail = "")
    {
        detail = detail.Replace('\r', ' ').Replace('\n', ' ');
        s_lines.Add($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " | " + detail : "")}");
    }

    static string SandboxTool => @"C:\tool\HIDMaestroUninstall.exe";

    static int InSandbox(string results)
    {
        // Windows Sandbox signs in as WDAGUtilityAccount. A machine without
        // that profile is not the sandbox, and nothing here may run on it.
        if (!Directory.Exists(@"C:\Users\WDAGUtilityAccount"))
        {
            Console.WriteLine("Refused: --in-sandbox runs only inside Windows Sandbox.");
            return 1;
        }
        // First, while usbip-win2 is absent: once a composite has used the
        // host controller it stays until a restart, and what its installer
        // left stays with it.
        try { PublicLink(); }
        catch (Exception ex) { Record("public: the run ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
        try { RemovalInSandbox(results); }
        catch (Exception ex) { Record("the run ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
        try { LateStart(results); }
        catch (Exception ex) { Record("late: the run ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
        try { ProgramDataJunction(); }
        catch (Exception ex) { Record("links: the run ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
        try { InProcess(); }
        catch (Exception ex) { Record("inproc: the run ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
        s_lines.Add("DONE");
        File.WriteAllLines(Path.Combine(results, "results.txt"), s_lines);
        return s_lines.Any(l => l.StartsWith("FAIL", StringComparison.Ordinal)) ? 1 : 0;
    }

    static string OemTargets() => string.Join("\n",
        Inventory.Dump($@"HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput\VID_{OemVid:X4}&PID_{OemPid:X4}"),
        Inventory.Dump($@"HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{OemVid:X4}&PID_{OemPid:X4}"),
        Inventory.Dump($@"HKCU\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{OemVid:X4}&PID_{OemPid:X4}"));

    static void RemovalInSandbox(string results)
    {
        var clock = Stopwatch.StartNew();
        string oemBefore = OemTargets();
        var clean = Inventory.Take();
        Record("the sandbox starts without HIDMaestro", clean.Packages.Count == 0 && clean.HidMaestroDevnodes().Count == 0 && clean.Certificates.Count == 0);

        // A stand-in SteamVR: discovery finds C:\SteamVR by its
        // bin\win64\vrpathreg.exe, which HIDMaestro never runs.
        Directory.CreateDirectory(@"C:\SteamVR\bin\win64");
        File.WriteAllBytes(@"C:\SteamVR\bin\win64\vrpathreg.exe", Array.Empty<byte>());

        string answer = Path.Combine(results, "hold.txt");
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--hold");
        psi.ArgumentList.Add(answer);
        using var holder = Process.Start(psi)!;
        var wait = Stopwatch.StartNew();
        string[] said = Array.Empty<string>();
        while (wait.ElapsedMilliseconds < 900_000 && !holder.HasExited)
        {
            try { if (File.Exists(answer)) said = ReadAnswer(answer); } catch { }
            if (said.Contains("ready")) break;
            Thread.Sleep(500);
        }
        foreach (string s in said.Where(s => s != "ready")) Note($"holder: {s}");
        Record("a program installs the driver", said.Contains("installed"), string.Join(" / ", said));
        Record("it creates a plain HID controller (positive control)", said.Contains("created dualshock-4-v2"));
        Record("it sets an OEM name override", said.Contains("oem set"));
        Note(FormattableString.Invariant($"setup took {clock.Elapsed.TotalSeconds:F1} s"));

        // The composite's HID functions arrive after its create returns, so
        // the snapshot waits for Windows to finish installing devices, or the
        // blocked run's comparison would see them arrive.
        Note($"before the snapshot, device installation {(CMP_WaitNoPendingInstallEvents(120_000) == 0 ? "settled" : "still pending after 120 s")}");
        var before = Inventory.Take();
        Record("the machine holds HIDMaestro: devices, packages and the certificate (positive control)",
               before.HidMaestroDevnodes().Count > 0 && before.Packages.Count > 0 && before.Certificates.Count > 0,
               $"{before.HidMaestroDevnodes().Count} device record(s), {before.Packages.Count} package(s), {before.Certificates.Count} certificate(s)");

        // While the program runs, the removal refuses and changes nothing.
        var (bexit, bout) = Run(SandboxTool, 600_000, "--yes", "--json");
        var blocked = Report.Parse(bout);
        Record("while the program runs the tool exits 2, blocked", bexit == 2 && blocked?.Status == "Blocked", $"exit {bexit}, status {blocked?.Status}");
        Record("and names the program's process", blocked?.Programs.Contains(holder.Id) == true,
               $"holder {holder.Id}, listed {string.Join(", ", blocked?.Programs ?? new List<int>())}");
        // A device record that appears is the composite's own late
        // enumeration: one HID record of it arrived after device installation
        // had settled (2026-10-21), and no removal adds a record. Everything
        // else counts.
        var diff = before.Diff(Inventory.Take()).ToList();
        foreach (string d in diff.Where(d => d.StartsWith("devnode new:", StringComparison.Ordinal))) Note($"while blocked: {d}");
        diff = diff.Where(d => !d.StartsWith("devnode new:", StringComparison.Ordinal)).ToList();
        Record("and changes nothing", diff.Count == 0, string.Join("; ", diff.Take(6)));

        // The certificate's private key, by its CNG name, before and after.
        var key = CertificateKey();
        Record("the certificate's private key is in the machine key store (positive control)", key is { } k0 && KeyExists(k0),
               key?.ToString() ?? "no RSA CNG key on the certificate in My");

        // Killed, the program leaves its devices and its record behind, as a
        // crash does.
        try { holder.Kill(); holder.WaitForExit(30_000); } catch { }
        // Links where a non-administrator could plant them. One account's
        // %TEMP% becomes a junction to a folder holding a HIDMaestro-named
        // folder, and C:\Windows\Temp gets a junction under a HIDMaestro
        // name. Neither target may lose its file.
        const string baitTemp = @"C:\uninstall_bait_temp", baitName = @"C:\uninstall_bait_name";
        string wdagTemp = @"C:\Users\WDAGUtilityAccount\AppData\Local\Temp";
        string plantedName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp", "HIDMaestro_00000000deadbeef");
        Directory.CreateDirectory(Path.Combine(baitTemp, "HIDMaestro"));
        File.WriteAllText(Path.Combine(baitTemp, "HIDMaestro", "keep.txt"), "uninstall_check");
        Directory.CreateDirectory(baitName);
        File.WriteAllText(Path.Combine(baitName, "keep.txt"), "uninstall_check");
        bool tempMoved = false;
        try
        {
            if (Directory.Exists(wdagTemp)) { Directory.Move(wdagTemp, wdagTemp + ".real"); tempMoved = true; }
            Run("cmd.exe", 30_000, "/c", "mklink", "/J", wdagTemp, baitTemp);
            Run("cmd.exe", 30_000, "/c", "mklink", "/J", plantedName, baitName);
        }
        catch (Exception ex) { Note($"the junctions could not be planted: {ex.Message}"); }
        Record("two junctions are planted (positive control)",
               IsLinkPath(wdagTemp) && IsLinkPath(plantedName));

        var hubs = Inventory.RootHubs();
        var step = Stopwatch.StartNew();
        var (exit, output) = Run(SandboxTool, 1_800_000, "--yes", "--json");
        Note(FormattableString.Invariant($"the removal took {step.Elapsed.TotalSeconds:F1} s"));
        var parsed = Report.Parse(output);
        Record("removal: a %TEMP% that is a junction is not walked, and the HIDMaestro folder behind it stays",
               File.Exists(Path.Combine(baitTemp, "HIDMaestro", "keep.txt"))
               && parsed?.Entries.Any(e => e.Outcome == "Kept" && e.Location.Equals(wdagTemp, StringComparison.OrdinalIgnoreCase)) == true,
               parsed?.Entries.FirstOrDefault(e => e.Location.Equals(wdagTemp, StringComparison.OrdinalIgnoreCase))?.ToString() ?? "no entry for it");
        Record("removal: a junction under a HIDMaestro name goes as a junction, and its target stays",
               !Directory.Exists(plantedName) && !File.Exists(plantedName) && File.Exists(Path.Combine(baitName, "keep.txt")));
        try
        {
            if (IsLinkPath(wdagTemp)) Directory.Delete(wdagTemp, recursive: false);
            if (tempMoved) Directory.Move(wdagTemp + ".real", wdagTemp);
        }
        catch (Exception ex) { Note($"the junction could not be taken back: {ex.Message}"); }
        RemovalChecks("removal", exit, parsed, output, hubs, clean: true, Note, (n, ok, d) => Record(n, ok, d));
        if (key is { } k1) Record("removal: the certificate's private key is gone from the machine key store", !KeyExists(k1), k1.ToString());
        string oemAfter = OemTargets();
        Record("removal: the OEM name targets hold what they held before HIDMaestro", oemAfter == oemBefore,
               oemAfter == oemBefore ? "" : $"before [{oemBefore}] after [{oemAfter}]");
    }

    /// <summary>The CNG key name and provider of the HIDMaestro certificate
    /// in LocalMachine My, or null.</summary>
    static (string Name, string Provider)? CertificateKey()
    {
        using var store = new System.Security.Cryptography.X509Certificates.X509Store(
            System.Security.Cryptography.X509Certificates.StoreName.My, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
        foreach (var c in store.Certificates)
            using (c)
            {
                if (c.Subject != "CN=HIDMaestroTestCert") continue;
                using var rsa = System.Security.Cryptography.X509Certificates.RSACertificateExtensions.GetRSAPrivateKey(c);
                if (rsa is System.Security.Cryptography.RSACng cng && cng.Key.KeyName is { } name)
                    return (name, cng.Key.Provider?.Provider ?? "");
            }
        return null;
    }

    static bool KeyExists((string Name, string Provider) key) =>
        System.Security.Cryptography.CngKey.Exists(key.Name, new System.Security.Cryptography.CngProvider(key.Provider),
            System.Security.Cryptography.CngKeyOpenOptions.MachineKey);

    /// <summary>A program's own uninstaller: it installs, creates, and
    /// calls HMUninstall.Run in its own process with its controller still
    /// open, then installs and creates again.</summary>
    static void InProcess()
    {
        var clock = Stopwatch.StartNew();
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var profile = ctx.GetProfile("dualshock-4-v2")!;
        var c = ctx.CreateController(profile, "uninstall-inproc");
        Record("inproc: the program's controller creates (positive control)", Inventory.Take().HidMaestroDevnodes().Count > 0);
        Record("inproc: the program has its owner record (positive control)", Inventory.KeyExists(@"HKLM\SOFTWARE\HIDMaestroOwners"));

        var r = HMUninstall.Run();
        var failed = r.Entries.Where(e => e.Outcome == HMUninstallOutcome.Failed).ToList();
        Record("inproc: HMUninstall.Run in the program removes HIDMaestro",
               r.Status is HMUninstallStatus.Removed or HMUninstallStatus.RemovedRestartPending && failed.Count == 0,
               $"{r.Status}: {string.Join("; ", failed.Take(5))}");
        Record("inproc: the program is not its own blocker", r.RunningPrograms.Count == 0);
        var after = Inventory.Take();
        Record("inproc: its controller's devices are gone", after.HidMaestroDevnodes().Count == 0, string.Join(", ", after.HidMaestroDevnodes().Take(5)));
        Record("inproc: its owner record is gone", !Inventory.KeyExists(@"HKLM\SOFTWARE\HIDMaestroOwners"));
        Record("inproc: the packages and the certificate are gone", after.Packages.Count == 0 && after.Certificates.Count == 0);
        Exception? disposeError = null;
        try { c.Dispose(); } catch (Exception ex) { disposeError = ex; }
        Record("inproc: the program's controller still disposes", disposeError == null, disposeError?.Message ?? "");

        // The same process installs and creates again, which needs the
        // caches the removal reset: the child temp folder, the signed
        // package, the DeviceOverrides key.
        try
        {
            ctx.InstallDriver();
            using (var again = ctx.CreateController(profile, "uninstall-inproc-again"))
            {
                Record("inproc: the same process installs the driver and creates again", Inventory.Take().HidMaestroDevnodes().Count > 0);
                Record("inproc: and writes its DeviceOverrides key again",
                       Inventory.KeyExists($@"HKLM\SYSTEM\CurrentControlSet\Control\DeviceOverrides\root#VID_{OemVid:X4}&PID_{OemPid:X4}"));
            }
        }
        catch (Exception ex) { Record("inproc: the same process installs the driver and creates again", false, $"{ex.GetType().Name}: {ex.Message}"); }
        var last = HMUninstall.Run();
        Record("inproc: a second removal ends clean", last.Status is HMUninstallStatus.Removed or HMUninstallStatus.RemovedRestartPending
               && Inventory.Take().Packages.Count == 0, last.Status.ToString());
        Note(FormattableString.Invariant($"inproc: took {clock.Elapsed.TotalSeconds:F1} s"));

        try { InUseAndRecords(ctx); }
        catch (Exception ex) { Record("held: the run ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
        try { RecordsFrom200(); }
        catch (Exception ex) { Record("2.0.0 records: the run ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Files in use, and override records a removal cannot prove.
    /// A file another program holds open inside %ProgramFiles%\HIDMaestro
    /// waits for the next start, and one in %ProgramData%\HIDMaestro stays
    /// and fails the run, with nothing queued for it. A record older than
    /// 2.0.1 stays with its label, and a 2.0.1 record of another account
    /// has its machine-wide labels put back and keeps its per-user one.</summary>
    static void InUseAndRecords(HMContext ctx)
    {
        ctx.InstallDriver();
        string pf = Inventory.ProgramFilesFolder, pd = Inventory.ProgramDataFolder;
        string pfHeld = Path.Combine(pf, "childtemp", "held.bin");
        string pdHeld = Path.Combine(pd, "openvr", "hidmaestro", "held.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(pfHeld)!);
        Directory.CreateDirectory(Path.GetDirectoryName(pdHeld)!);

        const string di = @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput";
        const string joy = @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";
        const string pending = @"SOFTWARE\HIDMaestroOemOverrides";
        // A record as 2.0.0 wrote it, with no label: the target keeps a value
        // the removal cannot show is HIDMaestro's.
        using (var k = Registry.LocalMachine.CreateSubKey($@"{di}\VID_BEEF&PID_0A01\OEM")) k.SetValue("OEM Name", "legacy label");
        using (var r = Registry.LocalMachine.CreateSubKey($@"{pending}\VID_BEEF&PID_0A01"))
        {
            r.SetValue("OriginalKeyExisted", 0, RegistryValueKind.DWord);
        }
        // A 2.0.1 record another account's program wrote.
        using (var k = Registry.LocalMachine.CreateSubKey($@"{di}\VID_BEEF&PID_0A02\OEM")) k.SetValue("OEM Name", "other label");
        using (var k = Registry.LocalMachine.CreateSubKey($@"{joy}\VID_BEEF&PID_0A02")) k.SetValue("OEMName", "other label");
        using (var r = Registry.LocalMachine.CreateSubKey($@"{pending}\VID_BEEF&PID_0A02"))
        {
            r.SetValue("OriginalKeyExisted", 0, RegistryValueKind.DWord);
            r.SetValue("OriginalJoystickKeyExisted_HKLM", 0, RegistryValueKind.DWord);
            r.SetValue("OriginalJoystickKeyExisted_HKCU", 0, RegistryValueKind.DWord);
            r.SetValue("WrittenLabel", "other label");
            r.SetValue("UserSid", "S-1-5-21-1000000000-1000000000-1000000000-1001");
        }

        HMUninstallReport report;
        using (new FileStream(pfHeld, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        using (new FileStream(pdHeld, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            report = HMUninstall.Run();
        var pendingDeletes = Inventory.PendingDeletes();
        Record("held: a file in use under %ProgramFiles%\\HIDMaestro waits for the next start",
               pendingDeletes.Contains(pfHeld, StringComparer.OrdinalIgnoreCase)
               && report.Entries.Any(e => e.Location.Equals(pf, StringComparison.OrdinalIgnoreCase) && e.Outcome == HMUninstallOutcome.RemovedAtRestart),
               string.Join("; ", report.Entries.Where(e => e.Location.StartsWith(pf, StringComparison.OrdinalIgnoreCase)))
               + $" | queued: {string.Join(", ", pendingDeletes.Take(6))}");
        Record("held: a file in use under %ProgramData%\\HIDMaestro stays, reported failed, with nothing queued for the next start",
               File.Exists(pdHeld) && !pendingDeletes.Any(p => p.StartsWith(pd, StringComparison.OrdinalIgnoreCase))
               && report.Entries.Any(e => e.Location.Equals(pd, StringComparison.OrdinalIgnoreCase) && e.Outcome == HMUninstallOutcome.Failed),
               string.Join("; ", report.Entries.Where(e => e.Location.StartsWith(pd, StringComparison.OrdinalIgnoreCase))));
        string ReadDi(string id) { using var k = Registry.LocalMachine.OpenSubKey($@"{di}\{id}\OEM"); return k?.GetValue("OEM Name") as string ?? "absent"; }
        Record("records: a record older than 2.0.1 stays, and so does its label",
               Inventory.KeyExists($@"HKLM\{pending}\VID_BEEF&PID_0A01") && ReadDi("VID_BEEF&PID_0A01") == "legacy label"
               && report.Entries.Any(e => e.Location.EndsWith("VID_BEEF&PID_0A01", StringComparison.OrdinalIgnoreCase) && e.Outcome == HMUninstallOutcome.Kept));
        bool otherKept;
        using (var r = Registry.LocalMachine.OpenSubKey($@"{pending}\VID_BEEF&PID_0A02"))
            otherKept = r != null && r.GetValue("OriginalJoystickKeyExisted_HKCU") is 0 && r.GetValue("OriginalKeyExisted") == null;
        Record("records: another account's record has its machine-wide labels put back and keeps its per-user one",
               otherKept && ReadDi("VID_BEEF&PID_0A02") == "absent" && !Inventory.KeyExists($@"HKLM\{joy}\VID_BEEF&PID_0A02"));

        Registry.LocalMachine.DeleteSubKeyTree(pending, throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree($@"{di}\VID_BEEF&PID_0A01", throwOnMissingSubKey: false);
        var again = HMUninstall.Run();
        Record("held: once nothing holds them, a removal ends with nothing failed",
               again.Entries.All(e => e.Outcome != HMUninstallOutcome.Failed),
               string.Join("; ", again.Entries.Where(e => e.Outcome == HMUninstallOutcome.Failed).Take(4)));
    }

    /// <summary>OEM override records as 2.0.0 wrote them, naming no
    /// account. One another account made, whose label this account's hive
    /// does not hold: a Set and a Clear from here leave this hive alone and
    /// keep that account's part. One this account made, whose label this
    /// hive holds: a Set from here takes it over, and a full removal puts
    /// this hive back and drops the record.</summary>
    static void RecordsFrom200()
    {
        const string di = @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput";
        const string joy = @"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";
        const string pending = @"SOFTWARE\HIDMaestroOemOverrides";
        string me = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        string? Read(RegistryKey hive, string path, string value) { using var k = hive.OpenSubKey(path); return k?.GetValue(value) as string; }
        void Plant(string id, string label, bool thisHive)
        {
            using (var k = Registry.LocalMachine.CreateSubKey($@"{di}\{id}\OEM")) k.SetValue("OEM Name", label);
            using (var k = Registry.LocalMachine.CreateSubKey($@"{joy}\{id}")) k.SetValue("OEMName", label);
            if (thisHive) using (var k = Registry.CurrentUser.CreateSubKey($@"{joy}\{id}")) k.SetValue("OEMName", label);
            using var r = Registry.LocalMachine.CreateSubKey($@"{pending}\{id}");
            r.SetValue("OriginalKeyExisted", 0, RegistryValueKind.DWord);
            r.SetValue("OriginalJoystickKeyExisted_HKLM", 0, RegistryValueKind.DWord);
            r.SetValue("OriginalJoystickKeyExisted_HKCU", 0, RegistryValueKind.DWord);
        }

        // Another account's: its label is on the HKLM targets, and this
        // hive has no key for the identity.
        Plant("VID_BEEF&PID_0A05", "label of another account", thisHive: false);
        HMOemNameOverride.Set(0xBEEF, 0x0A05, "label of this account");
        bool hiveUntouched = Read(Registry.CurrentUser, $@"{joy}\VID_BEEF&PID_0A05", "OEMName") == null;
        bool machineSet = Read(Registry.LocalMachine, $@"{di}\VID_BEEF&PID_0A05\OEM", "OEM Name") == "label of this account";
        HMOemNameOverride.Clear(0xBEEF, 0x0A05);
        string? keptUserLabel; bool keptUserPart;
        using (var r = Registry.LocalMachine.OpenSubKey($@"{pending}\VID_BEEF&PID_0A05"))
        {
            keptUserLabel = r?.GetValue("WrittenUserLabel") as string;
            keptUserPart = r?.GetValue("OriginalJoystickKeyExisted_HKCU") is 0 && r.GetValue("OriginalKeyExisted") == null;
        }
        Record("2.0.0 records: another account's record gets no write to this hive from a Set or a Clear, and keeps that account's part and label",
               hiveUntouched && machineSet && keptUserPart && keptUserLabel == "label of another account"
               && Read(Registry.CurrentUser, $@"{joy}\VID_BEEF&PID_0A05", "OEMName") == null
               && !Inventory.KeyExists($@"HKLM\{di}\VID_BEEF&PID_0A05\OEM"),
               $"hive untouched after Set {hiveUntouched}, HKLM set {machineSet}, record kept {keptUserPart}, label {keptUserLabel ?? "none"}");
        Registry.LocalMachine.DeleteSubKeyTree($@"{pending}\VID_BEEF&PID_0A05", throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree($@"{di}\VID_BEEF&PID_0A05", throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree($@"{joy}\VID_BEEF&PID_0A05", throwOnMissingSubKey: false);

        // This account's: its label is on all three targets.
        Plant("VID_BEEF&PID_0A04", "label from 2.0.0", thisHive: true);
        HMOemNameOverride.Set(0xBEEF, 0x0A04, "label from 2.0.1");
        string? stamped;
        using (var r = Registry.LocalMachine.OpenSubKey($@"{pending}\VID_BEEF&PID_0A04")) stamped = r?.GetValue("UserSid") as string;
        bool hiveSet = Read(Registry.CurrentUser, $@"{joy}\VID_BEEF&PID_0A04", "OEMName") == "label from 2.0.1";
        var report = HMUninstall.Run();
        var entry = report.Entries.FirstOrDefault(e => e.Location.EndsWith("VID_BEEF&PID_0A04", StringComparison.OrdinalIgnoreCase));
        Record("2.0.0 records: this account's record is taken over by a Set and put back by a full removal",
               hiveSet && stamped == me && entry?.Outcome == HMUninstallOutcome.Removed
               && !Inventory.KeyExists($@"HKLM\{pending}\VID_BEEF&PID_0A04")
               && Read(Registry.CurrentUser, $@"{joy}\VID_BEEF&PID_0A04", "OEMName") == null,
               $"hive set {hiveSet}, account stamped {stamped == me}, removal said {entry?.ToString() ?? "nothing"}");
    }

    static bool IsLinkPath(string p)
    {
        try { return (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    /// <summary>The program the sandbox run kills: it installs, creates one
    /// controller of each kind the sandbox supports, sets an OEM name
    /// override, registers the OpenVR driver, says so, and waits. With
    /// <paramref name="one"/> it creates one plain HID controller and
    /// nothing else.</summary>
    static int Hold(string answer, bool one = false)
    {
        var lines = new List<string>();
        void Say() => SayTo(answer, lines);
        var held = new List<HMController>();
        try
        {
            var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            lines.Add("installed");
            if (one)
            {
                held.Add(ctx.CreateController(ctx.GetProfile("dualshock-4-v2")!, "uninstall-one"));
                lines.Add("created dualshock-4-v2");
                lines.Add("ready");
                Say();
                Thread.Sleep(Timeout.Infinite);
            }
            var profiles = new List<string> { "dualshock-4-v2", "xbox-360-wired", "xbox-elite-v2", "dualsense-composite" };
            foreach (string id in profiles)
            {
                try
                {
                    held.Add(ctx.CreateController(ctx.GetProfile(id)!, "uninstall-" + id));
                    lines.Add($"created {id}");
                }
                catch (Exception ex) { lines.Add($"failed {id} {ex.GetType().Name}: {ex.Message}"); }
            }
            try { HMOemNameOverride.Set(OemVid, OemPid, OemLabel); lines.Add("oem set"); }
            catch (Exception ex) { lines.Add($"oem failed {ex.Message}"); }
            try { lines.Add($"openvr registered {HMVR.EnsureDriverRegistered()}"); }
            catch (Exception ex) { lines.Add($"openvr failed {ex.Message}"); }
        }
        catch (Exception ex) { lines.Add($"failed {ex.GetType().Name}: {ex.Message}"); }
        lines.Add("ready");
        Say();
        Thread.Sleep(Timeout.Infinite);
        GC.KeepAlive(held);
        return 0;
    }

    /// <summary>Replace the answer file whole. The probe reads it with
    /// delete sharing, so the replace never meets its read, and a replace
    /// another reader still refuses is tried again.</summary>
    static void SayTo(string answer, List<string> lines)
    {
        string tmp = answer + ".tmp";
        File.WriteAllLines(tmp, lines);
        for (int i = 0; ; i++)
        {
            try { File.Move(tmp, answer, overwrite: true); return; }
            catch (Exception e) when (i < 100 && e is IOException or UnauthorizedAccessException) { Thread.Sleep(20); }
        }
    }

    static string[] ReadAnswer(string answer)
    {
        using var fs = new FileStream(answer, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>A program that starts while a removal runs. It constructs its
    /// context and loads its profiles before the removal starts, which
    /// claims nothing on a machine whose driver is current, then waits on
    /// <paramref name="eventName"/>. What the removal gate logs reaches its
    /// answer file at once, through the SDK's diagnostic hook. With
    /// <paramref name="mode"/> "install" it installs the driver and creates
    /// one controller. With "two" it creates two controllers from two
    /// threads at once, with no InstallDriver first.</summary>
    static int Late(string answer, string eventName, string mode)
    {
        var lines = new List<string>();
        var held = new List<HMController>();
        void Say(string line) { lock (lines) { lines.Add(line); SayTo(answer, lines); } }
        // The installing program also calls back into the gate from the
        // gate's own wait, on the same thread, as a diagnostic hook or a
        // message pumped on an STA thread can: the call must wait with it,
        // not hang.
        int reentered = mode == "install" ? 0 : 1;
        var tryEnsureOwner = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.MachineClaims")!
            .GetMethod("TryEnsureOwner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.DeviceOrchestrator")!
            .GetField("DiagTap", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, (Action<string>)(m =>
            {
                // A probe file problem never reaches the SDK.
                try
                {
                    if (!m.StartsWith("removal gate:", StringComparison.Ordinal)) return;
                    Say("gate " + m);
                    if (m.StartsWith("removal gate: another process is removing", StringComparison.Ordinal)
                        && Interlocked.Exchange(ref reentered, 1) == 0)
                    {
                        Say("reentering");
                        Say($"reentry returned {tryEnsureOwner.Invoke(null, null)}");
                    }
                }
                catch { }
            }));
        string lockName = AdminName("Uninstall");
        // Whether the removal's lock is free now, read without waiting.
        string LockState()
        {
            if (!Mutex.TryOpenExisting(lockName, out var mx)) return "free";
            using (mx)
            {
                bool free;
                try { free = mx.WaitOne(0); } catch (AbandonedMutexException) { free = true; }
                if (free) mx.ReleaseMutex();
                return free ? "free" : "held";
            }
        }
        try
        {
            var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            var profile = ctx.GetProfile("dualshock-4-v2")!;
            using (var go = new EventWaitHandle(false, EventResetMode.ManualReset, eventName))
            {
                Say("waiting");
                if (!go.WaitOne(600_000)) { Say("never signaled"); return 1; }
            }
            Say("signaled");
            if (mode == "two")
            {
                var threads = new List<Thread>();
                for (int i = 1; i <= 2; i++)
                {
                    int n = i;
                    var t = new Thread(() =>
                    {
                        try
                        {
                            var c = ctx.CreateController(profile, $"uninstall-two-{n}");
                            string state = LockState();
                            lock (held) held.Add(c);
                            Say($"created {n}");
                            Say($"lock {state} after created {n}");
                        }
                        catch (Exception ex) { Say($"failed {n} {ex.GetType().Name}: {ex.Message} at {ex.StackTrace?.Replace(Environment.NewLine, " <- ")}"); }
                    });
                    threads.Add(t);
                }
                foreach (var t in threads) t.Start();
                foreach (var t in threads) t.Join();
            }
            else
            {
                ctx.InstallDriver();
                string state = LockState();
                Say("installed");
                Say($"lock {state} after installed");
                held.Add(ctx.CreateController(profile, "uninstall-late"));
                state = LockState();
                Say("created dualshock-4-v2");
                Say($"lock {state} after created dualshock-4-v2");
            }
        }
        catch (Exception ex) { Say($"failed {ex.GetType().Name}: {ex.Message} at {ex.StackTrace?.Replace(Environment.NewLine, " <- ")}"); }
        Say("ready");
        Thread.Sleep(Timeout.Infinite);
        GC.KeepAlive(held);
        return 0;
    }

    /// <summary>A program that makes one machine-wide call and nothing
    /// else, then waits: "oem" sets an OEM name override, "vr" registers
    /// the OpenVR driver, "usbip" installs the USB/IP transport.</summary>
    static int OnlyOne(string answer, string what)
    {
        var lines = new List<string>();
        try
        {
            switch (what)
            {
                case "oem": HMOemNameOverride.Set(0xBEEF, 0x0A03, "HIDMaestro removal check, OEM only"); lines.Add("done"); break;
                case "vr": lines.Add($"done registered {HMVR.EnsureDriverRegistered()}"); break;
                case "usbip": HMContext.InstallUsbipBackend(); lines.Add("done"); break;
            }
        }
        catch (Exception ex) { lines.Add($"failed {ex.GetType().Name}: {ex.Message}"); }
        lines.Add("ready");
        SayTo(answer, lines);
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    static string[] WaitFor(string answer, Process p, string word, int timeoutMs) =>
        WaitUntil(answer, p, said => said.Contains(word), timeoutMs);

    static string[] WaitUntil(string answer, Process p, Func<string[], bool> done, int timeoutMs)
    {
        var wait = Stopwatch.StartNew();
        string[] said = Array.Empty<string>();
        while (wait.ElapsedMilliseconds < timeoutMs && !p.HasExited)
        {
            try { if (File.Exists(answer)) said = ReadAnswer(answer); } catch { }
            if (done(said)) break;
            Thread.Sleep(50);
        }
        return said;
    }

    static Process StartSelf(params string[] args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    /// <summary>A name in the SDK's admin namespace, by reflection.</summary>
    static string AdminName(string objectName) =>
        (string)typeof(HMUninstall).Assembly.GetType("HIDMaestro.Internal.AdminNamespace")!
            .GetMethod("Name", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { objectName })!;

    static bool Has(string[] said, string prefix) => said.Any(s => s.StartsWith(prefix, StringComparison.Ordinal));

    const string GateWaits = "gate removal gate: another process is removing HIDMaestro";
    const string GateWaited = "gate removal gate: waited";
    const string GateBehindThread = "gate removal gate: another thread of this program is at the gate";

    /// <summary>Programs that start while the tool removes HIDMaestro wait
    /// for the removal to end, do not block it, and then install again and
    /// keep their controllers. The probe holds the driver deploy lock, so
    /// the removal stops at its deploy-lock step holding its own lock, and
    /// releases it only once each late program's log shows it waiting at the
    /// gate: nothing here depends on how fast either side runs. One late
    /// program installs and creates. The other creates from two threads at
    /// once with no InstallDriver first, and the thread that loses the
    /// session sweep's race must wait at the gate too. Then a program that
    /// only set an OEM name override is running, and the removal is blocked
    /// by it.</summary>
    static void LateStart(string results)
    {
        string oneAnswer = Path.Combine(results, "hold-one.txt");
        using (var one = StartSelf("--hold-one", oneAnswer))
        {
            var said = WaitFor(oneAnswer, one, "ready", 600_000);
            Record("late: a program installs and creates a controller (positive control)", said.Contains("created dualshock-4-v2"), string.Join(" / ", said));
            try { one.Kill(); one.WaitForExit(30_000); } catch { }
        }
        Note($"late: before the removal, device installation {(CMP_WaitNoPendingInstallEvents(120_000) == 0 ? "settled" : "still pending after 120 s")}");
        var devices = Inventory.Take().HidMaestroDevnodes();
        Record("late: the killed program's devices are there (positive control)", devices.Count > 0, $"{devices.Count} device record(s)");

        string aAnswer = Path.Combine(results, "late-install.txt"), bAnswer = Path.Combine(results, "late-two.txt");
        string eventName = "HIDMaestroUninstallCheckLate_" + Guid.NewGuid().ToString("N");
        using var go = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var a = StartSelf("--late", aAnswer, eventName, "install");
        using var b = StartSelf("--late", bAnswer, eventName, "two");
        var aReady = WaitFor(aAnswer, a, "waiting", 300_000);
        var bReady = WaitFor(bAnswer, b, "waiting", 300_000);
        Record("late: both late programs are up before the removal, with nothing claimed (positive control)",
               aReady.Contains("waiting") && bReady.Contains("waiting") && !Has(aReady, "gate ") && !Has(bReady, "gate ")
               && !OwnerPids().Overlaps(new[] { a.Id, b.Id }),
               $"{string.Join(" / ", aReady)} | {string.Join(" / ", bReady)}; owner records of {string.Join(", ", OwnerPids())}, late programs {a.Id}, {b.Id}");

        string deployName = AdminName((string)typeof(HMUninstall).Assembly.GetType("HIDMaestro.Internal.DriverDeploy")!
            .GetField("MutexObject", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!);
        string uninstallName = AdminName("Uninstall");
        var deploy = new Mutex(false, deployName);
        bool deployHeld = false;
        try { deployHeld = deploy.WaitOne(60_000); } catch (AbandonedMutexException) { deployHeld = true; }
        Record("late: the probe holds the driver deploy lock (positive control)", deployHeld);

        var tpsi = new ProcessStartInfo(SandboxTool)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        tpsi.ArgumentList.Add("--yes");
        tpsi.ArgumentList.Add("--json");
        using var tool = Process.Start(tpsi)!;
        var stdout = tool.StandardOutput.ReadToEndAsync();
        var stderr = tool.StandardError.ReadToEndAsync();
        // The removal takes its own lock first and then stops at the deploy
        // lock, so the probe finds its lock held for as long as it likes.
        var sw = Stopwatch.StartNew();
        bool removalHolds = false;
        while (!tool.HasExited && sw.ElapsedMilliseconds < 300_000)
        {
            if (Mutex.TryOpenExisting(uninstallName, out var mx))
                using (mx)
                {
                    bool free;
                    try { free = mx.WaitOne(0); } catch (AbandonedMutexException) { free = true; }
                    if (free) mx.ReleaseMutex();
                    else { removalHolds = true; break; }
                }
            Thread.Sleep(20);
        }
        Record("late: the removal holds its lock and waits at the deploy lock (positive control)", removalHolds && !tool.HasExited);

        go.Set();
        var aWait = WaitUntil(aAnswer, a, s => Has(s, GateWaits) && s.Contains("reentering"), 120_000);
        var bWait = WaitUntil(bAnswer, b, s => Has(s, GateWaits) && Has(s, GateBehindThread), 120_000);
        bool stillRunning = !tool.HasExited;
        // Read again right before the lock goes: nothing of either program
        // may have got past the gate, and neither may hold a claim.
        aWait = WaitUntil(aAnswer, a, _ => true, 1_000);
        bWait = WaitUntil(bAnswer, b, _ => true, 1_000);
        var claimedWhileWaiting = OwnerPids();
        if (deployHeld) deploy.ReleaseMutex();
        deploy.Dispose();
        bool NothingPast(string[] s) => !Has(s, "installed") && !Has(s, "created") && !Has(s, "reentry returned") && !Has(s, "lock ");
        Record("late: while the removal holds its lock, neither program has installed, created or claimed",
               NothingPast(aWait) && NothingPast(bWait) && !claimedWhileWaiting.Overlaps(new[] { a.Id, b.Id }),
               $"{string.Join(" / ", aWait)} | {string.Join(" / ", bWait)}; owner records of {string.Join(", ", claimedWhileWaiting)}");
        Record("late: a program that installs waits at the gate while the removal runs", Has(aWait, GateWaits) && stillRunning,
               string.Join(" / ", aWait));
        Record("late: a program creating from two threads waits at the gate on both, the one that lost the session sweep's race behind the other",
               Has(bWait, GateWaits) && Has(bWait, GateBehindThread) && stillRunning, string.Join(" / ", bWait));

        bool exited = tool.WaitForExit(1_800_000);
        if (!exited) { try { tool.Kill(entireProcessTree: true); } catch { } }
        var report = Report.Parse(stdout.Result + stderr.Result);
        Record("late: the removal is not blocked by them and removes HIDMaestro",
               exited && tool.ExitCode is 0 or 3 && report?.Status is "Removed" or "RemovedRestartPending",
               $"exit {(exited ? tool.ExitCode : -1)}, status {report?.Status}, programs {string.Join(", ", report?.Programs ?? new List<int>())}: " +
               string.Join("; ", report?.Entries.Where(e => e.Outcome is "Failed").Take(4) ?? Enumerable.Empty<Entry>()));

        var aSaid = WaitFor(aAnswer, a, "ready", 900_000);
        var bSaid = WaitFor(bAnswer, b, "ready", 900_000);
        foreach (string s in aSaid.Concat(bSaid).Where(s => s.StartsWith("failed", StringComparison.Ordinal))) Note($"late: {s}");
        var after = Inventory.Take();
        string[] lockLines = aSaid.Concat(bSaid).Where(s => s.StartsWith("lock ", StringComparison.Ordinal)).ToArray();
        Record("late: each install and create returned with the removal's lock free",
               lockLines.Length == 4 && lockLines.All(s => s.StartsWith("lock free ", StringComparison.Ordinal)), string.Join(" / ", lockLines));
        Record("late: a call back into the gate from the gate's own wait, on the same thread, waits with it and returns",
               aSaid.Contains("reentry returned True"), string.Join(" / ", aSaid.Where(s => s.StartsWith("reentr", StringComparison.Ordinal))));
        Record("late: after the removal they pass the gate, install again and keep their controllers",
               Has(aSaid, GateWaited) && Has(bSaid, GateWaited) && aSaid.Contains("created dualshock-4-v2")
               && bSaid.Contains("created 1") && bSaid.Contains("created 2") && !a.HasExited && !b.HasExited
               && after.Packages.Count > 0 && after.HidMaestroDevnodes().Count > 0,
               $"{string.Join(" / ", aSaid)} | {string.Join(" / ", bSaid)}; {after.Packages.Count} package(s), {after.HidMaestroDevnodes().Count} device record(s)");
        try { a.Kill(); a.WaitForExit(30_000); } catch { }
        try { b.Kill(); b.WaitForExit(30_000); } catch { }

        // A program that has made one machine-wide call holds its owner
        // claim, so a removal sees it and is blocked by it. The stand-in
        // SteamVR from the first removal stays for the OpenVR case.
        bool steamVr = File.Exists(@"C:\SteamVR\bin\win64\vrpathreg.exe");
        foreach (var (what, label) in new[] { ("oem", "set an OEM name override"), ("vr", "registered the OpenVR driver"), ("usbip", "installed the USB/IP transport") })
        {
            if (what == "vr" && !steamVr) { Note("late: SteamVR's stand-in is missing, so the OpenVR registration case did not run"); continue; }
            string answer = Path.Combine(results, $"only-{what}.txt");
            using var only = StartSelf("--only", answer, what);
            var said = WaitFor(answer, only, "ready", 600_000);
            var (oexit, oout) = Run(SandboxTool, 1_800_000, "--yes", "--json");
            var oreport = Report.Parse(oout);
            Record($"late: a running program that only {label} blocks a removal and is named",
                   oexit == 2 && oreport?.Status == "Blocked" && oreport.Programs.Contains(only.Id),
                   $"{string.Join(" / ", said)}; exit {oexit}, status {oreport?.Status}, programs {string.Join(", ", oreport?.Programs ?? new List<int>())}, holder {only.Id}");
            try { only.Kill(); only.WaitForExit(30_000); } catch { }
        }

        // Without SteamVR the OpenVR registration writes nothing and claims
        // nothing, so the removal that cleans up runs beside it unblocked.
        bool moved = false;
        try { if (steamVr) { Directory.Move(@"C:\SteamVR", @"C:\SteamVR.away"); moved = true; } }
        catch (Exception ex) { Note($"late: the SteamVR stand-in could not be moved aside: {ex.Message}"); }
        string noVrAnswer = Path.Combine(results, "only-vr-absent.txt");
        using (var noVr = StartSelf("--only", noVrAnswer, "vr"))
        {
            var said = WaitFor(noVrAnswer, noVr, "ready", 600_000);
            var (cexit, cout) = Run(SandboxTool, 1_800_000, "--yes", "--json");
            var creport = Report.Parse(cout);
            Record("late: without SteamVR a program that asks for the OpenVR registration claims nothing and does not block a removal",
                   said.Contains("done registered False") && cexit is 0 or 3 && creport?.Programs.Contains(noVr.Id) != true,
                   $"{string.Join(" / ", said)}; exit {cexit}, status {creport?.Status}");
            try { noVr.Kill(); noVr.WaitForExit(30_000); } catch { }
            var clean = Inventory.Take();
            Record("late: a removal afterward leaves the machine clean", cexit is 0 or 3 && clean.Packages.Count == 0 && clean.HidMaestroDevnodes().Count == 0
                   && !Inventory.KeyExists(@"HKLM\SOFTWARE\HIDMaestroOemOverrides"),
                   $"exit {cexit}, status {creport?.Status}");
        }
        try { if (moved) Directory.Move(@"C:\SteamVR.away", @"C:\SteamVR"); }
        catch (Exception ex) { Note($"late: the SteamVR stand-in could not be put back: {ex.Message}"); }
    }

    /// <summary>The process IDs the owner records name.</summary>
    static HashSet<int> OwnerPids()
    {
        var pids = new HashSet<int>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroOwners");
        if (root == null) return pids;
        foreach (string id in root.GetSubKeyNames())
        {
            using var rec = root.OpenSubKey(id);
            if (rec?.GetValue("Pid") is int pid) pids.Add(pid);
        }
        return pids;
    }

    /// <summary>A junction planted at %ProgramData%\HIDMaestro goes as a
    /// link, and the folder it points to keeps its file.</summary>
    static void ProgramDataJunction()
    {
        string pd = Inventory.ProgramDataFolder;
        const string bait = @"C:\uninstall_bait_programdata";
        Directory.CreateDirectory(bait);
        File.WriteAllText(Path.Combine(bait, "keep.txt"), "uninstall_check");
        if (!Directory.Exists(pd)) Run("cmd.exe", 30_000, "/c", "mklink", "/J", pd, bait);
        Record("links: %ProgramData%\\HIDMaestro is a junction (positive control)", IsLinkPath(pd), pd);

        var r = HMUninstall.Run();
        var pdEntries = r.Entries.Where(e => e.Location.Equals(pd, StringComparison.OrdinalIgnoreCase)).ToList();
        Record("links: the junction at %ProgramData%\\HIDMaestro goes as a link, and its target keeps its file",
               !Directory.Exists(pd) && !File.Exists(pd) && File.Exists(Path.Combine(bait, "keep.txt"))
               && pdEntries.Count > 0 && pdEntries.All(e => e.Outcome == HMUninstallOutcome.Removed),
               string.Join("; ", pdEntries));
        try { Directory.Delete(bait, recursive: true); } catch { }
    }

    /// <summary>usbip-win2's installer's desktop link lives on the all-users
    /// desktop, under the Public profile. A removal that includes usbip-win2
    /// deletes it from there rather than refusing a path under no anchor.
    /// Runs on the clean sandbox, before anything is installed.</summary>
    static void PublicLink()
    {
        string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "USBip.lnk");
        File.WriteAllBytes(link, new byte[] { 0x4C, 0, 0, 0 });
        Record("public: the all-users desktop link is planted (positive control)", File.Exists(link), link);
        var r = HMUninstall.Run(new HMUninstallOptions { IncludeUsbip = true });
        var linkEntry = r.Entries.FirstOrDefault(e => e.Location.Equals(link, StringComparison.OrdinalIgnoreCase));
        Record("public: the all-users desktop link is removed from the Public profile, not refused",
               !File.Exists(link) && linkEntry?.Outcome == HMUninstallOutcome.Removed,
               $"{r.Status}: {linkEntry?.ToString() ?? "no entry for it"}");
        try { if (File.Exists(link)) File.Delete(link); } catch { }
    }

    // ── The tool's JSON report ─────────────────────────────────────────

    sealed record Entry(string Kind, string Location, string Outcome, string? Reason)
    {
        public override string ToString() => $"{Outcome} {Kind} {Location}{(Reason != null ? ": " + Reason : "")}";
    }

    sealed class Report
    {
        public bool Preview;
        public string Status = "";
        public int ExitCode;
        public List<int> Programs = new();
        public List<Entry> Entries = new();

        public static Report? Parse(string output)
        {
            int at = output.IndexOf('{');
            if (at < 0) return null;
            try
            {
                using var d = JsonDocument.Parse(output[at..]);
                var root = d.RootElement;
                if (root.GetProperty("schemaVersion").GetInt32() != 1) return null;
                var r = new Report
                {
                    Preview = root.GetProperty("preview").GetBoolean(),
                    Status = root.GetProperty("status").GetString() ?? "",
                    ExitCode = root.GetProperty("exitCode").GetInt32(),
                };
                foreach (var p in root.GetProperty("runningPrograms").EnumerateArray())
                    r.Programs.Add(p.GetProperty("processId").GetInt32());
                foreach (var e in root.GetProperty("entries").EnumerateArray())
                    r.Entries.Add(new Entry(e.GetProperty("kind").GetString()!, e.GetProperty("location").GetString()!,
                        e.GetProperty("outcome").GetString()!, e.GetProperty("reason").ValueKind == JsonValueKind.Null ? null : e.GetProperty("reason").GetString()));
                return r;
            }
            catch { return null; }
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────

    // cfgmgr32.h: CM_WaitNoPendingInstallEvents is this export. 0 when no
    // device installation is pending, WAIT_TIMEOUT when one still was.
    [System.Runtime.InteropServices.DllImport("cfgmgr32.dll")]
    static extern uint CMP_WaitNoPendingInstallEvents(uint timeoutMs);

    static string Tail(string s) => s.Length <= 400 ? s.Trim() : s[^400..].Trim();

    static (int Exit, string Output) Run(string exe, int timeoutMs, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (-1, "timed out");
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    static string? JsonString(string json, string name)
    {
        try
        {
            using var d = JsonDocument.Parse(json[json.IndexOf('{')..]);
            return d.RootElement.TryGetProperty(name, out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    static int? JsonInt(string json, string name)
    {
        try
        {
            using var d = JsonDocument.Parse(json[json.IndexOf('{')..]);
            return d.RootElement.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : null;
        }
        catch { return null; }
    }

    static string Xml(string s) => System.Security.SecurityElement.Escape(s);

    static string? FindOnPath(string exe)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string p = Path.Combine(dir.Trim(), exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        string apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", exe);
        return File.Exists(apps) ? apps : null;
    }
}
