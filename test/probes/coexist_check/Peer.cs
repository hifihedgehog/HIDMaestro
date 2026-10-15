// One program that embeds HIDMaestro, driven line by line over stdin by the
// probe. Each reply is one stdout line starting with OK or ERR. Everything
// a release before v1.13.0 lacks is read through reflection, so this file
// compiles unchanged against an older HIDMaestro.Core.dll and runs as a
// program built on that release.
//
//   install                     HMContext.InstallDriver
//   create pad <pid> <key|->    a custom gamepad, VID BEEF and this PID
//   create xbox - <key|->       xbox-360-wired
//   create composite - <key|->  dualsense-composite (USB/IP)
//   create gc - <key|->         gamecube-adapter (USB/IP)
//   createat <slot> <kind> <pid|->  CreateControllerAt(slot, profile), the
//                               same kinds as create, no identity key
//   stream <btn> <x> <hz> <ramp>  submit at <hz> to every live controller:
//                               button <btn> held, X at <x>, and with
//                               ramp 1, X stepping by one count a frame
//   stopstream                  stop, and report the submit intervals
//   sweep                       RemoveAllVirtualControllers(preserveInstall: true)
//   removeall                   RemoveAllVirtualControllers()
//   everything                  remove every controller of every program,
//                               keep the driver
//   uninstall                   remove every controller and the driver
//   spawnchild                  start a child that inherits handles
//   driverversion               the last controller's DriverVersion, read
//                               for up to 2 s, or none
//   stagingdir                  stage the driver payload the way an install
//                               does (DriverBuilder.EnsureExtracted) in this
//                               process and reply with its directory, last
//                               on the line so a path with spaces survives
//   swdhelper                   stage hmswd.exe the way a device create does
//                               (SwdDeviceFactory.EnsureHelperExtracted) and
//                               reply with its path, last on the line
//   failpump                    the next composite's device emulator
//                               throws right after its input pump starts
//   pausemapping                the next create stops before it maps its
//                               index's input section, replies "OK paused"
//                               and goes on at the next line ("go")
//   pauseindex                  the same, replying "OK paused index=<N>"
//                               with the transport index the create took
//   alive                       reply, to show the program still runs
//   sdkevents <index>           open the index's output doorbell the way
//                               the SDK does (SharedMemoryIO.TryOpenOutputEvent),
//                               wait on it without blocking, and signal its
//                               StopEvent as the SDK's sweep does
//   threads <prefix>            how many of this program's threads have a
//                               name starting with <prefix>
//   staleport <n>               set the last controller's remembered vhci
//                               port to <n>, the value a drop that races
//                               the port tracker leaves
//   bindwinusb                  the last controller's BindWinUsb
//   exports <port>              what the SDK's sweep reads a USB/IP
//                               server on <port> to export, through its
//                               internal UsbipBackend.ExportedBusIds:
//                               n=<count> or n=null, and ms=<elapsed>
//   dispose                     dispose every controller
//   exit                        dispose the context and exit
//   quit                        exit without disposing anything, so only
//                               the SDK's exit handler cleans up
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using HIDMaestro;

internal static class Peer
{
    static HMContext? s_ctx;
    static readonly List<HMController> s_live = new();
    static Thread? s_streamer;
    static volatile bool s_streaming;
    static readonly List<double> s_intervals = new();

