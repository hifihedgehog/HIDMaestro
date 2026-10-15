// U. A create that fails after it claimed a transport index and an
//    identity. The program's record is made to refuse the write the create
//    needs, once before any device exists and once after the device is
//    made, and once over an entry it cannot write that is already there.
//    A composite's device emulator fails after it mapped the index's
//    sections. Each time the create leaves no claim pipe, no device and no
//    record entry, and the same identity creates again at once, in the same
//    program and in another.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
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

        // A composite's device emulator maps the index's sections, which
        // holds the index's v1.12.0 owner pipe, then creates its output
        // doorbell. A mutex squatting the doorbell's name fails that step.
        var before = Native.HidPaths(0x054C, 0x0CE6);
        string c4 = a.Send("create composite - -");
        if (!Ok(c4)) { Check("U: the program creates a composite", false, c4); return; }
        string tc = PeerProcess.Field(c4, "transport")!;
        a.Send("dispose");
        WaitFor(() => Native.HidPaths(0x054C, 0x0CE6).Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 15_000);
        string f4;
        using (var squat = new System.Threading.Mutex(false, $@"Global\HIDMaestroOutputEvent{tc}"))
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
