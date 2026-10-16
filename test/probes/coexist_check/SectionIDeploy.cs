// I, continued. Staging a deploy uses, staging another build reaches while
// a deploy prunes, and a %ProgramData%\HIDMaestro that a program that is not
// elevated made first.
//
//   I8   A running program's staged signtool manifest removed and another
//        staged tool file changed to bytes of the same length. Its next
//        deploy restores both from the embedded bytes before it runs the
//        tools. Until v1.13.0 the staging directory was trusted for the
//        life of the process and only signtool.exe and Inf2Cat.exe were
//        checked again.
//   I9   A deploy's prune paused after it read the owner records, and a
//        program of another build reaching its staging directory during the
//        pause. Its staging gives no reply while the prune holds the staging
//        lock, replies once the prune is done, and its directory then holds
//        its payload. The other build is this SDK with another payload hash.
//   I10  %ProgramData%\HIDMaestro, which holds the OpenVR driver, as an
//        upgrade finds it: created by an elevated program, so owned by
//        Administrators with the DACL it inherits, and the OpenVR driver's
//        file held open as SteamVR holds it. The OpenVR folders' repair
//        resets it where it is, and nothing is moved. A root a program that
//        is not elevated created (Native.MediumToken) is moved aside, and
//        the OpenVR path is made under the new, protected root. The same
//        root with a file under it held open cannot move, and the OpenVR
//        repair is refused rather than reset. In that state, the squat a
//        program without elevation can make, the driver payload and
//        hmswd.exe still stage, under %ProgramFiles%\HIDMaestro\Staging, and
//        the squatted root is left as it was.
//   I11  The OpenVR driver's folder present and its files gone: a
//        registration extracts them again at the path SteamVR has
//        registered. Skipped where SteamVR is not installed.
//   I12  While a program holds hmswd.exe the way a device create holds it
//        for the helper's run (SwdDeviceFactory.PinHelper), the helper
//        cannot be written or renamed, and its directory, the build's
//        directory above it, Staging and the folder above Staging cannot be
//        renamed,
//        even by an elevated process. Until v1.13.0 the helper was checked
//        and then run with nothing held.
//
// I10 and I11 set the machine's %ProgramData%\HIDMaestro aside first and put
// it back at the end, with the OpenVR install record as it was.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

internal static partial class Program
{
    static void SectionIDeploy()
    {
        SectionI8();
        Clean();
        SectionI9();
        Clean();
        SectionI10();
        SectionI12();
    }

    static void SectionI12()
    {
        using var p = PeerProcess.Start("I12");
        string r = p.Send("swdpin");
        Check("I12: while the SWD helper is held for its run, its directory cannot be renamed",
              r.StartsWith("OK ", StringComparison.Ordinal) && r.Contains(" leaf=refused ", StringComparison.Ordinal), r);
        Check("I12: nor the build's staging directory above it",
              r.StartsWith("OK ", StringComparison.Ordinal) && r.Contains(" version=refused ", StringComparison.Ordinal), r);
        Check("I12: nor Staging",
              r.StartsWith("OK ", StringComparison.Ordinal) && r.Contains(" staging=refused ", StringComparison.Ordinal), r);
        Check("I12: nor the folder above Staging",
              r.StartsWith("OK ", StringComparison.Ordinal) && r.Contains(" root=refused ", StringComparison.Ordinal), r);
        Check("I12: and hmswd.exe itself cannot be opened for writing or renamed",
              r.StartsWith("OK ", StringComparison.Ordinal) && r.Contains(" write=refused ", StringComparison.Ordinal)
              && r.Contains(" file=refused ", StringComparison.Ordinal), r);
        string? path = StagingPath(r);
        Check("I12: and hmswd.exe is still in place once the hold is released", path != null && File.Exists(path), r);
    }

    static string NativePrefix =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "HIDMaestro.Native.arm64." : "HIDMaestro.Native.x64.";

