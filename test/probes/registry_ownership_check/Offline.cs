// The part that needs no device and no elevation: which descriptors the
// GameInput table applies to, and that no catalog profile is one of them.

using System;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;

internal static partial class Program
{
    static HidDescriptorBuilder Canonical(int stickBits, int triggerBits, int buttons)
    {
        var d = new HidDescriptorBuilder().Gamepad();
        d.AddStick("Left", stickBits); d.AddStick("Right", stickBits);
        d.AddTrigger("Left", triggerBits); d.AddTrigger("Right", triggerBits);
        d.AddHat(); d.AddButtons(buttons);
        return d;
    }

    static void Offline()
    {
        Console.WriteLine("\n== Offline: the shape the GameInput table was measured on ==");
        Check("Gamepad: 16-bit sticks, 16-bit triggers, hat, 11 buttons qualifies",
              GameInputMapping.HasMeasuredShape(Canonical(16, 16, 11).Build()));
        Check("the same with 8-bit triggers qualifies",
              GameInputMapping.HasMeasuredShape(Canonical(16, 8, 11).Build()));
        Check("the same with 8-bit sticks and 16 buttons qualifies",
              GameInputMapping.HasMeasuredShape(Canonical(8, 8, 16).Build()));

        var joy = new HidDescriptorBuilder().Joystick();
        joy.AddStick("Left", 16); joy.AddStick("Right", 16); joy.AddTrigger("Left", 16); joy.AddTrigger("Right", 16);
        joy.AddHat(); joy.AddButtons(11);
        Check("a Joystick collection does not", !GameInputMapping.HasMeasuredShape(joy.Build()));

        var trigFirst = new HidDescriptorBuilder().Gamepad();
        trigFirst.AddTrigger("Left"); trigFirst.AddTrigger("Right");
        trigFirst.AddStick("Left"); trigFirst.AddStick("Right"); trigFirst.AddHat(); trigFirst.AddButtons(11);
        Check("triggers before sticks do not", !GameInputMapping.HasMeasuredShape(trigFirst.Build()));

        var noHat = new HidDescriptorBuilder().Gamepad();
        noHat.AddStick("Left"); noHat.AddStick("Right"); noHat.AddTrigger("Left"); noHat.AddTrigger("Right"); noHat.AddButtons(11);
        Check("no hat does not", !GameInputMapping.HasMeasuredShape(noHat.Build()));

        var fewButtons = Canonical(16, 8, 8);
        Check("eight buttons do not", !GameInputMapping.HasMeasuredShape(fewButtons.Build()));

        var extraAxis = new HidDescriptorBuilder().Gamepad();
        extraAxis.AddStick("Left"); extraAxis.AddStick("Right"); extraAxis.AddTrigger("Left"); extraAxis.AddTrigger("Right");
        extraAxis.AddHat(); extraAxis.AddButtons(11); extraAxis.AddAxis(HMAxis.Slider);
        Check("an extra slider does not", !GameInputMapping.HasMeasuredShape(extraAxis.Build()));

        var built = new HMProfileBuilder().Id("shape-check").Vid(0xBEEF).Pid(0xF0A6)
            .FromDescriptorBuilder(Canonical(16, 8, 11)).Build();
        Check("a profile built in code with the shape gets the table", GameInputMapping.Applies(built.Inner));

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var catalog = ctx.AllProfiles.ToList();
        var applies = catalog.Where(p => GameInputMapping.Applies(p.Inner)).Select(p => p.Id).ToList();
        Check($"no catalog profile gets the table ({catalog.Count} checked)", applies.Count == 0, string.Join(", ", applies));
        var shaped = catalog.Where(p => p.Inner.GetDescriptorBytes() is { } b && GameInputMapping.HasMeasuredShape(b)).Select(p => p.Id).ToList();
        Console.WriteLine($"  catalog descriptors with the measured shape: {(shaped.Count == 0 ? "none" : string.Join(", ", shaped))}");
        var clone = new HMProfileBuilder().FromProfile(ctx.GetProfile("dualsense")!).Pid(0xF0A8).Build();
        Check("a code-built clone of a catalog DualSense does not", !GameInputMapping.Applies(clone.Inner));

        LegacyGiveUp();
        WinUsbPackageNote();
    }

