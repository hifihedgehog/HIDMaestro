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
    /// <paramref name="registered"/> is called with the instance ID as soon
    /// as DIF_REGISTERDEVICE has made the device, so a caller whose create
    /// fails at any later step, this method's lookup included, removes the
    /// device by that ID.
    /// </summary>
    public static Result CreateDeviceNode(ControllerProfile profile, PresentationPlan plan, int controllerIndex,
                                          DeviceIdentity? identity = null, Action<string>? registered = null)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        identity ??= DeviceIdentity.ForIndex(controllerIndex);

        // The plan names the enumerator and the hardware and compatible IDs
        // (PresentationPlan.Of). An &IG_00 enumerator is what makes HIDAPI
        // and SDL3 skip the device and read it through XInput instead.
        string desc = profile.ProductString;
        string enumerator = plan.Enumerator!;
        string hwId = plan.HardwareIds[0];
        string hwMulti = string.Join("\0", plan.HardwareIds) + "\0\0";

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
        // The identity's own child path unless an older build's vetoed
        // removal left a record there with no driver, which no later child
        // on that path starts from (DeviceManager.ChildPrefixFor). The
        // rival guard and the prefix written below take the one chosen.
        string childPrefix = DeviceManager.ChildPrefixFor(enumerator, identity.ParentIdPrefix);
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
            clearRivals: remove => DeviceManager.ClearParentIdPrefixRivals("ROOT", enumerator, identity.Token, childPrefix, remove),
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
                DeviceIdentity.ApplyParentIdPrefix(instId, childPrefix, createKey: true);

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
                if (plan.UpperFilterTripwire)
                {
                    byte[] ufBytes = Encoding.Unicode.GetBytes("xinputhid\0\0");
                    if (!SetupDiSetDeviceRegistryPropertyW(dis, devInfoHandle.AddrOfPinnedObject(),
                            17 /*SPDRP_UPPERFILTERS*/, ufBytes, (uint)ufBytes.Length))
                        DeviceOrchestrator.LogDiag(
                            "      SPDRP_UPPERFILTERS pre-registration write failed; " +
                            "WGI may double this controller (issue #59)");
                }

                // The compatible IDs the plan gives (PresentationPlan.Of): the
                // XUSB ones WGI and GameInputSvc read, or the Bluetooth ones
                // SDL3 and hidapi read from the parent for the bus type.
                string? compatMulti = plan.CompatibleIds.Count > 0
                    ? string.Join("\0", plan.CompatibleIds) + "\0\0" : null;
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
                registered?.Invoke(instId);
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
            // Until v2.0.0 this was UpdateDriverForPlugAndPlayDevices with the
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
            if (!InstallBestStoreDriver(dis, instId, plan))
            {
                int err = Marshal.GetLastWin32Error();
                DeviceOrchestrator.LogDiag(
                    $"      DiInstallDevice FAILED (Win32={err}) for {instId}; downstream waits will time out");
            }

            // Wait for the HID interface to arrive: this is what the Phase 1 loop
            // races against. The enumerator is the one the device was created
            // under.
            string devEnumer = enumerator;

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
                DeviceOrchestrator.RootChildWait(devInstId, controllerIndex);
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

    /// <summary>DiInstallDevice on one element of <paramref name="dis"/>, so
    /// the best driver-store match is installed on that device and no other.
    /// The element is opened by instance id because the create's
    /// SP_DEVINFO_DATA buffer has been released by now.
    ///
    /// <para>With no driver named, DiInstallDevice "searches the drivers
    /// preinstalled in the driver store for the driver that is the best match
    /// to the specified device" (Learn, DiInstallDevice). That search is most
    /// of a create: in setupapi.dev.log of 2026-10-19 its Build Driver List
    /// took 101 ms and DIF_SELECTBESTCOMPATDRV 52 ms of a 376 ms
    /// DiInstallDevice. Its answer depends on the device's hardware and
    /// compatible ids and on the driver store, so it is kept for the ids with
    /// the state of the driver store it was found in
    /// (<see cref="StoreDriverChoice"/>). While that state holds, the next
    /// create of the same ids, in this process or a later one, builds its
    /// driver list from that one INF (DI_ENUMSINGLEINF) and passes the driver
    /// to DiInstallDevice, the steps Learn's remarks give for naming one. Any
    /// other outcome, an INF gone included, takes the search.</para></summary>
    private static bool InstallBestStoreDriver(IntPtr dis, string instId, PresentationPlan plan)
    {
        string ids = string.Join("\0", plan.HardwareIds) + "\0\0" + string.Join("\0", plan.CompatibleIds);
        string? state = StoreDriverChoice.State();
        if (state != null && StoreDriverChoice.For(ids, state) is { } inf)
        {
            if (InstallFromStoreInf(instId, inf, out int failed))
            {
                DeviceOrchestrator.LogDiag($"      driver from {inf}, the store's search skipped");
                return true;
            }
            StoreDriverChoice.Forget(ids);
            DeviceOrchestrator.LogDiag($"      driver from {inf} not installed (Win32={failed}); searching the store");
        }

        byte[] buf = new byte[IntPtr.Size == 8 ? 32 : 28];
        BitConverter.GetBytes(buf.Length).CopyTo(buf, 0);
        var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            if (!SetupDiOpenDeviceInfoW(dis, instId, IntPtr.Zero, 0, handle.AddrOfPinnedObject()))
                return false;
            if (!DiInstallDevice(IntPtr.Zero, dis, handle.AddrOfPinnedObject(), IntPtr.Zero,
                                 DIIDFLAG_NOFINISHINSTALLUI, out _))
                return false;
        }
        finally { handle.Free(); }

        // What the search chose: the device's driver INF, a published name
        // (DEVPKEY_Device_DriverInfPath), and that package's INF in the
        // driver store, the path the search's driver node carries. The state
        // is read again: a package added during the search makes the choice
        // one nothing can match.
        if (state != null && StoreInfOf(instId) is { } chosen && StoreDriverChoice.State() == state)
            StoreDriverChoice.Remember(ids, state, chosen);
        return true;
    }

    /// <summary>The store INF the driver-store search chose for a device's
    /// hardware and compatible ids, with the state of the driver store it
    /// was chosen in. Kept in the process and under
    /// <c>HKLM\SOFTWARE\HIDMaestro\StoreDriverChoice</c>, one value per
    /// set of ids, so a program's first create skips the search too. The
    /// key goes with <c>SOFTWARE\HIDMaestro</c> when the packages are
    /// removed.</summary>
    internal static class StoreDriverChoice
    {
        private const string KeyPath = @"SOFTWARE\HIDMaestro\StoreDriverChoice";
        private static readonly object s_lock = new();
        private static readonly System.Collections.Generic.Dictionary<string, (string State, string Inf)> s_byIds =
            new(StringComparer.Ordinal);

        /// <summary>The state of the driver store a choice holds for: the
        /// installed-payload record, which every deploy of the driver
        /// rewrites (<see cref="DriverDeploy.DeployMachine.ManifestHash"/>),
        /// and the last write of the driver store's FileRepository
        /// directory, which moves when any package is added or removed: on
        /// the bench machine on 2026-10-19 it moved with each deploy and
        /// stayed through the device installs between them. Null when either
        /// cannot be read.</summary>
        internal static string? State()
        {
            string? manifest = DriverDeploy.Machine.ManifestHash();
            if (manifest == null) return null;
            try
            {
                var repo = new System.IO.DirectoryInfo(System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "DriverStore", "FileRepository"));
                if (!repo.Exists) return null;
                return manifest + "|" + repo.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>The INF chosen for the ids in this state, from this
        /// process or the registry, or null.</summary>
        internal static string? For(string ids, string state)
        {
            lock (s_lock)
                if (s_byIds.TryGetValue(ids, out var c) && c.State == state) return c.Inf;
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(KeyPath);
                if (k?.GetValue(ValueName(ids)) is string[] { Length: 2 } v && v[0] == state && v[1].Length > 0)
                {
                    lock (s_lock) s_byIds[ids] = (state, v[1]);
                    return v[1];
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
            return null;
        }

        internal static void Remember(string ids, string state, string inf)
        {
            lock (s_lock) s_byIds[ids] = (state, inf);
            try
            {
                using var k = Registry.LocalMachine.CreateSubKey(KeyPath);
                k.SetValue(ValueName(ids), new[] { state, inf }, RegistryValueKind.MultiString);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        }

        internal static void Forget(string ids)
        {
            lock (s_lock) s_byIds.Remove(ids);
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
                k?.DeleteValue(ValueName(ids), throwOnMissingValue: false);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
        }

        /// <summary>The value's name: the SHA-256 of the ids, which hold
        /// characters a value name may not.</summary>
        private static string ValueName(string ids) =>
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ids)));
    }

    /// <summary>The driver-store INF of the driver installed on a present
    /// devnode, or null when it cannot be read.</summary>
    private static string? StoreInfOf(string instId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instId, 0) != 0) return null;
        var key = DEVPKEY_Device_DriverInfPath;
        uint len = 0;
        CM_Get_DevNode_PropertyW(dev, ref key, out _, null, ref len, 0);
        if (len == 0) return null;
        var buf = new byte[len];
        if (CM_Get_DevNode_PropertyW(dev, ref key, out _, buf, ref len, 0) != 0) return null;
        string published = Encoding.Unicode.GetString(buf, 0, (int)Math.Min(len, (uint)buf.Length)).TrimEnd('\0');
        if (published.Length == 0) return null;
        try { return PnputilHelper.DriverStorePathOf(published); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>DiInstallDevice of the one driver a driver list built from
    /// <paramref name="inf"/> alone gives the device, in a device information
    /// set of its own, so the create's set keeps its installation parameters
    /// for the search. Learn, DiInstallDevice, Remarks: "call
    /// SetupDiBuildDriverInfoList to build a list of drivers for the device
    /// and then call SetupDiEnumDriverInfo to enumerate the elements of the
    /// driver list", with DI_ENUMSINGLEINF, under which "DriverPath contains
    /// the path of a single INF file instead of a path of a directory"
    /// (SP_DEVINSTALL_PARAMS). The driver is the list's best rank
    /// (SP_DRVINSTALL_PARAMS.Rank, the lower the better), as the search
    /// ranks the drivers of one INF, whose date and version are the same. A
    /// best rank two drivers share is left to the search. False when no
    /// driver was installed, with the failed call's error in
    /// <paramref name="error"/>, 0 for a list with no driver or a
    /// tie.</summary>
    private static unsafe bool InstallFromStoreInf(string instId, string inf, out int error)
    {
        error = 0;
        if (inf.Length >= 260) return false;
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1)) { error = Marshal.GetLastWin32Error(); return false; }
        try
        {
            var dev = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
            var prm = new SP_DEVINSTALL_PARAMS_W { cbSize = (uint)sizeof(SP_DEVINSTALL_PARAMS_W) };
            if (!SetupDiOpenDeviceInfoW(set, instId, IntPtr.Zero, 0, (IntPtr)(&dev))
                || !SetupDiGetDeviceInstallParamsW(set, ref dev, ref prm))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            prm.Flags |= DI_ENUMSINGLEINF;
            inf.AsSpan().CopyTo(new Span<char>(prm.DriverPath, 260));
            prm.DriverPath[inf.Length] = '\0';
            if (!SetupDiSetDeviceInstallParamsW(set, ref dev, ref prm)
                || !SetupDiBuildDriverInfoList(set, ref dev, SPDIT_COMPATDRIVER))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            try
            {
                SP_DRVINFO_DATA_W best = default;
                uint bestRank = uint.MaxValue;
                bool tie = false;
                for (uint i = 0; ; i++)
                {
                    var drv = new SP_DRVINFO_DATA_W { cbSize = (uint)sizeof(SP_DRVINFO_DATA_W) };
                    if (!SetupDiEnumDriverInfoW(set, ref dev, SPDIT_COMPATDRIVER, i, ref drv)) break;
                    var dp = new SP_DRVINSTALL_PARAMS { cbSize = (uint)sizeof(SP_DRVINSTALL_PARAMS) };
                    if (!SetupDiGetDriverInstallParamsW(set, ref dev, ref drv, ref dp))
                    {
                        error = Marshal.GetLastWin32Error();
                        return false;
                    }
                    if (dp.Rank < bestRank) { best = drv; bestRank = dp.Rank; tie = false; }
                    else if (dp.Rank == bestRank) tie = true;
                }
                if (bestRank == uint.MaxValue || tie) return false;
                if (DiInstallDevice(IntPtr.Zero, set, (IntPtr)(&dev), (IntPtr)(&best), DIIDFLAG_NOFINISHINSTALLUI, out _)) return true;
                error = Marshal.GetLastWin32Error();
                return false;
            }
            finally { SetupDiDestroyDriverInfoList(set, ref dev, SPDIT_COMPATDRIVER); }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private const uint DIIDFLAG_NOFINISHINSTALLUI = 0x00000002;
    private const uint DI_ENUMSINGLEINF = 0x00010000;
    private const uint SPDIT_COMPATDRIVER = 2;

    // devpkey.h: DEVPKEY_Device_DriverInfPath, {a8b865dd-2e3d-4094-ad97-e593a70c75d6}, 5.
    private static readonly DEVPROPKEY DEVPKEY_Device_DriverInfPath = new()
    {
        fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"),
        pid = 5,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    // SP_DRVINFO_DATA_V2_W (setupapi.h). FILETIME is two DWORDs.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SP_DRVINFO_DATA_W
    {
        public uint cbSize;
        public uint DriverType;
        public IntPtr Reserved;
        public fixed char Description[256];
        public fixed char MfgName[256];
        public fixed char ProviderName[256];
        public uint DriverDateLow;
        public uint DriverDateHigh;
        public ulong DriverVersion;
    }

    // SP_DRVINSTALL_PARAMS (setupapi.h): Rank is the driver's rank for this
    // device, lower being the better match.
    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DRVINSTALL_PARAMS
    {
        public uint cbSize;
        public uint Rank;
        public uint Flags;
        public UIntPtr PrivateData;
        public uint Reserved;
    }

    // SP_DEVINSTALL_PARAMS_W (setupapi.h, which packs to 8 bytes on 64-bit
    // Windows): 584 bytes there, DriverPath at offset 60.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct SP_DEVINSTALL_PARAMS_W
    {
        public uint cbSize;
        public uint Flags;
        public uint FlagsEx;
        public IntPtr hwndParent;
        public IntPtr InstallMsgHandler;
        public IntPtr InstallMsgHandlerContext;
        public IntPtr FileQueue;
        public UIntPtr ClassInstallReserved;
        public uint Reserved;
        public fixed char DriverPath[260];
    }

    [LibraryImport("SetupAPI.dll", EntryPoint = "SetupDiCreateDeviceInfoList", SetLastError = true)]
    private static partial IntPtr SetupDiCreateDeviceInfoList(IntPtr ClassGuid, IntPtr hwndParent);

    [LibraryImport("SetupAPI.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstallParamsW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_DEVINSTALL_PARAMS_W DeviceInstallParams);

    [LibraryImport("SetupAPI.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiSetDeviceInstallParamsW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_DEVINSTALL_PARAMS_W DeviceInstallParams);

    [LibraryImport("SetupAPI.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiBuildDriverInfoList(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint DriverType);

    [LibraryImport("SetupAPI.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDriverInfoList(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint DriverType);

    [LibraryImport("SetupAPI.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDriverInfoW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData, uint DriverType,
        uint MemberIndex, ref SP_DRVINFO_DATA_W DriverInfoData);

    [LibraryImport("SetupAPI.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDriverInstallParamsW(IntPtr DeviceInfoSet, ref SP_DEVINFO_DATA DeviceInfoData,
        ref SP_DRVINFO_DATA_W DriverInfoData, ref SP_DRVINSTALL_PARAMS DriverInstallParams);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Get_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY PropertyKey, out uint PropertyType,
        byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DiInstallDevice(IntPtr hwndParent, IntPtr DeviceInfoSet, IntPtr DeviceInfoData, IntPtr DriverInfoData, uint Flags, [MarshalAs(UnmanagedType.Bool)] out bool NeedReboot);

    [LibraryImport("SetupAPI.dll", EntryPoint = "SetupDiOpenDeviceInfoW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInfoW(IntPtr DeviceInfoSet, string DeviceInstanceId, IntPtr hwndParent, uint OpenFlags, IntPtr DeviceInfoData);

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
}
