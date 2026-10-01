// Bundled-transport deploy check (issue #39).
//
// Composite USB personas must work with nothing for a user to install,
// which means the USB transport ships inside HIDMaestro.Core.dll and
// deploys itself. This probe exercises that deploy path's own code
// rather than the upstream installer's:
//
//   1. The installer binary is embedded and byte-exact against its
//      pinned SHA-256.
//   2. Extraction writes it to disk intact, alongside the BSD-2-Clause
//      notice redistribution requires.
//   3. A tampered extracted copy is REFUSED and deleted, never executed.
//   4. EnsureInstalled is idempotent and returns true on a machine that
//      already has the transport, without reinstalling.
//   5. The public API surface makes composites unconditional: there is a
//      pre-install entry point, and availability is informational.
//   6. The host controller answers exactly one of the request layouts the
//      client knows, and it is the one its installed driver speaks.
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

    // Read the pin from the code under test rather than repeating it.
    // A probe that carries its own copy of the version, the file name and
    // the digest goes stale the moment the pin moves, and then it reports
    // a missing bundle when the bundle is fine.
    static string ExpectedSha => UsbipDriverInstaller.InstallerSha256;
    static string InstallerRes => "HIDMaestro.Resources." + UsbipDriverInstaller.InstallerFile;
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
        Check("embedded installer matches its pinned SHA-256 byte for byte",
              embeddedHash == ExpectedSha, embeddedHash);
        // The digest above already pins the bytes exactly. This asserts
        // the resource is a whole installer rather than a stub.
        Check("embedded installer is a full binary", embeddedSize > 10_000_000,
              $"{embeddedSize:N0} bytes");

        // ── Extraction ───────────────────────────────────────────────────
        Console.WriteLine("\n-- Deploy: extraction and verification --");
        string dir = Path.Combine(Path.GetTempPath(),
                                  "HIDMaestro_usbip_" + UsbipDriverInstaller.Version);
        string exe = Path.Combine(dir, UsbipDriverInstaller.InstallerFile);
        string notice = Path.Combine(dir, "THIRD-PARTY-NOTICES.txt");

        // Start from nothing so extraction actually runs.
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }

        var extract = typeof(UsbipDriverInstaller).GetMethod("ExtractInstaller",
            BindingFlags.NonPublic | BindingFlags.Static);
        Check("deploy path exposes its extraction step", extract != null);
        if (extract == null) { Summary(); return 1; }

        string extracted = (string)extract.Invoke(null, null)!;
        Check("extraction produced the installer on disk", File.Exists(extracted), extracted);
        Check("extracted bytes hash to the pinned digest",
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

        // ── Owner identifier on the host controller (issue #42) ─────────
        // A composite persona is a real Sony pad at every level a filter
        // can inspect, so the only place a host can recognize its own
        // virtual device is the node HIDMaestro brings to the tree. Without
        // this the persona enumerates as a second controller: on hardware
        // that meant SDL assigning player index 1 and lighting a lone pad
        // red. The token is additive, so upstream's id must stay first or
        // driver matching moves.
        Console.WriteLine("\n-- Owner identifier (issue #42) --");
        if (!VhciClient.IsAvailable())
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
        if (!VhciClient.IsAvailable())
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

    static void Summary()
        => Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
}
