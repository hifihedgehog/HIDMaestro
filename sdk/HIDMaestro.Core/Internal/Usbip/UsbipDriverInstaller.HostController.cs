using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace HIDMaestro.Internal.Usbip;

// The transport's install from the host controller package the assembly
// carries, with no vendor installer. usbip-win2's own installer runs
// "devnode.exe install usbip2_ude.inf ROOT\USBIP_WIN2\UDE" for the host
// controller (setup.iss at v.0.9.8.1, [Run] 176-177), and devnode.exe makes
// the devnode and puts the INF on it with four calls
// (userspace/devnode/main.cpp:232-295, install_devnode_and_driver):
// SetupDiCreateDeviceInfo with DICD_GENERATE_ID under the INF's class, the
// hardware id written with SetupDiSetDeviceRegistryProperty,
// DIF_REGISTERDEVICE, then UpdateDriverForPlugAndPlayDevices with
// INSTALLFLAG_FORCE. The SDK makes the same four calls. The last runs in
// hmswd.exe, as the move off an older host controller already runs it,
// because that call has no timeout.
//
// The installer's other step, "pnputil /add-driver usbip2_filter.inf
// /install" (the line before, 176), is the one that restarts every USB root
// hub: the filter is an extension INF on the generic USB\ROOT_HUB30
// (drivers/ude_filter/usbip2_filter.inf:32-43), and its install reconfigures
// every hub that carries that id (drivers/ude_filter/readme.txt). The SDK
// never installs it. What the filter does for usbip2_ude, and how the SDK
// does without it, is in UsbAudioEngine's notes on stream state.
internal static partial class UsbipDriverInstaller
{
    /// <summary>The setup class the host controller INF names: Class=USB,
    /// GUID_DEVCLASS_USB (usbip2_ude.inf lines 8-9).</summary>
    private const string UdeClassName = "USB";
    private static readonly Guid UdeClassGuid = new("36fc9e60-c465-11cf-8056-444553540000");

    /// <summary>Install the host controller on a machine where none is
    /// present: register a <c>ROOT\USB</c> devnode with usbip-win2's hardware
    /// id and HIDMaestro's stamp, force the pinned package onto it, and
    /// write the install record. The caller holds the transport lock and
    /// found no present host controller.</summary>
    private static bool InstallHostController(TransportWork work)
    {
        work.Report($"Installing USB audio transport (usbip-win2 {Version} host controller)...");

        // The packages the driver store held before. A published name that
        // is new after the install is one this install added, and only that
        // one is the install record's to remove. A listing that fails throws
        // here, before anything changes.
        var before = new HashSet<string>(UdePackageNames(), StringComparer.OrdinalIgnoreCase);

        string inf = StageUdePackage();
        // The service key outlives a host controller removed without its
        // package, and an earlier 0.9.7.5 install left KMDF's debug values in
        // it. The driver reads them when it loads, which the forced install
        // below does.
        ClearStaleVerifier(new[] { ReadInfText(inf) }, "before the install");

        string instance = RegisterHostController();
        var record = new UsbipInstallRecord(instance, null, false, Version, DateTime.UtcNow, MachineClaims.SdkVersion);
        UsbipInstallRecord.Write(record);
        DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: registered {instance}; installing the host controller driver.");

        var sw = Stopwatch.StartNew();
        try { RunForceDriver(inf); }
        catch (TimeoutException)
        {
            // The devnode stays, and so does the record that names it: a
            // driver install still running cannot be undone from here, and a
            // removal later finds both.
            throw;
        }
        catch
        {
            UnregisterHostController(instance);
            UsbipInstallRecord.Delete();
            throw;
        }

        string? oem = DriverInfPathOf(instance);
        UsbipInstallRecord.Write(record with { OemInf = oem, PackageAdded = oem != null && !before.Contains(oem) });
        DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: {instance} runs {oem ?? "an unknown package"} ({(oem != null && !before.Contains(oem) ? "added by this install" : "already in the driver store")}).");

        while (sw.ElapsedMilliseconds < TimeoutScale.Apply(60_000))
        {
            if (VhciClient.IsAvailable())
            {
                StampOwnerHardwareId();
                DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: the host controller answers {sw.ElapsedMilliseconds} ms after its driver install began.");
                work.Report("USB audio transport ready.");
                return true;
            }
            Thread.Sleep(250);
        }
        throw new InvalidOperationException(
            $"The usbip-win2 {Version} host controller ({instance}) was installed but did not answer within the timeout. " +
            "Check Device Manager for 'USBip 3.X Emulated Host Controller'.");
    }