    static void SectionI8()
    {
        using var p = PeerProcess.Start("I8");
        p.Send("install");
        string? dir = StagingPath(p.Send("stagingdir"));
        if (dir == null) { Check("I8: the driver payload stages", false); return; }
        string manifest = Path.Combine(dir, "signtool.exe.manifest");
        string ini = Path.Combine(dir, "wintrust.dll.ini");
        byte[]? manifestGood = ReadEmbedded(NativePrefix + "signtool.exe.manifest");
        byte[]? iniGood = ReadEmbedded(NativePrefix + "wintrust.dll.ini");
        if (manifestGood == null || iniGood == null) { Check("I8: the embedded tool files are readable", false); return; }
        try
        {
            File.Delete(manifest);
            // Plain text of the same length, so nothing that reads the file
            // can fail on it in a way that shows a dialog.
            byte[] changed = Enumerable.Repeat((byte)'x', iniGood.Length).ToArray();
            File.WriteAllBytes(ini, changed);
            Check("I8: a staged manifest is removed and a staged file changed to bytes of the same length",
                  !File.Exists(manifest) && new FileInfo(ini).Length == iniGood.Length && !File.ReadAllBytes(ini).SequenceEqual(iniGood));
            using (var other = PeerProcess.Start("I8-other"))
                other.Send("uninstall");
            string r = p.Send("install");
            Check("I8: the program's next deploy runs", PeerProcess.Field(r, "outcome") == "Installed", r);
            Check("I8: the deploy restored the removed manifest from the embedded bytes",
                  File.Exists(manifest) && File.ReadAllBytes(manifest).SequenceEqual(manifestGood), manifest);
            Check("I8: and the changed file", File.ReadAllBytes(ini).SequenceEqual(iniGood), ini);
        }
        finally
        {
            // An SDK that does not restore them leaves the tool tree as this
            // section changed it. Put the embedded bytes back.
            try { if (!File.Exists(manifest) || !File.ReadAllBytes(manifest).SequenceEqual(manifestGood)) File.WriteAllBytes(manifest, manifestGood); } catch { }
            try { if (!File.ReadAllBytes(ini).SequenceEqual(iniGood)) File.WriteAllBytes(ini, iniGood); } catch { }
        }
    }

    static void SectionI9()
    {
        // Another build: the same SDK with another payload hash, so its
        // staging directory is <version>-0123456789abcdef.
        const string fakeHash = "0123456789abcdef000000000000000000000000000000000000000000000000";
        string? first;
        using (var earlier = PeerProcess.Start("I9-other-build"))
        {
            earlier.Send("fakebuild " + fakeHash);
            first = StagingPath(earlier.Send("stagingdir"));
        }
        string? versionDir = first == null ? null : Path.GetDirectoryName(first);
        Check("I9: a program of another build left its staging directory from an earlier run",
              first != null && Directory.Exists(first) && Path.GetFileName(versionDir)!.EndsWith("-0123456789abcdef", StringComparison.Ordinal),
              first ?? "nothing staged");
        if (first == null || versionDir == null) return;
        try
        {
            using var deploy = PeerProcess.Start("I9-deploy");
            string hook = deploy.Send("pauseprune");
            if (!Ok(hook)) { Check("I9: the deploy's prune can be paused", false, hook); return; }
            Native.DropInstalledManifest();
            deploy.Post("install");
            string paused = deploy.Receive("prune");
            Check("I9: a deploy's prune pauses after reading the owner records", paused.StartsWith("OK paused", StringComparison.Ordinal), paused);
            if (!paused.StartsWith("OK paused", StringComparison.Ordinal)) return;

            using var again = PeerProcess.Start("I9-other-build-again");
            again.Send("fakebuild " + fakeHash);
            again.Post("stagingdir");
            // While the prune is paused the other program's staging note
            // must wait for the staging lock, so it gives no reply yet.
            string early = again.Receive("stagingdir while the prune is paused", 3000);
            Check("I9: the other build's staging waits while the prune holds the staging lock",
                  early.StartsWith("ERR timeout", StringComparison.Ordinal), early);
            deploy.Post("go");
            string ri = deploy.Receive("install");
            string rs = early.StartsWith("ERR timeout", StringComparison.Ordinal) ? again.Receive("stagingdir") : early;
            Check("I9: the deploy finishes", Ok(ri), ri);
            string? dir = StagingPath(rs);
            string[] want = { "signtool.exe", "wintrust.dll", "Inf2Cat.exe", "Microsoft.UniversalStore.HardwareWorkflow.Catalogs.dll" };
            string[] missing = dir == null ? want : want.Where(f => !File.Exists(Path.Combine(dir, f))).ToArray();
            Check("I9: the staging the other build's program reached during the prune holds its payload afterward",
                  Ok(rs) && missing.Length == 0, $"{rs}; missing: {(missing.Length == 0 ? "none" : string.Join(", ", missing))}");
        }
        finally
        {
            // The other build's directory exists only for this section.
            try { if (Directory.Exists(versionDir)) Directory.Delete(versionDir, true); } catch { }
        }
    }

