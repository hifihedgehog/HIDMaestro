using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace HIDMaestro.Internal;

/// <summary>
/// Creates SWD-enumerated (software-device) PnP devices via the <c>hmswd.exe</c>
/// helper process: which wraps <c>cfgmgr32!SwDeviceCreate</c> in native C.
///
/// <para><b>Why an out-of-process helper:</b> on .NET 10 / Win11 26200,
/// direct P/Invoke to <c>SwDeviceCreate</c> returns
/// <c>hr = 0x8007007E (ERROR_MOD_NOT_FOUND)</c> synchronously, even though
/// the identical C call succeeds. CoInitialize, apartment changes, delegate-
/// vs-function-pointer marshaling, <c>UnmanagedCallersOnly</c>, and DLL
/// preloading were all tried and did NOT resolve it. Rather than block
/// the slot-1-skip fix on root-causing a .NET runtime marshaling bug,
/// the SDK shells out to a 200-line native helper that performs the same
/// call from C (where it works) and prints the resulting instance ID.</para>
///
/// <para><b>Why this migration matters:</b> xinput1_4.dll's slot allocator
/// reads <c>DEVPKEY_Device_ContainerId</c> on any XUSB-interface device it
/// enumerates. A null-sentinel container triggers a "this is an embedded/
/// primary device" branch that sets bit 2 on the allocator's device struct,
/// which (a) skips internal slot 0 during fallback allocation and (b) marks
/// the device for a primary-swap that makes slot 1 present as empty. Giving
/// our virtuals a real ContainerId via SwDeviceCreate's explicit
/// <c>pContainerId</c> bypasses both. See memory:
/// project-slot-1-skip-swd-migration-plan.md.</para>
///
/// <para><b>Device identity:</b> none of the properties consumers care about
/// are affected by the ROOT→SWD switch. HardwareIds, CompatibleIds, VID/PID,
/// HID descriptor, interface GUIDs all pass through unchanged: only the
/// InstanceId path prefix becomes <c>SWD\HIDMAESTRO\&lt;instance&gt;</c>.</para>
///
/// <para><b>Lifetime:</b> <c>hmswd.exe</c> sets
/// <c>SWDeviceLifetimeParentPresent</c> so devices persist past the helper
/// process's exit. Cleanup is via standard PnP DIF_REMOVE (which the
/// existing HIDMaestro teardown paths already do).</para>
/// </summary>
internal static class SwdDeviceFactory
{
    /// <summary>Default enumerator name. Callers can override per device
    /// kind. For Xbox Series BT gamepad companions we pass
    /// <c>"VID_xxxx&amp;PID_yyyy&amp;IG_00"</c> so the HID child PDO also
    /// inherits that name in its instance ID: matching the pattern that
    /// xinputhid's INF selects against.</summary>
    public const string DefaultEnumeratorName = "HIDMAESTRO";

    /// <summary>The helper's file name, staged from
    /// <c>HIDMaestro.Native.&lt;arch&gt;.hmswd.exe</c>.</summary>
    internal const string HelperFile = "hmswd.exe";

    /// <summary>How long hmswd.exe waits for Plug and Play to report the
    /// device enumerated, by its creation callback or, for a create, a
    /// started devnode, before it closes the handle and exits with code 3:
    /// 600 polls of 100 ms (driver\hmswd\hmswd.c, the wait loop create and
    /// remove share). The helper reads no timeout scale.</summary>
    internal const int HelperWaitMs = 60_000;

    /// <summary>The SDK's bound on one run of hmswd.exe up to the start of
    /// its close, its STEP CLOSING line, before scaling: the helper's own
    /// wait and 30 s for its start and its SwDeviceCreate call.
    /// SwDeviceCreate documents no bound of its own, and a slow Plug and Play
    /// queue delays the helper's start and its callback alike, so the SDK
    /// waits for the helper's answer and stops it only past this bound.
    /// Nothing retries a run. A timeout scale below 1 brings the bound under
    /// the helper's own wait.</summary>
    internal const int HelperBoundMs = HelperWaitMs + 30_000;