    /// <summary>Register the host controller's devnode, as devnode.exe does
    /// (main.cpp:248-265), with HIDMaestro's stamp after usbip-win2's id, and
    /// return its instance id. Under the PnP lock, as every devnode
    /// registration of the SDK is.</summary>
    private static string RegisterHostController()
    {
        using var pnp = DeviceManager.HoldPnpLock();
        Guid cls = UdeClassGuid;
        IntPtr set = SetupDiCreateDeviceInfoListForClass(ref cls, IntPtr.Zero);
        if (set == INVALID_HANDLE_VALUE)
            throw new InvalidOperationException($"SetupDiCreateDeviceInfoList failed ({Marshal.GetLastWin32Error()}).");
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiCreateDeviceInfoW(set, UdeClassName, ref cls, null, IntPtr.Zero, DICD_GENERATE_ID, ref data))
                throw new InvalidOperationException($"SetupDiCreateDeviceInfo for the usbip-win2 host controller failed ({Marshal.GetLastWin32Error()}).");

            // usbip-win2's id first, so driver matching is what devnode.exe
            // gets. The stamp after it is the one StampOne adds to a host
            // controller someone else installed.
            byte[] ids = MultiSzToBytes(new[] { UdeHardwareId, OwnerHardwareId });
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, SPDRP_HARDWAREID, ids, (uint)ids.Length))
                throw new InvalidOperationException($"Writing the host controller's hardware ids failed ({Marshal.GetLastWin32Error()}).");
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref data))
                throw new InvalidOperationException($"DIF_REGISTERDEVICE for the usbip-win2 host controller failed ({Marshal.GetLastWin32Error()}).");

            var buf = new char[512];
            if (!SetupDiGetDeviceInstanceIdW(set, ref data, buf, (uint)buf.Length, out uint len) || len == 0)
            {
                int err = Marshal.GetLastWin32Error();
                SetupDiCallClassInstaller(DIF_REMOVE, set, ref data);
                throw new InvalidOperationException($"The registered host controller's instance id could not be read ({err}).");
            }
            return new string(buf, 0, (int)len - 1);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>Remove a host controller devnode this install registered
    /// and could not put a driver on, as devnode.exe's rollback does
    /// (main.cpp:267-269). A devnode with no driver has nothing to stop, so
    /// this does not wait on a driver stack.</summary>
    private static void UnregisterHostController(string instanceId)
    {
        try
        {
            using var pnp = DeviceManager.HoldPnpLock();
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == INVALID_HANDLE_VALUE) return;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data))
                {
                    bool removed = SetupDiCallClassInstaller(DIF_REMOVE, set, ref data);
                    DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: removed the registered {instanceId} after its failed install: {removed}.");
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: {instanceId} left after its failed install: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Put <paramref name="inf"/> on every present devnode with
    /// usbip-win2's hardware id: hmswd force-driver, which calls
    /// UpdateDriverForPlugAndPlayDevices with INSTALLFLAG_FORCE, the call
    /// devnode.exe makes (main.cpp:280). It runs in the helper because that
    /// call has no timeout: a host controller that will not let go of its
    /// driver would hold the calling thread forever, and that thread belongs
    /// to the consumer. The helper and the package are held, and checked
    /// under the hold, until the helper has exited: the update reads the
    /// package's files while it runs.</summary>
    /// <exception cref="TimeoutException">The helper did not return within
    /// 90 s and was ended.</exception>
    /// <exception cref="InvalidOperationException">The helper returned an
    /// error.</exception>
    private static void RunForceDriver(string inf)
    {
        var sw = Stopwatch.StartNew();
        using (SwdDeviceFactory.PinHelper(out string helper))
        using (PinUdePackage(inf))
        {
            using var p = Process.Start(BuildForceDriverStartInfo(helper, inf)) ?? throw new InvalidOperationException("Could not start hmswd.exe.");
            // Drained as it comes, so hmswd never blocks on a full pipe
            // while this waits for it to exit.
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeoutScale.Apply(90_000)))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException($"The driver install for {UdeHardwareId} did not return in time.");
            }
            if (p.ExitCode != 0 && p.ExitCode != 3010)
                throw new InvalidOperationException(
                    $"hmswd force-driver returned {p.ExitCode}: {(stderr.Wait(TimeoutScale.Apply(5_000)) ? stderr.Result.Trim() : "")}");
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller:   driver install returned {p.ExitCode} in {sw.ElapsedMilliseconds} ms.");
        }
    }

    /// <summary>The driver install's <see cref="ProcessStartInfo"/>: hmswd
    /// force-driver with the package and usbip-win2's hardware id, no
    /// window, and its working directory, TEMP and TMP confined to the
    /// helper's protected staging folder. A seam
    /// <see cref="RunForceDriver"/> and coexistence probe OF10 both use, so
    /// dropping the confinement shows in the test.</summary>
    internal static ProcessStartInfo BuildForceDriverStartInfo(string helper, string inf)
    {
        var psi = new ProcessStartInfo
        {
            FileName = helper,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(helper)!,
        };
        ProtectedStaging.ConfineChildTemp(psi, Path.GetDirectoryName(helper)!);
        psi.ArgumentList.Add("force-driver");
        psi.ArgumentList.Add(inf);
        psi.ArgumentList.Add(UdeHardwareId);
        return psi;
    }

    // ── Host controllers ─────────────────────────────────────────────────

    /// <summary>Every <c>ROOT\USB</c> devnode whose hardware ids carry
    /// usbip-win2's, present or not, with whether it is present.</summary>
    internal static List<(string Id, bool Present)> HostControllers()
    {
        var found = new List<(string, bool)>();
        foreach (string id in DeviceIdList("ROOT"))
        {
            if (!id.StartsWith(@"ROOT\USB\", StringComparison.OrdinalIgnoreCase)) continue;
            bool present = CM_Locate_DevNodeW(out uint devInst, id, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;
            if (!present && CM_Locate_DevNodeW(out devInst, id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) continue;
            foreach (string hw in GetMultiSz(devInst, CM_DRP_HARDWAREID))
                if (hw.IndexOf("USBIP_WIN2", StringComparison.OrdinalIgnoreCase) >= 0) { found.Add((id, present)); break; }
        }
        return found;
    }

    /// <summary>Every device instance id under an enumerator, present or
    /// not, or every one when <paramref name="enumerator"/> is null.</summary>
    internal static List<string> DeviceIdList(string? enumerator)
    {
        uint flags = enumerator == null ? 0 : CM_GETIDLIST_FILTER_ENUMERATOR;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint len, enumerator, flags) != CR_SUCCESS) break;
            var list = new char[len];
            // A device that arrives between the two calls makes the list
            // longer than the size read: read the size again.
            if (CM_Get_Device_ID_ListW(enumerator, list, len, flags) != CR_SUCCESS) continue;
            return new List<string>(new string(list).Split('\0', StringSplitOptions.RemoveEmptyEntries));
        }
        return new List<string>();
    }

    /// <summary>The published INF (oemNN.inf) the devnode's driver came
    /// from, present or not, or null.</summary>
    internal static string? DriverInfPathOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS &&
            CM_Locate_DevNodeW(out devInst, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
            return null;
        string? inf = GetStringProperty(devInst, DEVPKEY_Device_DriverInfPath);
        return string.IsNullOrEmpty(inf) ? null : inf;
    }

    /// <summary>A devnode's parent, present or not, or null.</summary>
    internal static string? ParentOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS &&
            CM_Locate_DevNodeW(out devInst, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
            return null;
        return GetStringProperty(devInst, DEVPKEY_Device_Parent);
    }

    /// <summary>A devnode's hardware ids, present or not.</summary>
    internal static string[] HardwareIdsOf(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS &&
            CM_Locate_DevNodeW(out devInst, instanceId, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS)
            return Array.Empty<string>();
        return GetMultiSz(devInst, CM_DRP_HARDWAREID);
    }

    /// <summary>True when the devnode is in the tree now.</summary>
    internal static bool IsPresent(string instanceId) =>
        CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;

    /// <summary>True when Windows holds any record of the devnode, present
    /// or not.</summary>
    internal static bool IsKnown(string instanceId) =>
        CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS ||
        CM_Locate_DevNodeW(out _, instanceId, CM_LOCATE_DEVNODE_PHANTOM) == CR_SUCCESS;

    /// <summary><see cref="HostControllerUsedSinceBoot(out string)"/> for
    /// the removal.</summary>
    internal static bool UsedSinceBoot(out string why) => HostControllerUsedSinceBoot(out why);

    /// <summary>Take <see cref="OwnerHardwareId"/> off a present host
    /// controller's hardware ids, leaving the rest in their order. True when
    /// the devnode no longer carries it.</summary>
    internal static bool Unstamp(string instanceId)
    {
        using var pnp = DeviceManager.HoldPnpLock();
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return false;
        string[] ids = GetMultiSz(devInst, CM_DRP_HARDWAREID);
        var kept = Array.FindAll(ids, id => !string.Equals(id, OwnerHardwareId, StringComparison.OrdinalIgnoreCase));
        if (kept.Length == ids.Length) return true;
        if (kept.Length == 0) return false;
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == INVALID_HANDLE_VALUE) return false;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data)) return false;
            byte[] buf = MultiSzToBytes(kept);
            return SetupDiSetDeviceRegistryPropertyW(set, ref data, SPDRP_HARDWAREID, buf, (uint)buf.Length);
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>The published names of every usbip2_ude.inf package in the
    /// driver store.</summary>
    /// <exception cref="InvalidOperationException">The driver store could not
    /// be read.</exception>
    internal static List<string> UdePackageNames() =>
        UdePackageNames(() => PnputilHelper.ListPackages(n => string.Equals(n, ProductInf, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The same, with <paramref name="list"/> for the listing.
    /// Coexistence probe OF11 calls it through reflection with listings
    /// that fail. A failed listing is never an empty one: it throws with
    /// the reason.</summary>
    internal static List<string> UdePackageNames(Func<List<PnputilHelper.DriverRecord>> list)
    {
        List<PnputilHelper.DriverRecord> listing;
        try { listing = list(); }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"HIDMaestro could not read the driver store to tell whether usbip-win2 is installed ({ex.Message.TrimEnd('.')}), so it does not install it.", ex);
        }
        var names = new List<string>();
        foreach (var r in listing)
            if (string.Equals(r.OriginalName, ProductInf, StringComparison.OrdinalIgnoreCase)) names.Add(r.PublishedName);
        return names;
    }

    // ── KMDF's debug values ──────────────────────────────────────────────
    //
    // The 0.9.7.5 INF that SDK v1.9.0 and v1.9.1 installed writes
    // HKR,Parameters\Wdf,VerboseOn and VerifierOn (research, extracted INF
    // lines 77-78). 0.9.7.7 and 0.9.8.1 carry the same lines commented out
    // (usbip2_ude.inf at v.0.9.8.1, lines 54-58). Nothing deletes a value an
    // older INF wrote, so a machine moved from 0.9.7.5 keeps the KMDF
    // verifier on for every later driver, read at each load. They are
    // cleared once the driver in use is one whose INF does not write them.

    private static volatile bool s_verifierChecked;

    private const string WdfParametersKey = @"SYSTEM\CurrentControlSet\Services\usbip2_ude\Parameters\Wdf";
    internal static readonly string[] WdfDebugValues = { "VerifierOn", "VerboseOn" };

    /// <summary>Once per process, on a machine whose transport answers:
    /// clear the stale values when no present host controller's INF writes
    /// them. The values take effect at the driver's next load.</summary>
    private static void ClearStaleVerifierOnce()
    {
        if (s_verifierChecked) return;
        s_verifierChecked = true;
        if (Environment.GetEnvironmentVariable("HIDMAESTRO_KEEP_TRANSPORT") == "1") return;
        try
        {
            if (StaleVerifierValues().Count == 0) return;
            var texts = new List<string>();
            foreach (var (id, present) in HostControllers())
            {
                if (!present) continue;
                // A controller whose INF cannot be read could be one that
                // writes them: leave them.
                if (DriverInfPathOf(id) is not string oem) return;
                texts.Add(ReadInfText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", oem)));
            }
            if (texts.Count > 0) ClearStaleVerifier(texts, "for the installed driver");
        }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: KMDF debug values not checked: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The KMDF debug values the service key holds now.</summary>
    internal static List<string> StaleVerifierValues()
    {
        var found = new List<string>();
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(WdfParametersKey);
            if (k == null) return found;
            foreach (string v in WdfDebugValues)
                if (k.GetValue(v) != null) found.Add(v);
        }
        catch { }
        return found;
    }

    /// <summary>Delete the KMDF debug values unless one of
    /// <paramref name="infTexts"/> writes them. Returns the values
    /// deleted.</summary>
    internal static List<string> ClearStaleVerifier(IEnumerable<string> infTexts, string when)
    {
        var deleted = new List<string>();
        foreach (string text in infTexts)
            if (InfWritesWdfDebugValues(text)) return deleted;
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(WdfParametersKey, writable: true);
            if (k == null) return deleted;
            foreach (string v in WdfDebugValues)
            {
                if (k.GetValue(v) == null) continue;
                k.DeleteValue(v, throwOnMissingValue: false);
                deleted.Add(v);
            }
        }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: KMDF debug values not cleared: {ex.GetType().Name}: {ex.Message}");
        }
        if (deleted.Count > 0)
            DeviceOrchestrator.LogDiag($"UsbipDriverInstaller: cleared {string.Join(" and ", deleted)} under usbip2_ude's Parameters\\Wdf {when}; they take effect at the driver's next load.");
        return deleted;
    }

    /// <summary>True when an INF line that is not a comment writes VerifierOn
    /// or VerboseOn under Parameters\Wdf.</summary>
    internal static bool InfWritesWdfDebugValues(string infText)
    {
        foreach (string raw in infText.Split('\n'))
        {
            int semi = raw.IndexOf(';');
            string line = semi >= 0 ? raw[..semi] : raw;
            if (line.IndexOf(@"Parameters\Wdf", StringComparison.OrdinalIgnoreCase) < 0) continue;
            foreach (string v in WdfDebugValues)
                if (line.IndexOf(v, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    /// <summary>An INF's text, UTF-16 with a byte order mark or UTF-8.</summary>
    internal static string ReadInfText(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        return raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE
            ? System.Text.Encoding.Unicode.GetString(raw, 2, raw.Length - 2)
            : System.Text.Encoding.UTF8.GetString(raw);
    }

    // ── Win32 ────────────────────────────────────────────────────────────

    private const uint DICD_GENERATE_ID = 0x00000001;
    private const uint DIF_REGISTERDEVICE = 0x00000019;
    private const uint DIF_REMOVE = 0x00000005;

    private static readonly DEVPROPKEY DEVPKEY_Device_DriverInfPath =
        new() { fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 5 };

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiCreateDeviceInfoList", SetLastError = true)]
    private static partial IntPtr SetupDiCreateDeviceInfoListForClass(ref Guid ClassGuid, IntPtr hwndParent);

    [LibraryImport("setupapi.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiCreateDeviceInfoW(IntPtr DeviceInfoSet, string DeviceName, ref Guid ClassGuid, string? DeviceDescription, IntPtr hwndParent, uint CreationFlags, ref SP_DEVINFO_DATA DeviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiCallClassInstaller(uint InstallFunction, IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData);

    [LibraryImport("setupapi.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstanceIdW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, [Out] char[] DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);
}

/// <summary>What HIDMaestro installed of usbip-win2 on this machine:
/// <c>HKLM\SOFTWARE\HIDMaestroUsbip</c>, written when the SDK registers a
/// host controller and installs its driver. It sits outside
/// <c>HKLM\SOFTWARE\HIDMaestro</c>, which the Everything cleanup deletes,
/// because it answers a question a removal asks later: whether that host
/// controller and package are HIDMaestro's to take away. A machine holds
/// one record, of the last install.</summary>
/// <param name="Instance">The host controller devnode the install
/// registered (<c>ROOT\USB\000N</c>).</param>
/// <param name="OemInf">The published INF the devnode's driver came from,
/// null until the driver install has returned.</param>
/// <param name="PackageAdded">True when the install added that package to
/// the driver store. False when the store held it before, as a vendor
/// install of the same release leaves it.</param>
/// <param name="Version">The usbip-win2 release installed.</param>
/// <param name="InstalledUtc">When.</param>
/// <param name="SdkVersion">The HIDMaestro version that installed it.</param>
internal sealed record UsbipInstallRecord(string Instance, string? OemInf, bool PackageAdded, string Version, DateTime InstalledUtc, string SdkVersion)
{
    internal const string Key = @"SOFTWARE\HIDMaestroUsbip";

    internal static void Write(UsbipInstallRecord r)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(Key);
            k.SetValue("Instance", r.Instance, Microsoft.Win32.RegistryValueKind.String);
            if (r.OemInf != null) k.SetValue("OemInf", r.OemInf, Microsoft.Win32.RegistryValueKind.String);
            else k.DeleteValue("OemInf", throwOnMissingValue: false);
            k.SetValue("PackageAdded", r.PackageAdded ? 1 : 0, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("Version", r.Version, Microsoft.Win32.RegistryValueKind.String);
            k.SetValue("InstalledUtc", r.InstalledUtc.ToString("o", System.Globalization.CultureInfo.InvariantCulture), Microsoft.Win32.RegistryValueKind.String);
            k.SetValue("SdkVersion", r.SdkVersion, Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            DeviceOrchestrator.LogDiag($"UsbipInstallRecord: not written: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The record, or null when there is none or it names no
    /// instance.</summary>
    internal static UsbipInstallRecord? Read()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Key);
            if (k?.GetValue("Instance") is not string instance || instance.Length == 0) return null;
            DateTime.TryParse(k.GetValue("InstalledUtc") as string, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTime when);
            return new UsbipInstallRecord(
                instance,
                k.GetValue("OemInf") as string,
                k.GetValue("PackageAdded") is int added && added != 0,
                k.GetValue("Version") as string ?? "",
                when,
                k.GetValue("SdkVersion") as string ?? "");
        }
        catch { return null; }
    }

    /// <summary>True when a record key exists, whatever it holds.</summary>
    internal static bool Exists()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(Key);
            return k != null;
        }
        catch { return false; }
    }

    internal static void Delete()
    {
        try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(Key, throwOnMissingSubKey: false); }
        catch (Exception ex) { DeviceOrchestrator.LogDiag($"UsbipInstallRecord: not deleted: {ex.GetType().Name}: {ex.Message}"); }
    }
}
