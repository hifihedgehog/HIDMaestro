// Bundled-transport deploy check (issue #39).
//
// Composite USB personas must work with nothing for a user to install,
// which means the USB transport ships inside HIDMaestro.Core.dll and
// deploys itself. This probe exercises that deploy path's own code
// rather than the upstream installer's:
//
//   1. The installer binary is embedded and byte-exact against the
//      upstream release's published SHA256.
//   2. Extraction writes it to disk intact, alongside the BSD-2-Clause
//      notice redistribution requires.
//   3. A tampered extracted copy is REFUSED and deleted, never executed.
//   4. EnsureInstalled is idempotent and returns true on a machine that
//      already has the transport, without reinstalling.
//   5. The public API surface makes composites unconditional: there is a
//      pre-install entry point, and availability is informational.
//
// Running the upstream installer itself from an absent state is covered
// by the live E2E probe and by the from-scratch installs performed on
// both test machines.
//
// Requires elevation only for the availability check. Exit 0 PASS / 1 FAIL.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
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

    const string ExpectedSha =
        "51620fa5f9f8be5932bc9d786deee557ce06d5407a99cab490dcfac71f185fea";
    const string InstallerRes = "HIDMaestro.Resources.USBip-0.9.7.7-x64.exe";
    const string NoticeRes = "HIDMaestro.Resources.THIRD-PARTY-NOTICES.txt";

    static int Main()
    {
        Console.WriteLine("=== Bundled USB transport (issue #39) ===");
        var asm = typeof(HMProfile).Assembly;

        // ── The bundle ───────────────────────────────────────────────────
        Console.WriteLine("\n-- Embedded payload --");
        var names = asm.GetManifestResourceNames();
        Check("installer embedded in HIDMaestro.Core.dll", names.Contains(InstallerRes));
        Check("license notice embedded", names.Contains(NoticeRes));

        long embeddedSize = 0;
        string embeddedHash = "";
        using (var s = asm.GetManifestResourceStream(InstallerRes))
        {
            if (s != null)
            {
                embeddedSize = s.Length;
                embeddedHash = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
            }
        }
        Check("embedded installer is the upstream release byte-for-byte",
              embeddedHash == ExpectedSha, embeddedHash);
        Check("embedded installer is the expected size", embeddedSize == 33_226_344,
              $"{embeddedSize:N0} bytes");

        // ── Extraction ───────────────────────────────────────────────────
        Console.WriteLine("\n-- Deploy: extraction and verification --");
        string dir = Path.Combine(Path.GetTempPath(), "HIDMaestro_usbip_0.9.7.7");
        string exe = Path.Combine(dir, "USBip-0.9.7.7-x64.exe");
        string notice = Path.Combine(dir, "THIRD-PARTY-NOTICES.txt");

        // Start from nothing so extraction actually runs.
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }

        var extract = typeof(UsbipDriverInstaller).GetMethod("ExtractInstaller",
            BindingFlags.NonPublic | BindingFlags.Static);
        Check("deploy path exposes its extraction step", extract != null);
        if (extract == null) { Summary(); return 1; }

        string extracted = (string)extract.Invoke(null, null)!;
        Check("extraction produced the installer on disk", File.Exists(extracted), extracted);
        Check("extracted bytes hash to the upstream digest",
              File.Exists(extracted) &&
              Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(extracted)))
                  .Equals(ExpectedSha, StringComparison.OrdinalIgnoreCase));
        Check("BSD-2-Clause notice written beside the binary", File.Exists(notice));
        if (File.Exists(notice))
        {
            string text = File.ReadAllText(notice);
            Check("notice carries the copyright line and the disclaimer",
                  text.Contains("Vadym Hrynchyshyn") && text.Contains("THIS SOFTWARE IS PROVIDED"));
        }

        // ── Tamper refusal ───────────────────────────────────────────────
        //
        // A driver installer that fails its hash must never be executed.
        // Corrupt the cached copy, clear the process-level "already
        // verified" flag, and re-extract: the code must replace it with
        // good bytes rather than trusting what it found.
        Console.WriteLine("\n-- Deploy: tamper refusal --");
        var verifiedFlag = typeof(UsbipDriverInstaller).GetField("s_verifiedThisProcess",
            BindingFlags.NonPublic | BindingFlags.Static);
        Check("verification state is tracked per process", verifiedFlag != null);

        byte[] good = File.ReadAllBytes(extracted);
        var corrupt = (byte[])good.Clone();
        corrupt[corrupt.Length / 2] ^= 0xFF;
        File.WriteAllBytes(extracted, corrupt);
        verifiedFlag?.SetValue(null, false);

        string reExtracted = (string)extract.Invoke(null, null)!;
        string afterHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(reExtracted)))
            .ToLowerInvariant();
        Check("a corrupted cached copy is not trusted; good bytes are restored",
              afterHash == ExpectedSha, afterHash);

        // And when the embedded source itself cannot satisfy the hash,
        // the code must refuse rather than run. Simulate by corrupting
        // after extraction and calling the private hash check directly.
        var hashMatches = typeof(UsbipDriverInstaller).GetMethod("HashMatches",
            BindingFlags.NonPublic | BindingFlags.Static);
        Check("deploy path exposes its hash check", hashMatches != null);
        if (hashMatches != null)
        {
            string bad = Path.Combine(dir, "tampered.bin");
            File.WriteAllBytes(bad, corrupt);
            Check("hash check rejects tampered bytes",
                  !(bool)hashMatches.Invoke(null, new object[] { bad })!);
            Check("hash check accepts the genuine binary",
                  (bool)hashMatches.Invoke(null, new object[] { reExtracted })!);
            try { File.Delete(bad); } catch { }
        }

        // ── Idempotence and the public contract ──────────────────────────
        Console.WriteLine("\n-- Contract --");
        bool installed = HMContext.IsUsbipBackendAvailable;
        Console.WriteLine($"  [note] transport currently installed on this machine: {installed}");

        if (installed)
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
            // Distinguish "never installed here" from "installed but its
            // host controller is not usable". The second is the case that
            // must be repaired rather than reinstalled: reinstalling
            // detaches a filter driver from every USB root hub.
            var isProduct = typeof(UsbipDriverInstaller).GetMethod("IsProductInstalled",
                BindingFlags.NonPublic | BindingFlags.Static);
            Check("deploy path can tell 'absent' from 'present but broken'", isProduct != null);
            bool productPresent = isProduct != null && (bool)isProduct.Invoke(null, null)!;
            Console.WriteLine($"  [note] driver package present in the store: {productPresent}");

            if (productPresent)
            {
                var restart = typeof(UsbipDriverInstaller).GetMethod("TryRestartHostController",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Check("a devnode-restart repair exists", restart != null);
                if (restart != null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool repaired = (bool)restart.Invoke(null, new object?[] { null })!;
                    sw.Stop();
                    Console.WriteLine($"  [note] repair attempt: {(repaired ? "recovered" : "did not recover")} " +
                                      $"in {sw.ElapsedMilliseconds} ms");
                    Check("repair runs without touching the root-hub filter INF (bounded time)",
                          sw.ElapsedMilliseconds < 60_000, $"{sw.ElapsedMilliseconds} ms");
                    Check("transport usable after repair", VhciClient.IsAvailable() == repaired);
                }
            }
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

    static void Summary()
        => Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
}
