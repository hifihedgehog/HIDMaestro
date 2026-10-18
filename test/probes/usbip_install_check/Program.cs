// The usbip-win2 install on a clean machine, in Windows Sandbox.
//
//   A. clean    A composite create on a machine without usbip-win2 runs the
//               bundled installer. Afterward there is no Start menu folder,
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
// The host role starts a fresh sandbox for each scenario with wsb.exe,
// which opens no window, runs this program inside it as SYSTEM, and reports
// what it wrote back. Inside, the program runs as a self-contained copy
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
            return InSandbox(args[1], args[2]);
        if (args.Length >= 3 && args[0] == "--create-composite")
            return CreateComposite(args[1], args[2]);
        if (args.Length > 0 && args[0] == "--offline")
            return Offline();
        return Host(args.Length > 0 ? args[0].ToUpperInvariant() : "ABC");
    }

    // ── Host ───────────────────────────────────────────────────────────

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
        };
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
        try
        {
            string config =
                "<Configuration><Networking>Disable</Networking><MappedFolders>" +
                $"<MappedFolder><HostFolder>{Xml(publish)}</HostFolder><SandboxFolder>C:\\probe</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>" +
                $"<MappedFolder><HostFolder>{Xml(results)}</HostFolder><SandboxFolder>C:\\results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>" +
                "</MappedFolders></Configuration>";
            var started = Run(wsb, 120_000, "start", "--config", config, "--raw");
            id = JsonString(started.Output, "Id");
            if (id == null) { Check($"{scenario}: a sandbox starts", false, started.Output.Trim()); return; }

            // The first command can arrive before the sandbox has finished
            // starting, so wait for a trivial one to succeed.
            var sw = Stopwatch.StartNew();
            bool ready = false;
            while (!ready && sw.ElapsedMilliseconds < 180_000)
            {
                var probe = Run(wsb, 60_000, "exec", "--id", id, "-c", "cmd.exe /c exit 0", "-r", "System", "--raw");
                ready = JsonInt(probe.Output, "ExitCode") == 0;
                if (!ready) Thread.Sleep(2000);
            }
            if (!ready) { Check($"{scenario}: the sandbox accepts a command", false); return; }

            var ran = Run(wsb, 1_200_000, "exec", "--id", id, "-c",
                $"C:\\probe\\UsbipInstallCheck.exe --in-sandbox {scenario} C:\\results", "-r", "System", "--raw");
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
            if (id != null) Run(wsb, 120_000, "stop", "--id", id);
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

            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            HMProfile profile = scenario == "nostream" ? WithoutStream(ctx) : ctx.GetProfile("dualsense-composite")!;
            HMController? c = null;
            Exception? error = null;
            DateTime createdFrom = DateTime.UtcNow;
            try { c = ctx.CreateController(profile, "sandbox-composite"); }
            catch (Exception ex) { error = ex; }

            if (scenario == "record") RecordChecks(error);
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

        // The composite sends a report for each submitted frame, and with its
        // idle stream one every 4 ms between them, so a working device
        // returns at least a report for nearly every submit. Half is the bar,
        // and a read that fails means the device went away mid-window.
        var state = new HMGamepadState { Buttons = HMButton.A };
        void Submit() { try { c.SubmitState(in state); } catch { } }
        var early = Hid.CountReports(paths[0], 1500, Submit);
        Record($"{p} input arrives as soon as the interface appears",
               early.ReadError == 0 && early.Reports * 2 >= early.Ticks, early.ToString());
        Note($"{scenario}: the early window opened at {Pnp.Stamp(early.StartUtc)}, {(early.StartUtc - appeared).TotalMilliseconds:F0} ms after the interface was seen" +
             (early.ErrorMs >= 0 ? $", and its read failed at {Pnp.Stamp(early.StartUtc.AddMilliseconds(early.ErrorMs))}" : ""));
        NoteChain(scenario, "after the early window", chain, null);
        Thread.Sleep(3000);
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

    /// <summary>Two programs, each this binary with --create-composite, wait
    /// on one event and create their first composite when it fires.</summary>
    static void Together(string results)
    {
        using (var setup = new HMContext())
        {
            setup.LoadDefaultProfiles();
            setup.InstallDriver();
        }
        using var go = new EventWaitHandle(false, EventResetMode.ManualReset, GoEvent);
        using var done = new EventWaitHandle(false, EventResetMode.ManualReset, DoneEvent);
        var children = new List<Process>();
        for (int i = 0; i < 2; i++)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("--create-composite");
            psi.ArgumentList.Add($"together-{i}");
            psi.ArgumentList.Add(Path.Combine(results, $"child{i}.txt"));
            children.Add(Process.Start(psi)!);
        }
        // Both start the SDK and wait. The event releases them together.
        Thread.Sleep(5000);
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
    static int CreateComposite(string key, string answer)
    {
        try
        {
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            using var go = EventWaitHandle.OpenExisting(GoEvent);
            using var done = EventWaitHandle.OpenExisting(DoneEvent);
            go.WaitOne(120_000);
            HMController c;
            try { c = ctx.CreateController(ctx.GetProfile("dualsense-composite")!, key); }
            catch (Exception ex)
            {
                File.WriteAllText(answer, $"failed {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            File.WriteAllText(answer, $"created {key} on transport index {c.TransportIndex}");
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

    /// <summary>Host controller devnodes usbip-win2's devnode.exe made, as
    /// pnputil lists the hardware ID of its emulated host controller.</summary>
    static int HostControllers()
    {
        var r = Run("pnputil.exe", 60_000, "/enum-devices", "/deviceid", @"ROOT\USBIP_WIN2\UDE");
        return r.Output.Split('\n').Count(l => l.TrimStart().StartsWith("Instance ID:", StringComparison.OrdinalIgnoreCase));
    }

    static void RecordChecks(Exception? error)
    {
        Record("record: the composite create is refused", error != null);
        Record("record: the refusal names the existing usbip-win2 install",
               error != null && error.Message.Contains("uninstall entry", StringComparison.OrdinalIgnoreCase),
               error == null ? "" : $"{error.GetType().Name}: {error.Message}");
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
