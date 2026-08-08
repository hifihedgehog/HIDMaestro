using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace HIDMaestro.Internal.Usbip;

/// <summary>Deploys the bundled usbip-win2 transport on demand (issue #39).
///
/// <para>Composite USB personas need a driver-backed USB device for their
/// audio endpoints, which no user-mode API can create. HIDMaestro ships
/// the transport inside HIDMaestro.Core.dll and installs it itself, the
/// same way it ships and installs its own UMDF2 driver: the consumer
/// calls <see cref="HIDMaestro.HMContext.CreateController"/> with a
/// composite profile and it works. There is no separate download, no
/// second package, and nothing for a user to go find.</para>
///
/// <para>The embedded binary is the upstream release asset, unmodified,
/// and its SHA256 is verified after extraction against the digest the
/// upstream release publishes. A mismatch throws rather than running an
/// unverified installer. The BSD-2-Clause notice ships beside it and is
/// written next to the binary at deploy time.</para>
///
/// <para>Install is silent, needs the same elevation
/// <see cref="HIDMaestro.HMContext.InstallDriver"/> already needs, and
/// is idempotent: present-and-working short-circuits to a no-op. Because
/// usbip-win2's extension INF matches the generic USB 3.0 root-hub
/// hardware ID, PnP re-enumerates the root hubs once during install,
/// which momentarily interrupts USB devices. That happens once per
/// machine, on the first composite controller ever created.</para></summary>
internal static class UsbipDriverInstaller
{
    public const string Version = "0.9.7.7";
    private const string InstallerFile = "USBip-" + Version + "-x64.exe";
    private const string NoticeFile = "THIRD-PARTY-NOTICES.txt";

    /// <summary>SHA256 of the upstream release asset, as published by the
    /// GitHub release API for v.0.9.7.7. The MSBuild PackResources target
    /// verifies the same digest at build time, so a corrupted or
    /// substituted binary fails the build; this is the runtime half of
    /// that check, covering the extracted copy.</summary>
    private const string InstallerSha256 =
        "51620fa5f9f8be5932bc9d786deee557ce06d5407a99cab490dcfac71f185fea";

    private static readonly object s_lock = new();
    private static bool s_verifiedThisProcess;

    /// <summary>True when the vhci host controller is present and started,
    /// meaning composite personas can be created right now with no
    /// install step.</summary>
    public static bool IsInstalled => VhciClient.IsAvailable();

    /// <summary>Ensure the transport is installed, deploying the bundled
    /// installer if it is not. Idempotent and safe to call on every
    /// composite create. Returns true when the driver is usable.</summary>
    /// <param name="progress">Optional status callback: consumers with a
    /// UI can surface "installing controller audio support" during the
    /// one-time install, which takes a few seconds and briefly
    /// re-enumerates USB.</param>
    public static bool EnsureInstalled(Action<string>? progress = null)
    {
        if (IsInstalled)
        {
            s_verifiedThisProcess = true;
            StampOwnerHardwareId();
            return true;
        }

        lock (s_lock)
        {
            if (IsInstalled) return true;

            // A machine can have usbip-win2 already (VIIPER and DS4Windows
            // install the same transport) with its host controller merely
            // not started, or left interface-less by an interrupted PnP
            // operation. Running the installer over an existing install is
            // the wrong repair for that: Inno uninstalls the old version
            // first, and its driver-package removal touches the extension
            // INF bound to every USB root hub, which re-enumerates the whole
            // USB tree and can take many minutes. Restarting the devnode
            // republishes the interface in seconds. Try that first, and only
            // install when the product is genuinely absent.
            if (IsProductInstalled())
            {
                progress?.Invoke("Repairing the existing USB audio transport...");
                if (TryRestartHostController(progress)) return true;

                throw new InvalidOperationException(
                    $"usbip-win2 {Version} is installed on this machine but its virtual host " +
                    "controller is not available, and restarting the device did not recover it. " +
                    "Reinstalling it from HIDMaestro would first uninstall the existing copy, " +
                    "which detaches a filter driver from every USB root hub and can take several " +
                    "minutes, so that is not done automatically. Repair or reinstall usbip-win2, " +
                    "or reboot, and try again.");
            }

            progress?.Invoke($"Installing USB audio transport (usbip-win2 {Version})...");

            string exe = ExtractInstaller();
            RunSilentInstall(exe);

            // PnP settles the new host controller asynchronously. The
            // driver's own device arrives quickly; the root-hub
            // re-enumeration its filter INF triggers takes longer and does
            // not gate us.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < TimeoutScale.Apply(60_000))
            {
                if (VhciClient.IsAvailable())
                {
                    StampOwnerHardwareId();
                    progress?.Invoke("USB audio transport ready.");
                    return true;
                }
                Thread.Sleep(250);
            }

            throw new InvalidOperationException(
                $"usbip-win2 {Version} installer completed but its virtual host controller did not " +
                "appear within the timeout. Check Device Manager for 'USBip 3.X Emulated Host " +
                "Controller' and any pending-reboot state.");
        }
    }

