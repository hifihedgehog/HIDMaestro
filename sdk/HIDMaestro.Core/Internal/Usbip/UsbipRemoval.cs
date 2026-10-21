using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using HIDMaestro.Internal.Uninstall;
using Microsoft.Win32;

namespace HIDMaestro.Internal.Usbip;

/// <summary>The usbip-win2 part of a full removal of HIDMaestro. It runs
/// after HIDMaestro's own imports are unplugged and before HIDMaestro's
/// staged files are deleted.
///
/// <para>It never touches usbip-win2's root-hub filter, whose removal
/// restarts every USB root hub. It removes the host controller, its driver
/// package and the rest of usbip-win2 only when HIDMaestro's install record
/// shows HIDMaestro installed it, or when the caller's
/// <c>includeUnproven</c> asks for it, and in either case only
/// when no other program uses usbip-win2.</para>
///
/// <para>The steps follow usbip-win2's own uninstaller and leave out its
/// filter step. Its [UninstallRun] removes the host controller devnodes
/// with "devnode.exe remove ROOT\USBIP_WIN2\UDE root", which calls
/// DiUninstallDevice on each (userspace/devnode/main.cpp:297-371), then
/// deletes the host controller's packages, then the filter's (setup.iss at
/// v.0.9.8.1, 181 and 320-424). The filter's
/// <c>pnputil /delete-driver /uninstall</c> reinstalls every
/// USB\ROOT_HUB30 the extension INF matched
/// (drivers/ude_filter/usbip2_filter.inf:32-43), which restarts each hub
/// (drivers/ude_filter/readme.txt). The filter stays: its package, its
/// service and its registration on each hub. It attaches to no hub that is
/// not usbip-win2's own (drivers/ude_filter/device.cpp:310-324).</para>
///
/// <para>A host controller that has carried a device since Windows started
/// is left until a restart: forcing a driver onto one, measured
/// 2026-09-20 on 0.9.7.7 and 0.9.8.0, timed out in its query-remove after
/// four minutes and left Plug and Play waiting on it until Windows
/// restarted. The removal says so, and a run after the restart finishes
/// it.</para></summary>
internal static partial class UsbipRemoval
{
    /// <summary>The scheduled task usbip-win2 0.9.8.0 and later registers
    /// (setup.iss at v.0.9.8.1, 248-288).</summary>
    internal const string DetachTaskName = "USBip Detach All On Reboot Or Shutdown";

    /// <summary>The synthetic serial a persona without a captured one
    /// serves: "HM" and twelve hex digits of its identity's hash
    /// (DeviceIdentity.SyntheticSerial).</summary>
    private static readonly Regex SyntheticSerial = new("^HM[0-9A-F]{12}$", RegexOptions.CultureInvariant);

    private const string FilterInf = "usbip2_filter.inf";
    private const string UdeServiceKey = @"SYSTEM\CurrentControlSet\Services\usbip2_ude";
    private const string FilterServiceKey = @"SYSTEM\CurrentControlSet\Services\usbip2_filter";

    /// <summary>Remove usbip-win2's pieces as the class describes, or with
    /// <paramref name="preview"/> report what a removal would do and change
    /// nothing. A preview starts no process and writes nothing.
    ///
    /// <para>A removal runs on a thread of its own that takes the transport
    /// lock and then, step by step, the PnP lock. The caller must hold
    /// neither: that thread would wait for the caller, and the caller for
    /// it.</para></summary>
    /// <exception cref="InvalidOperationException">The calling thread holds
    /// the PnP lock or runs transport work, and <paramref name="preview"/> is
    /// false.</exception>
    internal static IReadOnlyList<UninstallEntry> Remove(bool preview, bool includeUnproven)
    {
        if (!preview && DeviceManager.PnpGateHeldHere)
            throw new InvalidOperationException("UsbipRemoval.Remove was called with the PnP lock held. Its own thread takes that lock, so the call would never return.");
        if (!preview && UsbipDriverInstaller.TransportLockHeldHere)
            throw new InvalidOperationException("UsbipRemoval.Remove was called from transport work, which holds the transport lock its own thread takes, so the call would never return.");
        var entries = new List<UninstallEntry>();
        try
        {
            if (preview) Run(entries, preview: true, includeUnproven);
            // Under the transport lock, so no program installs or repairs
            // the transport while it is taken apart.
            else UsbipDriverInstaller.TransportWork.Run(_ => { Run(entries, preview: false, includeUnproven); return true; }, null);
        }
        catch (Exception ex)
        {
            entries.Add(new("usbip-win2", "USB/IP transport", UninstallOutcome.Failed, $"{ex.GetType().Name}: {ex.Message}"));
        }
        return entries;
    }

