// The budgets of the coexistence design, measured the same way before and after
// on one machine: built against this tree, and with -p:HmBase=true against a
// b4d29d9 tree. The parts that measure what b4d29d9 lacks, the ownership
// claims and the controller ports, are left out of that build. The budgets
// of the v2.0.0 conversion (docs/design/data-driven-devices.md, "Budgets")
// take their baseline from a 9bd6279 tree: -p:HmBaselineTree=<checkout>
// builds this source against that checkout's SDK (PerfBudgetCheck.csproj).
//
//   frame [runs] [kbm] [--settle=<ms>] [--key=<identity>]
//                  time and bytes allocated per call on each submit path,
//                  the keyboard, mouse, consumer and pointer paths and
//                  steam-controller-2 (a keyboard and mouse in lizard
//                  mode) only with kbm, since those devices type and move
//                  the pointer on the desktop,
//                  and outside the gate SubmitState on dualsense-bt once
//                  its feature reads have armed it, steam-controller-2, the
//                  Switch 2 Pro composite, the two Luna profiles and
//                  switch-pro, with its protocol host's tick. With
//                  --settle each measurement waits that long first, past
//                  what Windows starts on a new device
//   e2e [runs] [profile[@report...] ...]
//                  SubmitState to the driver's acknowledgement that a read
//                  carried the frame, on a DualShock 4 and an Xbox 360 pad,
//                  or the profiles named. A profile named is also read as a
//                  host reads it: each output report given is written
//                  first, then the time runs from SubmitState to the
//                  ReadFile that returns a report carrying the new state,
//                  and on a profile XInput lists, to the XInputGetState
//                  that shows it
//   feature [runs] [profile[@report...] ...]
//                  feature answers as a host asks for them: HidD_GetFeature
//                  on a UMDF2 profile, GET_REPORT(Feature) on a USB/IP one
//                  in this process. dualsense-bt, dualshock-3-full and
//                  dualsense-composite unless profiles are named
//   output [runs] [profile@report ...]
//                  an output report to OutputReceived, and bytes allocated
//                  per report anywhere in the process: report 05 on a
//                  DualShock 4, or the report given for each profile
//   signal [runs] [--key=<identity>]
//                  live: on a DualShock 4 and an Xbox 360 pad, SetEvent on
//                  the input event and the companion's doorbell alone, and a
//                  frame written to the section without and with its signal,
//                  and to this process's own memory (SignalLive.cs). It
//                  builds against b4d29d9 as is
//   rate [runs] [seconds] [profile ...]
//                  sustained throughput: SubmitState every millisecond and
//                  back to back, with a reader in a child process, the
//                  reports read per second and the CPU cycles per report of
//                  the driver hosts, the reader and this process, on a
//                  DualShock 4 and an Xbox 360 pad or the profiles named
//                  (RateLive.cs)
//   create [runs] [profile ...] [--settle=<ms>]
//                  CreateController and Dispose for four kinds of device (a
//                  keyboard only when named), or
//                  the catalog profiles named, with the SDK's own step
//                  timings (HIDMAESTRO_TIMING), each controller up for <ms>
//                  first
//   report [runs] [calls]
//                  offline, no elevation: one launch of the hot path's cost
//                  under the one arithmetic, BuildReportInto, EncodeInput and
//                  SubmitState on an offline controller, each variant's
//                  median ns per call (ReportCost.cs). A script takes p50 and
//                  p95 over 30 launches or more, this tree's and the tree's
//                  before
//   program [runs] [calls]
//                  offline: the Switch 2 Pro composite's reports, rumble
//                  decode and state block, each variant's median ns per call
//                  (ProgramCost.cs). Built with -p:HmDd4Base=true
//                  against the tree before, the code they replace
//   program-file <profile file> [runs] [reports]
//                  offline: one launch of each program of the profiles a
//                  user's file loads, on the interpreter, measured as
//                  HIDMaestroProfileCheck --cost measures it (the tool's
//                  InterpreterCost.cs): each variant's median ns per report
//                  and its compile in microseconds, for a script to take p50
//                  and p95 over 30 launches
//   submit [runs] [calls]
//                  offline: the SDK's own cost per call on the frame mode's
//                  submit paths and switch-pro's, its protocol host's tick
//                  included, each variant's median ns per call
//                  (SubmitCost.cs). It builds against b4d29d9 as is
//   submit-bytes [states]
//                  offline: a hash of the input section after each submit,
//                  per catalog profile, armed and not, for two trees'
//                  outputs to compare line by line (SubmitCost.cs)
//   submit-repeat [runs] [calls]
//                  offline: the same on the two budget profiles that repeat
//                  their last frame, with the repeat running
//   encode [runs] [calls]
//                  offline: the codec's encode of the budget table's
//                  codec-encoded input reports, each median ns per call
//                  (EncodeCost.cs). It builds against older trees as is
//   idle <periodMs> [seconds]
//                  offline: the process's CPU time per second with one
//                  dualsense-bt's idle repeat at the period and nothing else
//                  submitting, and the frames it wrote per second
//   idle-live [runs] [seconds]
//                  live, elevated: one dualsense-bt at rest with one reader
//                  in a child process, the CPU cycles per second of the SDK's
//                  process, the reader and the WUDFHost processes, and the
//                  reports read per second (IdleLive.cs)
//   report-build [runs]
//                  the Switch 2 Pro composite's device-built report in each
//                  layout, offline
//   install        InstallDriver, then proof that the driver installed is
//                  the one this SDK embeds (exit 4 when it is not)
//   selftest       the parsing the modes above share, offline
//   compare <before> <after>
//                  offline: the budgets' non-inferiority rule over two
//                  sides' measurements, the offline modes' launch lines or a
//                  live mode's summaries (Compare.cs)
//   start [runs]   process start to the first controller, each run in a
//                  fresh process, with the session cleanup step's time
//   claim [runs]   the owner claim, the census and the lease (this release)
//   census         the census walk over Enum keys it cannot read (scratch
//                  keys under HKCU: denied, deleted, gone) gives what the
//                  configuration manager lists. Creates no device and needs
//                  no elevation. Tells a fallback from a skip only while a
//                  HIDMaestro devnode is present. S84 runs it with an Xbox
//                  360 up
//   usbip [runs]   a USB/IP device's frames, from the input section to the
//                  host's reply, on each of its three send paths: bytes the
//                  process allocated per frame, the time, and lock
//                  contentions. Then a DualSense composite's output and
//                  audio transfers: bytes per transfer. Then the protocol
//                  engine's lock per frame, beside an uncontended lock alone
//   gate           S84: no submit path allocates, a USB/IP device's send
//                  paths and its output and audio transfers included, the
//                  codec path, consumer
//                  usages, a keyboard's raw reports and an absolute pointer
//                  included, a long mouse movement leaves 16 frames kept,
//                  the census's own walk completes and finds what the
//                  configuration manager lists, also when it cannot read a
//                  key, and hid.dll's BOOLEAN results read as one byte
//
// --trace prints wall-clock times around each Dispose, to set beside the
// SDK's diag log. Every measurement prints one line with p50 and p95 over
// all samples.
// Requires elevation, except census, report-build, selftest, compare and
// the offline measuring modes. Exit 0 PASS, 1 FAIL (gate, census,
// selftest, compare) or an argument refused before
// anything ran, 2 when another program that embeds HIDMaestro is running, 3
// when a mode that needs elevation runs without it, 4 when install finds
// another driver installed, 5 when a measured devnode runs a driver other
// than the one this SDK embeds.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;
using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    const string Ds4 = "dualshock-4-v2";
    const string Xbox360 = "xbox-360-wired";
    const string Composite = "dualsense-composite";
    const string Keyboard = "microsoft-wired-keyboard-600";
    const string Mouse = "microsoft-notebook-optical-mouse";
    const string GameCube = "gamecube-adapter";
    // Codec path on every frame, and a keyboard with consumer controls and
    // report IDs.
    const string Ds3 = "dualshock-3-full";
    const string MagicKeyboard = "apple-magic-keyboard-touch-id-numeric";

#if HM_BASE
    const string Build = "b4d29d9";
#elif HM_BASELINE
    static readonly string Build = "the baseline, " + string.Join(" at ", typeof(Program).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>()
        .Where(m => m.Key is "HmBaselineTree" or "HmBaselineCommit").OrderByDescending(m => m.Key).Select(m => m.Value));
#else
    const string Build = "this release";
