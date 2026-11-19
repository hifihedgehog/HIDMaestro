// gip_persona_check: the wired Xbox One S controller as a USB device on the
// USB/IP backend (profile xbox-one-s-full), which has no HID interface and
// speaks the Gaming Input Protocol. Windows binds its own GIP stack to the
// device, and the pad takes an XInput slot with no HIDMaestro driver.
//
// Three parts, each a check of its own layer:
//   model  Offline, no elevation. The catalog profile loads, its descriptor
//          set holds the device Microsoft's GIP specification describes, its
//          metadata is Santroller's with the persona's own firmware version,
//          a mis-authored copy is refused at the JSON path of its mistake,
//          and every other USB/IP persona keeps its interface.
//   wire   Elevated, no device. A USB/IP client plays the host against the
//          in-process server: every descriptor, the Microsoft OS compatible
//          ID, Hello every 500 ms until the host answers, the metadata in
//          fragments paced by the host's acknowledgements, Start, Status and
//          the first input report, an acknowledgement for a message that
//          asks for one, the input report and the Guide message on change,
//          Share in the console function map, the Direct Motor command on
//          the output ring, and Reset back to Hello.
//   live   Elevated, with the USB/IP host controller. One controller is
//          created, its devnode must carry Windows' GIP driver from
//          dc1-controller.inf, a submitted state must read back through
//          XInputGetState, the device must log the host's GIP messages raw,
//          xboxgip must hold the pad's arrival and metadata, XInputSetState
//          must arrive as the Direct Motor command and reach OutputReceived
//          in the UMDF2 form's shape, Windows.Gaming.Input must list the pad
//          once, and Share must read through the XUSB gamepad state. Trigger
//          levels from GameInput are recorded: Windows sends them for a
//          program with input focus. dualsense-composite is created beside
//          it as the positive control and read over HID.
//
// No argument runs all three. "model", "wire" and "live" each run one, and
// "live" with "nooptout" serves the metadata without the security opt-out.
// Two more run only when named. "umdf2" creates xbox-one-s on the UMDF2
// driver and prints the vibration OutputReceived raises there, beside the
// GIP form's. "capture" prints the arrival and metadata xboxgip holds for
// every GIP device, a real pad on a cable included, through \\.\XboxGIP.
// The GIP function of the twelve xinputhid profiles has four more live
// parts, each run only when named: "family" with profile ids (all twelve
// with none) creates each as its GIP form (FamilyLive.cs), and
// "nodriver", "pair" and "restart" create a GIP device no INF binds, two
// GIP pads at once, and a pad whose driver stack restarts with no port
// reset (FamilyMore.cs).
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
    const string ProfileId = "xbox-one-s-full";

    // Above every transport index a create takes (ControllerLease), and
    // beside usbip_server_check's 5000 and xusb_persona_check's 5002.
    const int Index = 5003;

    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);
    static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));

    static int Main(string[] args)
    {
        // Before any SDK type loads: the device then logs each control
        // request a host sends it, which the live part prints.
        Environment.SetEnvironmentVariable("HIDMAESTRO_DIAG", "1");
        if (args.Contains("capture")) return Capture();
        bool all = !args.Contains("model") && !args.Contains("wire") && !args.Contains("live") && !args.Contains("umdf2") && !args.Contains("family")
                   && !args.Contains("nodriver") && !args.Contains("pair") && !args.Contains("restart");
        int skip = 0;
        if (all || args.Contains("model")) Model();
        if (all || args.Contains("model")) FamilyModel();
        if (all || args.Contains("wire")) Wire();
        if (all || args.Contains("live")) skip = Live(args.Contains("nooptout"));
        // The UMDF2 comparison runs only when asked: it needs the driver.
        if (args.Contains("umdf2")) skip = Math.Max(skip, Umdf2Rumble());
        // The family's live part runs only when asked: "family", then the
        // profile ids, or none for all twelve.
        if (args.Contains("family"))
        {
            var ids = args.SkipWhile(a => a != "family").Skip(1).ToList();
            skip = Math.Max(skip, FamilyLive(ids.Count > 0 ? ids : Family.Select(f => f.Id).ToList()));
        }
        // Three more live parts of the GIP function run only when named
        // (FamilyMore.cs).
        foreach (var (name, part) in new (string, Func<int>)[] { ("nodriver", NoDriver), ("pair", Pair), ("restart", Restart) })
            if (args.Contains(name)) skip = Math.Max(skip, LiveGuard() ?? part());
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        if (s_failures != 0) return 1;
        return skip;
    }

    // The device of the GIP specification H001419, tables 34 to 38: USB 2.00, class
    // FF/47/D0, a 64-byte control endpoint, Santroller's bcdDevice 0x0100
    // (src/emulation/usb/usb_descriptors.cpp), strings 1 to 3.
    static readonly byte[] DeviceDescriptor = Bytes("12 01 00 02 FF 47 D0 40 5E 04 EA 02 00 01 01 02 03 01");

    // One configuration, bus-powered with remote wakeup and 500 mA (table 35,
    // as GP2040-CE's XBOneDescriptors.h has it), the GIP data interface with
    // interrupt IN 0x81 and interrupt OUT 0x01 of 64 bytes at 4 ms.
    static readonly byte[] ConfigurationDescriptor = Bytes(
        "09 02 20 00 01 01 00 A0 FA  09 04 00 00 02 FF 47 D0 00  07 05 81 03 40 00 04  07 05 01 03 40 00 04");

    // Table 33: 40 bytes, version 1.00, index 4, one section for interface 0
    // with the compatible ID XGIP10.
    static readonly byte[] CompatibleId = Bytes(
        "28 00 00 00 00 01 04 00 01 00 00 00 00 00 00 00 00 01 58 47 49 50 31 30 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00");

    // Santroller 65dfee6, src/emulation/usb/xone_device.cpp:174-188: the
    // metadata it serves for 045E:02EA, 218 bytes.
    static readonly byte[] Santroller = Bytes(
        "1000010000000000000000000000DA009B0016001F00200027002D004A000000" +
        "000000000000020100000001000100000601020304060705010405060A011A00" +
        "57696E646F77732E58626F782E496E7075742E47616D657061640556FF7697FD" +
        "9B8145AD45B645BBA526D62C402E08DF07E145A5ABA3127AF197B5E71FF3B886" +
        "73E940A9F82F21263ACFB7FED2DDEC87D39442BD961A712E3DC77D77CE347AE2" +
        "7DC6458CA40042C08BD94A021700202000010010000000000000000000000000" +
        "0000001700093C00010008000000000000000000000000000000");

    /// <summary>The persona's metadata: Santroller's, with the firmware version
    /// list (one byte of count and four per version, at 38) holding the
    /// persona's 0x4D48.0x0230 alone, and the five fields that follow from
    /// it: the total size, the device metadata's length, and the offsets of
    /// the sections after the list, each four less.</summary>
    static byte[] ExpectedMetadata()
    {
        var m = new List<byte>();
        m.AddRange(Santroller.AsSpan(0, 14).ToArray());
        m.AddRange(BitConverter.GetBytes((ushort)(Santroller.Length - 4)));
        var device = Santroller.AsSpan(16, 22).ToArray();
        void Less(int at) => BinaryPrimitives.WriteUInt16LittleEndian(device.AsSpan(at), (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(device.AsSpan(at)) - 4));
        Less(0);
        for (int at = 4; at <= 12; at += 2) Less(at);
        m.AddRange(device);
        m.AddRange(Bytes("01 48 4D 30 02"));
        m.AddRange(Santroller.AsSpan(16 + 22 + 9).ToArray());
        return m.ToArray();
    }

    // The Hello's payload after the device ID: VID, PID, firmware major and
    // minor (the persona's), Santroller's build 0x0C82, revision 0 and
    // hardware version 04 05, and protocol versions 1.0 three times
    // (Table 415, xone_device.cpp:170-172).
    const string HelloTail = "5E04EA02484D3002820C0000040501000100" + "0100";

    // ── model ────────────────────────────────────────────────────────────

    static ControllerProfile? s_profile;

    static ControllerProfile? Catalog()
    {
        s_profile ??= ProfileDatabase.LoadEmbedded().All.FirstOrDefault(x => x.Id == ProfileId);
        return s_profile;
    }

    /// <summary>The packets the last of <paramref name="commands"/> draws,
    /// sent in order to a new engine of <paramref name="p"/>.</summary>
    static List<byte[]> Packets(ControllerProfile p, params string[] commands)
    {
        var compiled = CompiledProtocol.Compile(p, new UsbDescriptorSet(p))!;
        var engine = new ProtocolEngine(compiled, "HM000000000000", 0, ProgramBinder.BindLayouts(compiled.Model));
        var list = new List<byte[]>();
        var buf = new byte[64];
        foreach (string command in commands)
        {
            list.Clear();
            engine.HandleCommand(Bytes(command), System.Diagnostics.Stopwatch.GetTimestamp());
            for (int n; (n = engine.TakeReply(64, buf)) >= 0;) list.Add(buf[..n]);
        }
        return list;
    }

    const string MetadataRequest = "04 20 01 00", FirstAck = "01 20 01 09 00 04 20 3A 00 00 00 9C 00", LastAck = "01 20 01 09 00 04 20 D6 00 00 00 00 00";

    /// <summary>A GIP persona's metadata as its fragments carry it, and the
    /// key Windows caches it under: VID, PID and the firmware major and minor
    /// its Hello announces.</summary>
    static (string Key, byte[] Metadata) MetadataOf(ControllerProfile p)
    {
        var frags = Packets(p, MetadataRequest).Concat(Packets(p, MetadataRequest, FirstAck)).ToList();
        var meta = frags.SelectMany(x => x.Skip(6)).ToArray();
        var compiled = CompiledProtocol.Compile(p, new UsbDescriptorSet(p))!;
        var engine = new ProtocolEngine(compiled, "HM000000000000", 0, ProgramBinder.BindLayouts(compiled.Model));
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        engine.RunTimers(t0);
        engine.RunTimers(t0 + System.Diagnostics.Stopwatch.Frequency * 2);
        var hello = new byte[64];
        int hn = engine.TakeReply(64, hello);
        string fw = hn >= 20 && hello[0] == 0x02
            ? $"{BinaryPrimitives.ReadUInt16LittleEndian(hello.AsSpan(16)):X4}.{BinaryPrimitives.ReadUInt16LittleEndian(hello.AsSpan(18)):X4}"
            : "no-hello";
        return ($"{p.VendorId:X4} {p.ProductId:X4} {fw}", meta);
    }

    /// <summary>Why <paramref name="p"/>'s metadata does not match the pin
    /// for its VID, PID and firmware version, or null when it does. Windows
    /// caches metadata by those three, so metadata that changes keeps a
    /// firmware version of its own.</summary>
    static string? PinMismatch(ControllerProfile p, Dictionary<string, string> pins)
    {
        var (key, meta) = MetadataOf(p);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(meta));
        if (!pins.TryGetValue(key, out var pinned)) return $"{p.Id}: no pin for {key}, metadata {hash}";
        return pinned == hash ? null : $"{p.Id}: {key} is pinned to {pinned}, and its metadata is now {hash}. Give the new metadata a firmware version of its own";
    }

    static Dictionary<string, string> Pins()
    {
        var pins = new Dictionary<string, string>(StringComparer.Ordinal);
        string file = Path.Combine(AppContext.BaseDirectory, "metadata-pins.txt");
        if (!File.Exists(file)) return pins;
        foreach (var line in File.ReadAllLines(file))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4) pins[$"{parts[0]} {parts[1]} {parts[2]}"] = parts[3];
        }
        return pins;
    }

    static void Model()
    {
        Console.WriteLine("=== model: the profile, its descriptor set and its metadata ===");
        var p = Catalog();
        Check("the catalog holds the profile", p != null);
        if (p == null) return;
        Check("it runs on USB/IP, with no descriptor, and names an interface gip",
              p.RequiresUsbipBackend && !p.HasDescriptor && p.ReportsOnGipInterface && !p.ReportsOnXusbInterface);
        Check("it is deployable", new HMProfile(p).IsDeployable);
        string loaded = "";
        try { p.ValidateAtLoad(); } catch (Exception e) { loaded = e.Message; }
        Check("it meets every load check", loaded.Length == 0, loaded);

        UsbDescriptorSet? set = null;
        try { set = new UsbDescriptorSet(p); } catch (Exception e) { Check("its descriptor set builds", false, e.Message); }
        if (set != null)
        {
            Check("device descriptor, 18 bytes of H001419's table 34",
                  set.DeviceDescriptor.AsSpan().SequenceEqual(DeviceDescriptor), Hex(set.DeviceDescriptor));
            Check("configuration descriptor, 32 bytes: the GIP data interface of H001419's tables 35 to 38",
                  set.ConfigurationDescriptor.AsSpan().SequenceEqual(ConfigurationDescriptor), Hex(set.ConfigurationDescriptor));
            Check("no HID interface, and the messages travel on interface 0, function gip",
                  !set.HasHidInterface && set.HidInterfaceNumber == 0 && set.ReportFunction == "gip" && !set.IsHidInterface(0) && set.SecondaryHidInterfaces.Count == 0);
            Check("no HID descriptor of either type is served",
                  set.GetHidDescriptor(0x22, 0) == null && set.GetHidDescriptor(0x21, 0) == null);
            Check("full speed, so the device qualifier stalls (the specification's USB Device Qualifier)", set.Speed == 2 && set.GetDescriptor(0x06, 0, 0) == null);
            Check("interrupt IN 0x81 and interrupt OUT 0x01, 64 bytes each, at 4 ms",
                  set.Endpoints.Count == 2
                  && set.Endpoints[0x81] is { TransferType: 3, MaxPacketSize: 64, Interval: 4, InterfaceNumber: 0 }
                  && set.Endpoints[0x01] is { TransferType: 3, MaxPacketSize: 64, Interval: 4, InterfaceNumber: 0 });
            Check("the compatible ID is XGIP10 for interface 0, and the extended properties stall",
                  set.GetMicrosoftOsFeature(4) is { } c && c.AsSpan().SequenceEqual(CompatibleId) && set.GetMicrosoftOsFeature(5) == null);
            Check("string 0xEE carries vendor code 0x90, the specification's default (table 32)",
                  set.GetDescriptor(0x03, 0xEE, 0) is { Length: 18 } os && os[16] == 0x90 && os[17] == 0x00);
        }

        Console.WriteLine("\n-- the metadata and the Hello --");
        Check("Santroller's blob is 218 bytes, as its header says", Santroller.Length == 218 && Santroller[14] == 0xDA, $"{Santroller.Length}");
        var expected = ExpectedMetadata();
        var first = Packets(p, MetadataRequest);
        var rest = Packets(p, MetadataRequest, FirstAck);
        var done = Packets(p, MetadataRequest, FirstAck, LastAck);
        var again = Packets(p, MetadataRequest, FirstAck, FirstAck);
        Check("a Metadata Request draws one fragment, 04 F0 with the request's sequence, total 214 as D6 01",
              first.Count == 1 && first[0].Length == 64 && Hex(first[0].AsSpan(0, 6)) == "04F0013AD601", first.Count > 0 ? Hex(first[0].AsSpan(0, 6)) : "none");
        Check("the host's acknowledgement of 58 bytes draws the other three fragments: offsets 3A, 74 and AE 01",
              rest.Count == 3 && Hex(rest[0].AsSpan(0, 6)) == "04A001BA003A" && Hex(rest[1].AsSpan(0, 6)) == "04A001BA0074"
              && Hex(rest[2].AsSpan(0, 6)) == "04B00128AE01" && rest[2].Length == 46,
              string.Join(" ", rest.Select(x => Hex(x.AsSpan(0, 6)))));
        Check("the acknowledgement of all 214 bytes draws the completion, 04 A0 with length 0 and D6 01",
              done.Count == 1 && Hex(done[0]) == "04A00100D601", done.Count > 0 ? Hex(done[0]) : "none");
        Check("a second acknowledgement of the first fragment draws nothing", again.Count == 0, string.Join(" ", again.Select(x => Hex(x.AsSpan(0, 6)))));
        var assembled = first.Concat(rest).SelectMany(x => x.Skip(6)).ToArray();
        Check("the fragments carry Santroller's metadata with the persona's firmware version alone",
              assembled.AsSpan().SequenceEqual(expected), $"{assembled.Length} bytes, first difference at {FirstDifference(assembled, expected)}");
        Check("its firmware version list is 0x4D48.0x0230 alone, at the offset its device metadata gives",
              assembled.Length > 47 && BinaryPrimitives.ReadUInt16LittleEndian(assembled.AsSpan(18)) == 22 && Hex(assembled.AsSpan(38, 5)) == "01484D3002");
        var compiled = CompiledProtocol.Compile(p, new UsbDescriptorSet(p))!;
        var engine = new ProtocolEngine(compiled, "HM0123456789AB", 0, ProgramBinder.BindLayouts(compiled.Model));
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        engine.RunTimers(t0);
        engine.RunTimers(t0 + System.Diagnostics.Stopwatch.Frequency / 2);
        var hello = new byte[64];
        int hn = engine.TakeReply(64, hello);
        Check("the Hello: 02 20, sequence 1, 28 bytes, the device ID (the serial's last eight hex digits, then 00 00 FF FB as the top bytes), then 045E:02EA and the persona's firmware 0x4D48.0x0230",
              hn == 32 && Hex(hello.AsSpan(0, hn)) == "0220011C" + "AB896745FBFF0000" + HelloTail, hn > 0 ? Hex(hello.AsSpan(0, hn)) : "none");

        // Quiesce clears the motors: the consumer gets a report with every
        // level at 0 ([MS-GIPUSB] 3.1.5.6.1, Quiesce).
        {
            var qc = CompiledProtocol.Compile(p, new UsbDescriptorSet(p))!;
            var qe = new ProtocolEngine(qc, "HM0123456789AB", 0, ProgramBinder.BindLayouts(qc.Model));
            var quiesce = Bytes("05 20 07 01 05");
            var qr = qe.HandleCommand(quiesce, System.Diagnostics.Stopwatch.GetTimestamp());
            var qbuf = new byte[64];
            int qn = qr.Emit >= 0 ? qe.BuildEmit(qr.Emit, quiesce, qbuf) : -1;
            Check("Quiesce emits a report with every motor level at 0: 0F, then seven zero bytes",
                  qn == 8 && Hex(qbuf.AsSpan(0, 8)) == "0F00000000000000", qn < 0 ? "no report" : Hex(qbuf.AsSpan(0, qn)));
        }

        // No acknowledgement within a second of a fragment that asks for one
        // sends the device back to Hello ([MS-GIPUSB] 3.1.5.2).
        {
            var mc = CompiledProtocol.Compile(p, new UsbDescriptorSet(p))!;
            var me = new ProtocolEngine(mc, "HM0123456789AB", 0, ProgramBinder.BindLayouts(mc.Model));
            var mbuf = new byte[64];
            long m0 = System.Diagnostics.Stopwatch.GetTimestamp();
            long Ms(double ms) => m0 + (long)(ms * System.Diagnostics.Stopwatch.Frequency / 1000);
            me.HandleCommand(Bytes(MetadataRequest), m0);
            while (me.TakeReply(64, mbuf) >= 0) { }
            me.RunTimers(m0);
            me.RunTimers(Ms(999));
            bool early = me.TakeReply(64, mbuf) >= 0;
            // The device clock ticks every 4 ms: the timeout at 1000 ms, the
            // Hello's schedule from the next tick, its first 500 ms after.
            me.RunTimers(Ms(1000));
            me.RunTimers(Ms(1004));
            me.RunTimers(Ms(1504));
            int hn2 = me.TakeReply(64, mbuf);
            Check("with no acknowledgement of the first fragment within a second, the device sends Hello again 500 ms later",
                  !early && hn2 == 32 && mbuf[0] == 0x02, early ? "a message before the second" : hn2 < 0 ? "nothing" : Hex(mbuf.AsSpan(0, hn2)));
        }

        // Windows caches metadata by VID, PID and firmware version, so
        // metadata that changes needs a firmware version of its own. Each
        // persona's metadata is pinned under its key, and a copy with one
        // metadata byte changed and the same version is refused.
        Console.WriteLine("\n-- metadata pinned to its firmware version --");
        var pins = Pins();
        var personas = new List<ControllerProfile> { p };
        string local = Path.Combine(AppContext.BaseDirectory, "profiles", "xbox-one-s-full-no-opt-out.json");
        if (File.Exists(local) && ControllerProfile.Parse(File.ReadAllBytes(local)) is { } variant) personas.Add(variant);
        foreach (var persona in personas)
        {
            string? why = PinMismatch(persona, pins);
            Check($"{persona.Id}: its metadata matches the pin for its VID, PID and firmware version", why == null, why ?? MetadataOf(persona).Key);
        }
        var bumped = JsonNode.Parse(Source(p))!;
        var frag = bumped["protocol"]!["commands"]!["rules"]!.AsArray().First(r => r!["match"]!.GetValue<string>().StartsWith("04", StringComparison.Ordinal))!["reply"]!.AsArray();
        string tail = frag[^1]!["hex"]!.GetValue<string>();
        frag[^1]!["hex"] = tail[..^2] + (tail[^2..] == "00" ? "01" : "00");
        var changed = ControllerProfile.Parse(Encoding.UTF8.GetBytes(bumped.ToJsonString()))!;
        Check("a copy with one metadata byte changed and the same firmware version is refused by the pin", PinMismatch(changed, pins) != null, PinMismatch(changed, pins) ?? "matched");
        Check("the Hello's firmware major and minor are the metadata's one supported version",
              hn == 32 && hello.AsSpan(16, 4).SequenceEqual(assembled.AsSpan(39, 4)));

        Console.WriteLine("\n-- a mis-authored copy is refused at its mistake --");
        const string Function = "$.usbConfiguration.interfaces[0].function";
        Refused("a descriptor beside a gip interface", p,
                o => o["descriptor"] = "05010905a101c0",
                "$.descriptor", "no HID interface");
        Refused("gip on an interface that is not class 0xFF, subclass 0x47, protocol 0xD0", p,
                o => Usb(o)["configurationDescriptor"] = Usb(o)["configurationDescriptor"]!.GetValue<string>().Replace("ff47d000", "ff47d100"),
                Function, "protocol 0xD0");
        Refused("gip on an interface with no interrupt OUT endpoint", p,
                o =>
                {
                    Usb(o)["configurationDescriptor"] = Usb(o)["configurationDescriptor"]!.GetValue<string>().Replace("070501034000", "070582034000");
                    Usb(o)["interfaces"]![0]!["altSettings"]![0]!["endpoints"]![1]!["address"] = 130;
                },
                Function, "interrupt OUT");
        Refused("gip and xusb both named", p,
                o =>
                {
                    var second = JsonNode.Parse(Usb(o)["interfaces"]![0]!.ToJsonString())!.AsObject();
                    second["function"] = "xusb";
                    second["interfaceNumber"] = 1;
                    Usb(o)["interfaces"]!.AsArray().Add(second);
                },
                "$.usbConfiguration.interfaces[0].function", "each mark the one interface");
        Refused("the same device without the gip name still needs a descriptor", p,
                o => Usb(o)["interfaces"]![0]!["function"] = "vendor",
                "$.descriptor", "is required for the usbip backend");

        Console.WriteLine("\n-- a copy outside the catalog --");
        string Presentation(Action<JsonObject>? edit)
        {
            var o = JsonNode.Parse(Source(p))!.AsObject();
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
        Refused("a gip device that builds no reports", p,
                o => { o.Remove("inputReports"); o.Remove("stateBlock"); },
                "$.inputReports", "is required for a device that reports on a GIP interface");

        Console.WriteLine("\n-- every other USB/IP persona keeps its interface --");
        var catalog = ProfileDatabase.LoadEmbedded().All;
        var others = catalog.Where(x => x.RequiresUsbipBackend && x.Id != ProfileId).OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        Check("the catalog's other USB/IP personas", others.Count >= 9, string.Join(" ", others.Select(x => x.Id)));
        foreach (var other in others)
        {
            string why = "";
            try
            {
                var s = new UsbDescriptorSet(other);
                if (other.ReportsOnGipInterface) why = "names an interface gip";
                else if (other.ReportsOnXusbInterface) { if (s.HasHidInterface || s.ReportFunction != "xusb") why = "an xusb persona that is not one"; }
                else if (!s.HasHidInterface || s.ReportFunction != null) why = "has no HID interface";
                else if (s.GetHidDescriptor(0x22, s.HidInterfaceNumber) is not { } served || !served.AsSpan().SequenceEqual(other.GetDescriptorBytes())) why = "serves another report descriptor";
            }
            catch (Exception e) { why = e.Message; }
            Check($"{other.Id}: keeps its own interface", why.Length == 0, why);
        }
    }

    static string FirstDifference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return $"byte {i}: {a[i]:X2} against {b[i]:X2}";
        return a.Length == b.Length ? "none" : $"lengths {a.Length} and {b.Length}";
    }

    static JsonObject Usb(JsonObject o) => o["usbConfiguration"]!.AsObject();

    /// <summary>The catalog file's text, model groups included.</summary>
    static string Source(ControllerProfile p)
    {
        var asm = typeof(HMContext).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)))
        {
            using var st = asm.GetManifestResourceStream(res)!;
            using var sr = new StreamReader(st, Encoding.UTF8);
            string t = sr.ReadToEnd();
            if (JsonNode.Parse(t) is JsonObject o && (string?)o["id"] == p.Id) return t;
        }
        throw new InvalidOperationException($"no catalog file for {p.Id}");
    }

    /// <summary>A copy of <paramref name="basis"/>'s file, edited, must be
    /// refused at <paramref name="path"/> with <paramref name="reason"/> in
    /// the refusal.</summary>
    static void Refused(string name, ControllerProfile basis, Action<JsonObject> edit, string path, string reason)
    {
        var o = JsonNode.Parse(Source(basis))!.AsObject();
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

    static void Wire()
    {
        Console.WriteLine("\n=== wire: a USB/IP client as the host ===");
        var p = Catalog();
        if (p == null) { Check("the catalog holds the profile", false); return; }
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
            Check("the import reply: full speed, 045E:02EA, bcdDevice 0x0100, class FF/47/D0, one configuration, one interface",
                  BinaryPrimitives.ReadUInt32BigEndian(imported.AsSpan(296)) == 2
                  && imported.AsSpan(300, 12).SequenceEqual(Bytes("04 5E 02 EA 01 00 FF 47 D0 01 01 01")),
                  Hex(imported.AsSpan(296, 16)));
            var attached = System.Diagnostics.Stopwatch.StartNew();

            Console.WriteLine("\n-- descriptors and requests --");
            var dd = cl.ControlIn(0x80, 0x06, 0x0100, 0, 18);
            Check("GET_DESCRIPTOR(Device)", dd.Status == 0 && dd.Data.AsSpan().SequenceEqual(DeviceDescriptor), Hex(dd.Data));
            var whole = cl.ControlIn(0x80, 0x06, 0x0200, 0, 255);
            Check("GET_DESCRIPTOR(Configuration), all 32 bytes", whole.Status == 0 && whole.Data.AsSpan().SequenceEqual(ConfigurationDescriptor), Hex(whole.Data));
            Check("the device qualifier stalls", cl.ControlIn(0x80, 0x06, 0x0600, 0, 10).Status != 0);
            Check("string 1 is Microsoft, string 2 Controller",
                  Text(cl.ControlIn(0x80, 0x06, 0x0301, 0x0409, 255)) == "Microsoft" && Text(cl.ControlIn(0x80, 0x06, 0x0302, 0x0409, 255)) == "Controller");
            string serial = Text(cl.ControlIn(0x80, 0x06, 0x0303, 0x0409, 255));
            Check("string 3 is the controller's own serial", serial == DeviceIdentity.ForIndex(Index).SyntheticSerial, serial);
            var os = cl.ControlIn(0x80, 0x06, 0x03EE, 0, 18);
            Check("string 0xEE is MSFT100 with vendor code 0x90", os.Status == 0 && os.Data.Length == 18 && os.Data[16] == 0x90, Hex(os.Data));
            var compat = cl.ControlIn(0xC0, 0x90, 0, 0x0004, 40);
            Check("the extended compatible ID, all 40 bytes, XGIP10", compat.Status == 0 && compat.Data.AsSpan().SequenceEqual(CompatibleId), Hex(compat.Data));
            Check("the extended properties stall", cl.ControlIn(0xC0, 0x90, 0, 0x0005, 10).Status != 0 && cl.ControlIn(0xC1, 0x90, 0, 0x0005, 10).Status != 0);
            Check("SET_CONFIGURATION(1) acks and reads back",
                  cl.ControlOut(0x00, 0x09, 1, 0, Array.Empty<byte>()).Status == 0 && cl.ControlIn(0x80, 0x08, 0, 0, 1) is { Status: 0 } cfg && cfg.Data[0] == 1);
            var status = cl.ControlIn(0x80, 0x00, 0, 0, 2);
            Check("GET_STATUS reads bus-powered, as bmAttributes 0xA0 says", status.Status == 0 && status.Data.AsSpan().SequenceEqual(new byte[] { 0, 0 }), Hex(status.Data));
            Check("every class request on the GIP interface stalls: GET_REPORT, SET_REPORT, SET_IDLE",
                  cl.ControlIn(0xA1, 0x01, 0x0100, 0, 64).Status != 0
                  && cl.ControlOut(0x21, 0x09, 0x0200, 0, new byte[] { 0, 1 }).Status != 0
                  && cl.ControlOut(0x21, 0x0A, 0, 0, Array.Empty<byte>()).Status != 0);

            Console.WriteLine("\n-- Hello until the host answers --");
            // The Hello's cadence is the timer's, which S95 checks on a
            // virtual clock. Here a read waits as long as a hang takes to
            // show on the slowest machine, and the times are printed.
            int hang = TimeoutScale.Apply(5_000);
            var h1 = cl.ReadIn(hang);
            Check("the first IN packet is the Hello, sequence 1", h1 is { Length: 32 } && Hex(h1.AsSpan(0, 4)) == "0220011C",
                  h1 == null ? "nothing" : $"{Hex(h1)} at {attached.ElapsedMilliseconds} ms");
            Check("its device ID's four most significant bytes are 00 00 FF FB ([MS-GIPUSB] 2.2.1.3), and the other four the serial's last eight hex digits low byte first",
                  h1 is { Length: 32 } && h1.AsSpan(4, 8).SequenceEqual(ExpectedDeviceId(serial)), h1 == null ? "" : Hex(h1.AsSpan(4, 8)));
            Check("its tail is 045E:02EA and the persona's versions", h1 is { Length: 32 } && Hex(h1.AsSpan(12)) == HelloTail, h1 == null ? "" : Hex(h1.AsSpan(12)));
            var tw = System.Diagnostics.Stopwatch.StartNew();
            var h2 = cl.ReadIn(hang);
            Check("the next Hello follows, sequence 2", h2 is { Length: 32 } && h2[2] == 2,
                  h2 == null ? "nothing" : $"{Hex(h2.AsSpan(0, 4))} after {tw.ElapsedMilliseconds} ms");

            Console.WriteLine("\n-- metadata, paced by the host's acknowledgements --");
            cl.Out(1, Bytes("04 20 01 00"));
            var f1 = cl.ReadIn(1_000);
            Check("the Metadata Request draws the first fragment, 04 F0 01 3A D6 01", f1 is { Length: 64 } && Hex(f1.AsSpan(0, 6)) == "04F0013AD601", f1 == null ? "nothing" : Hex(f1.AsSpan(0, 6)));
            Check("nothing more before the host acknowledges it", cl.ReadIn(300) == null);
            cl.Out(1, Bytes("01 20 01 09 00 04 20 3A 00 00 00 9C 00"));
            var f2 = cl.ReadIn(1_000);
            var f3 = cl.ReadIn(1_000);
            var f4 = cl.ReadIn(1_000);
            Check("the acknowledgement of 58 bytes draws the other three, the last asking for an acknowledgement",
                  f2 != null && f3 != null && f4 != null && f2[1] == 0xA0 && f3[1] == 0xA0 && f4[1] == 0xB0 && f4.Length == 46,
                  string.Join(" ", new[] { f2, f3, f4 }.Select(x => x == null ? "none" : Hex(x.AsSpan(0, 6)))));
            Check("nothing more before the host acknowledges the last", cl.ReadIn(300) == null);
            cl.Out(1, Bytes("01 20 01 09 00 04 20 3A 00 00 00 9C 00"));
            Check("a second acknowledgement of the first fragment draws nothing", cl.ReadIn(300) == null);
            cl.Out(1, Bytes("01 20 01 09 00 04 20 D6 00 00 00 00 00"));
            var f5 = cl.ReadIn(1_000);
            Check("the acknowledgement of all 214 bytes draws the completion", f5 != null && Hex(f5) == "04A00100D601", f5 == null ? "nothing" : Hex(f5));
            var meta = new[] { f1, f2, f3, f4 }.Where(x => x != null).SelectMany(x => x!.Skip(6)).ToArray();
            Check("the four fragments carry the persona's metadata whole", meta.AsSpan().SequenceEqual(ExpectedMetadata()), $"{meta.Length} bytes");
            Check("no Hello once the host has asked for the metadata", cl.ReadIn(1_200) == null);

            Console.WriteLine("\n-- Start, Status and the first input report --");
            IntPtr view = SharedMemoryIO.EnsureInputMapping(Index);
            IntPtr evt = SharedMemoryIO.GetInputEvent(Index);
            uint seqNo = (uint)Marshal.ReadInt32(view, 0);
            cl.Out(1, Bytes("05 20 02 01 00"));
            var a = cl.ReadIn(1_000);
            var b = cl.ReadIn(1_000);
            byte[]? statusMsg = new[] { a, b }.FirstOrDefault(x => x != null && x[0] == 0x03);
            byte[]? input = new[] { a, b }.FirstOrDefault(x => x != null && x[0] == 0x20);
            Check("Start draws a Status: 03 20, 4 bytes, full power with no battery (0x80), device inactive",
                  statusMsg != null && statusMsg.Length == 8 && statusMsg[1] == 0x20 && Hex(statusMsg.AsSpan(3)) == "0480000000", statusMsg == null ? "none" : Hex(statusMsg));
            Check("and the first input report, the pad at rest: 20 00, sequence 1, 32 bytes, sticks centered and nothing pressed",
                  input != null && input.Length == 36 && Hex(input.AsSpan(0, 4)) == "20000120" && input.AsSpan(4).IndexOfAnyExcept((byte)0) < 0, input == null ? "none" : Hex(input));
            // From here a Status goes out every second for ten seconds. The
            // reads below set those aside and count them.
            cl.Skip = x => x.Length == 8 && x[0] == 0x03 && x[4] == 0x80;
            var started = System.Diagnostics.Stopwatch.StartNew();
            Check("then nothing while nothing changes", cl.ReadIn(300) == null);

            Console.WriteLine("\n-- an acknowledgement for a message that asks for one --");
            cl.Out(1, Bytes("0A 30 03 03 00 01 14"));
            var ack = cl.ReadIn(1_000);
            Check("the Guide LED command with the acknowledge flag draws 01 20 03 09 00 0A 20 03 00 00 00 00 00",
                  ack != null && Hex(ack) == "01200309000A2003000000" + "0000", ack == null ? "nothing" : Hex(ack));

            Console.WriteLine("\n-- input, Guide and Share --");
            var spec = p.StateBlockSpecForCodec();
            byte[] Block(HMGamepadState s, float lx, float ly, float rx, float ry, float lt, float rt)
            {
                var block = new byte[spec.Size];
                VendorBlobCodec.EncodeInput(spec, in s, lx, ly, rx, ry, lt, rt, block, new VendorBlobCodec.EncoderState());
                return block;
            }
            void Submit(byte[] block) => SharedMemoryIO.WriteInputFrame(view, evt, ref seqNo, ReadOnlySpan<byte>.Empty, 0, null, extendedData: block, extendedLen: block.Length);
            var pressed = Block(new HMGamepadState { Buttons = HMButton.A | HMButton.LeftBumper | HMButton.Back }, 1f, 0f, 0f, 1f, 0.5f, 1f);
            Submit(pressed);
            var r1 = cl.ReadIn(1_000);
            short S(byte[] r, int at) => BinaryPrimitives.ReadInt16LittleEndian(r.AsSpan(at));
            ushort U(byte[] r, int at) => BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(at));
            Check("a change sends the next report: A and View in the low button byte, the left bumper in the high, sequence 2",
                  r1 != null && Hex(r1.AsSpan(0, 4)) == "20000220" && r1[4] == 0x18 && r1[5] == 0x10, r1 == null ? "nothing" : Hex(r1));
            Check("the triggers run 0 to 1023 and the sticks -32768 to 32767 with up positive (Table 468)",
                  r1 != null && U(r1, 6) is >= 511 and <= 512 && U(r1, 8) == 1023 && S(r1, 10) == 32767 && S(r1, 12) == 32767 && S(r1, 14) == -32768 && S(r1, 16) == -32768,
                  r1 == null ? "" : $"LT {U(r1, 6)} RT {U(r1, 8)} LX {S(r1, 10)} LY {S(r1, 12)} RX {S(r1, 14)} RY {S(r1, 16)}");
            Submit(pressed);
            Check("the same state again sends nothing", cl.ReadIn(300) == null);
            var guide = Block(new HMGamepadState { Buttons = HMButton.A | HMButton.LeftBumper | HMButton.Back | HMButton.Guide }, 1f, 0f, 0f, 1f, 0.5f, 1f);
            Submit(guide);
            var g1 = cl.ReadIn(1_000);
            Check("Guide goes out as its own message, 07 20 with the global sequence, 02 01 5B, and the input report stays",
                  g1 != null && g1.Length == 6 && g1[0] == 0x07 && g1[1] == 0x20 && g1[2] != 0 && Hex(g1.AsSpan(3)) == "02015B" && cl.ReadIn(300) == null,
                  g1 == null ? "nothing" : Hex(g1));
            var share = Block(new HMGamepadState { Buttons = HMButton.Share }, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f);
            Submit(share);
            var s1 = cl.ReadIn(1_000);
            var s2 = cl.ReadIn(1_000);
            byte[]? shareInput = new[] { s1, s2 }.FirstOrDefault(x => x != null && x[0] == 0x20);
            byte[]? guideUp = new[] { s1, s2 }.FirstOrDefault(x => x != null && x[0] == 0x07);
            Check("Share sets the console function map's first byte to 0x01, after the 14-byte report (tables 471 and 472)",
                  shareInput != null && shareInput[4] == 0 && shareInput[5] == 0 && shareInput[18] == 0x01 && shareInput.AsSpan(19).IndexOfAnyExcept((byte)0) < 0,
                  shareInput == null ? "nothing" : Hex(shareInput));
            Check("and Guide's release goes out as 07 20 .. 02 00 5B", guideUp != null && Hex(guideUp.AsSpan(3)) == "02005B", guideUp == null ? "nothing" : Hex(guideUp));
            // Guide and Status number from the global pool, so a Status
            // sent between the two Guide messages takes the number between.
            var pool = new List<byte>();
            bool inWindow = false;
            foreach (var m in cl.Seen)
            {
                if (ReferenceEquals(m, g1)) inWindow = true;
                if (inWindow && (m[0] == 0x07 || m[0] == 0x03)) pool.Add(m[2]);
                if (ReferenceEquals(m, guideUp)) break;
            }
            bool counts = g1 != null && guideUp != null && pool.Count >= 2 && pool[^1] == guideUp[2]
                          && pool.Zip(pool.Skip(1)).All(x => x.Second == (byte)(x.First == 0xFF ? 1 : x.First + 1));
            Check("the system messages number from one pool, each the next, which never gives 0",
                  counts, string.Join(" ", pool.Select(x => x.ToString("X2"))));

            Console.WriteLine("\n-- the Direct Motor command on interrupt OUT 0x01 --");
            IntPtr ring = SharedMemoryIO.EnsureOutputMapping(Index);
            uint last = (uint)Marshal.ReadInt32(ring, 0);
            var buf = new byte[SharedMemoryIO.DATA_CAPACITY];
            var motor = Bytes("09 00 04 09 00 0F 64 32 19 0A FF 00 00");
            cl.Out(1, motor);
            bool ringed = WaitRing(ring, ref last, buf, out byte source, out byte id, out int size);
            Check("it reaches the output ring as xinputhid's HID output report on UMDF2: the motor bitmap 0x0F as the report ID, then its seven bytes",
                  ringed && source == UsbipEmulatedDevice.SourceHidOutput && id == 0x0F && buf.AsSpan(0, size).SequenceEqual(motor.AsSpan(6)),
                  $"source {source} id {id:X2} data {Hex(buf.AsSpan(0, size))}");
            Check("and the device's command log holds it raw, beside the host's earlier messages",
                  device.Protocol!.SnapshotLog().Select(x => Hex(x.Command)).Contains(Hex(motor)));
            cl.Out(1, Bytes("0A 20 05 03 00 01 14"));
            Check("the Guide LED command without the flag draws nothing and stays off the ring",
                  cl.ReadIn(300) == null && !WaitRing(ring, ref last, buf, out _, out _, out _, 100));

            Console.WriteLine("\n-- Quiesce --");
            cl.Out(1, Bytes("05 20 07 01 05"));
            bool quiet = WaitRing(ring, ref last, buf, out byte qSource, out byte qId, out int qSize);
            Check("Quiesce raises a report with every motor level at 0: the motor bitmap 0x0F as the report ID, then zeros",
                  quiet && qSource == UsbipEmulatedDevice.SourceHidOutput && qId == 0x0F && qSize == 7 && buf.AsSpan(0, qSize).IndexOfAnyExcept((byte)0) < 0,
                  quiet ? $"source {qSource} id {qId:X2} data {Hex(buf.AsSpan(0, qSize))}" : "nothing on the ring");

            // The Status cadence is the timer's, checked in S95. Here the
            // count is printed, and a hang shows as none.
            double secs = started.Elapsed.TotalSeconds;
            Check("meanwhile Status messages went out", cl.Skipped >= 1, $"{cl.Skipped} in {secs:F1} s");

            // A Status already waiting goes out ahead of the Reset's own.
            Console.WriteLine("\n-- Reset brings the Hellos back --");
            cl.Out(1, Bytes("05 20 06 01 07"));
            var off = cl.ReadIn(1_000);
            Check("Reset draws a powering-off Status, 03 20 .. 04 00 00 00 00", off != null && off[0] == 0x03 && Hex(off.AsSpan(3)) == "0400000000", off == null ? "nothing" : Hex(off));
            var again = cl.ReadIn(hang);
            Check("and then a Hello", again != null && again[0] == 0x02 && again.Length == 32, again == null ? "nothing" : Hex(again.AsSpan(0, 4)));

            // A host that asks for the metadata and never acknowledges the
            // first fragment: within a second the device goes back to Hello
            // ([MS-GIPUSB] 3.1.5.2).
            Console.WriteLine("\n-- no acknowledgement of the metadata --");
            cl.Out(1, Bytes("04 20 08 00"));
            var lone = cl.ReadIn(1_000);
            Check("the Metadata Request draws the first fragment", lone is { Length: 64 } && lone[0] == 0x04, lone == null ? "nothing" : Hex(lone.AsSpan(0, 6)));
            var tm = System.Diagnostics.Stopwatch.StartNew();
            var back = cl.ReadIn(hang);
            Check("with no acknowledgement the device sends Hello again", back is { Length: 32 } && back[0] == 0x02,
                  back == null ? "nothing" : $"{Hex(back.AsSpan(0, 4))} after {tm.ElapsedMilliseconds} ms");
        }
        catch (Exception e) { Check("the wire part ran to its end", false, e.ToString()); }
        finally
        {
            server.Unregister(device);
            device.Dispose();
            SharedMemoryIO.DestroyController(Index);
        }
    }

    /// <summary>The Hello's device ID for <paramref name="serial"/>: its
    /// last eight hex digits low byte first, then FB FF 00 00, so the four
    /// most significant bytes are 00 00 FF FB ([MS-GIPUSB] 2.2.1.3).</summary>
    internal static byte[] ExpectedDeviceId(string serial)
    {
        var id = Bytes("00000000FBFF0000");
        string hex = serial[^8..];
        for (int i = 0; i < 4; i++) id[i] = Convert.ToByte(hex.Substring(6 - 2 * i, 2), 16);
        return id;
    }

    static string Text(Client.Ret r) =>
        r.Status == 0 && r.Data.Length >= 2 && r.Data[1] == 0x03 ? Encoding.Unicode.GetString(r.Data, 2, r.Data[0] - 2) : $"status {r.Status}";

    static bool WaitRing(IntPtr view, ref uint lastSeq, byte[] buf, out byte source, out byte reportId, out int size, int ms = 1000)
    {
        for (int waited = 0; waited < ms; waited += 2)
        {
            if (SharedMemoryIO.TryReadOutputFrame(view, ref lastSeq, out source, out reportId, out size, buf)) return true;
            Thread.Sleep(2);
        }
        source = 0; reportId = 0; size = 0;
        return false;
    }

    /// <summary>A USB/IP client that plays the host controller's part: the
    /// import, control transfers and interrupt transfers
    /// (usbip_protocol.rst), as xusb_persona_check's does. It keeps one
    /// interrupt IN read on endpoint 1 parked, as a host keeps polling.</summary>
    sealed class Client : IDisposable
    {
        readonly TcpClient _tcp;
        readonly NetworkStream _s;
        readonly uint _devid;
        uint _seq = 100;
        readonly Dictionary<uint, Ret> _stash = new();
        readonly HashSet<uint> _pendingIn = new();
        uint _parked;

        /// <summary>Packets <see cref="ReadIn"/> sets aside, counted in
        /// <see cref="Skipped"/>.</summary>
        public Func<byte[], bool>? Skip;
        public int Skipped;

        /// <summary>Every IN packet read, the skipped ones included, in the
        /// order the device sent them.</summary>
        public readonly List<byte[]> Seen = new();

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

        /// <summary>An interrupt OUT transfer on <paramref name="ep"/>, answered.</summary>
        public void Out(uint ep, byte[] data)
        {
            uint seq = NextSeq();
            SendHeader(seq, 0, ep, data.Length, 4, 0);
            _s.Write(data);
            var r = ReadRet(seq);
            if (r.Status != 0) throw new InvalidOperationException($"OUT {Convert.ToHexString(data)} failed with status {r.Status}");
        }

        /// <summary>The next packet on interrupt IN 1 within
        /// <paramref name="ms"/>, or null. One read stays parked between
        /// calls, so a packet the device sends meanwhile waits here.</summary>
        public byte[]? ReadIn(int ms)
        {
            if (_parked == 0)
            {
                _parked = NextSeq();
                _pendingIn.Add(_parked);
                SendHeader(_parked, 1, 1, 64, 4, 0);
            }
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (_stash.Remove(_parked, out var hit))
                {
                    _parked = 0;
                    Seen.Add(hit.Data);
                    if (Skip != null && Skip(hit.Data))
                    {
                        Skipped++;
                        _parked = NextSeq();
                        _pendingIn.Add(_parked);
                        SendHeader(_parked, 1, 1, 64, 4, 0);
                        continue;
                    }
                    return hit.Data;
                }
                int left = ms - (int)clock.ElapsedMilliseconds;
                if (left <= 0) return null;
                if (!_tcp.Client.Poll(left * 1000, SelectMode.SelectRead)) return null;
                ReadOne();
            }
        }

        public Ret ReadRet(uint seqnum)
        {
            while (true)
            {
                if (_stash.Remove(seqnum, out var hit)) return hit;
                ReadOne();
            }
        }

        void ReadOne()
        {
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
            if (_pendingIn.Remove(seq) && r.ActualLength > 0) r.Data = ReadExactly(r.ActualLength);
            _stash[seq] = r;
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
