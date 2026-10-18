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

/// <summary>One OutputDecoded per output report, when a profile's
/// extendedOutputReport and a protocol decoder both name its ID: the codec's
/// decode, as at v1.12.0, where the Switch 2 decode was the last arm of the
/// chain. A create refuses such a profile (ControllerProfile
/// .ValidateProgramModel), so the check holds the chain itself, on a
/// controller built past that refusal: the refusal is checked first. The
/// controller runs offline on process memory, its output thread reading a
/// ring this check writes as the driver's PublishOutput does
/// (driver.c:1252-1261), as device_oracle_check's case 7 does, and its
/// threads and clock are stopped as case 1 stops them, since a controller of
/// a context that never initialized cannot be disposed.</summary>
internal static class OutputOnce
{
    static void Check(string what, bool ok, string detail = "") => Checks.Check(what, ok, detail);
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    public static void Run()
    {
        Console.WriteLine("-- one OutputDecoded per output report");
        var o = JsonNode.Parse(Device.Catalog("switch2-pro-controller-composite"))!.AsObject();
        o["id"] = "output-once-test";
        o["extendedOutputReport"] = JsonNode.Parse("""{ "reportId": "0x02", "size": 33, "fields": [ { "byte": 1, "type": "uint8", "semantic": "first" } ] }""");
        var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()))!;
        string refusal = "loads";
        try { p.ValidateDeviceModel(); }
        catch (InvalidOperationException e) { refusal = e.Message; }
        Check("a create refuses the profile: a decoder on extendedOutputReport's report", refusal.Contains("A report has one decode", StringComparison.Ordinal), refusal);
        const int index = 7950;
        IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
        IntPtr ring = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_OUTPUT_SIZE);
        Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
        Marshal.Copy(new byte[SharedMemoryIO.SHARED_OUTPUT_SIZE], 0, ring, SharedMemoryIO.SHARED_OUTPUT_SIZE);
        Cache("s_inputViews")[index] = view;
        Cache("s_outputViews")[index] = ring;
        SharedMemoryIO.MarkOutputStart(index);
        var ctor = typeof(HMController).GetConstructor(Instance, new[]
        {
            typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string), typeof(UsbipBackendHandle), typeof(string),
        }) ?? throw new MissingMethodException("HMController's private constructor changed.");
        var context = (HMContext)RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
        var c = (HMController)ctor.Invoke(new object?[] { context, index, new HMProfile(p), null, null, null });
        var decoded = new List<string>();
        var done = new SemaphoreSlim(0);
        c.OutputReceived += (_, pkt) => { if (pkt.ReportId == 0xEE) done.Release(); };
        c.OutputDecoded += (_, e) => { lock (decoded) decoded.Add(string.Join(",", e.Fields!.Keys)); };
        uint head = 0;
        var data = new byte[32];
        data[0] = 0x5A;
        Write(ring, ref head, (byte)HMOutputSource.HidOutput, 0x02, data);
        Write(ring, ref head, (byte)HMOutputSource.HidOutput, 0xEE, Array.Empty<byte>());
        bool reached = done.Wait(10_000);
        ((CancellationTokenSource)typeof(HMController).GetField("_outputCts", Instance)!.GetValue(c)!).Cancel();
        ((Thread?)typeof(HMController).GetField("_outputThread", Instance)!.GetValue(c))?.Join(5000);
        if (typeof(HMController).GetField("_idleClock", Instance)?.GetValue(c) is IDeviceClock clock)
        {
            clock.Stop(1000);
            typeof(HMController).GetField("_idleClock", Instance)!.SetValue(c, null);
        }
        SharedMemoryIO.UnregisterOutputPump(index);
        Cache("s_inputViews").Remove(index);
        Cache("s_outputViews").Remove(index);
        Marshal.FreeHGlobal(view);
        Marshal.FreeHGlobal(ring);
        Check("the output thread reads the report", reached);
        Check("an output report both an extendedOutputReport and a decoder name raises OutputDecoded once, from the codec",
            decoded.Count == 1 && decoded[0] == "first", $"{decoded.Count} raised: {string.Join(" | ", decoded)}");
    }

    static void Write(IntPtr ring, ref uint head, byte source, byte id, byte[] data)
    {
        uint seq = head + 1;
        int slot = SharedMemoryIO.OUTPUT_HEADER_SIZE + (int)((seq - 1) % SharedMemoryIO.OUTPUT_RING_SLOTS) * SharedMemoryIO.OUTPUT_SLOT_SIZE;
        Marshal.WriteByte(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SOURCE, source);
        Marshal.WriteByte(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_REPORT_ID, id);
        Marshal.WriteInt16(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SIZE, (short)data.Length);
        if (data.Length > 0) Marshal.Copy(data, 0, ring + slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_DATA, data.Length);
        Thread.MemoryBarrier();
        Marshal.WriteInt32(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SEQNO, (int)seq);
        Thread.MemoryBarrier();
        Marshal.WriteInt32(ring, 0, (int)seq);
        head = seq;
    }

    static Dictionary<int, IntPtr> Cache(string field) =>
        (Dictionary<int, IntPtr>)typeof(SharedMemoryIO).GetField(field, Static)!.GetValue(null)!;
}