    private static void Run(List<UninstallEntry> entries, bool preview, bool includeUnproven)
    {
        // ── HIDMaestro's own imports ──────────────────────────────────────
        var imports = UsbipBackend.ClassifiedImports();
        if (imports.Exists(i => i.Ours))
        {
            if (!preview) UsbipBackend.DetachAllOwned();
            var after = preview ? imports : UsbipBackend.ClassifiedImports();
            foreach (var i in imports)
            {
                if (!i.Ours) continue;
                bool left = after.Exists(a => a.Ours && a.BusId == i.BusId && a.Service == i.Service);
                entries.Add(new("USB/IP import", $"vhci port {i.Port}, {i.Host}:{i.Service} bus {i.BusId}",
                    preview ? UninstallOutcome.WouldRemove : left ? UninstallOutcome.Failed : UninstallOutcome.Removed,
                    !preview && left ? "still attached after the detach" : null));
            }
            if (!preview) imports = UsbipBackend.ClassifiedImports();
        }

        // ── What else uses usbip-win2 ─────────────────────────────────────
        var users = new List<string>();
        foreach (var i in imports)
            if (!i.Ours) users.Add($"a device attached from {i.Host}:{i.Service} (bus {i.BusId})");
        users.AddRange(OtherConsumers());

        var controllers = UsbipDriverInstaller.HostControllers();
        var record = UsbipInstallRecord.Read();
        bool recordKey = UsbipInstallRecord.Exists();
        string? recordMismatch = record == null ? null : RecordMismatch(record, controllers);
        bool proven = record != null && recordMismatch == null;
        bool take = users.Count == 0 && (proven || includeUnproven);

        string keptReason = users.Count > 0
            ? "usbip-win2 is in use: " + string.Join("; ", users) + "."
            : record == null
                ? "no install record shows HIDMaestro installed it. A removal with usbip-win2 included takes it."
                : $"HIDMaestro's install record no longer matches it ({recordMismatch}). A removal with usbip-win2 included takes it.";

        // Whether a host controller has carried a device since Windows
        // started is read from the records of the devices it carried
        // (HostControllerUsedSinceBoot). While one has, those records stay
        // until a restart: without them a second removal, or the SDK's move
        // off an older host controller, would take the controller for one
        // that is safe to remove.
        string? used = null;
        if (controllers.Exists(c => c.Present) && UsbipDriverInstaller.UsedSinceBoot(out string why)) used = why;

        // ── Persona phantoms, before the hubs they name go ────────────────
        var hubs = UsbHubsOf(controllers);
        RemovePersonaPhantoms(entries, hubs, preview, used == null ? null :
            "a host controller has carried a device since Windows started, which these records show. They stay until a restart, " +
            "and a removal after it takes them.");

        // ── Host controllers ──────────────────────────────────────────────
        var targets = new List<(string Id, bool Present)>();
        if (take)
            foreach (var c in controllers)
                if (includeUnproven || string.Equals(c.Id, record!.Instance, StringComparison.OrdinalIgnoreCase))
                    targets.Add(c);

        var stayed = new List<string>();
        foreach (var c in controllers)
        {
            bool target = targets.Exists(t => t.Id == c.Id);
            string where = $"{c.Id} ({(c.Present ? "present" : "not present")})";
            if (!target)
            {
                entries.Add(new("devnode", where, UninstallOutcome.Kept, keptReason));
                if (c.Present) stayed.Add(c.Id);
                continue;
            }
            if (c.Present && used != null)
            {
                entries.Add(new("devnode", where, UninstallOutcome.Kept,
                    $"the host controller has carried a device since Windows started ({used}). Removing it now can leave " +
                    "Plug and Play waiting on it until Windows restarts. Run the removal again after a restart."));
                stayed.Add(c.Id);
                continue;
            }
            if (preview) { entries.Add(new("devnode", where, UninstallOutcome.WouldRemove)); continue; }
            var (outcome, reason) = c.Present ? RemoveHostController(c.Id)
                : RemoveRecord(c.Id) ? (UninstallOutcome.Removed, null) : (UninstallOutcome.Failed, "its record could not be removed");
            entries.Add(new("devnode", where, outcome, reason));
            if (outcome != UninstallOutcome.Removed && UsbipDriverInstaller.IsPresent(c.Id)) stayed.Add(c.Id);
        }

        // The removed host controllers' root hubs keep a record of their own.
        foreach (var hub in hubs)
        {
            if (!targets.Exists(t => string.Equals(t.Id, hub.Controller, StringComparison.OrdinalIgnoreCase))) continue;
            if (stayed.Contains(hub.Controller)) continue;
            if (preview) { entries.Add(new("devnode", hub.Hub, UninstallOutcome.WouldRemove, "the root hub of " + hub.Controller)); continue; }
            if (!UsbipDriverInstaller.IsKnown(hub.Hub)) continue;
            bool ok = !UsbipDriverInstaller.IsPresent(hub.Hub) && RemoveRecord(hub.Hub);
            entries.Add(new("devnode", hub.Hub, ok ? UninstallOutcome.Removed : UninstallOutcome.Failed,
                ok ? "the root hub of " + hub.Controller : "the root hub of " + hub.Controller + " is still present"));
        }

        // ── Driver packages ───────────────────────────────────────────────
        List<string>? packages = null;
        try { packages = UsbipDriverInstaller.UdePackageNames(); }
        catch (InvalidOperationException ex) { entries.Add(new("driver package", UsbipDriverInstaller.ProductInf, UninstallOutcome.Failed, ex.Message)); }
        if (packages != null)
        {
            // A package a devnode still uses, present or not, stays: the plain
            // delete refuses it anyway.
            var inUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in UsbipDriverInstaller.HostControllers())
                if (UsbipDriverInstaller.DriverInfPathOf(c.Id) is string oem && !(preview && targets.Exists(t => t.Id == c.Id) && !stayed.Contains(c.Id)))
                    inUse.Add(oem);
            foreach (string oem in packages)
            {
                bool covered = take && (includeUnproven || RecordCovers(record!, oem));
                string where = $"{oem} ({UsbipDriverInstaller.ProductInf})";
                if (!covered)
                {
                    entries.Add(new("driver package", where, UninstallOutcome.Kept,
                        take ? "it was in the driver store before HIDMaestro's install. A removal with usbip-win2 included takes it." : keptReason));
                    continue;
                }
                if (inUse.Contains(oem))
                {
                    entries.Add(new("driver package", where, UninstallOutcome.Kept, "a host controller that stays uses it."));
                    continue;
                }
                if (preview) { entries.Add(new("driver package", where, UninstallOutcome.WouldRemove)); continue; }
                var (rc, output) = PnputilHelper.Run($"/delete-driver {oem}", timeoutMs: 60_000);
                bool deleted = PnputilHelper.PlainDeleteDone(rc, output);
                entries.Add(new("driver package", where, deleted ? UninstallOutcome.Removed : UninstallOutcome.Failed,
                    deleted ? null : $"pnputil /delete-driver returned {rc}: {output.Trim()}"));
            }
        }

