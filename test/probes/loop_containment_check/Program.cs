// S102: each long-running loop of the SDK outlives a pass that fails, logs
// the failure, and never goes round again with no wait while the failure
// lasts. Each case makes one loop fail on every pass, or makes its wait fail
// at once, and runs in a child process of its own, so a loop whose failure
// ends its thread, and with it the process, fails its own case and no other.
//
// A loop that spins is told from one that waits by the cycles this process
// spends over a 2 s window (QueryProcessCycleTime), set beside the cycles a
// thread that spins on purpose spends over the same window
// (QueryThreadCycleTime). A spinning loop costs about as much as that
// thread, whatever else the machine runs, since load slows both alike. A
// loop that waits between passes costs a small part of it. A case passes
// under 5%. Each case also checks that the failure reached the diagnostic
// log (DeviceOrchestrator.DiagTap) under the loop's thread name at the first
// failure, the second and the fourth, not at the third, and, where the case
// can end the failure, that the loop works again and logs the end of the
// failures once a second has passed without one. A wait for a line the loop writes later
// is bounded only by the hang bound, and a case whose loop spins or wrote
// no first line does not wait for the later ones.
//
//   LoopContainmentCheck.exe            every case, each in a child process
//   LoopContainmentCheck.exe <case>     one case, in this process
//
// Offline: no device is created and no elevation is needed. A case that
// reaches a device's loop builds the device's object without its device, as
// usbip_server_check's offline input pump does, and plants process memory
// where a section would be. Exit 0 PASS, 1 FAIL.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal static partial class Program
{
    internal const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

    /// <summary>The share of the spinning thread's cycles a loop may cost
    /// over the window.</summary>
    const double MaxShare = 0.05;

    /// <summary>The measured window.</summary>
    internal const int WindowMs = 2000;

    /// <summary>A hang bound for each wait for something the loop does in a
    /// millisecond or a second: 60 s, scaled by
    /// HIDMAESTRO_TIMEOUT_SCALE.</summary>
    internal static readonly int HangMs = TimeoutScale.Apply(60_000);

    /// <summary>The hang bound of one case's child process.</summary>
    static readonly int CaseHangMs = TimeoutScale.Apply(300_000);

    /// <summary>A handle value no object can hold in this process: above
    /// every value the handle table hands out. Each case that waits on it
    /// first checks that a wait on it fails at once with
    /// ERROR_INVALID_HANDLE.</summary>
    internal static readonly IntPtr NoObject = (IntPtr)0x7FFFFFF0;

    /// <summary>The text of the line a loop writes once a second has passed
    /// with no failure.</summary>
    internal const string EndText = "then none for a second";

    static readonly (string Name, string What, Action Run)[] Cases =
    {
        ("clock-tick", "a device clock whose tick throws on every call", Clock.Tick),
        ("clock-running", "a device clock whose Running throws on every call", Clock.Running),
        ("protocol-host", "a UMDF2 protocol host's tick, through its device clock, on a channel whose write throws", Clock.ProtocolHostTick),
        ("input-pump-wait", "the USB/IP input pump on a wait that fails at once", Usbip.InputPumpWait),
        ("input-pump-throw", "the USB/IP input pump on a pass that throws", Usbip.InputPumpThrow),
        ("delivery-wait", "the wait for a frame's delivery on an event handle that fails at once", Delivery.Wait),
        ("audio-pump-wait", "the USB/IP audio pump on a timer wait that fails at once", Usbip.AudioPumpWait),
        ("audio-pump-throw", "the USB/IP audio pump on a completion that throws", Usbip.AudioPumpThrow),
        ("audio-events-throw", "the USB/IP audio event thread on a raise that throws", Usbip.AudioEventsThrow),
        ("accept-fail", "the USB/IP server's accept loop on a listener that stops listening", Usbip.AcceptFail),
        ("connection-report", "a USB/IP connection that ends on an exception of the server's own", Usbip.ConnectionReport),
        ("haptic-throw", "the VR haptic reader on a pass that throws", Haptic.Throw),
        ("output-report", "the output reader on a drain that throws", Output.Report),
    };

    static int Main(string[] args)
    {
        // A crash or a loader failure in this process or a case's ends in an
        // exit code, never in a dialog on the desktop. Children inherit it.
        SetErrorMode(SetErrorMode(0) | SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
        if (args.Length == 0) return RunAll();
        var c = Cases.FirstOrDefault(k => k.Name == args[0]);
        if (c.Run == null)
        {
            Console.WriteLine($"unknown case '{args[0]}'. Cases: {string.Join(", ", Cases.Select(k => k.Name))}");
            return 1;
        }
        Console.WriteLine($"-- {c.Name}: {c.What}");
        Diag.Start();
        c.Run();
        return Checks.Failed == 0 ? 0 : 1;
    }

    /// <summary>Each case in a child process, one after another, so no
    /// case's threads or cycles reach another's window.</summary>
    static int RunAll()
    {
        Console.WriteLine("=== loop_containment_check: the SDK's loops outlive a failing pass and never spin on one ===");
        Console.WriteLine($"SDK {typeof(HMContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}");
        int failed = 0;
        foreach (var c in Cases)
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!, c.Name)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start the case {c.Name}");
            var err = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (err) err.AppendLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            var sw = Stopwatch.StartNew();
            if (!p.WaitForExit(CaseHangMs))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                p.WaitForExit();
                Console.WriteLine($"  [FAIL] case {c.Name} did not end within {CaseHangMs / 1000} s, stopped");
                failed++;
                continue;
            }
            // The redirected streams are read to their end.
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                failed++;
                string tail;
                lock (err) tail = err.ToString().Trim();
                Console.WriteLine($"  [FAIL] case {c.Name} exited with code {p.ExitCode} (0x{p.ExitCode:X8}) after {sw.Elapsed.TotalSeconds:F1} s"
                                  + (tail.Length > 0 ? $": {string.Join(" | ", tail.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(6))}" : ""));
            }
        }
        Console.WriteLine(failed == 0 ? $"=== ALL PASS ({Cases.Length} cases) ===" : $"=== FAIL: {failed}/{Cases.Length} cases ===");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Waits up to <see cref="HangMs"/> for
    /// <paramref name="done"/>.</summary>
    internal static bool WaitUntil(Func<bool> done)
    {
        var sw = Stopwatch.StartNew();
        while (!done())
        {
            if (sw.ElapsedMilliseconds > HangMs) return false;
            Thread.Sleep(2);
        }
        return true;
    }

    /// <summary>Checks the premise of a case that waits on
    /// <see cref="NoObject"/>: a wait on it fails at once with
    /// ERROR_INVALID_HANDLE.</summary>
    internal static bool NoObjectFails()
    {
        uint r = WaitForSingleObject(NoObject, 0);
        int error = Marshal.GetLastPInvokeError();
        bool ok = r == WAIT_FAILED && error == ERROR_INVALID_HANDLE;
        Checks.Check($"a wait on handle 0x{NoObject:X} fails at once with ERROR_INVALID_HANDLE", ok, $"0x{r:X8}, error {error}");
        return ok;
    }

    /// <summary>The cycles this process spends while <paramref name="during"/>
    /// runs, or over <see cref="WindowMs"/>, beside those of a thread that
    /// spins over the same time, and the first as a share of the second.
    /// The spinning thread runs above normal priority, as the SDK's device
    /// clocks and input pump do, so other programs' threads take no more
    /// from it than from them.</summary>
    internal static (double Share, ulong Other, ulong Spin) Measure(Action? during = null)
    {
        // Compiled now, outside the window.
        new Spinner { Stop = true }.Run();
        var spinner = new Spinner();
        var t = new Thread(spinner.Run) { IsBackground = true, Name = "LCC spinner", Priority = ThreadPriority.AboveNormal };
        QueryProcessCycleTime(GetCurrentProcess(), out ulong p0);
        t.Start();
        if (during != null) during();
        else Thread.Sleep(WindowMs);
        Volatile.Write(ref spinner.Stop, true);
        t.Join();
        QueryProcessCycleTime(GetCurrentProcess(), out ulong p1);
        ulong total = p1 - p0;
        ulong other = total > spinner.Cycles ? total - spinner.Cycles : 0;
        return (other / (double)Math.Max(1UL, spinner.Cycles), other, spinner.Cycles);
    }

    /// <summary>The measurement as a check: the loop costs under
    /// <see cref="MaxShare"/> of the spinning thread. True when it
    /// does.</summary>
    internal static bool CheckBounded(string loop, (double Share, ulong Other, ulong Spin) m)
    {
        bool ok = m.Share < MaxShare;
        Checks.Check($"{loop} costs under {MaxShare:P0} of a spinning thread's cycles over the same window", ok,
            FormattableString.Invariant($"{m.Share:P2}: this process {m.Other / 1e6:F1} Mcycles, the spinning thread {m.Spin / 1e6:F1} Mcycles"));
        return ok;
    }

    sealed class Spinner
    {
        public bool Stop;
        public ulong Cycles;

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        public void Run()
        {
            QueryThreadCycleTime(GetCurrentThread(), out ulong c0);
            while (!Volatile.Read(ref Stop)) { }
            QueryThreadCycleTime(GetCurrentThread(), out ulong c1);
            Cycles = c1 - c0;
        }
    }

    /// <summary>A catalog profile's JSON, from the SDK's embedded
    /// catalog.</summary>
    internal static string Catalog(string id)
    {
        var asm = typeof(HMContext).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)))
        {
            using var st = asm.GetManifestResourceStream(res)!;
            using var sr = new StreamReader(st, Encoding.UTF8);
            string t = sr.ReadToEnd();
            if (JsonNode.Parse(t) is JsonObject o && (string?)o["id"] == id) return t;
        }
        throw new InvalidOperationException($"no catalog profile {id}");
    }

    internal static ControllerProfile Profile(string json)
    {
        var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(json)) ?? throw new InvalidDataException("no profile");
        p.ValidateDeviceModel();
        return p;
    }

    internal const uint WAIT_FAILED = 0xFFFFFFFF;
    internal const int ERROR_INVALID_HANDLE = 6;
    const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;

    [LibraryImport("kernel32.dll")] private static partial uint SetErrorMode(uint mode);
    [LibraryImport("kernel32.dll")] private static partial IntPtr GetCurrentProcess();
    [LibraryImport("kernel32.dll")] private static partial IntPtr GetCurrentThread();
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool QueryProcessCycleTime(IntPtr process, out ulong cycles);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial uint WaitForSingleObject(IntPtr handle, uint ms);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial IntPtr CreateEventW(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, IntPtr name);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetEvent(IntPtr handle);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static partial bool CloseHandle(IntPtr handle);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect, uint sizeHigh, uint sizeLow, IntPtr name);
    [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);
}

