// Driver catalog and signature check (issue #63, release 2.3.0). No device.
//
// Through v2.2.0 HIDMaestro.Core.dll carried Inf2Cat from the WDK and
// signtool from the Windows SDK and ran them to catalog and sign the driver
// package. It now writes the catalogs through wintrust.dll's catalog
// functions (DriverCatalog) and signs through mssign32.dll's SignerSignEx2
// (AuthenticodeSigner), in process. This probe checks both on the x64 and
// the ARM64 payload and on a WinUSB binding package:
//
//   1. The plan: the catalogs each package's INFs name, their members, and
//      the hardware IDs and OS value each catalog carries.
//   2. Each written catalog, decoded here with an ASN.1 reader of its own:
//      a version 2 catalog of driver files, two members per file (SHA-1 and
//      SHA-256), each tag the digest this probe computes itself (the bytes
//      of an INF, the Authenticode hash of a PE file), the attributes File
//      and OSAttr on each member, the SHA-256 member's indirect data
//      carrying the tag, and HWIDn and OS on the catalog.
//   3. Where the WDK's Inf2Cat is installed (a development machine): the
//      catalogs Inf2Cat writes for the same files and /os: value, compared
//      element by element. Only the list identifier and the time, which
//      differ on every run, are left out.
//   4. With elevation and a HIDMaestroTestCert with its private key in
//      LocalMachine\My (any machine HIDMaestro installed on): the binaries
//      and catalogs of both architectures signed, and each signature
//      verified by WinVerifyTrust. Where the Windows SDK's signtool is
//      installed, the same files signed by it and compared byte for byte.
//      The certificate is chosen by its exact subject: a name that is part
//      of HIDMaestroTestCert's selects nothing.
//   5. Refusals Inf2Cat makes: INFs with no models section decorated for
//      the target architecture (each architecture's INFs under the other),
//      and a member that resolves outside the package folder. The hardware
//      IDs kept as Inf2Cat keeps them, and an INF without a byte-order mark
//      read in the ANSI code page, as Inf2Cat reads it.
//   6. Controls that prove the checks can fail: an INF changed after its
//      catalog was written no longer matches its tag, a changed HWID breaks
//      the comparison, and a certificate name with no certificate throws.
//   7. The assembly carries no resource whose name holds signtool or
//      Inf2Cat.
//
// The reference runs of Inf2Cat and signtool start at normal priority and
// are stopped only by a hang detector of ten minutes, scaled by
// HIDMAESTRO_TIMEOUT_SCALE.
//
// Exit 0 PASS, 1 FAIL, 2 when every check passed and the signing part did
// not run (not elevated, or no HIDMaestroTestCert with its key), which it
// prints as its own condition.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Asn1;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static void Note(string text) => Console.WriteLine($"  [NOTE] {text}");

    // Set when the signing part did not run: the condition it printed.
    static string? s_signingSkipped;

    const string Kit = @"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0";
    static string Inf2CatExe => Path.Combine(Kit, "x86", "Inf2Cat.exe");
    static string SignToolExe => Path.Combine(Kit, RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64", "signtool.exe");

    // The hardware IDs of driver\hidmaestro.inf and driver\hidmaestro_xusb.inf,
    // in the order their models lines come, as Inf2Cat writes them.
    static readonly string[] DriverHwids =
    {
        @"root\hidmaestro",
        @"root\vid_045e&pid_028e&xi_00",
        @"root\vid_045e&pid_0291&xi_00",
        @"root\vid_045e&pid_0719&xi_00",
        @"root\hidmaestroxusb",
    };

    static readonly List<string> s_dirs = new();

    static int Main()
    {
        Console.WriteLine("=== Driver catalogs and signatures written in process ===");
        bool oracle = File.Exists(Inf2CatExe);
        Note(oracle ? $"Inf2Cat found at {Inf2CatExe}: its catalogs are compared" : "no WDK Inf2Cat on this machine: the catalogs are checked against this probe's own reading only");
        try
        {
            foreach (var arch in new[] { Architecture.X64, Architecture.Arm64 })
            {
                Console.WriteLine($"\n-- The {arch} driver package --");
                string? dir = StageDriver(arch);
                if (dir == null) continue;
                var plan = DriverCatalog.Plan(dir, arch);
                Check($"{arch}: two catalogs, hidmaestro.cat and hidmaestro_xusb.cat",
                      plan.Select(p => Path.GetFileName(p.Path)).SequenceEqual(new[] { "hidmaestro.cat", "hidmaestro_xusb.cat" }),
                      string.Join(", ", plan.Select(p => Path.GetFileName(p.Path))));
                Check($"{arch}: hidmaestro.cat holds HIDMaestro.dll and hidmaestro.inf",
                      plan.Count > 0 && Names(plan[0]).SequenceEqual(new[] { "hidmaestro.dll", "hidmaestro.inf" }),
                      plan.Count > 0 ? string.Join(", ", Names(plan[0])) : "");
                Check($"{arch}: hidmaestro_xusb.cat holds HMXInput.dll and hidmaestro_xusb.inf",
                      plan.Count > 1 && Names(plan[1]).SequenceEqual(new[] { "hidmaestro_xusb.inf", "hmxinput.dll" }),
                      plan.Count > 1 ? string.Join(", ", Names(plan[1])) : "");
                Check($"{arch}: both carry the five hardware IDs of both INFs, in order",
                      plan.All(p => p.HardwareIds.SequenceEqual(DriverHwids)),
                      plan.Count > 0 ? string.Join(" | ", plan[0].HardwareIds) : "");
                string os = arch == Architecture.Arm64 ? "_v100_ARM64_RS3" : "_v100_X64";
                Check($"{arch}: OS is {os}", plan.All(p => p.Os == os));

                var written = DriverCatalog.Write(dir, arch);
                foreach (var p in plan) CheckCatalog(arch.ToString(), p, os);
                if (oracle) CompareWithInf2Cat(arch, dir, written);
                Signing(arch.ToString(), dir, written);
            }

            Console.WriteLine("\n-- A WinUSB binding package --");
            foreach (var arch in new[] { Architecture.X64, Architecture.Arm64 })
            {
                string dir = NewDir(arch == Architecture.Arm64 ? "wa" : "wx");
                var pkg = new WinUsbBinding.Package("hmwinusb_signcat-probe.inf", "hmwinusb_signcat-probe.cat", 0x057E, 0x0337,
                    @"USB\COMPAT_VID_057E&Class_03&SubClass_00&Prot_00", new Guid("6f0b0b0a-3c3b-4bb5-9d0f-0d4b8c3f2a11"),
                    "WUP-028 \"probe\"", "signcat-probe");
                File.WriteAllBytes(Path.Combine(dir, pkg.InfName),
                    [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(WinUsbBinding.InfText(pkg))]);
                var plan = DriverCatalog.Plan(dir, arch);
                Check($"{arch}: one catalog holding the INF alone",
                      plan.Count == 1 && Names(plan[0]).SequenceEqual(new[] { pkg.InfName }),
                      plan.Count == 1 ? string.Join(", ", Names(plan[0])) : $"{plan.Count} catalogs");
                var ids = new List<string> { @"usb\vid_057e&pid_0337&hidmaestro_winusb" };
                for (int i = 1; i <= 15; i++) ids.Add($@"usb\hidmaestro_winusb_rank_{i:D2}");
                ids.Add(@"usb\compat_vid_057e&class_03&subclass_00&prot_00");
                Check($"{arch}: its 17 IDs, once each though both models sections list them",
                      plan.Count == 1 && plan[0].HardwareIds.SequenceEqual(ids),
                      plan.Count == 1 ? $"{plan[0].HardwareIds.Count} IDs" : "");
                var written = DriverCatalog.Write(dir, arch);
                if (plan.Count == 1) CheckCatalog($"{arch} WinUSB", plan[0], arch == Architecture.Arm64 ? "_v100_ARM64_RS3" : "_v100_X64");
                if (oracle) CompareWithInf2Cat(arch, dir, written);
                Signing(arch + " WinUSB", dir, written);
            }

            Console.WriteLine("\n-- Refusals and reading rules Inf2Cat has --");
            Refusals();

            Console.WriteLine("\n-- Controls: the checks can fail --");
            Controls(oracle);

            Console.WriteLine("\n-- What the assembly carries --");
            var kitResources = typeof(HMContext).Assembly.GetManifestResourceNames()
                .Where(n => n.Contains("signtool", StringComparison.OrdinalIgnoreCase) || n.Contains("inf2cat", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Check("no resource name holds signtool or Inf2Cat", kitResources.Count == 0, string.Join(", ", kitResources));
        }
        finally
        {
            foreach (var d in s_dirs)
                try { Directory.Delete(d, true); } catch { }
        }

        if (s_failures == 0 && s_signingSkipped != null)
        {
            Console.WriteLine($"\n=== {s_total}/{s_total} checks passed. The signing part did not run: {s_signingSkipped} ===");
            return 2;
        }
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} {(s_failures == 0 ? "PASS" : "FAIL")} ===");
        return s_failures == 0 ? 0 : 1;
    }

    static IEnumerable<string> Names(DriverCatalog.CatalogPlan p) => p.Members.Select(m => m.Name);

    /// <summary>A fresh directory with a short name: Inf2Cat is a .NET
    /// Framework program and fails on a library path of 260 characters.</summary>
    static string NewDir(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"hmcat{Environment.ProcessId}{tag}");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        s_dirs.Add(dir);
        return dir;
    }

    /// <summary>An architecture's driver package, each file from the
    /// resource EnsureExtracted reads it from.</summary>
    static string? StageDriver(Architecture arch)
    {
        string dir = NewDir(arch == Architecture.Arm64 ? "a" : "x");
        var asm = typeof(HMContext).Assembly;
        var missing = new List<string>();
        foreach (var (file, logical) in DriverBuilder.PayloadFor(arch))
        {
            using var src = asm.GetManifestResourceStream(logical);
            if (src == null) { missing.Add(logical); continue; }
            using var dst = File.Create(Path.Combine(dir, file));
            src.CopyTo(dst);
        }
        Check($"every {arch} driver package resource is embedded", missing.Count == 0, string.Join(", ", missing));
        return missing.Count == 0 ? dir : null;
    }

    // ── 2. The written catalog, read with this probe's own decoder ──────

    static void CheckCatalog(string label, DriverCatalog.CatalogPlan plan, string os)
    {
        string name = Path.GetFileName(plan.Path);
        if (!File.Exists(plan.Path)) { Check($"{label} {name}: written", false); return; }
        var ctl = Ctl.Read(File.ReadAllBytes(plan.Path));
        Check($"{label} {name}: a catalog list (usage 1.3.6.1.4.1.311.12.1.1), version 2 members (1.3.6.1.4.1.311.12.1.3)",
              ctl.Usage == "1.3.6.1.4.1.311.12.1.1" && ctl.SubjectAlgorithm == "1.3.6.1.4.1.311.12.1.3",
              $"{ctl.Usage}, {ctl.SubjectAlgorithm}");
        Check($"{label} {name}: {plan.Members.Count * 2} members, two per file", ctl.Entries.Count == plan.Members.Count * 2,
              $"{ctl.Entries.Count}");

        foreach (var (file, fileName) in plan.Members)
        {
            byte[] bytes = File.ReadAllBytes(file);
            bool pe = IsPe(bytes);
            byte[] sha1 = pe ? PeHash(bytes, HashAlgorithmName.SHA1) : SHA1.HashData(bytes);
            byte[] sha256 = pe ? PeHash(bytes, HashAlgorithmName.SHA256) : SHA256.HashData(bytes);
            var e1 = ctl.Entries.FirstOrDefault(e => e.Id.AsSpan().SequenceEqual(sha1));
            var e2 = ctl.Entries.FirstOrDefault(e => e.Id.AsSpan().SequenceEqual(sha256));
            string kind = pe ? "Authenticode hash" : "file hash";
            Check($"{label} {name}: {fileName}'s SHA-1 {kind} is a member", e1 != null, Convert.ToHexString(sha1));
            Check($"{label} {name}: {fileName}'s SHA-256 {kind} is a member", e2 != null, Convert.ToHexString(sha256));
            foreach (var e in new[] { e1, e2 }.OfType<Ctl.Entry>())
            {
                Check($"{label} {name}: {fileName} {e.Id.Length}-byte member: File={fileName}, OSAttr=2:10.0, flags 0x10010001",
                      e.NameValue("File") == (fileName, 0x10010001u) && e.NameValue("OSAttr") == ("2:10.0", 0x10010001u)
                      && e.Attributes.Any(a => a.Oid == "1.3.6.1.4.1.311.12.2.3"),
                      $"File={e.NameValue("File")}, OSAttr={e.NameValue("OSAttr")}");
            }
            if (e1 != null)
                Check($"{label} {name}: {fileName}'s SHA-1 member carries no indirect data, as Inf2Cat's", e1.IndirectDigest == null);
            if (e2 != null)
                Check($"{label} {name}: {fileName}'s SHA-256 member's indirect data is {(pe ? "PE image" : "flat file")} data of that digest",
                      e2.IndirectDigest != null && e2.IndirectDigest.AsSpan().SequenceEqual(sha256)
                      && e2.IndirectType == (pe ? "1.3.6.1.4.1.311.2.1.15" : "1.3.6.1.4.1.311.2.1.25")
                      && e2.IndirectAlgorithm == "2.16.840.1.101.3.4.2.1",
                      $"{e2.IndirectType} {e2.IndirectAlgorithm}");
        }

        var want = plan.HardwareIds.Select((id, i) => ($"HWID{i + 1}", id)).Append(("OS", os)).ToList();
        var have = ctl.CatalogAttributes.Select(a => (a.Name, a.Value)).OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
        Check($"{label} {name}: catalog attributes HWID1 to HWID{plan.HardwareIds.Count} and OS={os}, flags 0x10010001",
              have.SequenceEqual(want.OrderBy(a => a.Item1, StringComparer.Ordinal))
              && ctl.CatalogAttributes.All(a => a.Flags == 0x10010001u),
              string.Join(" | ", ctl.CatalogAttributes.Select(a => $"{a.Name}={a.Value}")));
    }

    // ── 3. Against Inf2Cat ───────────────────────────────────────────────

    static void CompareWithInf2Cat(Architecture arch, string dir, IReadOnlyList<string> written)
    {
        string copy = NewDir((arch == Architecture.Arm64 ? "a" : "x") + "i" + s_dirs.Count);
        foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith(".cat", StringComparison.OrdinalIgnoreCase)))
            File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
        string os = DriverBuilder.CatalogOsFor(arch);
        var (rc, output) = Run(Inf2CatExe, $"/driver:\"{copy}\" /os:{os}", copy);
        Check($"{arch}: Inf2Cat /os:{os} ran on the same files", rc == 0, $"exit {rc}: {LastLine(output)}");
        var theirs = Directory.GetFiles(copy, "*.cat").Select(Path.GetFileName).OrderBy(n => n).ToList();
        var ours = written.Select(Path.GetFileName).OrderBy(n => n).ToList();
        Check($"{arch}: the same catalog names as Inf2Cat ({string.Join(", ", theirs)})", theirs.SequenceEqual(ours),
              string.Join(", ", ours));
        foreach (string? n in ours)
        {
            string t = Path.Combine(copy, n!);
            if (!File.Exists(t)) continue;
            var a = Ctl.Read(File.ReadAllBytes(Path.Combine(dir, n!)));
            var b = Ctl.Read(File.ReadAllBytes(t));
            var diff = Ctl.Differences(a, b);
            Check($"{arch} {n}: element for element Inf2Cat's catalog (list identifier and time aside)", diff.Count == 0,
                  diff.Count == 0 ? $"{a.Entries.Count} members, {a.CatalogAttributes.Count} catalog attributes" : string.Join("; ", diff.Take(4)));
        }
    }

    // ── 4. Signing ───────────────────────────────────────────────────────

    static void Signing(string label, string dir, IReadOnlyList<string> catalogs)
    {
        bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        if (!elevated)
        {
            s_signingSkipped = "not elevated, and the signing needs the private key in LocalMachine\\My";
            Console.WriteLine($"  [SKIP] {label} signing: {s_signingSkipped}");
            return;
        }
        System.Security.Cryptography.X509Certificates.X509Certificate2 cert;
        try { cert = AuthenticodeSigner.SelectCertificate(DriverBuilder.CertFriendlyName); }
        catch (InvalidOperationException e)
        {
            s_signingSkipped = $"no signing certificate on this machine ({e.Message.TrimEnd('.')})";
            Console.WriteLine($"  [SKIP] {label} signing: {s_signingSkipped}");
            return;
        }
        using var _ = cert;
        Check($"{label}: the certificate selected is CN={DriverBuilder.CertFriendlyName} exactly",
              cert.Subject == "CN=" + DriverBuilder.CertFriendlyName, cert.Subject);
        string part = DriverBuilder.CertFriendlyName[..^4];
        Check($"{label}: {part}, only part of the name, selects no certificate",
              Refused(() => AuthenticodeSigner.SelectCertificate(part).Dispose()) != null);
        bool signtool = File.Exists(SignToolExe);
        var files = catalogs.ToList();
        files.AddRange(Directory.GetFiles(dir, "*.dll"));
        foreach (string file in files)
        {
            string name = $"{label} {Path.GetFileName(file)}";
            string ours = Path.Combine(dir, "ours_" + Path.GetFileName(file)), theirs = Path.Combine(dir, "theirs_" + Path.GetFileName(file));
            File.Copy(file, ours, true);
            File.Copy(file, theirs, true);
            try
            {
                AuthenticodeSigner.Sign(ours, cert);
                Check($"{name}: signed in process", true);
            }
            catch (InvalidOperationException e) { Check($"{name}: signed in process", false, e.Message); continue; }
            int trust = DriverSigningPolicy.Verify(ours, driverMode: false);
            Check($"{name}: WinVerifyTrust accepts the signature", trust == 0, $"0x{trust:X8}");
            if (!signtool) continue;
            var (rc, output) = Run(SignToolExe, $"sign /a /sm /s My /n {DriverBuilder.CertFriendlyName} /fd SHA256 \"{theirs}\"", dir);
            if (rc != 0) { Check($"{name}: signtool signs the same file", false, LastLine(output)); continue; }
            byte[] a = File.ReadAllBytes(ours), b = File.ReadAllBytes(theirs);
            Check($"{name}: byte for byte signtool's signature ({a.Length} bytes)", a.AsSpan().SequenceEqual(b),
                  a.Length == b.Length ? $"first difference at {FirstDifference(a, b)}" : $"{a.Length} against {b.Length} bytes");
        }
    }

    static int FirstDifference(byte[] a, byte[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (a[i] != b[i]) return i;
        return Math.Min(a.Length, b.Length);
    }

    // ── 5. Refusals and reading rules ────────────────────────────────────

    static void Refusals()
    {
        // Each architecture's INFs under the other. Inf2Cat's
        // FindARMModelDecorations and Find64BitModelDecorations refuse an INF
        // whose [Manufacturer] names no models section decorated for the
        // target.
        foreach (var (infs, target, decoration) in new[]
                 { (Architecture.X64, Architecture.Arm64, "NTARM64"), (Architecture.Arm64, Architecture.X64, "NTAMD64") })
        {
            string? dir = StageDriver(infs);
            if (dir == null) continue;
            string? error = Refused(() => DriverCatalog.Plan(dir, target));
            Check($"the {infs} INFs are refused for {target}: no {decoration} models section",
                  error != null && error.Contains(decoration, StringComparison.Ordinal), error ?? "a plan came back");
            string? write = Refused(() => DriverCatalog.Write(dir, target));
            Check($"and no catalog is written for {target}", write != null && Directory.GetFiles(dir, "*.cat").Length == 0,
                  write ?? string.Join(", ", Directory.GetFiles(dir, "*.cat").Select(Path.GetFileName)));
        }

        // A member outside the package folder: SourceDisksNames points one
        // folder up, where a file of the name the INF copies waits.
        string pkg = NewDir("o");
        string outside = pkg + "-outside";
        Directory.CreateDirectory(outside);
        s_dirs.Add(outside);
        File.WriteAllBytes(Path.Combine(outside, "escape.dll"), new byte[] { 0x4D, 0x5A });
        string up = "..\\" + Path.GetFileName(outside);
        File.WriteAllText(Path.Combine(pkg, "escape.inf"), string.Join("\r\n", new[]
        {
            "[Version]", "Signature = \"$WINDOWS NT$\"", "Class = HIDClass", "ClassGuid = {745a17a0-74d3-11d0-b6fe-00a0c90f57da}",
            "Provider = %P%", "CatalogFile = escape.cat", "DriverVer = 11/18/2026,2.3.0.0",
            "[SourceDisksNames]", "1 = %P%,,," + up, "[SourceDisksFiles]", "escape.dll = 1",
            "[DestinationDirs]", "DefaultDestDir = 13", "[Manufacturer]", "%P% = M,NTamd64,NTarm64",
            "[M.NTamd64]", "%P% = I, root\\escape", "[M.NTarm64]", "%P% = I, root\\escape",
            "[I.NT]", "CopyFiles = F", "[F]", "escape.dll", "[Strings]", "P = \"Escape\"",
        }));
        string? escaped = Refused(() => DriverCatalog.Plan(pkg, RuntimeInformation.OSArchitecture));
        Check("a member that resolves outside the package folder is refused",
              escaped != null && escaped.Contains("outside", StringComparison.Ordinal), escaped ?? "a plan came back");

        // The hardware IDs Inf2Cat writes (IsHardwareIdValid): every ID but
        // a six-character PnP ID, or a PnP ID under ACPI or BIOS, unless it
        // is one of the legacy keyboard, mouse and serial IDs, matched whole.
        foreach (var (id, kept) in new[]
        {
            (@"root\hidmaestro", true), (@"usb\vid_057e&pid_0337", true), ("pnp0f13", true), (@"acpi\pnp0f13", true),
            (@"acpi\pnp0c0a", false), (@"bios\pnp0c0a", false), (@"acpi\pnp03", false), (@"acpi\int3400", true), ("pnp030", false),
        })
            Check($"hardware ID {id} is {(kept ? "kept" : "left out")}", DriverCatalog.InfFile.IsHardwareIdKept(id) == kept);

        // No byte-order mark: Inf2Cat converts the bytes from
        // Encoding.Default, the ANSI code page on .NET Framework
        // (InfReader.ForceMissingBOMToUnicode).
        byte[] bytes = { 0x80, 0xE9, 0x41 };
        string ansi = Ansi(bytes);
        string read = DriverCatalog.InfFile.Decode(bytes);
        Check($"an INF without a byte-order mark reads in the ANSI code page ({GetACP()})", read == ansi,
              $"{Hex(read)} against {Hex(ansi)}");
    }

    static string Hex(string s) => string.Join(" ", s.Select(c => ((int)c).ToString("X4")));

    static string? Refused(Action a)
    {
        try { a(); return null; }
        catch (InvalidOperationException e) { return e.Message; }
    }

    static unsafe string Ansi(byte[] bytes)
    {
        fixed (byte* b = bytes)
        {
            int n = MultiByteToWideChar(0, 0, b, bytes.Length, null, 0);
            var chars = new char[n];
            fixed (char* c = chars) MultiByteToWideChar(0, 0, b, bytes.Length, c, n);
            return new string(chars);
        }
    }

    [DllImport("kernel32.dll")]
    static extern uint GetACP();

    [DllImport("kernel32.dll")]
    static extern unsafe int MultiByteToWideChar(uint codePage, uint flags, byte* mb, int cb, char* wide, int cch);

    // ── 6. Controls ──────────────────────────────────────────────────────

    static void Controls(bool oracle)
    {
        string? dir = StageDriver(RuntimeInformation.OSArchitecture);
        if (dir == null) return;
        var written = DriverCatalog.Write(dir, RuntimeInformation.OSArchitecture);
        string inf = Path.Combine(dir, "hidmaestro.inf");
        var ctl = Ctl.Read(File.ReadAllBytes(written[0]));
        File.AppendAllText(inf, "; changed after the catalog\r\n");
        byte[] changed = SHA256.HashData(File.ReadAllBytes(inf));
        Check("an INF changed after its catalog was written matches no member",
              !ctl.Entries.Any(e => e.Id.AsSpan().SequenceEqual(changed)));

        var other = Ctl.Read(File.ReadAllBytes(written[0]));
        var hw = other.CatalogAttributes.First(a => a.Name == "HWID1");
        other.CatalogAttributes[other.CatalogAttributes.IndexOf(hw)] = hw with { Value = hw.Value + "x" };
        Check("a changed HWID1 is a difference", Ctl.Differences(ctl, other).Count > 0);

        bool threw = false;
        try { AuthenticodeSigner.SelectCertificate("HIDMaestroNoSuchCertificate" + Environment.ProcessId).Dispose(); }
        catch (InvalidOperationException) { threw = true; }
        Check("a certificate name with no certificate throws", threw);
    }

    // ── PE Authenticode hash (the Authenticode PE image hash) ───────────

    static bool IsPe(byte[] b) =>
        b.Length > 0x40 && b[0] == 'M' && b[1] == 'Z' && BitConverter.ToInt32(b, 0x3C) is int o && o + 4 < b.Length
        && b[o] == 'P' && b[o + 1] == 'E' && b[o + 2] == 0 && b[o + 3] == 0;

    /// <summary>The hash of the file with the checksum, the certificate
    /// table's directory entry and the certificate table left out.</summary>
    static byte[] PeHash(byte[] b, HashAlgorithmName alg)
    {
        int pe = BitConverter.ToInt32(b, 0x3C);
        int opt = pe + 4 + 20;
        ushort magic = BitConverter.ToUInt16(b, opt);
        int checksum = opt + 64;
        int dirs = opt + (magic == 0x20B ? 112 : 96);
        int secDir = dirs + 4 * 8;
        int certOffset = BitConverter.ToInt32(b, secDir);
        int certSize = BitConverter.ToInt32(b, secDir + 4);
        int end = certSize > 0 && certOffset > 0 ? certOffset : b.Length;
        using var h = IncrementalHash.CreateHash(alg);
        h.AppendData(b, 0, checksum);
        h.AppendData(b, checksum + 4, secDir - (checksum + 4));
        h.AppendData(b, secDir + 8, end - (secDir + 8));
        return h.GetHashAndReset();
    }

    // ── Processes ────────────────────────────────────────────────────────

    static (int Rc, string Output) Run(string exe, string args, string workingDir)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // A child takes an idle or below-normal priority class from its
        // parent (CreateProcess), and the battery can run a probe at idle
        // priority under load, so the reference tools start at normal
        // priority. The bound is a hang detector, not a time budget.
        Process p;
        using (var self = Process.GetCurrentProcess())
        {
            var before = self.PriorityClass;
            self.PriorityClass = ProcessPriorityClass.Normal;
            try { p = Process.Start(psi)!; }
            finally { self.PriorityClass = before; }
        }
        using var owned = p;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        int bound = TimeoutScale.Apply(ToolHangMs);
        if (!p.WaitForExit(bound))
        {
            try { p.Kill(true); } catch { }
            return (-999, $"(stopped after {bound} ms, the hang detector at scale {TimeoutScale.Factor})");
        }
        Task.WaitAll(new Task[] { stdout, stderr }, 10_000);
        return (p.ExitCode, (stdout.IsCompletedSuccessfully ? stdout.Result : "")
                          + (stderr.IsCompletedSuccessfully ? stderr.Result : ""));
    }

    /// <summary>The reference runs' hang detector before scaling: ten
    /// minutes, against about 2 s for Inf2Cat unloaded.</summary>
    const int ToolHangMs = 600_000;

    static string LastLine(string output) =>
        output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
}

