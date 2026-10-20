// The usbip-win2 install on a clean machine, in Windows Sandbox.
//
//   A. clean    A composite create on a machine without usbip-win2 runs the
//               bundled installer, when A runs alone. In the sandbox it
//               shares with B and C, C's creates run it and A's checks read
//               that install. Afterward there is no Start menu folder,
//               no desktop shortcut, and no GUI, SDK or PDB file. The VC++
//               runtime the host controller's own installer step needs is
//               there, and the composite enumerates and carries input.
//   B. record   A machine holds usbip-win2's uninstall entry, pointing at an
//               uninstaller that exists, but not its driver package. The SDK
//               refuses instead of running the installer, which would run
//               that uninstaller first.
//   C. together Two programs create their first composite at the same
//               moment on a clean machine. One installs, the other waits for
//               it under the deploy lock: one host controller, and both
//               composites appear.
//   N. nostream A, with the composite's profile loaded from a file without
//               its idle stream ("stream"), the one change in what the
//               device sends between v1.12.0 and 2.0.0. Runs only when named.
//   K. killed-loader
//               One program's first composite while the installer's loader
//               is ended mid-install: a watcher ends the loader with exit
//               code 255 once its setup child runs. The setup finishes the
//               install alone, and the create still succeeds, reading the
//               machine again after the failing exit code. The SDK's
//               diagnostic log must name both steps. Runs only when named.
//
// --offline checks, on this machine with no sandbox, no device and no
// elevation, the parts of A and N that can be: N's profile differs from the
// catalog's only in its stream, and the devnode chain, arrival times and
// Kernel-PnP events read back from this machine's own HID devices.
//
// Scenarios A and N also note what Windows did to the composite's devnodes
// while the probe read it (Pnp.cs): each node from the HID interface up to
// the host controller with its last arrival and removal, read when the
// interface appears and after each read window, whether device installation
// was pending, and the Kernel-PnP configuration events that name them.
// Notes print as [NOTE] and are not checks.
//
// The host role starts a sandbox with wsb.exe, which opens no window, runs
// this program inside it as SYSTEM, and reports what it wrote back. A, B
// and C share one sandbox, since each sandbox starts clean and its
// usbip-win2 install takes about four minutes (the sandbox itself starts in
// about 2 s, measured 2026-10-18): B runs first and leaves the machine without usbip-win2, C installs it on
// that clean machine, and A's checks then read C's install and a
// composite created over it. Any other selection, N included, starts a
// sandbox per scenario. Inside, the program runs as a self-contained copy
// published to sandbox\ beside this one, since the sandbox has no .NET
// runtime. Run with /VERYSILENT as its first argument, it is the planted
// uninstaller of scenario B and only leaves a marker.
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