    /// <summary>The uninstall cleanup's note of the WinUSB packages it left.
    /// Each change comes from the call that makes it in the SDK: a package
    /// add (RegistryLedger.NoteWinUsbPackageAdded, taken through
    /// NoteForProbe in place of its write), and the cleanup's and the
    /// deferred run's removal (WinUsbBinding.NoteAfterRemoval, what
    /// RemovePackage writes). Every change is written when it is made,
    /// under the deploy lock, so the changes of every program apply in the
    /// order they were decided, as here.</summary>
    static void WinUsbPackageNote()
    {
        Console.WriteLine("\n== Offline: the note of the WinUSB packages a cleanup left ==");
        static string Show(string[] n) => n.Length == 0 ? "(none)" : string.Join(", ", n);
        static RegistryLedger.NoteChange Added(string inf)
        {
            RegistryLedger.NoteChange? taken = null;
            RegistryLedger.NoteForProbe = c => taken = c;
            try { RegistryLedger.NoteWinUsbPackageAdded(inf); }
            finally { RegistryLedger.NoteForProbe = null; }
            return taken ?? throw new InvalidOperationException("NoteWinUsbPackageAdded made no change");
        }
        static RegistryLedger.NoteChange Cleanup(params string[] kept) =>
            WinUsbBinding.NoteAfterRemoval(deferred: false, noted: Array.Empty<string>(), kept);
        static RegistryLedger.NoteChange Deferred(string[] noted, params string[] kept) =>
            WinUsbBinding.NoteAfterRemoval(deferred: true, noted, kept);
        static string[] Apply(string[] noted, params RegistryLedger.NoteChange[] changes)
        {
            foreach (var c in changes) noted = c.ApplyTo(noted);
            return noted;
        }
        const string X = "hmwinusb_x.inf", Y = "hmwinusb_y.inf", Z = "hmwinusb_z.inf";

        var a = Apply(Array.Empty<string>(), Cleanup(X), Added(Y));
        Check("a cleanup keeps X, then the program adds Y: the note still names X",
              a.SequenceEqual(new[] { X }), Show(a));
        var b = Apply(new[] { X }, Added(X));
        Check("adding X again takes X out of the note", b.Length == 0, Show(b));
        var c = Apply(new[] { X, Z }, Cleanup(Y));
        Check("a cleanup's whole listing makes the note what it kept, so a name it did not list goes",
              c.SequenceEqual(new[] { Y }), Show(c));
        var f = Apply(new[] { X }, Cleanup());
        Check("a cleanup that keeps nothing leaves no note", f.Length == 0, Show(f));
        var d = Apply(new[] { X, Z }, Deferred(new[] { X, Z }, Z));
        Check("the deferred run takes out every name it read, gone from the store or removed, and puts back what it kept",
              d.SequenceEqual(new[] { Z }), Show(d));
        // Program A's cleanup lists and keeps X. Then program B adds Y, and
        // its cleanup lists X and Y and keeps both. In decision order, B's
        // keeps stand.
        var e = Apply(Array.Empty<string>(), Cleanup(X), Added(Y), Cleanup(X, Y));
        Check("two programs' cleanups and an add, in decision order, leave every kept package named",
              e.SequenceEqual(new[] { X, Y }), Show(e));

        // A listing cut short, as when pnputil is ended at its timeout, is
        // no listing, and the cleanup then removes nothing and leaves the
        // note. A whole listing with no package is an empty one.
        const string whole = "<?xml version=\"1.0\"?><PnpUtil><Driver DriverName=\"oem7.inf\"><OriginalName>hmwinusb_x.inf</OriginalName>"
                           + "<ProviderName>HIDMaestro</ProviderName><DriverVersion>1.13.0.0</DriverVersion></Driver></PnpUtil>";
        Check("a whole listing parses", PnputilHelper.ParseDrivers(whole) is { Count: 1 } l && l[0].OriginalName == X);
        Check("a listing cut short is no listing", PnputilHelper.ParseDrivers(whole[..(whole.Length / 2)]) == null);
        Check("no output is no listing", PnputilHelper.ParseDrivers("") == null);
        // XDocument.Parse refuses a document with no root element ("Root
        // element is missing"), so ParseDrivers' own null-root guard is never
        // the one that answers.
        Check("output with no root element is no listing", PnputilHelper.ParseDrivers("<!-- pnputil -->") == null);
        Check("a whole listing with no driver is an empty one", PnputilHelper.ParseDrivers("<?xml version=\"1.0\"?><PnpUtil></PnpUtil>") is { Count: 0 });
        Check("a run that did not exit with 0 is no listing, whatever it printed", PnputilHelper.ListingOf(1, whole) == null);
        Check("a run that exited with 0 is its listing", PnputilHelper.ListingOf(0, whole) is { Count: 1 });

        // The removal's decision, RemovePackage's step from the listing to
        // what it deletes and keeps.
        static PnputilHelper.DriverRecord Rec(string published, string original, string provider = "HIDMaestro") =>
            new(published, original, provider, "1.13.0.0");
        var store = new System.Collections.Generic.List<PnputilHelper.DriverRecord>
        {
            Rec("oem7.inf", X), Rec("oem8.inf", Y), Rec("oem9.inf", "hmwinusb_q.inf", "Contoso"), Rec("oem10.inf", "hidmaestro.inf"),
        };
        // A devnode walk, by published name, as DevnodesByPackage returns
        // it: oem7.inf has a devnode on it.
        static System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> On(params string[] published)
        {
            var on = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pub in published) on[pub] = new() { @"USB\VID_057E&PID_0337\Z" };
            return on;
        }
        Check("a listing that failed plans no removal, so nothing is removed and the note stays",
              WinUsbBinding.PlanRemoval(null, deferred: false, Array.Empty<string>(), _ => On()) == null);
        Check("the deferred run plans no removal from a listing that failed either",
              WinUsbBinding.PlanRemoval(null, deferred: true, new[] { X }, _ => On()) == null);
        Check("a devnode walk that failed plans no removal, though the listing holds a package no devnode was seen on",
              WinUsbBinding.PlanRemoval(store, deferred: false, Array.Empty<string>(), _ => null) == null);
        var plan = WinUsbBinding.PlanRemoval(store, deferred: false, Array.Empty<string>(), _ => On("oem7.inf"));
        Check("the cleanup deletes the package no devnode uses, keeps the one a devnode uses, and lists only HIDMaestro's WinUSB packages",
              plan is { } pl && pl.Delete.Select(r => r.OriginalName).SequenceEqual(new[] { Y })
              && pl.Keep.SequenceEqual(new[] { X }),
              plan is { } p1 ? $"delete {Show(p1.Delete.Select(r => r.OriginalName).ToArray())}, keep {Show(p1.Keep)}" : "null");
        var deferredPlan = WinUsbBinding.PlanRemoval(store, deferred: true, new[] { X }, _ => On("oem7.inf"));
        Check("the deferred run looks only at what it noted, and deletes nothing a devnode uses",
              deferredPlan is { } dp && dp.Delete.Count == 0 && dp.Keep.SequenceEqual(new[] { X }),
              deferredPlan is { } p2 ? $"delete {Show(p2.Delete.Select(r => r.OriginalName).ToArray())}, keep {Show(p2.Keep)}" : "null");

