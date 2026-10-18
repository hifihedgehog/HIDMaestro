// The modes the v2.0.0 conversion's baselines need beside the v1.13.0
// ones: feature answers on both transports, a device-built USB/IP report,
// the driver install a baseline session starts with, and the offline
// self-test of the parsing these modes share.
//
// A profile argument is a catalog id, optionally followed by reports in
// hex, each after an @, report ID first:
// "switch-pro@010000014040000140400330". What the reports are for depends
// on the mode: output reports written before e2e measures, the feature
// report SET before each timed GET in feature, the output report output
// sends.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    sealed record ProfileArg(string Text, string Id, byte[][] Reports);

    /// <summary>Parse the profile arguments, or print why not and return
    /// null. Every id must be in the embedded catalog, so nothing is
    /// created before a typo is found.</summary>
    static List<ProfileArg>? ParseProfileArgs(IEnumerable<string> args, string mode)
    {
        var list = new List<ProfileArg>();
        var catalog = ProfileDatabase.LoadEmbedded();
        foreach (string text in args)
        {
            var parts = text.Split('@');
            byte[][] reports;
            try { reports = parts.Skip(1).Select(Convert.FromHexString).ToArray(); }
            catch (FormatException)
            {
                Console.WriteLine($"{mode}: {text}: a report after @ is not hex");
                return null;
            }
            if (reports.Any(r => r.Length == 0))
            {
                Console.WriteLine($"{mode}: {text}: an empty report after @");
                return null;
            }
            if (!catalog.All.Any(p => p.Id == parts[0]))
            {
                Console.WriteLine($"{mode}: no profile {parts[0]} in the catalog");
                return null;
            }
            list.Add(new ProfileArg(text, parts[0], reports));
        }
        return list;
    }

    static bool Elevated(string mode)
    {
        using var me = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        Console.WriteLine($"{mode}: [REFUSED] it needs elevation. Nothing was created");
        return false;
    }

