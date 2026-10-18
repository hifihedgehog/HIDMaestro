// The deploy policy under the design note's decision (c): "the V2
// controllers take precedence over and supersede the V1".
//
// A create's deploy gate runs the policy when the driver store lacks a
// package of either INF or the newest package of either is older than this
// build's, and confirms the installed-payload record only when neither is.
// Over an older driver nothing holds, the policy deploys in full. Over an
// older driver, either INF, that running programs' controllers hold, it adds
// this build's packages beside the old ones and removes nothing: no device
// is updated and nothing is refused. A newer driver is kept.
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
// recorded. Nothing touches the real store.
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
        Section(c, "the start", Start);
        Section(c, "the deploy lock", LockHandle);
        Section(c, "what HMDriverVersionException says", Cases);
        Section(c, "a refused create", RefusedCreate);
        // The deploy refuses no holder (decision (c)), so the list of holders
        // that refusal named went with it. Until this check DriverDeploy
        // still carried Describe, with no caller.
        c.Run("DriverDeploy carries no holder list for a refusal it no longer makes", () =>
        {
            var m = typeof(DriverDeploy).GetMethod("Describe", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return (m == null, m == null ? "none" : m.ToString()!);
        });
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

    /// <summary>The gate on a machine, the real machine put back after.</summary>
    static T On<T>(FakeMachine m, Func<T> body)
    {
        var machine = DriverDeploy.Machine;
        DriverDeploy.Machine = m;
        DriverDeploy.ForgetConfirmed();
        try { return body(); }
        finally
        {
            DriverDeploy.Machine = machine;
            DriverDeploy.ForgetConfirmed();
        }
    }

    /// <summary>A create's deploy gate for <paramref name="token"/>, called
    /// by reflection so the check also runs against an SDK whose gate took
    /// a second argument, the removed coexistence setting's, given false
    /// there.</summary>
    static bool Gate(string token)
    {
        var f = typeof(DriverDeploy).GetMethod("EnsureForCreate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException("DriverDeploy", "EnsureForCreate");
        object[] args = f.GetParameters().Length == 1 ? new object[] { token } : new object[] { token, false };
        try { return (bool)f.Invoke(null, args)!; }
        catch (System.Reflection.TargetInvocationException x) when (x.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(x.InnerException).Throw();
            throw;
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
        c.Check("the floor is 2.0.0", DriverDeploy.MinimumDriverVersion == new Version(2, 0, 0), DriverDeploy.MinimumDriverVersion.ToString());

        // Decision (c): a create installs its own driver over an older one,
        // whoever runs on it, by adding its packages beside the old ones,
        // with no device updated and no package removed. At 7f6bf5b3 each
        // held case below was refused with HMDriverVersionException or kept
        // the older driver.
        bool Has(FakeMachine m, string inf, DriverDeploy.DriverStamp s) => m.Store[inf].Any(x => x.CompareTo(s) == 0);
        bool Ours(FakeMachine m) => Has(m, "hidmaestro.inf", m.Build) && Has(m, "hidmaestro_xusb.inf", m.Build);
        string Calls(FakeMachine m) => string.Join(", ", m.Calls);
        foreach (var (name, stamp) in new[] { ("v1.12.0's", Stamp(1, 12, 0)), ("an older 2.0.0 build's", Stamp(2, 0, 0, 3)) })
            c.Run($"{name} two packages held by a running program: the create adds this build's beside them, deploys nothing and removes nothing", () =>
            {
                var m = new FakeMachine().Holding(stamp).HeldByV1();
                string r = On(m, () => Refusal(() => Gate(Token)));
                return (r == "deployed" && Ours(m) && Has(m, "hidmaestro.inf", stamp) && Has(m, "hidmaestro_xusb.inf", stamp)
                        && !m.Calls.Contains("deploy"), $"{r}, {Calls(m)}");
            });
        c.Run("after the add the gate confirms the record, and the next create takes the fast return", () =>
        {
            var m = new FakeMachine().Holding(Stamp(1, 12, 0)).HeldByV1();
            int before = 0;
            string r = On(m, () =>
            {
                Gate(Token);
                before = m.Calls.Count;
                return Refusal(() => Gate("HM_0002"));
            });
            return (r == "passed" && m.Calls.Count == before, $"{r}, {m.Calls.Count - before} more steps");
        });
        c.Run("InstallDriver over v1.12.0's held packages adds this build's and returns Upgraded", () =>
        {
            var m = new FakeMachine().Holding(Stamp(1, 12, 0)).HeldByV1();
            var outcome = On(m, () => DriverDeploy.Run());
            return (outcome == HMDriverInstallOutcome.Upgraded && Ours(m) && !m.Calls.Contains("deploy"), $"{outcome}, {Calls(m)}");
        });
        c.Run("v1.12.0's two packages with no holder: a full deploy", () =>
        {
            var m = new FakeMachine().Holding(Stamp(1, 12, 0));
            string r = On(m, () => Refusal(() => Gate(Token)));
            return (r == "deployed" && m.Calls.Contains("deploy"), $"{r}, {Calls(m)}");
        });
        // One INF behind the other.
        FakeMachine Split(DriverDeploy.DriverStamp main)
        {
            var m = new FakeMachine();
            m.Store["hidmaestro.inf"].Add(main);
            m.Store["hidmaestro_xusb.inf"].Add(Stamp(1, 12, 0));
            m.Manifest = "another-build";
            return m;
        }
        foreach (var (name, main) in new[] { ("an older 2.0.0 build's", Stamp(2, 0, 0, 3)), ("this build's", Stamp(2, 0, 0, 7)), ("a newer 2.0.1", Stamp(2, 0, 1, 1)) })
            c.Run($"{name} hidmaestro.inf beside a held 1.12.0 hidmaestro_xusb.inf: this build's XUSB package is added, nothing removed", () =>
            {
                var m = Split(main).HeldByV1();
                string r = On(m, () => Refusal(() => Gate(Token)));
                return (!r.StartsWith("refused", StringComparison.Ordinal) && Has(m, "hidmaestro_xusb.inf", m.Build)
                        && Has(m, "hidmaestro_xusb.inf", Stamp(1, 12, 0)) && Has(m, "hidmaestro.inf", main) && !m.Calls.Contains("deploy"),
                        $"{r}, {Calls(m)}");
            });
        FakeMachine LoneXusb()
        {
            var m = new FakeMachine();
            m.Store["hidmaestro_xusb.inf"].Add(Stamp(1, 12, 0));
            m.Manifest = "another-build";
            return m;
        }
        c.Run("a lone held 1.12.0 hidmaestro_xusb.inf: this build's two packages are added beside it", () =>
        {
            var m = LoneXusb().HeldByV1();
            string r = On(m, () => Refusal(() => Gate(Token)));
            return (r == "deployed" && Ours(m) && Has(m, "hidmaestro_xusb.inf", Stamp(1, 12, 0)) && !m.Calls.Contains("deploy"), $"{r}, {Calls(m)}");
        });
        c.Run("a lone 1.12.0 hidmaestro_xusb.inf, nothing held: a full deploy", () =>
        {
            var m = LoneXusb();
            string r = On(m, () => Refusal(() => Gate(Token)));
            return (r == "deployed" && m.Calls.Contains("deploy"), $"{r}, {Calls(m)}");
        });
        c.Run("a newer 2.0.1 driver, both INFs, held: kept, nothing added", () =>
        {
            var m = new FakeMachine().Holding(Stamp(2, 0, 1, 1)).HeldByV1();
            var outcome = On(m, () => DriverDeploy.Run());
            return (outcome == HMDriverInstallOutcome.KeptNewer && !m.Calls.Any(x => x.StartsWith("add", StringComparison.Ordinal)) && !m.Calls.Contains("deploy"),
                    $"{outcome}, {Calls(m)}");
        });
        c.Run("this build's hidmaestro.inf held, with no hidmaestro_xusb.inf package: this build's XUSB package is added", () =>
        {
            var m = new FakeMachine().HeldByV1();
            m.Store["hidmaestro.inf"].Add(m.Build);
            m.Manifest = "another-build";
            string r = On(m, () => Refusal(() => Gate(Token)));
            return (m.Calls.Contains("add hidmaestro_xusb.inf") && !m.Calls.Contains("deploy"), $"{r}, {Calls(m)}");
        });
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
        // Every code a failed driver leaves ends the wait, and the error
        // names it: 10, 31 and 37 (cfg.h:77, 98, 104), 39 for a driver that
        // did not load (106), and 43 for a driver that failed after its
        // start, as a crashed UMDF host leaves it (110). At bde72499 39 and
        // 43 waited out the budget.
        foreach (uint code in new uint[] { 10, 31, 37, 39, 43 })
            c.Run($"problem {code} ends the wait within one poll, and the error names it", () =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (wait, error) = With(_ => (true, HasProblem, code), _ => null, _ => (0, 0, null, 0), () =>
                    (DeviceOrchestrator.WaitForHidChild(Node, 3, 2_000), DeviceOrchestrator.StartError(Node, 3)));
                long ms = sw.ElapsedMilliseconds;
                return (wait == DeviceOrchestrator.StartWait.Failed && ms < 1000 && error.Message.Contains($"problem code {code}", StringComparison.Ordinal),
                        $"{wait} in {ms} ms: {error.Message}");
            });
        // A value the driver could not read names the device's problem code
        // too, the record a live run keeps of what Windows saw. At fe9134b9
        // only a failure with no error word named it.
        c.Run("a failed start with the error word set names the value and the problem code", () =>
        {
            var error = With(_ => (true, HasProblem, 43u), _ => null, _ => (4, 4, new Version(2, 0, 0), 1), () => DeviceOrchestrator.StartError(Node, 3));
            return (error.Message.Contains("FeatureAnswers", StringComparison.Ordinal) && error.Message.Contains("problem code 43", StringComparison.Ordinal),
                    error.Message);
        });
        // A HID child that fails while its parent started is the device the
        // error names, with the child's code. At 65451b0c the error read
        // the parent alone and said "problem code 0".
        c.Run("a failed HID child under a started parent: the error names the child and its problem code", () =>
        {
            const string Child = @"HID\HIDCLASS\HM_0003&COL01\1";
            var withChild = typeof(DeviceOrchestrator).GetMethod("StartError", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                new[] { typeof(string), typeof(int), typeof(string) });
            if (withChild == null) return (false, "StartError takes no child");
            var error = With(id => id == Child ? (true, HasProblem, 43u) : (true, Started, 0u), _ => Child, _ => (0, 0, null, 0),
                () => (Exception)withChild.Invoke(null, new object?[] { Node, 3, Child })!);
            return (error.Message.Contains(Child, StringComparison.Ordinal) && error.Message.Contains("problem code 43", StringComparison.Ordinal),
                    error.Message);
        });
        // The public wait keeps the form v1.13.0 had, so a caller built
        // against it still binds. At de87fa90 a third optional parameter
        // replaced it.
        c.Run("DeviceManager.WaitForHidChild keeps its (string, int) form beside the one with a failure test", () =>
        {
            var t = typeof(DeviceManager);
            bool old = t.GetMethod("WaitForHidChild", new[] { typeof(string), typeof(int) }) != null;
            bool now = t.GetMethod("WaitForHidChild", new[] { typeof(string), typeof(int), typeof(Func<bool>) }) != null;
            return (old && now, $"two-argument {old}, with a failure test {now}");
        });
        // The ROOT path's own wait for the HID child, inside
        // DeviceNodeCreator.CreateDeviceNode, ends on a failed start too.
        // At bde72499 it waited for a child alone, 5 s, before step 4 saw the
        // failure.
        c.Run("the ROOT path's wait for the HID child ends within one poll on a failed start", () =>
        {
            var wait = typeof(DeviceOrchestrator).GetField("RootChildWait", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?? throw new MissingMemberException("DeviceOrchestrator", "RootChildWait");
            var f = (Func<string, int, bool>)wait.GetValue(null)!;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool child = With(_ => (true, HasProblem, 31u), _ => null, _ => (0, 0, null, 0), () => f(@"ROOT\HIDCLASS\HM_7777", 3));
            long ms = sw.ElapsedMilliseconds;
            return (!child && ms < 1000, $"{child} in {ms} ms");
        });
        // The XUSB companion's wait for its interface ends on a failed start
        // too. At e0c4d951 it waited 5 s three times, restarting the
        // companion between, and the create went on without the interface.
        c.Run("the XUSB companion's wait for its interface ends within one poll on a failed start", () =>
        {
            var wait = typeof(DeviceOrchestrator).GetField("CompanionInterfaceWait", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?? throw new MissingMemberException("DeviceOrchestrator", "CompanionInterfaceWait");
            var f = (Func<string, int, bool>)wait.GetValue(null)!;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool present = With(_ => (true, HasProblem, 43u), _ => null, _ => (0, 0, null, 0), () => f(@"SWD\HIDMAESTRO\HM_XUSB_7777", 3));
            long ms = sw.ElapsedMilliseconds;
            return (!present && ms < 1000, $"{present} in {ms} ms");
        });
        // The protocol check runs when the HID child is slow too. At
        // 6cf274b6 a create whose child did not appear within its 10 s wait
        // finished with no check, as b22324e did. With no child the driver
        // may not have written its half yet, so an empty section passes, and
        // a written range without 4 still fails the create.
        foreach (var (childSeen, min, max, ok) in new[] { (false, 5, 5, false), (false, 0, 0, true), (false, 4, 4, true), (true, 0, 0, false) })
            c.Run($"the start's protocol check with the child {(childSeen ? "seen" : "not seen")} and protocols {min} to {max} {(ok ? "passes" : "fails the create")}", () =>
            {
                var check = typeof(DeviceOrchestrator).GetMethod("CheckStartedDriver", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                    ?? throw new MissingMethodException("DeviceOrchestrator", "CheckStartedDriver");
                var p = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == "heusinkveld-ultimate-pedals");
                Exception? e = null;
                With(_ => (true, Started, 0u), _ => null, _ => (max, min, max == 0 ? null : new Version(2, 0, 0), 0), () =>
                {
                    try { check.Invoke(null, check.GetParameters().Length == 3 ? new object[] { 3, Node, childSeen } : new object[] { 3, Node, childSeen, p }); }
                    catch (System.Reflection.TargetInvocationException x) { e = x.InnerException; }
                    return 0;
                });
                return (ok ? e == null : e is HMDriverVersionException, e == null ? "passed" : e.Message);
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
            c.Run($"a started driver serving protocols {min} to {max}{(none ? ", with the HID child not seen" : "")} {(ok ? "passes" : "fails the create with HMDriverVersionException")}", () =>
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

    /// <summary>Releasing the deploy lock closes the mutex handle too. Each
    /// create's gate and each InstallDriver open a new Mutex (Acquire), and
    /// at 517255dd the hold released it and left the handle to the
    /// finalizer. b22324e disposed it (using var mutex = Acquire()).</summary>
    static void LockHandle(Checks c)
    {
        c.Run("disposing the deploy lock's hold releases the mutex and closes its handle", () =>
        {
            var held = typeof(DriverDeploy.DeployMachine).GetNestedType("Held", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?? throw new MissingMemberException("DeployMachine", "Held");
            var m = new System.Threading.Mutex(false);
            m.WaitOne();
            var hold = (IDisposable)Activator.CreateInstance(held, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new object[] { m }, null)!;
            hold.Dispose();
            return (m.SafeWaitHandle.IsClosed, m.SafeWaitHandle.IsClosed ? "" : "the handle is still open");
        });
    }

    /// <summary>HMDriverVersionException is thrown for three things, and says
    /// which: a started driver that does not serve protocol 4, and a value
    /// the driver could not read. At
    /// dcf14ca2 it had no way to tell them apart, and its message told a
    /// program whose driver was newer to install this SDK's, which never
    /// replaces a newer one.</summary>
    static void Cases(Checks c)
    {
        static string Problem(Exception e) => Api.Prop(e, "Problem")!.ToString()!;
        Exception? Half(int min, int max, Version? version)
        {
            Exception? e = null;
            With(_ => (true, Started, 0u), _ => null, _ => (max, min, version, 0), () =>
            {
                try { DeviceOrchestrator.CheckDriverHalf(3, Node, acceptNoSection: false); }
                catch (Exception x) { e = x; }
                return 0;
            });
            return e;
        }
        // Decision (c): no create or install is refused for an older driver,
        // so the problem that named one is gone. At 7f6bf5b3 it was
        // HeldOlderDriver.
        c.Check("HMDriverVersionProblem names only the two start cases",
                Enum.GetNames(typeof(HMDriverVersionProblem)).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(new[] { "ProtocolNotServed", "ValueNotRead" }),
                string.Join(", ", Enum.GetNames(typeof(HMDriverVersionProblem))));
        c.Run("a value the driver could not read: Problem ValueNotRead, Blockers the create's own device, and no older-driver advice", () =>
        {
            var error = With(_ => (true, HasProblem, 10u), _ => null, _ => (4, 4, new Version(2, 0, 0), 1), () => DeviceOrchestrator.StartError(Node, 3));
            return (error is HMDriverVersionException v && Problem(v) == "ValueNotRead" && v.Blockers.SequenceEqual(new[] { Node })
                    && !v.Message.Contains("older", StringComparison.Ordinal), $"{(error is HMDriverVersionException x ? Problem(x) : error.GetType().Name)}: {error.Message}");
        });
        // InstallDriver advice only where it would replace the driver. At
        // b92e3d0c a value a newer driver, or one of this release, could
        // not read always asked for InstallDriver, which keeps both.
        foreach (var (name, version, wantAdvice) in new[] { ("a newer 3.0.0", new Version(3, 0, 0), false), ("this release's", new Version(2, 0, 0), false), ("an older release's", new Version(1, 99, 0), true) })
            c.Run($"a value {name} driver could not read: the message {(wantAdvice ? "asks" : "does not ask")} for InstallDriver", () =>
            {
                // This SDK's driver is the fake machine's, 2.0.0.
                var error = On(new FakeMachine(), () =>
                    With(_ => (true, HasProblem, 10u), _ => null, _ => (4, 4, version, 1), () => DeviceOrchestrator.StartError(Node, 3)));
                bool asks = error.Message.Contains("InstallDriver", StringComparison.Ordinal);
                return (asks == wantAdvice && (wantAdvice || version.Major < 3 || error.Message.Contains("SDK of its release", StringComparison.Ordinal)), error.Message);
            });
        c.Run("a newer driver that dropped protocol 4: Problem ProtocolNotServed, and the message does not ask for InstallDriver", () =>
        {
            var e = Half(5, 5, new Version(3, 0, 0));
            return (e is HMDriverVersionException v && Problem(v) == "ProtocolNotServed" && !v.Message.Contains("InstallDriver", StringComparison.Ordinal),
                    e == null ? "passed" : $"{e.Message}");
        });
        c.Run("an older driver that serves protocols 2 to 3: Problem ProtocolNotServed, and the message asks for InstallDriver, which installs this SDK's whoever runs on that one", () =>
        {
            var e = Half(2, 3, new Version(1, 13, 0));
            return (e is HMDriverVersionException v && Problem(v) == "ProtocolNotServed" && v.Message.Contains("InstallDriver", StringComparison.Ordinal),
                    e == null ? "passed" : $"{e.Message}");
        });
    }

    /// <summary>A create refused before it made a device, for a resource
    /// another program holds, takes back only what it wrote and never
    /// sweeps the devices on its index, which can be another program's. A
    /// start failure made a device, and its teardown stays.</summary>
    static void RefusedCreate(Checks c)
    {
        var f = typeof(DeviceOrchestrator).GetMethod("RefusedBeforeDevices", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
        bool Refused(Exception e) => f == null ? throw new MissingMethodException("DeviceOrchestrator", "RefusedBeforeDevices") : (bool)f.Invoke(null, new object[] { e })!;
        c.Run("a resource another program holds is a refusal before any device", () =>
            (Refused(new HMResourceInUseException("held", "transport index 3", null)), ""));
        c.Run("a start failure is not: the create's own device is torn down", () =>
        {
            var error = With(_ => (true, HasProblem, 10u), _ => null, _ => (4, 4, new Version(2, 0, 0), 1), () => DeviceOrchestrator.StartError(Node, 3));
            return (!Refused(error), error.GetType().Name);
        });
    }
}