        // The devnode walk itself, with the configuration manager's two
        // reads stood in for: a devnode list that could not be read, and a
        // devnode whose driver could not be read, as one that left between
        // two reads, are a walk that failed, never "no devnode".
        var ours = new System.Collections.Generic.List<PnputilHelper.DriverRecord> { Rec("oem7.inf", X) };
        static (bool, string?) OnOem7(string id) => (true, @"C:\Windows\INF\oem7.inf");
        Check("the walk finds the devnode on a package",
              WinUsbBinding.DevnodesByPackage(ours, () => new() { "USB\\A" }, OnOem7) is { } w1 && w1.ContainsKey("oem7.inf"));
        Check("a devnode with no driver installed uses no package",
              WinUsbBinding.DevnodesByPackage(ours, () => new() { "USB\\A" }, _ => (true, null)) is { Count: 0 });
        Check("a devnode list that could not be read is a walk that failed",
              WinUsbBinding.DevnodesByPackage(ours, () => null, OnOem7) == null);
        Check("a devnode whose driver could not be read is a walk that failed",
              WinUsbBinding.DevnodesByPackage(ours, () => new() { "USB\\A", "USB\\B" }, id => id == "USB\\B" ? (false, null) : (true, null)) == null);

        // The size query of a devnode's driver INF (WinUsbBinding.InfSize):
        // only CR_NO_SUCH_VALUE is "no driver". A failed read must never
        // count as one, or the package it uses goes to delete.
        const int CrBufferSmall = 0x1A, CrNoSuchValue = 0x25, CrNoSuchDevinst = 0x0D, CrRegistryError = 0x14;
        Check("an INF size query answering CR_NO_SUCH_VALUE is a devnode with no driver",
              WinUsbBinding.InfSize(CrNoSuchValue, 0) == WinUsbBinding.InfAnswer.NoDriver);
        Check("CR_BUFFER_SMALL with a length is an INF to read",
              WinUsbBinding.InfSize(CrBufferSmall, 120) == WinUsbBinding.InfAnswer.Read);
        Check("CR_BUFFER_SMALL with no length, CR_NO_SUCH_DEVINST, CR_REGISTRY_ERROR and a bare success, with a length or none, are failed reads",
              new[] { (CrBufferSmall, 0u), (CrNoSuchDevinst, 0u), (CrRegistryError, 0u), (0, 0u), (0, 120u) }
                  .All(t => WinUsbBinding.InfSize(t.Item1, t.Item2) == WinUsbBinding.InfAnswer.Failed));

        // The devnode list (WinUsbBinding.ReadIdList), with the size and
        // list calls answered here.
        static System.Collections.Generic.List<string>? Ids(int[] listAnswers, int sizeCr = 0, uint len = 8)
        {
            int call = 0;
            return WinUsbBinding.ReadIdList(() => (sizeCr, len), buf =>
            {
                int cr = listAnswers[Math.Min(call++, listAnswers.Length - 1)];
                if (cr == 0) "USB\\A\0".CopyTo(0, buf, 0, 6);
                return cr;
            });
        }
        Check("a list that grew three times between its size and its read is no list",
              Ids(new[] { CrBufferSmall, CrBufferSmall, CrBufferSmall }) == null);
        Check("a list that grew once is read again",
              Ids(new[] { CrBufferSmall, 0 }) is { Count: 1 } g && g[0] == "USB\\A");
        Check("a size query that failed is no list", Ids(new[] { 0 }, sizeCr: CrRegistryError) == null);
        Check("a list read that failed is no list", Ids(new[] { CrRegistryError }) == null);
        Check("a size of one is an empty list", Ids(new[] { 0 }, len: 1) is { Count: 0 });

        // What the cleanup notes when its walk fails: every package it
        // listed, so a later sweep's deferred run plans them again.
        var undecided = WinUsbBinding.DecideRemoval(store, deferred: false, Array.Empty<string>(), _ => null);
        Check("a cleanup whose walk failed removes nothing and notes every package it listed",
              undecided.Plan == null && undecided.Undecided is { } un
              && un.ApplyTo(new[] { "hmwinusb_old.inf" }).SequenceEqual(new[] { X, Y }, StringComparer.OrdinalIgnoreCase),
              undecided.Undecided is { } u1 ? Show(u1.ApplyTo(Array.Empty<string>())) : "no note");
        Check("a deferred run whose walk failed changes no note",
              WinUsbBinding.DecideRemoval(store, deferred: true, new[] { X }, _ => null) is { Plan: null, Undecided: null });
        Check("a cleanup whose listing failed changes no note",
              WinUsbBinding.DecideRemoval(null, deferred: false, Array.Empty<string>(), _ => On()) is { Plan: null, Undecided: null });
        Check("a cleanup that decided notes through its plan, not the undecided note",
              WinUsbBinding.DecideRemoval(store, deferred: false, Array.Empty<string>(), _ => On("oem7.inf")) is { Plan: not null, Undecided: null });

