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
internal static class DeviceOrchestrator
{
    private const string REG_BASE = @"SOFTWARE\HIDMaestro";
    private static string RegPathForIndex(int index) =>
        $@"{REG_BASE}\Controller{index}";

    private static bool s_ghostsCleaned;

    /// <summary>
    /// Per-process base for SwD instance-ID suffixes. Combined with a
    /// per-creation atomic sequence number (see <see cref="NextSwdSuffix"/>)
    /// so every SwDeviceCreate call within this process gets a UNIQUE
    /// (enumerator + suffix + ContainerId) tuple. Required because Windows
    /// retains a sticky per-container record after SwDeviceClose — a
    /// subsequent SwDeviceCreate with an IDENTICAL tuple takes a fast
    /// "re-enumerate" path that leaves the devnode as an empty shell
    /// (no Service/Driver bound, no interface classes registered). The bug
    /// reproduces both across-process AND within-process: prior fix only
    /// varied across process launches, leaving same-process live-swap
    /// recreations broken on the 2nd swap onward. FindExistingCompanion
    /// matches by ControllerIndex in Device Parameters (not by suffix), so
    /// varying suffixes per call is transparent to teardown / sweep code.
    /// </summary>
    private static readonly string s_sessionId =
        System.Diagnostics.Process.GetCurrentProcess().Id.ToString("X").ToUpperInvariant();

    private static int s_swdCreateSeq;

    /// <summary>
    /// Generates a unique SwD instance-suffix for one SwDeviceCreate call.
    /// Format: "&lt;pid-hex&gt;&lt;seq-hex&gt;_&lt;ctrl-idx&gt;". Sequence number
    /// is process-scoped and atomic; suffix becomes unique even when the
    /// same controllerIndex is recreated multiple times in the same process.
    /// </summary>
    private static string NextSwdSuffix(int controllerIndex)
    {
        int seq = System.Threading.Interlocked.Increment(ref s_swdCreateSeq);
        return $"{s_sessionId}{seq:X4}_{controllerIndex:D4}";
    }

    // ════════════════════════════════════════════════════════════════════
    //  Diagnostic log for teardown investigations — gated by env var
    //  HIDMAESTRO_DIAG=1. Writes to %TEMP%\HIDMaestro\teardown_diag.log.
    //  Each TeardownController / RemoveDevice / WaitForDeviceRemoval call
    //  emits one line with timestamp + elapsed-ms + outcome. Off by default
    //  to keep production runs file-write-clean; flip the env var to repro
    //  any future "device left behind after teardown" regression.
    // ════════════════════════════════════════════════════════════════════
    private static readonly bool s_diagEnabled =
        Environment.GetEnvironmentVariable("HIDMAESTRO_DIAG") == "1";
    private static readonly object s_diagLock = new();
    private static string? s_diagPath;

