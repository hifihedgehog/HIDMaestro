// U. A create that fails after it claimed a transport index and an
//    identity. The program's record is made to refuse the write the create
//    needs, once before any device exists and once after the device is
//    made, and once over an entry it cannot write that is already there.
//    The index's input event exists, owned by Administrators and closed to
//    signaling, so its input mapping fails partway. A composite's device
//    emulator fails the same way after it mapped the index's sections, and
//    once more after its input pump started. Each time the create leaves
//    no claim pipe, no device, no record entry and no section, the program
//    keeps running, and the same identity creates again at once, in the
//    same program and in another. Before each lock every object of the
//    index is gone, the name the lock takes included, and the program
//    whose controller was disposed holds none of them. Last, a v1.12.0
//    program starts an Xbox 360 controller on the index while a create
//    waits before its mapping: the create is refused, and the v1.12.0
//    program's devices, configuration and sections stay as they were.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

internal static partial class Program
{
    const ushort UPid = 0xF1B0;

    static void SectionU()
    {
        using var a = PeerProcess.Start("U1");
        string c0 = a.Send("create pad F1B0 -");
        if (!Ok(c0)) { Check("U: the program creates", false, c0); return; }
        string t = PeerProcess.Field(c0, "transport")!;
        string? ownerId = Native.OwnerRecords().Where(r => r.Value == a.Pid).Select(r => r.Key).FirstOrDefault();
        a.Send("dispose");
        if (ownerId == null) { Check("U: the program has an owner record", false); return; }
        if (!WaitFor(() => Native.HidPaths(0xBEEF, UPid).Count == 0, 10_000))
        {
            Check("U: the first controller is gone after its dispose", false);
            return;
        }
        string controllers = $@"SOFTWARE\HIDMaestroOwners\{ownerId}\Controllers";
        HashSet<string> baseline = ClaimPipes();

        // The entry a create writes before any PnP work is refused: the
        // record's Controllers key denies creating a subkey.
        var noSubkey = DenyRule(RegistryRights.CreateSubKey, children: false);
        string f1;
        SetDeny(controllers, noSubkey, on: true);
        try { f1 = a.Send("create pad F1B0 -"); }
        finally { SetDeny(controllers, noSubkey, on: false); }
        Check("U: a create whose entry write is refused fails", !Ok(f1), f1);
        UnclaimedAndUnrecorded("U: after it", baseline, controllers, entriesChecked: true);

        using (var b = PeerProcess.Start("U2"))
        {
            string cb = b.Send($"create pad F1B0 index:{t}");
            Check($"U: another program creates identity index:{t} at once", Ok(cb), cb);
            b.Send("dispose");
        }
        WaitFor(() => Native.HidPaths(0xBEEF, UPid).Count == 0, 10_000);
        string r1 = a.Send("create pad F1B0 -");
        Check($"U: a retry in the failed program takes transport index {t} and identity index:{t}",
              Ok(r1) && PeerProcess.Field(r1, "transport") == t && PeerProcess.Field(r1, "key") == "index:" + t, r1);
        a.Send("dispose");
        WaitFor(() => Native.HidPaths(0xBEEF, UPid).Count == 0, 10_000);

        // The same refusal for a keyed create, whose identity is claimed
        // before the transport index.
        string f2;
        SetDeny(controllers, noSubkey, on: true);
        try { f2 = a.Send("create pad F1B0 coexist-u"); }
        finally { SetDeny(controllers, noSubkey, on: false); }
        Check("U: a keyed create whose entry write is refused fails", !Ok(f2), f2);
        UnclaimedAndUnrecorded("U: after the keyed one", baseline, controllers, entriesChecked: true);
        using (var b = PeerProcess.Start("U3"))
        {
            string cb = b.Send("create pad F1B0 coexist-u");
            Check("U: another program creates key coexist-u at once", Ok(cb), cb);
            b.Send("dispose");
        }
        WaitFor(() => Native.HidPaths(0xBEEF, UPid).Count == 0, 10_000);
        string r2 = a.Send("create pad F1B0 coexist-u");
        Check("U: a retry of key coexist-u in the failed program creates", Ok(r2), r2);
        a.Send("dispose");
        WaitFor(() => Native.HidPaths(0xBEEF, UPid).Count == 0, 10_000);

        // An entry for the identity is already there and refuses writes,
        // as an entry a failed removal left would. The create's write
        // fails on it, and the failed create removes it all the same: the
        // removal needs DELETE on the entry, not the write access that was
        // refused.
        string stale = $@"{controllers}\HM_{int.Parse(t):D4}";
        var noValueHere = DenyRule(RegistryRights.SetValue, children: false);
        using (Registry.LocalMachine.CreateSubKey(stale)) { }
        SetDeny(stale, noValueHere, on: true);
        try
        {
            string f2b = a.Send("create pad F1B0 -");
            Check("U: a create over an entry it cannot write fails", !Ok(f2b), f2b);
            UnclaimedAndUnrecorded("U: after it", baseline, controllers, entriesChecked: true);
        }
        finally
        {
            // Only when the create left it.
            using (var k = Registry.LocalMachine.OpenSubKey(stale))
                if (k != null) SetDeny(stale, noValueHere, on: false);
            Registry.LocalMachine.DeleteSubKeyTree(stale, throwOnMissingSubKey: false);
        }

        // The device is made, then the entry refuses its instance id. A
        // controller's new entry inherits a deny on setting values: the
        // create's own handle wrote the entry, and reopening it to add the
        // instance id after the device exists fails. The removal of the
        // entry needs no write access, so the deny does not stop it.
        var noValue = DenyRule(RegistryRights.SetValue, children: true);
        string f3;
        SetDeny(controllers, noValue, on: true);
        try
        {
            f3 = a.Send("create pad F1B0 -");
            Check("U: a create whose instance id write is refused fails", !Ok(f3), f3);
            Check("U: no device of it is left",
                  WaitFor(() => Native.HidPaths(0xBEEF, UPid).Count == 0, 10_000),
                  $"{Native.HidPaths(0xBEEF, UPid).Count} present");
            UnclaimedAndUnrecorded("U: after the refused instance id", baseline, controllers, entriesChecked: true);
        }
        finally { SetDeny(controllers, noValue, on: false); }
        // A failed check above leaves entries the next steps would read.
        using (var cs = Registry.LocalMachine.OpenSubKey(controllers, writable: true))
            foreach (var name in cs?.GetSubKeyNames() ?? Array.Empty<string>())
                cs!.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        string r3 = a.Send("create pad F1B0 -");
        Check($"U: a retry in the failed program takes transport index {t} and identity index:{t} again",
              Ok(r3) && PeerProcess.Field(r3, "transport") == t && PeerProcess.Field(r3, "key") == "index:" + t, r3);
        Check("U: and one device of it is present", WaitPaths(0xBEEF, UPid, 1, 15_000).Count == 1);
        a.Send("dispose");
        Check("U: its dispose leaves no entry", WaitFor(() => Entries(controllers).Count == 0, 15_000),
              string.Join(",", Entries(controllers)));
        WaitFor(() => Native.HidPaths(0xBEEF, UPid).Count == 0, 10_000);

        // The input mapping's last step creates the input event. The probe
        // holds an event of that name that Administrators own, as they own
        // HIDMaestro's, so the transport allocator keeps the index. The
        // event grants nobody the right to signal or wait on it, so the
        // create's open of it is refused after every other object of the
        // mapping exists. Only index t carries the event, so the denial in
        // the reply shows the create reached index t and failed there.
        bool clear5 = WaitInputObjectsGone(t, output: false, out string trace5, out var holders5);
        Check($"U: after its dispose the program holds no object of index {t}", !holders5.Contains(a.Pid), $"program {a.Pid}");
        Check($"U: before the next create no object of index {t}'s input mapping exists", clear5, trace5);
        string f5;
        using (LockedEvent($@"Global\HIDMaestroInputEvent{t}", t))
            f5 = a.Send("create pad F1B0 -");
        bool denied5 = !Ok(f5) && PeerProcess.Field(f5, "win32") == ErrorAccessDenied;
        Check($"U: a create on index {t} is refused at its input event", denied5, f5);
        Check("U: the program still runs after it", Ok(a.Send("alive")));
        var left5 = InputObjectsPresent(t, output: false);
        Check($"U: after it, no object of index {t}'s input mapping is open", denied5 && clear5 && left5.Count == 0,
              denied5 ? string.Join(",", left5) : "the create did not fail at the input event");
        UnclaimedAndUnrecorded("U: after it", baseline, controllers, entriesChecked: true);
        string r5 = a.Send("create pad F1B0 -");
        Check($"U: a retry in the failed program takes transport index {t}",
              Ok(r5) && PeerProcess.Field(r5, "transport") == t, r5);
        a.Send("dispose");
        WaitFor(() => Native.HidPaths(0xBEEF, UPid).Count == 0, 10_000);

        // A composite's device emulator maps the index's sections, which
        // holds the index's v1.12.0 owner pipe, then creates its output
        // doorbell. The same kind of locked event under the doorbell's name
        // fails that step.
        var before = CompositePaths();
        string c4 = a.Send("create composite - -");
        if (!Ok(c4)) { Check("U: the program creates a composite", false, c4); return; }
        string tc = PeerProcess.Field(c4, "transport")!;
        a.Send("dispose");
        WaitFor(() => CompositePaths().Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 15_000);
        bool clear4 = WaitInputObjectsGone(tc, output: true, out string trace4, out var holders4);
        Check($"U: after the composite's dispose the program holds no object of index {tc}", !holders4.Contains(a.Pid), $"program {a.Pid}");
        Check($"U: before the next composite no section or event of index {tc} exists", clear4, trace4);
        string f4;
        using (LockedEvent($@"Global\HIDMaestroOutputEvent{tc}", tc))
            f4 = a.Send("create composite - -");
        bool denied4 = !Ok(f4) && PeerProcess.Field(f4, "win32") == ErrorAccessDenied;
        Check($"U: a composite on index {tc} is refused at its output doorbell", denied4, f4);
        Check("U: the program still runs after it", Ok(a.Send("alive")));
        var left4 = InputObjectsPresent(tc, output: true);
        Check($"U: after it, no section or event of index {tc} is open", denied4 && clear4 && left4.Count == 0,
              denied4 ? string.Join(",", left4) : "the create did not fail at the output doorbell");
        UnclaimedAndUnrecorded("U: after the failed composite", baseline, controllers, entriesChecked: true);
        using (var b = PeerProcess.Start("U4"))
        {
            string cb = b.Send("create composite - -");
            Check($"U: another program creates a composite on transport index {tc} at once",
                  Ok(cb) && PeerProcess.Field(cb, "transport") == tc, cb);
            b.Send("dispose");
        }
        WaitFor(() => CompositePaths().Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 15_000);
        string r4 = a.Send("create composite - -");
        Check($"U: a retry in the failed program takes transport index {tc} and identity index:{tc}",
              Ok(r4) && PeerProcess.Field(r4, "transport") == tc && PeerProcess.Field(r4, "key") == "index:" + tc, r4);
        a.Send("dispose");
        WaitFor(() => CompositePaths().Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 15_000);

        // The same emulator fails after its input pump started. The pump
        // reads the input section, and the failed create unmaps it.
        Check("U: the next composite's emulator is set to fail after its pump starts", Ok(a.Send("failpump")));
        string f6 = a.Send("create composite - -");
        Check("U: a composite whose emulator fails after its pump starts is refused",
              !Ok(f6) && f6.Contains("failpump:", StringComparison.Ordinal), f6);
        // Every composite of this program is disposed, so any input pump
        // left is the failed one's, whatever index it took.
        string pumps = a.Send("threads HMUsbipInput_");
        Check("U: no input pump of it is left running", PeerProcess.Field(pumps, "n") == "0", pumps);
        Thread.Sleep(1500);   // three of the pump's 500 ms waits
        string alive = a.Send("alive");
        Check("U: the program still runs after it", Ok(alive), alive);
        if (Ok(alive))
        {
            UnclaimedAndUnrecorded("U: after it", baseline, controllers, entriesChecked: true);
            string r6 = a.Send("create composite - -");
            Check($"U: a retry in the failed program takes transport index {tc}",
                  Ok(r6) && PeerProcess.Field(r6, "transport") == tc, r6);
            a.Send("dispose");
            WaitFor(() => CompositePaths().Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 15_000);
            // The retry can map its sections where the failed one did, and
            // its dispose unmaps them again. A pump left running wakes from
            // its wait for a frame within 500 ms (UsbipEmulatedDevice.InputPump)
            // and reads the unmapped section. One wait and half again.
            Thread.Sleep(750);
            string alive2 = a.Send("alive");
            Check("U: and still runs after the retry's dispose", Ok(alive2), alive2);
        }

        // A program of its own, after this one exits, so nothing this one
        // holds moves the next create off the index.
        a.Dispose();
        using var r = PeerProcess.Start("U6");
        SectionULegacyRace(r, t, baseline);
    }