    static string? OpenVrRecord()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestro");
        return k?.GetValue("InstalledVrManifestSha256") as string;
    }

    /// <summary>Create a directory tree on a thread that runs under the given
    /// impersonation token, so the directory gets that token's default owner.
    /// The token is closed here.</summary>
    static string CreateUnder(IntPtr token, string dir)
    {
        string d = "";
        var t = new Thread(() =>
        {
            if (!Native.SetThreadToken(IntPtr.Zero, token)) { d = $"SetThreadToken error {Marshal.GetLastWin32Error()}"; return; }
            try { Directory.CreateDirectory(dir); d = "created"; }
            catch (Exception e) { d = $"{e.GetType().Name}: {e.Message}"; }
            finally { Native.RevertToSelf(); }
        });
        t.Start();
        t.Join();
        Native.CloseHandle(token);
        return d;
    }

    static void SectionI10()
    {
        string root = ProgramDataRoot;
        string programData = Path.GetDirectoryName(root)!;
        string saved = root + ".coexist-saved-" + Guid.NewGuid().ToString("N");
        var asideBefore = Directory.GetDirectories(programData, "HIDMaestro.untrusted-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? vrRecord = OpenVrRecord();
        bool setAside = false;
        try
        {
            try { Directory.Move(root, saved); setAside = true; }
            catch (Exception e) { Check("I10: the machine's %ProgramData%\\HIDMaestro is set aside for the section", false, e.Message); return; }

            // I10a. The upgrade case: a root an elevated program created, so
            // owned by Administrators with the DACL it inherits from
            // %ProgramData%, and the OpenVR driver's file held open under it
            // as SteamVR holds it.
            string dllDir = Path.Combine(root, "openvr", "hidmaestro", "bin", "win64");
            Directory.CreateDirectory(dllDir);
            string? why = Native.WhyWritableByOthers(root);
            Check("I10: an elevated program's %ProgramData%\\HIDMaestro is owned by Administrators and not protected",
                  why != null && !why.StartsWith("owner", StringComparison.Ordinal), why ?? "protected");
            string dll = Path.Combine(dllDir, "driver_hidmaestro.dll");
            File.WriteAllBytes(dll, new byte[] { 0x4D, 0x5A });
            using (var loaded = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var p = PeerProcess.Start("I10a"))
            {
                string r = p.Send(@"shareddir openvr\hidmaestro\bin\win64");
                Check("I10: with a file under it held open, the OpenVR path is repaired", Ok(r), r);
                Check("I10: the root is reset where it is, Administrators/SYSTEM-write-only",
                      Native.WhyWritableByOthers(root) == null, Native.WhyWritableByOthers(root) ?? "");
                Check("I10: the held file is still at its path", File.Exists(dll) && loaded.CanRead, dll);
            }
            Check("I10: nothing was moved aside", Directory.GetDirectories(programData, "HIDMaestro.untrusted-*").All(asideBefore.Contains),
                  string.Join(", ", Directory.GetDirectories(programData, "HIDMaestro.untrusted-*")));

            // I11. The OpenVR driver's folder present, its files gone.
            Directory.Delete(Path.Combine(root, "openvr"), true);
            SectionI11(root);

            // I10b. A root a program that is not elevated created: moved
            // aside, and the OpenVR path made under the new, protected root.
            Directory.Delete(root, true);
            string made = CreateUnder(Native.MediumToken(), root);
            why = Native.WhyWritableByOthers(root);
            Check("I10: a program that is not elevated creates %ProgramData%\\HIDMaestro, owned by its user",
                  made == "created" && why != null && why.StartsWith("owner", StringComparison.Ordinal), $"{made}; {why ?? "protected"}");
            using (var p = PeerProcess.Start("I10b"))
            {
                string r = p.Send(@"shareddir openvr\hidmaestro");
                string? vr = Ok(r) ? StagingPath(r) : null;
                var asides = Directory.GetDirectories(programData, "HIDMaestro.untrusted-*").Where(a => !asideBefore.Contains(a)).ToList();
                Check("I10: that root is moved aside, not reset where it is", asides.Count == 1, string.Join(", ", asides));
                Check("I10: the new root is Administrators/SYSTEM-write-only",
                      Native.WhyWritableByOthers(root) == null, Native.WhyWritableByOthers(root) ?? "");
                Check("I10: and the OpenVR path is made under it, protected",
                      vr != null && vr.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                      && Directory.Exists(vr) && Native.WhyWritableByOthers(vr) == null, r);
            }

            // I10c. The same root with a file under it held open, so it
            // cannot be moved: refused, and left as it was.
            Directory.Delete(root, true);
            made = CreateUnder(Native.MediumToken(), Path.Combine(root, "held"));
            string heldFile = Path.Combine(root, "held", "file");
            File.WriteAllText(heldFile, "held");
            using (var holding = new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var p = PeerProcess.Start("I10c"))
            {
                string r = p.Send(@"shareddir openvr\hidmaestro");
                string? whyAfter = Native.WhyWritableByOthers(root);
                Check("I10: a root its user owns that cannot be moved is refused for the OpenVR path, not reset where it is",
                      !Ok(r) && r.Contains(root, StringComparison.OrdinalIgnoreCase) && whyAfter != null && whyAfter.StartsWith("owner", StringComparison.Ordinal),
                      $"{made}; {r}; {whyAfter ?? "protected"}");

                // I10d. The same squat does not reach staging.
                string s = p.Send("stagingdir");
                string? staged = Ok(s) ? StagingPath(s) : null;
                Check("I10: with the root squatted, the driver payload still stages, under %ProgramFiles%\\HIDMaestro\\Staging",
                      staged != null && staged.StartsWith(StagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), s);
                string h = p.Send("swdhelper");
                string? swd = Ok(h) ? StagingPath(h) : null;
                Check("I10: and so does hmswd.exe",
                      swd != null && swd.StartsWith(StagingRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && File.Exists(swd), h);
                whyAfter = Native.WhyWritableByOthers(root);
                Check("I10: and the squatted root is left as it was",
                      File.Exists(heldFile) && whyAfter != null && whyAfter.StartsWith("owner", StringComparison.Ordinal), whyAfter ?? "protected");
            }
        }
        finally
        {
            if (setAside)
            {
                // Everything under these was made in this section, by the
                // impersonating thread, this probe or the SDK under test.
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception e) { Console.WriteLine($"    could not remove the section's root: {e.Message}"); }
                foreach (string aside in Directory.GetDirectories(programData, "HIDMaestro.untrusted-*"))
                    if (!asideBefore.Contains(aside))
                        try { Directory.Delete(aside, true); } catch (Exception e) { Console.WriteLine($"    could not remove {aside}: {e.Message}"); }
                try { Directory.Move(saved, root); }
                catch (Exception e) { Check("I10: the machine's %ProgramData%\\HIDMaestro is put back", false, $"{saved}: {e.Message}"); }
                using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\HIDMaestro");
                if (vrRecord == null) k.DeleteValue("InstalledVrManifestSha256", throwOnMissingValue: false);
                else k.SetValue("InstalledVrManifestSha256", vrRecord, RegistryValueKind.String);
            }
        }
    }

    static void SectionI11(string root)
    {
        string ext = Path.Combine(root, "openvr", "hidmaestro");
        using var p = PeerProcess.Start("I11");
        string first = p.Send("vrregister");
        if (first == "OK vrregister False")
        {
            Console.WriteLine("  [SKIP] I11: SteamVR is not installed, so nothing registers the OpenVR driver");
            return;
        }
        string manifest = Path.Combine(ext, "driver.vrdrivermanifest");
        string dll = Path.Combine(ext, "bin", "win64", "driver_hidmaestro.dll");
        Check("I11: a registration extracts the OpenVR driver under %ProgramData%\\HIDMaestro",
              Ok(first) && File.Exists(manifest) && File.Exists(dll), first);
        if (!File.Exists(manifest)) return;
        // Files the SDK under test extracted in this section.
        foreach (string f in Directory.GetFiles(ext, "*", SearchOption.AllDirectories)) File.Delete(f);
        string again = p.Send("vrregister");
        Check("I11: with the folder left and its files gone, the next registration extracts them again at the registered path",
              Ok(again) && File.Exists(manifest) && File.Exists(dll), again);
    }
}
