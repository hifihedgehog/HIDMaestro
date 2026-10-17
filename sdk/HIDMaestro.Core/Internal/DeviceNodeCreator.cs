using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Per-controller virtual device node creation. This is the heart of HIDMaestro
///: it builds a root-enumerated PnP device of the right HID class with the
/// right hardware ID, writes the per-instance ControllerIndex, installs the
/// driver against it, and waits for the HID class to layer on top before
/// returning.
///
/// <para>Three enumerator paths exist, picked from the profile:</para>
/// <list type="bullet">
/// <item><b>xinputhid path</b> (Xbox Series controllers): hardware ID has
/// <c>&amp;IG_00</c> + <c>DriverPid</c> override that matches xinputhid's INF.
/// xinputhid auto-binds and presents the device as XInput.</item>
/// <item><b>Xbox legacy path</b> (Xbox 360 wired etc): hardware ID has
/// <c>&amp;IG_00</c> for HIDAPI/SDL3 filtering plus <c>USB\MS_COMP_XUSB10</c>
/// compatible IDs so WGI recognizes it as a gamepad. The XUSB companion
/// is created separately.</item>
/// <item><b>Plain HID path</b> (DualSense, generic gamepads): standard
/// HIDClass enumerator. No upper filters, no IG_, just our HID minidriver.</item>
/// </list>
///
/// <para>Critical race fixes baked in:</para>
/// <list type="bullet">
/// <item><b>ControllerIndex registry write:</b> the driver reads this from
/// <c>Device Parameters\ControllerIndex</c> at startup to know which
/// per-instance shared section to attach to.</item>
/// <item><b>HID-child wait:</b> after driver install, we wait for the HID
/// interface to actually arrive before returning, otherwise sequential
/// controller creation races and PnP arrival order doesn't match creation
/// order.</item>
/// </list>
///
/// <para><b>ContainerID note:</b> root-enumerated virtual devices get the
/// <c>{00000000-0000-0000-FFFF-FFFFFFFFFFFF}</c> null-sentinel container
/// on Win11 26200. Attempts to set a real ContainerID from user mode
/// (CM API, registry writes to <c>Enum\&lt;instId&gt;\ContainerID</c>, INF
/// directives) are all refused or silently ignored by PnP. The documented
/// workaround (DeviceOverrides with Removable=1) also doesn't trigger
/// container generation for root devices without a location path on this
/// build. UMDF2 cannot act as a bus driver, so there's no user-mode path
/// to provide a real ContainerID. Consumers that dedupe on ContainerID
/// will treat our virtuals as individually-containerless devices.</para>
/// </summary>
internal static partial class DeviceNodeCreator
{
    // T34: per-hwId DeviceOverrides write dedup. Same hwId = same override
    // path, so writing the same Removable=1 value more than once per process
    // is wasted work. ConcurrentBag-flavored dedup (HashSet behind a lock).
    private static readonly System.Collections.Generic.HashSet<string> s_deviceOverridesWritten =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_deviceOverridesLock = new();
    /// <summary>HID class GUID: every virtual controller is created in this class.</summary>
    private static readonly Guid HIDClassGuid =
        new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

    /// <summary>Result of creating a device node: instance ID + the in-process device-info handle.
    /// On success the caller can pass the instanceId to e.g. <c>WaitForHidChild</c> for downstream waits.
    /// On failure InstanceId is null.</summary>
    public readonly struct Result
    {
        public readonly bool Success;
        public readonly string? InstanceId;
        public Result(bool success, string? instanceId) { Success = success; InstanceId = instanceId; }
    }

    /// <summary>Create a virtual device node for the given profile and assign it the
    /// given controller index. After this returns successfully:
    /// <list type="bullet">
    /// <item>The device exists at <c>ROOT\&lt;enumerator&gt;\&lt;instId&gt;</c></item>
    /// <item>Its <c>Device Parameters\ControllerIndex</c> = <paramref name="controllerIndex"/></item>
    /// <item>The driver is installed and bound: the best HIDMaestro package
    ///       in the driver store, on this devnode only</item>
    /// <item>The HID child interface has arrived (so the next per-controller
    ///       setup can start without racing PnP)</item>
    /// </list>
    /// </summary>
    public static Result CreateDeviceNode(ControllerProfile profile, int controllerIndex,
                                          DeviceIdentity? identity = null)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        identity ??= DeviceIdentity.ForIndex(controllerIndex);

