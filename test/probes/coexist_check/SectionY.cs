// Y. The driver's named events, and a v1.12.0 owner pipe another program
//    fakes. A thread of this probe impersonates the user's medium-integrity
//    token (explorer's, duplicated), so the program that is not elevated is a
//    thread, never a process.
//
//    Y1. On a controller of this SDK, the user cannot open
//        Global\HIDMaestroStopEvent<N> to signal it, or
//        Global\HIDMaestroOutputEvent<N> to wait on it or reset it, and each
//        event's DACL admits SYSTEM, Administrators and LocalService only.
//        The program, and a v1.12.0 program, still open both the way their
//        SDKs do. A v1.12.0 program's controller moved to the installed
//        driver gets the same StopEvent. Its OutputEvent, which the program
//        still holds from the earlier driver, gets the same DACL (Y11).
//        Both programs' input keeps flowing.
//    Y2. The user creates both names, the StopEvent signaled, after a create
//        of an Xbox 360 controller took its transport index and before its
//        device exists. The controller's driver takes neither: the StopEvent
//        stays signaled, and rumble sent through XInput never signals the
//        user's OutputEvent. XInput still reads the controller's input. The
//        program's own output reader does not wait on the user's event, so
//        rumble reaches it on the 8 ms poll. Once the user's names are gone,
//        and while the program still streams input, the driver makes its
//        own OutputEvent, and the reader waits on it again.
//    Y3. A program on this SDK is killed, and the user creates
//        \\.\pipe\HIDMaestroOwner<N> on its index. The driver had recorded
//        the program as its owner pipe's server and then recorded it gone,
//        so the next sweep of a running program removes the killed one's
//        devices.
//    Y4. The same with the driver's values deleted and its ended marker in
//        the acknowledgement section cleared, as a driver that records
//        nothing leaves them: the pipe keeps the devices, as before this
//        release.
//    Y5. A running v1.12.0 program. On its own driver, which records
//        nothing, the census counts its held pipe as before this release,
//        and this SDK's reader takes that driver's NULL-DACL OutputEvent.
//        Moved to the installed driver, the best match the driver store holds,
//        the census sees its owner pipe's server connected, alive and
//        elevated, and every sweep above left its controller running.
//    Y6. Values an earlier device left on the instance, naming a running
//        elevated server, are gone once the device starts on the installed
//        driver, and a v1.11.0 program, which makes no owner pipe, leaves
//        none: the census never reads a previous device's watch as this
//        one's.
//    Y7. A v1.12.0 program on the installed driver is killed. Past the
//        driver's idle handle reset, about 4 s, the driver still holds the
//        index's Global\HIDMaestroInputAck<N>, so the census reads the
//        device as a v1.12.0 program's that has exited, and a sweep
//        removes it.
//    Y8. A reset of the driver's handles, forced by a signal on the
//        StopEvent as a sweep sends it, leaves the release-on-exit bit in
//        the acknowledgement section's capabilities, and the SDK still
//        reads release-on-exit support.
//    Y9. The user creates \\.\pipe\HIDMaestroOwner0 before a v1.12.0 program
//        starts on index 0. Moved to the installed driver, its controller's
//        watch connects to a pipe it cannot vouch for, and a sweep still
//        leaves the running program's devices alone.
//    Y10. The driver's values name a process created after they were
//        written, as a reused process id would, and the user holds the
//        killed program's pipe name: the sweep removes the devices.
//    Y12. A program that is not elevated holds an EVENT_ALL_ACCESS handle to
//        a v1.12.0 device's NULL-DACL StopEvent from before the move. Once
//        the controller is on the installed driver, signaling that handle
//        does not disturb the controller, because the new driver refused the
//        object and runs an unnamed StopEvent.
//    Y13. A program that is not elevated holds a dead program's input section
//        and input event open and creates the owner pipe after the kill. The
//        driver does not reconnect its watch, so the census reads the device
//        gone and a sweep removes it.
//    Y14. A killed program's device whose driver values are gone, as a driver
//        that records nothing leaves them, after the driver has let go of
//        the input section. The user holds the owner pipe. With no input
//        section, which only a running program can create, the pipe does
//        not count, and a sweep removes the device.
//    Y15. The driver's values carry a key write time out of the range a
//        FILETIME converts to. The census completes instead of throwing.
//    Y16. A running program lets go of its input event. Its controller's
//        own copy of the handle now points at an unnamed event, and the
//        program sends input, which signals that live handle and not the
//        index's event. The driver, past its idle handle reset, waits for
//        the index's event to come back. The program is then killed. The driver records its owner pipe closed
//        while it waits, as it does while input flows.
//    Y17. A program's gamepad companion outlives it, the driver having
//        recorded its watch ended. The program created it on transport index
//        0 while no v1.12.0 controller existed, since beside one this SDK
//        creates from index 64 up. A v1.12.0 program, which ran its
//        once-per-session cleanup on a controller it disposed before, then
//        creates the same profile on index 0 and adopts the companion, and
//        the restart the adoption asks for is refused while the probe holds
//        the HID child open. The driver watches the new
//        program's pipe, the census reads its controller as a running
//        program's, and a sweep leaves it.
//    Y18. The same, with the dead program streaming input until it is
//        killed, so the driver's idle window starts at the kill, and the
//        v1.12.0 program streaming without a pause from the moment its
//        create returns, inside that window. The driver never maps the input
//        section again, and watches the new program's pipe all the same.
//    Y19. Y13's squatter, holding the acknowledgement section as well, and
//        then the dead program's device is added again, so a new driver
//        context starts with no values. The section still carries the
//        driver's ended marker, so the census does not count the held pipe,
//        and a sweep removes the device.
//    Y20. The same without the acknowledgement section, which goes with the
//        old context. With no such section the census does not count the
//        held pipe either.
//    Y11. A v1.12.0 program's DualShock 4 moved onto the installed driver
//        while its SDK holds the earlier driver's OutputEvent. The probe
//        writes 31 output reports 100 ms apart and times each one to the
//        program's OutputReceived. Then it kills the program and times the
//        sweep that removes the device. The user still cannot open the
//        OutputEvent, and every output arrives within 20 ms.
//
//    Every part runs v1.12.0 programs whose staging carries the signed
//    catalogs section E's install leaves there, so their creates install
//    v1.12.0's own driver on any machine (LegacyStagingCataloged).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

internal static partial class Program
{
    static void SectionY()
    {
        foreach (var v in new[] { "v1.12.0", "v1.11.0" })
            if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", v, "CoexistPeer.exe")))
            {
                Check($"Y: the {v} peer is built", false, "build CoexistCheck.csproj");
                return;
            }
        IntPtr user = Y.UserToken(out string why);
        Check("Y: the user's medium-integrity token is at hand", user != IntPtr.Zero, why);
        if (user == IntPtr.Zero) return;
        try
        {
            LegacyStagingCataloged("Y");
            string? installed = SectionYBody(user);
            if (installed == null) return;
            // Programs before v1.13.0 take index 0 whatever runs, so these
            // run alone.
            SectionYStale(installed);
            SectionYAckKept(installed);
            SectionYPreSquat(user, installed);
            SectionYReused(user);
            SectionYLegacyEvents(user, installed);
            SectionYHeldSection(user);
            SectionYNoSection(user);
            SectionYBadTime(user);
            SectionYWatchWhileWaiting();
            SectionYAdopted(installed, "Y17", stream: false);
            SectionYAdopted(installed, "Y18", stream: true);
            SectionYReAdded(user, "Y19", holdAck: true);
            SectionYReAdded(user, "Y20", holdAck: false);
        }
        finally { Y.CloseHandle(user); }
    }

    /// <summary>Y9. The v1.12.0 owner pipe's name, taken by the user before
    /// a v1.12.0 program starts on the index.</summary>
    static void SectionYPreSquat(IntPtr user, string installed)
    {
        Clean();
        using var fake = Y.AsUser(user, () => Y.FakeOwnerPipe(0));
        Check(@"Y9: the user creates \\.\pipe\HIDMaestroOwner0 first", fake != null);
        if (fake == null) return;
        using var old = PeerProcess.Start("Y-v1.12.0-squatted", "v1.12.0");
        string co = old.Send("create pad F1F7 coexist-y-presquat");
        if (!Ok(co)) { Check("Y9: the v1.12.0 program creates", false, co); return; }
        old.Send("stream 5 0.4 125 0");
        string inst = PeerProcess.Field(co, "instance")!;
        string tok = inst.Substring(inst.LastIndexOf('\\') + 1);
        bool moved = Y.InstallBestStoreDriver(inst)
                     && WaitFor(() => Native.DriverVersionOf(inst) == installed && Native.IsStarted(inst), 20_000)
                     && WaitFor(() => Y.WatchValues(inst) is { Connected: 1 }, 10_000);
        Check("Y9: its controller runs on the installed driver, whose watch connects", moved,
              $"{Native.DriverVersionOf(inst)}, installed {installed}, {Y.ShowWatch(inst)}");
        if (!moved) return;
        var tree = Native.Subtree(inst);
        var path = WaitPaths(0xBEEF, 0xF1F7, 1, 10_000);
        using var cap = path.Count == 1 ? HidCapture.Open(path[0]) : null;
        using var nu = PeerProcess.Start("Y-presquat-sweeper");
        string? entry = Y.CensusEntry(nu.Send("census"), tok);
        Check("Y9: the census holds the device as a running v1.12.0 program's",
              entry?.Split('|')[2] == "LiveLegacy", entry ?? "no entry");
        nu.Send("sweep");
        Thread.Sleep(2000);
        Check("Y9: a sweep leaves the running program's devices", tree.Count > 0 && tree.All(Native.IsPresent),
              string.Join(", ", tree.Where(d => !Native.IsPresent(d))));
        Check("Y9: and its input keeps flowing", cap != null && !cap.Broken && Flows(cap));
    }

