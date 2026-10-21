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
using System.Threading;
using HIDMaestro;
using Microsoft.Win32;

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
        Record("remove: nor one whose Apps entry usbip-win2's installer wrote after HIDMaestro's install",
               Has(vendor, "devnode", l => l.StartsWith(hc + " "), "Kept", "after HIDMaestro's install"));

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

        var taken = Remove("remove: included", preview: false, includeUnproven: true);
        Record("remove: with usbip-win2 included, no step failed", taken.All(e => e.Outcome != "Failed"), string.Join("; ", taken.Where(e => e.Outcome == "Failed")));
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
        var included = Remove("remove: used, usbip-win2 included", preview: false, includeUnproven: true);
        Record("remove: with usbip-win2 included, the Apps entry and folder stay with the host controller until a restart",
               Has(included, "devnode", l => l.StartsWith(hc + " "), "Kept", "since Windows started")
               && Has(included, "Apps entry", _ => true, "Kept", "until a restart") && KeyExists(UninstallKey) && Directory.Exists(InstallDir));
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

    /// <summary>The record an install writes before its driver install.</summary>
    static void WriteRecordWithNoDriver(string instance, string token)
    {
        using var k = Registry.LocalMachine.CreateSubKey(RecordKey);
        k.SetValue("Instance", instance);
        k.SetValue("Token", token);
        k.SetValue("PackageAdded", 0, RegistryValueKind.DWord);
        k.SetValue("PackagesBefore", Array.Empty<string>(), RegistryValueKind.MultiString);
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