        string vid = $"{profile.VendorId:X4}";
        string pid = $"{profile.ProductId:X4}";

        // driverPid: alternate PID in the hardware ID used for driver matching only
        // (e.g. xinputhid's INF matches PID 0x02FF). Apps still see the real PID via
        // HID attributes: the driver returns it from IOCTL_HID_GET_DEVICE_ATTRIBUTES.
        string hwPid = profile.ResolvedDriverPid != null
            ? $"{Convert.ToUInt16(profile.ResolvedDriverPid, 16):X4}" : pid;

        string desc = profile.ProductString;
        string enumerator, hwId;

        if (profile.UsesUpperFilter)
        {
            // xinputhid path. The &IG_00 in the path is what causes HIDAPI/SDL3 to
            // skip the device (HIDAPI's GAMECONTROLLER blocklist), forcing them to
            // use XInput instead: which is what we want for Xbox Series.
            enumerator = $"VID_{vid}&PID_{hwPid}&IG_00";
            hwId = $"root\\VID_{vid}&PID_{hwPid}&IG_00";
        }
        else if (profile.VendorId == 0x045E)
        {
            // Xbox legacy path (e.g. Xbox 360 wired). Same &IG_00 filtering plus
            // FunctionMode=1 in the registry enables WinExInput on the main device
            // so WGI fires GamepadAdded for it.
            enumerator = $"VID_{vid}&PID_{pid}&IG_00";
            hwId = $"root\\VID_{vid}&PID_{pid}&IG_00";
        }
        else
        {
            // Standard HID device: DualSense, generic third-party gamepads, etc.
            // No upper filter, plain HID class.
            enumerator = "HIDClass";
            hwId = $"root\\VID_{vid}&PID_{pid}";
        }
        string hwMulti = $"{hwId}\0root\\HIDMaestro\0\0";

        // Issue #60. The instance id is the identity's, not a number Windows
        // generates, so this parent has the same name on every life and the
        // HID child below it can keep its path. A record with that name that
        // is still around (a prior life's crash residue, or a phantom whose
        // teardown timed out) is removed first; a record that is not ours
        // is a hard stop, because claiming it would mutate a foreign device
        // (issue #28). No second parent on this prefix, ever: see
        // ClearParentIdPrefixRivals for why a failure there has to stop the
        // create rather than be logged past.
        //
        // The registration gate (DeviceManager.EnterRegistrationGate) does
        // both removals outside the machine-wide PnP lock, waits for Windows'
        // install queue, and returns holding the lock, which this create
        // lets go after DIF_REGISTERDEVICE. Every registration and removal
        // of a program of this release holds that lock. Two bugchecks 0x139
        // on 2026-10-14 and 10-15 fired in this registration, at its class
        // GUID write, while other programs created and removed HIDClass
        // devnodes. The lock and the gate narrow that window. Neither is
        // shown to close it.
        string instId = $@"ROOT\{enumerator}\{identity.Token}";
        using var pnp = DeviceManager.EnterRegistrationGate(instId, DeviceManager.RegistrationKind.Root,
            removePriorLife: () =>
            {
                if (!DeviceManager.IsHidMaestroOwned(instId))
                {
                    DeviceOrchestrator.LogDiag($"      {instId} exists and is not HIDMaestro-owned; refusing to claim it");
                    return null;
                }
                DeviceOrchestrator.LogDiag($"      {instId} still exists from a prior life; removing before recreate");
                return DeviceManager.RemovePriorLife(instId, timeoutMs: 120_000);
            },
            clearRivals: remove => DeviceManager.ClearParentIdPrefixRivals("ROOT", enumerator, identity.Token, identity.ParentIdPrefix, remove),
            out _);
        if (pnp == null)
            return new Result(false, null);

        Guid classGuid = HIDClassGuid;
        IntPtr dis = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (dis == new IntPtr(-1)) return new Result(false, null);