        // The look that decides whether the deferred run takes the deploy
        // lock at all (WinUsbBinding.LeftRemovalWouldChange).
        var both = new System.Collections.Generic.List<PnputilHelper.DriverRecord> { Rec("oem7.inf", X), Rec("oem12.inf", X), Rec("oem8.inf", Y) };
        Check("the look: a noted name no longer in the store makes the run worth it",
              WinUsbBinding.LeftRemovalWouldChange(new[] { X, Z }, store, _ => On("oem7.inf")));
        Check("the look: a noted package no devnode uses makes the run worth it",
              WinUsbBinding.LeftRemovalWouldChange(new[] { X }, store, _ => On()));
        Check("the look: every noted package kept by a devnode is not worth a run",
              !WinUsbBinding.LeftRemovalWouldChange(new[] { X }, store, _ => On("oem7.inf")));
        Check("the look: a listing that failed is not worth a run",
              !WinUsbBinding.LeftRemovalWouldChange(new[] { X }, null, _ => On()));
        Check("the look: a devnode walk that failed is not worth a run",
              !WinUsbBinding.LeftRemovalWouldChange(new[] { X }, store, _ => null));
        Check("the look: two packages of one noted name, both in use, do not hide another noted name gone from the store",
              WinUsbBinding.LeftRemovalWouldChange(new[] { X, Z }, both, _ => On("oem7.inf", "oem12.inf")));
        // Case. pnputil and the devnode's INF path name the same package in
        // whatever case each reads, so every comparison of a published or
        // INF name, a provider or a noted name ignores case.
        Check("the walk matches a devnode's INF path to a published name in another case",
              WinUsbBinding.DevnodesByPackage(ours, () => new() { "USB\\A" }, _ => (true, @"C:\WINDOWS\INF\OEM7.INF")) is { } wc && wc.ContainsKey("oem7.inf"));
        var cased = new System.Collections.Generic.List<PnputilHelper.DriverRecord> { Rec("oem20.inf", "HMWINUSB_W.INF", "hidmaestro") };
        Check("a listing's provider and INF name in another case are still HIDMaestro's WinUSB package",
              WinUsbBinding.PlanRemoval(cased, deferred: false, Array.Empty<string>(), _ => On()) is { } pc
              && pc.Delete.Select(r => r.OriginalName).SequenceEqual(new[] { "HMWINUSB_W.INF" }));
        Check("the deferred run looks at a noted name in another case",
              WinUsbBinding.PlanRemoval(store, deferred: true, new[] { "HMWINUSB_Y.INF" }, _ => On("oem7.inf")) is { } pd
              && pd.Delete.Select(r => r.OriginalName).SequenceEqual(new[] { Y }));
        Check("the look reads a noted name in another case as the package in the store",
              !WinUsbBinding.LeftRemovalWouldChange(new[] { "HMWINUSB_X.INF" }, store, _ => On("oem7.inf")));
        Check("the look counts a name noted twice, in two cases, as one name in the store",
              !WinUsbBinding.LeftRemovalWouldChange(new[] { X, "HMWINUSB_X.INF" }, store, _ => On("oem7.inf")));
        Check("an add in another case takes the noted name out",
              RegistryLedger.NoteChange.Added("HMWINUSB_X.INF").ApplyTo(new[] { X }).Length == 0);
        Check("a walk of no package reads nothing and is an empty walk, not a failed one",
              WinUsbBinding.DevnodesByPackage(new(), () => null, _ => (false, null)) is { Count: 0 });

        // DriverInfOf's reads (WinUsbBinding.DriverInfFrom): the locate, the
        // size query and the read, each answered here. A locate or a read
        // that fails is a failed read, never "no driver", and the INF name
        // comes back without its terminator, as DevnodesByPackage matches it.
        static (bool, string?) InfRead(int locateCr, int sizeCr, int readCr, string value)
        {
            byte[] bytes = System.Text.Encoding.Unicode.GetBytes(value + "\0");
            return WinUsbBinding.DriverInfFrom(() => (locateCr, 7u), (dev, buf) =>
            {
                if (dev != 7) throw new InvalidOperationException("the read went to another devnode");
                if (buf == null) return (sizeCr, sizeCr == CrBufferSmall ? (uint)bytes.Length : 0u);
                if (readCr != 0) return (readCr, 0u);
                bytes.CopyTo(buf, 0);
                return (0, (uint)bytes.Length);
            });
        }
        Check("a devnode the phantom locate cannot find is a failed read",
              InfRead(CrNoSuchDevinst, CrBufferSmall, 0, "oem13.inf") == (false, null));
        Check("a size query answering CR_NO_SUCH_VALUE is a devnode with no driver",
              InfRead(0, CrNoSuchValue, 0, "oem13.inf") == (true, null));
        Check("a size query that fails is a failed read",
              InfRead(0, CrRegistryError, 0, "oem13.inf") == (false, null));
        Check("a second read that fails is a failed read",
              InfRead(0, CrBufferSmall, CrRegistryError, "oem13.inf") == (false, null));
        Check("a whole read is the INF name without its terminator",
              InfRead(0, CrBufferSmall, 0, "oem13.inf") is (true, "oem13.inf"));
        Check("a read of a full path keeps the path, without its terminator",
              InfRead(0, CrBufferSmall, 0, @"C:\Windows\INF\oem13.inf") is (true, @"C:\Windows\INF\oem13.inf"));