    /// <summary>A v1.12.0 program takes no claim. It starts on the index a
    /// create holds, between the create's lease and its input mapping, and
    /// the create is refused there. The refusal removes nothing of the
    /// v1.12.0 program's.</summary>
    static void SectionULegacyRace(PeerProcess a, string t, HashSet<string> baseline)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", "v1.12.0", "CoexistPeer.exe")))
        {
            Check("U: the v1.12.0 peer is built", false, "build CoexistCheck.csproj");
            return;
        }
        string config = $@"SOFTWARE\HIDMaestro\Controller{t}";
        // The acknowledgement checks below need the v1.12.0 controller on a
        // driver: only a driver writes that section. Without the staging's
        // catalogs its create installs none (LegacyStagingCataloged).
        LegacyStagingCataloged("U");
        Check("U: the next create is set to wait before its input mapping", Ok(a.Send("pausemapping")));
        a.Post("create pad F1B0 -");
        string paused = a.Receive("create pad F1B0 - (waiting)", 60_000);
        if (paused != "OK paused")
        {
            Check("U: the create waits before its input mapping", false, paused);
            return;
        }
        // An Xbox 360 controller: its XUSB companion is one of the devices
        // an index-wide teardown removes by ControllerIndex.
        using var old = PeerProcess.Start("U5-v1.12.0", "v1.12.0");
        string co = old.Send("create xbox - -");
        Check($"U: the v1.12.0 program creates on transport index {t} meanwhile",
              Ok(co) && PeerProcess.Field(co, "index") == t, co);
        var oldDevices = IndexDevices(int.Parse(t)).Where(IndexWideTeardownRemoves).ToList();
        string? oldMain = PeerProcess.Field(co, "instance");
        if (oldMain != null && oldMain != "none") oldDevices.Add(oldMain);
        bool configThere = Registry.LocalMachine.OpenSubKey(config) is { } ck && Close(ck);
        Check("U: its devices are present and its configuration written", oldDevices.Count >= 2 && configThere,
              $"{string.Join(",", oldDevices)}, {config} {configThere}");
        Check("U: its controller runs on a driver", oldMain is not (null or "none") && Native.DriverVersionOf(oldMain) != null && Native.IsStarted(oldMain),
              oldMain is null or "none" ? "no instance" : $"{Native.DriverVersionOf(oldMain) ?? "no driver"}, {(Native.IsStarted(oldMain) ? "started" : "not started")}");