internal static class Program
{
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{199505b0-b93d-4521-a8c7-897818e0205a}_is1";
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
            // K reads the SDK's diagnostic lines, so it always runs with them.
            if (diag || args[1] == "killed-loader") DiagLog.Start(Path.Combine(args[2], "main-diag.log"));
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
        if (args.Length >= 2 && args[0] == "--record-create")
            return RecordCreate(args[1]);
        if (args.Length > 0 && args[0] == "--offline")
            return Offline();
        int at = Array.IndexOf(args, "--diag");
        if (at >= 0 && at + 1 < args.Length) s_diagDir = Path.GetFullPath(args[at + 1]);
        return Host(args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0].ToUpperInvariant() : "ABC");
    }

    // ── Host ───────────────────────────────────────────────────────────

    // --diag <dir>: each program inside the sandbox runs with HIDMAESTRO_DIAG=1
    // and writes the SDK's diagnostic lines to a log of its own. Those logs, a
    // once-a-second list of the installer processes, the VC++ runtime's
    // bundle and package logs, the Windows Installer events and Windows'
    // setupapi logs are copied to <dir> (DiagLog).
    static string? s_diagDir;

    static int Host(string only)
    {
        Console.WriteLine("=== The usbip-win2 install on a clean machine ===");
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

        var scenarios = new (char Id, string Name, string Title)[]
        {
            ('A', "clean", "a clean machine"),
            ('B', "record", "an uninstall entry without the driver package"),
            ('C', "together", "two programs' first composites at the same moment"),
            ('N', "nostream", "a clean machine, the composite without its idle stream"),
            ('K', "killed-loader", "one program's first composite while the installer's loader is ended mid-install"),
        };
        if (only.Contains('A') && only.Contains('B') && only.Contains('C'))
        {
            Console.WriteLine();
            Console.WriteLine("B, C, A. an uninstall entry without the driver package, two programs' first composites at the same moment, then a clean install's checks");
            try { RunInSandbox(wsb, publish, "all"); }
            catch (Exception ex) { Check("B, C, A ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
            only = only.Replace("A", "").Replace("B", "").Replace("C", "");
        }
        foreach (var (id, name, title) in scenarios)
        {
            if (!only.Contains(id)) continue;
            Console.WriteLine();
            Console.WriteLine($"{id}. {title}");
            try { RunInSandbox(wsb, publish, name); }
            catch (Exception ex) { Check($"{id} ran to the end", false, $"{ex.GetType().Name}: {ex.Message}"); }
        }

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static void RunInSandbox(string wsb, string publish, string scenario)
    {
        string results = Path.Combine(Path.GetTempPath(), $"HIDMaestro_sandbox_{scenario}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(results);
        string? id = null;
        double startS = double.NaN, readyS = double.NaN, runS = double.NaN;
        int tries = 0;
        try
        {
            string config =
                "<Configuration><Networking>Disable</Networking><MappedFolders>" +
                $"<MappedFolder><HostFolder>{Xml(publish)}</HostFolder><SandboxFolder>C:\\probe</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>" +
                $"<MappedFolder><HostFolder>{Xml(results)}</HostFolder><SandboxFolder>C:\\results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>" +
                "</MappedFolders></Configuration>";
            var step = Stopwatch.StartNew();
            var started = Run(wsb, 120_000, "start", "--config", config, "--raw");
            id = JsonString(started.Output, "Id");
            if (id == null) { Check($"{scenario}: a sandbox starts", false, started.Output.Trim()); return; }
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
            var ran = Run(wsb, 1_200_000, "exec", "--id", id, "-c",
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
        }
        finally
        {
            var stop = Stopwatch.StartNew();
            if (id != null) Run(wsb, 120_000, "stop", "--id", id);
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
                   UninstallerPath() == null && !DriverPackagePresent() && !Directory.Exists(InstallDir));

            if (scenario == "all")
            {
                All(results);
                s_lines.Add("DONE");
                File.WriteAllLines(Path.Combine(results, "results.txt"), s_lines);
                return s_lines.Any(l => l.StartsWith("FAIL", StringComparison.Ordinal)) ? 1 : 0;
            }

            if (scenario == "record")
            {
                using var k = Registry.LocalMachine.CreateSubKey(UninstallKey);
                k.SetValue("DisplayName", "USBip");
                k.SetValue("UninstallString", "\"C:\\probe\\UsbipInstallCheck.exe\"");
                Record("record: the planted uninstall entry reads as an existing install",
                       UninstallerPath() != null, UninstallerPath() ?? "");
            }

            if (scenario == "together")
            {
                Together(results);
                s_lines.Add("DONE");
                File.WriteAllLines(Path.Combine(results, "results.txt"), s_lines);
                return s_lines.Any(l => l.StartsWith("FAIL", StringComparison.Ordinal)) ? 1 : 0;
            }

            if (scenario == "killed-loader")
            {
                KilledLoader(results);
                s_lines.Add("DONE");
                File.WriteAllLines(Path.Combine(results, "results.txt"), s_lines);
                return s_lines.Any(l => l.StartsWith("FAIL", StringComparison.Ordinal)) ? 1 : 0;
            }

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

            if (scenario == "record") RecordChecks(error != null, error == null ? "" : $"{error.GetType().Name}: {error.Message}");
            else CleanChecks(scenario, c, error, createdFrom);

            c?.Dispose();
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

    static void CleanChecks(string scenario, HMController? c, Exception? error, DateTime createdFrom)
    {
        string p = scenario + ":";
        Record($"{p} the composite creates", c != null, error == null ? "" : $"{error.GetType().Name}: {error.Message}");

        // The positive controls: the install happened.
        Record($"{p} usbip-win2's driver package is installed", DriverPackagePresent());
        Record($"{p} its uninstall entry is there", UninstallerPath() != null);
        Record($"{p} devnode.exe and usbip.exe are installed",
               File.Exists(Path.Combine(InstallDir, "devnode.exe")) && File.Exists(Path.Combine(InstallDir, "usbip.exe")), InstallDir);
        Record($"{p} the VC++ runtime devnode.exe imports is installed",
               File.Exists(Path.Combine(Environment.SystemDirectory, "vcruntime140.dll")) &&
               File.Exists(Path.Combine(Environment.SystemDirectory, "msvcp140.dll")));

        // What the installer must not leave.
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
        Record($"{p} no GUI", !File.Exists(Path.Combine(InstallDir, "wusbip.exe")));
        Record($"{p} no SDK headers or library",
               !Directory.Exists(Path.Combine(InstallDir, "include")) && !Directory.Exists(Path.Combine(InstallDir, "lib")));
        string[] pdbs = Directory.Exists(InstallDir) ? Directory.GetFiles(InstallDir, "*.pdb") : Array.Empty<string>();
        Record($"{p} no PDB files", pdbs.Length == 0, string.Join(", ", pdbs.Select(Path.GetFileName)));

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
        var held = ReadWith(paths[0], new HMGamepadState
        {
            Buttons = HMButton.A | HMButton.B | HMButton.X | HMButton.Y | HMButton.LeftBumper | HMButton.RightBumper | HMButton.Back | HMButton.Start,
            Hat = HMHat.North,
        });
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
        var events = Pnp.Events(DateTime.UtcNow.AddDays(-7), new[] { "HID", "USB" }, Run);
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
        Record("together: usbip-win2 was installed once: one host controller",
               HostControllers() == 1, $"{HostControllers()} host controller(s)");
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

    // ── The loader ended mid-install ───────────────────────────────────

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool TerminateProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint exitCode);

    /// <summary>The exit code the watcher ends the loader with: the code
    /// the failed S73 run reported.</summary>
    const uint KilledLoaderExitCode = 255;

    /// <summary>One program's first composite. A watcher polls the process
    /// list every 100 ms for the setup Inno Setup's loader runs
    /// (USBip-*.tmp), and once it runs, ends the loader (the other USBip-*
    /// process) with exit code 255, then follows the setup to its exit. The
    /// SDK's diagnostic lines are read from the log the probe's tap
    /// writes.</summary>
    static void KilledLoader(string results)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        var run = Stopwatch.StartNew();
        int loaderPid = -1, setupPid = -1, loaderExit = int.MinValue, setupExit = int.MinValue;
        long killedAt = -1, setupEndedAt = -1;
        bool setupRanAfterKill = false;
        string watchError = "";
        var stop = new ManualResetEventSlim();
        var watcher = new Thread(() =>
        {
            try
            {
                Process? setup = null, loader = null;
                while (setup == null && !stop.Wait(100))
                {
                    foreach (var p in Process.GetProcesses())
                    {
                        string n;
                        try { n = p.ProcessName; } catch { p.Dispose(); continue; }
                        if (!n.StartsWith("USBip-", StringComparison.OrdinalIgnoreCase)) { p.Dispose(); continue; }
                        if (n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) setup ??= p;
                        else loader ??= p;
                    }
                    if (setup == null) { loader?.Dispose(); loader = null; }
                }
                if (setup == null) return;
                if (loader == null) { watchError = $"the setup child (pid {setup.Id}) ran with no loader beside it"; return; }
                loaderPid = loader.Id;
                setupPid = setup.Id;
                // A handle held from here keeps the setup's exit code
                // readable after it ends.
                _ = setup.SafeHandle;
                if (!TerminateProcess(loader.SafeHandle, KilledLoaderExitCode))
                {
                    watchError = $"TerminateProcess failed with Win32 error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
                    return;
                }
                killedAt = run.ElapsedMilliseconds;
                loader.WaitForExit(10_000);
                loaderExit = loader.ExitCode;
                setupRanAfterKill = !setup.HasExited;
                if (setup.WaitForExit(600_000))
                {
                    setupEndedAt = run.ElapsedMilliseconds;
                    setupExit = setup.ExitCode;
                }
            }
            catch (Exception ex) { watchError = $"{ex.GetType().Name}: {ex.Message}"; }
        }) { IsBackground = true, Name = "killed-loader watcher" };
        watcher.Start();

        HMController? c = null;
        Exception? error = null;
        DiagLog.Note("probe: killed-loader create starts");
        try { c = ctx.CreateController(ctx.GetProfile("dualsense-composite")!, "sandbox-composite"); }
        catch (Exception ex) { error = ex; }
        long created = run.ElapsedMilliseconds;
        DiagLog.Note($"probe: killed-loader create returned after {created} ms{(error == null ? "" : ": " + error)}");
        Note(FormattableString.Invariant($"killed-loader: the create took {created / 1000.0:F1} s"));
        // The setup has ended by the time the create returns, unless the
        // create gave up first.
        if (!watcher.Join(15_000)) { stop.Set(); watcher.Join(5_000); }
        stop.Set();

        Record("killed-loader: the watcher ended the loader while its setup child ran (positive control)",
               killedAt >= 0 && setupRanAfterKill,
               killedAt < 0
                   ? (watchError.Length > 0 ? watchError : "the watcher never saw the setup child, so no fault was injected")
                   : FormattableString.Invariant($"loader pid {loaderPid} ended with exit code {loaderExit} at {killedAt} ms into the create, setup pid {setupPid} {(setupRanAfterKill ? "still running" : "already gone")}") +
                     (setupEndedAt >= 0 ? FormattableString.Invariant($", the setup exited {setupExit} at {setupEndedAt} ms") : ", the setup's exit not seen") +
                     (watchError.Length > 0 ? $", {watchError}" : ""));
        Record("killed-loader: the composite creates", c != null, error == null ? "" : $"{error.GetType().Name}: {error.Message}");

        var paths = new List<string>();
        var sw = Stopwatch.StartNew();
        while (c != null && paths.Count == 0 && sw.ElapsedMilliseconds < 30_000)
        {
            paths = Hid.Paths(0x054C, 0x0CE6);
            if (paths.Count == 0) Thread.Sleep(250);
        }
        Record("killed-loader: the composite's HID interface appears", paths.Count > 0, $"{paths.Count} interface(s)");

        string diag = "";
        try
        {
            using var fs = new FileStream(Path.Combine(results, "main-diag.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            diag = reader.ReadToEnd();
        }
        catch (Exception ex) { diag = ""; Note($"killed-loader: the diag log could not be read: {ex.GetType().Name}: {ex.Message}"); }
        string exited = $"the installer exited {loaderExit}; reading the machine again";
        string answers = $"the host controller answers after the installer exited {loaderExit}";
        bool sawExit = loaderExit != int.MinValue && diag.Contains(exited, StringComparison.Ordinal);
        bool sawAnswer = loaderExit != int.MinValue && diag.Contains(answers, StringComparison.Ordinal);
        Record("killed-loader: the diag log names the loader's exit code, then the host controller answering after it",
               sawExit && sawAnswer,
               $"\"{exited}\" {(sawExit ? "found" : "missing")}, \"{answers}\" {(sawAnswer ? "found" : "missing")}");
        foreach (string line in diag.Split('\n'))
            if (line.Contains("UsbipDriverInstaller:", StringComparison.Ordinal))
                Note($"killed-loader: diag {line.Trim()}");

        c?.Dispose();
    }

    /// <summary>Host controller devnodes usbip-win2's devnode.exe made: the
    /// devnodes with the hardware ID of its emulated host controller.</summary>
    static int HostControllers() => Pnp.WithHardwareId(@"ROOT\USBIP_WIN2\UDE");

    /// <summary>B, C and A in one sandbox. B's create runs in a child
    /// process, so nothing it leaves in a process reaches A's, and the
    /// planted entry goes once it has run. C then installs usbip-win2 on
    /// the clean machine, and A's checks read that install and a composite
    /// this process creates over it.</summary>
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
        string answer = Path.Combine(results, "record.txt");
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--record-create");
        psi.ArgumentList.Add(answer);
        using (var child = Process.Start(psi)!)
        {
            if (!child.WaitForExit(300_000)) try { child.Kill(); } catch { }
        }
        string said = File.Exists(answer) ? File.ReadAllText(answer).Trim() : "no answer";
        RecordChecks(said.StartsWith("refused ", StringComparison.Ordinal), said);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Record("record: the machine is without usbip-win2 again once the planted entry is gone",
               UninstallerPath() == null && !DriverPackagePresent() && !Directory.Exists(InstallDir));
        Note(FormattableString.Invariant($"record: took {part.Elapsed.TotalSeconds:F1} s"));

        part.Restart();
        Together(results);

        // C's programs dispose their composites as they exit, and A's
        // checks read the HID interface of the composite created next, so
        // C's go first.
        var gone = Stopwatch.StartNew();
        while (Hid.Paths(0x054C, 0x0CE6).Count > 0 && gone.ElapsedMilliseconds < 30_000) Thread.Sleep(250);
        int left = Hid.Paths(0x054C, 0x0CE6).Count;
        Record("together: both composites are gone once their programs dispose them", left == 0, $"{left} interface(s) left");
        Note(FormattableString.Invariant($"together: took {part.Elapsed.TotalSeconds:F1} s"));

        part.Restart();
        using var ctx = new HMContext();
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
        Note(FormattableString.Invariant($"clean: took {part.Elapsed.TotalSeconds:F1} s"));
    }

    /// <summary>B's create, in a child process: refused or created, written
    /// to <paramref name="answer"/>.</summary>
    static int RecordCreate(string answer)
    {
        try
        {
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            try
            {
                using var c = ctx.CreateController(ctx.GetProfile("dualsense-composite")!, "sandbox-composite");
                File.WriteAllText(answer, "created");
            }
            catch (Exception ex) { File.WriteAllText(answer, $"refused {ex.GetType().Name}: {ex.Message}"); }
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(answer, $"failed {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    static void RecordChecks(bool refused, string refusal)
    {
        Record("record: the composite create is refused", refused);
        Record("record: the refusal names the existing usbip-win2 install",
               refused && refusal.Contains("uninstall entry", StringComparison.OrdinalIgnoreCase), refusal);
        Record("record: the planted uninstaller never ran", !File.Exists(Marker));
        Record("record: usbip-win2 was not installed", !DriverPackagePresent() && !Directory.Exists(InstallDir));
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

    static bool DriverPackagePresent()
    {
        string inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        foreach (string f in Directory.EnumerateFiles(inf, "oem*.inf"))
        {
            try { if (File.ReadAllText(f).Contains("usbip2_ude", StringComparison.OrdinalIgnoreCase)) return true; }
            catch { }
        }
        return false;
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
