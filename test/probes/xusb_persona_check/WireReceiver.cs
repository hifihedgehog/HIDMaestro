// The wire part's receiver half: xbox-360-wireless as the Xbox 360
// Wireless Receiver for Windows, 045E:0719, with its pad on the first slot.
using System;
using System.Buffers.Binary;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static unsafe partial class Program
{
    static void WireReceiver()
    {
        Console.WriteLine("\n=== wire: a USB/IP client as the host, xbox-360-wireless ===");
        var p = ProfileDatabase.LoadEmbedded().All.FirstOrDefault(x => x.Id == "xbox-360-wireless");
        if (p == null) { Check("the catalog holds the profile", false); return; }
        var rx = XusbFunction.Model.Functions.Single(f => f.Name == "receiver");
        const int At = Index + 1;
        UsbipServer.AdmitImportsFromThisProcess = true;
        var server = UsbipServer.GetOrStart();
        UsbipEmulatedDevice device;
        try { device = new UsbipEmulatedDevice(p, At); }
        catch (Exception e) { Check("the device is built (needs elevation, for the shared sections)", false, e.Message); return; }
        server.Register(device);
        try
        {
            using var cl = new Client(server.Port, At);
            cl.SendImport(device.BusId);
            var op = cl.ReadExactly(8);
            var imported = cl.ReadExactly(312);
            Check("the import: full speed, 045E:0719, bcdDevice 0x0100, class FF/FF/FF, eight interfaces",
                  BinaryPrimitives.ReadUInt32BigEndian(op.AsSpan(4)) == 0 && BinaryPrimitives.ReadUInt32BigEndian(imported.AsSpan(296)) == 2
                  && imported.AsSpan(300, 12).SequenceEqual(new byte[] { 0x04, 0x5E, 0x07, 0x19, 0x01, 0x00, 0xFF, 0xFF, 0xFF, 0x01, 0x01, 0x08 }),
                  Hex(imported.AsSpan(296, 16)));
            var dd = cl.ControlIn(0x80, 0x06, 0x0100, 0, 18);
            Check("GET_DESCRIPTOR(Device): the retail receiver's", dd.Status == 0 && dd.Data.AsSpan().SequenceEqual(ReceiverDevice), Hex(dd.Data));
            var whole = cl.ControlIn(0x80, 0x06, 0x0200, 0, 512);
            Check("GET_DESCRIPTOR(Configuration): all 321 bytes of the retail receiver's",
                  whole.Status == 0 && whole.Data.AsSpan().SequenceEqual(ReceiverConfiguration), $"{whole.Data.Length} bytes");
            Check("string 1 is ©Microsoft, string 2 the receiver's name",
                  Text(cl.ControlIn(0x80, 0x06, 0x0301, 0x0409, 255)) == "©Microsoft"
                  && Text(cl.ControlIn(0x80, 0x06, 0x0302, 0x0409, 255)) == "Xbox 360 Wireless Receiver for Windows");
            string serial = Text(cl.ControlIn(0x80, 0x06, 0x0303, 0x0409, 255));
            Check("string 3 is the controller's own serial", serial == DeviceIdentity.ForIndex(At).SyntheticSerial, serial);
            Check("string 0xEE stalls", cl.ControlIn(0x80, 0x06, 0x03EE, 0, 18).Status != 0);
            var status = cl.ControlIn(0x80, 0x00, 0, 0, 2);
            Check("GET_STATUS reads bus-powered, as bmAttributes 0xA0 says", status.Status == 0 && status.Data.AsSpan().SequenceEqual(new byte[] { 0, 0 }), Hex(status.Data));

            Console.WriteLine("\n-- the first slot announces its pad on its first reads --");
            var link = XusbFunction.Bytes(rx.StartMessages[1], serial, XusbFunction.SubType(p));
            Check("SET_CONFIGURATION(1) acks", cl.ControlOut(0x00, 0x09, 1, 0, Array.Empty<byte>()).Status == 0);
            var first = ReadIn(cl, 1);
            var second = ReadIn(cl, 1);
            Check("the first read on 0x81 is 08 80, a data device attached", first.AsSpan().SequenceEqual(new byte[] { 0x08, 0x80 }), Hex(first));
            Check("the next is the link control packet: the capture's bytes, the serial's last eight hex digits at byte 10, subtype 1 at byte 25",
                  second.Length == 29 && second.AsSpan().SequenceEqual(link) && second[10] == Convert.ToByte(serial[^8..^6], 16) && second[25] == 0x01,
                  Hex(second));

            Console.WriteLine("\n-- connection requests --");
            byte[] request = { 0x08, 0x00, 0x0F, 0xC0, 0, 0, 0, 0, 0, 0, 0, 0 };
            Out(cl, 3, request);
            Check("a connection request on slot 2 (OUT 0x03) is answered 08 00 on 0x83: nothing attached", ReadIn(cl, 3).AsSpan().SequenceEqual(new byte[] { 0x08, 0x00 }));
            Out(cl, 7, request);
            Check("and on slot 4 (OUT 0x07) on 0x87", ReadIn(cl, 7).AsSpan().SequenceEqual(new byte[] { 0x08, 0x00 }));
            Out(cl, 1, request);
            var again1 = ReadIn(cl, 1);
            var again2 = ReadIn(cl, 1);
            Check("on slot 1 (OUT 0x01) it is answered 08 80 and the link control packet again",
                  again1.AsSpan().SequenceEqual(new byte[] { 0x08, 0x80 }) && again2.AsSpan().SequenceEqual(link), $"{Hex(again1)} {Hex(again2)}");
            uint parked = cl.NextSeq();
            cl.SendSubmitInterruptIn(parked, 2, 32);
            Out(cl, 2, new byte[] { 0x08, 0x01 });
            Check("a voice interface's write is taken and answered with nothing", cl.Within(200) == "nothing");

            Console.WriteLine("\n-- the pad's input, wrapped --");
            IntPtr view = SharedMemoryIO.EnsureInputMapping(At);
            IntPtr evt = SharedMemoryIO.GetInputEvent(At);
            uint seqNo = (uint)Marshal.ReadInt32(view, 0);
            var built = new byte[29];
            var state = new HMGamepadState { Buttons = HMButton.A | HMButton.LeftBumper | HMButton.Guide, Hat = HMHat.North };
            VendorBlobCodec.EncodeInput(rx.Report, in state, 1f, 0f, 0f, 1f, 0.5f, 1f, built, new VendorBlobCodec.EncoderState());
            byte[] want = Convert.FromHexString("000100F00013011580FFFF7FFF7F008000800000000000000000000000");
            Check("the codec builds the 29-byte data report: 00 01 00 F0, then 00 13 and the wired report's fields", built.AsSpan().SequenceEqual(want), Hex(built));
            uint read = cl.NextSeq();
            cl.SendSubmitInterruptIn(read, 1, 32);
            SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, ReadOnlySpan<byte>.Empty, 0, null, extendedData: built, extendedLen: built.Length);
            var got = cl.ReadRet(read);
            Check("a read on 0x81 gets it, byte for byte", got.Status == 0 && got.Data.AsSpan().SequenceEqual(want), Hex(got.Data));
            Check("a raw frame is 29 bytes on this device", device.RawFrameLength == 29);

            Console.WriteLine("\n-- requests to the pad, as the companion's packets --");
            IntPtr ring = SharedMemoryIO.EnsureOutputMapping(At);
            uint last = (uint)Marshal.ReadInt32(ring, 0);
            var buf = new byte[SharedMemoryIO.DATA_CAPACITY];
            void Packet(string name, byte[] report, byte[]? wantPacket)
            {
                Out(cl, 1, report);
                bool ringed = WaitRing(ring, ref last, buf, out byte source, out byte id, out int size, wantPacket == null ? 300 : 1000);
                bool ok = wantPacket == null ? !ringed : ringed && source == UsbipEmulatedDevice.SourceXInput && id == 0 && buf.AsSpan(0, size).SequenceEqual(wantPacket);
                Check(name, ok, ringed ? $"source {source} id {id:X2} {Hex(buf.AsSpan(0, size))}" : "nothing on the ring");
            }
            Packet("rumble 00 01 0F C0 00 40 80 is XInput 00 00 40 80 02",
                   new byte[] { 0x00, 0x01, 0x0F, 0xC0, 0x00, 0x40, 0x80, 0, 0, 0, 0, 0 }, new byte[] { 0, 0, 0x40, 0x80, 2 });
            Packet("the LED 00 00 08 46 is XInput 00 06 00 00 01",
                   new byte[] { 0x00, 0x00, 0x08, 0x46, 0, 0, 0, 0, 0, 0, 0, 0 }, new byte[] { 0, 6, 0, 0, 1 });
            Packet("power off 00 00 08 C0 is taken and carried nowhere",
                   new byte[] { 0x00, 0x00, 0x08, 0xC0, 0, 0, 0, 0, 0, 0, 0, 0 }, null);
            Packet("a header-only controller request 00 00 00 40 is taken and carried nowhere",
                   new byte[] { 0x00, 0x00, 0x00, 0x40, 0, 0, 0, 0, 0, 0, 0, 0 }, null);
            Packet("a voice device request 01 03 09 01 is taken and carried nowhere",
                   new byte[] { 0x01, 0x03, 0x09, 0x01, 0, 0, 0, 0, 0, 0, 0, 0 }, null);
        }
        catch (Exception e) { Check("the receiver's wire part ran to its end", false, e.ToString()); }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(At);
        }
    }

    /// <summary>One interrupt IN read on endpoint <paramref name="ep"/>,
    /// answered within a second, or no bytes.</summary>
    static byte[] ReadIn(Client cl, uint ep)
    {
        uint seq = cl.NextSeq();
        cl.SendSubmitInterruptIn(seq, ep, 32);
        if (cl.Within(1000) != "data") return Array.Empty<byte>();
        var r = cl.ReadRet(seq);
        return r.Status == 0 ? r.Data : Array.Empty<byte>();
    }

    static void Out(Client cl, uint ep, byte[] data)
    {
        uint w = cl.NextSeq();
        cl.SendSubmitInterruptOut(w, ep, data);
        cl.ReadRet(w);
    }
}