    /// <summary>How long the SDK waits for hmswd.exe's SwDeviceClose once
    /// the helper has said it began it (STEP CLOSING), before scaling. The
    /// device's outcome is settled by then: a created device's lifetime is
    /// ParentPresent and a removed one's Handle, and stopping the helper
    /// closes the handle as SwDeviceClose does. In issue #18 a remove's
    /// helper ran past the old 30 s bound three times with its device already
    /// gone, and v1.3.0 cut that bound to these 8 s for the whole run.</summary>
    internal const int HelperCloseMs = 8_000;

    /// <summary>HRESULT_FROM_WIN32(WAIT_TIMEOUT): the helper's wait or the
    /// SDK's bound ran out.</summary>
    internal const int WaitTimeoutHr = unchecked((int)0x80070102);

    const int E_FAIL       = unchecked((int)0x80004005);
    const int E_INVALIDARG = unchecked((int)0x80070057);

    /// <summary>The program a create or a remove runs in place of hmswd.exe,
    /// unstaged and unpinned. Probe swd_bound_check sets it to a stand-in
    /// that answers as hmswd.exe does, at a pace of its choosing. Null
    /// outside the probe.</summary>
    internal static string? HelperPathForTest;

    public readonly struct Result
    {
        public readonly bool    Success;
        public readonly string? InstanceId;       // e.g. "SWD\\HIDMAESTRO\\HMC_0001"
        public readonly int     HResult;
        /// <summary>What ended a failed create, in the words the create's
        /// exception carries, or null on success.</summary>
        internal readonly string? Cause;

        public Result(bool success, string? instanceId, int hr) : this(success, instanceId, hr, null) { }

        internal Result(bool success, string? instanceId, int hr, string? cause)
        {
            Success = success; InstanceId = instanceId; HResult = hr; Cause = cause;
        }
    }

    /// <summary>How a run of hmswd.exe ended.</summary>
    internal enum HelperEnd
    {
        /// <summary>It exited on its own.</summary>
        Exited,
        /// <summary>It had not begun its close at the SDK's bound and was
        /// stopped.</summary>
        StoppedAtBound,
        /// <summary>Its SwDeviceClose had not returned after
        /// <see cref="HelperCloseMs"/> and it was stopped.</summary>
        StoppedInClose,
        /// <summary>It could not be started.</summary>
        NotStarted,
    }

    /// <summary>One run of hmswd.exe as the SDK saw it: how it ended, its
    /// exit code when it exited, what it printed, the last step it reached
    /// (the text after "STEP "), and the exit code its STEP CLOSING line
    /// announced. A run that did not start carries its HRESULT and the
    /// reason.</summary>
    internal readonly record struct HelperRun(HelperEnd End, int ExitCode, string Stdout, string Stderr,
                                              string? LastStep, int? ClosingCode, int StartHr = 0, string? StartError = null);

    /// <summary>Build a per-controller deterministic ContainerId GUID.
    /// Shared between the main HID device and any companion devices for the
    /// same controller so PnP / Windows Settings group them as one physical
    /// controller (per "How Container IDs are Generated" docs: "all devnodes
    /// that belong to a container on a given bus type must share the same
    /// ContainerID value").</summary>
    /// <remarks>
    /// Encoding: <c>{48494430-4D41-4553-5452-4F000000&lt;idx:X4&gt;}</c> =
    /// ASCII "HIDMAESTRO" + 16-bit index. Non-sentinel, deterministic,
    /// per-controller unique up to 65535 controllers.
    /// </remarks>
    public static Guid ContainerIdFor(int controllerIndex)
    {
        byte[] d4 =
        {
            0x54, 0x52, 0x4F, 0x00,            // "TRO\0"
            0x00, 0x00,                         // reserved
            (byte)((controllerIndex >> 8) & 0xFF),
            (byte)(controllerIndex & 0xFF),
        };
        return new Guid(0x48494430, 0x4D41, 0x4553, d4);
    }