        // The v1.12.0 controller's input frame and acknowledgement, at rest
        // after a burst of input. The refused create must write neither.
        string inputName = $@"Global\HIDMaestroInput{t}", ackName = $@"Global\HIDMaestroInputAck{t}";
        // The driver stores a frame's sequence number in the section's first
        // word once HID class has taken it, at the read after the one that
        // carried it (driver.c NoteFrameCompletedLocked), so a delivered
        // frame needs a reader. Nothing else reads the device in a sandbox,
        // so the probe reads its HID interface through the burst, as a
        // program that uses the controller would.
        string? oldHid = oldMain is null or "none" ? null
            : Native.Subtree(oldMain).Skip(1).SelectMany(c => Native.InterfaceList(Native.HidGuid, c)).FirstOrDefault();
        byte[]? inBefore, ackBefore;
        using (var oldCap = oldHid == null ? null : HidCapture.Open(oldHid))
        {
            long mark = oldCap?.Mark() ?? 0;
            old.Send("stream 0 0.7 60 0");
            Thread.Sleep(500);
            old.Send("stopstream");
            Thread.Sleep(300);
            int reports = oldCap?.Since(mark).Count ?? 0;
            Check("U: the probe reads the v1.12.0 controller's HID interface through the burst", reports > 0,
                  oldHid == null ? "no HID interface" : oldCap == null ? $"{oldHid} did not open" : $"{reports} reports");
            inBefore = Native.SectionBytes(inputName, InputSectionSize);
            ackBefore = Native.SectionBytes(ackName, AckSectionSize);
        }
        Check("U: the v1.12.0 controller's input section carries its frame",
              inBefore != null && inBefore.Any(x => x != 0), inBefore == null ? "absent" : inBefore.Any(x => x != 0) ? "" : "all zero");
        int delivered = ackBefore == null ? 0 : BitConverter.ToInt32(ackBefore, 0);
        int caps = ackBefore == null ? 0 : BitConverter.ToInt32(ackBefore, 4);
        Check("U: and its acknowledgement section carries a delivered frame",
              ackBefore != null && delivered != 0,
              ackBefore == null ? "absent" : $"DeliveredSeqNo {delivered}, Capabilities 0x{caps:X8}");

