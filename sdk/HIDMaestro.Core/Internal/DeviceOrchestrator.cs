using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>
/// Per-controller setup orchestrator. Owns the full sequence of steps that
/// turns a profile into a live virtual HID device. Handles all three profile
/// paths: xinputhid companion-only, non-xinputhid Xbox (XUSB companion), and
/// plain HID.
/// </summary>
internal static partial class DeviceOrchestrator
{
    private const string REG_BASE = @"SOFTWARE\HIDMaestro";
    private static string RegPathForIndex(int index) =>
        $@"{REG_BASE}\Controller{index}";

    // SwD instance suffixes are the controller's DeviceIdentity token
    // (issue #60): the same (enumerator + suffix + ContainerId) tuple on
    // every life of one identity. From v1.1.30 to v1.7.3 the suffix was
    // unique per SwDeviceCreate call, because a DIF_REMOVE on a
    // SWDeviceLifetimeParentPresent device left the software device alive
    // and a create with the same tuple reconnected to that half-removed
    // shell. Teardown has gone through SwDeviceSetLifetime(Handle) +
    // SwDeviceClose since v1.1.31, which destroys the software device, and
    // a fixed tuple recreates cleanly after it: measured on 26200 as three
    // lives each of the gamepad companion (Service bound, HID child started)
    // and the XUSB companion (interface published), with the phantom record
    // retained and with it purged, and with a fast remove that returned
    // before the cascade finished. The one thing that does leave an empty
    // shell is an instance key that exists BEFORE SwDeviceCreate, which
    // nothing here does.

    // ════════════════════════════════════════════════════════════════════
    //  Diagnostic log for teardown investigations: gated by env var
    //  HIDMAESTRO_DIAG=1. Writes to %TEMP%\HIDMaestro\teardown_diag.log.
    //  Each TeardownController / RemoveDevice / WaitForDeviceRemoval call
    //  emits one line with timestamp + elapsed-ms + outcome. Off by default
    //  to keep production runs file-write-clean; flip the env var to repro
    //  any future "device left behind after teardown" regression.
    // ════════════════════════════════════════════════════════════════════
    private static readonly bool s_diagEnabled =
        Environment.GetEnvironmentVariable("HIDMAESTRO_DIAG") == "1";
    private static readonly object s_diagLock = new();
    /// <summary>True when HIDMAESTRO_DIAG=1, so a caller can skip building
    /// a line nobody will read.</summary>
    internal static bool DiagEnabled => s_diagEnabled;
    private static StreamWriter? s_diagWriter;

    /// <summary>Open a diagnostic log under the user's %TEMP% for appending,
    /// creating its folder, with the user's own rights
    /// (<see cref="OpenVrPathRegistry.RunAsUser{T}"/>). The SDK usually runs
    /// elevated, and %TEMP% is a folder any of the user's programs can
    /// change, so the folder and file are opened as the user: whatever that
    /// folder resolves to, the open reaches nothing the user could not write
    /// already. The handle keeps working after the impersonation
    /// ends.</summary>
    internal static FileStream OpenUserLog(string dir, string path) =>
        OpenVrPathRegistry.RunAsUser(() =>
        {
            Directory.CreateDirectory(dir);
            return new FileStream(path, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096);
        });

    /// <summary>Sees every diag line, whether or not HIDMAESTRO_DIAG is set.
    /// pnp_notify_check's offline part sets it to read the lines a removal
    /// writes. Null outside it.</summary>
    internal static Action<string>? DiagTap;

    internal static void LogDiag(string message)
    {
        DiagTap?.Invoke(message);
        if (!s_diagEnabled) return;
        lock (s_diagLock)
        {
            try
            {
                if (s_diagWriter == null)
                {
                    string dir = Path.Combine(Path.GetTempPath(), "HIDMaestro");
                    string diagPath = Path.Combine(dir, "teardown_diag.log");
                    // T32-2: keep the StreamWriter open across LogDiag calls
                    // and flush per write. Was File.AppendAllText (open/write/
                    // close per call); during HIDMAESTRO_DIAG=1 battery runs
                    // that's ~3-4K file ops adding 10-40 s to the wall time.
                    // FileShare.ReadWrite | Delete so external tools can tail
                    // the file AND the harness's per-run Remove-Item can clear
                    // it even if a prior HIDMaestroTest writer didn't drain
                    // its ProcessExit cleanly.
                    var fs = OpenUserLog(dir, diagPath);
                    s_diagWriter = new StreamWriter(fs) { AutoFlush = true };
                    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                    {
                        try { lock (s_diagLock) { s_diagWriter?.Dispose(); s_diagWriter = null; } }
                        catch { }
                    };
                }
                s_diagWriter.Write('[');
                s_diagWriter.Write(DateTime.Now.ToString("HH:mm:ss.fff"));
                s_diagWriter.Write("] tid=");
                s_diagWriter.Write(Environment.CurrentManagedThreadId.ToString().PadRight(3));
                s_diagWriter.Write(' ');
                s_diagWriter.Write(message);
                s_diagWriter.Write('\n');
            }
            catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Timing instrumentation: gated by env var HIDMAESTRO_TIMING=1.
    //  When enabled, each SetupController step's duration is appended to
    //  %TEMP%\HIDMaestro\setup_timing.log so we can pin regressions across
    //  runs without polluting stdout. Zero-overhead when disabled.
    // ════════════════════════════════════════════════════════════════════
    private static readonly bool s_timingEnabled =
        Environment.GetEnvironmentVariable("HIDMAESTRO_TIMING") == "1";
    private static readonly object s_timingLock = new();
    private static StreamWriter? s_timingWriter;

    private static void LogTiming(int ctrlIdx, string profileId, string step, long elapsedMs)
    {
        if (!s_timingEnabled) return;
        lock (s_timingLock)
        {
            try
            {
                if (s_timingWriter == null)
                {
                    string dir = Path.Combine(Path.GetTempPath(), "HIDMaestro");
                    string timingPath = Path.Combine(dir, "setup_timing.log");
                    var fs = OpenUserLog(dir, timingPath);
                    s_timingWriter = new StreamWriter(fs) { AutoFlush = true };
                    AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                    {
                        try { lock (s_timingLock) { s_timingWriter?.Dispose(); s_timingWriter = null; } }
                        catch { }
                    };
                }
                s_timingWriter.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss.fff}] ctrl={ctrlIdx} profile={profileId,-24} step={step,-36} {elapsedMs,6}ms");
            }
            catch { }
        }
    }

    private readonly struct TimingScope : IDisposable
    {
        private readonly int _idx;
        private readonly string _profile;
        private readonly string _step;
        private readonly long _start;
        public TimingScope(int idx, string profile, string step)
        {
            _idx = idx; _profile = profile; _step = step;
            _start = s_timingEnabled ? Stopwatch.GetTimestamp() : 0L;
        }
        public void Dispose()
        {
            if (!s_timingEnabled) return;
            long ticks = Stopwatch.GetTimestamp() - _start;
            long ms = ticks * 1000L / Stopwatch.Frequency;
            LogTiming(_idx, _profile, _step, ms);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Per-controllerIndex teardown gate: defense-in-depth coordination
    //  between TeardownController and SetupController.
    //
    //  PadForge's swap path is:
    //      vc.Dispose()           // sync, blocks polling thread
    //      _ctx.CreateController()  // immediately after Dispose returns
    //
    //  PadForge's contract assumes Dispose returns ONLY after the prior
    //  device's full kernel cascade (xinputhid unbind, interface-class
    //  cleanup, ContainerID GC, slot release) is complete. The gate
    //  enforces that contract from BOTH sides:
    //
    //  Primary path: TeardownController calls WaitForDeviceRemoval
    //    (CM_Register_Notification → CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED).
    //    The notification fires only after the OS finalizes removal.
    //
    //  Defense-in-depth: a per-index ManualResetEventSlim. TeardownController
    //    Resets at entry and Sets at exit. SetupController Waits on it at
    //    entry. If the primary wait times out or returns early for any
    //    reason (kernel slow, xinputhid stuck, future regression), the
    //    new SetupController for the same index still blocks at the gate
    //    until the prior teardown's exit completes: preventing the
    //    "duplicate controller because the new bind pre-empted the old
    //    teardown" symptom.
    //
    //  Initial state of every gate is signaled (created lazily, fresh).
    //  Different controllerIndex values get separate gates: parallel
    //  teardown of unrelated controllers isn't serialized.
    // ════════════════════════════════════════════════════════════════════

    private static readonly Dictionary<int, ManualResetEventSlim> s_teardownGates = new();
    private static readonly object s_teardownGatesLock = new();

    private static ManualResetEventSlim GetTeardownGate(int controllerIndex)
    {
        lock (s_teardownGatesLock)
        {
            if (!s_teardownGates.TryGetValue(controllerIndex, out var ev))
            {
                // Created in signaled state: no teardown is in flight yet.
                ev = new ManualResetEventSlim(initialState: true);
                s_teardownGates[controllerIndex] = ev;
            }
            return ev;
        }
    }

    private static void BeginTeardownGate(int controllerIndex)
    {
        var ev = GetTeardownGate(controllerIndex);
        ev.Reset();
    }

    private static void EndTeardownGate(int controllerIndex)
    {
        ManualResetEventSlim? ev;
        lock (s_teardownGatesLock)
        {
            s_teardownGates.TryGetValue(controllerIndex, out ev);
        }
        ev?.Set();
    }

    /// <summary>Block until any in-flight TeardownController for this
    /// controllerIndex has fully completed. Called by SetupController at
    /// entry. Returns immediately if no teardown is in flight (gate already
    /// signaled). The caller-supplied timeout protects against a
    /// Reset-without-Set regression: beyond which we proceed regardless
    /// and trust the primary cascade-complete wait inside
    /// TeardownController.</summary>
    private static void WaitForPriorTeardown(int controllerIndex, int timeoutMs = 120_000)
    {
        ManualResetEventSlim? ev;
        lock (s_teardownGatesLock)
        {
            s_teardownGates.TryGetValue(controllerIndex, out ev);
        }
        ev?.Wait(TimeoutScale.Apply(timeoutMs));
    }

    // ════════════════════════════════════════════════════════════════════
    //  P/Invoke: CfgMgr32
    // ════════════════════════════════════════════════════════════════════

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [LibraryImport("CfgMgr32.dll", SetLastError = true)]
    private static partial uint CM_Set_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey, uint propertyType, byte[] propertyBuffer, uint propertyBufferSize, uint ulFlags);

    [LibraryImport("CfgMgr32.dll", SetLastError = true)]
    private static partial uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("CfgMgr32.dll", SetLastError = true)]
    private static partial uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    private const uint CM_LOCATE_DEVNODE_PHANTOM = 0x00000001;
    private const uint DN_HAS_PROBLEM = 0x00000400;
    private const uint DN_STARTED = 0x00000008;

    // ════════════════════════════════════════════════════════════════════
    //  Well-known GUIDs
    // ════════════════════════════════════════════════════════════════════

    private static readonly Guid XusbInterfaceGuid = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");

