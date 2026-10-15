// One program that embeds HIDMaestro, driven line by line over stdin by the
// probe. Each reply is one stdout line starting with OK or ERR. Everything
// a release before v1.13.0 lacks is read through reflection, so this file
// compiles unchanged against an older HIDMaestro.Core.dll and runs as a
// program built on that release.
//
//   install                     HMContext.InstallDriver
//   create pad <pid> <key|->    a custom gamepad, VID BEEF and this PID
//   create gipad <pid> <key|->  a custom gamepad in the shape the SDK writes
//                               a GameInput table for, VID BEEF
//   create xbox - <key|->       xbox-360-wired
//   create composite - <key|->  dualsense-composite (USB/IP)
//   create gc - <key|->         gamecube-adapter (USB/IP)
//   create xinputhid - <key|->  xbox-elite-v2, a gamepad companion
//   create xboxbt - <key|->     xbox-series-xs-bt, a gamepad companion
//   createat <slot> <kind> <pid|->  CreateControllerAt(slot, profile), the
//                               same kinds as create, no identity key
//   stream <btn> <x> <hz> <ramp>  submit at <hz> to every live controller:
//                               button <btn> held, X at <x>, and with
//                               ramp 1, X stepping by one count a frame
//   stopstream                  stop, and report the submit intervals
//   frames                      how many SubmitState calls of the current
//                               stream returned without throwing: n=<count>
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
//   pausereclaim                the next sweep stops once it holds the
//                               claims of the groups it is about to remove,
//                               replies "OK paused targets=<n>" and goes on
//                               at the next line
//   alive                       reply, to show the program still runs
//   oemset <pid>                HMOemNameOverride.Set for BEEF:<pid>
//   oemrecover                  HMOemNameOverride.RecoverOrphans
//   sdkevents <index>           open the index's output doorbell the way
//                               the SDK does (SharedMemoryIO.TryOpenOutputEvent),
//                               wait on it without blocking, and signal its
//                               StopEvent as the SDK's sweep does
//   dropinputevent <index>      close this program's handle to the index's
//                               input event, as a program that let go of
//                               it would, and keep the owner pipe open.
//                               Each controller on the index keeps its own
//                               copy of the handle value and signals it per
//                               frame, so that copy is pointed at a new
//                               unnamed event, made before the close so its
//                               value differs: swapped=<controllers>,
//                               verified=<copies read back>,
//                               matched=<copies that were the closed handle>
//   droppedsignal <index>       whether a frame signaled that index's
//                               unnamed event since the last ask:
//                               signaled=0|1
//   phantomstats                wait for this program's phantom cleanup,
//                               up to 120 s, and reply what its run did
//                               (OwnerSweep.LastPhantomCleanup): removed,
//                               totalms, ids, waitedms, unsettled
//   census                      the SDK's census of present devices, one
//                               token|index|owner|watch per device group
//   releases <index>            SharedMemoryIO.ReleaseOnExitSupported, the
//                               read behind HMController.ReleasesOnConsumerExit
//   outputs on                  note the Stopwatch timestamp of every
//                               OutputReceived on the live controllers
//   outputs                     reply with those timestamps, t=a,b,...
//   classify <instance>         the SDK's owner check of one devnode
//                               (OwnerCensus.Classify), which a create runs
//                               on a rival before it removes it
//   droplease <key|->           take a transport index and identity the
//                               way a create does (ControllerLease.Acquire),
//                               keep no reference to the lease, collect
//                               garbage, and reply token=<t> transport=<n>
//                               and control=<1 when an unreferenced claim
//                               taken beside it was closed by the collection>
//   readallrace                 write three owner records of a killed
//                               program, delete the first while the SDK's
//                               MachineClaims.ReadAll reads it, and reply
//                               listed=<how many of the other two it read>
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
//   sddl <type> <field>         the security descriptor string a
//                               HIDMaestro.Internal type creates its
//                               objects with, read from its constant
//   claimrefused                create a pipe under a protected prefix that
//                               does not exist, and reply with the error
//                               and what the SDK's MachineClaims.RefusedClaim
//                               makes of it
//   dispose                     dispose every controller
//   exit                        dispose the context and exit
//   quit                        exit without disposing anything, so only
//                               the SDK's exit handler cleans up
//   quitmidcreate <kind> <pid> <key|->  start a create on another thread,
//                               hold it before its input mapping, reply
//                               index=<n> token=<t> and exit. A handler of
//                               this program's own, which runs after the
//                               SDK's, lets the create finish first
//   quitdispose                 exit, with a handler of this program's own,
//                               which runs after the SDK's, that disposes
//                               the context
//   quitthencreate <kind> <pid> <key|->  reply token=<t> and exit. A
//                               handler of this program's own, which runs
//                               after the SDK's, creates the controller
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
    static int s_framesSent;
    static readonly List<double> s_intervals = new();
    static readonly List<long> s_outputTimes = new();

    /// <summary>The departing-devnode rule (WinUsbBinding.DepartingDevnodeRule)
    /// below which bindwinusb refuses to bind.</summary>
    const int BindRule = 6;

    /// <summary>Commands this file does not know, for a section whose own
    /// file only the current SDK's probe compiles (SectionZ.cs). Returns
    /// the reply, or null for an unknown command.</summary>
    public static Func<string[], string?>? Extra { get; set; }

    /// <summary>The controller this program created last, or null.</summary>
    public static HMController? Last()
    {
        lock (s_live) return s_live.Count > 0 ? s_live[^1] : null;
    }

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
                    case "frames":
                        Reply($"OK frames n={Volatile.Read(ref s_framesSent)}");
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
                    case "claimrefused":
                    {
                        string name = $@"\\.\pipe\ProtectedPrefix\NoSuchPrefix\CoexistCheck.{Environment.ProcessId}";
                        IntPtr pipe = CreateNamedPipeW(name, 0x00000001 | 0x00080000, 0x00000008, 1, 0, 0, 0, IntPtr.Zero);
                        int err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                        if (pipe != new IntPtr(-1)) { CloseHandle(pipe); Reply("ERR claimrefused the pipe was created"); break; }
                        var claims = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.MachineClaims");
                        var refused = claims?.GetMethod("RefusedClaim", BindingFlags.NonPublic | BindingFlags.Static);
                        if (refused == null) { Reply($"ERR claimrefused error={err} no MachineClaims.RefusedClaim"); break; }
                        var ex = (Exception)refused.Invoke(null, new object[] { err })!;
                        Reply($"OK claimrefused error={err} type={ex.GetType().Name} {ex.Message}");
                        break;
                    }
                    case "sddl":
                    {
                        var type = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal." + a[1]);
                        var field = type?.GetField(a[2], BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (field?.GetRawConstantValue() is not string sddl) { Reply($"ERR sddl no {a[1]}.{a[2]}"); break; }
                        Reply($"OK sddl value={sddl}");
                        break;
                    }
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
                        string dir;
                        try { dir = (string)ensure.Invoke(null, null)!; }
                        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
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
                    case "pausereclaim":
                        Reply(SetHook("HIDMaestro.Internal.OwnerSweep", "BeforeReclaimActs", targets =>
                              {
                                  Reply($"OK paused targets={targets}");
                                  Console.ReadLine();
                              })
                              ? "OK pausereclaim" : "ERR pausereclaim no hook");
                        break;
                    case "alive":
                        Reply("OK alive");
                        break;
                    case "pauseprune":
                    {
                        // A deploy's staging prune pauses after it reads the
                        // owner records and before it deletes anything, until
                        // the probe sends a line.
                        var f = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.ProtectedStaging")
                            ?.GetField("AfterPruneRead", BindingFlags.NonPublic | BindingFlags.Static);
                        if (f == null || f.FieldType != typeof(Action)) { Reply("ERR pauseprune no hook"); break; }
                        f.SetValue(null, (Action)(() =>
                        {
                            f.SetValue(null, null);
                            Reply("OK paused");
                            Console.ReadLine();
                        }));
                        Reply("OK pauseprune");
                        break;
                    }
                    case "fakebuild":
                    {
                        // This process takes the payload hash given, as a
                        // build with another driver payload would, so its
                        // staging directory is another build's. Must come
                        // before anything reads the hash.
                        var f = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.EmbeddedManifest")
                            ?.GetField("s_cachedHash", BindingFlags.NonPublic | BindingFlags.Static);
                        if (f == null) { Reply("ERR fakebuild no hash field"); break; }
                        f.SetValue(null, a[1]);
                        Reply("OK fakebuild");
                        break;
                    }
                    case "addpackages":
                    {
                        // The deploy policy's add of a package a set lacks,
                        // under the deploy lock as the policy calls it.
                        var asm = typeof(HMContext).Assembly;
                        var acquire = asm.GetType("HIDMaestro.Internal.DriverDeploy")
                            ?.GetMethod("Acquire", BindingFlags.NonPublic | BindingFlags.Static);
                        var add = asm.GetType("HIDMaestro.Internal.DriverBuilder")
                            ?.GetMethod("AddPackagesLocked", BindingFlags.NonPublic | BindingFlags.Static);
                        if (acquire == null || add == null) { Reply("ERR addpackages no method"); break; }
                        using var m = (Mutex)acquire.Invoke(null, null)!;
                        try { add.Invoke(null, new object[] { (IReadOnlyCollection<string>)a.Skip(1).ToArray() }); }
                        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
                        finally { m.ReleaseMutex(); }
                        Reply("OK addpackages");
                        break;
                    }
                    case "runtool":
                    {
                        // DriverBuilder.Run, the SDK's runner for signtool,
                        // inf2cat and pnputil, on a tool that exits at once
                        // and leaves a child that holds the same output for
                        // a[1] seconds (2 when absent) and then writes its
                        // last line. "beyond" holds it 5 s past the runner's
                        // drain bound as this process scales it
                        // (HIDMAESTRO_TIMEOUT_SCALE), so the two timers move
                        // together.
                        var builder = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.DriverBuilder");
                        var m = builder?.GetMethod("Run", BindingFlags.NonPublic | BindingFlags.Static);
                        if (m == null) { Reply("ERR runtool no DriverBuilder.Run"); break; }
                        int drain = -1;
                        int hold = 2;
                        if (a.Length > 1 && a[1] == "beyond")
                        {
                            var drainMs = builder!.GetField("OutputDrainMs", BindingFlags.NonPublic | BindingFlags.Static);
                            var apply = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.TimeoutScale")
                                ?.GetMethod("Apply", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(int) });
                            if (drainMs == null || apply == null) { Reply("ERR runtool no drain bound"); break; }
                            drain = (int)apply.Invoke(null, new object[] { (int)drainMs.GetValue(null)! })!;
                            hold = drain / 1000 + 5;
                        }
                        else if (a.Length > 1) hold = int.Parse(a[1], CultureInfo.InvariantCulture);
                        string cmd = System.IO.Path.Combine(Environment.SystemDirectory, "cmd.exe");
                        var sw = Stopwatch.StartNew();
                        object? r = m.Invoke(null, new object?[]
                        {
                            cmd, $"/d /c start \"\" /b \"{cmd}\" /d /c \"ping -n {hold + 1} 127.0.0.1 >nul & echo coexist-last-line\"", null, 30_000,
                        });
                        long ms = sw.ElapsedMilliseconds;
                        object? code = r?.GetType().GetField("Item1")?.GetValue(r);
                        string output = r?.GetType().GetField("Item2")?.GetValue(r) as string ?? "";
                        Reply($"OK runtool last={(output.Contains("coexist-last-line", StringComparison.Ordinal) ? "yes" : "no")} " +
                              $"open={(output.Contains("output still open", StringComparison.Ordinal) ? "yes" : "no")} exit={code} ms={ms} drain={drain}");
                        break;
                    }
                    case "builderstep":
                    {
                        // One of the public DriverBuilder steps by name:
                        // SignDrivers, GenerateCatalogs, InstallDrivers or
                        // RemoveOldDriverPackages.
                        var m = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.DriverBuilder")
                            ?.GetMethod(a[1], BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
                        if (m == null) { Reply($"ERR builderstep no DriverBuilder.{a[1]}"); break; }
                        var sw = Stopwatch.StartNew();
                        object? result;
                        try { result = m.Invoke(null, null); }
                        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
                        Reply($"OK builderstep {a[1]} result={result?.ToString() ?? "none"} ms={sw.ElapsedMilliseconds}");
                        break;
                    }
                    case "vrregister":
                    {
                        var m = typeof(HMContext).Assembly.GetType("HIDMaestro.HMVR")
                            ?.GetMethod("EnsureDriverRegistered", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
                        if (m == null) { Reply("ERR vrregister no HMVR.EnsureDriverRegistered"); break; }
                        object? r;
                        try { r = m.Invoke(null, null); }
                        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
                        Reply($"OK vrregister {r}");
                        break;
                    }
                    case "oemset":
                    {
                        ushort pid = Convert.ToUInt16(a[1], 16);
                        HMOemNameOverride.Set(0xBEEF, pid, $"HIDMaestro Coexist OEM {pid:X4}");
                        Reply($"OK oemset {pid:X4}");
                        break;
                    }
                    case "oemrecover":
                        Reply($"OK oemrecover restored={HMOemNameOverride.RecoverOrphans()}");
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
                        // A bind on an SDK below the departing-devnode rule
                        // can change a devnode on its way out at dispose.
                        object? rule = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.WinUsbBinding")
                            ?.GetField("DepartingDevnodeRule", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                        if (rule is not int r || r < BindRule) { Reply($"ERR bindwinusb the SDK's departing-devnode rule is {rule ?? "none"}, below {BindRule}"); break; }
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
                    case "dropinputevent":
                    {
                        var events = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.SharedMemoryIO")
                            ?.GetField("s_inputEvents", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Dictionary<int, IntPtr>;
                        if (events == null) { Reply("ERR dropinputevent no SharedMemoryIO.s_inputEvents"); break; }
                        int i = int.Parse(a[1], CultureInfo.InvariantCulture);
                        // The new event exists before the SDK's handle is
                        // closed, so it cannot take that handle's value, and
                        // a signal on it can only come from a controller
                        // that now holds it.
                        IntPtr fresh = CreateEventW(IntPtr.Zero, false, false, null);
                        // The entry is zeroed so the SDK's own cleanup does
                        // not close the handle a second time.
                        bool closed = events.TryGetValue(i, out IntPtr ev) && ev != IntPtr.Zero && CloseHandle(ev);
                        if (closed) events[i] = IntPtr.Zero;
                        var copy = typeof(HMController).GetField("_inputEvent", BindingFlags.NonPublic | BindingFlags.Instance);
                        int swapped = 0, verified = 0, matched = 0;
                        if (closed && copy != null && fresh != IntPtr.Zero)
                        {
                            lock (s_live)
                                foreach (var c in s_live)
                                    if (Get(c, "TransportIndex") == a[1] || Get(c, "Index") == a[1])
                                    {
                                        // The copy must have been the handle the SDK
                                        // closed. A controller with a handle of its own
                                        // would keep signaling the index's event.
                                        if (copy.GetValue(c) is IntPtr before && before == ev) matched++;
                                        copy.SetValue(c, fresh);
                                        swapped++;
                                        if (copy.GetValue(c) is IntPtr now && now == fresh) verified++;
                                    }
                            // A previous drop on this index left its event with
                            // controllers that now hold the new one.
                            if (s_dropped.TryGetValue(i, out IntPtr previous)) CloseHandle(previous);
                            s_dropped[i] = fresh;
                        }
                        else if (fresh != IntPtr.Zero) CloseHandle(fresh);
                        Reply($"OK dropinputevent closed={(closed ? 1 : 0)} swapped={swapped} verified={verified} matched={matched}");
                        break;
                    }
                    case "droppedsignal":
                        Reply($"OK droppedsignal signaled={(s_dropped.TryGetValue(int.Parse(a[1], CultureInfo.InvariantCulture), out IntPtr d) && WaitForSingleObject(d, 0) == 0 ? 1 : 0)}");
                        break;
                    case "phantomstats":
                    {
                        var sweep = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.OwnerSweep");
                        var join = sweep?.GetMethod("JoinPhantomCleanup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(int) });
                        var last = sweep?.GetProperty("LastPhantomCleanup", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        if (join == null || last == null) { Reply("ERR phantomstats no OwnerSweep.LastPhantomCleanup"); break; }
                        bool ended = (bool)join.Invoke(null, new object[] { 120_000 })!;
                        object? r = last.GetValue(null);
                        Reply(r == null ? $"OK phantomstats ran=0 ended={(ended ? 1 : 0)}"
                            : $"OK phantomstats ran=1 ended={(ended ? 1 : 0)} removed={Get(r, "Removed")} totalms={Get(r, "TotalMs")} ids={Get(r, "Ids")} " +
                              $"waitedms={Get(r, "WaitedMs")} unsettled={Get(r, "Unsettled")}");
                        break;
                    }
                    case "census":
                        Reply(Census());
                        break;
                    case "outputs":
                        if (a.Length > 1 && a[1] == "on")
                        {
                            lock (s_live)
                                foreach (var c in s_live)
                                    c.OutputReceived += (_, _) => { lock (s_outputTimes) s_outputTimes.Add(Stopwatch.GetTimestamp()); };
                            Reply("OK outputs on");
                        }
                        else
                        {
                            long[] t;
                            lock (s_outputTimes) t = s_outputTimes.ToArray();
                            Reply($"OK outputs n={t.Length} t={string.Join(',', t)}");
                        }
                        break;
                    case "releases":
                    {
                        var read = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.SharedMemoryIO")
                            ?.GetMethod("ReleaseOnExitSupported", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                        Reply(read == null ? "ERR releases no SharedMemoryIO.ReleaseOnExitSupported"
                              : $"OK releases on={((bool)read.Invoke(null, new object[] { int.Parse(a[1]) })! ? 1 : 0)}");
                        break;
                    }
                    case "classify":
                    {
                        var m = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.OwnerCensus")
                            ?.GetMethod("Classify", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(string) });
                        Reply(m == null ? "ERR classify no OwnerCensus.Classify" : $"OK classify owner={m.Invoke(null, new object[] { a[1] })}");
                        break;
                    }
                    case "droplease":
                        Reply(DropLease(a[1] == "-" ? null : a[1]));
                        break;
                    case "readallrace":
                        Reply(ReadAllRace());
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
                    case "quitmidcreate":
                    {
                        s_ctx ??= NewContext();
                        HMContext ctx = s_ctx;
                        HMProfile profile = Profile(ctx, a[1], a[2]);
                        string? key = a.Length > 3 && a[3] != "-" ? a[3] : null;
                        var paused = new ManualResetEventSlim();
                        var release = new ManualResetEventSlim();
                        int index = -1;
                        if (!SetHook("HIDMaestro.Internal.DeviceOrchestrator", "BeforeInputMapping", n =>
                            {
                                index = n;
                                paused.Set();
                                release.Wait();
                            }))
                        {
                            Reply("ERR quitmidcreate no hook");
                            break;
                        }
                        var create = new Thread(() =>
                        {
                            try { ctx.CreateController(profile, key); } catch { }
                        }) { IsBackground = true };
                        create.Start();
                        if (!paused.Wait(60_000)) { Reply("ERR quitmidcreate the create never reached its input mapping"); break; }
                        // Subscribed after the SDK's handler, which the
                        // create's claim registered, so it runs after it.
                        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                        {
                            release.Set();
                            create.Join(60_000);
                        };
                        Reply($"OK quitmidcreate index={index} token={Token(key, index)}");
                        Environment.Exit(0);
                        return 0;
                    }
                    case "quitdispose":
                    {
                        HMContext? ctx = s_ctx;
                        // Subscribed after the SDK's handler, which this
                        // program's earlier creates registered.
                        AppDomain.CurrentDomain.ProcessExit += (_, _) => ctx?.Dispose();
                        StopStream();
                        Reply("OK quitdispose");
                        Environment.Exit(0);
                        return 0;
                    }
                    case "quitthencreate":
                    {
                        s_ctx ??= NewContext();
                        HMContext ctx = s_ctx;
                        HMProfile profile = Profile(ctx, a[1], a[2]);
                        string? key = a.Length > 3 && a[3] != "-" ? a[3] : null;
                        // The SDK's handler was registered by this program's
                        // earlier create, so this one runs after it.
                        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                        {
                            try { ctx.CreateController(profile, key); } catch { }
                        };
                        Reply($"OK quitthencreate token={Token(key, 0)}");
                        Environment.Exit(0);
                        return 0;
                    }
                    case "exit":
                        StopStream();
                        s_ctx?.Dispose();
                        Reply("OK exit");
                        return 0;
                    default:
                        Reply(Extra?.Invoke(a) ?? $"ERR unknown {a[0]}");
                        break;
                }
            }
            catch (Exception ex)
            {
                var chain = new List<string>();
                for (var t = ex.GetType(); t != null && t != typeof(object); t = t.BaseType) chain.Add(t.Name);
                // The Win32 error code, which reads the same in every
                // display language, beside the message, which does not.
                string code = ex is System.ComponentModel.Win32Exception w ? $" win32={w.NativeErrorCode}" : "";
                Reply($"ERR {a[0]} {string.Join('<', chain)}{code} {ex.Message.Replace('\n', ' ').Replace('\r', ' ')}");
            }
        }
        StopStream();
        return 0;
    }

    static HMProfile Profile(HMContext ctx, string kind, string pid) => kind switch
    {
        "pad" => Pad(Convert.ToUInt16(pid, 16)),
        "gipad" => GameInputPad(Convert.ToUInt16(pid, 16)),
        "xbox" => ctx.GetProfile("xbox-360-wired")!,
        "composite" => ctx.GetProfile("dualsense-composite")!,
        "gc" => ctx.GetProfile("gamecube-adapter")!,
        "ds4" => ctx.GetProfile("dualshock-4-v2")!,
        "xinputhid" => ctx.GetProfile("xbox-elite-v2")!,
        "xboxbt" => ctx.GetProfile("xbox-series-xs-bt")!,
        _ => throw new ArgumentException(kind),
    };

    /// <summary>A gamepad nothing else on a machine has: VID BEEF, one stick, eight buttons.</summary>
    public static HMProfile Pad(ushort pid) =>
        new HMProfileBuilder().Id($"coexist-{pid:x4}").Name($"HIDMaestro Coexist {pid:X4}").Vendor("HIDMaestro")
            .Vid(0xBEEF).Pid(pid).ProductString($"HIDMaestro Coexist {pid:X4}").ManufacturerString("HIDMaestro")
            .Type("gamepad").Connection("usb")
            .FromDescriptorBuilder(new HidDescriptorBuilder().Gamepad().AddStick("Left").AddButtons(8))
            .Build();

    /// <summary>A gamepad in the one descriptor shape the SDK writes a
    /// GameInput table for (GameInputMapping.HasMeasuredShape): two sticks,
    /// two triggers, a hat and ten buttons, built in code. VID BEEF.</summary>
    public static HMProfile GameInputPad(ushort pid) =>
        new HMProfileBuilder().Id($"coexist-gi-{pid:x4}").Name($"HIDMaestro Coexist GI {pid:X4}").Vendor("HIDMaestro")
            .Vid(0xBEEF).Pid(pid).ProductString($"HIDMaestro Coexist GI {pid:X4}").ManufacturerString("HIDMaestro")
            .Type("gamepad").Connection("usb")
            .FromDescriptorBuilder(new HidDescriptorBuilder().Gamepad()
                .AddStick("Left").AddStick("Right").AddTrigger("Left").AddTrigger("Right").AddHat().AddButtons(10))
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
        Volatile.Write(ref s_framesSent, 0);
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
                    try
                    {
                        c.SubmitState(in state);
                        Interlocked.Increment(ref s_framesSent);
                    }
                    catch { }
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

    /// <summary>OwnerCensus.Take() and, where the SDK has it,
    /// OwnerCensus.DescribeOwnerWatch for each device group.</summary>
    static string Census()
    {
        var type = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.OwnerCensus");
        object? census = type?.GetMethod("Take", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes)?.Invoke(null, null);
        if (census == null) return "ERR census no OwnerCensus.Take";
        var describe = type!.GetMethod("DescribeOwnerWatch", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var parts = new List<string>();
        foreach (object g in (System.Collections.IEnumerable)census.GetType().GetProperty("Devices")!.GetValue(census)!)
        {
            string watch = describe == null ? "unsupported" : (string)describe.Invoke(null, new[] { g })!;
            parts.Add($"{Get(g, "Key")}|{Get(g, "ControllerIndex")}|{Get(g, "Owner")}|watch={watch}");
        }
        return parts.Count == 0 ? "OK census" : "OK census " + string.Join(' ', parts);
    }

    /// <summary>The identity token a create of <paramref name="key"/> on
    /// transport index <paramref name="index"/> takes. A key's token does
    /// not depend on the index.</summary>
    static string? Token(string? key, int index)
    {
        object? identity = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.DeviceIdentity")
            ?.GetMethod("ForKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(string), typeof(int) })
            ?.Invoke(null, new object?[] { key, index });
        return identity == null ? null : Get(identity, "Token");
    }

    /// <summary>A lease nothing references, then a full collection. A claim
    /// taken beside it and dropped the same way is the control: it shows the
    /// collection closed what nothing keeps.</summary>
    static string DropLease(string? key)
    {
        var asm = typeof(HMContext).Assembly;
        var acquire = asm.GetType("HIDMaestro.Internal.ControllerLease")
            ?.GetMethod("Acquire", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var claim = asm.GetType("HIDMaestro.Internal.MachineClaims")
            ?.GetMethod("TryClaim", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (acquire == null || claim == null) return "ERR droplease no ControllerLease.Acquire";
        const string control = @"ProtectedPrefix\Administrators\HIDMaestro.Probe.W7";
        string taken = TakeAndDrop(acquire, claim, key, control);
        if (taken.StartsWith("ERR", StringComparison.Ordinal)) return taken;
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        bool controlClosed = !WaitNamedPipeHeld(control);
        return $"OK droplease {taken} control={(controlClosed ? 1 : 0)}";
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static string TakeAndDrop(MethodInfo acquire, MethodInfo claim, string? key, string control)
    {
        if (claim.Invoke(null, new object[] { control }) == null) return "ERR droplease the control claim is taken";
        object lease = acquire.Invoke(null, new object?[] { 0, key, "umdf", "coexist-w7" })!;
        object identity = lease.GetType().GetProperty("Identity")!.GetValue(lease)!;
        return $"token={identity.GetType().GetProperty("Token")!.GetValue(identity)} transport={lease.GetType().GetProperty("TransportIndex")!.GetValue(lease)}";
    }

    static bool WaitNamedPipeHeld(string name)
    {
        if (WaitNamedPipeW(@"\\.\pipe\" + name, 1)) return true;
        return System.Runtime.InteropServices.Marshal.GetLastWin32Error() != 2 /*ERROR_FILE_NOT_FOUND*/;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool WaitNamedPipeW(string name, uint timeout);

    /// <summary>Three records the way a killed program on this SDK leaves
    /// them, read by the SDK's MachineClaims.ReadAll while its
    /// AfterRecordOpened hook deletes the first, as another program's sweep
    /// deletes a dead record. The ids sort after any GUID's, in this order.</summary>
    static string ReadAllRace()
    {
        var claims = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.MachineClaims");
        var hook = claims?.GetField("AfterRecordOpened", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        var readAll = claims?.GetMethod("ReadAll", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes);
        if (hook == null || readAll == null) return "ERR readallrace no MachineClaims.ReadAll hook";
        const string root = @"SOFTWARE\HIDMaestroOwners";
        string[] ids = { "ffffffffffffffffffffffffffff0001", "ffffffffffffffffffffffffffff0002", "ffffffffffffffffffffffffffff0003" };
        for (int i = 0; i < ids.Length; i++)
        {
            using var r = Microsoft.Win32.Registry.LocalMachine.CreateSubKey($@"{root}\{ids[i]}");
            r.SetValue("Pid", 0, Microsoft.Win32.RegistryValueKind.DWord);
            r.SetValue("SdkVersion", "1.13.0", Microsoft.Win32.RegistryValueKind.String);
            using var c = r.CreateSubKey($@"Controllers\HM_COEXISTW{i}");
            c.SetValue("TransportIndex", 4000 + i, Microsoft.Win32.RegistryValueKind.DWord);
            c.SetValue("Backend", "umdf", Microsoft.Win32.RegistryValueKind.String);
        }
        bool deleted = false;
        hook.SetValue(null, (Action<string>)(id =>
        {
            if (deleted || id != ids[0]) return;
            deleted = true;
            Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree($@"{root}\{ids[0]}", throwOnMissingSubKey: false);
        }));
        int listed = 0;
        try
        {
            foreach (object r in (System.Collections.IEnumerable)readAll.Invoke(null, null)!)
                if (Get(r, "OwnerId") is string id && (id == ids[1] || id == ids[2])) listed++;
        }
        finally
        {
            hook.SetValue(null, null);
            foreach (string id in ids)
                Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree($@"{root}\{id}", throwOnMissingSubKey: false);
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root);
            bool empty = k != null && k.SubKeyCount == 0 && k.ValueCount == 0;
            k?.Close();
            if (empty) Microsoft.Win32.Registry.LocalMachine.DeleteSubKey(root, throwOnMissingSubKey: false);
        }
        return $"OK readallrace listed={listed} deleted={(deleted ? 1 : 0)}";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenEventW(uint access, bool inherit, string name);
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outSize, uint inSize, uint timeout, IntPtr sa);
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
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string? name);
    /// <summary>The unnamed event dropinputevent gave each index's
    /// controllers.</summary>
    static readonly Dictionary<int, IntPtr> s_dropped = new();
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
