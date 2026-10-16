// Checks that need no elevation and no device: CoexistCheck.exe offline.
//
//   OF1  A directory this process creates under %TEMP%, owned by its own
//        user, may not be reset where it is: a program that owns a
//        directory keeps WRITE_DAC through a handle it opened before any
//        reset (ProtectedStaging.InPlaceRepairAllowed).
//   OF2  %ProgramData% itself may: SYSTEM owns it, and its DACL is the one
//        every folder an elevated program creates under it inherits,
//        v1.12.0's %ProgramData%\HIDMaestro included, which grants Users
//        create and write but no other principal the right to change it.
//   OF3  T6 at HIDMAESTRO_TIMEOUT_SCALE 1, 1.5 and 2: a peer at each scale
//        runs a tool whose child holds the output for 15 s scaled, and the
//        tool runner returns at its 10 s drain bound scaled, with the output
//        still open and the tool's exit code.
//   OF4  The refusal. A directory this process's user owns, with a file
//        under it held open so it cannot be moved aside, goes through the
//        repair the shared root takes (ProtectedStaging.EnsureProtected,
//        Repair.InPlace): it throws InvalidOperationException naming the
//        folder, its owner, why the move failed and what clears it, and the
//        folder is left as it was.
//   OF5  The pin's two kinds of directory open and its file hold
//        (ProtectedStaging.OpenLevel, HoldFile), called on a scratch copy
//        of the helper's chain: root\Staging\<version>\swd\hmswd.exe, with
//        whoami.exe standing in for the helper. A handle on the root that
//        shares nothing, for listing and for DELETE, as any program can
//        open the real root, does not stop the root's open, and stops the
//        open a held level uses (the control). With the chain open the way
//        Pin opens it and the file held, no rename of any level, no
//        recursive delete of the root, and no write, rename or delete of the
//        file gets through, while the file still runs and copies. Released,
//        the root renames and the file opens for writing (the controls).
//        The check Pin makes through each handle refuses a folder its user
//        owns and passes this machine's %ProgramData%\HIDMaestro.
//   OF6  The deploy's tools run from held files: the driver staging set
//        written to a scratch directory as EnsureExtracted writes it, every
//        file held, Inf2Cat run through DriverBuilder.Run as
//        GenerateCatalogs runs it writes a catalog per INF, and signtool
//        loads its libraries and reads a held binary.
//   OF7  Pin's choice of levels to hold (ProtectedStaging.HoldsLevel):
//        Staging and every level below it, and nothing above it, beside it
//        or under the OpenVR root. On a machine with the OpenVR folders,
//        Pin over them holds none: a listing open that shares nothing
//        succeeds on each level before the pin (the control) and while it
//        is held. Staging itself needs elevation, so I12 renames it live.
//   OF9  The moved-aside tree is never deleted by path. OF9a: a hostile
//        folder where StagingParent would be (one this process owns) is
//        refused, not moved aside, so another program's folder there is left
//        whole. OF9b: DeleteMovedAside, MovedAsideLevels and DeleteAsidesIn
//        no longer exist, so no code recursively deletes a moved-aside tree
//        a non-administrator may change through a stale grant.
//   OF8  The squat. Staging lives under %ProgramFiles%\HIDMaestro, and a
//        thread impersonating a program without elevation
//        (Native.MediumToken) can create no folder there, the staging
//        folder's own name included, while it can create one directly under
//        %ProgramData% (the control, removed after). So the folder-first
//        squat that makes a %ProgramData% folder refuse (OF4) has nowhere to
//        stand in front of staging. I10 runs it live against the real root.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