#if !HM_BASE
    // ── The driver a baseline session runs on ────────────────────────────

    /// <summary>InstallDriver, then proof that the driver store's newest
    /// package of each INF is the one this SDK embeds and that the install
    /// record names this SDK's payload. A newer driver stays installed under
    /// the never-downgrade policy, and every measurement after it would
    /// then be of that driver: exit 4 says so, with what is installed.
    /// Returns 0, 3 without elevation, 4 when the installed driver is not
    /// this SDK's.</summary>
    static int Install()
    {
        if (!Elevated("install")) return 3;
        using var ctx = new HMContext();
        ctx.InstallDriver();
        Console.WriteLine($"  InstallDriver: {ctx.LastDriverInstall}");
        bool ok = ctx.LastDriverInstall is HMDriverInstallOutcome.AlreadyCurrent or HMDriverInstallOutcome.Installed
            or HMDriverInstallOutcome.Upgraded;
        foreach (string inf in new[] { "hidmaestro.inf", "hidmaestro_xusb.inf" })
        {
            var embedded = DriverDeploy.EmbeddedStamp(inf);
            var installed = DriverDeploy.InstalledStamps(inf);
            var newest = installed.Count == 0 ? (DriverDeploy.DriverStamp?)null : installed.Max();
            bool same = newest is { } n && n.CompareTo(embedded) == 0;
            ok &= same;
            Console.WriteLine(FormattableString.Invariant(
                $"  [{(same ? "PASS" : "FAIL")}] {inf}: embedded {embedded} of {embedded.Date:yyyy-MM-dd}, newest in the driver store {(newest is { } m ? $"{m} of {m.Date:yyyy-MM-dd}" : "none")}, all: {string.Join(", ", installed.OrderBy(s => s).Select(s => FormattableString.Invariant($"{s} of {s.Date:yyyy-MM-dd}")))}"));
        }
        string? recorded = DriverBuilder.InstalledManifestHash();
        bool manifest = string.Equals(recorded, EmbeddedManifest.Sha256Hex, StringComparison.OrdinalIgnoreCase);
        ok &= manifest;
        Console.WriteLine($"  [{(manifest ? "PASS" : "FAIL")}] the install record names payload {recorded ?? "(none)"}, this SDK's is {EmbeddedManifest.Sha256Hex}");
        Console.WriteLine($"install: {(ok ? "PASS" : "FAIL, the driver installed is not this SDK's")}");
        return ok ? 0 : 4;
    }
#endif

    // ── Feature answers ──────────────────────────────────────────────────

    const int FeatureCallsPerRun = 50;

    /// <summary>Feature answers timed as a host asks for them. A UMDF2
    /// profile is created and asked through HidD_GetFeature on its own HID
    /// interface, with a buffer of the interface's FeatureReportByteLength,
    /// as Learn's HidD_GetFeature asks. A USB/IP profile is asked in this
    /// process, as the usbip mode drives one, with this probe in the host
    /// controller's place: GET_REPORT(Feature) on endpoint 0 with the
    /// report's declared length, the HID interface and its report
    /// descriptor read from the device first. Each report ID the descriptor
    /// declares is asked, or, when the argument carries reports, each
    /// report is SET first and the same ID asked after it, both timed.
    /// Returns 0, 1 for a bad argument, 3 without elevation.</summary>
    static int Feature(int runs, string[] args)
    {
        var list = ParseProfileArgs(args.Length > 0 ? args : new[] { "dualsense-bt", "dualshock-3-full", "dualsense-composite" }, "feature");
        if (list == null) return 1;
        if (!Elevated("feature")) return 3;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        bool installed = false;
        UsbipServer? server = null;
        foreach (var p in list)
        {
            var profile = ctx.GetProfile(p.Id)!;
            if (profile.RequiresUsbipBackend)
            {
                server ??= UsbipServer.GetOrStart();
                FeatureUsbip(server, profile.Inner, p, runs);
                continue;
            }
            if (!installed) { ctx.InstallDriver(); installed = true; }
            FeatureUmdf(ctx, profile, p, runs);
            if (s_wrongDriver) return 5;
        }
        return 0;
    }

    static void FeatureUmdf(HMContext ctx, HMProfile profile, ProfileArg p, int runs)
    {
        using var c = ctx.CreateController(profile);
        if (!OnEmbeddedDriver(c, p.Text)) return;
        using var dev = OpenOwnInterface(c, d => d.FeatureLength > 0, out string why);
        if (dev == null) { Check($"{p.Text}: a HID interface with feature reports", false, why); return; }
        var declared = DeclaredReports(profile.GetDescriptorBytes()!, ReportKind.Feature);
        var buf = new byte[dev.FeatureLength];
        Console.WriteLine($"  {p.Text}: FeatureReportByteLength {dev.FeatureLength}, declared feature IDs {string.Join(" ", declared.Keys.Select(k => $"0x{k:X2}"))}");

        if (p.Reports.Length == 0)
        {
            foreach (byte id in declared.Keys)
                TimeCalls($"{p.Text}: HidD_GetFeature 0x{id:X2}", runs, () =>
                {
                    Array.Clear(buf);
                    buf[0] = id;
                    return HidD_GetFeature(dev.Handle, buf, buf.Length) ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                });
            return;
        }
        foreach (var set in p.Reports)
        {
            byte id = set[0];
            var setBuf = new byte[Math.Max(dev.FeatureLength, set.Length)];
            set.CopyTo(setBuf, 0);
            TimeCalls($"{p.Text}: HidD_SetFeature 0x{id:X2} then HidD_GetFeature, the GET", runs, () =>
            {
                if (!HidD_SetFeature(dev.Handle, setBuf, setBuf.Length)) return -System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                Array.Clear(buf);
                buf[0] = id;
                long t = Stopwatch.GetTimestamp();
                bool ok = HidD_GetFeature(dev.Handle, buf, buf.Length);
                s_untimed = Stopwatch.GetTimestamp() - t;
                return ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            }, timeGetOnly: true);
        }
    }

    static void FeatureUsbip(UsbipServer server, ControllerProfile profile, ProfileArg p, int runs)
    {
        var device = new UsbipEmulatedDevice(profile, UsbipIndex);
        server.Register(device);
        try
        {
            using var w = new UsbipWire(server.Port, device.BusId);
            // The configuration descriptor, then each HID interface's report
            // descriptor, as a host reads them before it asks for a report.
            if (w.WaitFor(w.SubmitControlIn(0x80, 0x06, 0x0200, 0, 4096)) != 0)
            {
                Check($"{p.Text}: GET_DESCRIPTOR(Configuration)", false, "stalled");
                return;
            }
            var hidInterfaces = HidInterfaceNumbers(w.LastData.ToArray());
            if (hidInterfaces.Count == 0) { Check($"{p.Text}: a HID interface", false, "the configuration declares none"); return; }
            // Each SET and GET pair goes to the interfaces that declare its
            // feature report, as SDL sends it to the one that carries the
            // vendor report. Another interface stalls the SET.
            var measured = new HashSet<byte>();
            foreach (byte iface in hidInterfaces)
            {
                if (w.WaitFor(w.SubmitControlIn(0x81, 0x06, 0x2200, iface, 4096)) != 0)
                {
                    Check($"{p.Text}: GET_DESCRIPTOR(Report) of interface {iface}", false, "stalled");
                    continue;
                }
                var declared = DeclaredReports(w.LastData.ToArray(), ReportKind.Feature);
                Console.WriteLine($"  {p.Text}: interface {iface}, declared feature IDs {string.Join(" ", declared.Select(k => $"0x{k.Key:X2} ({k.Value} bytes)"))}");
                if (p.Reports.Length == 0)
                {
                    foreach (var (id, length) in declared)
                        TimeCalls($"{p.Text}: GET_REPORT(Feature 0x{id:X2}) on interface {iface}", runs,
                            () => w.WaitFor(w.SubmitControlIn(0xA1, 0x01, (ushort)(0x0300 | id), iface, (ushort)length)),
                            allocations: true);
                    continue;
                }
                bool ids = declared.Keys.Any(k => k != 0);
                foreach (var set in p.Reports)
                {
                    byte id = set[0];
                    if (!declared.ContainsKey(id)) continue;
                    measured.Add(id);
                    // A descriptor without report IDs puts no ID byte on the
                    // wire, so the 0 hidapi carries in front goes.
                    var data = ids ? set : set[1..];
                    int length = declared.TryGetValue(id, out int l) ? l : data.Length;
                    var stage = new byte[Math.Max(length, data.Length)];
                    data.CopyTo(stage, 0);
                    TimeCalls($"{p.Text}: SET_REPORT(Feature 0x{id:X2}) then GET_REPORT on interface {iface}, the GET", runs, () =>
                    {
                        int s = w.WaitFor(w.SubmitControlOut(0x21, 0x09, (ushort)(0x0300 | id), iface, stage));
                        if (s != 0) return s;
                        long t = Stopwatch.GetTimestamp();
                        s = w.WaitFor(w.SubmitControlIn(0xA1, 0x01, (ushort)(0x0300 | id), iface, (ushort)length));
                        s_untimed = Stopwatch.GetTimestamp() - t;
                        return s;
                    }, timeGetOnly: true, allocations: true);
                }
            }
            foreach (var set in p.Reports)
                if (!measured.Contains(set[0]))
                    Check($"{p.Text}: feature report 0x{set[0]:X2}", false, "no HID interface declares it");
        }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(UsbipIndex);
        }
    }

    /// <summary>The interface numbers of class 3 (HID) in a configuration
    /// descriptor, each alternate setting's interface once (USB 2.0 section
    /// 9.6.5).</summary>
    static List<byte> HidInterfaceNumbers(byte[] config)
    {
        var list = new List<byte>();
        for (int i = 0; i + 1 < config.Length && config[i] >= 2; i += config[i])
            if (config[i + 1] == 0x04 && i + 5 < config.Length && config[i + 5] == 0x03 && !list.Contains(config[i + 2]))
                list.Add(config[i + 2]);
        return list;
    }

    // Set by a timed call that times only its own part: the ticks of that
    // part, read by TimeCalls after the call. -1 when the call failed before
    // that part began.
    static long s_untimed;

    /// <summary>One call timed <see cref="FeatureCallsPerRun"/> times per
    /// run after twenty untimed ones. The call returns 0 on success or its
    /// error. Successes and failures are reported apart, with the last
    /// error. With timeGetOnly the call stores the ticks of the part that
    /// counts in s_untimed, and a call that failed before that part is
    /// counted, not timed. With allocations the bytes the whole process
    /// allocated per call are reported too.</summary>
    static void TimeCalls(string name, int runs, Func<int> call, bool timeGetOnly = false, bool allocations = false)
    {
        for (int i = 0; i < 20; i++) call();
        var ok = new List<double>(runs * FeatureCallsPerRun);
        var failed = new List<double>();
        var perCall = new List<double>(runs);
        int lastError = 0, failedBefore = 0, errorBefore = 0;
        for (int run = 0; run < runs; run++)
        {
            long bytes0 = allocations ? GC.GetTotalAllocatedBytes(precise: true) : 0;
            for (int i = 0; i < FeatureCallsPerRun; i++)
            {
                s_untimed = -1;
                long t0 = Stopwatch.GetTimestamp();
                int error = call();
                long ticks = timeGetOnly ? s_untimed : Stopwatch.GetTimestamp() - t0;
                if (ticks < 0) { failedBefore++; errorBefore = error; continue; }
                double us = ticks * 1e6 / Stopwatch.Frequency;
                if (error == 0) ok.Add(us);
                else { failed.Add(us); lastError = error; }
            }
            if (allocations) perCall.Add((double)(GC.GetTotalAllocatedBytes(precise: true) - bytes0) / FeatureCallsPerRun);
        }
        Report($"{name}, answered, microseconds", ok, runs, "");
        if (failed.Count > 0) Report($"{name}, refused (last error {lastError}), microseconds", failed, runs, "");
        if (failedBefore > 0) Check($"{name}: the SET before each GET", false, $"{failedBefore} refused, last error {errorBefore}");
        if (allocations) Report($"{name}: bytes the process allocated per call", perCall, runs, "");
    }

