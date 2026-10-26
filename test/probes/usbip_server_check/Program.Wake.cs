// "--offline": the device's handoffs from a frame or a reply to the read
// that waits for it. Each device is built by its own constructor on process
// memory, its sections handed in through SharedMemoryIO's view caches as
// protocol_engine_check's DeviceLifetime builds one, and its connection is
// the server's own type over a local socket pair, as device_oracle_check's
// case 6 builds it. This thread plays the connection's reader: it hands the
// device each CMD_SUBMIT and CMD_UNLINK and reads back what the device sends.
// No device is plugged in and nothing needs elevation.
//
//   - a frame, a report on change, a reply on a protocol's input endpoint
//     and part of a bulk reply, each answered on a read the host then
//     unlinks, are lost and never sent again, as a real device's are behind
//     Linux's usbip-host. usbip-win2 completes a read the moment it cancels
//     it, sends CMD_UNLINK, and drops a RET_SUBMIT that comes for it after
//     (device_ioctl.cpp send_cmd_unlink_and_complete, request_list.cpp
//     cancel and find_sent_request), and the stub answers an unlink of a
//     completed URB with status 0 alone (stub_rx.c stub_recv_cmd_unlink).
//     The next read takes what comes next. A read unlinked while it is
//     parked changes nothing, and a host that cancels only the older of two
//     answered reads gets neither again
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
//   - under a host that cancels reads one at a time as usbip-win2 does, a
//     game pad's, a keyboard's and a mouse's reports never reach it twice or
//     older than one it has, a keyboard's and a mouse's every frame reaches
//     it unless it canceled the read, a game pad's last state reaches it
//     through its stream's repeat, a split bulk reply's every part goes on
//     the wire once, in order, and every RET_UNLINK with status 0 follows
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
        Contained("an unlink of an answered read", () => UnlinkAnsweredRead(ds));
        Contained("an unlink of an answered read with a read parked", () => UnlinkAnsweredReadParked(ds));
        Contained("an unlink after a newer frame", () => UnlinkAfterNewerFrame(ds));
        Contained("an unlink of a parked read", () => UnlinkParkedRead(ds));
        Contained("a new import beside the old one's detach", () => NewImportBesideDetach(ds));
        Contained("frames no read took", () => NewestFrameWaits(ds));
        Contained("a keyboard's frames no read took", KeyboardKeepsEveryFrame);
        Contained("a keyboard's frame the host never got", KeyboardLostFrame);
        Contained("a keyboard's single cancel", () => KeyboardSingleCancel("a keyboard", "keyboard", HandoffIndex + 29));
        Contained("a mouse's single cancel", () => KeyboardSingleCancel("a mouse", "mouse", HandoffIndex + 30));
        Contained("a report on change the host never got", LayoutChangeLost);
        Contained("a reply on the input endpoint the host never got", ProtocolPacketsLost);
        Contained("a bulk reply the host never got", BulkReplyLost);
        Contained("a bulk reply's single cancel", BulkSingleCancel);
        Contained("a game pad's stress", () => Stress("a game pad", ds, HandoffIndex + 25, every: false, seed: 20261026));
        Contained("a keyboard's stress", () => Stress("a keyboard", KeyboardCopy("s43-keyboard-stress"), HandoffIndex + 26, every: true, seed: 20261027));
        Contained("a mouse's stress", () => Stress("a mouse", KeyboardCopy("s43-mouse-stress", "mouse"), HandoffIndex + 27, every: true, seed: 20261028));
        Contained("a split bulk reply's stress", () => BulkStress(HandoffIndex + 31, 20261029));
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

    /// <summary>A frame answered on a read the host then unlinks is lost, as
    /// a real device's is behind Linux's usbip-host: the device has handed
    /// it over, and the stub answers the unlink with status 0 and sends it
    /// no more (stub_rx.c stub_recv_cmd_unlink). The next read takes the
    /// next frame.</summary>
    static void UnlinkAnsweredRead(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        rig.Frame(1);
        var ra = rig.Next();
        int ua = rig.Unlink(a);
        uint b = rig.SubmitIn(ep, 64);
        rig.Frame(2);
        var rb = rig.NextWithin(Hang);
        rig.SubmitIn(ep, 64);
        var after = rb.IsFrame(b, 2) ? rig.UntilSentinel() : new();
        Check("a frame answered on a read the host then unlinks is not sent again, and the next read takes the next frame",
              ra.IsFrame(a, 1) && ua == 0 && rb.IsFrame(b, 2) && after.Count == 0,
              $"first read {ra}, unlink {ua}, next read {rb}, then {Describe(after)}");
    }

    /// <summary>The same with a read parked behind the answered one: that
    /// read gets nothing until the next frame, which it takes.</summary>
    static void UnlinkAnsweredReadParked(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 1);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        uint b = rig.SubmitIn(ep, 64);
        rig.Frame(10);
        var ra = rig.Next();
        int ua = rig.Unlink(a);
        rig.Frame(11);
        var rb = rig.NextWithin(Hang);
        rig.SubmitIn(ep, 64);
        var after = rb.IsFrame(b, 11) ? rig.UntilSentinel() : new();
        Check("a frame answered on a read the host then unlinks does not go to the read parked behind it, which takes the next frame",
              ra.IsFrame(a, 10) && ua == 0 && rb.IsFrame(b, 11) && after.Count == 0,
              $"first read {ra}, unlink {ua}, parked read {rb}, then {Describe(after)}");
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

    /// <summary>A copy of the DualSense composite typed as a keyboard, or as
    /// <paramref name="type"/>, which gets every report in order
    /// (ControllerProfile.DeliversEveryReport).</summary>
    static ControllerProfile KeyboardCopy(string id, string type = "keyboard")
    {
        var j = JsonNode.Parse(CatalogJson("dualsense-composite"))!.AsObject();
        j["id"] = id;
        j["type"] = type;
        return ControllerProfile.Parse(Encoding.UTF8.GetBytes(j.ToJsonString()))!;
    }

    /// <summary>A keyboard's frame answered on a read the host then unlinks,
    /// with newer frames queued: the frame is lost, and the frames queued
    /// after it go to the next reads, in order.</summary>
    static void KeyboardLostFrame()
    {
        using var rig = new HandoffRig(KeyboardCopy("s43-keyboard-lost-frame"), HandoffIndex + 20);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        rig.Frame(41);
        var ra = rig.Next();
        bool queued = rig.FrameQueued(42) && rig.FrameQueued(43);
        int ua = rig.Unlink(a);
        var got = rig.ReadEach(ep, 2);
        rig.SubmitIn(ep, 64);
        var after = got.Count == 2 ? rig.UntilSentinel() : new();
        Check("a keyboard's frame answered on a read the host then unlinks is not sent again, and the frames queued after it go out in order",
              ra.IsFrame(a, 41) && queued && ua == 0 && got.Count == 2
              && got[0].IsFrame(got[0].Seq, 42) && got[1].IsFrame(got[1].Seq, 43) && after.Count == 0,
              $"first read {ra}, frames queued {queued}, unlink {ua}, reads {Describe(got)}, then {Describe(after)}");
    }

    /// <summary>A keyboard with two reads out, both answered, whose host
    /// cancels only the older read. usbip-win2 drops the canceled read's
    /// RET_SUBMIT and delivers the other (request_list.cpp cancel and
    /// find_sent_request), so the host has the newer frame, and a real
    /// device behind Linux's usbip-host sends neither frame again
    /// (stub_rx.c stub_recv_cmd_unlink answers an unlink of a completed URB
    /// with status 0 and nothing else). The review's single-cancel case.</summary>
    static void KeyboardSingleCancel(string what, string type, int index)
    {
        using var rig = new HandoffRig(KeyboardCopy($"s43-{type}-single-cancel", type), index);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        uint b = rig.SubmitIn(ep, 64);
        rig.Frame(61);
        var ra = rig.Next();
        rig.Frame(62);
        var rb = rig.Next();
        int ua = rig.Unlink(a);
        // The host has frame 62 and lost 61. Its next read takes the next
        // frame, and nothing it already had or lost comes before it.
        uint c = rig.SubmitIn(ep, 64);
        rig.Frame(63);
        var rc = rig.NextWithin(Hang);
        rig.SubmitIn(ep, 64);
        var after = rc.Data != null ? rig.UntilSentinel() : new();
        Check($"{what} whose host cancels only the older of two answered reads gets no frame again, and the next read the next frame",
              ra.IsFrame(a, 61) && rb.IsFrame(b, 62) && ua == 0 && rc.IsFrame(c, 63) && after.Count == 0,
              $"reads {ra} and {rb}, unlink {ua}, next read {rc}, then {Describe(after)}");
    }

    /// <summary>An 80-byte bulk reply over two parked reads, 64 bytes and 16,
    /// whose host cancels only the read with the first part and keeps the
    /// second. The 64 bytes are lost, as a real device's would be, and no
    /// part goes out again. The review's single-cancel case.</summary>
    static void BulkSingleCancel()
    {
        var j = JsonNode.Parse(CatalogJson("switch2-pro-controller-composite"))!.AsObject();
        j["id"] = "s43-bulk-single-cancel";
        using var rig = new HandoffRig(ControllerProfile.Parse(Encoding.UTF8.GetBytes(j.ToJsonString()))!, HandoffIndex + 28);
        var p = rig.Device.Protocol!.Protocol;
        uint a = rig.SubmitIn(p.ReplyEndpoint, 64);
        uint b = rig.SubmitIn(p.ReplyEndpoint, 64);
        // 02/01 reads 64 bytes of the flash image from 0x13000: an 80-byte
        // reply.
        byte[] flashRead = Convert.FromHexString("02910001000000000000000000300100");
        uint cmd = rig.SubmitOut(p.CommandEndpoint, flashRead);
        var got = rig.UntilSentinel();
        int ua = rig.Unlink(a);
        uint c = rig.SubmitIn(p.ReplyEndpoint, 64);
        var rc = rig.NextWithin(1000);
        Check("a bulk reply whose host cancels only the read with its first part sends no part again",
              got.Count == 3 && got[0].Is(cmd, flashRead.Length) && got[1].Is(a, 64) && got[2].Is(b, 16) && ua == 0 && rc.Data == null,
              $"{Describe(got)}, unlink {ua}, the next read {(rc.Data == null ? "parked" : rc.ToString())}");
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

    /// <summary>A report on change answered on a read the host then unlinks
    /// is lost: while the state holds no tick sends it again, as a real
    /// device that reports on change sends nothing, and the next change goes
    /// to the next read.</summary>
    static void LayoutChangeLost()
    {
        using var rig = new HandoffRig(ChangeLayoutCopy("s43-change-report-lost"), HandoffIndex + 22);
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
        // The tick runs on this thread, and anything it sends is on the
        // host's end of the connection when it returns.
        clock.TickNow();
        bool quiet = rig.Available == 0;
        // Button B down: byte 0 of the state block.
        var block = rig.Device.Protocol!.CopyStateBlock();
        block[0] ^= 0x01;
        rig.Device.Protocol!.SetStateBlock(block);
        clock.TickNow();
        var rb = rig.NextWithin(Hang);
        Check("a report on change the host never got is not sent again while the state holds, and the next change goes to the next read",
              ack.Is(on, 8) && ticked && ra.Is(a, 64) && ra.Data[0] == 0x09 && ua == 0 && quiet
              && rb.Is(b, 64) && !rb.Data.AsSpan().SequenceEqual(ra.Data),
              $"reports on {ack}, ticked {ticked}, first read {ra}, unlink {ua}, nothing sent while the state held {quiet}, next read {rb}");
    }

    /// <summary>A reply the poll clock answered a read with, which the host
    /// then unlinks, is lost: the next poll has nothing to send.</summary>
    static void ProtocolPacketsLost()
    {
        using var rig = new HandoffRig(PollClockCopy("s43-poll-clock-lost"), HandoffIndex + 23);
        var p = rig.Device.Protocol!.Protocol;
        var (clock, _) = rig.HoldPollClock();
        uint a = rig.SubmitIn(p.InputEndpoint, 64);
        uint cmd = rig.SubmitOut(p.CommandEndpoint, Convert.FromHexString("1091000100000000"));
        var ack = rig.Next();
        bool ticked = clock.TickNow();
        var ra = ticked ? rig.Next() : default;
        int ua = rig.Unlink(a);
        rig.SubmitIn(p.InputEndpoint, 64);
        bool again = clock.TickNow();
        bool quiet = rig.Available == 0;
        Check("a reply on the input endpoint answered on a read the host then unlinks is not sent again",
              ack.Is(cmd, 8) && ticked && ra.Is(a, 20) && ra.Data[0] == 0x10 && ua == 0 && !again && quiet,
              $"command answered {ack}, first read {ra}, unlink {ua}, ticked again {again}, nothing sent {quiet}");
    }

    /// <summary>The first 64 bytes of an 80-byte bulk reply, answered on a
    /// read the host then unlinks, are lost, and the next read gets the last
    /// 16.</summary>
    static void BulkReplyLost()
    {
        var j = JsonNode.Parse(CatalogJson("switch2-pro-controller-composite"))!.AsObject();
        j["id"] = "s43-bulk-reply-lost";
        using var rig = new HandoffRig(ControllerProfile.Parse(Encoding.UTF8.GetBytes(j.ToJsonString()))!, HandoffIndex + 24);
        var p = rig.Device.Protocol!.Protocol;
        uint a = rig.SubmitIn(p.ReplyEndpoint, 64);
        // 02/01 reads 64 bytes of the flash image from 0x13000.
        byte[] flashRead = Convert.FromHexString("02910001000000000000000000300100");
        uint cmd = rig.SubmitOut(p.CommandEndpoint, flashRead);
        var got = rig.UntilSentinel();
        int ua = rig.Unlink(a);
        var rest = rig.ReadEach(p.ReplyEndpoint, 2);
        Check("the part of a bulk reply answered on a read the host then unlinks is not sent again, and the next read gets the rest",
              got.Count == 2 && got[0].Is(cmd, flashRead.Length) && got[1].Is(a, 64) && ua == 0
              && rest.Count == 1 && rest[0].Length == 16,
              $"{Describe(got)}, unlink {ua}, next reads {Describe(rest)}");
    }

    // How long each stress runs, and the most frames it writes.
    const int StressMs = 3_000, StressFrames = 60_000;

    /// <summary>The host's side of a stress: reads it keeps out, reads it
    /// canceled, and every answer and unlink answer a thread of its own takes
    /// from the connection, slowed at random. A cancel follows usbip-win2: the
    /// read completes at once, a CMD_UNLINK goes out, and an answer that comes
    /// for the read after is dropped (request_list.cpp cancel and
    /// find_sent_request). A RET_UNLINK with status 0 must follow its read's
    /// answer, and a read the device unlinked while parked must get none.</summary>
    sealed class StressHost
    {
        public readonly object Lock = new();
        public readonly HashSet<uint> Outstanding = new(), Canceled = new(), Answered = new(), ResetReads = new();
        public readonly Dictionary<uint, uint> UnlinkOf = new();
        public int Unknown, AfterReset, Early, AnsweredUnlinks, ParkedUnlinks;
        public Exception? ReceiveError;
        public bool Stopping;
        readonly HandoffRig _rig;
        readonly Random _rng;
        readonly Thread _receiver;

        /// <summary>Starts the receiver, which hands each answer to an
        /// outstanding read to <paramref name="delivered"/> and each answer to a
        /// canceled read to <paramref name="dropped"/>, under the lock.</summary>
        public StressHost(HandoffRig rig, int seed, Action<HandoffRig.Message> delivered, Action<HandoffRig.Message> dropped)
        {
            _rig = rig;
            _rng = new Random(seed);
            _receiver = new Thread(() =>
            {
                var r = new Random(seed + 2);
                try
                {
                    while (true)
                    {
                        // A host takes a while to see each answer.
                        if (r.Next(3) == 0) Spin(r.Next(0, 120));
                        var m = rig.Next();
                        lock (Lock)
                        {
                            if (m.Kind == UsbipProtocol.RetUnlink)
                            {
                                if (!UnlinkOf.Remove(m.Seq, out uint v)) Unknown++;
                                else if (m.Status == 0)
                                {
                                    AnsweredUnlinks++;
                                    // Status 0 says the read's RET_SUBMIT came first.
                                    if (!Answered.Contains(v)) Early++;
                                }
                                else
                                {
                                    ParkedUnlinks++;
                                    ResetReads.Add(v);
                                    if (Answered.Contains(v)) AfterReset++;
                                }
                            }
                            else if (Outstanding.Contains(m.Seq) || Canceled.Contains(m.Seq))
                            {
                                Answered.Add(m.Seq);
                                if (ResetReads.Contains(m.Seq)) AfterReset++;
                                if (Canceled.Remove(m.Seq)) dropped(m);
                                else
                                {
                                    Outstanding.Remove(m.Seq);
                                    delivered(m);
                                }
                            }
                            else if (!Commands.Remove(m.Seq)) Unknown++;
                            Monitor.PulseAll(Lock);
                        }
                    }
                }
                catch (Exception) when (Volatile.Read(ref Stopping)) { }
                catch (Exception e) { ReceiveError = e; }
            }) { IsBackground = true, Name = "S43 stress receiver" };
            _receiver.Start();
        }

        /// <summary>OUT transfers whose answer the receiver takes and drops.</summary>
        public readonly HashSet<uint> Commands = new();

        /// <summary>Two reads out on <paramref name="endpoint"/>, as HID class
        /// keeps its two ping-pong reads (IOCTL_HID_READ_REPORT remarks, Oney's
        /// Programming the Microsoft Windows Driver Model, HIDCLASS
        /// minidrivers).</summary>
        public void EnsureTwo(byte endpoint)
        {
            while (true)
            {
                uint s;
                lock (Lock)
                {
                    if (Outstanding.Count >= 2) return;
                    s = _rig.NextSeq();
                    Outstanding.Add(s);
                }
                _rig.SubmitIn(s, endpoint, 64);
            }
        }

        /// <summary>Cancels one outstanding read at random, as a host with a
        /// timeout per read does.</summary>
        public void CancelOne()
        {
            uint u, v;
            lock (Lock)
            {
                if (Outstanding.Count == 0) return;
                v = Outstanding.ElementAt(_rng.Next(Outstanding.Count));
                Outstanding.Remove(v);
                Canceled.Add(v);
                u = _rig.NextSeq();
                UnlinkOf[u] = v;
            }
            _rig.SendUnlink(u, v);
        }

        /// <summary>Cancels every outstanding read, one at a time, as HID class
        /// cancels its ping-pong reads before a power-down: each with its own
        /// IoCancelIrp, which usbip-win2 turns into a CMD_UNLINK of that read
        /// alone, while the receiver takes the answers on their way. A read
        /// whose answer arrives first is no longer outstanding, and is not
        /// canceled.</summary>
        public void CancelEach()
        {
            while (true)
            {
                lock (Lock) if (Outstanding.Count == 0) return;
                CancelOne();
                if (_rng.Next(2) == 0) Spin(_rng.Next(0, 60));
            }
        }

        public void Stop()
        {
            Volatile.Write(ref Stopping, true);
        }

        public void Join() => _receiver.Join(Hang);

        public string Faults() => $"unlinks ahead of the answer {Early}, answers to a read unlinked while parked {AfterReset}, unknown {Unknown}"
                                  + (ReceiveError != null ? ", " + ReceiveError.Message : "");

        public bool Clean => Early == 0 && AfterReset == 0 && Unknown == 0 && ReceiveError == null;
    }

    /// <summary>A device's frames under the cancels a host makes, for
    /// StressMs. The host keeps two reads out and cancels one at random while
    /// a thread of its own writes frames in bursts of one to six, each with
    /// its number. Each burst ends with every read canceled, one at a time,
    /// a moment after its last frame, then two new reads. The host never
    /// takes a frame again or out of order: an answer it lost to a cancel
    /// stays lost, as a real device's does behind Linux's usbip-host
    /// (stub_rx.c stub_recv_cmd_unlink). A keyboard or mouse delivers every
    /// other frame, so its writer waits for the input pump to read each frame
    /// from the section, which holds one, before it writes the next. A game
    /// pad holds its newest frame, and its burst's last state reaches the
    /// host through the idle repeat a stream sends (HMController's StreamTick,
    /// every catalog USB/IP pad's stream, idle frames or clock), which the
    /// stress writes once the device is idle.</summary>
    static void Stress(string what, ControllerProfile profile, int index, bool every, int seed)
    {
        var rig = new HandoffRig(profile, index);
        StressHost? host = null;
        Thread? writer = null;
        var go = new SemaphoreSlim(0);
        int burst = 0;
        try
        {
            byte ep = rig.PrimaryIn;
            var rng = new Random(seed);
            // Each report by its number and its repeat, in the order the host
            // took them, and the numbers of those it lost to a cancel.
            var delivered = new List<(int Mark, int Repeat)>();
            var dropped = new List<int>();
            static (int Mark, int Repeat) Mark(HandoffRig.Message m) =>
                m.Data.Length == 64 ? (m.Data[1] | m.Data[2] << 8, m.Data[3]) : (-1, -1);
            host = new StressHost(rig, seed, m => delivered.Add(Mark(m)), m => dropped.Add(Mark(m).Mark));
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

            int bursts = 0, repeated = 0, stuck = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < StressMs && Volatile.Read(ref written) < StressFrames && host.ReceiveError == null)
            {
                bursts++;
                host.EnsureTwo(ep);
                Volatile.Write(ref burst, rng.Next(1, 7));
                go.Release();
                while (!burstDone.Wait(0))
                {
                    if (rng.Next(5) == 0) host.CancelOne();
                    host.EnsureTwo(ep);
                    Spin(rng.Next(0, 80));
                }
                int last = Volatile.Read(ref written);
                // A host that stops reading: its reads canceled a moment after
                // the last frame, then two new reads.
                Spin(rng.Next(0, 60));
                host.CancelEach();
                host.EnsureTwo(ep);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool settled = false;
                int repeat = 0;
                while (sw.ElapsedMilliseconds < Hang)
                {
                    host.EnsureTwo(ep);
                    lock (host.Lock)
                    {
                        // A keyboard's or mouse's every frame went to the host or to
                        // a read it canceled. A game pad's newest state reached it.
                        settled = every ? delivered.Count + dropped.Count >= last
                                        : delivered.Count > 0 && delivered[^1].Mark == last;
                        if (settled) break;
                    }
                    // Both reads parked, nothing held, and the frame numbered last
                    // read from the section: the game pad's last state was lost to
                    // a cancel, and its stream sends it again.
                    if (!every && rig.IdleWithParked(2, last))
                    {
                        if (repeat == 0) repeated++;
                        rig.FrameMarked((ushort)last, (byte)++repeat);
                    }
                    lock (host.Lock) Monitor.Wait(host.Lock, 1);
                }
                if (!settled) stuck++;
            }
            Volatile.Write(ref burst, -1);
            go.Release();
            writer.Join(Hang);

            int n = Volatile.Read(ref written), breakAt = -1;
            lock (host.Lock)
            {
                // No report twice and none older than one before it.
                for (int i = 1; i < delivered.Count && breakAt < 0; i++)
                    if (delivered[i].CompareTo(delivered[i - 1]) <= 0) breakAt = i;
                // A keyboard's or mouse's every frame once: taken by the host or
                // lost to a read it canceled.
                bool whole = !every || delivered.Select(d => d.Mark).Concat(dropped).OrderBy(x => x).SequenceEqual(Enumerable.Range(1, n));
                Console.WriteLine($"  info: {what}: {bursts} bursts in {clock.ElapsedMilliseconds} ms, {n} frames written, {delivered.Count} reports delivered, " +
                                  $"{dropped.Count} lost to a canceled read, {host.AnsweredUnlinks} unlinks of answered reads, {host.ParkedUnlinks} of parked reads" +
                                  (every ? "" : $", {repeated} last states sent again by the stream"));
                string where = breakAt < 0 ? "" : $", first break at {breakAt}: {delivered[breakAt - 1]} then {delivered[breakAt]}";
                Check($"{what}: the host gets no report twice and no state older than one it has, under single and repeated cancels",
                      breakAt < 0 && Volatile.Read(ref taken), $"{delivered.Count} delivered{where}{(Volatile.Read(ref taken) ? "" : ", a frame the pump did not read in time")}");
                Check(every ? $"{what}: every frame reaches the host or is lost with a read the host canceled, and no other"
                            : $"{what}: every burst's last state reaches the host, through the stream's repeat when a cancel lost it",
                      whole && stuck == 0, $"{(every ? $"frames accounted for {whole}, " : "")}{stuck} of {bursts} bursts never settled");
                Check($"{what}: a RET_UNLINK with status 0 follows its read's answer, and a read unlinked while parked gets none",
                      host.Clean, host.Faults());
            }
        }
        finally
        {
            // No frame is written to the section once the rig frees it, and
            // the receiver's read of the closed connection ends it.
            Volatile.Write(ref burst, -1);
            go.Release();
            writer?.Join(Hang);
            host?.Stop();
            rig.Dispose();
            host?.Join();
        }
    }

    /// <summary>An 80-byte bulk reply, 64 bytes and 16, for each of a run of
    /// flash reads, under single cancels of the two bulk reads the host keeps
    /// out, for StressMs. A part answered on a read the host canceled is lost,
    /// as a real device's is, and every part goes on the wire once, in order:
    /// each 64-byte part echoes its command's address, which rises by 0x40
    /// from one command to the next.</summary>
    static void BulkStress(int index, int seed)
    {
        var j = JsonNode.Parse(CatalogJson("switch2-pro-controller-composite"))!.AsObject();
        j["id"] = "s43-bulk-stress";
        var rig = new HandoffRig(ControllerProfile.Parse(Encoding.UTF8.GetBytes(j.ToJsonString()))!, index);
        StressHost? host = null;
        try
        {
            var p = rig.Device.Protocol!.Protocol;
            var rng = new Random(seed);
            // Every part on the wire in order, delivered or dropped, with the
            // parts the host kept.
            var wire = new List<(int Length, uint Address)>();
            int kept = 0;
            static (int, uint) Part(HandoffRig.Message m) =>
                (m.Length, m.Length == 64 ? BinaryPrimitives.ReadUInt32LittleEndian(m.Data.AsSpan(12)) : 0u);
            host = new StressHost(rig, seed, m => { wire.Add(Part(m)); kept++; }, m => wire.Add(Part(m)));
            // 02/01 reads 64 bytes of the flash image from the address in bytes
            // 12 to 15.
            byte[] flashRead = Convert.FromHexString("02910001000000000000000000300100");
            int commands = 0, stuck = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < StressMs && host.ReceiveError == null)
            {
                host.EnsureTwo(p.ReplyEndpoint);
                BinaryPrimitives.WriteUInt32LittleEndian(flashRead.AsSpan(12), 0x13000u + 0x40u * (uint)commands);
                uint cmd;
                lock (host.Lock)
                {
                    cmd = rig.NextSeq();
                    host.Commands.Add(cmd);
                }
                rig.SubmitOut(cmd, p.CommandEndpoint, flashRead);
                commands++;
                // Both parts on the wire before the next command, the host
                // canceling one of its reads at random meanwhile.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool both = false;
                while (sw.ElapsedMilliseconds < Hang)
                {
                    // A part sent twice ends the wait too, and the check below
                    // names it.
                    lock (host.Lock) both = wire.Count >= 2 * commands;
                    if (both) break;
                    if (rng.Next(4) == 0) host.CancelOne();
                    host.EnsureTwo(p.ReplyEndpoint);
                    Spin(rng.Next(0, 80));
                    lock (host.Lock) Monitor.Wait(host.Lock, 1);
                }
                if (!both) { stuck++; break; }
            }
            int breakAt = -1;
            lock (host.Lock)
            {
                for (int i = 0; i < wire.Count && breakAt < 0; i++)
                {
                    var (length, address) = wire[i];
                    bool first = i % 2 == 0;
                    if (first ? length != 64 || address != 0x13000u + 0x40u * (uint)(i / 2) : length != 16) breakAt = i;
                }
                Console.WriteLine($"  info: a split bulk reply: {commands} commands in {clock.ElapsedMilliseconds} ms, {wire.Count} parts on the wire, {kept} kept by the host, " +
                                  $"{host.AnsweredUnlinks} unlinks of answered reads, {host.ParkedUnlinks} of parked reads");
                string where = breakAt < 0 ? "" : $", first break at part {breakAt}: {wire[breakAt]}";
                Check("a split bulk reply: every part goes on the wire once, in order, and a part answered on a read the host canceled is not sent again",
                      breakAt < 0 && wire.Count == 2 * commands && stuck == 0, $"{wire.Count} parts for {commands} commands{where}, {stuck} stuck");
                Check("a split bulk reply: a RET_UNLINK with status 0 follows its read's answer, and a read unlinked while parked gets none",
                      host.Clean, host.Faults());
            }
        }
        finally
        {
            host?.Stop();
            rig.Dispose();
            host?.Join();
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
    /// first, so it follows the pump's answer, and the frame is lost with it,
    /// as for any answered read the host unlinks.</summary>
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
        rig.Frame(62);
        var rb = rig.NextWithin(Hang);
        rig.SubmitIn(ep, 64);
        var after = rb.IsFrame(b, 62) ? rig.UntilSentinel() : new();
        Check("a read the input pump answers, unlinked before the answer is on the wire, is answered before the unlink, and its frame is not sent again",
              waiting && ended && first.IsFrame(a, 61) && second.Kind == UsbipProtocol.RetUnlink && second.Seq == u && second.Status == 0
              && rb.IsFrame(b, 62) && after.Count == 0,
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
        /// bytes, low byte first, and <paramref name="repeat"/> in the third:
        /// the same state sent again, as a stream's idle repeat sends it with
        /// a newer timestamp. One thread writes the frames at a time.</summary>
        public void FrameMarked(ushort mark, byte repeat = 0)
        {
            var data = new byte[63];
            data[0] = (byte)mark;
            data[1] = (byte)(mark >> 8);
            data[2] = repeat;
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
        /// parked on the primary endpoint, no frame queued, and the frame
        /// numbered <paramref name="last"/> in its input report slot. The pump
        /// writes the slot and answers or queues the frame in one hold of the
        /// HID lock, so that frame has gone to a read.</summary>
        public bool IdleWithParked(int reads, int last)
        {
            var t = typeof(UsbipEmulatedDevice);
            object Field(string name) => t.GetField(name, Private)!.GetValue(Device)!;
            lock (HidLock)
            {
                if (((System.Collections.ICollection)Field("_frameQueue")).Count != 0) return false;
                if (((System.Collections.ICollection)Field("_primaryPending")).Count != reads) return false;
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

        /// <summary>The same with seqnum <paramref name="seq"/>, which a
        /// receiver on another thread knows first.</summary>
        public void SubmitOut(uint seq, byte endpoint, byte[] data) =>
            Device.HandleSubmit(Command(seq, endpoint, false, data.Length), data, ReadOnlySpan<(uint, uint)>.Empty);

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