    public static int Run()
    {
        Reply($"OK ready pid={Environment.ProcessId} sdk={typeof(HMContext).Assembly.GetName().Version}");
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            var a = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 0) continue;
            try
            {
                switch (a[0])
                {
                    case "install":
                    {
                        s_ctx ??= NewContext();
                        var sw = Stopwatch.StartNew();
                        s_ctx.InstallDriver();
                        Reply($"OK install ms={sw.ElapsedMilliseconds} outcome={Get(s_ctx, "LastDriverInstall") ?? "unknown"}");
                        break;
                    }
                    case "create":
                    {
                        s_ctx ??= NewContext();
                        HMProfile profile = Profile(s_ctx, a[1], a[2]);
                        string? key = a.Length > 3 && a[3] != "-" ? a[3] : null;
                        var sw = Stopwatch.StartNew();
                        HMController c = s_ctx.CreateController(profile, key);
                        long ms = sw.ElapsedMilliseconds;
                        lock (s_live) s_live.Add(c);
                        string index = Get(c, "Index") ?? "?";
                        Reply($"OK create index={index} transport={Get(c, "TransportIndex") ?? index} " +
                              $"instance={Get(c, "InstanceId") ?? "none"} key={c.IdentityKey} ms={ms}");
                        break;
                    }
                    case "createat":
                    {
                        s_ctx ??= NewContext();
                        HMProfile profile = Profile(s_ctx, a[2], a[3]);
                        var sw = Stopwatch.StartNew();
                        HMController c = s_ctx.CreateControllerAt(int.Parse(a[1], CultureInfo.InvariantCulture), profile);
                        long ms = sw.ElapsedMilliseconds;
                        lock (s_live) s_live.Add(c);
                        string index = Get(c, "Index") ?? "?";
                        Reply($"OK createat index={index} transport={Get(c, "TransportIndex") ?? index} key={c.IdentityKey} ms={ms}");
                        break;
                    }
                    case "stream":
                        StartStream(int.Parse(a[1]), float.Parse(a[2], CultureInfo.InvariantCulture), int.Parse(a[3]), a[4] == "1");
                        Reply("OK stream");
                        break;
                    case "stopstream":
                    {
                        StopStream();
                        double[] v;
                        lock (s_intervals) v = s_intervals.OrderBy(x => x).ToArray();
                        Reply(v.Length == 0 ? "OK stopstream n=0" :
                              FormattableString.Invariant($"OK stopstream n={v.Length} p50={v[v.Length / 2]:F3} p99={v[Math.Min(v.Length - 1, (int)(v.Length * 0.99))]:F3} max={v[^1]:F3}"));
                        break;
                    }
                    case "sweep":
                        HMContext.RemoveAllVirtualControllers(preserveInstall: true);
                        Reply("OK sweep");
                        break;
                    case "removeall":
                        HMContext.RemoveAllVirtualControllers();
                        Reply("OK removeall");
                        break;
                    case "everything":
                        RemoveEverything(preserveInstall: true);
                        Reply("OK everything");
                        break;
                    case "uninstall":
                        RemoveEverything(preserveInstall: false);
                        Reply("OK uninstall");
                        break;
                    case "driverversion":
                    {
                        HMController? last;
                        lock (s_live) last = s_live.Count > 0 ? s_live[^1] : null;
                        string? v = null;
                        var sw = Stopwatch.StartNew();
                        while (last != null && (v = Get(last, "DriverVersion")) == null && sw.ElapsedMilliseconds < 2000)
                            Thread.Sleep(20);
                        Reply($"OK driverversion {v ?? "none"}");
                        break;
                    }
                    case "stagingdir":
                    {
                        // Public since v1.3.0, but read through reflection
                        // like everything else here so the file compiles
                        // against any release.
                        var builder = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.DriverBuilder");
                        var ensure = builder?.GetMethod("EnsureExtracted", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
                        if (ensure == null) { Reply("ERR stagingdir no DriverBuilder.EnsureExtracted"); break; }
                        string dir = (string)ensure.Invoke(null, null)!;
                        Reply($"OK stagingdir path={dir}");
                        break;
                    }
                    case "swdhelper":
                    {
                        // Stage hmswd.exe the way a device create does, and
                        // report its path. Internal on every release, so
                        // reflection reaches it.
                        var swd = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.SwdDeviceFactory");
                        var m = swd?.GetMethod("EnsureHelperExtracted", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
                        if (m == null) { Reply("ERR swdhelper no SwdDeviceFactory.EnsureHelperExtracted"); break; }
                        string? path = (string?)m.Invoke(null, null);
                        Reply(path == null ? "ERR swdhelper none" : $"OK swdhelper path={path}");
                        break;
                    }
                    case "spawnchild":
                    {
                        // Started the way the SDK starts pnputil and hmswd:
                        // redirected output, so CreateProcess inherits every
                        // inheritable handle.
                        var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 300 127.0.0.1 >nul")
                        { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
                        Reply($"OK spawnchild pid={p.Id}");
                        break;
                    }
                    case "failpump":
                        Reply(SetHook("HIDMaestro.Internal.Usbip.UsbipEmulatedDevice", "AfterInputPumpStart",
                                      () => throw new InvalidOperationException("failpump: the device emulator fails after its input pump starts"))
                              ? "OK failpump" : "ERR failpump no hook");
                        break;
                    case "pausemapping":
                        Reply(SetHook("HIDMaestro.Internal.DeviceOrchestrator", "BeforeInputMapping", () =>
                              {
                                  Reply("OK paused");
                                  Console.ReadLine();
                              })
                              ? "OK pausemapping" : "ERR pausemapping no hook");
                        break;
                    case "pauseindex":
                        Reply(SetHook("HIDMaestro.Internal.DeviceOrchestrator", "BeforeInputMapping", index =>
                              {
                                  Reply($"OK paused index={index}");
                                  Console.ReadLine();
                              })
                              ? "OK pauseindex" : "ERR pauseindex no hook");
                        break;
                    case "alive":
                        Reply("OK alive");
                        break;
                    case "staleport":
                    {
                        HMController? last;
                        lock (s_live) last = s_live.Count > 0 ? s_live[^1] : null;
                        object? handle = last?.GetType().GetProperty("UsbipHandle", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(last);
                        object? device = handle?.GetType().GetProperty("Device")?.GetValue(handle);
                        var field = device?.GetType().GetField("_vhciPort", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (field == null) { Reply("ERR staleport no device"); break; }
                        field.SetValue(device, int.Parse(a[1], CultureInfo.InvariantCulture));
                        Reply($"OK staleport {a[1]}");
                        break;
                    }
                    case "bindwinusb":
                    {
                        HMController? last;
                        lock (s_live) last = s_live.Count > 0 ? s_live[^1] : null;
                        var m = last?.GetType().GetMethod("BindWinUsb", Type.EmptyTypes);
                        if (m == null) { Reply("ERR bindwinusb no method"); break; }
                        Reply($"OK bindwinusb {m.Invoke(last, null)}");
                        break;
                    }
                    case "exports":
                    {
                        var m = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.Usbip.UsbipBackend")
                            ?.GetMethod("ExportedBusIds", BindingFlags.NonPublic | BindingFlags.Static);
                        if (m == null) { Reply("ERR exports no UsbipBackend.ExportedBusIds"); break; }
                        var sw = Stopwatch.StartNew();
                        object? set = m.Invoke(null, new object[] { int.Parse(a[1], CultureInfo.InvariantCulture) });
                        Reply($"OK exports n={(set == null ? "null" : set.GetType().GetProperty("Count")!.GetValue(set))} ms={sw.ElapsedMilliseconds}");
                        break;
                    }
                    case "sdkevents":
                        Reply(SdkEvents(int.Parse(a[1])));
                        break;
                    case "threads":
                        Reply($"OK threads n={CountThreads(a[1])}");
                        break;
                    case "dispose":
                        StopStream();
                        lock (s_live) { foreach (var c in s_live) c.Dispose(); s_live.Clear(); }
                        Reply("OK dispose");
                        break;
                    case "quit":
                        StopStream();
                        Reply("OK quit");
                        Environment.Exit(0);
                        return 0;
                    case "exit":
                        StopStream();
                        s_ctx?.Dispose();
                        Reply("OK exit");
                        return 0;
                    default:
                        Reply($"ERR unknown {a[0]}");
                        break;
                }
            }
            catch (Exception ex)
            {
                var chain = new List<string>();
                for (var t = ex.GetType(); t != null && t != typeof(object); t = t.BaseType) chain.Add(t.Name);
                Reply($"ERR {a[0]} {string.Join('<', chain)} {ex.Message.Replace('\n', ' ').Replace('\r', ' ')}");
            }
        }
        StopStream();
        return 0;
    }

    static HMProfile Profile(HMContext ctx, string kind, string pid) => kind switch
    {
        "pad" => Pad(Convert.ToUInt16(pid, 16)),
        "xbox" => ctx.GetProfile("xbox-360-wired")!,
        "composite" => ctx.GetProfile("dualsense-composite")!,
        "gc" => ctx.GetProfile("gamecube-adapter")!,
        _ => throw new ArgumentException(kind),
    };

    /// <summary>A gamepad nothing else on a machine has: VID BEEF, one stick, eight buttons.</summary>
    public static HMProfile Pad(ushort pid) =>
        new HMProfileBuilder().Id($"coexist-{pid:x4}").Name($"HIDMaestro Coexist {pid:X4}").Vendor("HIDMaestro")
            .Vid(0xBEEF).Pid(pid).ProductString($"HIDMaestro Coexist {pid:X4}").ManufacturerString("HIDMaestro")
            .Type("gamepad").Connection("usb")
            .FromDescriptorBuilder(new HidDescriptorBuilder().Gamepad().AddStick("Left").AddButtons(8))
            .Build();

    static HMContext NewContext()
    {
        var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        return ctx;
    }

    /// <summary>The uninstall-grade cleanup. From v1.13.0 the existing
    /// overloads leave live programs' controllers alone, and the scope
    /// argument asks for everything. Before it they removed everything.</summary>
    static void RemoveEverything(bool preserveInstall)
    {
        Type? scope = typeof(HMContext).Assembly.GetType("HIDMaestro.HMCleanupScope");
        if (scope != null)
        {
            MethodInfo m = typeof(HMContext).GetMethod("RemoveAllVirtualControllers", new[] { scope, typeof(bool) })
                ?? throw new MissingMethodException("HMContext.RemoveAllVirtualControllers(HMCleanupScope, bool)");
            m.Invoke(null, new[] { Enum.Parse(scope, "Everything"), (object)preserveInstall });
        }
        else if (preserveInstall) HMContext.RemoveAllVirtualControllers(preserveInstall: true);
        else HMContext.RemoveAllVirtualControllers();
    }

    static void StartStream(int button, float x, int hz, bool ramp)
    {
        StopStream();
        lock (s_intervals) s_intervals.Clear();
        s_streaming = true;
        s_streamer = new Thread(() =>
        {
            long step = Stopwatch.Frequency / hz, next = Stopwatch.GetTimestamp(), last = 0;
            var axes = new Dictionary<HMAxis, float> { [HMAxis.X] = x, [HMAxis.Y] = 0.5f };
            var state = new HMGamepadState { Axes = axes, Buttons = (HMButton)(1u << button) };
            int frame = 0;
            while (s_streaming)
            {
                if (ramp) axes[HMAxis.X] = x + (frame++ % 64) / 65535f;
                HMController[] live;
                lock (s_live) live = s_live.ToArray();
                foreach (var c in live)
                {
                    try { c.SubmitState(in state); } catch { }
                }
                long now = Stopwatch.GetTimestamp();
                if (last != 0) lock (s_intervals) s_intervals.Add((now - last) * 1000.0 / Stopwatch.Frequency);
                last = now;
                next += step;
                while (Stopwatch.GetTimestamp() < next) Thread.Sleep(0);
            }
        }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
        s_streamer.Start();
    }

    static void StopStream()
    {
        s_streaming = false;
        s_streamer?.Join(2000);
        s_streamer = null;
    }

    /// <summary>Set one of the SDK's internal static Action&lt;int&gt;
    /// hooks to run <paramref name="body"/> once, on the next call.</summary>
    static bool SetHook(string type, string field, Action body) => SetHook(type, field, (int _) => body());

    /// <summary>The same, handing <paramref name="body"/> the hook's
    /// argument.</summary>
    static bool SetHook(string type, string field, Action<int> body)
    {
        var f = typeof(HMContext).Assembly.GetType(type)?.GetField(field, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (f == null) return false;
        f.SetValue(null, (Action<int>)(n =>
        {
            f.SetValue(null, null);
            body(n);
        }));
        return true;
    }

    /// <summary>The output doorbell opened by the SDK's own code, read
    /// through reflection, and waited on for no time. Then the StopEvent
    /// opened and signaled with the access the SDK's sweep asks for
    /// (DeviceOrchestrator.SignalStopEventsAndDrain), from this program's
    /// token.</summary>
    static string SdkEvents(int index)
    {
        var open = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.SharedMemoryIO")
            ?.GetMethod("TryOpenOutputEvent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (open == null) return "ERR sdkevents no SharedMemoryIO.TryOpenOutputEvent";
        IntPtr ev = (IntPtr)open.Invoke(null, new object[] { index })!;
        uint wait = ev == IntPtr.Zero ? uint.MaxValue : WaitForSingleObject(ev, 0);
        if (ev != IntPtr.Zero) CloseHandle(ev);
        IntPtr stop = OpenEventW(0x0002 /*EVENT_MODIFY_STATE*/, false, $@"Global\HIDMaestroStopEvent{index}");
        bool signaled = stop != IntPtr.Zero && SetEvent(stop);
        if (stop != IntPtr.Zero) CloseHandle(stop);
        return $"OK sdkevents output={(ev != IntPtr.Zero ? 1 : 0)} wait={wait} stop={(signaled ? 1 : 0)}";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenEventW(uint access, bool inherit, string name);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetEvent(IntPtr h);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenThread(uint access, bool inherit, uint id);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern int GetThreadDescription(IntPtr thread, out IntPtr description);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr p);

    /// <summary>Threads of this process whose name starts with
    /// <paramref name="prefix"/>. The runtime gives a named managed thread
    /// that name as its thread description.</summary>
    static int CountThreads(string prefix)
    {
        int n = 0;
        using var self = Process.GetCurrentProcess();
        foreach (ProcessThread t in self.Threads)
        {
            IntPtr h = OpenThread(0x0800 /*THREAD_QUERY_LIMITED_INFORMATION*/, false, (uint)t.Id);
            if (h == IntPtr.Zero) continue;
            try
            {
                if (GetThreadDescription(h, out IntPtr d) >= 0 && d != IntPtr.Zero)
                {
                    string? name = System.Runtime.InteropServices.Marshal.PtrToStringUni(d);
                    LocalFree(d);
                    if (name != null && name.StartsWith(prefix, StringComparison.Ordinal)) n++;
                }
            }
            finally { CloseHandle(h); }
        }
        return n;
    }

    static string? Get(object o, string name) =>
        o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(o)?.ToString();

    static void Reply(string s)
    {
        Console.WriteLine(s);
        Console.Out.Flush();
    }
}
