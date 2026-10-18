// HIDMaestro.Core end to end in a NativeAOT app and in a trimmed,
// self-contained, single-file app. One source, published both ways for x64
// and ARM64 into aot-<arch>\ and trim-<arch>\ beside the probe. Each copy:
//
//   1. Runs as the kind of app it was published as, with reflection-based
//      JSON switched off, which is what NativeAOT and trimming do.
//   2. Loads the whole embedded catalog. Every profile resource parses, and
//      every layout the JSON carries comes back as a layout of its kind.
//   3. Installs the driver.
//   4. Creates dualshock-4-v2 on the UMDF2 driver and dualsense-composite on
//      USB/IP. For each, the HID interface appears, input submitted with
//      SubmitState is read back from the interface the way a host reads it,
//      and an output report written to the interface raises OutputReceived.
//      Delivery is judged on content: the reports read while a neutral state
//      is submitted and the reports read while a pressed state is submitted
//      must differ in at least one byte that is steady within each window.
//      A counter or timestamp changes inside a window and so cannot pass
//      for the state.
//   5. Disposes both, and both interfaces go away.
//
// Usage: AotSmoke.exe aot|trim [--neutral-only]. Requires elevation. Exit 0
// PASS, 1 FAIL, 2 when another program that embeds HIDMaestro is running.
// --neutral-only submits the neutral state in the pressed window as well, so
// reports flow but carry no pressed state and the delivery check must fail.
// Every run also compares two neutral windows, which must differ in nothing.
//
// AotSmoke.exe --startup prints the milliseconds from process start to the
// catalog loaded and exits, touching no device: the cold start the
// performance notes compare between JIT, NativeAOT and single-file.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
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

    static int Main(string[] args)
    {
        var started = Process.GetCurrentProcess().StartTime;
        if (args.Length > 0 && args[0] == "--startup")
        {
            using var c = new HMContext();
            int n = c.LoadDefaultProfiles();
            double ms = (DateTime.Now - started).TotalMilliseconds;
            Console.WriteLine(FormattableString.Invariant($"{ms:F1} {n}"));
            return n > 0 ? 0 : 1;
        }
        string expected = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        bool neutralOnly = args.Contains("--neutral-only");
        Console.WriteLine($"=== HIDMaestro in a {(expected == "aot" ? "NativeAOT" : expected == "trim" ? "trimmed single-file" : "plain")} app ===");

        string? consumer = OtherSdkConsumer();
        if (consumer != null)
        {
            Console.WriteLine($"  [SKIP] {consumer} is running and shares the SDK's devices.");
            return 2;
        }

        // 1. The app is what it was published as.
        bool aot = !RuntimeFeature.IsDynamicCodeSupported;
        bool singleFile = !File.Exists(Path.Combine(AppContext.BaseDirectory, "HIDMaestro.Core.dll"));
        bool jsonReflectionOff = AppContext.TryGetSwitch("System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault", out bool on) && !on;
        if (expected == "aot") Check("the app runs as NativeAOT", aot);
        if (expected == "trim") Check("the app runs as a single file", singleFile && !aot);
        Check("reflection-based JSON is switched off in this app", jsonReflectionOff);

        using var ctx = new HMContext();

        // 2. The catalog.
        var resources = typeof(HMContext).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var expectedKinds = new SortedSet<string>(StringComparer.Ordinal);
        int expectedLayouts = 0;
        foreach (string name in resources)
        {
            using var s = typeof(HMContext).Assembly.GetManifestResourceStream(name)!;
            using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.TryGetProperty("layout", out var layout) && layout.ValueKind == JsonValueKind.Object
                && layout.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String)
            {
                expectedLayouts++;
                expectedKinds.Add(kind.GetString()!.ToLowerInvariant());
            }
        }
        var load = Stopwatch.StartNew();
        int loaded = ctx.LoadDefaultProfiles();
        double catalogMs = load.Elapsed.TotalMilliseconds;
        Check("every embedded profile loads", resources.Length > 0 && loaded == resources.Length, $"{loaded} of {resources.Length}");
        var withLayout = ctx.AllProfiles.Where(p => p.Layout != null).ToList();
        var kinds = new SortedSet<string>(withLayout.Select(p => JsonNamingPolicy.SnakeCaseLower.ConvertName(p.Layout!.Kind.ToString())), StringComparer.Ordinal);
        Check("every layout in the JSON comes back as a layout", withLayout.Count == expectedLayouts, $"{withLayout.Count} of {expectedLayouts}");
        Check("every layout kind the catalog uses comes back as that kind", kinds.SetEquals(expectedKinds),
              $"{string.Join(",", kinds)} vs {string.Join(",", expectedKinds)}");

        // 3. The driver.
        try { ctx.InstallDriver(); Check("the driver installs", true); }
        catch (Exception ex) { Check("the driver installs", false, $"{ex.GetType().Name}: {ex.Message}"); return Finish(); }

        // 4 and 5. One controller on each backend.
        var devices = new (string Id, ushort Vid, ushort Pid, byte OutputId, string Key, byte[] Body)[]
        {
            ("dualshock-4-v2", 0x054C, 0x09CC, 0x05, "aot-smoke-umdf2", new byte[] { 0x07, 0x00, 0x00, 0x40, 0x40, 0x00, 0x00, 0xFF }),
            ("dualsense-composite", 0x054C, 0x0CE6, 0x02, "aot-smoke-usbip", new byte[] { 0x03, 0x00, 0x40, 0x40 }),
        };
        double firstControllerMs = -1;
        foreach (var d in devices)
        {
            var profile = ctx.GetProfile(d.Id);
            if (profile == null) { Check($"{d.Id}: the profile is in the catalog", false); continue; }
            var before = Native.HidPaths(d.Vid, d.Pid);
            HMController c;
            var create = Stopwatch.StartNew();
            try { c = ctx.CreateController(profile, d.Key); }
            catch (Exception ex) { Check($"{d.Id}: creates", false, $"{ex.GetType().Name}: {ex.Message}"); continue; }
            if (firstControllerMs < 0) firstControllerMs = create.Elapsed.TotalMilliseconds;
            Check($"{d.Id}: creates", true);

            int outputs = 0;
            c.OutputReceived += (_, pkt) => { if (pkt.ReportId == d.OutputId) Interlocked.Increment(ref outputs); };

            string? path = WaitFor(() => Native.HidPaths(d.Vid, d.Pid).FirstOrDefault(p => !before.Contains(p, StringComparer.OrdinalIgnoreCase)), 30_000);
            Check($"{d.Id}: its HID interface appears", path != null);
            if (path != null)
            {
                var neutral = new HMGamepadState();
                var pressed = new HMGamepadState
                {
                    Buttons = HMButton.A | HMButton.B | HMButton.X | HMButton.Y | HMButton.LeftBumper | HMButton.RightBumper | HMButton.Back | HMButton.Start,
                    Hat = HMHat.North,
                };
                var (idle, idleError) = ReadWhileSubmitting(c, path, neutral);
                var (held, heldError) = ReadWhileSubmitting(c, path, neutralOnly ? neutral : pressed);
                int readError = idleError != 0 ? idleError : heldError;
                Check($"{d.Id}: input arrives", idle != null && held != null && idle.Count > 0 && held.Count > 0 && readError == 0,
                      $"{idle?.Count ?? -1} and {held?.Count ?? -1} report(s) in 600 ms each{(readError != 0 ? $", a read failed with Win32 error {readError}" : "")}");
                var (idleAgain, againError) = ReadWhileSubmitting(c, path, neutral);
                var changed = idle != null && held != null ? SteadyDifferences(idle, held) : new List<int>();
                Check($"{d.Id}: the submitted state is what the host reads", changed.Count > 0,
                      changed.Count > 0 ? $"byte(s) {string.Join(",", changed)} differ between the neutral and pressed windows"
                                        : "no byte that is steady in each window differs between them");
                // The control: the same comparison between two neutral windows
                // finds nothing, so a steady byte that ignores the state is
                // not taken for the state.
                var spurious = idle != null && idleAgain != null ? SteadyDifferences(idle, idleAgain) : new List<int> { -1 };
                Check($"{d.Id}: two neutral windows show no difference", againError == 0 && idleAgain != null && idleAgain.Count > 0 && spurious.Count == 0,
                      spurious.Count == 0 ? "" : $"byte(s) {string.Join(",", spurious)} differ");
                int writeError = Native.WriteOutput(path, d.OutputId, d.Body);
                bool raised = WaitFor(() => Volatile.Read(ref outputs) > 0 ? "yes" : null, 3000) != null;
                Check($"{d.Id}: an output report raises OutputReceived", writeError == 0 && raised,
                      writeError != 0 ? $"WriteFile failed with Win32 error {writeError}" : "");
            }

            c.Dispose();
            if (path != null)
                Check($"{d.Id}: its HID interface goes away on dispose",
                      WaitFor(() => Native.HidPaths(d.Vid, d.Pid).Contains(path, StringComparer.OrdinalIgnoreCase) ? null : "gone", 20_000) != null);
        }

        Console.WriteLine($"  timing: LoadDefaultProfiles {catalogMs:F0} ms, first CreateController {firstControllerMs:F0} ms");
        return Finish();
    }

    /// <summary>Submits <paramref name="state"/> for a short settle, then
    /// reads the interface for 600 ms with the state still submitted every
    /// 8 ms. The handle is opened after the settle, so its queue holds only
    /// reports of this state.</summary>
    static (List<byte[]>? Reports, int ReadError) ReadWhileSubmitting(HMController c, string path, HMGamepadState state)
    {
        void Submit() { try { c.SubmitState(in state); } catch { } }
        for (int i = 0; i < 40; i++) { Submit(); Thread.Sleep(8); }
        return Native.CaptureReports(path, 600, Submit);
    }

    /// <summary>The byte positions that hold one value in every report of
    /// <paramref name="a"/>, one value in every report of <paramref name="b"/>,
    /// and different values between the two. Reports of different lengths
    /// are compared over the shortest length.</summary>
    static List<int> SteadyDifferences(List<byte[]> a, List<byte[]> b)
    {
        var found = new List<int>();
        if (a.Count == 0 || b.Count == 0) return found;
        int n = Math.Min(a.Min(r => r.Length), b.Min(r => r.Length));
        for (int i = 0; i < n; i++)
            if (a.All(r => r[i] == a[0][i]) && b.All(r => r[i] == b[0][i]) && a[0][i] != b[0][i]) found.Add(i);
        return found;
    }

    static int Finish()
    {
        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static string? WaitFor(Func<string?> probe, int ms)
    {
        var sw = Stopwatch.StartNew();
        string? v;
        while ((v = probe()) == null && sw.ElapsedMilliseconds < ms) Thread.Sleep(100);
        return v;
    }

    /// <summary>Another running program that embeds HIDMaestro: one with
    /// HIDMaestro.Core.dll loaded, or one a v2.0.0 owner record names. The
    /// record catches NativeAOT and single-file programs, which load no such
    /// DLL.</summary>
    static string? OtherSdkConsumer()
    {
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self) continue;
                foreach (ProcessModule m in p.Modules)
                    if (string.Equals(m.ModuleName, "HIDMaestro.Core.dll", StringComparison.OrdinalIgnoreCase))
                        return p.ProcessName;
            }
            catch { }
            finally { p.Dispose(); }
        }
        using var owners = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroOwners");
        foreach (string id in owners?.GetSubKeyNames() ?? Array.Empty<string>())
        {
            using var rec = owners!.OpenSubKey(id);
            if (rec?.GetValue("Pid") is not int pid || pid == self) continue;
            try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
            catch { }
        }
        return null;
    }
}