#endif

    static int s_failures;
    // A measured devnode ran a driver other than the one this SDK embeds.
    static bool s_wrongDriver;
    // --trace: wall-clock times around each Dispose, to set beside the SDK's
    // diag log.
    static bool s_trace;

    // --key=<identity>: the identity key the create, rate, idle-live,
    // frame and signal modes create with. Without it each create takes the
    // index's default identity.
    internal static string? s_key;

    static int Main(string[] args)
    {
        // Read once by the SDK's first create, so it is set before any.
        Environment.SetEnvironmentVariable("HIDMAESTRO_TIMING", "1");
        s_trace = args.Contains("--trace");
        args = args.Where(a => a != "--trace").ToArray();
        s_key = KeyArg(args);
        args = args.Where(a => !a.StartsWith("--key=", StringComparison.Ordinal)).ToArray();
        string mode = args.Length > 0 ? args[0] : "gate";
        int runs = args.Length > 1 && int.TryParse(args[1], out int r) ? r : 30;
        if (mode == "start-child") return StartChild();
        if (mode == "idle-reader") return IdleLive.Reader(args[1]);
        if (mode == "rate-reader") return RateReader(args[1]);
#if !HM_BASE
        // The usbip mode plays the host controller from this process.
        UsbipServer.AdmitImportsFromThisProcess = true;
#endif
        // The offline modes touch no device, so another program's
        // controllers do not matter to them.
        if (mode == "selftest") return SelfTest();
        if (mode == "compare")
        {
            if (args.Length != 3) { Console.WriteLine("usage: PerfBudgetCheck compare <before file or dir> <after file or dir>"); return 1; }
            return BudgetCompare.Run(args[1], args[2]);
        }
#if HM_BASELINE && !HM_BASE
        if (mode == "report-build")
        {
            Console.WriteLine($"=== budgets, {Build}, mode {mode} ===");
            ReportBuild(runs);
            return s_failures == 0 ? 0 : 1;
        }
#endif
#if !HM_BASE
        if (mode == "report")
        {
            ReportCost.Run(runs, args.Length > 2 && int.TryParse(args[2], out int calls) ? calls : 100_000);
            return 0;
        }
#endif
#if !HM_BASE
        if (mode == "encode")
        {
            EncodeCost.Run(runs, args.Length > 2 && int.TryParse(args[2], out int calls) ? calls : 100_000);
            return 0;
        }
#endif
#if HM_PROGRAM_FILE
        if (mode == "program-file")
        {
            if (args.Length < 2) { Console.WriteLine("usage: PerfBudgetCheck program-file <profile file> [runs] [reports]"); return 1; }
            return ProgramFile(Path.GetFullPath(args[1]), args.Length > 2 && int.TryParse(args[2], out int fr) ? fr : 10,
                               args.Length > 3 && int.TryParse(args[3], out int fn) ? fn : 100_000);
        }
#endif
#if !HM_BASE
        if (mode == "program")
        {
            ProgramCost.Run(runs, args.Length > 2 && int.TryParse(args[2], out int calls) ? calls : 100_000);
            return 0;
        }
#endif
#if !HM_BASE
        if (mode == "submit-bytes")
        {
            SubmitCost.Bytes(args.Length > 1 && int.TryParse(args[1], out int states) ? states : 2000);
            return 0;
        }
#endif
        if (mode is "submit" or "submit-repeat")
        {
            SubmitCost.Run(runs, args.Length > 2 && int.TryParse(args[2], out int calls) ? calls : 100_000, mode == "submit-repeat");
            return 0;
        }
        if (mode == "ds3cost")
        {
            Ds3Cost.Run(runs, args.Length > 2 && int.TryParse(args[2], out int calls) ? calls : 100_000);
            return 0;
        }
#if !HM_BASE
        if (mode == "idle")
        {
            double period = args.Length > 1 ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 4;
            double seconds = args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 3;
            ProgramCost.Idle(period, seconds);
            return 0;
        }
#endif
        if (OtherSdkRunning()) return 2;
        if (mode == "idle-live")
        {
            IdleLive.Run(runs, args.Length > 2 ? double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 10);
            return 0;
        }

        var sdk = typeof(HMContext).Assembly;
        Console.WriteLine($"=== budgets, {Build}, mode {mode} ===");
        Console.WriteLine($"  SDK {sdk.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion} at {sdk.Location}");
        switch (mode)
        {
            case "frame":
                s_frameSettleMs = SettleMs(args);
                Frame(runs, gate: false, keyboardAndMouse: args.Skip(2).Contains("kbm"));
                break;
            case "e2e":
                int e2e = EndToEnd(runs, args.Skip(2).ToArray());
                if (e2e != 0) return e2e;
                break;
            case "feature":
                int feature = Feature(runs, args.Skip(2).ToArray());
                if (feature != 0) return feature;
                break;
            case "output":
                int output = Output(runs, args.Skip(2).ToArray());
                if (output != 0) return output;
                break;
            case "signal":
                int signal = Signal(runs);
                if (signal != 0) return signal;
                break;
            case "rate":
                int rate = Rate(runs, args.Skip(2).ToArray());
                if (rate != 0) return rate;
                break;
            case "create":
                int create = Create(runs, args.Skip(2).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray(),
                    SettleMs(args), s_key);
                if (create != 0) return create;
                break;
#if !HM_BASE
            case "install": return Install();
#endif
            case "start": Start(runs); break;
            case "usbip": Usbip(runs, gate: false); break;
#if !HM_BASE
            case "claim": Claim(runs); break;
            case "census": CensusFallback(); break;
#endif
            case "gate":
#if !HM_BASE
                HidBoolean();
#endif
                Frame(3, gate: true);
                Usbip(5, gate: true);
                break;
            default:
                Console.WriteLine($"unknown mode {mode}");
                return 1;
        }
        if (mode == "census")
        {
            Console.WriteLine($"census: {(s_failures == 0 ? "PASS" : $"{s_failures} FAIL")}");
            return s_failures == 0 ? 0 : 1;
        }
        if (mode == "gate")
        {
            Console.WriteLine($"S84: {(s_failures == 0 ? "PASS" : $"{s_failures} FAIL")}");
            return s_failures == 0 ? 0 : 1;
        }
        if (s_wrongDriver) return 5;
        if (s_failures > 0)
        {
            Console.WriteLine($"{mode}: {s_failures} FAIL");
            return 1;
        }
        return 0;
    }

    // ── Per-call cost on the submit paths ───────────────────────────────

    /// <summary>Each submit path called many times per run on a live
    /// controller: the time per call, and the bytes the calling thread
    /// allocated per call. The gamepad paths take two states in turn, so
    /// every call writes a new frame. The keyboard and mouse paths wait for
    /// the driver to deliver each report and send at most one a
    /// millisecond, so they run fewer calls.</summary>
    static void Frame(int runs, bool gate, bool keyboardAndMouse = true)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        var a = new HMGamepadState { Buttons = HMButton.A, Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0.25f, [HMAxis.Y] = 0.75f } };
        var b = new HMGamepadState { Buttons = HMButton.B, Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 0.75f, [HMAxis.Y] = 0.25f } };

        using (var ds4 = ctx.CreateController(ctx.GetProfile(Ds4)!, s_key))
        {
            if (!gate && !OnEmbeddedDriver(ds4, Ds4)) return;
            bool flip = false;
            Measure("SubmitState, dualshock-4-v2", runs, 20_000, () => { ds4.SubmitState(flip ? a : b); flip = !flip; }, gate);
            var raw = new byte[63];
            Measure("SubmitRawReport, dualshock-4-v2", runs, 20_000, () => { raw[0]++; ds4.SubmitRawReport(raw); }, gate);
        }
        using (var x360 = ctx.CreateController(ctx.GetProfile(Xbox360)!, s_key))
        {
            bool flip = false;
            Measure("SubmitState, xbox-360-wired", runs, 20_000, () => { x360.SubmitState(flip ? a : b); flip = !flip; }, gate);
#if !HM_BASE
            // The census walks the Enum keys instead of listing every present
            // device. With a shared-enumerator devnode and an SWD companion up,
            // the two must find the same devnodes.
            if (gate)
            {
                var walked = HIDMaestro.Internal.OwnerCensus.PresentOwnedDevnodes().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                var listed = HIDMaestro.Internal.OwnerCensus.PresentOwnedDevnodesListed().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                Check("the census walk finds the devnodes the configuration manager lists, the Xbox 360's two among them",
                    walked.Count >= 2 && walked.SequenceEqual(listed, StringComparer.OrdinalIgnoreCase),
                    $"walked {string.Join(", ", walked)}; listed {string.Join(", ", listed)}");
                // The walk falls back to the listings when it cannot read a
                // key, which makes the check above pass by construction. The
                // census's own walk must read every key it needs.
                var candidates = new List<(string Id, bool Exclusive)>();
                bool read;
                string detail;
                try
                {
                    read = HIDMaestro.Internal.OwnerCensus.TryWalkEnum(candidates);
                    detail = $"{candidates.Count} candidate instances";
                }
                catch (Exception e)
                {
                    read = false;
                    detail = $"threw {e.GetType().FullName}: {e.Message}";
                }
                Check("the census's own walk of Enum\\ROOT and Enum\\SWD reads every key it needs, without the fallback",
                    read, detail);
                CensusFallback();
            }
#endif
        }
        // A keyboard or mouse call waits about a millisecond for the driver
        // to deliver the report before it. Measure counts the calling
        // thread's bytes and fails on the worst of the runs, so 3 runs of 50
        // calls after the warm-up catch an allocation that comes once in
        // any 150 calls. These paths allocate only at a frame's, release
        // frame's or key count's first size, which the warm-up meets, and
        // run no timer on the calling thread. The defects the gate was
        // written for came on every call (539 bytes a SubmitKeyboard and
        // 248 a SubmitMouse before eed63d3f, a 32-byte report ID string
        // before d27be387). The frame mode keeps 200 for its time per call.
        int paced = gate ? 50 : 200;
        // A keyboard, mouse, consumer control or pointer this probe creates
        // types, clicks and moves the pointer on the desktop of whoever is
        // using the machine. The frame mode measures them only when its
        // arguments name kbm.
        if (keyboardAndMouse)
        using (var kb = ctx.CreateController(ctx.GetProfile(Keyboard)!, s_key))
        {
            var down = new[] { HMKey.A };
            bool flip = false;
            Measure("SubmitKeyboard, " + Keyboard, runs, paced, () =>
            {
                kb.SubmitKeyboard(HMKeyModifiers.None, flip ? new ReadOnlySpan<HMKey>(down) : ReadOnlySpan<HMKey>.Empty);
                flip = !flip;
            }, gate);
        }
        if (keyboardAndMouse)
        using (var mouse = ctx.CreateController(ctx.GetProfile(Mouse)!, s_key))
        {
            var move = new HMMouseState { X = 1 };
            Measure("SubmitMouse, " + Mouse, runs, paced, () => mouse.SubmitMouse(in move), gate);
#if !HM_BASE
            if (gate)
            {
                // A long movement splits across many reports. Once they are
                // sent the controller keeps only a few of their frames.
                mouse.SubmitMouse(new HMMouseState { X = 20_000 });
                var kept = typeof(HMController).GetField("_kbmBuffers", Any)!.GetValue(mouse) as HIDMaestro.Internal.KeyboardMouseBuffers;
                Check($"after a movement of 20,000 counts the mouse keeps at most {HIDMaestro.Internal.KeyboardMouseBuffers.MotionFramesKept} frames",
                      kept != null && kept.Motion.Count <= HIDMaestro.Internal.KeyboardMouseBuffers.MotionFramesKept,
                      $"{kept?.Motion.Count} kept");
            }
#endif
        }
        using (var ds3 = ctx.CreateController(ctx.GetProfile(Ds3)!, s_key))
        {
            bool flip = false;
            Measure("SubmitState, " + Ds3 + " (the report codec)", runs, 20_000, () => { ds3.SubmitState(flip ? a : b); flip = !flip; }, gate);
        }
        // The rest of the budget table of the data-driven design
        // (docs/design/data-driven-devices.md, Budgets): the centered Luna
        // sticks, the USB/IP codec and packer, and a GameCube port. The
        // armed codec is FrameConversionProfiles' dualsense-bt, armed by a
        // host's feature reads. This loop armed one by setting the flag
        // through reflection on a device no host had read, so it sent
        // report 0x31 in a state no host produces, and timed that. Not in
        // the gate, which measures the USB/IP send paths in its usbip mode.
        if (!gate)
            foreach (string id in new[]
            {
                "amazon-luna-usb", "amazon-luna-ble", "steam-controller-2", "switch2-pro-controller-composite", GameCube,
            })
            {
#if HM_BASE
                // b4d29d9's gamecube-adapter is a HID-only placeholder it
                // cannot deploy, and its persona came after.
                if (id == GameCube) continue;
#endif
                // steam-controller-2 declares a keyboard and a mouse (its
                // lizard mode), so it waits for kbm as the keyboards do.
                if (!keyboardAndMouse && id == "steam-controller-2") continue;
                using var c = ctx.CreateController(ctx.GetProfile(id)!, s_key);
                bool flip = false;
                Measure($"SubmitState, {id}", runs, 20_000, () => { c.SubmitState(flip ? a : b); flip = !flip; }, gate);
            }
        if (keyboardAndMouse)
        using (var mk = ctx.CreateController(ctx.GetProfile(MagicKeyboard)!, s_key))
        {
            var playPause = new ushort[] { 0x00CD };
            bool flip = false;
            Measure("SubmitConsumer, " + MagicKeyboard, runs, paced, () =>
            {
                mk.SubmitConsumer(flip ? playPause : ReadOnlySpan<ushort>.Empty);
                flip = !flip;
            }, gate);
            // Report 1's data, which the driver sends after the ID:
            // modifiers, reserved, six keys and the extras byte.
            var down = new byte[] { 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
            var up = new byte[9];
            Measure("SubmitRawReport, " + MagicKeyboard + " report 1", runs, paced, () =>
            {
                mk.SubmitRawReport(flip ? down : up);
                flip = !flip;
            }, gate);
        }
        var absolute = new HMProfileBuilder().Id("perf-absolute-pointer").Name("HIDMaestro Perf Absolute Pointer").Vendor("HIDMaestro")
            .Vid(0xBEEF).Pid(0xF0C0).ProductString("HIDMaestro Perf Absolute Pointer").ManufacturerString("HIDMaestro")
            .Type("mouse").Connection("usb")
            .FromDescriptorBuilder(new HidDescriptorBuilder().Mouse(absolute: true, wheel: false, pan: false)).Build();
        if (keyboardAndMouse)
        using (var abs = ctx.CreateController(absolute, "perf-absolute-pointer"))
        {
            int x = 0;
            Measure("SubmitMouse, an absolute pointer", runs, paced, () =>
            {
                x = (x + 997) % 32768;
                abs.SubmitMouse(new HMMouseState { X = x, Y = 32767 - x });
            }, gate);
        }
#if !HM_BASE
        using (var gc = ctx.CreateController(ctx.GetProfile(GameCube)!, s_key))
        {
            bool flip = false;
            var port = gc.Ports[0];
            Measure("HMPort.SubmitState, gamecube-adapter port 1", runs, 20_000, () => { port.SubmitState(flip ? a : b); flip = !flip; }, gate);
            Measure("SubmitState, gamecube-adapter", runs, 20_000, () => { gc.SubmitState(flip ? a : b); flip = !flip; }, gate);
        }
#endif
        if (!gate) FrameConversionProfiles(ctx, runs, a, b, keyboardAndMouse);
        if (!gate) FrameSwitchPro(ctx, runs, a, b);
    }

    /// <summary>switch-pro, whose 0x30 report goes out on the device's
    /// 15 ms clock. At b4d29d9 SubmitState packs the body and writes it,
    /// and the driver's stream thread builds each report from the section.
    /// Here SubmitState hands the state block to the protocol host, and a
    /// tick of the host's clock builds the report the device sends and
    /// writes it, so a frame's cost on this side is a submit and a tick:
    /// in the simple mode, 0x3F sent on change, and in the full mode, 0x30
    /// sent on every tick, which the host selects by speaking the
    /// protocol. The command goes to the host as the output reader hands
    /// it on. Outside the gate.</summary>
    static void FrameSwitchPro(HMContext ctx, int runs, HMGamepadState a, HMGamepadState b)
    {
        using var sw = ctx.CreateController(ctx.GetProfile("switch-pro")!, s_key);
        if (!OnEmbeddedDriver(sw, "switch-pro")) return;
        bool flip = false;
        Measure("SubmitState, switch-pro", runs, 20_000, () => { sw.SubmitState(flip ? a : b); flip = !flip; }, gate: false);
#if !HM_BASE
        var host = (ProtocolHost)typeof(HMController).GetField("_host", Any)!.GetValue(sw)!;
        Measure("SubmitState and a protocol host tick, switch-pro 0x3F", runs, 20_000, () =>
        {
            sw.SubmitState(flip ? a : b);
            flip = !flip;
            host.Tick(ProtocolHost.DeliveryTimeoutMs);
        }, gate: false);
        // Subcommand 0x03, set input report mode, to 0x30.
        var command = new byte[49];
        command[0] = 0x01; command[10] = 0x03; command[11] = 0x30;
        host.OnOutput(command, command.Length, ProtocolHost.DeliveryTimeoutMs);
        Measure("SubmitState and a protocol host tick, switch-pro 0x30", runs, 20_000, () =>
        {
            sw.SubmitState(flip ? a : b);
            flip = !flip;
            host.Tick(ProtocolHost.DeliveryTimeoutMs);
        }, gate: false);
        Measure("protocol host tick, switch-pro 0x30", runs, 20_000, () => host.Tick(ProtocolHost.DeliveryTimeoutMs), gate: false);
#endif
    }

    /// <summary>The submit paths the v2.0.0 conversion's budget names that
    /// the frame mode did not measure: dualsense-bt in its extended mode,
    /// the Valve and Switch 2 Pro composites, which build their reports
    /// through the codec and the device, and the Luna profiles, whose
    /// sticks the conversion centers (data-driven-devices.md, decision
    /// (a)). Outside the gate, so S84 is unchanged.</summary>
    static void FrameConversionProfiles(HMContext ctx, int runs, HMGamepadState a, HMGamepadState b, bool keyboardAndMouse)
    {
        using (var ds = ctx.CreateController(ctx.GetProfile("dualsense-bt")!, s_key))
        {
            if (!OnEmbeddedDriver(ds, "dualsense-bt")) return;
            string armed = ArmByFeatureReads(ds, a, b);
            bool flip = false;
            Measure($"SubmitState, dualsense-bt {armed}", runs, 20_000, () => { ds.SubmitState(flip ? a : b); flip = !flip; }, gate: false);
        }
        foreach (string id in new[] { "steam-controller-2", "switch2-pro-controller-composite", "amazon-luna-usb", "amazon-luna-ble" })
        {
            if (!keyboardAndMouse && id == "steam-controller-2") continue;
            using var c = ctx.CreateController(ctx.GetProfile(id)!, s_key);
            if (!OnEmbeddedDriver(c, id)) return;
            bool flip = false;
            Measure("SubmitState, " + id, runs, 20_000, () => { c.SubmitState(flip ? a : b); flip = !flip; }, gate: false);
        }
    }

    /// <summary>A host's first feature reads, every feature report ID the
    /// descriptor declares once, which is what arms a Sony Bluetooth
    /// profile's extended report (the driver's feature-read notification).
    /// Returns how the frames read afterwards say it went: the report ID
    /// they carry beside the descriptor's first input ID.</summary>
    static string ArmByFeatureReads(HMController c, HMGamepadState a, HMGamepadState b)
    {
        using var dev = OpenOwnInterface(c, d => d.FeatureLength > 0 && d.InputLength > 0, out string why);
        if (dev == null) return $"(not armed: {why})";
        var descriptor = c.Profile.GetDescriptorBytes()!;
        var buf = new byte[dev.FeatureLength];
        foreach (byte id in DeclaredReports(descriptor, ReportKind.Feature).Keys)
        {
            Array.Clear(buf);
            buf[0] = id;
            HidD_GetFeature(dev.Handle, buf, buf.Length);
        }
        using var reader = new HidReader(dev);
        long before = reader.Count;
        c.SubmitState(a);
        c.SubmitState(b);
        Thread.Sleep(150);
        var (last, count) = reader.Last();
        byte firstInput = DeclaredReports(descriptor, ReportKind.Input).Keys.FirstOrDefault();
        if (count == before || last.Length == 0) return "(arming not seen: no report read in 150 ms)";
        return last[0] != firstInput
            ? $"armed (frames carry report 0x{last[0]:X2} of {last.Length} bytes, not 0x{firstInput:X2})"
            : $"(not armed: frames still carry report 0x{last[0]:X2})";
    }

    /// <summary>Whether the controller's devnode runs this SDK's driver,
    /// printed. A measurement on another driver would be filed under the
    /// wrong tree, so the mode stops and exits 5.</summary>
    static bool OnEmbeddedDriver(HMController c, string label)
    {
        bool ok = RunsEmbeddedDriver(c, out string detail);
        Console.WriteLine($"  {label}: {(ok ? "" : "[WRONG DRIVER] ")}{detail}");
        if (!ok) s_wrongDriver = true;
        return ok;
    }

    /// <summary>frame --settle=&lt;ms&gt;: each measurement waits this long
    /// first, so a controller just created has been opened and read by
    /// what Windows and its services start on a new device before its calls
    /// are timed. 0, the default and the gate's, times them at once.</summary>
    static int s_frameSettleMs;

    static void Measure(string name, int runs, int calls, Action call, bool gate)
    {
        if (s_frameSettleMs > 0) Thread.Sleep(s_frameSettleMs);
        for (int i = 0; i < Math.Min(calls, 2_000); i++) call();
        var perCall = new List<double>(runs);
        long worstBytes = 0;
        for (int run = 0; run < runs; run++)
        {
            long bytes0 = GC.GetAllocatedBytesForCurrentThread();
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < calls; i++) call();
            long t1 = Stopwatch.GetTimestamp();
            long bytes = GC.GetAllocatedBytesForCurrentThread() - bytes0;
            worstBytes = Math.Max(worstBytes, bytes);
            perCall.Add((t1 - t0) * 1e9 / Stopwatch.Frequency / calls);
        }
        Report($"{name}: ns per call", perCall, runs, $", most bytes allocated in one run of {calls} calls {worstBytes}");
        if (gate)
            Check($"{name} allocates nothing", worstBytes == 0, $"{worstBytes} bytes in the worst of {runs} runs of {calls} calls");
    }

    // ── Submit to the driver's acknowledgement ──────────────────────────

    /// <summary>The time from a SubmitState call to the driver's
    /// acknowledgement that a READ_REPORT carried the frame, read from the
    /// acknowledgement section the SDK and driver share
    /// (Global\HIDMaestroInputAck&lt;N&gt;, DeliveredSeqNo at offset 0).
    /// Fifty submits per run, each at a random offset. With profiles named,
    /// each is then read as a host reads it (HostReadPass). Returns 1 when a
    /// profile argument is refused and 3 without elevation, both before
    /// anything is created.</summary>
    static int EndToEnd(int runs, string[] args)
    {
        var named = ParseProfileArgs(args, "e2e");
        if (named == null) return 1;
        if (!Elevated("e2e")) return 3;
        var list = named.Count > 0 ? named : new List<ProfileArg> { new(Ds4, Ds4, Array.Empty<byte[]>()), new(Xbox360, Xbox360, Array.Empty<byte[]>()) };
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        foreach (var p in list)
        {
            var xinputBefore = XInputConnected();
            using var c = ctx.CreateController(ctx.GetProfile(p.Id)!);
            if (named.Count > 0 && !OnEmbeddedDriver(c, p.Text)) return 5;
            AckPass(c, p.Text, runs);
            if (named.Count > 0)
            {
                HostReadPass(c, p, runs);
                XInputPass(c, p.Text, runs, xinputBefore);
            }
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint PacketNumber;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    static extern int XInputGetState(int userIndex, out XInputState state);

    const ushort XInputA = 0x1000, XInputB = 0x2000;

    /// <summary>The XInput slots that read connected.</summary>
    static HashSet<int> XInputConnected()
    {
        var set = new HashSet<int>();
        for (int i = 0; i < 4; i++)
            if (XInputGetState(i, out _) == 0) set.Add(i);
        return set;
    }

    /// <summary>SubmitState to the first XInputGetState that shows it, what a
    /// game that reads an Xbox persona through XInput waits. The slot is the
    /// one the controller's create connected, so a profile XInput does not
    /// list is skipped, and a pad another program or a real controller holds
    /// is never read.</summary>
    static void XInputPass(HMController c, string id, int runs, HashSet<int> before)
    {
        int slot = -1;
        var wait = Stopwatch.StartNew();
        while (slot < 0 && wait.ElapsedMilliseconds < 5000)
        {
            foreach (int s in XInputConnected())
                if (!before.Contains(s)) { slot = s; break; }
            if (slot < 0) Thread.Sleep(50);
        }
        if (slot < 0) return;
        var a = new HMGamepadState { Buttons = HMButton.A };
        var b = new HMGamepadState { Buttons = HMButton.B };
        c.SubmitState(b);
        Thread.Sleep(200);
        var rng = new Random(84);
        var samples = new List<double>(runs * 50);
        int missed = 0;
        bool flip = true;
        for (int run = 0; run < runs; run++)
        {
            for (int i = 0; i < 50; i++)
            {
                Spin(rng.Next(2_000, 19_000));
                flip = !flip;
                ushort want = flip ? XInputB : XInputA;
                long t0 = Stopwatch.GetTimestamp();
                c.SubmitState(flip ? b : a);
                long deadline = t0 + Stopwatch.Frequency / 10;
                long hit = 0;
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    if (XInputGetState(slot, out var st) == 0 && (st.Buttons & (XInputA | XInputB)) == want)
                    {
                        hit = Stopwatch.GetTimestamp();
                        break;
                    }
                }
                if (hit != 0) samples.Add((hit - t0) * 1e6 / Stopwatch.Frequency);
                else { missed++; Thread.Sleep(200); }
            }
        }
        Report($"{id}: SubmitState to an XInputGetState that shows it, slot {slot}, microseconds", samples, runs,
            $", {missed} not seen within 100 ms");
    }

    static void AckPass(HMController c, string id, int runs)
    {
        int index = (int)typeof(HMController).GetProperty("Index", Any)!.GetValue(c)!;
        var seqField = typeof(HMController).GetField("_inputSeqNo", Any)!;
        IntPtr map = OpenFileMappingW(FILE_MAP_READ, false, $@"Global\HIDMaestroInputAck{index}");
        if (map == IntPtr.Zero)
        {
            Console.WriteLine($"{id}: no acknowledgement section (error {Marshal.GetLastWin32Error()})");
            return;
        }
        IntPtr view = MapViewOfFile(map, FILE_MAP_READ, 0, 0, UIntPtr.Zero);
        try
        {
            var a = new HMGamepadState { Buttons = HMButton.A };
            var b = new HMGamepadState { Buttons = HMButton.B };
            Thread.Sleep(500);
            var rng = new Random(84);
            var samples = new List<double>(runs * 50);
            int missed = 0;
            bool flip = false;
            for (int run = 0; run < runs; run++)
            {
                for (int i = 0; i < 50; i++)
                {
                    Spin(rng.Next(2_000, 4_000));
                    long t0 = Stopwatch.GetTimestamp();
                    c.SubmitState(flip ? a : b);
                    flip = !flip;
                    uint seq = (uint)seqField.GetValue(c)!;
                    long deadline = t0 + Stopwatch.Frequency / 20;
                    bool seen = false;
                    while (Stopwatch.GetTimestamp() < deadline)
                    {
                        if ((int)((uint)Marshal.ReadInt32(view, 0) - seq) >= 0) { seen = true; break; }
                        Thread.SpinWait(16);
                    }
                    if (seen) samples.Add((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
                    else missed++;
                }
                // A driver that acknowledges none of a profile's frames (the
                // 9bd6279 Switch Pro stream sends them from its own thread)
                // would cost each submit the whole 50 ms.
                if (run == 0 && samples.Count == 0)
                {
                    Console.WriteLine($"  {id}: the driver acknowledged none of the first 50 frames, so the acknowledgement is not measured");
                    return;
                }
            }
            Report($"{id}: SubmitState to the driver's delivery, microseconds", samples, runs, $", {missed} not acknowledged within 50 ms");
        }
        finally
        {
            UnmapViewOfFile(view);
            CloseHandle(map);
        }
    }

    // ── Submit to a host's read ──────────────────────────────────────────

    /// <summary>The time from SubmitState to the moment a host's ReadFile
    /// on the controller's own HID interface returns a report that carries
    /// the new state, which needs nothing of the driver but its input
    /// reports, so it measures a 9bd6279 driver and a converted one alike.
    /// The profile's output reports, if any, are written first, as a host
    /// writes them (SDL's input-mode subcommand on a Switch Pro, which
    /// starts the 0x30 stream). Calibrate learns which bytes tell the two
    /// states apart. Fifty submits per run, each after a random pause of 2
    /// to 19 ms, so a device that sends on a 15 ms clock is caught at every
    /// phase of it. A report not seen within 100 ms counts as missed, and
    /// the next submit waits 200 ms more so a late one cannot match
    /// it.</summary>
    static void HostReadPass(HMController c, ProfileArg p, int runs)
    {
        using var dev = OpenOwnInterface(c, d => d.InputLength > 0, out string why);
        if (dev == null) { Check($"{p.Text}: a HID interface with input reports", false, why); return; }
        // The writes go first: the handle is synchronous, so the I/O manager
        // runs one request on it at a time, and a write would wait behind
        // the reader's pending ReadFile.
        foreach (var report in p.Reports)
        {
            if (!WriteOutput(dev, report, out int error))
            {
                Check($"{p.Text}: output report 0x{report[0]:X2} written", false, $"WriteFile error {error}");
                return;
            }
            Thread.Sleep(100);
        }
        using var reader = new HidReader(dev);
        var a = new HMGamepadState { Buttons = HMButton.A };
        var b = new HMGamepadState { Buttons = HMButton.B };
        var patterns = Calibrate(c, reader, a, b, out string learned);
        if (patterns == null) { Check($"{p.Text}: the two states tell apart in its reports", false, learned); return; }
        Console.WriteLine($"  {p.Text}: report 0x{patterns.Value.A.Expect[0]:X2} of {patterns.Value.A.Expect.Length} bytes, {learned}");
        var rng = new Random(84);
        var samples = new List<double>(runs * 50);
        int missed = 0;
        // Calibration left state b in place, so the first submit is a.
        bool flip = true;
        for (int run = 0; run < runs; run++)
        {
            for (int i = 0; i < 50; i++)
            {
                Spin(rng.Next(2_000, 19_000));
                flip = !flip;
                reader.Arm(flip ? patterns.Value.B : patterns.Value.A);
                long t0 = Stopwatch.GetTimestamp();
                c.SubmitState(flip ? b : a);
                long deadline = t0 + Stopwatch.Frequency / 10;
                long hit = 0;
                while (Stopwatch.GetTimestamp() < deadline && (hit = reader.Hit) == 0) Thread.SpinWait(16);
                reader.Disarm();
                if (hit != 0) samples.Add((hit - t0) * 1e6 / Stopwatch.Frequency);
                else { missed++; Thread.Sleep(200); }
            }
        }
        if (reader.ReadError != 0) Check($"{p.Text}: the reader read to the end", false, $"ReadFile error {reader.ReadError}");
        Report($"{p.Text}: SubmitState to a host's ReadFile of the report that carries it, microseconds", samples, runs,
            $", {missed} not seen within 100 ms");
    }

    // ── The output reader ───────────────────────────────────────────────

    /// <summary>Output reports sent through HID, each timed to
    /// OutputReceived, and the bytes the whole process allocated per report
    /// while the SDK's reader handled them: report 05 of 32 bytes on a
    /// DualShock 4, or for each profile named the report given, padded to
    /// its interface's output report length. Byte 4 changes with every
    /// report, so no two in a row are the same. Returns 1 when an argument
    /// is refused, 3 without elevation, 5 on another driver.</summary>
    static int Output(int runs, string[] args)
    {
        var named = ParseProfileArgs(args, "output");
        if (named == null) return 1;
        if (named.Any(p => p.Reports.Length != 1))
        {
            Console.WriteLine("output: each profile named needs one report, as <profile>@<report hex>");
            return 1;
        }
        if (!Elevated("output")) return 3;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        if (named.Count == 0)
        {
            var report = new byte[32];
            report[0] = 0x05;
            named.Add(new ProfileArg(Ds4, Ds4, new[] { report }));
        }
        foreach (var p in named)
        {
            using var c = ctx.CreateController(ctx.GetProfile(p.Id)!);
            if (!OnEmbeddedDriver(c, p.Text)) return 5;
            OutputOne(c, p, runs);
        }
        return 0;
    }

    static void OutputOne(HMController c, ProfileArg p, int runs)
    {
        long sent = 0;
        using var got = new AutoResetEvent(false);
        var rtts = new List<double>(runs * 100);
        c.OutputReceived += (_, _) =>
        {
            long t = Stopwatch.GetTimestamp();
            long s = Interlocked.Exchange(ref sent, 0);
            if (s != 0)
            {
                rtts.Add((t - s) * 1e6 / Stopwatch.Frequency);
                got.Set();
            }
        };
        using var dev = OpenOwnInterface(c, d => d.OutputLength > 0, out string why);
        if (dev == null) { Check($"{p.Text}: a HID interface with output reports", false, why); return; }
        var report = new byte[Math.Max(dev.OutputLength, p.Reports[0].Length)];
        p.Reports[0].CopyTo(report, 0);
        int vary = Math.Min(4, report.Length - 1);
        var perReport = new List<double>(runs);
        int lost = 0;
        for (int run = 0; run < runs; run++)
        {
            int delivered = 0;
            long bytes0 = GC.GetTotalAllocatedBytes(precise: true);
            for (int i = 0; i < 100; i++)
            {
                report[vary] = (byte)(1 + ((run * 100 + i) % 254));
                Interlocked.Exchange(ref sent, Stopwatch.GetTimestamp());
                if (!HidD_SetOutputReport(dev.Handle, report, report.Length)) { Interlocked.Exchange(ref sent, 0); lost++; continue; }
                if (got.WaitOne(500)) delivered++;
                else { Interlocked.Exchange(ref sent, 0); lost++; }
                Spin(1_000);
            }
            long bytes = GC.GetTotalAllocatedBytes(precise: true) - bytes0;
            if (delivered > 0) perReport.Add((double)bytes / delivered);
        }
        Report($"{p.Text}: output report 0x{report[0]:X2} to OutputReceived, microseconds", rtts, runs, $", {lost} lost");
        Report($"{p.Text}: bytes the process allocated per output report", perReport, runs, "");
    }

    // ── Create and dispose ──────────────────────────────────────────────

    /// <summary>The catalog profiles named after the run count, in that
    /// order, or all five. Returns 1 when a name is refused, 5 when a
    /// devnode runs another driver.</summary>
    static int Create(int runs, string[] only, int settleMs, string? key)
    {
        var named = ParseProfileArgs(only, "create");
        if (named == null) return 1;
        if (named.Any(p => p.Reports.Length > 0))
        {
            Console.WriteLine("create: a profile takes no report here");
            return 1;
        }
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        // No keyboard unless named: one this probe creates sits on the
        // desktop of whoever is using the machine.
        var ids = new List<string> { Ds4, Xbox360, Composite };
#if !HM_BASE
        ids.Add(GameCube);
#endif
        if (named.Count > 0) ids = named.Select(p => p.Id).ToList();
        string log = TimingLog();
        foreach (string id in ids)
        {
            var profile = ctx.GetProfile(id)!;
            // One create and dispose first, so a cold first create (driver
            // binding, the USB/IP server's first start) is not a sample.
            using (var first = ctx.CreateController(profile, key))
                if (!OnEmbeddedDriver(first, id)) return 5;
            long logStart = new FileInfo(log).Exists ? new FileInfo(log).Length : 0;
            var create = new List<double>(runs);
            var dispose = new List<double>(runs);
            for (int run = 0; run < runs; run++)
            {
                long t0 = Stopwatch.GetTimestamp();
                var c = ctx.CreateController(profile, key);
                long t1 = Stopwatch.GetTimestamp();
                if (settleMs > 0) Thread.Sleep(settleMs);
                long t2 = Stopwatch.GetTimestamp();
                if (s_trace) Console.WriteLine($"    [{DateTime.Now:HH:mm:ss.fff}] dispose begins");
                c.Dispose();
                long t3 = Stopwatch.GetTimestamp();
                if (s_trace) Console.WriteLine($"    [{DateTime.Now:HH:mm:ss.fff}] dispose returned after {(t3 - t2) * 1000.0 / Stopwatch.Frequency:F1} ms");
                create.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                dispose.Add((t3 - t2) * 1000.0 / Stopwatch.Frequency);
            }
            Report($"{id}: CreateController, ms", create, runs, "");
            Report($"{id}: Dispose, ms", dispose, runs, "");
            foreach (var (step, values) in TimingSteps(log, logStart, id))
                Report($"{id}: step {step}, ms", values, runs, "");
        }
        return 0;
    }

    /// <summary>--settle=&lt;ms&gt;: the time each created controller stays up
    /// before its Dispose is timed.</summary>
    static int SettleMs(string[] args)
    {
        foreach (string a in args)
            if (a.StartsWith("--settle=", StringComparison.Ordinal) && int.TryParse(a[9..], out int ms)) return ms;
        return 0;
    }

    /// <summary>The value of --key=, or null.</summary>
    static string? KeyArg(string[] args)
    {
        foreach (string a in args)
            if (a.StartsWith("--key=", StringComparison.Ordinal) && a.Length > 6) return a[6..];
        return null;
    }

    // ── Process start to the first controller ───────────────────────────

    /// <summary>Fresh processes, each creating one DualShock 4 on the
    /// installed driver and exiting. One run first makes sure the driver is
    /// current, so every timed run takes the same-version fast path.</summary>
    static void Start(int runs)
    {
        string self = Environment.ProcessPath!;
        string log = TimingLog();
        var ready = new List<double>(runs);
        var sweep = new List<double>(runs);
        for (int run = -1; run < runs; run++)
        {
            long logStart = File.Exists(log) ? new FileInfo(log).Length : 0;
            var psi = new ProcessStartInfo(self, "start-child")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (run < 0) continue;
            var line = output.Split('\n').FirstOrDefault(l => l.StartsWith("ready ", StringComparison.Ordinal));
            if (line == null) { Console.WriteLine($"start: run {run} gave no time: {output.Trim()}"); continue; }
            ready.Add(double.Parse(line[6..].Trim(), System.Globalization.CultureInfo.InvariantCulture));
            foreach (var (step, values) in TimingSteps(log, logStart, Ds4))
                if (step == "0.ghost_cleanup") sweep.AddRange(values);
        }
        Report("process start to the first controller, ms", ready, runs, "");
        Report("the session cleanup step in that create (0.ghost_cleanup), ms", sweep, runs, "");
    }

    static int StartChild()
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        using var c = ctx.CreateController(ctx.GetProfile(Ds4)!);
        DateTime started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        double ms = (DateTime.UtcNow - started).TotalMilliseconds;
        Console.WriteLine(FormattableString.Invariant($"ready {ms:F1}"));
        return 0;
    }

#if !HM_BASE
    // ── The ownership claims ────────────────────────────────────────────

    /// <summary>What a create spends on ownership: the claim on one name,
    /// the census of owners and devices it takes, and the whole lease
    /// (census, index and identity claims, and the record entry) with its
    /// release. The owner claim a process takes once is timed in fresh
    /// processes by the start mode's first create.</summary>
    static void Claim(int runs)
    {
        HIDMaestro.Internal.MachineClaims.EnsureOwner();
        var claim = new List<double>(runs * 10);
        var census = new List<double>(runs * 10);
        var lease = new List<double>(runs * 10);
        var release = new List<double>(runs * 10);
        for (int i = 0; i < runs * 10; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var h = HIDMaestro.Internal.MachineClaims.TryClaim($"HIDMaestro.Budget.{Environment.ProcessId}.{i}");
            h?.Dispose();
            long t1 = Stopwatch.GetTimestamp();
            HIDMaestro.Internal.OwnerCensus.Take();
            long t2 = Stopwatch.GetTimestamp();
            var held = HIDMaestro.Internal.ControllerLease.Acquire(0, null, "umdf2", Ds4);
            long t3 = Stopwatch.GetTimestamp();
            held.Dispose();
            long t4 = Stopwatch.GetTimestamp();
            claim.Add((t1 - t0) * 1e6 / Stopwatch.Frequency);
            census.Add((t2 - t1) * 1e6 / Stopwatch.Frequency);
            lease.Add((t3 - t2) * 1e6 / Stopwatch.Frequency);
            release.Add((t4 - t3) * 1e6 / Stopwatch.Frequency);
        }
        Report("one claim taken and released, microseconds", claim, runs, "");
        Report("the census of owners and devices, microseconds", census, runs, "");
        Report("a lease taken, what a create pays, census included, microseconds", lease, runs, "");
        Report("a lease released, what a dispose pays, microseconds", release, runs, "");

        // The census's parts.
        var records = new List<double>(runs * 10);
        var rootIds = new List<double>(runs * 10);
        var swdIds = new List<double>(runs * 10);
        var owned = new List<double>(runs * 10);
        var imports = new List<double>(runs * 10);
        int rootCount = 0, swdCount = 0, ownedCount = 0;
        for (int i = 0; i < runs * 10; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            HIDMaestro.Internal.MachineClaims.ReadAll();
            long t1 = Stopwatch.GetTimestamp();
            rootCount = HIDMaestro.Internal.OwnerCensus.PresentDeviceIds("ROOT").Count;
            long t2 = Stopwatch.GetTimestamp();
            swdCount = HIDMaestro.Internal.OwnerCensus.PresentDeviceIds("SWD").Count;
            long t3 = Stopwatch.GetTimestamp();
            ownedCount = HIDMaestro.Internal.OwnerCensus.PresentOwnedDevnodes().Count;
            long t4 = Stopwatch.GetTimestamp();
            HIDMaestro.Internal.Usbip.VhciClient.GetImportedDevices();
            long t5 = Stopwatch.GetTimestamp();
            records.Add((t1 - t0) * 1e6 / Stopwatch.Frequency);
            rootIds.Add((t2 - t1) * 1e6 / Stopwatch.Frequency);
            swdIds.Add((t3 - t2) * 1e6 / Stopwatch.Frequency);
            owned.Add((t4 - t3) * 1e6 / Stopwatch.Frequency);
            imports.Add((t5 - t4) * 1e6 / Stopwatch.Frequency);
        }
        Report("census part: the owner records, microseconds", records, runs, "");
        Report($"the listing the census made before, present ROOT ids ({rootCount}), microseconds", rootIds, runs, "");
        Report($"the listing the census made before, present SWD ids ({swdCount}), microseconds", swdIds, runs, "");
        Report($"census part: the owned devnodes ({ownedCount}), microseconds", owned, runs, "");
        Report("census part: the USB/IP imports, microseconds", imports, runs, "");
    }
#endif

#if !HM_BASE
    // ── The census walk on keys it cannot read ──────────────────────────

    /// <summary>The census walk handed Enum roots it cannot read must give
    /// what the configuration manager lists, as it does past 64 candidates.
    /// The roots are scratch keys under HKCU\Software\HIDMaestroProbe, so no
    /// device key's DACL changes: a device key whose DACL denies everyone
    /// its subkeys and values, so OpenSubKey meets ERROR_ACCESS_DENIED, a
    /// root that cannot be opened for the same reason, a root deleted after
    /// the walk opened it, as PnP deletes a removed device's key, so listing
    /// it meets ERROR_KEY_DELETED, and a device key its root lists that is
    /// gone when the walk opens it, so OpenSubKey returns null. A null can
    /// also come from a failure other than a missing key, so the walk does
    /// not read it as "no instances". A readable root holding only an
    /// absent instance is the positive control: the walk reads it and finds
    /// nothing. With a controller up (S84) the listing is not empty, so the
    /// control tells a walk that read its keys from one that fell back.
    /// Each run first removes the scratch keys of a run killed before its
    /// cleanup.</summary>
    static void CensusFallback()
    {
        RemoveStaleCensusScratch();
        string scratch = $@"{ProbeRoot}\{ScratchPrefix}{Environment.ProcessId}";
        var hkcu = Microsoft.Win32.Registry.CurrentUser;
        // Handles the cases keep open, closed after the walks.
        var handles = new List<Microsoft.Win32.RegistryKey>();
        try
        {
            List<string> listed;
            try
            {
                listed = HIDMaestro.Internal.OwnerCensus.PresentOwnedDevnodesListed().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception e)
            {
                Check("the configuration manager lists the present devnodes", false, $"threw {e.GetType().FullName}: {e.Message}");
                return;
            }
            Func<string, Microsoft.Win32.RegistryKey?> Under(string tree) =>
                root => hkcu.OpenSubKey($@"{scratch}\{tree}\{root}");
            void Create(params string[] paths)
            {
                foreach (string path in paths) hkcu.CreateSubKey($@"{scratch}\{path}").Dispose();
            }

            CensusCase("the census walk reads a readable root and finds its absent instance absent", () =>
            {
                Create(@"readable\ROOT\HIDMAESTRO\PROBE-ABSENT", @"readable\SWD");
                return Under("readable");
            }, new List<string>());
            CensusCase("the census walk on a device key it may not open gives what the configuration manager lists", () =>
            {
                Create(@"denied-device\ROOT\HIDMAESTRO", @"denied-device\SWD");
                SetDenyAll($@"{scratch}\{DeniedDevice}", add: true);
                return Under("denied-device");
            }, listed);
            CensusCase("the census walk on a root key it may not open gives what the configuration manager lists", () =>
            {
                Create(@"denied-root\ROOT", @"denied-root\SWD");
                SetDenyAll($@"{scratch}\{DeniedRoot}", add: true);
                return Under("denied-root");
            }, listed);
            CensusCase("the census walk on a device key that is gone when it opens it gives what the configuration manager lists", () =>
            {
                Create(@"dangling\ROOT", @"dangling\SWD");
                CreateDanglingLink($@"{scratch}\{DanglingLink}", $@"{scratch}\dangling\nowhere");
                return Under("dangling");
            }, listed);
            CensusCase("the census walk on a root key deleted after it was opened gives what the configuration manager lists", () =>
            {
                Create(@"deleted\ROOT\HIDMAESTRO", @"deleted-rest\SWD");
                var gone = hkcu.OpenSubKey($@"{scratch}\deleted\ROOT")
                    ?? throw new InvalidOperationException($@"{scratch}\deleted\ROOT did not open");
                handles.Add(gone);
                hkcu.DeleteSubKeyTree($@"{scratch}\deleted");
                var rest = Under("deleted-rest");
                return root => root == "ROOT" ? gone : rest(root);
            }, listed);
        }
        finally
        {
            foreach (var h in handles) h.Dispose();
            RemoveCensusScratch(scratch);
            try
            {
                using var parent = hkcu.OpenSubKey(ProbeRoot);
                bool empty = parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0;
                parent?.Dispose();
                if (empty) hkcu.DeleteSubKey(ProbeRoot, throwOnMissingSubKey: false);
            }
            catch (Exception e) { Console.WriteLine($@"  cleanup of {ProbeRoot}: {e.GetType().Name} {e.Message}"); }
        }
    }

    const string ProbeRoot = @"Software\HIDMaestroProbe";
    const string ScratchPrefix = "CensusFallback.";
    // The keys a run denies or links, relative to its scratch key. The
    // cleanup undoes them by these paths, a stale run's as well.
    const string DeniedDevice = @"denied-device\ROOT\HIDMAESTRO";
    const string DeniedRoot = @"denied-root\SWD";
    const string DanglingLink = @"dangling\ROOT\HIDMAESTRO";

    static readonly RegistryAccessRule s_denyAll = new(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
        RegistryRights.EnumerateSubKeys | RegistryRights.QueryValues, AccessControlType.Deny);

    /// <summary>Add or remove the deny rule on one HKCU key. Opening it for
    /// its DACL alone works while the rule is in place.</summary>
    static void SetDenyAll(string path, bool add)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(path, Microsoft.Win32.RegistryKeyPermissionCheck.ReadWriteSubTree,
            RegistryRights.ChangePermissions | RegistryRights.ReadPermissions);
        if (k == null)
        {
            if (add) throw new InvalidOperationException($"{path} did not open for its DACL");
            return;
        }
        var acl = k.GetAccessControl();
        if (add) acl.AddAccessRule(s_denyAll);
        else acl.RemoveAccessRule(s_denyAll);
        k.SetAccessControl(acl);
    }

    /// <summary>Scratch keys a run killed before its cleanup left behind:
    /// every key under HIDMaestroProbe named with this probe's prefix but
    /// another process id.</summary>
    static void RemoveStaleCensusScratch()
    {
        string[] names;
        try
        {
            using var parent = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ProbeRoot);
            if (parent == null) return;
            names = parent.GetSubKeyNames();
        }
        catch (Exception e)
        {
            Console.WriteLine($"  stale scratch keys under {ProbeRoot} not listed: {e.GetType().Name} {e.Message}");
            return;
        }
        string own = $"{ScratchPrefix}{Environment.ProcessId}";
        foreach (string name in names)
        {
            if (!name.StartsWith(ScratchPrefix, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, own, StringComparison.OrdinalIgnoreCase))
                continue;
            Console.WriteLine($@"  removing {ProbeRoot}\{name}, left by an earlier run");
            RemoveCensusScratch($@"{ProbeRoot}\{name}");
        }
    }

    /// <summary>Delete one run's scratch key: its link through a handle to
    /// the link itself, since DeleteSubKeyTree would open the missing key
    /// it names, then its deny rules, then the tree. Each step logs its own
    /// failure and the next one runs.</summary>
    static void RemoveCensusScratch(string scratch)
    {
        string link = $@"{scratch}\{DanglingLink}";
        int cr = RegOpenKeyExW(HKEY_CURRENT_USER, link, REG_OPTION_OPEN_LINK, DELETE, out IntPtr h);
        if (cr == 0)
        {
            int status = NtDeleteKey(h);
            RegCloseKey(h);
            if (status != 0) Console.WriteLine($"  cleanup of {link}: NtDeleteKey 0x{status:X8}");
        }
        else if (cr != ERROR_FILE_NOT_FOUND) Console.WriteLine($"  cleanup of {link}: RegOpenKeyEx error {cr}");
        foreach (string path in new[] { $@"{scratch}\{DeniedDevice}", $@"{scratch}\{DeniedRoot}" })
        {
            try { SetDenyAll(path, add: false); }
            catch (Exception e) { Console.WriteLine($"  cleanup of {path}: {e.GetType().Name} {e.Message}"); }
        }
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false); }
        catch (Exception e) { Console.WriteLine($"  cleanup of {scratch}: {e.GetType().Name} {e.Message}"); }
    }

    /// <summary>A volatile registry link at <paramref name="path"/> under HKCU
    /// to <paramref name="target"/>, a key that does not exist. The parent
    /// lists it, and opening it fails with ERROR_FILE_NOT_FOUND, as when a
    /// key is deleted between the walk's listing and its open.</summary>
    static void CreateDanglingLink(string path, string target)
    {
        int cr = RegCreateKeyExW(HKEY_CURRENT_USER, path, 0, null, REG_OPTION_VOLATILE | REG_OPTION_CREATE_LINK,
            KEY_SET_VALUE | KEY_CREATE_LINK, IntPtr.Zero, out IntPtr h, out _);
        if (cr != 0) throw new InvalidOperationException($"RegCreateKeyEx of the link {path} failed, error {cr}");
        string sid = WindowsIdentity.GetCurrent().User!.Value;
        byte[] name = System.Text.Encoding.Unicode.GetBytes($@"\REGISTRY\USER\{sid}\{target}");
        cr = RegSetValueExW(h, "SymbolicLinkValue", 0, REG_LINK, name, name.Length);
        RegCloseKey(h);
        if (cr != 0) throw new InvalidOperationException($"RegSetValueEx of the link {path} failed, error {cr}");
    }

    static readonly IntPtr HKEY_CURRENT_USER = unchecked((IntPtr)(int)0x80000001);
    const int REG_OPTION_VOLATILE = 0x1, REG_OPTION_CREATE_LINK = 0x2, REG_OPTION_OPEN_LINK = 0x8;
    const int KEY_SET_VALUE = 0x2, KEY_CREATE_LINK = 0x20, DELETE = 0x10000, REG_LINK = 6;
    const int ERROR_FILE_NOT_FOUND = 2;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegCreateKeyExW(IntPtr key, string subKey, int reserved, string? cls, int options, int sam,
        IntPtr sa, out IntPtr result, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegSetValueExW(IntPtr key, string name, int reserved, int type, byte[] data, int size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegOpenKeyExW(IntPtr key, string subKey, int options, int sam, out IntPtr result);

    [DllImport("advapi32.dll")]
    static extern int RegCloseKey(IntPtr key);

    [DllImport("ntdll.dll")]
    static extern int NtDeleteKey(IntPtr key);

    /// <summary>One case: its setup, then one walk over the roots the setup
    /// hands back. Passes when the walk returns the expected devnodes. A
    /// setup or a walk that throws is a failed check, and the next case
    /// runs.</summary>
    static void CensusCase(string name, Func<Func<string, Microsoft.Win32.RegistryKey?>> setup, List<string> expected)
    {
        Func<string, Microsoft.Win32.RegistryKey?> openEnumRoot;
        try
        {
            openEnumRoot = setup();
        }
        catch (Exception e)
        {
            Check(name, false, $"setup threw {e.GetType().FullName}: {e.Message}");
            return;
        }
        try
        {
            var got = HIDMaestro.Internal.OwnerCensus.PresentOwnedDevnodes(openEnumRoot).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            Check(name, got.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase),
                $"got [{string.Join(", ", got)}], expected [{string.Join(", ", expected)}]");
        }
        catch (Exception e)
        {
            Check(name, false, $"threw {e.GetType().FullName}: {e.Message}");
        }
    }
#endif

    // ── A USB/IP device's frames ────────────────────────────────────────

    const int UsbipIndex = 56;   // far from live controller indices

    /// <summary>Frames through USB/IP devices in this process, with this
    /// probe on the wire in the host controller's place: each frame written
    /// to the input section as SubmitState writes it, then read back as the
    /// device's reply to an interrupt IN transfer. One device per send path:
    /// the input pump's own sends (a DualSense, every other frame waiting in
    /// the queue for a read), a declared protocol's host poll clock (the
    /// GameCube adapter) and a clock that builds each report (the Switch 2
    /// Pro, started with 03/0D). The probe allocates nothing while it counts,
    /// so the process's count is the device's: its input pump, its clock and
    /// its connection's reader.</summary>
    static void Usbip(int runs, bool gate)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var server = UsbipServer.GetOrStart();

        // The check counts every thread's bytes and fails when the median
        // of the runs allocates, so a run must hold any allocation the
        // device makes on a period. The periodic wakes on its threads come
        // at most every 500 ms (DeviceClock's idle wait, the input pump's
        // wait timeout, the audio engine's 250 ms park). The clocks send a
        // frame each 4 and 8 ms, so 150 and 75 frames span 600 ms a run.
        // The input pump keeps 2,000 frames.
        var ds = new byte[63];
        UsbipPath(server, ctx.GetProfile(Composite)!.Inner, "input pump, " + Composite, runs, gate ? 2_000 : 1_000,
            inEp: 4, readLength: 64, frame: ds, extended: false, queueEveryOther: true, start: null, gate);

        // The state block the Switch 2 composite builds its reports from.
        var body = new byte[24];
        UsbipPath(server, ctx.GetProfile("switch2-pro-controller-composite")!.Inner,
            "report clock, switch2-pro-controller-composite", runs, gate ? 150 : 50,
            inEp: 1, readLength: 64, frame: body, extended: true, queueEveryOther: false,
            start: w => w.WaitFor(w.SubmitOut(2, new byte[] { 0x03, 0x91, 0x00, 0x0D, 0x00, 0x08, 0x00, 0x00,
                                                              0x01, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF })), gate);
        UsbipTransfers(server, ctx.GetProfile(Composite)!.Inner, runs, gate);
#if !HM_BASE
        var gcProfile = ctx.GetProfile(GameCube)!.Inner;
        var gcReport = new byte[37];
        gcReport[0] = 0x21;
        UsbipPath(server, gcProfile, "protocol clock, " + GameCube, runs, gate ? 75 : 25,
            inEp: 1, readLength: 37, frame: gcReport, extended: true, queueEveryOther: false,
            start: w => w.WaitFor(w.SubmitOut(2, new byte[] { 0x13 })), gate);
        if (!gate) EngineLock(gcProfile, runs);
#endif
    }

    static void UsbipPath(UsbipServer server, ControllerProfile profile, string name, int runs, int frames,
                          uint inEp, int readLength, byte[] frame, bool extended, bool queueEveryOther,
                          Action<UsbipWire>? start, bool gate)
    {
        var device = new UsbipEmulatedDevice(profile, UsbipIndex);
        server.Register(device);
        try
        {
            using var w = new UsbipWire(server.Port, device.BusId);
            // The server then looks for the host controller's port of this
            // import every 25 ms, 200 times, since no host controller plugs
            // this one in (UsbipServer's TrackVhciPort). The runs below fall
            // inside that time, so they count its lookups too.
            long imported = Stopwatch.GetTimestamp();
            start?.Invoke(w);
            IntPtr view = SharedMemoryIO.EnsureInputMapping(UsbipIndex);
            IntPtr evt = SharedMemoryIO.GetInputEvent(UsbipIndex);
            uint seqNo = (uint)Marshal.ReadInt32(view, 0);
            var times = new double[runs * frames];
            int timed = 0;
            var bytesPerFrame = new List<double>(runs);
            var contentions = new List<double>(runs);
            long worst = 0;
            void Frames(bool record)
            {
                for (int i = 0; i < frames; i++)
                {
                    frame[3] = (byte)i;
                    bool queued = queueEveryOther && (i & 1) == 1;
                    uint seq = queued ? 0 : w.SubmitIn(inEp, readLength);
                    long t0 = Stopwatch.GetTimestamp();
                    if (extended)
                        SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, Array.Empty<byte>(), 0, null,
                            dataOffset: 0, extendedData: frame, extendedLen: frame.Length);
                    else
                        SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, frame, frame.Length, null);
                    if (queued)
                    {
                        // The pump has queued the frame by now, so the read
                        // takes it from the queue.
                        Spin(300);
                        seq = w.SubmitIn(inEp, readLength);
                    }
                    w.WaitFor(seq);
                    long t1 = Stopwatch.GetTimestamp();
                    if (record && !queued) times[timed++] = (t1 - t0) * 1e6 / Stopwatch.Frequency;
                }
            }

            // Warm up for a second and a half, then give the runtime's
            // background recompilation of the hot methods time to finish.
            // With tiered compilation on, that work allocates a few dozen
            // bytes now and then while it runs, on threads not the device's.
            var warm = Stopwatch.StartNew();
            do Frames(record: false);
            while (warm.ElapsedMilliseconds < 1500);
            Thread.Sleep(500);

            double firstRunAt = (Stopwatch.GetTimestamp() - imported) / (double)Stopwatch.Frequency;
            for (int run = 0; run < runs; run++)
            {
                long bytes0 = GC.GetTotalAllocatedBytes(precise: true);
                long locks0 = Monitor.LockContentionCount;
                Frames(record: true);
                long bytes = GC.GetTotalAllocatedBytes(precise: true) - bytes0;
                long locks = Monitor.LockContentionCount - locks0;
                worst = Math.Max(worst, bytes);
                bytesPerFrame.Add((double)bytes / frames);
                contentions.Add((double)locks / frames);
            }
            Report($"USB/IP {name}: bytes the process allocated per frame", bytesPerFrame, runs, $", most in one run of {frames} frames {worst}");
            Report($"USB/IP {name}: frame written to the host's reply, microseconds", new List<double>(times[..timed]), runs, "");
            Report($"USB/IP {name}: lock contentions per frame", contentions, runs, "");
            Console.WriteLine(FormattableString.Invariant(
                $"  USB/IP {name}: the runs took {firstRunAt:F1} s to {(Stopwatch.GetTimestamp() - imported) / (double)Stopwatch.Frequency:F1} s after the import"));
            // Every run carries an allocation a frame makes, so the median
            // run is zero only when no frame allocates. The runtime's tiered
            // recompilation adds a few dozen bytes to an odd run, and none
            // with tiered compilation off.
            if (gate)
            {
                var sorted = bytesPerFrame.OrderBy(x => x).ToList();
                double median = sorted[sorted.Count / 2];
                Check($"USB/IP {name}: a frame allocates nothing", median == 0,
                      $"median run {median * frames:F0} bytes, worst {worst}, over {runs} runs of {frames} frames");
            }
        }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(UsbipIndex);
        }
    }

    /// <summary>A DualSense composite's transfers from the host: an output
    /// report on the interrupt OUT endpoint (rumble and the light bar), ten
    /// packets of speaker PCM on the isochronous OUT endpoint and ten of
    /// microphone on the isochronous IN endpoint, each set completed before
    /// the next, on the audio pump's 1 ms pacing. A consumer takes the
    /// speaker's PCM as HMUsbAudio does. Counted per transfer, three a
    /// set.</summary>
    static void UsbipTransfers(UsbipServer server, ControllerProfile profile, int runs, bool gate)
    {
        const string name = "output and audio transfers, " + Composite;
        const int Sets = 25, Packets = 10, SpeakerPacket = 392, MicPacket = 196;
        var device = new UsbipEmulatedDevice(profile, UsbipIndex);
        server.Register(device);
        int heard = 0;
        device.Audio.OutFrames += _ => heard++;
        try
        {
            using var w = new UsbipWire(server.Port, device.BusId);
            long imported = Stopwatch.GetTimestamp();
            var output = new byte[48];
            output[0] = 0x02;   // DualSense USB output report
            var pcm = new byte[Packets * SpeakerPacket];

            void Transfers()
            {
                for (int i = 0; i < Sets; i++)
                {
                    output[4] = (byte)i;
                    pcm[0] = (byte)i;
                    w.WaitFor(w.SubmitOut(3, output));
                    uint speaker = w.SubmitIsoOut(1, pcm, Packets, SpeakerPacket);
                    uint mic = w.SubmitIsoIn(2, Packets, MicPacket);
                    w.WaitForBoth(speaker, mic);
                }
            }

            var warm = Stopwatch.StartNew();
            do Transfers();
            while (warm.ElapsedMilliseconds < 1500);
            Thread.Sleep(500);

            double firstRunAt = (Stopwatch.GetTimestamp() - imported) / (double)Stopwatch.Frequency;
            var perTransfer = new List<double>(runs);
            long worst = 0;
            for (int run = 0; run < runs; run++)
            {
                long bytes0 = GC.GetTotalAllocatedBytes(precise: true);
                Transfers();
                long bytes = GC.GetTotalAllocatedBytes(precise: true) - bytes0;
                worst = Math.Max(worst, bytes);
                perTransfer.Add((double)bytes / (Sets * 3));
            }
            Report($"USB/IP {name}: bytes the process allocated per transfer", perTransfer, runs, $", most in one run of {Sets * 3} transfers {worst}");
            Console.WriteLine(FormattableString.Invariant(
                $"  USB/IP {name}: the runs took {firstRunAt:F1} s to {(Stopwatch.GetTimestamp() - imported) / (double)Stopwatch.Frequency:F1} s after the import, {heard} speaker windows heard"));
            if (gate)
            {
                var sorted = perTransfer.OrderBy(x => x).ToList();
                double median = sorted[sorted.Count / 2];
                Check($"USB/IP {name}: a transfer allocates nothing", median == 0,
                      $"median run {median * Sets * 3:F0} bytes, worst {worst}, over {runs} runs of {Sets * 3} transfers");
            }
        }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(UsbipIndex);
        }
    }