        // RemovePackage's work under its locks (WinUsbBinding.RemoveUnderLocks),
        // with the note, the listing, the walk, the gone test, the flag and
        // the delete answered here, and each note change taken in place of
        // its write. Its devnode on oem7.inf is USB\VID_057E&PID_0337\Z.
        static (System.Collections.Generic.List<string> Deleted, System.Collections.Generic.List<string> Flagged,
                System.Collections.Generic.List<RegistryLedger.NoteChange> Notes, bool Listed)
            Remove(bool deferred, string[] noted, System.Collections.Generic.List<PnputilHelper.DriverRecord>? listing,
                   Func<System.Collections.Generic.List<PnputilHelper.DriverRecord>, System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>?> walk,
                   bool gone = true, bool deletes = true)
        {
            var deleted = new System.Collections.Generic.List<string>();
            var flagged = new System.Collections.Generic.List<string>();
            var notes = new System.Collections.Generic.List<RegistryLedger.NoteChange>();
            bool listedCalled = false;
            RegistryLedger.NoteForProbe = c => notes.Add(c);
            try
            {
                WinUsbBinding.RemoveUnderLocks(deferred, () => noted, () => { listedCalled = true; return listing; }, walk,
                    _ => gone, id => flagged.Add(id), pub => { deleted.Add(pub); return (deletes, deletes ? "" : "in use"); });
            }
            finally { RegistryLedger.NoteForProbe = null; }
            return (deleted, flagged, notes, listedCalled);
        }
        static string[] NoteFrom(System.Collections.Generic.List<RegistryLedger.NoteChange> notes, params string[] before)
        {
            foreach (var n in notes) before = n.ApplyTo(before);
            return before;
        }
        const string Dev = @"USB\VID_057E&PID_0337\Z";
        var r1 = Remove(false, Array.Empty<string>(), store, _ => On("oem7.inf"));
        Check("the cleanup deletes the package no devnode uses, flags the gone devnode on the one it keeps, and notes that one",
              r1.Deleted.SequenceEqual(new[] { "oem8.inf" }) && r1.Flagged.SequenceEqual(new[] { Dev })
              && r1.Notes.Count == 1 && NoteFrom(r1.Notes, "hmwinusb_old.inf").SequenceEqual(new[] { X }),
              $"deleted {Show(r1.Deleted.ToArray())}, flagged {Show(r1.Flagged.ToArray())}, note {Show(NoteFrom(r1.Notes, "hmwinusb_old.inf"))}");
        var r2 = Remove(false, Array.Empty<string>(), store, _ => On("oem7.inf"), gone: false);
        Check("the cleanup flags no devnode that is still present", r2.Flagged.Count == 0 && r2.Deleted.SequenceEqual(new[] { "oem8.inf" }),
              $"flagged {Show(r2.Flagged.ToArray())}");
        var r3 = Remove(false, Array.Empty<string>(), store, _ => On("oem7.inf"), deletes: false);
        Check("a package whose delete fails stays in the note",
              NoteFrom(r3.Notes).SequenceEqual(new[] { X, Y }), Show(NoteFrom(r3.Notes)));
        var r4 = Remove(false, Array.Empty<string>(), store, _ => null);
        Check("a cleanup whose walk fails deletes and flags nothing and notes every package it listed",
              r4.Deleted.Count == 0 && r4.Flagged.Count == 0 && r4.Notes.Count == 1
              && NoteFrom(r4.Notes, "hmwinusb_old.inf").SequenceEqual(new[] { X, Y }),
              $"deleted {Show(r4.Deleted.ToArray())}, note {Show(NoteFrom(r4.Notes, "hmwinusb_old.inf"))}");
        var r5 = Remove(false, Array.Empty<string>(), null, _ => On());
        Check("a cleanup whose listing fails deletes nothing and leaves the note",
              r5.Deleted.Count == 0 && r5.Flagged.Count == 0 && r5.Notes.Count == 0);
        var r6 = Remove(true, new[] { X, Z }, store, _ => On("oem7.inf"));
        Check("the deferred run deletes nothing a devnode uses, and its note drops the name gone from the store",
              r6.Deleted.Count == 0 && NoteFrom(r6.Notes, X, Z).SequenceEqual(new[] { X }), Show(NoteFrom(r6.Notes, X, Z)));
        var r7 = Remove(true, Array.Empty<string>(), store, _ => On());
        Check("the deferred run with nothing noted lists nothing and changes nothing",
              !r7.Listed && r7.Deleted.Count == 0 && r7.Notes.Count == 0);
        var r8 = Remove(true, new[] { Y }, store, _ => On("oem7.inf"));
        Check("the deferred run deletes the noted package no devnode uses, and only it, and keeps every other noted name",
              r8.Deleted.SequenceEqual(new[] { "oem8.inf" }) && NoteFrom(r8.Notes, Y, "hmwinusb_w.inf").SequenceEqual(new[] { "hmwinusb_w.inf" }),
              $"deleted {Show(r8.Deleted.ToArray())}, note {Show(NoteFrom(r8.Notes, Y, "hmwinusb_w.inf"))}");
        var r9 = Remove(true, new[] { Z }, store, p => WinUsbBinding.DevnodesByPackage(p, () => null, _ => (false, null)));
        Check("the deferred run drops a noted name no package in the store has, though no devnode can be read",
              r9.Deleted.Count == 0 && NoteFrom(r9.Notes, Z).Length == 0, Show(NoteFrom(r9.Notes, Z)));

