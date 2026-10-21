// R: the usbip-win2 part of a full removal (UsbipRemoval.Remove), run inside
// the sandbox. Every verdict reads the machine directly: the devnodes, the
// driver store, the service and record keys and the files, never only the
// entries the removal reports.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    const string UdeServiceKey = @"SYSTEM\CurrentControlSet\Services\usbip2_ude";
    const string CompanionKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B2AF7A1F-HCTEST}_is1";

    readonly record struct Entry(string Kind, string Location, string Outcome, string? Reason)
    {
        public override string ToString() => $"{Kind} {Location}: {Outcome}{(Reason != null ? " (" + Reason + ")" : "")}";
    }

    /// <summary>UsbipRemoval.Remove, called through reflection as U1's
    /// HMUninstall calls it, its entries read back as text.</summary>
    static List<Entry> Remove(string label, bool preview, bool includeUnproven)
    {
        var type = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.Usbip.UsbipRemoval", throwOnError: true)!;
        var method = type.GetMethod("Remove", BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(bool), typeof(bool) })!;
        var sw = Stopwatch.StartNew();
        var list = (System.Collections.IEnumerable)method.Invoke(null, new object[] { preview, includeUnproven })!;
        var entries = new List<Entry>();
        foreach (object e in list)
        {
            var t = e.GetType();
            entries.Add(new Entry((string)t.GetProperty("Kind")!.GetValue(e)!, (string)t.GetProperty("Location")!.GetValue(e)!,
                t.GetProperty("Outcome")!.GetValue(e)!.ToString()!, (string?)t.GetProperty("Reason")!.GetValue(e)));
        }
        Note(FormattableString.Invariant($"{label}: Remove(preview: {preview}, includeUnproven: {includeUnproven}) took {sw.ElapsedMilliseconds} ms, {entries.Count} entries"));
        foreach (var e in entries) Note($"{label}:   {e}");
        return entries;
    }

    static bool Has(List<Entry> entries, string kind, Func<string, bool> where, string outcome, string? reasonHas = null) =>
        entries.Any(e => e.Kind == kind && where(e.Location) && e.Outcome == outcome
                         && (reasonHas == null || (e.Reason?.Contains(reasonHas, StringComparison.OrdinalIgnoreCase) ?? false)));

    /// <summary>The state a removal acts on, as one line: the host
    /// controllers with their ids, the usbip2_ude packages, the record, the
    /// service, the Apps entry and the program folder.</summary>
    static string Snapshot()
    {
        var hcs = Pnp.RootDevnodes(UdeHardwareId).Select(c => $"{c.Id}[{(c.Present ? "P" : "-")}]{string.Join(";", c.HardwareIds)}={Pnp.DriverInf(c.Id)}");
        string record;
        using (var k = Registry.LocalMachine.OpenSubKey(RecordKey))
            record = k == null ? "none" : string.Join(",", k.GetValueNames().OrderBy(n => n).Select(n => $"{n}={k.GetValue(n)}"));
        return $"hc {string.Join(" ", hcs)} | packages {string.Join(",", UdePackages())} | record {record} | service {KeyExists(UdeServiceKey)} " +
               $"| apps {KeyExists(UninstallKey)} | folder {Directory.Exists(InstallDir)} | wdf {string.Join(",", WdfDebugValues())}";
    }

    /// <summary>The published names of the usbip2_ude packages, read from
    /// %windir%\INF.</summary>
    static List<string> UdePackages()
    {
        var found = new List<string>();
        foreach (string f in Directory.EnumerateFiles(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF"), "oem*.inf"))
            if (InfOriginal(Path.GetFileName(f)) == "usbip2_ude.inf") found.Add(Path.GetFileName(f));
        return found;
    }

    /// <summary>The present host controller, its root hub and its driver
    /// package after an install, or nulls.</summary>
    static (string? Hc, string? Hub, string? Oem) Installed()
    {
        var hc = Pnp.RootDevnodes(UdeHardwareId).Where(c => c.Present).Select(c => c.Id).FirstOrDefault();
        if (hc == null) return (null, null, null);
        string? hub = Pnp.IdList("USB").FirstOrDefault(id => id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)
                                                            && string.Equals(Pnp.Parent(id), hc, StringComparison.OrdinalIgnoreCase));
        return (hc, hub, Pnp.DriverInf(hc));
    }

    static bool Answers()
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 30_000)
        {
            if (HMContext.IsUsbipBackendAvailable) return true;
            Thread.Sleep(250);
        }
        return false;
    }

    static void Removal()
    {
        // ── R00: a driver install that timed out and then bound ───────────
        // The state the timeout leaves: the record written before the driver
        // install, with no package, and the SDK's own driver install, which
        // binds after the create gave up. A removal proves it, and the next
        // create on the working transport records the package.
        var (late, lateToken) = RegisterOnly();
        WriteRecordWithNoDriver(late, lateToken, PinnedWholeVer);
        ForceDriverAsTheSdkDoes();
        Record("remove: a driver install that bound after its timeout leaves a working host controller and a record with no package",
               Answers() && Pnp.DriverInf(late) != null && RecordValue("OemInf") == null, $"{late} runs {Pnp.DriverInf(late)}");
        var latePreview = Remove("remove: bound late", preview: true, includeUnproven: false);
        Record("remove: a removal proves it, and would take the host controller and its package",
               Has(latePreview, "devnode", l => l.StartsWith(late + " "), "WouldRemove")
               && Has(latePreview, "driver package", l => l.StartsWith(Pnp.DriverInf(late) + " "), "WouldRemove"));

        // The same devnode under records that differ in one value each. The
        // record goes back to the plain one after each.
        string? lateOemNow = Pnp.DriverInf(late);
        // The store held that package before the install, as a user's
        // identical one: the record still proves the devnode, and the
        // package is not the record's to take.
        WriteRecordWithNoDriver(late, lateToken, PinnedWholeVer, lateOemNow == null ? null : new[] { lateOemNow });
        var heldBefore = Remove("remove: bound late, package held before", preview: true, includeUnproven: false);
        Record("remove: with the package in the store before the install, a removal takes the devnode and keeps the package",
               lateOemNow != null && Has(heldBefore, "devnode", l => l.StartsWith(late + " "), "WouldRemove")
               && Has(heldBefore, "driver package", l => l.StartsWith(lateOemNow + " "), "Kept"),
               string.Join("; ", heldBefore.Where(e => e.Kind is "devnode" or "driver package")));
        // A record that names another release than the one bound: an older
        // program's pin.
        WriteRecordWithNoDriver(late, lateToken, "01/01/2000,0.0.0.0");
        CreateOnWorkingTransport("remove: bound late, another release recorded: the next create's record check ran and recorded nothing", expect: 0);
        var otherRelease = Remove("remove: bound late, another release recorded", preview: true, includeUnproven: false);
        Record("remove: with another release recorded, the package is not recorded and a removal keeps the devnode",
               RecordValue("OemInf") == null && Has(otherRelease, "devnode", l => l.StartsWith(late + " "), "Kept"),
               $"OemInf {RecordValue("OemInf") ?? "none"}; " + string.Join("; ", otherRelease.Where(e => e.Kind == "devnode")));
        // usbip-win2's Apps entry written after the install: its installer
        // ran, and the package bound may be its own.
        WriteRecordWithNoDriver(late, lateToken, PinnedWholeVer);
        PlantAppsEntry();
        CreateOnWorkingTransport("remove: bound late, Apps entry written since: the next create's record check ran and recorded nothing", expect: 0);
        Record("remove: with usbip-win2's Apps entry written since the install, the next create does not record the package",
               RecordValue("OemInf") == null, $"OemInf {RecordValue("OemInf") ?? "none"}");
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        WriteRecordWithNoDriver(late, lateToken, PinnedWholeVer);

        CreateOnWorkingTransport("remove: bound late: the next create's record check ran and recorded the package (the positive control)", expect: 1);
        Record("remove: the next create on the working transport records the package",
               RecordValue("OemInf") is string lateOem && lateOem.Equals(Pnp.DriverInf(late), StringComparison.OrdinalIgnoreCase),
               $"OemInf {RecordValue("OemInf") ?? "none"}");
        var lateRemoved = Remove("remove: bound late, removed", preview: false, includeUnproven: false);
        Record("remove: and a default removal takes it all",
               !Pnp.Known(late) && UdePackages().Count == 0 && !KeyExists(RecordKey) && lateRemoved.All(e => e.Outcome != "Failed"), Snapshot());

        // ── R01: another program's package bound to that devnode ──────────
        // The same record with no package, but another usbip-win2 release's
        // own package forced onto the devnode, as another program's installer
        // forces its own. Neither the next create nor a removal takes it for
        // HIDMaestro's. The probe's build fetches that release, hash-pinned in
        // ForeignUsbipPackage.json, and the host copies it in
        // (CopyForeignPackage). A build without it fails here.
        string foreignInf = Path.Combine(ForeignDir, "usbip2_ude.inf");
        string foreignFrom = File.Exists(Path.Combine(ForeignDir, "source.txt")) ? File.ReadAllText(Path.Combine(ForeignDir, "source.txt")).Trim() : "";
        Record("remove: the tree holds another usbip-win2 release's host controller package for the foreign bind",
               File.Exists(foreignInf), File.Exists(foreignInf) ? foreignFrom : @"foreign-usbip\<arch> beside the probe is missing or fails ForeignUsbipPackage.json's hashes");
        if (File.Exists(foreignInf))
        {
            var (other, otherToken) = RegisterOnly();
            WriteRecordWithNoDriver(other, otherToken, PinnedWholeVer);
            int forced = Pnp.ForceDriver(foreignInf, UdeHardwareId);
            string? foreignOem = Pnp.DriverInf(other);
            Record($"remove: another release's package ({foreignFrom}) binds to HIDMaestro's devnode",
                   forced == 0 && foreignOem != null && !InfDriverVer(foreignOem).Equals(PinnedVer, StringComparison.OrdinalIgnoreCase),
                   $"force {forced}, {other} runs {foreignOem} at {(foreignOem == null ? "" : InfDriverVer(foreignOem))}");
            CreateOnWorkingTransport("remove: foreign bind: the next create's record check ran and recorded nothing (the positive control)", expect: 0);
            Record("remove: the next create does not record it", RecordValue("OemInf") == null, $"OemInf {RecordValue("OemInf") ?? "none"}");
            // The record update after a move (RefreshDriver), run on this
            // devnode as a move would leave it if it had put this package on.
            RecordType.GetMethod("RefreshDriver", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            Record("remove: nor does the record update after a move, for a package not of the pinned release",
                   RecordValue("OemInf") == null, $"OemInf {RecordValue("OemInf") ?? "none"}");
            var foreignPreview = Remove("remove: foreign bind", preview: true, includeUnproven: false);
            Record("remove: and a default removal keeps the host controller and that package",
                   Has(foreignPreview, "devnode", l => l.StartsWith(other + " "), "Kept")
                   && foreignOem != null && Has(foreignPreview, "driver package", l => l.StartsWith(foreignOem + " "), "Kept"));
            var foreignCleared = Remove("remove: foreign bind, usbip-win2 included", preview: false, includeUnproven: true);
            Record("remove: with usbip-win2 included, it all goes",
                   Pnp.RootDevnodes(UdeHardwareId).Count == 0 && UdePackages().Count == 0 && foreignCleared.All(e => e.Outcome != "Failed"), Snapshot());
            Registry.LocalMachine.DeleteSubKeyTree(RecordKey, throwOnMissingSubKey: false);
        }

        // ── R0: an install cut short between its devnode and its driver ────
        // The SDK's own registration, called as an install calls it, then
        // nothing more: a timeout or a killed process. With the record the
        // install writes before its driver, a default removal takes the
        // driverless devnode. Without it, the next install finishes it.
        var (cut, cutToken) = RegisterOnly();
        WriteRecordWithNoDriver(cut, cutToken);
        Record("remove: an install cut short leaves a driverless host controller with HIDMaestro's token",
               Pnp.Present(cut) && Pnp.DriverInf(cut) == null && Pnp.OwnerToken(cut) == cutToken, $"{cut}, token {Pnp.OwnerToken(cut)}");
        var finished = Remove("remove: cut short", preview: false, includeUnproven: false);
        Record("remove: a default removal takes it, with its record",
               Has(finished, "devnode", l => l.StartsWith(cut + " "), "Removed") && !Pnp.Known(cut) && !KeyExists(RecordKey)
               && finished.All(e => e.Outcome != "Failed"));

        // ── R02: a rebuilt record beside a user's own install ─────────────
        // A user's usbip-win2 0.9.8.1 install left its package, identical to
        // HIDMaestro's, and its Apps entry. HIDMaestro's devnode lost its
        // record. The record the next install writes again counts that
        // package as one the store held before, so a default removal keeps
        // it.
        string stagedInf = StagedPinnedInf();
        var added = Run(Path.Combine(Environment.SystemDirectory, "pnputil.exe"), 120_000, "/add-driver", stagedInf);
        var users = UdePackages();
        PlantAppsEntry();
        var (rebuilt, _) = RegisterOnly();
        HMContext.InstallUsbipBackend();
        Record("remove: beside a user's package and Apps entry, the next install finishes HIDMaestro's devnode on that package",
               added.Exit == 0 && users.Count == 1 && Pnp.DriverInf(rebuilt)?.Equals(users[0], StringComparison.OrdinalIgnoreCase) == true && Answers(),
               $"pnputil {added.Exit}, store {string.Join(",", users)}, {rebuilt} runs {Pnp.DriverInf(rebuilt)}");
        var userPreview = Remove("remove: beside a user's package", preview: true, includeUnproven: false);
        Record("remove: a default removal would take HIDMaestro's devnode and keep the user's package",
               Has(userPreview, "devnode", l => l.StartsWith(rebuilt + " "), "WouldRemove")
               && users.Count == 1 && Has(userPreview, "driver package", l => l.StartsWith(users[0] + " "), "Kept", "before HIDMaestro's install"));
        var userCleared = Remove("remove: beside a user's package, usbip-win2 included", preview: false, includeUnproven: true);
        Record("remove: with usbip-win2 included, it all goes",
               Pnp.RootDevnodes(UdeHardwareId).Count == 0 && UdePackages().Count == 0 && !KeyExists(UninstallKey) && !KeyExists(RecordKey)
               && userCleared.All(e => e.Outcome != "Failed"), Snapshot());

        (cut, cutToken) = RegisterOnly();
        var resumed = Stopwatch.StartNew();
        HMContext.InstallUsbipBackend();
        Note(FormattableString.Invariant($"remove: the install that finished a cut-short one took {resumed.Elapsed.TotalSeconds:F1} s"));
        using (var k = Registry.LocalMachine.OpenSubKey(RecordKey))
            Record("remove: with no record, the next install finishes the same devnode, answers, and records it with its token",
                   Pnp.RootDevnodes(UdeHardwareId).Count == 1 && Pnp.Present(cut) && Pnp.DriverInf(cut) != null && Answers()
                   && (k?.GetValue("Instance") as string)?.Equals(cut, StringComparison.OrdinalIgnoreCase) == true && k?.GetValue("Token") as string == cutToken,
                   $"{cut} runs {Pnp.DriverInf(cut)}");

        // ── R1: an install the record proves, unused since Windows started ──
        var install = Stopwatch.StartNew();
        HMContext.InstallUsbipBackend();
        Note(FormattableString.Invariant($"remove: the install took {install.Elapsed.TotalSeconds:F1} s"));
        InstallChecks("remove: install:");
        var (hc, hub, oem) = Installed();
        Record("remove: the host controller answers, with its root hub", hc != null && hub != null && Answers(), $"{hc} {hub} {oem}");
        if (hc == null || hub == null || oem == null) return;

        string before = Snapshot();
        var preview = Remove("remove: preview", preview: true, includeUnproven: false);
        Record("remove: the preview would remove the host controller, its root hub, its package, its service and the record",
               Has(preview, "devnode", l => l.StartsWith(hc + " "), "WouldRemove") && Has(preview, "devnode", l => l == hub, "WouldRemove")
               && Has(preview, "driver package", l => l.StartsWith(oem + " "), "WouldRemove") && Has(preview, "service", l => l == "usbip2_ude", "WouldRemove")
               && Has(preview, "registry key", l => l.EndsWith(RecordKey), "WouldRemove"));
        Record("remove: the preview reports nothing removed or failed", preview.All(e => e.Outcome is "WouldRemove" or "Kept"));
        string afterPreview = Snapshot();
        Record("remove: the preview changed nothing", afterPreview == before, afterPreview == before ? "" : $"before {before} / after {afterPreview}");

        var removed = Remove("remove: run", preview: false, includeUnproven: false);
        Record("remove: no step failed", removed.All(e => e.Outcome != "Failed"), string.Join("; ", removed.Where(e => e.Outcome == "Failed")));
        Record("remove: the host controller is gone, record and all", !Pnp.Known(hc));
        Record("remove: its root hub's record is gone", !Pnp.Known(hub));
        Record("remove: no usbip-win2 host controller is left", Pnp.RootDevnodes(UdeHardwareId).Count == 0);
        Record("remove: the package the install added is gone from the driver store", UdePackages().Count == 0, string.Join(",", UdePackages()));
        Record("remove: the usbip2_ude service is gone", !KeyExists(UdeServiceKey));
        Record("remove: the install record is gone", !KeyExists(RecordKey));
        Record("remove: the transport no longer answers", !HMContext.IsUsbipBackendAvailable);
        Record("remove: the filter was never there and is not now", !DriverPackagePresent("usbip2_filter") && !KeyExists(@"SYSTEM\CurrentControlSet\Services\usbip2_filter"));

        // ── R2: a reinstall after the removal ─────────────────────────────
        install.Restart();
        HMContext.InstallUsbipBackend();
        Note(FormattableString.Invariant($"remove: the reinstall took {install.Elapsed.TotalSeconds:F1} s"));
        (hc, hub, oem) = Installed();
        Record("remove: a reinstall after it brings the host controller back, answering", hc != null && Answers(), $"{hc} {oem}");
        InstallChecks("remove: reinstall:");
        if (hc == null) return;

        // ── R3: an install no record proves ───────────────────────────────
        // A devnode another installer registered on the same instance id
        // carries no HIDMaestro token, and one whose Apps entry usbip-win2's
        // installer wrote after HIDMaestro's install is no longer
        // HIDMaestro's alone. Previews, so nothing changes.
        string? token = Pnp.OwnerToken(hc);
        bool swapped = Pnp.SetOwnerToken(hc, "another-installers-devnode");
        var foreign = Remove("remove: another devnode", preview: true, includeUnproven: false);
        Record("remove: a devnode without the record's token is not taken",
               swapped && Has(foreign, "devnode", l => l.StartsWith(hc + " "), "Kept", "not the devnode HIDMaestro registered"));
        Pnp.SetOwnerToken(hc, token ?? "");
        using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "USBip version 0.9.8.1");
            k.SetValue("UninstallString", "\"C:\\Program Files\\USBip\\unins000.exe\"");
        }
        var vendor = Remove("remove: vendor install after", preview: true, includeUnproven: false);
        Record("remove: nor one whose usbip-win2 Apps entry was written since HIDMaestro's install",
               Has(vendor, "devnode", l => l.StartsWith(hc + " "), "Kept", "Apps entry was written or removed since HIDMaestro's install"));

        Registry.LocalMachine.DeleteSubKeyTree(RecordKey, throwOnMissingSubKey: false);
        using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "USBip version 0.9.8.1");
            k.SetValue("UninstallString", "\"C:\\Program Files\\USBip\\unins000.exe\"");
        }
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Path.Combine(InstallDir, "Readme.md"), "planted by the probe");
        string unproven = Snapshot();
        var kept = Remove("remove: unproven", preview: false, includeUnproven: false);
        Record("remove: with no record, the host controller, its package, the Apps entry and the folder are kept, and say why",
               Has(kept, "devnode", l => l.StartsWith(hc + " "), "Kept", "no install record") && Has(kept, "driver package", l => l.StartsWith(oem + " "), "Kept")
               && Has(kept, "Apps entry", _ => true, "Kept") && Has(kept, "folder", l => l.Equals(InstallDir, StringComparison.OrdinalIgnoreCase), "Kept"));
        Record("remove: and the only change is HIDMaestro's stamp, taken off the host controller",
               kept.All(e => e.Outcome is "Kept" || (e.Kind == "hardware id" && e.Outcome == "Removed") || (e.Kind == "folder" && e.Outcome == "Removed" && !e.Location.Equals(InstallDir, StringComparison.OrdinalIgnoreCase)))
               && Pnp.HardwareIds(hc).SequenceEqual(new[] { UdeHardwareId }, StringComparer.OrdinalIgnoreCase),
               string.Join(";", Pnp.HardwareIds(hc)));
        Record("remove: the host controller still answers", Answers());
        string afterKept = Snapshot();
        Record("remove: the host controller, package, Apps entry and folder are as they were, apart from the stamp",
               afterKept == unproven.Replace(";" + StampHardwareId, ""), afterKept == unproven.Replace(";" + StampHardwareId, "") ? "" : $"before {unproven} / after {afterKept}");

        using (var k = Registry.LocalMachine.CreateSubKey(CompanionKey))
            k.SetValue("DisplayName", "Handheld Companion 1.2.1.2");
        string companion = Snapshot();
        var refused = Remove("remove: in use", preview: false, includeUnproven: true);
        Record("remove: with another program that uses usbip-win2 installed, nothing of usbip-win2 goes even with usbip-win2 included",
               Has(refused, "devnode", l => l.StartsWith(hc + " "), "Kept", "Handheld Companion") && Snapshot() == companion,
               Snapshot() == companion ? "" : $"before {companion} / after {Snapshot()}");
        Registry.LocalMachine.DeleteSubKeyTree(CompanionKey, throwOnMissingSubKey: false);

        // What usbip-win2's installer left, with entries another program
        // holds open. Outside %ProgramFiles%\HIDMaestro nothing waits for the
        // next start: a held entry fails and says to close the program, the
        // rest goes, and a removal after the program closes takes what is
        // left.
        string menuDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "USBip");
        string heldDir = Path.Combine(menuDir, "held");
        string heldExe = Path.Combine(InstallDir, "usbip.exe");
        Directory.CreateDirectory(heldDir);
        File.WriteAllText(Path.Combine(heldDir, "inside.txt"), "planted by the probe");
        File.WriteAllText(Path.Combine(menuDir, "USBip.lnk"), "planted by the probe");
        File.WriteAllText(PublicLink, "planted by the probe");
        File.WriteAllText(heldExe, "planted by the probe");
        List<Entry> taken;
        using (new FileStream(heldExe, FileMode.Open, FileAccess.Read, FileShare.None))
        using (new FileStream(PublicLink, FileMode.Open, FileAccess.Read, FileShare.None))
        using (OpenFolderWithoutDeleteSharing(heldDir))
            taken = Remove("remove: included, entries held", preview: false, includeUnproven: true);
        Record("remove: a held file in usbip-win2's program folder fails and says to close the program, and the rest of the folder goes",
               Has(taken, "folder", l => l.Equals(InstallDir, StringComparison.OrdinalIgnoreCase), "Failed", "Close the program using it")
               && File.Exists(heldExe) && !File.Exists(Path.Combine(InstallDir, "Readme.md")),
               string.Join("; ", taken.Where(e => e.Location.Equals(InstallDir, StringComparison.OrdinalIgnoreCase))));
        // The link is the removal's target itself, which a held handle keeps
        // from being opened at all, so its reason is the open's error.
        Record("remove: a held all-users desktop link fails and stays",
               Has(taken, "file", l => l.Equals(PublicLink, StringComparison.OrdinalIgnoreCase), "Failed", "Win32 error 32") && File.Exists(PublicLink),
               string.Join("; ", taken.Where(e => e.Location.Equals(PublicLink, StringComparison.OrdinalIgnoreCase))));
        Record("remove: a held folder in usbip-win2's Start menu folder is emptied, and the Start menu folder fails the same way",
               Has(taken, "folder", l => l.Equals(menuDir, StringComparison.OrdinalIgnoreCase), "Failed", "Close the program using it")
               && Directory.Exists(heldDir) && !File.Exists(Path.Combine(heldDir, "inside.txt")) && !File.Exists(Path.Combine(menuDir, "USBip.lnk")),
               string.Join("; ", taken.Where(e => e.Location.Equals(menuDir, StringComparison.OrdinalIgnoreCase))));
        Record("remove: nothing of usbip-win2's is queued for the next start", !PendingRenames().Any(p => p.Contains(@"\USBip", StringComparison.OrdinalIgnoreCase)),
               string.Join(", ", PendingRenames()));
        Record("remove: the rest of that removal went with nothing else failed",
               taken.Where(e => e.Outcome == "Failed").All(e => e.Location.Equals(InstallDir, StringComparison.OrdinalIgnoreCase)
                   || e.Location.Equals(PublicLink, StringComparison.OrdinalIgnoreCase) || e.Location.Equals(menuDir, StringComparison.OrdinalIgnoreCase)),
               string.Join("; ", taken.Where(e => e.Outcome == "Failed")));
        taken = Remove("remove: included, once nothing holds them", preview: false, includeUnproven: true);
        Record("remove: with usbip-win2 included, a removal once nothing holds them takes them, with no step failed",
               taken.All(e => e.Outcome != "Failed") && !Directory.Exists(menuDir) && !File.Exists(PublicLink),
               string.Join("; ", taken.Where(e => e.Outcome == "Failed")));
        Record("remove: the host controller, its package, its service, the Apps entry and the folder are gone",
               Pnp.RootDevnodes(UdeHardwareId).Count == 0 && UdePackages().Count == 0 && !KeyExists(UdeServiceKey)
               && !KeyExists(UninstallKey) && !Directory.Exists(InstallDir), Snapshot());
        Record("remove: and the Apps entry's uninstaller was never run", !File.Exists(Marker));

        // ── R4: a host controller that carried a composite this session ───
        HMContext.InstallUsbipBackend();
        (hc, hub, oem) = Installed();
        Record("remove: the third install answers", hc != null && hub != null && Answers(), $"{hc} {hub}");
        if (hc == null || hub == null) return;
        string? persona = null;
        using (var ctx = new HMContext())
        {
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            using var c = ctx.CreateController(ctx.GetProfile("dualsense-composite")!, "sandbox-composite");
            var sw = Stopwatch.StartNew();
            while (Hid.Paths(0x054C, 0x0CE6).Count == 0 && sw.ElapsedMilliseconds < 30_000) Thread.Sleep(250);
            Pnp.InstallState(30_000);
            persona = Pnp.IdList("USB").FirstOrDefault(id => id.StartsWith(@"USB\VID_054C&PID_0CE6\HM", StringComparison.OrdinalIgnoreCase)
                                                             && string.Equals(Pnp.Parent(id), hub, StringComparison.OrdinalIgnoreCase));
        }
        var gone = Stopwatch.StartNew();
        while (persona != null && Pnp.Present(persona) && gone.ElapsedMilliseconds < 30_000) Thread.Sleep(250);
        Record("remove: the composite's persona is a record under the host controller's root hub, no longer present",
               persona != null && Pnp.Known(persona) && !Pnp.Present(persona), persona ?? "none found");
        if (persona == null) return;
        var subtree = Subtree(persona);
        Note($"remove: the persona's records: {string.Join(", ", subtree)}");

        var used = Remove("remove: used", preview: false, includeUnproven: false);
        Record("remove: the host controller that carried a device since Windows started is kept until a restart, and says so",
               Has(used, "devnode", l => l.StartsWith(hc + " "), "Kept", "since Windows started") && Pnp.Present(hc));
        Record("remove: its package and the install record stay with it",
               UdePackages().Contains(oem!, StringComparer.OrdinalIgnoreCase) && KeyExists(RecordKey));
        // The persona's records are what show the host controller carried a
        // device, so they stay with it until a restart.
        Record("remove: the persona's records stay until a restart, and say so",
               Has(used, "devnode", l => l == persona, "Kept", "since Windows started") && subtree.All(Pnp.Known),
               string.Join(", ", subtree.Where(id => !Pnp.Known(id))));
        Record("remove: HIDMaestro's stamp is off the host controller that stays",
               Pnp.HardwareIds(hc).SequenceEqual(new[] { UdeHardwareId }, StringComparer.OrdinalIgnoreCase), string.Join(";", Pnp.HardwareIds(hc)));
        Record("remove: no step failed", used.All(e => e.Outcome != "Failed"), string.Join("; ", used.Where(e => e.Outcome == "Failed")));
        Record("remove: the host controller still answers", Answers());

        var again = Remove("remove: used, again", preview: false, includeUnproven: false);
        Record("remove: a second removal before a restart keeps the host controller too",
               Has(again, "devnode", l => l.StartsWith(hc + " "), "Kept", "since Windows started") && Pnp.Present(hc) && KeyExists(RecordKey));

        // With usbip-win2 included, what its installer left stays with the
        // host controller it belongs to.
        using (var k = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "USBip version 0.9.8.1");
            k.SetValue("UninstallString", "\"C:\\Program Files\\USBip\\unins000.exe\"");
        }
        Directory.CreateDirectory(InstallDir);
        File.WriteAllText(Path.Combine(InstallDir, "Readme.md"), "planted by the probe");
        File.WriteAllText(PublicLink, "planted by the probe");
        List<Entry> included;
        using (new FileStream(PublicLink, FileMode.Open, FileAccess.Read, FileShare.None))
            included = Remove("remove: used, usbip-win2 included", preview: false, includeUnproven: true);
        Record("remove: with usbip-win2 included, the Apps entry and folder stay with the host controller until a restart",
               Has(included, "devnode", l => l.StartsWith(hc + " "), "Kept", "since Windows started")
               && Has(included, "Apps entry", _ => true, "Kept", "until a restart") && KeyExists(UninstallKey)
               && Has(included, "folder", l => l.Equals(InstallDir, StringComparison.OrdinalIgnoreCase), "Kept", "until a restart") && File.Exists(Path.Combine(InstallDir, "Readme.md")));
        Record("remove: and so does the all-users desktop link, held or not, with no delete tried",
               Has(included, "file", l => l.Equals(PublicLink, StringComparison.OrdinalIgnoreCase), "Kept", "until a restart") && File.Exists(PublicLink),
               string.Join("; ", included.Where(e => e.Location.Equals(PublicLink, StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>--preview, on this machine: what the usbip-win2 part of a
    /// full removal would do, with usbip-win2 included and not, and that
    /// neither preview changed anything a removal acts on.</summary>
    static int Preview()
    {
        Console.WriteLine("=== The usbip-win2 part of a full removal, previewed on this machine ===");
        string before = Snapshot();
        foreach (bool include in new[] { false, true })
        {
            s_lines.Clear();
            var entries = Remove($"preview{(include ? ", usbip-win2 included" : "")}", preview: true, includeUnproven: include);
            foreach (string line in s_lines) Console.WriteLine("  " + line);
            Check($"the preview{(include ? " with usbip-win2 included" : "")} reports nothing removed or failed",
                  entries.All(e => e.Outcome is "WouldRemove" or "Kept"),
                  string.Join("; ", entries.Where(e => e.Outcome is not ("WouldRemove" or "Kept"))));
            Check($"the preview{(include ? " with usbip-win2 included" : "")} never names the root-hub filter for removal",
                  !entries.Any(e => e.Location.Contains("usbip2_filter", StringComparison.OrdinalIgnoreCase) && e.Outcome != "Kept"));
        }
        string after = Snapshot();
        Check("the previews changed nothing", after == before, after == before ? before : $"before {before} / after {after}");
        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>The SDK's own host controller registration, with nothing
    /// after it: its devnode and the owner token it wrote.</summary>
    static (string Instance, string Token) RegisterOnly()
    {
        var installer = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.Usbip.UsbipDriverInstaller", throwOnError: true)!;
        var result = installer.GetMethod("RegisterHostController", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var t = result.GetType();
        return ((string)t.GetField("Item1")!.GetValue(result)!, (string)t.GetField("Item2")!.GetValue(result)!);
    }

    /// <summary>The SDK's own driver install on every present host
    /// controller, from its staged package, as an install runs it.</summary>
    static void ForceDriverAsTheSdkDoes()
    {
        var installer = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.Usbip.UsbipDriverInstaller", throwOnError: true)!;
        string inf = (string)installer.GetMethod("StageUdePackage", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        installer.GetMethod("RunForceDriver", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { inf });
    }

    /// <summary>Where the host put another usbip-win2 release's host
    /// controller package for R01.</summary>
    const string ForeignDir = @"C:\results\foreign";

    static Type Installer => typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.Usbip.UsbipDriverInstaller", throwOnError: true)!;

    /// <summary>The SDK's pinned package, staged as an install stages it.</summary>
    static string StagedPinnedInf() =>
        (string)Installer.GetMethod("StageUdePackage", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

    static string PinnedVer => InfDriverVerOf(ReadInf(StagedPinnedInf()));

    /// <summary>The pinned package's whole DriverVer value, date and version,
    /// as an install record stores it.</summary>
    static string PinnedWholeVer => InfDriverVerWholeOf(ReadInf(StagedPinnedInf()));

    /// <summary>An INF's whole DriverVer value with the spaces around each
    /// part taken out ("09/22/2026,12.1.30.403"), or "".</summary>
    static string InfDriverVerWholeOf(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Split(';')[0].Trim();
            if (!line.StartsWith("DriverVer", StringComparison.OrdinalIgnoreCase) || line.IndexOf('=') < 0) continue;
            return string.Join(",", line[(line.IndexOf('=') + 1)..].Split(',').Select(s => s.Trim()));
        }
        return "";
    }

    /// <summary>The DriverVer version of a published INF.</summary>
    static string InfDriverVer(string published) =>
        InfDriverVerOf(ReadInf(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", published)));

    static string InfDriverVerOf(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Split(';')[0].Trim();
            if (line.StartsWith("DriverVer", StringComparison.OrdinalIgnoreCase)) return line[(line.LastIndexOf(',') + 1)..].Trim();
        }
        return "";
    }

    /// <summary>Let the next create in this process check the record again,
    /// as the next process's first create does.</summary>
    static void ResetRecordCheck()
    {
        Installer.GetField("s_recordChecked", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        Installer.GetField("s_lateBindResult", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, -1);
    }

    static bool RecordChecked() =>
        (bool)Installer.GetField("s_recordChecked", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    /// <summary>What the record check's CompleteLateBind did: -1 not run, 0
    /// recorded nothing, 1 recorded a package.</summary>
    static int LateBindResult() =>
        (int)Installer.GetField("s_lateBindResult", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    static Type RecordType => typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.Usbip.UsbipInstallRecord", throwOnError: true)!;

    /// <summary>A create's transport step on a transport that answers, as
    /// the next process's first create runs it, until its record check has
    /// run: the check runs only on that path (EnsureInstalled), and only when
    /// it takes the transport lock with no wait, which a transport step that
    /// just ended may still hold for a moment. Passes once it ran within
    /// 15 s and CompleteLateBind gave <paramref name="expect"/> (1 recorded,
    /// 0 recorded nothing), recorded as <paramref name="label"/>.</summary>
    static void CreateOnWorkingTransport(string label, int expect)
    {
        if (!Answers()) { Record(label, false, "the transport did not answer within 30 s"); return; }
        ResetRecordCheck();
        var sw = Stopwatch.StartNew();
        int creates = 0;
        while (sw.ElapsedMilliseconds < 15_000)
        {
            HMContext.InstallUsbipBackend();
            creates++;
            if (RecordChecked())
            {
                int result = LateBindResult();
                Record(label, result == expect, result == -1
                    ? $"the record check ended before CompleteLateBind ran, after {creates} create(s)"
                    : $"CompleteLateBind gave {result} after {creates} create(s)");
                return;
            }
            Thread.Sleep(250);
        }
        Record(label, false, $"the record check never ran in {creates} create(s): the transport lock stayed held");
    }

    /// <summary>usbip-win2's all-users desktop link (setup.iss's
    /// desktopicon task).</summary>
    static string PublicLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "USBip.lnk");

    /// <summary>A folder held open as another program holds one: read and
    /// write sharing, no delete sharing, so it cannot be deleted while the
    /// handle is open.</summary>
    static SafeFileHandle OpenFolderWithoutDeleteSharing(string path)
    {
        var h = CreateFileW(path, 0x80000000 /* GENERIC_READ */, 0x1 | 0x2 /* FILE_SHARE_READ | FILE_SHARE_WRITE */, IntPtr.Zero,
                            3 /* OPEN_EXISTING */, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);
        if (h.IsInvalid) throw new IOException($"{path} could not be opened (Win32 error {Marshal.GetLastWin32Error()})");
        return h;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    /// <summary>The paths queued for deletion at the next start
    /// (Session Manager's PendingFileRenameOperations).</summary>
    static string[] PendingRenames()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
        return k?.GetValue("PendingFileRenameOperations") as string[] ?? Array.Empty<string>();
    }

    static void PlantAppsEntry()
    {
        using var k = Registry.LocalMachine.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "USBip version 0.9.8.1");
        k.SetValue("DisplayVersion", "0.9.8.1");
        k.SetValue("UninstallString", "\"C:\\Program Files\\USBip\\unins000.exe\"");
    }

    static string? RecordValue(string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(RecordKey);
        return k?.GetValue(name) as string;
    }

    /// <summary>The record an install writes before its driver install, on a
    /// machine with no usbip-win2 Apps entry. With no DriverVer it is a record
    /// from before the record kept one. <paramref name="before"/> is the
    /// packages the store held before, none by default.</summary>
    static void WriteRecordWithNoDriver(string instance, string token, string? driverVer = null, string[]? before = null)
    {
        using var k = Registry.LocalMachine.CreateSubKey(RecordKey);
        k.SetValue("Instance", instance);
        k.SetValue("Token", token);
        k.SetValue("PackageAdded", 0, RegistryValueKind.DWord);
        k.SetValue("PackagesBefore", before ?? Array.Empty<string>(), RegistryValueKind.MultiString);
        k.DeleteValue("OemInf", throwOnMissingValue: false);
        k.SetValue("AppsEntry", "");
        if (driverVer != null) k.SetValue("DriverVer", driverVer);
        else k.DeleteValue("DriverVer", throwOnMissingValue: false);
        k.SetValue("Version", "0.9.8.1");
        k.SetValue("InstalledUtc", DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        k.SetValue("SdkVersion", "probe");
    }

    /// <summary>A devnode's record and every record below it, by each
    /// record's parent.</summary>
    static List<string> Subtree(string root)
    {
        var parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in Pnp.IdList(null))
            if (Pnp.Parent(id) is string p) parents[id] = p;
        var found = new List<string> { root };
        for (int i = 0; i < found.Count; i++)
            foreach (var kv in parents)
                if (string.Equals(kv.Value, found[i], StringComparison.OrdinalIgnoreCase) && !found.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                    found.Add(kv.Key);
        return found;
    }
}