/// <summary>A catalog's certificate trust list, read from its PKCS #7
/// SignedData with System.Formats.Asn1 and nothing of wintrust.dll.</summary>
internal sealed class Ctl
{
    internal sealed record Attribute(string Oid, List<byte[]> Values);
    internal sealed record NameValue(string Name, uint Flags, string Value);

    internal sealed class Entry
    {
        public byte[] Id = Array.Empty<byte>();
        public List<Attribute> Attributes = new();
        public byte[] Encoded = Array.Empty<byte>();
        public string? IndirectType, IndirectAlgorithm;
        public byte[]? IndirectDigest;

        public (string, uint)? NameValue(string name)
        {
            foreach (var a in Attributes.Where(a => a.Oid == "1.3.6.1.4.1.311.12.2.1"))
                foreach (var v in a.Values)
                    if (Ctl.ReadNameValue(v) is { } nv && nv.Name == name) return (nv.Value, nv.Flags);
            return null;
        }
    }

    public string Usage = "", SubjectAlgorithm = "";
    public List<Entry> Entries = new();
    public List<NameValue> CatalogAttributes = new();
    // Every element of the list in order, its list identifier and time
    // replaced by a marker, for the comparison.
    public List<byte[]> Elements = new();

    internal static Ctl Read(byte[] cat)
    {
        var ci = new AsnReader(cat, AsnEncodingRules.BER).ReadSequence();
        ci.ReadObjectIdentifier();
        var sd = ci.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0)).ReadSequence();
        sd.ReadInteger();
        sd.ReadSetOf();
        var encap = sd.ReadSequence();
        encap.ReadObjectIdentifier();
        var content = encap.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0));
        ReadOnlyMemory<byte> ctlBytes = content.PeekTag() == Asn1Tag.PrimitiveOctetString
            ? content.ReadOctetString() : content.ReadEncodedValue();
        var list = new AsnReader(ctlBytes, AsnEncodingRules.BER).ReadSequence();
        var ctl = new Ctl();
        bool usageSeen = false, timeSeen = false;
        while (list.HasData)
        {
            var tag = list.PeekTag();
            var encoded = list.PeekEncodedValue().ToArray();
            if (tag == Asn1Tag.Integer && !usageSeen) { list.ReadEncodedValue(); ctl.Elements.Add(encoded); continue; }
            if (tag == Asn1Tag.Sequence && !usageSeen)
            {
                usageSeen = true;
                ctl.Usage = string.Join(",", ReadOids(list.ReadSequence()));
                ctl.Elements.Add(encoded);
                continue;
            }
            if (tag == Asn1Tag.PrimitiveOctetString) { list.ReadEncodedValue(); ctl.Elements.Add(Encoding.ASCII.GetBytes("<list identifier>")); continue; }
            if (tag == Asn1Tag.UtcTime || tag == Asn1Tag.GeneralizedTime)
            {
                list.ReadEncodedValue();
                ctl.Elements.Add(Encoding.ASCII.GetBytes(timeSeen ? "<next update>" : "<this update>"));
                timeSeen = true;
                continue;
            }
            if (tag == Asn1Tag.Sequence && ctl.SubjectAlgorithm.Length == 0)
            {
                ctl.SubjectAlgorithm = list.ReadSequence().ReadObjectIdentifier();
                ctl.Elements.Add(encoded);
                continue;
            }
            if (tag == Asn1Tag.Sequence)
            {
                var subjects = list.ReadSequence();
                while (subjects.HasData) ctl.Entries.Add(ReadEntry(subjects.ReadEncodedValue().ToArray()));
                ctl.Elements.Add(encoded);
                continue;
            }
            if (tag == new Asn1Tag(TagClass.ContextSpecific, 0, true))
            {
                var exts = list.ReadSequence(tag).ReadSequence();
                while (exts.HasData)
                {
                    var ext = exts.ReadSequence();
                    string oid = ext.ReadObjectIdentifier();
                    if (ext.PeekTag() == Asn1Tag.Boolean) ext.ReadBoolean();
                    byte[] value = ext.ReadOctetString();
                    if (oid == "1.3.6.1.4.1.311.12.2.1" && ReadNameValue(value) is { } nv) ctl.CatalogAttributes.Add(nv);
                    else ctl.CatalogAttributes.Add(new NameValue(oid, 0, Convert.ToHexString(value)));
                }
                ctl.Elements.Add(Encoding.ASCII.GetBytes("<extensions>"));
                continue;
            }
            list.ReadEncodedValue();
            ctl.Elements.Add(encoded);
        }
        return ctl;
    }

    static List<string> ReadOids(AsnReader r)
    {
        var l = new List<string>();
        while (r.HasData) l.Add(r.ReadObjectIdentifier());
        return l;
    }

    static Entry ReadEntry(byte[] encoded)
    {
        var e = new Entry { Encoded = encoded };
        var s = new AsnReader(encoded, AsnEncodingRules.BER).ReadSequence();
        e.Id = s.ReadOctetString();
        if (s.HasData)
        {
            var set = s.ReadSetOf();
            while (set.HasData)
            {
                var a = set.ReadSequence();
                string oid = a.ReadObjectIdentifier();
                var values = new List<byte[]>();
                var vs = a.ReadSetOf();
                while (vs.HasData) values.Add(vs.ReadEncodedValue().ToArray());
                e.Attributes.Add(new Attribute(oid, values));
                if (oid == "1.3.6.1.4.1.311.2.1.4")
                {
                    // SpcIndirectDataContent: SEQUENCE { data SEQUENCE { type, value }, messageDigest DigestInfo }
                    var ind = new AsnReader(values[0], AsnEncodingRules.BER).ReadSequence();
                    var data = ind.ReadSequence();
                    e.IndirectType = data.ReadObjectIdentifier();
                    var digest = ind.ReadSequence();
                    e.IndirectAlgorithm = digest.ReadSequence().ReadObjectIdentifier();
                    e.IndirectDigest = digest.ReadOctetString();
                }
            }
        }
        return e;
    }

    internal static NameValue? ReadNameValue(byte[] value)
    {
        try
        {
            var s = new AsnReader(value, AsnEncodingRules.BER).ReadSequence();
            string name = s.ReadCharacterString(UniversalTagNumber.BMPString);
            uint flags = (uint)(long)s.ReadInteger();
            byte[] data = s.ReadOctetString();
            return new NameValue(name, flags, Encoding.Unicode.GetString(data).TrimEnd('\0'));
        }
        catch (Exception e) when (e is AsnContentException or InvalidOperationException) { return null; }
    }

    /// <summary>Every place two lists differ: their elements in order (the
    /// list identifier and time left out), each member by its tag, encoded
    /// whole, and the catalog attributes by name.</summary>
    internal static List<string> Differences(Ctl a, Ctl b)
    {
        var d = new List<string>();
        if (a.Elements.Count != b.Elements.Count) d.Add($"{a.Elements.Count} elements against {b.Elements.Count}");
        for (int i = 0; i < Math.Min(a.Elements.Count, b.Elements.Count); i++)
            if (!a.Elements[i].AsSpan().SequenceEqual(b.Elements[i]))
                d.Add($"element {i} differs");
        var ta = a.Entries.Select(e => Convert.ToHexString(e.Id)).ToList();
        var tb = b.Entries.Select(e => Convert.ToHexString(e.Id)).ToList();
        if (!ta.SequenceEqual(tb)) d.Add($"member tags [{string.Join(",", ta)}] against [{string.Join(",", tb)}]");
        else
            for (int i = 0; i < a.Entries.Count; i++)
                if (!a.Entries[i].Encoded.AsSpan().SequenceEqual(b.Entries[i].Encoded)) d.Add($"member {ta[i]} encodes differently");
        var ca = a.CatalogAttributes.Select(x => $"{x.Name}={x.Value}/{x.Flags:X}").ToList();
        var cb = b.CatalogAttributes.Select(x => $"{x.Name}={x.Value}/{x.Flags:X}").ToList();
        if (!ca.SequenceEqual(cb)) d.Add($"catalog attributes [{string.Join(" ", ca)}] against [{string.Join(" ", cb)}]");
        return d;
    }
}