    internal static void LogDiag(string message)
    {
        if (!s_diagEnabled) return;
        lock (s_diagLock)
        {
            try
            {
                if (s_diagPath == null)
                {
                    string dir = Path.Combine(Path.GetTempPath(), "HIDMaestro");
                    Directory.CreateDirectory(dir);
                    s_diagPath = Path.Combine(dir, "teardown_diag.log");
                }
                File.AppendAllText(s_diagPath!,
                    $"[{DateTime.Now:HH:mm:ss.fff}] tid={Environment.CurrentManagedThreadId,-3} {message}\n");
            }
            catch { }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Timing instrumentation — gated by env var HIDMAESTRO_TIMING=1.
    //  When enabled, each SetupController step's duration is appended to
    //  %TEMP%\HIDMaestro\setup_timing.log so we can pin regressions across
    //  runs without polluting stdout. Zero-overhead when disabled.
    // ════════════════════════════════════════════════════════════════════
    private static readonly bool s_timingEnabled =
        Environment.GetEnvironmentVariable("HIDMAESTRO_TIMING") == "1";
    private static readonly object s_timingLock = new();
    private static string? s_timingPath;

    private static void LogTiming(int ctrlIdx, string profileId, string step, long elapsedMs)
    {
        if (!s_timingEnabled) return;
        lock (s_timingLock)
        {
            try
            {
                if (s_timingPath == null)
                {
                    string dir = Path.Combine(Path.GetTempPath(), "HIDMaestro");
                    Directory.CreateDirectory(dir);
                    s_timingPath = Path.Combine(dir, "setup_timing.log");
                }
                File.AppendAllText(s_timingPath!,
                    $"[{DateTime.Now:HH:mm:ss.fff}] ctrl={ctrlIdx} profile={profileId,-24} step={step,-36} {elapsedMs,6}ms\n");
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
    //  Per-controllerIndex teardown gate — defense-in-depth coordination
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
    //    until the prior teardown's exit completes — preventing the
    //    "duplicate controller because the new bind pre-empted the old
    //    teardown" symptom.
    //
    //  Initial state of every gate is signaled (created lazily, fresh).
    //  Different controllerIndex values get separate gates — parallel
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
                // Created in signaled state — no teardown is in flight yet.
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
    /// Reset-without-Set regression — beyond which we proceed regardless
    /// and trust the primary cascade-complete wait inside
    /// TeardownController.</summary>
    private static void WaitForPriorTeardown(int controllerIndex, int timeoutMs = 120_000)
    {
        ManualResetEventSlim? ev;
        lock (s_teardownGatesLock)
        {
            s_teardownGates.TryGetValue(controllerIndex, out ev);
        }
        ev?.Wait(timeoutMs);
    }

    // ════════════════════════════════════════════════════════════════════
    //  P/Invoke: SetupAPI
    // ════════════════════════════════════════════════════════════════════

    [DllImport("SetupAPI.dll", SetLastError = true)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiCreateDeviceInfoW")]
    private static extern bool SetupDiCreateDeviceInfoW(IntPtr DeviceInfoSet, string DeviceName,
        ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, int CreationFlags, IntPtr DeviceInfoData);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    private static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        int Property, byte[] PropertyBuffer, uint PropertyBufferSize);

    [DllImport("SetupAPI.dll", SetLastError = true, EntryPoint = "SetupDiCallClassInstaller")]
    private static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, IntPtr DeviceInfoData);

    [DllImport("SetupAPI.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    // ════════════════════════════════════════════════════════════════════
    //  P/Invoke: CfgMgr32
    // ════════════════════════════════════════════════════════════════════

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Set_DevNode_PropertyW(uint dnDevInst, ref DEVPROPKEY propertyKey,
        uint propertyType, byte[] propertyBuffer, uint propertyBufferSize, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true)]
    private static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll", SetLastError = true)]
    private static extern uint CM_Get_Parent(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

    [DllImport("CfgMgr32.dll")]
    private static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);

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

    private static readonly Guid HidClassGuid = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
    private static readonly Guid SystemClassGuid = new("4D36E97D-E325-11CE-BFC1-08002BE10318");
    private static readonly Guid XusbInterfaceGuid = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");

    // ════════════════════════════════════════════════════════════════════
    //  Process helper (silent — no Console output)
    // ════════════════════════════════════════════════════════════════════

    private static (int exitCode, string output) RunProcess(string fileName, string args, int timeoutMs = 30_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName, Arguments = args,
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        // See PnputilHelper.Run: sync ReadToEnd blocks past timeoutMs when the
        // child hangs. Async read + Kill-on-timeout makes the timeout real.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.WaitForExit(5_000);
        }
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        int exitCode;
        try { exitCode = proc.ExitCode; } catch { exitCode = -1; }
        return (exitCode, stdout + stderr);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Cleanup-paths topology
    //
    //  Five distinct cleanup entry points, each with a specific scope and
    //  trigger. Reading order: triggered earliest first.
    //
    //   1. RemoveAllVirtualControllers (public)
    //        Trigger:  consumer calls explicitly (PadForge calls it at
    //                  startup AND from its ProcessExit handler)
    //        Scope:    full purge — every ROOT/SWD enumerator subtree owned
    //                  by HM, every HID orphan child, EC87F1E3 and
    //                  WinExInput interface registrations, joy.cpl OEM
    //                  cache. Uses fast=true + forceFallbacks=true so
    //                  pnputil/devcon are tried for stuck phantoms.
    //        Cost:     seconds to tens of seconds when stale state exists
    //
    //   2. CleanupGhostDevices (private)
    //        Trigger:  lazy, once per process via the s_ghostsCleaned gate
    //                  in SetupController's first invocation
    //        Scope:    similar enumerator walk to (1) plus the EC87F1E3
    //                  registry pruning. Uses fast=true with no forceFall-
    //                  backs (the post-v1.1.31 perf fix made fallbacks for
    //                  SWD\ a no-op anyway). Calls (4) and (5) below.
    //        Cost:     ~hundreds of ms on a clean machine, more if
    //                  phantoms exist
    //
    //   3. RemoveOrphanHidChildren (public, in DeviceManager)
    //        Trigger:  TeardownController calls it after each per-
    //                  controller teardown (skipped during batch dispose
    //                  via skipOrphanSweep flag, which calls (3) once
    //                  after the batch instead of N times during)
    //        Scope:    HID-class children whose parent devnode is gone.
    //                  Most common after a force-kill where SDL3/etc.
    //                  held the HID handle past the parent removal.
    //
    //   4. DisableGhostXusbInterfaces (private)
    //        Trigger:  invoked by (2)
    //        Scope:    {EC87F1E3} interface entries that are still listed
    //                  but have a gone or stuck devnode behind them.
    //
    //   5. RemoveAccumulatedHmPhantoms (internal, in DeviceManager)
    //        Trigger:  invoked by (2) [added in v1.1.32]
    //        Scope:    SetupDiGetClassDevs(DIGCF_ALLCLASSES) walk of every
    //                  HM-named devinfo record that is NOT currently
    //                  PRESENT, calls SetupDiRemoveDevice on each. This
    //                  is the only path that actually deletes the
    //                  per-instance registry hive cache for non-present
    //                  devnodes (the other paths leave hidden devices
    //                  in Device Manager).
    //
    //  Why so many paths: each fixed a different observable symptom over
    //  the v1.1.x series. (1) was the original consumer purge. (2) was
    //  added so even consumers that don't call (1) still get a clean
    //  slate. (3) was added when force-kill scenarios surfaced HID
    //  orphans that (1) and (2) missed. (4) was added when stale XUSB
    //  interface registrations occupied XInput slots even after their
    //  devnodes were gone. (5) was added in v1.1.32 to clean the
    //  registry-cache "hidden devices" accumulation that the per-call
    //  unique SwD suffix scheme inevitably produces over many sessions.
    //
    //  Consolidation tradeoff: extracting the shared enumerator walks
    //  into one helper would reduce duplication by ~40 lines but loses
    //  the per-path tuning (timeouts, fast/force flags, walk-prefix
    //  variants) that has been adjusted for specific failure modes. The
    //  current shape is verbose but each call site is self-contained;
    //  any future regression localizes cleanly.
    // ════════════════════════════════════════════════════════════════════

    private static void CleanupGhostDevices()
    {
        // Remove known non-VID_ enumerators under ROOT (legacy paths)
        string[] fixedPrefixes = { @"ROOT\HID_IG_00", @"ROOT\HIDCLASS", @"ROOT\XnaComposite",
                                   @"ROOT\HIDMAESTRO", @"SWD\HIDMAESTRO" };
        foreach (var prefix in fixedPrefixes)
        {
            for (int idx = 0; idx < 10; idx++)
            {
                string instId = $@"{prefix}\{idx:D4}";
                if (CM_Locate_DevNodeW(out _, instId, 0) == 0)
                    DeviceManager.RemoveDevice(instId, fast: true);
            }
        }

        // Enumerators we own under ROOT and SWD: legacy VID_* (ROOT) and
        // any HIDMAESTRO-prefixed SWD enumerator (gamepad companion form is
        // HIDMAESTRO_VID_<vid>_PID_<pid>&IG_00).
        foreach (var enumRoot in new[] { "ROOT", "SWD" })
        try
        {
            using var enumKey = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumRoot}");
            if (enumKey == null) continue;
            foreach (var subName in enumKey.GetSubKeyNames())
            {
                bool isVidForm = subName.StartsWith("VID_", StringComparison.OrdinalIgnoreCase);
                bool isSwdForm = subName.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);
                if (!isVidForm && !isSwdForm) continue;
                using var vidKey = enumKey.OpenSubKey(subName);
                if (vidKey == null) continue;
                foreach (var instName in vidKey.GetSubKeyNames())
                {
                    string instId = $@"{enumRoot}\{subName}\{instName}";
                    DeviceManager.RemoveDevice(instId, fast: true);
                }
            }
        }
        catch { }

        // Clean ghost XUSB and WinExInput interface registrations
        string[] interfaceGuids = {
            @"{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}",
            @"{6c53d5fd-6480-440f-b618-476750c5e1a6}",
        };
        foreach (var guid in interfaceGuids)
        {
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{guid}", writable: true);
                if (classKey == null) continue;
                foreach (var subName in classKey.GetSubKeyNames())
                {
                    // Interface-class keys encode devnode paths with `#` in place
                    // of `\`. Match our legacy ROOT\ forms and the post-SWD-migration
                    // SWD\HIDMAESTRO* + SWD\HIDMAESTRO forms.
                    bool isOurDevice =
                        subName.Contains("ROOT#VID_") ||
                        subName.Contains("ROOT#HIDCLASS") ||
                        subName.Contains("ROOT#HID_IG") ||
                        subName.Contains("ROOT#HIDMAESTRO", StringComparison.OrdinalIgnoreCase) ||
                        subName.Contains("SWD#HIDMAESTRO", StringComparison.OrdinalIgnoreCase);
                    if (!isOurDevice && subName.Contains("ROOT#SYSTEM#"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(subName, @"ROOT#SYSTEM#(\d+)");
                        if (match.Success && int.Parse(match.Groups[1].Value) >= 2)
                            isOurDevice = true;
                    }
                    if (isOurDevice)
                    {
                        try { classKey.DeleteSubKeyTree(subName); } catch { }
                    }
                }
            }
            catch { }
        }

        // Sweep accumulated HIDMaestro phantom devnodes from prior sessions.
        // v1.1.30's per-call unique SwD instance suffix prevents the
        // empty-shell bug at create time, but Windows preserves a per-
        // instance registry hive entry indefinitely after each teardown,
        // so heavy users see "hidden devices" in Device Manager pile up
        // across many sessions. SetupDiRemoveDevice via DIGCF_ALLCLASSES
        // deletes the registry cache cleanly. Only touches PHANTOM
        // entries; PRESENT devnodes are explicitly skipped, so this is
        // safe to call while a session is live.
        try { DeviceManager.RemoveAccumulatedHmPhantoms(); } catch { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  DisableGhostXusbInterfaces
    // ════════════════════════════════════════════════════════════════════

    private static void DisableGhostXusbInterfaces()
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

                bool isGhost = false;
                uint devInst = 0;
                if (CM_Locate_DevNodeW(out devInst, devPath, 0) != 0)
                {
                    if (CM_Locate_DevNodeW(out devInst, devPath, CM_LOCATE_DEVNODE_PHANTOM) == 0)
                        isGhost = true;
                    else
                        continue;
                }
                else
                {
                    if (CM_Get_DevNode_Status(out uint status, out _, devInst, 0) == 0)
                    {
                        if ((status & DN_HAS_PROBLEM) != 0 || (status & DN_STARTED) == 0)
                            isGhost = true;
                        else
                            continue;
                    }
                    else
                        isGhost = true;
                }
                if (!isGhost) continue;

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

    private static void EnsureGameInputService()
    {
        try
        {
            var (_, status) = RunProcess("sc.exe", "query GameInputSvc");
            if (!status.Contains("RUNNING"))
            {
                RunProcess("sc.exe", "config GameInputSvc start= auto");
                RunProcess("sc.exe", "start GameInputSvc");
            }
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  WriteGameInputRegistry
    // ════════════════════════════════════════════════════════════════════

    private static void WriteGameInputRegistry(ControllerProfile profile)
    {
        string deviceKey = $@"SYSTEM\CurrentControlSet\Control\GameInput\Devices\{profile.VendorId:X4}{profile.ProductId:X4}00010005";
        try { Registry.LocalMachine.DeleteSubKeyTree(deviceKey, false); } catch { }

        // Also clean up any stale entry from the earlier (now-disabled)
        // SOFTWARE path experiment so we don't leave a DS4-shaped mapping
        // for 045E VIDs that could override WGI's native handling.
        string staleSoftwareKey = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameInput\Devices\{profile.VendorId:X4}{profile.ProductId:X4}00010005";
        try { Registry.LocalMachine.DeleteSubKeyTree(staleSoftwareKey, false); } catch { }

        using var root = Registry.LocalMachine.CreateSubKey(deviceKey);

        string gpPath = $@"{deviceKey}\Gamepad";
        string[] subs = { "Menu","View","A","B","X","Y","LeftShoulder","RightShoulder",
            "LeftThumbstickButton","RightThumbstickButton",
            "DPadUp","DPadDown","DPadLeft","DPadRight",
            "LeftTrigger","RightTrigger",
            "LeftThumbstickX","LeftThumbstickY","RightThumbstickX","RightThumbstickY" };
        foreach (var sub in subs)
            Registry.LocalMachine.CreateSubKey($@"{gpPath}\{sub}");

        void SetAxis(string name, int index, bool invert = false)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{gpPath}\{name}", true)!;
            k.SetValue("AxisIndex", index, RegistryValueKind.DWord);
            if (invert) k.SetValue("Invert", 1, RegistryValueKind.DWord);
        }
        void SetButton(string name, int index)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{gpPath}\{name}", true)!;
            k.SetValue("ButtonIndex", index, RegistryValueKind.DWord);
        }
        void SetDPad(string name, string position)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"{gpPath}\{name}", true)!;
            k.SetValue("SwitchIndex", 0, RegistryValueKind.DWord);
            k.SetValue("SwitchPosition", position, RegistryValueKind.String);
            k.SetValue("IncludeAdjacent", 1, RegistryValueKind.DWord);
        }

        bool combinedTriggers = profile.HasCombinedTriggers;

        SetAxis("LeftThumbstickX", 0);
        SetAxis("LeftThumbstickY", 1, invert: true);
        SetAxis("RightThumbstickX", 2);
        SetAxis("RightThumbstickY", 3, invert: true);

        if (combinedTriggers)
        {
            SetAxis("LeftTrigger", 5);
            SetAxis("RightTrigger", 6);
        }
        else
        {
            SetAxis("LeftTrigger", 4);
            SetAxis("RightTrigger", 5);
        }

        SetButton("A", 0); SetButton("B", 1); SetButton("X", 2); SetButton("Y", 3);
        SetButton("LeftShoulder", 4); SetButton("RightShoulder", 5);
        SetButton("View", 6); SetButton("Menu", 7);
        SetButton("LeftThumbstickButton", 8); SetButton("RightThumbstickButton", 9);

        SetDPad("DPadUp", "Up"); SetDPad("DPadDown", "Down");
        SetDPad("DPadLeft", "Left"); SetDPad("DPadRight", "Right");
    }

    // ════════════════════════════════════════════════════════════════════
    //  WriteInstanceConfig
    // ════════════════════════════════════════════════════════════════════

    private static void WriteInstanceConfig(int controllerIndex, ControllerProfile profile)
    {
        byte[] descriptor = profile.GetDescriptorBytes()!;
        int inputReportLen = (profile.InputReportSize ?? 0) > 0
            ? profile.InputReportSize!.Value
            : ComputeInputReportByteLength(descriptor);

        // FunctionMode=1 for non-xinputhid Xbox (XUSB on main device),
        // FunctionMode=0 for everything else.
        bool funcMode = profile.VendorId == 0x045E && !profile.UsesUpperFilter;

        string instancePath = RegPathForIndex(controllerIndex);
        using var key = Registry.LocalMachine.CreateSubKey(instancePath);
        string instanceSuffix = controllerIndex > 0 ? $"\\{controllerIndex:D4}" : "\\0000";
        key.SetValue("DeviceInstanceId",
            $@"ROOT\VID_{profile.VendorId:X4}&PID_{profile.ProductId:X4}&IG_00{instanceSuffix}",
            RegistryValueKind.String);
        key.SetValue("FunctionMode", funcMode ? 1 : 0, RegistryValueKind.DWord);
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

        // Joystick OEM display name (HKLM only). HKCU Joystick\OEM\<VID&PID>
        // takes precedence over HKLM for joy.cpl, and Windows ships HKCU
        // preloaded for many common clone VID:PIDs. Writing HKCU from
        // DeviceOrchestrator would destructively overwrite that preload
        // without a capture-and-restore mechanism, leaving joy.cpl showing
        // the profile's ProductString forever even after the virtual is
        // disposed. Route HKCU writes exclusively through HMOemNameOverride,
        // which captures the prior value to a pending record before mutating
        // and restores on Clear / RecoverOrphans. The HKLM write here stays
        // because it's non-destructive (HKCU wins for joy.cpl anyway) and
        // some legacy MME consumers do read HKLM.
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

    // ════════════════════════════════════════════════════════════════════
    //  CreateGamepadCompanion (xinputhid path)
    // ════════════════════════════════════════════════════════════════════

    private static string? CreateGamepadCompanion(int controllerIndex, ControllerProfile profile)
    {
        string gpVid = $"{profile.VendorId:X4}";
        string gpPid = $"{profile.ProductId:X4}";
        string hwPid = profile.DriverPid != null
            ? $"{Convert.ToUInt16(profile.DriverPid, 16):X4}" : gpPid;
        string gpEnumeratorLegacy = $"VID_{gpVid}&PID_{hwPid}&IG_00";                 // ROOT\… path
        // SWD enumerator: `HIDMAESTRO_VID_<vid>_PID_<pid>&IG_00`. The only
        // load-bearing detail vs the pre-experiment `HIDMAESTRO_VID_xxx&PID_yyy&IG_00`
        // form is the single `&` → `_` swap between VID and PID. That form
        // (still matching `VID_*&PID_*&IG_*`) triggered a Windows PnP edge
        // case where SWD devices registered but never fully enumerated —
        // Status=Stopped, HID child Disconnected. Using an underscore between
        // VID and PID breaks the substring match without losing HIDMAESTRO
        // branding or the `&IG_` suffix (load-bearing for HIDAPI/SDL3
        // blocklist + xinputhid INF match).
        string gpEnumeratorSwd    = $"HIDMAESTRO_VID_{gpVid}_PID_{hwPid}&IG_00";      // SWD\… path

        // 1. Look for an existing companion claimed by THIS controllerIndex —
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
            // Per-call unique instance suffix bypasses Windows' sticky
            // per-container "recently-here" fast path that otherwise leaves
            // this devnode as an empty shell on RECREATION (across-process
            // or within-process — e.g. PadForge live-profile-swap).
            string instanceSuffix = NextSwdSuffix(controllerIndex);
            string companionDesc = profile.DeviceDescription ?? profile.ProductString ?? "HIDMaestro Gamepad";

            var result = SwdDeviceFactory.Create(
                instanceSuffix,
                hardwareIds,
                compatList.ToArray(),
                SwdDeviceFactory.ContainerIdFor(controllerIndex),
                companionDesc,
                driverRequired: true,
                enumeratorName: gpSwdEnumerator);

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
            }
        }

        // 3. Restart so it picks up latest descriptor + VID/PID, then poll
        //    for the HID child to appear (replaces a fixed 2000ms sleep).
        if (gpInstId != null)
        {
            DeviceManager.RestartDevice(gpInstId);

            // Poll for HID child PDO to be created by HIDClass below our parent.
            // On fast machines this takes <100ms; the old Thread.Sleep(2000) was
            // wasting 1900+ms on every creation.
            WaitForHidChild(gpInstId, timeoutMs: 5000);
        }

        return gpInstId;
    }

    // ════════════════════════════════════════════════════════════════════
    //  CreateXusbCompanion (non-xinputhid Xbox path)
    // ════════════════════════════════════════════════════════════════════

    private static string? CreateXusbCompanion(int controllerIndex, ControllerProfile profile)
    {
        // Look for a live HIDMAESTRO already claimed by THIS controllerIndex.
        // The "HIDMAESTRO" enumerator name is load-bearing for WGI's
        // RawGameController dedup: with any other name (HIDMAESTRO,
        // HIDMAESTRO, etc.), WGI counts the XUSB companion AND the
        // ROOT\VID_*&PID_* main device as two separate RawGameControllers
        // for the same Xbox 360 controller, even though they share a
        // ContainerID. HIDMAESTRO gets deduped; others don't. Empirical —
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
            // Per-call unique instance suffix — same rationale as the
            // gamepad-companion path: bypass the kernel's sticky per-container
            // "recently-here" fast path that leaves HIDMAESTRO devnodes as
            // empty shells (no Service/Driver, no XUSB interface registered)
            // on RECREATION (across-process or within-process live-swap).
            string instanceSuffix = NextSwdSuffix(controllerIndex);

            var result = SwdDeviceFactory.Create(
                instanceSuffix,
                hardwareIds,
                compatIds,
                SwdDeviceFactory.ContainerIdFor(controllerIndex),
                companionDesc,
                driverRequired: true,
                enumeratorName: "HIDMAESTRO");

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
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    if (DeviceManager.WaitForDeviceInterface(xusbInstId, XusbInterfaceGuid, timeoutMs: 2000))
                        break;
                    DeviceManager.RestartDevice(xusbInstId);
                }
            }
            else if (!result.Success)
            {
                // SwDeviceCreate failed — record HRESULT in a trace breadcrumb
                // so post-mortem diagnostics know why the companion didn't
                // materialize. Return null; caller's error path kicks in.
                try
                {
                    using var k = Registry.LocalMachine.CreateSubKey(RegPathForIndex(controllerIndex));
                    k?.SetValue("LastSwdCompanionHResult", result.HResult, RegistryValueKind.DWord);
                }
                catch { }
            }
        }
        else
        {
            // Existing device — kick it to pick up any changed descriptor.
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
                    if (CM_Locate_DevNodeW(out _, candidate, 0) != 0) continue;
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
    //  that already publish an HIDMAESTRO XUSB provider — so WGI still has
    //  an XUSB Gamepad source after the HID Gamepad is skipped.
    // ════════════════════════════════════════════════════════════════════

    private static void SetHidParentUpperFilterXinputhid(int controllerIndex)
    {
        // Idempotent: sweep every ROOT HID parent whose HIDMAESTRO counterpart
        // exists (same ControllerIndex registered under ROOT\HIDMAESTRO\*).
        // Writing to just the current controller's instance has a timing race
        // — Windows PnP occasionally clears properties when a sibling root
        // device is added shortly after. Sweeping every call converges the
        // state regardless of which iteration set which value.
        try
        {
            // Build a set of controllerIndexes that have an HIDMAESTRO
            // (profiles with XUSB companions — xbox-360 wired family).
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
        catch { /* best-effort — absent tripwire means 2 Gamepads, not a hard failure */ }
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

        foreach (string enumer in new[] { "HID_IG_00", "HIDClass", "XnaComposite",
            "VID_045E&PID_02FF&IG_00", "VID_045E&PID_0B13&IG_00",
            // HIDMAESTRO enumerator added per gap analysis item #6:
            // WGI may check DEVPKEY_Device_BusTypeGuid on the XUSB-interface-
            // providing devnode, expecting GUID_BUS_TYPE_USB for Xbox 360
            // hardware. HIDMAESTRO is the primary XUSB source for non-xinputhid
            // Xbox profiles; mark it as USB bus type so that check passes.
            "HIDMAESTRO" })
        {
            // Sweep both ROOT and SWD roots for this enumerator.
            foreach (var enumRoot in new[] { "ROOT", "SWD" })
            {
                for (int idx = 0; idx < 10; idx++)
                {
                    string devId = $@"{enumRoot}\{enumer}\{idx:D4}";
                    if (CM_Locate_DevNodeW(out uint devInst, devId, 0) == 0)
                    {
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

        // Also scan VID_* enumerators dynamically (under both ROOT and SWD)
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
                    bool isOurs = sub.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase)
                               || sub.Equals("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);
                    if (!isVidForm && !isOurs) continue;
                    using var vidKey = enumKey.OpenSubKey(sub);
                    if (vidKey == null) continue;
                    foreach (var inst in vidKey.GetSubKeyNames())
                    {
                        string devId = $@"{enumRootPrefix}\{sub}\{inst}";
                        if (CM_Locate_DevNodeW(out uint devInst, devId, 0) == 0)
                        {
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
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════════════
    //  SetupController — full orchestration
    // ════════════════════════════════════════════════════════════════════

    public static string? SetupController(
        int controllerIndex, ControllerProfile profile, string infPath)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (controllerIndex < 0) throw new ArgumentOutOfRangeException(nameof(controllerIndex));
        if (!profile.HasDescriptor)
            throw new ArgumentException(
                $"Profile '{profile.Id}' has no HID descriptor.", nameof(profile));

        // Defense-in-depth gate: block until any in-flight TeardownController
        // for this controllerIndex has fully completed (including
        // WaitForDeviceRemoval's CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED
        // event). PadForge's contract — "Dispose returns when teardown is
        // complete" — is enforced primarily by RemoveDevice's
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
        LogDiag($">>> SETUP  ENTER ctrl={controllerIndex} profile={profile.Id} vid=0x{profile.VendorId:X4} pid=0x{profile.ProductId:X4} uses_upper_filter={profile.UsesUpperFilter} companion_only={profile.CompanionOnly}");
        string? returnedId = null;
        try {
        using var _total = new TimingScope(controllerIndex, profile.Id, "TOTAL");

        // Snapshot XInput slot count BEFORE any setup so the post-setup wait
        // can detect the new claim. Sony / generic profiles get -1 (skip).
        int slotsBefore = (profile.VendorId == 0x045E) ? CountConnectedXInputSlots() : -1;
        bool xinputFull = slotsBefore >= 4;

        // ── Step 0: pre-flight environment ───────────────────────────────
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "0.shm"))
        {
            SharedMemoryIO.EnsureInputMapping(controllerIndex);
            try { SharedMemoryIO.EnsureOutputMapping(controllerIndex); } catch { }
        }

        // Once-per-session cleanup
        if (!s_ghostsCleaned)
        {
            var cleanupSw = Stopwatch.StartNew();
            using var _ts = new TimingScope(controllerIndex, profile.Id, "0.ghost_cleanup");
            CleanupGhostDevices();
            DisableGhostXusbInterfaces();

            // Clean HIDMaestro SYSTEM companions (never ViGEmBus/HidHide)
            for (int idx = 0; idx < 10; idx++)
            {
                string sysId = $@"ROOT\SYSTEM\{idx:D4}";
                if (CM_Locate_DevNodeW(out _, sysId, 0) == 0)
                {
                    try
                    {
                        using var key = Registry.LocalMachine.OpenSubKey(
                            $@"SYSTEM\CurrentControlSet\Enum\{sysId}");
                        if (key != null)
                        {
                            string? fn = key.GetValue("FriendlyName") as string;
                            string? dd = key.GetValue("DeviceDesc") as string;
                            string?[] hwIds = key.GetValue("HardwareID") as string[] ?? Array.Empty<string>();
                            bool isOurs = (fn != null && fn.Contains("HIDMaestro")) ||
                                          (dd != null && dd.Contains("HIDMaestro")) ||
                                          hwIds.Any(h => h != null && h.Contains("HIDMaestro"));
                            if (isOurs)
                                DeviceManager.RemoveDevice(sysId, fast: true);
                        }
                    }
                    catch { }
                }
            }

            s_ghostsCleaned = true;
            LogDiag($"    once-per-session cleanup ran in {cleanupSw.ElapsedMilliseconds}ms");
        }

        using (var _ts = new TimingScope(controllerIndex, profile.Id, "0.gameinput_svc"))
        {
            EnsureGameInputService();
            WriteGameInputRegistry(profile);
        }

        // ── Step 1: per-instance registry config ────────────────────────
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "1.instance_config"))
            WriteInstanceConfig(controllerIndex, profile);

        // ── Step 2: ensure driver is in the store ────────────────────────
        // Use IsDriverInstalled here (not unconditional FullDeploy) because
        // SetupController may be called per-controller in tight loops; the
        // strict pnputil parser is reliable so this gate is now safe. The
        // unconditional FullDeploy lives in HMContext.InstallDriver, which
        // is the canonical entry point and is called once at app startup.
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "2.driver_install_check"))
        {
            var driverSw = Stopwatch.StartNew();
            bool deployed = false;
            if (!DriverBuilder.IsDriverInstalled())
            {
                deployed = true;
                if (!DriverBuilder.FullDeploy())
                {
                    LogDiag($"    driver install FAILED (FullDeploy returned false) after {driverSw.ElapsedMilliseconds}ms");
                    throw new InvalidOperationException(
                        "Driver install failed. Run elevated and check pnputil output.");
                }
            }
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
            companionId = CreateGamepadCompanion(controllerIndex, profile);
            LogDiag($"    CreateGamepadCompanion -> {(companionId ?? "(null)")} in {createSw.ElapsedMilliseconds}ms");
        }
        else if (!profile.CompanionOnly)
        {
            // Plain HID or non-xinputhid Xbox: create main device node
            var createSw = Stopwatch.StartNew();
            using var _ts = new TimingScope(controllerIndex, profile.Id, "3.create_main_devnode");
            var result = DeviceNodeCreator.CreateDeviceNode(profile, infPath, controllerIndex);
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
        //    Old: Thread.Sleep(3000) — fixed worst-case wait for PnP async install.
        //    New: poll for the HID child PDO to appear, then finalize names.
        //    On a warm-start machine this exits in <500ms instead of 3000ms.
        {
            using var _ts = new TimingScope(controllerIndex, profile.Id, "4.wait_hid_child");
            string? parentId = mainInstanceId ?? companionId;
            if (parentId != null)
            {
                var hidSw = Stopwatch.StartNew();
                bool gotChild = WaitForHidChild(parentId, timeoutMs: 10000);
                LogDiag($"    WaitForHidChild({parentId}) -> {gotChild} in {hidSw.ElapsedMilliseconds}ms");
            }
        }
        string displayName = profile.DeviceDescription ?? profile.ProductString ?? "Controller";
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "4.fix_hid_child_names_1"))
            DeviceProperties.FixHidChildNames(displayName, controllerIndex);

        // Set names on root device — locate via Device Parameters\ControllerIndex.
        // Gamepad companion parent lives at SWD\HIDMAESTRO_VID_*_PID_*&IG_00;
        // Xbox 360 wired main device lives at ROOT\VID_*&PID_*&IG_00. Sweep
        // both roots so either path gets named.
        {
        using var _ts = new TimingScope(controllerIndex, profile.Id, "4.set_names_root");
        try
        {
            bool found = false;
            foreach (var enumRoot in new[] { "SWD", "ROOT" })
            {
                if (found) break;
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
                        int actual = (dpKey?.GetValue("ControllerIndex") is int v) ? v : 0;
                        if (actual != controllerIndex) continue;
                        string rootId = $@"{enumRoot}\{sub}\{inst}";
                        DeviceProperties.SetBusReportedDeviceDesc(rootId, displayName);
                        DeviceProperties.SetDeviceFriendlyName(rootId, displayName);
                        found = true; break;
                    }
                    if (found) break;
                }
            }
        }
        catch { }
        }

        // Final name fix — poll for the device to be fully started (DN_STARTED)
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
                    WaitForDeviceStarted(hidChildId, timeoutMs: 5000);

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
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "4.fix_hid_child_names_2"))
            DeviceProperties.FixHidChildNames(displayName, controllerIndex);

        // ── Step 5: bus type + companions ─────────────────────────────────
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "5.set_bustype_usb"))
            SetBusTypeGuidUsb();

        // Non-xinputhid Xbox: create XUSB companion for XInput.
        // HIDMAESTRO setup class changed from XnaComposite to System in
        // hidmaestro_xusb.inf so Windows.Gaming.Input.dll doesn't classify it
        // as a WGI Gamepad (would duplicate the main virtual's HID-path
        // Gamepad and hang WGI). xinput1_4 discovery still finds it via the
        // {EC87F1E3} XUSB device interface class.
        if (profile.VendorId == 0x045E && !profile.UsesUpperFilter)
        {
            string? xusbId;
            var xusbSw = Stopwatch.StartNew();
            using (var _ts = new TimingScope(controllerIndex, profile.Id, "5.create_xusb_companion"))
                xusbId = CreateXusbCompanion(controllerIndex, profile);
            LogDiag($"    CreateXusbCompanion -> {(xusbId ?? "(null)")} in {xusbSw.ElapsedMilliseconds}ms");
            if (profile.CompanionOnly && companionId == null)
                companionId = xusbId;

            // Profiles with an HIDMAESTRO XUSB companion need the HID parent
            // gated out of WGI's HidClient classifier via xinputhid tripwire
            // (see memory:project-xinputhid-upperfilter-tripwire.md). Without
            // this, WGI creates TWO Gamepads for the same virtual — one HID-
            // backed with input but no vibration channel, one XUSB-backed with
            // vibration but no input. Setting UpperFilters="xinputhid" on the
            // HID parent makes ProviderManagerWorker::OnPnpDeviceAdded skip
            // HidClient::CreateProvider, leaving the XUSB-backed Gamepad as
            // the single WGI entity.
            //
            // Must be profile-specific (per-device, not in the INF) — other
            // profiles (DualSense, Xbox Series BT, Switch Pro, etc.) have no
            // HIDMAESTRO; blocking their HID Gamepad would produce zero WGI
            // entities for them.
            using (var _ts2 = new TimingScope(controllerIndex, profile.Id, "5.hidparent_upperfilter_xinputhid"))
                SetHidParentUpperFilterXinputhid(controllerIndex);
        }

        // ── Step 6: final friendly name ──────────────────────────────────
        using (var _ts = new TimingScope(controllerIndex, profile.Id, "6.apply_friendly_name"))
            DeviceProperties.ApplyFriendlyNameForController(controllerIndex, displayName);

        // ── Step 7: wait for XInput slot claim ───────────────────────────
        // Without this, xinputhid (slow) and our XUSB companion (fast) race
        // for slot 0 and the slot order does NOT match the creation order.
        // Only Xbox-VID profiles touch XInput, so non-Xbox profiles skip the
        // wait entirely. When XInput is already full (4/4), we skip
        // gracefully — the controller is still visible via DI/HIDAPI/Browser.
        // Timeout is non-fatal: log only, never throw, to match the proven
        // pre-SDK test app behavior.
        if (slotsBefore >= 0 && !xinputFull)
        {
            using var _ts = new TimingScope(controllerIndex, profile.Id, "7.wait_xinput_slot_claim");
            var sw = Stopwatch.StartNew();
            int slotsAfter = slotsBefore;
            while (sw.ElapsedMilliseconds < 15000)
            {
                slotsAfter = CountConnectedXInputSlots();
                if (slotsAfter > slotsBefore) break;
                Thread.Sleep(100);
            }
            // Either the slot was claimed, or 15s elapsed and we move on.
            LogDiag($"    XInput slot wait: before={slotsBefore} after={slotsAfter} in {sw.ElapsedMilliseconds}ms");
        }

        // Return main instance ID, or companion ID for companion-only profiles
        returnedId = mainInstanceId ?? companionId;
        return returnedId;
        }
        finally {
            LogDiag($"<<< SETUP  EXIT  ctrl={controllerIndex} returnedId={(returnedId ?? "(null)")} total={setupTotalSw.ElapsedMilliseconds}ms");
        }
    }

    /// <summary>
    /// Polls for a HID child PDO to appear under the given parent devnode.
    /// Replaces fixed Thread.Sleep calls with an event-driven poll that exits
    /// as soon as the condition is met. On fast machines this returns in
    /// &lt;100ms; on slow machines it adapts up to the timeout.
    /// </summary>
    private static bool WaitForHidChild(string parentInstanceId, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (DeviceManager.GetHidChildId(parentInstanceId) != null)
                return true;
            Thread.Sleep(100);
        }
        return false;
    }

    /// <summary>
    /// Polls for a device to reach DN_STARTED status (driver fully bound and
    /// device functional). Replaces fixed Thread.Sleep calls that waited for
    /// xinputhid or other upper filters to finish binding.
    /// </summary>
    private static bool WaitForDeviceStarted(string instanceId, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) == 0)
            {
                if (CM_Get_DevNode_Status(out uint status, out _, devInst, 0) == 0
                    && (status & DN_STARTED) != 0)
                    return true;
            }
            Thread.Sleep(100);
        }
        return false;
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

    [System.Runtime.InteropServices.DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out _XINPUT_STATE pState);

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
    /// The sweep is idempotent but expensive — running it once at the end of
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
        // working — so any explicit per-child cleanup needs the list in
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
        // devnode FIRST leaves the SWDEVICE kernel refcount dangling — the
        // devnode goes from PnP but the kernel-side state (interface-class
        // registrations, slot-allocator entries) lingers. SwDeviceClose
        // first triggers Windows' clean cascade: devnode removal +
        // interface-class cleanup + HID children cascade-remove + Sw kernel
        // state released, in the correct order.
        //
        // On process exit, Windows handles all of this for free regardless
        // of user-mode order — that's why baseline tests (create N → exit)
        // pass even with the wrong order. Mid-process live profile swaps
        // (HMController.Dispose followed immediately by CreateController
        // for a different profile at the same ControllerIndex) require the
        // explicit ordering, otherwise the prior controller's Sw-managed
        // devnodes leave zombies that occupy XInput slots forever.
        if (!string.IsNullOrEmpty(instanceId))
        {
            // forceFallbacks: this is the live-swap teardown path. We must
            // leave nothing behind — if the old device persists as a phantom,
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
            // is a hard error mode — RemoveDevice returns with
            // goneAfterDif=false and the caller moves on while the kernel
            // is still cleaning up, breaking PadForge's "Dispose returns
            // when teardown is fully complete" contract. 120s is the
            // historical pre-regression value: generous enough that real
            // cascades always finish inside it, capped so a truly hung
            // kernel doesn't block forever.
            var parentSw = Stopwatch.StartNew();
            bool parentRemoved = false;
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
                        // 120s timeout — same generous budget as the
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
        // multiple top-level HID collections per parent — when one
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
                            // 120s timeout — same rationale as the HIDMAESTRO
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

        // Clear the per-instance registry config
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(RegPathForIndex(controllerIndex), throwOnMissingSubKey: false);
        }
        catch { }

        // Sweep any HID children orphaned by the parent removals above.
        // DeviceManager.RemoveDevice on a ROOT\ parent should cascade to its
        // HID child PDOs, but in practice on Win11 26200 the child sometimes
        // survives — detached from its removed parent, enumerable by SDL /
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
        // just-removed parents — live controllers still anchor their HID
        // children to a locatable parent and are untouched.
        if (!skipOrphanSweep)
            try { DeviceManager.RemoveOrphanHidChildren(); } catch { }

        }
        finally
        {
            // Release the gate — SetupController for this controllerIndex
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
    //  RemoveAllVirtualControllers — purge every HIDMaestro virtual device
    //  from the system, including orphans from prior runs. Used by the
    //  "cleanup" CLI command and by consumers who want a clean slate.
    // ════════════════════════════════════════════════════════════════════

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenEventW(uint dwDesiredAccess, bool bInheritHandle, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private const uint EVENT_MODIFY_STATE = 0x0002;

    /// <summary>
    /// Signal every named HIDMaestro StopEvent (slots 0..15), then sleep
    /// 500ms to give blocked WUDFHost worker threads a chance to exit.
    /// Without this, after a force-kill the workers stay blocked on
    /// WaitForMultipleObjects(StopEvent, InputDataEvent) and PnP removal
    /// blocks ~1s/device waiting for the kernel query-remove to time
    /// out. With the signal: workers exit, WUDFHost releases, subsequent
    /// DIF_REMOVE completes in milliseconds. For graceful shutdown the
    /// signal makes cleanup near-instant.
    /// </summary>
    private static void SignalStopEventsAndDrain()
    {
        for (int i = 0; i < 16; i++)
        {
            IntPtr ev = OpenEventW(EVENT_MODIFY_STATE, false, $@"Global\HIDMaestroStopEvent{i}");
            if (ev != IntPtr.Zero)
            {
                SetEvent(ev);
                CloseHandle(ev);
            }
        }
        Thread.Sleep(500);
    }

    public static void RemoveAllVirtualControllers()
    {
        SignalStopEventsAndDrain();

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
                bool alwaysOurs = sub.StartsWith("VID_", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("XnaComposite", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("HIDMAESTRO", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("HID_IG_00", StringComparison.OrdinalIgnoreCase)
                    || sub.StartsWith("HIDMAESTRO", StringComparison.OrdinalIgnoreCase);

                bool shared = sub.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase)
                    || sub.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase);

                if (!alwaysOurs && !shared) continue;

                using var subKey = enumKey.OpenSubKey(sub);
                if (subKey == null) continue;

                foreach (var inst in subKey.GetSubKeyNames())
                {
                    string instId = $@"{enumRoot}\{sub}\{inst}";

                    if (alwaysOurs)
                    {
                        DeviceManager.RemoveDevice(instId, timeoutMs: 5000, fast: true, forceFallbacks: true);
                        continue;
                    }

                    // Shared: only remove if hardware ID contains "HIDMaestro"
                    try
                    {
                        using var devKey = subKey.OpenSubKey(inst);
                        var hwIds = devKey?.GetValue("HardwareID") as string[];
                        if (hwIds?.Any(h => h.Contains("HIDMaestro", StringComparison.OrdinalIgnoreCase)) == true)
                            DeviceManager.RemoveDevice(instId, timeoutMs: 3000, fast: true, forceFallbacks: true);
                    }
                    catch { }
                }
            }
        }
        catch { }

        // Remove orphaned HID children (survive parent removal as "Unknown").
        try
        {
            using var hidEnum = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\HID");
            if (hidEnum != null)
            {
                foreach (var sub in hidEnum.GetSubKeyNames())
                {
                    bool couldBeOurs = sub.StartsWith("VID_045E", StringComparison.OrdinalIgnoreCase)
                        || sub.Equals("HIDCLASS", StringComparison.OrdinalIgnoreCase)
                        || sub.StartsWith("HID_IG", StringComparison.OrdinalIgnoreCase);
                    if (!couldBeOurs) continue;

                    using var childEnum = hidEnum.OpenSubKey(sub);
                    if (childEnum == null) continue;
                    foreach (var inst in childEnum.GetSubKeyNames())
                    {
                        string childId = $@"HID\{sub}\{inst}";
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
                            // ones — yielding "driver upgraded but behavior
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

        // Clean Device Parameters under our enumerators via reg.exe (PnP ACLs
        // prevent direct writes). Leaves the PnP instance keys themselves intact.
        // Sweeps both ROOT\ and SWD\ subtrees.
        {
            string[] ourEnumerators = { "VID_", "XnaComposite", "HIDMAESTRO", "HID_IG_00", "HIDMAESTRO" };
            foreach (var enumRootName in new[] { "ROOT", "SWD" })
            try
            {
                using var enumRoot = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumRootName}");
                if (enumRoot == null) continue;
                foreach (var sub in enumRoot.GetSubKeyNames())
                {
                    bool ours = ourEnumerators.Any(e =>
                        sub.StartsWith(e, StringComparison.OrdinalIgnoreCase)
                        || sub.Equals(e, StringComparison.OrdinalIgnoreCase));
                    if (!ours) continue;
                    using var subKey = enumRoot.OpenSubKey(sub);
                    if (subKey == null) continue;
                    foreach (var inst in subKey.GetSubKeyNames())
                    {
                        string dpPath = $@"SYSTEM\CurrentControlSet\Enum\{enumRootName}\{sub}\{inst}\Device Parameters";
                        RunProcess("reg.exe", $"delete \"HKLM\\{dpPath}\" /f", timeoutMs: 3000);
                    }
                }
            }
            catch { }
        }

        // Clean interface registries (XUSB + WinExInput).
        // Match both ROOT# and SWD# instance-name encodings (backslash → hash in
        // interface-class registry keys).
        foreach (var guid in new[] {
            "{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}",
            "{6c53d5fd-6480-440f-b618-476750c5e1a6}" })
        {
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{guid}", writable: true);
                if (classKey != null)
                    foreach (var sub in classKey.GetSubKeyNames())
                        if (sub.Contains("ROOT#") ||
                            sub.Contains("SWD#HIDMAESTRO", StringComparison.OrdinalIgnoreCase))
                            try { classKey.DeleteSubKeyTree(sub); } catch { }
            }
            catch { }
        }

        // Clean joy.cpl joystick OEM cache + slot assignments
        string[] oemPrefixes = { "VID_045E&PID_", "VID_054C&PID_", "VID_0000&PID_" };
        string oemRelPath = @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var oemKey = root.OpenSubKey(oemRelPath, writable: true);
                if (oemKey != null)
                    foreach (var sub in oemKey.GetSubKeyNames())
                        if (oemPrefixes.Any(p => sub.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                            try { oemKey.DeleteSubKeyTree(sub, false); } catch { }
            }
            catch { }
        }
        try
        {
            using var jsRoot = Registry.CurrentUser.OpenSubKey(
                @"System\CurrentControlSet\Control\MediaResources\Joystick", writable: true);
            if (jsRoot != null)
                foreach (var sub in jsRoot.GetSubKeyNames())
                {
                    try
                    {
                        using var settings = jsRoot.OpenSubKey($@"{sub}\CurrentJoystickSettings", writable: true);
                        if (settings != null)
                            foreach (var name in settings.GetValueNames())
                            {
                                if (name.StartsWith("Joystick", StringComparison.OrdinalIgnoreCase) &&
                                    (name.Contains("OEMName", StringComparison.OrdinalIgnoreCase) ||
                                     name.Contains("Configuration", StringComparison.OrdinalIgnoreCase)))
                                    settings.DeleteValue(name, false);
                            }
                    }
                    catch { }
                    try
                    {
                        using var jsSettings = jsRoot.OpenSubKey($@"{sub}\JoystickSettings", writable: true);
                        if (jsSettings != null)
                            foreach (var vidPid in jsSettings.GetSubKeyNames())
                            {
                                try
                                {
                                    using var vidPidKey = jsSettings.OpenSubKey(vidPid, writable: true);
                                    if (vidPidKey == null) continue;
                                    foreach (var name in vidPidKey.GetValueNames())
                                    {
                                        if (name.StartsWith("Joystick", StringComparison.OrdinalIgnoreCase) &&
                                            (name.Contains("OEMName", StringComparison.OrdinalIgnoreCase) ||
                                             name.Contains("Configuration", StringComparison.OrdinalIgnoreCase)))
                                            vidPidKey.DeleteValue(name, false);
                                    }
                                }
                                catch { }
                            }
                    }
                    catch { }
                }
        }
        catch { }

        // Wait for WUDFHost processes to release our DLLs.
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
                        wudf.WaitForExit(10000);
                }
                catch { }
            }
        }
        catch { }

        // Remove driver packages from store. Strict, retry-aware, verifies
        // afterward — see PnputilHelper. Catch the verification throw here
        // because cleanup is best-effort: leaving a stale package is bad
        // (it'll bite the next install) but it shouldn't kill the cleanup
        // path entirely. The next FullDeploy will try again, and FullDeploy
        // does NOT swallow this exception.
        try { PnputilHelper.RemoveAllHidMaestroPackages(); } catch { }

        // Clear registry config
        try { Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\HIDMaestro", false); } catch { }

        // Release shared-memory mappings
        try { SharedMemoryIO.Cleanup(); } catch { }

        // Drain orphaned WUDFHost processes that STILL have our stale
        // HIDMaestro.dll memory-mapped from a since-deleted DriverStore
        // directory. This is what bit us for hours: pnputil /delete-driver
        // removes the file on disk, but any WUDFHost process that had the
        // DLL mapped keeps the OLD code resident in virtual memory. When a
        // fresh device is created AFTER a driver upgrade, PnP can assign
        // the new device to an existing WUDFHost (they're reused across
        // devices of the same UMDF class) — and then the new device runs
        // the OLD driver bytes while the disk has NEW ones. That's the
        // "driver upgraded but behavior unchanged" mystery.
        //
        // Termination is SAFE because we inspect the target process's
        // loaded modules first: we only kill WUDFHost instances that host
        // NOTHING but our HIDMaestro.dll / HMXInput.dll (plus framework
        // DLLs like WUDFx02000.dll, WUDFPlatform.dll). Any WUDFHost that
        // hosts ANOTHER third-party UMDF driver (e.g.
        // microsoft.bluetooth.profiles.hidovergatt.dll for a real BT Xbox
        // controller) is skipped entirely — that's what the NEVER-kill-
        // WUDFHost rule in feedback-never-kill-wudfhost.md is about.
        DrainOrphanedWudfHosts();
    }

    /// <summary>Terminate WUDFHost instances that are hosting ONLY our
    /// HIDMaestro driver (plus UMDF framework DLLs). Skips any WUDFHost
    /// that has another third-party driver loaded — killing those would
    /// break real devices (most notably real Bluetooth Xbox controllers
    /// hosted by microsoft.bluetooth.profiles.hidovergatt.dll).
    ///
    /// Why this is needed: pnputil /delete-driver removes the DLL from
    /// disk but doesn't unload it from running WUDFHost processes. Those
    /// keep the OLD code mapped until the process itself exits. Fresh
    /// device creation after an INF upgrade can bind a new device into
    /// an existing WUDFHost that has the stale mapping — so the new
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

        // Our own UMDF drivers — fine to terminate a host that runs only these.
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

                    // Framework — ignore
                    if (frameworkModules.Contains(name) ||
                        path.StartsWith(@"C:\Windows\System32\", StringComparison.OrdinalIgnoreCase) &&
                        !path.Contains(@"\DriverStore\", StringComparison.OrdinalIgnoreCase) &&
                        !path.Contains(@"\drivers\umdf\", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Ours — note it but keep scanning (a WUDFHost with BOTH
                    // ours AND third-party is still must-not-kill).
                    if (ourDrivers.Contains(name)) { hostsOurs = true; continue; }

                    // Anything else loaded from DriverStore or the UMDF drivers
                    // dir — this is another third-party UMDF driver. Abort.
                    if (path.Contains(@"\DriverStore\FileRepository\", StringComparison.OrdinalIgnoreCase) ||
                        path.Contains(@"\System32\drivers\umdf\", StringComparison.OrdinalIgnoreCase))
                    {
                        hostsThirdParty = true;
                        break;
                    }
                }

                if (hostsOurs && !hostsThirdParty)
                {
                    try { proc.Kill(); proc.WaitForExit(2000); } catch { }
                }
            }
            catch { /* access denied, process exited, etc. — skip this host */ }
            finally { proc.Dispose(); }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  ComputeInputReportByteLength
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Compute the input report byte length from a HID descriptor.
    /// Ported verbatim from the proven pre-SDK test app's implementation.
    /// Critically: handles multi-Report-ID descriptors (e.g. dualsense which
    /// declares Report IDs 1, 2, 3, ...) by counting bits ONLY for the first
    /// encountered Report ID — that's the input report we use. Adds +1 to
    /// the byte total for descriptors that have any Report ID, accounting
    /// for the prefix byte the kernel HID stack adds when delivering reports.
    /// </summary>
    private static int ComputeInputReportByteLength(byte[] desc)
    {
        int totalBits = 0;
        int reportSize = 0;
        int reportCount = 0;
        int currentReportId = 0;
        int firstInputReportId = 0;
        bool hasReportIds = false;

        for (int i = 0; i < desc.Length;)
        {
            byte prefix = desc[i];
            int bSize = prefix & 0x03;
            if (bSize == 3) bSize = 4;
            int bType = (prefix >> 2) & 0x03;
            int bTag = (prefix >> 4) & 0x0F;

            int value = 0;
            if (i + bSize < desc.Length)
            {
                for (int j = 0; j < bSize; j++)
                    value |= desc[i + 1 + j] << (8 * j);
            }

            if (bType == 1) // Global
            {
                if (bTag == 7) reportSize = value;     // Report Size
                if (bTag == 9) reportCount = value;    // Report Count
                if (bTag == 8)                          // Report ID
                {
                    currentReportId = value;
                    if (!hasReportIds) firstInputReportId = value;
                    hasReportIds = true;
                }
            }
            else if (bType == 0) // Main
            {
                if (bTag == 8) // Input
                {
                    // Count bits for the first report ID encountered (or all
                    // bits if the descriptor doesn't use Report IDs at all).
                    if (!hasReportIds || currentReportId == firstInputReportId)
                        totalBits += reportSize * reportCount;
                }
            }

            i += 1 + bSize;
        }

        int totalBytes = (totalBits + 7) / 8;
        return hasReportIds ? totalBytes + 1 : totalBytes; // +1 for Report ID byte
    }
}