        string f7 = a.Send("go");
        Check("U: the waiting create is refused as in use",
              !Ok(f7) && f7.Contains("HMResourceInUseException", StringComparison.Ordinal), f7);
        Thread.Sleep(2000);
        byte[]? inAfter = Native.SectionBytes(inputName, InputSectionSize);
        byte[]? ackAfter = Native.SectionBytes(ackName, AckSectionSize);
        Check($"U: the refused create left {inputName} as it was",
              inBefore != null && inAfter != null && inBefore.SequenceEqual(inAfter), FirstDifference(inBefore, inAfter));
        Check($"U: and {ackName}",
              ackBefore != null && ackAfter != null && ackBefore.Any(x => x != 0) && ackBefore.SequenceEqual(ackAfter),
              FirstDifference(ackBefore, ackAfter) + (ackBefore != null && ackBefore.All(x => x == 0) ? ", all zero before" : ""));
        var gone = oldDevices.Where(d => !Native.IsPresent(d)).ToList();
        Check("U: the v1.12.0 program's devices are all still present", gone.Count == 0,
              gone.Count == 0 ? "" : "gone: " + string.Join(",", gone));
        Check($"U: and its configuration {config} is still there",
              Registry.LocalMachine.OpenSubKey(config) is { } k2 && Close(k2));
        Check("U: the refused program made no device", Native.HidPaths(0xBEEF, UPid).Count == 0,
              $"{Native.HidPaths(0xBEEF, UPid).Count} present");
        // The v1.12.0 program holds its index's owner pipe.
        var withOld = new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase) { $"HIDMaestroOwner{t}" };
        string? ownerId = Native.OwnerRecords().Where(o => o.Value == a.Pid).Select(o => o.Key).FirstOrDefault();
        UnclaimedAndUnrecorded("U: after it", withOld, $@"SOFTWARE\HIDMaestroOwners\{ownerId}\Controllers", entriesChecked: ownerId != null);
        Check("U: the v1.12.0 program's controller still takes its dispose", Ok(old.Send("dispose")));
    }

    /// <summary>The present HIDMaestro devices whose Device Parameters carry
    /// ControllerIndex <paramref name="index"/>: under ROOT and SWD, every
    /// instance of the HIDMAESTRO enumerator and every instance named HM_
    /// (DeviceIdentity's token) of any other, such as ROOT\HIDClass. A key
    /// that cannot be read is named in <paramref name="unreadable"/>.</summary>
    static List<string> IndexDevices(int index, List<string>? unreadable = null)
    {
        var list = new List<string>();
        foreach (var root in new[] { "SWD", "ROOT" })
        {
            try
            {
                using var e = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{root}");
                if (e == null) continue;
                foreach (var enumerator in e.GetSubKeyNames())
                {
                    bool ours = enumerator.Equals("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);
                    try
                    {
                        using var ek = e.OpenSubKey(enumerator);
                        if (ek == null) continue;
                        foreach (var inst in ek.GetSubKeyNames())
                        {
                            if (!ours && !inst.StartsWith("HM_", StringComparison.OrdinalIgnoreCase)) continue;
                            using var dp = ek.OpenSubKey($@"{inst}\Device Parameters");
                            string id = $@"{root}\{enumerator}\{inst}";
                            if (dp?.GetValue("ControllerIndex") is int ci && ci == index && Native.IsPresent(id))
                                list.Add(id);
                        }
                    }
                    catch (Exception x) when (x is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                    {
                        unreadable?.Add($@"{root}\{enumerator}");
                    }
                }
            }
            catch (Exception x) when (x is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                unreadable?.Add(root);
            }
        }
        return list;
    }

    /// <summary>The devices an index-wide teardown removes by
    /// ControllerIndex: the SWD\HIDMAESTRO and ROOT\HIDMAESTRO ones.</summary>
    static bool IndexWideTeardownRemoves(string instanceId) =>
        instanceId.StartsWith(@"SWD\HIDMAESTRO\", StringComparison.OrdinalIgnoreCase)
        || instanceId.StartsWith(@"ROOT\HIDMAESTRO\", StringComparison.OrdinalIgnoreCase);

    /// <summary>An event owned by Administrators that grants everyone
    /// READ_CONTROL and nothing else. The transport allocator reads its
    /// owner and keeps the index. A create, which opens it to signal and
    /// wait, is refused access. Closed on dispose. When the name exists
    /// already, the exception names its owner, its holders and the present
    /// devices on <paramref name="index"/>.</summary>
    static IDisposable LockedEvent(string name, string index)
    {
        IntPtr sa = Native.Sa("O:BAD:P(A;;0x00020000;;;WD)");
        IntPtr h;
        int error;
        try
        {
            h = Native.CreateEventW(sa, false, false, name);
            error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        }
        finally { Native.FreeSa(sa); }
        if (h == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(error, $"could not create {name}");
        // ERROR_ALREADY_EXISTS: the handle is to an event someone else made,
        // which this descriptor did not lock.
        if (error == 183)
        {
            Native.CloseHandle(h);
            throw new InvalidOperationException($"{name} already exists, so the probe cannot lock it. "
                + ObjectHolders.Describe(name, section: false).Text + ". " + DevicesText(index));
        }
        return new Handle(h);
    }

    /// <summary>The objects of an index's input mapping that exist, and
    /// with <paramref name="output"/> its output section and doorbell: what
    /// a failed create must close (SharedMemoryIO.ReleaseInputExtrasLocked
    /// and DestroyController). The input event and the doorbell are the
    /// names <see cref="LockedEvent"/> takes, so a check that waits on this
    /// list before a lock waits on the name it locks. The owner pipe is in
    /// <see cref="ClaimPipes"/>.</summary>
    static List<string> InputObjectsPresent(string index, bool output)
    {
        var present = new List<string>();
        var sections = new List<string> { "Input", "InputAck", "Version" };
        if (output) sections.Add("Output");
        foreach (var k in sections)
            if (Native.SectionExists($@"Global\HIDMaestro{k}{index}")) present.Add($"HIDMaestro{k}{index}");
        var events = new List<string> { "InputEvent", "CompanionInputEvent", "InputAckEvent" };
        if (output) events.Add("OutputEvent");
        foreach (var k in events)
            if (Native.OwnerOf($@"Global\HIDMaestro{k}{index}", section: false) != "absent") present.Add($"HIDMaestro{k}{index}");
        return present;
    }

    /// <summary>Wait up to 10 s for every object
    /// <see cref="InputObjectsPresent"/> lists to go. The first look is the
    /// list alone. One scan then reads the owner and holders of everything
    /// it found (ObjectHolders.DescribeAll) and closes its handles before
    /// the poll starts, so no handle of the probe keeps a name alive while
    /// the poll times it. The poll reads the list alone. <paramref name="trace"/>
    /// says what was there at the first look, its owners and holders and
    /// how long their scan took, when each object went, came back or first
    /// appeared, who holds any still there after 10 s, and the present
    /// devices on the index, so a check that finds one left says what kept
    /// it. <paramref name="firstHolders"/> is every process the scan found
    /// holding one of them.</summary>
    static bool WaitInputObjectsGone(string index, bool output, out string trace, out HashSet<int> firstHolders)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var first = InputObjectsPresent(index, output);
        var reports = ObjectHolders.DescribeAll(first.Select(o => ($@"Global\{o}", IsSection(o))).ToList());
        long scanned = clock.ElapsedMilliseconds;
        firstHolders = reports.SelectMany(r => r.Holders).Select(h => h.Pid).ToHashSet();
        string devices = first.Count == 0 ? "" : DevicesText(index);
        var changes = new List<string>();
        var previous = new HashSet<string>(first);
        bool clear = WaitFor(() =>
        {
            var now = new HashSet<string>(InputObjectsPresent(index, output));
            foreach (var o in previous.Except(now)) changes.Add($"{o} gone at {clock.ElapsedMilliseconds} ms");
            foreach (var o in now.Except(previous))
                changes.Add($"{o} {(first.Contains(o) ? "back" : "appeared")} at {clock.ElapsedMilliseconds} ms");
            previous = now;
            return now.Count == 0;
        }, 10_000);
        var parts = new List<string>
        {
            first.Count == 0
                ? "nothing at the first look"
                : $"at the first look: {string.Join(" | ", reports.Select(r => r.Text))} (read in {scanned} ms)",
        };
        if (devices.Length > 0) parts.Add(devices);
        if (changes.Count > 0) parts.Add(string.Join(" | ", changes.Take(12)));
        if (!clear)
        {
            long at = clock.ElapsedMilliseconds;
            var left = ObjectHolders.DescribeAll(InputObjectsPresent(index, output).Select(o => ($@"Global\{o}", IsSection(o))).ToList());
            parts.Add($"after {at} ms: " + string.Join(" | ", left.Select(r => r.Text)));
            parts.Add(DevicesText(index));
        }
        trace = string.Join(". ", parts);
        return clear;
    }

    static bool IsSection(string objectName) => !objectName.Contains("Event", StringComparison.Ordinal);

    /// <summary>The present devices on an index with each one's status and
    /// problem code, and any key IndexDevices could not read.</summary>
    static string DevicesText(string index)
    {
        var unreadable = new List<string>();
        var devices = IndexDevices(int.Parse(index), unreadable).Select(id =>
        {
            if (Native.CM_Locate_DevNodeW(out uint devInst, id, 0) != 0) return $"{id} gone";
            Native.CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0);
            return $"{id} status 0x{status:X} problem {problem}";
        }).ToList();
        return $"devices on index {index}: " + (devices.Count == 0 ? "none" : string.Join(", ", devices))
            + (unreadable.Count == 0 ? "" : ", unreadable: " + string.Join(", ", unreadable));
    }

    // ERROR_ACCESS_DENIED, as the peer's reply carries it.
    const string ErrorAccessDenied = "5";

    sealed class Handle(IntPtr h) : IDisposable
    {
        public void Dispose() => Native.CloseHandle(h);
    }

    // HIDMAESTRO_SHARED_INPUT and the acknowledgement section (driver.h):
    // 362 bytes, and 16 bytes of header with eight 84-byte release slots.
    const int InputSectionSize = 362, AckSectionSize = 16 + 8 * 84;

    static string FirstDifference(byte[]? before, byte[]? after)
    {
        if (before == null || after == null) return $"before {(before == null ? "absent" : "read")}, after {(after == null ? "absent" : "read")}";
        for (int i = 0; i < before.Length; i++)
            if (before[i] != after[i]) return $"byte {i}: 0x{before[i]:X2} then 0x{after[i]:X2}";
        return "";
    }

    static bool Close(RegistryKey k)
    {
        k.Dispose();
        return true;
    }

    /// <summary>No claim pipe beyond the baseline, and with
    /// <paramref name="entriesChecked"/> no controller entry.</summary>
    static void UnclaimedAndUnrecorded(string label, HashSet<string> baseline, string controllers, bool entriesChecked)
    {
        var left = ClaimPipes().Except(baseline, StringComparer.OrdinalIgnoreCase).ToList();
        Check($"{label}, no index or identity claim is held", left.Count == 0, string.Join(",", left));
        if (entriesChecked)
        {
            var entries = Entries(controllers);
            Check($"{label}, the record has no controller entry", entries.Count == 0, string.Join(",", entries));
        }
    }

    /// <summary>The transport index and identity claim pipes on the machine,
    /// and the v1.12.0 owner pipe a mapped index holds.</summary>
    static HashSet<string> ClaimPipes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(@"\\.\pipe\"))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("HIDMaestro.Index.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("HIDMaestro.Identity.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("HIDMaestroOwner", StringComparison.OrdinalIgnoreCase))
                set.Add(name);
        }
        return set;
    }

    static List<string> Entries(string controllers)
    {
        using var cs = Registry.LocalMachine.OpenSubKey(controllers);
        return cs?.GetSubKeyNames().ToList() ?? new List<string>();
    }

    /// <summary>A deny for everyone, on the key itself or, with
    /// <paramref name="children"/>, on the subkeys created under it.</summary>
    static RegistryAccessRule DenyRule(RegistryRights rights, bool children) =>
        new(new SecurityIdentifier(WellKnownSidType.WorldSid, null), rights,
            children ? InheritanceFlags.ContainerInherit : InheritanceFlags.None,
            children ? PropagationFlags.InheritOnly : PropagationFlags.None,
            AccessControlType.Deny);

    static void SetDeny(string path, RegistryAccessRule rule, bool on)
    {
        using var k = Registry.LocalMachine.OpenSubKey(path, RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ReadPermissions | RegistryRights.ChangePermissions)
            ?? throw new InvalidOperationException($"no key {path}");
        RegistrySecurity s = k.GetAccessControl();
        if (on) s.AddAccessRule(rule);
        else s.RemoveAccessRuleSpecific(rule);
        k.SetAccessControl(s);
    }
}
