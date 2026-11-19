// xusb_persona_check: the wired Xbox 360 pad as a USB device on the USB/IP
// backend (profile xbox-360-wired-full), which has no HID interface. Windows
// binds its inbox XUSB driver, xusb22.sys, to the device, and the pad takes
// an XInput slot with no HIDMaestro driver.
//
// Three parts, each a check of its own layer:
//   model  Offline, no elevation. The catalog profile loads, its descriptor
//          set holds the device [MS-XUSBI] describes, a mis-authored copy
//          is refused at the JSON path of its mistake, and every other
//          USB/IP persona still needs its HID interface and descriptor.
//   wire   Elevated, no device. A USB/IP client plays the host controller
//          against the in-process server: every descriptor, the Microsoft
//          OS compatible ID, the stalls [MS-XUSBI] table 29 asks for, the
//          20-byte input report on interrupt IN, and the rumble and LED
//          reports on interrupt OUT, read raw off the output ring and then
//          decoded.
//   live   Elevated, with the USB/IP host controller. One controller is
//          created, its devnode must carry xusb22 from xusb22.inf under the
//          USB/IP root hub, a submitted state must read back through
//          XInputGetState, and XInputSetState's motor bytes must arrive at
//          the device's OUT endpoint. dualsense-composite is created beside
//          it as the positive control and read back over HID.
//
// No argument runs all three. "model", "wire" and "live" each run one.
// Exit 0 PASS, 1 FAIL, 2 SKIP when another program that embeds HIDMaestro
// is running (live part only).
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static unsafe partial class Program
{
    const string ProfileId = "xbox-360-wired-full";

    // Above every transport index a create takes (ControllerLease), and
    // beside usbip_server_check's 5000.
    const int Index = 5002;

    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);

    static int Main(string[] args)
    {
        // Before any SDK type loads: the device then logs each control
        // request a host sends it, which the live part prints.
        Environment.SetEnvironmentVariable("HIDMAESTRO_DIAG", "1");
        bool all = !args.Contains("model") && !args.Contains("wire") && !args.Contains("live");
        int skip = 0;
        if (all || args.Contains("model")) Model();
        if (all || args.Contains("wire")) Wire();
        if (all || args.Contains("live")) skip = Live();
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        if (s_failures != 0) return 1;
        return skip;
    }

    // The device [MS-XUSBI] 2.2.4.1 and 3.2.1.1 describe, with GP2040-CE's
    // bcdDevice and power (headers/drivers/xinput/XInputDescriptors.h).
    static readonly byte[] DeviceDescriptor =
    {
        0x12, 0x01, 0x00, 0x02, 0xFF, 0xFF, 0xFF, 0x08, 0x5E, 0x04, 0x8E, 0x02, 0x14, 0x01, 0x01, 0x02, 0x03, 0x01,
    };

    static readonly byte[] ConfigurationDescriptor =
    {
        0x09, 0x02, 0x31, 0x00, 0x01, 0x01, 0x00, 0xA0, 0xFA,
        0x09, 0x04, 0x00, 0x00, 0x02, 0xFF, 0x5D, 0x01, 0x00,
        0x11, 0x21, 0x00, 0x01, 0x01, 0x25, 0x81, 0x14, 0x00, 0x00, 0x00, 0x00, 0x13, 0x02, 0x08, 0x00, 0x00,
        0x07, 0x05, 0x81, 0x03, 0x20, 0x00, 0x04,
        0x07, 0x05, 0x02, 0x03, 0x20, 0x00, 0x08,
    };

    // [MS-XUSBI] 2.2.4.6, table 19: 40 bytes, version 1.00, index 4, one
    // section for interface 0 with the compatible ID XUSB10.
    static readonly byte[] CompatibleId =
    {
        0x28, 0x00, 0x00, 0x00, 0x00, 0x01, 0x04, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0,
        0x00, 0x01, 0x58, 0x55, 0x53, 0x42, 0x31, 0x30, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
    };

    // ── model ────────────────────────────────────────────────────────────

    static void Model()
    {
        Console.WriteLine("=== model: the profile and its descriptor set ===");
        var catalog = ProfileDatabase.LoadEmbedded().All;
        var p = catalog.FirstOrDefault(x => x.Id == ProfileId);
        Check("the catalog holds the profile", p != null);
        if (p == null) return;
        Check("it runs on USB/IP, with no descriptor, and names an interface xusb",
              p.RequiresUsbipBackend && !p.HasDescriptor && p.ReportsOnXusbInterface);
        Check("it is deployable", new HMProfile(p).IsDeployable);
        string loaded = "";
        try { p.ValidateAtLoad(); } catch (Exception e) { loaded = e.Message; }
        Check("it meets every load check", loaded.Length == 0, loaded);

        UsbDescriptorSet? set = null;
        try { set = new UsbDescriptorSet(p); } catch (Exception e) { Check("its descriptor set builds", false, e.Message); }
        if (set != null)
        {
            Check("device descriptor, 18 bytes of [MS-XUSBI] table 13",
                  set.DeviceDescriptor.AsSpan().SequenceEqual(DeviceDescriptor), Hex(set.DeviceDescriptor));
            Check("configuration descriptor, 49 bytes: the game controller interface of [MS-XUSBI] 3.2.1.1",
                  set.ConfigurationDescriptor.AsSpan().SequenceEqual(ConfigurationDescriptor), Hex(set.ConfigurationDescriptor));
            Check("no HID interface, and the reports travel on interface 0",
                  !set.HasHidInterface && set.HidInterfaceNumber == 0 && !set.IsHidInterface(0) && set.SecondaryHidInterfaces.Count == 0);
            Check("no HID descriptor of either type is served",
                  set.GetHidDescriptor(0x22, 0) == null && set.GetHidDescriptor(0x21, 0) == null);
            Check("full speed, so the device qualifier stalls", set.Speed == 2 && set.GetDescriptor(0x06, 0, 0) == null);
            Check("interrupt IN 0x81 and interrupt OUT 0x02, 32 bytes each, at 4 and 8 ms",
                  set.Endpoints.Count == 2
                  && set.Endpoints[0x81] is { TransferType: 3, MaxPacketSize: 32, Interval: 4, InterfaceNumber: 0 }
                  && set.Endpoints[0x02] is { TransferType: 3, MaxPacketSize: 32, Interval: 8, InterfaceNumber: 0 });
            Check("the compatible ID is XUSB10 for interface 0, and the extended properties stall",
                  set.GetMicrosoftOsFeature(4) is { } c && c.AsSpan().SequenceEqual(CompatibleId) && set.GetMicrosoftOsFeature(5) == null);
        }

        Console.WriteLine("\n-- a mis-authored copy is refused at its mistake --");
        const string Function = "$.usbConfiguration.interfaces[0].function";
        Refused("a descriptor beside an xusb interface", p,
                o => o["descriptor"] = "05010905a101c0",
                "$.descriptor", "no HID interface");
        Refused("xusb on an interface that is not class 0xFF, subclass 0x5D", p,
                o => Usb(o)["configurationDescriptor"] = Usb(o)["configurationDescriptor"]!.GetValue<string>().Replace("ff5d01", "ff0001"),
                Function, "class 0xFF, subclass 0x5D");
        Refused("xusb on an interface with no interrupt IN endpoint", p,
                o =>
                {
                    Usb(o)["configurationDescriptor"] = Usb(o)["configurationDescriptor"]!.GetValue<string>().Replace("07058103", "07050103");
                    Usb(o)["interfaces"]![0]!["altSettings"]![0]!["endpoints"]![0]!["address"] = 1;
                },
                Function, "no interrupt IN endpoint");
        Refused("xusb on an interface the configuration descriptor lacks", p,
                o => Usb(o)["interfaces"]![0]!["interfaceNumber"] = 1,
                Function, "lacks");
        Refused("an xusb device with no extended report", p,
                o => o.Remove("extendedReport"),
                "$.extendedReport", "is required for a device with no HID interface");
        Refused("an xusb device whose extended report is not always armed", p,
                o => o["extendedReport"]!["alwaysArmed"] = false,
                "$.extendedReport.alwaysArmed", "must be true on a device with no HID interface");
        Refused("the same device without the xusb name still needs a descriptor", p,
                o => Usb(o)["interfaces"]![0]!["function"] = "vendor",
                "$.descriptor", "is required for the usbip backend");
        Refused("and with a descriptor still needs a HID interface", p,
                o => { Usb(o)["interfaces"]![0]!["function"] = "vendor"; o["descriptor"] = "05010905a101c0"; },
                "$.usbConfiguration.configurationDescriptor", "has no HID interface");

        // A copy outside the catalog: the profile says it takes no model, so
        // the identity default for Microsoft's Xbox pads, which gives an
        // XUSB companion's presentation, gives it nothing. The same copy
        // without that key shows the default would otherwise apply.
        Console.WriteLine("\n-- a copy outside the catalog --");
        string Presentation(Action<JsonObject>? edit)
        {
            var o = JsonNode.Parse(JsonSerializer.Serialize(p, HMJsonContext.Default.ControllerProfile))!.AsObject();
            o["id"] = "user-" + p.Id;
            edit?.Invoke(o);
            try
            {
                var copy = ProfileDatabase.LoadTexts(new[] { ("user.json", o.ToJsonString()) }, ModelRegistry.Catalog).All.Single();
                string source = copy.Resolved.Sources.TryGetValue("presentation", out var s) ? s : "absent";
                return $"{source}, deployable {new HMProfile(copy).IsDeployable}, problem {copy.Resolved.Problem ?? "none"}";
            }
            catch (Exception e) { return e.Message; }
        }
        string kept = Presentation(null), dropped = Presentation(o => o.Remove("deviceModel"));
        Check("it loads, is deployable and takes no presentation from its identity", kept == "none, deployable True, problem none", kept);
        Check("without deviceModel none it would take the Xbox identity default's", dropped.StartsWith("identity:microsoft-xbox-named", StringComparison.Ordinal), dropped);

        // The other personas: none takes the new path, and each is held to
        // the rules it met before.
        Console.WriteLine("\n-- every other USB/IP persona keeps its HID interface --");
        var others = catalog.Where(x => x.RequiresUsbipBackend && x.Id != ProfileId).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        Check("the catalog's other USB/IP personas", others.Count >= 8, string.Join(" ", others.Select(x => x.Id)));
        foreach (var other in others)
        {
            string why = "";
            try
            {
                var s = new UsbDescriptorSet(other);
                var served = s.GetHidDescriptor(0x22, s.HidInterfaceNumber);
                if (other.ReportsOnXusbInterface) why = "names an interface xusb";
                else if (!s.HasHidInterface || !s.IsHidInterface(s.HidInterfaceNumber)) why = "has no HID interface";
                else if (served == null || !served.AsSpan().SequenceEqual(other.GetDescriptorBytes())) why = "serves another report descriptor";
                else if (s.GetHidDescriptor(0x21, s.HidInterfaceNumber) is not { Length: >= 9 }) why = "serves no HID class descriptor";
                // Its data frames keep the length its descriptor gives them.
                else if (other.GetOrBuildReportBuilder().InputReportByteSize <= 0) why = "its descriptor sizes no input report";
            }
            catch (Exception e) { why = e.Message; }
            Check($"{other.Id}: a HID interface that serves the profile's descriptor, which sizes its input report", why.Length == 0, why);
        }
        var withHid = others.First(x => x.Id == "dualsense-composite");
        Refused("a HID persona without its descriptor", withHid,
                o => o.Remove("descriptor"),
                "$.descriptor", "is required for the usbip backend");
        Refused("a HID persona that names an interface xusb and keeps its descriptor", withHid,
                o => Usb(o)["interfaces"]![0]!["function"] = "xusb",
                "$.descriptor", "no HID interface");
        // dualsense-composite sends its whole report from the start
        // (alwaysArmed), so the case sets it back to false.
        Refused("a HID persona that names an interface xusb and drops its descriptor", withHid,
                o => { Usb(o)["interfaces"]![0]!["function"] = "xusb"; o.Remove("descriptor"); o["extendedReport"]!["alwaysArmed"] = false; },
                "$.extendedReport.alwaysArmed", "must be true on a device with no HID interface");
        Refused("the same with its extended report always armed", withHid,
                o => { Usb(o)["interfaces"]![0]!["function"] = "xusb"; o.Remove("descriptor"); o["extendedReport"]!["alwaysArmed"] = true; },
                Function, "of the configuration descriptor is one");
        var withOs = others.First(x => x.UsbConfiguration?.MicrosoftOs != null);
        Refused($"a HID persona ({withOs.Id}) without its extended properties", withOs,
                o => Usb(o)["microsoftOs"]!.AsObject().Remove("extendedProperties"),
                "$.usbConfiguration.microsoftOs.extendedProperties", "is required");
    }

    static JsonObject Usb(JsonObject o) => o["usbConfiguration"]!.AsObject();

    /// <summary>A copy of <paramref name="basis"/>, edited, must be refused
    /// at <paramref name="path"/> with <paramref name="reason"/> in the
    /// refusal. The copy goes through the SDK's own JSON, which leaves the
    /// model groups out, and the load's checks.</summary>
    static void Refused(string name, ControllerProfile basis, Action<JsonObject> edit, string path, string reason)
    {
        var o = JsonNode.Parse(JsonSerializer.Serialize(basis, HMJsonContext.Default.ControllerProfile))!.AsObject();
        o["id"] = "user-" + basis.Id;
        edit(o);
        string got;
        try
        {
            var copy = ControllerProfile.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()))!;
            copy.ValidateAtLoad();
            got = "accepted";
        }
        catch (Exception e) when (ProfileDefects.IsDefect(e))
        {
            var (at, said) = ProfileDefects.Locate(e);
            got = $"{at}: {said}";
        }
        catch (Exception e) { got = $"{e.GetType().Name}: {e.Message}"; }
        Check($"refused: {name}", got.StartsWith(path + ":", StringComparison.Ordinal) && got.Contains(reason, StringComparison.Ordinal), got);
    }

    // ── wire ─────────────────────────────────────────────────────────────

    // One state and the report it makes ([MS-XUSBI] 3.2.5.1.1): d-pad up,
    // the left bumper, Guide and A, the left trigger half and the right
    // trigger full, the left stick right and up, the right stick left and
    // down.
    static HMGamepadState WireState() => new()
    {
        Buttons = HMButton.A | HMButton.LeftBumper | HMButton.Guide,
        Hat = HMHat.North,
    };

    static readonly byte[] WireReport =
    {
        0x00, 0x14, 0x01, 0x15, 0x80, 0xFF, 0xFF, 0x7F, 0xFF, 0x7F, 0x00, 0x80, 0x00, 0x80, 0, 0, 0, 0, 0, 0,
    };

    static void Wire()
    {
        Console.WriteLine("\n=== wire: a USB/IP client as the host ===");
        var p = ProfileDatabase.LoadEmbedded().All.FirstOrDefault(x => x.Id == ProfileId);
        if (p == null) { Check("the catalog holds the profile", false); return; }
        // The probe plays the host controller from this process.
        UsbipServer.AdmitImportsFromThisProcess = true;
        var server = UsbipServer.GetOrStart();
        UsbipEmulatedDevice device;
        try { device = new UsbipEmulatedDevice(p, Index); }
        catch (Exception e) { Check("the device is built (needs elevation, for the shared sections)", false, e.Message); return; }
        server.Register(device);
        try
        {
            using var cl = new Client(server.Port, Index);
            cl.SendImport(device.BusId);
            var op = cl.ReadExactly(8);
            var imported = cl.ReadExactly(312);
            Check("the import is accepted", BinaryPrimitives.ReadUInt32BigEndian(op.AsSpan(4)) == 0);
            // usbip_usb_device: path 256, busid 32, busnum, devnum, speed,
            // then idVendor, idProduct, bcdDevice and the class bytes.
            Check("the import reply: full speed, 045E:028E, bcdDevice 0x0114, class FF/FF/FF, one interface",
                  BinaryPrimitives.ReadUInt32BigEndian(imported.AsSpan(296)) == 2
                  && imported.AsSpan(300, 12).SequenceEqual(new byte[] { 0x04, 0x5E, 0x02, 0x8E, 0x01, 0x14, 0xFF, 0xFF, 0xFF, 0x01, 0x01, 0x01 }),
                  Hex(imported.AsSpan(296, 16)));

            Console.WriteLine("\n-- descriptors --");
            var dd = cl.ControlIn(0x80, 0x06, 0x0100, 0, 18);
            Check("GET_DESCRIPTOR(Device)", dd.Status == 0 && dd.Data.AsSpan().SequenceEqual(DeviceDescriptor), Hex(dd.Data));
            var head = cl.ControlIn(0x80, 0x06, 0x0200, 0, 9);
            var whole = cl.ControlIn(0x80, 0x06, 0x0200, 0, 255);
            Check("GET_DESCRIPTOR(Configuration): its 9-byte header, then all 49 bytes",
                  head.Status == 0 && head.Data.AsSpan().SequenceEqual(ConfigurationDescriptor.AsSpan(0, 9))
                  && whole.Status == 0 && whole.Data.AsSpan().SequenceEqual(ConfigurationDescriptor), Hex(whole.Data));
            Check("the device qualifier stalls, as a full-speed device's does", cl.ControlIn(0x80, 0x06, 0x0600, 0, 10).Status != 0);
            Check("string 1 is the manufacturer", Text(cl.ControlIn(0x80, 0x06, 0x0301, 0x0409, 255)) == "©Microsoft Corporation",
                  Text(cl.ControlIn(0x80, 0x06, 0x0301, 0x0409, 255)));
            Check("string 2 is the product", Text(cl.ControlIn(0x80, 0x06, 0x0302, 0x0409, 255)) == "Controller");
            string serial = Text(cl.ControlIn(0x80, 0x06, 0x0303, 0x0409, 255));
            Check("string 3 is the controller's own serial", serial == DeviceIdentity.ForIndex(Index).SyntheticSerial, serial);
            var os = cl.ControlIn(0x80, 0x06, 0x03EE, 0, 18);
            Check("string 0xEE is MSFT100 with vendor code 0x20 ([MS-XUSBI] table 18)",
                  os.Status == 0 && os.Data.Length == 18 && Encoding.Unicode.GetString(os.Data, 2, 14) == "MSFT100" && os.Data[16] == 0x20 && os.Data[17] == 0,
                  Hex(os.Data));
            var compatHead = cl.ControlIn(0xC0, 0x20, 0, 0x0004, 16);
            var compat = cl.ControlIn(0xC0, 0x20, 0, 0x0004, 40);
            Check("the extended compatible ID: its 16-byte header, then all 40 bytes, XUSB10",
                  compatHead.Status == 0 && compatHead.Data.AsSpan().SequenceEqual(CompatibleId.AsSpan(0, 16))
                  && compat.Status == 0 && compat.Data.AsSpan().SequenceEqual(CompatibleId), Hex(compat.Data));
            Check("the extended properties stall, for the device and for the interface",
                  cl.ControlIn(0xC0, 0x20, 0, 0x0005, 10).Status != 0 && cl.ControlIn(0xC1, 0x20, 0, 0x0005, 10).Status != 0);
            Check("an interface's HID descriptors stall: the report descriptor and the class descriptor",
                  cl.ControlIn(0x81, 0x06, 0x2200, 0, 255).Status != 0 && cl.ControlIn(0x81, 0x06, 0x2100, 0, 9).Status != 0);

            Console.WriteLine("\n-- requests --");
            Check("SET_CONFIGURATION(1) acks and reads back",
                  cl.ControlOut(0x00, 0x09, 1, 0, Array.Empty<byte>()).Status == 0
                  && cl.ControlIn(0x80, 0x08, 0, 0, 1) is { Status: 0 } cfg && cfg.Data[0] == 1);
            var status = cl.ControlIn(0x80, 0x00, 0, 0, 2);
            Check("GET_STATUS reads bus-powered, as bmAttributes 0xA0 says", status.Status == 0 && status.Data.AsSpan().SequenceEqual(new byte[] { 0, 0 }), Hex(status.Data));
            // [MS-XUSBI] table 29 lists the vendor requests, and GP2040-CE
            // stalls every one with no console authentication
            // (XInputDriver.cpp:419-423).
            Check("the capability requests stall (C1 01, wValue 0x0100 and 0x0000)",
                  cl.ControlIn(0xC1, 0x01, 0x0100, 0, 20).Status != 0 && cl.ControlIn(0xC1, 0x01, 0x0000, 0, 8).Status != 0);
            Check("GET_DEVICE_ID stalls (C0 01)", cl.ControlIn(0xC0, 0x01, 0, 0, 4).Status != 0);
            Check("SET_CONTROL stalls (41 00)", cl.ControlOut(0x41, 0x00, 0, 0, Array.Empty<byte>()).Status != 0);
            Check("the HID class's other requests stall: SET_IDLE, GET_IDLE, SET_PROTOCOL, GET_REPORT(Feature), SET_REPORT(Feature)",
                  cl.ControlOut(0x21, 0x0A, 0, 0, Array.Empty<byte>()).Status != 0
                  && cl.ControlIn(0xA1, 0x02, 0, 0, 1).Status != 0
                  && cl.ControlOut(0x21, 0x0B, 0, 0, Array.Empty<byte>()).Status != 0
                  && cl.ControlIn(0xA1, 0x01, 0x0300, 0, 20).Status != 0
                  && cl.ControlOut(0x21, 0x09, 0x0300, 0, new byte[] { 0, 8, 0, 1, 2, 0, 0, 0 }).Status != 0);

            Console.WriteLine("\n-- input report 0x00 on interrupt IN 0x81 --");
            IntPtr view = SharedMemoryIO.EnsureInputMapping(Index);
            IntPtr evt = SharedMemoryIO.GetInputEvent(Index);
            uint seqNo = (uint)Marshal.ReadInt32(view, 0);
            var atRest = new byte[20];
            atRest[1] = 0x14;
            var first = cl.ControlIn(0xA1, 0x01, 0x0100, 0, 20);
            Check("before any frame, GET_REPORT(Input) answers the pad at rest: 00 14 and eighteen zero bytes",
                  first.Status == 0 && first.Data.AsSpan().SequenceEqual(atRest), Hex(first.Data));
            var report = new byte[p.ExtendedReport!.Size];
            var state = WireState();
            VendorBlobCodec.EncodeInput(p.ExtendedReport, in state, 1f, 0f, 0f, 1f, 0.5f, 1f, report, new VendorBlobCodec.EncoderState());
            Check("the codec builds the 20 bytes of [MS-XUSBI] table 53 from the profile's fields",
                  report.AsSpan().SequenceEqual(WireReport), Hex(report));
            // A read with no new report waits: the device answers a poll
            // with a state it has not reported, or not at all ([MS-XUSBI]
            // 1.3.4).
            uint read = cl.NextSeq();
            cl.SendSubmitInterruptIn(read, 1, 32);
            Check("a read waits while no report is new", cl.Within(200) == "nothing");
            SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, ReadOnlySpan<byte>.Empty, 0, null,
                                           extendedData: report, extendedLen: report.Length);
            var got = cl.ReadRet(read);
            Check("the waiting read gets the report, byte for byte", got.Status == 0 && got.Data.AsSpan().SequenceEqual(WireReport), Hex(got.Data));
            var again = cl.ControlIn(0xA1, 0x01, 0x0100, 0, 20);
            Check("GET_REPORT(Input) answers the report just sent ([MS-XUSBI] 2.2.8.2)",
                  again.Status == 0 && again.Data.AsSpan().SequenceEqual(WireReport), Hex(again.Data));
            Check("GET_REPORT(Input) of a report ID the device never sends stalls", cl.ControlIn(0xA1, 0x01, 0x0103, 0, 20).Status != 0);
            // SubmitRawReport's lane: the frame is the report, whole, at the
            // extended report's length. B alone.
            byte[] rawFrame = { 0x00, 0x14, 0x00, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, rawFrame, rawFrame.Length, null);
            read = cl.NextSeq();
            cl.SendSubmitInterruptIn(read, 1, 32);
            got = cl.ReadRet(read);
            Check("a raw frame goes out as the report it is, all 20 bytes", got.Status == 0 && got.Data.AsSpan().SequenceEqual(rawFrame), Hex(got.Data));

            Console.WriteLine("\n-- output reports on interrupt OUT 0x02 --");
            IntPtr ring = SharedMemoryIO.EnsureOutputMapping(Index);
            uint last = (uint)Marshal.ReadInt32(ring, 0);
            var buf = new byte[SharedMemoryIO.DATA_CAPACITY];
            byte[] rumble = { 0x00, 0x08, 0x00, 0x40, 0x80, 0x00, 0x00, 0x00 };
            uint w = cl.NextSeq();
            cl.SendSubmitInterruptOut(w, 2, rumble);
            var ack = cl.ReadRet(w);
            bool ringed = WaitRing(ring, ref last, buf, out byte source, out byte id, out int size);
            Check("the rumble report is acked whole and reaches the ring raw: report 0x00, then its seven bytes",
                  ack.Status == 0 && ack.ActualLength == 8 && ringed && source == UsbipEmulatedDevice.SourceHidOutput && id == 0x00
                  && buf.AsSpan(0, size).SequenceEqual(rumble.AsSpan(1)), $"source {source} id {id:X2} data {Hex(buf.AsSpan(0, size))}");
            var (fields, _) = VendorBlobCodec.Decode(p.ExtendedOutputReport!, rumble);
            Check("it decodes to leftMotor 0x40, the big motor, and rightMotor 0x80, the small one ([MS-XUSBI] table 61)",
                  fields.TryGetValue("leftMotor", out var l) && l is byte lb && lb == 0x40
                  && fields.TryGetValue("rightMotor", out var r) && r is byte rb && rb == 0x80,
                  string.Join(" ", fields.Select(kv => $"{kv.Key}={kv.Value}")));

            byte[] led = { 0x01, 0x03, 0x06 };
            w = cl.NextSeq();
            cl.SendSubmitInterruptOut(w, 2, led);
            ack = cl.ReadRet(w);
            ringed = WaitRing(ring, ref last, buf, out source, out id, out size);
            Check("the LED report reaches the ring raw: report 0x01, then 03 06",
                  ack.Status == 0 && ringed && source == UsbipEmulatedDevice.SourceHidOutput && id == 0x01
                  && buf.AsSpan(0, size).SequenceEqual(led.AsSpan(1)), $"source {source} id {id:X2} data {Hex(buf.AsSpan(0, size))}");
            var decoders = ProgramBinder.BindDecoders(p.GetOrCompileModel());
            var scratch = GC.AllocateArray<byte>(SharedMemoryIO.DATA_CAPACITY + 1 + HIDMaestro.Internal.Programs.ProgramForm.Slack + 1, pinned: true);
            var decoded = GC.AllocateArray<byte>(HIDMaestro.Internal.Programs.ProgramForm.MaxReport, pinned: true);
            bool ran = decoders.Length == 1 && decoders[0].Form.Reports.SequenceEqual(new byte[] { 0x01 });
            fixed (byte* d = decoded)
                ran = ran && decoders[0].RunOutput(0x01, led.AsSpan(1).ToArray(), 2, scratch, d);
            Check("protocol.decoders.led raises it as ledPattern 6, the first LED on ([MS-XUSBI] 3.2.5.2.2)",
                  ran && decoders[0].Form.Semantics.SequenceEqual(new[] { "ledPattern" }) && decoded[0] == 6, $"{decoded[0]}");

            byte[] viaControl = { 0x00, 0x08, 0x00, 0x11, 0x22, 0x00, 0x00, 0x00 };
            var set = cl.ControlOut(0x21, 0x09, 0x0200, 0, viaControl);
            ringed = WaitRing(ring, ref last, buf, out source, out id, out size);
            Check("SET_REPORT(Output) is the same report on the control endpoint ([MS-XUSBI] 2.2.8.3)",
                  set.Status == 0 && ringed && id == 0x00 && buf.AsSpan(0, size).SequenceEqual(viaControl.AsSpan(1)), Hex(buf.AsSpan(0, size)));
        }
        catch (Exception e) { Check("the wire part ran to its end", false, e.ToString()); }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(Index);
        }
    }

    static string Text(Client.Ret r) =>
        r.Status == 0 && r.Data.Length >= 2 && r.Data[1] == 0x03 ? Encoding.Unicode.GetString(r.Data, 2, r.Data[0] - 2) : $"status {r.Status}";

    static bool WaitRing(IntPtr view, ref uint lastSeq, byte[] buf, out byte source, out byte reportId, out int size)
    {
        for (int i = 0; i < 500; i++)
        {
            if (SharedMemoryIO.TryReadOutputFrame(view, ref lastSeq, out source, out reportId, out size, buf)) return true;
            Thread.Sleep(2);
        }
        source = 0; reportId = 0; size = 0;
        return false;
    }

    /// <summary>A USB/IP client that plays the host controller's part:
    /// the import, control transfers and interrupt transfers
    /// (usbip_protocol.rst), as usbip_server_check's does.</summary>
    sealed class Client : IDisposable
    {
        readonly TcpClient _tcp;
        readonly NetworkStream _s;
        readonly uint _devid;
        uint _seq = 100;
        readonly Dictionary<uint, Ret> _stash = new();
        readonly HashSet<uint> _pendingIn = new();

        public Client(int port, int index)
        {
            _tcp = new TcpClient("127.0.0.1", port) { NoDelay = true };
            _s = _tcp.GetStream();
            _s.ReadTimeout = 60_000;
            _devid = (1u << 16) | (uint)(index + 1);
        }

        public uint NextSeq() => _seq++;

        public struct Ret
        {
            public int Status, ActualLength;
            public byte[] Data;
        }

        public void SendImport(string busid)
        {
            var b = new byte[8 + 32];
            BinaryPrimitives.WriteUInt16BigEndian(b, 0x0111);
            BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), 0x8003);
            Encoding.UTF8.GetBytes(busid).CopyTo(b, 8);
            _s.Write(b);
        }

        void SendHeader(uint seqnum, uint direction, uint ep, int bufLen, int interval, ulong setup)
        {
            var h = new byte[48];
            BinaryPrimitives.WriteUInt32BigEndian(h, 1); // CMD_SUBMIT
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(4), seqnum);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8), _devid);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(12), direction);
            BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(16), ep);
            BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(24), bufLen);
            BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(32), -1);
            BinaryPrimitives.WriteInt32BigEndian(h.AsSpan(36), interval);
            BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(40), setup);
            _s.Write(h);
        }

        static ulong Setup(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, ushort wLength)
        {
            Span<byte> s = stackalloc byte[8];
            s[0] = bmRequestType; s[1] = bRequest;
            BinaryPrimitives.WriteUInt16LittleEndian(s[2..], wValue);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], wIndex);
            BinaryPrimitives.WriteUInt16LittleEndian(s[6..], wLength);
            return BinaryPrimitives.ReadUInt64LittleEndian(s);
        }

        public Ret ControlIn(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, ushort wLength)
        {
            uint seq = NextSeq();
            _pendingIn.Add(seq);
            SendHeader(seq, 1, 0, wLength, 0, Setup(bmRequestType, bRequest, wValue, wIndex, wLength));
            return ReadRet(seq);
        }

        public Ret ControlOut(byte bmRequestType, byte bRequest, ushort wValue, ushort wIndex, byte[] data)
        {
            uint seq = NextSeq();
            SendHeader(seq, 0, 0, data.Length, 0, Setup(bmRequestType, bRequest, wValue, wIndex, (ushort)data.Length));
            if (data.Length > 0) _s.Write(data);
            return ReadRet(seq);
        }

        public void SendSubmitInterruptIn(uint seq, uint ep, int len)
        {
            _pendingIn.Add(seq);
            SendHeader(seq, 1, ep, len, 4, 0);
        }

        public void SendSubmitInterruptOut(uint seq, uint ep, byte[] data)
        {
            SendHeader(seq, 0, ep, data.Length, 8, 0);
            _s.Write(data);
        }

        /// <summary>What the server sends within <paramref name="ms"/>:
        /// "data", "closed" or "nothing".</summary>
        public string Within(int ms)
        {
            if (!_tcp.Client.Poll(ms * 1000, SelectMode.SelectRead)) return "nothing";
            return _tcp.Client.Available > 0 ? "data" : "closed";
        }

        public Ret ReadRet(uint seqnum)
        {
            while (true)
            {
                if (_stash.Remove(seqnum, out var hit)) return hit;
                var h = ReadExactly(48);
                uint command = BinaryPrimitives.ReadUInt32BigEndian(h);
                uint seq = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(4));
                if (command != 3) throw new InvalidOperationException($"unexpected command {command}");
                var r = new Ret
                {
                    Status = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(20)),
                    ActualLength = BinaryPrimitives.ReadInt32BigEndian(h.AsSpan(24)),
                    Data = Array.Empty<byte>(),
                };
                // The server sends data for an IN transfer alone.
                if (_pendingIn.Remove(seq) && r.ActualLength > 0) r.Data = ReadExactly(r.ActualLength);
                _stash[seq] = r;
            }
        }

        public byte[] ReadExactly(int n)
        {
            var b = new byte[n];
            for (int got = 0; got < n;)
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
