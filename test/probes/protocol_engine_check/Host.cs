using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

/// <summary>The SDK's controller hosting a UMDF2 device's protocol, offline:
/// the controller runs on process memory, its output thread reading a ring
/// this check writes as the driver's PublishOutput does (driver.c:1252-1261),
/// and its frames land in an input section of process memory, where the
/// check reads them as the driver's worker would. No device is created.</summary>
internal static class Host
{
    static void Check(string what, bool ok, string detail = "") => Checks.Check(what, ok, detail);
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    sealed class Offline : IDisposable
    {
        public readonly HMController C;
        public readonly IntPtr View, Ring;
        readonly int _index;
        uint _head;
        public readonly List<(byte Id, byte[] Data)> Received = new();

        public Offline(string json, int index)
        {
            _index = index;
            var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(json))!;
            p.ValidateDeviceModel();
            View = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
            Ring = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_OUTPUT_SIZE);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, View, SharedMemoryIO.SHARED_INPUT_SIZE);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_OUTPUT_SIZE], 0, Ring, SharedMemoryIO.SHARED_OUTPUT_SIZE);
            Cache("s_inputViews")[index] = View;
            Cache("s_outputViews")[index] = Ring;
            SharedMemoryIO.MarkOutputStart(index);
            var ctor = typeof(HMController).GetConstructor(Instance, new[]
            {
                typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string), typeof(UsbipBackendHandle), typeof(string),
            }) ?? throw new MissingMethodException("HMController's private constructor changed.");
            var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
            C = (HMController)ctor.Invoke(new object?[] { context, index, new HMProfile(p), null, null, null });
            C.OutputReceived += (_, pkt) => { lock (Received) Received.Add((pkt.ReportId, pkt.Data.ToArray())); };
        }

        /// <summary>An output report into the ring, then a sentinel the
        /// check waits for, so the reader has handled the report.</summary>
        public void Output(byte id, byte[] data)
        {
            Write(id, data);
            Write(0xEE, Array.Empty<byte>());
            var deadline = Environment.TickCount64 + 10_000;
            while (Environment.TickCount64 < deadline)
            {
                lock (Received) if (Received.Exists(r => r.Id == 0xEE)) { Received.RemoveAll(r => r.Id == 0xEE); return; }
                Thread.Sleep(1);
            }
            throw new TimeoutException("the output reader did not reach the sentinel");
        }

        void Write(byte id, byte[] data)
        {
            uint seq = _head + 1;
            int slot = SharedMemoryIO.OUTPUT_HEADER_SIZE + (int)((seq - 1) % SharedMemoryIO.OUTPUT_RING_SLOTS) * SharedMemoryIO.OUTPUT_SLOT_SIZE;
            Marshal.WriteByte(Ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SOURCE, (byte)HMOutputSource.HidOutput);
            Marshal.WriteByte(Ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_REPORT_ID, id);
            Marshal.WriteInt16(Ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SIZE, (short)data.Length);
            if (data.Length > 0) Marshal.Copy(data, 0, Ring + slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_DATA, data.Length);
            Thread.MemoryBarrier();
            Marshal.WriteInt32(Ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SEQNO, (int)seq);
            Thread.MemoryBarrier();
            Marshal.WriteInt32(Ring, 0, (int)seq);
            _head = seq;
        }

        /// <summary>The section's sequence number, data lane and extended lane.</summary>
        public (uint Seq, byte[] Data, byte[] Ext) Frame()
        {
            uint seq = (uint)Marshal.ReadInt32(View, 0);
            int dataLen = Marshal.ReadInt32(View, 4);
            int extLen = Marshal.ReadInt32(View, SharedMemoryIO.EXTENDED_SIZE_OFFSET);
            var d = new byte[Math.Clamp(dataLen, 0, SharedMemoryIO.DATA_CAPACITY)];
            var e = new byte[Math.Clamp(extLen, 0, SharedMemoryIO.EXTENDED_DATA_CAPACITY)];
            Marshal.Copy(View + SharedMemoryIO.DATA_OFFSET, d, 0, d.Length);
            Marshal.Copy(View + SharedMemoryIO.EXTENDED_DATA_OFFSET, e, 0, e.Length);
            return (seq, d, e);
        }

        /// <summary>The controller's threads stopped as Dispose stops them,
        /// without the context's bookkeeping, which an offline controller
        /// has none of.</summary>
        public void Dispose()
        {
            ((CancellationTokenSource)typeof(HMController).GetField("_outputCts", Instance)!.GetValue(C)!).Cancel();
            ((Thread?)typeof(HMController).GetField("_outputThread", Instance)!.GetValue(C))?.Join(5000);
            ((IDisposable?)typeof(HMController).GetField("_host", Instance)?.GetValue(C))?.Dispose();
            Cache("s_inputViews").Remove(_index);
            Cache("s_outputViews").Remove(_index);
            Marshal.FreeHGlobal(View);
            Marshal.FreeHGlobal(Ring);
        }
    }

    static Dictionary<int, IntPtr> Cache(string field) =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, Static)!.GetValue(null)!;

    public static void Run()
    {
        Console.WriteLine("-- the SDK's controller hosts a UMDF2 device's protocol");
        Subcommands();
        SubmitAndLiveFeature();
    }

    static void Subcommands()
    {
        // The layout goes out on the clock while a register no rule sets
        // holds, so the replies are the only frames.
        string json = Umdf2.Profile(o =>
        {
            o["protocol"]!["state"]!.AsArray().Add(JsonNode.Parse("""{ "name": "streaming" }"""));
            o["inputReports"]![0]!["while"] = "streaming";
            var rules = o["protocol"]!["commands"]!["rules"]!.AsArray();
            rules.Add(JsonNode.Parse("""{ "match": "11", "consume": true }"""));
        });
        using var dev = new Offline(json, 7960);
        var before = dev.Frame();
        var cmd = new byte[48];
        cmd[9] = 0x48;
        dev.Output(0x01, cmd);
        var after = dev.Frame();
        Check("a subcommand's reply reaches the input section's extended lane, 49 bytes as input report 0x21",
            after.Seq != before.Seq && after.Ext.Length == 49 && after.Ext[0] == 0x21 && after.Ext[1] == 0x48 && after.Data.Length == 0,
            $"seq {before.Seq} to {after.Seq}, extended {Checks.Hex(after.Ext)}");
        bool raised;
        lock (dev.Received) raised = dev.Received.Exists(r => r.Id == 0x01 && r.Data.Length == 48);
        Check("the subcommand is raised on OutputReceived as written", raised);
        dev.Output(0x11, new byte[48]);
        lock (dev.Received) raised = dev.Received.Exists(r => r.Id == 0x11);
        Check("a command its rule consumes is not raised", !raised);
        Check("the host's engine is the controller's DeviceProtocol", dev.C.DeviceProtocol != null);
        var s = new HMGamepadState { Buttons = (HMButton)1 };
        uint seq = dev.Frame().Seq;
        dev.C.SubmitState(in s);
        Check("SubmitState on a device whose layouts go out on the clock writes no frame", dev.Frame().Seq == seq);
        var host = typeof(HMController).GetField("_host", Instance)?.GetValue(dev.C);
        Thread? Clock() => (Thread?)host?.GetType().GetField("_clock", Instance)?.GetValue(host);
        Check("the constructor starts no clock: a controller on process memory runs none", host != null && Clock() == null, host == null ? "no host" : "");
        typeof(HMController).GetMethod("StartProtocol", Instance)?.Invoke(dev.C, null);
        var clock = Clock();
        Check("StartProtocol, which the create calls, starts the clock above normal priority",
            clock is { IsAlive: true, Priority: ThreadPriority.AboveNormal }, clock == null ? "no clock" : $"{clock.Name} {clock.Priority}");
    }

    static void SubmitAndLiveFeature()
    {
        string json = Umdf2.Folding("""[ { "match": "02", "minLength": 48, "write": [ { "memory": "body", "address": { "value": 0 }, "from": "3-47" } ], "emitOutput": { "reportId": "0x01", "size": 49, "memory": "body" } } ]""");
        var o = JsonNode.Parse(json)!.AsObject();
        o["liveFeatureReports"] = JsonNode.Parse("""[ { "reportId": "0x00", "size": 6, "program": [ { "const": { "0": "003F" } }, { "copy": "0-1", "to": 2 } ] } ]""");
        using var dev = new Offline(o.ToJsonString(), 7961);
        uint before = dev.Frame().Seq;
        typeof(HMController).GetMethod("StartProtocol", Instance)?.Invoke(dev.C, null);
        var rest = dev.Frame();
        Check("StartProtocol sends the state at rest: the submit layout and the live feature report, before any SubmitState",
            rest.Seq != before && rest.Ext.Length == 12 && rest.Data.Length == 6 && rest.Data[1] == 0x3F,
            $"seq {before} to {rest.Seq}, data {Checks.Hex(rest.Data)}, extended {Checks.Hex(rest.Ext)}");
        var s = new HMGamepadState { Buttons = (HMButton)3 };
        dev.C.SubmitState(in s);
        var f = dev.Frame();
        Check("SubmitState sends a submit layout on the extended lane and the live feature report on the data lane, in one frame",
            f.Ext.Length == 12 && f.Ext[0] == 3 && f.Data.Length == 6 && f.Data[0] == 0x00 && f.Data[1] == 0x3F && f.Data[2] == 3,
            $"data {Checks.Hex(f.Data)}, extended {Checks.Hex(f.Ext)}");
        var cmd = new byte[47];
        for (int i = 2; i < 47; i++) cmd[i] = (byte)i;
        dev.Output(0x02, cmd);
        (byte Id, byte[] Data) got;
        lock (dev.Received) got = dev.Received.Find(r => r.Id == 0x01);
        Check("a command whose rule emits a report raises that report in its place",
            got.Data != null && got.Data.Length == 48 && got.Data[0] == 2 && got.Data[44] == 46,
            got.Data == null ? "none" : $"0x{got.Id:X2} {Checks.Hex(got.Data)}");
    }
}