    /// <summary>Y11. Output latency and removal time of a v1.12.0 program's
    /// controller moved onto the installed driver.</summary>
    static void SectionYLegacyEvents(IntPtr user, string installed)
    {
        Clean();
        string inst;
        int t;
        using (var old = PeerProcess.Start("Y-v1.12.0-ds4", "v1.12.0"))
        {
            string co = old.Send("create ds4 - coexist-y-ds4");
            if (!Ok(co)) { Check("Y11: the v1.12.0 program creates a DualShock 4", false, co); return; }
            inst = PeerProcess.Field(co, "instance")!;
            t = int.Parse(PeerProcess.Field(co, "transport")!);
            Check("Y11: its output reader is subscribed", Ok(old.Send("outputs on")));

            // Y12: open the v1.12.0 device's NULL-DACL StopEvent for every
            // right, from the medium thread, before the move. The handle
            // keeps whatever it is granted now.
            string stopName = $@"Global\HIDMaestroStopEvent{t}";
            IntPtr heldStop = Y.AsUser(user, () => Y.OpenEventW(Y.EVENT_ALL_ACCESS, false, stopName));
            Check("Y12: the user opens the v1.12.0 StopEvent for every right (its DACL is null)", heldStop != IntPtr.Zero);

            bool moved = Y.InstallBestStoreDriver(inst)
                         && WaitFor(() => Native.DriverVersionOf(inst) == installed && Native.IsStarted(inst), 20_000)
                         && WaitFor(() => Y.WatchValues(inst) is { Connected: 1 }, 10_000);
            Check("Y11: the controller moves to the installed driver", moved, $"{Native.DriverVersionOf(inst)}, installed {installed}");
            if (!moved) { if (heldStop != IntPtr.Zero) Y.CloseHandle(heldStop); return; }

            // Y12: signal the held StopEvent while the controller streams
            // input. The refused object is not the worker's, so a signal on
            // it reaches nothing. The takeover made it the worker's event.
            if (heldStop != IntPtr.Zero)
            {
                old.Send("stream 5 0.5 125 0");
                var pathIn = WaitPaths(0x054C, 0x09CC, 1, 10_000);
                using var capIn = pathIn.Count == 1 ? HidCapture.Open(pathIn[0]) : null;
                if (capIn == null) Check("Y12: the moved controller's input opens", false);
                else
                {
                    // The StopEvent is manual-reset. If the new worker waits
                    // on this object, it resets it on the signal. The
                    // takeover made the held object the worker's, so the
                    // signal was consumed and the worker recycled. The
                    // refusal leaves the worker an unnamed event, so the
                    // signal stays and input is untouched.
                    Y.SetEvent(heldStop);
                    Thread.Sleep(500);
                    bool consumed = Y.WaitForSingleObject(heldStop, 0) != Y.WAIT_OBJECT_0;
                    Check("Y12: the new worker does not consume a signal on the held StopEvent, and input flows",
                          !consumed && Native.IsStarted(inst) && !capIn.Broken && Flows(capIn),
                          consumed ? "the worker reset the held event" : "");
                }
                old.Send("stopstream");
                Y.CloseHandle(heldStop);
            }
            string output = $@"Global\HIDMaestroOutputEvent{t}";
            int err = Y.AsUser(user, () => Y.OpenError(Y.SYNCHRONIZE, output));
            Check($"Y11: the user cannot open {output}", err == Y.ERROR_ACCESS_DENIED, Y.Win32(err) + ", " + (Y.EventSddl(output) ?? "none"));

            string? hid = Native.Subtree(inst).Skip(1).SelectMany(c => Native.InterfaceList(Native.HidGuid, c)).FirstOrDefault();
            if (hid == null) { Check("Y11: the controller's HID interface is present", false); return; }
            IntPtr h = Native.CreateFileW(hid, 0x40000000 /*GENERIC_WRITE*/, 3, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
            if (h == Native.INVALID) { Check("Y11: the controller's HID interface opens for writing", false); return; }
            OutputTiming timing;
            try
            {
                timing = TimeOutputs(old, i =>
                {
                    var report = new byte[32];
                    report[0] = 0x05;
                    report[1] = 0x07;
                    report[4] = (byte)(i * 8);
                    report[6] = (byte)i;
                    Y.WriteFile(h, report, report.Length, out _, IntPtr.Zero);
                }, 31, 100);
            }
            finally { Native.CloseHandle(h); }
            Console.WriteLine($"    Y11 output latency: {timing.Shown}");
            Check("Y11: every output arrives within 20 ms", timing.AllArrived && timing.Max < 20, timing.Shown);
            old.Kill();
        }
        var tree = Native.Subtree(inst);
        Thread.Sleep(1000);
        using var nu = PeerProcess.Start("Y-ds4-sweeper");
        var sw = Stopwatch.StartNew();
        string s = nu.Send("sweep");
        bool gone = WaitFor(() => tree.All(Native.IsGone), 30_000);
        Console.WriteLine(FormattableString.Invariant($"    Y11 removal: sweep reply {sw.ElapsedMilliseconds} ms after it was sent, devices gone {(gone ? "yes" : "no")}"));
        Check("Y11: the killed program's controller is removed", Ok(s) && gone, $"{sw.ElapsedMilliseconds} ms");
    }

    /// <summary>Y13. A dead program's input section and event held open, and
    /// the owner pipe taken, must not revive the device.</summary>
    static void SectionYHeldSection(IntPtr user)
    {
        Clean();
        string inst, tok;
        int t;
        using (var p = PeerProcess.Start("Y-held"))
        {
            string cp = p.Send("create pad F1FA coexist-y-held");
            if (!Ok(cp)) { Check("Y13: a program creates", false, cp); return; }
            inst = PeerProcess.Field(cp, "instance")!;
            t = int.Parse(PeerProcess.Field(cp, "transport")!);
            tok = inst.Substring(inst.LastIndexOf('\\') + 1);
            WaitFor(() => Y.WatchValues(inst) is { Connected: 1 }, 5000);
            p.Kill();
        }
        var tree = Native.Subtree(inst);
        // The medium thread keeps the dead program's input section and event
        // alive, so the driver's worker can reach Phase 2 and recycle, and
        // creates the owner pipe. On the pre-fix driver the recycle
        // reconnected the watch to this pipe and recorded it connected.
        IntPtr sec = Y.AsUser(user, () => Y.HoldSection($@"Global\HIDMaestroInput{t}"));
        // READ_CONTROL is within the Everyone read the SDK's event grants, so
        // a medium user may hold it. The right does not matter: any handle
        // keeps the object alive so the driver's Phase 1 can open it again.
        IntPtr ev = Y.AsUser(user, () => Y.OpenEventW(Y.READ_CONTROL, false, $@"Global\HIDMaestroInputEvent{t}"));
        var pipe = Y.AsUser(user, () => Y.FakeOwnerPipe(t));
        Check("Y13: the user holds the dead program's section, event and pipe",
              sec != IntPtr.Zero && ev != IntPtr.Zero && pipe != null,
              $"section {sec != IntPtr.Zero}, event {ev != IntPtr.Zero}, pipe {pipe != null}");
        try
        {
            // Past the worker's eight idle 500 ms waits, so a pre-fix driver
            // has recycled and reconnected the watch.
            Thread.Sleep(7000);
            using var nu = PeerProcess.Start("Y-held-sweeper");
            string? entry = Y.CensusEntry(nu.Send("census"), tok);
            Check("Y13: the census does not count the held pipe as a running program's",
                  entry != null && entry.Split('|')[2] is "Dead" or "DeadLegacy", entry ?? "no entry");
            nu.Send("sweep");
            WaitFor(() => tree.All(Native.IsGone), 5000);
            Check("Y13: a sweep removes the dead program's devices", tree.Count > 0 && tree.All(Native.IsGone),
                  string.Join(", ", tree.Where(d => !Native.IsGone(d))));
        }
        finally
        {
            if (sec != IntPtr.Zero) Y.CloseHandle(sec);
            if (ev != IntPtr.Zero) Y.CloseHandle(ev);
            pipe?.Dispose();
        }
    }

    /// <summary>Y14. A held pipe with no driver values and no input section.</summary>
    static void SectionYNoSection(IntPtr user)
    {
        Clean();
        string inst, tok;
        int t;
        using (var p = PeerProcess.Start("Y-nosection"))
        {
            string cp = p.Send("create pad F1FB coexist-y-nosection");
            if (!Ok(cp)) { Check("Y14: a program creates", false, cp); return; }
            inst = PeerProcess.Field(cp, "instance")!;
            t = int.Parse(PeerProcess.Field(cp, "transport")!);
            tok = inst.Substring(inst.LastIndexOf('\\') + 1);
            WaitFor(() => Y.WatchValues(inst) is { Connected: 1 }, 5000);
            p.Kill();
        }
        var tree = Native.Subtree(inst);
        string section = $@"Global\HIDMaestroInput{t}";
        // Eight idle 500 ms waits, then the driver drops the input section.
        bool sectionGone = WaitFor(() => !Native.SectionExists(section), 12_000);
        Check($"Y14: past the driver's idle reset, {section} is gone", sectionGone);
        Check("Y14: the driver's values are deleted", Y.DeleteWatchValues(inst) && Y.WatchValues(inst) == null, Y.ShowWatch(inst));
        using var fake = Y.AsUser(user, () => Y.FakeOwnerPipe(t));
        Check($"Y14: the user creates \\\\.\\pipe\\HIDMaestroOwner{t}", fake != null);
        using var nu = PeerProcess.Start("Y-nosection-sweeper");
        string? entry = Y.CensusEntry(nu.Send("census"), tok);
        Check("Y14: the census does not count a held pipe with no input section",
              entry?.Split('|')[2] is "Dead" or "DeadLegacy", entry ?? "no entry");
        nu.Send("sweep");
        WaitFor(() => tree.All(Native.IsGone), 5000);
        Check("Y14: a sweep removes the killed program's devices", tree.Count > 0 && tree.All(Native.IsGone),
              string.Join(", ", tree.Where(d => !Native.IsGone(d))));
    }

    /// <summary>Y15. A key write time out of FILETIME's range.</summary>
    static void SectionYBadTime(IntPtr user)
    {
        Clean();
        string inst, tok;
        int t;
        using (var p = PeerProcess.Start("Y-badtime"))
        {
            string cp = p.Send("create pad F1FC coexist-y-badtime");
            if (!Ok(cp)) { Check("Y15: a program creates", false, cp); return; }
            inst = PeerProcess.Field(cp, "instance")!;
            t = int.Parse(PeerProcess.Field(cp, "transport")!);
            tok = inst.Substring(inst.LastIndexOf('\\') + 1);
            WaitFor(() => Y.WatchValues(inst) is { Connected: 1 }, 5000);
            p.Kill();
        }
        WaitFor(() => Y.WatchValues(inst) is { Connected: 0 }, 5000);
        using var nu = PeerProcess.Start("Y-badtime-census");
        bool planted = Y.PlantWatchRaw(inst, nu.Pid, "S-1-5-32-544", long.MaxValue);
        Check("Y15: the values carry a write time past FILETIME's range", planted, Y.ShowWatch(inst));
        using var fake = Y.AsUser(user, () => Y.FakeOwnerPipe(t));
        Check($"Y15: the user creates \\\\.\\pipe\\HIDMaestroOwner{t}", fake != null);
        string census = nu.Send("census");
        Check("Y15: the census completes and lists the device", Ok(census) && Y.CensusEntry(census, tok) != null, census);
    }

    /// <summary>Y16. An owner pipe that closes while the driver waits for
    /// the input event.</summary>
    static void SectionYWatchWhileWaiting()
    {
        Clean();
        using var p = PeerProcess.Start("Y-waiting");
        string cp = p.Send("create pad F1FD coexist-y-waiting");
        if (!Ok(cp)) { Check("Y16: a program creates", false, cp); return; }
        string inst = PeerProcess.Field(cp, "instance")!;
        int t = int.Parse(PeerProcess.Field(cp, "transport")!);
        Check("Y16: the driver's watch connects", WaitFor(() => Y.WatchValues(inst) is { Connected: 1 }, 5000), Y.ShowWatch(inst));
        string drop = p.Send($"dropinputevent {t}");
        Check("Y16: the program lets go of its input event: its controller held the handle the SDK closed, and now holds an unnamed event",
              PeerProcess.Field(drop, "closed") == "1" && PeerProcess.Field(drop, "swapped") == "1"
              && PeerProcess.Field(drop, "matched") == "1" && PeerProcess.Field(drop, "verified") == "1", drop);
        // Frames now signal a live handle that is not the index's event. A
        // program whose frames still reached that event would wake the
        // driver each time, keep it from its idle reset, and fail the
        // next check.
        p.Send($"droppedsignal {t}");
        p.Send("stream 1 0.5 125 0");
        Thread.Sleep(100);
        p.Send("stopstream");
        string signaled = p.Send($"droppedsignal {t}");
        Check("Y16: the program's frames signal the controller's live handle", PeerProcess.Field(signaled, "signaled") == "1", signaled);
        // Eight idle 500 ms waits, then the driver closes the last handle and
        // waits for the event to come back.
        string input = $@"Global\HIDMaestroInputEvent{t}";
        bool gone = WaitFor(() =>
        {
            IntPtr e = Y.OpenEventW(Y.SYNCHRONIZE, false, input);
            if (e == IntPtr.Zero) return true;
            Y.CloseHandle(e);
            return false;
        }, 12_000);
        Check($"Y16: past the driver's idle reset, {input} is gone", gone);
        Check("Y16: the watch stays connected while the program runs", Y.WatchValues(inst) is { Connected: 1 }, Y.ShowWatch(inst));
        p.Kill();
        bool recorded = WaitFor(() => Y.WatchValues(inst) is { Connected: 0 }, 3000);
        Check("Y16: the driver records the closed pipe while it waits for the input event", recorded, Y.ShowWatch(inst));
    }

    /// <summary>Y17 and Y18. A gamepad companion a v1.12.0 program adopts
    /// after its first program died, with the restart refused. With
    /// <paramref name="stream"/> the program streams without a pause from
    /// its create on.</summary>
    static void SectionYAdopted(string installed, string y, bool stream)
    {
        Clean();
        // A v1.12.0 program adopts only a companion whose ControllerIndex is
        // the index its own create takes, the lowest its own controllers
        // leave free (v1.12.0 DeviceOrchestrator.FindExistingCompanion,
        // HMContext.CreateController). While a v1.12.0 program has a
        // controller, this SDK's create starts at transport index 64
        // (ControllerLease.LegacyClearance), out of that program's reach.
        // So the program on this SDK creates while no v1.12.0 controller
        // exists. The v1.12.0 program's first create in a process removes
        // every HIDMaestro device present (its once-per-session
        // CleanupGhostDevices), which would take the companion, so it
        // creates and disposes one controller first. Then it has no device
        // and no owner pipe, so this SDK does not count it as a running
        // v1.12.0 program (ControllerLease.LegacyProgramRuns counts a present
        // device of one), and its next create takes index 0 again and can
        // follow the kill inside the driver's idle window.
        using var old = PeerProcess.Start("Y-v1.12.0-adopter", "v1.12.0");
        string first = old.Send("create pad F1FE coexist-y-adopter-first");
        if (!Ok(first)) { Check($"{y}: the v1.12.0 program creates", false, first); return; }
        string firstInst = PeerProcess.Field(first, "instance")!;
        old.Send("dispose");
        Check($"{y}: the v1.12.0 program's first controller, made for its once-per-session cleanup, is disposed and its device gone",
              WaitFor(() => !Native.IsPresent(firstInst), 15_000), Native.StatusOf(firstInst));
        string inst;
        int t;
        HidCapture? cap;
        using (var p = PeerProcess.Start("Y-adopted"))
        {
            string cp = p.Send("create xinputhid - coexist-y-adopted");
            if (!Ok(cp)) { Check($"{y}: a program creates a gamepad companion", false, cp); return; }
            inst = PeerProcess.Field(cp, "instance")!;
            t = int.Parse(PeerProcess.Field(cp, "transport")!);
            Check($"{y}: with no v1.12.0 controller on the machine, it takes transport index 0, the one a v1.12.0 program's first create takes",
                  t == 0, cp);
            if (t != 0) { p.Kill(); return; }
            Check($"{y}: the driver watches the program", WaitFor(() => Y.WatchValues(inst) is { Connected: 1 }, 10_000), Y.ShowWatch(inst));
            // A handle on the HID child makes Windows refuse the polite
            // disable a restart begins with, as an XInput handle does.
            var path = WaitPaths(0x045E, 0x0B00, 1, 10_000);
            cap = path.Count == 1 ? HidCapture.Open(path[0]) : null;
            Check($"{y}: the probe holds the companion's HID child open", cap != null, string.Join(", ", path));
            // The driver's idle window counts from the last input, not from
            // the kill. With the dead program streaming until it dies, the
            // kill starts a full window of eight 500 ms waits, so a program
            // that attaches and streams inside it gets no recycle and no map.
            if (stream)
            {
                long mark = cap?.Mark() ?? 0;
                p.Send("stream 1 0.5 250 1");
                // Kill only once frames have reached the driver: the peer
                // reports frames submitted, and the probe's handle on the
                // HID child reads them back. The kill then lands within a
                // frame of the last input.
                bool landed = WaitFor(() => int.TryParse(PeerProcess.Field(p.Send("frames"), "n"), out int sent) && sent > 0
                                            && cap != null && cap.Since(mark).Count > 0, 5000);
                Check($"{y}: the dead program's frames reach the device before the kill", landed);
            }
            p.Kill();
        }
        var killed = Stopwatch.StartNew();
        try
        {
            Check($"{y}: the driver records the program gone",
                  WaitFor(() => Y.WatchValues(inst) is { Connected: 0 }, 5000), Y.ShowWatch(inst));
            string second = old.Send("create xinputhid - coexist-y-adopter");
            if (stream)
            {
                // At once, before the window the kill started ends. Each
                // frame wakes the worker, so it never recycles and never
                // maps the section again.
                old.Send("stream 1 0.5 250 1");
                Check($"{y}: the program attaches and streams inside the driver's idle window",
                      killed.ElapsedMilliseconds < 3500, $"{killed.ElapsedMilliseconds} ms after the kill");
                Check($"{y}: its input reaches the device", cap != null && Flows(cap));
            }
            Check($"{y}: the v1.12.0 program's create adopts the companion on its index",
                  Ok(second) && string.Equals(PeerProcess.Field(second, "instance"), inst, StringComparison.OrdinalIgnoreCase)
                  && PeerProcess.Field(second, "transport") == t.ToString(), second);
            Check($"{y}: the restart was refused: the probe's handle still reads and the device keeps the installed driver",
                  cap != null && !cap.Broken && Native.DriverVersionOf(inst) == installed,
                  $"read error {cap?.ReadError}, {Native.DriverVersionOf(inst)}, installed {installed}");
            bool watched = WaitFor(() => Y.WatchValues(inst) is { Connected: 1 } w && w.Pid == old.Pid, 12_000);
            Check($"{y}: the driver watches the v1.12.0 program's pipe", watched, Y.ShowWatch(inst) + $", program {old.Pid}");
            var tree = Native.Subtree(inst);
            using var nu = PeerProcess.Start("Y-adopted-sweeper");
            string census = nu.Send("census");
            string? entry = census.Split(' ').FirstOrDefault(e => e.Split('|') is { Length: > 2 } f && f[1] == t.ToString());
            Check($"{y}: the census reads the controller as a running v1.12.0 program's", entry?.Split('|')[2] == "LiveLegacy", entry ?? census);
            nu.Send("sweep");
            Thread.Sleep(2000);
            Check($"{y}: a sweep leaves it", tree.Count > 0 && tree.All(d => !Native.IsGone(d)),
                  string.Join(", ", tree.Where(Native.IsGone)));
        }
        finally
        {
            if (stream) old.Send("stopstream");
            cap?.Dispose();
        }
    }

    /// <summary>Y19 and Y20. Y13's squatter, then a re-add of the dead
    /// program's device. With <paramref name="holdAck"/> the user also holds
    /// the acknowledgement section.</summary>
    static void SectionYReAdded(IntPtr user, string y, bool holdAck)
    {
        Clean();
        string inst, tok;
        int t;
        using (var p = PeerProcess.Start("Y-readded"))
        {
            string cp = p.Send($"create pad {(holdAck ? "F1E0" : "F1E1")} coexist-y-readded");
            if (!Ok(cp)) { Check($"{y}: a program creates", false, cp); return; }
            inst = PeerProcess.Field(cp, "instance")!;
            t = int.Parse(PeerProcess.Field(cp, "transport")!);
            tok = inst.Substring(inst.LastIndexOf('\\') + 1);
            WaitFor(() => Y.WatchValues(inst) is { Connected: 1 }, 5000);
            p.Kill();
        }
        Check($"{y}: the driver records the program gone", WaitFor(() => Y.WatchValues(inst) is { Connected: 0 }, 5000), Y.ShowWatch(inst));
        var tree = Native.Subtree(inst);
        IntPtr sec = Y.AsUser(user, () => Y.HoldSection($@"Global\HIDMaestroInput{t}"));
        IntPtr ack = holdAck ? Y.AsUser(user, () => Y.HoldSection($@"Global\HIDMaestroInputAck{t}")) : IntPtr.Zero;
        IntPtr ev = Y.AsUser(user, () => Y.OpenEventW(Y.READ_CONTROL, false, $@"Global\HIDMaestroInputEvent{t}"));
        var pipe = Y.AsUser(user, () => Y.FakeOwnerPipe(t));
        Check($"{y}: the user holds the dead program's section, event and pipe" + (holdAck ? ", and its acknowledgement section" : ""),
              sec != IntPtr.Zero && ev != IntPtr.Zero && pipe != null && (!holdAck || ack != IntPtr.Zero),
              $"section {sec != IntPtr.Zero}, ack {ack != IntPtr.Zero}, event {ev != IntPtr.Zero}, pipe {pipe != null}");
        try
        {
            // A disable and enable gives the device a new driver context,
            // which deletes the previous context's values when it starts.
            bool readded = Y.RestartDevice(inst) && WaitFor(() => Native.IsStarted(inst), 20_000);
            Check($"{y}: the device is added again, and the new context has no values",
                  readded && WaitFor(() => Y.WatchValues(inst) == null, 5000), Y.ShowWatch(inst));
            // Each case must reach its own branch of the census's rule.
            string ackName = $@"Global\HIDMaestroInputAck{t}";
            if (holdAck)
                Check($"{y}: the acknowledgement section still carries the driver's ended marker",
                      Y.AckCapabilities(ackName) is int caps && (caps & unchecked((int)0x80000000)) != 0,
                      $"capabilities {Y.AckCapabilities(ackName)?.ToString("X") ?? "none"}");
            else
                Check($"{y}: the acknowledgement section is gone", !Native.SectionExists(ackName));
            // The new worker maps the held section within its first 200 ms
            // tick. Past that, it has refused the user's pipe.
            Thread.Sleep(3000);
            Check($"{y}: the new context does not connect to the user's pipe", Y.WatchValues(inst) == null, Y.ShowWatch(inst));
            using var nu = PeerProcess.Start("Y-readded-sweeper");
            string? entry = Y.CensusEntry(nu.Send("census"), tok);
            Check($"{y}: the census does not count the held pipe as a running program's",
                  entry != null && entry.Split('|')[2] is "Dead" or "DeadLegacy", entry ?? "no entry");
            nu.Send("sweep");
            WaitFor(() => tree.All(Native.IsGone), 5000);
            Check($"{y}: a sweep removes the dead program's devices", tree.Count > 0 && tree.All(Native.IsGone),
                  string.Join(", ", tree.Where(d => !Native.IsGone(d))));
        }
        finally
        {
            if (sec != IntPtr.Zero) Y.CloseHandle(sec);
            if (ack != IntPtr.Zero) Y.CloseHandle(ack);
            if (ev != IntPtr.Zero) Y.CloseHandle(ev);
            pipe?.Dispose();
        }
    }

    /// <summary>Y10. Values that name a process created after them.</summary>
    static void SectionYReused(IntPtr user)
    {
        Clean();
        string inst, tok;
        int t;
        using (var c = PeerProcess.Start("Y-reused-killed"))
        {
            string cc = c.Send("create pad F1F8 coexist-y-reused");
            if (!Ok(cc)) { Check("Y10: a program creates", false, cc); return; }
            inst = PeerProcess.Field(cc, "instance")!;
            t = int.Parse(PeerProcess.Field(cc, "transport")!);
            tok = inst.Substring(inst.LastIndexOf('\\') + 1);
            WaitFor(() => Y.WatchValues(inst) is { Connected: 1 }, 5000);
            c.Kill();
        }
        var tree = Native.Subtree(inst);
        WaitFor(() => Y.WatchValues(inst) is { Connected: 0 }, 5000);
        using var later = PeerProcess.Start("Y-reused-later");
        // The values name this later, elevated process. The key's write time
        // is set back before it started, as if the driver had recorded a
        // server whose id this process took over.
        bool planted = Y.PlantWatch(inst, later.Pid, "S-1-5-32-544", Y.ProcessCreated(later.Pid).AddSeconds(-5));
        Check("Y10: the values name a process created after they were written", planted, Y.ShowWatch(inst));
        using var fake = Y.AsUser(user, () => Y.FakeOwnerPipe(t));
        Check($"Y10: the user creates \\\\.\\pipe\\HIDMaestroOwner{t}", fake != null);
        string? entry = Y.CensusEntry(later.Send("census"), tok);
        Check("Y10: the census does not count the pipe as a running program's",
              entry?.Split('|')[2] == "Dead", entry ?? "no entry");
        later.Send("sweep");
        WaitFor(() => tree.All(Native.IsGone), 5000);
        Check("Y10: the sweep removes the killed program's devices", tree.Count > 0 && tree.All(Native.IsGone),
              string.Join(", ", tree.Where(d => !Native.IsGone(d))));
    }

    /// <summary>Y7. The acknowledgement section of a killed v1.12.0
    /// program's device on the installed driver.</summary>
    static void SectionYAckKept(string installed)
    {
        string inst, tok;
        int t;
        using (var old = PeerProcess.Start("Y-v1.12.0-killed", "v1.12.0"))
        {
            string co = old.Send("create pad F1F6 coexist-y-ack");
            if (!Ok(co)) { Check("Y7: the v1.12.0 program creates", false, co); return; }
            inst = PeerProcess.Field(co, "instance")!;
            t = int.Parse(PeerProcess.Field(co, "transport")!);
            tok = inst.Substring(inst.LastIndexOf('\\') + 1);
            bool moved = Y.InstallBestStoreDriver(inst)
                         && WaitFor(() => Native.DriverVersionOf(inst) == installed && Native.IsStarted(inst), 20_000)
                         && WaitFor(() => Y.WatchValues(inst) is { Connected: 1 } v && v.Pid == old.Pid, 10_000);
            Check("Y7: the v1.12.0 program's controller runs on the installed driver, which watches its pipe", moved,
                  $"{Native.DriverVersionOf(inst)}, installed {installed}, {Y.ShowWatch(inst)}");
            if (!moved) return;
            old.Kill();
        }
        var tree = Native.Subtree(inst);
        // Eight idle 500 ms waits reset the driver's handles. Seven seconds
        // covers it and the reopen attempts after it.
        Thread.Sleep(7000);
        string section = $@"Global\HIDMaestroInputAck{t}";
        Check($"Y7: past the driver's idle reset, {section} still exists", Native.SectionExists(section));
        using var nu = PeerProcess.Start("Y-sweeper");
        string? entry = Y.CensusEntry(nu.Send("census"), tok);
        Check("Y7: the census reads the device as an exited v1.12.0 program's",
              entry?.Split('|')[2] == "DeadLegacy", entry ?? "no entry");
        string sw = nu.Send("sweep");
        WaitFor(() => tree.All(Native.IsGone), 5000);
        Check("Y7: a sweep removes it", Ok(sw) && tree.Count > 0 && tree.All(Native.IsGone),
              string.Join(", ", tree.Where(d => !Native.IsGone(d))));
    }

    /// <summary>Y8. The release-on-exit bit across a reset of the driver's
    /// handles.</summary>
    static void SectionYReleaseBit(PeerProcess live, int t, HidCapture cap)
    {
        string section = $@"Global\HIDMaestroInputAck{t}";
        int? before = Y.AckCapabilities(section);
        string sdkBefore = live.Send($"releases {t}");
        Check("Y8: before any reset the driver announces release on exit, and the SDK reads it",
              before is int b && (b & 2) != 0 && PeerProcess.Field(sdkBefore, "on") == "1",
              $"capabilities {before?.ToString("X") ?? "none"}, {sdkBefore}");
        IntPtr stop = Y.OpenEventW(Y.EVENT_MODIFY_STATE, false, $@"Global\HIDMaestroStopEvent{t}");
        bool signaled = stop != IntPtr.Zero && Y.SetEvent(stop);
        if (stop != IntPtr.Zero) Y.CloseHandle(stop);
        Check("Y8: a signal on the StopEvent, as a sweep sends it", signaled);
        Thread.Sleep(1500);
        int? after = Y.AckCapabilities(section);
        string sdkAfter = live.Send($"releases {t}");
        Check("Y8: after the reset the bit is still set", after is int a && (a & 2) != 0,
              $"capabilities {after?.ToString("X") ?? "none"}");
        Check("Y8: and the SDK still reads release-on-exit support", PeerProcess.Field(sdkAfter, "on") == "1", sdkAfter);
        Check("Y8: the controller's input keeps flowing", !cap.Broken && Flows(cap));
    }

    /// <summary>Y6. A previous device's values on the instance.</summary>
    static void SectionYStale(string installed)
    {
        using var v11 = PeerProcess.Start("Y-v1.11.0", "v1.11.0");
        string cv = v11.Send("create pad F1F4 coexist-y-v11");
        if (!Ok(cv)) { Check("Y6: the v1.11.0 program creates", false, cv); return; }
        // Before v1.12.0 a controller does not name its devnode: the HID
        // collection's parent is it.
        string? inst = PeerProcess.Field(cv, "instance");
        if (inst is null or "none")
            inst = WaitPaths(0xBEEF, 0xF1F4, 1, 10_000).Select(Native.InstanceOfInterface).Select(c => c == null ? null : Native.ParentOf(c)).FirstOrDefault();
        if (inst == null) { Check("Y6: the v1.11.0 program's device is present", false); return; }
        // What a device that watched an elevated program's pipe leaves.
        using (var k = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Enum\{inst}\Device Parameters\{Y.WatchSubkey}"))
        {
            k.SetValue("OwnerConnected", 1, RegistryValueKind.DWord);
            k.SetValue("OwnerProcessId", Environment.ProcessId, RegistryValueKind.DWord);
            k.SetValue("OwnerPipeOwner", "S-1-5-32-544", RegistryValueKind.String);
        }
        bool moved = Y.InstallBestStoreDriver(inst)
                     && WaitFor(() => Native.DriverVersionOf(inst) == installed && Native.IsStarted(inst), 20_000);
        Check("Y6: the v1.11.0 program's controller moves to the installed driver", moved,
              $"{Native.DriverVersionOf(inst)}, installed {installed}");
        if (!moved) return;
        Thread.Sleep(1500);   // past the worker's first 200 ms bootstrap waits
        Check("Y6: the values the earlier device left are gone, and none are written", Y.WatchValues(inst) == null, Y.ShowWatch(inst));
    }

    /// <summary>Y1 to Y5. Returns the installed driver's version, or null
    /// when the section stopped early.</summary>
    static string? SectionYBody(IntPtr user)
    {
        // A v1.12.0 program takes the lowest index of its own, so it starts
        // first.
        using var old = PeerProcess.Start("Y-v1.12.0", "v1.12.0");
        string co = old.Send("create pad F1F0 coexist-y-old");
        if (!Ok(co)) { Check("Y: the v1.12.0 program creates", false, co); return null; }
        old.Send("stream 3 0.25 125 0");
        string instOld = PeerProcess.Field(co, "instance")!;
        int tOld = int.Parse(PeerProcess.Field(co, "transport")!);

        using var live = PeerProcess.Start("Y-live");
        string cl = live.Send("create pad F1F1 coexist-y-live");
        if (!Ok(cl)) { Check("Y: the program on this SDK creates", false, cl); return null; }
        live.Send("stream 4 0.75 125 0");
        string instLive = PeerProcess.Field(cl, "instance")!;
        int tLive = int.Parse(PeerProcess.Field(cl, "transport")!);
        string? installed = Native.DriverVersionOf(instLive);
        string tokOld = instOld.Substring(instOld.LastIndexOf('\\') + 1);

        // A v1.12.0 program installs its own package on each device it makes
        // (UpdateDriverForPlugAndPlayDevices with its INF), and that driver
        // records nothing.
        Check("Y5: the v1.12.0 program's controller runs on its own driver",
              Native.DriverVersionOf(instOld) is { } ownDriver && ownDriver != installed && Y.WatchValues(instOld) == null,
              $"{Native.DriverVersionOf(instOld)} beside {installed}, {Y.ShowWatch(instOld)}");
        // The v1.12.0 driver made its OutputEvent with a NULL DACL, owned by
        // LocalService. This SDK's reader takes that event (the arm in
        // SharedMemoryIO.TryOpenOutputEvent), as a v1.13.0 program running
        // on an earlier driver must, or it would poll at 8 ms.
        string legacyOpen = live.Send($"sdkevents {tOld}");
        Check("Y5: this SDK's reader opens the v1.12.0 driver's NULL-DACL OutputEvent",
              PeerProcess.Field(legacyOpen, "output") == "1", legacyOpen);
        string? before = Y.CensusEntry(live.Send("census"), tokOld);
        Check("Y5: there the census keeps the rule before this release: the held pipe is a running v1.12.0 program's",
              before is { } b && b.Split('|')[2] == "LiveLegacy" && b.EndsWith("|watch=none", StringComparison.Ordinal), before ?? "no entry");

        // The best driver in the store on that device alone, as this SDK's
        // create installs one (DeviceNodeCreator.InstallBestStoreDriver).
        bool moved = Y.InstallBestStoreDriver(instOld)
                     && WaitFor(() => Native.DriverVersionOf(instOld) == installed && Native.IsStarted(instOld), 20_000);
        Check("Y: the v1.12.0 program's controller moves to the installed driver", moved,
              $"{Native.DriverVersionOf(instOld)}, installed {installed}");
        if (!moved) return null;

        var pathOld = WaitPaths(0xBEEF, 0xF1F0, 1, 10_000);
        var pathLive = WaitPaths(0xBEEF, 0xF1F1, 1, 10_000);
        using var capOld = pathOld.Count == 1 ? HidCapture.Open(pathOld[0]) : null;
        using var capLive = pathLive.Count == 1 ? HidCapture.Open(pathLive[0]) : null;
        if (capOld == null || capLive == null) { Check("Y: both programs' devices open", false); return null; }

        // Y8 before Y1, whose own StopEvent signals reset the handles too.
        SectionYReleaseBit(live, tLive, capLive);

        // Y1.
        {
            string stop = $@"Global\HIDMaestroStopEvent{tLive}", output = $@"Global\HIDMaestroOutputEvent{tLive}";
            var asUser = Y.AsUser(user, () => new[]
            {
                Y.OpenError(Y.EVENT_MODIFY_STATE, stop),
                Y.OpenError(Y.SYNCHRONIZE, output),
                Y.OpenError(Y.EVENT_MODIFY_STATE, output),
            });
            Check("Y1: the user cannot open the controller's StopEvent to signal it", asUser[0] == Y.ERROR_ACCESS_DENIED, Y.Win32(asUser[0]));
            Check("Y1: the user cannot open its OutputEvent to wait on it", asUser[1] == Y.ERROR_ACCESS_DENIED, Y.Win32(asUser[1]));
            Check("Y1: the user cannot open its OutputEvent to set or reset it", asUser[2] == Y.ERROR_ACCESS_DENIED, Y.Win32(asUser[2]));
            foreach (string name in new[] { stop, output })
            {
                string? sddl = Y.EventSddl(name);
                Check($"Y1: {name}'s DACL admits SYSTEM, Administrators and LocalService only", Y.HostOnlyDacl(sddl), sddl ?? "cannot read it");
            }
            foreach (var (who, peer) in new[] { ("its program", live), ("a v1.12.0 program", old) })
            {
                string own = peer.Send($"sdkevents {tLive}");
                Check($"Y1: {who} opens and waits on the OutputEvent and signals the StopEvent as its SDK does",
                      PeerProcess.Field(own, "output") == "1" && PeerProcess.Field(own, "wait") is "0" or "258"
                      && PeerProcess.Field(own, "stop") == "1", own);
            }

            string oldStop = $@"Global\HIDMaestroStopEvent{tOld}";
            int oldErr = Y.AsUser(user, () => Y.OpenError(Y.EVENT_MODIFY_STATE, oldStop));
            Check("Y1: the user cannot open the moved v1.12.0 controller's StopEvent to signal it", oldErr == Y.ERROR_ACCESS_DENIED, Y.Win32(oldErr));
            string? oldSddl = Y.EventSddl(oldStop);
            Check($"Y1: {oldStop}'s DACL admits SYSTEM, Administrators and LocalService only", Y.HostOnlyDacl(oldSddl), oldSddl ?? "cannot read it");
            string oldOwn = old.Send($"sdkevents {tOld}");
            Check("Y1: the v1.12.0 program still opens and waits on its OutputEvent and signals its StopEvent",
                  PeerProcess.Field(oldOwn, "output") == "1" && PeerProcess.Field(oldOwn, "wait") is "0" or "258"
                  && PeerProcess.Field(oldOwn, "stop") == "1", oldOwn);
            Check("Y1: the v1.12.0 program's input keeps flowing", !capOld.Broken && Flows(capOld), capOld.Broken ? $"read error {capOld.ReadError}" : "");
            Check("Y1: this SDK's program's input keeps flowing", !capLive.Broken && Flows(capLive), capLive.Broken ? $"read error {capLive.ReadError}" : "");
        }

        SectionYSquat(user);
        SectionYFakePipe(user, live, tOld, tLive);

        // Y5, on the installed driver.
        string census = live.Send("census");
        string? entry = Y.CensusEntry(census, tokOld);
        Check("Y5: the census holds the v1.12.0 program's device as a running v1.12.0 program's",
              entry?.Split('|')[2] == "LiveLegacy", entry ?? census);
        Check("Y5: the census sees its owner pipe's server connected, alive and elevated",
              entry != null && Regex.IsMatch(entry, $@"\|watch=connected,pid={old.Pid},owner=[^,]+,alive=1,elevated=1,trusted=1$"),
              entry ?? census);
        Check("Y5: every sweep left the v1.12.0 program's controller running", Native.IsStarted(instOld) && !capOld.Broken && Flows(capOld));
        Check("Y5: and the running program's own", Native.IsStarted(instLive) && !capLive.Broken && Flows(capLive));
        return installed;
    }

    /// <summary>Y2. Both event names, made by the user before a create.</summary>
    static void SectionYSquat(IntPtr user)
    {
        // The transport allocator skips an index whose events a program
        // that is not elevated made (SharedMemoryIO.ForeignObjectAt), so the
        // user creates both names once the create holds its index, before
        // the device exists. The StopEvent is made signaled.
        IntPtr stopSquat = IntPtr.Zero, outputSquat = IntPtr.Zero;
        try
        {
            using var p = PeerProcess.Start("Y-squat");
            Check("Y2: the next create is set to wait once it holds its index", Ok(p.Send("pauseindex")));
            p.Post("create xbox - coexist-y-squat");
            string paused = p.Receive("create xbox - coexist-y-squat (waiting)", 60_000);
            if (!paused.StartsWith("OK paused index=", StringComparison.Ordinal))
            {
                Check("Y2: the create waits once it holds its index", false, paused);
                return;
            }
            int t = int.Parse(PeerProcess.Field(paused, "index")!);
            bool made = Y.AsUser(user, () =>
            {
                stopSquat = Y.CreateOpenEvent($@"Global\HIDMaestroStopEvent{t}", manualReset: true, signaled: true, out bool sNew);
                outputSquat = Y.CreateOpenEvent($@"Global\HIDMaestroOutputEvent{t}", manualReset: false, signaled: false, out bool oNew);
                return sNew && oNew;
            });
            Check($"Y2: the user creates both names for transport index {t}", made);
            string cx = p.Send("go");
            if (!Ok(cx)) { Check("Y2: the Xbox 360 controller creates beside the user's names", false, cx); return; }
            if (!made) return;
            Check("Y2: the program's output reader is subscribed", Ok(p.Send("outputs on")));
            p.Send("stream 0 0.95 125 0");
            int slot = -1;
            bool reads = WaitFor(() =>
            {
                for (uint s = 0; s < 4; s++)
                    if (Native.XInputGetState(s, out var st) == 0 && st.Gamepad.sThumbLX > 25000 && (st.Gamepad.wButtons & 0x1000) != 0)
                    { slot = (int)s; return true; }
                return false;
            }, 15_000);
            Check("Y2: XInput reads the controller's input", reads, reads ? $"slot {slot}" : "no XInput slot carries it");
            Check("Y2: the driver did not take the user's StopEvent, which is still signaled",
                  Y.WaitForSingleObject(stopSquat, 0) == Y.WAIT_OBJECT_0);
            if (!reads) return;

            // The program's own output reader (HMController.OutputPollLoop)
            // must not wait on the user's event, which nothing signals: it
            // stays on its 8 ms poll. A reader that took the event waited
            // out a 500 ms timeout for each of its first outputs before it
            // dropped to polling, so these are the first outputs it sees.
            Action<int> rumble = i =>
            {
                var v = new Y.XINPUT_VIBRATION { wLeftMotorSpeed = (ushort)(0x1000 + i * 0x100), wRightMotorSpeed = (ushort)(0x800 + i * 0x80) };
                Y.XInputSetState((uint)slot, ref v);
            };
            OutputTiming squatted = TimeOutputs(p, rumble, 30, 60);
            Console.WriteLine($"    Y2 output latency beside the user's event: {squatted.Shown}");
            Check("Y2: the program's reader does not wait on the user's OutputEvent: every rumble arrives within 50 ms",
                  squatted.AllArrived && squatted.Max < 50, squatted.Shown);

            // Rumble through XInput: the XUSB companion publishes each packet
            // and signals the output doorbell it holds.
            int wakes = 0;
            var waiter = new Thread(() =>
            {
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2500)
                    if (Y.WaitForSingleObject(outputSquat, 50) == Y.WAIT_OBJECT_0) wakes++;
            });
            waiter.Start();
            for (int i = 0; i < 40; i++)
            {
                var vib = new Y.XINPUT_VIBRATION { wLeftMotorSpeed = (ushort)(i % 2 == 0 ? 0x4000 : 0), wRightMotorSpeed = (ushort)(i % 2 == 0 ? 0 : 0x4000) };
                Y.XInputSetState((uint)slot, ref vib);
                Thread.Sleep(40);
            }
            var stopVib = new Y.XINPUT_VIBRATION();
            Y.XInputSetState((uint)slot, ref stopVib);
            waiter.Join();
            Check("Y2: rumble never signals the user's OutputEvent", wakes == 0, $"{wakes} wakes");

            // The user lets go of both names while the program keeps
            // streaming input. The driver retries its doorbell on a steady
            // stream too, and the reader, polling, opens it within 64 polls.
            Y.CloseHandle(stopSquat);
            stopSquat = IntPtr.Zero;
            Y.CloseHandle(outputSquat);
            outputSquat = IntPtr.Zero;
            string output = $@"Global\HIDMaestroOutputEvent{t}";
            bool own = WaitFor(() => Y.HostOnlyDacl(Y.EventSddl(output)), 10_000);
            Check($"Y2: while input streams, the driver makes its own {output}", own, Y.EventSddl(output) ?? "none");
            // The XUSB companion, which publishes the rumble, retries its own
            // handle within 256 input frames (about 2 s at 125 Hz), and the
            // reader opens the event within 64 polls.
            Thread.Sleep(3000);
            OutputTiming after = TimeOutputs(p, rumble, 30, 60);
            Console.WriteLine($"    Y2 output latency on the driver's event: {after.Shown}");
            Check("Y2: and the program's reader waits on it: p95 under 2 ms",
                  after.AllArrived && after.P95 < 2, after.Shown);
            var stopRumble = new Y.XINPUT_VIBRATION();
            Y.XInputSetState((uint)slot, ref stopRumble);
            p.Send("stopstream");
            p.Send("dispose");
        }
        finally
        {
            if (stopSquat != IntPtr.Zero) Y.CloseHandle(stopSquat);
            if (outputSquat != IntPtr.Zero) Y.CloseHandle(outputSquat);
        }
    }

    /// <summary>Y3 and Y4. A killed program's index, and the v1.12.0 owner
    /// pipe of it made by the user.</summary>
    static void SectionYFakePipe(IntPtr user, PeerProcess live, int tOld, int tLive)
    {
        // Y3.
        string inst, tok;
        int t;
        using (var a = PeerProcess.Start("Y-killed"))
        {
            string ca = a.Send("create pad F1F2 coexist-y-killed");
            if (!Ok(ca)) { Check("Y3: a program creates", false, ca); return; }
            inst = PeerProcess.Field(ca, "instance")!;
            t = int.Parse(PeerProcess.Field(ca, "transport")!);
            tok = inst.Substring(inst.LastIndexOf('\\') + 1);
            bool rec = WaitFor(() => Y.WatchValues(inst) is { Connected: 1 } v && v.Pid == a.Pid, 5000);
            Check("Y3: the driver records the program as its owner pipe's server", rec, Y.ShowWatch(inst));
            a.Kill();
        }
        var tree = Native.Subtree(inst);
        Check("Y3: once the program is killed the driver records no owner",
              WaitFor(() => Y.WatchValues(inst) is { Connected: 0 }, 5000), Y.ShowWatch(inst));
        using (var fake = Y.AsUser(user, () => Y.FakeOwnerPipe(t)))
        {
            Check($"Y3: the user creates \\\\.\\pipe\\HIDMaestroOwner{t}", fake != null);
            string? entry = Y.CensusEntry(live.Send("census"), tok);
            Check("Y3: the census does not count the user's pipe as a running program's",
                  entry?.Split('|')[2] == "Dead", entry ?? "no entry");
            string s = live.Send("sweep");
            WaitFor(() => tree.All(Native.IsGone), 5000);
            Check("Y3: the running program's sweep removes the killed program's devices", Ok(s) && tree.All(Native.IsGone),
                  string.Join(", ", tree.Where(d => !Native.IsGone(d))));
        }

        // Y4.
        using (var c = PeerProcess.Start("Y-unreported"))
        {
            string cc = c.Send("create pad F1F3 coexist-y-unreported");
            if (!Ok(cc)) { Check("Y4: a program creates", false, cc); return; }
            inst = PeerProcess.Field(cc, "instance")!;
            t = int.Parse(PeerProcess.Field(cc, "transport")!);
            tok = inst.Substring(inst.LastIndexOf('\\') + 1);
            WaitFor(() => Y.WatchValues(inst) is { Connected: 1 }, 5000);
            c.Kill();
        }
        tree = Native.Subtree(inst);
        WaitFor(() => Y.WatchValues(inst) is { Connected: 0 }, 5000);
        Check("Y4: the driver's values are deleted", Y.DeleteWatchValues(inst) && Y.WatchValues(inst) == null, Y.ShowWatch(inst));
        Check("Y4: and its ended marker is cleared", Y.ClearAckEnded($@"Global\HIDMaestroInputAck{t}"));
        using (var fake = Y.AsUser(user, () => Y.FakeOwnerPipe(t)))
        {
            Check($"Y4: the user creates \\\\.\\pipe\\HIDMaestroOwner{t}", fake != null);
            string census = live.Send("census");
            Check("Y4: without the driver's values the census counts the pipe as a running v1.12.0 program's",
                  Y.CensusEntry(census, tok) is { } e && e.Split('|')[2] == "LiveLegacy" && e.EndsWith("|watch=none", StringComparison.Ordinal),
                  Y.CensusEntry(census, tok) ?? census);
            live.Send("sweep");
            Thread.Sleep(2000);
            Check("Y4: and the sweep keeps the killed program's devices", tree.Count > 0 && tree.All(Native.IsPresent),
                  string.Join(", ", tree.Where(d => !Native.IsPresent(d))));
        }
        // The sweep that kept them dropped the killed program's record, as
        // it did before this release, so the cleanup at the end of the
        // section removes them.
    }

    /// <summary>The latency of each output, from the send to the program's
    /// OutputReceived, on the one machine-wide Stopwatch clock.</summary>
    record struct OutputTiming(List<double> Ms, bool AllArrived, double P50, double P95, double Max, string Shown);

    /// <summary>Send <paramref name="n"/> outputs <paramref name="spacingMs"/>
    /// apart through <paramref name="send"/> and time each one to the first
    /// OutputReceived the peer noted after it ("outputs on" must have run).
    /// An output with no OutputReceived before the next send is a miss.</summary>
    static OutputTiming TimeOutputs(PeerProcess peer, Action<int> send, int n, int spacingMs)
    {
        var sent = new List<long>();
        for (int i = 0; i < n; i++)
        {
            sent.Add(Stopwatch.GetTimestamp());
            send(i);
            Thread.Sleep(spacingMs);
        }
        Thread.Sleep(600);
        string reply = peer.Send("outputs");
        var got = (PeerProcess.Field(reply, "t") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).OrderBy(x => x).ToList();
        var ms = new List<double>();
        for (int i = 0; i < sent.Count; i++)
        {
            long end = i + 1 < sent.Count ? sent[i + 1] : long.MaxValue;
            long hit = got.FirstOrDefault(x => x >= sent[i] && x < end);
            ms.Add(hit == 0 ? double.NaN : (hit - sent[i]) * 1000.0 / Stopwatch.Frequency);
        }
        var arrived = ms.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToList();
        double P(double q) => arrived.Count == 0 ? double.NaN : arrived[Math.Min(arrived.Count - 1, (int)(arrived.Count * q))];
        double max = arrived.Count == 0 ? double.NaN : arrived[^1];
        string shown = FormattableString.Invariant($"first {ms[0]:F1} ms, p50 {P(0.5):F1} ms, p95 {P(0.95):F1} ms, max {max:F1} ms, {arrived.Count}/{ms.Count} arrived");
        return new OutputTiming(ms, arrived.Count == ms.Count, P(0.5), P(0.95), max, shown);
    }

    /// <summary>Section Y's helpers and the Win32 calls they make.</summary>
    static class Y
    {
        /// <summary>The capabilities word of an acknowledgement section
        /// (HIDMAESTRO_INPUT_ACK, offset 4), or null when it does not exist.</summary>
        internal static int? AckCapabilities(string name)
        {
            IntPtr h = OpenFileMappingW(0x0004 /*FILE_MAP_READ*/, false, name);
            if (h == IntPtr.Zero) return null;
            try
            {
                IntPtr v = MapViewOfFile(h, 0x0004, 0, 0, (UIntPtr)16);
                if (v == IntPtr.Zero) return null;
                try { return Marshal.ReadInt32(v, 4); }
                finally { UnmapViewOfFile(v); }
            }
            finally { CloseHandle(h); }
        }

        /// <summary>DiInstallDevice with no driver named on one devnode: the
        /// best match the driver store holds, on that device alone.</summary>
        internal static bool InstallBestStoreDriver(string instanceId)
        {
            IntPtr dis = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
            if (dis == new IntPtr(-1)) return false;
            byte[] buf = new byte[IntPtr.Size == 8 ? 32 : 28];
            BitConverter.GetBytes(buf.Length).CopyTo(buf, 0);
            var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                if (!SetupDiOpenDeviceInfoW(dis, instanceId, IntPtr.Zero, 0, pin.AddrOfPinnedObject())) return false;
                return DiInstallDevice(IntPtr.Zero, dis, pin.AddrOfPinnedObject(), IntPtr.Zero, 0x2 /*DIIDFLAG_NOFINISHINSTALLUI*/, out _);
            }
            finally
            {
                pin.Free();
                SetupDiDestroyDeviceInfoList(dis);
            }
        }

        /// <summary>One device group of a "census" reply: token|index|owner|watch.</summary>
        internal static string? CensusEntry(string reply, string token) =>
            reply.Split(' ').FirstOrDefault(p => p.StartsWith(token + "|", StringComparison.OrdinalIgnoreCase));

        // ── The driver's owner watch values ────────────────────────────────

        internal const string WatchSubkey = "HIDMaestro";

        internal record struct Watch(int? Connected, int? Pid, string? Owner);

        /// <summary>The values the driver writes in the device's own key, or
        /// null when it wrote none.</summary>
        internal static Watch? WatchValues(string instanceId)
        {
            using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\{WatchSubkey}");
            if (k == null) return null;
            var w = new Watch(k.GetValue("OwnerConnected") as int?, k.GetValue("OwnerProcessId") as int?, k.GetValue("OwnerPipeOwner") as string);
            return w.Connected == null && w.Pid == null && w.Owner == null ? null : w;
        }

        internal static string ShowWatch(string instanceId) => WatchValues(instanceId) is { } w
            ? $"connected={w.Connected?.ToString() ?? "-"} pid={w.Pid?.ToString() ?? "-"} owner={w.Owner ?? "-"}"
            : "no values";

        /// <summary>Write the three values as the driver writes them for a
        /// connected watch, then set the key's last-write time
        /// (ZwSetInformationKey, KeyWriteTimeInformation).</summary>
        internal static bool PlantWatch(string instanceId, int pid, string owner, DateTime written)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\{WatchSubkey}", writable: true);
                if (k == null) return false;
                k.SetValue("OwnerProcessId", pid, RegistryValueKind.DWord);
                k.SetValue("OwnerPipeOwner", owner, RegistryValueKind.String);
                k.SetValue("OwnerConnected", 1, RegistryValueKind.DWord);
                long ft = written.ToFileTimeUtc();
                return NtSetInformationKey(k.Handle, 0 /*KeyWriteTimeInformation*/, ref ft, 8) == 0;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Console.WriteLine($"    cannot plant the values: {e.Message}");
                return false;
            }
        }

        /// <summary>The same with a raw last-write FILETIME, which may lie
        /// outside the range DateTime accepts.</summary>
        internal static bool PlantWatchRaw(string instanceId, int pid, string owner, long fileTime)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\{WatchSubkey}", writable: true);
                if (k == null) return false;
                k.SetValue("OwnerProcessId", pid, RegistryValueKind.DWord);
                k.SetValue("OwnerPipeOwner", owner, RegistryValueKind.String);
                k.SetValue("OwnerConnected", 1, RegistryValueKind.DWord);
                return NtSetInformationKey(k.Handle, 0 /*KeyWriteTimeInformation*/, ref fileTime, 8) == 0;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Console.WriteLine($"    cannot plant the values: {e.Message}");
                return false;
            }
        }

        internal static DateTime ProcessCreated(int pid)
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime();
        }

        [DllImport("ntdll.dll")]
        static extern int NtSetInformationKey(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, int infoClass, ref long info, int length);

        internal static bool DeleteWatchValues(string instanceId)
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters\{WatchSubkey}", writable: true);
                if (k == null) return false;
                foreach (string v in new[] { "OwnerConnected", "OwnerProcessId", "OwnerPipeOwner" })
                    k.DeleteValue(v, throwOnMissingValue: false);
                return true;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Console.WriteLine($"    cannot delete the values: {e.Message}");
                return false;
            }
        }

        // ── The user's token, on one thread ────────────────────────────────

        /// <summary>A duplicate of the user's medium-integrity token, taken from
        /// explorer.exe in this session, for impersonation. Zero when there is
        /// none, with the reason.</summary>
        internal static IntPtr UserToken(out string why)
        {
            why = "no explorer.exe at medium integrity in this session";
            int session = Process.GetCurrentProcess().SessionId;
            foreach (var p in Process.GetProcessesByName("explorer"))
            {
                using (p)
                {
                    if (p.SessionId != session) continue;
                    IntPtr hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                    if (hp == IntPtr.Zero) continue;
                    try
                    {
                        if (!OpenProcessToken(hp, TOKEN_DUPLICATE | TOKEN_QUERY, out IntPtr pt)) continue;
                        try
                        {
                            if (!DuplicateTokenEx(pt, TOKEN_IMPERSONATE | TOKEN_QUERY, IntPtr.Zero, 2 /*SecurityImpersonation*/, 2 /*TokenImpersonation*/, out IntPtr dup))
                                continue;
                            if (IntegrityRid(dup) == 0x2000 && !Elevated(dup))
                            {
                                why = $"explorer.exe pid {p.Id}";
                                return dup;
                            }
                            CloseHandle(dup);
                        }
                        finally { CloseHandle(pt); }
                    }
                    finally { CloseHandle(hp); }
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>Run <paramref name="body"/> on a new thread that impersonates
        /// <paramref name="token"/>, and revert before the thread ends. Handles
        /// it opens stay open: they belong to the process.</summary>
        internal static T AsUser<T>(IntPtr token, Func<T> body)
        {
            T result = default!;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                if (!SetThreadToken(IntPtr.Zero, token)) { error = new Win32Exception(Marshal.GetLastWin32Error(), "SetThreadToken"); return; }
                try { result = body(); }
                catch (Exception e) { error = e; }
                finally { RevertToSelf(); }
            });
            thread.Start();
            thread.Join();
            if (error != null) throw new InvalidOperationException("the impersonating thread failed", error);
            return result;
        }

        /// <summary>The error an OpenEvent with <paramref name="access"/> ends
        /// with, 0 when it opens.</summary>
        internal static int OpenError(uint access, string name)
        {
            IntPtr h = OpenEventW(access, false, name);
            int err = h == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            if (h != IntPtr.Zero) CloseHandle(h);
            return err;
        }

        internal static string Win32(int err) => err == 0 ? "it opened" : $"Win32 error {err}";

        /// <summary>An event with a NULL DACL, as the driver made its own before
        /// this release. <paramref name="created"/> is false when the name
        /// existed.</summary>
        internal static IntPtr CreateOpenEvent(string name, bool manualReset, bool signaled, out bool created)
        {
            var sd = Marshal.AllocHGlobal(64);
            try
            {
                InitializeSecurityDescriptor(sd, 1);
                SetSecurityDescriptorDacl(sd, true, IntPtr.Zero, false);
                var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
                IntPtr h = CreateEventW(ref sa, manualReset, signaled, name);
                created = h != IntPtr.Zero && Marshal.GetLastWin32Error() != ERROR_ALREADY_EXISTS;
                return h;
            }
            finally { Marshal.FreeHGlobal(sd); }
        }

        /// <summary>\\.\pipe\HIDMaestroOwner&lt;N&gt; with v1.12.0's mode and
        /// instance count, and the default security a program gets. Null when
        /// the name is taken.</summary>
        internal static Microsoft.Win32.SafeHandles.SafeFileHandle? FakeOwnerPipe(int index)
        {
            var h = CreateNamedPipeW($@"\\.\pipe\HIDMaestroOwner{index}", 0x00000002 /*OUTBOUND*/ | 0x00080000 /*FIRST_PIPE_INSTANCE*/,
                                     0x00000008 /*REJECT_REMOTE_CLIENTS*/, 2, 0, 0, 0, IntPtr.Zero);
            if (!h.IsInvalid) return h;
            h.Dispose();
            return null;
        }

        /// <summary>The owner and DACL of a named event in SDDL, read from an
        /// elevated handle, or null.</summary>
        internal static string? EventSddl(string name)
        {
            IntPtr h = OpenEventW(READ_CONTROL, false, name);
            if (h == IntPtr.Zero) return null;
            try
            {
                if (GetSecurityInfo(h, 6 /*SE_KERNEL_OBJECT*/, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
                                    out _, out _, out _, out _, out IntPtr sd) != 0)
                    return null;
                try
                {
                    if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(sd, 1, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION, out IntPtr s, out _))
                        return null;
                    try { return Marshal.PtrToStringUni(s); }
                    finally { LocalFree(s); }
                }
                finally { LocalFree(sd); }
            }
            finally { CloseHandle(h); }
        }

        /// <summary>A present DACL whose every ACE allows SYSTEM, Administrators
        /// or LocalService, each of the three once.</summary>
        internal static bool HostOnlyDacl(string? sddl)
        {
            if (sddl == null) return false;
            int d = sddl.IndexOf("D:", StringComparison.Ordinal);
            if (d < 0 || sddl.Contains("NO_ACCESS_CONTROL", StringComparison.Ordinal)) return false;
            var sids = Regex.Matches(sddl.Substring(d), @"\(([^;]*);[^;]*;[^;]*;[^;]*;[^;]*;([^)]*)\)")
                .Select(m => (Type: m.Groups[1].Value, Sid: m.Groups[2].Value)).ToList();
            return sids.Count == 3 && sids.All(a => a.Type == "A")
                   && sids.Select(a => a.Sid).OrderBy(s => s).SequenceEqual(new[] { "BA", "LS", "SY" });
        }

        static int IntegrityRid(IntPtr token)
        {
            GetTokenInformation(token, 25 /*TokenIntegrityLevel*/, IntPtr.Zero, 0, out int len);
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(token, 25, buf, len, out _)) return -1;
                IntPtr sid = Marshal.ReadIntPtr(buf);
                byte count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        static bool Elevated(IntPtr token)
        {
            IntPtr buf = Marshal.AllocHGlobal(4);
            try { return GetTokenInformation(token, 20 /*TokenElevation*/, buf, 4, out _) && Marshal.ReadInt32(buf) != 0; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        internal const uint EVENT_MODIFY_STATE = 0x0002, SYNCHRONIZE = 0x00100000, READ_CONTROL = 0x00020000;
        internal const uint EVENT_ALL_ACCESS = 0x1F0003;

        /// <summary>Clear the driver's ended marker, bit 31 of an
        /// acknowledgement section's capabilities word, as a driver that
        /// records nothing leaves it. False when the section does not
        /// exist.</summary>
        internal static bool ClearAckEnded(string name)
        {
            IntPtr h = OpenFileMappingW(0x0002 /*FILE_MAP_WRITE*/, false, name);
            if (h == IntPtr.Zero) return false;
            try
            {
                IntPtr v = MapViewOfFile(h, 0x0002, 0, 0, (UIntPtr)16);
                if (v == IntPtr.Zero) return false;
                try
                {
                    Marshal.WriteInt32(v, 4, Marshal.ReadInt32(v, 4) & 0x7FFFFFFF);
                    return true;
                }
                finally { UnmapViewOfFile(v); }
            }
            finally { CloseHandle(h); }
        }

        /// <summary>Disable the device and enable it again, as an
        /// administrator's disable and enable does. False when the device
        /// cannot be found or the disable is refused.</summary>
        internal static bool RestartDevice(string instanceId)
        {
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != 0) return false;
            if (CM_Disable_DevNode(devInst, 0) != 0) return false;
            WaitFor(() => !Native.IsStarted(instanceId), 10_000);
            if (CM_Locate_DevNodeW(out devInst, instanceId, 1 /*CM_LOCATE_DEVNODE_PHANTOM*/) != 0) return false;
            return CM_Enable_DevNode(devInst, 0) == 0;
        }
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Disable_DevNode(uint devInst, uint flags);
        [DllImport("cfgmgr32.dll")] static extern uint CM_Enable_DevNode(uint devInst, uint flags);

        /// <summary>A held FILE_MAP_READ handle to a named section, or zero.</summary>
        internal static IntPtr HoldSection(string name) => OpenFileMappingW(0x0004 /*FILE_MAP_READ*/, false, name);
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint TOKEN_DUPLICATE = 0x0002, TOKEN_IMPERSONATE = 0x0004, TOKEN_QUERY = 0x0008;
        const uint OWNER_SECURITY_INFORMATION = 0x1, DACL_SECURITY_INFORMATION = 0x4;
        internal const int ERROR_ACCESS_DENIED = 5, ERROR_ALREADY_EXISTS = 183;
        internal const uint WAIT_OBJECT_0 = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed, wRightMotorSpeed; }

        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwnd);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwnd, uint flags, IntPtr data);
        [DllImport("newdev.dll", SetLastError = true)]
        static extern bool DiInstallDevice(IntPtr hwnd, IntPtr set, IntPtr data, IntPtr driver, uint flags, out bool needReboot);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteFile(IntPtr h, byte[] buf, int n, out int written, IntPtr overlapped);
        [DllImport("xinput1_4.dll")] internal static extern uint XInputSetState(uint idx, ref XINPUT_VIBRATION v);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr OpenEventW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetEvent(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offHigh, uint offLow, UIntPtr bytes);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool UnmapViewOfFile(IntPtr view);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES sa, bool manualReset, bool initialState, string name);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode,
            uint maxInstances, uint outSize, uint inSize, uint timeout, IntPtr sa);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr sa, int level, int type, out IntPtr dup);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetThreadToken(IntPtr thread, IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool RevertToSelf();
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int returned);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
        [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint n);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool InitializeSecurityDescriptor(IntPtr sd, uint revision);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetSecurityDescriptorDacl(IntPtr sd, bool present, IntPtr dacl, bool defaulted);
        [DllImport("advapi32.dll")]
        static extern uint GetSecurityInfo(IntPtr handle, int type, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr sd);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(IntPtr sd, uint revision, uint info, out IntPtr sddl, out uint len);
    }
}