    /// <summary>True when usbip-win2's driver package is already in the
    /// driver store, whether or not its host controller is currently
    /// usable. Distinguishes "never installed here" from "installed but
    /// not working", which need different remedies.</summary>
    private static bool IsProductInstalled()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "pnputil.exe",
                Arguments = "/enum-drivers",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(TimeoutScale.Apply(30_000)))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return false;
            }
            return output.Contains("usbip2_ude.inf", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Restart the vhci devnode so it republishes its device
    /// interface. This is the cheap repair for an installed-but-unusable
    /// transport, and it never touches the root-hub filter INF.
    ///
    /// <para>Done through CfgMgr rather than <c>pnputil /restart-device</c>
    /// because that command's <c>/deviceid</c> form does not exist on
    /// Windows 10's pnputil, where it prints usage and exits zero: a
    /// silent no-op that would make this repair look like it ran. The
    /// remove-then-setup pair below is the documented restart sequence
    /// and behaves the same on 10 and 11.</para></summary>
    private static bool TryRestartHostController(Action<string>? progress)
    {
        // Every CfgMgr call here can block indefinitely inside the PnP
        // subsystem: CM_Query_And_Remove_SubTree waits on the driver
        // stack's removal, and a wedged PnP operation elsewhere on the
        // machine holds a global lock that stalls all of them. Observed
        // during development, where an unrelated stuck pnputil made this
        // sequence hang for tens of minutes.
        //
        // A repair must never be able to hang a consumer's
        // CreateController call, so the PnP work runs on a background
        // thread and this method only ever waits a bounded time for the
        // OUTCOME it cares about: the device interface reappearing. If
        // the thread is still stuck when the budget expires, it is
        // abandoned (background, so it cannot keep the process alive) and
        // the repair reports failure.
        var worker = new Thread(() =>
        {
            try
            {
                // Same instance-number caveat as StampOwnerHardwareId:
                // the vhci root is not always 0000. Repair the first
                // present controller that carries the usbip UDE id.
                uint devInst = 0;
                bool located = false;
                for (int n = 0; n < 16 && !located; n++)
                {
                    if (CM_Locate_DevNodeW(out devInst, $"ROOT\\USB\\{n:D4}", CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                        continue;
                    foreach (var hwid in GetMultiSz(devInst, CM_DRP_HARDWAREID))
                        if (hwid.IndexOf("USBIP_WIN2", StringComparison.OrdinalIgnoreCase) >= 0) { located = true; break; }
                }
                if (!located)
                    return;

                // Start it first. The common broken state is a devnode
                // that is root-enumerated with its driver loaded but
                // never started (DN_STARTED clear, no problem code), and
                // for that a plain setup is the whole fix: cheap, and it
                // does not block the way a subtree removal does. Only
                // escalate to remove-and-setup when starting fails.
                if (CM_Setup_DevNode(devInst, CM_SETUP_DEVNODE_READY) == CR_SUCCESS)
                    return;

                CM_Query_And_Remove_SubTreeW(devInst, out _, IntPtr.Zero, 0, 0);
                if (CM_Setup_DevNode(devInst, CM_SETUP_DEVNODE_READY) != CR_SUCCESS
                    && CM_Locate_DevNodeW(out uint root, null, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS)
                {
                    // Still not up, so ask the root to re-enumerate and
                    // let PnP rebuild the devnode.
                    CM_Reenumerate_DevNode(root, 0);
                }
            }
            catch { /* the poll below is the only verdict that matters */ }
        })
        { IsBackground = true, Name = "HMUsbipRepair" };
        worker.Start();

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < TimeoutScale.Apply(30_000))
        {
            if (VhciClient.IsAvailable())
            {
                progress?.Invoke("USB audio transport ready.");
                return true;
            }
            Thread.Sleep(250);
        }
        return false;
    }

    /// <summary>Additional hardware ID stamped onto the emulated host
    /// controller so a composite persona's devnode ancestry names
    /// HIDMaestro (issue #42).
    ///
    /// <para>A composite persona is byte-for-byte a real Sony pad at every
    /// level a filter can inspect, which is required for the UAC class
    /// driver to bind and must not change. That leaves a host with nothing
    /// of its own to match on: the standard HIDMAESTRO token every UMDF2
    /// virtual carries in its hardware IDs is absent from the whole chain,
    /// so a consumer filtering its own virtual pads out of enumeration
    /// picks the persona up as a second controller. On hardware that
    /// showed up as SDL assigning player index 1 and lighting a lone pad
    /// red.</para>
    ///
    /// <para>The token goes on the host controller, the one node
    /// HIDMaestro brings to the tree, and never on the persona. It is
    /// added to the existing hardware IDs rather than replacing them: the
    /// upstream ROOT\USBIP_WIN2\UDE id stays first, so driver matching
    /// resolves exactly as before and the write is additive.</para></summary>
    private const string OwnerHardwareId = "ROOT\\HIDMAESTRO_UDE";

    /// <summary>Append <see cref="OwnerHardwareId"/> to the host
    /// controller's hardware IDs if it is not already there. Idempotent,
    /// cheap enough to run on every composite create (one registry read,
    /// and a write only on the first call per machine), and best-effort:
    /// a machine where this cannot be written still creates controllers,
    /// it just cannot be filtered by the token.</summary>
    internal static bool StampOwnerHardwareId()
    {
        // The vhci host controller is ROOT-enumerated, and its instance
        // NUMBER is not stable: deleting and reinstalling the transport
        // (or a second install racing a phantom of the first) makes PnP
        // allocate ROOT\USB\0001 while a stamped 0000 lingers, and every
        // attach then rides the unstamped sibling. Found live 2026-08-07:
        // 0000 present and stamped, 0001 present, unstamped, and carrying
        // the personas, which broke the #42 ancestry filter downstream.
        // So: enumerate the instance-number space and stamp EVERY present
        // controller whose hardware IDs mark it as the usbip UDE root,
        // rather than assuming instance 0000.
        bool any = false;
        for (int n = 0; n < 16; n++)
        {
            string instanceId = $"ROOT\\USB\\{n:D4}";
            if (StampOne(instanceId))
                any = true;
        }
        return any;
    }

    private static bool StampOne(string instanceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                return false;

            string[] ids = GetMultiSz(devInst, CM_DRP_HARDWAREID);
            bool isUde = false;
            foreach (var id in ids)
            {
                if (string.Equals(id, OwnerHardwareId, StringComparison.OrdinalIgnoreCase))
                    return true; // already stamped
                if (id.IndexOf("USBIP_WIN2", StringComparison.OrdinalIgnoreCase) >= 0)
                    isUde = true;
            }
            if (!isUde)
                return false; // some other root-enumerated USB controller: leave it alone

            // Upstream's id stays at index 0 so driver matching is unchanged.
            var merged = new string[ids.Length + 1];
            Array.Copy(ids, merged, ids.Length);
            merged[ids.Length] = OwnerHardwareId;

            // SetupDi, not CM_Set_DevNode_Registry_Property. The CfgMgr
            // setter reports CR_SUCCESS on this devnode and the Enum key is
            // left untouched, so the stamp silently does nothing. The
            // SPDRP_HARDWAREID path below is what usbip-win2's own
            // installer uses to write this exact property
            // (userspace/devnode/main.cpp, install_devnode_and_driver).
            byte[] buf = MultiSzToBytes(merged);
            IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (set == INVALID_HANDLE_VALUE) return false;
            try
            {
                var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref data))
                    return false;
                if (!SetupDiSetDeviceRegistryPropertyW(set, ref data, SPDRP_HARDWAREID, buf, (uint)buf.Length))
                    return false;
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

            DeviceOrchestrator.LogDiag(
                $"UsbipDriverInstaller: stamped {OwnerHardwareId} onto {instanceId} " +
                $"(was {ids.Length} id(s)).");
            return true;
        }
        catch { return false; }
    }

    /// <summary>Read a REG_MULTI_SZ devnode property, or an empty array.</summary>
    private static string[] GetMultiSz(uint devInst, uint prop)
    {
        uint len = 0, type = 0;
        CM_Get_DevNode_Registry_PropertyW(devInst, prop, ref type, null, ref len, 0);
        if (len == 0) return Array.Empty<string>();
        var buf = new byte[len];
        if (CM_Get_DevNode_Registry_PropertyW(devInst, prop, ref type, buf, ref len, 0) != CR_SUCCESS)
            return Array.Empty<string>();
        return System.Text.Encoding.Unicode.GetString(buf, 0, (int)len)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static byte[] MultiSzToBytes(string[] values)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var v in values) { sb.Append(v); sb.Append('\0'); }
        sb.Append('\0'); // REG_MULTI_SZ terminator
        return System.Text.Encoding.Unicode.GetBytes(sb.ToString());
    }

    // cfgmgr32.h and setupapi.h number these DIFFERENTLY, and the two
    // sets are one apart on exactly this property: CM_DRP_DEVICEDESC is
    // 1 and CM_DRP_HARDWAREID is 2, while SPDRP_DEVICEDESC is 0 and
    // SPDRP_HARDWAREID is 1. Reading with the SetupDi number returns the
    // device description, which then gets written back as the hardware
    // id and unbinds the driver.
    private const uint CM_DRP_HARDWAREID = 0x00000002; // cfgmgr32.h
    private const uint SPDRP_HARDWAREID = 0x00000001;  // setupapi.h
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public int cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint dnDevInst, uint ulProperty,
        ref uint pulRegDataType, byte[]? Buffer, ref uint pulLength, uint ulFlags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr DeviceInfoSet, string DeviceInstanceId,
        IntPtr hwndParent, uint Flags, ref SP_DEVINFO_DATA DeviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet,
        ref SP_DEVINFO_DATA DeviceInfoData, uint Property, byte[] PropertyBuffer, uint PropertyBufferSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    private const uint CM_LOCATE_DEVNODE_NORMAL = 0;
    private const uint CM_SETUP_DEVNODE_READY = 0;
    private const int CR_SUCCESS = 0;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint pdnDevInst, string? pDeviceID, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Query_And_Remove_SubTreeW(uint dnAncestor, out uint pVetoType,
        IntPtr pszVetoName, uint ulNameLength, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Setup_DevNode(uint dnDevInst, uint ulFlags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Reenumerate_DevNode(uint dnDevInst, uint ulFlags);

    /// <summary>Extract the bundled installer and its license notice to a
    /// per-version temp directory, verifying the binary's hash. Reuses an
    /// already-extracted copy when the hash still matches.</summary>
    private static string ExtractInstaller()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"HIDMaestro_usbip_{Version}");
        Directory.CreateDirectory(dir);
        string exePath = Path.Combine(dir, InstallerFile);

        if (File.Exists(exePath) && (s_verifiedThisProcess || HashMatches(exePath)))
            return exePath;

        var asm = typeof(UsbipDriverInstaller).Assembly;
        string logical = "HIDMaestro.Resources." + InstallerFile;
        using (var stream = asm.GetManifestResourceStream(logical))
        {
            if (stream == null)
                throw new InvalidOperationException(
                    $"The bundled usbip-win2 installer ('{logical}') is missing from " +
                    "HIDMaestro.Core.dll. The PackResources MSBuild target fetches and embeds it; " +
                    "rebuild the SDK.");
            using var fs = File.Create(exePath);
            stream.CopyTo(fs);
        }

        if (!HashMatches(exePath))
        {
            try { File.Delete(exePath); } catch { }
            throw new InvalidOperationException(
                "The extracted usbip-win2 installer failed its SHA256 check and was deleted. " +
                "Refusing to run an unverified driver installer.");
        }
        s_verifiedThisProcess = true;

        // Ship the BSD-2-Clause notice next to the binary, which is the
        // license's "documentation and/or other materials provided with
        // the distribution" requirement met at the point of deployment.
        try
        {
            using var notice = asm.GetManifestResourceStream("HIDMaestro.Resources." + NoticeFile);
            if (notice != null)
            {
                using var nf = File.Create(Path.Combine(dir, NoticeFile));
                notice.CopyTo(nf);
            }
        }
        catch { /* the notice is also embedded in the assembly itself */ }

        return exePath;
    }

    private static bool HashMatches(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            byte[] hash = SHA256.HashData(fs);
            return Convert.ToHexString(hash).Equals(InstallerSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>Run the Inno Setup installer silently. The flags are the
    /// upstream installer's own: no UI, no message boxes, no reboot.</summary>
    private static void RunSilentInstall(string exePath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the usbip-win2 installer.");

        if (!p.WaitForExit(TimeoutScale.Apply(300_000)))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException(
                "The usbip-win2 installer did not finish within the timeout.");
        }

        // Inno Setup: 0 success, 3010 success-with-reboot-pending. The
        // driver still works before that reboot, so treat it as success
        // and let the availability poll be the real verdict.
        if (p.ExitCode != 0 && p.ExitCode != 3010)
            throw new InvalidOperationException(
                $"The usbip-win2 installer failed with exit code {p.ExitCode}. " +
                "Installing a driver requires elevation, the same as HMContext.InstallDriver.");
    }
}
