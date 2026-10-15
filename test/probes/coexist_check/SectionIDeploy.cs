// I, continued. Staging a deploy uses, and staging another build reaches
// while a deploy prunes.
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
}