    // ════════════════════════════════════════════════════════════════════
    //  Process helper (silent: no Console output)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Run a program from System32 by its file name. The full path
    /// keeps CreateProcess from finding a program of that name in the
    /// application's or the current directory first.</summary>
    private static (int exitCode, string output) RunProcess(string systemTool, string args, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), systemTool), Arguments = args,
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        // See PnputilHelper.Run: sync ReadToEnd blocks past timeoutMs when the
        // child hangs. Async read + Kill-on-timeout makes the timeout real.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(TimeoutScale.Apply(timeoutMs)))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit(TimeoutScale.Apply(5_000));
        }
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        int exitCode;
        try { exitCode = proc.ExitCode; } catch { exitCode = -1; }
        return (exitCode, stdout + stderr);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Cleanup-paths topology (v1.13.0)
    //
    //  Every sweep but one acts only on proof that a device's owner is gone
    //  (OwnerCensus). Reading order: triggered earliest first.
    //
    //   1. OwnerSweep.ReclaimDeadOwners
    //        Trigger:  HMContext.InstallDriver, and once per process ahead
    //                  of the first create (OwnerSweep.SessionSweepOnce)
    //        Scope:    devices and USB/IP imports of dead owners: a record
    //                  whose owner claim is gone, or a v1.12.0 device whose
    //                  owner pipe has closed. Ledger holds of dead pids.
    //        Cost:     one census when nothing is dead, no enumerator walk
    //
    //   2. OwnerSweep.RemoveOrphaned
    //        Trigger:  RemoveAllVirtualControllers() and (bool), which
    //                  PadForge calls in its launch sweep, at engine start
    //                  and from its ProcessExit handler
    //        Scope:    (1), the v1.11.0 GameInput tables no running program
    //                  uses, then the packages only when no device of any
    //                  program is left on the driver. A running program's
    //                  controllers, the caller's own included, stay.
    //
    //   2a. OwnerSweep.RemoveOwnDevicesAtExit
    //        Trigger:  ProcessExit, registered with the owner claim, once
    //                  the phantom cleanup and this process's creates in
    //                  flight are done (OwnerSweep.CreateGate)
    //        Scope:    the devices of this process's controllers that it
    //                  did not dispose. OwnerSweep.ExitWaitMs bounds the
    //                  waits for the cleanup and a create in flight: past
    //                  it nothing is removed and the record stays for the
    //                  next sweep. The removal itself runs to its end, each
    //                  RemoveDevice within its own bound
    //
    //   3. RemoveEverything
    //        Trigger:  RemoveAllVirtualControllers(HMCleanupScope.Everything, bool)
    //        Scope:    every HIDMaestro device, import and ledger entry on
    //                  the machine, whoever made it. The pre-1.13.0 sweep.
    //                  It holds OwnerSweep.CreateGate for writing, so it
    //                  never runs beside (5) or a create of this process
    //
    //   4. RemoveOrphanHidChildren (DeviceManager)
    //        Trigger:  each teardown, once after a batch, and after (1)
    //        Scope:    HID children whose parent devnode is gone
    //
    //   5. Phantom cleanup: RemoveAccumulatedHmPhantoms (DeviceManager),
    //      RemoveGhostXusbInterfaces and RemoveOwnedInterfaceEntries
    //      (phantomOnly)
    //        Trigger:  a background thread once per process, started by
    //                  (1) or (2), behind OwnerSweep.CreateGate so it never runs
    //                  during one of this process's creates. It keeps the
    //                  gate, and each phantom's identity claim, until
    //                  Windows no longer knows the id it uninstalled,
    //                  with half the exit's budget of waiting in all per
    //                  run (OwnerSweep.UninstallWait). At exit it starts
    //                  no further uninstall, and the exit handler joins it
    //                  within the exit's budget (OwnerSweep.JoinPhantomCleanup).
    //                  A later call starts none. The call that starts it
    //                  returns without waiting for it to end, and a create
    //                  of this process and (3) wait behind it: one pass
    //                  over the phantoms, their uninstalls, at most
    //                  UninstallWaitBudgetMs of waiting in all, for the
    //                  PnP lock before each uninstall and for Windows
    //                  after it, and two interface passes that wait for
    //                  the lock within the same budget. A first call
    //                  made while the process exits (ProcessExitSignal)
    //                  runs it on the calling thread instead. Under .NET a
    //                  program in which no code that refers to the SDK ran
    //                  or was compiled before exit began misses that
    //                  signal, and the thread ends with the process
    //                  (OwnerSweep.StartPhantomCleanupOnce)
    //        Scope:    HIDMaestro records of devnodes that are not
    //                  present, except one whose identity claim another
    //                  program holds or whose default identity's index a
    //                  v1.12.0 program's owner pipe holds, and their
    //                  interface registrations
    // ════════════════════════════════════════════════════════════════════

    // Issue #28 (v1.3.16): resolve a HID child's parent instance ID so the
    // parent's HardwareID can prove ownership. HID children themselves
    // carry HidClass-level hwids (`HID_DEVICE_SYSTEM_GAME`, etc.) and don't
    // record "HIDMaestro": checking IsHidMaestroOwned on the child
    // directly would always miss. Returns null on lookup failure.
    private static string? TryResolveHidChildParent(RegistryKey childEnumKey, string instName)
    {
        // childEnumKey was opened at SYSTEM\CurrentControlSet\Enum\HID\<sub>.
        // Reconstruct the HID child path and ask CfgMgr32 for its parent.
        string deviceName;
        try { deviceName = System.IO.Path.GetFileName(childEnumKey.Name) ?? string.Empty; }
        catch { return null; }
        if (string.IsNullOrEmpty(deviceName)) return null;
        return DeviceManager.GetRegistryParentId(
            @"SYSTEM\CurrentControlSet\Enum\HID", deviceName, instName);
    }

    /// <summary>Delete the XUSB and WinExInput interface registrations of
    /// HIDMaestro devnodes. Interface-class keys encode the devnode path with
    /// <c>#</c> in place of <c>\</c>. Issue #28 (v1.3.16): the exclusively-HM
    /// forms (ROOT#HIDMAESTRO*, SWD#HIDMAESTRO*, and the April 2026
    /// ROOT#HMCOMPANION) go without further proof. The shared forms
    /// (ROOT#VID_*, ROOT#HIDCLASS, ROOT#HID_IG_*, ROOT#XNACOMPOSITE,
    /// ROOT#SYSTEM#) need the owning devnode's HardwareID proof, or no
    /// devnode record at all: an entry whose instance key is gone describes
    /// no device, ours or anyone's. The prior `ROOT#SYSTEM#(N>=2)`
    /// heuristic tagged ViGEmBus / HidHide entries as ours, and the full
    /// cleanup used to delete every ROOT# entry of both classes
    /// (foundation-sunshine #1138). Any other form is not ours.</summary>
    /// <param name="phantomOnly">Leave the entries of present devnodes alone.
    /// A present HIDMaestro devnode is a running program's controller, or
    /// the reclaim has just removed it, so only the explicit uninstall-grade
    /// sweep passes false.</param>
    internal static void RemoveOwnedInterfaceEntries(bool phantomOnly, Func<DeviceManager.PnpLockHold?>? holdLock = null)
    {
        foreach (var guid in new[] {
            "{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}",
            "{6c53d5fd-6480-440f-b618-476750c5e1a6}" })
        {
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{guid}", writable: true);
                if (classKey == null) continue;
                foreach (var subName in classKey.GetSubKeyNames())
                {
                    bool exclusivelyOurs =
                        subName.Contains("ROOT#HIDMAESTRO", StringComparison.OrdinalIgnoreCase) ||
                        subName.Contains("SWD#HIDMAESTRO", StringComparison.OrdinalIgnoreCase) ||
                        subName.Contains("ROOT#HMCOMPANION", StringComparison.OrdinalIgnoreCase);
                    bool sharedShape =
                        subName.Contains("ROOT#VID_", StringComparison.OrdinalIgnoreCase) ||
                        subName.Contains("ROOT#HIDCLASS", StringComparison.OrdinalIgnoreCase) ||
                        subName.Contains("ROOT#HID_IG", StringComparison.OrdinalIgnoreCase) ||
                        subName.Contains("ROOT#XNACOMPOSITE", StringComparison.OrdinalIgnoreCase) ||
                        subName.Contains("ROOT#SYSTEM#", StringComparison.OrdinalIgnoreCase);

                    bool isOurDevice = exclusivelyOurs;
                    // Decode the symbolic-link form back to a devnode
                    // instance path. The leading `##?#` (or `\??\`) and
                    // trailing `#{guid}` are interface-link encoding;
                    // the middle is `Enumerator#Device#Instance`.
                    string devPath = subName;
                    int guidStart = devPath.LastIndexOf("#{");
                    if (guidStart > 0) devPath = devPath.Substring(0, guidStart);
                    devPath = devPath.Replace("##?#", string.Empty);
                    devPath = devPath.Replace('#', '\\');
                    if (!exclusivelyOurs && !sharedShape) continue;
                    // A present devnode and an entry not shown to be ours are
                    // left without taking the lock, which is the safe side,
                    // so a call with nothing to delete waits for no other
                    // program.
                    if (phantomOnly && CM_Locate_DevNodeW(out _, devPath, 0) == 0)
                        continue;
                    if (!isOurDevice && sharedShape)
                    {
                        bool recordExists;
                        try
                        {
                            using var rec = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{devPath}");
                            recordExists = rec != null;
                        }
                        catch { recordExists = true; }
                        isOurDevice = !recordExists || DeviceManager.IsHidMaestroOwned(devPath);
                    }
                    if (!isOurDevice) continue;
                    // The presence check again and the delete under the PnP
                    // lock, so a create of this release cannot make the
                    // devnode present in between: its registration holds the
                    // same lock. A devnode whose lock did not come is left
                    // for the next run.
                    using var pnp = holdLock != null ? holdLock() : DeviceManager.HoldPnpLock();
                    if (pnp == null) continue;
                    if (phantomOnly && CM_Locate_DevNodeW(out _, devPath, 0) == 0)
                        continue;
                    try { classKey.DeleteSubKeyTree(subName); } catch { }
                }
            }
            catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  RemoveGhostXusbInterfaces
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Disable HIDMaestro HID devnodes that carry the xinputhid
    /// filter and are no longer present, and delete their XUSB and
    /// WinExInput registrations, which otherwise keep an XInput slot
    /// occupied. Before v1.13.0 this also disabled present devnodes that had
    /// a problem or had not started yet, which is what another program's
    /// controller looks like in the middle of its create. A present
    /// devnode of a dead program is removed outright by the reclaim.
    ///
    /// <para>Each devnode's presence check, disable and registry cleanup
    /// run under the PnP lock, so a create of this release cannot make it
    /// present in between: its SwDeviceCreate holds the same lock. A
    /// devnode already present is left without the lock. The lock comes
    /// from <paramref name="holdLock"/> when given (the phantom cleanup's
    /// budgeted wait), and a devnode whose lock did not come is left for the
    /// next run.</para></summary>
    internal static void RemoveGhostXusbInterfaces(Func<DeviceManager.PnpLockHold?>? holdLock = null)
    {
        try
        {
            using var xusbKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}");
            if (xusbKey == null) return;

            foreach (var subName in xusbKey.GetSubKeyNames())
            {
                if (subName.Contains("ROOT#", StringComparison.OrdinalIgnoreCase)) continue;
                if (!subName.Contains("HID#", StringComparison.OrdinalIgnoreCase)) continue;

                string devPath = subName;
                int guidStart = devPath.LastIndexOf("#{");
                if (guidStart < 0) continue;
                devPath = devPath.Substring(0, guidStart);
                devPath = devPath.Replace("##?#", "");
                devPath = devPath.Replace('#', '\\');

                // Issue #28 (v1.3.16): the "UpperFilters contains xinputhid"
                // + phantom/problem/not-started criterion still matches
                // disconnected real Xbox 360 controllers (they carry the
                // same UpperFilter for the WGI vibration tripwire). Require
                // HardwareID proof that this is OUR devnode before
                // disabling it and deleting its DeviceClasses entries.
                if (!DeviceManager.IsHidMaestroOwned(devPath)) continue;

                bool hasXinputHid = false;
                try
                {
                    using var devKey = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Enum\{devPath}");
                    if (devKey == null) continue;
                    var upperFilters = devKey.GetValue("UpperFilters") as string[];
                    hasXinputHid = upperFilters != null && upperFilters.Any(
                        f => f.Equals("xinputhid", StringComparison.OrdinalIgnoreCase));
                }
                catch { continue; }
                if (!hasXinputHid) continue;

                // Present: a running program's device, never touched here,
                // and left without taking the lock. What looks absent is
                // looked at again under it.
                if (CM_Locate_DevNodeW(out _, devPath, 0) == 0) continue;
                using var pnp = holdLock != null ? holdLock() : DeviceManager.HoldPnpLock();
                if (pnp == null) continue;
                if (CM_Locate_DevNodeW(out _, devPath, 0) == 0) continue;
                if (CM_Locate_DevNodeW(out uint devInst, devPath, CM_LOCATE_DEVNODE_PHANTOM) != 0) continue;

                CM_Disable_DevNode(devInst, 0);

                // Clean XUSB DeviceClasses entry
                try
                {
                    string regPath = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}";
                    using var classKey = Registry.LocalMachine.OpenSubKey(regPath, writable: true);
                    if (classKey != null)
                    {
                        try { classKey.DeleteSubKeyTree(subName); }
                        catch
                        {
                            try
                            {
                                string fullRegPath = $@"HKLM\{regPath}\{subName}";
                                RunProcess("reg.exe", $"delete \"{fullRegPath}\" /f", timeoutMs: 3000);
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                // Clean WinExInput entry
                try
                {
                    string winExPath = @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{6c53d5fd-6480-440f-b618-476750c5e1a6}";
                    using var winExKey = Registry.LocalMachine.OpenSubKey(winExPath, writable: true);
                    if (winExKey != null)
                    {
                        string devPathHash = devPath.Replace('\\', '#');
                        foreach (var weSub in winExKey.GetSubKeyNames())
                        {
                            if (weSub.Contains(devPathHash, StringComparison.OrdinalIgnoreCase))
                            {
                                try { winExKey.DeleteSubKeyTree(weSub); }
                                catch
                                {
                                    try
                                    {
                                        string fullRegPath = $@"HKLM\{winExPath}\{weSub}";
                                        RunProcess("reg.exe", $"delete \"{fullRegPath}\" /f", timeoutMs: 3000);
                                    }
                                    catch { }
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  EnsureGameInputService
    // ════════════════════════════════════════════════════════════════════

    // v1.3.0: per-process cache: GameInputSvc state is global to the OS,
    // not per-controller. Checking on every CreateController spawned sc.exe
    // 1–3 times (100–300 ms even when nothing changed). Once we've confirmed
    // the service is running in this process, subsequent calls are a no-op.
    private static volatile bool s_gameInputSvcRunning;

    private static void EnsureGameInputService()
    {
        if (s_gameInputSvcRunning) return;
        try
        {
            var (_, status) = RunProcess("sc.exe", "query GameInputSvc");
            if (!status.Contains("RUNNING"))
            {
                RunProcess("sc.exe", "config GameInputSvc start= auto");
                RunProcess("sc.exe", "start GameInputSvc");
            }
            s_gameInputSvcRunning = true;
        }
        catch { }
    }

    /// <summary>T11: pre-warm hook called from HMContext's background ctor
    /// task. Same logic as the gated <see cref="EnsureGameInputService"/>
    /// but exposed at internal scope so callers outside of SetupController
    /// can pre-prime the per-process cache. Safe to call concurrently with
    /// SetupController's own EnsureGameInputService invocation: the
    /// volatile-bool gate means only one path actually does the sc.exe work.</summary>
    internal static void PrewarmGameInputService() => EnsureGameInputService();

    // ════════════════════════════════════════════════════════════════════
    //  GameInput mapping
    // ════════════════════════════════════════════════════════════════════

    /// <summary>foundation-sunshine #1056 and #1138. A profile built in code
    /// with the descriptor shape the table was measured on holds the
    /// GameInput table while it lives (<see cref="GameInputMapping"/>,
    /// <see cref="RegistryLedger"/>). Every other profile gets none and
    /// reads through WGI as the real device does. A table an older build
    /// left under the identity goes first, or this virtual and any real pad
    /// of the same identity would read through it.</summary>
    private static void ApplyGameInputMapping(int controllerIndex, ControllerProfile profile)
    {
        if (GameInputMapping.Applies(profile))
            RegistryLedger.ClaimGameInput(profile.VendorId, profile.ProductId, controllerIndex);
        else
            RegistryLedger.RemoveLegacyTable(profile.VendorId, profile.ProductId);
    }

    // ════════════════════════════════════════════════════════════════════
    //  WriteInstanceConfig
    // ════════════════════════════════════════════════════════════════════

    /// <summary>The controller's configuration, written anew on every
    /// create: the key is deleted first, so no value another profile wrote
    /// on this index survives. Then the identity entry, which names the
    /// index for the controller's token. Both come before the deploy gate
    /// and any PnP work.</summary>
    private static void WriteInstanceConfig(int controllerIndex, ControllerProfile profile, DeviceIdentity identity)
    {
        byte[] descriptor = profile.GetDescriptorBytes()!;
        var (_, inputReportLen, _) = DriverInputReport(profile);

        // FunctionMode=1 for non-xinputhid Xbox (XUSB on main device),
        // FunctionMode=0 for everything else. Gated on IsXboxBranded so a
        // SideWinder / FF wheel / other Microsoft-VID non-Xbox device does
        // not get its main HID treated as an XInput function.
        bool funcMode = profile.RequiresXusbCompanion;

        string instancePath = RegPathForIndex(controllerIndex);
        Registry.LocalMachine.DeleteSubKeyTree(instancePath, throwOnMissingSubKey: false);
        using var key = Registry.LocalMachine.CreateSubKey(instancePath);
        // The identity first: another program's sweep reads VendorId and
        // ProductId from a configuration a create may still be writing
        // (OwnerCensus.GameInputKeysInUse).
        key.SetValue("VendorId", (int)profile.VendorId, RegistryValueKind.DWord);
        key.SetValue("ProductId", (int)profile.ProductId, RegistryValueKind.DWord);
        key.SetValue("FunctionMode", funcMode ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("ReportDescriptor", descriptor, RegistryValueKind.Binary);
        // v1.3.5: VersionNumber default 0x0100 (real Sony USB convention).
        // BT profiles that emulate a real Sony controller need 0 here so
        // Chromium's Dualshock4Controller::BusTypeFromVersionNumber routes
        // browser-driven vibration through SetVibrationBluetooth (Report
        // 0x11) instead of SetVibrationUsb (Report 0x05). Profiles can
        // override via the JSON `versionNumber` field.
        key.SetValue("VersionNumber", (int)(profile.VersionNumber ?? 0x0100),
                     RegistryValueKind.DWord);
        // v2.0.0: the answers to GET_FEATURE, compiled for this controller
        // (mechanism 4): the index and the serial the driver serves
        // patched in, CRCs final. A profile without answers writes the
        // header alone. The driver reads it only under the version
        // contract.
        key.SetValue("FeatureAnswers", FeatureAnswersBlob(profile, controllerIndex), RegistryValueKind.Binary);
        // v2.0.0: the input report IDs a polled GET_INPUT_REPORT answers
        // from a cache of their own. No profile names any yet, so the
        // value is empty, and the driver answers IDs 0 and 1 from the last
        // frame as before.
        key.SetValue("CachedInputReportIds", Array.Empty<byte>(), RegistryValueKind.Binary);
        // PadForge discussion 476: a DualShock 3 in the form sixaxis.sys and
        // DsHidMini's SXS mode present. The driver derives that form's
        // input, feature and output handling from the native report.
        key.SetValue("Ds3Sixaxis", profile.SixaxisCompatible == true ? 1 : 0, RegistryValueKind.DWord);
        if (profile.ProductString != null)
            key.SetValue("ProductString", profile.ProductString, RegistryValueKind.String);
        // Both written or cleared every time, so an index another profile
        // used never keeps its report. The driver keeps its default length
        // for 0, and without InputReportId reads the ID from the descriptor
        // as it always has.
        key.SetValue("InputReportByteLength", inputReportLen, RegistryValueKind.DWord);
        if (InputReportIdValue(profile) is { } reportIdValue)
            key.SetValue("InputReportId", reportIdValue, RegistryValueKind.Binary);
        else
            key.DeleteValue("InputReportId", throwOnMissingValue: false);

        string displayName = profile.DeviceDescription ?? profile.ProductString ?? "HIDMaestro Controller";
        key.SetValue("DeviceDescription", displayName, RegistryValueKind.String);

        // Joystick OEM display name (HKLM only). HKCU Joystick\OEM\<VID&PID>
        // takes precedence over HKLM for joy.cpl, and Windows ships HKCU
        // preloaded for many common clone VID:PIDs, so HKCU is written only
        // through HMOemNameOverride. The HKLM key is shared with every real
        // device of the same identity (foundation-sunshine #1138): the
        // ledger records it before the write and puts it back when the last
        // virtual holding it goes. OEMData and Axes belong to the device and
        // are no longer deleted. A keyboard or mouse is no joystick and gets
        // no joy.cpl entry (issue #67).
        if (!profile.IsKeyboardOrMouse)
            RegistryLedger.ClaimJoystickOem(profile.VendorId, profile.ProductId, displayName, controllerIndex);

        WriteIdentityEntry(identity.Token, controllerIndex);
    }

    /// <summary>The serial the UMDF2 driver serves for an index
    /// (driver.c InitInstancePaths): HM-CTL- and the index, at least four
    /// digits.</summary>
    internal static string DriverSerial(int controllerIndex) => $"HM-CTL-{controllerIndex:D4}";

    /// <summary>The FeatureAnswers value of a controller: the blob of the
    /// table its resolved model compiles to, or the empty blob.</summary>
    internal static byte[] FeatureAnswersBlob(ControllerProfile profile, int controllerIndex) =>
        Usbip.FeatureStubTable.From(profile, controllerIndex, DriverSerial(controllerIndex))?.ToBlob()
        ?? Usbip.FeatureStubTable.EmptyBlob();

    // ════════════════════════════════════════════════════════════════════
    //  CreateGamepadCompanion (xinputhid path)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>A software companion that was not created ends the create,
    /// as a main devnode that was not created does: a controller without it
    /// is one no game sees (an xinputhid controller has no other device, and
    /// an Xbox 360 controller gets no XInput). The failed create's cleanup
    /// removes what the create made. <paramref name="hr"/> is null when the
    /// registration gate refused.</summary>
    private static InvalidOperationException CompanionNotCreated(string what, string instanceId, ControllerProfile profile,
                                                                 int controllerIndex, int? hr) =>
        new($"The {what} {instanceId} of profile '{profile.Id}' at index {controllerIndex} was not created: " + hr switch
        {
            null => "a prior life of it or a device with its ParentIdPrefix was still there after its removal.",
            SwdDeviceFactory.StillInTreeHr => "the device a timed-out create attempt made was still in the device tree when its removal test ended.",
            _ => $"hmswd.exe returned 0x{hr.Value:X8}.",
        });

    private static string? CreateGamepadCompanion(int controllerIndex, ControllerProfile profile,
                                                  DeviceIdentity identity)
    {
        string gpVid = $"{profile.VendorId:X4}";
        string gpPid = $"{profile.ProductId:X4}";
        string hwPid = profile.ResolvedDriverPid != null
            ? $"{Convert.ToUInt16(profile.ResolvedDriverPid, 16):X4}" : gpPid;
        string gpEnumeratorLegacy = $"VID_{gpVid}&PID_{hwPid}&IG_00";                 // ROOT\… path
        // SWD enumerator: `HIDMAESTRO_VID_<vid>_PID_<pid>&IG_00`. The only
        // load-bearing detail vs the pre-experiment `HIDMAESTRO_VID_xxx&PID_yyy&IG_00`
        // form is the single `&` → `_` swap between VID and PID. That form
        // (still matching `VID_*&PID_*&IG_*`) triggered a Windows PnP edge
        // case where SWD devices registered but never fully enumerated
        // Status=Stopped, HID child Disconnected. Using an underscore between
        // VID and PID breaks the substring match without losing HIDMAESTRO
        // branding or the `&IG_` suffix (load-bearing for HIDAPI/SDL3
        // blocklist + xinputhid INF match).
        string gpEnumeratorSwd    = $"HIDMAESTRO_VID_{gpVid}_PID_{hwPid}&IG_00";      // SWD\… path

        // 1. Look for an existing companion claimed by THIS controllerIndex
        //    check new SWD path first, then legacy ROOT path.
        string? gpInstId = FindExistingCompanion(gpEnumeratorSwd, controllerIndex)
                        ?? FindExistingCompanion(gpEnumeratorLegacy, controllerIndex);

        if (gpInstId == null)
        {
            // 2. Create a new SWD-enumerated gamepad companion.
            //
            // Migrated from SetupDiCreateDeviceInfoW + DIF_REGISTERDEVICE (ROOT
            // enumerator) to SwDeviceCreate (SWD enumerator) on 2026-04-24 for
            // the slot-1-skip fix. xinputhid publishes the {EC87F1E3} XUSB
            // interface on this HID device's stack; xinput1_4's de2c queries
            // THIS device's ContainerId, and a real (non-sentinel) container
            // keeps bit 2 clear so the XInput slot allocator fills 0..3
            // contiguously for multi-controller setups.
            //
            // Hardware IDs unchanged from the previous SetupDi path. The first
            // entry (root\VID_xxxx&PID_yyyy&IG_00) is the xinputhid-INF match
            // anchor that makes xinputhid attach as an upper filter. The
            // &IG_00 suffix is load-bearing for SDL3 / HIDAPI blocklist
            // (makes those APIs route through XInput instead of trying to
            // read native HID).
            string[] hardwareIds = new[]
            {
                $"root\\VID_{gpVid}&PID_{hwPid}&IG_00",
                "root\\HIDMaestroGamepad",
                "root\\HIDMaestro",
            };
            var compatList = new List<string> { "root\\HIDMaestroGamepad", "root\\HIDMaestro" };
            if (profile.Connection == "bluetooth")
            {
                // Prepend the BTH-LE spoof alias so devices keying off a BT
                // hardware descriptor still match.
                compatList.Insert(0, $"BTHLEDEVICE\\{{00001812-0000-1000-8000-00805f9b34fb}}_Dev_VID&02{gpVid}_PID&{gpPid}");
            }

            // Enumerator: `HIDMAESTRO_VID_<vid>_PID_<pid>&IG_00`. Two pieces
            // meet at the HID child's instance path:
            //   1. xinputhid's INF matches on HID child HardwareIds, which
            //      carry `HID\VID_xxxx&PID_yyyy&IG_00` via our parent's
            //      `root\VID_xxx&PID_yyy&IG_00` hardware ID. Unaffected by
            //      the enumerator name choice.
            //   2. HIDAPI / SDL3 blocklist substring-matches `&IG_` in the
            //      HID child's instance path (`HID\<enumerator>\<instance>`)
            //      to decide "XInput-bound, skip for HIDAPI." The `&IG_00`
            //      suffix here satisfies that match.
            // The underscore between VID and PID is load-bearing. The form
            // `HIDMAESTRO_VID_xxx&PID_yyy&IG_00` (matching `VID_*&PID_*&IG_*`)
            // triggered a Windows PnP edge case where the SWD device registered
            // but never fully enumerated: pnputil reported not-found, the
            // companion stayed Stopped, HID child Disconnected. Swapping the
            // `&` between VID and PID for `_` breaks the substring match
            // without touching anything else.
            string gpSwdEnumerator = gpEnumeratorSwd;
            // The identity's token: the same instance id on every life
            // (issue #60; see the note above CreateGamepadCompanion's
            // caller on why a fixed tuple is safe now).
            string instanceSuffix = identity.Token;
            string companionDesc = profile.DeviceDescription ?? profile.ProductString ?? "HIDMaestro Gamepad";

            // Through the registration gate (DeviceManager.EnterRegistrationGate),
            // which returns holding the machine-wide PnP lock for
            // SwDeviceCreate. A prior life of this id still in the tree, one
            // FindExistingCompanion did not take, goes first, and must pass
            // its removal test: Learn's promise that a create after
            // SwDeviceClose "will be queued until the previous removal
            // processing completes" covers SwDeviceClose only, and the SDK
            // and programs before v1.13.0 remove these devnodes other ways
            // too. Its record stays, since SwDeviceCreate takes it over. This
            // parent has a HID child, so the one-parent-per-prefix rule
            // applies to it exactly as it does to a ROOT parent.
            string gpSwdId = $@"SWD\{gpSwdEnumerator}\{instanceSuffix}";
            SwdDeviceFactory.Result result;
            using (var pnp = DeviceManager.EnterRegistrationGate(gpSwdId, DeviceManager.RegistrationKind.Software,
                       () => DeviceManager.IsHidMaestroOwned(gpSwdId) ? DeviceManager.RemovePriorLife(gpSwdId, timeoutMs: 120_000) : null,
                       remove => DeviceManager.ClearParentIdPrefixRivals("SWD", gpSwdEnumerator, identity.Token, identity.ParentIdPrefix, remove),
                       out _))
            {
                if (pnp == null)
                    throw CompanionNotCreated("gamepad companion", gpSwdId, profile, controllerIndex, null);

                result = SwdDeviceFactory.Create(
                    instanceSuffix,
                    hardwareIds,
                    compatList.ToArray(),
                    identity.ContainerId,
                    companionDesc,
                    driverRequired: true,
                    enumeratorName: gpSwdEnumerator);
            }

            if (result.Success && result.InstanceId != null)
            {
                gpInstId = result.InstanceId;

                // Write ControllerIndex so any downstream consumer that reads
                // it (companion driver, future filters) can locate its shared
                // memory. Matches pre-migration behavior.
                try
                {
                    string dpPath = $@"SYSTEM\CurrentControlSet\Enum\{gpInstId}\Device Parameters";
                    using var dpKey = Registry.LocalMachine.CreateSubKey(dpPath);
                    dpKey?.SetValue("ControllerIndex", controllerIndex, RegistryValueKind.DWord);
                }
                catch { }
            }
            else if (!result.Success)
            {
                try
                {
                    using var k = Registry.LocalMachine.CreateSubKey(RegPathForIndex(controllerIndex));
                    k?.SetValue("LastSwdGamepadHResult", result.HResult, RegistryValueKind.DWord);
                }
                catch { }
                throw CompanionNotCreated("gamepad companion", gpSwdId, profile, controllerIndex, result.HResult);
            }
        }

        // 3. Restart so it picks up latest descriptor + VID/PID, then poll
        //    for the HID child to appear (replaces a fixed 2000ms sleep).
        if (gpInstId != null)
        {
            // Issue #60. SwDeviceCreate binds and starts the driver inside
            // the call, so the HID child has already enumerated with
            // whatever ParentIdPrefix the parent's key held: the retained
            // value from the last life, or a fresh counter when the record
            // was purged. Put the identity's value in now; the restart
            // that follows re-enumerates the child under it. Measured on
            // 26200: after a purge the child came back as 1&hash&N first,
            // and as the identity's 1&hash&0 after this write + restart,
            // three lives in a row. When the value already matches this
            // is a no-op.
            DeviceIdentity.ApplyParentIdPrefix(gpInstId, identity.ParentIdPrefix, createKey: false);
            DeviceManager.RestartDevice(gpInstId);

            // Poll for HID child PDO to be created by HIDClass below our parent.
            // On fast machines this takes <100ms; the old Thread.Sleep(2000) was
            // wasting 1900+ms on every creation. A failed start fails the
            // create in step 4.
            WaitForHidChild(gpInstId, controllerIndex, timeoutMs: 5000);
        }

        return gpInstId;
    }

    // ════════════════════════════════════════════════════════════════════
    //  CreateXusbCompanion (non-xinputhid Xbox path)
    // ════════════════════════════════════════════════════════════════════

    private static string? CreateXusbCompanion(int controllerIndex, ControllerProfile profile,
                                               DeviceIdentity identity)
    {
        // Look for a live HIDMAESTRO already claimed by THIS controllerIndex.
        // The "HIDMAESTRO" enumerator name is load-bearing for WGI's
        // RawGameController dedup: with any other name (HIDMAESTRO,
        // HIDMAESTRO, etc.), WGI counts the XUSB companion AND the
        // ROOT\VID_*&PID_* main device as two separate RawGameControllers
        // for the same Xbox 360 controller, even though they share a
        // ContainerID. HIDMAESTRO gets deduped; others don't. Empirical
        // I don't know which specific WGI code path treats "HIDMAESTRO" as
        // "merge with sibling," so changing it breaks multi-consumer counts.
        string? xusbInstId = FindExistingCompanion("HIDMAESTRO", controllerIndex);

        bool xusbExists = xusbInstId != null;
        if (!xusbExists)
        {
            // ── SWD-enumerated creation ──────────────────────────────────
            //
            // Migrated from SetupDiCreateDeviceInfoW + DIF_REGISTERDEVICE (ROOT
            // enumerator) to SwDeviceCreate (SWD enumerator) on 2026-04-24 for
            // the slot-1-skip fix. The critical change is the explicit
            // pContainerId: ROOT-enumerated devices get the null-sentinel
            // {00000000-0000-0000-FFFF-FFFFFFFFFFFF} container, which xinput1_4!
            // FUN_18000de2c treats as "embedded/primary device" (sets bit 2 on
            // its device struct), causing the fallback slot allocator to skip
            // internal slot 0 and the presentation swap to surface an empty
            // slot 1. With an explicit non-sentinel pContainerId, de2c returns
            // 0 via the HardwareIds branch (no XINPUT_EMBEDDED_DEVICE in our
            // HW IDs, verified), bit 2 stays clear, and multi-controller
            // setups fill slots 0..3 contiguously.
            //
            // See memory:project-slot-1-skip-swd-migration-plan.md for the
            // full disassembly + path trace.
            //
            // Hardware IDs exactly match the previous SetupDi path so the
            // existing hidmaestro_xusb.inf [Models] entries (root\VID_045E&
            // PID_028E&XI_00, root\HIDMaestroXUSB) still bind.
            string vidHw = $"root\\VID_{profile.VendorId:X4}&PID_{profile.ProductId:X4}&XI_00";
            string[] hardwareIds = new[] { vidHw, "root\\HIDMaestroXUSB" };
            string[] compatIds = new[]
            {
                "USB\\MS_COMP_XUSB10",
                "USB\\Class_FF&SubClass_5D&Prot_01",
                "USB\\Class_FF&SubClass_5D",
                "USB\\Class_FF",
            };
            string companionDesc = profile.DeviceDescription ?? profile.ProductString ?? "Controller";
            // The identity's token, fixed across lives (issue #60). The
            // companion has no HID child, so its XUSB interface path is
            // SWD#HIDMAESTRO#<token>#{EC87F1E3-...} on every life.
            string instanceSuffix = identity.Token;

            // Through the registration gate, as the gamepad companion in
            // CreateGamepadCompanion. It has no HID child, so no prefix rule.
            string xusbId = $@"SWD\HIDMAESTRO\{instanceSuffix}";
            SwdDeviceFactory.Result result;
            using (var pnp = DeviceManager.EnterRegistrationGate(xusbId, DeviceManager.RegistrationKind.Software,
                       () => DeviceManager.IsHidMaestroOwned(xusbId) ? DeviceManager.RemovePriorLife(xusbId, timeoutMs: 120_000) : null,
                       clearRivals: null, out _))
            {
                if (pnp == null)
                    throw CompanionNotCreated("XUSB companion", xusbId, profile, controllerIndex, null);

                result = SwdDeviceFactory.Create(
                    instanceSuffix,
                    hardwareIds,
                    compatIds,
                    identity.ContainerId,
                    companionDesc,
                    driverRequired: true,
                    enumeratorName: "HIDMAESTRO");
            }

            if (result.Success && result.InstanceId != null)
            {
                xusbInstId = result.InstanceId;

                // Write ControllerIndex to Device Parameters so the companion
                // driver (HMXInput.dll) reads the right shared section at
                // startup. Matches the pre-migration behavior.
                try
                {
                    string dpPath = $@"SYSTEM\CurrentControlSet\Enum\{xusbInstId}\Device Parameters";
                    using var dpKey = Registry.LocalMachine.CreateSubKey(dpPath);
                    dpKey?.SetValue("ControllerIndex", controllerIndex, RegistryValueKind.DWord);
                }
                catch { }

                // SwDeviceCreate with DriverRequired binds the INF synchronously
                // as part of the callback; the XUSB interface should publish by
                // the time the callback fires. Still wait a short time for the
                // interface to actually register (some PnP coinstallers are
                // asynchronous even after driver load).
                // Floor-bumped 2 → 5 s base in v1.3.0: 2 s was tight even on
                // fast hw; some XUSB coinstallers can take 1–4 s to register
                // the interface even after the SwDeviceCreate callback fires.
                // Scaling is layered on top of the 5 s base by DeviceManager.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (DeviceManager.WaitForDeviceInterface(xusbInstId, XusbInterfaceGuid, timeoutMs: 5000))
                        break;
                    DeviceManager.RestartDevice(xusbInstId);
                }
            }
            else if (!result.Success)
            {
                // SwDeviceCreate failed: record HRESULT in a trace breadcrumb
                // so post-mortem diagnostics know why the companion didn't
                // materialize, then refuse the create.
                try
                {
                    using var k = Registry.LocalMachine.CreateSubKey(RegPathForIndex(controllerIndex));
                    k?.SetValue("LastSwdCompanionHResult", result.HResult, RegistryValueKind.DWord);
                }
                catch { }
                throw CompanionNotCreated("XUSB companion", xusbId, profile, controllerIndex, result.HResult);
            }
        }
        else
        {
            // Existing device: kick it to pick up any changed descriptor.
            DeviceManager.RestartDevice(xusbInstId!);
        }

        // Set companion name to match the controller profile
        if (xusbInstId != null)
        {
            string companionName = profile.DeviceDescription ?? profile.ProductString ?? "Controller";
            DeviceProperties.SetBusReportedDeviceDesc(xusbInstId, companionName);
            DeviceProperties.SetDeviceFriendlyName(xusbInstId, companionName);
        }

        return xusbInstId;
    }

    /// <summary>Remove the XUSB companion of <paramref name="token"/> when
    /// it is present on another transport index. Its driver reads the index
    /// when it starts, and a restart cannot reach it while a process that
    /// called XInput holds it open, as this one does after its first Xbox
    /// create: Windows does not stop a device with an open handle. A removal
    /// takes it down, and the create makes it again on the new index.</summary>
    private static void RemoveCompanionOnOtherIndex(string token, int controllerIndex)
    {
        string id = $@"SWD\HIDMAESTRO\{token}";
        if (CM_Locate_DevNodeW(out _, id, 0) != 0) return;
        int prior = OwnerCensus.ReadControllerIndex(id);
        if (prior < 0 || prior == controllerIndex) return;
        LogDiag($"    companion {id} is on index {prior}; removing it before this create on {controllerIndex}");
        // A helper that cannot be staged throws here, before any wait, and
        // the create fails with its reason.
        DeviceManager.RemoveDevice(id, timeoutMs: 30_000, forceFallbacks: true);
    }

    /// <summary>Scan both <c>SWD\{enumerator}</c> (new) and <c>ROOT\{enumerator}</c>
    /// (legacy) for a live device whose Device Parameters\ControllerIndex
    /// matches the requested index. Returns the instance ID or null.</summary>
    private static string? FindExistingCompanion(string enumerator, int controllerIndex)
    {
        string[] roots = { "SWD", "ROOT" };
        foreach (var root in roots)
        {
            try
            {
                using var ek = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{root}\{enumerator}");
                if (ek == null) continue;
                foreach (var inst in ek.GetSubKeyNames())
                {
                    string candidate = $@"{root}\{enumerator}\{inst}";
                    if (CM_Locate_DevNodeW(out uint devInst, candidate, 0) != 0) continue;
                    // Issue #38: adopt only a HEALTHY devnode. A candidate
                    // that is remove-pending, problem-flagged, or not
                    // started (a predecessor's teardown still in flight,
                    // or a half-revived reuse ghost) must not be handed
                    // back as a live companion: the caller would wire a
                    // controller to a device that is about to vanish or
                    // never re-ran device start. Skip it; the caller
                    // creates fresh (per-call-unique SwD suffix, no
                    // collision) and the ghost stays the sweep's job.
                    const uint DN_STARTED = 0x00000008;
                    const uint DN_WILL_BE_REMOVED = 0x00040000;
                    if (CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) != 0
                        || (status & DN_STARTED) == 0
                        || (status & DN_WILL_BE_REMOVED) != 0
                        || problem != 0)
                    {
                        LogDiag($"    FindExistingCompanion: skipping unhealthy {candidate} (status=0x{status:X8} problem={problem})");
                        continue;
                    }
                    using var dp = ek.OpenSubKey($@"{inst}\Device Parameters");
                    if (dp?.GetValue("ControllerIndex") is int ci && ci == controllerIndex)
                        return candidate;
                }
            }
            catch { }
        }
        return null;
    }

    // ════════════════════════════════════════════════════════════════════
    //  SetHidParentUpperFilterXinputhid
    //  Profile-specific WGI HID-classifier skip. Called only for profiles
    //  that already publish an HIDMAESTRO XUSB provider: so WGI still has
    //  an XUSB Gamepad source after the HID Gamepad is skipped.
    // ════════════════════════════════════════════════════════════════════

    private static void SetHidParentUpperFilterXinputhid(int controllerIndex)
    {
        // Idempotent: sweep every ROOT HID parent whose HIDMAESTRO counterpart
        // exists (same ControllerIndex registered under ROOT\HIDMAESTRO\*).
        // Writing to just the current controller's instance has a timing race
        //: Windows PnP occasionally clears properties when a sibling root
        // device is added shortly after. Sweeping every call converges the
        // state regardless of which iteration set which value.
        try
        {
            // Build a set of controllerIndexes that have an HIDMAESTRO
            // (profiles with XUSB companions: xbox-360 wired family).
            // Sweep BOTH SWD (post-slot-1-skip-fix) and ROOT (legacy)
            // enumerators so a mixed install sees everything.
            var companionIndexes = new HashSet<int>();
            foreach (var enumRoot in new[] { "SWD", "ROOT" })
            {
                using var hmEnum = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumRoot}\HIDMAESTRO");
                if (hmEnum == null) continue;
                foreach (var inst in hmEnum.GetSubKeyNames())
                {
                    using var dp = hmEnum.OpenSubKey($@"{inst}\Device Parameters");
                    if (dp?.GetValue("ControllerIndex") is int ci) companionIndexes.Add(ci);
                }
            }
            companionIndexes.Add(controllerIndex);  // include current even if HIDMAESTRO isn't enum-visible yet

            // Sweep BOTH ROOT (legacy `VID_*&IG_00`) and SWD (any HIDMAESTRO*
            // gamepad-parent enumerator, currently HIDMAESTRO_VID_*_PID_*&IG_00).
            foreach (var enumRoot in new[] { "ROOT", "SWD" })
            {
                using var rootEnum = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumRoot}");
                if (rootEnum == null) continue;
                foreach (var sub in rootEnum.GetSubKeyNames())
                {
                    bool isVidForm = sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase);
                    bool isSwdForm = sub.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);
                    if (!isVidForm && !isSwdForm) continue;
                    using var subKey = rootEnum.OpenSubKey(sub);
                    if (subKey == null) continue;
                    foreach (var inst in subKey.GetSubKeyNames())
                    {
                        using var dpKey = Registry.LocalMachine.OpenSubKey(
                            $@"SYSTEM\CurrentControlSet\Enum\{enumRoot}\{sub}\{inst}\Device Parameters");
                        int ci = (dpKey?.GetValue("ControllerIndex") is int v) ? v : -1;
                        if (!companionIndexes.Contains(ci)) continue;
                        string instKeyPath = $@"SYSTEM\CurrentControlSet\Enum\{enumRoot}\{sub}\{inst}";
                        using var instKey = Registry.LocalMachine.OpenSubKey(instKeyPath, writable: true);
                        if (instKey == null) continue;
                        var existing = instKey.GetValue("UpperFilters") as string[];
                        if (existing != null && Array.Exists(existing, s => string.Equals(s, "xinputhid", StringComparison.OrdinalIgnoreCase)))
                            continue;
                        instKey.SetValue("UpperFilters", new[] { "xinputhid" }, RegistryValueKind.MultiString);
                    }
                }
            }
        }
        catch { /* best-effort: absent tripwire means 2 Gamepads, not a hard failure */ }
    }

    // ════════════════════════════════════════════════════════════════════
    //  SetBusTypeGuidUsb
    // ════════════════════════════════════════════════════════════════════

    private static void SetBusTypeGuidUsb()
    {
        var busTypeKey = new DEVPROPKEY
        {
            fmtid = new Guid(0xa45c254e, 0xdf1c, 0x4efd, 0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0),
            pid = 21 // DEVPKEY_Device_BusTypeGuid
        };
        byte[] usbBusGuid = new Guid("9d7debbc-c85d-11d1-9eb4-006008c3a19a").ToByteArray();

        // Every present HIDMaestro devnode on the machine gets the write,
        // another program's included, with its first child, a HIDClass
        // devnode. Under the PnP lock, so no locate and write lands on a
        // devnode another program of this release is removing.
        using var pnp = DeviceManager.HoldPnpLock();

        // T11: trimmed list. The dynamic VID_*/HIDMAESTRO* scan below
        // (lines 1156+) covers VID_045E&PID_02FF&IG_00, VID_045E&PID_0B13&IG_00,
        // and HIDMAESTRO automatically: they all match the dynamic prefixes.
        // Only the non-VID/non-HIDMAESTRO enumerators (HID_IG_00, HIDClass,
        // XnaComposite) need explicit hardcoded coverage. Cuts 60 redundant
        // CM_Locate calls per setup (3 enumerators × 2 roots × 10 indices).
        // Issue #28 (v1.3.16): the shared enumerators (HID_IG_00, HIDClass,
        // XnaComposite) are not exclusively HIDMaestro: vJoy lives at
        // ROOT\HIDClass. Gate the property write on IsHidMaestroOwned to
        // avoid stamping our BusType GUID onto a coexisting third-party
        // device.
        foreach (string enumer in new[] { "HID_IG_00", "HIDClass", "XnaComposite" })
        {
            // Sweep both ROOT and SWD roots for this enumerator. Every
            // instance name, since ours are identity tokens (issue #60).
            foreach (var enumRoot in new[] { "ROOT", "SWD" })
            {
                string[] instNames;
                try
                {
                    using var ek = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumRoot}\{enumer}");
                    instNames = ek?.GetSubKeyNames() ?? Array.Empty<string>();
                }
                catch { instNames = Array.Empty<string>(); }
                foreach (var instName in instNames)
                {
                    string devId = $@"{enumRoot}\{enumer}\{instName}";
                    if (CM_Locate_DevNodeW(out uint devInst, devId, 0) != 0) continue;
                    if (!DeviceManager.IsHidMaestroOwned(devId)) continue;
                    CM_Set_DevNode_PropertyW(devInst, ref busTypeKey, 0x0D,
                        usbBusGuid, (uint)usbBusGuid.Length, 0);
                    if (CM_Get_Child(out uint childInst, devInst, 0) == 0)
                    {
                        CM_Set_DevNode_PropertyW(childInst, ref busTypeKey, 0x0D,
                            usbBusGuid, (uint)usbBusGuid.Length, 0);
                    }
                }
            }
        }

        // Also scan VID_* enumerators dynamically (under both ROOT and SWD).
        // VID_* is third-party-shared too (any root-enumerated USB-style
        // device); HIDMAESTRO* is exclusive.
        foreach (var enumRootPath in new[] { @"SYSTEM\CurrentControlSet\Enum\ROOT", @"SYSTEM\CurrentControlSet\Enum\SWD" })
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey(enumRootPath);
            if (enumKey != null)
            {
                string enumRootPrefix = enumRootPath.EndsWith(@"\SWD") ? "SWD" : "ROOT";
                foreach (var sub in enumKey.GetSubKeyNames())
                {
                    bool isVidForm = sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase);
                    bool isExclusiveOurs = sub.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase)
                                        || sub.Equals("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);
                    if (!isVidForm && !isExclusiveOurs) continue;
                    using var vidKey = enumKey.OpenSubKey(sub);
                    if (vidKey == null) continue;
                    foreach (var inst in vidKey.GetSubKeyNames())
                    {
                        string devId = $@"{enumRootPrefix}\{sub}\{inst}";
                        if (CM_Locate_DevNodeW(out uint devInst, devId, 0) != 0) continue;
                        if (!isExclusiveOurs && !DeviceManager.IsHidMaestroOwned(devId)) continue;
                        CM_Set_DevNode_PropertyW(devInst, ref busTypeKey, 0x0D,
                            usbBusGuid, (uint)usbBusGuid.Length, 0);
                        if (CM_Get_Child(out uint childInst, devInst, 0) == 0)
                        {
                            CM_Set_DevNode_PropertyW(childInst, ref busTypeKey, 0x0D,
                                usbBusGuid, (uint)usbBusGuid.Length, 0);
                        }
                    }
                }
            }
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  SetupController: full orchestration
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Called right before step 0 maps the index's input
    /// section. Coexistence probe section U sets it through reflection to
    /// hold a create there while a v1.12.0 program starts on the same
    /// index: that program takes no claim, and the window between the
    /// lease and the mapping is too short to hit any other way. Null
    /// outside the probe.</summary>
    internal static Action<int>? BeforeInputMapping;

    public static string? SetupController(
        int controllerIndex, ControllerProfile profile, DeviceIdentity? identity = null)
    {
        // The read side of the gate the background phantom cleanup takes for
        // writing: creates run together, the cleanup never during one.
        OwnerSweep.CreateGate.EnterReadLock();
        try { return SetupControllerCore(controllerIndex, profile, identity); }
        finally { OwnerSweep.CreateGate.ExitReadLock(); }
    }

    private static string? SetupControllerCore(
        int controllerIndex, ControllerProfile profile, DeviceIdentity? identity)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (controllerIndex < 0) throw new ArgumentOutOfRangeException(nameof(controllerIndex));
        if (!profile.HasDescriptor)
            throw new ArgumentException(
                $"Profile '{profile.Id}' has no HID descriptor.", nameof(profile));
        identity ??= DeviceIdentity.ForIndex(controllerIndex);

        // Defense-in-depth gate: block until any in-flight TeardownController
        // for this controllerIndex has fully completed (including
        // WaitForDeviceRemoval's CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED
        // event). PadForge's contract: "Dispose returns when teardown is
        // complete": is enforced primarily by RemoveDevice's
        // WaitForDeviceRemoval inside TeardownController, but if that wait
        // ever returns early (timeout, unexpected exception, future
        // regression), this gate prevents a new SetupController for the
        // same index from racing the prior teardown's kernel cascade and
        // creating a duplicate. Initial gate state is signaled, so the
        // first SetupController for an index never blocks. After a
        // successful TeardownController, the gate is Set in its finally
        // block. No-op when no teardown was in flight.
        WaitForPriorTeardown(controllerIndex);

        var setupTotalSw = Stopwatch.StartNew();
        LogDiag($">>> SETUP  ENTER ctrl={controllerIndex} profile={profile.Id} vid=0x{profile.VendorId:X4} pid=0x{profile.ProductId:X4} uses_upper_filter={profile.UsesUpperFilter} companion_only={profile.CompanionOnly} identity={identity}");
        string? returnedId = null;
        bool configWritten = false;
        try {
        using var _total = new TimingScope(controllerIndex, profile.Id, "TOTAL");

        // A companion an earlier life of this identity left on another
        // index goes before the slot count, so the count does not include it.
        if (profile.RequiresXusbCompanion)
            RemoveCompanionOnOtherIndex(identity.Token, controllerIndex);

        // Snapshot XInput slot count BEFORE any setup so the post-setup wait
        // can detect the new claim. Sony / generic profiles get -1 (skip).
        // Perf audit 2026-07-21: gate on the XInput-participating shapes,
        // not raw Microsoft VID. SideWinder (045E, no companion, no upper
        // filter) never claims a slot and paid the full 500 ms wait on
        // every create.
        int slotsBefore = (profile.UsesUpperFilter || profile.RequiresXusbCompanion)
            ? CountConnectedXInputSlots() : -1;
        bool xinputFull = slotsBefore >= 4;

        // ── Step 0: pre-flight environment ───────────────────────────────
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "0.shm"))
        {
            BeforeInputMapping?.Invoke(controllerIndex);
            SharedMemoryIO.EnsureInputMapping(controllerIndex);
            SharedMemoryIO.WriteIdentityTag(controllerIndex, identity.Tag);
            try
            {
                SharedMemoryIO.EnsureOutputMapping(controllerIndex);
                SharedMemoryIO.MarkOutputStart(controllerIndex);
            }
            catch { }
        }

        // Once-per-session cleanup, for a program that creates without
        // calling InstallDriver first: the devices and ledger holds of
        // programs that have exited. HMContext runs it before the lease, so
        // here it finds it done. A running program's devices, in this
        // process or another, are never touched, and the phantom-record
        // cleanup runs behind OwnerSweep.CreateGate on a thread of its own.
        // This thread holds the gate's read side, so a cleanup started here
        // while the process exits would run none. HMContext's own call,
        // before the gate, has already started it.
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "0.ghost_cleanup"))
            OwnerSweep.SessionSweepOnce();

        using (var _ts = new TimingScope(controllerIndex, profile.Id, "0.gameinput_svc"))
        {
            EnsureGameInputService();
            ApplyGameInputMapping(controllerIndex, profile);
        }

        // ── Step 1: per-instance registry config ────────────────────────
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "1.instance_config"))
        {
            configWritten = true;
            WriteInstanceConfig(controllerIndex, profile, identity);
        }

        // ── Step 2: ensure driver is in the store ────────────────────────
        // The deploy gate: under the machine-wide deploy lock, after this
        // controller's record entry exists, so a deploy or package removal
        // of another program either finished before this create's PnP work
        // or saw the entry and left the driver alone (DriverDeploy).
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "2.driver_install_check"))
        {
            var driverSw = Stopwatch.StartNew();
            bool deployed = DriverDeploy.EnsureForCreate(identity.Token, profile.RunsOnOlderDriver);
            LogDiag($"    driver install check (deployed={deployed}) in {driverSw.ElapsedMilliseconds}ms");
        }

        // ── Step 3: create device(s) ────────────────────────────────────
        string? mainInstanceId = null;
        string? companionId = null;

        if (profile.UsesUpperFilter)
        {
            // xinputhid path: companion-only (no main HID device)
            var createSw = Stopwatch.StartNew();
            using var _ts = new TimingScope(controllerIndex, profile.Id, "3.create_gamepad_companion");
            companionId = CreateGamepadCompanion(controllerIndex, profile, identity);
            LogDiag($"    CreateGamepadCompanion -> {(companionId ?? "(null)")} in {createSw.ElapsedMilliseconds}ms");
        }
        else if (!profile.CompanionOnly)
        {
            // Plain HID or non-xinputhid Xbox: create main device node
            var createSw = Stopwatch.StartNew();
            using var _ts = new TimingScope(controllerIndex, profile.Id, "3.create_main_devnode");
            var result = DeviceNodeCreator.CreateDeviceNode(profile, controllerIndex, identity);
            if (!result.Success || result.InstanceId == null)
            {
                LogDiag($"    DeviceNodeCreator.CreateDeviceNode FAILED after {createSw.ElapsedMilliseconds}ms");
                throw new InvalidOperationException(
                    $"DeviceNodeCreator.CreateDeviceNode failed for profile " +
                    $"'{profile.Id}' at index {controllerIndex}.");
            }
            mainInstanceId = result.InstanceId;
            LogDiag($"    CreateDeviceNode -> {mainInstanceId} in {createSw.ElapsedMilliseconds}ms");
        }

        // ── Step 4: wait for HID child + name finalization ───────────────
        bool childSeen = false;
        //    Old: Thread.Sleep(3000): fixed worst-case wait for PnP async install.
        //    New: poll for the HID child PDO to appear, then finalize names.
        //    On a warm-start machine this exits in <500ms instead of 3000ms.
        {
            using var _ts = new TimingScope(controllerIndex, profile.Id, "4.wait_hid_child");
            string? parentId = mainInstanceId ?? companionId;
            if (parentId != null)
            {
                var hidSw = Stopwatch.StartNew();
                var gotChild = WaitForHidChild(parentId, controllerIndex, timeoutMs: 10000);
                LogDiag($"    WaitForHidChild({parentId}) -> {gotChild} in {hidSw.ElapsedMilliseconds}ms");
                if (gotChild == StartWait.Failed) throw StartError(parentId, controllerIndex);
                childSeen = gotChild == StartWait.Done;
            }
        }
        string displayName = profile.DeviceDescription ?? profile.ProductString ?? "Controller";

        // Apply names directly to the parent we just created. We already have
        // the instance ID from CreateDeviceNode/CreateGamepadCompanion, so the
        // pre-v1.3.0-T9 enumerator walk that scanned SWD\ + ROOT\ subtrees
        // matching Device Parameters\ControllerIndex is pure overhead: it
        // was finding the same devnode every time. SetAllNamingProperties is
        // a strict superset of FixHidChildNames (BusReportedDeviceDesc +
        // FriendlyName + DeviceDesc on root + first HID child), so dropping
        // the prior FixHidChildNames pre-call here is also a clean win.
        {
            using var _ts = new TimingScope(controllerIndex, profile.Id, "4.set_names_root");
            string? parentForNaming = mainInstanceId ?? companionId;
            if (parentForNaming != null)
                try { DeviceProperties.SetAllNamingProperties(parentForNaming, displayName); }
                catch { }
        }

        // Final name fix: poll for the device to be fully started (DN_STARTED)
        // rather than a fixed 2000ms sleep. For xinputhid profiles this waits
        // for xinputhid to fully bind; for others it's typically instant.
        {
            using var _ts = new TimingScope(controllerIndex, profile.Id, "4.wait_started");
            string? parentId = mainInstanceId ?? companionId;
            if (parentId != null)
            {
                string? hidChildId = DeviceManager.GetHidChildId(parentId);
                if (hidChildId != null)
                {
                    if (WaitForDeviceStarted(hidChildId, parentId, controllerIndex, timeoutMs: 5000) == StartWait.Failed)
                        throw StartError(parentId, controllerIndex);

                    // Write ControllerIndex directly to the HID child's HW key
                    // so any future HID-child filter can read it without having
                    // to walk up to the parent devnode via DEVPKEY_Device_Parent.
                    // Robustness belt-and-suspenders: the parent walk works
                    // but this shortcut eliminates a failure mode if
                    // WdfDeviceQueryPropertyEx returns stale or missing parent
                    // data mid-enumeration.
                    try
                    {
                        string dpPath = $@"SYSTEM\CurrentControlSet\Enum\{hidChildId}\Device Parameters";
                        using var dpKey = Registry.LocalMachine.CreateSubKey(dpPath);
                        dpKey?.SetValue("ControllerIndex", controllerIndex, RegistryValueKind.DWord);
                    }
                    catch { /* non-fatal */ }
                }
            }
        }
        // v2.0.0: the driver that started the device serves this SDK's
        // protocol (decision (c)). It wrote its half at add, before HID
        // class made the child. A failed check throws, and the create's
        // cleanup removes the device.
        {
            string? parentId = mainInstanceId ?? companionId;
            if (parentId != null && childSeen)
                CheckDriverHalf(controllerIndex, parentId,
                    acceptNoSection: DriverDeploy.Coexistence == DriverDeploy.CoexistenceRule.AllowModelFreeProfiles && profile.RunsOnOlderDriver);
        }

        // Post-WaitForDeviceStarted name re-application: by this point the
        // HID class driver has bound and may have stomped on FriendlyName /
        // DeviceDesc with INF defaults; re-applying makes our names stick.
        // We have parentId already, so call directly instead of walking
        // SWD\ + ROOT\ enumerator subtrees by ControllerIndex.
        {
            using var _ts = new TimingScope(controllerIndex, profile.Id, "4.fix_hid_child_names_2");
            string? parentForRename = mainInstanceId ?? companionId;
            if (parentForRename != null)
                try { DeviceProperties.SetAllNamingProperties(parentForRename, displayName); }
                catch { }
        }

        // ── Step 5: bus type + companions ─────────────────────────────────
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "5.set_bustype_usb"))
            SetBusTypeGuidUsb();

        // Non-xinputhid Xbox: create XUSB companion for XInput.
        // HIDMAESTRO setup class changed from XnaComposite to System in
        // hidmaestro_xusb.inf so Windows.Gaming.Input.dll doesn't classify it
        // as a WGI Gamepad (would duplicate the main virtual's HID-path
        // Gamepad and hang WGI). xinput1_4 discovery still finds it via the
        // {EC87F1E3} XUSB device interface class.
        string? xusbCompanionId = null;
        if (profile.RequiresXusbCompanion)
        {
            var xusbSw = Stopwatch.StartNew();
            using (var _ts = new TimingScope(controllerIndex, profile.Id, "5.create_xusb_companion"))
                xusbCompanionId = CreateXusbCompanion(controllerIndex, profile, identity);
            LogDiag($"    CreateXusbCompanion -> {(xusbCompanionId ?? "(null)")} in {xusbSw.ElapsedMilliseconds}ms");
            if (profile.CompanionOnly && companionId == null)
                companionId = xusbCompanionId;

            // Profiles with an HIDMAESTRO XUSB companion need the HID parent
            // gated out of WGI's HidClient classifier via xinputhid tripwire
            // (see memory:project-xinputhid-upperfilter-tripwire.md). Without
            // this, WGI creates TWO Gamepads for the same virtual: one HID-
            // backed with input but no vibration channel, one XUSB-backed with
            // vibration but no input. Setting UpperFilters="xinputhid" on the
            // HID parent makes ProviderManagerWorker::OnPnpDeviceAdded skip
            // HidClient::CreateProvider, leaving the XUSB-backed Gamepad as
            // the single WGI entity.
            //
            // Must be profile-specific (per-device, not in the INF): other
            // profiles (DualSense, Xbox Series BT, Switch Pro, etc.) have no
            // HIDMAESTRO; blocking their HID Gamepad would produce zero WGI
            // entities for them.
            using (var _ts2 = new TimingScope(controllerIndex, profile.Id, "5.hidparent_upperfilter_xinputhid"))
                SetHidParentUpperFilterXinputhid(controllerIndex);

            // Issue #59. The tripwire only gates WGI on a RE-arrival. At the
            // HID child's first arrival the classifier admits it no matter
            // when the value was written - measured on 26200 with the value
            // stamped after registration, and again with it in the SetupDi
            // property state before the devnode ever existed: 2 Gamepads in
            // 3 of 3 fresh creates both ways, while a disable/enable of the
            // HID child collapsed to 1 every time, matching the reporter's
            // clear/restore/restart toggle. So the main devnode gets one
            // deliberate restart here, after the companion exists and with
            // the tripwire guaranteed present from birth by the
            // SPDRP_UPPERFILTERS write in DeviceNodeCreator. WGI re-evaluates
            // the child on the way back up, routes it away from
            // HidClient::CreateProvider, and the companion's XUSB Gamepad is
            // the one WGI entity. The re-waits below keep the restart
            // invisible to the consumer: the controller is not handed over
            // until the HID child is back and started.
            if (mainInstanceId != null)
            {
                using var _ts3 = new TimingScope(controllerIndex, profile.Id, "5.restart_main_for_wgi");
                var rsSw = Stopwatch.StartNew();
                bool restarted = DeviceManager.RestartDevice(mainInstanceId);
                bool childBack = false;
                if (restarted)
                {
                    var back = WaitForHidChild(mainInstanceId, controllerIndex, timeoutMs: 10000);
                    if (back == StartWait.Failed) throw StartError(mainInstanceId, controllerIndex);
                    childBack = back == StartWait.Done;
                    if (childBack)
                    {
                        string? hidChildId2 = DeviceManager.GetHidChildId(mainInstanceId);
                        if (hidChildId2 != null
                            && WaitForDeviceStarted(hidChildId2, mainInstanceId, controllerIndex, timeoutMs: 5000) == StartWait.Failed)
                            throw StartError(mainInstanceId, controllerIndex);
                    }
                }
                LogDiag($"    restart_main_for_wgi restarted={restarted} childBack={childBack} in {rsSw.ElapsedMilliseconds}ms");
            }
        }

        // ── Step 6: final friendly name ──────────────────────────────────
        // T10: direct application on the specific instance IDs we created
        // instead of walking SWD\ + ROOT\ + HIDCLASS by ControllerIndex.
        // We have parentId (mainInstanceId or companionId) and (for
        // non-xinputhid Xbox profiles) xusbCompanionId. The original walk
        // applied to root + every child + every sibling; SetAllNamingProperties
        // applies to root + first HID child which covers the same naming
        // surface for our devices (single HID child, no second-level
        // grandchildren). Keep ApplyFriendlyNameForController as a fallback
        // commented out: easy revert if a future PnP edge surfaces a
        // device we missed here.
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "6.apply_friendly_name"))
        {
            string? primaryId = mainInstanceId ?? companionId;
            if (primaryId != null)
                try { DeviceProperties.SetAllNamingProperties(primaryId, displayName); }
                catch { }
            if (xusbCompanionId != null && xusbCompanionId != primaryId)
                try { DeviceProperties.SetAllNamingProperties(xusbCompanionId, displayName); }
                catch { }
        }

        // ── Step 7: wait for XInput slot claim ───────────────────────────
        // Without this, xinputhid (slow) and our XUSB companion (fast) race
        // for slot 0 and the slot order does NOT match the creation order.
        // Only Xbox-VID profiles touch XInput, so non-Xbox profiles skip the
        // wait entirely. When XInput is already full (4/4), we skip
        // gracefully: the controller is still visible via DI/HIDAPI/Browser.
        // Timeout is non-fatal: log only, never throw, to match the proven
        // pre-SDK test app behavior.
        if (slotsBefore >= 0 && !xinputFull)
        {
            using var _ts = new TimingScope(controllerIndex, profile.Id, "7.wait_xinput_slot_claim");
            var sw = Stopwatch.StartNew();
            int slotsAfter = slotsBefore;
            // v1.3.0: short-circuit when XInput is already at the 4-slot
            // cap before we even started: there's no slot 5 to wait for.
            // Without this, every 5th-onward Xbox-family create burns the
            // full 15 s budget waiting for the impossible. The controller
            // is still functional via DI/HIDAPI/Browser/WGI; it just
            // doesn't get an XInput slot. Same outcome, 15 s faster.
            if (slotsBefore >= 4)
            {
                LogDiag($"    XInput slot wait: skipped (slotsBefore={slotsBefore} already at cap)");
            }
            else
            {
                // v1.3.2: budget 500 ms (was 15 s). The slot-claim
                // distribution is bimodal: in the working case xinputhid
                // publishes the new slot in <100 ms (typical observation:
                // ~65 ms for Xbox 360 wired, ~3-10 ms for Xbox Series BT;
                // atom Z8350 worst-case healthy was ~90 ms). In the stuck-
                // allocator case (kernel state issue, prior-session residue,
                // xinputhid recovering from a prior unbind) the slot never
                // comes, and the prior 15 s budget waited the full duration
                // only to log the failure. PadForge users observed 13-14 s
                // freeze on a single Xbox Series BT create when this hit.
                //
                // 500 ms is ~5x the slowest observed healthy-case slot
                // claim (with TimeoutScale.Apply, 1 s on atom: ~10x
                // healthy). User-validated: PadForge swaps and creates
                // are visibly instantaneous at this budget. The
                // regression battery's slot-wait timeout cases now cost
                // 500 ms each instead of 15 s: the user-perceived UX
                // win is unmistakable even though the battery's wall-
                // time delta is dominated by run-to-run noise.
                //
                // The failure mode is non-fatal: controller stays
                // functional via DI/HIDAPI/Browser/WGI when XInput
                // doesn't pick it up; XInput consumers see the slot
                // appear lazily on their next poll cycle.
                //
                // 10 ms poll cadence; CountConnectedXInputSlots is 4
                // XInputGetState P/Invokes (~few hundred µs total).
                int slotWaitBudget = TimeoutScale.Apply(500);
                while (sw.ElapsedMilliseconds < slotWaitBudget)
                {
                    slotsAfter = CountConnectedXInputSlots();
                    if (slotsAfter > slotsBefore) break;
                    Thread.Sleep(10);
                }
                LogDiag($"    XInput slot wait: before={slotsBefore} after={slotsAfter} in {sw.ElapsedMilliseconds}ms (budget={slotWaitBudget}ms)");
                if (slotsAfter == slotsBefore)
                    LogDiag($"    XInput slot wait TIMEOUT: xinputhid did not publish a slot. Controller still functional via DI/HIDAPI/Browser/WGI; XInput consumers may see it appear lazily on next poll.");
            }
        }

        // Return main instance ID, or companion ID for companion-only profiles
        returnedId = mainInstanceId ?? companionId;
        return returnedId;
        }
        catch (HMResourceInUseException)
        {
            // A refusal: a running program started on this index during
            // the create (SharedMemoryIO.EnsureInputMapping), or holds the
            // ParentIdPrefix this identity derives
            // (DeviceManager.ClearParentIdPrefixRivals). Both come before
            // this create made a device. Only what this call wrote goes.
            // TeardownController would remove every device and the
            // configuration on the index, and those can be the other
            // program's.
            LogDiag($"    SETUP  REFUSED ctrl={controllerIndex}: releasing only this create's state");
            try { SharedMemoryIO.DestroyController(controllerIndex); } catch { }
            try { RegistryLedger.Release(controllerIndex); } catch { }
            if (configWritten)
                DeleteInstanceConfig(controllerIndex);
            throw;
        }
        finally {
            LogDiag($"<<< SETUP  EXIT  ctrl={controllerIndex} returnedId={(returnedId ?? "(null)")} total={setupTotalSw.ElapsedMilliseconds}ms");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  v2.0.0: the start and the driver's half of the version contract
    // ════════════════════════════════════════════════════════════════════

    /// <summary>How a wait for a device ended.</summary>
    internal enum StartWait { Done, Failed, TimedOut }

    /// <summary>A devnode's presence, status and problem number
    /// (CM_Get_DevNode_Status). A check replaces it to give the waits a
    /// device of its own.</summary>
    internal static Func<string, (bool Present, uint Status, uint Problem)> DevnodeStatus = ReadDevnodeStatus;

    /// <summary>The HID child a parent devnode has, or null.</summary>
    internal static Func<string, string?> HidChildOf = DeviceManager.GetHidChildId;

    /// <summary>The driver's half of an index's version section
    /// (<see cref="SharedMemoryIO.DriverHalf"/>).</summary>
    internal static Func<int, (int Protocol, int ProtocolMin, Version? Version, int ConfigError)> DriverHalf = SharedMemoryIO.DriverHalf;

    private static (bool Present, uint Status, uint Problem) ReadDevnodeStatus(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0) return (false, 0, 0);
        return CM_Get_DevNode_Status(out uint status, out uint problem, devInst, 0) == 0 ? (true, status, problem) : (false, 0, 0);
    }

    // Problem numbers a failed add or start leaves (cfg.h): the device
    // stays down until something restarts it, so a wait need not run out.
    private const uint CM_PROB_FAILED_START = 10, CM_PROB_FAILED_ADD = 31, CM_PROB_FAILED_DRIVER_ENTRY = 37;

    /// <summary>The device's driver failed its add or start: the devnode
    /// reports one of the problems above, or the driver set its error word
    /// in the controller's version section.</summary>
    private static bool StartFailed(string instanceId, int controllerIndex)
    {
        if (DriverHalf(controllerIndex).ConfigError != 0) return true;
        var (present, status, problem) = DevnodeStatus(instanceId);
        return present && (status & DN_HAS_PROBLEM) != 0
            && problem is CM_PROB_FAILED_START or CM_PROB_FAILED_ADD or CM_PROB_FAILED_DRIVER_ENTRY;
    }

    /// <summary>The value a driver's error word names (driver.h
    /// HIDMAESTRO_CONFIG_ERROR_*).</summary>
    internal static string ConfigErrorValue(int error) => error switch
    {
        1 => "FeatureAnswers",
        2 => "CachedInputReportIds",
        _ => $"error {error}",
    };

    /// <summary>What a failed start throws: an
    /// <see cref="HMDriverVersionException"/> naming the value when the
    /// driver set its error word, else the devnode's problem.</summary>
    internal static Exception StartError(string instanceId, int controllerIndex)
    {
        var half = DriverHalf(controllerIndex);
        if (half.ConfigError != 0)
            return new HMDriverVersionException(HMDriverVersionProblem.ValueNotRead,
                $"The HIDMaestro driver {half.Version?.ToString() ?? "(no version written)"} could not read the value " +
                $"{ConfigErrorValue(half.ConfigError)} this create wrote for {instanceId}, so the device did not start " +
                "and the create removed it. This SDK and that driver disagree on the value's format: InstallDriver puts " +
                "this SDK's driver in place once nothing runs on the installed one.",
                half.Version ?? new Version(0, 0, 0), DriverDeploy.MinimumDriverVersion, new[] { instanceId });
        var (_, _, problem) = DevnodeStatus(instanceId);
        return new InvalidOperationException($"The device {instanceId} failed its start, problem code {problem}.");
    }

    /// <summary>After the device started: its driver must serve this SDK's
    /// protocol, <see cref="SharedMemoryIO.SDK_PROTOCOL"/>, within its range
    /// from <c>DriverProtocolMin</c> to <c>DriverProtocol</c>, so a
    /// mis-stamped driver or a later one that dropped protocol 4 is refused
    /// rather than passing a check of the maximum alone. A driver that wrote
    /// no section passes only for a profile alternative B lets run on an
    /// older driver (<paramref name="acceptNoSection"/>).</summary>
    internal static void CheckDriverHalf(int controllerIndex, string instanceId, bool acceptNoSection)
    {
        var half = DriverHalf(controllerIndex);
        if (half.ConfigError != 0) throw StartError(instanceId, controllerIndex);
        if (half.Protocol == 0 && acceptNoSection) return;
        if (half.ProtocolMin <= SharedMemoryIO.SDK_PROTOCOL && SharedMemoryIO.SDK_PROTOCOL <= half.Protocol) return;
        string range = half.Protocol == 0 ? "wrote no protocol" : $"serves protocols {half.ProtocolMin} to {half.Protocol}";
        // A driver whose oldest protocol is past this SDK's is newer, and a
        // newer driver is never replaced by an older SDK's.
        string advice = half.ProtocolMin > SharedMemoryIO.SDK_PROTOCOL
            ? "That driver is newer and is never replaced by this SDK's: use an SDK of its release."
            : "InstallDriver puts this SDK's driver in place once nothing runs on the installed one.";
        throw new HMDriverVersionException(HMDriverVersionProblem.ProtocolNotServed,
            $"The HIDMaestro driver {half.Version?.ToString() ?? "of an older release"} on {instanceId} {range}, " +
            $"and this SDK speaks protocol {SharedMemoryIO.SDK_PROTOCOL}. The create removed the device. {advice}",
            half.Version ?? new Version(0, 0, 0), DriverDeploy.MinimumDriverVersion, new[] { instanceId });
    }

    /// <summary>
    /// Polls for a HID child PDO to appear under the given parent devnode.
    /// Replaces fixed Thread.Sleep calls with an event-driven poll that exits
    /// as soon as the condition is met. On fast machines this returns in
    /// &lt;100ms; on slow machines it adapts up to the timeout. v2.0.0: it
    /// also ends at once when the parent's driver failed its start.
    /// </summary>
    internal static StartWait WaitForHidChild(string parentInstanceId, int controllerIndex, int timeoutMs)
    {
        // v1.3.0 wip 12 kept this poll after the diag log showed
        // DeviceManager.WaitForHidChild returning False at once. That was
        // the registration failing on a misdeclared CM_NOTIFY_FILTER (432
        // bytes, refused as CR_INVALID_DATA), not a race with xinputhid,
        // and is fixed in DeviceManager. The poll stays: the 25 ms cadence
        // vs the v1.2.x 100 ms one saves up to 75 ms per controller after
        // the child appears.
        int budget = TimeoutScale.Apply(timeoutMs);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budget)
        {
            if (HidChildOf(parentInstanceId) != null)
                return StartWait.Done;
            if (StartFailed(parentInstanceId, controllerIndex))
                return StartWait.Failed;
            Thread.Sleep(25);
        }
        return StartWait.TimedOut;
    }

    /// <summary>
    /// Polls for a device to reach DN_STARTED status (driver fully bound and
    /// device functional). Replaces fixed Thread.Sleep calls that waited for
    /// xinputhid or other upper filters to finish binding. v2.0.0: it also
    /// ends at once on a failed start of the device or of its parent's
    /// driver.
    /// </summary>
    internal static StartWait WaitForDeviceStarted(string instanceId, string parentInstanceId, int controllerIndex, int timeoutMs)
    {
        // A poll loop. Windows does report a start
        // (CM_NOTIFY_ACTION_DEVICEINSTANCESTARTED), but this poll carries the
        // measured create timings. v1.3.0: tighter poll cadence (25 ms vs 100 ms)
        // reduces the worst-case tail wait from ~99 ms to ~24 ms after
        // the device actually starts. The 75 ms saved on every Xbox-family
        // create adds up across multi-controller batches.
        int budget = TimeoutScale.Apply(timeoutMs);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budget)
        {
            var (present, status, _) = DevnodeStatus(instanceId);
            if (present && (status & DN_STARTED) != 0)
                return StartWait.Done;
            if (StartFailed(instanceId, controllerIndex) || StartFailed(parentInstanceId, controllerIndex))
                return StartWait.Failed;
            Thread.Sleep(25);
        }
        return StartWait.TimedOut;
    }

    /// <summary>Count XInput slots currently reporting connected. Used by
    /// SetupController to wait for the slot claim after each Xbox controller
    /// is created so multi-Xbox setups get deterministic slot ordering.</summary>
    private static int CountConnectedXInputSlots()
    {
        int count = 0;
        for (uint slot = 0; slot < 4; slot++)
            if (XInputGetState(slot, out _) == 0) count++;
        return count;
    }

    [LibraryImport("xinput1_4.dll")]
    private static partial uint XInputGetState(uint dwUserIndex, out _XINPUT_STATE pState);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct _XINPUT_GAMEPAD
    {
        public ushort wButtons; public byte bLeftTrigger; public byte bRightTrigger;
        public short sThumbLX; public short sThumbLY; public short sThumbRX; public short sThumbRY;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct _XINPUT_STATE
    {
        public uint dwPacketNumber; public _XINPUT_GAMEPAD Gamepad;
    }

    // ════════════════════════════════════════════════════════════════════
    //  TeardownController
    // ════════════════════════════════════════════════════════════════════

    public static void TeardownController(int controllerIndex, string? instanceId)
        => TeardownController(controllerIndex, instanceId, skipOrphanSweep: false);

    /// <summary>Internal overload used by HMContext.Dispose's parallel batch
    /// teardown to skip the system-wide HID orphan sweep on every controller.
    /// The sweep is idempotent but expensive: running it once at the end of
    /// the batch (instead of N times concurrently) is a clean win.</summary>
    internal static void TeardownController(int controllerIndex, string? instanceId, bool skipOrphanSweep)
    {
        var totalSw = Stopwatch.StartNew();
        LogDiag($">>> TEARDOWN ENTER ctrl={controllerIndex} instanceId={instanceId ?? "(null)"}");

        // Acquire the per-controllerIndex teardown gate IMMEDIATELY at entry.
        // SetupController for the same index will block on this gate at its
        // own entry until we Set it again at the END of this function. This
        // is the contract enforcement: no SetupController for index N can
        // proceed while a TeardownController for index N is still in flight,
        // even if HMController.Dispose returned early to the caller. Released
        // in finally below.
        BeginTeardownGate(controllerIndex);

        try
        {

        try { SharedMemoryIO.DestroyController(controllerIndex); } catch { }

        // Capture the HID children of the parent BEFORE any removal step.
        // Once the parent goes, CM_Get_Child / CM_Get_Sibling on it stops
        // working: so any explicit per-child cleanup needs the list in
        // hand first. Covers issue #11 (xbox-360-wired orphan that
        // v1.1.17's post-teardown orphan-sweep was missing because its
        // ROOT parent was still lingering as phantom when the sweep ran).
        List<string> preCapturedHidChildren = new List<string>();
        if (!string.IsNullOrEmpty(instanceId))
        {
            try { preCapturedHidChildren = DeviceManager.GetAllHidChildIds(instanceId!); }
            catch { preCapturedHidChildren = new List<string>(); }
        }
        LogDiag($"    pre-captured HID children: {preCapturedHidChildren.Count}");
        foreach (var c in preCapturedHidChildren) LogDiag($"      child: {c}");

        // Close all SwDeviceCreate-owned handles for this controller FIRST.
        // SwDeviceCreate-managed devices (xinputhid-path gamepad companion
        // at SWD\HIDMAESTRO_VID_*_PID_*&IG_00, and the XUSB companion at
        // SWD\HIDMAESTRO\<sid>_<idx>) clean up properly only when their
        // HSWDEVICE handle is released. Calling DIF_REMOVE on a Sw-created
        // devnode FIRST leaves the SWDEVICE kernel refcount dangling: the
        // devnode goes from PnP but the kernel-side state (interface-class
        // registrations, slot-allocator entries) lingers. SwDeviceClose
        // first triggers Windows' clean cascade: devnode removal +
        // interface-class cleanup + HID children cascade-remove + Sw kernel
        // state released, in the correct order.
        //
        // On process exit, Windows handles all of this for free regardless
        // of user-mode order: that's why baseline tests (create N → exit)
        // pass even with the wrong order. Mid-process live profile swaps
        // (HMController.Dispose followed immediately by CreateController
        // for a different profile at the same ControllerIndex) require the
        // explicit ordering, otherwise the prior controller's Sw-managed
        // devnodes leave zombies that occupy XInput slots forever.
        if (!string.IsNullOrEmpty(instanceId))
        {
            // forceFallbacks: this is the live-swap teardown path. We must
            // leave nothing behind: if the old device persists as a phantom,
            // its driver's WUDFHost kept alive by it, CM IOCTLs keep being
            // served, and the new controller at the same ControllerIndex
            // shares Global\HIDMaestroOutput{N} with the zombie, surfacing
            // the zombie's rumble publishes on the new controller's SDK
            // (observed: XInput rumble bleeding onto a DS4 after live-swap
            // from xbox-360-wired, because the prior Xbox 360 HIDMAESTRO
            // failed to tear down in default-mode and the Xbox UI's Guide
            // haptic kept poking its XUSB interface).
            //
            // SwD-enumerated parents go through SwdDeviceFactory.Remove
            // (lifetime-downgrade + SwDeviceClose) inside RemoveDevice's
            // SWD\ branch; legacy ROOT-enumerator parents (Xbox 360 wired
            // main HID, plain HID profiles) take the DIF_REMOVE path.
            //
            // timeoutMs=120000: per the SDK README, xinputhid filter unload
            // for BT typically takes 5-11 seconds, but on heavily loaded or
            // slow user machines (low-end CPU + many active HID stacks +
            // Windows running PnP coinstaller work in parallel) the
            // cascade can stretch much further. A 5s or even 15s timeout
            // is a hard error mode: RemoveDevice returns with
            // goneAfterDif=false and the caller moves on while the kernel
            // is still cleaning up, breaking PadForge's "Dispose returns
            // when teardown is fully complete" contract. 120s is the
            // historical pre-regression value: generous enough that real
            // cascades always finish inside it, capped so a truly hung
            // kernel doesn't block forever.
            var parentSw = Stopwatch.StartNew();
            bool parentRemoved = false;
            // This catch and the teardown's others below log every failure and
            // go on, a PnpLockBusyException included. During the exit the
            // refusal is counted (DeviceManager.LockTakesRefusedHere) and the
            // lease stays. Outside it, each later RemoveDevice here waits for
            // the lock again, up to ten minutes for each child and candidate.
            try { parentRemoved = DeviceManager.RemoveDevice(instanceId!, timeoutMs: 120_000, forceFallbacks: true); }
            catch (Exception ex) { LogDiag($"    parent RemoveDevice EXCEPTION: {ex.Message}"); }
            LogDiag($"    parent RemoveDevice({instanceId}) returned {parentRemoved} after {parentSw.ElapsedMilliseconds}ms");

            // Now explicitly remove each captured HID child. If Windows
            // already cascade-removed them (typical for Sw-managed parents
            // after SwDeviceClose, or for ROOT parents after DIF_REMOVE),
            // these calls are harmless no-ops. If cascade failed or is
            // still in flight, this ensures the child is cleanly torn down
            // rather than lingering as an enumerable-but-unusable PDO.
            foreach (var childId in preCapturedHidChildren)
            {
                var childSw = Stopwatch.StartNew();
                bool childRemoved = false;
                try { childRemoved = DeviceManager.RemoveDevice(childId, timeoutMs: 120_000, fast: true, forceFallbacks: true); }
                catch (Exception ex) { LogDiag($"    child RemoveDevice EXCEPTION ({childId}): {ex.Message}"); }
                LogDiag($"    child RemoveDevice({childId}) returned {childRemoved} after {childSw.ElapsedMilliseconds}ms");
            }
        }

        // Backstop: scan and remove HIDMAESTRO devices by ControllerIndex.
        // Sweep BOTH the SWD enumerator (post-slot-1-skip-fix path) and the
        // legacy ROOT enumerator (devices created by older versions still on
        // a pre-SWD install). Belt-and-suspenders against any path where
        // the per-controller teardown above missed an instance (e.g.,
        // crash recovery from a previous session, or hand-installed
        // legacy companions still on a pre-SWD layout).
        foreach (var enumRoot in new[] { "SWD", "ROOT" })
        {
            try
            {
                using var hmEnum = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Enum\{enumRoot}\HIDMAESTRO");
                if (hmEnum == null) continue;
                foreach (var inst in hmEnum.GetSubKeyNames())
                {
                    string candidate = $@"{enumRoot}\HIDMAESTRO\{inst}";
                    using var dp = hmEnum.OpenSubKey($@"{inst}\Device Parameters");
                    if (dp?.GetValue("ControllerIndex") is int ci && ci == controllerIndex)
                    {
                        // 120s timeout: same generous budget as the
                        // primary parent removal above, so SwDevice-
                        // managed XUSB companions on slow machines have
                        // time to fully cascade before the gate signals
                        // SetupController to proceed.
                        var sweepSw = Stopwatch.StartNew();
                        bool sweepRemoved = false;
                        try { sweepRemoved = DeviceManager.RemoveDevice(candidate, timeoutMs: 120_000, forceFallbacks: true); }
                        catch (Exception ex) { LogDiag($"    HIDMAESTRO sweep EXCEPTION ({candidate}): {ex.Message}"); }
                        LogDiag($"    HIDMAESTRO sweep RemoveDevice({candidate}) returned {sweepRemoved} after {sweepSw.ElapsedMilliseconds}ms");
                    }
                }
            }
            catch { }
        }

        // Also scan VID_*&IG_00 companions (xinputhid gamepad companions).
        // Sweep SWD (new) + ROOT (legacy) + HID (xinputhid synthesizes
        // multiple top-level HID collections per parent: when one
        // survives the kernel cascade after SwDeviceClose, the surviving
        // sibling under HID\HIDMAESTRO_VID_*_PID_*&IG_00 must be reaped
        // by ControllerIndex match here, otherwise it lingers as a
        // duplicate XInput slot until reboot. Live-swap repro: Series BT
        // → Xbox 360 wired → Series BT was leaving a HID child orphan
        // that no other sweep caught).
        foreach (var enumRoot in new[] { "SWD", "ROOT", "HID" })
        {
            try
            {
                using var enumKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumRoot}");
                if (enumKey == null) continue;
                foreach (var sub in enumKey.GetSubKeyNames())
                {
                    if (!sub.Contains("&IG_00", StringComparison.OrdinalIgnoreCase)) continue;
                    using var vidKey = enumKey.OpenSubKey(sub);
                    if (vidKey == null) continue;
                    foreach (var instName in vidKey.GetSubKeyNames())
                    {
                        string candidate = $@"{enumRoot}\{sub}\{instName}";
                        using var dp = vidKey.OpenSubKey($@"{instName}\Device Parameters");
                        if (dp?.GetValue("ControllerIndex") is int ci && ci == controllerIndex)
                        {
                            // 120s timeout: same rationale as the HIDMAESTRO
                            // sweep above: full cascade-complete on slow
                            // machines must not race the gate.
                            var igSw = Stopwatch.StartNew();
                            bool igRemoved = false;
                            try { igRemoved = DeviceManager.RemoveDevice(candidate, timeoutMs: 120_000, forceFallbacks: true); }
                            catch (Exception ex) { LogDiag($"    IG_00 sweep EXCEPTION ({candidate}): {ex.Message}"); }
                            LogDiag($"    IG_00 sweep RemoveDevice({candidate}) returned {igRemoved} after {igSw.ElapsedMilliseconds}ms");
                        }
                    }
                }
            }
            catch { }
        }

        // Clear the per-instance registry config and the identity entry
        DeleteInstanceConfig(controllerIndex);

        // Sweep any HID children orphaned by the parent removals above.
        // DeviceManager.RemoveDevice on a ROOT\ parent should cascade to its
        // HID child PDOs, but in practice on Win11 26200 the child sometimes
        // survives: detached from its removed parent, enumerable by SDL /
        // RawInput / WGI, and no longer visible through a HIDMaestro PnP
        // ancestor walk. Consumers that filter on "is this ours?" by walking
        // the parent chain see an un-owned HID gamepad at VID:PID 045E:028E
        // (or the profile's VID:PID) and treat it as a real physical device.
        //
        // Specific to the XUSB companion path (xbox-360-wired) per issue #9
        // but the orphan-sweep pass is safe and general: it removes HID
        // children whose registered parent instance-id is a ROOT\ device
        // that can no longer be located (neither live nor phantom). That
        // match is intrinsically "our just-removed parents" and only our
        // just-removed parents: live controllers still anchor their HID
        // children to a locatable parent and are untouched.
        if (!skipOrphanSweep)
            try { DeviceManager.RemoveOrphanHidChildren(); } catch { }

        }
        finally
        {
            // Shared registry keys this controller held (foundation-sunshine
            // #1138) go back to their prior state once no other live
            // controller holds them.
            try { RegistryLedger.Release(controllerIndex); } catch { }

            // Release the gate: SetupController for this controllerIndex
            // can now proceed. Reached via every code path including
            // exceptions inside the teardown body.
            EndTeardownGate(controllerIndex);
            LogDiag($"<<< TEARDOWN EXIT  ctrl={controllerIndex} total={totalSw.ElapsedMilliseconds}ms");
        }
    }

    /// <summary>One-time orphan sweep used after a batch teardown
    /// (HMContext.Dispose with multiple controllers). Skipping the per-
    /// controller sweep + running this once is what avoids the redundant
    /// system-wide HID enumeration.</summary>
    internal static void RemoveOrphanHidChildrenBatch()
    {
        try { DeviceManager.RemoveOrphanHidChildren(); } catch { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  RemoveAllVirtualControllers: purge every HIDMaestro virtual device
    //  from the system, including orphans from prior runs. Used by the
    //  "cleanup" CLI command and by consumers who want a clean slate.
    // ════════════════════════════════════════════════════════════════════

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenEventW(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, string lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(IntPtr hEvent);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    private const uint EVENT_MODIFY_STATE = 0x0002;

    /// <summary>
    /// Signal the named HIDMaestro StopEvents for the given controller
    /// indices, then sleep 500ms to give blocked WUDFHost worker threads
    /// a chance to exit. Without this, after a force-kill the workers
    /// stay blocked on WaitForMultipleObjects(StopEvent, InputDataEvent)
    /// and PnP removal blocks ~1s/device waiting for the kernel
    /// query-remove to time out. With the signal: workers exit, WUDFHost
    /// releases, subsequent DIF_REMOVE completes in milliseconds. For
    /// graceful shutdown the signal makes cleanup near-instant.
    ///
    /// <para>Issue #38: <paramref name="targets"/> is the index set
    /// harvested from the devnodes the sweep is actually about to remove
    /// (<see cref="CollectSweepTargetIndices"/>), not an unconditional
    /// 0..15 broadcast. The named events are shared machine-wide, so the
    /// old broadcast signaled every live process's workers on every
    /// sweep. The driver's TearingDown gate makes a stray signal
    /// non-lethal now (workers recycle instead of exiting), but the
    /// sweep still should not make other sessions' workers churn. Pass
    /// null to fall back to the broadcast set (0..15 plus every
    /// Controller&lt;N&gt; config key), used when a devnode was found
    /// whose index could not be read.</para>
    /// </summary>
    private static void SignalStopEventsAndDrain(HashSet<int>? targets)
    {
        HashSet<int> indices;
        if (targets != null)
        {
            indices = targets;
        }
        else
        {
            // Broadcast fallback: 0-15 baseline plus any Controller<N>
            // config key (the 2026-07-21 perf audit: indexing is
            // unbounded, so controllers >= 16 previously missed the
            // fast-stop signal and paid the slow query-remove path).
            indices = new HashSet<int>(Enumerable.Range(0, 16));
            try
            {
                using var hmKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestro");
                if (hmKey != null)
                    foreach (var name in hmKey.GetSubKeyNames())
                        if (name.StartsWith("Controller", StringComparison.OrdinalIgnoreCase)
                            && int.TryParse(name.Substring(10), out int idx))
                            indices.Add(idx);
            }
            catch { }
        }

        bool anySignaled = false;
        foreach (int i in indices)
        {
            IntPtr ev = OpenEventW(EVENT_MODIFY_STATE, false, $@"Global\HIDMaestroStopEvent{i}");
            if (ev != IntPtr.Zero)
            {
                SetEvent(ev);
                CloseHandle(ev);
                anySignaled = true;
            }
        }
        // Drain only when a live driver worker was actually signaled. The
        // unconditional 500 ms sleep ran on EVERY launch (twice on the
        // documented InstallDriver + explicit-cleanup sequence) even with
        // zero orphans on a clean machine.
        if (anySignaled)
            Thread.Sleep(TimeoutScale.Apply(500));
    }

    /// <summary>Read-only pre-pass over the exact enumerator/ownership set
    /// the sweep below removes, harvesting each owned devnode's
    /// <c>Device Parameters\ControllerIndex</c>. Returns null when any
    /// owned devnode's index could not be read, which tells the caller to
    /// fall back to the broadcast signal so a force-killed session's
    /// worker is never left blocking PnP removal (issue #38).</summary>
    private static HashSet<int>? CollectSweepTargetIndices()
    {
        var indices = new HashSet<int>();
        bool unindexed = false;
        foreach (var enumRoot in new[] { "ROOT", "SWD" })
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumRoot}");
            if (enumKey == null) continue;
            foreach (var sub in enumKey.GetSubKeyNames())
            {
                bool exclusivelyOurs = sub.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);
                bool shared = sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("HID_IG_00", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase);
                if (!exclusivelyOurs && !shared) continue;

                using var subKey = enumKey.OpenSubKey(sub);
                if (subKey == null) continue;
                foreach (var inst in subKey.GetSubKeyNames())
                {
                    if (!exclusivelyOurs
                        && !DeviceManager.IsHidMaestroOwned($@"{enumRoot}\{sub}\{inst}"))
                        continue;
                    using var dp = subKey.OpenSubKey($@"{inst}\Device Parameters");
                    if (dp?.GetValue("ControllerIndex") is int ci)
                        indices.Add(ci);
                    else
                        unindexed = true;
                }
            }
        }
        catch { unindexed = true; }
        return unindexed ? null : indices;
    }

    /// <summary>Signal the stop events of these indices so their driver
    /// workers exit before their devices are removed.</summary>
    internal static void SignalStopEvents(HashSet<int> indices) => SignalStopEventsAndDrain(indices);

    /// <summary>Delete one index's per-instance configuration key and every
    /// identity entry that names the index.</summary>
    internal static void DeleteInstanceConfig(int controllerIndex)
    {
        try { Registry.LocalMachine.DeleteSubKeyTree(RegPathForIndex(controllerIndex), throwOnMissingSubKey: false); }
        catch { }
        DeleteIdentityEntries(controllerIndex);
    }

    /// <summary>v2.0.0: <c>SOFTWARE\HIDMaestro\Identity</c>. Each subkey is
    /// an identity token, with the value <see cref="IdentityIndexValue"/>
    /// naming the controller index the token's devnodes read their
    /// configuration from. A driver reads it before WdfDeviceCreate, from
    /// its own instance ID's last segment, since an SWD devnode starts
    /// before its hardware key holds the index (the design note's decision
    /// (c), "The index before the first start").</summary>
    internal const string IdentityRoot = REG_BASE + @"\Identity";
    internal const string IdentityIndexValue = "ControllerIndex";

    /// <summary>Writes the identity entry of a controller at an index,
    /// after deleting every other entry that names the index, which only a
    /// program that ended without its teardown leaves.</summary>
    internal static void WriteIdentityEntry(string token, int controllerIndex)
    {
        DeleteIdentityEntries(controllerIndex);
        using var key = Registry.LocalMachine.CreateSubKey($@"{IdentityRoot}\{token}");
        key.SetValue(IdentityIndexValue, controllerIndex, RegistryValueKind.DWord);
    }

    /// <summary>Deletes every identity entry that names the index.</summary>
    internal static void DeleteIdentityEntries(int controllerIndex)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(IdentityRoot, writable: true);
            if (root == null) return;
            foreach (string token in root.GetSubKeyNames())
            {
                object? value;
                using (var entry = root.OpenSubKey(token)) value = entry?.GetValue(IdentityIndexValue);
                if (value is int i && i == controllerIndex)
                    root.DeleteSubKeyTree(token, throwOnMissingSubKey: false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    /// <summary>The uninstall-grade sweep, <see cref="HMCleanupScope.Everything"/>:
    /// every HIDMaestro device and USB/IP import on the machine, whoever made
    /// it, then the ledger, and with <paramref name="preserveInstall"/> false
    /// the driver packages and <c>SOFTWARE\HIDMaestro</c>. Running programs
    /// lose their controllers. Before v1.13.0 this was what InstallDriver and
    /// every RemoveAllVirtualControllers overload ran. The 2026-07-21 perf
    /// audit's <paramref name="preserveInstall"/> keeps the packages and the
    /// manifest-hash fast path, which a battery or support tool resetting
    /// devices between runs wants.</summary>
    internal static void RemoveEverything(bool preserveInstall)
    {
        // This process's phantom cleanup uninstalls phantom records under
        // CreateGate's write side, and its creates register devnodes under
        // the read side. The walk below removes those same records and the
        // packages they are bound to, so it waits for both and holds the
        // next back until it is done.
        OwnerSweep.CreateGate.EnterWriteLock();
        try
        {
            BeforeEverythingActs?.Invoke();
            RemoveEverythingGated(preserveInstall);
        }
        finally { OwnerSweep.CreateGate.ExitWriteLock(); }
    }

    /// <summary>Called by <see cref="RemoveEverything"/> once it holds
    /// CreateGate, before it acts. The phantomgate probe sets it through
    /// reflection to see when the sweep would start, and throws from it so
    /// the sweep acts on nothing. Null outside the probe.</summary>
    internal static Action? BeforeEverythingActs;

    private static void RemoveEverythingGated(bool preserveInstall)
    {
        var sweepSw = System.Diagnostics.Stopwatch.StartNew();
        long lastMs = 0;
        // Local phase logger: no-ops unless HIDMAESTRO_DIAG=1 (LogDiag gate).
        void Phase(string name)
        {
            LogDiag($"    sweep phase {name}: {sweepSw.ElapsedMilliseconds - lastMs}ms (total {sweepSw.ElapsedMilliseconds}ms)");
            lastMs = sweepSw.ElapsedMilliseconds;
        }
        LogDiag($">>> SWEEP ENTER preserveInstall={preserveInstall}");

        SignalStopEventsAndDrain(CollectSweepTargetIndices());
        Phase("stop-drain");

        // Composite personas first (issue #44). They are USB devices behind
        // the emulated host controller and carry no HIDMAESTRO token of
        // their own, so the enumerator walk below cannot reach them: a
        // consumer that created one and exited left it enumerated. Detach
        // before the walk so the USB devnodes are already gone when PnP
        // settles the rest.
        Usbip.UsbipBackend.DetachAllOwned();
        Phase("usbip-detach");

        // Walk ROOT + SWD enumerators and remove HIDMaestro-owned devices.
        // Enumerators we always own: VID_*, XnaComposite, HIDMAESTRO, HID_IG_00
        // Shared enumerators (HIDCLASS, SYSTEM): verify hardware ID contains "HIDMaestro"
        foreach (var enumRoot in new[] { "ROOT", "SWD" })
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumRoot}");
            if (enumKey == null) continue;

            foreach (var sub in enumKey.GetSubKeyNames())
            {
                // Issue #28 (v1.3.16): HIDMAESTRO* is the only enumerator
                // we own exclusively. VID_*, XnaComposite, HID_IG_00,
                // HIDCLASS, SYSTEM are all shared with third-party drivers
                //: require IsHidMaestroOwned on every instance under those.
                bool exclusivelyOurs = sub.Equals("HIDMAESTRO", StringComparison.OrdinalIgnoreCase)
                    || sub.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);

                bool shared = sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("HID_IG_00", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase);

                if (!exclusivelyOurs && !shared) continue;

                using var subKey = enumKey.OpenSubKey(sub);
                if (subKey == null) continue;

                foreach (var inst in subKey.GetSubKeyNames())
                {
                    string instId = $@"{enumRoot}\{sub}\{inst}";

                    // One device whose removal throws (an SWD parent whose
                    // helper cannot be staged) does not stop the sweep.
                    try
                    {
                        if (exclusivelyOurs)
                        {
                            DeviceManager.RemoveDevice(instId, timeoutMs: 5000, fast: true, forceFallbacks: true);
                            continue;
                        }

                        // Shared: only remove if hardware ID contains "HIDMaestro"
                        if (DeviceManager.IsHidMaestroOwned(instId))
                            DeviceManager.RemoveDevice(instId, timeoutMs: 3000, fast: true, forceFallbacks: true);
                    }
                    catch (Exception ex) when (ex is not DeviceManager.PnpLockBusyException)
                    {
                        // A staging failure arrives as InvalidOperationException,
                        // UnauthorizedAccessException or IOException. Any of them
                        // leaves this device and the walk goes on to the next.
                        // The PnP lock held past its bound stops this walk, as
                        // every device's removal would wait for it again. It
                        // stops this walk only: the SWD walk, the HID orphans
                        // and each interface class catch it on their own and
                        // take the lock again, so with a frozen holder the
                        // cleanup still waits once for each of them.
                        LogDiag($"    sweep RemoveDevice({instId}) refused: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
        catch { }

        Phase("root-swd-evict");

        // Remove orphaned HID children (survive parent removal as "Unknown").
        try
        {
            using var hidEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID");
            if (hidEnum != null)
            {
                foreach (var sub in hidEnum.GetSubKeyNames())
                {
                    // Issue #28 (v1.3.16): the shape match
                    // `HID\VID_045E*` collides with real Xbox 360 controller
                    // phantom records; `HID\HIDCLASS` and `HID\HID_IG_*`
                    // collide with vJoy's HID child. HID children carry HID
                    // class hwids (`HID_DEVICE_SYSTEM_GAME`, etc.) not the
                    // HM signature, so the child's own HardwareID can't
                    // prove ownership. The ParentIdPrefix → parent
                    // instance ID lookup proves ownership via the parent's
                    // HardwareID instead.
                    bool shapeMatch = sub.StartsWith("VID_045E", StringComparison.OrdinalIgnoreCase)
                        || sub.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase)
                        || sub.StartsWith("HID_IG", StringComparison.OrdinalIgnoreCase);
                    if (!shapeMatch) continue;

                    using var childEnum = hidEnum.OpenSubKey(sub);
                    if (childEnum == null) continue;
                    foreach (var inst in childEnum.GetSubKeyNames())
                    {
                        string childId = $@"HID\{sub}\{inst}";
                        // Prove ownership through the parent HM root /
                        // companion: read the child's ParentIdPrefix, find
                        // the parent under the appropriate enumerator,
                        // require IsHidMaestroOwned. If the parent is
                        // already gone (phantom-orphan case), fall back to
                        // accepting it: the only legitimate writer of HID
                        // children with HM-shape hwid + HM-shape parent
                        // pattern would have been us, and a phantom whose
                        // parent has been removed can't be matched to a
                        // live third-party owner.
                        string? parentId = TryResolveHidChildParent(childEnum, inst);
                        bool parentIsOurs = parentId != null && DeviceManager.IsHidMaestroOwned(parentId);
                        bool parentIsGone = parentId == null
                            || CM_Locate_DevNodeW(out _, parentId, CM_LOCATE_DEVNODE_PHANTOM) != 0;
                        if (!parentIsOurs && !parentIsGone) continue;
                        if (CM_Locate_DevNodeW(out uint childInst, childId, 0) == 0)
                        {
                            bool parentGone = CM_Get_Parent(out uint _, childInst, 0) != 0;
                            if (parentGone)
                                DeviceManager.RemoveDevice(childId, timeoutMs: 3000, fast: true, forceFallbacks: true);
                        }
                        else if (CM_Locate_DevNodeW(out childInst, childId, CM_LOCATE_DEVNODE_PHANTOM) == 0)
                        {
                            // CRITICAL: forceFallbacks=true is what lets devcon
                            // actually tear down the phantom. Without it the
                            // phantom HID child keeps WUDFHost alive for the
                            // prior-session driver process, and that WUDFHost
                            // has OUR DLL memory-mapped from a DriverStore
                            // directory that's since been deleted. A freshly
                            // created device from the NEW session can get
                            // assigned to that stale WUDFHost, executing the
                            // OLD driver bytes while the disk has the NEW
                            // ones: yielding "driver upgraded but behavior
                            // unchanged" mysteries. Draining the phantoms
                            // lets the stale WUDFHost exit and a fresh one
                            // start with the fresh DLL.
                            DeviceManager.RemoveDevice(childId, timeoutMs: 3000, fast: true, forceFallbacks: true);
                        }
                    }
                }
            }
        }
        catch { }

        Phase("hid-orphans");

        // Clean Device Parameters under our enumerators via reg.exe (PnP ACLs
        // prevent direct writes). Leaves the PnP instance keys themselves intact.
        // Sweeps both ROOT\ and SWD\ subtrees. HIDMAESTRO* is ours alone. The
        // other enumerators are shared with third-party root devices, so an
        // instance there needs the HardwareID proof first (#28, #1138).
        {
            foreach (var enumRootName in new[] { "ROOT", "SWD" })
            try
            {
                using var enumRoot = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumRootName}");
                if (enumRoot == null) continue;
                foreach (var sub in enumRoot.GetSubKeyNames())
                {
                    bool exclusivelyOurs = sub.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);
                    bool shared = sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                        || sub.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase)
                        || sub.Equals("HID_IG_00", StringComparison.OrdinalIgnoreCase);
                    if (!exclusivelyOurs && !shared) continue;
                    using var subKey = enumRoot.OpenSubKey(sub);
                    if (subKey == null) continue;
                    foreach (var inst in subKey.GetSubKeyNames())
                    {
                        if (!exclusivelyOurs && !DeviceManager.IsHidMaestroOwned($@"{enumRootName}\{sub}\{inst}"))
                            continue;
                        string dpPath = $@"SYSTEM\CurrentControlSet\Enum\{enumRootName}\{sub}\{inst}\Device Parameters";
                        RunProcess("reg.exe", $"delete \"HKLM\\{dpPath}\" /f", timeoutMs: 3000);
                    }
                }
            }
            catch { }
        }

        Phase("device-params");

        // Clean interface registries (XUSB + WinExInput), with the same
        // ownership proof the once-per-session cleanup uses.
        RemoveOwnedInterfaceEntries(phantomOnly: false);

        Phase("interface-classes");

        // Shared joy.cpl and GameInput keys (foundation-sunshine #1138).
        // Every virtual is gone by now, so every key the ledger holds goes
        // back to its prior state, and tables older builds left behind go.
        // Nothing else under those roots is touched: a vendor prefix is not
        // ownership.
        RegistryLedger.ReleaseAll();
        int legacyTables = RegistryLedger.RemoveLegacyTables();
        LogDiag($"    removed {legacyTables} legacy GameInput table(s)");

        Phase("shared-keys");

        // The WUDFHost release-wait and the orphan drain below exist to
        // protect DriverStore FILE REPLACEMENT: a host that still has the
        // old DLL mapped can hand stale code to a new device after an
        // upgrade. When this is a launch sweep (preserveInstall) AND
        // FullDeploy will take the same-version fast path, no file is
        // going to change: the winding-down hosts hold bytes identical to
        // the current DriverStore, and ProcessSharingDisabled means a new
        // device always gets a fresh host, never a recycled one. Skipping
        // the wait removes the ~1.7 s the recovery path spent watching
        // orphaned hosts exit gracefully (2026-07-21 perf audit). Any
        // version change or full-nuke cleanup keeps the full discipline.
        bool skipWudfSteps = preserveInstall && DriverBuilder.WillTakeFastPath();

        // Wait for WUDFHost processes to release our DLLs.
        if (!skipWudfSteps)
            WaitForDriverHostsToExit();

        // Remove driver packages from store. Strict, retry-aware, verifies
        // afterward (see PnputilHelper). Catch the verification throw here
        // because cleanup is best-effort: leaving a stale package is bad
        // (it'll bite the next install) but it shouldn't kill the cleanup
        // path entirely. The next FullDeploy will try again, and FullDeploy
        // does NOT swallow this exception.
        //
        // preserveInstall (launch path): keep the packages and the
        // SOFTWARE\HIDMaestro tree (manifest hash + per-controller config)
        // so FullDeploy's same-version fast path survives. See the
        // preserving overload's doc for the measured cost of nuking these
        // every launch.
        Phase("wudf-release-wait");

        if (!preserveInstall)
        {
            // Under the deploy lock, so no other program's deploy is between
            // its check and its install while the packages go.
            using var deploy = DriverDeploy.Acquire();
            try
            {
                try { PnputilHelper.RemoveAllHidMaestroPackages(); } catch { }
                // The WinUSB binding packages that no devnode uses. A devnode
                // still on one keeps its driver and the package stays.
                try { WinUsbBinding.RemovePackage(); } catch { }

                // Clear registry config
                try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\HIDMaestro", false); } catch { }
            }
            finally { deploy.ReleaseMutex(); }
        }
        Phase("package-removal");

        // Release shared-memory mappings
        try { SharedMemoryIO.Cleanup(); } catch { }

        // Records of programs that have exited describe nothing now. A
        // running program's record stays: it still holds its claims, and
        // its own dispose removes its entries.
        foreach (var rec in MachineClaims.ReadAll())
            if (!rec.Live) MachineClaims.DeleteRecord(rec.OwnerId);

        // Drain orphaned WUDFHost processes that STILL have our stale
        // HIDMaestro.dll memory-mapped from a since-deleted DriverStore
        // directory. This is what bit us for hours: pnputil /delete-driver
        // removes the file on disk, but any WUDFHost process that had the
        // DLL mapped keeps the OLD code resident in virtual memory. When a
        // fresh device is created AFTER a driver upgrade, PnP can assign
        // the new device to an existing WUDFHost (they're reused across
        // devices of the same UMDF class): and then the new device runs
        // the OLD driver bytes while the disk has NEW ones. That's the
        // "driver upgraded but behavior unchanged" mystery.
        //
        // Termination is SAFE because we inspect the target process's
        // loaded modules first: we only kill WUDFHost instances that host
        // NOTHING but our HIDMaestro.dll / HMXInput.dll (plus framework
        // DLLs like WUDFx02000.dll, WUDFPlatform.dll). Any WUDFHost that
        // hosts ANOTHER third-party UMDF driver (e.g.
        // microsoft.bluetooth.profiles.hidovergatt.dll for a real BT Xbox
        // controller) is skipped entirely: that's what the NEVER-kill-
        // WUDFHost rule in feedback-never-kill-wudfhost.md is about.
        if (!skipWudfSteps)
            DrainOrphanedWudfHosts();
        Phase("wudf-drain");
        LogDiag($"<<< SWEEP EXIT total={sweepSw.ElapsedMilliseconds}ms");
    }

    /// <summary>Before a package replacement with no HIDMaestro device
    /// left: wait for the hosts of the removed devices to exit, then drain
    /// any that still map the old DLL (<see cref="DrainOrphanedWudfHosts"/>).
    /// The caller has established that no device remains, so every such
    /// host is an orphan.</summary>
    internal static void ReleaseStaleDriverHosts()
    {
        WaitForDriverHostsToExit();
        DrainOrphanedWudfHosts();
    }

    private static void WaitForDriverHostsToExit()
    {
        try
        {
            string[] ourDlls = { "HIDMaestro.dll", "HMXInput.dll", "HIDMaestroCompanion.dll" };
            foreach (var wudf in Process.GetProcessesByName("WUDFHost"))
            {
                try
                {
                    bool hostsOurs = false;
                    foreach (ProcessModule mod in wudf.Modules)
                    {
                        if (ourDlls.Any(d => mod.ModuleName.Equals(d, StringComparison.OrdinalIgnoreCase)))
                        { hostsOurs = true; break; }
                    }
                    if (hostsOurs)
                        wudf.WaitForExit(TimeoutScale.Apply(10000));
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Terminate WUDFHost instances that are hosting ONLY our
    /// HIDMaestro driver (plus UMDF framework DLLs). Skips any WUDFHost
    /// that has another third-party driver loaded: killing those would
    /// break real devices (most notably real Bluetooth Xbox controllers
    /// hosted by microsoft.bluetooth.profiles.hidovergatt.dll).
    ///
    /// Why this is needed: pnputil /delete-driver removes the DLL from
    /// disk but doesn't unload it from running WUDFHost processes. Those
    /// keep the OLD code mapped until the process itself exits. Fresh
    /// device creation after an INF upgrade can bind a new device into
    /// an existing WUDFHost that has the stale mapping: so the new
    /// device runs old code. Killing the safe-to-terminate instances
    /// lets PnP spawn a fresh WUDFHost that loads the fresh DLL from
    /// the current DriverStore directory.</summary>
    private static void DrainOrphanedWudfHosts()
    {
        // Framework / OS modules that every WUDFHost loads by default.
        // Presence of anything OUTSIDE this set (other than our own
        // HIDMaestro.dll / HMXInput.dll) marks the WUDFHost as hosting
        // a third-party driver we must not disrupt.
        var frameworkModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] {
            "WUDFHost.exe", "WUDFPlatform.dll", "WUDFx02000.dll",
            "WUDFCoinstaller.dll", "WudfSMCClassExt.dll", "Mshidumdf.dll",
        }) frameworkModules.Add(name);

        // Our own UMDF drivers: fine to terminate a host that runs only these.
        var ourDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "HIDMaestro.dll", "HMXInput.dll", "HIDMaestroCompanion.dll",
        };

        foreach (var proc in System.Diagnostics.Process.GetProcessesByName("WUDFHost"))
        {
            try
            {
                bool hostsThirdParty = false;
                bool hostsOurs = false;

                foreach (System.Diagnostics.ProcessModule m in proc.Modules)
                {
                    string name = m.ModuleName ?? "";
                    string path = m.FileName ?? "";

                    // Framework: ignore
                    if (frameworkModules.Contains(name) ||
                        path.StartsWith(@"C:\Windows\System32\", StringComparison.OrdinalIgnoreCase) &&
                        !path.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase) &&
                        !path.Contains(@"\drivers\umdf\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Ours: note it but keep scanning (a WUDFHost with BOTH
                    // ours AND third-party is still must-not-kill).
                    if (ourDrivers.Contains(name)) { hostsOurs = true; continue; }

                    // Anything else loaded from DriverStore or the UMDF drivers
                    // dir: this is another third-party UMDF driver. Abort.
                    if (path.Contains(@"\DriverStore\FileRepository\", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains(@"\System32\drivers\umdf\", StringComparison.OrdinalIgnoreCase))
                    {
                        hostsThirdParty = true;
                        break;
                    }
                }

                if (hostsOurs && !hostsThirdParty)
                {
                    try { proc.Kill(); proc.WaitForExit(TimeoutScale.Apply(2000)); } catch { }
                }
            }
            catch { /* access denied, process exited, etc. Skip this host */ }
            finally { proc.Dispose(); }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  DriverInputReport
    // ════════════════════════════════════════════════════════════════════

    /// <summary>The input report the driver sends a data-only frame as,
    /// SubmitState's and SubmitRawReport's legacy path, and its length on
    /// the wire with the report ID byte. It is the report the SDK fills:
    /// for a keyboard or mouse that gets every report delivered, the
    /// profile's dataReportId or the descriptor's first input report
    /// (HMController.SubmitRawReport), and for everything else the report
    /// HidReportBuilder lays out, which a dataReportId also names. Both come
    /// from HidReportMap, which keeps the global item stack Push and Pop
    /// work on. A profile's inputReportSize replaces the length, and
    /// DeclaredLength stays the report's own.
    ///
    /// <para>Through v1.12.0 the SDK counted the bits of whichever report
    /// ID the descriptor named first, input or not, with no Push and Pop,
    /// and the driver sent the first Report ID item it found. A descriptor
    /// whose first report is an output report (gamecube-adapter-hid) got a
    /// 1-byte report under the output report's ID, and one whose layout
    /// report is not its first input report (the NSO controllers' 0x3F)
    /// sent the layout's bytes under 0x21.</para></summary>
    internal static (byte Id, int Length, int DeclaredLength) DriverInputReport(ControllerProfile profile)
    {
        byte id;
        int declared;
        var kbm = profile.GetOrBuildKeyboardMousePacker();
        if ((kbm.HasMouse || kbm.HasKeyboard || kbm.HasConsumer) && profile.DeliversEveryReport(kbm))
        {
            id = profile.KeyboardMouseDataReportId(kbm);
            declared = kbm.Map.Find(HidReportKind.Input, id)?.FrameBytes ?? 0;
        }
        else
        {
            var builder = profile.GetOrBuildReportBuilder();
            id = builder.InputReportId;
            declared = builder.InputReportByteSize;
        }
        int length = (profile.InputReportSize ?? 0) > 0 ? profile.InputReportSize!.Value : declared;
        return (id, length, declared);
    }

    /// <summary>The registry value InputReportId: the report ID, its
    /// length as a 32-bit little-endian number, then the report descriptor
    /// both were read from. The driver takes the ID only when the length
    /// equals InputReportByteLength and the descriptor is the one in use,
    /// so a value a killed program left under an index never pairs its ID
    /// with the length a v1.12.0 program writes there for the same
    /// descriptor (that program writes no InputReportId). Null, and no
    /// value, when the profile's inputReportSize differs from the report's
    /// own length: Sony Bluetooth profiles send their 10-byte 0x01 at the
    /// 78 bytes of 0x31, and switch-pro names 0x31's 362 for a 12-byte
    /// 0x3F. The driver then reads the ID from the descriptor as before
    /// this value existed, and no report goes out at another's
    /// length.</summary>
    internal static byte[]? InputReportIdValue(ControllerProfile profile)
    {
        var (id, length, declared) = DriverInputReport(profile);
        byte[]? descriptor = profile.GetDescriptorBytes();
        if (descriptor == null || length <= 0 || length != declared) return null;
        var value = new byte[5 + descriptor.Length];
        value[0] = id;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(1, 4), length);
        descriptor.CopyTo(value, 5);
        return value;
    }
}