        // A package add's outcome (WinUsbBinding.RecordAdd): its published
        // name, and the note loses the add's own name and keeps the rest.
        var added = new System.Collections.Generic.List<RegistryLedger.NoteChange>();
        RegistryLedger.NoteForProbe = c => added.Add(c);
        string? addedAs = null;
        bool addThrew = false;
        try
        {
            addedAs = WinUsbBinding.RecordAdd("HMWINUSB_X.INF", new() { Rec("oem7.inf", X), Rec("oem8.inf", Y) }, 0, "");
            try { WinUsbBinding.RecordAdd(Z, new() { Rec("oem7.inf", X) }, 1, "failed"); }
            catch (InvalidOperationException) { addThrew = true; }
        }
        finally { RegistryLedger.NoteForProbe = null; }
        Check("an add finds its package by INF name in any case and takes only that name out of the note",
              addedAs == "oem7.inf" && added.Count == 1 && NoteFrom(added, X, Y).SequenceEqual(new[] { Y }),
              $"{addedAs}, note {Show(NoteFrom(added, X, Y))}");
        Check("an add whose package is not in the listing throws and changes no note", addThrew && added.Count == 1);

        // The note's write (RegistryLedger.NoteWithin), the call that decides
        // whether the deferred run is due (LeftToRemove) and the one that
        // starts it (RunLeftRemoval).
        string[]? written = Array.Empty<string>();
        RegistryLedger.NoteWithin(RegistryLedger.NoteChange.Added(X), () => new[] { X }, n => written = n);
        Check("a change that leaves no name deletes the note", written == null);
        RegistryLedger.NoteWithin(RegistryLedger.NoteChange.Cleanup(new[] { X }, new[] { Y }), () => new[] { X, Z }, n => written = n);
        Check("a change is applied to the note as it stands and written", written is { } wn && wn.SequenceEqual(new[] { Y, Z }),
              written == null ? "deleted" : Show(written));
        Check("the deferred run is due with a name noted and no WinUSB entry", RegistryLedger.LeftToRemove(new[] { X }, 0));
        Check("not while a WinUSB entry remains", !RegistryLedger.LeftToRemove(new[] { X }, 1));
        Check("nor with nothing noted", !RegistryLedger.LeftToRemove(Array.Empty<string>(), 0));
        string[]? lookedAt = null;
        bool ran = false;
        RegistryLedger.RunLeftRemoval(() => new[] { X }, n => { lookedAt = n; return true; }, () => ran = true);
        Check("the deferred run runs when the look says it can change something, and the look reads the note",
              ran && lookedAt is { } la && la.SequenceEqual(new[] { X }));
        ran = false;
        RegistryLedger.RunLeftRemoval(() => new[] { X }, _ => false, () => ran = true);
        Check("and does not run when the look says it cannot", !ran);

        // The note's registry reads and writes themselves, against HKCU,
        // which this part writes without elevation: the same key paths the
        // SDK uses under HKLM. Nothing in HKCU uses them, and each key made
        // here is deleted after.
        const string NotePath = @"SOFTWARE\HIDMaestroPackagesLeft", LedgerPath = @"SOFTWARE\HIDMaestroRegistryLedger";
        var hkcu = Microsoft.Win32.Registry.CurrentUser;
        bool clear;
        using (var k1 = hkcu.OpenSubKey(NotePath)) using (var k2 = hkcu.OpenSubKey(LedgerPath)) clear = k1 == null && k2 == null;
        if (!clear)
            Console.WriteLine($"  not checked here: HKCU\\{NotePath} or HKCU\\{LedgerPath} exists, and this part writes only keys it made");
        else
        {
            try
            {
                RegistryLedger.WriteNote(hkcu, new[] { X, Y });
                Check("the note's write and read keep its names",
                      RegistryLedger.NotedPackages(hkcu).SequenceEqual(new[] { X, Y }), Show(RegistryLedger.NotedPackages(hkcu)));
                Check("with no WinUSB entry the noted packages are left to remove",
                      RegistryLedger.PackagesLeftNoted(hkcu) && RegistryLedger.LeftToRemoveIn(hkcu).SequenceEqual(new[] { X, Y }));
                using (hkcu.CreateSubKey(LedgerPath + @"\WinUsb\HMZENTRY")) { }
                Check("while a WinUSB entry remains none is left to remove",
                      !RegistryLedger.PackagesLeftNoted(hkcu) && RegistryLedger.LeftToRemoveIn(hkcu).Length == 0);
                hkcu.DeleteSubKeyTree(LedgerPath + @"\WinUsb\HMZENTRY");
                Check("an empty WinUSB key holds no entry", RegistryLedger.PackagesLeftNoted(hkcu));
                RegistryLedger.NoteWithin(RegistryLedger.NoteChange.Cleanup(new[] { X, Y }, Array.Empty<string>()),
                    () => RegistryLedger.NotedPackages(hkcu), n => RegistryLedger.WriteNote(hkcu, n));
                bool gone;
                using (var k = hkcu.OpenSubKey(NotePath)) gone = k == null;
                Check("a change that leaves no name deletes the note's key, so nothing is left to remove",
                      gone && RegistryLedger.NotedPackages(hkcu).Length == 0 && !RegistryLedger.PackagesLeftNoted(hkcu));

                // The two reads the SDK binds to HKLM, NoteIn for every note
                // change and LeftToRemoveLocked for the deferred run, here
                // against HKCU with a lock that records its take and its
                // release.
                var steps = new System.Collections.Generic.List<string>();
                Func<Action> recorded = () => { steps.Add("take"); return () => steps.Add("release"); };
                RegistryLedger.WriteNote(hkcu, new[] { X, Y });
                RegistryLedger.NoteIn(hkcu, RegistryLedger.NoteChange.Added(Y), recorded);
                Check("NoteIn applies a change to the note it reads, under its lock",
                      RegistryLedger.NotedPackages(hkcu).SequenceEqual(new[] { X }) && steps.SequenceEqual(new[] { "take", "release" }),
                      $"{Show(RegistryLedger.NotedPackages(hkcu))}, {string.Join(",", steps)}");
                steps.Clear();
                RegistryLedger.NoteIn(hkcu, RegistryLedger.NoteChange.Cleanup(new[] { X }, Array.Empty<string>()), recorded);
                using (var k = hkcu.OpenSubKey(NotePath)) gone = k == null;
                Check("NoteIn deletes the key when the change leaves no name", gone && steps.SequenceEqual(new[] { "take", "release" }));
                steps.Clear();
                RegistryLedger.NoteIn(hkcu, RegistryLedger.NoteChange.Added(X), () => throw new TimeoutException("the lock"));
                using (var k = hkcu.OpenSubKey(NotePath)) gone = k == null;
                Check("NoteIn whose lock is not taken writes nothing and does not throw", gone);
                RegistryLedger.WriteNote(hkcu, new[] { X });
                string[] left = RegistryLedger.LeftToRemoveLocked(hkcu, recorded);
                Check("LeftToRemoveLocked reads the noted packages under its lock when no WinUSB entry remains",
                      left.SequenceEqual(new[] { X }) && steps.SequenceEqual(new[] { "take", "release" }),
                      $"{Show(left)}, {string.Join(",", steps)}");
                steps.Clear();
                using (hkcu.CreateSubKey(LedgerPath + @"\WinUsb\HMZENTRY")) { }
                left = RegistryLedger.LeftToRemoveLocked(hkcu, recorded);
                Check("LeftToRemoveLocked leaves none while a WinUSB entry remains, under its lock",
                      left.Length == 0 && steps.SequenceEqual(new[] { "take", "release" }), Show(left));
            }
            finally
            {
                hkcu.DeleteSubKeyTree(NotePath, throwOnMissingSubKey: false);
                hkcu.DeleteSubKeyTree(LedgerPath, throwOnMissingSubKey: false);
            }
        }