    /// <summary>Create an SWD-enumerated device with the given properties
    /// via <c>hmswd.exe</c>, which asks for a driver (DriverRequired). Blocks
    /// until the helper returns, the device enumerated and its driver bound,
    /// or until the helper's bounds have passed (<see cref="Run"/>), and
    /// returns its InstanceId or what failed.</summary>
    /// <param name="instanceIdSuffix">Appended after the enumerator
    /// becomes the full instance-id's last segment. Must be unique
    /// across live devices under this enumerator.</param>
    /// <param name="hardwareIds">List of hardware IDs. INFs match by
    /// string on these.</param>
    /// <param name="compatibleIds">List of compatible IDs, or empty.</param>
    /// <param name="containerId">Non-sentinel container GUID. Use
    /// <see cref="ContainerIdFor"/> for the HIDMaestro convention.</param>
    /// <param name="deviceDescription">Human-readable description.</param>
    public static Result Create(
        string instanceIdSuffix,
        string[] hardwareIds,
        string[] compatibleIds,
        Guid containerId,
        string deviceDescription,
        string? enumeratorName = null)
    {
        // Held until the helper has exited. A helper that cannot be staged
        // or held throws, with the reason, out to the create
        // (HMContext.CreateController).
        using var pin = PinForRun(out string helperPath);

        // hmswd expects '|' as the list separator.
        string hwList     = string.Join('|', hardwareIds ?? Array.Empty<string>());
        string compatList = string.Join('|', compatibleIds ?? Array.Empty<string>());
        string guidStr    = containerId.ToString("B");
        string enumName   = enumeratorName ?? DefaultEnumeratorName;

        var psi = HelperStartInfo(helperPath, "create", enumName, instanceIdSuffix, guidStr, hwList, compatList,
                                  deviceDescription ?? "HIDMaestro Device");

        // One run of the helper. With DriverRequired its callback waits for
        // the driver's install, which a slow Plug and Play queue delays. In a
        // run on 2026-10-26, driver installs and driver-host starts waited
        // behind a machine-wide image-load lock for longer than the three
        // 15 s attempts this replaced. Stopping the helper closes the device
        // it made, and a second helper's create queues behind that removal
        // (SwDeviceClose), so the create waits for the helper's own answer.
        int boundMs = TimeoutScale.Apply(HelperBoundMs);
        string device = $@"{enumName}\{instanceIdSuffix}";
        return CreateOnce($@"SWD\{device}", boundMs, () => CreateAnswer(Run(psi, "create", device, boundMs), boundMs));
    }

