// What a build of HIDMaestro.Core carries, read back from the built
// assembly, and what it refuses when a payload is missing.
//
// "complete": the build every release ships. It carries the driver and the
// USB/IP transport for x64 and ARM64 and the OpenVR driver, it carries no
// tool from a Windows kit (no resource whose name holds signtool, Inf2Cat,
// mssign32, wintrust, UniversalStore or WindowsProtectedFiles), and its four
// INFs carry the one DriverVer stamp the build took.
//
// "variant": the copy of this probe the project build puts in variant\,
// beside an SDK built with HMSkipArm64 and HMSkipUsbip from a tree whose
// build\ held no ARM64 driver and no OpenVR driver, and whose Resources\
// cache still held ARM64 and USB/IP files from a full build. It must carry
// the x64 driver only, nothing stale, and refuse what it lacks before any
// machine-wide step. It still creates a UMDF2 controller on x64 where Auto
// resolves to the driver, and elsewhere its create names the payload the
// resolved backend lacks.
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

    // Parts of the names of the Windows SDK's signtool set and the WDK's
    // Inf2Cat set, which the SDK carried through v2.2.0. It now writes and
    // signs catalogs through Windows' own wintrust.dll and mssign32.dll.
    static readonly string[] KitTools =
    {
        "signtool", "inf2cat", "mssign32", "wintrust", "appxsip", "appxpackaging", "opcservices",
        "UniversalStore", "WindowsProtectedFiles", "Kits.Logger",
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
        string[] kit = names.Where(n => KitTools.Any(t => n.Contains(t, StringComparison.OrdinalIgnoreCase))).ToArray();
        Check("no tool from a Windows kit is carried (signtool, Inf2Cat and their files)",
              kit.Length == 0, string.Join(", ", kit));

        // One build, one stamp. build_all.cmd takes the time once for both
        // architectures, and the companion INF takes the driver INF's stamp.
        // Through v1.12.0 each script read the clock, so a build whose two
        // stamps fell in two minutes carried two DriverVers.
        var stamps = new List<string>();
        foreach (string arch in new[] { "x64", "arm64" })
            foreach (string inf in new[] { "hidmaestro.inf", "hidmaestro_xusb.inf" })
                stamps.Add($"{arch} {inf} {EmbeddedDriverVer($"HIDMaestro.Native.{arch}.{inf}") ?? "none"}");
        var distinct = stamps.Select(s => s[(s.LastIndexOf(' ') + 1)..]).Distinct().ToList();
        Check("every INF of the build carries one DriverVer stamp",
              distinct.Count == 1 && distinct[0] != "none", string.Join("; ", stamps));
        return 0;
    }

    static string? EmbeddedDriverVer(string logicalName)
    {
        using var s = typeof(HMContext).Assembly.GetManifestResourceStream(logicalName);
        if (s == null) return null;
        using var r = new StreamReader(s);
        var m = System.Text.RegularExpressions.Regex.Match(r.ReadToEnd(), @"(?m)^\s*DriverVer\s*=\s*(\S+)\s*$");
        return m.Success ? m.Groups[1].Value : null;
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
            // The pipes are machine-wide. A program that embeds HIDMaestro
            // and starts, creates or exits during the refusals adds or drops
            // claims of its own, named in its own record, so only new claims
            // no other program's record names are this process's.
            var others = OtherProgramsClaims();
            var added = after.Except(before).ToList();
            var foreign = added.Where(others.Contains).ToList();
            var taken = added.Where(e => !others.Contains(e)).ToList();
            if (foreign.Count > 0)
                Console.WriteLine($"  info: claims another program took meanwhile: {string.Join(", ", foreign)}");
            Check("the refusals took no machine-wide claim and wrote nothing to the owner record",
                  taken.Count == 0, taken.Count == 0 ? "" : "new: " + string.Join(", ", taken));

            // A create of a profile both backends present resolves to the
            // machine's backend. This build carries neither the USB/IP
            // transport nor the ARM64 driver, so on ARM64, and wherever
            // HIDMAESTRO_BACKEND makes Auto resolve to USB/IP, the create names
            // the payload its backend lacks. On x64 it runs on the x64 driver.
            HMBackend machine = HMContext.MachineBackend;
            if (os == Architecture.Arm64 || machine == HMBackend.Usbip)
            {
                var (payload, arch) = machine == HMBackend.Usbip
                    ? (HMPayload.UsbipTransport, os)
                    : (HMPayload.Driver, Architecture.Arm64);
                ExpectMissing($"a dualshock-4-v2 create, which resolves to {machine} on this {os} machine,", payload, arch,
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

    /// <summary>The claim pipes other programs' owner records name, in
    /// <see cref="ClaimSnapshot"/>'s form: each record's owner claim, and
    /// the transport index and identity of each of its controllers.</summary>
    static HashSet<string> OtherProgramsClaims()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroOwners");
        if (root == null) return set;
        foreach (string id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            if (k == null || (k.GetValue("Pid") is int pid && pid == Environment.ProcessId)) continue;
            set.Add("pipe HIDMaestro.Owner." + id);
            using var cs = k.OpenSubKey("Controllers");
            foreach (string token in cs?.GetSubKeyNames() ?? Array.Empty<string>())
            {
                set.Add("pipe HIDMaestro.Identity." + token);
                using var c = cs!.OpenSubKey(token);
                if (c?.GetValue("TransportIndex") is int index) set.Add("pipe HIDMaestro.Index." + index);
            }
        }
        return set;
    }

    static bool OtherConsumerRunning(out string? name)
    {
        name = SdkConsumers.Find();
        return name != null;
    }
}
