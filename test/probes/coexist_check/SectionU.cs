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
//    same program and in another. Last, a v1.12.0
//    program starts an Xbox 360 controller on the index while a create waits
//    before its mapping: the create is refused, and the v1.12.0 program's
//    devices and configuration stay.
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
        Check("U: and one device of it is present", WaitPaths(0xBEEF, UPid, 1, 5000).Count == 1);
        a.Send("dispose");
        Check("U: its dispose leaves no entry", WaitFor(() => Entries(controllers).Count == 0, 5000),
              string.Join(",", Entries(controllers)));
        WaitFor(() => Native.HidPaths(0xBEEF, UPid).Count == 0, 10_000);

        // The input mapping's last step creates the input event. An event
        // of that name owned by Administrators, as HIDMaestro's own are, so
        // the transport allocator keeps the index, but open to nobody for
        // signaling fails that step after the input section exists.
        string section = $@"Global\HIDMaestroInput{t}";
        bool sectionGone = WaitFor(() => !Native.SectionExists(section), 10_000);
        Check($"U: before the next create nothing holds {section}", sectionGone);
        string f5;
        using (LockedEvent($@"Global\HIDMaestroInputEvent{t}"))
            f5 = a.Send("create pad F1B0 -");
        Check("U: a create whose input event cannot be made fails", !Ok(f5), f5);
        if (sectionGone)
            Check($"U: after it, no handle to {section} is open", !Native.SectionExists(section));
        UnclaimedAndUnrecorded("U: after it", baseline, controllers, entriesChecked: true);
        string r5 = a.Send("create pad F1B0 -");
        Check($"U: a retry in the failed program takes transport index {t}",
              Ok(r5) && PeerProcess.Field(r5, "transport") == t, r5);
        a.Send("dispose");
        WaitFor(() => Native.HidPaths(0xBEEF, UPid).Count == 0, 10_000);

        // A composite's device emulator maps the index's sections, which
        // holds the index's v1.12.0 owner pipe, then creates its output
        // doorbell. The same locked event under the doorbell's name fails
        // that step.
        var before = Native.HidPaths(0x054C, 0x0CE6);
        string c4 = a.Send("create composite - -");
        if (!Ok(c4)) { Check("U: the program creates a composite", false, c4); return; }
        string tc = PeerProcess.Field(c4, "transport")!;
        a.Send("dispose");
        WaitFor(() => Native.HidPaths(0x054C, 0x0CE6).Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 15_000);
        string f4;
        using (LockedEvent($@"Global\HIDMaestroOutputEvent{tc}"))
            f4 = a.Send("create composite - -");
        Check("U: a composite whose device emulator fails is refused", !Ok(f4), f4);
        UnclaimedAndUnrecorded("U: after the failed composite", baseline, controllers, entriesChecked: true);
        using (var b = PeerProcess.Start("U4"))
        {
            string cb = b.Send("create composite - -");
            Check($"U: another program creates a composite on transport index {tc} at once",
                  Ok(cb) && PeerProcess.Field(cb, "transport") == tc, cb);
            b.Send("dispose");
        }
        WaitFor(() => Native.HidPaths(0x054C, 0x0CE6).Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 15_000);
        string r4 = a.Send("create composite - -");
        Check($"U: a retry in the failed program takes transport index {tc} and identity index:{tc}",
              Ok(r4) && PeerProcess.Field(r4, "transport") == tc && PeerProcess.Field(r4, "key") == "index:" + tc, r4);
        a.Send("dispose");
        WaitFor(() => Native.HidPaths(0x054C, 0x0CE6).Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 15_000);

        // The same emulator fails after its input pump started. The pump
        // reads the input section, and the failed create unmaps it.
        Check("U: the next composite's emulator is set to fail after its pump starts", Ok(a.Send("failpump")));
        string f6 = a.Send("create composite - -");
        Check("U: a composite whose emulator fails after its pump starts is refused", !Ok(f6), f6);
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
            WaitFor(() => Native.HidPaths(0x054C, 0x0CE6).Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 15_000);
            // The retry can map its sections where the failed one did, and
            // its dispose unmaps them again.
            Thread.Sleep(1500);
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
        var oldDevices = IndexDevices(int.Parse(t));
        string? oldMain = PeerProcess.Field(co, "instance");
        if (oldMain != null && oldMain != "none") oldDevices.Add(oldMain);
        bool configThere = Registry.LocalMachine.OpenSubKey(config) is { } ck && Close(ck);
        Check("U: its devices are present and its configuration written", oldDevices.Count >= 2 && configThere,
              $"{string.Join(",", oldDevices)}, {config} {configThere}");

        // The v1.12.0 controller's input frame and acknowledgement, at rest
        // after a burst of input. The refused create must write neither.
        string inputName = $@"Global\HIDMaestroInput{t}", ackName = $@"Global\HIDMaestroInputAck{t}";
        old.Send("stream 0 0.7 60 0");
        Thread.Sleep(500);
        old.Send("stopstream");
        Thread.Sleep(300);
        byte[]? inBefore = Native.SectionBytes(inputName, InputSectionSize);
        byte[]? ackBefore = Native.SectionBytes(ackName, AckSectionSize);
        Check("U: the v1.12.0 controller's input section carries its frame",
              inBefore != null && inBefore.Any(x => x != 0), inBefore == null ? "absent" : inBefore.Any(x => x != 0) ? "" : "all zero");

        string f7 = a.Send("go", 120_000);
        Check("U: the waiting create is refused as in use",
              !Ok(f7) && f7.Contains("HMResourceInUseException", StringComparison.Ordinal), f7);
        Thread.Sleep(2000);
        byte[]? inAfter = Native.SectionBytes(inputName, InputSectionSize);
        byte[]? ackAfter = Native.SectionBytes(ackName, AckSectionSize);
        Check($"U: the refused create left {inputName} as it was",
              inBefore != null && inAfter != null && inBefore.SequenceEqual(inAfter), FirstDifference(inBefore, inAfter));
        Check($"U: and {ackName}",
              ackBefore != null && ackAfter != null && ackBefore.SequenceEqual(ackAfter),
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

    /// <summary>The present devices an index-wide teardown removes by
    /// ControllerIndex: SWD\HIDMAESTRO and ROOT\HIDMAESTRO instances.</summary>
    static List<string> IndexDevices(int index)
    {
        var list = new List<string>();
        foreach (var root in new[] { "SWD", "ROOT" })
        {
            using var e = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{root}\HIDMAESTRO");
            if (e == null) continue;
            foreach (var inst in e.GetSubKeyNames())
            {
                using var dp = e.OpenSubKey($@"{inst}\Device Parameters");
                string id = $@"{root}\HIDMAESTRO\{inst}";
                if (dp?.GetValue("ControllerIndex") is int ci && ci == index && Native.IsPresent(id))
                    list.Add(id);
            }
        }
        return list;
    }

    /// <summary>An event owned by Administrators that grants everyone
    /// READ_CONTROL and nothing else. The transport allocator reads its
    /// owner and keeps the index. A create, which opens it to signal and
    /// wait, is refused access. Closed on dispose.</summary>
    static IDisposable LockedEvent(string name)
    {
        IntPtr sa = Native.Sa("O:BAD:P(A;;0x00020000;;;WD)");
        IntPtr h;
        try { h = Native.CreateEventW(sa, false, false, name); }
        finally { Native.FreeSa(sa); }
        if (h == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), $"could not create {name}");
        return new Handle(h);
    }

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
