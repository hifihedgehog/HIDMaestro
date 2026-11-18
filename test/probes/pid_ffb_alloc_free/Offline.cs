#nullable enable
// "offline": a USB/IP device's PID block over the USB/IP wire, with no
// kernel and no Windows device. The device is built on process memory,
// registered with the SDK's own USB/IP server, and imported by a client on
// loopback that sends what HidUsb sends for HidD_GetFeature, HidD_SetFeature,
// HidD_SetOutputReport, HidD_GetInputReport and WriteFile: GET_REPORT and
// SET_REPORT on the control pipe, and an interrupt OUT transfer. Each answer
// is the one the UMDF2 driver gives the same request (driver.c, the
// IOCTL_UMDF_HID_GET_FEATURE, _SET_FEATURE, _SET_OUTPUT_REPORT,
// _GET_INPUT_REPORT and IOCTL_HID_WRITE_REPORT cases), worked out by hand
// here from that code, with a STALL where the driver fails the request.
// Then the candidate report IDs against driver.c BuildPidCandidates on
// descriptors made for each of its rules.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static class Offline
{
    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    // Well clear of any index a live program's lease hands out.
    const int Index = 5200;

    public static int Run()
    {
        Console.WriteLine("=== PID block of a USB/IP device, over the wire (offline) ===");
        Candidates();
        using (var ctx = new HMContext())
        {
            ctx.LoadDefaultProfiles();
            Served(ctx);
            Wire();
        }
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    // ── Which devices serve the block ───────────────────────────────────

    static void Served(HMContext ctx)
    {
        Console.WriteLine("\n-- Which devices serve the PID block --");
        // An authored persona serves it only for a descriptor with the PID
        // page, which none of the catalog's has.
        var composite = ctx.GetProfile("dualsense-composite")!.Inner;
        using (var d = new UsbipEmulatedDevice(composite, Index))
            Check("an authored persona without the PID page (dualsense-composite) serves none", !d.ServesPid);
        SharedMemoryIO.DestroyController(Index);
        // A derived device serves it by the driver's rule, on any descriptor.
        var sidewinder = ctx.GetProfile("sidewinder-force-feedback-2")!.Inner;
        using (var d = new UsbipEmulatedDevice(sidewinder, Index))
            Check("a derived device (sidewinder-force-feedback-2) serves it", d.ServesPid && d.Derived);
        SharedMemoryIO.DestroyController(Index);
    }

    // ── The wire ────────────────────────────────────────────────────────

    static void Wire()
    {
        Console.WriteLine("\n-- GET_REPORT and SET_REPORT through the USB/IP server --");
        var profile = new HMProfileBuilder().Id("pid-offline").Name("PID Offline").Vendor("HIDMaestro")
            .Vid(0xBEEF).Pid(0xF00E).ProductString("HIDMaestro PID Offline").ManufacturerString("HIDMaestro")
            .Type("joystick").Connection("usb")
            .FromDescriptorBuilder(new HidDescriptorBuilder().Joystick().AddStick("Left", 16).AddButtons(8).AddPidFfbBlock())
            .Build().Inner;
        // The server imports only for the host controller, and for a
        // connection this process owns once a check says so.
        UsbipServer.AdmitImportsFromThisProcess = true;
        var server = UsbipServer.GetOrStart();
        var device = new UsbipEmulatedDevice(profile, Index);
        server.Register(device);
        try
        {
            using var cl = new Client(server.Port, Index);
            cl.SendImport(device.BusId);
            var op = cl.ReadExactly(8);
            cl.ReadExactly(312);
            Check("the import is accepted", BinaryPrimitives.ReadUInt32BigEndian(op.AsSpan(4)) == 0);
            byte outEp = 0;
            foreach (var kv in device.Descriptors.Endpoints)
                if (kv.Value.TransferType == 3 && !kv.Value.IsIn) outEp = kv.Value.Address;
            Check("the derived device has an interrupt OUT endpoint", outEp != 0, $"0x{outEp:X2}");

            // Before the program makes the section: the driver's
            // EnsurePidStateMapping fails.
            var g = cl.GetFeature(0x13, 5);
            Check("no section: GET Pool stalls (driver: STATUS_NO_SUCH_DEVICE)", g.Status == -UsbipProtocol.EPipe, Ret(g));
            Check("and counts as a refused Pool read", device.PidPoolRefusals == 1, $"{device.PidPoolRefusals}");
            g = cl.GetFeature(0x12, 5);
            Check("no section: GET Block Load stalls (driver: STATUS_NOT_SUPPORTED)", g.Status == -UsbipProtocol.EPipe, Ret(g));
            Check("and is no refused Pool read", device.PidPoolRefusals == 1, $"{device.PidPoolRefusals}");
            g = cl.GetFeature(0x11, 5);
            Check("no section: GET Create New Effect succeeds with nothing written", g.Status == 0 && g.ActualLength == 0, Ret(g));
            g = cl.ControlOut(0x21, 0x09, 0x0311, 0, new byte[] { 0x11, 0x01, 0x00, 0x00 });
            Check("no section: SET Create New Effect is taken", g.Status == 0, Ret(g));

            // The section as the controller makes it on the first PublishPid*
            // call (HMController.EnsurePidStateViewLocked), PidEnabled 0.
            IntPtr view = SharedMemoryIO.EnsurePidStateMapping(Index);
            var rids = PidReportIdExtractor.Extract(profile.GetDescriptorBytes());
            SharedMemoryIO.WritePidReportIds(view, rids.PoolReportId, rids.StateReportId, rids.BlockLoadReportId,
                rids.CreateNewEffectReportId, rids.BlockFreeReportId, rids.DeviceControlReportId);
            uint seq = 0;
            g = cl.GetFeature(0x13, 5);
            Check("section, no Pool published: GET Pool stalls", g.Status == -UsbipProtocol.EPipe, Ret(g));
            Check("the second refused Pool read", device.PidPoolRefusals == 2, $"{device.PidPoolRefusals}");

            SharedMemoryIO.WritePidPool(view, ref seq, 0x0400, 2, deviceManagedPool: true, sharedParameterBlocks: false);
            g = cl.GetFeature(0x13, 5);
            Check("GET Pool: 13 00 04 02 01", Hex(g) == "1300040201", Ret(g));
            g = cl.GetFeature(0x13, 4);
            Check("GET Pool in a 4-byte request stalls (driver: STATUS_BUFFER_TOO_SMALL)", g.Status == -UsbipProtocol.EPipe, Ret(g));
            g = cl.GetFeature(0x12, 5);
            Check("GET Block Load before any Create New Effect: Error (3), RAM 0", Hex(g) == "1200030000", Ret(g));

            g = cl.ControlOut(0x21, 0x09, 0x0311, 0, new byte[] { 0x11, 0x01, 0x00, 0x00 });
            g = cl.GetFeature(0x12, 5);
            Check("SET Create New Effect, then GET Block Load: EBI 1, Success, RAM (0x400 / 2) * 1", Hex(g) == "1201010002", Ret(g));
            Check("bitmap 0x1, count 1", Bitmap(view) == (1u, 1u), Show(view));
            cl.ControlOut(0x21, 0x09, 0x0311, 0, new byte[] { 0x11, 0x01, 0x00, 0x00 });
            g = cl.GetFeature(0x12, 5);
            Check("a second: EBI 2, Success, RAM 0", Hex(g) == "1202010000", Ret(g));
            cl.ControlOut(0x21, 0x09, 0x0311, 0, new byte[] { 0x11, 0x01, 0x00, 0x00 });
            g = cl.GetFeature(0x12, 5);
            Check("a third past the two simultaneous effects: EBI 0, Full", Hex(g) == "1200020000", Ret(g));
            Check("bitmap 0x3, count 2", Bitmap(view) == (3u, 2u), Show(view));

            // Create New Effect's ID on the output path is Set Effect, which
            // allocates nothing.
            cl.ControlOut(0x21, 0x09, 0x0211, 0, new byte[] { 0x11, 0x01, 0x00, 0x00 });
            Check("SET_REPORT(Output 0x11), Set Effect, allocates nothing", Bitmap(view) == (3u, 2u), Show(view));

            g = cl.ControlOut(0x21, 0x09, 0x021B, 0, new byte[] { 0x1B, 0x01 });
            Check("SET_REPORT(Output Block Free, EBI 1) frees it: bitmap 0x2, count 1", g.Status == 0 && Bitmap(view) == (2u, 1u), Show(view));
            cl.ControlOut(0x21, 0x09, 0x021B, 0, new byte[] { 0x1B, 0x01 });
            Check("a second free of EBI 1 changes nothing", Bitmap(view) == (2u, 1u), Show(view));

            // WriteFile is IOCTL_HID_WRITE_REPORT, which the driver publishes
            // without the PID block, and HidUsb sends it on the OUT endpoint.
            g = cl.InterruptOut(outEp, new byte[] { 0x1B, 0x02 });
            Check("interrupt OUT Block Free, EBI 2 (WriteFile), frees nothing", g.Status == 0 && Bitmap(view) == (2u, 1u), Show(view));
            g = cl.ControlOut(0x21, 0x09, 0x031B, 0, new byte[] { 0x1B, 0x02 });
            Check("SET_REPORT(Feature Block Free, EBI 2) frees it", Bitmap(view) == (0u, 0u), Show(view));

            cl.ControlOut(0x21, 0x09, 0x0311, 0, new byte[] { 0x11, 0x01, 0x00, 0x00 });
            g = cl.ControlOut(0x21, 0x09, 0x021C, 0, new byte[] { 0x1C, 0x03 });
            Check("Device Control 3 (Stop All) resets nothing", Bitmap(view) == (1u, 1u), Show(view));
            g = cl.ControlOut(0x21, 0x09, 0x021C, 0, new byte[] { 0x1C, 0x04 });
            g = cl.GetFeature(0x12, 5);
            Check("Device Control 4 (Reset): bitmap and count 0, Block Load cleared, RAM the pool size",
                  Bitmap(view) == (0u, 0u) && Hex(g) == "1200030004", $"{Show(view)}, {Ret(g)}");

            SharedMemoryIO.WritePidState(view, ref seq, 5, 0x03);
            g = cl.GetFeature(0x14, 3);
            Check("GET State: 14 05 03", Hex(g) == "140503", Ret(g));
            g = cl.GetFeature(0x14, 2);
            Check("GET State in a 2-byte request stalls", g.Status == -UsbipProtocol.EPipe, Ret(g));
            g = cl.ControlIn(0xA1, 0x01, 0x0114, 0, 8);
            Check("GET_REPORT(Input State): 14 05 03 (driver: IOCTL_UMDF_HID_GET_INPUT_REPORT)", Hex(g) == "140503", Ret(g));
            g = cl.GetFeature(0x15, 8);
            Check("GET on an ID no PID report arrives on stalls", g.Status == -UsbipProtocol.EPipe, Ret(g));

            // Feature reads and writes still reach the program's ring.
            Check("Pool reads refused stay at 2 once a Pool is published", device.PidPoolRefusals == 2, $"{device.PidPoolRefusals}");
        }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(Index);
        }
    }

    static (uint Bitmap, uint Count) Bitmap(IntPtr view) =>
        ((uint)Marshal.ReadInt32(view, SharedMemoryIO.PID_OFFSET_EBI_BITMAP), (uint)Marshal.ReadInt32(view, SharedMemoryIO.PID_OFFSET_EBI_COUNT));

    static string Show(IntPtr view)
    {
        var (b, c) = Bitmap(view);
        return $"bitmap 0x{b:X}, count {c}";
    }

    static string Hex(Client.Ret r) => r.Status == 0 ? Convert.ToHexString(r.Data) : $"status {r.Status}";
    static string Ret(Client.Ret r) => r.Status == 0 ? $"[{Convert.ToHexString(r.Data)}] {r.ActualLength} bytes" : $"status {r.Status}";

    // ── Candidates ──────────────────────────────────────────────────────

    static void Candidates()
    {
        Console.WriteLine("\n-- Candidate report IDs (driver.c BuildPidCandidates) --");
        // A gamepad with no PID page and one vendor feature report 0x30.
        byte[] plain = { 0x05, 0x01, 0x09, 0x05, 0xA1, 0x01, 0x85, 0x01, 0x05, 0x09, 0x19, 0x01, 0x29, 0x08,
                         0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02,
                         0x06, 0x00, 0xFF, 0x85, 0x30, 0x09, 0x01, 0x75, 0x08, 0x95, 0x02, 0xB1, 0x02, 0xC0 };
        var c = UsbipEmulatedDevice.PidCandidatesFor(plain, requirePidPage: false)!;
        Check("any descriptor: the canonical six and nothing else",
              Ids(c) == "11 12 13 14 1B 1C", Ids(c));
        Check("without the PID page, an authored device's walk gives none",
              UsbipEmulatedDevice.PidCandidatesFor(plain, requirePidPage: true) == null);

        // PID page, Pool in a Logical Collection on report 0x03, then a
        // Push, Block Load on 0x02, a Pop and a Set Effect report with no
        // report ID of its own: the scoped ID is 0x03 again and the flat one
        // stays 0x02, and both are candidates (driver.c keeps both).
        byte[] vendorIds = {
            0x05, 0x0F, 0x09, 0x92, 0xA1, 0x01,
            0x09, 0x7F, 0xA1, 0x02, 0x85, 0x03, 0x09, 0x80, 0x75, 0x10, 0x95, 0x01, 0xB1, 0x02, 0xC0,
            0xA4, 0x09, 0x89, 0xA1, 0x02, 0x85, 0x02, 0x09, 0x22, 0x75, 0x08, 0x95, 0x01, 0xB1, 0x02, 0xC0, 0xB4,
            0x09, 0x21, 0xA1, 0x02, 0x09, 0x22, 0x75, 0x08, 0x95, 0x01, 0x91, 0x02, 0xC0,
            0xC0 };
        c = UsbipEmulatedDevice.PidCandidatesFor(vendorIds, requirePidPage: true)!;
        Check("PID page with vendor IDs: 0x02 and 0x03 join the canonical six",
              c != null && Ids(c) == "02 03 11 12 13 14 1B 1C", c == null ? "null" : Ids(c));

        // A usage given as four bytes on the PID page counts as the page.
        byte[] longUsage = { 0x05, 0x01, 0x0B, 0x21, 0x00, 0x0F, 0x00, 0xA1, 0x02, 0x85, 0x05, 0x75, 0x08, 0x95, 0x01, 0x91, 0x02, 0xC0 };
        c = UsbipEmulatedDevice.PidCandidatesFor(longUsage, requirePidPage: true)!;
        Check("a four-byte PID usage counts as the page, and its Set Effect report 0x05 is a candidate",
              c != null && c[0x05], c == null ? "null" : Ids(c));

        // A descriptor cut inside an item: every ID is a candidate.
        byte[] cut = { 0x05, 0x0F, 0x09, 0x21, 0xA1, 0x02, 0x85 };
        c = UsbipEmulatedDevice.PidCandidatesFor(cut, requirePidPage: true)!;
        Check("a descriptor the walk cannot finish makes all 256 IDs candidates", c != null && Array.TrueForAll(c, x => x));
    }

    static string Ids(bool[] c)
    {
        var l = new List<string>();
        for (int i = 0; i < 256; i++) if (c[i]) l.Add($"{i:X2}");
        return string.Join(" ", l);
    }

    // ── A minimal vhci-side client ──────────────────────────────────────

    sealed class Client : IDisposable
    {
        readonly TcpClient _tcp;
        readonly NetworkStream _s;
        readonly int _index;
        uint _seq = 100;

        public Client(int port, int index)
        {
            _index = index;
            _tcp = new TcpClient("127.0.0.1", port) { NoDelay = true };
            _s = _tcp.GetStream();
            _s.ReadTimeout = 30000;
        }

        public struct Ret { public int Status, ActualLength; public byte[] Data; }

        public void SendImport(string busid)
        {
            var b = new byte[8 + 32];
            BinaryPrimitives.WriteUInt16BigEndian(b, 0x0111);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), 0x8003);
            System.Text.Encoding.UTF8.GetBytes(busid).CopyTo(b, 8);
            _s.Write(b);
        }

        void SendHeader(uint seqnum, uint direction, uint ep, int bufLen, ulong setup)
        {
            var h = new byte[48];
            BinaryPrimitives.WriteUInt32BigEndian(h, 1);   // CMD_SUBMIT
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), seqnum);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), (1u << 16) | (uint)(_index + 1));
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), direction);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(16), ep);
            BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(24), bufLen);
            BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(32), -1);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(40), setup);
            _s.Write(h);
        }

        static ulong Setup(byte type, byte request, ushort value, ushort index, ushort length) =>
            type | ((ulong)request << 8) | ((ulong)value << 16) | ((ulong)index << 32) | ((ulong)length << 48);

        public Ret GetFeature(byte reportId, ushort length) => ControlIn(0xA1, 0x01, (ushort)(0x0300 | reportId), 0, length);

        public Ret ControlIn(byte type, byte request, ushort value, ushort index, ushort length)
        {
            uint seq = _seq++;
            SendHeader(seq, 1, 0, length, Setup(type, request, value, index, length));
            return Read(seq, isIn: true);
        }

        public Ret ControlOut(byte type, byte request, ushort value, ushort index, byte[] data)
        {
            uint seq = _seq++;
            SendHeader(seq, 0, 0, data.Length, Setup(type, request, value, index, (ushort)data.Length));
            _s.Write(data);
            return Read(seq, isIn: false);
        }

        public Ret InterruptOut(byte endpoint, byte[] data)
        {
            uint seq = _seq++;
            SendHeader(seq, 0, (uint)(endpoint & 0x0F), data.Length, 0);
            _s.Write(data);
            return Read(seq, isIn: false);
        }

        Ret Read(uint want, bool isIn)
        {
            var h = ReadExactly(48);
            uint command = BinaryPrimitives.ReadUInt32BigEndian(h);
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4));
            if (command != 3 || seq != want) throw new InvalidOperationException($"expected RET_SUBMIT {want}, got command {command} seq {seq}");
            var r = new Ret
            {
                Status = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(20)),
                ActualLength = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(24)),
                Data = Array.Empty<byte>(),
            };
            if (isIn && r.ActualLength > 0) r.Data = ReadExactly(r.ActualLength);
            return r;
        }

        public byte[] ReadExactly(int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = _s.Read(b, got, n - got);
                if (r <= 0) throw new IOException("EOF");
                got += r;
            }
            return b;
        }

        public void Dispose()
        {
            try { _tcp.Close(); } catch { }
        }
    }
}
