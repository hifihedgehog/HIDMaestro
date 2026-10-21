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
//        still open and the tool's exit code. The three peers run at once,
//        beside the checks below.
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
//        open the shared root and the OpenVR folders, which grant Users
//        read, does not stop the root's open, and stops the
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
//        whole. OF9b: the prune's keep-or-delete decision
//        (ProtectedStaging.PruneWouldDelete): a moved-aside directory is
//        never pruned, an old version no record names is, and a version a
//        live record names is kept.
//   OF10 Diagnostic files. The usbip-win2 host controller install's start
//        info (BuildForceDriverStartInfo) points TEMP and TMP at its helper's
//        staging folder (ProtectedStaging.ConfineChildTemp), not the user's
//        %TEMP%. A diagnostic log the SDK
//        opens (DeviceOrchestrator.OpenUserLog) is created with its folder
//        and is appendable. Its owner is the user only when the process is
//        elevated (RunAsUser is a no-op otherwise), so that part is a live
//        check, not one this offline run can fail.
//   OF8  The squat. Staging lives under %ProgramFiles%\HIDMaestro, and a
//        thread impersonating a program without elevation
//        (Native.MediumToken) can create no folder there, the staging
//        folder's own name included, while it can create one directly under
//        %ProgramData% (the control, removed after). So the folder-first
//        squat that makes a %ProgramData% folder refuse (OF4) has nowhere to
//        stand in front of staging. I10 runs it live against the real root.
//   OF11 An unread driver-store listing is never taken for "absent". The
//        usbip-win2 check (UsbipDriverInstaller.UdePackageNames, given a
//        stand-in listing) throws on a listing that fails, naming why, and
//        answers from a whole listing only. RemoveAllHidMaestroPackages,
//        given a failed listing, throws before any delete, and HIDMaestro's
//        own installed check (PnputilHelper.InstalledFrom) throws on one,
//        both naming the failure. HMContext.IsDriverInstalled's answer
//        (HMContext.AnswerInstalled) is false, not a throw, for each way the
//        driver store's read fails.
//   OF12 ObjectHolders, which names the processes that hold an object a
//        live check finds left behind (ObjectHolders.SelfCheck). "offline
//        holders" runs it alone.
//   OF13 A devnode whose transport index the census cannot read
//        (OfflineCensusIndex.cs). "offline census" runs it alone.
//   OF14 The floor refuses no identity key: with a v1.12.0 program's
//        device present the floor is 64, and a keyed create's identity is
//        its key's, index:1 included (OfflineCensusIndex.cs, run with
//        OF13).
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

internal static partial class Program
{
    static int Offline(string? only)
    {
        Console.WriteLine("=== Coexistence checks that need no elevation ===");
        if (only is "holders" or "census")
        {
            if (only == "holders") ObjectHolders.SelfCheck(Check);
            else { OfflineCensusIndex(); OfflineKeyBelowFloor(); OfflineLedgerHolders(); OfflineExitRecord(); }
            Console.WriteLine();
            Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
            return s_failures == 0 ? 0 : 1;
        }
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
            finally { RemoveScratch(own, "OF1"); }
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            bool pdAllowed = (bool)allowed.Invoke(null, new object[] { programData })!;
            Check("OF2: %ProgramData%, owned by SYSTEM with the DACL its folders inherit, may", pdAllowed, programData);
        }
        // OF3's three peers run at once, beside the checks after it. Each
        // runner's drain bound is a timer in its own process, which nothing
        // here shares, so the three wait out their bounds together and OF3
        // takes the longest bound, not the sum of the three.
        var of3 = new List<(string Scale, PeerProcess Peer)>();
        try
        {
            foreach (string scale in new[] { "1", "1.5", "2" })
            {
                var p = PeerProcess.Start("OF3-" + scale, timeoutScale: scale);
                of3.Add((scale, p));
                p.Post("runtool beyond");
            }
            OfflineRefusal();
            OfflinePinShape();
            OfflineHeldTools();
            OfflinePinLevels();
            OfflineSquat();
            OfflineSweep();
            OfflineDiagFiles();
            OfflineListings();
            ObjectHolders.SelfCheck(Check);
            OfflineCensusIndex();
            OfflineKeyBelowFloor();
            OfflineLedgerHolders();
            OfflineExitRecord();
            foreach (var (scale, p) in of3)
            {
                string h = p.Receive("runtool beyond", 180_000);
                Check($"OF3: at timeout scale {scale}, the tool runner returns at its drain bound, not when the child lets go", HeldForDrainBound(h), h);
            }
        }
        finally
        {
            foreach (var (_, p) in of3) p.Dispose();
        }
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