internal static class Checks
{
    public static int Failed;

    public static bool Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
        if (!ok) Failed++;
        return ok;
    }
}

/// <summary>The diagnostic log's lines, through the tap the SDK gives every
/// line whether or not HIDMAESTRO_DIAG is set.</summary>
internal static class Diag
{
    static readonly List<string> s_lines = new();

    public static void Start() => DeviceOrchestrator.DiagTap = line => { lock (s_lines) s_lines.Add(line); };

    /// <summary>A line of the thread named <paramref name="thread"/> that
    /// holds <paramref name="text"/>, and <paramref name="also"/> when it is
    /// not null.</summary>
    public static string? Find(string thread, string text, string? also = null)
    {
        lock (s_lines)
            return s_lines.FirstOrDefault(l => l.StartsWith(thread + ": ", StringComparison.Ordinal) && l.Contains(text, StringComparison.Ordinal)
                                               && (also == null || l.Contains(also, StringComparison.Ordinal)));
    }

    /// <summary>The check that the loop on <paramref name="thread"/> has
    /// logged a line holding <paramref name="text"/>, and
    /// <paramref name="also"/> when it is not null. With
    /// <paramref name="wait"/>, it waits up to <see cref="Program.HangMs"/>
    /// for a line the loop writes later. Without, the line must be there
    /// now: the case calls it once the loop has gone past the point that
    /// writes it, or once the loop showed it is not the loop that writes
    /// it.</summary>
    public static bool Logged(string what, string thread, string text, bool wait, string? also = null)
    {
        string? line = Find(thread, text, also);
        if (line == null && wait) Program.WaitUntil(() => (line = Find(thread, text, also)) != null);
        string wanted = also == null ? $"\"{text}\"" : $"\"{text}\" and \"{also}\"";
        return Checks.Check(what, line != null, line ?? $"no line of {thread} holds {wanted}{Others(thread)}");
    }

