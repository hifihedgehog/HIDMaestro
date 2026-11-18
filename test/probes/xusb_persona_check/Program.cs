// xusb_persona_check: the XUSB functions, the USB devices the profiles whose
// XInput presentation is the XUSB companion (xinput.via "xusbCompanion")
// present on the USB/IP backend. Windows binds its inbox XUSB driver,
// xusb22.sys, to the device, and the pad takes an XInput slot with no
// HIDMaestro driver. The devices are the XUSB model's data
// (profiles/models/xusb-companion.json, usbFunction): a retail wired Xbox 360
// controller's descriptors with the profile's VID, PID and subtype written
// in, and for xbox-360-wireless a retail Xbox 360 Wireless Receiver for
// Windows with the pad on its first slot.
//
// Parts, each a check of its own layer:
//   model  Offline, no elevation. Every XUSB companion profile presents its
//          function, its descriptors the retail dumps' with its own values,
//          its subtype the one the UMDF2 companion answers. Each function's
//          input report reads in XInput as the companion's state does for
//          the same input, its output rules give the companion's packets,
//          the identities xinputhid and the GIP driver bind are refused at
//          load, and a user's own profile that authors a device with no HID
//          interface keeps its rules.
//   wire   Elevated, no device. A USB/IP client plays the host controller
//          against the in-process server, with xbox-360-wired and then
//          xbox-360-wireless: every descriptor and string, the stalls, the
//          receiver's announcement and its answers to connection requests,
//          the input report on interrupt IN, the interfaces that park their
//          reads, and the rumble and LED requests on interrupt OUT, read off
//          the output ring as the companion's packets.
//   live   Elevated, with the USB/IP host controller. xbox-360-wired, or the
//          profile named after the part, on the backend --backend names
//          (usbip by default): on USB/IP its devnode carries xusb22 from
//          xusb22.inf, the create returns with its XInput slot taken, a
//          submitted state reads back through XInputGetState, XInputSetState
//          arrives as the companion's packet, and a raw frame of another
//          length is refused. dualsense-composite beside it is the positive
//          control.
//   family Elevated. Each of the ten profiles (or the ids given) on every
//          backend --backend names (both by default where the machine runs
//          UMDF2): XInputGetCapabilities, the HID collection's parsed fields
//          and product string, the XInput state of one input and the packet
//          of one XInputSetState, compared across backends.
//   sweep  Elevated, USB/IP. A copy of xbox-360-wired for each subtype the
//          arguments name (hex, default 01 to 13): XInputGetCapabilities and
//          the parsed fields of the HID collection xusb22 makes for it.
//
// No argument runs model and wire. Exit 0 PASS, 1 FAIL, 2 SKIP when another
// program that embeds HIDMaestro is running (live parts only).
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
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
    const string ProfileId = "xbox-360-wired";

    /// <summary>Every catalog profile whose XInput presentation is the XUSB
    /// companion.</summary>
    static readonly string[] Family =
    {
        "xbox-360-arcade-stick", "xbox-360-dance-pad", "xbox-360-flight-stick", "xbox-360-guitar-v1", "xbox-360-guitar-v2",
        "xbox-360-type2", "xbox-360-wheel-v1", "xbox-360-wheel-v2", "xbox-360-wired", "xbox-360-wireless",
    };

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
        var list = args.ToList();
        string backend = "";
        int at = list.IndexOf("--backend");
        if (at >= 0 && at + 1 < list.Count) { backend = list[at + 1]; list.RemoveRange(at, 2); }
        string part = list.Count > 0 ? list[0] : "";
        var rest = list.Skip(1).ToList();
        int skip = 0;
        switch (part)
        {
            case "": Model(); Wire(); WireReceiver(); break;
            case "model": Model(); break;
            case "wire": Wire(); WireReceiver(); break;
            case "live": skip = Live(backend.Length > 0 ? backend : "usbip", rest.Count > 0 ? rest[0] : ProfileId); break;
            case "family": skip = FamilyPart(rest, backend); break;
            case "sweep": skip = Sweep(rest); break;
            default: Console.WriteLine($"unknown part {part}"); return 1;
        }
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        if (s_failures != 0) return 1;
        return skip;
    }

    // The retail pad of ControllersInfo's xbox360/DescriptorDump_Controller
    // (Xbox 360 Wired Model X854237-001).txt.
    static readonly byte[] RetailDevice =
    {
        0x12, 0x01, 0x00, 0x02, 0xFF, 0xFF, 0xFF, 0x08, 0x5E, 0x04, 0x8E, 0x02, 0x14, 0x01, 0x01, 0x02, 0x03, 0x01,
    };

    static readonly byte[] RetailConfiguration = Convert.FromHexString(
        "09029900040100A0FA" +
        "0904000002FF5D0100" + "1121000101258114000000001301080000" + "07058103200004" + "07050103200008" +
        "0904010004FF5D0300" + "1B2100010101824001022016830000000000001603000000000000" +
        "07058203200002" + "07050203200004" + "07058303200040" + "07050303200010" +
        "0904020001FF5D0200" + "092100010122840700" + "07058403200010" +
        "0904030000FFFD1304" + "064100010103");

    const string RetailManufacturer = "©Microsoft Corporation";
    // The retail receiver of rombert's gist 6902ab691e12ab478e31 (lsusb -v of
    // 045e:0719), its configuration written out interface by interface.
    static readonly byte[] ReceiverDevice =
    {
        0x12, 0x01, 0x00, 0x02, 0xFF, 0xFF, 0xFF, 0x08, 0x5E, 0x04, 0x19, 0x07, 0x00, 0x01, 0x01, 0x02, 0x03, 0x01,
    };

    static readonly byte[] ReceiverConfiguration = Convert.FromHexString(
        "09024101080100A082" +
        "0904000002FF5D8100" + "1422000113811D001701020813010C000C010208" + "07058103200001" + "07050103200008" +
        "0904010002FF5D8200" + "0C2200010182004001022000" + "07058203200002" + "07050203200004" +
        "0904020002FF5D8100" + "1422000113831D001701020813030C000C010208" + "07058303200001" + "07050303200008" +
        "0904030002FF5D8200" + "0C2200010184004001042000" + "07058403200002" + "07050403200004" +
        "0904040002FF5D8100" + "1422000113851D001701020813050C000C010208" + "07058503200001" + "07050503200008" +
        "0904050002FF5D8200" + "0C2200010186004001062000" + "07058603200002" + "07050603200004" +
        "0904060002FF5D8100" + "1422000113871D001701020813070C000C010208" + "07058703200001" + "07050703200008" +
        "0904070002FF5D8200" + "0C2200010188004001082000" + "07058803200002" + "07050803200004");

    // tattiebogle.net's Wireless Gaming Receiver Information page: the link
    // control packet a retail pad sent through the receiver.
    static readonly byte[] CapturedLink = Convert.FromHexString("000F00F0F0CCFD0CD17048B0F100000513E7201D300340015001FFFFFF");

    const string RetailProduct = "Controller";
    const string RetailSecurity = "Xbox Security Method 3, Version 1.00, © 2005 Microsoft Corporation. All rights reserved.";

    // ── model ────────────────────────────────────────────────────────────

    static void Model()
    {
        Console.WriteLine("=== model: the XUSB function of every XUSB companion profile ===");
        var catalog = ProfileDatabase.LoadEmbedded().All;
        Check("the catalog holds no xbox-360-wired-full: xbox-360-wired presents the device on USB/IP",
              catalog.All(x => x.Id != "xbox-360-wired-full"));
        var family = catalog.Where(x => XInputPresentation.Resolve(x).XusbCompanion).Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check("the catalog's XUSB companion profiles are the ten", family.SequenceEqual(Family.OrderBy(x => x, StringComparer.Ordinal)), string.Join(" ", family));
        Check("the retail configuration descriptor is 153 bytes, its wTotalLength", RetailConfiguration.Length == 153 && RetailConfiguration[2] == 153);

        var m = XusbFunction.Model.Functions.Single(f => f.Name == "wired");
        var rx = XusbFunction.Model.Functions.Single(f => f.Name == "receiver");
        Check("two functions: the receiver for a wireless-adapter profile, then the wired pad for every other",
              XusbFunction.Model.Functions.Length == 2 && XusbFunction.Model.Functions[0] == rx && rx.WhenConnection == "wireless-adapter" && m.WhenConnection == null);
        Check("the receiver's descriptors are the retail receiver's: 18 and 321 bytes",
              rx.DeviceDescriptor.AsSpan().SequenceEqual(ReceiverDevice) && rx.ConfigurationDescriptor.AsSpan().SequenceEqual(ReceiverConfiguration)
              && ReceiverConfiguration.Length == 321, Hex(rx.ConfigurationDescriptor));
        Check("the receiver's strings: 1 \u00A9Microsoft, 2 Xbox 360 Wireless Receiver for Windows",
              rx.Strings.Count == 2 && rx.Strings[1] == "\u00A9Microsoft" && rx.Strings[2] == "Xbox 360 Wireless Receiver for Windows");
        Check("the receiver's identity is the profile's xinput.information, written at device bytes 8 and 10",
              rx.Fields.SequenceEqual(new[] { ("device", 8, "information.vid"), ("device", 10, "information.pid") }));
        var linkBytes = XusbFunction.Bytes(rx.StartMessages[1], "HM0123456789AB", 0x01);
        var wantLink = (byte[])CapturedLink.Clone();
        wantLink[10] = 0x45; wantLink[11] = 0x67; wantLink[12] = 0x89; wantLink[13] = 0xAB;
        Check("once configured the receiver sends 08 80, then the captured link packet with the serial's last eight hex digits at byte 10",
              rx.StartMessages.Length == 2 && rx.StartMessages[0].Template.SequenceEqual(new byte[] { 0x08, 0x80 })
              && linkBytes.AsSpan().SequenceEqual(wantLink), Hex(linkBytes));
        Check("the model's descriptors are the retail pad's", m.DeviceDescriptor.AsSpan().SequenceEqual(RetailDevice)
              && m.ConfigurationDescriptor.AsSpan().SequenceEqual(RetailConfiguration), Hex(m.ConfigurationDescriptor));
        Check("the model's strings are the retail pad's: 1 the manufacturer, 2 the product, 4 the security interface's",
              m.Strings.Count == 3 && m.Strings[1] == RetailManufacturer && m.Strings[2] == RetailProduct && m.Strings[4] == RetailSecurity);
        Check("the model writes the VID and PID at device bytes 8 and 10, the subtype at configuration byte 22",
              m.Fields.SequenceEqual(new[] { ("device", 8, "vid"), ("device", 10, "pid"), ("configuration", 22, "subType") }));
        Check("configuration byte 22 is byte 4 of the XUSB interface descriptor ([MS-XUSBI] 2.2.4.5)",
              RetailConfiguration[18] == 0x11 && RetailConfiguration[19] == 0x21 && RetailConfiguration[9 + 1] == 0x04 && RetailConfiguration[9 + 6] == 0x5D);

        foreach (var id in Family)
        {
            var p = catalog.FirstOrDefault(x => x.Id == id);
            if (p == null) { Check($"{id}: in the catalog", false); continue; }
            var hm = new HMProfile(p);
            string why = "";
            UsbDescriptorSet? set = null;
            byte subType = 0, umdf2SubType = 0;
            try
            {
                var pr = UsbPresentation.Resolve(p);
                set = new UsbDescriptorSet(p);
                var x = XInputPresentation.Resolve(p).Spec!;
                subType = XInputPresentation.SubTypeByte(p.Id, x) ?? XusbAnswers.Model.SubType;
                umdf2SubType = CompanionSubType(p);
                // A wireless-adapter profile presents the receiver, with the
                // identity its xinput.information gives, and every other the
                // wired pad, its subtype at configuration byte 22.
                bool receiver = p.Connection == "wireless-adapter";
                var d = (byte[])(receiver ? ReceiverDevice : RetailDevice).Clone();
                var info = XInputPresentation.Resolve(p).Spec!.Information;
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(8), receiver ? XInputPresentation.ParseId(info!.Vid!, "vid") : p.VendorId);
                BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(10), receiver ? XInputPresentation.ParseId(info!.Pid!, "pid") : p.ProductId);
                var c = (byte[])(receiver ? ReceiverConfiguration : RetailConfiguration).Clone();
                if (!receiver) c[22] = subType;
                if (!hm.SupportsBackend(HMBackend.Usbip) || !hm.SupportsBackend(HMBackend.Umdf2)) why = "not both backends";
                else if (pr.Kind != UsbPresentationKind.XusbFunction) why = $"kind {pr.Kind}";
                else if (!set.DeviceDescriptor.AsSpan().SequenceEqual(d)) why = "device descriptor " + Hex(set.DeviceDescriptor);
                else if (!set.ConfigurationDescriptor.AsSpan().SequenceEqual(c)) why = "configuration descriptor";
                else if (set.HasHidInterface || set.HidInterfaceNumber != 0 || set.ReportDescriptor.Length != 0) why = "a HID interface";
                else if (set.Speed != 2 || set.NumInterfaces != (receiver ? 8 : 4) || set.Endpoints.Count != (receiver ? 16 : 7))
                    why = $"speed {set.Speed}, {set.NumInterfaces} interfaces, {set.Endpoints.Count} endpoints";
                else if (set.GetDescriptor(0x03, 0xEE, 0) != null || set.GetMicrosoftOsFeature(4) != null) why = "Microsoft OS descriptors";
                else if (subType != umdf2SubType) why = $"subtype 0x{subType:X2} here, 0x{umdf2SubType:X2} from the companion";
                else if (pr.Report is not { AlwaysArmed: true } || pr.Report.Size != (receiver ? 29 : 20)) why = "report";
            }
            catch (Exception e) { why = $"{e.GetType().Name}: {e.Message}"; }
            Check($"{id}: the XUSB function, {(p.Connection == "wireless-adapter" ? "the receiver 045E:0719" : $"{p.VendorId:X4}:{p.ProductId:X4}")}, subtype 0x{subType:X2} as the UMDF2 companion answers", why.Length == 0, why);
        }

        Console.WriteLine("\n-- the 20-byte report reads in XInput as the companion's state --");
        Equivalence(m.Report, 0, "wired");
        Equivalence(rx.Report, 4, "receiver");

        Console.WriteLine("\n-- the output packets are the companion's --");
        Check("the wired function's rules: rumble 00 08 to {0, 0, L, R, 2}, the LED 01 03 to {0, P, 0, 0, 1}, source XInput, and the master rumble level 02 taken",
              m.OutputPackets.Length == 3
              && m.OutputPackets[0] is { MinLength: 5, Source: (byte)HMOutputSource.XInput, Interfaces: null } r
              && r.Match.SequenceEqual(new byte[] { 0x00, 0x08 }) && r.Packet!.SequenceEqual(new byte[] { 0, 0, 0, 0, 2 })
              && r.Copies.SequenceEqual(new[] { (3, 2, (byte)0xFF), (4, 3, (byte)0xFF) })
              && m.OutputPackets[1] is { MinLength: 3, Source: (byte)HMOutputSource.XInput } l
              && l.Match.SequenceEqual(new byte[] { 0x01, 0x03 }) && l.Packet!.SequenceEqual(new byte[] { 0, 0, 0, 0, 1 })
              && l.Copies.SequenceEqual(new[] { (2, 1, (byte)0xFF) })
              && m.OutputPackets[2] is { Packet: null, Replies.Length: 0 } level && level.Match.SequenceEqual(new byte[] { 0x02 }));
        var rr = rx.OutputPackets;
        Check("the receiver's rules: a data link request answered 08 80 and the link packet, other requests 08 80, empty slots 08 00, power off taken, "
              + "the LED 00 00 08 4P to {0, P, 0, 0, 1}, rumble 00 01 0F C0 00 L R to {0, 0, L, R, 2}",
              rr.Length == 6
              && rr[0].Match.SequenceEqual(new byte[] { 0x08, 0x00 }) && rr[0].Replies.Length == 2 && rr[0].Packet == null
              && rr[1].Match.SequenceEqual(new byte[] { 0x08 }) && rr[1].Replies.Length == 1 && rr[1].Interfaces == null
              && rr[2].Interfaces!.SequenceEqual(new byte[] { 2, 4, 6 }) && rr[2].Replies[0].Template.SequenceEqual(new byte[] { 0x08, 0x00 })
              && rr[3].Match.SequenceEqual(new byte[] { 0x00, 0x00, 0x08, 0xC0 }) && rr[3].Packet == null && rr[3].Replies.Length == 0
              && rr[4].Packet!.SequenceEqual(new byte[] { 0, 0, 0, 0, 1 }) && rr[4].Copies.SequenceEqual(new[] { (3, 1, (byte)0x0F) })
              && rr[5].Match.SequenceEqual(new byte[] { 0x00, 0x01, 0x0F, 0xC0 }) && rr[5].Copies.SequenceEqual(new[] { (5, 2, (byte)0xFF), (6, 3, (byte)0xFF) }));

        Console.WriteLine("\n-- refusals --");
        var wired = catalog.First(x => x.Id == ProfileId);
        Refused("an XUSB companion copy with an identity xinputhid binds (045E:02EA)", wired, o => o["pid"] = "0x02EA", "$.pid", "xinputhid.inf");
        Refused("an XUSB companion copy with an identity only the GIP driver binds (045E:0B12)", wired, o => o["pid"] = "0x0B12", "$.pid", "dc1-controller.inf");
        Refused("an XUSB companion copy with the identity xinputhid and the GIP driver both bind (045E:0B00)", wired, o => o["pid"] = "0x0B00",
                "$.pid", "xinputhid.inf and dc1-controller.inf");
        string pinned;
        try
        {
            var o = Json(wired);
            o["usbConfiguration"] = new JsonObject { ["busSpeed"] = "full" };
            var copy = ControllerProfile.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()))!;
            _ = UsbPresentation.Resolve(copy);
            pinned = "accepted";
        }
        catch (ProfileDefectException e) { pinned = $"{e.JsonPath}: {e.Message}"; }
        catch (Exception e) { pinned = $"{e.GetType().Name}: {e.Message}"; }
        Check("pins over a derived HID device on an XUSB companion profile are refused at $.usbConfiguration",
              pinned.StartsWith("$.usbConfiguration:", StringComparison.Ordinal) && pinned.Contains("XUSB function", StringComparison.Ordinal), pinned);
        Check("an identity no INF of the two lists loads: 045E:028E, 045E:02A1", RefusedWhy(wired, "0x028E") == null && RefusedWhy(wired, "0x02A1") == null);

        Authored(catalog);
    }

    /// <summary>The subtype the UMDF2 companion answers in its 20-byte
    /// GET_CAPABILITIES reply for <paramref name="p"/>, read off the blob it
    /// is given.</summary>
    static byte CompanionSubType(ControllerProfile p)
    {
        var blob = XusbAnswers.Compile(p, 0)!;
        int count = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(8));
        int arena = XusbAnswers.HeaderSize + count * XusbAnswers.EntrySize;
        for (int i = 0; i < count; i++)
        {
            int e = XusbAnswers.HeaderSize + i * XusbAnswers.EntrySize;
            uint code = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(e));
            ushort min = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(e + 4));
            ushort offset = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(e + 8));
            if (code == 0x8000E004 && min == 0) return blob[arena + offset + 3];
        }
        throw new InvalidOperationException("no GET_CAPABILITIES answer");
    }

    static string? RefusedWhy(ControllerProfile basis, string pid)
    {
        var o = Json(basis);
        o["pid"] = pid;
        try { ControllerProfile.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()))!.ValidateAtLoad(); return null; }
        catch (Exception e) { return e.Message; }
    }

    // ── XInput state on both backends ────────────────────────────────────

    /// <summary>XINPUT_GAMEPAD as xinput1_4 reads it.</summary>
    record struct Pad(ushort Buttons, byte Lt, byte Rt, short Lx, short Ly, short Rx, short Ry);

    /// <summary>The companion's state of a GIP slice (driver/companion.c
    /// DecodeGipToXInput), line for line.</summary>
    static Pad CompanionPad(byte[] d)
    {
        byte btnLow = d[12], btnHigh = d[13];
        int hat = (btnHigh >> 2) & 0x0F;
        ushort b = 0;
        if ((btnLow & 0x01) != 0) b |= 0x1000;
        if ((btnLow & 0x02) != 0) b |= 0x2000;
        if ((btnLow & 0x04) != 0) b |= 0x4000;
        if ((btnLow & 0x08) != 0) b |= 0x8000;
        if ((btnLow & 0x10) != 0) b |= 0x0100;
        if ((btnLow & 0x20) != 0) b |= 0x0200;
        if ((btnLow & 0x40) != 0) b |= 0x0040;
        if ((btnLow & 0x80) != 0) b |= 0x0080;
        if ((btnHigh & 0x01) != 0) b |= 0x0020;
        if ((btnHigh & 0x02) != 0) b |= 0x0010;
        if ((btnHigh & 0x40) != 0) b |= 0x0400;
        b |= hat switch { 1 => 0x0001, 2 => 0x0009, 3 => 0x0008, 4 => 0x000A, 5 => 0x0002, 6 => 0x0006, 7 => 0x0004, 8 => 0x0005, _ => 0 };
        ushort W(int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));
        return new Pad(b, (byte)((W(8) & 0x03FF) * 255 / 1023), (byte)((W(10) & 0x03FF) * 255 / 1023),
                       (short)(W(0) - 32768), (short)(32767 - W(2)), (short)(W(4) - 32768), (short)(32767 - W(6)));
    }

    /// <summary>xusb22's state of a 20-byte report ([MS-XUSBI] 3.2.5.1.1):
    /// the button word, the triggers and the sticks as they stand.</summary>
    static Pad WirePad(byte[] r) => new(BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(2)), r[4], r[5],
        BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(6)), BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(8)),
        BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(10)), BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(12)));

    static void Equivalence(ExtendedReportSpec report, int offset, string name)
    {
        var pack = typeof(HMController).GetMethod("PackXusbSlice", BindingFlags.NonPublic | BindingFlags.Static)
                   ?? throw new InvalidOperationException("HMController.PackXusbSlice is gone");
        var rng = new Random(20261118);
        var hats = Enum.GetValues<HMHat>();
        // The edges, then random values: every float the slots can hold
        // comes in as a float, so each is drawn as one.
        var values = new List<float> { 0f, 1f, 0.5f, 0.25f, 0.75f, 1f / 65535f, 0.49999f, 0.50001f, 1f / 255f, 0.5f / 255f, 254.5f / 255f };
        int cases = 0, wrong = 0;
        string first = "";
        for (int i = 0; i < 20000; i++)
        {
            float V() => i < values.Count * 6 && rng.Next(3) == 0 ? values[rng.Next(values.Count)] : (float)rng.NextDouble();
            float lx = V(), ly = V(), rx = V(), ry = V(), lt = V(), rt = V();
            var state = new HMGamepadState { Buttons = (HMButton)(rng.Next() & 0x7FF), Hat = hats[rng.Next(hats.Length)] };
            var wire = new byte[report.Size];
            VendorBlobCodec.EncodeInput(report, in state, lx, ly, rx, ry, lt, rt, wire, new VendorBlobCodec.EncoderState());
            var slice = new byte[14];
            pack.Invoke(null, new object[] { state, (double)lx, (double)ly, (double)rx, (double)ry, (double)lt, (double)rt, slice });
            var a = WirePad(wire.AsSpan(offset).ToArray());
            var b = CompanionPad(slice);
            cases++;
            bool header = offset == 0 ? wire[0] == 0 && wire[1] == 20
                : wire.AsSpan(0, 6).SequenceEqual(new byte[] { 0x00, 0x01, 0x00, 0xF0, 0x00, 0x13 }) && wire.Length == 29;
            if (a != b || !header)
            {
                wrong++;
                if (first.Length == 0) first = $"buttons {state.Buttons} hat {state.Hat} {lx},{ly},{rx},{ry},{lt},{rt}: wire {a} companion {b}";
            }
        }
        Check($"{name}: {cases} states, the report's XINPUT_GAMEPAD equals the UMDF2 companion's for the same slots and buttons, Guide and the d-pad included",
              wrong == 0, wrong == 0 ? "" : $"{wrong} differ, first {first}");
    }

    // ── a user's own authored device with no HID interface ───────────────

    static void Authored(IReadOnlyList<ControllerProfile> catalog)
    {
        Console.WriteLine("\n-- a user's own profile that authors a device with no HID interface --");
        string text;
        using (var s = typeof(Program).Assembly.GetManifestResourceStream("authored-xusb.json")!)
        using (var r = new StreamReader(s)) text = r.ReadToEnd();
        var loaded = ProfileDatabase.LoadTexts(new[] { ("authored-xusb.json", text) }, ModelRegistry.Catalog);
        var p = loaded.All.SingleOrDefault();
        Check("it loads", p != null, string.Join("; ", loaded.Issues.Select(i => i.Reason)));
        if (p == null) return;
        Check("it runs on USB/IP, with no descriptor, names an interface xusb, and is deployable",
              p.RequiresUsbipBackend && !p.HasDescriptor && p.ReportsOnXusbInterface && new HMProfile(p).IsDeployable);
        var pr = UsbPresentation.Resolve(p);
        var set = new UsbDescriptorSet(p);
        Check("its presentation is its own, its report its extendedReport",
              pr.Kind == UsbPresentationKind.Authored && ReferenceEquals(pr.Spec, p.UsbConfiguration) && ReferenceEquals(pr.Report, p.ExtendedReport)
              && pr.OutputPackets == null && pr.Strings.Count == 0);
        Check("no HID interface, the reports on interface 0, the compatible ID XUSB10",
              !set.HasHidInterface && set.HidInterfaceNumber == 0 && set.GetMicrosoftOsFeature(4) is { Length: 40 } c && Encoding.ASCII.GetString(c, 18, 6) == "XUSB10");

        const string Function = "$.usbConfiguration.interfaces[0].function";
        Refused("a descriptor beside an xusb interface", p, o => o["descriptor"] = "05010905a101c0", "$.descriptor", "no HID interface");
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
        Refused("xusb on an interface the configuration descriptor lacks", p, o => Usb(o)["interfaces"]![0]!["interfaceNumber"] = 1, Function, "lacks");
        Refused("an xusb device with no extended report", p, o => o.Remove("extendedReport"), "$.extendedReport", "is required for a device with no HID interface");
        Refused("an xusb device whose extended report is not always armed", p, o => o["extendedReport"]!["alwaysArmed"] = false,
                "$.extendedReport.alwaysArmed", "must be true on a device with no HID interface");
        Refused("the same device without the xusb name still needs a descriptor", p, o => Usb(o)["interfaces"]![0]!["function"] = "vendor",
                "$.descriptor", "is required for the usbip backend");
        Refused("and with a descriptor still needs a HID interface", p,
                o => { Usb(o)["interfaces"]![0]!["function"] = "vendor"; o["descriptor"] = "05010905a101c0"; },
                "$.usbConfiguration.configurationDescriptor", "has no HID interface");

        Console.WriteLine("\n-- every other USB/IP persona keeps its HID interface --");
        var others = catalog.Where(x => x.RequiresUsbipBackend && !x.ReportsOnXusbInterface).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        foreach (var other in others)
        {
            string why = "";
            try
            {
                var s = new UsbDescriptorSet(other);
                var served = s.GetHidDescriptor(0x22, s.HidInterfaceNumber);
                if (!s.HasHidInterface || !s.IsHidInterface(s.HidInterfaceNumber)) why = "has no HID interface";
                else if (served == null || !served.AsSpan().SequenceEqual(other.GetDescriptorBytes())) why = "serves another report descriptor";
                else if (s.GetHidDescriptor(0x21, s.HidInterfaceNumber) is not { Length: >= 9 }) why = "serves no HID class descriptor";
                else if (other.GetOrBuildReportBuilder().InputReportByteSize <= 0) why = "its descriptor sizes no input report";
            }
            catch (Exception e) { why = e.Message; }
            Check($"{other.Id}: a HID interface that serves the profile's descriptor", why.Length == 0, why);
        }
        var withHid = others.First(x => x.Id == "dualsense-composite");
        Refused("a HID persona without its descriptor", withHid, o => o.Remove("descriptor"), "$.descriptor", "is required for the usbip backend");
        Refused("a HID persona that names an interface xusb and keeps its descriptor", withHid,
                o => Usb(o)["interfaces"]![0]!["function"] = "xusb", "$.descriptor", "no HID interface");
    }

    /// <summary>A copy of <paramref name="basis"/> through the SDK's own
    /// JSON, which leaves the model groups out, with its resolved XInput
    /// block put back when it is the companion's.</summary>
    static JsonObject Json(ControllerProfile basis)
    {
        var o = JsonNode.Parse(JsonSerializer.Serialize(basis, HMJsonContext.Default.ControllerProfile))!.AsObject();
        o["id"] = "user-" + basis.Id;
        if (XInputPresentation.Resolve(basis).XusbCompanion && o["xinput"] == null)
            o["xinput"] = JsonNode.Parse(JsonSerializer.Serialize(XInputPresentation.Resolve(basis).Spec!, HMJsonContext.Default.XInputSpec));
        return o;
    }

    static JsonObject Usb(JsonObject o) => o["usbConfiguration"]!.AsObject();

    /// <summary>A copy of <paramref name="basis"/>, edited, must be refused
    /// at <paramref name="path"/> with <paramref name="reason"/> in the
    /// refusal. The copy goes through the SDK's own JSON, which leaves the
    /// model groups out, and the load's checks.</summary>
    static void Refused(string name, ControllerProfile basis, Action<JsonObject> edit, string path, string reason)
    {
        var o = Json(basis);
        edit(o);
        string got;
        try
        {
            var copy = ControllerProfile.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()))!;
            copy.ValidateAtLoad();
            if (copy.RequiresUsbipBackend) _ = new UsbDescriptorSet(copy);
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

    static void Wire()
    {
        Console.WriteLine("\n=== wire: a USB/IP client as the host, xbox-360-wired ===");
        var p = ProfileDatabase.LoadEmbedded().All.FirstOrDefault(x => x.Id == ProfileId);
        if (p == null) { Check("the catalog holds the profile", false); return; }
        var report = XusbFunction.Model.Functions.Single(f => f.Name == "wired").Report;
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
            // then idVendor, idProduct, bcdDevice, the class bytes, the
            // configuration value and the counts.
            Check("the import reply: full speed, 045E:028E, bcdDevice 0x0114, class FF/FF/FF, four interfaces",
                  BinaryPrimitives.ReadUInt32BigEndian(imported.AsSpan(296)) == 2
                  && imported.AsSpan(300, 12).SequenceEqual(new byte[] { 0x04, 0x5E, 0x02, 0x8E, 0x01, 0x14, 0xFF, 0xFF, 0xFF, 0x01, 0x01, 0x04 }),
                  Hex(imported.AsSpan(296, 16)));

            Console.WriteLine("\n-- descriptors and strings --");
            var dd = cl.ControlIn(0x80, 0x06, 0x0100, 0, 18);
            Check("GET_DESCRIPTOR(Device): the retail pad's", dd.Status == 0 && dd.Data.AsSpan().SequenceEqual(RetailDevice), Hex(dd.Data));
            var head = cl.ControlIn(0x80, 0x06, 0x0200, 0, 9);
            var whole = cl.ControlIn(0x80, 0x06, 0x0200, 0, 255);
            Check("GET_DESCRIPTOR(Configuration): its 9-byte header, then all 153 bytes of the retail pad's",
                  head.Status == 0 && head.Data.AsSpan().SequenceEqual(RetailConfiguration.AsSpan(0, 9))
                  && whole.Status == 0 && whole.Data.AsSpan().SequenceEqual(RetailConfiguration), Hex(whole.Data));
            Check("the device qualifier stalls, as a full-speed device's does", cl.ControlIn(0x80, 0x06, 0x0600, 0, 10).Status != 0);
            Check("string 1 is the retail manufacturer", Text(cl.ControlIn(0x80, 0x06, 0x0301, 0x0409, 255)) == RetailManufacturer);
            Check("string 2 is the retail product, Controller", Text(cl.ControlIn(0x80, 0x06, 0x0302, 0x0409, 255)) == RetailProduct);
            string serial = Text(cl.ControlIn(0x80, 0x06, 0x0303, 0x0409, 255));
            Check("string 3 is the controller's own serial", serial == DeviceIdentity.ForIndex(Index).SyntheticSerial, serial);
            Check("string 4 is the security interface's", Text(cl.ControlIn(0x80, 0x06, 0x0304, 0x0409, 255)) == RetailSecurity);
            Check("string 0xEE stalls: the retail pad serves no Microsoft OS descriptor", cl.ControlIn(0x80, 0x06, 0x03EE, 0, 18).Status != 0);
            Check("the OS feature request stalls", cl.ControlIn(0xC0, 0x20, 0, 0x0004, 16).Status != 0);
            Check("an interface's HID descriptors stall", cl.ControlIn(0x81, 0x06, 0x2200, 0, 255).Status != 0 && cl.ControlIn(0x81, 0x06, 0x2100, 0, 9).Status != 0);

            Console.WriteLine("\n-- requests --");
            Check("SET_CONFIGURATION(1) acks and reads back",
                  cl.ControlOut(0x00, 0x09, 1, 0, Array.Empty<byte>()).Status == 0
                  && cl.ControlIn(0x80, 0x08, 0, 0, 1) is { Status: 0 } cfg && cfg.Data[0] == 1);
            var status = cl.ControlIn(0x80, 0x00, 0, 0, 2);
            Check("GET_STATUS reads bus-powered, as bmAttributes 0xA0 says", status.Status == 0 && status.Data.AsSpan().SequenceEqual(new byte[] { 0, 0 }), Hex(status.Data));
            Check("the capability requests stall (C1 01, wValue 0x0100 and 0x0000)",
                  cl.ControlIn(0xC1, 0x01, 0x0100, 0, 20).Status != 0 && cl.ControlIn(0xC1, 0x01, 0x0000, 0, 8).Status != 0);
            Check("GET_DEVICE_ID stalls (C0 01)", cl.ControlIn(0xC0, 0x01, 0, 0, 4).Status != 0);
            Check("the HID class's other requests stall on interface 0: SET_IDLE, GET_REPORT(Feature)",
                  cl.ControlOut(0x21, 0x0A, 0, 0, Array.Empty<byte>()).Status != 0 && cl.ControlIn(0xA1, 0x01, 0x0300, 0, 20).Status != 0);
            Check("class requests to the headset interface stall", cl.ControlIn(0xA1, 0x01, 0x0100, 1, 20).Status != 0);

            Console.WriteLine("\n-- input report 0x00 on interrupt IN 0x81 --");
            IntPtr view = SharedMemoryIO.EnsureInputMapping(Index);
            IntPtr evt = SharedMemoryIO.GetInputEvent(Index);
            uint seqNo = (uint)Marshal.ReadInt32(view, 0);
            var atRest = new byte[20];
            atRest[1] = 0x14;
            var first = cl.ControlIn(0xA1, 0x01, 0x0100, 0, 20);
            Check("before any frame, GET_REPORT(Input) answers the pad at rest: 00 14 and eighteen zero bytes",
                  first.Status == 0 && first.Data.AsSpan().SequenceEqual(atRest), Hex(first.Data));
            var built = new byte[report.Size];
            var state = new HMGamepadState { Buttons = HMButton.A | HMButton.LeftBumper | HMButton.Guide, Hat = HMHat.North };
            VendorBlobCodec.EncodeInput(report, in state, 1f, 0f, 0f, 1f, 0.5f, 1f, built, new VendorBlobCodec.EncoderState());
            byte[] wireReport = { 0x00, 0x14, 0x01, 0x15, 0x80, 0xFF, 0xFF, 0x7F, 0xFF, 0x7F, 0x00, 0x80, 0x00, 0x80, 0, 0, 0, 0, 0, 0 };
            Check("the codec builds the 20 bytes of [MS-XUSBI] table 53 from the model's report", built.AsSpan().SequenceEqual(wireReport), Hex(built));
            uint read = cl.NextSeq();
            cl.SendSubmitInterruptIn(read, 1, 32);
            Check("a read waits while no report is new", cl.Within(200) == "nothing");
            SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, ReadOnlySpan<byte>.Empty, 0, null, extendedData: built, extendedLen: built.Length);
            var got = cl.ReadRet(read);
            Check("the waiting read gets the report, byte for byte", got.Status == 0 && got.Data.AsSpan().SequenceEqual(wireReport), Hex(got.Data));
            var again = cl.ControlIn(0xA1, 0x01, 0x0100, 0, 20);
            Check("GET_REPORT(Input) answers the report just sent ([MS-XUSBI] 2.2.8.2)", again.Status == 0 && again.Data.AsSpan().SequenceEqual(wireReport), Hex(again.Data));
            byte[] rawFrame = { 0x00, 0x14, 0x00, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, rawFrame, rawFrame.Length, null);
            read = cl.NextSeq();
            cl.SendSubmitInterruptIn(read, 1, 32);
            got = cl.ReadRet(read);
            Check("a raw frame goes out as the report it is, all 20 bytes", got.Status == 0 && got.Data.AsSpan().SequenceEqual(rawFrame), Hex(got.Data));
            Check("the raw frame length the controller holds a caller to is 20", device.RawFrameLength == 20);

            Console.WriteLine("\n-- the other interfaces --");
            foreach (uint ep in new uint[] { 2, 3, 4 })
            {
                uint parked = cl.NextSeq();
                cl.SendSubmitInterruptIn(parked, ep, 32);
            }
            SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, ReadOnlySpan<byte>.Empty, 0, null, extendedData: built, extendedLen: built.Length);
            Check("reads on 0x82, 0x83 and 0x84 park, and a report goes to none of them", cl.Within(300) == "nothing");

            Console.WriteLine("\n-- output reports on interrupt OUT 0x01 --");
            IntPtr ring = SharedMemoryIO.EnsureOutputMapping(Index);
            uint last = (uint)Marshal.ReadInt32(ring, 0);
            var buf = new byte[SharedMemoryIO.DATA_CAPACITY];
            void Out(string name, uint ep, byte[] data, byte wantSource, byte wantId, byte[]? wantData)
            {
                uint w = cl.NextSeq();
                cl.SendSubmitInterruptOut(w, ep, data);
                var ack = cl.ReadRet(w);
                bool ringed = WaitRing(ring, ref last, buf, out byte source, out byte id, out int size, wantData == null ? 300 : 1000);
                bool ok = ack.Status == 0 && ack.ActualLength == data.Length
                          && (wantData == null ? !ringed : ringed && source == wantSource && id == wantId && buf.AsSpan(0, size).SequenceEqual(wantData));
                Check(name, ok, ringed ? $"source {source} id {id:X2} data {Hex(buf.AsSpan(0, size))}" : "nothing on the ring");
            }
            Out("rumble 00 08 00 40 80 00 00 00 is the companion's packet: XInput, 00 00 40 80 02", 1,
                new byte[] { 0x00, 0x08, 0x00, 0x40, 0x80, 0x00, 0x00, 0x00 }, UsbipEmulatedDevice.SourceXInput, 0, new byte[] { 0, 0, 0x40, 0x80, 2 });
            Out("the LED 01 03 06 is the companion's packet: XInput, 00 06 00 00 01", 1,
                new byte[] { 0x01, 0x03, 0x06 }, UsbipEmulatedDevice.SourceXInput, 0, new byte[] { 0, 6, 0, 0, 1 });
            Out("a report no rule takes goes to the ring as it came: HidOutput 02, 08 03", 1,
                new byte[] { 0x02, 0x08, 0x03 }, UsbipEmulatedDevice.SourceHidOutput, 0x02, new byte[] { 0x08, 0x03 });
            Out("a short rumble report no rule takes goes as it came", 1,
                new byte[] { 0x00, 0x08, 0x00, 0x40 }, UsbipEmulatedDevice.SourceHidOutput, 0x00, new byte[] { 0x08, 0x00, 0x40 });
            Out("a write to the headset interface's OUT 0x02 is taken and carried nowhere", 2,
                new byte[] { 0x00, 0x08, 0x00, 0x40, 0x80, 0x00, 0x00, 0x00 }, 0, 0, null);
            Out("and to its OUT 0x03", 3, new byte[] { 0x01, 0x02 }, 0, 0, null);
            var set = cl.ControlOut(0x21, 0x09, 0x0200, 0, new byte[] { 0x00, 0x08, 0x00, 0x11, 0x22, 0x00, 0x00, 0x00 });
            bool ringedSet = WaitRing(ring, ref last, buf, out byte src, out byte rid, out int n, 1000);
            Check("SET_REPORT(Output) of a rumble report is the same packet ([MS-XUSBI] 2.2.8.3)",
                  set.Status == 0 && ringedSet && src == UsbipEmulatedDevice.SourceXInput && rid == 0 && buf.AsSpan(0, n).SequenceEqual(new byte[] { 0, 0, 0x11, 0x22, 2 }),
                  Hex(buf.AsSpan(0, n)));
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

    static bool WaitRing(IntPtr view, ref uint lastSeq, byte[] buf, out byte source, out byte reportId, out int size, int ms)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < ms)
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