        try
        {
            // SP_DEVINFO_DATA: 32 bytes on x64 (cbSize at offset 0)
            byte[] devInfoBuf = new byte[32];
            int devInfoSize = IntPtr.Size == 8 ? 32 : 28;
            BitConverter.GetBytes(devInfoSize).CopyTo(devInfoBuf, 0);
            var devInfoHandle = GCHandle.Alloc(devInfoBuf, GCHandleType.Pinned);

            try
            {
                // Without DICD_GENERATE_ID the name is the full instance id.
                bool created = SetupDiCreateDeviceInfoW(dis, instId, ref classGuid, desc,
                        IntPtr.Zero, 0, devInfoHandle.AddrOfPinnedObject());
                int createErr = created ? 0 : Marshal.GetLastWin32Error();

                // A create that died between SetupDiCreateDeviceInfo and
                // DIF_REGISTERDEVICE leaves the instance key behind, marked
                // Phantom, with no devnode for CM_Locate to find. Because the
                // name is the identity's and never changes, that one orphan
                // would refuse this controller forever with
                // ERROR_DEVINST_ALREADY_EXISTS. Seen on 26200 after a create
                // was killed while PnP was blocked. The key is ours to clear
                // when its hardware ids say so and nothing answers to it.
                if (!created && createErr == ERROR_DEVINST_ALREADY_EXISTS
                    && CM_Locate_DevNodeW(out _, instId, 0) != 0
                    && CM_Locate_DevNodeW(out _, instId, 1) != 0
                    && DeviceManager.IsHidMaestroOwned(instId))
                {
                    DeviceOrchestrator.LogDiag($"      {instId} is an orphan instance key with no devnode; clearing it and retrying");
                    try
                    {
                        using var enumKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                            $@"SYSTEM\CurrentControlSet\Enum\ROOT\{enumerator}", writable: true);
                        enumKey?.DeleteSubKeyTree(identity.Token, throwOnMissingSubKey: false);
                    }
                    catch (Exception ex)
                    {
                        DeviceOrchestrator.LogDiag($"      orphan key delete FAILED: {ex.GetType().Name}");
                    }
                    created = SetupDiCreateDeviceInfoW(dis, instId, ref classGuid, desc,
                            IntPtr.Zero, 0, devInfoHandle.AddrOfPinnedObject());
                    createErr = created ? 0 : Marshal.GetLastWin32Error();
                }

                if (!created)
                {
                    DeviceOrchestrator.LogDiag(
                        $"      SetupDiCreateDeviceInfoW({instId}) FAILED (Win32={createErr})");
                    return new Result(false, null);
                }

                byte[] hwBytes = Encoding.Unicode.GetBytes(hwMulti);
                if (!SetupDiSetDeviceRegistryPropertyW(dis, devInfoHandle.AddrOfPinnedObject(),
                        1 /*SPDRP_HARDWAREID*/, hwBytes, (uint)hwBytes.Length))
                    return new Result(false, null);

                // Issue #60. The HID child's instance id is
                // <ParentIdPrefix>&<collection>, and PnP reads the prefix
                // from this parent's instance key when the child first
                // arrives, minting a new counter value only when the value
                // is absent. SetupDiCreateDeviceInfoW has created the key
                // by now (it holds the hardware id just written), so the
                // identity's prefix goes in before DIF_REGISTERDEVICE and
                // the child comes back as the same HID\...\1&hash&0&0000
                // every life. Measured on 26200: three lives with the value
                // pre-written produced one child path; three without it
                // counted 0, 1, 2.
                DeviceIdentity.ApplyParentIdPrefix(instId, identity.ParentIdPrefix, createKey: true);

                // Issue #59. XUSB-companion profiles carry the xinputhid
                // tripwire on this HID parent so WGI's OnPnpDeviceAdded
                // routes the HID child away from HidClient::CreateProvider,
                // leaving the companion's XUSB Gamepad as the only WGI
                // entity. WHERE this is written decides whether it works:
                // the value must be part of the device's property state
                // when the devnode is REGISTERED. A plain registry write
                // to Enum\<id>\UpperFilters after DIF_REGISTERDEVICE is
                // invisible to the check at the HID child's first arrival
                // and only honored on a later re-arrival - measured both
                // ways on 26200: post-registration write doubled WGI in
                // 3 of 3 fresh creates, while a disable/enable of the HID
                // child (a re-arrival) was gated correctly every time. So
                // it goes through SetupDi on the devinfo element, exactly
                // like the hardware id above, before registration exists.
                if (profile.RequiresXusbCompanion)
                {
                    byte[] ufBytes = Encoding.Unicode.GetBytes("xinputhid\0\0");
                    if (!SetupDiSetDeviceRegistryPropertyW(dis, devInfoHandle.AddrOfPinnedObject(),
                            17 /*SPDRP_UPPERFILTERS*/, ufBytes, (uint)ufBytes.Length))
                        DeviceOrchestrator.LogDiag(
                            "      SPDRP_UPPERFILTERS pre-registration write failed; " +
                            "WGI may double this controller (issue #59)");
                }

                // Compatible IDs serve two purposes:
                //   1. WGI / GameInputSvc identifies Xbox controllers via
                //      USB\MS_COMP_XUSB10. Only set for non-xinputhid Xbox.
                //   2. SDL3 / hidapi (used by Steam Input + every SDL-based
                //      game) walks up to the parent devnode and substring-greps
                //      DEVPKEY_Device_CompatibleIds for "USB", "BTHENUM",
                //      "BTHLEDEVICE", etc. to set is_bluetooth. Without a
                //      BTHENUM compat ID on a profile declaring connection
                //      "bluetooth", SDL falls through to bus_type=UNKNOWN
                //      → is_bluetooth=false → SDL_hidapi_ps5 picks USB
                //      Report 0x02 effects framing. Steam then misclassifies
                //      a virtual DualSense BT as USB, breaking effect
                //      passthrough and the "Bluetooth" badge in the Steam
                //      Input config UI.
                //   The BTHENUM string only needs to exist as a substring;
                //   real BT HID HCI form is `BTHENUM\Dev_VID&02xxxx_PID&yyyy`.
                //   We mint a matching string from the profile's VID/PID;
                //   nothing on Windows side actually walks up to a real BT
                //   radio so the form just has to satisfy hidapi's check.
                string compatMulti = null!;
                if (!profile.UsesUpperFilter && profile.VendorId == 0x045E)
                {
                    compatMulti = "USB\\MS_COMP_XUSB10\0USB\\Class_FF&SubClass_5D&Prot_01\0USB\\Class_FF&SubClass_5D\0\0";
                }
                else if (!profile.UsesUpperFilter
                      && string.Equals(profile.Connection, "bluetooth", StringComparison.OrdinalIgnoreCase))
                {
                    // Gated on !UsesUpperFilter so xinputhid profiles (Xbox
                    // Series BT, Xbox Elite v2 BT, Xbox One BT) keep their
                    // existing BTHLEDEVICE compat ID on the gamepad-companion
                    // devnode (DeviceOrchestrator.CreateGamepadCompanion) and
                    // never get a SECOND, different-shaped BT compat ID on
                    // the main device. BLE-keyed consumers (Settings →
                    // Devices "Bluetooth", browser Gamepad API) keep working
                    // unchanged. Sony BT and any generic non-Xbox BT profile
                    // (which doesn't go through the xinputhid path) gets the
                    // BTHENUM string here so SDL3/hidapi's bus-type
                    // substring-match resolves to BLUETOOTH.
                    compatMulti = $"BTHENUM\\Dev_VID&02{vid}_PID&{pid}\0BTHENUM\\{{00001124-0000-1000-8000-00805f9b34fb}}_VID&02{vid}_PID&{pid}\0\0";
                }
                if (compatMulti != null)
                {
                    byte[] compatBytes = Encoding.Unicode.GetBytes(compatMulti);
                    SetupDiSetDeviceRegistryPropertyW(dis, devInfoHandle.AddrOfPinnedObject(),
                        2 /*SPDRP_COMPATIBLEIDS*/, compatBytes, (uint)compatBytes.Length);
                }

                // DeviceOverrides marks the device as Removable BEFORE registration.
                // This tells PnP to generate a unique ContainerId per instance instead
                // of merging all our ROOT devices into one container in Settings.
                // T34: per-hwId dedup: the override path is keyed only by hwId,
                // so the same write happens for every controller of the same profile.
                // Skip after the first per-process per-hwId write.
                bool needsOverride;
                lock (s_deviceOverridesLock)
                    needsOverride = s_deviceOverridesWritten.Add(hwId);
                if (needsOverride)
                {
                    try
                    {
                        string overrideHwId = hwId.Replace('\\', '#');
                        string overridePath = $@"SYSTEM\CurrentControlSet\Control\DeviceOverrides\{overrideHwId}\*";
                        using var overrideKey = Registry.LocalMachine.CreateSubKey(overridePath);
                        overrideKey.SetValue("Removable", 1, RegistryValueKind.DWord);
                    }
                    catch
                    {
                        // Re-allow retry on next call if the write failed.
                        lock (s_deviceOverridesLock) s_deviceOverridesWritten.Remove(hwId);
                    }
                }

                // DIF_REGISTERDEVICE actually creates the PnP node. This is admin-only
                // (SeLoadDriverPrivilege): failures here mean the consumer process
                // isn't elevated.
                if (!SetupDiCallClassInstaller(0x19 /*DIF_REGISTERDEVICE*/, dis,
                        devInfoHandle.AddrOfPinnedObject()))
                    return new Result(false, null);
                pnp.Dispose();
            }
            finally
            {
                devInfoHandle.Free();
            }

