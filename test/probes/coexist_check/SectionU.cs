// U. A create that fails after it claimed a transport index and an
//    identity. The program's record is made to refuse the controller entry
//    the create writes before any device exists, for a keyless create and
//    a keyed one. Each time the create leaves no claim pipe and no record
//    entry, and the same identity creates again at once, in the same
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

    /// <summary>The transport index and identity claim pipes on the machine.</summary>
    static HashSet<string> ClaimPipes()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(@"\\.\pipe\"))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith("HIDMaestro.Index.", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("HIDMaestro.Identity.", StringComparison.OrdinalIgnoreCase))
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