#if !HM_BASE
    // ── A report the device builds ───────────────────────────────────────

    const int ReportBuildCalls = 100_000;

    delegate void BuildReportFn(Switch2ProDevice device, byte reportId, uint n, Span<byte> report);

    /// <summary>The Switch 2 Pro composite's device-built report, each of
    /// its two layouts, two ways: as the device's 4 ms report clock builds
    /// it (Switch2ProDevice.BuildNextReport, its lock included), and the
    /// build alone (BuildReport, through a delegate made once). The time per
    /// report and the bytes the calling thread allocated. Offline: no
    /// device, no section, no elevation. The layout is selected as a host's
    /// 03/0A command would select it, by its field.</summary>
    static void ReportBuild(int runs)
    {
        var device = new Switch2ProDevice(null, "perf");
        var selected = typeof(Switch2ProDevice).GetField("_selectedReport", Any)!;
        var build = typeof(Switch2ProDevice).GetMethod("BuildReport", Any)!.CreateDelegate<BuildReportFn>();
        var bodyA = Switch2ProPacker.NeutralBody();
        var bodyB = (byte[])bodyA.Clone();
        bodyB[0] ^= 0x01;
        var report = new byte[Switch2ProDevice.ReportSize];
        foreach (byte id in new[] { Switch2ProDevice.ReportCommon, Switch2ProDevice.ReportPro })
        {
            selected.SetValue(device, id);
            TimeReports($"Switch2ProDevice.BuildNextReport, report 0x{id:X2}", runs, bodyA, bodyB, device, report,
                (d, r, _) => d.BuildNextReport(r));
            Check($"report-build 0x{id:X2}: BuildNextReport's report carries ID 0x{id:X2}", report[0] == id, $"byte 0 is 0x{report[0]:X2}");
            TimeReports($"Switch2ProDevice.BuildReport, report 0x{id:X2}", runs, bodyA, bodyB, device, report,
                (d, r, n) => build(d, id, n, r));
            Check($"report-build 0x{id:X2}: BuildReport's report carries ID 0x{id:X2}", report[0] == id, $"byte 0 is 0x{report[0]:X2}");
        }
    }

    delegate void ReportCall(Switch2ProDevice device, Span<byte> report, uint n);

    static void TimeReports(string name, int runs, byte[] bodyA, byte[] bodyB, Switch2ProDevice device, byte[] report, ReportCall call)
    {
        for (int i = 0; i < ReportBuildCalls; i++) call(device, report, (uint)i);
        var perCall = new List<double>(runs);
        long worst = 0;
        for (int run = 0; run < runs; run++)
        {
            device.SetBody((run & 1) == 0 ? bodyA : bodyB);
            long bytes0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < ReportBuildCalls; i++) call(device, report, (uint)i);
            long t1 = Stopwatch.GetTimestamp();
            worst = Math.Max(worst, GC.GetAllocatedBytesForCurrentThread() - bytes0);
            perCall.Add((t1 - t0) * 1e9 / Stopwatch.Frequency / ReportBuildCalls);
        }
        Report($"{name}: ns per report", perCall, runs, $", most bytes allocated in one run of {ReportBuildCalls} reports {worst}");
    }