#if !HM_BASE
    /// <summary>What the protocol engine's lock costs a frame: SetReport,
    /// the one call a frame makes into the engine, timed whole on one
    /// thread, beside an uncontended lock taken and released alone.</summary>
    static void EngineLock(ControllerProfile gc, int runs)
    {
        var engine = new ProtocolEngine(CompiledProtocol.Compile(gc, new UsbDescriptorSet(gc))!);
        var report = new byte[37];
        report[0] = 0x21;
        long origin = Stopwatch.GetTimestamp();
        const int Calls = 200_000;
        for (int i = 0; i < Calls; i++) engine.SetReport(report, origin + i, origin);
        var set = new List<double>(runs);
        var bare = new List<double>(runs);
        object sync = new();
        int n = 0;
        for (int run = 0; run < runs; run++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < Calls; i++)
            {
                report[3] = (byte)i;
                engine.SetReport(report, t0 + i * 1000L, origin);
            }
            long t1 = Stopwatch.GetTimestamp();
            for (int i = 0; i < Calls; i++) lock (sync) n++;
            long t2 = Stopwatch.GetTimestamp();
            set.Add((t1 - t0) * 1e9 / Stopwatch.Frequency / Calls);
            bare.Add((t2 - t1) * 1e9 / Stopwatch.Frequency / Calls);
        }
        Report("ProtocolEngine.SetReport, lock included, ns per frame", set, runs, "");
        Report("an uncontended lock taken and released alone, ns", bare, runs, $" ({n} entries)");
    }
