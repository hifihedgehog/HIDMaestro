// The deploy floor (design note, decision (c), "A v2.0.0 program on an
// older driver").
//
// MinimumDriverVersion is 2.0.0. A create's deploy gate runs the policy when
// the driver store lacks a package of either INF or the newest package of
// either is older than the floor, and confirms the installed-payload record
// only when both meet it. The policy then refuses a held older driver with
// HMDriverVersionException naming its holders, upgrades one nothing holds,
// and keeps a held 2.0.x build. What a create does on a held older driver
// is the owner's choice: alternative A refuses every create, alternative B
// lets a profile whose resolved model is empty in every group, which no
// identity default matches and which has no XUSB companion or xinputhid
// parent, run there. DriverDeploy.Coexistence holds the choice.
//
// After the deploy step the create waits for the device to start. The wait
// also ends at once when the driver failed its add or start, by the
// devnode's problem or the driver's error word, and the create then throws
// HMDriverVersionException naming the value the driver could not read. A
// device that started is checked for a driver whose range of protocols
// holds 4.
//
// The checks run the gate on machines of their own (DriverDeploy.Machine):
// a driver store, a census and a lock the check gives, the deploy steps
// recorded. Nothing touches the real store. The base runs the policy only
// when a package is missing, so a create there binds a held v1.12.0 driver.
using System;
using System.Collections.Generic;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Deploy
{
    public static int Run()
    {
        var c = new Checks("deploy");
        Section(c, "the gate", Gate);
        Section(c, "the predicate", Predicate);
        Section(c, "the start", Start);
        return c.Failures;
    }

    static void Section(Checks c, string name, Action<Checks> body)
    {
        try { body(c); }
        catch (Exception e) when (e is TypeLoadException or MissingMemberException)
        {
            c.Check($"{name}: the SDK has the members these checks call", false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    static DriverDeploy.DriverStamp Stamp(int major, int minor, int patch, int build = 1) =>
        new(new DateTime(2026, 10, 1), new Version(major, minor, patch, build));

    /// <summary>A machine: the two INFs' packages, this build's stamp, the
    /// census, and what the policy did.</summary>
    sealed class FakeMachine : DriverDeploy.DeployMachine
    {
        public readonly Dictionary<string, List<DriverDeploy.DriverStamp>> Store = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hidmaestro.inf"] = new(), ["hidmaestro_xusb.inf"] = new(),
        };
        public DriverDeploy.DriverStamp Build = Stamp(2, 0, 0, 7);
        public OwnerCensus Census_ = new();
        public string? Manifest;
        public readonly List<string> Calls = new();
        const string Ours = "this-build";

        public FakeMachine Holding(params DriverDeploy.DriverStamp[] both)
        {
            foreach (var s in both) { Store["hidmaestro.inf"].Add(s); Store["hidmaestro_xusb.inf"].Add(s); }
            Manifest = "another-build";
            return this;
        }

        public FakeMachine HeldByV1()
        {
            Census_.Devices.Add(new OwnerCensus.DeviceGroup("HM_0000", 0,
                new List<string> { @"ROOT\HIDCLASS\HM_0000" }, OwnerCensus.Owner.LiveLegacy, null));
            return this;
        }

        internal override IDisposable Lock() => new Nothing();
        internal override List<DriverDeploy.DriverStamp> Stamps(string infName) => Store[infName];
        internal override DriverDeploy.DriverStamp Embedded(string infName) => Build;
        internal override OwnerCensus Census() { Calls.Add("census"); return Census_; }
        internal override bool FastPath() => Manifest == Ours && StoreHasBothPackages();
        internal override void MarkConfirmed() { }
        internal override string? ManifestHash() => Manifest;
        internal override bool StoreHasBothPackages() => Store.Values.All(v => v.Count > 0);
        internal override void Deploy()
        {
            Calls.Add("deploy");
            foreach (var v in Store.Values) { v.Clear(); v.Add(Build); }
            Manifest = Ours;
        }
        internal override void AddPackages(IReadOnlyCollection<string> infs)
        {
            foreach (var inf in infs) { Calls.Add("add " + inf); Store[inf].Add(Build); }
        }
        internal override void RecordPayload() => Manifest = Ours;
        internal override void PruneStaging() { }

        sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>The gate on a machine, under a rule, the real machine and
    /// rule put back after.</summary>
    static T On<T>(FakeMachine m, DriverDeploy.CoexistenceRule rule, Func<T> body)
    {
        var machine = DriverDeploy.Machine;
        var was = DriverDeploy.Coexistence;
        DriverDeploy.Machine = m;
        DriverDeploy.Coexistence = rule;
        DriverDeploy.ForgetConfirmed();
        try { return body(); }
        finally
        {
            DriverDeploy.Machine = machine;
            DriverDeploy.Coexistence = was;
            DriverDeploy.ForgetConfirmed();
        }
    }

    static string Refusal(Func<bool> create)
    {
        try { return create() ? "deployed" : "passed"; }
        catch (HMDriverVersionException e)
        {
            return $"refused, {e.InstalledVersion} below {e.MinimumVersion}: {string.Join("; ", e.Blockers)}";
        }
    }

    static void Gate(Checks c)
    {
        const string Token = "HM_0001";
        var A = DriverDeploy.CoexistenceRule.RefuseEveryCreate;
        var B = DriverDeploy.CoexistenceRule.AllowModelFreeProfiles;
        c.Check("the floor is 2.0.0", DriverDeploy.MinimumDriverVersion == new Version(2, 0, 0), DriverDeploy.MinimumDriverVersion.ToString());

        c.Run("under alternative A, v1.12.0's two packages held by a running v1.12.0 program refuse a generic create, naming the devnode and the program", () =>
        {
            var m = new FakeMachine().Holding(Stamp(1, 12, 0)).HeldByV1();
            string r = On(m, A, () => Refusal(() => DriverDeploy.EnsureForCreate(Token, runsOnOlderDriver: true)));
            return (r.StartsWith("refused, 1.12.0 below 2.0.0: ", StringComparison.Ordinal)
                    && r.Contains(@"ROOT\HIDCLASS\HM_0000 (a running program built on HIDMaestro 1.12.0)", StringComparison.Ordinal)
                    && !m.Calls.Contains("deploy"), r);
        });
        c.Run("the same store with no holder upgrades", () =>
        {
            var m = new FakeMachine().Holding(Stamp(1, 12, 0));
            string r = On(m, A, () => Refusal(() => DriverDeploy.EnsureForCreate(Token, runsOnOlderDriver: false)));
            return (r == "deployed" && m.Calls.Contains("deploy"), r);
        });
        c.Run("a held 2.0.0 build older than this one is kept in use", () =>
        {
            var m = new FakeMachine().Holding(Stamp(2, 0, 0, 3)).HeldByV1();
            var outcome = On(m, A, () => DriverDeploy.Run(Token));
            return (outcome == HMDriverInstallOutcome.KeptInUse, outcome.ToString());
        });
        c.Run("a 2.0.0 hidmaestro.inf beside a 1.12.0 hidmaestro_xusb.inf runs the policy, which adds this build's XUSB package", () =>
        {
            var m = new FakeMachine();
            m.Store["hidmaestro.inf"].Add(m.Build);
            m.Store["hidmaestro_xusb.inf"].Add(Stamp(1, 12, 0));
            m.Manifest = "another-build";
            string r = On(m, A, () => Refusal(() => DriverDeploy.EnsureForCreate(Token, runsOnOlderDriver: false)));
            return (m.Calls.Contains("add hidmaestro_xusb.inf"), $"{r}, {string.Join(", ", m.Calls)}");
        });
        c.Run("once both packages meet the floor the gate's record is confirmed, and the next create takes the fast return", () =>
        {
            var m = new FakeMachine().Holding(Stamp(1, 12, 0));
            int before = 0;
            string r = On(m, A, () =>
            {
                DriverDeploy.EnsureForCreate(Token, false);
                before = m.Calls.Count;
                return Refusal(() => DriverDeploy.EnsureForCreate("HM_0002", false));
            });
            return (r == "passed" && m.Calls.Count == before, $"{r}, {m.Calls.Count - before} more steps");
        });
        c.Run("under alternative B, a generic create runs on the held v1.12.0 driver, and then dualsense-bt is refused at the gate", () =>
        {
            var m = new FakeMachine().Holding(Stamp(1, 12, 0)).HeldByV1();
            var (first, second, census) = On(m, B, () =>
            {
                string a = Refusal(() => DriverDeploy.EnsureForCreate(Token, runsOnOlderDriver: true));
                int n = m.Calls.Count(x => x == "census");
                string b = Refusal(() => DriverDeploy.EnsureForCreate("HM_0002", runsOnOlderDriver: false));
                return (a, b, m.Calls.Count(x => x == "census") - n);
            });
            return (first == "passed" && second.StartsWith("refused", StringComparison.Ordinal) && census == 1,
                    $"{first}; {second}; the second create took the census {census} time(s)");
        });
        c.Run("under alternative A the same generic create is refused", () =>
        {
            var m = new FakeMachine().Holding(Stamp(1, 12, 0)).HeldByV1();
            string r = On(m, A, () => Refusal(() => DriverDeploy.EnsureForCreate(Token, runsOnOlderDriver: true)));
            return (r.StartsWith("refused", StringComparison.Ordinal), r);
        });
    }

    /// <summary>The catalog's UMDF2 profiles that alternative B refuses on a
    /// held older driver: those with a model, an identity default, an XUSB
    /// companion or an xinputhid parent. Every other UMDF2 profile passes.</summary>
    static readonly string[] Refused =
    {
        // The 14 Sony profiles: identity defaults on vendor 054C and sixaxisCompatible.
        "dualsense", "dualsense-bt", "dualsense-bt-full", "dualsense-edge", "dualsense-edge-bt",
        "dualshock-3", "dualshock-3-full", "dualshock-4-v1", "dualshock-4-v1-full", "dualshock-4-v2",
        "dualshock-4-v2-bt", "ps-classic", "ps-move", "ps3-remote",
        // 057E:2009, the Switch responder's identity default.
        "switch-pro",
        // The 10 with an XUSB companion.
        "xbox-360-arcade-stick", "xbox-360-dance-pad", "xbox-360-flight-stick", "xbox-360-guitar-v1",
        "xbox-360-guitar-v2", "xbox-360-type2", "xbox-360-wheel-v1", "xbox-360-wheel-v2", "xbox-360-wired",
        "xbox-360-wireless",
        // The 12 with an xinputhid parent.
        "xbox-adaptive", "xbox-elite-v1", "xbox-elite-v2", "xbox-elite-v2-bt", "xbox-elite-v2-bt-v2",
        "xbox-one-original", "xbox-one-rev1", "xbox-one-s", "xbox-one-s-bt", "xbox-one-s-bt-full",
        "xbox-series-xs", "xbox-series-xs-bt",
    };

    static void Predicate(Checks c)
    {
        var umdf2 = ProfileDatabase.LoadEmbedded().All.Where(p => !p.RequiresUsbipBackend).ToList();
        var refused = umdf2.Where(p => !p.RunsOnOlderDriver).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        foreach (var id in refused) Console.WriteLine($"    refused: {id}");
        c.Check($"of {umdf2.Count} UMDF2 catalog profiles, {umdf2.Count - refused.Count} pass alternative B's predicate and the {refused.Count} the golden list names do not",
                refused.SequenceEqual(Refused.OrderBy(x => x, StringComparer.Ordinal)),
                string.Join(", ", refused.Except(Refused).Select(x => "+" + x).Concat(Refused.Except(refused).Select(x => "-" + x))));
    }

    // ── The start ────────────────────────────────────────────────────────

    /// <summary>The waits and the post-start check on a devnode the check
    /// gives (DeviceOrchestrator's DevnodeStatus, HidChildOf and
    /// DriverHalf), the real ones put back after.</summary>
    static T With<T>(Func<string, (bool, uint, uint)> status, Func<string, string?> child,
                     Func<int, (int, int, Version?, int)> half, Func<T> body)
    {
        var (s0, c0, h0) = (DeviceOrchestrator.DevnodeStatus, DeviceOrchestrator.HidChildOf, DeviceOrchestrator.DriverHalf);
        DeviceOrchestrator.DevnodeStatus = id => status(id);
        DeviceOrchestrator.HidChildOf = child;
        DeviceOrchestrator.DriverHalf = i => half(i);
        try { return body(); }
        finally { (DeviceOrchestrator.DevnodeStatus, DeviceOrchestrator.HidChildOf, DeviceOrchestrator.DriverHalf) = (s0, c0, h0); }
    }

    const uint HasProblem = 0x400, Started = 0x8;
    const string Node = @"ROOT\HIDCLASS\HM_0003";

    static void Start(Checks c)
    {
        c.Run("a devnode whose driver failed its start and set its error word: the wait ends within one 25 ms poll and the create throws HMDriverVersionException naming FeatureAnswers", () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (wait, error) = With(_ => (true, HasProblem, 10u), _ => null, _ => (4, 4, new Version(2, 0, 0), 1), () =>
                (DeviceOrchestrator.WaitForHidChild(Node, 3, 10_000), DeviceOrchestrator.StartError(Node, 3)));
            long ms = sw.ElapsedMilliseconds;
            return (wait == DeviceOrchestrator.StartWait.Failed && ms < 1000 && error is HMDriverVersionException v
                    && v.Message.Contains("FeatureAnswers", StringComparison.Ordinal) && v.Blockers.SequenceEqual(new[] { Node }),
                    $"{wait} in {ms} ms: {error.GetType().Name}: {error.Message}");
        });
        c.Run("a failed add with no error word ends the wait as well, and names the problem", () =>
        {
            var (wait, error) = With(_ => (true, HasProblem, 31u), _ => null, _ => (0, 0, null, 0), () =>
                (DeviceOrchestrator.WaitForHidChild(Node, 3, 10_000), DeviceOrchestrator.StartError(Node, 3)));
            return (wait == DeviceOrchestrator.StartWait.Failed && error is not HMDriverVersionException
                    && error.Message.Contains("problem code 31", StringComparison.Ordinal), $"{wait}: {error.Message}");
        });
        c.Run("a devnode still being installed (problem 28) does not end the wait, and its child ends it", () =>
        {
            int polls = 0;
            var wait = With(_ => (true, HasProblem, 28u), _ => ++polls >= 3 ? @"HID\HM_0003\1" : null, _ => (0, 0, null, 0), () =>
                DeviceOrchestrator.WaitForHidChild(Node, 3, 10_000));
            return (wait == DeviceOrchestrator.StartWait.Done && polls == 3, $"{wait} after {polls} polls");
        });
        c.Run("the HID child's wait ends on its parent driver's error word", () =>
        {
            var wait = With(id => id == Node ? (true, 0u, 0u) : (true, 0u, 0u), _ => null, _ => (4, 4, new Version(2, 0, 0), 2), () =>
                DeviceOrchestrator.WaitForDeviceStarted(@"HID\HM_0003\1", Node, 3, 10_000));
            return (wait == DeviceOrchestrator.StartWait.Failed, wait.ToString());
        });
        foreach (var (min, max, none, ok) in new[] { (3, 3, false, false), (5, 5, false, false), (4, 4, false, true), (4, 5, false, true),
                                                     (0, 0, false, false), (0, 0, true, true) })
            c.Run($"a started driver serving protocols {min} to {max}{(none ? ", for a profile alternative B runs on an older driver" : "")} {(ok ? "passes" : "fails the create with HMDriverVersionException")}", () =>
            {
                Exception? e = null;
                With(_ => (true, Started, 0u), _ => null, _ => (max, min, max == 0 ? null : new Version(2, 0, 0), 0), () =>
                {
                    try { DeviceOrchestrator.CheckDriverHalf(3, Node, acceptNoSection: none); }
                    catch (Exception x) { e = x; }
                    return 0;
                });
                return (ok ? e == null : e is HMDriverVersionException v && v.Blockers.SequenceEqual(new[] { Node }),
                        e == null ? "passed" : $"{e.GetType().Name}: {e.Message}");
            });
    }
}