    /// <summary>Removes a scratch folder under TEMP. A program that ran from
    /// it a moment ago can stay open in another process for a while, such as
    /// an antivirus scan of a new executable, so the delete is tried again for
    /// up to 10 s, and a folder still held then is left with a note. Cleanup
    /// never fails a check.</summary>
    static void RemoveScratch(string dir, string part)
    {
        long until = Environment.TickCount64 + 10_000;
        while (true)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 < until) { System.Threading.Thread.Sleep(200); continue; }
                Console.WriteLine($"    {part}: {dir} not removed: {e.Message}");
                return;
            }
        }
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
        RemoveScratch(scratch, "OF5");
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
            RemoveScratch(dir, "OF6");
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
        // Made as the user runs without elevation, so the user owns it when
        // the probe runs elevated too, where a new folder can be owned by
        // Administrators and the refusal would name a grant instead.
        string made = CreateUnder(Native.MediumToken(), dir);
        if (made != "created") { Check("OF4: the user makes the folder", false, made); return; }
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
        finally { RemoveScratch(dir, "OF4"); }
    }

    static void OfflineDiagFiles()
    {
        var asm = typeof(HIDMaestro.HMContext).Assembly;
        var build = asm.GetType("HIDMaestro.Internal.Usbip.UsbipDriverInstaller")?.GetMethod("BuildForceDriverStartInfo", BindingFlags.NonPublic | BindingFlags.Static);
        var open = asm.GetType("HIDMaestro.Internal.DeviceOrchestrator")?.GetMethod("OpenUserLog", BindingFlags.NonPublic | BindingFlags.Static);
        if (build == null || open == null)
        {
            Check("OF10: UsbipDriverInstaller.BuildForceDriverStartInfo and DeviceOrchestrator.OpenUserLog exist", false);
            return;
        }
        // The host controller's driver install's ProcessStartInfo, built the
        // way RunForceDriver builds it, so dropping the confinement there
        // fails this. TEMP and TMP must be the helper's protected staging
        // folder, not the user's %TEMP%.
        string exe = Path.Combine(Path.GetTempPath(), "hmof10", "swd", "hmswd.exe");
        var psi = Call<System.Diagnostics.ProcessStartInfo>(build, new object[] { exe, Path.Combine(Path.GetTempPath(), "hmof10", "usbip-ude", "usbip2_ude.inf") });
        string want = Path.GetDirectoryName(exe)!;
        string userTemp = Path.GetTempPath().TrimEnd('\\');
        Check("OF10: the host controller install's TEMP and TMP are its helper's staging folder, not the user's %TEMP%",
              psi.Environment["TEMP"] == want && psi.Environment["TMP"] == want
              && !string.Equals(psi.Environment["TEMP"], userTemp, StringComparison.OrdinalIgnoreCase),
              $"TEMP={psi.Environment["TEMP"]}, TMP={psi.Environment["TMP"]}");

        // A process that is not elevated confines nothing when no folder is
        // given and does not even try to make the folder: ConfineChildTemp
        // checks elevation first, which ChildTempAttempts shows. And a failed
        // try is not remembered: two calls to ChildTempDir are two tries, each
        // failing on the Program Files create, not one try and a remembered
        // answer. Elevated, as S72 runs it, these checks do not apply.
        var ps = asm.GetType("HIDMaestro.Internal.ProtectedStaging");
        var confine = ps?.GetMethod("ConfineChildTemp", BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(System.Diagnostics.ProcessStartInfo), typeof(string) }, null);
        var confineAs = ps?.GetMethod("ConfineChildTemp", BindingFlags.NonPublic | BindingFlags.Static, null,
            new[] { typeof(System.Diagnostics.ProcessStartInfo), typeof(string), typeof(bool) }, null);
        var childTempDir = ps?.GetMethod("ChildTempDir", BindingFlags.NonPublic | BindingFlags.Static);
        var attempts = ps?.GetField("ChildTempAttempts", BindingFlags.NonPublic | BindingFlags.Static);
        if (confine == null || childTempDir == null || attempts == null)
            Check("OF10: ProtectedStaging.ConfineChildTemp, ChildTempDir and ChildTempAttempts exist", false);
        else if (!Environment.IsPrivilegedProcess)
        {
            var plain = new System.Diagnostics.ProcessStartInfo("pnputil.exe");
            string? before = plain.Environment["TEMP"];
            int tries = (int)attempts.GetValue(null)!;
            try { Call<object?>(confine, new object?[] { plain, null }); }
            catch (Exception e) { Console.WriteLine($"    ConfineChildTemp threw {e.GetType().Name}: {e.Message}"); }
            Check("OF10: without elevation, a child with no folder of its own keeps the inherited TEMP and nothing is tried",
                  plain.Environment["TEMP"] == before && (int)attempts.GetValue(null)! == tries,
                  $"TEMP={plain.Environment["TEMP"]}, tries {tries} -> {attempts.GetValue(null)}");
            var thrown = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                try { Call<string>(childTempDir, Array.Empty<object>()); thrown.Add("none"); }
                catch (Exception e) { thrown.Add(e.GetType().Name); }
            }
            Check("OF10: a failed try at the child-temp folder is not remembered, the next call tries again",
                  (int)attempts.GetValue(null)! == tries + 2 && !thrown.Contains("none"),
                  $"tries {tries} -> {attempts.GetValue(null)}, thrown {string.Join(", ", thrown)}");

            // Fail closed: called as an elevated process would be, where the
            // folder cannot be made (this one is not elevated, so the Program
            // Files create fails), ConfineChildTemp refuses the tool with the
            // reason rather than leave it on the user's TEMP.
            if (confineAs == null) Check("OF10: ProtectedStaging.ConfineChildTemp(psi, dir, elevated) exists", false);
            else
            {
                var tool = new System.Diagnostics.ProcessStartInfo("pnputil.exe");
                string? temp = tool.Environment["TEMP"];
                Exception? refused = null;
                try { Call<object?>(confineAs, new object?[] { tool, null, true }); }
                catch (Exception e) { refused = e; }
                Check("OF10: an elevated tool whose temporary folder cannot be made is refused, with the reason, and its TEMP is not set",
                      refused is InvalidOperationException && refused.GetType().Name == "ChildTempUnavailableException"
                      && refused.InnerException != null
                      && refused.Message.Contains("did not run pnputil.exe", StringComparison.Ordinal)
                      && tool.Environment["TEMP"] == temp,
                      refused == null ? "returned normally" : $"{refused.GetType().Name}: {refused.Message}");
            }
        }

        // The prune's keep-or-delete decision for a child-temp folder
        // (ChildTempWouldDelete). This process's own name, and its id with
        // its real start time, are kept. Its id with another start time, a
        // reused id, is deleted. A process that has exited is deleted. A name
        // that is not "<pid>-<ticks>" is kept.
        var wouldDelete = ps?.GetMethod("ChildTempWouldDelete", BindingFlags.NonPublic | BindingFlags.Static);
        var nameOf = ps?.GetMethod("ChildTempName", BindingFlags.NonPublic | BindingFlags.Static);
        if (wouldDelete == null || nameOf == null)
            Check("OF10: ProtectedStaging.ChildTempWouldDelete and ChildTempName exist", false);
        else
        {
            bool Del(string n) => Call<bool>(wouldDelete, new object[] { n });
            DateTime started;
            using (var self = System.Diagnostics.Process.GetCurrentProcess()) started = self.StartTime;
            string own = Call<string>(nameOf, new object[] { Environment.ProcessId, started });
            string reused = Call<string>(nameOf, new object[] { Environment.ProcessId, started.AddSeconds(-1) });
            var gone = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
            int gonePid = gone.Id;
            DateTime goneStart = gone.StartTime;
            gone.WaitForExit(10_000);
            gone.Dispose();
            string dead = Call<string>(nameOf, new object[] { gonePid, goneStart });
            Check("OF10: the prune keeps a running process's child-temp folder", !Del(own), own);
            Check("OF10: and deletes the folder of an earlier process given the same id", Del(reused), reused);
            Check("OF10: and deletes the folder of a process that has exited", Del(dead), dead);
            Check("OF10: and keeps a folder whose name is not <pid>-<ticks>", !Del("1234") && !Del("abc-1"));
        }

        // OpenUserLog creates the folder and the log and returns a usable
        // handle. Its point, impersonating the user, only runs when the
        // process is elevated (RunAsUser is a no-op otherwise), which this
        // offline run is not, so whether the file ends up owned by the user
        // rather than by an elevated token is a live check (S72, elevated),
        // not one this can fail. Here it confirms the log is created and
        // written, with its own folder made.
        string dir = Path.Combine(Path.GetTempPath(), "hmof10log" + Guid.NewGuid().ToString("N")[..8]);
        string log = Path.Combine(dir, "x.log");
        try
        {
            using (var fs = Call<FileStream>(open, new object[] { dir, log }))
            using (var w = new StreamWriter(fs)) w.WriteLine("x");
            Check("OF10: OpenUserLog creates its folder and an appendable log",
                  Directory.Exists(dir) && File.Exists(log) && new FileInfo(log).Length > 0, log);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
    static void OfflineListings()
    {
        var asm = typeof(HIDMaestro.HMContext).Assembly;
        var installer = asm.GetType("HIDMaestro.Internal.Usbip.UsbipDriverInstaller");
        var pnputil = asm.GetType("HIDMaestro.Internal.PnputilHelper");
        var recordType = pnputil?.GetNestedType("DriverRecord", BindingFlags.Public | BindingFlags.NonPublic);
        var refusalType = asm.GetType("HIDMaestro.Internal.ProtectedStaging+ChildTempUnavailableException");
        var listType = recordType == null ? null : typeof(List<>).MakeGenericType(recordType);
        var listerType = listType == null ? null : typeof(Func<>).MakeGenericType(listType);
        var isProduct = listerType == null ? null : installer?.GetMethod("UdePackageNames", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { listerType }, null);
        var removeAll = listerType == null ? null : pnputil?.GetMethod("RemoveAllHidMaestroPackages", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { listerType }, null);
        var installedFrom = listerType == null ? null : pnputil?.GetMethod("InstalledFrom", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { listerType }, null);
        var recordCtor = recordType?.GetConstructor(new[] { typeof(string), typeof(string), typeof(string), typeof(string) });
        var answer = typeof(HIDMaestro.HMContext).GetMethod("AnswerInstalled", BindingFlags.NonPublic | BindingFlags.Static);
        var refusalCtor = refusalType?.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(string), typeof(Exception) }, null);
        if (recordType == null || recordCtor == null || isProduct == null || removeAll == null || installedFrom == null || answer == null || refusalCtor == null)
        {
            Check("OF11: PnputilHelper.DriverRecord, UsbipDriverInstaller.UdePackageNames(list), RemoveAllHidMaestroPackages(list), InstalledFrom(list), HMContext.AnswerInstalled and the refusal's type exist", false);
            return;
        }

        // Stand-in listings: the packages of the given original names, or a
        // listing that fails the way PnputilHelper.ListPackages does, with an
        // InvalidOperationException that names why.
        object Listing(string provider, params string[] originals)
        {
            var list = (System.Collections.IList)Activator.CreateInstance(listType!)!;
            int n = 40;
            foreach (string original in originals)
                list.Add(recordCtor.Invoke(new object[] { $"oem{n++}.inf", original, provider, "01/01/2026 1.0.0.0" }));
            return typeof(Program).GetMethod(nameof(ListerOf), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(recordType).Invoke(null, new object[] { list })!;
        }
        object Failing(string reason) =>
            typeof(Program).GetMethod(nameof(FailingListerOf), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(recordType).Invoke(null, new object[] { reason })!;
        const string reason = @"C:\Windows\INF could not be read (test)";

        string Product(object list, out Exception? thrown)
        {
            thrown = null;
            try { return Call<List<string>>(isProduct, new[] { list }).Count > 0 ? "installed" : "not installed"; }
            catch (Exception e) { thrown = e; return $"{e.GetType().Name}: {e.Message}"; }
        }
        Exception? t;
        string r;
        r = Product(Failing(reason), out t);
        Check("OF11: the usbip-win2 check throws on a listing that fails, naming why, never \"not installed\"",
              t is InvalidOperationException && t.Message.Contains(reason.TrimEnd('.'), StringComparison.Ordinal)
              && t.Message.Contains("does not install it", StringComparison.Ordinal), r);
        r = Product(Listing("Someone"), out t);
        Check("OF11: a whole listing with no package answers \"not installed\"", t == null && r == "not installed", r);
        r = Product(Listing("Someone", "usbip2_ude.inf"), out t);
        Check("OF11: a whole listing that names usbip2_ude.inf answers \"installed\"", t == null && r == "installed", r);
        r = Product(Listing("Someone", "usbip2_filter.inf"), out t);
        Check("OF11: and one that names another package answers \"not installed\"", t == null && r == "not installed", r);

        Exception? removal = null;
        try { Call<object?>(removeAll, new[] { Failing(reason) }); }
        catch (Exception e) { removal = e; }
        Check("OF11: the HIDMaestro package removal throws on a failed listing, naming it, never reading it as nothing to remove",
              removal is InvalidOperationException && removal.Message.Contains("could not list", StringComparison.Ordinal)
              && removal.Message.Contains(reason, StringComparison.Ordinal),
              removal == null ? "returned normally" : $"{removal.GetType().Name}: {removal.Message}");

        // HIDMaestro's own installed check, past the folder shortcut: a
        // failed listing throws with its failure, so a deploy's post-add
        // check names it, and is never "not installed".
        string Installed(object list)
        {
            try { return Call<bool>(installedFrom, new[] { list }) ? "installed" : "not installed"; }
            catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; }
        }
        string iFailed = Installed(Failing(reason));
        Check("OF11: HIDMaestro's installed check throws on a failed listing and names it, never \"not installed\"",
              iFailed.StartsWith("InvalidOperationException", StringComparison.Ordinal) && iFailed.Contains("could not list", StringComparison.Ordinal)
              && iFailed.Contains(reason, StringComparison.Ordinal), iFailed);
        string iEmpty = Installed(Listing("HIDMaestro")), iOne = Installed(Listing("HIDMaestro", "hidmaestro.inf")),
               iBoth = Installed(Listing("HIDMaestro", "hidmaestro.inf", "hidmaestro_xusb.inf")),
               iOther = Installed(Listing("Someone", "hidmaestro.inf", "hidmaestro_xusb.inf"));
        Check("OF11: and answers from a whole listing: none or one package is \"not installed\", both are \"installed\", another provider's are not HIDMaestro's",
              iEmpty == "not installed" && iOne == "not installed" && iBoth == "installed" && iOther == "not installed",
              $"{iEmpty} | {iOne} | {iBoth} | {iOther}");

        string Answer(Func<bool> check)
        {
            try { return Call<bool>(answer, new object[] { check }).ToString(); }
            catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; }
        }
        var refusal = (Exception)refusalCtor.Invoke(new object[] { "HIDMaestro did not run pnputil.exe: test", new UnauthorizedAccessException("test") });
        var answers = new[]
        {
            Answer(() => throw refusal),
            Answer(() => throw new System.ComponentModel.Win32Exception(5)),
            Answer(() => throw new AggregateException(new IOException("pipe"))),
            Answer(() => throw new InvalidOperationException("listing")),
        };
        Check("OF11: IsDriverInstalled answers false, not a throw, when the driver store cannot be read",
              Array.TrueForAll(answers, a => a == "False"), string.Join(" | ", answers));
        Check("OF11: control: it answers true from a check that does", Answer(() => true) == "True");
    }

    /// <summary>A stand-in listing that gives <paramref name="list"/>, for
    /// OF11, built for the SDK's record type through reflection.</summary>
    static Func<List<T>> ListerOf<T>(List<T> list) => () => list;

    /// <summary>A stand-in listing that fails as the SDK's does.</summary>
    static Func<List<T>> FailingListerOf<T>(string reason) => () => throw new InvalidOperationException(reason);

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