#endif

    // ── Offline self-test ────────────────────────────────────────────────

    /// <summary>Whether a Push item appears as an item, not as a data
    /// byte.</summary>
    static bool UsesPushPop(byte[] d)
    {
        for (int i = 0; i < d.Length;)
        {
            byte prefix = d[i];
            if (prefix == 0xFE) { if (i + 1 >= d.Length) break; i += 3 + d[i + 1]; continue; }
            if ((prefix & 0xFC) == 0xA4) return true;
            int n = prefix & 3;
            i += 1 + (n == 3 ? 4 : n);
        }
        return false;
    }

    /// <summary>The first Report ID item's value, or 0 when there is
    /// none.</summary>
    static byte FirstReportId(byte[] d)
    {
        for (int i = 0; i < d.Length;)
        {
            byte prefix = d[i];
            if (prefix == 0xFE) { if (i + 1 >= d.Length) break; i += 3 + d[i + 1]; continue; }
            int n = prefix & 3;
            if (n == 3) n = 4;
            if ((prefix & 0xFC) == 0x84 && i + 1 < d.Length) return d[i + 1];
            i += 1 + n;
        }
        return 0;
    }

    /// <summary>What the modes above share, checked with no device: the
    /// report-length reader on hand-made descriptors and on catalog
    /// profiles against the lengths the SDK itself gives, the HID interface
    /// reader on a composite's configuration, the profile arguments, and the
    /// pattern a report must match. Exit 0 PASS, 1 FAIL.</summary>
    static int SelfTest()
    {
        // Two feature reports with IDs, a Push and Pop around the second
        // one's size, and an input report: 0x05 is 8 x 4 bits plus the ID,
        // 0x09 is 3 bytes plus the ID, input 0x01 is 2 bytes plus the ID.
        byte[] withIds =
        {
            0x05, 0x01, 0x09, 0x05, 0xA1, 0x01,
            0x85, 0x01, 0x75, 0x08, 0x95, 0x02, 0x81, 0x02,
            0x85, 0x05, 0x75, 0x04, 0x95, 0x08, 0xB1, 0x02,
            0xA4, 0x85, 0x09, 0x75, 0x08, 0x95, 0x03, 0xB1, 0x02, 0xB4,
            0xC0,
        };
        var f = DeclaredReports(withIds, ReportKind.Feature);
        Check("feature lengths with report IDs", f.Count == 2 && f[0x05] == 5 && f[0x09] == 4,
            string.Join(", ", f.Select(k => $"0x{k.Key:X2}={k.Value}")));
        var inp = DeclaredReports(withIds, ReportKind.Input);
        Check("input lengths with report IDs", inp.Count == 1 && inp[0x01] == 3, string.Join(", ", inp.Select(k => $"0x{k.Key:X2}={k.Value}")));
        // After Pop the ID is 0x05 again, so a feature item there adds to
        // 0x05, and a long item is skipped whole.
        byte[] popAndLong = { 0x85, 0x05, 0x75, 0x08, 0x95, 0x01, 0xA4, 0x85, 0x09, 0xB4, 0xFE, 0x02, 0x10, 0xB1, 0x02, 0xB1, 0x02 };
        var pl = DeclaredReports(popAndLong, ReportKind.Feature);
        Check("Pop restores the report ID and a long item is skipped", pl.Count == 1 && pl[0x05] == 2,
            string.Join(", ", pl.Select(k => $"0x{k.Key:X2}={k.Value}")));
        // No report IDs: one report, ID 0, no ID byte.
        byte[] noIds = { 0x75, 0x08, 0x95, 0x30, 0xB1, 0x02, 0x95, 0x0C, 0x81, 0x02 };
        var nf = DeclaredReports(noIds, ReportKind.Feature);
        Check("a descriptor without IDs declares feature 0 of its own length", nf.Count == 1 && nf[0] == 48,
            string.Join(", ", nf.Select(k => $"0x{k.Key:X2}={k.Value}")));

        // Every catalog profile: the input length read here equals the one
        // the SDK gives the driver. 9bd6279 counts it in
        // ComputeInputReportByteLength(byte[]), for the report ID the
        // descriptor names first and without Push and Pop. After lane W3
        // DriverInputReport(profile) gives the report the SDK fills, its ID
        // and DeclaredLength, from a walk that keeps the global item stack.
        // An SDK with neither fails here: the check is never skipped.
        var catalog = ProfileDatabase.LoadEmbedded();
        var orchestrator = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.DeviceOrchestrator")!;
        var driverInput = orchestrator.GetMethod("DriverInputReport", Any, new[] { typeof(ControllerProfile) });
        var compute = orchestrator.GetMethod("ComputeInputReportByteLength", Any, new[] { typeof(byte[]) });
        int compared = 0;
        var mismatches = new List<string>();
        var pushPop = new List<string>();
        foreach (var prof in catalog.All)
        {
            var d = prof.GetDescriptorBytes();
            if (d == null) continue;
            var ins = DeclaredReports(d, ReportKind.Input);
            if (driverInput != null)
            {
                object? got;
                try { got = driverInput.Invoke(null, new object[] { prof }); }
                catch (System.Reflection.TargetInvocationException e)
                {
                    mismatches.Add($"{prof.Id} threw {e.InnerException?.GetType().Name}");
                    continue;
                }
                if (got is not ValueTuple<byte, int, int> r) { mismatches.Add($"{prof.Id} returned {got?.GetType().Name}"); continue; }
                compared++;
                int mine = ins.TryGetValue(r.Item1, out int m) ? m : 0;
                if (mine != r.Item3) mismatches.Add($"{prof.Id} 0x{r.Item1:X2} {mine}/{r.Item3}");
                continue;
            }
            if (compute == null) continue;
            byte first = FirstReportId(d);
            if (!ins.TryGetValue(first, out int mineFirst)) continue;
            int sdk = (int)compute.Invoke(null, new object[] { d })!;
            compared++;
            if (mineFirst == sdk) continue;
            // 9bd6279's count does not apply Push and Pop, so it differs
            // where a descriptor uses them. Any other difference is this
            // reader's.
            if (UsesPushPop(d)) pushPop.Add($"{prof.Id} {mineFirst}/{sdk}");
            else mismatches.Add($"{prof.Id} {mineFirst}/{sdk}");
        }
        string against = driverInput != null ? "DriverInputReport's DeclaredLength"
            : compute != null ? "ComputeInputReportByteLength" : "nothing";
        Check($"the input length equals the SDK's ({against}) on {compared - pushPop.Count} of {compared} catalog profiles" +
              (pushPop.Count > 0 ? ", the rest using Push and Pop, which 9bd6279's count skips" : ""),
            (driverInput != null || compute != null) && compared > 0 && mismatches.Count == 0,
            driverInput == null && compute == null
                ? "the SDK has neither DriverInputReport(ControllerProfile) nor ComputeInputReportByteLength(byte[])"
                : string.Join(", ", mismatches.Take(10)));
        if (pushPop.Count > 0)
            Console.WriteLine($"  [INFO] where the descriptor uses Push and Pop, this reader's length/the SDK's: {string.Join(", ", pushPop)}");

        // The catalog profiles the feature mode runs on by default.
        var ds = DeclaredReports(catalog.All.First(p => p.Id == "dualsense-bt").GetDescriptorBytes()!, ReportKind.Feature);
        Console.WriteLine($"  dualsense-bt declares feature {string.Join(" ", ds.Select(k => $"0x{k.Key:X2}/{k.Value}"))}");
        var d3 = DeclaredReports(catalog.All.First(p => p.Id == "dualshock-3-full").GetDescriptorBytes()!, ReportKind.Feature);
        Console.WriteLine($"  dualshock-3-full declares feature {string.Join(" ", d3.Select(k => $"0x{k.Key:X2}/{k.Value}"))}");
        Check("dualsense-bt and dualshock-3-full each declare a feature report", ds.Count > 0 && d3.Count > 0, "");

        // A composite's configuration, as the device serves it: the HID
        // interface the profile's interface list names, and its report
        // descriptor's feature IDs.
        foreach (string id in new[] { "dualsense-composite", "dualshock-4-v2-composite", "steam-deck-composite", "steam-controller-composite", "steam-controller-2", "switch2-pro-controller-composite" })
        {
            var prof = catalog.All.First(p => p.Id == id);
            var set = new UsbDescriptorSet(prof);
            var hid = HidInterfaceNumbers(set.ConfigurationDescriptor);
            var detail = hid.Select(i => (Iface: i, Report: set.GetHidDescriptor(0x22, i))).ToList();
            Check($"{id}: each HID interface of its {set.ConfigurationDescriptor.Length}-byte configuration has a report descriptor",
                hid.Count > 0 && detail.All(x => x.Report != null),
                string.Join("; ", detail.Select(x => $"interface {x.Iface}: feature {(x.Report == null ? "(none)" : string.Join(" ", DeclaredReports(x.Report, ReportKind.Feature).Select(k => $"0x{k.Key:X2}/{k.Value}")))}")));
        }

        // Profile arguments.
        var args = ParseProfileArgs(new[] { "switch-pro@010000014040000140400330", "dualsense-bt" }, "selftest");
        Check("a profile argument with a report", args != null && args[0].Id == "switch-pro" && args[0].Reports.Length == 1
            && args[0].Reports[0].Length == 12 && args[0].Reports[0][10] == 0x03 && args[0].Reports[0][11] == 0x30 && args[1].Reports.Length == 0, "");
        Check("an unknown profile and odd hex are refused before anything runs",
            ParseProfileArgs(new[] { "no-such-profile" }, "selftest") == null && ParseProfileArgs(new[] { "switch-pro@010" }, "selftest") == null, "");

        // A pattern: byte 1 counts and is not compared, byte 3 tells A from
        // B, and the length must match.
        var care = new[] { true, false, true, true };
        var pa = new ReportPattern(new byte[] { 0x30, 0x00, 0x91, 0x08 }, care);
        Check("a pattern ignores the bytes calibration found varying", pa.Matches(new byte[] { 0x30, 0x7F, 0x91, 0x08 }, 4), "");
        Check("a pattern refuses another state, another length", !pa.Matches(new byte[] { 0x30, 0x00, 0x91, 0x04 }, 4)
            && !pa.Matches(new byte[] { 0x30, 0x00, 0x91, 0x08, 0x00 }, 5), "");

        Console.WriteLine($"selftest: {(s_failures == 0 ? "PASS" : $"{s_failures} FAIL")}");
        return s_failures == 0 ? 0 : 1;
    }
}
