// Section E's creates, under decision (c) as the owner settled it: "the V2
// controllers take precedence over and supersede the V1. Period." E1 to E4
// reach the install policy through InstallDriver. A program need not call
// it: its first create reaches the same policy through the create's deploy
// gate (DriverDeploy.EnsureForCreate). Here the new program never calls
// InstallDriver.
//
//   E6. A v1.12.0 program's controller runs on its own driver. The new
//       program's creates install this release's driver beside it, and its
//       controllers run on it and work: a generic pad's input arrives, a
//       dualsense-bt answers the calibration read SDL makes first (feature
//       0x05), and an xbox-360-wired gets its XUSB interface on this
//       release's companion. The v1.12.0 controller keeps its device and
//       driver.
//   E7. The v1.12.0 program has gone and taken its controllers, its driver
//       still installed. The same creates upgrade the driver in full: they
//       work as in E6, and the driver store keeps no older package of
//       either INF.
//
// Each run prints the WUDF service's ImagePath before and after the first
// create, which the design note asks the first live run to record (decision
// (c), "What a v1 program's running controllers then do").
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

internal static partial class Program
{
    static void SectionECreate()
    {
        string? mainVer = EmbeddedDriverVer("hidmaestro.inf"), xusbVer = EmbeddedDriverVer("hidmaestro_xusb.inf");
        if (mainVer == null || xusbVer == null)
        {
            Check("E6: the SDK embeds both INFs' DriverVer", false, $"hidmaestro.inf {mainVer ?? "none"}, hidmaestro_xusb.inf {xusbVer ?? "none"}");
            return;
        }

        // E6. Beside a running v1.12.0 program's controller.
        Uninstall();
        using (var old = PeerProcess.Start("E6-v1.12.0", "v1.12.0"))
        {
            old.Send("install");
            string co = old.Send("create pad F0C1 coexist-e6-old");
            if (!Ok(co)) { Check("E6: the v1.12.0 program creates", false, co); return; }
            old.Send("stream 5 0.2 125 0");
            string instOld = PeerProcess.Field(co, "instance")!;
            string? verOld = Native.DriverVersionOf(instOld);
            Check("E6: the v1.12.0 program's controller runs its own driver, not this release's", verOld != null && verOld != mainVer,
                  $"{verOld} (this release {mainVer})");
            var pathOld = WaitPaths(0xBEEF, 0xF0C1, 1, 10_000);
            using var capOld = pathOld.Count == 1 ? HidCapture.Open(pathOld[0]) : null;
            if (capOld == null) { Check("E6: the v1.12.0 program's device opens", false); return; }
            var treeOld = Native.Subtree(instOld);

            Console.WriteLine($"  E6: the WUDF service's ImagePath before the new program's first create: {WudfImagePath()}");
            using (var nu = PeerProcess.Start("E6-new"))
            {
                NewControllersWork(nu, "E6", 0xF0C2, mainVer, xusbVer);
                Console.WriteLine($"  E6: the WUDF service's ImagePath after it: {WudfImagePath()}");
                Check("E6: the v1.12.0 program's controller is untouched, driver and all",
                      !capOld.Broken && Native.Subtree(instOld).SequenceEqual(treeOld, StringComparer.OrdinalIgnoreCase)
                      && Native.DriverVersionOf(instOld) == verOld && Arrives(capOld),
                      capOld.Broken ? $"read error {capOld.ReadError}" : $"driver {Native.DriverVersionOf(instOld)}");
                nu.Send("sweep");
            }
        }

        // E7. The v1.12.0 program gone with its controllers, its driver left.
        Uninstall();
        string? verLeft = null;
        using (var old = PeerProcess.Start("E7-v1.12.0", "v1.12.0"))
        {
            old.Send("install");
            string co = old.Send("create pad F0C3 coexist-e7-old");
            if (!Ok(co)) { Check("E7: the v1.12.0 program creates", false, co); return; }
            string inst = PeerProcess.Field(co, "instance")!;
            verLeft = Native.DriverVersionOf(inst);
            old.Send("sweep");
            WaitFor(() => Native.IsGone(inst), 10_000);
            Check("E7: the v1.12.0 program's controller is gone and its driver stays installed",
                  Native.IsGone(inst) && verLeft != null && StoreVersions("hidmaestro.inf").Contains(verLeft),
                  $"driver {verLeft}, the store holds {string.Join(", ", StoreVersions("hidmaestro.inf"))}");
        }
        Console.WriteLine($"  E7: the WUDF service's ImagePath before the new program's first create: {WudfImagePath()}");
        using (var nu = PeerProcess.Start("E7-new"))
        {
            NewControllersWork(nu, "E7", 0xF0C4, mainVer, xusbVer);
            Console.WriteLine($"  E7: the WUDF service's ImagePath after it: {WudfImagePath()}");
            foreach (var (inf, ver) in new[] { ("hidmaestro.inf", mainVer), ("hidmaestro_xusb.inf", xusbVer) })
            {
                var store = StoreVersions(inf);
                Check($"E7: the upgrade left only this release's {inf} in the driver store", store.Count > 0 && store.All(v => v == ver),
                      string.Join(", ", store));
            }
            nu.Send("sweep");
        }
    }

