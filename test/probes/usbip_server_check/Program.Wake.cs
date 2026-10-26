// "--offline": the device's handoffs from a frame or a reply to the read
// that waits for it. Each device is built by its own constructor on process
// memory, its sections handed in through SharedMemoryIO's view caches as
// protocol_engine_check's DeviceLifetime builds one, and its connection is
// the server's own type over a local socket pair, as device_oracle_check's
// case 6 builds it. This thread plays the connection's reader: it hands the
// device each CMD_SUBMIT and CMD_UNLINK and reads back what the device sends.
// No device is plugged in and nothing needs elevation.
//
//   - a frame the input pump answered a read with goes to the next read when
//     the host unlinks the read it went to. usbip-win2 completes a read the
//     moment it cancels it, sends CMD_UNLINK, and drops a RET_SUBMIT that
//     comes for it after (device_ioctl.cpp send_cmd_unlink_and_complete,
//     request_list.cpp cancel and find_sent_request), and a device keeps its
//     report until a read takes it. With no read parked at the unlink, with
//     one parked, with both reads of a pair unlinked, and with the read the
//     frame went back to unlinked in turn. The frame reaches the wire before
//     a newer one, a newer frame queued before the unlink goes in its place,
//     a read unlinked while it is parked changes nothing, and no frame goes
//     out twice
//   - a command on the bulk OUT endpoint that leaves a reply for the input
//     endpoint wakes the host's poll clock, as a command on every other path
//     does
//   - a reply that a SET_REPORT or an interrupt OUT command leaves for the
//     bulk IN endpoint goes to the bulk read already parked for it
//   - a read a new import parks survives the old import's detach, which
//     clears its own reads before it lets the device go
//   - frames the input pump takes while no read is parked give way to the
//     newest, which the next read takes, as an endpoint carries the
//     firmware's current report and the HID path's driver keeps one frame
//     (driver.c ProcessSharedInput). A keyboard or mouse keeps every frame
//     in order, as the HID path's acknowledgement delivers every report
//   - a reader the output ring has lapped, reading the slot the device
//     refills next, never takes a slot mixed from two packets
//   - an isochronous URB the audio pump is completing is answered before an
//     unlink of it, and on the import it came on when a new import holds the
//     device by then
//   - a read the input pump or the poll clock has answered, unlinked before
//     the answer is on the wire, is answered before the RET_UNLINK
//   - a keyboard's frames, a report on change, a reply on a protocol's input
//     endpoint and part of a bulk reply, each answered on a read the host
//     then unlinks, go to the next reads: a keyboard's ahead of the frames
//     queued after them, and both of a pair the host unlinks together
//   - under a host that cancels reads as usbip-win2 does, a game pad's
//     frames arrive in rising order with each burst's last, a keyboard's
//     every frame once in order, and every RET_UNLINK with status 0 after
//     its read's answer
//
// Each scenario ends on a frame or a read that tells a correct device from
// one that lost the handoff at once, and a wait that only a correct device
// ends is skipped once an earlier step has failed.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    // Above the indexes the rest of the probe uses, so no cache entry here
    // meets one of theirs.
    const int HandoffIndex = Index + 100;

    static void HandoffOffline()
    {
        Console.WriteLine("\n-- A frame or a reply goes to the read that waits for it --");
        var ds = ControllerProfile.Parse(Encoding.UTF8.GetBytes(CatalogJson("dualsense-composite")))!;
        Contained("an unlink with no read parked", () => UnlinkNoReadParked(ds));
        Contained("an unlink with a read parked, before a new frame", () => UnlinkReadParkedNewFrame(ds));
        Contained("an unlink with a read parked", () => UnlinkReadParked(ds));
        Contained("the unlink of a pair", () => UnlinkPair(ds));
        Contained("the unlink of the read a frame was put back to", () => UnlinkPutBackRead(ds));
        Contained("an unlink after a newer frame", () => UnlinkAfterNewerFrame(ds));
        Contained("an unlink of a parked read", () => UnlinkParkedRead(ds));
        Contained("a frame put back and a read through the queue", () => PutBackAheadOfQueue(ds));
        Contained("a new import beside the old one's detach", () => NewImportBesideDetach(ds));
        Contained("frames no read took", () => NewestFrameWaits(ds));
        Contained("a keyboard's frames no read took", KeyboardKeepsEveryFrame);
        Contained("a keyboard's frame put back ahead of its queue", KeyboardPutBackAheadOfQueue);
        Contained("a keyboard's pair of reads unlinked", KeyboardPutBackPair);
        Contained("a report on change the host never got", LayoutChangePutBack);
        Contained("a reply on the input endpoint the host never got", ProtocolPacketsPutBack);
        Contained("a bulk reply the host never got", BulkReplyPutBack);
        Contained("a game pad's stress", () => Stress("a game pad", ds, HandoffIndex + 25, every: false, cancelOne: true, seed: 20261026));
        Contained("a keyboard's stress", () => Stress("a keyboard", KeyboardCopy("s43-keyboard-stress"), HandoffIndex + 26, every: true, cancelOne: false, seed: 20261027));
        Contained("a lapped output reader", () => LappedOutputReader(ds));
        Contained("an isochronous URB unlinked while it completes", () => AudioAnswerBeforeUnlink(ds));
        Contained("an isochronous URB whose import drops while it completes", () => AudioAnswerToItsImport(ds));
        Contained("a read the input pump answers, unlinked before the answer goes", () => PumpAnswerBeforeUnlink(ds));
        Contained("a read the poll clock answers, unlinked before the answer goes", PollAnswerBeforeUnlink);
        Contained("a bulk command on a poll clock", BulkCommandWakesPollClock);
        Contained("bulk replies to other command paths", RepliesReachParkedBulkRead);
    }

    /// <summary>A scenario that throws fails its own check, and the run
    /// goes on.</summary>
    static void Contained(string what, Action scenario)
    {
        try { scenario(); }
        catch (Exception e) { Check($"{what} runs to its end", false, e.ToString()); }
    }

    /// <summary>No read parked when the unlink comes: the next read takes
    /// the frame from the queue, ahead of a newer one.</summary>
    static void UnlinkNoReadParked(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        rig.Frame(1);
        var ra = rig.Next();
        int ua = rig.Unlink(a);
        uint b = rig.SubmitIn(ep, 64);
        rig.Frame(2);
        var rb = rig.Next();
        uint c = rig.SubmitIn(ep, 64);
        var rc = rb.IsFrame(b, 1) ? rig.Next() : default;
        rig.SubmitIn(ep, 64);
        var after = rc.IsFrame(c, 2) ? rig.UntilSentinel() : new();
        Check("a frame answered on a read the host then unlinks goes to the next read, ahead of a newer frame",
              ra.IsFrame(a, 1) && ua == 0 && rb.IsFrame(b, 1) && rc.IsFrame(c, 2) && after.Count == 0,
              $"first read {ra}, unlink {ua}, next read {rb}, the one after {rc}, then {Describe(after)}");
    }

    /// <summary>A read parked when the unlink comes, and a new frame before
    /// the input pump looks again: the pump hands the parked read the frame
    /// put back, then queues the new one. The pump reads the section under
    /// the device's lifetime lock, so holding it holds the pump until both
    /// are in.</summary>
    static void UnlinkReadParkedNewFrame(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 1);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        uint b = rig.SubmitIn(ep, 64);
        rig.Frame(3);
        var ra = rig.Next();
        int ua;
        lock (rig.LifetimeLock)
        {
            ua = rig.Unlink(a);
            rig.Frame(4);
        }
        var rb = rig.Next();
        uint c = rig.SubmitIn(ep, 64);
        var rc = rb.IsFrame(b, 3) ? rig.Next() : default;
        rig.SubmitIn(ep, 64);
        var after = rc.IsFrame(c, 4) ? rig.UntilSentinel() : new();
        Check("a frame answered on a read the host then unlinks goes to the read parked behind it, ahead of a newer frame",
              ra.IsFrame(a, 3) && ua == 0 && rb.IsFrame(b, 3) && rc.IsFrame(c, 4) && after.Count == 0,
              $"first read {ra}, unlink {ua}, parked read {rb}, next read {rc}, then {Describe(after)}");
    }

    /// <summary>A read parked when the unlink comes, and no new frame: the
    /// unlink wakes the input pump, which hands the parked read the frame
    /// put back.</summary>
    static void UnlinkReadParked(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 2);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        uint b = rig.SubmitIn(ep, 64);
        rig.Frame(10);
        var ra = rig.Next();
        int ua = rig.Unlink(a);
        var rb = rig.Next();
        rig.Frame(11);
        uint c = rig.SubmitIn(ep, 64);
        var rc = rig.Next();
        rig.SubmitIn(ep, 64);
        var after = rig.UntilSentinel();
        Check("an unlink with a read parked behind the answered one wakes the input pump, which hands that read the frame",
              ra.IsFrame(a, 10) && ua == 0 && rb.IsFrame(b, 10) && rc.IsFrame(c, 11) && after.Count == 0,
              $"first read {ra}, unlink {ua}, parked read {rb}, next read {rc}, then {Describe(after)}");
    }

    /// <summary>Both reads of a pair unlinked before the input pump looks
    /// again, as a host that cancels its reads does: the second is still
    /// parked at its unlink, and the frame goes to the read after them.</summary>
    static void UnlinkPair(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 3);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        uint b = rig.SubmitIn(ep, 64);
        rig.Frame(5);
        var ra = rig.Next();
        int ua, ub;
        lock (rig.LifetimeLock)
        {
            ua = rig.Unlink(a);
            ub = rig.Unlink(b);
        }
        uint c = rig.SubmitIn(ep, 64);
        rig.Frame(6);
        var rc = rig.Next();
        uint d = rig.SubmitIn(ep, 64);
        var rd = rc.IsFrame(c, 5) ? rig.Next() : default;
        rig.SubmitIn(ep, 64);
        var after = rd.IsFrame(d, 6) ? rig.UntilSentinel() : new();
        Check("a frame answered on a read when the host unlinks both reads of a pair goes to the read after them",
              ra.IsFrame(a, 5) && ua == 0 && ub == -UsbipProtocol.EConnReset && rc.IsFrame(c, 5) && rd.IsFrame(d, 6) && after.Count == 0,
              $"first read {ra}, unlinks {ua} and {ub}, next {rc}, then {rd}, then {Describe(after)}");
    }

    /// <summary>The read a frame was put back to, unlinked after it took the
    /// frame: the frame goes back again, to the read after it.</summary>
    static void UnlinkPutBackRead(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 4);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        uint b = rig.SubmitIn(ep, 64);
        rig.Frame(12);
        var ra = rig.Next();
        int ua = rig.Unlink(a);
        var rb = rig.Next();
        int ub = rig.Unlink(b);
        uint c = rig.SubmitIn(ep, 64);
        rig.Frame(13);
        var rc = rig.Next();
        uint d = rig.SubmitIn(ep, 64);
        var rd = rc.IsFrame(c, 12) ? rig.Next() : default;
        rig.SubmitIn(ep, 64);
        var after = rd.IsFrame(d, 13) ? rig.UntilSentinel() : new();
        Check("the read a frame was put back to, unlinked after it took it, passes the frame to the read after it",
              ra.IsFrame(a, 12) && ua == 0 && rb.IsFrame(b, 12) && ub == 0 && rc.IsFrame(c, 12) && rd.IsFrame(d, 13) && after.Count == 0,
              $"first read {ra}, unlink {ua}, parked read {rb}, unlink {ub}, next {rc}, then {rd}, then {Describe(after)}");
    }

    /// <summary>A newer frame queued before the unlink: it goes, and the
    /// frame the unlinked read had does not go out again.</summary>
    static void UnlinkAfterNewerFrame(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 5);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        rig.Frame(7);
        var ra = rig.Next();
        rig.Frame(8);
        bool queued = WaitUntil(() => rig.QueuedFrames == 1, Hang);
        int ua = rig.Unlink(a);
        uint b = rig.SubmitIn(ep, 64);
        var rb = rig.Next();
        rig.SubmitIn(ep, 64);
        var after = rig.UntilSentinel();
        Check("a frame queued before the unlink of an answered read goes in its place, and nothing goes out twice",
              ra.IsFrame(a, 7) && queued && ua == 0 && rb.IsFrame(b, 8) && after.Count == 0,
              $"first read {ra}, queued {queued}, unlink {ua}, next read {rb}, then {Describe(after)}");
    }

    /// <summary>A read unlinked while it is parked: the frame after it is
    /// queued and goes to the next read, once.</summary>
    static void UnlinkParkedRead(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 6);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        int ua = rig.Unlink(a);
        rig.Frame(9);
        bool queued = WaitUntil(() => rig.QueuedFrames == 1, Hang);
        uint b = rig.SubmitIn(ep, 64);
        var rb = rig.Next();
        rig.SubmitIn(ep, 64);
        var after = rig.UntilSentinel();
        Check("a read unlinked while parked is answered -ECONNRESET, and the next frame goes to the next read once",
              ua == -UsbipProtocol.EConnReset && queued && rb.IsFrame(b, 9) && after.Count == 0,
              $"unlink {ua}, queued {queued}, next read {rb}, then {Describe(after)}");
    }

    /// <summary>A frame put back for a parked read, and a newer frame: the
    /// input pump has the parked read and waits to send it the frame, held
    /// on the connection's send lock, which this thread takes first. A read
    /// that comes meanwhile, as the connection's reader would send it, gets
    /// the newer frame after the frame put back.</summary>
    static void PutBackAheadOfQueue(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 11);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        uint b = rig.SubmitIn(ep, 64);
        rig.Frame(14);
        var ra = rig.Next();
        int ua;
        uint d = 0;
        bool taken;
        Thread? reader;
        lock (rig.SendLock)
        {
            lock (rig.LifetimeLock)
            {
                ua = rig.Unlink(a);
                rig.Frame(15);
            }
            taken = WaitUntil(() => rig.WaitsToSend(rig.InputPump, () => rig.ParkedReads), Hang);
            d = rig.NextSeq();
            reader = rig.RunAsReader(() => rig.SubmitIn(d, ep, 64));
        }
        taken &= reader?.Join(Hang) ?? true;
        var first = rig.Next();
        var second = first.IsFrame(b, 14) ? rig.Next() : default;
        rig.SubmitIn(ep, 64);
        var after = second.IsFrame(d, 15) ? rig.UntilSentinel() : new();
        Check("a frame put back for a parked read reaches the wire before a newer frame a read takes from the queue",
              ra.IsFrame(a, 14) && ua == 0 && taken && first.IsFrame(b, 14) && second.IsFrame(d, 15) && after.Count == 0,
              $"first read {ra}, unlink {ua}, parked read taken {taken}, then {first}, then {second}, then {Describe(after)}");
    }

    /// <summary>The old import's connection ends while a new one claims the
    /// device. Its detach clears the reads, the queued frames and the
    /// protocol state it leaves, and lets the device go. This thread holds
    /// the device's HID lock, which the clear takes, so the detach stops
    /// there, and claims the device for a new connection and parks a read on
    /// it meanwhile. A detach that let the device go before its clear wiped
    /// the new import's read, and a frame after it waited in the queue
    /// instead. One that clears first still holds the device: the claim
    /// waits for it, as a new import's ST_DEV_BUSY makes the host controller
    /// try again.</summary>
    static void NewImportBesideDetach(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 12);
        using var next = new HandoffRig.Wire($"hm-s43-handoff-{Environment.ProcessId}-{HandoffIndex + 12}-next.sock");
        byte ep = rig.PrimaryIn;
        var detach = new Thread(() => rig.Device.DetachConnection(rig.Connection)) { IsBackground = true, Name = "S43 old import's detach" };
        bool stopped, claimedDuring;
        uint read = 0;
        lock (rig.HidLock)
        {
            detach.Start();
            stopped = WaitUntil(() => (detach.ThreadState & ThreadState.WaitSleepJoin) != 0, Hang);
            claimedDuring = rig.Device.TryClaimConnection(next.Connection);
            if (claimedDuring) read = next.SubmitIn(rig.Device, ep, 64);
        }
        bool ended = detach.Join(Hang);
        bool claimedAfter = !claimedDuring && rig.Device.TryClaimConnection(next.Connection);
        if (claimedAfter) read = next.SubmitIn(rig.Device, ep, 64);
        rig.Frame(16);
        // The frame goes to the read, or into the queue when the read is gone.
        bool handled = WaitUntil(() => next.Available > 0 || rig.QueuedFrames > 0, Hang);
        var got = next.Available > 0 ? next.Next() : default;
        Check("a read a new import parks beside the old import's detach takes the next frame",
              stopped && ended && (claimedDuring || claimedAfter) && handled && got.IsFrame(read, 16),
              $"detach stopped at the clear {stopped}, claimed during it {claimedDuring}, after it {claimedAfter}, read {got}, frames queued {rig.QueuedFrames}");
    }

    /// <summary>Frames the input pump takes while no read is parked: the
    /// device holds one for the read to come, the newest, as an endpoint
    /// carries the firmware's current report and the HID path's driver keeps
    /// one frame. The next read takes it, and the read after it waits for the
    /// frame to come.</summary>
    static void NewestFrameWaits(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 13);
        byte ep = rig.PrimaryIn;
        bool queued = true;
        for (byte mark = 21; mark <= 25; mark++) queued &= rig.FrameQueued(mark);
        int held = rig.QueuedFrames;
        uint a = rig.SubmitIn(ep, 64);
        var ra = rig.Next();
        uint b = rig.SubmitIn(ep, 64);
        rig.Frame(26);
        var rb = ra.IsFrame(a, 25) ? rig.Next() : default;
        rig.SubmitIn(ep, 64);
        var after = rb.IsFrame(b, 26) ? rig.UntilSentinel() : new();
        Check("frames no read took give way to the newest, which the next read takes, and the read after it waits for the next frame",
              queued && held == 1 && ra.IsFrame(a, 25) && rb.IsFrame(b, 26) && after.Count == 0,
              $"each frame taken up {queued}, frames held {held}, first read {ra}, next read {rb}, then {Describe(after)}");
    }

    /// <summary>A keyboard or mouse gets every report in order, as the HID
    /// path's acknowledgement gives it: a copy of the DualSense composite
    /// typed as a keyboard keeps each frame no read took, and the reads take
    /// them oldest first.</summary>
    static void KeyboardKeepsEveryFrame()
    {
        using var rig = new HandoffRig(KeyboardCopy("s43-dualsense-composite-as-keyboard"), HandoffIndex + 14);
        byte ep = rig.PrimaryIn;
        bool queued = true;
        for (byte mark = 31; mark <= 33; mark++) queued &= rig.FrameQueued(mark);
        int held = rig.QueuedFrames;
        var got = new List<HandoffRig.Message>();
        var reads = new List<uint>();
        for (int i = 0; i < 3; i++)
        {
            reads.Add(rig.SubmitIn(ep, 64));
            got.Add(rig.Next());
        }
        rig.SubmitIn(ep, 64);
        var after = rig.UntilSentinel();
        Check("a keyboard keeps every frame no read took, and the reads take them oldest first",
              queued && held == 3 && got[0].IsFrame(reads[0], 31) && got[1].IsFrame(reads[1], 32) && got[2].IsFrame(reads[2], 33) && after.Count == 0,
              $"each frame taken up {queued}, frames held {held}, reads {Describe(got)}, then {Describe(after)}");
    }

    /// <summary>A copy of the DualSense composite typed as a keyboard, which
    /// gets every report in order (ControllerProfile.DeliversEveryReport).</summary>
    static ControllerProfile KeyboardCopy(string id)
    {
        var j = JsonNode.Parse(CatalogJson("dualsense-composite"))!.AsObject();
        j["id"] = id;
        j["type"] = "keyboard";
        return ControllerProfile.Parse(Encoding.UTF8.GetBytes(j.ToJsonString()))!;
    }

    /// <summary>A keyboard's frame answered on a read the host then unlinks,
    /// with newer frames queued: the frame goes back ahead of them, since a
    /// keyboard needs every report in order.</summary>
    static void KeyboardPutBackAheadOfQueue()
    {
        using var rig = new HandoffRig(KeyboardCopy("s43-keyboard-put-back-ahead"), HandoffIndex + 20);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        rig.Frame(41);
        var ra = rig.Next();
        bool queued = rig.FrameQueued(42) && rig.FrameQueued(43);
        int ua = rig.Unlink(a);
        var got = rig.ReadEach(ep, 3);
        rig.SubmitIn(ep, 64);
        var after = got.Count == 3 ? rig.UntilSentinel() : new();
        Check("a keyboard's frame answered on a read the host then unlinks goes to the next read, ahead of the frames queued after it",
              ra.IsFrame(a, 41) && queued && ua == 0 && got.Count == 3
              && got[0].IsFrame(got[0].Seq, 41) && got[1].IsFrame(got[1].Seq, 42) && got[2].IsFrame(got[2].Seq, 43) && after.Count == 0,
              $"first read {ra}, frames queued {queued}, unlink {ua}, reads {Describe(got)}, then {Describe(after)}");
    }

    /// <summary>Both reads of a keyboard's pair answered, then both unlinked,
    /// as a host that cancels its reads does: both frames go back, in the
    /// order they went out.</summary>
    static void KeyboardPutBackPair()
    {
        using var rig = new HandoffRig(KeyboardCopy("s43-keyboard-put-back-pair"), HandoffIndex + 21);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        uint b = rig.SubmitIn(ep, 64);
        rig.Frame(51);
        var ra = rig.Next();
        rig.Frame(52);
        var rb = rig.Next();
        int ua = rig.Unlink(a);
        int ub = rig.Unlink(b);
        var got = rig.ReadEach(ep, 2);
        rig.SubmitIn(ep, 64);
        var after = got.Count == 2 ? rig.UntilSentinel() : new();
        Check("both frames of a keyboard's pair of reads the host unlinks go to the next reads, in order",
              ra.IsFrame(a, 51) && rb.IsFrame(b, 52) && ua == 0 && ub == 0 && got.Count == 2
              && got[0].IsFrame(got[0].Seq, 51) && got[1].IsFrame(got[1].Seq, 52) && after.Count == 0,
              $"reads {ra} and {rb}, unlinks {ua} and {ub}, next reads {Describe(got)}, then {Describe(after)}");
    }

    /// <summary>A copy of the Switch 2 Pro composite whose report 0x09 goes
    /// out on change, without the report counter its program writes, so
    /// unchanged state builds the same bytes.</summary>
    static ControllerProfile ChangeLayoutCopy(string id)
    {
        var j = JsonNode.Parse(CatalogJson("switch2-pro-controller-composite"))!.AsObject();
        j["id"] = id;
        var layout = j["inputReports"]!.AsArray().Select(n => n!.AsObject()).First(l => (string?)l["reportId"] == "0x09");
        layout["send"] = "change";
        var program = layout["program"]!.AsArray();
        program.Remove(program.First(op => op!.AsObject().ContainsKey("counter")));
        return ControllerProfile.Parse(Encoding.UTF8.GetBytes(j.ToJsonString()))!;
    }

    /// <summary>A report on change answered on a read the host then unlinks:
    /// the state has not changed, so no tick sends it again, and the next
    /// read gets it once more, as a device whose report the host lost.</summary>
    static void LayoutChangePutBack()
    {
        using var rig = new HandoffRig(ChangeLayoutCopy("s43-change-report-put-back"), HandoffIndex + 22);
        var p = rig.Device.Protocol!.Protocol;
        var clock = rig.HoldLayoutClock();
        // 03/0D, which lets reports flow.
        uint on = rig.SubmitOut(p.CommandEndpoint, Convert.FromHexString("0300000D00000000"));
        var ack = rig.Next();
        uint a = rig.SubmitIn(p.InputEndpoint, 64);
        bool ticked = clock.TickNow();
        var ra = ticked ? rig.NextWithin(Hang) : default;
        int ua = rig.Unlink(a);
        uint b = rig.SubmitIn(p.InputEndpoint, 64);
        clock.TickNow();
        var rb = rig.NextWithin(1000);
        Check("a report on change the host never got goes to the next read, though the state has not changed",
              ack.Is(on, 8) && ticked && ra.Is(a, 64) && ra.Data[0] == 0x09 && ua == 0
              && rb.Is(b, 64) && rb.Data.AsSpan().SequenceEqual(ra.Data),
              $"reports on {ack}, ticked {ticked}, first read {ra}, unlink {ua}, next read {rb}");
    }

    /// <summary>A reply the poll clock answered a read with, which the host
    /// then unlinks: the next read gets the reply's packets again.</summary>
    static void ProtocolPacketsPutBack()
    {
        using var rig = new HandoffRig(PollClockCopy("s43-poll-clock-put-back"), HandoffIndex + 23);
        var p = rig.Device.Protocol!.Protocol;
        var (clock, _) = rig.HoldPollClock();
        uint a = rig.SubmitIn(p.InputEndpoint, 64);
        uint cmd = rig.SubmitOut(p.CommandEndpoint, Convert.FromHexString("1091000100000000"));
        var ack = rig.Next();
        bool ticked = clock.TickNow();
        var ra = ticked ? rig.Next() : default;
        int ua = rig.Unlink(a);
        uint b = rig.SubmitIn(p.InputEndpoint, 64);
        bool again = clock.TickNow();
        var rb = again ? rig.NextWithin(1000) : default;
        Check("a reply on the input endpoint answered on a read the host then unlinks goes to the next read",
              ack.Is(cmd, 8) && ticked && ra.Is(a, 20) && ra.Data[0] == 0x10 && ua == 0
              && again && rb.Is(b, 20) && rb.Data.AsSpan().SequenceEqual(ra.Data),
              $"command answered {ack}, first read {ra}, unlink {ua}, ticked again {again}, next read {rb}");
    }

    /// <summary>The first 64 bytes of an 80-byte bulk reply, answered on a
    /// read the host then unlinks: the next read gets those 64 bytes again
    /// and the one after it the last 16.</summary>
    static void BulkReplyPutBack()
    {
        var j = JsonNode.Parse(CatalogJson("switch2-pro-controller-composite"))!.AsObject();
        j["id"] = "s43-bulk-reply-put-back";
        using var rig = new HandoffRig(ControllerProfile.Parse(Encoding.UTF8.GetBytes(j.ToJsonString()))!, HandoffIndex + 24);
        var p = rig.Device.Protocol!.Protocol;
        uint a = rig.SubmitIn(p.ReplyEndpoint, 64);
        // 02/01 reads 64 bytes of the flash image from 0x13000.
        byte[] flashRead = Convert.FromHexString("02910001000000000000000000300100");
        uint cmd = rig.SubmitOut(p.CommandEndpoint, flashRead);
        var got = rig.UntilSentinel();
        int ua = rig.Unlink(a);
        var rest = rig.ReadEach(p.ReplyEndpoint, 2);
        Check("the part of a bulk reply answered on a read the host then unlinks goes to the next read, and the rest after it",
              got.Count == 2 && got[0].Is(cmd, flashRead.Length) && got[1].Is(a, 64) && ua == 0
              && rest.Count == 2 && rest[0].Length == 64 && rest[0].Data.AsSpan().SequenceEqual(got[1].Data) && rest[1].Length == 16,
              $"{Describe(got)}, unlink {ua}, next reads {Describe(rest)}");
    }

    // How long each stress runs, and the most frames it writes.
    const int StressMs = 3_000, StressFrames = 60_000;

    /// <summary>A device's handoffs under the cancels a host makes, for
    /// StressMs. The host keeps two reads out, and a thread of its own takes
    /// the answers, slowed at random. Another thread writes frames in bursts
    /// of one to six, each numbered. A cancel follows usbip-win2: the read
    /// completes at once, a CMD_UNLINK goes out, and an answer that comes
    /// for the read after is dropped (request_list.cpp cancel and
    /// find_sent_request). Each burst ends with a cancel of both reads a
    /// moment after its last frame, and with <paramref name="cancelOne"/> a
    /// read is canceled at random while the burst is written. A RET_UNLINK
    /// with status 0 follows its read's RET_SUBMIT, and a read the device
    /// unlinked parked gets no answer. A device that holds one frame delivers
    /// frames in rising order and each burst's last frame. A keyboard
    /// delivers every frame once, in order, so its writer waits for the
    /// input pump to read each frame from the section, which holds one,
    /// before it writes the next.</summary>
    static void Stress(string what, ControllerProfile profile, int index, bool every, bool cancelOne, int seed)
    {
        var rig = new HandoffRig(profile, index);
        Thread? receiver = null, writer = null;
        bool stopping = false;
        var go = new SemaphoreSlim(0);
        int burst = 0;
        try
        {
            byte ep = rig.PrimaryIn;
            var rng = new Random(seed);
            object host = new();
            var outstanding = new HashSet<uint>();
            var canceled = new HashSet<uint>();
            var answered = new HashSet<uint>();
            var unlinkOf = new Dictionary<uint, uint>();
            var resetReads = new HashSet<uint>();
            var delivered = new List<int>();
            int dropped = 0, unknown = 0, afterReset = 0, early = 0, answeredUnlinks = 0, parkedUnlinks = 0;
            Exception? receiveError = null;
            receiver = new Thread(() =>
            {
                var r = new Random(seed + 2);
                try
                {
                    while (true)
                    {
                        // A host takes a while to see each answer.
                        if (r.Next(3) == 0) Spin(r.Next(0, 120));
                        var m = rig.Next();
                        lock (host)
                        {
                            if (m.Kind == UsbipProtocol.RetUnlink)
                            {
                                if (!unlinkOf.Remove(m.Seq, out uint v)) unknown++;
                                else if (m.Status == 0)
                                {
                                    answeredUnlinks++;
                                    // Status 0 says the read's RET_SUBMIT came first.
                                    if (!answered.Contains(v)) early++;
                                }
                                else
                                {
                                    parkedUnlinks++;
                                    resetReads.Add(v);
                                    if (answered.Contains(v)) afterReset++;
                                }
                                Monitor.PulseAll(host);
                                continue;
                            }
                            answered.Add(m.Seq);
                            if (resetReads.Contains(m.Seq)) afterReset++;
                            if (canceled.Remove(m.Seq)) dropped++;
                            else if (outstanding.Remove(m.Seq) && m.Data.Length == 64) delivered.Add(m.Data[1] | m.Data[2] << 8);
                            else unknown++;
                            Monitor.PulseAll(host);
                        }
                    }
                }
                catch (Exception) when (Volatile.Read(ref stopping)) { }
                catch (Exception e) { receiveError = e; }
            }) { IsBackground = true, Name = $"S43 stress receiver, {what}" };
            receiver.Start();

            var burstDone = new SemaphoreSlim(0);
            int written = 0;
            bool taken = true;
            writer = new Thread(() =>
            {
                var r = new Random(seed + 1);
                while (true)
                {
                    go.Wait();
                    int k = Volatile.Read(ref burst);
                    if (k < 0) return;
                    for (int i = 0; i < k; i++)
                    {
                        int m = written + 1;
                        rig.FrameMarked((ushort)m);
                        Volatile.Write(ref written, m);
                        if (every && !rig.WaitTaken()) Volatile.Write(ref taken, false);
                        if (r.Next(2) == 0) Spin(r.Next(0, 150));
                    }
                    burstDone.Release();
                }
            }) { IsBackground = true, Name = $"S43 stress writer, {what}" };
            writer.Start();

            void EnsureTwo()
            {
                while (true)
                {
                    uint s;
                    lock (host)
                    {
                        if (outstanding.Count >= 2) return;
                        s = rig.NextSeq();
                        outstanding.Add(s);
                    }
                    rig.SubmitIn(s, ep, 64);
                }
            }
            void Cancel(bool all)
            {
                var victims = new List<(uint U, uint V)>();
                lock (host)
                {
                    var list = outstanding.ToList();
                    if (list.Count == 0) return;
                    if (!all) list = new List<uint> { list[rng.Next(list.Count)] };
                    foreach (var v in list)
                    {
                        outstanding.Remove(v);
                        canceled.Add(v);
                        uint u = rig.NextSeq();
                        unlinkOf[u] = v;
                        victims.Add((u, v));
                    }
                }
                foreach (var (u, v) in victims) rig.SendUnlink(u, v);
            }
            // The newest frame delivered is the burst's last: a keyboard's
            // frames come in order, so the burst's others came before it.
            bool Arrived(int last) => delivered.Count > 0 && delivered[^1] == last;

            int bursts = 0, lost = 0, stuck = 0;
            var lostAt = new List<int>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < StressMs && Volatile.Read(ref written) < StressFrames && receiveError == null)
            {
                bursts++;
                EnsureTwo();
                Volatile.Write(ref burst, rng.Next(1, 7));
                go.Release();
                while (!burstDone.Wait(0))
                {
                    if (cancelOne && rng.Next(5) == 0) Cancel(all: false);
                    EnsureTwo();
                    Spin(rng.Next(0, 80));
                }
                int last = Volatile.Read(ref written);
                // A host that stops reading: both reads canceled a moment
                // after the last frame, then two new reads.
                Spin(rng.Next(0, 60));
                Cancel(all: true);
                EnsureTwo();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool arrived = false, idle = false;
                while (sw.ElapsedMilliseconds < Hang)
                {
                    EnsureTwo();
                    lock (host)
                        if (Arrived(last)) { arrived = true; break; }
                    // Both of the host's reads parked: every answer to a read
                    // it did not cancel has reached it, since it sends a new
                    // read only once it has an answer or has canceled one,
                    // and a frame the pump read has gone to a read.
                    if (rig.IdleWithParked(2, last))
                    {
                        lock (host) arrived = Arrived(last);
                        idle = !arrived;
                        break;
                    }
                    lock (host) Monitor.Wait(host, 1);
                }
                if (arrived) continue;
                if (idle)
                {
                    lost++;
                    if (lostAt.Count < 5) lostAt.Add(last);
                }
                else stuck++;
            }
            // The writer ends at its next burst, and has written its last
            // frame once it has.
            Volatile.Write(ref burst, -1);
            go.Release();
            writer.Join(Hang);

            int n = Volatile.Read(ref written), breakAt = -1;
            bool inOrder;
            lock (host)
            {
                for (int i = 1; i < delivered.Count && breakAt < 0; i++)
                    if (every ? delivered[i] != delivered[i - 1] + 1 : delivered[i] <= delivered[i - 1]) breakAt = i;
                inOrder = breakAt < 0 && (!every || delivered.Count == n && (n == 0 || delivered[0] == 1));
                Console.WriteLine($"  info: {what}: {bursts} bursts in {clock.ElapsedMilliseconds} ms, {n} frames written, {delivered.Count} delivered, " +
                                  $"{dropped} answers dropped for canceled reads, {answeredUnlinks} unlinks of answered reads, {parkedUnlinks} of parked reads");
                string where = breakAt < 0 ? "" : $", first break at {breakAt}: {delivered[breakAt - 1]} then {delivered[breakAt]}";
                Check(every ? $"{what}: every frame reaches the host once, in order, under the host's cancels"
                            : $"{what}: frames reach the host in rising order, none twice, under the host's cancels",
                      inOrder && Volatile.Read(ref taken), $"{delivered.Count} of {n} delivered{where}{(Volatile.Read(ref taken) ? "" : ", a frame the pump did not read in time")}");
                Check($"{what}: the last frame of every burst reaches the host", lost == 0 && stuck == 0,
                      $"{lost} of {bursts} bursts lost their last frame{(lostAt.Count > 0 ? ", the first at " + string.Join(", ", lostAt) : "")}, {stuck} left the device busy");
                Check($"{what}: a RET_UNLINK with status 0 follows its read's answer, and a read unlinked while parked gets none",
                      early == 0 && afterReset == 0 && unknown == 0 && receiveError == null,
                      $"unlinks ahead of the answer {early}, answers to a read unlinked while parked {afterReset}, unknown {unknown}{(receiveError != null ? ", " + receiveError.Message : "")}");
            }
        }
        finally
        {
            // No frame is written to the section once the rig frees it, and
            // the receiver's read of the closed connection ends it.
            Volatile.Write(ref burst, -1);
            go.Release();
            writer?.Join(Hang);
            Volatile.Write(ref stopping, true);
            rig.Dispose();
            receiver?.Join(Hang);
        }
    }

    /// <summary>The output ring's writer against a reader it has lapped. The
    /// reader starts each read from a cursor far behind, so it reads the
    /// oldest slot, the one the device refills next, while the device
    /// publishes packets whose report ID and bytes all carry the packet's own
    /// number. A slot taken with bytes of two packets fails the check, and a
    /// reader that took no slot at all proves nothing.</summary>
    static void LappedOutputReader(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 15);
        var publish = rig.PublishOutput;
        IntPtr ring = rig.Ring;
        var payload = new byte[SharedMemoryIO.DATA_CAPACITY];
        const int Packets = 200_000;
        int k = 0;
        while (k <= SharedMemoryIO.OUTPUT_RING_SLOTS)
        {
            k++;
            Array.Fill(payload, (byte)k);
            publish(0, (byte)k, payload);
        }
        bool stop = false;
        long taken = 0, mixed = 0;
        string? firstMixed = null;
        var reader = new Thread(() =>
        {
            var buf = new byte[SharedMemoryIO.DATA_CAPACITY];
            while (!Volatile.Read(ref stop))
            {
                uint cursor = 0;
                if (!SharedMemoryIO.TryReadOutputFrame(ring, ref cursor, out _, out byte id, out int size, buf)) continue;
                taken++;
                bool whole = size == buf.Length;
                for (int i = 0; i < size && whole; i++) whole = buf[i] == id;
                if (!whole && mixed++ == 0) firstMixed = $"report ID {id:X2}, size {size}, bytes {buf[0]:X2} to {buf[^1]:X2}";
            }
        }) { IsBackground = true, Name = "S43 lapped output reader" };
        reader.Start();
        while (k < Packets)
        {
            k++;
            Array.Fill(payload, (byte)k);
            publish(0, (byte)k, payload);
        }
        Volatile.Write(ref stop, true);
        reader.Join();
        Check("a reader the output ring has lapped takes no slot mixed from two packets",
              taken > 0 && mixed == 0,
              $"{Packets} packets, {taken} slots taken, {mixed} mixed{(firstMixed != null ? ", the first " + firstMixed : "")}");
    }

    /// <summary>An isochronous URB unlinked while the audio pump completes
    /// it: the pump has taken it from the URBs that wait for their tick and
    /// runs the consumer's handler, held here, before it answers. A RET_UNLINK
    /// with status 0 says the URB was answered, so it follows the URB's
    /// RET_SUBMIT on the wire (usbip_protocol.rst, USBIP_RET_UNLINK). Linux's
    /// stub sends a completed URB's RET_SUBMIT before any RET_UNLINK
    /// (stub_tx.c stub_tx_loop), and Linux's vhci gives the URB back on the
    /// RET_UNLINK and fails the connection on a RET_SUBMIT after it
    /// (vhci_rx.c vhci_recv_ret_unlink, vhci_recv_ret_submit).</summary>
    static void AudioAnswerBeforeUnlink(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 16);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        rig.Device.Audio.OutFrames += _ => { entered.Set(); release.Wait(Hang); };
        uint urb = rig.SubmitIsoOut(0x01, 10, 392);
        bool inHandler = entered.Wait(Hang);
        uint u = rig.NextSeq();
        rig.SendUnlink(u, urb);
        release.Set();
        var first = rig.Next();
        var second = rig.Next();
        Check("an isochronous URB unlinked while the audio pump completes it is answered before the unlink",
              inHandler && first.Kind == UsbipProtocol.RetSubmit && first.Seq == urb && first.Status == 0
              && second.Kind == UsbipProtocol.RetUnlink && second.Seq == u && second.Status == 0,
              $"in the handler {inHandler}, then {first}, then {second}");
    }

    /// <summary>An isochronous URB whose import drops while the audio pump
    /// completes it, and a new import that claims the device meanwhile. The
    /// URB's RET_SUBMIT goes to the connection the URB came on. A new import
    /// counts its seqnums from its own start (usbip-win2 context.cpp
    /// next_seqnum), so an answer it never asked for can carry the seqnum of
    /// a URB it did.</summary>
    static void AudioAnswerToItsImport(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 17);
        using var next = new HandoffRig.Wire($"hm-s43-handoff-{Environment.ProcessId}-{HandoffIndex + 17}-next.sock");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        rig.Device.Audio.OutFrames += _ => { entered.Set(); release.Wait(Hang); };
        uint urb = rig.SubmitIsoOut(0x01, 10, 392);
        bool inHandler = entered.Wait(Hang);
        rig.Device.DetachConnection(rig.Connection);
        bool claimed = rig.Device.TryClaimConnection(next.Connection);
        release.Set();
        bool answered = WaitUntil(() => rig.Available > 0 || next.Available > 0, Hang);
        var got = rig.Available > 0 ? rig.Next() : default;
        bool newQuiet = !WaitUntil(() => next.Available > 0, 300);
        Check("an isochronous URB answered after its import dropped is answered to that import, and the new import gets nothing",
              inHandler && claimed && answered && got.Kind == UsbipProtocol.RetSubmit && got.Seq == urb && newQuiet,
              $"in the handler {inHandler}, new import claimed {claimed}, the old import got {got}, the new one nothing {newQuiet}");
    }

    /// <summary>A read the input pump answers, unlinked before the answer is
    /// on the wire: the pump waits for the connection's send lock, which this
    /// thread holds, and the unlink comes as the connection's reader would
    /// send it. The RET_UNLINK's status 0 says the read's RET_SUBMIT went
    /// first, so it follows the pump's answer, and the frame goes back to the
    /// next read as it does for any answered read the host unlinks.</summary>
    static void PumpAnswerBeforeUnlink(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 18);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        uint u = rig.NextSeq();
        bool waiting;
        Thread? reader;
        lock (rig.SendLock)
        {
            rig.Frame(61);
            waiting = WaitUntil(() => rig.WaitsToSend(rig.InputPump, () => rig.ParkedReads), Hang);
            reader = rig.RunAsReader(() => rig.SendUnlink(u, a));
        }
        bool ended = reader?.Join(Hang) ?? true;
        var first = rig.Next();
        var second = rig.Next();
        uint b = rig.SubmitIn(ep, 64);
        var rb = rig.Next();
        rig.SubmitIn(ep, 64);
        var after = rig.UntilSentinel();
        Check("a read the input pump answers, unlinked before the answer is on the wire, is answered before the unlink, and its frame goes to the next read",
              waiting && ended && first.IsFrame(a, 61) && second.Kind == UsbipProtocol.RetUnlink && second.Seq == u && second.Status == 0
              && rb.IsFrame(b, 61) && after.Count == 0,
              $"pump waiting {waiting}, unlink ended {ended}, then {first}, then {second}, next read {rb}, then {Describe(after)}");
    }

    /// <summary>The same for a read the protocol's poll clock answers: the
    /// clock's tick, run on a thread here, waits for the send lock with the
    /// read it filled.</summary>
    static void PollAnswerBeforeUnlink()
    {
        using var rig = new HandoffRig(PollClockCopy("s43-poll-clock-answer-order"), HandoffIndex + 19);
        var p = rig.Device.Protocol!.Protocol;
        var (clock, _) = rig.HoldPollClock();
        uint read = rig.SubmitIn(p.InputEndpoint, 64);
        // 10/01, which the copy answers with its rules' reply.
        uint cmd = rig.SubmitOut(p.CommandEndpoint, Convert.FromHexString("1091000100000000"));
        var ack = rig.Next();
        uint u = rig.NextSeq();
        var tick = new Thread(() => clock.TickNow()) { IsBackground = true, Name = "S43 poll clock tick" };
        bool waiting;
        Thread? reader;
        lock (rig.SendLock)
        {
            tick.Start();
            waiting = WaitUntil(() => rig.WaitsToSend(tick, () => rig.PolledReads), Hang);
            reader = rig.RunAsReader(() => rig.SendUnlink(u, read));
        }
        bool ended = tick.Join(Hang) & (reader?.Join(Hang) ?? true);
        var first = rig.Next();
        var second = rig.Next();
        Check("a read the poll clock answers, unlinked before the answer is on the wire, is answered before the unlink",
              ack.Is(cmd, 8) && waiting && ended && first.Is(read, 20) && first.Data[0] == 0x10
              && second.Kind == UsbipProtocol.RetUnlink && second.Seq == u && second.Status == 0,
              $"command answered {ack}, tick waiting {waiting}, ended {ended}, then {first}, then {second}");
    }

    /// <summary>A copy of the Switch 2 Pro composite whose input endpoint
    /// sends the SDK's report on the host's poll clock, with replies on that
    /// endpoint and commands on its bulk OUT endpoint, which the protocol
    /// compiler accepts.</summary>
    static ControllerProfile PollClockCopy(string id)
    {
        var j = JsonNode.Parse(CatalogJson("switch2-pro-controller-composite"))!.AsObject();
        j["id"] = id;
        // The state block goes with the layouts, which only a device that
        // builds its reports has.
        j.Remove("inputReports");
        j.Remove("stateBlock");
        var protocol = j["protocol"]!.AsObject();
        protocol["input"]!.AsObject().Remove("clock");
        protocol.Remove("getReport");
        protocol["replies"] = new JsonObject { ["channel"] = "input", ["pending"] = 64, ["overflow"] = "dropOldestUnstarted" };
        return ControllerProfile.Parse(Encoding.UTF8.GetBytes(j.ToJsonString()))!;
    }

    static void BulkCommandWakesPollClock()
    {
        using var rig = new HandoffRig(PollClockCopy("s43-poll-clock-bulk-commands"), HandoffIndex + 7);
        var p = rig.Device.Protocol!.Protocol;
        Check("a copy of the Switch 2 Pro composite runs a poll clock on its input endpoint, with bulk OUT commands",
              p is { BuildsReports: false, InputEndpoint: 0x81, Source: CommandSource.BulkOut, Channel: ReplyChannel.Input },
              $"builds {p.BuildsReports}, input 0x{p.InputEndpoint:X2}, commands {p.Source}, replies {p.Channel}");
        var (clock, wake) = rig.HoldPollClock();
        uint read = rig.SubmitIn(p.InputEndpoint, 64);
        bool readWoke = wake.WaitOne(0);
        bool idle = !clock.TickNow();
        Check("a read on the input endpoint wakes the poll clock, which has nothing to send before a command",
              readWoke && idle, $"woken {readWoke}, idle {idle}");
        // 10/01, which the copy answers with its rules' reply, from the
        // stream device_oracle_check's case 8 sends.
        uint cmd = rig.SubmitOut(p.CommandEndpoint, Convert.FromHexString("1091000100000000"));
        var ack = rig.Next();
        bool commandWoke = wake.WaitOne(0);
        Check("a bulk OUT command that leaves a reply on the input endpoint wakes the poll clock",
              ack.Is(cmd, 8) && commandWoke, $"command answered {ack}, clock woken {commandWoke}");
        bool ticked = clock.TickNow();
        var reply = ticked ? rig.Next() : default;
        Check("the poll clock's next tick hands the reply to the parked read",
              ticked && reply.Is(read, 20) && reply.Data[0] == 0x10, $"ticked {ticked}, read {reply}");
    }

    /// <summary>The bulk read a reply waits for, parked before the command:
    /// it takes the reply after the command's own answer, before anything
    /// else comes. A copy of the Switch 2 Pro composite as it ships, on its
    /// bulk OUT endpoint, then copies whose commands come by SET_REPORT and
    /// on the HID interface's interrupt OUT endpoint.</summary>
    static void RepliesReachParkedBulkRead()
    {
        // 02/01 reads 64 bytes of the flash image from 0x13000: an 80-byte
        // reply, 64 to the parked read and 16 to the next.
        byte[] flashRead = Convert.FromHexString("02910001000000000000000000300100");
        int index = HandoffIndex + 8;
        foreach (var (name, path, patch) in new (string, string, Action<JsonObject>)[]
        {
            ("bulk-out", "a bulk OUT command", _ => { }),
            ("set-report", "a SET_REPORT command", c => c["setReport"] = true),
            ("interrupt-out", "an interrupt OUT command", c => c["endpoint"] = "0x01"),
        })
        {
            var j = JsonNode.Parse(CatalogJson("switch2-pro-controller-composite"))!.AsObject();
            j["id"] = "s43-bulk-replies-" + name;
            patch(j["protocol"]!["commands"]!.AsObject());
            var profile = ControllerProfile.Parse(Encoding.UTF8.GetBytes(j.ToJsonString()))!;
            using var rig = new HandoffRig(profile, index++);
            var p = rig.Device.Protocol!.Protocol;
            uint parked = rig.SubmitIn(p.ReplyEndpoint, 64);
            uint cmd = name switch
            {
                "set-report" => rig.ControlOut(0x21, 0x09, 0x0202, rig.Device.Descriptors.HidInterfaceNumber, flashRead),
                _ => rig.SubmitOut(p.CommandEndpoint, flashRead),
            };
            var got = rig.UntilSentinel();
            uint next = rig.SubmitIn(p.ReplyEndpoint, 64);
            var rest = got.Count == 2 ? rig.Next() : default;
            Check($"{path}'s reply goes to the bulk read parked before it, after the command's answer, and the rest to the next read",
                  got.Count == 2 && got[0].Is(cmd, flashRead.Length) && got[1].Is(parked, 64) && got[1].Data[0] == 0x02
                  && rest.Is(next, 16),
                  $"commands {p.Source}{(p.CommandsFromSetReport ? " and SET_REPORT" : "")}: {Describe(got)}, then {rest}");
        }
    }

    static string Describe(List<HandoffRig.Message> messages) =>
        messages.Count == 0 ? "nothing" : string.Join(", ", messages.Select(m => m.ToString()));

    static string CatalogJson(string id)
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

    static Dictionary<int, IntPtr> ViewCache(string field) =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

    /// <summary>One device on process memory, its connection, and the host's
    /// end of that connection.</summary>
    sealed class HandoffRig : IDisposable
    {
        public readonly UsbipEmulatedDevice Device;
        readonly int _index;
        readonly IntPtr _view, _ring, _inputEvent;
        readonly EventWaitHandle _outputEvent = new(false, EventResetMode.AutoReset);
        readonly string _path;
        readonly Socket _listener, _client, _server;
        // The IN transfers' seqnums, which a stress reads on a thread of its own.
        readonly System.Collections.Concurrent.ConcurrentDictionary<uint, bool> _in = new();
        readonly byte[] _header = new byte[UsbipProtocol.HeaderSize];
        uint _seq = 0x100, _frameSeq;
        IDeviceClock? _heldClock;
        readonly UsbipServer.Connection _connection;

        public HandoffRig(ControllerProfile profile, int index)
        {
            _index = index;
            _view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
            _ring = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_OUTPUT_SIZE);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, _view, SharedMemoryIO.SHARED_INPUT_SIZE);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_OUTPUT_SIZE], 0, _ring, SharedMemoryIO.SHARED_OUTPUT_SIZE);
            ViewCache("s_inputViews")[index] = _view;
            ViewCache("s_outputViews")[index] = _ring;
            ViewCache("s_outputEvents")[index] = _outputEvent.SafeWaitHandle.DangerousGetHandle();
            Device = new UsbipEmulatedDevice(profile, index);
            // The named input event needs elevation, so the device's pump
            // found none and polls. An unnamed one takes its place, which the
            // pump waits on from its next pass and closes as it ends, and
            // which each frame below signals as SubmitState does.
            _inputEvent = CreateEventW(IntPtr.Zero, false, false, null);
            typeof(UsbipEmulatedDevice).GetField("_inputWaitEvent", Private)!.SetValue(Device, _inputEvent);

            _path = Path.Combine(Path.GetTempPath(), $"hm-s43-handoff-{Environment.ProcessId}-{index}.sock");
            if (File.Exists(_path)) File.Delete(_path);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_path));
            _listener.Listen(1);
            _client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _client.Connect(new UnixDomainSocketEndPoint(_path));
            _server = _listener.Accept();
            // The hang bound: a read no device answers fails the run there.
            _client.ReceiveTimeout = Hang;
            var t = typeof(UsbipServer.Connection);
            var connection = _connection = (UsbipServer.Connection)RuntimeHelpers.GetUninitializedObject(t);
            t.GetField("_socket", Private)!.SetValue(connection, _server);
            t.GetField("_sendLock", Private)!.SetValue(connection, new object());
            // The reply buffer its field initializer makes (UsbipServer.cs,
            // Connection._tx), which no constructor run sets here.
            t.GetField("_tx", Private)!.SetValue(connection, new byte[UsbipProtocol.HeaderSize + 4096]);
            if (!Device.TryClaimConnection(connection)) throw new InvalidOperationException("the device refused the connection");
        }

        /// <summary>The interrupt IN endpoint of the primary HID interface,
        /// the one the input pump feeds.</summary>
        public byte PrimaryIn
        {
            get
            {
                foreach (var kv in Device.Descriptors.Endpoints)
                {
                    var e = kv.Value;
                    if (e.TransferType == 3 && e.IsIn && e.InterfaceNumber == Device.Descriptors.HidInterfaceNumber) return e.Address;
                }
                throw new InvalidOperationException("no interrupt IN endpoint on the HID interface");
            }
        }

        /// <summary>The device's lifetime lock, which the input pump holds
        /// while it reads the input section: held here, it keeps the pump
        /// from its next look at the section and at a frame put back.</summary>
        public object LifetimeLock => typeof(UsbipEmulatedDevice).GetField("_lifetimeLock", Private)!.GetValue(Device)!;

        /// <summary>The connection's send lock, which every reply takes:
        /// held here, it stops a reply the input pump sends, while this
        /// thread, which may take it again, still sends.</summary>
        public object SendLock => typeof(UsbipServer.Connection).GetField("_sendLock", Private)!.GetValue(_connection)!;

        /// <summary>The connection the device was claimed for.</summary>
        public UsbipServer.Connection Connection => _connection;

        /// <summary>The device's output ring.</summary>
        public IntPtr Ring => _ring;

        /// <summary>The device's publish of one output packet into its ring,
        /// which the connection's reader calls for an output report.</summary>
        public delegate void Publish(byte source, byte reportId, ReadOnlySpan<byte> data);

        public Publish PublishOutput =>
            (Publish)Delegate.CreateDelegate(typeof(Publish), Device, typeof(UsbipEmulatedDevice).GetMethod("PublishOutput", Private)!);

        /// <summary>The device's HID lock, which guards its parked reads and
        /// queued frames.</summary>
        public object HidLock => typeof(UsbipEmulatedDevice).GetField("_hidLock", Private)!.GetValue(Device)!;

        /// <summary>Reads parked on the primary endpoint.</summary>
        public int ParkedReads
        {
            get
            {
                var t = typeof(UsbipEmulatedDevice);
                lock (t.GetField("_hidLock", Private)!.GetValue(Device)!)
                    return ((System.Collections.ICollection)t.GetField("_primaryPending", Private)!.GetValue(Device)!).Count;
            }
        }

        /// <summary>Frames the device holds for reads to come.</summary>
        public int QueuedFrames
        {
            get
            {
                var t = typeof(UsbipEmulatedDevice);
                lock (t.GetField("_hidLock", Private)!.GetValue(Device)!)
                    return ((System.Collections.ICollection)t.GetField("_frameQueue", Private)!.GetValue(Device)!).Count;
            }
        }

        /// <summary>One frame on the data lane, as SubmitState writes it: 63
        /// bytes, the first <paramref name="mark"/>.</summary>
        public void Frame(byte mark)
        {
            var data = new byte[63];
            data[0] = mark;
            SharedMemoryIO.WriteInputFrame(_view, _inputEvent, ref _frameSeq, data, data.Length, null);
        }

        /// <summary>One frame while no read is parked, then a wait until the
        /// input pump has taken it up into the frames the device holds.
        /// False when it has not within the hang bound.</summary>
        public bool FrameQueued(byte mark)
        {
            Frame(mark);
            return WaitUntil(() => NewestHeldMark == mark, Hang);
        }

        /// <summary>The mark of the newest frame the device holds for a read,
        /// null for none.</summary>
        public byte? NewestHeldMark
        {
            get
            {
                var t = typeof(UsbipEmulatedDevice);
                lock (HidLock)
                {
                    object? newest = null;
                    foreach (var f in (System.Collections.IEnumerable)t.GetField("_frameQueue", Private)!.GetValue(Device)!) newest = f;
                    return newest == null ? null : ((byte[])newest.GetType().GetField("Buffer")!.GetValue(newest)!)[1];
                }
            }
        }

        /// <summary>A frame numbered <paramref name="mark"/> in its first two
        /// bytes, low byte first. One thread writes the frames.</summary>
        public void FrameMarked(ushort mark)
        {
            var data = new byte[63];
            data[0] = (byte)mark;
            data[1] = (byte)(mark >> 8);
            SharedMemoryIO.WriteInputFrame(_view, _inputEvent, ref _frameSeq, data, data.Length, null);
        }

        /// <summary>Waits until the input pump has read the frame written
        /// last from the section, which holds one frame. False when it has
        /// not within the hang bound.</summary>
        public bool WaitTaken()
        {
            var field = typeof(UsbipEmulatedDevice).GetField("_lastSharedSeqNo", Private)!;
            uint want = _frameSeq;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var spin = new SpinWait();
            while ((uint)field.GetValue(Device)! != want)
            {
                if (sw.ElapsedMilliseconds > Hang) return false;
                spin.SpinOnce(-1);
            }
            return true;
        }

        /// <summary>True when the device has <paramref name="reads"/> reads
        /// parked on the primary endpoint, no frame queued or put back, and
        /// the frame numbered <paramref name="last"/> in its input report
        /// slot. The pump writes the slot and answers or queues the frame in
        /// one hold of the HID lock, so that frame has gone to a read.</summary>
        public bool IdleWithParked(int reads, int last)
        {
            var t = typeof(UsbipEmulatedDevice);
            object Field(string name) => t.GetField(name, Private)!.GetValue(Device)!;
            lock (HidLock)
            {
                if (((System.Collections.ICollection)Field("_frameQueue")).Count != 0) return false;
                if (((System.Collections.ICollection)Field("_primaryPending")).Count != reads) return false;
                if ((bool)Field("_resend")) return false;
                var slot = ((byte[][])Field("_inputReports"))[((int[])Field("_inputSlot"))[0x01]];
                return (slot[1] | slot[2] << 8) == (last & 0xFFFF);
            }
        }

        /// <summary>The device's poll clock, stopped and replaced with an
        /// unstarted one of the same tick, so this thread sees its wake event
        /// and runs its ticks. Returns the clock and its wake event.</summary>
        public (IDeviceClock Clock, AutoResetEvent Wake) HoldPollClock()
        {
            var t = typeof(UsbipEmulatedDevice);
            var field = t.GetField("_protocolClock", Private)!;
            ((IDeviceClock)field.GetValue(Device)!).Stop(Hang);
            var tickType = t.GetNestedType("HostPollTick", BindingFlags.NonPublic)!;
            object tick = Activator.CreateInstance(tickType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { Device }, null)!;
            var clockType = typeof(DeviceClock<>).MakeGenericType(tickType);
            var clock = (IDeviceClock)Activator.CreateInstance(clockType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { tick, MissedTick.NextPoll, "S43 held poll clock", false }, null)!;
            field.SetValue(Device, clock);
            _heldClock = clock;
            return (clock, (AutoResetEvent)clockType.GetField("_wake", Private)!.GetValue(clock)!);
        }

        UsbipProtocol.CommandHeader Command(byte endpoint, bool isIn, int length) => Command(++_seq, endpoint, isIn, length);

        static UsbipProtocol.CommandHeader Command(uint seq, byte endpoint, bool isIn, int length) => new()
        {
            Command = UsbipProtocol.CmdSubmit,
            Seqnum = seq,
            Direction = isIn ? 1u : 0u,
            Ep = (uint)(endpoint & 0x0F),
            TransferBufferLength = length,
            NumberOfPackets = UsbipProtocol.NumberOfPacketsNonIsoch,
        };

        /// <summary>An IN transfer, interrupt or bulk as the endpoint is.</summary>
        public uint SubmitIn(byte endpoint, int length)
        {
            uint seq = NextSeq();
            SubmitIn(seq, endpoint, length);
            return seq;
        }

        /// <summary>The same with seqnum <paramref name="seq"/>, for a
        /// transfer another thread sends.</summary>
        public void SubmitIn(uint seq, byte endpoint, int length)
        {
            _in[seq] = true;
            Device.HandleSubmit(Command(seq, endpoint, true, length), ReadOnlySpan<byte>.Empty, ReadOnlySpan<(uint, uint)>.Empty);
        }

        /// <summary>The device's input pump.</summary>
        public Thread InputPump => (Thread)typeof(UsbipEmulatedDevice).GetField("_inputThread", Private)!.GetValue(Device)!;

        /// <summary>Reads parked for the protocol's poll clock.</summary>
        public int PolledReads
        {
            get
            {
                lock (HidLock)
                    return ((System.Collections.ICollection)typeof(UsbipEmulatedDevice).GetField("_protocolReads", Private)!.GetValue(Device)!).Count;
            }
        }

        /// <summary>True once <paramref name="sender"/>, which took a read to
        /// answer, waits for a lock, this thread holding the send lock: either
        /// in the hold of the HID lock it took the read in, or after it, the
        /// read gone from <paramref name="parked"/>, which counts under the
        /// HID lock.</summary>
        public bool WaitsToSend(Thread sender, Func<int> parked)
        {
            if ((sender.ThreadState & ThreadState.WaitSleepJoin) == 0) return false;
            if (!Monitor.TryEnter(HidLock)) return true;
            try { return parked() == 0; }
            finally { Monitor.Exit(HidLock); }
        }

        /// <summary>Runs <paramref name="command"/> as the connection's reader
        /// would run it now: on this thread while the HID lock is free, else
        /// on a thread of its own, which waits for the lock behind its holder
        /// while this one goes on. Returns that thread, or null.</summary>
        public Thread? RunAsReader(Action command)
        {
            if (Monitor.TryEnter(HidLock))
            {
                try { command(); }
                finally { Monitor.Exit(HidLock); }
                return null;
            }
            var reader = new Thread(() => command()) { IsBackground = true, Name = "S43 reader behind the HID lock" };
            reader.Start();
            return reader;
        }

        /// <summary>An OUT transfer, interrupt or bulk as the endpoint is.</summary>
        public uint SubmitOut(byte endpoint, byte[] data)
        {
            var cmd = Command(endpoint, false, data.Length);
            Device.HandleSubmit(cmd, data, ReadOnlySpan<(uint, uint)>.Empty);
            return cmd.Seqnum;
        }

        public uint ControlOut(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, byte[] data)
        {
            var cmd = Command(0, false, data.Length);
            cmd.Setup = bmRequestType | ((ulong)bRequest << 8) | ((ulong)wValue << 16)
                      | ((ulong)wIndex << 32) | ((ulong)(ushort)data.Length << 48);
            Device.HandleSubmit(cmd, data, ReadOnlySpan<(uint, uint)>.Empty);
            return cmd.Seqnum;
        }

        /// <summary>An isochronous OUT transfer of <paramref name="packets"/>
        /// packets of <paramref name="packetBytes"/> bytes of silence.</summary>
        public uint SubmitIsoOut(byte endpoint, int packets, int packetBytes)
        {
            var cmd = Command(endpoint, false, packets * packetBytes);
            cmd.NumberOfPackets = packets;
            var iso = new (uint Offset, uint Length)[packets];
            for (int i = 0; i < packets; i++) iso[i] = ((uint)(i * packetBytes), (uint)packetBytes);
            Device.HandleSubmit(cmd, new byte[packets * packetBytes], iso);
            return cmd.Seqnum;
        }

        /// <summary>The next seqnum, for a command another thread sends.</summary>
        public uint NextSeq() => ++_seq;

        /// <summary>A CMD_UNLINK of <paramref name="victim"/> with seqnum
        /// <paramref name="seq"/>. Nothing is read back.</summary>
        public void SendUnlink(uint seq, uint victim) => Device.HandleUnlink(seq, victim);

        /// <summary>Bytes waiting on the host's end.</summary>
        public int Available => _client.Available;

        /// <summary>A CMD_UNLINK and its RET_UNLINK's status.</summary>
        public int Unlink(uint victim) => Unlink(victim, out _);

        /// <summary>A CMD_UNLINK and its RET_UNLINK's status, with what came
        /// on the wire before the RET_UNLINK.</summary>
        public int Unlink(uint victim, out List<Message> before)
        {
            uint seq = ++_seq;
            Device.HandleUnlink(seq, victim);
            before = new List<Message>();
            while (true)
            {
                var m = Next();
                if (m.Kind == UsbipProtocol.RetUnlink && m.Seq == seq) return m.Status;
                before.Add(m);
            }
        }

        /// <summary>Everything the device sent before the answer to a
        /// GET_STATUS this thread sends now, which the device answers on this
        /// thread before HandleSubmit returns. Bounded, for a device that
        /// sends more than it should.</summary>
        public List<Message> UntilSentinel()
        {
            var cmd = Command(0, true, 2);
            cmd.Setup = 0x80 | (2UL << 48);
            _in[cmd.Seqnum] = true;
            Device.HandleSubmit(cmd, ReadOnlySpan<byte>.Empty, ReadOnlySpan<(uint, uint)>.Empty);
            var got = new List<Message>();
            while (got.Count < 8)
            {
                var m = Next();
                if (m.Kind == UsbipProtocol.RetSubmit && m.Seq == cmd.Seqnum) break;
                got.Add(m);
            }
            return got;
        }

        /// <summary>The next RET_SUBMIT or RET_UNLINK on the wire, or nothing
        /// when none starts within <paramref name="ms"/>.</summary>
        public Message NextWithin(int ms) => WaitUntil(() => _client.Available > 0, ms) ? Next() : default;

        /// <summary>Up to <paramref name="count"/> reads on
        /// <paramref name="endpoint"/>, each sent once the one before it is
        /// answered: the answers, until a read gets none within a second.</summary>
        public List<Message> ReadEach(byte endpoint, int count)
        {
            var got = new List<Message>();
            for (int i = 0; i < count; i++)
            {
                uint seq = SubmitIn(endpoint, 64);
                var m = NextWithin(1000);
                if (m.Data == null) break;
                got.Add(m);
                if (m.Seq != seq) break;
            }
            return got;
        }

        /// <summary>The device's layout clock, stopped and replaced with an
        /// unstarted one of the same tick, so this thread runs its ticks.</summary>
        public IDeviceClock HoldLayoutClock()
        {
            var field = typeof(UsbipEmulatedDevice).GetField("_layoutClock", Private)!;
            var running = (IDeviceClock)field.GetValue(Device)!;
            running.Stop(Hang);
            var clockType = running.GetType();
            object tick = clockType.GetField("_tick", Private)!.GetValue(running)!;
            object missed = clockType.GetField("_missed", Private)!.GetValue(running)!;
            var clock = (IDeviceClock)Activator.CreateInstance(clockType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new object[] { tick, missed, "S43 held layout clock", false }, null)!;
            field.SetValue(Device, clock);
            _heldClock = clock;
            return clock;
        }

        /// <summary>The next RET_SUBMIT or RET_UNLINK on the wire.</summary>
        public Message Next()
        {
            ReadExactly(_header);
            uint kind = BinaryPrimitives.ReadUInt32BigEndian(_header);
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4));
            int status = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(20));
            int actual = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(24));
            int packets = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(32));
            // An OUT transfer's RET_SUBMIT carries its length and no data.
            var data = new byte[kind == UsbipProtocol.RetSubmit && _in.ContainsKey(seq) ? actual : 0];
            ReadExactly(data);
            // An isochronous one ends with a descriptor per packet.
            if (kind == UsbipProtocol.RetSubmit && packets > 0) ReadExactly(new byte[packets * UsbipProtocol.IsoDescriptorSize]);
            return new Message(kind, seq, status, kind == UsbipProtocol.RetSubmit && !_in.ContainsKey(seq) ? actual : data.Length, data);
        }

        void ReadExactly(Span<byte> dst)
        {
            for (int got = 0; got < dst.Length;)
            {
                int n = _client.Receive(dst[got..], SocketFlags.None);
                if (n <= 0) throw new EndOfStreamException("the device's end of the connection closed");
                got += n;
            }
        }

        public void Dispose()
        {
            _heldClock?.Stop(Hang);
            Device.Dispose();
            try { _client.Dispose(); } catch { }
            try { _server.Dispose(); } catch { }
            try { _listener.Dispose(); } catch { }
            try { File.Delete(_path); } catch { }
            ViewCache("s_inputViews").Remove(_index);
            ViewCache("s_outputViews").Remove(_index);
            ViewCache("s_outputEvents").Remove(_index);
            Marshal.FreeHGlobal(_view);
            Marshal.FreeHGlobal(_ring);
            _outputEvent.Dispose();
        }

        /// <summary>A second connection over a socket pair of its own, built
        /// as the rig builds its first, for a device another import claims.</summary>
        public sealed class Wire : IDisposable
        {
            readonly string _path;
            readonly Socket _listener, _client, _server;
            readonly byte[] _header = new byte[UsbipProtocol.HeaderSize];
            uint _seq = 0x8000;
            public readonly UsbipServer.Connection Connection;

            public Wire(string name)
            {
                _path = Path.Combine(Path.GetTempPath(), name);
                if (File.Exists(_path)) File.Delete(_path);
                _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _listener.Bind(new UnixDomainSocketEndPoint(_path));
                _listener.Listen(1);
                _client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                _client.Connect(new UnixDomainSocketEndPoint(_path));
                _server = _listener.Accept();
                _client.ReceiveTimeout = Hang;
                var t = typeof(UsbipServer.Connection);
                Connection = (UsbipServer.Connection)RuntimeHelpers.GetUninitializedObject(t);
                t.GetField("_socket", Private)!.SetValue(Connection, _server);
                t.GetField("_sendLock", Private)!.SetValue(Connection, new object());
                t.GetField("_tx", Private)!.SetValue(Connection, new byte[UsbipProtocol.HeaderSize + 4096]);
            }

            /// <summary>Bytes waiting on the host's end.</summary>
            public int Available => _client.Available;

            /// <summary>An interrupt IN CMD_SUBMIT on <paramref name="dev"/>.</summary>
            public uint SubmitIn(UsbipEmulatedDevice dev, byte endpoint, int length)
            {
                var cmd = new UsbipProtocol.CommandHeader
                {
                    Command = UsbipProtocol.CmdSubmit,
                    Seqnum = ++_seq,
                    Direction = 1,
                    Ep = (uint)(endpoint & 0x0F),
                    TransferBufferLength = length,
                    NumberOfPackets = UsbipProtocol.NumberOfPacketsNonIsoch,
                };
                dev.HandleSubmit(cmd, ReadOnlySpan<byte>.Empty, ReadOnlySpan<(uint, uint)>.Empty);
                return cmd.Seqnum;
            }

            /// <summary>The next RET_SUBMIT, of an IN transfer.</summary>
            public Message Next()
            {
                Receive(_header);
                uint kind = BinaryPrimitives.ReadUInt32BigEndian(_header);
                uint seq = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4));
                int status = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(20));
                int actual = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(24));
                var data = new byte[kind == UsbipProtocol.RetSubmit ? actual : 0];
                Receive(data);
                return new Message(kind, seq, status, data.Length, data);
            }

            void Receive(Span<byte> dst)
            {
                for (int got = 0; got < dst.Length;)
                {
                    int n = _client.Receive(dst[got..], SocketFlags.None);
                    if (n <= 0) throw new EndOfStreamException("the device's end of the connection closed");
                    got += n;
                }
            }

            public void Dispose()
            {
                try { _client.Dispose(); } catch { }
                try { _server.Dispose(); } catch { }
                try { _listener.Dispose(); } catch { }
                try { File.Delete(_path); } catch { }
            }
        }

        public readonly struct Message
        {
            public readonly uint Kind, Seq;
            public readonly int Status, Length;
            public readonly byte[] Data;
            public Message(uint kind, uint seq, int status, int length, byte[] data)
            {
                Kind = kind; Seq = seq; Status = status; Length = length; Data = data;
            }

            /// <summary>A RET_SUBMIT of <paramref name="seq"/> carrying the
            /// frame marked <paramref name="mark"/>: report ID 0x01, then the
            /// mark, 64 bytes.</summary>
            public bool IsFrame(uint seq, byte mark) =>
                Is(seq, 64) && Data.Length == 64 && Data[0] == 0x01 && Data[1] == mark;

            /// <summary>A RET_SUBMIT of <paramref name="seq"/> with status 0
            /// and an actual length of <paramref name="length"/>.</summary>
            public bool Is(uint seq, int length) =>
                Data != null && Kind == UsbipProtocol.RetSubmit && Seq == seq && Status == 0 && Length == length;

            public override string ToString() => Data == null ? "nothing"
                : Kind == UsbipProtocol.RetUnlink ? $"RET_UNLINK {Seq} status {Status}"
                : $"RET_SUBMIT {Seq} status {Status}, {Length} bytes{(Data.Length > 1 ? $" {Data[0]:X2} {Data[1]:X2}" : "")}";
        }
    }
}
