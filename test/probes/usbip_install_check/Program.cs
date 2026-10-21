// usbip-win2's host controller on a clean machine, in Windows Sandbox: the
// SDK's install with no vendor installer, the composite with no root-hub
// filter, and the usbip-win2 part of a full removal.
//
//   A. clean    A composite create on a machine without usbip-win2 installs
//               the host controller from the package HIDMaestro.Core carries
//               (C's creates install it when A runs with B and C, and A's
//               checks read that install). One ROOT\USBIP_WIN2\UDE devnode
//               with HIDMaestro's stamp after usbip-win2's id, the package
//               in the driver store, and an install record naming both. No
//               usbip2_filter package or service, no Apps entry, program
//               folder, scheduled task, Start menu folder or shortcut, and no
//               KMDF debug values. The composite enumerates and carries input
//               with no filter on its root hub.
//   B. record   A machine holds usbip-win2's uninstall entry, pointing at an
//               uninstaller that exists, but not its driver package. The
//               install goes ahead from the SDK's own package, the planted
//               uninstaller never runs, and the entry is left as it was.
//   C. together Two programs create their first composite at the same
//               moment on a clean machine. One installs, the other waits for
//               it under the transport lock: one host controller, and both
//               composites appear.
//   E. e2e      With A's install, S45's probe (usbip_e2e_check: HID input,
//               output, feature, audio out and the microphone) and S65's
//               (switch2_composite_check: a HID and WinUSB composite, its bulk
//               commands and rumble) run inside the sandbox, so every check
//               they make runs with no filter on the root hub.
//   R. remove   The usbip-win2 part of a full removal (UsbipRemoval.Remove):
//               a preview that changes nothing, a removal of an install the
//               record proves, a reinstall after it, an install with no
//               record (kept, then taken when usbip-win2 is included, refused
//               while another program that uses usbip-win2 is installed), and
//               a host controller that carried a composite since Windows
//               started (kept until a restart, its persona's records gone).
//   N. nostream A, with the composite's profile loaded from a file without
//               its idle stream ("stream"), the one change in what the
//               device sends between v1.12.0 and 2.0.0. Runs only when named.
//   V. verifier The USB/IP input path with KMDF's verifier values on and off:
//               SubmitState to the ReadFile that shows it, on a host
//               controller restarted unused after the values were set or
//               left out, in sandboxes that alternate on, off, on, off.
//               Runs only when named.
//
// --offline checks, on this machine with no sandbox, no device and no
// elevation, the parts of A and N that can be: N's profile differs from the
// catalog's only in its stream, and the devnode chain, arrival times and
// Kernel-PnP events read back from this machine's own HID devices.
//
// --preview, on this machine and elevated, prints what the usbip-win2 part
// of a full removal would do here, with usbip-win2 included and not, and
// checks that neither preview changed anything a removal acts on.
//
// Scenarios A and N also note what Windows did to the composite's devnodes
// while the probe read it (Pnp.cs): each node from the HID interface up to
// the host controller with its last arrival and removal, read when the
// interface appears and after each read window, whether device installation
// was pending, and the Kernel-PnP configuration events that name them.
// Notes print as [NOTE] and are not checks.
//
// The host role starts a sandbox with wsb.exe, which opens no window, runs
// this program inside it as SYSTEM, and reports what it wrote back. B, C, A
// and E share one sandbox: B plants its entry, C installs on that machine,
// A's checks read C's install and a composite created over it, and E runs
// the two probes over the same install. R runs in a sandbox of its own, and
// so does each of N and V's runs. Inside, the programs run as self-contained
// copies published beside this one (sandbox\, sandbox-e2e\ and
// sandbox-switch2\), since the sandbox has no .NET runtime. Run with
// /VERYSILENT as its first argument, it is the planted uninstaller of
// scenario B and only leaves a marker.
//
// Requires elevation for nothing on the host. Exit 0 PASS, 1 FAIL, 2 when
// Windows Sandbox is not available.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32;

