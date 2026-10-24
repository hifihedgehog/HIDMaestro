// Power policy check: the driver hosts of every pad live through a system
// sleep, and the pads read again after the resume.
//
// HidClass owns power policy for a HID minidriver stack, and the
// framework-based minidriver under MsHidUmdf must not (Learn, "Creating WDF
// HID Minidrivers", step 3, and Windows-driver-samples hid/vhidmini2/driver/
// vhidmini.c:160-163). A UMDF device that owns power policy and receives a
// set-device-power IRP it never requested hits WDF_POWER_MULTIPLE_PPO
// (Windows-Driver-Frameworks src/framework/shared/irphandlers/pnp/
// fdopower.cpp:311-327), and UMDF ends its host. Until 2.1.0 the HID device
// of every profile with an XUSB companion ran as a function driver that kept
// the default ownership. On 2026-10-24, across a system sleep, both Xbox 360
// hosts of a load run ended with that failure (System log 10121 for
// HIDMaestro.dll, WER WUDFVerifierFailure at fdopower.cpp:327).
//
// That a sleep sends the device power IRP is what those records show, and
// this check is the test of it and of the fix. HidClass's idle did not
// send one to this root-enumerated stack: with SelectiveSuspendEnabled and
// EnableDefaultIdleNotificationHandler in the hardware key, a DualShock 4
// with no handle open stayed in D0 for 60 s. So this check sleeps the
// machine, with the WDK's pwrtest and its wake timer, and only when run as
//
//   PowerPolicyCheck sleep [profile ...] [--seconds=<n>] [--pwrtest=<path>]
//
// It is no battery scenario, and nothing runs it on its own: a sleep stops
// everything else on the machine. For each profile (dualshock-4-v2 and
// xbox-360-wired unless named) it creates the pad and reads its HID input
// reports on a thread, one consumer thread submitting every pad every 2 ms.
// It then reads for 2 s (the positive control: every pad reads), runs
// pwrtest /sleep /c:1 /d:5 /p:<n> /s:3 (30 s unless given), lets the pads
// settle for 10 s and reads for 2 s again. Checks, per pad:
//   - it read reports before the sleep
//   - no UMDF host of the pad ended across the sleep: no System log event
//     10110 to 10121 since the sleep began names its device node or its
//     XUSB companion, or shares a host lifetime with one that does, and the
//     device node is still started with no problem code
//   - it reads reports after the resume, on the handle it opened before
// It also prints how many of those events the whole machine logged, for
// any device.
//
//   PowerPolicyCheck events <since> <main instance> ...
//
// runs the same event matching on the System log as it stands, with no
// device and no sleep, for a record already logged. Elevated, headless. Exit 0 PASS, 1 FAIL, 2 usage or pwrtest
// missing, 3 not elevated.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static void Log(string line) =>
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  [{DateTime.Now:HH:mm:ss.fff}] {line}"));

    const string DefaultPwrtest = @"C:\Program Files (x86)\Windows Kits\10\Tools\10.0.26100.0\x64\pwrtest.exe";

    /// <summary>One pad, its HID handle and the count its read thread
    /// keeps.</summary>
    sealed class Pad
    {
        public required string Id;
        public required HMController Controller;
        public required string Main;
        public required string Companion;
        public required SafeFileHandle Handle;
        public long Reads;
        public int EndError;
        public Thread? Reader;
    }

    static int Main(string[] args)
    {
        // No error dialog from this process or any it starts (pwrtest,
        // wevtutil), over the bits already set.
        SetErrorMode(SetErrorMode(0) | SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
        if (args.Length >= 3 && args[0] == "events")
            return Events(DateTime.Parse(args[1], CultureInfo.InvariantCulture), args[2..]);
        if (args.Length == 0 || args[0] != "sleep")
        {
            Console.WriteLine("usage: PowerPolicyCheck sleep [profile ...] [--seconds=<n>] [--pwrtest=<path>]");
            Console.WriteLine("The check sleeps this machine. It runs only when asked by name.");
            return 2;
        }
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.WriteLine("PowerPolicyCheck needs elevation.");
            return 3;
        }
        int seconds = 30;
        string pwrtest = DefaultPwrtest;
        var ids = new List<string>();
        foreach (string a in args.Skip(1))
        {
            if (a.StartsWith("--seconds=", StringComparison.Ordinal)) seconds = int.Parse(a[10..], CultureInfo.InvariantCulture);
            else if (a.StartsWith("--pwrtest=", StringComparison.Ordinal)) pwrtest = a[10..];
            else ids.Add(a);
        }
        if (ids.Count == 0) ids.AddRange(new[] { "dualshock-4-v2", "xbox-360-wired" });
        if (!File.Exists(pwrtest)) { Console.WriteLine($"no pwrtest at {pwrtest}: pass --pwrtest=<path>"); return 2; }

        var sdk = typeof(HMContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Console.WriteLine($"=== power policy check, SDK {sdk} ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var pads = new List<Pad>();
        using var stop = new ManualResetEventSlim(false);
        Thread? submitter = null;
        try
        {
            for (int n = 0; n < ids.Count; n++)
            {
                var profile = ctx.GetProfile(ids[n]);
                if (profile == null) { Check($"{ids[n]}: a catalog profile", false, "no such id"); return 1; }
                // One create at a time: never two in parallel in one process.
                var c = ctx.CreateController(profile, $"power-policy-{n}");
                // HMController.InstanceId is internal. Reflection reads it on
                // any 2.x SDK, whose internals name no such probe.
                string? main = typeof(HMController).GetProperty("InstanceId", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(c) as string;
                string? child = main == null ? null : HidChild(main);
                string? path = child == null ? null : HidInterface(child);
                if (main == null || path == null) { c.Dispose(); Check($"{ids[n]}: a HID interface under the controller", false); return 1; }
                var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) { c.Dispose(); Check($"{ids[n]}: its HID interface opens", false, path); return 1; }
                // A companion's instance is SWD\HIDMAESTRO\ and the main
                // device node's token. A pad without one names none.
                string companion = @"SWD\HIDMAESTRO\" + main[(main.LastIndexOf('\\') + 1)..];
                var pad = new Pad { Id = ids[n], Controller = c, Main = main, Companion = companion, Handle = h };
                pad.Reader = new Thread(() => ReadLoop(pad)) { IsBackground = true, Name = "reader " + ids[n] };
                pad.Reader.Start();
                pads.Add(pad);
                Log($"{ids[n]}: {main} runs {DriverVersion(main)}");
            }

            submitter = new Thread(() =>
            {
                bool flip = false;
                while (!stop.Wait(2))
                {
                    flip = !flip;
                    foreach (var p in pads) p.Controller.SubmitState(new HMGamepadState { Buttons = flip ? HMButton.A : HMButton.B });
                }
            }) { IsBackground = true, Name = "submitter" };
            submitter.Start();

            Thread.Sleep(1000);
            var before = ReadsOver(pads, 2000);
            var since = DateTime.Now;
            Log($"pwrtest /sleep for {seconds} s");
            int code = RunPwrtest(pwrtest, seconds);
            Log($"pwrtest exit {code}");
            Thread.Sleep(10000);
            var after = ReadsOver(pads, 2000);
            var events = HostEvents(since);
            foreach (var e in events) Log($"event {e.Id} {e.InstanceId ?? "(no instance)"} lifetime {e.LifetimeId ?? "(none)"}: {e.Text}");
            Log($"the machine logged {events.Count} UMDF host events (10110 to 10121) since the sleep began, for any device");

            for (int i = 0; i < pads.Count; i++)
            {
                var p = pads[i];
                var mine = EventsOf(events, p.Main, p.Companion);
                Check($"{p.Id}: reads before the sleep", before[i] > 0, $"{before[i]} reports in 2 s");
                Check($"{p.Id}: no UMDF host of the pad ended across the sleep", mine.Count == 0 && Started(p.Main),
                      $"{mine.Count} of its events ({string.Join(" ", mine.Select(e => e.Id))}), {State(p.Main)}");
                Check($"{p.Id}: reads after the resume on the same handle", after[i] > 0,
                      $"{after[i]} reports in 2 s, read loop {(p.EndError == 0 ? "running" : "ended with error " + p.EndError)}");
            }
            Check("pwrtest slept and resumed", code == 0, $"exit {code}");
        }
        finally
        {
            stop.Set();
            submitter?.Join(2000);
            foreach (var p in pads)
            {
                // The handle stays open while its read is in the kernel
                // (SafeHandle counts the call), so the read is canceled
                // first, then the handle closes.
                CancelIoEx(p.Handle, IntPtr.Zero);
                p.Reader?.Join(2000);
                p.Handle.Dispose();
            }
            // Disposed one at a time, in the order created.
            foreach (var p in pads) p.Controller.Dispose();
        }
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>events &lt;since&gt; &lt;main instance&gt; ...: the matching
    /// the sleep check does, run on the System log as it is, with no device
    /// and no sleep. Each main instance's companion is derived as the sleep
    /// check derives it. Exit 0.</summary>
    static int Events(DateTime since, string[] mains)
    {
        var events = HostEvents(since);
        foreach (var e in events) Log($"event {e.Id} {e.InstanceId ?? "(no instance)"} lifetime {e.LifetimeId ?? "(none)"}: {e.Text}");
        Log($"the machine logged {events.Count} UMDF host events (10110 to 10121) since {since:O}, for any device");
        foreach (string main in mains)
        {
            string companion = @"SWD\HIDMAESTRO\" + main[(main.LastIndexOf('\\') + 1)..];
            var mine = EventsOf(events, main, companion);
            Log($"{main}: {mine.Count} of its events ({string.Join(" ", mine.Select(e => e.Id))})");
        }
        return 0;
    }

    static void ReadLoop(Pad p)
    {
        var buffer = new byte[512];
        while (ReadFile(p.Handle, buffer, buffer.Length, out _, IntPtr.Zero)) Interlocked.Increment(ref p.Reads);
        Volatile.Write(ref p.EndError, Marshal.GetLastWin32Error());
    }

    static long[] ReadsOver(List<Pad> pads, int ms)
    {
        var r0 = pads.Select(p => Interlocked.Read(ref p.Reads)).ToArray();
        Thread.Sleep(ms);
        return pads.Select((p, i) => Interlocked.Read(ref p.Reads) - r0[i]).ToArray();
    }

    /// <summary>pwrtest /sleep, one S3 cycle woken by its timer, run in its
    /// own folder so its logs land beside nothing else.</summary>
    static int RunPwrtest(string exe, int seconds)
    {
        string dir = Path.Combine(Path.GetTempPath(), "PowerPolicyCheck-pwrtest");
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = dir,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string a in new[] { "/sleep", "/c:1", "/d:5", $"/p:{seconds.ToString(CultureInfo.InvariantCulture)}", "/s:3" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        foreach (string line in text.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(6)) Log("pwrtest: " + line.Trim());
        return p.ExitCode;
    }

    // Device nodes

    static bool Started(string instanceId) =>
        CM_Locate_DevNodeW(out uint dev, instanceId, 0) == 0
        && CM_Get_DevNode_Status(out uint status, out uint problem, dev, 0) == 0
        && (status & DN_STARTED) != 0 && problem == 0;

    static string State(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, 0) != 0) return "not present";
        CM_Get_DevNode_Status(out uint status, out uint problem, dev, 0);
        return string.Create(CultureInfo.InvariantCulture, $"status 0x{status:X8} problem {problem}");
    }

    static string? HidChild(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, 0) != 0) return null;
        if (CM_Get_Child(out uint child, dev, 0) != 0) return null;
        var buf = new char[256];
        return CM_Get_Device_IDW(child, buf, buf.Length, 0) == 0 ? new string(buf).TrimEnd('\0') : null;
    }

    static string? HidInterface(string childId)
    {
        var hid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");
        if (CM_Get_Device_Interface_List_SizeW(out uint len, ref hid, childId, 0) != 0 || len <= 1) return null;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref hid, childId, buf, len, 0) != 0) return null;
        string first = new string(buf).Split('\0')[0];
        return first.Length > 0 ? first : null;
    }

    static string DriverVersion(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint dev, instanceId, 0) != 0) return "(not present)";
        var key = new DEVPROPKEY { fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 3 };
        var buf = new byte[256];
        uint size = (uint)buf.Length;
        string version = CM_Get_DevNode_PropertyW(dev, ref key, out _, buf, ref size, 0) == 0
            ? System.Text.Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0') : "(no version)";
        key.pid = 2;
        size = (uint)buf.Length;
        string date = CM_Get_DevNode_PropertyW(dev, ref key, out _, buf, ref size, 0) == 0 && size == 8
            ? DateTime.FromFileTimeUtc(BitConverter.ToInt64(buf, 0)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "(no date)";
        return $"driver {version} of {date}";
    }

    /// <summary>One UMDF host event: its ID, the device instance and host
    /// lifetime its data names (10120 and 10111 name both, 10121 only the
    /// lifetime), and its data fields as text. A failed query is one record
    /// with QueryFailed set.</summary>
    sealed record HostEvent(string Id, string? InstanceId, string? LifetimeId, string Text, bool QueryFailed = false);

    /// <summary>The events of one pad: those naming its device node or its
    /// companion, and those of a host lifetime one of them names. A failed
    /// query counts against every pad.</summary>
    static List<HostEvent> EventsOf(List<HostEvent> events, string main, string companion)
    {
        bool Names(HostEvent e) => e.InstanceId != null
            && (string.Equals(e.InstanceId, main, StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.InstanceId, companion, StringComparison.OrdinalIgnoreCase));
        var lifetimes = new HashSet<string>(events.Where(Names).Select(e => e.LifetimeId).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        return events.Where(e => e.QueryFailed || Names(e) || (e.LifetimeId != null && lifetimes.Contains(e.LifetimeId))).ToList();
    }

    /// <summary>UMDF's host failure events in the System log since a time,
    /// 10110 to 10121 from DriverFrameworks-UserMode, through wevtutil, which
    /// every Windows carries, in its XML form.</summary>
    static List<HostEvent> HostEvents(DateTime since)
    {
        var list = new List<HostEvent>();
        string ms = ((long)(DateTime.Now - since).TotalMilliseconds + 1000).ToString(CultureInfo.InvariantCulture);
        var psi = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\wevtutil.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("qe");
        psi.ArgumentList.Add("System");
        psi.ArgumentList.Add($"/q:*[System[Provider[@Name='Microsoft-Windows-DriverFrameworks-UserMode'] and (EventID>=10110 and EventID<=10121) and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
        psi.ArgumentList.Add("/f:xml");
        using var p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) { list.Add(new HostEvent("query", null, null, $"wevtutil exit {p.ExitCode}", QueryFailed: true)); return list; }
        // The events come one after another with no root element.
        System.Xml.Linq.XElement root;
        try { root = System.Xml.Linq.XElement.Parse("<Events>" + text + "</Events>"); }
        catch (System.Xml.XmlException e) { list.Add(new HostEvent("query", null, null, "unreadable XML: " + e.Message, QueryFailed: true)); return list; }
        foreach (var ev in root.Elements())
        {
            string id = ev.Descendants().FirstOrDefault(x => x.Name.LocalName == "EventID")?.Value ?? "?";
            var data = ev.Descendants().FirstOrDefault(x => x.Name.LocalName == "UserData")?.Elements().FirstOrDefault();
            var fields = data?.Elements().ToList() ?? new List<System.Xml.Linq.XElement>();
            string? Field(string name) => fields.FirstOrDefault(x => x.Name.LocalName == name)?.Value;
            list.Add(new HostEvent(id, Field("InstanceId"), Field("LifetimeId"),
                $"{data?.Name.LocalName} " + string.Join(" ", fields.Where(x => x.Name.LocalName is not ("InstanceId" or "LifetimeId")).Select(x => $"{x.Name.LocalName}={x.Value}"))));
        }
        return list;
    }

    // Interop

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3, DN_STARTED = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Child(out uint child, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, int length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string deviceId, char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[] buffer, ref uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte[] buffer, int length, out int read, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);

    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);

    const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;
}
