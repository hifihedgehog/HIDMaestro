using System;
using System.IO;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Per-controller setup orchestrator. Owns the full sequence of steps that
/// turns a profile into a live virtual HID device:
///
/// <list type="number">
/// <item>Pre-flight environment (shared memory sections, GameInput service hint)</item>
/// <item>Per-instance config registry (descriptor, VID/PID, product strings)</item>
/// <item>Driver install if not already in the store (delegated to <see cref="DriverBuilder"/>)</item>
/// <item>Device node creation (delegated to <see cref="DeviceNodeCreator"/>)</item>
/// <item>Friendly name finalization (delegated to <see cref="DeviceProperties"/>)</item>
/// </list>
///
/// <para>The current implementation supports the <b>plain HID</b> profile path
/// (DualSense, generic third-party gamepads — no upper filter, no companion).
/// The <b>xinputhid wrapper-companion</b> and <b>XUSB system-companion</b>
/// paths used by Xbox profiles are documented in EmulateProfile in the test
/// app and will be ported here in a follow-up. Until then, calling
/// <see cref="SetupController"/> with an Xbox profile throws
/// <see cref="NotSupportedException"/>.</para>
/// </summary>
internal static class DeviceOrchestrator
{
    private const string REG_BASE = @"SOFTWARE\HIDMaestro";
    private static string RegPathForIndex(int index) =>
        $@"{REG_BASE}\Controller{index}";

    /// <summary>
    /// Set up one controller end-to-end. After this returns successfully, the
    /// virtual device is live in PnP, the per-controller shared memory
    /// sections are mapped (caller can write input via
    /// <see cref="SharedMemoryIO.WriteInputFrame"/>), and the friendly name
    /// is finalized.
    /// </summary>
    /// <param name="controllerIndex">Zero-based index of this controller in
    /// the consuming context. Used to derive per-instance registry paths,
    /// shared memory section names, and HID serial number.</param>
    /// <param name="profile">Loaded controller profile with HID descriptor.</param>
    /// <param name="infPath">Filesystem path to <c>hidmaestro.inf</c>.</param>
    /// <returns>The PnP instance ID of the newly-created device, e.g.
    /// <c>ROOT\HIDClass\0001</c>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
    /// <exception cref="NotSupportedException">Profile uses xinputhid or is an
    /// Xbox VID — these paths require companions which are not yet ported
    /// from EmulateProfile.</exception>
    /// <exception cref="InvalidOperationException">Driver install failed or
    /// device node creation failed.</exception>
    public static string SetupController(
        int controllerIndex, ControllerProfile profile, string infPath)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (controllerIndex < 0) throw new ArgumentOutOfRangeException(nameof(controllerIndex));
        if (!profile.HasDescriptor)
            throw new ArgumentException(
                $"Profile '{profile.Id}' has no HID descriptor.", nameof(profile));

        // Hard gate: Xbox profiles need companions that aren't ported yet.
        // EmulateProfile in the test app handles them today; HMContext
        // consumers should fall through to the test app or wait for the
        // follow-up port.
        if (profile.UsesUpperFilter || profile.VendorId == 0x045E)
            throw new NotSupportedException(
                $"Profile '{profile.Id}' is an Xbox-family profile that needs " +
                "the xinputhid wrapper companion or XUSB companion. Those " +
                "code paths are not yet ported into the SDK orchestrator " +
                "(EmulateProfile in HIDMaestroTest.exe is the working " +
                "implementation today). Use a plain HID profile (e.g. " +
                "'dualsense', 'dualshock-4-v2') with HMContext for now.");

        // ── Step 0: pre-flight environment ───────────────────────────────
        // Pre-create the per-instance shared memory sections. The driver
        // (running in WUDFHost as LocalService) lacks SeCreateGlobalPrivilege
        // and can only OpenFileMapping a Global\* section, never create one.
        // The consumer process is the only entity that can create them, so
        // we do it before the device starts.
        SharedMemoryIO.EnsureInputMapping(controllerIndex);
        try { SharedMemoryIO.EnsureOutputMapping(controllerIndex); } catch { }

        // ── Step 1: per-instance registry config ────────────────────────
        // The driver reads this at startup (InitInstancePaths in driver.c)
        // to know the descriptor, VID/PID, product string, and report length
        // for THIS controller index.
        WriteInstanceConfig(controllerIndex, profile);

        // ── Step 2: ensure driver is in the store ────────────────────────
        // Idempotent — does nothing if a current build is already installed.
        if (DriverBuilder.NeedsBuild() || !DriverBuilder.IsDriverInstalled())
        {
            if (!DriverBuilder.FullDeploy(rebuild: DriverBuilder.NeedsBuild()))
                throw new InvalidOperationException(
                    "Driver build/install failed. Run elevated and check logs.");
        }

        // ── Step 3: create the PnP device node ───────────────────────────
        // DeviceNodeCreator handles SetupAPI registration, hardware ID
        // assignment, ContainerID write (per-instance GUID for unique
        // container in Settings), ControllerIndex registry write, driver
        // bind via UpdateDriverForPlugAndPlayDevicesW, and HID-child wait.
        var result = DeviceNodeCreator.CreateDeviceNode(profile, infPath, controllerIndex);
        if (!result.Success || result.InstanceId == null)
            throw new InvalidOperationException(
                $"DeviceNodeCreator.CreateDeviceNode failed for profile " +
                $"'{profile.Id}' at index {controllerIndex}.");