            // ── post-creation: assign ControllerIndex ────────────────────────
            //
            // The instance id is ours by construction (issue #60), so the
            // ControllerIndex the driver reads at start goes straight onto
            // it. The old walk that hunted for "the live node without a
            // ControllerIndex" is gone with the generated names that made
            // it necessary (and that once claimed a coexisting vJoy device,
            // issue #28).
            string? newlyCreatedInstId = null;
            try
            {
                if (CM_Locate_DevNodeW(out uint _, instId, 0) == 0)
                {
                    string dpPath = $@"SYSTEM\CurrentControlSet\Enum\{instId}\Device Parameters";
                    using var dpKey = Registry.LocalMachine.CreateSubKey(dpPath);
                    dpKey.SetValue("ControllerIndex", controllerIndex, RegistryValueKind.DWord);
                    newlyCreatedInstId = instId;
                }
            }
            catch { }

            // (Removed 2026-04-21: post-registration registry write to
            // Enum\<instId>\ContainerID. PnP caches ContainerID at first
            // enumeration and ignores subsequent registry writes to this
            // path: stable MS behavior across Windows 10/11. The write
            // ran every deploy but never took effect; verified empirically
            // during issue #8 investigation. The DeviceOverrides write
            // above is retained because its effect on older Windows
            // versions has not been verified.)

