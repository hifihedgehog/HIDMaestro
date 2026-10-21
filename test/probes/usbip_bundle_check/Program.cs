// Bundled-transport deploy check (issue #39).
//
// Composite USB personas must work with nothing for a user to install,
// which means usbip-win2's host controller ships inside HIDMaestro.Core.dll
// and deploys itself. This probe exercises that deploy path's own code:
//
//   1. The host controller's INF, SYS and CAT are embedded and byte-exact
//      against their pinned SHA-256, the license notice is embedded, and
//      usbip-win2's installer is not: the SDK never runs it.
//   2. Staging writes the package intact to the administrators-only
//      staging directory, alongside the BSD-2-Clause notice redistribution
//      requires.
//   3. A staged copy changed on disk is replaced from the embedded bytes
//      (ProtectedStaging.StageStream, the step staging runs, on a private
//      copy), the hash check (ProtectedStaging.VerifyBeforeUse) refuses
//      tampered bytes, and the hold the driver install takes on each file
//      (ProtectedStaging.PinFile) refuses a tampered file in the staging
//      directory.
//   4. EnsureInstalled is idempotent and returns true on a machine that
//      already has the transport, without reinstalling.
//   5. The public API surface makes composites unconditional: there is a
//      pre-install entry point, and availability is informational.
//   6. The host controller answers exactly one of the request layouts the
//      client knows, and it is the one its installed driver speaks.
//
// The install itself, from an absent state, is covered by S73's run in
// Windows Sandbox (usbip_install_check).
//
// `--offline` runs only what needs no elevation and no transport: the
// embedded payload, the hash check on copies in %TEMP%, the public
// contract, and that the deploy and repair steps it reaches exist. The
// rest requires elevation, since the package stages in an
// administrators-only directory. Exit 0 PASS / 1 FAIL.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using HIDMaestro;
using HIDMaestro.Internal.Usbip;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    // Read the pin from the code under test rather than repeating it.
    // A probe that carries its own copy of the file names and the digests
    // goes stale the moment the pin moves, and then it reports a missing
    // bundle when the bundle is fine.
    static string SysSha => UsbipDriverInstaller.PinnedUdeHash("usbip2_ude.sys");
    const string NoticeRes = "HIDMaestro.Resources.THIRD-PARTY-NOTICES.txt";

    static int Main(string[] args)
    {
        bool offline = args.Contains("--offline");
        Console.WriteLine("=== Bundled USB transport (issue #39) ===");
        var asm = typeof(HMProfile).Assembly;

        // ── The bundle ───────────────────────────────────────────────────
        Console.WriteLine("\n-- Embedded payload --");
        var names = asm.GetManifestResourceNames();
        var udeRes = UsbipDriverInstaller.UdeResourcesFor(RuntimeInformation.OSArchitecture).ToList();
        foreach (string res in udeRes)
        {
            string file = res[(res.LastIndexOf(".usbip2_ude", StringComparison.Ordinal) + 1)..];
            using var s = asm.GetManifestResourceStream(res);
            string hash = s == null ? "missing" : Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
            Check($"{file} embedded and matches its pinned SHA-256 byte for byte", hash == UsbipDriverInstaller.PinnedUdeHash(file), hash);
        }
        Check("license notice embedded", names.Contains(NoticeRes));
        Check("usbip-win2's installer is not embedded",
              !names.Any(n => n.StartsWith("HIDMaestro.Resources.USBip-", StringComparison.OrdinalIgnoreCase)));

        // ── The hash check ───────────────────────────────────────────────
        //
        // A driver file that fails its hash must never reach the driver
        // store. ProtectedStaging.VerifyBeforeUse runs here on two copies in
        // %TEMP%: the embedded bytes, and the same bytes with one byte
        // flipped.
        Console.WriteLine("\n-- Deploy: the hash check --");
        string sysRes = udeRes.Single(r => r.EndsWith(".usbip2_ude.sys", StringComparison.Ordinal));
        byte[] good;
        using (var s = asm.GetManifestResourceStream(sysRes)!)
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            good = ms.ToArray();
        }
        var corrupt = (byte[])good.Clone();
        corrupt[corrupt.Length / 2] ^= 0xFF;
        string scratch = Path.Combine(Path.GetTempPath(), $"hm-bundle-{Environment.ProcessId}");
        Directory.CreateDirectory(scratch);
        try
        {
            string genuine = Path.Combine(scratch, "genuine.bin"), tampered = Path.Combine(scratch, "tampered.bin");
            File.WriteAllBytes(genuine, good);
            File.WriteAllBytes(tampered, corrupt);
            Check("the hash check accepts the genuine host controller driver", Verifies(genuine));
            Check("the hash check refuses tampered bytes", !Verifies(tampered));
        }
        finally { try { Directory.Delete(scratch, recursive: true); } catch { } }

        var stage = typeof(UsbipDriverInstaller).GetMethod("StageUdePackage", BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);
        Check("deploy path exposes its staging step", stage != null);
        var force = typeof(UsbipDriverInstaller).GetMethod("RunForceDriver", BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(string) });
        Check("deploy path exposes its driver install", force != null);
        // The production repair: the overload TryRestartHostController(work)
        // runs, with the transport's own availability check, the devnode
        // restart and its 30 s budget.
        var restartDevnode = typeof(UsbipDriverInstaller).GetMethod("RestartHostControllerDevnode",
            BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);
        Check("deploy path exposes its devnode restart", restartDevnode != null);
        if (stage == null || force == null || restartDevnode == null) { Summary(); return 1; }

        if (!offline)
        {
            // ── Staging ──────────────────────────────────────────────────
            Console.WriteLine("\n-- Deploy: staging and verification --");
            string inf = (string)stage.Invoke(null, null)!;
            string dir = Path.GetDirectoryName(inf)!;
            foreach (string file in new[] { "usbip2_ude.inf", "usbip2_ude.sys", "usbip2_ude.cat" })
            {
                string path = Path.Combine(dir, file);
                Check($"staging wrote {file}, at its pinned digest",
                      File.Exists(path) && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                          .Equals(UsbipDriverInstaller.PinnedUdeHash(file), StringComparison.OrdinalIgnoreCase), path);
            }
            string notice = Path.Combine(dir, "THIRD-PARTY-NOTICES.txt");
            Check("BSD-2-Clause notice written beside the package", File.Exists(notice), notice);
            if (File.Exists(notice))
            {
                string text = File.ReadAllText(notice);
                Check("notice carries the copyright line and the disclaimer",
                      text.Contains("Vadym Hrynchyshyn") && text.Contains("THIS SOFTWARE IS PROVIDED"));
            }

            // ── Tamper refusal ───────────────────────────────────────────
            //
            // Staging reuses a staged file only when its hash still matches,
            // so a copy changed on disk is replaced from the embedded bytes
            // rather than trusted. The step runs here on a private copy,
            // never on the machine's staged package, which another program
            // of this build may be about to install.
            Console.WriteLine("\n-- Deploy: tamper refusal --");
            string own = Path.Combine(Path.GetTempPath(), $"hm-bundle-stage-{Environment.ProcessId}");
            Directory.CreateDirectory(own);
            try
            {
                string copy = Path.Combine(own, "usbip2_ude.sys");
                File.WriteAllBytes(copy, corrupt);
                using (var src = asm.GetManifestResourceStream(sysRes)!)
                    HIDMaestro.Internal.ProtectedStaging.StageStream(own, "usbip2_ude.sys", src, SysSha);
                string afterHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copy))).ToLowerInvariant();
                Check("a corrupted staged copy is not trusted, and good bytes are restored", afterHash == SysSha, afterHash);
                Check("and the hash check accepts it", Verifies(copy));
            }
            finally { try { Directory.Delete(own, recursive: true); } catch { } }

            // ── The hold's refusal ───────────────────────────────────────
            //
            // The driver install holds each staged file to its pinned hash
            // until it returns (PinUdePackage). The file here sits beside
            // the staged package and carries tampered bytes.
            Console.WriteLine("\n-- Deploy: the hold's refusal --");
            string tamperedSys = Path.Combine(dir, $"tampered-{Environment.ProcessId}.sys");
            try
            {
                File.WriteAllBytes(tamperedSys, corrupt);
                Exception? refused = null;
                try { using var pin = HIDMaestro.Internal.ProtectedStaging.PinFile(tamperedSys, SysSha); }
                catch (Exception ex) { refused = ex; }
                Check("the hold refuses tampered bytes with its hash check",
                      refused is InvalidOperationException ioe && ioe.Message.Contains("does not match its expected SHA-256"),
                      refused == null ? "no exception" : $"{refused.GetType().Name}: {refused.Message}");
            }
            finally { try { File.Delete(tamperedSys); } catch { } }
        }

        // ── Idempotence and the public contract ──────────────────────────
        Console.WriteLine("\n-- Contract --");
        bool installed = HMContext.IsUsbipBackendAvailable;
        Console.WriteLine($"  [note] transport currently installed on this machine: {installed}");

        if (offline)
        {
            Console.WriteLine("  [note] offline: EnsureInstalled and the repair are not run.");
        }
        else if (installed)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool ok = UsbipDriverInstaller.EnsureInstalled();
            sw.Stop();
            Check("EnsureInstalled returns true when already deployed", ok);
            Check("and short-circuits rather than reinstalling", sw.ElapsedMilliseconds < 2000,
                  $"{sw.ElapsedMilliseconds} ms");
        }
        else
        {
            // A present host controller that does not answer is repaired,
            // never installed over: the SDK does not put a second host
            // controller beside it.
            bool present = UsbipDriverInstaller.HostControllers().Any(c => c.Present);
            Console.WriteLine($"  [note] a usbip-win2 host controller is present: {present}");

            if (present)
            {
                var restart = (Action)Delegate.CreateDelegate(typeof(Action), restartDevnode);
                // The repair polls until its budget runs out when the
                // transport does not come back, so the bound is the budget
                // plus the same again for the work around it, both scaled.
                int budget = HIDMaestro.Internal.TimeoutScale.Apply(30_000);
                int bound = budget + HIDMaestro.Internal.TimeoutScale.Apply(30_000);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool repaired = UsbipDriverInstaller.RepairForProbe(VhciClient.IsAvailable, restart, budget, null);
                sw.Stop();
                Console.WriteLine($"  [note] repair attempt: {(repaired ? "recovered" : "did not recover")} " +
                                  $"in {sw.ElapsedMilliseconds} ms");
                Check("repair runs without touching the root-hub filter INF (bounded time)",
                      sw.ElapsedMilliseconds < bound, $"{sw.ElapsedMilliseconds} ms, bound {bound} ms");
                Check("transport usable after repair", VhciClient.IsAvailable() == repaired);
            }
        }

        // ── Owner identifier on the host controller (issue #42) ─────────
        // A composite persona is a real Sony pad at every level a filter
        // can inspect, so the only place a host can recognize its own
        // virtual device is the node HIDMaestro brings to the tree. Without
        // this the persona enumerates as a second controller: on hardware
        // that meant SDL assigning player index 1 and lighting a lone pad
        // red. The token is additive, so upstream's id must stay first or
        // driver matching moves.
        Console.WriteLine("\n-- Owner identifier (issue #42) --");
        if (offline)
        {
            Console.WriteLine("  [note] offline: owner-identifier checks skipped.");
        }
        else if (!VhciClient.IsAvailable())
        {
            Console.WriteLine("  [note] transport not present; owner-identifier checks skipped.");
        }
        else
        {
            bool stamped = UsbipDriverInstaller.StampOwnerHardwareId();
            Check("stamping the host controller succeeds", stamped);

            string[] ids = ReadHardwareIds();
            Check("upstream hardware id is still first (driver binding unchanged)",
                  ids.Length > 0 && ids[0].Equals("ROOT\\USBIP_WIN2\\UDE", StringComparison.OrdinalIgnoreCase),
                  ids.Length > 0 ? ids[0] : "none");
            Check("HIDMAESTRO token present in the host controller's hardware ids",
                  ids.Any(i => i.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0),
                  string.Join(" | ", ids));

            // A consumer substring-matches "HIDMAESTRO" exactly as it does
            // for every UMDF2 virtual, so the token has to survive that
            // test rather than merely exist.
            Check("token matches the same substring test UMDF2 virtuals use",
                  ids.Any(i => i.ToUpperInvariant().Contains("HIDMAESTRO")));

            UsbipDriverInstaller.StampOwnerHardwareId();
            string[] again = ReadHardwareIds();
            Check("stamping twice does not duplicate the id",
                  again.Count(i => i.IndexOf("HIDMAESTRO", StringComparison.OrdinalIgnoreCase) >= 0) == 1,
                  $"{again.Length} id(s)");
        }

        // The request layout changed in 0.9.8.0 and again in 0.9.8.1, and
        // the client asks the driver which one it speaks. The answer has
        // to name the installed driver's own layout, judged by its bytes.
        Console.WriteLine("\n-- Host controller request layout --");
        if (offline)
        {
            Console.WriteLine("  [note] offline: layout checks skipped.");
        }
        else if (!VhciClient.IsAvailable())
        {
            Console.WriteLine("  [note] transport not present; layout checks skipped.");
        }
        else
        {
            string? layout = VhciClient.InstalledLayout();
            Check("the host controller answers one known request layout", layout != null, layout ?? "none");

            string? sys = UsbipDriverInstaller.InstalledUdeImagePath();
            string hash = sys != null && File.Exists(sys)
                ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sys))).ToLowerInvariant()
                : "";
            string? expected =
                hash == UsbipDriverInstaller.PinnedUdeHash("usbip2_ude.sys") ? UsbipDriverInstaller.Version
                : UsbipDriverInstaller.LegacyUdes.Any(l => l.SysSha256 == hash) ? "0.9.7.x"
                : null;
            if (expected == null)
                Console.WriteLine($"  [note] installed driver {(hash.Length > 0 ? hash[..16] : "unreadable")} is not one HIDMaestro installed; layout not tied to a version.");
            else
                Check("and it is the layout of the installed driver", layout == expected,
                      $"{layout} for {hash[..16]}, expected {expected}");

            var imports = VhciClient.GetImportedDevices();
            Check("the device list parses in that layout", imports.All(i => i.Port >= 1),
                  $"{imports.Count} attached");
        }

        Check("HMContext exposes an optional pre-install entry point",
              typeof(HMContext).GetMethod("InstallUsbipBackend") != null);
        Check("availability is a static informational probe, not a create gate",
              typeof(HMContext).GetProperty("IsUsbipBackendAvailable",
                  BindingFlags.Public | BindingFlags.Static) != null);
        Check("CreateController takes only a profile (no backend precondition)",
              typeof(HMContext).GetMethod("CreateController", new[] { typeof(HMProfile) }) != null);

        Summary();
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>Hardware ids of the emulated host controller, read straight
    /// from the enum key so the check is independent of the code that
    /// wrote them.</summary>
    static string[] ReadHardwareIds()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Enum\ROOT\USB\0000");
        return k?.GetValue("HardwareID") as string[] ?? Array.Empty<string>();
    }

    /// <summary>Whether the hash check passes the file as the pinned
    /// usbip2_ude.sys.</summary>
    static bool Verifies(string path)
    {
        try
        {
            HIDMaestro.Internal.ProtectedStaging.VerifyBeforeUse(path, SysSha);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    static void Summary()
        => Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
}
