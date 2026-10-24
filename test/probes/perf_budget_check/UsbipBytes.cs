// usbip-bytes [frames]: what each catalog USB/IP profile puts on the wire,
// hashed, for two trees' outputs to compare line by line, as submit-bytes
// does for the input section. Each profile's device runs in this process
// with this probe in the host controller's place, as the usbip mode drives
// one, and is never plugged in, so no profile, a keyboard's or a mouse's
// included, makes a device Windows sees. Per profile, one line each for:
// the control replies (the device and configuration descriptors, each HID
// interface's report descriptor, GET_STATUS, GET_IDLE and GET_REPORT of each
// declared feature report), input frames of seeded random data on the data
// lane and the extended lane in turn ([frames], 200 unless given, each read
// on the input endpoint and then with GET_REPORT(Input), for a device the
// input pump answers, and up to 60 read back with GET_REPORT(Input) for a
// device with a protocol), and five microphone URBs of seeded PCM for a
// profile with a microphone. A hash covers each reply's status and data, never the
// frame number an isochronous reply carries.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    static void UsbipBytes(int frames)
    {
        var db = ((ProfileDatabase)typeof(ProfileDatabase).GetMethod("LoadEmbeddedFresh", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!
            .Invoke(null, null)!).All;
        var server = UsbipServer.GetOrStart();
        foreach (var p in db.Where(x => x.RequiresUsbipBackend).OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            UsbipEmulatedDevice device;
            try { device = new UsbipEmulatedDevice(p, UsbipIndex); }
            catch (Exception e) { Console.WriteLine($"usbip-bytes,{p.Id},skip {e.GetType().Name}"); continue; }
            server.Register(device);
            try
            {
                var config = device.Descriptors.ConfigurationDescriptor;
                using var w = new UsbipWire(server.Port, device.BusId);
                var rng = new Random(Seed(p.Id));

                using (var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    void Control(byte type, byte request, ushort value, ushort index, ushort length)
                    {
                        int status = w.WaitFor(w.SubmitControlIn(type, request, value, index, length));
                        h.AppendData(BitConverter.GetBytes(status));
                        if (status == 0) h.AppendData(w.LastData);
                    }
                    Control(0x80, 0x06, 0x0100, 0, 18);
                    Control(0x80, 0x06, 0x0200, 0, 4096);
                    Control(0x80, 0x00, 0, 0, 2);
                    foreach (byte iface in HidInterfaceNumbers(config))
                    {
                        Control(0xA1, 0x02, 0, iface, 1);
                        int status = w.WaitFor(w.SubmitControlIn(0x81, 0x06, 0x2200, iface, 4096));
                        h.AppendData(BitConverter.GetBytes(status));
                        if (status != 0) continue;
                        var descriptor = w.LastData.ToArray();
                        h.AppendData(descriptor);
                        foreach (var (id, length) in DeclaredReports(descriptor, ReportKind.Feature))
                            Control(0xA1, 0x01, (ushort)(0x0300 | id), iface, (ushort)length);
                    }
                    Console.WriteLine($"usbip-bytes,{p.Id},control,{Convert.ToHexString(h.GetHashAndReset())}");
                }

                // Input frames. The input pump reads every one, whatever the
                // device does with it after.
                {
                    byte ep = InputEndpoint(device);
                    bool pumpAnswers = device.Protocol == null && ep > 0;
                    using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    IntPtr view = SharedMemoryIO.EnsureInputMapping(UsbipIndex);
                    IntPtr evt = SharedMemoryIO.GetInputEvent(UsbipIndex);
                    uint seqNo = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(view, 0);
                    var data = new byte[SharedMemoryIO.DATA_CAPACITY];
                    var ext = new byte[SharedMemoryIO.EXTENDED_DATA_CAPACITY];
                    var inputIds = DeclaredReports(device.Descriptors.GetHidDescriptor(0x22, device.Descriptors.HidInterfaceNumber) ?? Array.Empty<byte>(), ReportKind.Input);
                    byte firstInput = device.Protocol is { } engine && engine.Protocol.ReportId != 0 ? engine.Protocol.ReportId
                        : inputIds.Count > 0 ? inputIds.Keys.First() : (byte)0;
                    ushort iface = device.Descriptors.HidInterfaceNumber;
                    // A device with a protocol answers its IN endpoint on its own
                    // clock, so its frame is read back through GET_REPORT(Input)
                    // once the answer changes, or after 2 s when the frame does
                    // not change it. Either way the pump has read the frame by
                    // the last request.
                    byte[] last = Array.Empty<byte>();
                    int lastStatus = w.WaitFor(w.SubmitControlIn(0xA1, 0x01, (ushort)(0x0100 | firstInput), iface, 1024));
                    last = w.LastData.ToArray();
                    int frameCount = pumpAnswers ? frames : Math.Min(frames, 60);
                    for (int i = 0; i < frameCount; i++)
                    {
                        uint seq = pumpAnswers ? w.SubmitIn((uint)(ep & 0x0F), 1024) : 0;
                        if ((i & 1) == 0)
                        {
                            int len = rng.Next(0, data.Length + 1);
                            rng.NextBytes(data);
                            SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, data, len, null);
                        }
                        else
                        {
                            int len = rng.Next(1, ext.Length + 1);
                            rng.NextBytes(ext);
                            SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, Array.Empty<byte>(), 0, null,
                                dataOffset: 0, extendedData: ext, extendedLen: len);
                        }
                        int status;
                        if (pumpAnswers)
                        {
                            status = w.WaitFor(seq);
                            h.AppendData(BitConverter.GetBytes(status));
                            h.AppendData(w.LastData);
                        }
                        else
                        {
                            var waited = System.Diagnostics.Stopwatch.StartNew();
                            while (true)
                            {
                                status = w.WaitFor(w.SubmitControlIn(0xA1, 0x01, (ushort)(0x0100 | firstInput), iface, 1024));
                                if (status != lastStatus || !w.LastData.SequenceEqual(last) || waited.ElapsedMilliseconds > 2000) break;
                                System.Threading.Thread.Sleep(1);
                            }
                        }
                        status = w.WaitFor(w.SubmitControlIn(0xA1, 0x01, (ushort)(0x0100 | firstInput), iface, 1024));
                        h.AppendData(BitConverter.GetBytes(status));
                        if (status == 0) h.AppendData(w.LastData);
                        lastStatus = status;
                        last = w.LastData.ToArray();
                    }
                    Console.WriteLine($"usbip-bytes,{p.Id},frames {frameCount}, report id 0x{firstInput:X2}{(pumpAnswers ? "" : ", protocol")},{Convert.ToHexString(h.GetHashAndReset())}");
                }

                // The microphone, for a profile with one.
                if (MicEndpoint(device) is byte mic and > 0)
                {
                    using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var pcm = new byte[1000];
                    for (int i = 0; i < 5; i++)
                    {
                        rng.NextBytes(pcm);
                        device.Audio.SubmitMicSamples(pcm.AsSpan(0, rng.Next(1, pcm.Length + 1)));
                        int status = w.WaitFor(w.SubmitIsoIn((uint)(mic & 0x0F), 10, 196));
                        h.AppendData(BitConverter.GetBytes(status));
                        h.AppendData(w.LastData);
                    }
                    Console.WriteLine($"usbip-bytes,{p.Id},mic,{Convert.ToHexString(h.GetHashAndReset())}");
                }
            }
            finally
            {
                server.Unregister(device);
                device.Dispose();
                SharedMemoryIO.DestroyController(UsbipIndex);
            }
        }
    }

    static int Seed(string id)
    {
        uint hash = 2166136261;
        foreach (char c in id) hash = (hash ^ c) * 16777619;
        return (int)hash;
    }

    static byte InputEndpoint(UsbipEmulatedDevice device)
    {
        foreach (var e in device.Descriptors.Endpoints.Values)
            if (e.TransferType == 3 && e.IsIn && e.InterfaceNumber == device.Descriptors.HidInterfaceNumber) return e.Address;
        return 0;
    }

    static byte MicEndpoint(UsbipEmulatedDevice device)
    {
        foreach (var e in device.Descriptors.Endpoints.Values)
            if (e.TransferType == 1 && e.IsIn) return e.Address;
        return 0;
    }
}