            // Install the driver on this devnode alone. DiInstallDevice with no
            // driver named takes the best match the driver store holds, which
            // is the package DriverDeploy left there: this SDK's, or the one it
            // kept because running programs use it or because it is newer.
            // Until v1.13.0 this was UpdateDriverForPlugAndPlayDevices with the
            // VID/PID hardware id and this SDK's own INF, which installs on
            // every present device with that id whenever the INF ranks better
            // ("installs the driver package on all devices present in the
            // system that have a hardware ID or compatible ID that matches",
            // learn.microsoft.com, SetupAPI functions that simplify driver
            // installation): a newer SDK's create moved another program's
            // controller of the same profile onto its driver, and staged its
            // package beside one the deploy policy had kept. Perf audit
            // 2026-07-21: a binding failure is named in the diag at once rather
            // than left to the two readiness waits below.
            if (!InstallBestStoreDriver(dis, instId))
            {
                int err = Marshal.GetLastWin32Error();
                DeviceOrchestrator.LogDiag(
                    $"      DiInstallDevice FAILED (Win32={err}) for {instId}; downstream waits will time out");
            }

            // Wait for the HID interface to arrive: this is what the Phase 1 loop
            // races against. The enumerator MUST match the one we created the device
            // under (xinputhid uses the &IG_00 form, plain HID uses HIDClass).
            string devEnumer;
            if (profile.UsesUpperFilter)
                devEnumer = $"VID_{profile.VendorId:X4}&PID_{(profile.ResolvedDriverPid != null ? Convert.ToUInt16(profile.ResolvedDriverPid, 16) : profile.ProductId):X4}&IG_00";
            else if (profile.VendorId == 0x045E)
                devEnumer = $"VID_{profile.VendorId:X4}&PID_{profile.ProductId:X4}&IG_00";
            else
                devEnumer = "HIDClass";