#endif

    /// <summary>The host controller's side of one USB/IP connection,
    /// allocating nothing per transfer. IN transfers take odd seqnums and
    /// OUT transfers even ones, so a reply's seqnum says whether data follows
    /// it.</summary>
    sealed class UsbipWire : IDisposable
    {
        readonly Socket _s;
        readonly byte[] _header = new byte[48];
        readonly byte[] _data = new byte[8192];
        readonly byte[] _descs = new byte[16 * 64];
        uint _seq = 1;

        public UsbipWire(int port, string busId)
        {
            _s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, ReceiveTimeout = 3000 };
            _s.Connect(IPAddress.Loopback, port);
            var import = new byte[8 + 32];
            BinaryPrimitives.WriteUInt16BigEndian(import, 0x0111);
            BinaryPrimitives.WriteUInt16BigEndian(import.AsSpan(2), 0x8003);   // OP_REQ_IMPORT
            System.Text.Encoding.ASCII.GetBytes(busId).CopyTo(import, 8);
            Send(import);
            var reply = new byte[8 + 312];
            Receive(reply);
            if (BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(4)) != 0)
                throw new InvalidOperationException($"import of {busId} refused");
        }

        uint Next(bool isIn)
        {
            uint s = _seq++;
            if (((s & 1) == 1) != isIn) s = _seq++;
            return s;
        }

        void Submit(uint seq, bool isIn, uint ep, int length, int packets = -1, ulong setup = 0)
        {
            Array.Clear(_header);
            BinaryPrimitives.WriteUInt32BigEndian(_header, 1);                  // CMD_SUBMIT
            BinaryPrimitives.WriteUInt32BigEndian(_header.AsSpan(4), seq);
            BinaryPrimitives.WriteUInt32BigEndian(_header.AsSpan(12), isIn ? 1u : 0u);
            BinaryPrimitives.WriteUInt32BigEndian(_header.AsSpan(16), ep);
            BinaryPrimitives.WriteInt32BigEndian(_header.AsSpan(24), length);
            BinaryPrimitives.WriteInt32BigEndian(_header.AsSpan(32), packets);  // -1: not isochronous
            BinaryPrimitives.WriteUInt64LittleEndian(_header.AsSpan(40), setup); // the setup packet, wire order
            Send(_header);
        }

        /// <summary>The 8 setup bytes as the header carries them: in wire
        /// order, as usbip_server_check's client builds them (USB 2.0
        /// section 9.3).</summary>
        static ulong Setup(byte requestType, byte request, ushort value, ushort index, ushort length) =>
            requestType | (ulong)request << 8 | (ulong)value << 16 | (ulong)index << 32 | (ulong)length << 48;

        /// <summary>A control transfer to the host on endpoint 0. Its data
        /// is LastData once WaitFor returns.</summary>
        public uint SubmitControlIn(byte requestType, byte request, ushort value, ushort index, ushort length)
        {
            uint s = Next(true);
            Submit(s, true, 0, length, setup: Setup(requestType, request, value, index, length));
            return s;
        }

        /// <summary>A control transfer from the host on endpoint 0, with its
        /// data stage.</summary>
        public uint SubmitControlOut(byte requestType, byte request, ushort value, ushort index, ReadOnlySpan<byte> data)
        {
            uint s = Next(false);
            Submit(s, false, 0, data.Length, setup: Setup(requestType, request, value, index, (ushort)data.Length));
            Send(data);
            return s;
        }

        /// <summary>The data of the last IN reply read.</summary>
        public ReadOnlySpan<byte> LastData => _data.AsSpan(0, _lastActual);
        int _lastActual;

        public uint SubmitIn(uint ep, int length)
        {
            uint s = Next(true);
            Submit(s, true, ep, length);
            return s;
        }

        public uint SubmitOut(uint ep, ReadOnlySpan<byte> data)
        {
            uint s = Next(false);
            Submit(s, false, ep, data.Length);
            Send(data);
            return s;
        }

        /// <summary>Isochronous OUT: the data, then one descriptor a
        /// packet.</summary>
        public uint SubmitIsoOut(uint ep, ReadOnlySpan<byte> data, int packets, int perPacket)
        {
            uint s = Next(false);
            Submit(s, false, ep, data.Length, packets);
            Send(data);
            SendDescriptors(packets, perPacket);
            return s;
        }

        public uint SubmitIsoIn(uint ep, int packets, int perPacket)
        {
            uint s = Next(true);
            Submit(s, true, ep, packets * perPacket, packets);
            SendDescriptors(packets, perPacket);
            return s;
        }

        void SendDescriptors(int packets, int perPacket)
        {
            var d = _descs.AsSpan(0, packets * 16);
            d.Clear();
            for (int i = 0; i < packets; i++)
            {
                BinaryPrimitives.WriteInt32BigEndian(d[(i * 16)..], i * perPacket);       // offset
                BinaryPrimitives.WriteInt32BigEndian(d[(i * 16 + 4)..], perPacket);       // length
            }
            Send(d);
        }

        /// <summary>Read replies up to <paramref name="seq"/>'s and return
        /// its status.</summary>
        public int WaitFor(uint seq)
        {
            while (true)
            {
                if (ReadReply(out int status) == seq) return status;
            }
        }

        /// <summary>Read replies until both seqnums have answered, in either
        /// order.</summary>
        public void WaitForBoth(uint a, uint b)
        {
            bool gotA = false, gotB = false;
            while (!gotA || !gotB)
            {
                uint got = ReadReply(out _);
                gotA |= got == a;
                gotB |= got == b;
            }
        }

        uint ReadReply(out int status)
        {
            Receive(_header);
            if (BinaryPrimitives.ReadUInt32BigEndian(_header) != 3)
                throw new InvalidOperationException("a reply that is not RET_SUBMIT");
            uint got = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4));
            status = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(20));
            int actual = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(24));
            int packets = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(32));
            if ((got & 1) == 1 && actual > 0) Receive(_data.AsSpan(0, actual));
            if ((got & 1) == 1) _lastActual = Math.Max(0, actual);
            if (packets > 0) Receive(_descs.AsSpan(0, packets * 16));
            return got;
        }

        void Send(ReadOnlySpan<byte> b)
        {
            while (!b.IsEmpty) b = b[_s.Send(b)..];
        }

        void Receive(Span<byte> b)
        {
            while (!b.IsEmpty)
            {
                int n = _s.Receive(b);
                if (n <= 0) throw new IOException("the device closed the connection");
                b = b[n..];
            }
        }

        public void Dispose() => _s.Dispose();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    static void Report(string name, List<double> samples, int runs, string tail)
    {
        if (samples.Count == 0)
        {
            Console.WriteLine($"  {name}: no samples{tail}");
            return;
        }
        var s = samples.OrderBy(x => x).ToList();
        double P(double q) => s[Math.Clamp((int)Math.Ceiling(q * s.Count) - 1, 0, s.Count - 1)];
        Console.WriteLine(FormattableString.Invariant(
            $"  {name}: p50 {P(0.50):F3}, p95 {P(0.95):F3}, max {s[^1]:F3} ({s.Count} samples, {runs} runs){tail}"));
    }

