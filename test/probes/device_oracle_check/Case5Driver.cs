// Case 5: the UMDF2 driver and the XUSB companion, natively.
//
// native\build.ps1 compiles driver.c, driver.h and companion.c against
// stand-ins for every WDF and Win32 call they make, once from the 9bd6279
// sources kept in native\oracle (driver_harness_oracle.exe) and once from
// the tree's driver\ (driver_harness_tree.exe). The harness drives them
// through EvtDeviceAdd, EvtIoDeviceControl and the threads they start,
// on a clock it advances to each wait's own deadline, and writes every
// output in NativeStreamReader's format.
//
// The harness runs one thread at a time, so a replay proves no locking: a
// change that moves work in or out of a lock replays identically. The
// lock-order sites only real threads exercise are listed in
// native\harness\hm_harness.h.
//
// This side writes the plan the harness runs. The registry values come
// from the SDK's own DeviceOrchestrator.WriteInstanceConfig
// (DeviceOrchestrator.cs:639-694), run against a registry hive the case
// owns (InstanceConfig below), and read back. What WriteInstanceConfig does
// not write comes from the SDK's helpers: the feature report ids, the read
// length and the report-id use from HidReportMap, the PID ids from
// PidReportIdExtractor, Switch Pro bodies from SwitchProPacker. Record runs
// the oracle harness and keeps the plan beside the golden file. Replay runs
// the tree's harness on the kept plan, and fails when the tree's SDK writes
// instance configuration the kept plan does not hold, naming each value,
// unless --plan-change lists exactly those differences. Then it runs the
// SDK's plan, so a converted driver meets the configuration it reads.
//
// --plan-change file, one difference a line, # starts a comment:
//   <profile-id> <value name> absent|<registry type>:<hex>
// the value the tree's SDK writes for that profile, or absent when it
// writes none.
//
// Each plan profile carries its HID device node's instance ID up to the
// token (DevnodePrefix), so the harness gives every device the instance ID
// the SDK gives its device node.
//
// A second plan holds the values a killed v2.0.0 program leaves under a v1
// controller's index (design note, decision (c)): dualsense-bt,
// amazon-luna-usb, xbox-360-wired and dualshock-3-full with the instance
// configuration 9bd6279's SDK writes, which is what a v1 program writes,
// and FeatureAnswers, XusbAnswers, XusbCompanion 1, LiveFeatureReport 1 and
// a cached input report ID list beside it (LeftValues.cs). The harness runs
// it with the version section absent and at protocol 3 (hm_case_stale.c).
// Record keeps that plan beside the golden file. Replay runs the kept v1
// configuration, since a v1 program's configuration does not change with
// the tree, with each left value the tree's SDK writes for that profile in
// place of LeftValues.cs's (StaleLeftValues). Record and replay both run
// the stale plan twice, with the left values and without them, and fail
// the case when any stream differs between the two runs: under the version
// contract no v1 controller's driver reads a left value.
//
// Replay also runs the tree's harness in its lifecycle mode on the same
// plan (hm_case_lifecycle.c), which checks rather than records: after a
// surprise removal with no removal after it, both drivers hold nothing of
// their index, their threads and timer have ended and a parked read is
// canceled, a rebalance stop and restart leaves the device working, no
// view is unmapped while a thread or timer that reads it runs, and nothing
// is closed twice. Record
// does not run it: 9bd6279's drivers keep everything until the removal.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal sealed class Case5Driver : IOracleCase
{
    public string Name => "case5-driver";

    /// <summary>The plan a recording ran, kept in the golden directory.</summary>
    const string PlanFile = "case5-plan.txt";

    /// <summary>The stale-value plan a recording ran, kept beside it.</summary>
    const string StalePlanFile = "case5-stale-plan.txt";

    /// <summary>The profiles the stale-value plan holds, the four the design
    /// note's stale cases name: a Sony profile with feature answers, a
    /// generic one, one with the XUSB companion, and the one
    /// LiveFeatureReport belongs to.</summary>
    static readonly string[] StaleProfiles = { "dualsense-bt", "amazon-luna-usb", "xbox-360-wired", "dualshock-3-full" };

    /// <summary>What a killed v2.0.0 program leaves in its configuration
    /// key (LeftValues.cs).</summary>
    static readonly RegValue[] StaleValues = LeftValues.All.Select(v => new RegValue(v.Name, v.Kind, v.Data)).ToArray();

    /// <summary>The --plan-change file a replay accepts, or null.</summary>
    public static string? PlanChangeFile;

    /// <summary>The change list's config entries (ChangeList.cs), which a
    /// replay accepts beside the --plan-change file's lines.</summary>
    public static List<ChangeList.Entry> ConfigChanges = new();

    /// <summary>The values the tree's SDK writes that no config entry pins,
    /// as config entry targets, for --write-changes.</summary>
    public static readonly List<string> ConfigUnlisted = new();

    static int s_runs;

    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);
    const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;

    public void Record(GoldenCase golden, Options options)
    {
        // A directory of this run's own, so two runs from one build never
        // share a plan or an output, removed once read (review F16).
        string work = Path.Combine(AppContext.BaseDirectory, "case5",
            $"run-{Environment.ProcessId}-{Interlocked.Increment(ref s_runs)}");
        Directory.CreateDirectory(work);
        try { Run(golden, options, work); }
        finally { DeleteDirectory(work); }
    }

    void Run(GoldenCase golden, Options options, string work)
    {
        string plan = Path.Combine(work, "plan.txt");
        string output = Path.Combine(work, options.Replay ? "tree.bin" : "oracle.bin");
        string kept = Path.Combine(options.GoldenDir, PlanFile);

        var built = BuildPlan(work);
        string text;
        Dictionary<string, List<RegValue>> keptConfig;
        var listed = new List<string>();
        if (options.Replay)
        {
            if (!File.Exists(kept)) throw new FileNotFoundException($"{kept} is missing: record case 5 first.");
            string keptText = File.ReadAllText(kept, Encoding.UTF8);
            keptConfig = ParseConfig(keptText);
            var diffs = ConfigDiffs(keptConfig, built.Config);
            if (diffs.Count == 0)
                text = keptText;
            else
            {
                if (PlanChangeFile != null) listed = ReadPlanChange(PlanChangeFile);
                // The last config entry of a profile's value pins it, and the
                // earlier ones are its chain's history (ChangeList.cs).
                var current = ChangeList.Current(ConfigChanges);
                listed.AddRange(current.Select(e => e.Target));
                foreach (var e in ConfigChanges) e.Used = !current.Contains(e) || diffs.Contains(e.Target);
                var unlisted = diffs.Except(listed).ToList();
                var missing = listed.Except(diffs).ToList();
                ConfigUnlisted.Clear();
                ConfigUnlisted.AddRange(unlisted);
                if (unlisted.Count > 0 || missing.Count > 0)
                {
                    var sb = new StringBuilder("the tree's SDK writes instance configuration the kept plan does not hold:");
                    foreach (var d in unlisted) sb.Append("\n      not listed: ").Append(d);
                    foreach (var d in missing) sb.Append("\n      listed, not written: ").Append(d);
                    throw new InvalidOperationException(sb.ToString());
                }
                Console.WriteLine($"    the tree's SDK writes the {diffs.Count} listed difference(s): the replay runs the SDK's plan");
                text = built.Text;
            }
        }
        else
        {
            text = built.Text;
            keptConfig = built.Config;
            Directory.CreateDirectory(options.GoldenDir);
            File.WriteAllText(kept, text, new UTF8Encoding(false));
        }
        File.WriteAllText(plan, text, new UTF8Encoding(false));

        // --explain: the plan's text, of which the golden file keeps the
        // digest, and the Switch Pro streams the driver serves from its
        // bodies.
        var planExplainer = new PlanExplainer();
        golden.ExplainerFor = name => name == "plan" ? planExplainer
            : name.StartsWith("switch/", StringComparison.Ordinal) ? planExplainer.Driver() : null;
        golden.Stream("plan");
        golden.Note("text", Encoding.UTF8.GetBytes(text));
        golden.Out("sha256", System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        // Both plans' instance configuration (review F7): the kept plan's,
        // and what the tree's SDK wrote for the kept plan's profiles with
        // each listed difference taken back (review N4). The second equals
        // the first only when the SDK wrote exactly the kept values and the
        // listed ones, which the check above enforces, so a fault in that
        // check shows here as a difference.
        EmitConfig(golden, "plan/instance-config-kept", keptConfig);
        EmitConfig(golden, "plan/instance-config-sdk", TakeBack(keptConfig, built.Config, listed));
        EmitMicrosoftProfiles(golden);

        string exe = Path.Combine(options.NativeDir, options.Replay ? "driver_harness_tree.exe" : "driver_harness_oracle.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException($"{exe} is not built: run native\\build.ps1 -Out {options.NativeDir}");
        RunHarness(exe, plan, output, options.Seed);
        NativeStreamReader.ReadInto(output, golden);
        RunStale(golden, options, work, exe);
        if (options.Replay)
            RunHarness(exe, plan, Path.Combine(work, "lifecycle.bin"), options.Seed, "--lifecycle", lastLineOnly: true);
    }

    void RunStale(GoldenCase golden, Options options, string work, string exe)
    {
        string kept = Path.Combine(options.GoldenDir, StalePlanFile);
        var all = ProfileDatabase.LoadEmbedded().All;
        var profiles = StaleProfiles
            .Select(id => all.FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException($"The catalog has no {id}."))
            .ToList();
        // What this tree's SDK writes for each stale profile: the base
        // configuration at record, and at both the left values it writes
        // itself.
        var sdk = InstanceConfig(profiles, work);
        string keptText;
        if (options.Replay)
        {
            if (!File.Exists(kept)) throw new FileNotFoundException($"{kept} is missing: record case 5 first.");
            keptText = File.ReadAllText(kept, Encoding.UTF8);
        }
        else
        {
            keptText = BuildStalePlan(profiles, sdk);
            File.WriteAllText(kept, keptText, new UTF8Encoding(false));
        }
        golden.Stream("stale/plan");
        golden.Out("sha256", System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(keptText)));

        var (text, fromSdk) = StaleLeftValues(keptText, sdk);
        foreach (var v in fromSdk) Console.WriteLine($"    stale left value from the tree's SDK: {v}");
        string plan = Path.Combine(work, "stale-plan.txt");
        string output = Path.Combine(work, options.Replay ? "stale-tree.bin" : "stale-oracle.bin");
        File.WriteAllText(plan, text, new UTF8Encoding(false));
        RunHarness(exe, plan, output, options.Seed, "--stale");
        int first = golden.Streams.Count;
        NativeStreamReader.ReadInto(output, golden);
        var withLeft = golden.Streams.Skip(first).ToList();

        // The same plan without the left values (review finding 2). A driver
        // that reads any of them answers otherwise in some stream.
        string basePlan = Path.Combine(work, "stale-base-plan.txt");
        string baseOutput = Path.Combine(work, options.Replay ? "stale-base-tree.bin" : "stale-base-oracle.bin");
        File.WriteAllText(basePlan, WithoutLeftValues(keptText), new UTF8Encoding(false));
        RunHarness(exe, basePlan, baseOutput, options.Seed, "--stale");
        var without = new GoldenCase("stale-base", options.Seed);
        NativeStreamReader.ReadInto(baseOutput, without);
        var baseByName = without.Streams.ToDictionary(s => s.Name, StringComparer.Ordinal);
        var differ = new List<string>();
        foreach (var s in withLeft)
        {
            if (!baseByName.Remove(s.Name, out var b)) differ.Add($"{s.Name}: not in the run without the left values");
            else if (s.Count != b.Count || s.Digest != b.Digest) differ.Add($"{s.Name}: {s.Count} outputs with the left values, {b.Count} without, digests {s.Digest[..16]} and {b.Digest[..16]}");
        }
        differ.AddRange(baseByName.Keys.Select(n => $"{n}: only in the run without the left values"));
        if (differ.Count > 0)
        {
            foreach (var d in differ) Console.WriteLine($"  [DIFF] case5-driver/{d}");
            throw new InvalidOperationException($"the driver reads the values a killed v2.0.0 program leaves: {differ.Count} stale stream(s) differ with and without them");
        }
        Console.WriteLine($"    stale: {withLeft.Count} streams identical with and without the left values");
    }

    static string BuildStalePlan(List<ControllerProfile> profiles, Dictionary<string, List<RegValue>> config)
    {
        var sb = new StringBuilder("hm-driver-plan 1\n");
        foreach (var p in profiles)
        {
            var values = config[p.Id].Where(v => StaleValues.All(x => x.Name != v.Name)).Concat(StaleValues)
                .OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
            AddProfile(sb, p.RequiresXusbCompanion ? "stale-xusb" : "stale", p, values);
        }
        return sb.ToString();
    }

    /// <summary>The stale plan with each left value the tree's SDK writes
    /// for that profile in place of the kept one, and those values as
    /// "id/name" (review finding 2). The design note fixes only each blob's
    /// header (length and format 4) and leaves the layout to DD-2, so a
    /// blob the SDK writes is the valid v2.0.0 value a killed program
    /// leaves. A value counts as written when it is not empty and, for a
    /// DWORD flag, not 0, since a 0 flag leaves nothing for a driver to
    /// act on and the fixed 1 tests more.</summary>
    static (string Text, List<string> FromSdk) StaleLeftValues(string plan, Dictionary<string, List<RegValue>> sdk)
    {
        var fromSdk = new List<string>();
        var lines = plan.Split('\n');
        string? id = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var w = lines[i].TrimEnd('\r').Split(' ');
            if (w[0] == "profile" && w.Length == 3) { id = w[2]; continue; }
            if (w[0] == "end") { id = null; continue; }
            if (w[0] != "value" || w.Length != 4 || id == null || StaleValues.All(x => x.Name != w[1])) continue;
            if (!sdk.TryGetValue(id, out var values)) continue;
            int at = values.FindIndex(x => x.Name == w[1]);
            if (at < 0) continue;
            var v = values[at];
            bool written = v.Kind == LeftValues.RegDword ? v.Data.Length == 4 && BitConverter.ToUInt32(v.Data) != 0 : v.Data.Length > 0;
            if (!written) continue;
            lines[i] = $"value {v.Name} {v.Kind} {Convert.ToHexString(v.Data)}";
            fromSdk.Add($"{id}/{v.Name}");
        }
        return (string.Join('\n', lines), fromSdk);
    }

    /// <summary>The stale plan without the left values: the v1
    /// configuration alone.</summary>
    static string WithoutLeftValues(string plan) =>
        string.Join('\n', plan.Split('\n').Where(l =>
        {
            var w = l.TrimEnd('\r').Split(' ');
            return !(w[0] == "value" && w.Length == 4 && StaleValues.Any(x => x.Name == w[1]));
        }));

    /// <summary>The harness as a child process with no window. The error
    /// mode is set first, so a fault or a loader failure ends in an exit
    /// code rather than a dialog. The child inherits it. With
    /// lastLineOnly the console gets the summary line alone, not a line per
    /// check.</summary>
    internal static void RunHarness(string exe, string plan, string output, ulong seed, string? mode = null, bool lastLineOnly = false)
    {
        SetErrorMode(SetErrorMode(0) | SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        if (mode != null) psi.ArgumentList.Add(mode);
        psi.ArgumentList.Add(plan);
        psi.ArgumentList.Add(output);
        psi.ArgumentList.Add(seed.ToString());
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        var stderr = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        string err = stderr.Result.Trim();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} exited {p.ExitCode}: {(err.Length > 0 ? err : stdout.Trim())}");
        string shown = stdout.Trim();
        if (lastLineOnly) shown = shown[(shown.LastIndexOf('\n') + 1)..].Trim();
        Console.WriteLine($"    {Path.GetFileName(exe)}: {shown}");
    }

    internal static void DeleteDirectory(string dir)
    {
        for (int attempt = 0; attempt < 40 && Directory.Exists(dir); attempt++)
        {
            try { Directory.Delete(dir, true); }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }

    // ── the plan ─────────────────────────────────────────────────────────

    internal readonly record struct RegValue(string Name, int Kind, byte[] Data)
    {
        public string Show() => $"{Kind}:{(Data.Length == 0 ? "-" : Convert.ToHexString(Data))}";
    }

    sealed record Built(string Text, Dictionary<string, List<RegValue>> Config);

    static Built BuildPlan(string work)
    {
        var all = ProfileDatabase.LoadEmbedded().All;
        ControllerProfile ById(string id) =>
            all.FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException($"The catalog has no {id}.");

        var subjects = new List<(string Group, ControllerProfile Profile)> { ("switch", ById("switch-pro")) };
        // Every Sony profile on the UMDF2 backend: the driver's feature
        // table gates on vendor 0x054C (driver.c:3126).
        subjects.AddRange(all.Where(p => p.VendorId == 0x054C && !p.RequiresUsbipBackend)
            .OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => ("sony", p)));
        subjects.Add(("control", ById("heusinkveld-ultimate-pedals")));
        subjects.Add(("ds3", ById("dualshock-3-full")));
        // The UMDF2 Valve profiles, whose feature reports the converted
        // driver answers from a table.
        subjects.AddRange(all.Where(p => p.VendorId == 0x28DE && !p.RequiresUsbipBackend)
            .OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => ("steam", p)));
        var xusb = all.Where(p => p.RequiresXusbCompanion).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        if (xusb.Count == 0) throw new InvalidOperationException("The catalog has no profile with an XUSB companion.");
        subjects.AddRange(xusb.Select(p => ("xusb", p)));
        // A profile on the xinputhid path, whose HID device node is the SWD
        // one with the driver PID in its enumerator (DevnodePrefix).
        subjects.Add(("xinputhid", ById("xbox-series-xs")));

        var config = InstanceConfig(subjects.Select(s => s.Profile).DistinctBy(p => p.Id).ToList(), work);
        var sb = new StringBuilder("hm-driver-plan 1\n");
        foreach (var (group, p) in subjects) AddProfile(sb, group, p, config[p.Id]);
        foreach (var (name, body) in SwitchBodies())
            sb.Append("body ").Append(name).Append(' ').Append(Convert.ToHexString(body)).Append('\n');
        return new Built(sb.ToString(), config);
    }

    internal static void AddProfile(StringBuilder sb, string group, ControllerProfile p, List<RegValue> values, string? name = null)
    {
        byte[] descriptor = p.GetDescriptorBytes() ?? throw new InvalidOperationException($"{p.Id} has no descriptor.");
        sb.Append("profile ").Append(group).Append(' ').Append(name ?? p.Id).Append('\n');
        sb.Append("devnode ").Append(DevnodePrefix(p)).Append('\n');
        foreach (var v in values)
            sb.Append("value ").Append(v.Name).Append(' ').Append(v.Kind).Append(' ')
              .Append(v.Data.Length == 0 ? "-" : Convert.ToHexString(v.Data)).Append('\n');

        var map = HidReportMap.Parse(descriptor);
        var features = map.Reports.Where(r => r.Kind == HidReportKind.Feature).Select(r => r.ReportId).Distinct().OrderBy(b => b);
        sb.Append("features");
        foreach (byte id in features) sb.Append(' ').Append(id.ToString("X2"));
        sb.Append('\n');
        sb.Append("reportids ").Append(map.UsesReportIds ? 1 : 0).Append('\n');
        // v2.0.0 writes no Bluetooth value, so the plan carries the
        // connection the Sony cases name their streams by.
        sb.Append("bluetooth ").Append(string.Equals(p.Connection, "bluetooth", StringComparison.OrdinalIgnoreCase) ? 1 : 0).Append('\n');
        int readLength = map.Reports.Where(r => r.Kind == HidReportKind.Input).Select(r => r.FrameBytes).DefaultIfEmpty(0).Max();
        sb.Append("readlength ").Append(readLength).Append('\n');
        // The ids HMController writes into the PID section when a consumer
        // first publishes PID state (HMController.cs:346-349).
        var rids = PidReportIdExtractor.Extract(descriptor);
        sb.Append($"pidrids {rids.PoolReportId:X2} {rids.StateReportId:X2} {rids.BlockLoadReportId:X2} " +
                  $"{rids.CreateNewEffectReportId:X2} {rids.BlockFreeReportId:X2} {rids.DeviceControlReportId:X2}\n");
        sb.Append("end\n");
    }

    /// <summary>The HID device node's instance ID up to its token, as the
    /// SDK builds it (review finding 1). DeviceOrchestrator creates it on
    /// the xinputhid path for a profile with an upper filter and through
    /// DeviceNodeCreator otherwise (DeviceOrchestrator.cs:1360-1373). The
    /// xinputhid path's is SWD\HIDMAESTRO_VID_vvvv_PID_pppp&amp;IG_00 with the
    /// driver PID when the profile has one (:748-761, 818-836).
    /// DeviceNodeCreator's is ROOT\VID_vvvv&amp;PID_pppp&amp;IG_00 for
    /// Microsoft's vendor ID and ROOT\HIDClass otherwise
    /// (DeviceNodeCreator.cs:89-133). Instance IDs compare without regard
    /// to case, and the ROOT enumerator is given in capitals.</summary>
    internal static string DevnodePrefix(ControllerProfile p)
    {
        string vid = $"{p.VendorId:X4}";
        string pid = $"{p.ProductId:X4}";
        string hwPid = p.DriverPid != null ? $"{Convert.ToUInt16(p.DriverPid, 16):X4}" : pid;
        if (p.UsesUpperFilter) return $@"SWD\HIDMAESTRO_VID_{vid}_PID_{hwPid}&IG_00";
        if (p.VendorId == 0x045E) return $@"ROOT\VID_{vid}&PID_{pid}&IG_00";
        return @"ROOT\HIDCLASS";
    }

    // ── WriteInstanceConfig against a hive the case owns ─────────────────
    //
    // RegLoadAppKey loads a hive file as an application hive, which "is not
    // visible to other processes" and needs no privilege, creating the file
    // when it does not exist (Microsoft Learn, RegLoadAppKeyW).
    // RegOverridePredefKey then maps HKEY_LOCAL_MACHINE to that hive for
    // this process, until it is called again with NULL (Microsoft Learn,
    // RegOverridePredefKey). Every HKLM access WriteInstanceConfig makes
    // through Registry.LocalMachine lands in the file. Before anything
    // writes, the case checks the redirection holds: HKLM must not show the
    // machine's SOFTWARE\Microsoft\Windows NT\CurrentVersion, and a value
    // written through HKLM must be read back through the hive's own handle.
    // If either fails it stops without writing HKLM.
    //
    // WriteInstanceConfig ends with RegistryLedger.ClaimJoystickOem, which
    // takes the machine-wide ledger lock in the administrators' private
    // namespace (DeviceOrchestrator.cs:690-691, RegistryLedger.cs:192-196).
    // It is skipped for a keyboard or mouse, so the call runs with the
    // profile's Type set to "keyboard" and restored after. No value the
    // call writes into Controller<N> reads Type. When the process is not
    // elevated, the ledger lock throws UnauthorizedAccessException before
    // it creates anything (AdminNamespace cannot open its namespace), so
    // each profile is also written once with its own Type, the exception
    // taken, and the two value sets must match: that checks the claim of
    // the sentence before.

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegLoadAppKeyW(string file, out IntPtr key, int samDesired, int options, int reserved);
    [DllImport("advapi32.dll")]
    static extern int RegOverridePredefKey(IntPtr key, IntPtr newKey);
    [DllImport("advapi32.dll")]
    static extern int RegCloseKey(IntPtr key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegQueryValueExW(SafeRegistryHandle key, string name, IntPtr reserved, out int type, byte[]? data, ref int size);

    static readonly IntPtr HKEY_LOCAL_MACHINE = unchecked((IntPtr)(int)0x80000002);
    const int KEY_ALL_ACCESS = 0xF003F;
    const string InstanceKey = @"SOFTWARE\HIDMaestro\Controller0";

    static readonly MethodInfo s_writeInstanceConfig =
        typeof(DeviceOrchestrator).GetMethod("WriteInstanceConfig", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new MissingMethodException("DeviceOrchestrator.WriteInstanceConfig");

    internal static Dictionary<string, List<RegValue>> InstanceConfig(List<ControllerProfile> profiles, string work)
    {
        string hive = Path.Combine(work, "instance-config.hive");
        int rc = RegLoadAppKeyW(hive, out IntPtr appKey, KEY_ALL_ACCESS, 0, 0);
        if (rc != 0) throw new InvalidOperationException($"RegLoadAppKeyW failed with {rc}");
        var result = new Dictionary<string, List<RegValue>>(StringComparer.Ordinal);
        try
        {
            using var hiveRoot = RegistryKey.FromHandle(new SafeRegistryHandle(appKey, ownsHandle: false));
            rc = RegOverridePredefKey(HKEY_LOCAL_MACHINE, appKey);
            if (rc != 0) throw new InvalidOperationException($"RegOverridePredefKey failed with {rc}");
            try
            {
                using (var machine = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (machine != null) throw new InvalidOperationException("HKLM is not redirected: nothing was written");
                using (var probe = Registry.LocalMachine.CreateSubKey("HmCase5Probe"))
                    probe.SetValue("Probe", 1, RegistryValueKind.DWord);
                using (var back = hiveRoot.OpenSubKey("HmCase5Probe"))
                    if (back?.GetValue("Probe") is not int one || one != 1)
                        throw new InvalidOperationException("a value written through HKLM did not land in the case's hive");
                Registry.LocalMachine.DeleteSubKeyTree("HmCase5Probe");

                bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
                foreach (var p in profiles)
                {
                    var values = Write(p, asKeyboard: true);
                    if (!elevated)
                    {
                        var own = Write(p, asKeyboard: false);
                        if (ConfigDiffs(new() { [p.Id] = values }, new() { [p.Id] = own }).FirstOrDefault() is string d)
                            throw new InvalidOperationException($"WriteInstanceConfig writes another value when Type is keyboard: {d}");
                    }
                    result[p.Id] = values;
                }
            }
            finally { RegOverridePredefKey(HKEY_LOCAL_MACHINE, IntPtr.Zero); }
        }
        finally { RegCloseKey(appKey); }
        return result;
    }

    static List<RegValue> Write(ControllerProfile p, bool asKeyboard)
    {
        Registry.LocalMachine.DeleteSubKeyTree("SOFTWARE", throwOnMissingSubKey: false);
        string type = p.Type;
        if (asKeyboard) p.Type = "keyboard";
        // v2.0.0 passes the controller's identity, whose entry the call
        // writes beside the configuration.
        object[] args = s_writeInstanceConfig.GetParameters().Length == 3
            ? new object[] { 0, p, DeviceIdentity.ForIndex(0) } : new object[] { 0, p };
        try { s_writeInstanceConfig.Invoke(null, args); }
        catch (TargetInvocationException e) when (!asKeyboard && e.InnerException is UnauthorizedAccessException) { }
        finally { p.Type = type; }
        using var key = Registry.LocalMachine.OpenSubKey(InstanceKey)
            ?? throw new InvalidOperationException($"{p.Id}: WriteInstanceConfig wrote no {InstanceKey}");
        var values = new List<RegValue>();
        foreach (string name in key.GetValueNames().OrderBy(n => n, StringComparer.Ordinal))
        {
            int size = 0;
            RegQueryValueExW(key.Handle, name, IntPtr.Zero, out int kind, null, ref size);
            var data = new byte[size];
            rc(RegQueryValueExW(key.Handle, name, IntPtr.Zero, out kind, data, ref size), name);
            values.Add(new RegValue(name, kind, data.AsSpan(0, size).ToArray()));
        }
        Registry.LocalMachine.DeleteSubKeyTree("SOFTWARE", throwOnMissingSubKey: false);
        return values;

        static void rc(int code, string name)
        {
            if (code != 0) throw new InvalidOperationException($"RegQueryValueExW({name}) failed with {code}");
        }
    }

    /// <summary>The instance values a plan holds, by profile.</summary>
    static Dictionary<string, List<RegValue>> ParseConfig(string plan)
    {
        var map = new Dictionary<string, List<RegValue>>(StringComparer.Ordinal);
        List<RegValue>? cur = null;
        foreach (var raw in plan.Split('\n'))
        {
            var w = raw.TrimEnd('\r').Split(' ');
            if (w[0] == "profile") cur = map.TryAdd(w[2], new()) ? map[w[2]] : null;
            else if (w[0] == "value" && cur != null)
                cur.Add(new RegValue(w[1], int.Parse(w[2]), w[3] == "-" ? Array.Empty<byte>() : Convert.FromHexString(w[3])));
            else if (w[0] == "end") cur = null;
        }
        return map;
    }

    /// <summary>Each value the SDK writes otherwise than the kept plan holds,
    /// as a --plan-change line, for the profiles the kept plan holds. A
    /// profile the probe added since is no change to WriteInstanceConfig and
    /// has no recording to replay. One the SDK no longer configures shows
    /// each of its values absent.</summary>
    static List<string> ConfigDiffs(Dictionary<string, List<RegValue>> kept, Dictionary<string, List<RegValue>> sdk)
    {
        var diffs = new List<string>();
        foreach (var id in kept.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var a = kept.TryGetValue(id, out var ka) ? ka.ToDictionary(v => v.Name) : new();
            var b = sdk.TryGetValue(id, out var kb) ? kb.ToDictionary(v => v.Name) : new();
            foreach (var name in a.Keys.Union(b.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                bool hasA = a.TryGetValue(name, out var va), hasB = b.TryGetValue(name, out var vb);
                if (hasA && hasB && va.Kind == vb.Kind && va.Data.AsSpan().SequenceEqual(vb.Data)) continue;
                diffs.Add($"{id} {name} {(hasB ? vb.Show() : "absent")}");
            }
        }
        return diffs;
    }

    /// <summary>The SDK's values for each profile the kept plan holds, with
    /// each listed difference taken back: the kept value restored, or the
    /// value dropped where the kept plan has none. Ordered by name, as
    /// Write reads them.</summary>
    static Dictionary<string, List<RegValue>> TakeBack(Dictionary<string, List<RegValue>> kept,
                                                       Dictionary<string, List<RegValue>> sdk, List<string> listed)
    {
        var result = new Dictionary<string, List<RegValue>>(StringComparer.Ordinal);
        foreach (var (id, keptValues) in kept)
        {
            var values = sdk.TryGetValue(id, out var s) ? s.ToDictionary(v => v.Name, StringComparer.Ordinal) : new();
            var keptByName = keptValues.ToDictionary(v => v.Name, StringComparer.Ordinal);
            foreach (var w in listed.Select(l => l.Split(' ')).Where(w => w.Length == 3 && w[0] == id))
            {
                if (keptByName.TryGetValue(w[1], out var k)) values[w[1]] = k;
                else values.Remove(w[1]);
            }
            result[id] = values.Values.OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
        }
        return result;
    }

    static List<string> ReadPlanChange(string path) =>
        File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToList();

    static void EmitConfig(GoldenCase golden, string stream, Dictionary<string, List<RegValue>> config)
    {
        golden.Stream(stream);
        foreach (var id in config.Keys.OrderBy(x => x, StringComparer.Ordinal))
            foreach (var v in config[id])
            {
                byte[] name = Encoding.ASCII.GetBytes($"{id}/{v.Name}");
                var rec = new byte[name.Length + 5 + v.Data.Length];
                name.CopyTo(rec, 0);
                BitConverter.TryWriteBytes(rec.AsSpan(name.Length + 1, 4), v.Kind);
                v.Data.CopyTo(rec, name.Length + 5);
                golden.Out("value", rec);
            }
    }

    /// <summary>Every catalog profile with Microsoft's vendor id, with the
    /// SDK's companion decision (ControllerProfile.cs:404-423): the three
    /// keyboards make no companion. The harness cannot show a companion's
    /// creation, which DeviceOrchestrator decides, so the decision is
    /// recorded where it is made.</summary>
    static void EmitMicrosoftProfiles(GoldenCase golden)
    {
        golden.Stream("plan/microsoft-profiles");
        foreach (var p in ProfileDatabase.LoadEmbedded().All.Where(p => p.VendorId == 0x045E).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            byte[] id = Encoding.ASCII.GetBytes(p.Id);
            var rec = new byte[id.Length + 4];
            id.CopyTo(rec, 0);
            rec[id.Length + 1] = (byte)(p.RequiresXusbCompanion ? 1 : 0);
            rec[id.Length + 2] = (byte)(p.IsXboxBranded ? 1 : 0);
            rec[id.Length + 3] = (byte)(p.IsKeyboardOrMouse ? 1 : 0);
            golden.Out("profile", rec);
        }
    }

    /// <summary>Switch Pro bodies as SubmitState writes them
    /// (HMController.cs:757-764): SwitchProPacker.BuildBody on states with
    /// each button alone, each hat direction, sticks at their edges and
    /// motion in both directions.</summary>
    static IEnumerable<(string, byte[])> SwitchBodies()
    {
        foreach (var x in SwitchBodyInputs())
        {
            var b = new byte[SwitchProPacker.BodySize];
            var s = x.State;
            SwitchProPacker.BuildBody(in s, x.Lx, x.Ly, x.Rx, x.Ry, b);
            yield return (x.Name, b);
        }
    }

    /// <summary>The input of each body the plan carries, by name.</summary>
    internal static IEnumerable<(string Name, HMGamepadState State, double Lx, double Ly, double Rx, double Ry)> SwitchBodyInputs()
    {
        yield return ("neutral", new HMGamepadState(), 0.5, 0.5, 0.5, 0.5);
        for (int i = 0; i < 14; i++)
            yield return ($"button{i}", new HMGamepadState { Buttons = (HMButton)(1u << i) }, 0.5, 0.5, 0.5, 0.5);
        for (int h = 1; h <= 8; h++)
            yield return ($"hat{h}", new HMGamepadState { Hat = (HMHat)h }, 0.5, 0.5, 0.5, 0.5);
        double[] edges = { 0.0, 0.25, 0.75, 1.0 };
        foreach (double e in edges)
        {
            yield return ($"left-x-{e:0.00}", new HMGamepadState(), e, 0.5, 0.5, 0.5);
            yield return ($"left-y-{e:0.00}", new HMGamepadState(), 0.5, e, 0.5, 0.5);
            yield return ($"right-x-{e:0.00}", new HMGamepadState(), 0.5, 0.5, e, 0.5);
            yield return ($"right-y-{e:0.00}", new HMGamepadState(), 0.5, 0.5, 0.5, e);
        }
        yield return ("motion-plus", new HMGamepadState
        {
            AccelGX = 1, AccelGY = 2, AccelGZ = -1, GyroDpsX = 100, GyroDpsY = -250, GyroDpsZ = 2000,
        }, 0.5, 0.5, 0.5, 0.5);
        yield return ("motion-minus", new HMGamepadState
        {
            AccelGX = -8, AccelGY = -0.5f, AccelGZ = 8, GyroDpsX = -2000, GyroDpsY = 45.5f, GyroDpsZ = -1,
        }, 0.5, 0.5, 0.5, 0.5);
        yield return ("everything", new HMGamepadState
        {
            Buttons = (HMButton)0x3FFF, Hat = HMHat.SouthWest, AccelGX = 0.25f, GyroDpsY = 10,
        }, 0.1, 0.9, 0.3, 0.7);
    }
}