        // The plain delete's command and how each pnputil answer reads
        // (WinUsbBinding.DeletePlain).
        string? asked = null;
        Check("a plain delete runs /delete-driver on the package alone, with no /uninstall or /force",
              WinUsbBinding.DeletePlain("oem7.inf", a => { asked = a; return (0, "Driver package deleted successfully."); }).Deleted
              && asked == "/delete-driver oem7.inf", asked ?? "not run");
        Check("a plain delete that pnputil refuses on exit code 0 is not done",
              WinUsbBinding.DeletePlain("oem7.inf", _ => (0, "Failed to delete driver package: One or more devices are presently installed using the specified INF.")) is (false, { Length: > 0 }));
        Check("a plain delete whose pnputil exits with an error is not done",
              !WinUsbBinding.DeletePlain("oem7.inf", _ => (-1, "")).Deleted);

        // The listing's call: pnputil's exit code reaches ListingOf.
        Check("a listing whose pnputil run did not exit with 0 is no listing, at TryEnumerateDrivers",
              PnputilHelper.TryEnumerateDrivers(() => (1, whole)) == null);
        Check("a listing whose run exited with 0 is read, at TryEnumerateDrivers",
              PnputilHelper.TryEnumerateDrivers(() => (0, whole)) is { Count: 1 } te
              && te[0] is { PublishedName: "oem7.inf", OriginalName: X, ProviderName: "HIDMaestro", DriverVersion: "1.13.0.0" });

        var realStore = PnputilHelper.TryEnumerateDrivers();
        if (realStore == null)
            Console.WriteLine("  not checked here: pnputil could not list the driver store");
        else
        {
            Check("the look the sweep calls: a noted name this driver store does not hold makes the run worth it",
                  WinUsbBinding.LeftRemovalCanChange(new[] { "hmwinusb_zz-never-added.inf" }));
            // RemovePackage's own walk. A package a devnode uses on this
            // machine must be kept by the plan the cleanup takes.
            var ourReal = realStore.Where(r => r.ProviderName.Equals("HIDMaestro", StringComparison.OrdinalIgnoreCase)
                                            && r.OriginalName.StartsWith("hmwinusb_", StringComparison.OrdinalIgnoreCase)).ToList();
            // The production walk itself, for a package no devnode uses, so
            // it reads every USB devnode on this machine whatever the store
            // holds. It must be whole. Taken twice when the first fails, for
            // a list that changed under the read.
            var none = new System.Collections.Generic.List<PnputilHelper.DriverRecord> { Rec("oem-hmz-none.inf", "hmwinusb_zz-never-added.inf") };
            var wholeWalk = WinUsbBinding.DevnodesByPackage(none) ?? WinUsbBinding.DevnodesByPackage(none);
            Check("the walk of this machine's USB devnodes, present and not, is whole", wholeWalk is { Count: 0 },
                  wholeWalk == null ? "the walk failed" : $"{wholeWalk.Count} on a package no devnode uses");
            var usedReal = WinUsbBinding.DevnodesByPackage(ourReal) ?? WinUsbBinding.DevnodesByPackage(ourReal);
            Check("the walk for the WinUSB packages in this machine's store is whole", usedReal != null);
            // The same question answered from the registry, with none of the
            // walk's code: each USB instance's Driver value names its class
            // key, whose InfPath is the published INF it was installed from.
            var oracle = DriversFromEnum(ourReal.Select(r => r.PublishedName));
            if (oracle == null)
                Check("the Enum and class keys of this machine's USB devnodes can be read", false);
            else if (usedReal != null)
            {
                static string Pairs(System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> m) =>
                    m.Count == 0 ? "(none)" : string.Join(" | ", m.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                        .Select(k => $"{k.Key}: {string.Join(", ", k.Value.OrderBy(v => v, StringComparer.OrdinalIgnoreCase))}"));
                bool same = oracle.Count == usedReal.Count
                    && oracle.All(k => usedReal.TryGetValue(k.Key, out var ids)
                        && new System.Collections.Generic.HashSet<string>(ids, StringComparer.OrdinalIgnoreCase).SetEquals(k.Value));
                Check("the walk finds on each WinUSB package exactly the devnodes the Enum and class keys put there",
                      same, $"walk {Pairs(usedReal)}, registry {Pairs(oracle)}");
                if (oracle.Count == 0 && usedReal.Count == 0)
                    Console.WriteLine($"  not checked here: no devnode on this machine uses one of HIDMaestro's WinUSB packages ({ourReal.Count} in the store)");
                else
                {
                    var realPlan = WinUsbBinding.PlanRemoval(realStore, deferred: false, Array.Empty<string>());
                    Check("the cleanup's plan from this machine's store and devnodes keeps every package a devnode uses",
                          realPlan is { } rp && ourReal.Where(r => oracle.ContainsKey(r.PublishedName)).All(r => rp.Keep.Contains(r.OriginalName, StringComparer.OrdinalIgnoreCase))
                          && rp.Delete.All(r => !oracle.ContainsKey(r.PublishedName)),
                          realPlan is { } rp2 ? $"keep {Show(rp2.Keep)}, delete {Show(rp2.Delete.Select(r => r.OriginalName).ToArray())}" : "null");
                }
            }
        }