            // T23-2: fast-path the instance-ID lookup. The pre-DIF_REGISTERDEVICE
            // walk above already discovered our newly-created devnode. After
            // UpdateDriverForPlugAndPlayDevicesW the device's enumerator path
            // doesn't change for the &IG_00 / VID_* path (the device stays at
            // ROOT\<enumerator>\<inst>). Skip the redundant walk when we
            // already have the ID; fall through to the registry walk only as
            // a defensive fallback.
            string? devInstId = newlyCreatedInstId;
            if (devInstId != null && CM_Locate_DevNodeW(out uint _, devInstId, 0) != 0)
                devInstId = null;
            if (devInstId == null)
            try
            {
                using var ek = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\ROOT\{devEnumer}");
                if (ek != null)
                {
                    foreach (var inst in ek.GetSubKeyNames())
                    {
                        string cand = $@"ROOT\{devEnumer}\{inst}";
                        if (CM_Locate_DevNodeW(out uint _, cand, 0) != 0) continue;
                        using var dp = ek.OpenSubKey($@"{inst}\Device Parameters");
                        if (dp?.GetValue("ControllerIndex") is int ci && ci == controllerIndex)
                        {
                            devInstId = cand;
                            break;
                        }
                    }
                }
            }
            catch { }

            if (devInstId != null)
            {
                DeviceManager.WaitForHidChild(devInstId);
            }

            // Apply the friendly name directly to the device we just created
            // (parentId is in scope). The prior FixHidChildNames call walked
            // SWD\ + ROOT\ enumerator subtrees by ControllerIndex to find the
            // same devnode we already have: pure overhead. SetAllNamingProperties
            // is a strict superset (BusReportedDeviceDesc + FriendlyName +
            // DeviceDesc on root + first HID child) and skips the walk.
            string displayName = profile.DeviceDescription ?? profile.ProductString;
            if (devInstId != null)
                try { DeviceProperties.SetAllNamingProperties(devInstId, displayName); }
                catch { }

            return new Result(true, devInstId);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(dis);
        }
    }

    // ── private P/Invokes ────────────────────────────────────────────────

    [LibraryImport("SetupAPI.dll", SetLastError = true)]
    private static partial IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

    [LibraryImport("SetupAPI.dll", EntryPoint = "SetupDiCreateDeviceInfoW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiCreateDeviceInfoW(IntPtr DeviceInfoSet, string DeviceName, ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, int CreationFlags, IntPtr DeviceInfoData);

    [LibraryImport("SetupAPI.dll", EntryPoint = "SetupDiSetDeviceRegistryPropertyW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, int Property, byte[] PropertyBuffer, uint PropertyBufferSize);

    [LibraryImport("SetupAPI.dll", EntryPoint = "SetupDiCallClassInstaller", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, IntPtr DeviceInfoData);

    [LibraryImport("SetupAPI.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    private const int ERROR_DEVINST_ALREADY_EXISTS = unchecked((int)0xE0000207);

    /// <summary>DiInstallDevice on one element of <paramref name="dis"/>, with
    /// no driver named, so the best driver-store match is installed on that
    /// device and no other. The element is opened by instance id because the
    /// create's SP_DEVINFO_DATA buffer has been released by now.</summary>
    private static bool InstallBestStoreDriver(IntPtr dis, string instId)
    {
        byte[] buf = new byte[IntPtr.Size == 8 ? 32 : 28];
        BitConverter.GetBytes(buf.Length).CopyTo(buf, 0);
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            if (!SetupDiOpenDeviceInfoW(dis, instId, IntPtr.Zero, 0, handle.AddrOfPinnedObject()))
                return false;
            return DiInstallDevice(IntPtr.Zero, dis, handle.AddrOfPinnedObject(), IntPtr.Zero,
                                   DIIDFLAG_NOFINISHINSTALLUI, out _);
        }
        finally { handle.Free(); }
    }

    private const uint DIIDFLAG_NOFINISHINSTALLUI = 0x00000002;

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DiInstallDevice(IntPtr hwndParent, IntPtr DeviceInfoSet, IntPtr DeviceInfoData, IntPtr DriverInfoData, uint Flags, [MarshalAs(UnmanagedType.Bool)] out bool NeedReboot);

    [LibraryImport("SetupAPI.dll", EntryPoint = "SetupDiOpenDeviceInfoW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInfoW(IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint OpenFlags, IntPtr DeviceInfoData);

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
}
