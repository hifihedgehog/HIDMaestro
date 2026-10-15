// Another OpenVR driver registered under HIDMaestro's driver name, and
// HIDMaestro's own registration in the OpenVR path registry.
//
// OpenVR requires a driver's manifest name to be unique. A derivative that
// keeps "hidmaestro" competes with HIDMaestro's own driver for the same
// name, resource prefix and named objects. HMVR.FindDriverNameConflicts
// reads the user's OpenVR path registry and SteamVR's drivers folder and
// lists every such folder other than HIDMaestro's own.
//
// HIDMaestro registers its driver by writing the path registry itself, the
// file SteamVR's vrpathreg.exe edits. Through v1.12.0 it ran vrpathreg.exe
// from the SteamVR folder, elevated, and any user can write that folder.
// vrpathreg_golden.json holds, for each case, a starting file and the bytes
// the shipped vrpathreg.exe (SteamVR 2.16, file version 1.1.1) wrote for
// "vrpathreg adddriver|removedriver <path>" over it, run against a scratch
// VR_PATHREG_OVERRIDE. The SDK must write the same bytes.
//
// The probe points VR_PATHREG_OVERRIDE (honored by OpenVR's
// GetVRPathRegistryFilename) at registry files of its own, so the real one
// is never read or written. Sections 1 to 3 need no elevation and no
// SteamVR. Section 4 runs when the probe is elevated: it points the SDK's
// SteamVR discovery at a folder whose vrpathreg.exe is not a program and
// registers, then, for the elevated half of a split token, checks the write
// carries the user's rights and no more. It restores the two HKLM values it
// changes. Exit 0 PASS / 1 FAIL.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using HIDMaestro;
using Microsoft.Win32;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static string Driver(string root, string folder, string name)
    {
        string dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "driver.vrdrivermanifest"),
            "{\n\t\"alwaysActivate\": true,\n\t\"name\" : \"" + name + "\",\n\t\"directory\" : \"\"\n}\n");
        return dir;
    }

    static int Main()
    {
        // A loader failure in anything started from here returns an error
        // instead of showing a dialog.
        SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);

        string root = Path.Combine(Path.GetTempPath(), "hm-vr-conflict-" + Guid.NewGuid().ToString("N"));
        string? saved = Environment.GetEnvironmentVariable("VR_PATHREG_OVERRIDE");
        try
        {
            NameConflicts(root);
            Golden();
            FileRoundTrip(root);
            Elevated(root);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VR_PATHREG_OVERRIDE", saved);
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        var live = HMVR.FindDriverNameConflicts();
        Console.WriteLine($"  this machine's own registry: {(live.Count == 0 ? "no conflict" : string.Join(", ", live))}");

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    // ── 1. Name conflicts ───────────────────────────────────────────────

    static void NameConflicts(string root)
    {
        Console.WriteLine("=== OpenVR driver name conflicts ===");
        Check("HIDMaestro's driver is named hidmaestro", HMVR.DriverName == "hidmaestro", HMVR.DriverName);

        string dir = Path.Combine(root, "conflicts");
        string foreign = Driver(dir, "hidmaestro", "hidmaestro");
        string other = Driver(dir, "otherdriver", "otherdriver");
        string upper = Driver(dir, "kitsune", "HIDMaestro");
        string ours = ExtractRoot;
        string registry = Path.Combine(dir, "openvrpaths.vrpath");
        string Esc(string p) => p.Replace("\\", "\\\\");
        File.WriteAllText(registry,
            "{\n\t\"external_drivers\" : \n\t[\n" +
            $"\t\t\"{Esc(foreign)}\",\n\t\t\"{Esc(other)}\",\n\t\t\"{Esc(upper)}\",\n\t\t\"{Esc(ours)}\",\n\t\t\"{Esc(Path.Combine(dir, "missing"))}\"\n" +
            "\t],\n\t\"jsonid\" : \"vrpathreg\",\n\t\"version\" : 1\n}\n");
        Environment.SetEnvironmentVariable("VR_PATHREG_OVERRIDE", registry);

        var found = HMVR.FindDriverNameConflicts();
        Console.WriteLine($"  found: {(found.Count == 0 ? "(none)" : string.Join(", ", found))}");
        Check("a foreign folder named hidmaestro is reported", found.Any(f => f.Equals(foreign, StringComparison.OrdinalIgnoreCase)));
        Check("so is one whose manifest says HIDMaestro in another case", found.Any(f => f.Equals(upper, StringComparison.OrdinalIgnoreCase)));
        Check("a driver with another name is not", !found.Any(f => f.Equals(other, StringComparison.OrdinalIgnoreCase)));
        Check("HIDMaestro's own folder is not", !found.Any(f => f.Equals(ours, StringComparison.OrdinalIgnoreCase)));
        Check("a registered folder that no longer exists is skipped", found.Count == 2, $"{found.Count} reported");

        File.WriteAllText(registry, "{\n\t\"external_drivers\" : [],\n\t\"version\" : 1\n}\n");
        Check("an empty registry reports nothing beyond SteamVR's own drivers folder",
              HMVR.FindDriverNameConflicts().All(f => !f.StartsWith(root, StringComparison.OrdinalIgnoreCase)));

        File.WriteAllText(registry, "not json");
        Check("an unreadable registry reports nothing and does not throw",
              HMVR.FindDriverNameConflicts().All(f => !f.StartsWith(root, StringComparison.OrdinalIgnoreCase)));

        // SteamVR reads the list with jsoncpp, which rejects a trailing
        // comma. A driver listed only in such a file is never loaded.
        File.WriteAllText(registry, "{\"external_drivers\":[\"" + Esc(foreign) + "\",]}");
        Check("a list SteamVR cannot parse reports nothing",
              HMVR.FindDriverNameConflicts().All(f => !f.StartsWith(root, StringComparison.OrdinalIgnoreCase)));
    }

    // ── 2. The bytes vrpathreg writes ───────────────────────────────────

    sealed record Case(string Name, string Verb, string Path, byte[]? Seed, byte[] Expect);

    static List<Case> LoadGolden()
    {
        string file = Path.Combine(AppContext.BaseDirectory, "vrpathreg_golden.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var list = new List<Case>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string? seed = e.GetProperty("seed").GetString();
            list.Add(new Case(e.GetProperty("name").GetString()!, e.GetProperty("verb").GetString()!,
                              e.GetProperty("path").GetString()!,
                              seed == null ? null : Convert.FromBase64String(seed),
                              Convert.FromBase64String(e.GetProperty("expect").GetString()!)));
        }
        return list;
    }

    static readonly Type? s_registry =
        typeof(HMVR).Assembly.GetType("HIDMaestro.Internal.OpenVrPathRegistry");

    static MethodInfo? Internal(string name, params Type[] args) =>
        s_registry?.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null, args, null);

    static string ExtractRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HIDMaestro", "openvr", "hidmaestro");

    static byte[]? Apply(byte[]? seed, string verb, string path) =>
        Internal("Apply", typeof(byte[]), typeof(string), typeof(string))?.Invoke(null, new object?[] { seed, verb, path }) as byte[];

    static string FirstDifference(byte[] want, byte[] got)
    {
        int n = Math.Min(want.Length, got.Length), i = 0;
        while (i < n && want[i] == got[i]) i++;
        string Around(byte[] b) => Convert.ToHexString(b, Math.Max(0, i - 8), Math.Min(b.Length, i + 8) - Math.Max(0, i - 8));
        return $"differs at byte {i} of {want.Length}/{got.Length}: want {Around(want)} got {Around(got)}";
    }

    static void Golden()
    {
        Console.WriteLine("\n=== The path registry bytes vrpathreg.exe writes ===");
        var apply = Internal("Apply", typeof(byte[]), typeof(string), typeof(string));
        Check("the SDK writes the path registry itself (OpenVrPathRegistry.Apply exists)", apply != null);
        if (apply == null) return;
        foreach (var c in LoadGolden())
        {
            byte[] got = (byte[])apply.Invoke(null, new object?[] { c.Seed, c.Verb, c.Path })!;
            bool same = got.AsSpan().SequenceEqual(c.Expect);
            Check($"{c.Verb}, {c.Name}", same, same ? "" : FirstDifference(c.Expect, got));
        }
    }

    // ── 3. The file, offline ────────────────────────────────────────────

    static void FileRoundTrip(string root)
    {
        Console.WriteLine("\n=== Writing the user's path registry ===");
        var add = Internal("AddDriver", typeof(string));
        var remove = Internal("RemoveDriver", typeof(string));
        Check("OpenVrPathRegistry.AddDriver and RemoveDriver exist", add != null && remove != null);
        if (add == null || remove == null) return;

        // GetVRPathRegistryFilename returns "" when the per-user folder
        // cannot be resolved, and BSaveToFile then writes nothing. The SDK
        // must not fall back to a path relative to the working directory.
        var resolve = Internal("ResolveFilePath", typeof(string), typeof(string));
        Check("the registry path is resolved by OpenVrPathRegistry.ResolveFilePath", resolve != null);
        if (resolve != null)
        {
            string? R(string? over, string local) => (string?)resolve.Invoke(null, new object?[] { over, local });
            Check("no override and no LocalApplicationData resolves to no file", R(null, "") == null, R(null, "") ?? "null");
            Check("an empty override counts as none", R("", "") == null, R("", "") ?? "null");
            Check("LocalApplicationData gives openvr\\openvrpaths.vrpath under it",
                  R(null, @"C:\Users\x\AppData\Local") == @"C:\Users\x\AppData\Local\openvr\openvrpaths.vrpath");
            Check("an override wins", R(@"D:\o\reg.vrpath", "") == @"D:\o\reg.vrpath");
        }

        string dir = Path.Combine(root, "roundtrip", "openvr");   // not created yet
        string file = Path.Combine(dir, "openvrpaths.vrpath");
        Environment.SetEnvironmentVariable("VR_PATHREG_OVERRIDE", file);
        string path = ExtractRoot;

        bool changed = (bool)add.Invoke(null, new object[] { path })!;
        byte[] after = File.Exists(file) ? File.ReadAllBytes(file) : Array.Empty<byte>();
        Check("adding to a missing file creates its folder and writes vrpathreg's bytes",
              changed && after.AsSpan().SequenceEqual(Apply(null, "adddriver", path)));

        DateTime stamp = File.GetLastWriteTimeUtc(file);
        changed = (bool)add.Invoke(null, new object[] { path })!;
        Check("adding again leaves the file alone", !changed &&
              File.ReadAllBytes(file).AsSpan().SequenceEqual(after) && File.GetLastWriteTimeUtc(file) == stamp);

        byte[] compact = System.Text.Encoding.UTF8.GetBytes(
            "{\"external_drivers\":[\"" + path.Replace("\\", "\\\\") + "\"],\"runtime\":[\"C:\\\\SteamVR\"]}");
        File.WriteAllBytes(file, compact);
        changed = (bool)add.Invoke(null, new object[] { path })!;
        Check("a file that already lists the folder is not rewritten", !changed &&
              File.ReadAllBytes(file).AsSpan().SequenceEqual(compact));

        changed = (bool)remove.Invoke(null, new object[] { path })!;
        Check("removing writes vrpathreg's bytes, other lists kept", changed &&
              File.ReadAllBytes(file).AsSpan().SequenceEqual(Apply(compact, "removedriver", path)));

        changed = (bool)remove.Invoke(null, new object[] { path })!;
        Check("removing an absent folder writes nothing", !changed);

        // One removedriver drops only the first match. Unregistering drops
        // them all: what removedriver leaves when run until nothing changes.
        string esc = path.Replace("\\", "\\\\");
        byte[] twice = System.Text.Encoding.UTF8.GetBytes(
            "{\"external_drivers\":[\"" + esc + "\",\"C:\\\\Drivers\\\\one\",\"" + esc + "\"]}");
        File.WriteAllBytes(file, twice);
        changed = (bool)remove.Invoke(null, new object[] { path })!;
        Check("removing drops every copy", changed &&
              File.ReadAllBytes(file).AsSpan().SequenceEqual(Apply(Apply(twice, "removedriver", path), "removedriver", path)));

        // A failed registration surfaces as InvalidOperationException with
        // the cause inside, as it did when vrpathreg ran. OpenVR's own reader
        // opens the file with _wfsopen(_SH_DENYNO), which does not share
        // delete, so the move over it fails while SteamVR is reading.
        var addCall = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), add);
        File.WriteAllBytes(file, compact);
        var removeCall = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), remove);
        removeCall(path);
        byte[] before = File.ReadAllBytes(file);
        Exception? caught = null;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            try { addCall(path); }
            catch (Exception e) { caught = e; }
        }
        Check("a write that loses to OpenVR's reader throws InvalidOperationException with the cause inside",
              caught is InvalidOperationException { InnerException: IOException or UnauthorizedAccessException },
              caught == null ? "no exception" : caught.GetType().Name + " / " + caught.InnerException?.GetType().Name);
        Check("and the file is as it was", File.ReadAllBytes(file).AsSpan().SequenceEqual(before));

        // A file that exists but cannot be read is left alone. vrpathreg
        // would treat it as empty and write over it, losing the runtime,
        // config and log paths. A deny-read ACE leaves the replace itself
        // allowed, so only the read decides.
        File.WriteAllBytes(file, compact);
        removeCall(path);
        before = File.ReadAllBytes(file);
        var me = WindowsIdentity.GetCurrent().User!;
        var info = new FileInfo(file);
        var denyRead = new FileSystemAccessRule(me, FileSystemRights.ReadData, AccessControlType.Deny);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(denyRead);
        info.SetAccessControl(acl);
        caught = null;
        try { addCall(path); }
        catch (Exception e) { caught = e; }
        finally
        {
            acl = info.GetAccessControl();
            acl.RemoveAccessRuleSpecific(denyRead);
            info.SetAccessControl(acl);
        }
        Check("a registry file that cannot be read is not written over",
              File.ReadAllBytes(file).AsSpan().SequenceEqual(before));
        Check("and the failure is InvalidOperationException with the cause inside",
              caught is InvalidOperationException { InnerException: UnauthorizedAccessException },
              caught == null ? "no exception" : caught.GetType().Name + " / " + caught.InnerException?.GetType().Name);

        // Held open with no sharing at all: neither the read nor the write
        // gets through, and the file is untouched.
        caught = null;
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { addCall(path); }
            catch (Exception e) { caught = e; }
        }
        Check("a file held open exclusively is left alone, with InvalidOperationException",
              caught is InvalidOperationException && File.ReadAllBytes(file).AsSpan().SequenceEqual(before),
              caught == null ? "no exception" : caught.GetType().Name + " / " + caught.InnerException?.GetType().Name);

        Check("no temporary file is left beside it",
              Directory.GetFiles(dir).Select(Path.GetFileName).SequenceEqual(new[] { "openvrpaths.vrpath" }),
              string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));
    }

    // ── 4. Elevated ─────────────────────────────────────────────────────

    static void Elevated(string root)
    {
        Console.WriteLine("\n=== Elevated registration ===");
        int elevation = ElevationType();
        bool admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if (!admin)
        {
            Console.WriteLine("  not elevated: section 4 runs elevated (the battery runs it so)");
            return;
        }
        if (HMVR.IsSteamVRRunning)
        {
            Check("SteamVR is stopped, so the driver folder can be refreshed", false, "vrserver is running");
            return;
        }
        PlantedTool(root);
        if (elevation == TokenElevationTypeFull)
            UserRights(root);
        else
            Console.WriteLine("  the token is not split (elevation type " + elevation + "): the SDK writes as is, user-rights checks do not apply");
    }

    /// <summary>Discovery pointed at a SteamVR folder whose vrpathreg.exe is
    /// not a program. Running it fails, so registration succeeds only if
    /// nothing from that folder runs.</summary>
    static void PlantedTool(string root)
    {
        const string Key = @"SOFTWARE\HIDMaestro";
        const string Hint = "SteamVRPath", Gate = "InstalledVrManifestSha256";
        string fake = Path.Combine(root, "FakeSteamVR");
        string planted = Path.Combine(fake, "bin", "win64", "vrpathreg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
        byte[] plantedBytes = System.Text.Encoding.ASCII.GetBytes("not a program\r\n");
        File.WriteAllBytes(planted, plantedBytes);

        var golden = LoadGolden().First(c => c.Name == "file as SteamVR leaves it");
        string file = Path.Combine(root, "planted", "openvrpaths.vrpath");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, golden.Seed!);
        Environment.SetEnvironmentVariable("VR_PATHREG_OVERRIDE", file);
        byte[] expect = string.Equals(golden.Path, ExtractRoot, StringComparison.Ordinal)
            ? golden.Expect
            : Apply(golden.Seed, "adddriver", ExtractRoot) ?? Array.Empty<byte>();

        using var k = Registry.LocalMachine.CreateSubKey(Key);
        object? savedHint = k.GetValue(Hint);
        object? savedGate = k.GetValue(Gate);
        try
        {
            k.SetValue(Hint, fake);
            k.DeleteValue(Gate, throwOnMissingValue: false);
            Check("discovery finds the planted folder", string.Equals(HMVR.SteamVRPath, fake, StringComparison.OrdinalIgnoreCase),
                  HMVR.SteamVRPath ?? "(none)");

            bool ok = false;
            string detail = "";
            try { ok = HMVR.EnsureDriverRegistered(); }
            catch (Exception e) { detail = e.GetType().Name + ": " + e.Message; }
            Check("registering runs nothing from the SteamVR folder", ok, detail);
            byte[] got = File.ReadAllBytes(file);
            Check("the registry lists the driver folder as vrpathreg writes it", got.AsSpan().SequenceEqual(expect),
                  got.AsSpan().SequenceEqual(expect) ? "" : FirstDifference(expect, got));
            Check("the planted vrpathreg.exe is untouched", File.ReadAllBytes(planted).AsSpan().SequenceEqual(plantedBytes));
        }
        finally
        {
            if (savedHint != null) k.SetValue(Hint, savedHint); else k.DeleteValue(Hint, throwOnMissingValue: false);
            if (savedGate != null) k.SetValue(Gate, savedGate); else k.DeleteValue(Gate, throwOnMissingValue: false);
        }
        Check("the HKLM hint and gate are back as they were",
              Equals(k.GetValue(Hint), savedHint) && Equals(k.GetValue(Gate), savedGate));
    }

    /// <summary>The elevated half of a split token writes the user's file
    /// with the user's rights: a folder only administrators can write is
    /// refused, and a file it creates is owned by the user.</summary>
    static void UserRights(string root)
    {
        var add = Internal("AddDriver", typeof(string));
        Check("OpenVrPathRegistry.AddDriver exists", add != null);
        if (add == null) return;

        var locked = new DirectoryInfo(Path.Combine(root, "admins-only"));
        var sec = new DirectorySecurity();
        sec.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
        locked.Create(sec);
        string lockedFile = Path.Combine(locked.FullName, "openvrpaths.vrpath");
        Environment.SetEnvironmentVariable("VR_PATHREG_OVERRIDE", lockedFile);
        string detail = "";
        bool refused = false;
        try { add.Invoke(null, new object[] { ExtractRoot }); detail = "written"; }
        catch (TargetInvocationException e) when (e.InnerException is InvalidOperationException { InnerException: UnauthorizedAccessException }) { refused = true; }
        catch (TargetInvocationException e) { detail = e.InnerException?.GetType().Name + ": " + e.InnerException?.Message; }
        Check("a registry in a folder only administrators can write is refused", refused && !File.Exists(lockedFile), detail);
        Check("and nothing is left in that folder", locked.GetFiles().Length == 0,
              string.Join(", ", locked.GetFiles().Select(f => f.Name)));

        string openFile = Path.Combine(root, "user", "openvr", "openvrpaths.vrpath");
        Environment.SetEnvironmentVariable("VR_PATHREG_OVERRIDE", openFile);
        bool wrote = false;
        try { wrote = (bool)add.Invoke(null, new object[] { ExtractRoot })!; }
        catch (TargetInvocationException e) { detail = e.InnerException?.GetType().Name + ": " + e.InnerException?.Message; }
        Check("a registry in the user's own folder is written", wrote && File.Exists(openFile), detail);
        if (File.Exists(openFile))
        {
            var owner = new FileInfo(openFile).GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            var user = WindowsIdentity.GetCurrent().User;
            Check("the file belongs to the user, not to Administrators", Equals(owner, user), $"owner {owner}");
        }
        var thread = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        Check("the thread is back on the process token", !thread.IsSystem && thread.ImpersonationLevel == TokenImpersonationLevel.None,
              thread.ImpersonationLevel.ToString());
    }

    const int TokenElevationTypeFull = 2;

    static int ElevationType()
    {
        using var id = WindowsIdentity.GetCurrent();
        return GetTokenInformation(id.Token, 18 /* TokenElevationType */, out int type, sizeof(int), out _) ? type : 0;
    }

    const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;

    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
}
