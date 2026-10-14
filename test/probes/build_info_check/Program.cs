// What a build of HIDMaestro.Core carries, read back from the built
// assembly, and what it refuses when a payload is missing.
//
// "complete": the build every release ships. It carries the driver and the
// USB/IP transport for x64 and ARM64 and the OpenVR driver, and its Inf2Cat
// set holds only the six files Inf2Cat loads.
//
// "variant": the copy of this probe the project build puts in variant\,
// beside an SDK built with HMSkipArm64 and HMSkipUsbip from a tree whose
// build\ held no ARM64 driver and no OpenVR driver, and whose Resources\
// cache still held ARM64 and USB/IP files from a full build. It must carry
// the x64 driver only, nothing stale, and refuse what it lacks before any
// machine-wide step. It still creates a UMDF2 controller on x64.
//
// "complete" needs no device and no elevation. "variant" requires
// elevation for its device check, as the battery runs it.
//
// Exit 0 PASS, 1 FAIL, 2 when another program that embeds HIDMaestro is
// running, which the variant's device check would share devices with.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

    static string List(IEnumerable<Architecture> a) => "[" + string.Join(", ", a) + "]";

    static string[] ResourceNames() =>
        typeof(HMContext).Assembly.GetManifestResourceNames().OrderBy(n => n, StringComparer.Ordinal).ToArray();

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "complete";
        Console.WriteLine($"=== What this HIDMaestro.Core build carries ({mode}) ===");
        Console.WriteLine($"  SDK: {typeof(HMContext).Assembly.Location}");
        Console.WriteLine($"  DriverArchitectures {List(HMBuildInfo.DriverArchitectures)}, " +
                          $"UsbipArchitectures {List(HMBuildInfo.UsbipArchitectures)}, " +
                          $"HasOpenVrDriver {HMBuildInfo.HasOpenVrDriver}, IsComplete {HMBuildInfo.IsComplete}");

        int rc = mode switch
        {
            "complete" => Complete(),
            "variant" => Variant(),
            _ => Usage(),
        };
        if (rc != 0) return rc;

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static int Usage()
    {
        Console.WriteLine("  usage: BuildInfoCheck complete | variant");
        return 1;
    }

    // The six files of Inf2Cat's set that Inf2Cat loads or reads.
    static readonly string[] Inf2CatSet =
    {
        "HIDMaestro.Resources.Inf2Cat.exe",
        "HIDMaestro.Resources.Microsoft.UniversalStore.HardwareWorkflow.Cabinets.dll",
        "HIDMaestro.Resources.Microsoft.UniversalStore.HardwareWorkflow.Catalogs.dll",
        "HIDMaestro.Resources.Microsoft.UniversalStore.HardwareWorkflow.InfReader.dll",
        "HIDMaestro.Resources.Microsoft.UniversalStore.HardwareWorkflow.SubmissionBuilder.dll",
        "HIDMaestro.Resources.WindowsProtectedFiles.xml",
    };

    static int Complete()
    {
        var both = new[] { Architecture.X64, Architecture.Arm64 };
        Check("the driver is carried for x64 and ARM64",
              HMBuildInfo.DriverArchitectures.OrderBy(a => a).SequenceEqual(both.OrderBy(a => a)),
              List(HMBuildInfo.DriverArchitectures));
        Check("the USB/IP transport is carried for x64 and ARM64",
              HMBuildInfo.UsbipArchitectures.OrderBy(a => a).SequenceEqual(both.OrderBy(a => a)),
              List(HMBuildInfo.UsbipArchitectures));
        Check("the OpenVR driver is carried", HMBuildInfo.HasOpenVrDriver);
        Check("the build reports itself complete", HMBuildInfo.IsComplete);

        string[] names = ResourceNames();
        string[] inf2cat = names.Where(n =>
                n.StartsWith("HIDMaestro.Resources.", StringComparison.Ordinal) &&
                (n.Contains("Inf2Cat", StringComparison.OrdinalIgnoreCase) ||
                 n.Contains("UniversalStore", StringComparison.Ordinal) ||
                 n.Contains("WindowsProtectedFiles", StringComparison.Ordinal) ||
                 n.Contains("Kits.Logger", StringComparison.Ordinal)))
            .ToArray();
        Check("the Inf2Cat set is the six files Inf2Cat loads",
              inf2cat.SequenceEqual(Inf2CatSet),
              string.Join(", ", inf2cat.Select(n => n["HIDMaestro.Resources.".Length..])));
        return 0;
    }

    static int Variant()
    {
        Check("the driver is carried for x64 only",
              HMBuildInfo.DriverArchitectures.SequenceEqual(new[] { Architecture.X64 }),
              List(HMBuildInfo.DriverArchitectures));
        Check("no USB/IP transport is carried", HMBuildInfo.UsbipArchitectures.Count == 0,
              List(HMBuildInfo.UsbipArchitectures));
        Check("no OpenVR driver is carried", !HMBuildInfo.HasOpenVrDriver);
        Check("the build does not report itself complete", !HMBuildInfo.IsComplete);

        string[] stale = ResourceNames().Where(n =>
                n.StartsWith("HIDMaestro.Native.arm64.", StringComparison.Ordinal) ||
                n.StartsWith("HIDMaestro.Usbip.", StringComparison.Ordinal) ||
                n.StartsWith("HIDMaestro.Resources.USBip-", StringComparison.Ordinal) ||
                n.StartsWith("HIDMaestro.VR.", StringComparison.Ordinal))
            .ToArray();
        Check("nothing from the Resources cache's earlier full build was embedded", stale.Length == 0,
              stale.Length == 0 ? "" : string.Join(", ", stale.Take(6)));

        Check("IsUsbipBackendAvailable is false without the transport", !HMContext.IsUsbipBackendAvailable);

        Architecture os = RuntimeInformation.OSArchitecture;
        var refused = new List<string>();
        void ExpectMissing(string what, HMPayload payload, Architecture? arch, Action act)
        {
            try
            {
                act();
                Check($"{what} is refused", false, "no exception");
            }
            catch (HMPayloadMissingException e)
            {
                bool ok = e.Payload == payload && e.Architecture == arch && e is InvalidOperationException;
                Check($"{what} is refused with HMPayloadMissingException({payload}, {(arch?.ToString() ?? "no architecture")})",
                      ok, $"got ({e.Payload}, {(e.Architecture?.ToString() ?? "no architecture")}): {e.Message}");
            }
            catch (Exception e)
            {
                Check($"{what} is refused", false, $"{e.GetType().Name}: {e.Message}");
            }
        }

        if (OtherConsumerRunning(out string? other))
        {
            Console.WriteLine($"  [SKIP] {other} is running and shares the SDK's devices.");
            return 2;
        }

        using (var ctx = new HMContext())
        {
            ctx.LoadDefaultProfiles();
            // The program's first machine-wide write takes its ownership
            // claim and writes its record, as the constructor's staging of
            // the driver does when a deploy is coming. That write goes
            // first here, so the snapshot holds what any running program
            // holds, and the refusals below must add nothing to it.
            try { HIDMaestro.Internal.DriverBuilder.EnsureExtracted(); }
            catch (HMPayloadMissingException) { }
            var before = ClaimSnapshot();
            var composite = ctx.GetProfile("dualsense-composite")!;
            ExpectMissing("a composite persona's create", HMPayload.UsbipTransport, os,
                          () => ctx.CreateController(composite).Dispose());
            ExpectMissing("InstallUsbipBackend", HMPayload.UsbipTransport, os,
                          () => HMContext.InstallUsbipBackend());
            ExpectMissing("HMVR.EnsureDriverRegistered", HMPayload.OpenVrDriver, null,
                          () => HMVR.EnsureDriverRegistered());
            ExpectMissing("the driver for ARM64", HMPayload.Driver, Architecture.Arm64,
                          () => HMBuildInfo.ThrowIfMissing(HMPayload.Driver, Architecture.Arm64));
            var after = ClaimSnapshot();
            Check("the refusals took no machine-wide claim and wrote nothing to the owner record",
                  after.SetEquals(before),
                  after.SetEquals(before) ? "" : "new: " + string.Join(", ", after.Except(before)));

            if (os == Architecture.Arm64)
            {
                ExpectMissing("a UMDF2 controller's create on ARM64", HMPayload.Driver, Architecture.Arm64,
                              () => ctx.CreateController(ctx.GetProfile("dualshock-4-v2")!).Dispose());
            }
            else
            {
                // The positive control: what this build does carry works.
                string detail = "";
                bool ok;
                try
                {
                    ctx.InstallDriver();
                    using var c = ctx.CreateController(ctx.GetProfile("dualshock-4-v2")!, "build-info-check");
                    c.SubmitState(new HMGamepadState { Buttons = HMButton.A });
                    detail = $"transport index {c.TransportIndex}";
                    ok = true;
                }
                catch (Exception e)
                {
                    detail = $"{e.GetType().Name}: {e.Message}";
                    ok = false;
                }
                Check("the x64-only build creates and disposes a UMDF2 controller on x64", ok, detail);
            }
        }
        return 0;
    }

    /// <summary>Claim pipes, and the owner record that names this process
    /// with its controller entries. A refused create must leave all of them
    /// untouched.</summary>
    static HashSet<string> ClaimSnapshot()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The pipe root lists a claim with its prefix,
        // ProtectedPrefix\Administrators\HIDMaestro.Index.0.
        const string prefix = @"ProtectedPrefix\Administrators\";
        foreach (string p in Directory.GetFiles(@"\\.\pipe\"))
        {
            string n = p.Substring(@"\\.\pipe\".Length);
            if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            n = n.Substring(prefix.Length);
            if (n.StartsWith("HIDMaestro.Owner.", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("HIDMaestro.Index.", StringComparison.OrdinalIgnoreCase) ||
                n.StartsWith("HIDMaestro.Identity.", StringComparison.OrdinalIgnoreCase))
                set.Add("pipe " + n);
        }
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroOwners");
        if (root != null)
        {
            foreach (string id in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(id);
                if (k?.GetValue("Pid") is not int pid || pid != Environment.ProcessId) continue;
                set.Add("record " + id);
                using var cs = k.OpenSubKey("Controllers");
                foreach (string token in cs?.GetSubKeyNames() ?? Array.Empty<string>())
                    set.Add("controller " + token);
            }
        }
        return set;
    }

    static bool OtherConsumerRunning(out string? name)
    {
        name = null;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                foreach (ProcessModule m in p.Modules)
                    if (string.Equals(m.ModuleName, "HIDMaestro.Core.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        name = p.ProcessName;
                        return true;
                    }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return false;
    }
}
