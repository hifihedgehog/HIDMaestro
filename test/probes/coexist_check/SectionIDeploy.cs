// I, continued. Staging a deploy uses.
//
//   I8   A running program's staged signtool manifest removed and another
//        staged tool file changed to bytes of the same length. Its next
//        deploy restores both from the embedded bytes before it runs the
//        tools. Until v1.13.0 the staging directory was trusted for the
//        life of the process and only signtool.exe and Inf2Cat.exe were
//        checked again.
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
}