#if !HM_BASE
    /// <summary>hid.dll's HidD_* functions return BOOLEAN, one byte
    /// (hidsdi.h). On an invalid handle this machine's HidD_GetAttributes
    /// returns FALSE in AL with the rest of EAX nonzero, which a four-byte
    /// BOOL read as TRUE. The SDK's own declaration, called as the profile
    /// extractor calls it.</summary>
    static void HidBoolean()
    {
        var type = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.HidDeviceEnumerator")!;
        var getAttributes = type.GetMethod("HidD_GetAttributes", Any)!;
        var attrType = type.GetNestedType("HIDD_ATTRIBUTES", Any)!;
        object attrs = Activator.CreateInstance(attrType)!;
        attrType.GetField("Size", Any)!.SetValue(attrs, Marshal.SizeOf(attrType));
        bool result = (bool)getAttributes.Invoke(null, new object[] { new IntPtr(-1), attrs })!;
        Check("HidD_GetAttributes on an invalid handle reads false through the SDK's declaration", !result, $"returned {result}");
    }
#endif

    static void Check(string name, bool ok, string detail)
    {
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}  {detail}");
    }

    static void Spin(long micros)
    {
        long end = Stopwatch.GetTimestamp() + micros * Stopwatch.Frequency / 1_000_000;
        while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(64);
    }

    static string TimingLog() => Path.Combine(Path.GetTempPath(), "HIDMaestro", "setup_timing.log");

    /// <summary>The SDK's step timings for one profile written to the log
    /// after <paramref name="from"/>, by step.</summary>
    static List<(string Step, List<double> Values)> TimingSteps(string log, long from, string profileId)
    {
        var steps = new Dictionary<string, List<double>>();
        if (!File.Exists(log)) return new();
        using var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(Math.Min(from, fs.Length), SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // [hh:mm:ss.fff] ctrl=N profile=<id> step=<step> <n>ms
            int p = line.IndexOf(" profile=", StringComparison.Ordinal);
            int s = line.IndexOf(" step=", StringComparison.Ordinal);
            if (p < 0 || s < 0) continue;
            string profile = line.Substring(p + 9, s - p - 9).Trim();
            if (!string.Equals(profile, profileId, StringComparison.Ordinal)) continue;
            string rest = line[(s + 6)..].Trim();
            int sp = rest.LastIndexOf(' ');
            if (sp < 0) continue;
            string step = rest[..sp].Trim();
            string ms = rest[(sp + 1)..].Trim().TrimEnd('s').TrimEnd('m');
            if (!double.TryParse(ms, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) continue;
            if (!steps.TryGetValue(step, out var list)) steps[step] = list = new List<double>();
            list.Add(v);
        }
        return steps.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => (k.Key, k.Value)).ToList();
    }

    /// <summary>Whether a process runs elevated. An unelevated process
    /// cannot create, remove or sweep a device, so another lane's offline
    /// probe with the SDK loaded is no consumer while a live run holds the
    /// bench. A token that cannot be read counts as elevated.</summary>
    static class ConsumerElevation
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        static extern System.IntPtr OpenProcess(uint access, bool inherit, int pid);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool CloseHandle(System.IntPtr h);
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        static extern bool OpenProcessToken(System.IntPtr process, uint access, out System.IntPtr token);
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(System.IntPtr token, int cls, out int value, int length, out int returned);

        // PROCESS_QUERY_LIMITED_INFORMATION, TOKEN_QUERY, TokenElevation (20).
        public static bool IsElevated(int pid)
        {
            System.IntPtr p = OpenProcess(0x1000, false, pid);
            if (p == System.IntPtr.Zero) return true;
            try
            {
                if (!OpenProcessToken(p, 0x0008, out System.IntPtr t)) return true;
                try { return !GetTokenInformation(t, 20, out int v, 4, out _) || v != 0; }
                finally { CloseHandle(t); }
            }
            finally { CloseHandle(p); }
        }
    }

    static bool OtherSdkRunning()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId || !ConsumerElevation.IsElevated(p.Id)) continue;
                foreach (ProcessModule m in p.Modules)
                    if (string.Equals(m.ModuleName, "HIDMaestro.Core.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"  [SKIP] {p.ProcessName} ({p.Id}) is running and shares the SDK's devices.");
                        return true;
                    }
            }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        return false;
    }

    const uint FILE_MAP_READ = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr map, uint access, uint offHigh, uint offLow, UIntPtr bytes);

    [DllImport("kernel32.dll")]
    static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buffer, int length);
}