    /// <summary>A program on this release creates a generic pad, a
    /// dualsense-bt and an xbox-360-wired with no InstallDriver call, and
    /// each runs on this release's driver and works.</summary>
    static void NewControllersWork(PeerProcess nu, string tag, ushort padPid, string mainVer, string xusbVer)
    {
        string cn = nu.Send($"create pad {padPid:X4} coexist-{tag.ToLowerInvariant()}-new");
        Check($"{tag}: the new program creates a generic pad without calling InstallDriver", Ok(cn), cn);
        if (Ok(cn))
        {
            string inst = PeerProcess.Field(cn, "instance")!;
            Check($"{tag}: the pad runs this release's driver", Native.DriverVersionOf(inst) == mainVer, $"{Native.DriverVersionOf(inst)} (this release {mainVer})");
            nu.Send($"stream 6 0.8 125 0");
            var path = WaitPaths(0xBEEF, padPid, 1, 10_000);
            using var cap = path.Count == 1 ? HidCapture.Open(path[0]) : null;
            Check($"{tag}: the pad's input arrives", cap != null && Arrives(cap));
        }

        var hidBefore = new HashSet<string>(Native.InterfaceList(Native.HidGuid, null), StringComparer.OrdinalIgnoreCase);
        string cs = nu.Send($"create profile dualsense-bt coexist-{tag.ToLowerInvariant()}-sony");
        Check($"{tag}: and a dualsense-bt", Ok(cs), cs);
        if (Ok(cs))
        {
            string inst = PeerProcess.Field(cs, "instance")!;
            int transport = int.Parse(PeerProcess.Field(cs, "transport")!, System.Globalization.CultureInfo.InvariantCulture);
            Check($"{tag}: the dualsense-bt runs this release's driver", Native.DriverVersionOf(inst) == mainVer, $"{Native.DriverVersionOf(inst)}");
            List<string> hid = new();
            WaitFor(() => (hid = OwnedHid(hidBefore, inst, transport)).Count > 0, 15_000);
            byte[]? descriptor = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\HIDMaestro\Controller{transport}")?.GetValue("ReportDescriptor") as byte[];
            var declared = descriptor == null ? new SortedDictionary<byte, int>() : HidFeatureLayout.Lengths(descriptor);
            bool answered = false;
            string detail = hid.Count == 0 ? "no HID interface" : !declared.ContainsKey(0x05) ? "feature 0x05 not declared" : "";
            if (hid.Count > 0 && declared.TryGetValue(0x05, out int length))
            {
                using var h = HidIo.OpenSync(hid[0]);
                var buf = new byte[length];
                buf[0] = 0x05;
                answered = !h.IsInvalid && HidIo.HidD_GetFeature(h, buf, length);
                detail = answered ? $"{length} bytes, {Convert.ToHexString(buf.AsSpan(0, Math.Min(8, length)))}..." : $"error {Marshal.GetLastWin32Error()}";
            }
            Check($"{tag}: the dualsense-bt answers SDL's calibration read, feature 0x05", answered, detail);
        }

        var xusbBefore = new HashSet<string>(Native.InterfaceList(XusbGuid, null), StringComparer.OrdinalIgnoreCase);
        string cx = nu.Send($"create profile xbox-360-wired coexist-{tag.ToLowerInvariant()}-xusb");
        Check($"{tag}: and an xbox-360-wired", Ok(cx), cx);
        if (Ok(cx))
        {
            int transport = int.Parse(PeerProcess.Field(cx, "transport")!, System.Globalization.CultureInfo.InvariantCulture);
            string? xusb = null;
            // A hang bound: the companion's install can run long on a busy
            // machine, and the wait ends as soon as the interface is there.
            WaitFor(() => (xusb = OwnedXusb(xusbBefore, transport)) != null, 60_000);
            string? companion = xusb == null ? null : Native.InstanceOfInterface(xusb);
            Check($"{tag}: the xbox-360-wired's XUSB interface is present, on this release's companion",
                  companion != null && Native.DriverVersionOf(companion) == xusbVer,
                  companion == null ? "no XUSB interface within 60 s" : $"{companion} runs {Native.DriverVersionOf(companion)} (this release {xusbVer})");
        }
    }

    /// <summary>The DriverVer of each package of an INF in the driver
    /// store, read from the INF in its FileRepository folder.</summary>
    static List<string> StoreVersions(string inf)
    {
        var versions = new List<string>();
        foreach (string dir in Directory.GetDirectories(FileRepository, inf + "_*"))
        {
            string file = Path.Combine(dir, inf);
            if (!File.Exists(file)) continue;
            var m = Regex.Match(File.ReadAllText(file), @"(?m)^\s*DriverVer\s*=\s*[^,\r\n]+,\s*([0-9.]+)");
            if (m.Success) versions.Add(m.Groups[1].Value);
        }
        return versions;
    }

    /// <summary>The binary UMDF loads for the HIDMaestro service: one
    /// package's, whichever the last install pointed it at.</summary>
    static string WudfImagePath() =>
        Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\HIDMaestro")
            ?.GetValue("ImagePath") as string ?? "(not set)";
}