        // What a deferred run's look with no lock costs here: the listing
        // and one walk of the USB devnodes. Until round 8 the deploy lock,
        // which every UMDF2 create waits on, was held across this and a
        // pnputil delete in every program's first sweep while a gone
        // devnode kept a noted package. Now that case takes no deploy lock.
        var noted = new[] { "hmwinusb_gamecube-adapter.inf" };
        var ms = new System.Collections.Generic.List<double>();
        bool can = false;
        for (int i = 0; i < 10; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            can = WinUsbBinding.LeftRemovalCanChange(noted);
            ms.Add(sw.Elapsed.TotalMilliseconds);
        }
        ms.Sort();
        bool listed = PnputilHelper.TryEnumerateDrivers() is { } all
            && all.Any(r => r.OriginalName.Equals(noted[0], StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(FormattableString.Invariant(
            $"  the look a deferred run takes before the deploy lock, 10 runs: p50 {ms[5]:F1} ms, max {ms[9]:F1} ms (would run: {can}, {noted[0]} in the store: {listed})"));
    }

    /// <summary>Every USB devnode, present or not, on each of
    /// <paramref name="published"/>, read from the registry with none of
    /// WinUsbBinding's code: each instance key under Enum\USB, its Driver
    /// value, and that class key's InfPath, the INF the device was installed
    /// from. Null when a key cannot be read. Read without elevation.</summary>
    static System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>? DriversFromEnum(System.Collections.Generic.IEnumerable<string> published)
    {
        var names = new System.Collections.Generic.HashSet<string>(published, StringComparer.OrdinalIgnoreCase);
        var on = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var usb = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return null;
            foreach (string device in usb.GetSubKeyNames())
            {
                using var dk = usb.OpenSubKey(device);
                if (dk == null) return null;
                foreach (string instance in dk.GetSubKeyNames())
                {
                    using var ik = dk.OpenSubKey(instance);
                    if (ik == null) return null;
                    if (ik.GetValue("Driver") is not string driver) continue;
                    using var ck = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{driver}");
                    if (ck?.GetValue("InfPath") is not string inf || !names.Contains(inf)) continue;
                    if (!on.TryGetValue(inf, out var ids)) on[inf] = ids = new();
                    ids.Add($@"USB\{device}\{instance}");
                }
            }
        }
        catch (System.Security.SecurityException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        return on;
    }

    /// <summary>When the ledger lock gives up on v1.12.0's mutex: the five
    /// cases of LedgerLock.GivesUp, the decision after its wait. A bound of
    /// INFINITE is an owner read as SYSTEM or Administrators, 5000 ms an
    /// owner read as neither.</summary>
    static void LegacyGiveUp()
    {
        Console.WriteLine("\n== Offline: when the ledger lock gives up on v1.12.0's mutex ==");
        const uint Infinite = 0xFFFFFFFF;
        Check("an owner read as SYSTEM or Administrators, the caller's bound cut the wait: it gives up",
              LedgerLock.GivesUp(held: false, timeout: 29_990, full: Infinite));
        Check("an owner read as neither, the caller's bound cut the wait: it gives up, never writing without the mutex",
              LedgerLock.GivesUp(held: false, timeout: 10, full: 5000));
        Check("an owner read as neither, the lock's own 5 s ran out: it goes on without the mutex, as with a squatter",
              !LedgerLock.GivesUp(held: false, timeout: 5000, full: 5000));
        Check("the mutex taken within a cut wait: it goes on, holding it",
              !LedgerLock.GivesUp(held: true, timeout: 10, full: 5000));
        Check("an unbounded wait on an owner read as SYSTEM or Administrators that failed: unchanged, it goes on",
              !LedgerLock.GivesUp(held: false, timeout: Infinite, full: Infinite));
    }
}