        // ── Step 4: finalize friendly name ───────────────────────────────
        // Phase 1.5 in EmulateProfile — re-applies FriendlyName / DeviceDesc
        // / BusReportedDeviceDesc filtered by Device Parameters\ControllerIndex.
        // Necessary because the per-controller rename inside the device-node
        // creation races with PnP driver-bind for the FIRST device, and the
        // writes get lost. Re-applying after PnP settles makes them stick.
        string displayName = profile.DeviceDescription ?? profile.ProductString ?? "Controller";
        DeviceProperties.ApplyFriendlyNameForController(controllerIndex, displayName);

        return result.InstanceId;
    }

    /// <summary>Free the per-controller shared memory sections and remove the
    /// PnP device node. Safe to call on a partially-set-up controller (each
    /// step is wrapped to swallow exceptions).</summary>
    public static void TeardownController(int controllerIndex, string? instanceId)
    {
        try { SharedMemoryIO.DestroyController(controllerIndex); } catch { }

        if (!string.IsNullOrEmpty(instanceId))
        {
            try { DeviceManager.RemoveDevice(instanceId!); } catch { }
        }

        // Optional: clear the per-instance registry config so a fresh setup
        // doesn't pick up stale state.
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(RegPathForIndex(controllerIndex), throwOnMissingSubKey: false);
        }
        catch { }
    }

    /// <summary>Write the per-instance config the driver reads on attach.
    /// Mirrors WriteConfig in the test app's Program.cs.</summary>
    private static void WriteInstanceConfig(int controllerIndex, ControllerProfile profile)
    {
        byte[] descriptor = profile.GetDescriptorBytes()!;
        int inputReportLen = (profile.InputReportSize ?? 0) > 0
            ? profile.InputReportSize!.Value
            : ComputeInputReportByteLength(descriptor);

        string instancePath = RegPathForIndex(controllerIndex);
        using var key = Registry.LocalMachine.CreateSubKey(instancePath);
        string instanceSuffix = controllerIndex > 0 ? $"\\{controllerIndex:D4}" : "\\0000";
        key.SetValue("DeviceInstanceId",
            $@"ROOT\VID_{profile.VendorId:X4}&PID_{profile.ProductId:X4}&IG_00{instanceSuffix}",
            RegistryValueKind.String);
        // Plain HID profiles always run with FunctionMode=0 (no XUSB on the
        // main device, no WinExInput skip). Xbox profiles override this when
        // their orchestration path is ported.
        key.SetValue("FunctionMode", 0, RegistryValueKind.DWord);
        key.SetValue("ReportDescriptor", descriptor, RegistryValueKind.Binary);
        key.SetValue("VendorId", (int)profile.VendorId, RegistryValueKind.DWord);
        key.SetValue("ProductId", (int)profile.ProductId, RegistryValueKind.DWord);
        key.SetValue("VersionNumber", 0x0100, RegistryValueKind.DWord);
        if (profile.ProductString != null)
            key.SetValue("ProductString", profile.ProductString, RegistryValueKind.String);
        if (inputReportLen > 0)
            key.SetValue("InputReportByteLength", inputReportLen, RegistryValueKind.DWord);
        string displayName = profile.DeviceDescription ?? profile.ProductString ?? "HIDMaestro Controller";
        key.SetValue("DeviceDescription", displayName, RegistryValueKind.String);

        // Joystick OEM display name (joy.cpl).
        string oemKeyPath = $@"SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{profile.VendorId:X4}&PID_{profile.ProductId:X4}";
        try
        {
            using var oem = Registry.LocalMachine.CreateSubKey(oemKeyPath);
            oem?.SetValue("OEMName", displayName, RegistryValueKind.String);
            oem?.DeleteValue("OEMData", false);
            try { Registry.LocalMachine.DeleteSubKeyTree($@"{oemKeyPath}\Axes", throwOnMissingSubKey: false); } catch { }
        }
        catch { }
    }

    /// <summary>Compute the input-report byte length from a HID descriptor.
    /// Lifted from Program.ComputeInputReportByteLength.</summary>
    private static int ComputeInputReportByteLength(byte[] desc)
    {
        // Walk items, accumulating reportSize * reportCount for each Input
        // (data) item until the first End Collection that closes the top
        // application collection. Coarse but matches what mshidumdf computes.
        int reportSize = 0, reportCount = 0;
        long inputBits = 0;
        int colDepth = 0;
        for (int i = 0; i < desc.Length;)
        {
            byte prefix = desc[i];
            int bSize = prefix & 0x03;
            if (bSize == 3) bSize = 4;
            int bType = (prefix >> 2) & 0x03;
            int bTag = (prefix >> 4) & 0x0F;

            int dataValue = 0;
            for (int b = 0; b < bSize && i + 1 + b < desc.Length; b++)
                dataValue |= desc[i + 1 + b] << (b * 8);

            if (bType == 1 && bTag == 7) reportSize = dataValue;       // ReportSize
            else if (bType == 1 && bTag == 9) reportCount = dataValue; // ReportCount
            else if (bType == 0 && bTag == 8)                          // Input
                inputBits += (long)reportSize * reportCount;
            else if (bType == 0 && bTag == 10) colDepth++;             // Collection
            else if (bType == 0 && bTag == 12)                         // End Collection
            {
                colDepth--;
                if (colDepth == 0) break;
            }

            i += 1 + bSize;
        }
        return (int)((inputBits + 7) / 8);
    }
}