    /// <summary>The helper's start: no window, its output read, and its
    /// working directory, TEMP and TMP its own protected staging
    /// directory, never one a non-administrator can write.</summary>
    private static ProcessStartInfo HelperStartInfo(string helperPath, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName  = helperPath,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
            WorkingDirectory       = Path.GetDirectoryName(helperPath)!,
        };
        ProtectedStaging.ConfineChildTemp(psi, Path.GetDirectoryName(helperPath)!);
        foreach (string a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    /// <summary>Run hmswd.exe and wait for it as far as its steps allow: up
    /// to <paramref name="boundMs"/> (already scaled) until it exits or
    /// prints STEP CLOSING, then up to <see cref="HelperCloseMs"/> (scaled)
    /// for the close it has begun. A helper still running at either end is
    /// stopped with its process tree. Its lines go to the diagnostic log,
    /// which is written as the user (DeviceOrchestrator.LogDiag). Until
    /// v2.0.0 they went to a file of their own under %TEMP%\HIDMaestro,
    /// written elevated into a folder the user's programs can change.</summary>
    internal static HelperRun Run(ProcessStartInfo psi, string verb, string device, int boundMs)
    {
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception e)
        {
            int hr = e is System.ComponentModel.Win32Exception w
                ? unchecked((int)(0x80070000u | ((uint)w.NativeErrorCode & 0xFFFFu)))
                : e.HResult;
            return new HelperRun(HelperEnd.NotStarted, 0, "", "", null, null, hr, e.Message);
        }
        if (proc == null) return new HelperRun(HelperEnd.NotStarted, 0, "", "", null, null, E_FAIL, "it did not start");
        using (proc)
        {
            var stdout = new StringBuilder();
            string? last = null;
            int? closing = null;
            using var closingSeen = new ManualResetEvent(false);
            using var outputEnded = new ManualResetEvent(false);
            // The reader's callbacks run on the thread pool, and one can come
            // after this run has returned and disposed the events: it then
            // signals nothing.
            static void Signal(ManualResetEvent ev)
            {
                try { ev.Set(); } catch (ObjectDisposedException) { }
            }
            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not string line)
                {
                    Signal(outputEnded);
                    return;
                }
                lock (stdout)
                {
                    stdout.AppendLine(line);
                    if (!line.StartsWith("STEP ", StringComparison.Ordinal)) return;
                    last = line.Substring(5).Trim();
                    if (last.StartsWith("CLOSING ", StringComparison.Ordinal)
                        && int.TryParse(last.AsSpan(8), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code))
                    {
                        closing = code;
                        Signal(closingSeen);
                    }
                }
            };
            // Read as the helper writes, so neither pipe fills.
            var stderrRead = proc.StandardError.ReadToEndAsync();
            proc.BeginOutputReadLine();
            using var exitedHandle = new ProcessExit(proc);
            var end = HelperEnd.Exited;
            int first = WaitHandle.WaitAny(new WaitHandle[] { exitedHandle, closingSeen }, boundMs);
            if (first != 0 && closingSeen.WaitOne(0))
            {
                if (!exitedHandle.WaitOne(TimeoutScale.Apply(HelperCloseMs))) end = HelperEnd.StoppedInClose;
            }
            else if (first == WaitHandle.WaitTimeout)
            {
                end = HelperEnd.StoppedAtBound;
            }
            if (end != HelperEnd.Exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                exitedHandle.WaitOne(TimeoutScale.Apply(5_000));
            }
            // Bounded after the exit, as PnputilHelper.Drain bounds its reads.
            outputEnded.WaitOne(TimeoutScale.Apply(5_000));
            try { stderrRead.Wait(TimeoutScale.Apply(5_000)); } catch { }
            string err = stderrRead.IsCompletedSuccessfully ? stderrRead.Result : "";
            string output;
            string? lastStep;
            int? closingCode;
            lock (stdout) { output = stdout.ToString(); lastStep = last; closingCode = closing; }
            int exitCode = end == HelperEnd.Exited ? proc.ExitCode : 0;
            DeviceOrchestrator.LogDiag(
                $"      hmswd {verb} {device}: " +
                (end == HelperEnd.Exited ? $"exit={exitCode}" : end == HelperEnd.StoppedAtBound
                    ? $"stopped at the SDK's bound of {Seconds(boundMs)}"
                    : $"stopped in its close after {Seconds(TimeoutScale.Apply(HelperCloseMs))}") +
                $" stdout=[{OneLine(output)}] stderr=[{OneLine(err)}]");
            return new HelperRun(end, exitCode, output, err, lastStep, closingCode);
        }
    }

    /// <summary>A process's own handle as a wait handle, signaled at its
    /// exit whatever its redirected output does. The process keeps the
    /// handle.</summary>
    private sealed class ProcessExit : WaitHandle
    {
        internal ProcessExit(Process process) =>
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process.Handle, ownsHandle: false);
    }

    static string OneLine(string text) => text.Trim().Replace("\r", "").Replace("\n", " | ");

    /// <summary>Where a run that had not begun its close stood, from the
    /// last step it printed (driver\hmswd\hmswd.c, "Steps").</summary>
    internal static string Where(string? lastStep) => lastStep switch
    {
        null => "before it reached SwDeviceCreate",
        "STARTED" => "inside SwDeviceCreate",
        "CREATED" => "waiting for Plug and Play to report the device enumerated",
        "ENUMERATED" => "setting the device's lifetime",
        "LIFETIME HANDLE" => "after setting the device's lifetime to Handle",
        _ => $"after its step {lastStep}",
    };

    /// <summary>A create's answer from its run (<see cref="Run"/>). A run
    /// stopped in its close is read by the exit code its STEP CLOSING line
    /// announced: after "OK" the device's lifetime is ParentPresent, so the
    /// create succeeded.</summary>
    internal static Result CreateAnswer(HelperRun run, int boundMs)
    {
        switch (run.End)
        {
            case HelperEnd.NotStarted:
                return new Result(false, null, run.StartHr, $"hmswd.exe could not be run: {run.StartError}");
            case HelperEnd.StoppedAtBound:
                return new Result(false, null, WaitTimeoutHr,
                    $"hmswd.exe was still running, {Where(run.LastStep)}, when the SDK's bound of {Seconds(boundMs)} ran out, " +
                    $"and was stopped (its own wait for Plug and Play is {Seconds(HelperWaitMs)}).");
            case HelperEnd.StoppedInClose:
                var answer = ReadAnswer(run.ClosingCode ?? E_FAIL, run.Stdout, run.Stderr);
                return answer.Success ? answer : new Result(false, null, answer.HResult,
                    $"{answer.Cause} Its SwDeviceClose had not returned after {Seconds(TimeoutScale.Apply(HelperCloseMs))}, and it was stopped.");
            default:
                return ReadAnswer(run.ExitCode, run.Stdout, run.Stderr);
        }
    }

    /// <summary>What an hmswd.exe create's exit code and output say
    /// (driver\hmswd\hmswd.c): exit 0 with "OK &lt;instance id&gt;", 1 for
    /// arguments it refused, 2 when SwDeviceCreate failed, 3 when its own
    /// wait for Plug and Play ran out, 4 when the creation callback reported
    /// a failure, 5 when SwDeviceSetLifetime failed. Exits 2, 4 and 5 print
    /// the HRESULT, which the result carries.</summary>
    internal static Result ReadAnswer(int exitCode, string stdout, string stderr)
    {
        string err = stderr.Trim();
        switch (exitCode)
        {
            case 0:
                foreach (string line in stdout.Split('\n'))
                {
                    string l = line.Trim();
                    if (l.StartsWith("OK ", StringComparison.Ordinal))
                        return new Result(true, l.Substring(3).Trim(), 0);
                }
                return new Result(false, null, E_FAIL, $"hmswd.exe exited with code 0 but printed no instance id ({Shown(OneLine(stdout))}).");
            case 1:
                return new Result(false, null, E_INVALIDARG, $"hmswd.exe refused its arguments, exit code 1 ({Shown(err)}).");
            case 2:
                return ParsedHr(err) is int created
                    ? new Result(false, null, created, $"SwDeviceCreate failed with 0x{created:X8} (hmswd.exe exit code 2).")
                    : new Result(false, null, E_FAIL, $"SwDeviceCreate failed (hmswd.exe exit code 2, {Shown(err)}).");
            case 3:
                return new Result(false, null, WaitTimeoutHr,
                    $"Plug and Play did not report the device enumerated within hmswd.exe's own wait of {Seconds(HelperWaitMs)}, " +
                    "and hmswd.exe closed it (exit code 3).");
            case 4:
                return ParsedHr(err) is int callback
                    ? new Result(false, null, callback, $"Plug and Play reported the device's creation failed with 0x{callback:X8} (hmswd.exe exit code 4).")
                    : new Result(false, null, E_FAIL, $"Plug and Play reported the device's creation failed (hmswd.exe exit code 4, {Shown(err)}).");
            case 5:
                return ParsedHr(err) is int lifetime
                    ? new Result(false, null, lifetime, $"SwDeviceSetLifetime failed with 0x{lifetime:X8}, and hmswd.exe closed the device (exit code 5).")
                    : new Result(false, null, E_FAIL, $"SwDeviceSetLifetime failed, and hmswd.exe closed the device (exit code 5, {Shown(err)}).");
            default:
                return new Result(false, null, E_FAIL, $"hmswd.exe exited with code 0x{exitCode:X8} ({Shown(err)}).");
        }
    }

    static string Shown(string text) => text.Length == 0 ? "no output" : $"\"{text}\"";

    /// <summary>The first <c>0x</c> hex number in the helper's error output,
    /// where hmswd.exe prints the HRESULT of exits 2, 4 and 5.</summary>
    private static int? ParsedHr(string stderr)
    {
        int hexIdx = stderr.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (hexIdx < 0) return null;
        var slice = stderr.Substring(hexIdx + 2);
        int end = 0;
        while (end < slice.Length && IsHex(slice[end])) end++;
        if (end > 0 && int.TryParse(slice.Substring(0, end),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out int parsed))
        {
            return parsed;
        }
        return null;
    }

    /// <summary>A time in milliseconds as a message gives it: 90 s,
    /// 22.5 s.</summary>
    internal static string Seconds(int ms) =>
        (ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture) + " s";

    /// <summary>HRESULT_FROM_WIN32(ERROR_BUSY): the device a timed-out
    /// create made was still in the device tree when its removal test
    /// ended.</summary>
    internal const int StillInTreeHr = unchecked((int)0x800700AA);

    /// <summary>Runs <paramref name="attempt"/>, the one create of
    /// <paramref name="instanceId"/>. A create that timed out, the helper
    /// ending its own wait or stopped at the SDK's bound, can have made the
    /// device and closed it, so Windows can still be removing
    /// <paramref name="instanceId"/>. After a timed-out create the removal
    /// test waits up to <paramref name="removalTestMs"/> (scaled, the
    /// helper's bound) for the device to leave the tree, inside the caller's
    /// hold of the PnP lock, so no other program's registration follows the
    /// hold while that removal runs. A device still there ends the create
    /// with <see cref="StillInTreeHr"/>, and the caller refuses the create
    /// either way. Nothing retries it: a second helper would only queue
    /// behind the removal of the first one's device.</summary>
    internal static Result CreateOnce(string instanceId, int removalTestMs, Func<Result> attempt)
    {
        Result result = attempt();
        if (result.Success || result.HResult != WaitTimeoutHr) return result;
        if (DeviceManager.WaitRemovalComplete(instanceId, uninstalls: false, removalTestMs)) return result;
        DeviceOrchestrator.LogDiag($"      {instanceId} still in the device tree after a timed-out create");
        string timedOut = result.Cause is string cause ? cause + " " : "";
        return new Result(false, null, StillInTreeHr,
            $"{timedOut}The device was still in the device tree when its removal test ended after {Seconds(removalTestMs)}.");
    }

    /// <summary>The pin a create or a remove runs the helper under, or none
    /// when the probe's stand-in (<see cref="HelperPathForTest"/>) runs in
    /// its place.</summary>
    private static ProtectedStaging.Pinned? PinForRun(out string helperPath)
    {
        if (HelperPathForTest is string standIn)
        {
            helperPath = standIn;
            return null;
        }
        return PinHelper(out helperPath);
    }

    /// <summary>
    /// Permanently remove a SwDevice via the documented teardown path:
    /// <c>SwDeviceCreate</c> with the original args (reconnects to the
    /// existing device, returns a fresh handle), then
    /// <c>SwDeviceSetLifetime(Handle)</c> + <c>SwDeviceClose</c>. This is
    /// the only way to defeat <c>SwDeviceLifetimeParentPresent</c>'s
    /// auto-resurrect: DIF_REMOVE and pnputil /force both succeed
    /// cosmetically but the kernel re-enumerates the SwDevice via the
    /// lifetime contract because parent (HTREE\ROOT\0) is always present.
    ///
    /// The required args (HardwareIds, CompatibleIDs, ContainerID,
    /// DeviceDesc) are reconstructed from the registry hive entry at
    /// <c>HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instanceId&gt;</c>. If
    /// the registry entry is missing, the device is already fully gone.
    ///
    /// <para>Returns 0 once the device's removal is under way or done, and
    /// otherwise an HRESULT, with the reason in the diagnostic log
    /// (<see cref="RemoveWith"/>). The caller's removal test decides the
    /// outcome.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">hmswd.exe could not be
    /// staged or held (<see cref="PinHelper"/>), with the reason. Nothing
    /// was removed. Thrown before any wait, so a create that removes a rival
    /// or a stale companion fails at once with the reason, and a teardown
    /// logs it and goes on.</exception>
    public static int Remove(string instanceId)
    {
        if (instanceId == null) return E_INVALIDARG;

        // instanceId format: SWD\<enumerator>\<suffix>
        // Parse to extract enumerator + suffix.
        int firstSep = instanceId.IndexOf('\\');
        int lastSep = instanceId.LastIndexOf('\\');
        if (firstSep < 0 || lastSep <= firstSep) return E_INVALIDARG;
        string root       = instanceId.Substring(0, firstSep);
        string enumerator = instanceId.Substring(firstSep + 1, lastSep - firstSep - 1);
        string suffix     = instanceId.Substring(lastSep + 1);

        if (!root.Equals("SWD", StringComparison.OrdinalIgnoreCase))
            return E_INVALIDARG; // not a SWD instance

        // Read args back from the registry hive entry.
        string regPath = $@"SYSTEM\CurrentControlSet\Enum\{instanceId}";
        string[] hwIds;
        string[] compatIds;
        Guid containerId;
        string description;

        using (var regKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath))
        {
            if (regKey == null) return 0; // already gone: nothing to remove
            hwIds       = regKey.GetValue("HardwareID") as string[]    ?? Array.Empty<string>();
            compatIds   = regKey.GetValue("CompatibleIDs") as string[] ?? Array.Empty<string>();
            string?  cidStr      = regKey.GetValue("ContainerID") as string;
            string?  rawDesc     = regKey.GetValue("DeviceDesc")  as string;
            // DeviceDesc format may be "@path,%key%;default": strip to default.
            description = rawDesc ?? "HIDMaestro Device";
            int semicolon = description.LastIndexOf(';');
            if (semicolon >= 0) description = description.Substring(semicolon + 1);
            if (string.IsNullOrWhiteSpace(description)) description = "HIDMaestro Device";

            if (cidStr == null || !Guid.TryParse(cidStr, out containerId))
                return E_FAIL; // no container, can't reconnect
        }

        return RemoveWith(enumerator, suffix, containerId, hwIds, compatIds, description, out _);
    }

    /// <summary>Run hmswd.exe's remove on the device the arguments name and
    /// read its answer: 0 once the device's removal is under way or done,
    /// otherwise an HRESULT, with <paramref name="cause"/> saying why.
    ///
    /// <para>The run gets <see cref="HelperCloseMs"/> (scaled) to reach its
    /// close, STEP CLOSING, and as long again for the close, the step issue
    /// #18 saw stuck behind a stale WUDFHost. A helper stuck before its close
    /// is stopped at 8 s as it was until v2.1.0, and the device stays. Until
    /// v2.1.0 the 8 s ran from the helper's start to its end, which also
    /// stopped a close that began at 7 s. A helper stopped in its close after STEP LIFETIME HANDLE
    /// counts as removed: the end of the process closes the handle, which
    /// removes the device. A software device gets no pnputil or devcon
    /// fallback: DeviceManager.RemoveDeviceLocked runs those for ROOT and
    /// HID ids only.</para></summary>
    /// <exception cref="InvalidOperationException">hmswd.exe could not be
    /// staged or held (<see cref="PinHelper"/>), with the reason. A helper
    /// that cannot be staged or held throws to the caller: no other step
    /// removes an SWD device whose lifetime is tied to its parent, so a
    /// fallback would only wait for a removal that never started.</exception>
    internal static int RemoveWith(string enumerator, string suffix, Guid containerId, string[] hwIds, string[] compatIds,
                                   string description, out string cause)
    {
        // Held until the helper has exited.
        using var pin = PinForRun(out string helperPath);
        var psi = HelperStartInfo(helperPath, "remove", enumerator, suffix, containerId.ToString("B"),
                                  string.Join('|', hwIds), string.Join('|', compatIds), description);
        // Issue #18: a helper stuck before its close (behind a stale WUDFHost)
        // is stopped at the old 8 s bound. Only once it reports STEP CLOSING
        // does the wait run longer, for the close.
        int boundMs = TimeoutScale.Apply(HelperCloseMs);
        string device = $@"{enumerator}\{suffix}";
        int hr = RemoveAnswer(Run(psi, "remove", device, boundMs), boundMs, out cause);
        if (hr != 0 || cause.Length > 0)
            DeviceOrchestrator.LogDiag($"      hmswd remove {device}: 0x{hr:X8} {cause}");
        return hr;
    }

    /// <summary>A remove's answer from its run: 0 once the device's removal
    /// is under way or done, otherwise an HRESULT, with the reason in
    /// <paramref name="cause"/> (empty for a remove that ran its course).
    /// The exit codes are hmswd.c's: 1 refused arguments, 2 SwDeviceCreate's
    /// HRESULT, 3 its own wait for the reconnect's callback, 5
    /// SwDeviceSetLifetime's HRESULT. With 2, 3 and 5 the device's lifetime
    /// is still ParentPresent, so it stays.</summary>
    internal static int RemoveAnswer(HelperRun run, int boundMs, out string cause)
    {
        string closeNote = "";
        int code;
        switch (run.End)
        {
            case HelperEnd.NotStarted:
                cause = $"hmswd.exe could not be run: {run.StartError}";
                return run.StartHr;
            case HelperEnd.StoppedAtBound:
                cause = $"hmswd.exe was still running, {Where(run.LastStep)}, when the SDK's bound of {Seconds(boundMs)} ran out, " +
                        "and was stopped. The device's lifetime is still ParentPresent, so it stays.";
                return WaitTimeoutHr;
            case HelperEnd.StoppedInClose:
                closeNote = $" Its SwDeviceClose had not returned after {Seconds(TimeoutScale.Apply(HelperCloseMs))}, and it was stopped.";
                code = run.ClosingCode ?? E_FAIL;
                if (code == 0)
                {
                    cause = closeNote.TrimStart() + " The device's lifetime is Handle, so the end of the helper's process closes the handle, which removes the device.";
                    return 0;
                }
                break;
            default:
                code = run.ExitCode;
                break;
        }
        string err = run.Stderr.Trim();
        switch (code)
        {
            case 0:
                cause = "";
                return 0;
            case 1:
                cause = $"hmswd.exe refused its arguments, exit code 1 ({Shown(err)}).{closeNote}";
                return E_INVALIDARG;
            case 2:
                cause = $"SwDeviceCreate could not reconnect to the device (hmswd.exe exit code 2, {Shown(err)}). It stays.{closeNote}";
                return ParsedHr(err) ?? E_FAIL;
            case 3:
                cause = $"Plug and Play did not answer the reconnect within hmswd.exe's own wait of {Seconds(HelperWaitMs)} " +
                        $"(exit code 3). The device's lifetime is still ParentPresent, so it stays.{closeNote}";
                return WaitTimeoutHr;
            case 5:
                cause = $"SwDeviceSetLifetime failed (hmswd.exe exit code 5, {Shown(err)}). The device's lifetime is still ParentPresent, so it stays.{closeNote}";
                return ParsedHr(err) ?? E_FAIL;
            default:
                cause = $"hmswd.exe exited with code 0x{code:X8} ({Shown(err)}).{closeNote}";
                return E_FAIL;
        }
    }

    // ── helper extraction ────────────────────────────────────────────────

    private static readonly object s_extractLock = new();
    private static string? s_extractedPath;
    private static string? s_expectedSha256;

    /// <summary>Stage <c>hmswd.exe</c> when this process has not, pin its
    /// directory and every directory above it and hold the file
    /// (<see cref="ProtectedStaging.PinFile"/>), checked against the
    /// embedded hash through the hold. Every run of the helper happens
    /// inside the pin this returns, so the file checked is the file that
    /// runs. A staged copy that fails is staged again once.</summary>
    /// <exception cref="InvalidOperationException">The helper could not be
    /// staged or held, with the reason: a staging refusal
    /// (<see cref="ProtectedStaging.DirFor"/>), a directory or file that
    /// could not be opened, or a file that does not match.</exception>
    /// <exception cref="UnauthorizedAccessException">The process is not
    /// elevated.</exception>
    internal static ProtectedStaging.Pinned PinHelper(out string helperPath)
    {
        lock (s_extractLock)
        {
            if (s_extractedPath != null && s_expectedSha256 != null)
            {
                try
                {
                    var pin = ProtectedStaging.PinFile(s_extractedPath, s_expectedSha256);
                    helperPath = s_extractedPath;
                    return pin;
                }
                catch (InvalidOperationException e)
                {
                    // Changed, gone or unprotected: stage it again below.
                    DeviceOrchestrator.LogDiag($"SwdDeviceFactory: staging hmswd.exe again: {e.Message}");
                    s_extractedPath = null;
                }
            }
            string path = Stage();
            var staged = ProtectedStaging.PinFile(path, s_expectedSha256!);
            helperPath = path;
            return staged;
        }
    }

    /// <summary>Stage <c>hmswd.exe</c> from the embedded resources into the
    /// protected per-version staging directory and return its path. Its
    /// SHA-256 is verified against the embedded bytes when it is staged, and
    /// again under the pin at each use (<see cref="PinHelper"/>). Called
    /// under <c>s_extractLock</c>. Throws what the staging throws.
    ///
    /// <para>There is no longer a sibling-to-the-assembly fast path: it read
    /// <c>Assembly.Location</c>, which is empty in a single-file or NativeAOT
    /// app, and it trusted whatever <c>hmswd.exe</c> sat next to the DLL. The
    /// helper always comes from the embedded resource now, which is both
    /// single-file-safe and the source whose bytes are verified.</para></summary>
    private static string Stage()
    {
        var asm = typeof(SwdDeviceFactory).Assembly;
        string logical = DriverBuilder.NativePrefix + HelperFile;
        string dir = ProtectedStaging.DirFor(ProtectedStaging.OperationSwd);
        s_expectedSha256 ??= ProtectedStaging.EmbeddedSha256(asm, logical);
        ProtectedStaging.StageResource(dir, HelperFile, asm, logical);
        s_extractedPath = Path.Combine(dir, HelperFile);
        return s_extractedPath;
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