        // ── The host controller's service ─────────────────────────────────
        bool serviceExists = KeyExists(UdeServiceKey);
        if (serviceExists)
        {
            bool nothingLeft = preview
                ? take && stayed.Count == 0 && controllers.TrueForAll(c => targets.Exists(t => t.Id == c.Id))
                  && packages != null && packages.TrueForAll(p => includeUnproven || RecordCovers(record!, p))
                : take && UsbipDriverInstaller.HostControllers().Count == 0 && SafeUdePackageCount() == 0;
            if (nothingLeft)
            {
                if (preview) entries.Add(new("service", "usbip2_ude", UninstallOutcome.WouldRemove));
                else
                {
                    var (rc, output) = RunSystemTool("sc.exe", "delete usbip2_ude");
                    // 1060: no such service, 1072: marked for deletion, gone
                    // once nothing holds it open.
                    entries.Add(rc == 0 || rc == 1060
                        ? new("service", "usbip2_ude", UninstallOutcome.Removed)
                        : rc == 1072
                            ? new("service", "usbip2_ude", UninstallOutcome.RemovedAtRestart, "marked for deletion")
                            : new("service", "usbip2_ude", UninstallOutcome.Failed, $"sc delete returned {rc}: {output.Trim()}"));
                }
            }
            else
            {
                // usbip-win2 stays. VerifierOn=1 and VerboseOn=1, the KMDF
                // debug pair usbip-win2 0.9.7.5's INF writes, go from its
                // service key unless the installed driver's INF writes them
                // itself. Nothing else there is touched.
                var stale = UsbipDriverInstaller.StaleVerifierValues();
                if (stale.Count > 0)
                {
                    var texts = new List<string>();
                    bool readable = true;
                    foreach (var c in UsbipDriverInstaller.HostControllers())
                    {
                        if (!c.Present) continue;
                        if (UsbipDriverInstaller.DriverInfPathOf(c.Id) is not string oem) { readable = false; break; }
                        try { texts.Add(UsbipDriverInstaller.ReadInfText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", oem))); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { readable = false; break; }
                    }
                    bool written = !readable || texts.Exists(UsbipDriverInstaller.InfWritesWdfDebugValues);
                    foreach (string v in stale)
                    {
                        string where = $@"HKLM\{UdeServiceKey}\Parameters\Wdf\{v}=1";
                        if (written) entries.Add(new("registry value", where, UninstallOutcome.Kept, "the installed driver's INF writes it."));
                        else if (preview) entries.Add(new("registry value", where, UninstallOutcome.WouldRemove, "a KMDF debug value usbip-win2 0.9.7.5's INF writes, which keeps the KMDF verifier on"));
                    }
                    if (!written && !preview)
                    {
                        var cleared = UsbipDriverInstaller.ClearStaleVerifier(texts, "in the removal");
                        foreach (string v in stale)
                            entries.Add(new("registry value", $@"HKLM\{UdeServiceKey}\Parameters\Wdf\{v}=1",
                                cleared.Contains(v) ? UninstallOutcome.Removed : UninstallOutcome.Failed,
                                cleared.Contains(v) ? "takes effect at the driver's next load" : "could not be deleted"));
                    }
                }
            }
        }

        // ── What usbip-win2's own installer left ──────────────────────────
        // HIDMaestro's own install adds none of these. An SDK through v2.0.0
        // ran usbip-win2's installer, and so do other programs, so only a
        // removal that includes usbip-win2 takes them.
        // While a host controller this removal would take stays until a
        // restart, they stay with it: its uninstaller and tools go with the
        // driver they belong to.
        bool deferred = targets.Exists(t => stayed.Contains(t.Id));
        VendorPieces(entries, preview, take && includeUnproven && !deferred,
            !take ? keptReason
            : deferred ? "the host controller stays until a restart, and a removal after it takes this with it."
            : "HIDMaestro's install record does not cover it. A removal with usbip-win2 included takes it.");

        // ── The filter ────────────────────────────────────────────────────
        List<PnputilHelper.DriverRecord>? filters = PnputilHelper.TryEnumerateDrivers(n => string.Equals(n, FilterInf, StringComparison.OrdinalIgnoreCase));
        if (filters != null)
            foreach (var f in filters)
                entries.Add(new("driver package", $"{f.PublishedName} ({FilterInf})", UninstallOutcome.Kept, FilterReason));
        if (KeyExists(FilterServiceKey))
            entries.Add(new("service", "usbip2_filter", UninstallOutcome.Kept, FilterReason));

        // ── HIDMaestro's stamp on host controllers that stay ──────────────
        foreach (string id in stayed)
        {
            if (!Array.Exists(UsbipDriverInstaller.HardwareIdsOf(id), h => string.Equals(h, "ROOT\\HIDMAESTRO_UDE", StringComparison.OrdinalIgnoreCase))) continue;
            string where = $@"{id}, ROOT\HIDMAESTRO_UDE";
            if (preview) { entries.Add(new("hardware id", where, UninstallOutcome.WouldRemove)); continue; }
            bool ok = UsbipDriverInstaller.Unstamp(id);
            entries.Add(new("hardware id", where, ok ? UninstallOutcome.Removed : UninstallOutcome.Failed, ok ? null : "the hardware ids could not be written"));
        }

        // ── The install record ────────────────────────────────────────────
        if (recordKey)
        {
            string where = $@"HKLM\{UsbipInstallRecord.Key}";
            bool keep = proven && (users.Count > 0 || targets.Exists(t => stayed.Contains(t.Id)) || !take);
            if (keep)
                entries.Add(new("registry key", where, UninstallOutcome.Kept,
                    "it shows HIDMaestro installed the host controller that stays, so a later removal can take it."));
            else if (preview) entries.Add(new("registry key", where, UninstallOutcome.WouldRemove));
            else
            {
                UsbipInstallRecord.Delete();
                entries.Add(new("registry key", where, UsbipInstallRecord.Exists() ? UninstallOutcome.Failed : UninstallOutcome.Removed));
            }
        }

        // ── HIDMaestro's own usbip folders ────────────────────────────────
        foreach (string dir in ResidueFolders())
            DeleteFolder(entries, dir, preview);
    }

    private const string FilterReason =
        "usbip-win2's root-hub filter. Removing it restarts every USB root hub, so HIDMaestro never touches it. " +
        "It attaches to no USB root hub but usbip-win2's own.";

    /// <summary>Why a record does not prove the host controller it names
    /// is HIDMaestro's install, or null when it does. It proves it when the
    /// devnode is still there with usbip-win2's id and the owner token the
    /// install wrote on it, which a devnode another installer registers on
    /// the same instance id does not carry, when usbip-win2's own installer
    /// has not written its Apps entry since, and when the devnode runs the
    /// package the install put on it, or, for an install cut short before its
    /// driver, runs none.</summary>
    private static string? RecordMismatch(UsbipInstallRecord record, List<(string Id, bool Present)> controllers)
    {
        if (!controllers.Exists(c => string.Equals(c.Id, record.Instance, StringComparison.OrdinalIgnoreCase)))
            return $"{record.Instance} is gone";
        if (record.Token == null || UsbipDriverInstaller.OwnerTokenOf(record.Instance) != record.Token)
            return $"{record.Instance} is not the devnode HIDMaestro registered";
        if (AppsEntryWrittenUtc() is DateTime written && written > record.InstalledUtc)
            return $"usbip-win2's installer wrote its Apps entry at {written:yyyy-MM-dd HH:mm}Z, after HIDMaestro's install";
        string? now = UsbipDriverInstaller.DriverInfPathOf(record.Instance);
        if (record.OemInf == null)
            return now == null ? null : $"{record.Instance} runs {now}, which HIDMaestro's install did not record";
        if (!string.Equals(now, record.OemInf, StringComparison.OrdinalIgnoreCase))
            return $"{record.Instance} now runs {now ?? "no driver"}, not {record.OemInf}";
        return null;
    }

    /// <summary>When usbip-win2's installer last wrote its Apps entry, the
    /// latest of the views it is found in, or null when there is none.</summary>
    private static DateTime? AppsEntryWrittenUtc()
    {
        DateTime? latest = null;
        foreach (var (hive, view, _) in AppsViews())
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(UsbipDriverInstaller.UninstallEntryKey);
                if (k == null) continue;
                if (RegQueryInfoKeyW(k.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out long fileTime) != 0) continue;
                var at = DateTime.FromFileTimeUtc(fileTime);
                if (latest == null || at > latest) latest = at;
            }
            catch { }
        }
        return latest;
    }

    /// <summary>Whether a usbip2_ude package is the install record's to take:
    /// one the devnode ran when the install returned and that was not in the
    /// driver store before it, or, for an install cut short before it
    /// recorded a package, any not in the store before it.</summary>
    private static bool RecordCovers(UsbipInstallRecord record, string oem)
    {
        if (Array.Exists(record.PackagesBefore, p => string.Equals(p, oem, StringComparison.OrdinalIgnoreCase))) return false;
        return record.OemInf == null || string.Equals(oem, record.OemInf, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Other programs that install usbip-win2 and use it: Handheld
    /// Companion, whose installer installs usbip-win2 (HandheldCompanion.iss,
    /// Dependency_AddUSBip), and VIIPER, which DS4Windows can use as its
    /// backend and whose install script installs usbip-win2 into
    /// %LOCALAPPDATA%\VIIPER (scripts/install.ps1, 66-160).</summary>
    internal static List<string> OtherConsumers()
    {
        var found = new List<string>();
        foreach (var (hive, view, label) in AppsViews())
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var apps = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (apps == null) continue;
                foreach (string name in apps.GetSubKeyNames())
                {
                    using var k = apps.OpenSubKey(name);
                    if (k?.GetValue("DisplayName") is not string display) continue;
                    if (display.StartsWith("Handheld Companion", StringComparison.OrdinalIgnoreCase) ||
                        display.StartsWith("VIIPER", StringComparison.OrdinalIgnoreCase))
                        found.Add($"{display} is installed ({label} Apps entry)");
                }
            }
            catch { }
        }
        foreach (string profile in ProfileFolders())
        {
            string exe = Path.Combine(profile, "AppData", "Local", "VIIPER", "viiper.exe");
            if (File.Exists(exe)) found.Add($"VIIPER is installed ({exe})");
        }
        foreach (var p in Process.GetProcessesByName("viiper"))
        {
            found.Add($"VIIPER is running (process {p.Id})");
            p.Dispose();
        }
        return found;
    }

    /// <summary>Every account's profile folder, SYSTEM's and the service
    /// accounts' included: each ProfileImagePath under the ProfileList key,
    /// which holds the profile of every account that has signed in, wherever
    /// the profiles live. A process running as SYSTEM finds the same set as
    /// one running as a user.</summary>
    private static List<string> ProfileFolders()
    {
        var found = new List<string>();
        try
        {
            using var list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (list == null) return found;
            foreach (string sid in list.GetSubKeyNames())
            {
                using var k = list.OpenSubKey(sid);
                if (k?.GetValue("ProfileImagePath") is not string path || path.Length == 0) continue;
                string full = Environment.ExpandEnvironmentVariables(path);
                if (Directory.Exists(full) && !found.Contains(full, StringComparer.OrdinalIgnoreCase)) found.Add(full);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        return found;
    }

    private static IEnumerable<(RegistryHive, RegistryView, string)> AppsViews()
    {
        yield return (RegistryHive.LocalMachine, RegistryView.Registry64, "64-bit");
        yield return (RegistryHive.LocalMachine, RegistryView.Registry32, "32-bit");
        yield return (RegistryHive.CurrentUser, RegistryView.Default, "current user's");
    }

    // ── Persona phantoms ─────────────────────────────────────────────────

    /// <summary>The root hub of each usbip-win2 host controller, present or
    /// not: a USB\ROOT_HUB30 devnode whose parent is the controller.</summary>
    private static List<(string Hub, string Controller)> UsbHubsOf(List<(string Id, bool Present)> controllers)
    {
        var hubs = new List<(string, string)>();
        foreach (string id in UsbipDriverInstaller.DeviceIdList("USB"))
        {
            if (!id.StartsWith(@"USB\ROOT_HUB", StringComparison.OrdinalIgnoreCase)) continue;
            string? parent = UsbipDriverInstaller.ParentOf(id);
            if (parent != null && controllers.Exists(c => string.Equals(c.Id, parent, StringComparison.OrdinalIgnoreCase)))
                hubs.Add((id, parent));
        }
        return hubs;
    }

    /// <summary>Uninstall the records of composite personas HIDMaestro
    /// plugged in through usbip-win2 and that have left: a USB device that
    /// is not present, whose last parent is a usbip-win2 root hub, and whose
    /// serial is one the SDK makes up, with every record below it. A real
    /// device of the same VID, PID and serial would share the instance id,
    /// and only a made-up serial rules that out. A persona with a captured
    /// serial is reported and left, and so is every persona while
    /// <paramref name="keepReason"/> says why.</summary>
    private static void RemovePersonaPhantoms(List<UninstallEntry> entries, List<(string Hub, string Controller)> hubs, bool preview, string? keepReason)
    {
        if (hubs.Count == 0) return;
        var hubIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hubs) hubIds.Add(h.Hub);

        var personas = new List<string>();
        foreach (string id in UsbipDriverInstaller.DeviceIdList("USB"))
        {
            string[] parts = id.Split('\\');
            if (parts.Length != 3 || parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase)) continue;
            if (!parts[1].StartsWith("VID_", StringComparison.OrdinalIgnoreCase)) continue;
            if (UsbipDriverInstaller.IsPresent(id)) continue;
            string? parent = UsbipDriverInstaller.ParentOf(id);
            if (parent == null || !hubIds.Contains(parent)) continue;
            if (!SyntheticSerial.IsMatch(parts[2]))
            {
                entries.Add(new("devnode", id, UninstallOutcome.Kept,
                    "a composite persona's record whose serial is a real device's: a real device of that VID, PID and serial shares it."));
                continue;
            }
            if (keepReason != null) entries.Add(new("devnode", id, UninstallOutcome.Kept, keepReason));
            else personas.Add(id);
        }
        if (personas.Count == 0) return;

        // Every record's parent, once, for the subtrees.
        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in UsbipDriverInstaller.DeviceIdList(null))
        {
            string? parent = UsbipDriverInstaller.ParentOf(id);
            if (parent == null) continue;
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = new List<string>();
            list.Add(id);
        }

        foreach (string persona in personas)
        {
            // Deepest first, so no record is removed while one below it
            // still names it.
            var order = new List<string>();
            var stack = new Stack<(string Id, bool Expanded)>();
            stack.Push((persona, false));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (stack.Count > 0)
            {
                var (id, expanded) = stack.Pop();
                if (expanded) { order.Add(id); continue; }
                if (!seen.Add(id)) continue;
                stack.Push((id, true));
                if (children.TryGetValue(id, out var kids))
                    foreach (string k in kids) stack.Push((k, false));
            }
            string? live = order.Find(UsbipDriverInstaller.IsPresent);
            if (live != null)
            {
                entries.Add(new("devnode", persona, UninstallOutcome.Kept, $"{live}, below it, is present."));
                continue;
            }
            foreach (string id in order)
            {
                string why = id == persona ? "a composite persona's record" : "below " + persona;
                if (preview) { entries.Add(new("devnode", id, UninstallOutcome.WouldRemove, why)); continue; }
                bool ok = RemoveRecord(id);
                entries.Add(new("devnode", id, ok ? UninstallOutcome.Removed : UninstallOutcome.Failed, ok ? why : why + ", not removed"));
            }
        }
    }

    // ── Devnode removal ──────────────────────────────────────────────────

    /// <summary>Remove a present host controller devnode and its subtree,
    /// out of process with a timeout: <c>pnputil /remove-device</c>, the
    /// documented form of the DiUninstallDevice call devnode.exe makes
    /// (main.cpp:325-336). Removed when it is gone, RemovedAtRestart when
    /// pnputil answers 3010 (ERROR_SUCCESS_REBOOT_REQUIRED: the removal
    /// finishes when Windows next starts), Failed otherwise.</summary>
    private static (UninstallOutcome Outcome, string? Reason) RemoveHostController(string instanceId)
    {
        int rc;
        string output;
        using (var pnp = DeviceManager.HoldPnpLock())
            (rc, output) = PnputilHelper.Run($"/remove-device \"{instanceId}\"", timeoutMs: 90_000);
        var sw = Stopwatch.StartNew();
        while (UsbipDriverInstaller.IsKnown(instanceId) && sw.ElapsedMilliseconds < TimeoutScale.Apply(5_000))
            Thread.Sleep(100);
        if (!UsbipDriverInstaller.IsKnown(instanceId)) return (UninstallOutcome.Removed, null);
        if (!UsbipDriverInstaller.IsPresent(instanceId) && RemoveRecord(instanceId)) return (UninstallOutcome.Removed, null);
        if (rc == 3010) return (UninstallOutcome.RemovedAtRestart, "Windows finishes the removal when it next starts");
        return (UninstallOutcome.Failed, $"pnputil /remove-device returned {rc}: {output.Trim()}");
    }

    /// <summary>Uninstall the record of a devnode that is not present,
    /// under the PnP lock, as the phantom cleanup does
    /// (DeviceManager.RemoveAccumulatedHmPhantoms). False when it is present
    /// or the uninstall failed.</summary>
    private static bool RemoveRecord(string instanceId)
    {
        using var pnp = DeviceManager.HoldPnpLock();
        if (UsbipDriverInstaller.IsPresent(instanceId)) return false;
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) return false;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data)) return false;
            return SetupDiRemoveDevice(set, ref data);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // ── usbip-win2's own installer's pieces ──────────────────────────────

    private static void VendorPieces(List<UninstallEntry> entries, bool preview, bool remove, string keptReason)
    {
        // The detach task, found by the definition file Task Scheduler keeps
        // for each task in its root folder, where the installer registers
        // it. Reading the file starts no process, so a preview changes
        // nothing: a tool started through PnputilHelper makes a child
        // temporary folder under %ProgramFiles%\HIDMaestro.
        if (File.Exists(Path.Combine(Environment.SystemDirectory, "Tasks", DetachTaskName)))
        {
            if (!remove) entries.Add(new("scheduled task", DetachTaskName, UninstallOutcome.Kept, keptReason));
            else if (preview) entries.Add(new("scheduled task", DetachTaskName, UninstallOutcome.WouldRemove));
            else
            {
                var (rc, output) = RunSystemTool("schtasks.exe", $"/delete /tn \"{DetachTaskName}\" /f");
                entries.Add(new("scheduled task", DetachTaskName, rc == 0 ? UninstallOutcome.Removed : UninstallOutcome.Failed,
                    rc == 0 ? null : $"schtasks /delete returned {rc}: {output.Trim()}"));
            }
        }

        // The Apps entry. Its uninstaller is never run: it removes the
        // filter, which restarts every USB root hub.
        foreach (var (hive, view, label) in AppsViews())
        {
            string where = $@"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\{UsbipDriverInstaller.UninstallEntryKey} ({label})";
            bool exists;
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(UsbipDriverInstaller.UninstallEntryKey);
                exists = k != null;
            }
            catch { exists = false; }
            if (!exists) continue;
            if (!remove) { entries.Add(new("Apps entry", where, UninstallOutcome.Kept, keptReason)); continue; }
            if (preview) { entries.Add(new("Apps entry", where, UninstallOutcome.WouldRemove, "its uninstaller is not run")); continue; }
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                root.DeleteSubKeyTree(UsbipDriverInstaller.UninstallEntryKey, throwOnMissingSubKey: false);
                entries.Add(new("Apps entry", where, UninstallOutcome.Removed, "its uninstaller is not run"));
            }
            catch (Exception ex) { entries.Add(new("Apps entry", where, UninstallOutcome.Failed, ex.Message)); }
        }

        // The program folder, the Start menu folder and the desktop
        // shortcut (setup.iss: DefaultDirName {autopf}\USBip, the group and
        // the desktopicon task).
        string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "USBip");
        string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "USBip");
        string link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "USBip.lnk");
        foreach (string dir in new[] { pf, menu })
        {
            if (!Directory.Exists(dir)) continue;
            if (!remove) entries.Add(new("folder", dir, UninstallOutcome.Kept, keptReason));
            else DeleteFolder(entries, dir, preview);
        }
        if (File.Exists(link))
        {
            if (!remove) entries.Add(new("file", link, UninstallOutcome.Kept, keptReason));
            else if (preview) entries.Add(new("file", link, UninstallOutcome.WouldRemove));
            else entries.Add(DeleteFile(link));
        }
    }

    // ── HIDMaestro's residue ─────────────────────────────────────────────

    /// <summary>HIDMaestro's own usbip folders: v1.12.0's host controller
    /// staging under %ProgramData%, each staging version's installer and
    /// host controller folders, and the %TEMP% folders through v1.12.0 in
    /// every profile, SYSTEM's and Windows' own included
    /// (ProtectedStaging's notes on where each lived).</summary>
    private static List<string> ResidueFolders()
    {
        var found = new List<string>();
        string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HIDMaestro", "usbip");
        if (Directory.Exists(data)) found.Add(data);
        string staging = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HIDMaestro", "Staging");
        try
        {
            if (Directory.Exists(staging))
                foreach (string version in Directory.EnumerateDirectories(staging))
                    foreach (string op in new[] { "usbip", ProtectedStaging.OperationUsbipUde })
                    {
                        string d = Path.Combine(version, op);
                        if (Directory.Exists(d)) found.Add(d);
                    }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var temps = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp") };
        foreach (string profile in ProfileFolders())
            temps.Add(Path.Combine(profile, "AppData", "Local", "Temp"));
        foreach (string temp in temps)
        {
            try
            {
                if (!Directory.Exists(temp)) continue;
                foreach (string d in Directory.EnumerateDirectories(temp, "HIDMaestro_usbip_*"))
                    if (!found.Contains(d, StringComparer.OrdinalIgnoreCase)) found.Add(d);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    /// <summary>Delete a folder through the full removal's own delete
    /// (<see cref="SafeDelete.TreeUnder"/>), which opens every folder from a
    /// trusted anchor down without following a link, changes no attribute,
    /// and leaves an entry in use for the next start only inside
    /// HIDMaestro's protected staging.</summary>
    private static void DeleteFolder(List<UninstallEntry> entries, string dir, bool preview)
    {
        if (preview) { entries.Add(new("folder", dir, UninstallOutcome.WouldRemove)); return; }
        var (outcome, reason) = SafeDelete.TreeUnder(dir, ProfileFolders());
        entries.Add(new("folder", dir, outcome, reason));
    }

    private static UninstallEntry DeleteFile(string path)
    {
        var (outcome, reason) = SafeDelete.TreeUnder(path, ProfileFolders());
        return new("file", path, outcome, reason);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static bool KeyExists(string path)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(path);
            return k != null;
        }
        catch { return false; }
    }

    private static int SafeUdePackageCount()
    {
        try { return UsbipDriverInstaller.UdePackageNames().Count; }
        catch (InvalidOperationException) { return -1; }
    }

    /// <summary>A tool from System32, never one found first in the
    /// application's or the current directory, within a timeout.</summary>
    private static (int ExitCode, string Output) RunSystemTool(string exe, string args) =>
        PnputilHelper.RunTool(Path.Combine(Environment.SystemDirectory, exe), args, 30_000);

    // ── Win32 ────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);

    [LibraryImport("setupapi.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInfoW(IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint Flags, ref SP_DEVINFO_DATA DeviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiRemoveDevice(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [LibraryImport("advapi32.dll")]
    private static partial int RegQueryInfoKeyW(Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey, IntPtr lpClass, IntPtr lpcchClass, IntPtr lpReserved,
        IntPtr lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen, IntPtr lpcValues, IntPtr lpcbMaxValueNameLen,
        IntPtr lpcbMaxValueLen, IntPtr lpcbSecurityDescriptor, out long lpftLastWriteTime);
}