    /// <summary>The check that the loop on <paramref name="thread"/> logged
    /// no line holding <paramref name="text"/>: the failures between two
    /// powers of two.</summary>
    public static void NotLogged(string what, string thread, string text)
    {
        string? line = Find(thread, text);
        Checks.Check(what, line == null, line ?? "");
    }

    static string Others(string thread)
    {
        lock (s_lines)
        {
            var mine = s_lines.Where(l => l.StartsWith(thread + ": ", StringComparison.Ordinal)).Take(3).ToList();
            return mine.Count == 0 ? "" : ": " + string.Join(" | ", mine);
        }
    }

    /// <summary>The usual checks of a loop's log once it fails on every
    /// pass: the first failure, the second and the fourth, and no line for
    /// the third, which came before the fourth. <paramref name="first"/> is
    /// the start of the first line's text, which ends with its count. With
    /// <paramref name="wait"/> false the first line must be there now, and
    /// the later checks wait only when it is. True when the first line is
    /// there.</summary>
    public static bool Failures(string thread, string first, bool wait)
    {
        bool logged = Logged("the failure reaches the diagnostic log under the loop's thread name", thread, first, wait, also: "(failure 1)");
        Logged("the second failure is logged too", thread, "(failure 2)", logged);
        if (Logged("the fourth failure is logged too", thread, "(failure 4)", logged))
            NotLogged("the third failure is not: lines come at the powers of two", thread, "(failure 3)");
        return logged;
    }
}
