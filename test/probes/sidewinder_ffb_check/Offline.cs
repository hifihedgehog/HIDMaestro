#nullable enable
// SideWinderFfbCheck offline: a USB/IP device's PID block over the USB/IP wire, with no
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
            InterruptIn();
            ExtendedLengths(ctx);
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

            // Requests HID class never sends, answered as the driver answers
            // them: an empty SET_FEATURE is report 0 there, and a GET for no
            // bytes fails STATUS_BUFFER_TOO_SMALL.
            g = cl.ControlOut(0x21, 0x09, 0x0311, 0, Array.Empty<byte>());
            Check("an empty SET_REPORT(Feature) on Create New Effect's ID allocates nothing", Bitmap(view) == (0u, 0u), Show(view));
            g = cl.GetFeature(0x11, 0);
            Check("GET Create New Effect for no bytes stalls", g.Status == -UsbipProtocol.EPipe, Ret(g));

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

            Check("Pool reads refused stay at 2 once a Pool is published", device.PidPoolRefusals == 2, $"{device.PidPoolRefusals}");

            // Both remake the section: the view above is gone after them.
            SeqLock(cl, rids);
            LateRequest(cl, rids);
        }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(Index);
        }
    }

    // ── SharedMemoryIO's own tables, for a teardown played in process ───

    const System.Reflection.BindingFlags AnyStatic =
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
    static readonly Type Sm = typeof(SharedMemoryIO);
    static object InputLock => Sm.GetField("s_inputViews", AnyStatic)!.GetValue(null)!;
    static Dictionary<int, long> AckGenerations => (Dictionary<int, long>)Sm.GetField("s_inputAckGenerations", AnyStatic)!.GetValue(null)!;

    /// <summary>The section released as DestroyController releases it, then
    /// made again as the controller's first PublishPid* call makes it, with
    /// a Pool published on <paramref name="seq"/>, the program's counter.</summary>
    static IntPtr RemakeSection(PidReportIdExtractor.PidReportIds rids, ref uint seq)
    {
        var release = Sm.GetMethod("ReleasePidStateLocked", AnyStatic)!;
        lock (SharedMemoryIO.PidStateLock) release.Invoke(null, new object[] { Index });
        IntPtr view = SharedMemoryIO.EnsurePidStateMapping(Index);
        SharedMemoryIO.WritePidReportIds(view, rids.PoolReportId, rids.StateReportId, rids.BlockLoadReportId,
            rids.CreateNewEffectReportId, rids.BlockFreeReportId, rids.DeviceControlReportId);
        SharedMemoryIO.WritePidPool(view, ref seq, 0x0400, 2, deviceManagedPool: true, sharedParameterBlocks: false);
        return view;
    }

    static uint SeqNo(IntPtr view) => (uint)Marshal.ReadInt32(view, SharedMemoryIO.PID_OFFSET_SEQNO);

    /// <summary>The program's writers and the device's share the section's
    /// SeqNo. The program publishes a Pool, the host creates an effect, and
    /// the program publishes a State: a reader that looks at the SeqNo
    /// before and after that State write must see it move, or it takes the
    /// half-written State as whole (driver.c ReadPidState).</summary>
    static void SeqLock(Client cl, PidReportIdExtractor.PidReportIds rids)
    {
        Console.WriteLine("\n-- One SeqNo for the program's writes and the device's --");
        uint seq = 0;
        IntPtr view = RemakeSection(rids, ref seq);
        cl.ControlOut(0x21, 0x09, 0x0311, 0, new byte[] { 0x11, 0x01, 0x00, 0x00 });
        uint before = SeqNo(view);
        uint seq1 = SeqNo(view);
        Check("positive control: with no write between them, the reader's two looks agree", seq1 == SeqNo(view));

        // WritePidState split where a reader on another thread or in the
        // driver can look: the odd SeqNo, half the fields, the even one.
        var begin = Sm.GetMethod("PidStateBeginWrite", AnyStatic)!;
        var end = Sm.GetMethod("PidStateEndWrite", AnyStatic)!;
        var args = new object[] { view, seq };
        lock (SharedMemoryIO.PidStateLock) begin.Invoke(null, args);
        uint during = SeqNo(view);
        Marshal.WriteByte(view, SharedMemoryIO.PID_OFFSET_STATE_EBI, 7);
        lock (SharedMemoryIO.PidStateLock) end.Invoke(null, args);
        uint after = SeqNo(view);
        Check("the SeqNo never moves backward: the program's write starts past the device's",
              during > before && after > during, $"{before} -> {during} -> {after}");
        Check("the write in progress is odd and the finished one even", (during & 1) == 1 && (after & 1) == 0, $"{during}, {after}");
        Check("a reader that looked before the write and after it sees the SeqNo move and reads again",
              seq1 != after, $"before {seq1}, after {after}");
    }

    /// <summary>A request on the connection's thread of a device whose
    /// controller is torn down, after this process made the index again: it
    /// must not reach the new controller's section. The teardown and the
    /// re-create are played on SharedMemoryIO's own tables, so the device's
    /// own input and output views stay mapped.</summary>
    static void LateRequest(Client cl, PidReportIdExtractor.PidReportIds rids)
    {
        Console.WriteLine("\n-- A late request after the index is made again --");
        long own;
        lock (InputLock) own = AckGenerations[Index];
        var last = Sm.GetField("s_lastAckGeneration", AnyStatic)!;
        lock (InputLock)
        {
            long next = (long)last.GetValue(null)! + 1;
            last.SetValue(null, next);
            AckGenerations[Index] = next;
        }
        uint seq = 0;
        IntPtr view = RemakeSection(rids, ref seq);
        try
        {
            var g = cl.GetFeature(0x13, 5);
            Check("GET Pool from the torn-down device does not read the new controller's Pool", g.Status == -UsbipProtocol.EPipe, Ret(g));
            cl.ControlOut(0x21, 0x09, 0x0311, 0, new byte[] { 0x11, 0x01, 0x00, 0x00 });
            Check("its SET Create New Effect allocates nothing there", Bitmap(view) == (0u, 0u), Show(view));
            g = cl.GetFeature(0x12, 5);
            Check("its GET Block Load stalls as with no section", g.Status == -UsbipProtocol.EPipe, Ret(g));
        }
        finally
        {
            lock (InputLock) AckGenerations[Index] = own;
        }
        // Positive control, same section, same connection: with the
        // device's own generation back, the same requests reach it.
        var p = cl.GetFeature(0x13, 5);
        Check("positive control: with its generation current, GET Pool reads 13 00 04 02 01", Hex(p) == "1300040201", Ret(p));
        cl.ControlOut(0x21, 0x09, 0x0311, 0, new byte[] { 0x11, 0x01, 0x00, 0x00 });
        Check("and SET Create New Effect allocates EBI 1", Bitmap(view) == (1u, 1u), Show(view));
    }

    // ── Interrupt IN answers ────────────────────────────────────────────

    /// <summary>An interrupt IN read gets at most the bytes its URB asked
    /// for, and an extended frame goes out only at its report's declared
    /// length, as driver.c ExtendedFrameDeclared lets one out. Any other is
    /// not sent and the last report stands, for the reads and for
    /// GET_REPORT(Input).</summary>
    static void InterruptIn()
    {
        Console.WriteLine("\n-- Interrupt IN answers and the URB's length --");
        const int index = Index + 1;
        var profile = new HMProfileBuilder().Id("in-offline").Name("IN Offline").Vendor("HIDMaestro")
            .Vid(0xBEEF).Pid(0xF00D).ProductString("HIDMaestro IN Offline").ManufacturerString("HIDMaestro")
            .Type("joystick").Connection("usb")
            .FromDescriptorBuilder(new HidDescriptorBuilder().Joystick().AddStick("Left", 16).AddButtons(8).AddPidFfbBlock())
            .Build().Inner;
        var server = UsbipServer.GetOrStart();
        var device = new UsbipEmulatedDevice(profile, index);
        server.Register(device);
        try
        {
            using var cl = new Client(server.Port, index);
            cl.SendImport(device.BusId);
            var op = cl.ReadExactly(8);
            cl.ReadExactly(312);
            Check("the import is accepted", BinaryPrimitives.ReadUInt32BigEndian(op.AsSpan(4)) == 0);
            byte inEp = 0;
            foreach (var kv in device.Descriptors.Endpoints)
                if (kv.Value.TransferType == 3 && kv.Value.IsIn) inEp = kv.Value.Address;
            var (id, length, _) = DeviceOrchestrator.DriverInputReport(profile);
            int declared = 0;
            foreach (var r in profile.GetOrBuildReportBuilder().InputReports)
                if (r.Id == id) declared = r.ByteSize;
            Check("the input report's declared length is the one the device sends", declared == length && length > 3,
                  $"report 0x{id:X2}, {length} bytes, declared {declared}");

            IntPtr view = SharedMemoryIO.EnsureInputMapping(index);
            IntPtr ev = SharedMemoryIO.GetInputEvent(index);
            uint seq = 0;
            int off = id != 0 ? 1 : 0;
            void Data(byte fill)
            {
                var d = new byte[length - off];
                Array.Fill(d, fill);
                SharedMemoryIO.WriteInputFrame(view, ev, ref seq, d, d.Length, null);
            }
            void Extended(byte fill, int size)
            {
                var x = new byte[size];
                Array.Fill(x, fill);
                if (id != 0) x[0] = id;
                SharedMemoryIO.WriteInputFrame(view, ev, ref seq, default, 0, null, 0, x, x.Length);
            }

            uint read = cl.SubmitIn(inEp, 64);
            Data(0x11);
            var g = cl.Read(read);
            Check($"positive control: a read for 64 bytes gets the {length}-byte report", g.Status == 0 && g.ActualLength == length, Ret(g));
            read = cl.SubmitIn(inEp, 3);
            Data(0x22);
            g = cl.Read(read);
            Check("a parked read for 3 bytes gets 3 of the next report", g.Status == 0 && g.ActualLength == 3, Ret(g));
            Data(0x33);
            System.Threading.Thread.Sleep(100);   // the frame queued with no read parked
            read = cl.SubmitIn(inEp, 2);
            g = cl.Read(read);
            Check("a read for 2 bytes that finds the report queued gets 2", g.Status == 0 && g.ActualLength == 2, Ret(g));

            read = cl.SubmitIn(inEp, 64);
            Extended(0x44, length + 3);
            var late = cl.TryRead(read, 300);
            Check($"an extended frame of {length + 3} bytes, not the declared {length}, is not sent", late == null,
                  late is { } l ? Ret(l) : "");
            var got = cl.ControlIn(0xA1, 0x01, (ushort)(0x0100 | id), 0, 64);
            Check("and GET_REPORT(Input) still answers the last report sent", got.Status == 0 && got.ActualLength == length
                  && got.Data[length - 1] == 0x33, Ret(got));
            Extended(0x55, length);
            g = late == null ? cl.Read(read) : cl.Read(cl.SubmitIn(inEp, 64));
            Check("positive control: one at the declared length goes to the parked read", g.Status == 0 && g.ActualLength == length
                  && g.Data[length - 1] == 0x55, Ret(g));
        }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(index);
        }
    }

    /// <summary>Every catalog profile whose SubmitState fills an extended
    /// report: its USB/IP device sends that report at the size SubmitState
    /// fills it, and refuses it three bytes longer where the device has a
    /// declared length to hold it to. A profile that builds its reports from
    /// a state block fills each of those instead, at its own size, when the
    /// program's protocol host builds them. The device and the sizes are the
    /// profile's form on USB/IP, which a create presents there
    /// (HMContext.CreateUsbipController builds the device from it): the
    /// Bluetooth HID form or the GIP form of a profile that reaches XInput
    /// through xinputhid, and the profile itself otherwise.</summary>
    static void ExtendedLengths(HMContext ctx)
    {
        Console.WriteLine("\n-- Catalog extended reports on the USB/IP device --");
        const int index = Index + 2;
        int count = 0, held = 0;
        var off = new List<string>();
        foreach (var p in ctx.AllProfiles)
        {
            if (!p.IsDeployable || !p.SupportsBackend(HMBackend.Usbip)) continue;
            var inner = p.FormOn(HMBackend.Usbip).Inner;
            var sizes = new List<(byte Id, int Size)>();
            if (inner.InputReports is { Count: > 0 } built)
                foreach (var r in built) sizes.Add((r.ReportId != null ? ReportIds.Parse(r.ReportId) : (byte)0, r.Size));
            else if (inner.ExtendedReport is { Size: > 0 } ext)
                sizes.Add((ext.ReportIdByte, ext.Size));
            if (sizes.Count == 0) continue;
            count++;
            try
            {
                using var d = new UsbipEmulatedDevice(inner, index);
                bool refuses = false;
                foreach (var (id, size) in sizes)
                {
                    if (!d.SendsExtendedFrame(id, size)) off.Add($"{Named(p, inner)} 0x{id:X2} {size}");
                    if (!d.SendsExtendedFrame(id, size + 3)) refuses = true;
                }
                if (refuses) held++;
            }
            catch (Exception e) { off.Add($"{Named(p, inner)}: {e.GetType().Name} {e.Message}"); }
            finally { SharedMemoryIO.DestroyController(index); }
        }
        Check($"each of the {count} profiles with an extended report sends it at the size SubmitState fills",
              count > 0 && off.Count == 0, string.Join(", ", off));
        Check($"and {held} of them refuse it three bytes longer", held > 0, $"{held}");
    }

    /// <summary>A profile's id, and the form it presents on USB/IP when that
    /// is another profile.</summary>
    static string Named(HMProfile p, ControllerProfile form) => form.Id == p.Id ? p.Id : $"{p.Id} as {form.Id}";

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

        /// <summary>An interrupt IN read sent, its answer left for
        /// <see cref="Read(uint)"/> or <see cref="TryRead"/>.</summary>
        public uint SubmitIn(byte endpoint, int length)
        {
            uint seq = _seq++;
            SendHeader(seq, 1, (uint)(endpoint & 0x0F), length, 0);
            return seq;
        }

        public Ret Read(uint want) => Read(want, isIn: true);

        /// <summary>The answer to <paramref name="want"/>, or null when none
        /// arrives within <paramref name="ms"/>.</summary>
        public Ret? TryRead(uint want, int ms) =>
            _tcp.Client.Poll(ms * 1000, SelectMode.SelectRead) ? Read(want, isIn: true) : null;

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
