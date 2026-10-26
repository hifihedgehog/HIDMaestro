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
    /// that comes meanwhile finds nothing queued, since the newer frame is
    /// taken up only once the frame put back is on the wire, and gets the
    /// newer frame after it.</summary>
    static void PutBackAheadOfQueue(ControllerProfile ds)
    {
        using var rig = new HandoffRig(ds, HandoffIndex + 11);
        byte ep = rig.PrimaryIn;
        uint a = rig.SubmitIn(ep, 64);
        uint b = rig.SubmitIn(ep, 64);
        rig.Frame(14);
        var ra = rig.Next();
        int ua;
        uint d;
        bool taken;
        lock (rig.SendLock)
        {
            lock (rig.LifetimeLock)
            {
                ua = rig.Unlink(a);
                rig.Frame(15);
            }
            taken = WaitUntil(() => rig.ParkedReads == 0, Hang);
            d = rig.SubmitIn(ep, 64);
        }
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

    /// <summary>A copy of the Switch 2 Pro composite whose input endpoint
    /// sends the SDK's report on the host's poll clock, with replies on that
    /// endpoint and commands on its bulk OUT endpoint, which the protocol
    /// compiler accepts.</summary>
    static void BulkCommandWakesPollClock()
    {
        var j = JsonNode.Parse(CatalogJson("switch2-pro-controller-composite"))!.AsObject();
        j["id"] = "s43-poll-clock-bulk-commands";
        // The state block goes with the layouts, which only a device that
        // builds its reports has.
        j.Remove("inputReports");
        j.Remove("stateBlock");
        var protocol = j["protocol"]!.AsObject();
        protocol["input"]!.AsObject().Remove("clock");
        protocol.Remove("getReport");
        protocol["replies"] = new JsonObject { ["channel"] = "input", ["pending"] = 64, ["overflow"] = "dropOldestUnstarted" };
        var profile = ControllerProfile.Parse(Encoding.UTF8.GetBytes(j.ToJsonString()))!;
        using var rig = new HandoffRig(profile, HandoffIndex + 7);
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
        readonly HashSet<uint> _in = new();
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

        UsbipProtocol.CommandHeader Command(byte endpoint, bool isIn, int length) => new()
        {
            Command = UsbipProtocol.CmdSubmit,
            Seqnum = ++_seq,
            Direction = isIn ? 1u : 0u,
            Ep = (uint)(endpoint & 0x0F),
            TransferBufferLength = length,
            NumberOfPackets = UsbipProtocol.NumberOfPacketsNonIsoch,
        };

        /// <summary>An IN transfer, interrupt or bulk as the endpoint is.</summary>
        public uint SubmitIn(byte endpoint, int length)
        {
            var cmd = Command(endpoint, true, length);
            _in.Add(cmd.Seqnum);
            Device.HandleSubmit(cmd, ReadOnlySpan<byte>.Empty, ReadOnlySpan<(uint, uint)>.Empty);
            return cmd.Seqnum;
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
            _in.Add(cmd.Seqnum);
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

        /// <summary>The next RET_SUBMIT or RET_UNLINK on the wire.</summary>
        public Message Next()
        {
            ReadExactly(_header);
            uint kind = BinaryPrimitives.ReadUInt32BigEndian(_header);
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4));
            int status = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(20));
            int actual = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(24));
            // An OUT transfer's RET_SUBMIT carries its length and no data.
            var data = new byte[kind == UsbipProtocol.RetSubmit && _in.Contains(seq) ? actual : 0];
            ReadExactly(data);
            return new Message(kind, seq, status, kind == UsbipProtocol.RetSubmit && !_in.Contains(seq) ? actual : data.Length, data);
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