internal static partial class Program
{
    static int Offline()
    {
        Console.WriteLine("=== Coexistence checks that need no elevation ===");
        var allowed = typeof(HIDMaestro.HMContext).Assembly.GetType("HIDMaestro.Internal.ProtectedStaging")
            ?.GetMethod("InPlaceRepairAllowed", BindingFlags.NonPublic | BindingFlags.Static);
        Check("OF: ProtectedStaging.InPlaceRepairAllowed exists", allowed != null);
        if (allowed != null)
        {
            string own = Path.Combine(Path.GetTempPath(), "HIDMaestro_offline_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(own);
            try
            {
                bool ownAllowed = (bool)allowed.Invoke(null, new object[] { own })!;
                Check("OF1: a directory this process's user owns may not be reset where it is", !ownAllowed, own);
            }
            finally { Directory.Delete(own, true); }
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            bool pdAllowed = (bool)allowed.Invoke(null, new object[] { programData })!;
            Check("OF2: %ProgramData%, owned by SYSTEM with the DACL its folders inherit, may", pdAllowed, programData);
        }
        foreach (string scale in new[] { "1", "1.5", "2" })
        {
            using var p = PeerProcess.Start("OF3-" + scale, timeoutScale: scale);
            string h = p.Send("runtool beyond", 180_000);
            Check($"OF3: at timeout scale {scale}, the tool runner returns at its drain bound, not when the child lets go", HeldForDrainBound(h), h);
        }
        OfflineRefusal();
        OfflinePinShape();
        OfflineHeldTools();
        OfflinePinLevels();
        OfflineSquat();
        OfflineSweep();
        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa,
        uint disposition, uint flags, IntPtr template);

    /// <summary>A directory handle the way a program without elevation can
    /// open the real root: <paramref name="access"/>, sharing nothing.</summary>
    static Microsoft.Win32.SafeHandles.SafeFileHandle Blocker(string dir, uint access) =>
        CreateFileW(dir, access, 0, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);

    static T Call<T>(MethodInfo m, object?[] args)
    {
        try { return (T)m.Invoke(null, args)!; }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
    }

    static string Attempt(Action a)
    {
        try { a(); return "done"; }
        catch (Exception e) { return e.GetType().Name; }
    }

    static void OfflinePinShape()
    {
        var staging = typeof(HIDMaestro.HMContext).Assembly.GetType("HIDMaestro.Internal.ProtectedStaging");
        var openLevel = staging?.GetMethod("OpenLevel", BindingFlags.NonPublic | BindingFlags.Static);
        var holdFile = staging?.GetMethod("HoldFile", BindingFlags.NonPublic | BindingFlags.Static);
        if (openLevel == null || holdFile == null)
        {
            Check("OF5: ProtectedStaging.OpenLevel and HoldFile exist", false);
            return;
        }
        string scratch = Path.Combine(Path.GetTempPath(), "hmpin" + Guid.NewGuid().ToString("N")[..8]);
        string root = Path.Combine(scratch, "HIDMaestro");
        string stagingDir = Path.Combine(root, "Staging");
        string version = Path.Combine(stagingDir, "1.13.0-0000000000000000");
        string swd = Path.Combine(version, "swd");
        string exe = Path.Combine(swd, "hmswd.exe");
        Directory.CreateDirectory(swd);
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), exe);
        string expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(exe))).ToLowerInvariant();
        const uint ListDirectory = 0x0001, Delete = 0x00010000;
        var handles = new List<IDisposable>();
        try
        {
            // A program's handle on the root that shares nothing, one for
            // listing and one for DELETE in turn (two such handles cannot
            // both be open).
            foreach (var (kind, access) in new[] { ("listing", ListDirectory), ("DELETE", Delete) })
            {
                using var blocker = Blocker(root, access);
                Check($"OF5: a {kind} handle that shares nothing opens on the root", !blocker.IsInvalid);
                string open = Attempt(() => Call<IDisposable>(openLevel, new object[] { root, false }).Dispose());
                Check($"OF5: beside it, the root opens the way Pin opens it", open == "done", open);
                string control = "";
                try { Call<IDisposable>(openLevel, new object[] { root, true }).Dispose(); control = "opened"; }
                catch (InvalidOperationException e) { control = e.Message; }
                Check($"OF5: control: a held open of the root fails beside it with a sharing violation",
                      control.Contains("Win32 error 32", StringComparison.Ordinal), control);
            }

            // Pin's check of a level reads it through the handle it holds.
            var isProtected = staging!.GetMethod("IsProtected", BindingFlags.NonPublic | BindingFlags.Static,
                new[] { typeof(Microsoft.Win32.SafeHandles.SafeFileHandle) });
            if (isProtected == null) Check("OF5: ProtectedStaging.IsProtected(SafeFileHandle) exists", false);
            else
            {
                using (var h = Call<Microsoft.Win32.SafeHandles.SafeFileHandle>(openLevel, new object[] { root, false }))
                    Check("OF5: through its handle, a folder its user owns is not protected",
                          !Call<bool>(isProtected, new object[] { h }), root);
                string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HIDMaestro");
                if (Directory.Exists(real))
                    using (var h = Call<Microsoft.Win32.SafeHandles.SafeFileHandle>(openLevel, new object[] { real, false }))
                        Check("OF5: through its handle, this machine's %ProgramData%\\HIDMaestro is",
                              Call<bool>(isProtected, new object[] { h }), real);
            }

            // The root's own open blocks nothing: with it alone open, the
            // root renames. What stops the rename below is the hold under it.
            using (Call<IDisposable>(openLevel, new object[] { root, false }))
            {
                string alone = Attempt(() => { Directory.Move(root, root + ".moved"); Directory.Move(root + ".moved", root); });
                Check("OF5: control: with only the root's own open, the root renames", alone == "done", alone);
            }

            handles.Add(Call<IDisposable>(openLevel, new object[] { root, false }));
            foreach (string held in new[] { stagingDir, version, swd })
                handles.Add(Call<IDisposable>(openLevel, new object[] { held, true }));
            var args = new object?[] { exe, null };
            handles.Add(Call<IDisposable>(holdFile, args));
            Check("OF5: the file's SHA-256 is read through its hold", (args[1] as string) == expected, $"{args[1]} vs {expected}");

            foreach (string dir in new[] { root, stagingDir, version, swd })
            {
                string moved = dir + ".moved";
                string r = Attempt(() => Directory.Move(dir, moved));
                if (r == "done") Directory.Move(moved, dir);
                Check($"OF5: held, {Path.GetFileName(dir)} cannot be renamed", r != "done", r);
            }
            string del = Attempt(() => Directory.Delete(root, true));
            Check("OF5: held, a recursive delete of the root removes nothing", del != "done" && File.Exists(exe), del);
            string write = Attempt(() => new FileStream(exe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete).Dispose());
            Check("OF5: held, the file cannot be opened for writing", write != "done", write);
            string rename = Attempt(() => File.Move(exe, exe + ".moved"));
            if (rename == "done") File.Move(exe + ".moved", exe);
            Check("OF5: held, the file cannot be renamed", rename != "done", rename);
            string delete = Attempt(() => File.Delete(exe));
            Check("OF5: held, the file cannot be deleted", delete != "done" && File.Exists(exe), delete);

            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            int code = -1;
            string run = Attempt(() =>
            {
                using var p = System.Diagnostics.Process.Start(psi)!;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(30_000);
                code = p.ExitCode;
            });
            Check("OF5: held, the file still runs", run == "done" && code == 0, $"{run}, exit {code}");
            string copy = Path.Combine(scratch, "copy.exe");
            string copied = Attempt(() => File.Copy(exe, copy));
            Check("OF5: held, the file still copies", copied == "done" && File.Exists(copy), copied);
        }
        finally
        {
            foreach (var h in handles) h.Dispose();
        }
        string released = Attempt(() => { Directory.Move(root, root + ".moved"); Directory.Move(root + ".moved", root); });
        Check("OF5: control: released, the root renames", released == "done", released);
        string writable = Attempt(() => new FileStream(exe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        Check("OF5: control: released, the file opens for writing", writable == "done", writable);
        Directory.Delete(scratch, true);
    }

    static void OfflinePinLevels()
    {
        var asm = typeof(HIDMaestro.HMContext).Assembly;
        var staging = asm.GetType("HIDMaestro.Internal.ProtectedStaging");
        var holds = staging?.GetMethod("HoldsLevel", BindingFlags.NonPublic | BindingFlags.Static);
        var pin = staging?.GetMethod("Pin", BindingFlags.NonPublic | BindingFlags.Static);
        string? stagingRoot = staging?.GetProperty("StagingRoot", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as string;
        string? sharedRoot = staging?.GetProperty("HidMaestroRoot", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as string;
        string? vrRoot = asm.GetType("HIDMaestro.Internal.VrDriverBuilder")?.GetProperty("ExtractRoot", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;
        if (holds == null || pin == null || stagingRoot == null || sharedRoot == null || vrRoot == null)
        {
            Check("OF7: ProtectedStaging.HoldsLevel, Pin, StagingRoot, HidMaestroRoot and VrDriverBuilder.ExtractRoot exist", false);
            return;
        }
        bool Holds(string d) => Call<bool>(holds, new object[] { d });
        string above = Path.GetDirectoryName(stagingRoot)!;
        Check("OF7: Staging is held", Holds(stagingRoot), stagingRoot);
        Check("OF7: a level below Staging is held", Holds(Path.Combine(stagingRoot, "1.13.0-0000000000000000", "swd")));
        Check("OF7: the folder above Staging is not", !Holds(above), above);
        Check("OF7: a sibling whose name starts with Staging's is not", !Holds(stagingRoot + "Old"), stagingRoot + "Old");
        Check("OF7: the OpenVR root and folders are not", !Holds(sharedRoot) && !Holds(vrRoot), $"{sharedRoot}, {vrRoot}");

        string leaf = Path.Combine(vrRoot, "bin", "win64");
        if (!Directory.Exists(leaf))
        {
            Console.WriteLine($"  [SKIP] OF7: no OpenVR folders on this machine ({leaf}), so Pin over them is not run");
            return;
        }
        var levels = new List<string>();
        for (string d = leaf; ; d = Path.GetDirectoryName(d)!)
        {
            levels.Add(d);
            if (string.Equals(d.TrimEnd('\\'), sharedRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) break;
        }
        const uint ListDirectory = 0x0001;
        string Exclusive()
        {
            foreach (string d in levels)
            {
                using var h = Blocker(d, ListDirectory);
                if (h.IsInvalid) return $"{d}: error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}";
            }
            return "opened";
        }
        string before = Exclusive();
        Check("OF7: control: before the pin, every OpenVR level opens for listing sharing nothing", before == "opened", before);
        if (before != "opened") return;
        string during;
        using (Call<IDisposable>(pin, new object[] { new[] { leaf } }))
            during = Exclusive();
        Check("OF7: while Pin holds the OpenVR chain, every level still opens that way: none is held", during == "opened", during);
    }

    static void OfflineSquat()
    {
        var staging = typeof(HIDMaestro.HMContext).Assembly.GetType("HIDMaestro.Internal.ProtectedStaging");
        string? parent = staging?.GetProperty("StagingParent", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as string;
        string? root = staging?.GetProperty("StagingRoot", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as string;
        if (parent == null || root == null)
        {
            Check("OF8: ProtectedStaging.StagingParent and StagingRoot exist", false);
            return;
        }
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        Check("OF8: staging is under %ProgramFiles%, directly in its HIDMaestro folder",
              string.Equals(Path.GetDirectoryName(parent), programFiles, StringComparison.OrdinalIgnoreCase)
              && string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase), $"{parent}, {root}");

        // The staging folder's own name when it is missing, as a squatter
        // would want it, and a fresh name beside it either way.
        var targets = new List<string> { Path.Combine(programFiles, "hmsquat" + Guid.NewGuid().ToString("N")[..8]) };
        if (!Directory.Exists(parent)) targets.Add(parent);
        foreach (string target in targets)
        {
            string made = CreateUnder(Native.MediumToken(), target);
            bool created = made == "created";
            if (created) { try { Directory.Delete(target, true); } catch { } }
            Check($"OF8: a program without elevation cannot create {target}",
                  !created && made.StartsWith("UnauthorizedAccessException", StringComparison.Ordinal), made);
        }
        string control = Path.Combine(programData, "hmsquat" + Guid.NewGuid().ToString("N")[..8]);
        string madeControl = CreateUnder(Native.MediumToken(), control);
        string? owner = null;
        if (Directory.Exists(control))
        {
            try { owner = new DirectoryInfo(control).GetAccessControl().GetOwner(typeof(System.Security.Principal.NTAccount))?.Value; } catch { }
            try { Directory.Delete(control, true); } catch { }
        }
        Check("OF8: control: the same thread creates a folder directly under %ProgramData%, owned by its user",
              madeControl == "created" && owner != null
              && owner.Equals(System.Security.Principal.WindowsIdentity.GetCurrent().Name, StringComparison.OrdinalIgnoreCase),
              $"{madeControl}, owner {owner}");
    }

    static void OfflineHeldTools()
    {
        var asm = typeof(HIDMaestro.HMContext).Assembly;
        var builder = asm.GetType("HIDMaestro.Internal.DriverBuilder");
        var payload = builder?.GetMethod("PayloadFor", BindingFlags.NonPublic | BindingFlags.Static);
        var run = builder?.GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Static);
        var catalogOs = builder?.GetMethod("CatalogOsFor", BindingFlags.NonPublic | BindingFlags.Static);
        var holdFile = asm.GetType("HIDMaestro.Internal.ProtectedStaging")?.GetMethod("HoldFile", BindingFlags.NonPublic | BindingFlags.Static);
        if (payload == null || run == null || catalogOs == null || holdFile == null)
        {
            Check("OF6: DriverBuilder.PayloadFor, Run, CatalogOsFor and ProtectedStaging.HoldFile exist", false);
            return;
        }
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;
        // Short: Inf2Cat is a .NET Framework program and fails on a library
        // path of 260 characters (driver_catalog_check).
        string dir = Path.Combine(Path.GetTempPath(), "hmof" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var handles = new List<IDisposable>();
        try
        {
            foreach (var (file, logical) in Call<IEnumerable<(string File, string Logical)>>(payload, new object[] { arch }))
            {
                using (var src = asm.GetManifestResourceStream(logical)!)
                using (var dst = File.Create(Path.Combine(dir, file)))
                    src.CopyTo(dst);
                handles.Add(Call<IDisposable>(holdFile, new object?[] { Path.Combine(dir, file), null }));
            }
            Check("OF6: every file of the staging set is written and held", handles.Count > 0, $"{handles.Count} files");
            string os = Call<string>(catalogOs, new object[] { arch });
            var r = Call<ValueTuple<int, string>>(run, new object?[] { Path.Combine(dir, "Inf2Cat.exe"), $"/driver:\"{dir}\" /os:{os}", dir, 60_000 });
            int infs = Directory.GetFiles(dir, "*.inf").Length, cats = Directory.GetFiles(dir, "*.cat").Length;
            Check("OF6: Inf2Cat run from held files catalogs the held INFs and binaries",
                  r.Item1 == 0 && cats == infs && infs > 0, $"exit {r.Item1}, {infs} INFs, {cats} catalogs: {Last(r.Item2)}");
            var s = Call<ValueTuple<int, string>>(run, new object?[] { Path.Combine(dir, "signtool.exe"), "verify /pa \"" + Path.Combine(dir, "HIDMaestro.dll") + "\"", dir, 60_000 });
            Check("OF6: signtool run from held files loads and reads a held binary (an unsigned one: no signature found)",
                  s.Item2.Contains("No signature found", StringComparison.Ordinal), $"exit {s.Item1}: {Last(s.Item2)}");
        }
        finally
        {
            foreach (var h in handles) h.Dispose();
            Directory.Delete(dir, true);
        }
    }

    static string Last(string output)
    {
        string last = "";
        foreach (string l in output.Split('\n'))
            if (l.Trim().Length > 0) last = l.Trim();
        return last;
    }

    static void OfflineRefusal()
    {
        var staging = typeof(HIDMaestro.HMContext).Assembly.GetType("HIDMaestro.Internal.ProtectedStaging");
        var ensure = staging?.GetMethod("EnsureProtected", BindingFlags.NonPublic | BindingFlags.Static);
        var repair = staging?.GetNestedType("Repair", BindingFlags.NonPublic);
        var sddl = staging?.GetField("RootSddl", BindingFlags.NonPublic | BindingFlags.Static);
        if (ensure == null || repair == null || sddl == null)
        {
            Check("OF4: ProtectedStaging.EnsureProtected, Repair and RootSddl exist", false);
            return;
        }
        string dir = Path.Combine(Path.GetTempPath(), "HIDMaestro_offline_" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "held", "file");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "held");
        string owner = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        try
        {
            Exception? thrown = null;
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { ensure.Invoke(null, new object[] { dir, sddl.GetValue(null)!, Enum.Parse(repair, "InPlace") }); }
                catch (TargetInvocationException e) { thrown = e.InnerException; }
            }
            string message = thrown?.Message ?? "nothing thrown";
            Check("OF4: a folder its user owns that cannot move is refused with InvalidOperationException",
                  thrown is InvalidOperationException, $"{thrown?.GetType().Name}: {message}");
            Check("OF4: the refusal names the folder, its owner, the failed move and what clears it",
                  message.StartsWith(dir + " is owned by " + owner + ", not Administrators or SYSTEM", StringComparison.OrdinalIgnoreCase)
                  && message.Contains("could not be moved aside (", StringComparison.Ordinal)
                  && message.Contains("SteamVR", StringComparison.Ordinal)
                  && message.Contains("elevated prompt", StringComparison.Ordinal), message);
            Check("OF4: and the folder is left where it was, its file in place",
                  File.Exists(file) && Directory.GetDirectories(Path.GetTempPath(), Path.GetFileName(dir) + ".untrusted-*").Length == 0, dir);

            // OF4b: the same refusal for a staging directory (StagingSddl)
            // does not tell the reader to close SteamVR, which holds nothing
            // under Program Files.
            var stagingSddl = staging!.GetField("StagingSddl", BindingFlags.NonPublic | BindingFlags.Static);
            if (stagingSddl == null) { Check("OF4b: ProtectedStaging.StagingSddl exists", false); }
            else
            {
                Exception? t2 = null;
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { ensure.Invoke(null, new object[] { dir, stagingSddl.GetValue(null)!, Enum.Parse(repair, "InPlace") }); }
                    catch (TargetInvocationException e) { t2 = e.InnerException; }
                }
                string m2 = t2?.Message ?? "nothing thrown";
                Check("OF4b: a staging refusal names the folder and the remedy but not SteamVR",
                      t2 is InvalidOperationException && m2.Contains("could not be moved aside (", StringComparison.Ordinal)
                      && m2.Contains("try again", StringComparison.Ordinal) && !m2.Contains("SteamVR", StringComparison.Ordinal), m2);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    static void OfflineSweep()
    {
        var staging = typeof(HIDMaestro.HMContext).Assembly.GetType("HIDMaestro.Internal.ProtectedStaging");
        var ensure = staging?.GetMethod("EnsureProtected", BindingFlags.NonPublic | BindingFlags.Static);
        var repair = staging?.GetNestedType("Repair", BindingFlags.NonPublic);
        var stagingSddl = staging?.GetField("StagingSddl", BindingFlags.NonPublic | BindingFlags.Static);
        var parentRepair = staging?.GetProperty("StagingParentRepair", BindingFlags.NonPublic | BindingFlags.Static);
        var wouldDelete = staging?.GetMethod("PruneWouldDelete", BindingFlags.NonPublic | BindingFlags.Static);
        if (ensure == null || repair == null || stagingSddl == null || parentRepair == null || wouldDelete == null)
        {
            Check("OF9: ProtectedStaging.EnsureProtected, Repair, StagingSddl, StagingParentRepair and PruneWouldDelete exist", false);
            return;
        }

        // OF9a: DirFor repairs the folder above Staging with StagingParentRepair.
        // The test reads that same seam, so a change back to MoveAside flips
        // it. On a folder this process owns (hostile, owner not Administrators
        // or SYSTEM) the seam's mode must refuse and leave the folder: with
        // MoveAside it would instead rename it aside, which these checks
        // catch.
        object mode = parentRepair.GetValue(null)!;
        Check("OF9a: StagingParent's repair mode is InPlaceOrRefuse, not MoveAside",
              mode.ToString() == "InPlaceOrRefuse", mode.ToString() ?? "null");
        string dir = Path.Combine(Path.GetTempPath(), "hmof9_" + Guid.NewGuid().ToString("N"));
        string inner = Path.Combine(dir, "someone-elses-files");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "keep.txt"), "keep");
        try
        {
            Exception? thrown = null;
            try { ensure.Invoke(null, new object[] { dir, stagingSddl.GetValue(null)!, mode }); }
            catch (TargetInvocationException e) { thrown = e.InnerException; }
            string message = thrown?.Message ?? "nothing thrown";
            Check("OF9a: a hostile StagingParent is refused with InvalidOperationException",
                  thrown is InvalidOperationException, $"{thrown?.GetType().Name}: {message}");
            Check("OF9a: the refusal names the folder and says HIDMaestro will not replace it",
                  message.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                  && message.Contains("belongs to another program", StringComparison.Ordinal)
                  && message.Contains("will not stage under it or move it aside", StringComparison.Ordinal), message);
            Check("OF9a: the folder and its contents are left in place, no aside made",
                  Directory.Exists(dir) && File.Exists(Path.Combine(inner, "keep.txt"))
                  && Directory.GetDirectories(Path.GetTempPath(), Path.GetFileName(dir) + ".untrusted-*").Length == 0, dir);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }

        // OF9b: the prune's keep/delete decision (PruneWouldDelete), the seam
        // its loop reads. A moved-aside tag is kept, so reverting the skip
        // flips it. An own old version with no live record is deleted. This
        // build's tag, and a live tag or version, are kept.
        var noLive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var liveV = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "9.9.9" };
        bool WouldDelete(string tag, HashSet<string> lt, HashSet<string> lv) =>
            (bool)wouldDelete.Invoke(null, new object[] { tag, lt, lv })!;
        Check("OF9b: a moved-aside directory is never pruned",
              !WouldDelete("1.2.3-abcdef0123456789.untrusted-" + Guid.NewGuid().ToString("N"), noLive, noLive));
        Check("OF9b: an own old version no record names is pruned",
              WouldDelete("0.0.1-0000000000000000", noLive, noLive));
        Check("OF9b: a version a live record names is kept",
              !WouldDelete("9.9.9-1111111111111111", noLive, liveV));
    }
}
