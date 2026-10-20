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
// carries the user's rights and no more and leaves the calling thread as it
// found it. It restores the two HKLM values it changes, removes the driver
// folder it extracted when there was none before, and first repairs a
// SteamVR hint a killed run left pointing at its own fake folder. While
// SteamVR runs, section 4 is skipped. Exit 0 PASS, 1 FAIL, 2 when section 4
// was skipped and everything else passed.

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

        RepairLeftoverHint();
        RepairLeftoverGate();
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
        return s_failures != 0 ? 1 : s_skipped ? 2 : 0;
    }

    static bool s_skipped;

    /// <summary>A run killed inside section 4 leaves the HKLM SteamVRPath
    /// hint pointing at its own fake folder under the temp directory, whose
    /// vrpathreg.exe discovery would keep returning. Remove such a hint: the
    /// SDK finds SteamVR again without it.</summary>
    static void RepairLeftoverHint()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestro", writable: IsAdmin());
        if (k?.GetValue("SteamVRPath") is not string hint) return;
        string temp = Path.GetFullPath(Path.GetTempPath());
        bool ours = hint.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                    && hint.Contains("hm-vr-conflict-", StringComparison.OrdinalIgnoreCase)
                    && hint.EndsWith("FakeSteamVR", StringComparison.OrdinalIgnoreCase);
        if (!ours) return;
        if (!IsAdmin())
        {
            Console.WriteLine($"  a killed run left the SteamVRPath hint at {hint}. Run elevated to remove it.");
            return;
        }
        k!.DeleteValue("SteamVRPath", throwOnMissingValue: false);
        Console.WriteLine($"  removed the SteamVRPath hint a killed run left at {hint}");
    }

    /// <summary>A run killed inside the unregister section leaves its marker
    /// in the HKLM gate and loses the real hash. Remove the marker: the next
    /// registration extracts the payload again and writes the hash back.</summary>
    static void RepairLeftoverGate()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestro", writable: IsAdmin());
        if (k?.GetValue("InstalledVrManifestSha256") as string != UnregisterMarker) return;
        if (!IsAdmin())
        {
            Console.WriteLine("  a killed run left its marker in the InstalledVrManifestSha256 gate. Run elevated to remove it.");
            return;
        }
        k!.DeleteValue("InstalledVrManifestSha256", throwOnMissingValue: false);
        Console.WriteLine("  removed the gate marker a killed run left");
    }

    const string UnregisterMarker = "S68-unregister-gate";

    static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

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
        // The scan also reads SteamVR's own drivers folder, where another
        // program may have installed a driver under this name. Only the
        // probe's own folders are counted, so that one cannot fail the check.
        int mine = found.Count(f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Check("a registered folder that no longer exists is skipped", mine == 2, $"{mine} of the probe's folders reported");

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
            // The driver folder cannot be refreshed under a running SteamVR,
            // and this machine's own SteamVR is not the probe's to stop.
            Console.WriteLine("  [SKIP] section 4: SteamVR is running on this machine");
            s_skipped = true;
            return;
        }
        PlantedTool(root);
        UnregisterFailure(root, elevation == TokenElevationTypeFull);
        if (elevation == TokenElevationTypeFull)
        {
            UserRights(root);
            ThreadState(root);
        }
        else
            Console.WriteLine("  the token is not split (elevation type " + elevation + "): the SDK writes as is, user-rights checks do not apply");
    }

    /// <summary>HMVR.UnregisterDriver throws InvalidOperationException when
    /// the registry cannot be updated, and the HKLM idempotence gate stays
    /// set while the entry is still registered. Two refusals: a registry file
    /// another handle holds open exclusively, and, for the elevated half of a
    /// split token, one in a folder only administrators can read (the write
    /// runs as the user). A clean removal still clears the gate.</summary>
    static void UnregisterFailure(string root, bool splitToken)
    {
        const string Key = @"SOFTWARE\HIDMaestro";
        const string Gate = "InstalledVrManifestSha256";
        const string Marker = UnregisterMarker;
        using var k = Registry.LocalMachine.CreateSubKey(Key);
        object? savedGate = k.GetValue(Gate);
        byte[] seed = Apply(null, "adddriver", ExtractRoot) ?? Array.Empty<byte>();
        try
        {
            string held = Path.Combine(root, "unreg-held", "openvrpaths.vrpath");
            Directory.CreateDirectory(Path.GetDirectoryName(held)!);
            File.WriteAllBytes(held, seed);
            Environment.SetEnvironmentVariable("VR_PATHREG_OVERRIDE", held);
            k.SetValue(Gate, Marker);
            using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                UnregisterRefused("a registry file another handle holds open", k, Gate, Marker);
            Check("and the entry is still registered", File.ReadAllBytes(held).AsSpan().SequenceEqual(seed));

            if (splitToken)
            {
                var locked = new DirectoryInfo(Path.Combine(root, "unreg-admins-only"));
                var sec = new DirectorySecurity();
                sec.SetSecurityDescriptorSddlForm("O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)");
                locked.Create(sec);
                string lockedFile = Path.Combine(locked.FullName, "openvrpaths.vrpath");
                File.WriteAllBytes(lockedFile, seed);
                Environment.SetEnvironmentVariable("VR_PATHREG_OVERRIDE", lockedFile);
                k.SetValue(Gate, Marker);
                UnregisterRefused("a registry in a folder only administrators can read", k, Gate, Marker);
                Check("and the entry is still registered", File.ReadAllBytes(lockedFile).AsSpan().SequenceEqual(seed));
            }

            string clean = Path.Combine(root, "unreg-clean", "openvrpaths.vrpath");
            Directory.CreateDirectory(Path.GetDirectoryName(clean)!);
            File.WriteAllBytes(clean, seed);
            Environment.SetEnvironmentVariable("VR_PATHREG_OVERRIDE", clean);
            k.SetValue(Gate, Marker);
            string detail = "";
            try { HMVR.UnregisterDriver(); }
            catch (Exception e) { detail = e.GetType().Name + ": " + e.Message; }
            Check("a removal that succeeds returns", detail.Length == 0, detail);
            var listed = Internal("ExternalDrivers")?.Invoke(null, null) as List<string>;
            Check("and takes the entry out of the registry",
                  listed != null && !listed.Contains(ExtractRoot, StringComparer.Ordinal), listed == null ? "ExternalDrivers missing" : "");
            Check("and clears the gate", k.GetValue(Gate) == null, k.GetValue(Gate)?.ToString() ?? "");
        }
        finally
        {
            if (savedGate != null) k.SetValue(Gate, savedGate); else k.DeleteValue(Gate, throwOnMissingValue: false);
        }
        Check("the HKLM gate is back as it was", Equals(k.GetValue(Gate), savedGate));
    }

    static void UnregisterRefused(string what, RegistryKey k, string gate, string marker)
    {
        string outcome = "returned normally";
        bool threw = false;
        try { HMVR.UnregisterDriver(); }
        catch (InvalidOperationException) { threw = true; outcome = ""; }
        catch (Exception e) { outcome = e.GetType().Name + ": " + e.Message; }
        Check($"UnregisterDriver throws InvalidOperationException for {what}", threw, outcome);
        Check("and the gate stays set", Equals(k.GetValue(gate), marker), k.GetValue(gate)?.ToString() ?? "(cleared)");
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
        // Deleting the gate makes registration extract the driver. A machine
        // that never had the folder gets it back to absent afterward.
        bool hadDriver = Directory.Exists(ExtractRoot);
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
            if (!hadDriver)
                try { Directory.Delete(ExtractRoot, recursive: true); } catch (DirectoryNotFoundException) { }
        }
        Check("the HKLM hint and gate are back as they were",
              Equals(k.GetValue(Hint), savedHint) && Equals(k.GetValue(Gate), savedGate));
        Check("the driver folder is as it was before the run", Directory.Exists(ExtractRoot) == hadDriver,
              hadDriver ? "it was there" : "it was not there");
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

    /// <summary>The write leaves the calling thread as it found it. A thread
    /// on a token of its own comes back on that token, and a caller's
    /// exception filter, which runs before any finally below the caller,
    /// runs on the process token. The internal method is called through a
    /// delegate: reflection's Invoke would catch the exception first and
    /// hide what the filter sees.</summary>
    static void ThreadState(string root)
    {
        var method = typeof(HMVR).Assembly.GetType("HIDMaestro.Internal.OpenVrPathRegistry")?
            .GetMethod("RunAsUser", BindingFlags.Static | BindingFlags.NonPublic);
        Check("OpenVrPathRegistry.RunAsUser exists", method != null);
        if (method == null) return;
        var run = (Func<Func<bool>, bool>)method.MakeGenericMethod(typeof(bool)).CreateDelegate(typeof(Func<Func<bool>, bool>));

        bool kept = false;
        string detail = "";
        var t = new System.Threading.Thread(() =>
        {
            if (!ImpersonateSelf(SecurityImpersonation)) { detail = "ImpersonateSelf error " + Marshal.GetLastWin32Error(); return; }
            try
            {
                run(() => true);
                kept = ThreadHasToken();
            }
            catch (Exception e) { detail = e.GetType().Name + ": " + e.Message; }
            finally { RevertToSelf(); }
        });
        t.Start();
        t.Join();
        Check("a thread that already runs on a token of its own comes back on it", kept, detail);

        bool? filterSawToken = null;
        try { run(() => throw new IOException("S68: a write that fails")); }
        catch (IOException) when (Observe(ref filterSawToken)) { }
        Check("a caller's exception filter runs on the process token, not the user's", filterSawToken == false,
              filterSawToken == null ? "the filter did not run" : filterSawToken.Value ? "the filter ran on the user's token" : "");
        Check("and the thread is back on the process token", !ThreadHasToken());
    }

    static bool Observe(ref bool? sawToken)
    {
        sawToken = ThreadHasToken();
        return true;
    }

    static bool ThreadHasToken()
    {
        if (!OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, true, out IntPtr token)) return false;
        CloseHandle(token);
        return true;
    }

    const int TokenElevationTypeFull = 2;
    const int SecurityImpersonation = 2;
    const uint TOKEN_QUERY = 0x0008;

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool ImpersonateSelf(int level);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool RevertToSelf();

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out IntPtr token);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

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