internal static partial class Program
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{199505b0-b93d-4521-a8c7-897818e0205a}_is1";
    const string RecordKey = @"SOFTWARE\HIDMaestroUsbip";
    const string UdeHardwareId = @"ROOT\USBIP_WIN2\UDE";
    const string StampHardwareId = @"ROOT\HIDMAESTRO_UDE";
    const string WdfKey = @"SYSTEM\CurrentControlSet\Services\usbip2_ude\Parameters\Wdf";
    const string DetachTask = "USBip Detach All On Reboot Or Shutdown";
    const string Marker = @"C:\results\uninstaller-ran.marker";

    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static int Main(string[] args)
    {
        // The planted uninstaller of scenario B. Inno Setup's PrepareToInstall
        // runs it with /VERYSILENT /NORESTART /SUPPRESSMSGBOXES.
        if (args.Length > 0 && args[0].Equals("/VERYSILENT", StringComparison.OrdinalIgnoreCase))
        {
            try { File.WriteAllText(Marker, string.Join(' ', args)); } catch { }
            return 0;
        }
        if (args.Length >= 3 && args[0] == "--in-sandbox")
        {
            bool diag = args.Length >= 4 && args[3] == "diag";
            if (diag) DiagLog.Start(Path.Combine(args[2], "main-diag.log"));
            if (!diag) return InSandbox(args[1], args[2]);
            using (DiagLog.SampleInstallers(Path.Combine(args[2], "processes.log")))
            {
                int code = InSandbox(args[1], args[2]);
                DiagLog.Collect(args[2]);
                return code;
            }
        }
        if (args.Length >= 4 && args[0] == "--create-composite")
        {
            if (args.Length >= 5 && args[4] == "diag")
                DiagLog.Start(Path.Combine(Path.GetDirectoryName(args[2])!, $"child{args[3]}-diag.log"));
            return CreateComposite(args[1], args[2], args[3]);
        }
        if (args.Length > 0 && args[0] == "--offline")
            return Offline();
        if (args.Length > 0 && args[0] == "--preview")
            return Preview();
        int at = Array.IndexOf(args, "--diag");
        if (at >= 0 && at + 1 < args.Length) s_diagDir = Path.GetFullPath(args[at + 1]);
        return Host(args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0].ToUpperInvariant() : "ABCER");
    }

    // ── Host ───────────────────────────────────────────────────────────

    // --diag <dir>: each program inside the sandbox runs with HIDMAESTRO_DIAG=1
    // and writes the SDK's diagnostic lines to a log of its own. Those logs, a
    // once-a-second list of the install's processes and Windows' setupapi
    // logs are copied to <dir> (DiagLog).
    static string? s_diagDir;

    static int Host(string only)
    {
        Console.WriteLine("=== usbip-win2's host controller on a clean machine ===");
        string? wsb = FindOnPath("wsb.exe");
        if (wsb == null)
        {
            Console.WriteLine("  [SKIP] Windows Sandbox (wsb.exe) is not available on this machine.");
            return 2;
        }
        string publish = Path.Combine(AppContext.BaseDirectory, "sandbox");
        if (!File.Exists(Path.Combine(publish, "UsbipInstallCheck.exe")))
        {
            Check("the self-contained copy for the sandbox was published", false, publish);
            return 1;
        }

        if (only.Contains('A') || only.Contains('B') || only.Contains('C') || only.Contains('E'))
        {
            Console.WriteLine();
            Console.WriteLine("B, C, A, E. an uninstall entry without the driver package, two programs' first composites at the same moment, a clean install's checks, then S45's and S65's probes over it");
            try { RunInSandbox(wsb, publish, only.Contains('E') ? "all" : "all-no-e2e"); }
            catch (Exception ex) { Check("B, C, A ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
        }
        if (only.Contains('R'))
        {
            Console.WriteLine();
            Console.WriteLine("R. the usbip-win2 part of a full removal");
            try { RunInSandbox(wsb, publish, "remove"); }
            catch (Exception ex) { Check("R ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
        }
        if (only.Contains('N'))
        {
            Console.WriteLine();
            Console.WriteLine("N. a clean machine, the composite without its idle stream");
            try { RunInSandbox(wsb, publish, "nostream"); }
            catch (Exception ex) { Check("N ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
        }
        if (only.Contains('V'))
        {
            Console.WriteLine();
            Console.WriteLine("V. the USB/IP input path with KMDF's verifier values on and off");
            foreach (string mode in new[] { "verifier-on", "verifier-off", "verifier-on", "verifier-off" })
            {
                try { RunInSandbox(wsb, publish, mode); }
                catch (Exception ex) { Check($"{mode} ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static readonly string SandboxIdFile = Path.Combine(Path.GetTempPath(), "HIDMaestro", "usbip_install_check.sandbox");
    static readonly string UninstallCheckIdFile = Path.Combine(Path.GetTempPath(), "HIDMaestro_uninstall_check.sandbox");

    static void RunInSandbox(string wsb, string publish, string scenario)
    {
        string results = Path.Combine(Path.GetTempPath(), $"HIDMaestro_sandbox_{scenario}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(results);
        string? id = null;
        double startS = double.NaN, readyS = double.NaN, runS = double.NaN;
        int tries = 0;
        try
        {
            string mapped =
                $"<MappedFolder><HostFolder>{Xml(publish)}</HostFolder><SandboxFolder>C:\\probe</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>" +
                $"<MappedFolder><HostFolder>{Xml(results)}</HostFolder><SandboxFolder>C:\\results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>";
            // S45's and S65's probes, for E.
            foreach (var (dir, inside) in new[] { ("sandbox-e2e", "C:\\probe-e2e"), ("sandbox-switch2", "C:\\probe-switch2") })
            {
                string host = Path.Combine(AppContext.BaseDirectory, dir);
                if (scenario == "all" && Directory.Exists(host))
                    mapped += $"<MappedFolder><HostFolder>{Xml(host)}</HostFolder><SandboxFolder>{inside}</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>";
            }
            string config = "<Configuration><Networking>Disable</Networking><MappedFolders>" + mapped + "</MappedFolders></Configuration>";
            // Windows Sandbox runs one instance at a time, and a run killed
            // before its finally leaves its sandbox running, so the next
            // start fails CO_E_APPSINGLEUSE. Each run records the sandbox it
            // started and stops the one an earlier run left: this probe's,
            // or uninstall_check's (S101), whose sandbox the battery starts
            // only after this one's is collected, so a recorded one is left
            // over. No other sandbox is touched.
            foreach (string idFile in new[] { SandboxIdFile, UninstallCheckIdFile })
            {
                if (!File.Exists(idFile)) continue;
                string left = File.ReadAllText(idFile).Trim();
                if (left.Length > 0) Run(wsb, 120_000, "stop", "--id", left);
                File.Delete(idFile);
            }
            var step = Stopwatch.StartNew();
            var started = Run(wsb, 120_000, "start", "--config", config, "--raw");
            id = JsonString(started.Output, "Id");
            if (id == null) { Check($"{scenario}: a sandbox starts", false, started.Output.Trim()); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(SandboxIdFile)!);
            File.WriteAllText(SandboxIdFile, id);
            startS = step.Elapsed.TotalSeconds;

            // The first command can arrive before the sandbox has finished
            // starting, so wait for a trivial one to succeed. Each refused
            // try is a process of its own, and the next follows 250 ms on.
            step.Restart();
            var sw = Stopwatch.StartNew();
            bool ready = false;
            while (!ready && sw.ElapsedMilliseconds < 180_000)
            {
                tries++;
                var probe = Run(wsb, 60_000, "exec", "--id", id, "-c", "cmd.exe /c exit 0", "-r", "System", "--raw");
                ready = JsonInt(probe.Output, "ExitCode") == 0;
                if (!ready) Thread.Sleep(250);
            }
            readyS = step.Elapsed.TotalSeconds;
            if (!ready) { Check($"{scenario}: the sandbox accepts a command", false); return; }

            step.Restart();
            var ran = Run(wsb, 1_800_000, "exec", "--id", id, "-c",
                $"C:\\probe\\UsbipInstallCheck.exe --in-sandbox {scenario} C:\\results{(s_diagDir != null ? " diag" : "")}", "-r", "System", "--raw");
            runS = step.Elapsed.TotalSeconds;
            string file = Path.Combine(results, "results.txt");
            if (!File.Exists(file)) { Check($"{scenario}: the run inside the sandbox reports back", false, ran.Output.Trim()); return; }
            foreach (string line in File.ReadAllLines(file))
            {
                if (line == "DONE") continue;
                int sp = line.IndexOf(' ');
                if (sp < 0) continue;
                string verdict = line[..sp], rest = line[(sp + 1)..];
                if (verdict == "NOTE") { Console.WriteLine($"  [NOTE] {rest}"); continue; }
                int bar = rest.IndexOf(" | ", StringComparison.Ordinal);
                Check(bar < 0 ? rest : rest[..bar], verdict == "PASS", bar < 0 ? "" : rest[(bar + 3)..]);
            }
            if (!File.ReadAllLines(file).Contains("DONE"))
                Check($"{scenario}: the run inside the sandbox finished", false, ran.Output.Trim());
            if (scenario == "all") EndToEnd(wsb, id, results);
        }
        finally
        {
            var stop = Stopwatch.StartNew();
            if (id != null) Run(wsb, 120_000, "stop", "--id", id);
            try { File.Delete(SandboxIdFile); } catch { }
            // What Windows Sandbox itself takes, apart from the probe's run
            // inside it.
            Console.WriteLine(FormattableString.Invariant(
                $"  [NOTE] {scenario}: the sandbox started in {startS:F1} s, took its first command {readyS:F1} s later after {tries} tries, ran the probe in {runS:F1} s and stopped in {stop.Elapsed.TotalSeconds:F1} s"));
            if (s_diagDir != null)
            {
                string kept = Path.Combine(s_diagDir, $"{scenario}-{DateTime.Now:yyyyMMdd-HHmmss}");
                try
                {
                    foreach (string f in Directory.GetFiles(results, "*", SearchOption.AllDirectories))
                    {
                        string to = Path.Combine(kept, Path.GetRelativePath(results, f));
                        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                        File.Copy(f, to, true);
                    }
                    Console.WriteLine($"  [NOTE] {scenario}: diagnostic logs in {kept}");
                }
                catch (Exception ex) { Console.WriteLine($"  [NOTE] {scenario}: diagnostic logs not copied: {ex.GetType().Name}: {ex.Message}"); }
            }
            try { Directory.Delete(results, true); } catch { }
        }
    }

    // ── Inside the sandbox ─────────────────────────────────────────────

    static readonly List<string> s_lines = new();

    static void Note(string text) => s_lines.Add("NOTE " + text.Replace('\r', ' ').Replace('\n', ' '));

    static void Record(string name, bool ok, string detail = "")
    {
        detail = detail.Replace('\r', ' ').Replace('\n', ' ');
        s_lines.Add($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? " | " + detail : "")}");
    }

    static int InSandbox(string scenario, string results)
    {
        try
        {
            Record($"{scenario}: the machine starts without usbip-win2",
                   UninstallerPath() == null && !DriverPackagePresent("usbip2_ude") && !Directory.Exists(InstallDir)
                   && Pnp.RootDevnodes(UdeHardwareId).Count == 0);

            switch (scenario)
            {
                case "all":
                case "all-no-e2e":
                    All(results);
                    break;
                case "remove":
                    Removal();
                    break;
                case "verifier-on":
                case "verifier-off":
                    Measure(scenario == "verifier-on");
                    break;
                default:
                {
                    using var ctx = new HMContext();
                    ctx.LoadDefaultProfiles();
                    ctx.InstallDriver();
                    HMProfile profile = scenario == "nostream" ? WithoutStream(ctx) : ctx.GetProfile("dualsense-composite")!;
                    HMController? c = null;
                    Exception? error = null;
                    DateTime createdFrom = DateTime.UtcNow;
                    var made = Stopwatch.StartNew();
                    DiagLog.Note($"probe: {scenario} create starts");
                    try { c = ctx.CreateController(profile, "sandbox-composite"); }
                    catch (Exception ex) { error = ex; }
                    DiagLog.Note($"probe: {scenario} create returned after {made.ElapsedMilliseconds} ms{(error == null ? "" : ": " + error)}");
                    Note(FormattableString.Invariant($"{scenario}: the create took {made.Elapsed.TotalSeconds:F1} s"));
                    CleanChecks(scenario, c, error, createdFrom);
                    c?.Dispose();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Record($"{scenario}: the run ran to the end", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        s_lines.Add("DONE");
        File.WriteAllLines(Path.Combine(results, "results.txt"), s_lines);
        return s_lines.Any(l => l.StartsWith("FAIL", StringComparison.Ordinal)) ? 1 : 0;
    }

    /// <summary>The catalog's dualsense-composite with its "stream" taken
    /// out, under an id of its own, loaded from a file the way a program
    /// loads its own profiles.</summary>
    static HMProfile WithoutStream(HMContext ctx)
    {
        var asm = typeof(HMContext).Assembly;
        string name = asm.GetManifestResourceNames().Single(n =>
            n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal) &&
            (n.EndsWith("\\dualsense-composite.json", StringComparison.OrdinalIgnoreCase) ||
             n.EndsWith("/dualsense-composite.json", StringComparison.OrdinalIgnoreCase) ||
             n.EndsWith(".dualsense-composite.json", StringComparison.OrdinalIgnoreCase)));
        using var s = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(s);
        var json = System.Text.Json.Nodes.JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        json.Remove("stream");
        json["id"] = "dualsense-composite-nostream";
        string dir = Path.Combine(Path.GetTempPath(), "HIDMaestro_nostream");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dualsense-composite-nostream.json"), json.ToJsonString());
        ctx.LoadProfilesFromDirectory(dir);
        return ctx.GetProfile("dualsense-composite-nostream")
            ?? throw new InvalidOperationException("the profile without its stream did not load");
    }

    /// <summary>What the install leaves, read directly: the host controller,
    /// its package, the install record, and none of what usbip-win2's own
    /// installer adds.</summary>
    static void InstallChecks(string p)
    {
        var controllers = Pnp.RootDevnodes(UdeHardwareId);
        Record($"{p} one host controller devnode, present", controllers.Count == 1 && controllers[0].Present,
               string.Join(", ", controllers.Select(c => $"{c.Id} ({(c.Present ? "present" : "not present")})")));
        if (controllers.Count == 0) return;
        var hc = controllers[0];
        Record($"{p} its hardware ids are usbip-win2's, then HIDMaestro's stamp",
               hc.HardwareIds.Length == 2 && hc.HardwareIds[0].Equals(UdeHardwareId, StringComparison.OrdinalIgnoreCase)
               && hc.HardwareIds[1].Equals(StampHardwareId, StringComparison.OrdinalIgnoreCase),
               string.Join(";", hc.HardwareIds));
        string? inf = Pnp.DriverInf(hc.Id);
        Record($"{p} its driver is a usbip2_ude.inf package in the driver store", inf != null && InfOriginal(inf) == "usbip2_ude.inf", inf ?? "no driver");

        using (var k = Registry.LocalMachine.OpenSubKey(RecordKey))
        {
            Record($"{p} the install record names that devnode and package, added by the install, 0.9.8.1",
                   k != null && (k.GetValue("Instance") as string)?.Equals(hc.Id, StringComparison.OrdinalIgnoreCase) == true
                   && (k.GetValue("OemInf") as string)?.Equals(inf, StringComparison.OrdinalIgnoreCase) == true
                   && k.GetValue("PackageAdded") is int added && added == 1 && k.GetValue("Version") as string == "0.9.8.1"
                   && k.GetValue("InstalledUtc") is string when && DateTime.TryParse(when, out _),
                   k == null ? "no record" : string.Join(", ", k.GetValueNames().Select(n => $"{n}={k.GetValue(n)}")));
        }

        // The filter: neither its package nor its service, so no root hub,
        // the host controller's own included, has it in its stack.
        Record($"{p} no usbip2_filter package in the driver store", !DriverPackagePresent("usbip2_filter"));
        Record($"{p} no usbip2_filter service", !KeyExists(@"SYSTEM\CurrentControlSet\Services\usbip2_filter"));

        // What usbip-win2's installer adds and this install does not.
        Record($"{p} no usbip-win2 Apps entry", UninstallerPath() == null && !KeyExists(UninstallKey));
        Record($"{p} no program folder", !Directory.Exists(InstallDir), InstallDir);
        string[] menus =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "USBip"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "USBip"),
        };
        Record($"{p} no Start menu folder", !menus.Any(Directory.Exists), string.Join(", ", menus.Where(Directory.Exists)));
        string[] shortcuts =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "USBip.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "USBip.lnk"),
        };
        Record($"{p} no desktop shortcut", !shortcuts.Any(File.Exists), string.Join(", ", shortcuts.Where(File.Exists)));
        Record($"{p} no detach task", Run(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), 30_000, "/query", "/tn", DetachTask).Exit != 0);
        Record($"{p} no KMDF debug values for usbip2_ude", WdfDebugValues().Count == 0, string.Join(", ", WdfDebugValues()));
    }

    static void CleanChecks(string scenario, HMController? c, Exception? error, DateTime createdFrom)
    {
        string p = scenario + ":";
        Record($"{p} the composite creates", c != null, error == null ? "" : $"{error.GetType().Name}: {error.Message}");
        InstallChecks(p);

        // The composite works end to end.
        if (c == null) return;
        s_controller = c;
        var paths = new List<string>();
        var sw = Stopwatch.StartNew();
        while (paths.Count == 0 && sw.ElapsedMilliseconds < 30_000)
        {
            paths = Hid.Paths(0x054C, 0x0CE6);
            if (paths.Count == 0) Thread.Sleep(250);
        }
        Record($"{p} the composite's HID interface appears", paths.Count > 0, $"{paths.Count} interface(s)");
        if (paths.Count == 0) return;
        var appeared = DateTime.UtcNow;
        var chain = Pnp.Chain(paths[0]);
        NoteChain(scenario, "when the interface appeared", chain, appeared);

        // Windows installs a HID game controller it has never seen in two
        // steps: the HID function on input.inf, then hidgamepad.inf, the
        // extension Microsoft binds to every HID_DEVICE_SYSTEM_GAME
        // collection, whose upper filter restarts the device. The interface
        // appears between the two, for a real pad as for this one, so the
        // early window opens once no installation is pending.
        string installState = Pnp.InstallState(30_000);
        var installed = DateTime.UtcNow;
        Note($"{scenario}: device installation {installState} at {Pnp.Stamp(installed)}, {(installed - appeared).TotalMilliseconds:F0} ms after the interface was seen");

        // The composite sends a report for each submitted frame, and with its
        // idle stream one every 4 ms between them, so a working device
        // returns at least a report for nearly every submit. Half is the bar,
        // and a read that fails means the device went away mid-window.
        var state = new HMGamepadState { Buttons = HMButton.A };
        void Submit() { try { c.SubmitState(in state); } catch { } }
        var early = Hid.CountReports(paths[0], 1500, Submit);
        Record($"{p} input arrives as soon as Windows has installed the HID device",
               installState == "none pending" && early.ReadError == 0 && early.Reports * 2 >= early.Ticks, $"installation {installState}, {early}");
        Note($"{scenario}: the early window opened at {Pnp.Stamp(early.StartUtc)}, {(early.StartUtc - appeared).TotalMilliseconds:F0} ms after the interface was seen" +
             (early.ErrorMs >= 0 ? $", and its read failed at {Pnp.Stamp(early.StartUtc.AddMilliseconds(early.ErrorMs))}" : ""));
        NoteChain(scenario, "after the early window", chain, null);
        // Settled: no device installation pending, the state the early
        // window already waited for, read again for up to the 3 s a fixed
        // wait gave it before.
        Note($"{scenario}: before the settled window, device installation {Pnp.InstallState(3000)}");
        var steady = Hid.CountReports(paths[0], 2000, Submit);
        Record($"{p} input keeps arriving once the device has settled",
               steady.ReadError == 0 && steady.Reports * 2 >= steady.Ticks, steady.ToString());
        NoteChain(scenario, "after the settled window", chain, null);

        // The composite streams reports whether or not a state was submitted,
        // so a count cannot show the submitted state arrived. Three windows:
        // neutral, buttons and a hat pressed, neutral again. A byte steady
        // inside each of the first two windows and different between them is
        // the state. The same comparison between the two neutral windows
        // must find nothing, which shows it does not take a steady byte that
        // ignores the state for the state.
        var idle = ReadWith(paths[0], new HMGamepadState());
        var held = ReadWith(paths[0], Pressed);
        var idleAgain = ReadWith(paths[0], new HMGamepadState());
        var changed = SteadyDifferences(idle, held);
        Record($"{p} the submitted state is what the host reads", changed.Count > 0,
               changed.Count > 0 ? $"byte(s) {string.Join(",", changed)} differ between the neutral and pressed windows"
                                 : $"no byte that is steady in each window differs between them ({idle.Reports} and {held.Reports} report(s))");
        var spurious = SteadyDifferences(idle, idleAgain);
        Record($"{p} two neutral windows show no difference", idle.Reports > 0 && idleAgain.Reports > 0 && spurious.Count == 0,
               spurious.Count == 0 ? "" : $"byte(s) {string.Join(",", spurious)} differ");

        // When device installation ended, then every configuration event since
        // the create that names a node of the chain or a function of the
        // composite. A node started twice, or deleted and configured again,
        // was restarted by Windows.
        var waited = Stopwatch.StartNew();
        string install = Pnp.InstallState(60_000);
        Note($"{scenario}: device installation {install} at {Pnp.Stamp(DateTime.UtcNow)}, after a wait of {waited.ElapsedMilliseconds} ms");
        var match = new List<string>(chain);
        // The composite's VID and PID with no interface number name its audio
        // functions too.
        string? usb = chain.FirstOrDefault(id => id.StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase) && !id.Contains("&MI_", StringComparison.OrdinalIgnoreCase));
        if (usb != null) match.Add(usb.Split('\\')[1]);
        foreach (string e in Pnp.Events(createdFrom, match, Run))
            Note($"{scenario}: event {e}");
    }

    static readonly HMGamepadState Pressed = new()
    {
        Buttons = HMButton.A | HMButton.B | HMButton.X | HMButton.Y | HMButton.LeftBumper | HMButton.RightBumper | HMButton.Back | HMButton.Start,
        Hat = HMHat.North,
    };

    /// <summary>Each node of the chain as it stands, and whether Windows had
    /// device installation pending at that moment.</summary>
    static void NoteChain(string scenario, string when, List<string> chain, DateTime? at)
    {
        Note($"{scenario}: {when}{(at is DateTime t ? $" ({Pnp.Stamp(t)})" : "")}, device installation {Pnp.InstallState(0)}");
        if (chain.Count == 0) Note($"{scenario}:   the interface's devnode could not be read");
        foreach (string id in chain)
            Note($"{scenario}:   {id}: {Pnp.Read(id)}");
    }

    /// <summary>Submits <paramref name="state"/> for a short settle, then
    /// reads for 600 ms with it still submitted every 8 ms. The handle opens
    /// after the settle, so its queue holds only reports of this state.</summary>
    static Hid.Reading ReadWith(string path, HMGamepadState state)
    {
        void Submit() { try { s_controller!.SubmitState(in state); } catch { } }
        for (int i = 0; i < 40; i++) { Submit(); Thread.Sleep(8); }
        return Hid.CountReports(path, 600, Submit);
    }

    static HMController? s_controller;

    /// <summary>The byte positions that hold one value in every report of
    /// <paramref name="a"/>, one value in every report of <paramref name="b"/>,
    /// and different values between the two. Reports of different lengths
    /// are compared over the shortest length.</summary>
    static List<int> SteadyDifferences(Hid.Reading a, Hid.Reading b)
    {
        var found = new List<int>();
        if (a.Frames == null || b.Frames == null || a.Frames.Count == 0 || b.Frames.Count == 0) return found;
        int n = Math.Min(a.Frames.Min(r => r.Length), b.Frames.Min(r => r.Length));
        for (int i = 0; i < n; i++)
            if (a.Frames.All(r => r[i] == a.Frames[0][i]) && b.Frames.All(r => r[i] == b.Frames[0][i]) && a.Frames[0][i] != b.Frames[0][i]) found.Add(i);
        return found;
    }

    // ── Offline ────────────────────────────────────────────────────────

    static int Offline()
    {
        Console.WriteLine("=== The usbip-win2 install check's offline parts ===");

        using (var ctx = new HMContext())
        {
            ctx.LoadDefaultProfiles();
            var full = ctx.GetProfile("dualsense-composite")!;
            HMProfile? bare = null;
            string why = "";
            try { bare = WithoutStream(ctx); }
            catch (Exception ex) { why = $"{ex.GetType().Name}: {ex.Message}"; }
            Check("the catalog's composite has an idle stream (the control)", StreamOf(full) != null);
            Check("scenario N's profile loads", bare != null, why);
            if (bare != null)
            {
                Check("scenario N's profile has no idle stream", StreamOf(bare) == null);
                Check("and is the catalog's composite in all else",
                      bare.VendorId == full.VendorId && bare.ProductId == full.ProductId &&
                      bare.DescriptorHex == full.DescriptorHex && bare.Backend == full.Backend &&
                      bare.ProductString == full.ProductString && bare.RequiresUsbipBackend &&
                      bare.DeviceModelSource.Count == full.DeviceModelSource.Count &&
                      bare.DeviceModelSource.All(kv => full.DeviceModelSource.TryGetValue(kv.Key, out var v) && v == kv.Value),
                      $"{bare.Id}, model from {string.Join(", ", bare.DeviceModelSource.Select(kv => kv.Key + "=" + kv.Value))}");
            }
        }

        // Any present HID device of this machine stands in for the composite.
        string? path = Hid.AllPaths().FirstOrDefault(x => Pnp.Chain(x).Count > 1);
        Check("a HID interface on this machine to read", path != null);
        if (path != null)
        {
            var chain = Pnp.Chain(path);
            var first = Pnp.Read(chain[0]);
            Check("its devnode chain reads up to a root-enumerated node or the tree root",
                  chain[^1].StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase) || chain[^1].StartsWith(@"HTREE\", StringComparison.OrdinalIgnoreCase) || chain.Count == 8,
                  string.Join(" <- ", chain));
            Check("its devnode reads as present with a last arrival", first.Present && first.Arrived != null, first.ToString());
            Check("the install state reads", !Pnp.InstallState(0).StartsWith("unknown", StringComparison.Ordinal), Pnp.InstallState(0));
        }
        // Every HID or USB configuration event the log holds: whether one
        // came in the last week depends on the machine's own devices.
        var events = Pnp.Events(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), new[] { "HID", "USB" }, Run);
        Check("Kernel-PnP configuration events read and parse",
              events.Count > 0 && events.All(e => e.Length > 0 && char.IsDigit(e[0])) && !events[0].StartsWith("wevtutil", StringComparison.Ordinal),
              events.Count > 0 ? $"{events.Count} event(s), the first: {events[0]}" : "none");

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>The profile's idle stream, read from the SDK's own model of
    /// it, which the probe has no public property for.</summary>
    static object? StreamOf(HMProfile profile)
    {
        var inner = typeof(HMProfile).GetProperty("Inner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(profile)!;
        return inner.GetType().GetProperty("Stream")!.GetValue(inner);
    }

    // ── Two programs at once ───────────────────────────────────────────

    const string GoEvent = @"Global\HIDMaestroS73Go";
    const string DoneEvent = @"Global\HIDMaestroS73Done";
    const string ReadyEvent = @"Global\HIDMaestroS73Ready";

    /// <summary>Two programs, each this binary with --create-composite, wait
    /// on one event and create their first composite when it fires.</summary>
    static void Together(string results)
    {
        var driver = Stopwatch.StartNew();
        using (var setup = new HMContext())
        {
            setup.LoadDefaultProfiles();
            setup.InstallDriver();
        }
        Note(FormattableString.Invariant($"together: the driver install before the two programs took {driver.Elapsed.TotalSeconds:F1} s"));
        using var go = new EventWaitHandle(false, EventResetMode.ManualReset, GoEvent);
        using var done = new EventWaitHandle(false, EventResetMode.ManualReset, DoneEvent);
        var children = new List<Process>();
        var ready = new List<EventWaitHandle>();
        for (int i = 0; i < 2; i++)
        {
            ready.Add(new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEvent + i));
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--create-composite");
            psi.ArgumentList.Add($"together-{i}");
            psi.ArgumentList.Add(Path.Combine(results, $"child{i}.txt"));
            psi.ArgumentList.Add(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (DiagLog.On)
            {
                psi.ArgumentList.Add("diag");
                psi.Environment["HIDMAESTRO_DIAG"] = "1";
            }
            children.Add(Process.Start(psi)!);
        }
        // Both start the SDK and wait on the go event, and each says so
        // just before it waits. The event releases them together once both
        // wait, or after the 5 s a fixed wait gave them before.
        bool bothWaiting = WaitHandle.WaitAll(ready.ToArray(), 5000);
        foreach (var r in ready) r.Dispose();
        Note($"together: both programs waited on the go event before it was set: {bothWaiting}");
        go.Set();

        var sw = Stopwatch.StartNew();
        string[] answers = new string[2];
        while (sw.ElapsedMilliseconds < 600_000)
        {
            for (int i = 0; i < 2; i++)
            {
                string f = Path.Combine(results, $"child{i}.txt");
                try { if (answers[i] == null && File.Exists(f)) answers[i] = File.ReadAllText(f).Trim(); } catch { }
            }
            if (answers.All(a => a != null)) break;
            Thread.Sleep(500);
        }
        Record("together: both programs' first composite creates", answers.All(a => a?.StartsWith("created", StringComparison.Ordinal) == true),
               string.Join(" / ", answers.Select(a => a ?? "no answer")));
        int controllers = Pnp.WithHardwareId(UdeHardwareId);
        Record("together: the host controller was installed once: one host controller", controllers == 1, $"{controllers} host controller(s)");
        var paths = new List<string>();
        var wait = Stopwatch.StartNew();
        while (paths.Count < 2 && wait.ElapsedMilliseconds < 30_000)
        {
            paths = Hid.Paths(0x054C, 0x0CE6);
            if (paths.Count < 2) Thread.Sleep(250);
        }
        Record("together: both composites' HID interfaces appear", paths.Count == 2, $"{paths.Count} interface(s)");
        done.Set();
        foreach (var c in children)
        {
            if (!c.WaitForExit(60_000)) try { c.Kill(); } catch { }
            c.Dispose();
        }
    }

    /// <summary>One of the two programs: create a composite when the go
    /// event fires, report, and keep it until the done event.</summary>
    static int CreateComposite(string key, string answer, string slot)
    {
        try
        {
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            using var go = EventWaitHandle.OpenExisting(GoEvent);
            using var done = EventWaitHandle.OpenExisting(DoneEvent);
            using (var ready = EventWaitHandle.OpenExisting(ReadyEvent + slot)) ready.Set();
            go.WaitOne(120_000);
            DiagLog.Note($"probe: {key} (pid {Environment.ProcessId}) released by the go event, creating");
            var create = Stopwatch.StartNew();
            HMController c;
            try { c = ctx.CreateController(ctx.GetProfile("dualsense-composite")!, key); }
            catch (Exception ex)
            {
                DiagLog.Note($"probe: {key} create failed after {create.ElapsedMilliseconds} ms: {ex}");
                File.WriteAllText(answer, $"failed {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            DiagLog.Note($"probe: {key} created on transport index {c.TransportIndex} after {create.ElapsedMilliseconds} ms");
            File.WriteAllText(answer, FormattableString.Invariant($"created {key} on transport index {c.TransportIndex} in {create.Elapsed.TotalSeconds:F1} s"));
            done.WaitOne(300_000);
            c.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(answer, $"failed {ex.GetType().Name}: {ex.Message}"); } catch { }
            return 1;
        }
    }

    /// <summary>B, C and A in one sandbox. B plants usbip-win2's uninstall
    /// entry, C's two programs install the host controller on that machine,
    /// B's checks read what the install did to the entry, the planted entry
    /// goes, and A's checks read the install and a composite this process
    /// creates over it. E follows in the same sandbox once this program has
    /// ended (EndToEnd).</summary>
    static void All(string results)
    {
        var part = Stopwatch.StartNew();
        using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "USBip");
            k.SetValue("UninstallString", "\"C:\\probe\\UsbipInstallCheck.exe\"");
        }
        Record("record: the planted uninstall entry reads as an existing install",
               UninstallerPath() != null, UninstallerPath() ?? "");

        Together(results);
        Record("record: the planted uninstaller never ran", !File.Exists(Marker));
        Record("record: the planted entry is as it was planted", UninstallerPath() == "C:\\probe\\UsbipInstallCheck.exe", UninstallerPath() ?? "gone");
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Note(FormattableString.Invariant($"record and together: took {part.Elapsed.TotalSeconds:F1} s"));

        // C's programs dispose their composites as they exit, and A's
        // checks read the HID interface of the composite created next, so
        // C's go first.
        var gone = Stopwatch.StartNew();
        while (Hid.Paths(0x054C, 0x0CE6).Count > 0 && gone.ElapsedMilliseconds < 30_000) Thread.Sleep(250);
        int left = Hid.Paths(0x054C, 0x0CE6).Count;
        Record("together: both composites are gone once their programs dispose them", left == 0, $"{left} interface(s) left");

        part.Restart();
        using (var ctx = new HMContext())
        {
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            HMController? c = null;
            Exception? error = null;
            DateTime createdFrom = DateTime.UtcNow;
            var made = Stopwatch.StartNew();
            try { c = ctx.CreateController(ctx.GetProfile("dualsense-composite")!, "sandbox-composite"); }
            catch (Exception ex) { error = ex; }
            Note(FormattableString.Invariant($"clean: the create took {made.Elapsed.TotalSeconds:F1} s"));
            CleanChecks("clean", c, error, createdFrom);
            c?.Dispose();
        }
        Note(FormattableString.Invariant($"clean: took {part.Elapsed.TotalSeconds:F1} s"));
    }

    /// <summary>E: S45's and S65's probes over A's install, each a
    /// self-contained copy started in the sandbox once this program's run
    /// there has ended: each refuses its live part while another program of
    /// the SDK runs. Each of their check lines is a check here, under the
    /// probe's name, and a skip line a note.</summary>
    static void EndToEnd(string wsb, string id, string results)
    {
        foreach (var (name, exe) in new[]
                 {
                     ("S45", @"C:\probe-e2e\UsbipE2ECheck.exe"),
                     ("S65", @"C:\probe-switch2\Switch2CompositeCheck.exe"),
                 })
        {
            var sw = Stopwatch.StartNew();
            var ran = Run(wsb, 1_200_000, "exec", "--id", id, "-c", $"cmd.exe /c {exe} > C:\\results\\{name}.txt 2>&1", "-r", "System", "--raw");
            int? exit = JsonInt(ran.Output, "ExitCode");
            string file = Path.Combine(results, $"{name}.txt");
            if (!File.Exists(file)) { Check($"e2e: {name}'s probe ran in the sandbox", false, ran.Output.Trim()); continue; }
            int pass = 0, fail = 0, skip = 0;
            foreach (string raw in File.ReadAllLines(file))
            {
                string line = raw.Trim();
                if (line.StartsWith("[PASS]", StringComparison.Ordinal)) { pass++; Check($"e2e {name}: {line[6..].Trim()}", true); }
                else if (line.StartsWith("[FAIL]", StringComparison.Ordinal)) { fail++; Check($"e2e {name}: {line[6..].Trim()}", false); }
                else if (line.Contains("SKIP", StringComparison.Ordinal)) { skip++; Console.WriteLine($"  [NOTE] e2e {name}: {line}"); }
            }
            Console.WriteLine(FormattableString.Invariant($"  [NOTE] e2e {name}: exit {exit}, {pass} passed, {fail} failed, {skip} skip line(s), {sw.Elapsed.TotalSeconds:F1} s"));
            Check($"e2e: {name}'s probe ran to its end and passed what it ran", (exit == 0 || exit == 2) && pass > 0 && fail == 0,
                  $"exit {exit}, {pass} passed, {fail} failed");
        }
    }

    static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip");

    /// <summary>The uninstaller usbip-win2's own setup would run before
    /// installing, read the way its GetInstalledUninstallString reads it:
    /// the 64-bit view of HKLM, then the 32-bit view, then HKCU. Only the
    /// quoted form matters here, since that is what the scenario plants and
    /// what Inno Setup writes.</summary>
    static string? UninstallerPath()
    {
        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            using var b = RegistryKey.OpenBaseKey(hive, view);
            using var k = b.OpenSubKey(UninstallKey);
            if (k?.GetValue("UninstallString") is not string s || s.Trim().Length == 0) continue;
            s = s.Trim();
            string path = s.StartsWith('"') ? s[1..].Split('"')[0] : s.Split(' ')[0];
            return File.Exists(path) ? path : null;
        }
        return null;
    }

    /// <summary>True when a published INF in %windir%\INF names
    /// <paramref name="text"/>: a package of usbip2_ude or usbip2_filter.</summary>
    static bool DriverPackagePresent(string text)
    {
        string inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        foreach (string f in Directory.EnumerateFiles(inf, "oem*.inf"))
        {
            try { if (ReadInf(f).Contains(text, StringComparison.OrdinalIgnoreCase)) return true; }
            catch { }
        }
        return false;
    }

    /// <summary>The original INF name of a published INF: usbip2_ude.inf for a
    /// package of the host controller, read from its text.</summary>
    static string? InfOriginal(string published)
    {
        try
        {
            string text = ReadInf(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", published));
            if (text.Contains("usbip2_ude", StringComparison.OrdinalIgnoreCase) && text.Contains(UdeHardwareId, StringComparison.OrdinalIgnoreCase)) return "usbip2_ude.inf";
            return null;
        }
        catch { return null; }
    }

    static string ReadInf(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        return raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE
            ? System.Text.Encoding.Unicode.GetString(raw, 2, raw.Length - 2)
            : System.Text.Encoding.UTF8.GetString(raw);
    }

    static bool KeyExists(string path)
    {
        using var k = Registry.LocalMachine.OpenSubKey(path);
        return k != null;
    }

    static List<string> WdfDebugValues()
    {
        var found = new List<string>();
        using var k = Registry.LocalMachine.OpenSubKey(WdfKey);
        if (k == null) return found;
        foreach (string v in new[] { "VerifierOn", "VerboseOn" })
            if (k.GetValue(v) != null) found.Add(v);
        return found;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    static (int Exit, string Output) Run(string exe, int timeoutMs, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
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
