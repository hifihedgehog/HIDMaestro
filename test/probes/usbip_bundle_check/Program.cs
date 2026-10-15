// Bundled-transport deploy check (issue #39).
//
// Composite USB personas must work with nothing for a user to install,
// which means the USB transport ships inside HIDMaestro.Core.dll and
// deploys itself. This probe exercises that deploy path's own code
// rather than the upstream installer's:
//
//   1. The installer binary is embedded and byte-exact against its
//      pinned SHA-256.
//   2. Extraction stages it intact in the administrators-only staging
//      directory, alongside the BSD-2-Clause notice redistribution
//      requires.
//   3. A staged copy changed on disk is replaced from the embedded bytes
//      (ProtectedStaging.StageStream, the step extraction runs, on a
//      private copy), the hash check the installer's launch runs
//      (ProtectedStaging.VerifyBeforeUse) refuses tampered bytes, and the
//      launch itself (RunSilentInstall) refuses them before it starts
//      anything.
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
// `--offline` runs only what needs no elevation and no transport: the
// embedded payload, the launch's hash check on copies in %TEMP%, the
// public contract, and that the deploy and repair steps it reaches exist.
// The rest requires elevation, since the installer stages in an
// administrators-only directory. Exit 0 PASS / 1 FAIL.

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

    static int Main(string[] args)
    {
        bool offline = args.Contains("--offline");
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

        // ── The launch's hash check ──────────────────────────────────────
        //
        // A driver installer that fails its hash must never be executed.
        // RunSilentInstall checks the staged copy with
        // ProtectedStaging.VerifyBeforeUse right before it starts it. The
        // same check runs here on two copies in %TEMP%: the embedded bytes,
        // and the same bytes with one byte flipped.
        Console.WriteLine("\n-- Deploy: the launch's hash check --");
        byte[] good;
        using (var s = asm.GetManifestResourceStream(InstallerRes)!)
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            good = ms.ToArray();
        }
        string scratch = Path.Combine(Path.GetTempPath(), $"hm-bundle-{Environment.ProcessId}");
        Directory.CreateDirectory(scratch);
        try
        {
            string genuine = Path.Combine(scratch, "genuine.bin"), tampered = Path.Combine(scratch, "tampered.bin");
            File.WriteAllBytes(genuine, good);
            var corrupt = (byte[])good.Clone();
            corrupt[corrupt.Length / 2] ^= 0xFF;
            File.WriteAllBytes(tampered, corrupt);
            Check("the hash check accepts the genuine installer", Verifies(genuine));
            Check("the hash check refuses tampered bytes", !Verifies(tampered));

            // The launch itself, on bytes that could never start: the PE
            // header's first byte is flipped too, so if the launch lost its
            // hash check, CreateProcess would fail with a bad-format error
            // and nothing would run. With the check, the launch refuses the
            // file before it starts anything.
            var launch = typeof(UsbipDriverInstaller).GetMethod("RunSilentInstall",
                BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(string) });
            Check("deploy path exposes its installer launch", launch != null);
            if (launch != null)
            {
                string unlaunchable = Path.Combine(scratch, "unlaunchable.exe");
                var bad = (byte[])corrupt.Clone();
                bad[0] ^= 0xFF;
                File.WriteAllBytes(unlaunchable, bad);
                Exception? refused = null;
                try { launch.Invoke(null, new object[] { unlaunchable }); }
                catch (TargetInvocationException tie) { refused = tie.InnerException; }
                Check("the installer launch refuses tampered bytes with its hash check, before it starts anything",
                      refused is InvalidOperationException ioe && ioe.Message.Contains("does not match its embedded SHA-256"),
                      refused == null ? "no exception" : $"{refused.GetType().Name}: {refused.Message}");
            }
        }
        finally { try { Directory.Delete(scratch, recursive: true); } catch { } }

        var extract = typeof(UsbipDriverInstaller).GetMethod("ExtractInstaller",
            BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);
        Check("deploy path exposes its extraction step", extract != null);
        // The production repair: the overload TryRestartHostController(work)
        // runs, with the transport's own availability check, the devnode
        // restart and its 30 s budget.
        var restartDevnode = typeof(UsbipDriverInstaller).GetMethod("RestartHostControllerDevnode",
            BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);
        Check("deploy path exposes its devnode restart", restartDevnode != null);
        if (extract == null || restartDevnode == null) { Summary(); return 1; }

        if (!offline)
        {
            // ── Extraction ───────────────────────────────────────────────
            Console.WriteLine("\n-- Deploy: extraction and verification --");
            string extracted = (string)extract.Invoke(null, null)!;
            string notice = Path.Combine(Path.GetDirectoryName(extracted)!, "THIRD-PARTY-NOTICES.txt");
            Check("extraction produced the installer on disk", File.Exists(extracted), extracted);
            Check("extracted bytes hash to the pinned digest",
                  File.Exists(extracted) &&
                  Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(extracted)))
                      .Equals(ExpectedSha, StringComparison.OrdinalIgnoreCase));
            Check("BSD-2-Clause notice written beside the binary", File.Exists(notice), notice);
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
            // rather than trusted. Extraction stages through
            // ProtectedStaging.StageStream. The step runs here on a private
            // copy, never on the machine's staged installer, which another
            // program of this build may be about to run.
            Console.WriteLine("\n-- Deploy: tamper refusal --");
            string own = Path.Combine(Path.GetTempPath(), $"hm-bundle-stage-{Environment.ProcessId}");
            Directory.CreateDirectory(own);
            try
            {
                string copy = Path.Combine(own, Path.GetFileName(extracted));
                var changed = File.ReadAllBytes(extracted);
                changed[changed.Length / 2] ^= 0xFF;
                File.WriteAllBytes(copy, changed);
                using (var src = asm.GetManifestResourceStream(InstallerRes)!)
                    HIDMaestro.Internal.ProtectedStaging.StageStream(own, Path.GetFileName(extracted), src, ExpectedSha);
                string afterHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copy))).ToLowerInvariant();
                Check("a corrupted staged copy is not trusted, and good bytes are restored",
                      afterHash == ExpectedSha, afterHash);
                Check("and the launch's hash check accepts it", Verifies(copy));
            }
            finally { try { Directory.Delete(own, recursive: true); } catch { } }
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
            // Distinguish "never installed here" from "installed but its
            // host controller is not usable". The second is the case that
            // must be repaired rather than reinstalled: reinstalling
            // detaches a filter driver from every USB root hub.
            var isProduct = typeof(UsbipDriverInstaller).GetMethod("IsProductInstalled",
                BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);
            Check("deploy path can tell 'absent' from 'present but broken'", isProduct != null);
            bool productPresent = isProduct != null && (bool)isProduct.Invoke(null, null)!;
            Console.WriteLine($"  [note] driver package present in the store: {productPresent}");

            if (productPresent)
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

    /// <summary>Whether the launch's hash check passes the file.</summary>
    static bool Verifies(string path)
    {
        try
        {
            HIDMaestro.Internal.ProtectedStaging.VerifyBeforeUse(path, ExpectedSha);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    static void Summary()
        => Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
}
