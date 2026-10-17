// S79: the Wii U GameCube adapter persona presents a real WUP-028's bytes.
//
// The catalog part reads the three 057E:0337 profiles: gamecube-adapter is
// the USB/IP persona with the real report descriptor, gamecube-adapter-hid
// carries the same 214 bytes, joypad-gc-adapter carries joypad-os's 70, and
// exactly one of the three is named Full. Every profile in the catalog must
// also compile as a device model: its ports, its USB/IP protocol and its
// WinUSB package.
//
// The device part reads the same bytes three ways and compares each with
// the real adapter's (GcCommon.cs, docs/design/v1.13.0.md):
//  - what the persona serves, from the USB/IP device's own descriptor set.
//  - what Windows enumerates, through the hub the device sits on, before
//    any driver loads. usbip-win2 gives UdeCx the device's own speed, full
//    here (v0.9.8.1 drivers/ude/device.cpp to_udex_speed), and the hub
//    reports it at high speed, as USBHUB3 treats every UDE device. For a
//    device below high speed usbip-win2 rewrites each interrupt bInterval
//    to the same period in microframes on every full configuration read
//    (drivers/ude/wsk_receive.cpp patch_config and to_high_speed_interval):
//    8 ms becomes bInterval 7. That is the one difference allowed.
//  - what libusb reads, through WinUSB once BindWinUsb has bound it: the
//    strings, the report descriptor and the stalls. usbhub3 forwards no
//    descriptor request to a device that failed to start, and a real
//    adapter fails to start on input.inf, so these need the binding.
// The device must also land where a real adapter does: the serial-named
// instance when no other adapter is present, on input.inf's HID driver,
// failing with Code 10.
//
// The malformed part copies gamecube-adapter with one mistake each and checks
// that the device model refuses every copy at create, before anything
// machine-wide, with an InvalidOperationException naming the profile, the
// JSON path and the limit broken: an unknown field type or semantic, a field
// wider than its block or report, a report larger than the extended lane, a
// command rule longer than the OUT endpoint's packets, a null where a value
// belongs, and their siblings. A HID descriptor string with an odd number
// of hex digits, or a character that is not one, is refused the same way
// with a FormatException that names the profile and the descriptor field.
//
// Members newer than b4d29d9 are reached by reflection, so the probe builds
// against b4d29d9 and fails there at run time.
//
// "--offline" runs the catalog and malformed parts alone, which create no
// device and need no elevation. Otherwise requires elevation. Exit 0 PASS,
// 1 FAIL, 2 when another program that embeds HIDMaestro is running.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using HIDMaestro;

internal static class Program
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    static int Main(string[] args)
    {
        bool offline = args.Contains("--offline");
        Console.WriteLine("=== S79: the GameCube adapter persona's descriptors ===");
        if (Probe.OtherSdkRunning()) return 2;
        if (!offline && Probe.RefuseWithoutDepartingRule("S79")) return Probe.Finish("S79");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        Console.WriteLine("-- catalog");
        var adapters = ctx.AllProfiles.Where(p => p.VendorId == 0x057E && p.ProductId == 0x0337).ToList();
        Probe.Check("three 057E:0337 profiles", adapters.Count == 3,
            string.Join(", ", adapters.Select(p => p.Id)));
        var full = adapters.Where(p => p.Name.Contains("Full", StringComparison.Ordinal)).Select(p => p.Id).ToList();
        Probe.Check("exactly one of them is named Full, gamecube-adapter",
            full.Count == 1 && full[0] == "gamecube-adapter", string.Join(", ", full));

        var gc = ctx.GetProfile("gamecube-adapter");
        Probe.Check("gamecube-adapter is a USB/IP persona", gc != null && gc.RequiresUsbipBackend && gc.IsDeployable);
        Probe.Check("gamecube-adapter carries the real report descriptor",
            gc?.DescriptorHex != null && Convert.FromHexString(gc.DescriptorHex).AsSpan().SequenceEqual(GcReal.ReportDescriptor));
        Probe.Check("gamecube-adapter's strings are Nintendo and WUP-028",
            gc != null && gc.ManufacturerString == GcReal.Manufacturer && gc.ProductString == GcReal.Product,
            gc == null ? "" : $"\"{gc.ManufacturerString}\" \"{gc.ProductString}\"");
        Probe.Check("gamecube-adapter's layout is a controller adapter with four ports",
            gc?.AsControllerAdapter()?.Ports == 4, $"{gc?.Layout?.Kind}");

        var hid = ctx.GetProfile("gamecube-adapter-hid");
        Probe.Check("gamecube-adapter-hid carries the real report descriptor",
            hid?.DescriptorHex != null && Convert.FromHexString(hid.DescriptorHex).AsSpan().SequenceEqual(GcReal.ReportDescriptor));
        Probe.Check("gamecube-adapter-hid's manufacturer string is Nintendo",
            hid?.ManufacturerString == GcReal.Manufacturer, $"\"{hid?.ManufacturerString}\"");

        var joypad = ctx.GetProfile("joypad-gc-adapter");
        byte[] joypadRd = joypad?.DescriptorHex != null ? Convert.FromHexString(joypad.DescriptorHex) : Array.Empty<byte>();
        Probe.Check("joypad-gc-adapter carries joypad-os's 70-byte descriptor",
            joypadRd.AsSpan().SequenceEqual(GcReal.JoypadOsReportDescriptor), $"{joypadRd.Length} bytes");

        CheckCatalogCompiles(ctx);
        CheckMalformedRefused(ctx);
        CheckWideMaskDecodes();
        if (offline) return Probe.Finish("S79");
        CheckCreateRefusedFirst(ctx);

        Console.WriteLine("-- device");
        if (gc == null || !gc.IsDeployable)
        {
            Probe.Check("create the persona", false, "gamecube-adapter is not deployable");
            return Probe.Finish("S79");
        }

        var before = Devnodes.PresentAdapters();
        HMController controller;
        try { controller = ctx.CreateController(gc); }
        catch (Exception ex)
        {
            Probe.Check("create the persona", false, $"{ex.GetType().Name}: {ex.Message}");
            return Probe.Finish("S79");
        }

        using (controller)
        {
            CheckServed(controller);

            string? id = null;
            Probe.WaitFor(() => (id = Devnodes.PresentAdapters().Except(before, StringComparer.OrdinalIgnoreCase).FirstOrDefault()) != null, 10_000);
            Probe.Check("the adapter arrives as a USB device", id != null, id ?? "");
            if (id == null) return Probe.Finish("S79");

            if (before.Count == 0)
                Probe.Check("it is the serial-named instance a real adapter gets",
                    string.Equals(id, GcReal.SerialInstance, StringComparison.OrdinalIgnoreCase), id);
            else
                Probe.Check("beside another adapter it gets a port-named instance, as a second real one does",
                    !string.Equals(id, GcReal.SerialInstance, StringComparison.OrdinalIgnoreCase), id);

            // Windows binds input.inf's HID driver, which cannot parse the
            // report descriptor: Code 10, as on a real adapter.
            Probe.WaitFor(() => Devnodes.Status(id) is { Problem: 10 }, 10_000);
            string? inf = Devnodes.InfPath(id);
            var status = Devnodes.Status(id);
            Probe.Check("Windows gives it input.inf's HID driver",
                inf != null && inf.EndsWith("input.inf", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Devnodes.Service(id), "HidUsb", StringComparison.OrdinalIgnoreCase),
                $"{inf}, {Devnodes.Service(id)}");
            Probe.Check("which fails with Code 10, as on a real adapter", status is { Problem: 10 },
                status == null ? "absent" : $"problem {status.Value.Problem}");

            CheckEnumerated(id);
            CheckThroughWinUsb(controller, id);
        }

        Probe.Check("the device leaves with the controller",
            Probe.WaitFor(() => Devnodes.PresentAdapters().Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 10_000));
        return Probe.Finish("S79");
    }

    /// <summary>Every catalog profile compiles as a device model, the check a
    /// create runs before it takes anything machine-wide.</summary>
    static void CheckCatalogCompiles(HMContext ctx)
    {
        var inner = typeof(HMProfile).GetProperty("Inner", Any);
        var validate = inner?.PropertyType.GetMethod("ValidateDeviceModel", Any);
        if (inner == null || validate == null)
        {
            Probe.Check("every catalog profile compiles as a device model", false, "ValidateDeviceModel is absent");
            return;
        }
        int count = 0;
        var failures = new StringBuilder();
        foreach (var p in ctx.AllProfiles)
        {
            count++;
            try { validate.Invoke(inner.GetValue(p), null); }
            catch (TargetInvocationException ex) { failures.Append($" {p.Id}: {ex.InnerException?.Message}"); }
        }
        Probe.Check($"every catalog profile compiles as a device model ({count})", failures.Length == 0, failures.ToString());
    }

    /// <summary>One mistake each in a copy of gamecube-adapter, and the text
    /// its refusal must carry after the profile's name.</summary>
    static readonly (string Name, Action<JsonObject> Break, string Expect)[] s_malformed =
    {
        // An unknown field type or semantic.
        ("a port field of an unknown type",
            j => PortField(j, 2)["type"] = "uint8-axes",
            "extendedReport.ports.fields[2].type 'uint8-axes' is not a field type"),
        ("a port field with an unknown semantic",
            j => PortField(j, 2)["semantic"] = "leftStickZ",
            "extendedReport.ports.fields[2] ('uint8-axis').semantic 'leftStickZ' names no value the SDK has"),
        ("a report field of an unknown type",
            j => In(j)["fields"] = Arr("""{"byte":1,"type":"uint8-axes"}"""),
            "extendedReport.fields[0].type 'uint8-axes' is not a field type"),
        ("a report field with an unknown semantic",
            j => In(j)["fields"] = Arr("""{"byte":1,"type":"int16-le","semantic":"gyroPitchh"}"""),
            "extendedReport.fields[0] ('int16-le').semantic 'gyroPitchh' names no value the SDK has"),
        ("a bitfield naming an unknown flag",
            j => In(j)["fields"] = Arr("""{"byte":1,"type":"bitfield","bits":"0-1","buttons":["batteryChraging"]}"""),
            "extendedReport.fields[0] ('bitfield').buttons[0] 'batteryChraging' is not a flag"),
        ("a button name the SDK cannot express, named with the profile",
            j => PortField(j, 0)["buttons"]![0] = "Turbo",
            "extendedReport.ports: Button name 'Turbo' at index 0 of the button mask at byte 1 resolves to no HMButton"),
        ("an armOn trigger of an unknown type",
            j => In(j)["armOn"] = Arr("""{"type":"featureWrit","reportId":"0x05"}"""),
            "extendedReport.armOn[0].type 'featureWrit' is not one of featureWrite, outputWrite, featureRead"),

        // A field wider than its block or report.
        ("a uint16 on a port block's last byte",
            j =>
            {
                // max as v1 implied it, so the field is refused for its
                // width and not for the key v2.0.0 requires.
                PortField(j, 7)["type"] = "uint16-trigger";
                PortField(j, 7)["max"] = 32767;
            },
            "extendedReport.ports.fields[7] ('uint16-trigger') at bytes 8-9 runs past the 9-byte port block"),
        ("a uint16 that ends past the block's data bytes",
            j =>
            {
                Ports(j)["data"] = "1-7";
                Ports(j)["fields"]!.AsArray().RemoveAt(7);
                PortField(j, 6)["type"] = "uint16-trigger";
                PortField(j, 6)["max"] = 32767;
            },
            "extendedReport.ports.fields[6] ('uint16-trigger') at bytes 7-8 is outside the block's data bytes 1-7"),
        ("an output port field read past its 1-byte block",
            j => OutPortField(j, 0)["type"] = "uint16-le",
            "extendedOutputReport.ports.fields[0] ('uint16-le') at bytes 0-1 runs past the 1-byte port block"),
        ("an output field past the report's end",
            j => Out(j)["fields"] = Arr("""{"byte":4,"type":"uint16-le","semantic":"x"}"""),
            "extendedOutputReport.fields[0] ('uint16-le') at bytes 4-5 runs past the 5-byte report"),
        ("a report field with no byte",
            j => In(j)["fields"] = Arr("""{"type":"uint8-axis","semantic":"leftStickX"}"""),
            "extendedReport.fields[0] ('uint8-axis') needs byte"),
        ("a bytes range that does not parse",
            j => Out(j)["fields"] = Arr("""{"bytes":"2","type":"bytes-passthrough","semantic":"x"}"""),
            "extendedOutputReport.fields[0].bytes \"2\" is not a range"),
        ("a bytes range that runs backward",
            j => Out(j)["fields"] = Arr("""{"bytes":"4-2","type":"bytes-passthrough","semantic":"x"}"""),
            "extendedOutputReport.fields[0].bytes \"4-2\" is not a range"),
        ("a CRC destination that is not four bytes",
            j => In(j)["fields"] = Arr("""{"bytes":"1-2","type":"crc32-le","scope":{"prefix":[],"from":0,"to":30}}"""),
            "extendedReport.fields[0] ('crc32-le').bytes \"1-2\" must name the four bytes the CRC is written to"),
        ("an rgb24 field of two bytes",
            j => Out(j)["fields"] = Arr("""{"bytes":"1-2","type":"rgb24","semantic":"lightbar"}"""),
            "extendedOutputReport.fields[0] ('rgb24').bytes \"1-2\" must name three bytes"),
        ("a CRC with no scope",
            j => In(j)["fields"] = Arr("""{"bytes":"1-4","type":"crc32-le"}"""),
            "extendedReport.fields[0] ('crc32-le') needs scope"),
        ("a CRC scope that starts past the report",
            j => In(j)["fields"] = Arr("""{"bytes":"1-4","type":"crc32-le","scope":{"prefix":[],"from":40,"to":50}}"""),
            "extendedReport.fields[0] ('crc32-le').scope 40-50 does not start inside the 37-byte report"),
        ("an output field past the output ring's 256 data bytes",
            j => { Out(j)["size"] = 300; Out(j)["fields"] = Arr("""{"byte":280,"type":"uint8","semantic":"x"}"""); },
            "extendedOutputReport.fields[0] ('uint8') at bytes 280-280 ends past byte 256"),
        ("bits on a type that reads whole bytes",
            j => PortField(j, 2)["bits"] = "0-3",
            "extendedReport.ports.fields[2] ('uint8-axis').bits: this type reads whole bytes"),
        ("bits past a single byte",
            j => OutPortField(j, 0)["bits"] = "0-9",
            "extendedOutputReport.ports.fields[0] ('uint8').bits \"0-9\" reaches bit 9. This type holds bits 0 to 7"),
        ("bits that do not parse",
            j => PortField(j, 0)["bits"] = "4",
            "extendedReport.ports.fields[0].bits \"4\" is not a range"),
        ("more buttons than bits",
            j => PortField(j, 0)["bits"] = "0-3",
            "extendedReport.ports.fields[0] ('button-mask') names 8 buttons for bits 0-3, which hold 4"),
        ("a button mask with no buttons",
            j => PortField(j, 1).Remove("buttons"),
            "extendedReport.ports.fields[1] ('button-mask') needs buttons"),
        ("a report ID that is not hex",
            j => In(j)["reportId"] = "0xZZ",
            "extendedReport.reportId \"0xZZ\" is not a hex byte"),
        ("an output report of no bytes",
            j => { Out(j).Remove("ports"); Out(j)["size"] = 0; },
            "extendedOutputReport.size 0 must be 1 or more"),
        // Connections that cannot be told apart. Each of these created at
        // a7e5015, and the first Connect or submit threw
        // IndexOutOfRangeException or showed a port as empty.
        ("a status block with only the empty connection",
            j => Ports(j)["status"]!["connections"] = Arr("""{"name":"empty","value":"0x00"}"""),
            "extendedReport.ports.status declares 1 connection. The first is the empty port, and a port needs a second to hold a controller"),
        ("a connection whose value repeats another's",
            j => Ports(j)["status"]!["connections"]![3]!["value"] = "0x10",
            "extendedReport.ports.status.connections[3].value 0x10 repeats connections[1]'s. Each connection needs its own status bits"),
        ("a connection whose value is the empty port's",
            j => Ports(j)["status"]!["connections"]![1]!["value"] = "0x00",
            "extendedReport.ports.status.connections[1].value 0x00 is the empty port's, connections[0]'s, so a port holding it would read as empty"),

        // Port blocks on byte 0. Each created at a7e5015: port 1's status
        // byte went out where the input report's ID belongs, and an output
        // block on byte 0 was never read.
        ("input port blocks from byte 0, the report ID's",
            j => Ports(j)["offset"] = 0,
            "extendedReport.ports.offset 0 puts port 1's block on byte 0, which holds report ID 0x21. The blocks start at byte 1 or later"),
        ("output port blocks from byte 0, the report ID's",
            j => OutPorts(j)["offset"] = 0,
            "extendedOutputReport.ports.offset 0 puts port 1's block on byte 0, which every output path hands the SDK as the report ID. The blocks start at byte 1 or later"),
        ("output port blocks from byte 0 of an output report with no ID",
            j => { Out(j)["reportId"] = ""; OutPorts(j)["offset"] = 0; },
            "extendedOutputReport.ports.offset 0 puts port 1's block on byte 0, which every output path hands the SDK as the report ID. The blocks start at byte 1 or later"),

        // Fields beside the port blocks.
        ("a report field beside the ports",
            j => In(j)["fields"] = Arr("""{"byte":1,"type":"uint8-axis","semantic":"leftStickX"}"""),
            "extendedReport.fields[0] ('uint8-axis') is beside ports. A report with ports is written from its port blocks alone, so the field would never be written. Move it into ports.fields"),
        ("an output field over a port block",
            j => Out(j)["fields"] = Arr("""{"byte":2,"type":"uint8","semantic":"x"}"""),
            "extendedOutputReport.fields[0] ('uint8') at bytes 2-2 overlaps the port blocks at bytes 1-4"),
        ("an output field past the OUT endpoint's packet",
            j => { Out(j)["size"] = 6; Out(j)["fields"] = Arr("""{"byte":5,"type":"uint8","semantic":"x"}"""); },
            "extendedOutputReport.fields[0] ('uint8') ends at byte 6, past the 5-byte packets usbConfiguration.protocol.commands.endpoint 0x02 delivers"),

        ("a port count that wraps an int",
            j =>
            {
                // 2^30 blocks of 4 bytes wrap an int to 0, which "fits".
                j.Remove("extendedOutputReport");
                var ports = Ports(j);
                ports["count"] = 1 << 30;
                ports["stride"] = 4;
                ports["data"] = "1-3";
                ports["fields"] = Arr("""{"byte":1,"type":"uint8-axis","semantic":"leftStickX"}""");
                Rule(j, 2)["reply"]![1]!["bytes"] = "1-3";
            },
            "extendedReport.ports: 1073741824 ports of 4 bytes from offset 1 do not fit a 37-byte report"),

        // A report larger than the extended lane, or than what carries it.
        ("a ports report larger than the extended lane",
            j => In(j)["size"] = 81,
            "extendedReport.size 81 is larger than the 80 bytes the extended report lane carries"),
        ("an output report whose ports pass the output ring's slot",
            j => { Out(j)["size"] = 300; OutPorts(j)["stride"] = 70; },
            "extendedOutputReport.ports end at byte 281. An output report reaches the SDK with at most 256 bytes after its report ID"),
        ("an output report whose ports pass the OUT endpoint's packet",
            j => { Out(j)["size"] = 9; OutPorts(j)["stride"] = 2; },
            "extendedOutputReport.ports end at byte 9, past the 5-byte packets usbConfiguration.protocol.commands.endpoint 0x02 delivers"),
        ("more ports than the protocol tracks",
            j =>
            {
                j.Remove("extendedOutputReport");
                In(j)["size"] = 67;
                var ports = Ports(j);
                ports["count"] = 33;
                ports["stride"] = 2;
                ports["data"] = "1-1";
                ports["fields"] = Arr("""{"byte":1,"type":"uint8-axis","semantic":"leftStickX"}""");
                Rule(j, 2)["reply"]![1]!["bytes"] = "1-1";
            },
            "extendedReport.ports.count 33: a device with a protocol has at most 32 ports"),
        ("a low-speed copy with a 37-byte interrupt endpoint",
            j => j["usbConfiguration"]!["busSpeed"] = "low",
            "usbConfiguration.busSpeed \"low\": interface 0 alternate setting 0 endpoint 0x81 has wMaxPacketSize 0x0025, over the 8 bytes a low-speed interrupt endpoint carries"),
        ("a low-speed copy whose 8-byte endpoint sets wMaxPacketSize bits 11-12",
            j =>
            {
                var usb = j["usbConfiguration"]!.AsObject();
                usb["busSpeed"] = "low";
                usb["configurationDescriptor"] = usb["configurationDescriptor"]!.GetValue<string>()
                    .Replace("0705810325000807", "0705810308080807", StringComparison.OrdinalIgnoreCase);
                usb["interfaces"]![0]!["altSettings"]![0]!["endpoints"]![0]!["maxPacketSize"] = 0x0808;
            },
            "usbConfiguration.busSpeed \"low\": interface 0 alternate setting 0 endpoint 0x81 has wMaxPacketSize 0x0808, over the 8 bytes a low-speed interrupt endpoint carries"),
        ("a low-speed copy whose alternate setting 0 has a 64-byte endpoint and setting 1 an 8-byte one",
            j =>
            {
                var usb = j["usbConfiguration"]!.AsObject();
                usb["busSpeed"] = "low";
                usb["configurationDescriptor"] =
                    "09024000010100e0fa" +
                    "090400000203000000" + "09211001000122d600" + "07058103400008" + "07050203050008" +
                    "090400010203000000" + "07058103080008" + "07050203050008";
                var alts = usb["interfaces"]![0]!["altSettings"]!.AsArray();
                alts[0]!["endpoints"]![0]!["maxPacketSize"] = 64;
                var alt1 = JsonNode.Parse(alts[0]!.ToJsonString())!.AsObject();
                alt1["altSetting"] = 1;
                alt1["endpoints"]![0]!["maxPacketSize"] = 8;
                alts.Add(alt1);
            },
            "usbConfiguration.busSpeed \"low\": interface 0 alternate setting 0 endpoint 0x81 has wMaxPacketSize 0x0040, over the 8 bytes a low-speed interrupt endpoint carries"),
        ("a full-speed copy whose alternate setting 0 has a 64-byte endpoint and setting 1 an 8-byte one",
            j =>
            {
                var usb = j["usbConfiguration"]!.AsObject();
                usb["configurationDescriptor"] =
                    "09024000010100e0fa" +
                    "090400000203000000" + "09211001000122d600" + "07058103400008" + "07050203050008" +
                    "090400010203000000" + "07058103080008" + "07050203050008";
                var alts = usb["interfaces"]![0]!["altSettings"]!.AsArray();
                alts[0]!["endpoints"]![0]!["maxPacketSize"] = 64;
                var alt1 = JsonNode.Parse(alts[0]!.ToJsonString())!.AsObject();
                alt1["altSetting"] = 1;
                alt1["endpoints"]![0]!["maxPacketSize"] = 8;
                alts.Add(alt1);
            },
            "configurationDescriptor endpoint 0x81 is interrupt, wMaxPacketSize 0x0040, bInterval 8, in interface 0 alternate setting 0, and interrupt, wMaxPacketSize 0x0008, bInterval 8, in interface 0 alternate setting 1. An interrupt or bulk endpoint keeps one descriptor across alternate settings"),
        ("endpoint 0x81 interrupt in alternate setting 0 and isochronous in setting 1",
            j => TwoSettings(j, ("81", 0x03, 0x40, 8), ("81", 0x01, 0x40, 1)),
            "configurationDescriptor endpoint 0x81 is interrupt, wMaxPacketSize 0x0040, bInterval 8, in interface 0 alternate setting 0, and isochronous, wMaxPacketSize 0x0040, bInterval 1, in interface 0 alternate setting 1. An endpoint keeps one transfer type across alternate settings"),
        ("endpoint 0x81 isochronous in alternate setting 0 and interrupt in setting 1",
            j => TwoSettings(j, ("81", 0x01, 0x40, 1), ("81", 0x03, 0x40, 8)),
            "configurationDescriptor endpoint 0x81 is isochronous, wMaxPacketSize 0x0040, bInterval 1, in interface 0 alternate setting 0, and interrupt, wMaxPacketSize 0x0040, bInterval 8, in interface 0 alternate setting 1. An endpoint keeps one transfer type across alternate settings"),
        ("isochronous endpoint 0x83 in both alternate settings, 64 bytes then 32",
            j => TwoSettings(j, ("81", 0x03, 0x25, 8), ("83", 0x01, 0x40, 1), ("81", 0x03, 0x25, 8), ("83", 0x01, 0x20, 1)),
            "configurationDescriptor endpoint 0x83 is isochronous, wMaxPacketSize 0x0040, bInterval 1, in interface 0 alternate setting 0, and isochronous, wMaxPacketSize 0x0020, bInterval 1, in interface 0 alternate setting 1. The device serves an isochronous endpoint from the one alternate setting that declares it"),
        ("isochronous endpoint 0x83 in both alternate settings, alike",
            j => TwoSettings(j, ("81", 0x03, 0x25, 8), ("83", 0x01, 0x40, 1), ("81", 0x03, 0x25, 8), ("83", 0x01, 0x40, 1)),
            "configurationDescriptor endpoint 0x83 is isochronous, wMaxPacketSize 0x0040, bInterval 1, in interface 0 alternate setting 0, and isochronous, wMaxPacketSize 0x0040, bInterval 1, in interface 0 alternate setting 1. The device serves an isochronous endpoint from the one alternate setting that declares it"),

        // A command rule longer than the OUT endpoint's packets.
        ("a rule match longer than a packet",
            j => Rule(j, 0)["match"] = "110000000000",
            "usbConfiguration.protocol.commands.rules[0].match is 6 bytes, longer than the 5-byte packets endpoint 0x02 delivers"),
        ("a rule minLength longer than a packet",
            j => Rule(j, 0)["minLength"] = 6,
            "usbConfiguration.protocol.commands.rules[0].minLength 6 is longer than the 5-byte packets endpoint 0x02 delivers"),
        ("a report ID strip with no SET_REPORT commands",
            j => Protocol(j)["commands"]!["setReport"] = false,
            "usbConfiguration.protocol.commands.setReportStripsId applies only with setReport true"),

        // Protocol values outside their range.
        ("no replies allowed to wait",
            j => Protocol(j)["commands"]!["pendingReplies"] = 0,
            "usbConfiguration.protocol.commands.pendingReplies 0 must be 1 or more"),
        ("a GET_REPORT answer that is not defined",
            j => Protocol(j)["getReport"] = "stal",
            "usbConfiguration.protocol.getReport \"stal\" is not one of input, reportId"),

        // A null where a value belongs.
        ("a null match",
            j => Rule(j, 3)["match"] = null,
            "usbConfiguration.protocol.commands.rules[3].match is missing"),
        ("a null in a while list",
            j => Protocol(j)["input"]!["while"] = Arr("\"polling\"", "null"),
            "usbConfiguration.protocol.input.while holds null where a state name belongs"),
        ("a null halt.while",
            j => Protocol(j)["halt"]!["while"] = null,
            "usbConfiguration.protocol.halt.while names no state"),
        ("a null reply part",
            j => Rule(j, 2)["reply"] = Arr("""{"hex":"22"}""", "null"),
            "usbConfiguration.protocol.commands.rules[2].reply[1] is null"),
        ("a null rule",
            j => Protocol(j)["commands"]!["rules"] = Arr("null"),
            "usbConfiguration.protocol.commands.rules[0] is null"),
        ("null rules",
            j => Protocol(j)["commands"]!["rules"] = null,
            "usbConfiguration.protocol.commands.rules is null"),
        ("a null state",
            j => Protocol(j)["state"]![0] = null,
            "usbConfiguration.protocol.state[0] is null"),
        ("null state",
            j => Protocol(j)["state"] = null,
            "usbConfiguration.protocol.state is null"),
        ("a null input endpoint",
            j => Protocol(j)["input"]!["endpoint"] = null,
            "usbConfiguration.protocol.input.endpoint is missing"),
        ("a null portsConnected",
            j => Protocol(j)["input"]!["clock"]!["sourcePollRules"]![0]!["portsConnected"] = null,
            "usbConfiguration.protocol.input.clock.sourcePollRules[0].portsConnected is null"),
        ("a null connection value",
            j => Ports(j)["status"]!["connections"]![1]!["value"] = null,
            "extendedReport.ports.status.connections[1].value is missing"),
        ("a null connection",
            j => Ports(j)["status"]!["connections"]![2] = null,
            "extendedReport.ports.status.connections[2] is null"),
        ("null connections",
            j => Ports(j)["status"]!["connections"] = null,
            "extendedReport.ports.status declares no connections"),
        ("a null flag",
            j => Ports(j)["status"]!["flags"] = Arr("null"),
            "extendedReport.ports.status.flags[0] is null"),
        ("a null snapshot",
            j => Ports(j)["snapshots"] = Arr("null"),
            "extendedReport.ports.snapshots[0] is null"),
        ("null port fields",
            j => Ports(j)["fields"] = null,
            "extendedReport.ports.fields is null"),
        ("a null port field",
            j => Ports(j)["fields"]![3] = null,
            "extendedReport.ports.fields[3] is null"),
    };

    /// <summary>A HID descriptor string that is not whole hex bytes, and the
    /// text its FormatException must carry after the profile's name. Before
    /// this release an odd count dropped the last digit and loaded the
    /// profile a byte short.</summary>
    static readonly (string Name, Func<string, string> Break, string Expect)[] s_badHex =
    {
        ("a HID descriptor with an odd number of hex digits",
            d => d + "0", "descriptor has an odd number of hex digits"),
        ("a HID descriptor with a character that is not a hex digit",
            d => "zz" + d, "descriptor holds a character that is not a hex digit"),
    };

    /// <summary>Copies that must still compile, as they did at af1e3d4,
    /// because the codec writes them correctly: its defined fallbacks and
    /// the bounds it clamps. A report with ports is written from its port
    /// blocks alone, so the copies that test a report's own fields drop
    /// the ports and the protocol that reads them first.</summary>
    static readonly (string Name, Action<JsonObject> Change)[] s_accepted =
    {
        ("an unaltered copy of gamecube-adapter", j => { }),
        ("a CRC scope past the report's end, which the codec stops at the end",
            j => { NoPorts(j); In(j)["fields"] = Arr("""{"bytes":"1-4","type":"crc32-le","scope":{"prefix":[],"from":0,"to":99}}"""); }),
        ("touchpad-finger with no semantic, finger 0",
            j => { NoPorts(j); In(j)["fields"] = Arr("""{"byte":1,"type":"touchpad-finger"}"""); }),
        ("touchpad-finger with another semantic, finger 0",
            j => { NoPorts(j); In(j)["fields"] = Arr("""{"byte":1,"type":"touchpad-finger","semantic":"touchpad"}"""); }),
        ("stick12-pair with no semantic, the left stick",
            j => { NoPorts(j); In(j)["fields"] = Arr("""{"byte":1,"type":"stick12-pair"}"""); }),
        ("uint8-axis with no semantic, the center",
            j => { NoPorts(j); In(j)["fields"] = Arr("""{"byte":1,"type":"uint8-axis"}"""); }),
        ("uint8-axis with an empty semantic, the center",
            j => { NoPorts(j); In(j)["fields"] = Arr("""{"byte":1,"type":"uint8-axis","semantic":""}"""); }),
        ("an output CRC with no place in a report past the ring, read at the received end",
            j => { NoPorts(j); Out(j)["size"] = 300; Out(j)["fields"] = Arr("""{"type":"crc32-le","scope":{"prefix":[],"from":0,"to":20}}"""); }),
        ("uint8-axis naming another type's value, the center",
            j => { NoPorts(j); In(j)["fields"] = Arr("""{"byte":1,"type":"uint8-axis","semantic":"leftTrigger"}"""); }),
        ("int16-axis with no semantic, the center",
            j => { NoPorts(j); In(j)["fields"] = Arr("""{"byte":1,"type":"int16-axis"}"""); }),
        ("uint16-le with no semantic, zero",
            j => { NoPorts(j); In(j)["fields"] = Arr("""{"byte":1,"type":"uint16-le"}"""); }),
        ("an output button mask of 16 bits",
            j => { NoPorts(j); Out(j)["fields"] = Arr(WideMask); }),
        ("port blocks from byte 0 of a report with no ID",
            j => { In(j)["reportId"] = ""; Ports(j)["offset"] = 0; }),
        ("output ports past one OUT packet, which a SET_REPORT carries whole",
            j => { Protocol(j)["commands"]!["setReportStripsId"] = false; Out(j)["size"] = 9; OutPorts(j)["stride"] = 2; }),
        ("an output field past the port blocks, on a device with no protocol",
            j =>
            {
                j["usbConfiguration"]!.AsObject().Remove("protocol");
                Out(j)["size"] = 6;
                Out(j)["fields"] = Arr("""{"byte":5,"type":"uint8","semantic":"x"}""");
            }),
    };

    /// <summary>Give interface 0 two alternate settings. The first
    /// endpoints listed go in setting 0 and the rest, as many, in setting 1,
    /// each beside endpoint 0x02 as the real adapter has it: (address,
    /// bmAttributes, wMaxPacketSize, bInterval). The configuration
    /// descriptor and the structured spec agree.</summary>
    static void TwoSettings(JsonObject j, params (string Address, int Attributes, int MaxPacket, int Interval)[] eps)
    {
        int half = eps.Length / 2;
        var usb = j["usbConfiguration"]!.AsObject();
        string Ep((string Address, int Attributes, int MaxPacket, int Interval) e) =>
            $"0705{e.Address}{e.Attributes:x2}{e.MaxPacket & 0xFF:x2}{e.MaxPacket >> 8:x2}{e.Interval:x2}";
        var alt0 = new StringBuilder();
        var alt1 = new StringBuilder();
        for (int i = 0; i < half; i++) alt0.Append(Ep(eps[i]));
        for (int i = half; i < eps.Length; i++) alt1.Append(Ep(eps[i]));
        string i0 = $"09040000{half + 1:x2}03000000" + "09211001000122d600" + alt0 + "07050203050008";
        string i1 = $"09040001{half + 1:x2}03000000" + alt1 + "07050203050008";
        int total = 9 + i0.Length / 2 + i1.Length / 2;
        usb["configurationDescriptor"] = $"0902{total & 0xFF:x2}{total >> 8:x2}010100e0fa" + i0 + i1;
        var alts = usb["interfaces"]![0]!["altSettings"]!.AsArray();
        JsonArray Endpoints(int from, int to)
        {
            var arr = new JsonArray();
            for (int i = from; i < to; i++)
            {
                var e = eps[i];
                arr.Add(new JsonObject
                {
                    ["address"] = Convert.ToInt32(e.Address, 16),
                    ["transferType"] = (e.Attributes & 3) == 1 ? "isochronous" : "interrupt",
                    ["maxPacketSize"] = e.MaxPacket,
                    ["interval"] = e.Interval,
                });
            }
            arr.Add(JsonNode.Parse("""{"address":2,"transferType":"interrupt","maxPacketSize":5,"interval":8}"""));
            return arr;
        }
        alts[0]!["endpoints"] = Endpoints(0, half);
        var second = JsonNode.Parse(alts[0]!.ToJsonString())!.AsObject();
        second["altSetting"] = 1;
        second["endpoints"] = Endpoints(half, eps.Length);
        alts.Add(second);
    }

    /// <summary>A copy of gamecube-adapter with one report of its own, no
    /// ports and no protocol.</summary>
    static void NoPorts(JsonObject j)
    {
        In(j).Remove("ports");
        Out(j).Remove("ports");
        j["usbConfiguration"]!.AsObject().Remove("protocol");
    }

    const string WideMask = """{"byte":1,"bits":"0-15","type":"button-mask","semantic":"held","buttons":["A","B","X","Y","LeftBumper","RightBumper","Back","Start","LeftStick","RightStick","Guide","_","_","_","_","_"]}""";

    delegate (System.Collections.Generic.Dictionary<string, object> Fields, bool CrcValid) DecodeFn(
        HIDMaestro.Internal.ExtendedReportSpec spec, ReadOnlySpan<byte> buffer);

    /// <summary>An output button mask wider than a byte decodes every bit it
    /// declares, the bits past the first byte included.</summary>
    static void CheckWideMaskDecodes()
    {
        var method = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.VendorBlobCodec")?.GetMethod("Decode", Any | BindingFlags.Static);
        if (method == null)
        {
            Probe.Check("a 16-bit output button mask decodes bits 8 to 15", false, "VendorBlobCodec.Decode is absent");
            return;
        }
        var decode = (DecodeFn)method.CreateDelegate(typeof(DecodeFn));
        var spec = new HIDMaestro.Internal.ExtendedReportSpec
        {
            ReportId = "0x11",
            Size = 4,
            Fields = { System.Text.Json.JsonSerializer.Deserialize<HIDMaestro.Internal.FieldSpec>(WideMask)! },
        };
        // A and Start in the first byte, LeftStick, RightStick and Guide in
        // the second.
        var (fields, _) = decode(spec, new byte[] { 0x11, 0x81, 0x07, 0x00 });
        var held = fields.TryGetValue("held", out var v) && v is System.Collections.Generic.List<string> l ? l : new();
        Probe.Check("a 16-bit output button mask decodes bits 8 to 15",
            string.Join(",", held) == "A,Start,LeftStick,RightStick,Guide", string.Join(",", held));
    }

    /// <summary>CreateController refuses a malformed profile before it
    /// claims anything: no claim pipe, no owner record and no devnode
    /// appear. The copy is one af1e3d4 accepted and created.</summary>
    static void CheckCreateRefusedFirst(HMContext ctx)
    {
        Console.WriteLine("-- a create refuses a malformed profile first");
        int index = Array.FindIndex(s_malformed, c => c.Name == "a rule match longer than a packet");
        string id = $"s79-malformed-{index}";
        var profile = ctx.GetProfile(id);
        if (profile == null)
        {
            Probe.Check("CreateController refuses a malformed profile", false, $"{id} is not loaded");
            return;
        }
        var pipes = ClaimPipes();
        var records = OwnerRecords();
        var devnodes = Devnodes.PresentAdapters();
        string outcome;
        HMController? created = null;
        try
        {
            created = ctx.CreateController(profile);
            outcome = "created";
        }
        catch (Exception ex)
        {
            outcome = $"{ex.GetType().Name}: {ex.Message}";
        }
        try
        {
            Probe.Check("CreateController refuses a malformed profile, naming it",
                outcome.StartsWith($"InvalidOperationException: Profile '{id}': usbConfiguration.protocol.commands.rules[0].match", StringComparison.Ordinal),
                outcome);
            // Read before any dispose. A device a create made arrives
            // asynchronously, so give it the time the device part gives a
            // real create.
            if (created != null)
                Probe.WaitFor(() => Devnodes.PresentAdapters().Except(devnodes, StringComparer.OrdinalIgnoreCase).Any(), 10_000);
            var pipesAfter = ClaimPipes();
            Probe.Check("no claim pipe appeared", pipesAfter.SetEquals(pipes),
                string.Join(" ", pipesAfter.Except(pipes)));
            var recordsAfter = OwnerRecords();
            Probe.Check("no owner record changed", recordsAfter.SetEquals(records),
                string.Join(" ", recordsAfter.Except(records)));
            var devnodesAfter = Devnodes.PresentAdapters();
            Probe.Check("no devnode appeared", !devnodesAfter.Except(devnodes, StringComparer.OrdinalIgnoreCase).Any(),
                string.Join(" ", devnodesAfter.Except(devnodes, StringComparer.OrdinalIgnoreCase)));
        }
        finally
        {
            if (created != null)
            {
                created.Dispose();
                Probe.WaitFor(() => !Devnodes.PresentAdapters().Except(devnodes, StringComparer.OrdinalIgnoreCase).Any(), 10_000);
            }
        }
    }

    /// <summary>The claim pipes that exist now: HIDMaestro.Owner, .Index
    /// and .Identity.</summary>
    static System.Collections.Generic.HashSet<string> ClaimPipes()
        => new(Directory.GetFiles(@"\\.\pipe\")
            .Select(Path.GetFileName)
            .Where(n => n != null && n.StartsWith("HIDMaestro.", StringComparison.OrdinalIgnoreCase))!);

    /// <summary>Every key, value name and value under the owner records.</summary>
    static System.Collections.Generic.HashSet<string> OwnerRecords()
    {
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroOwners");
        void Walk(Microsoft.Win32.RegistryKey key, string path)
        {
            seen.Add(path);
            foreach (var v in key.GetValueNames())
                seen.Add($"{path}:{v}={key.GetValue(v)}");
            foreach (var sub in key.GetSubKeyNames())
            {
                using var child = key.OpenSubKey(sub);
                if (child != null) Walk(child, $"{path}\\{sub}");
            }
        }
        if (root != null) Walk(root, "HIDMaestroOwners");
        return seen;
    }

    static JsonObject In(JsonObject j) => j["extendedReport"]!.AsObject();
    static JsonObject Out(JsonObject j) => j["extendedOutputReport"]!.AsObject();
    static JsonObject Ports(JsonObject j) => In(j)["ports"]!.AsObject();
    static JsonObject OutPorts(JsonObject j) => Out(j)["ports"]!.AsObject();
    static JsonObject PortField(JsonObject j, int i) => Ports(j)["fields"]![i]!.AsObject();
    static JsonObject OutPortField(JsonObject j, int i) => OutPorts(j)["fields"]![i]!.AsObject();
    static JsonObject Protocol(JsonObject j) => j["usbConfiguration"]!["protocol"]!.AsObject();
    static JsonObject Rule(JsonObject j, int i) => Protocol(j)["commands"]!["rules"]![i]!.AsObject();
    static JsonArray Arr(params string[] items) => JsonNode.Parse("[" + string.Join(",", items) + "]")!.AsArray();

    /// <summary>Each malformed copy of gamecube-adapter is refused by the
    /// check a create runs before it takes anything machine-wide, with an
    /// InvalidOperationException that names the profile, the JSON path and
    /// the limit. No device is created.</summary>
    static void CheckMalformedRefused(HMContext ctx)
    {
        Console.WriteLine("-- malformed profiles are refused");
        var inner = typeof(HMProfile).GetProperty("Inner", Any);
        var validate = inner?.PropertyType.GetMethod("ValidateDeviceModel", Any);
        string? name = typeof(HMContext).Assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.Replace('\\', '/').EndsWith("/gamecube-adapter.json", StringComparison.Ordinal));
        if (inner == null || validate == null || name == null)
        {
            Probe.Check("the device model check and gamecube-adapter's JSON are present", false,
                validate == null ? "ValidateDeviceModel is absent" : "gamecube-adapter.json is not embedded");
            return;
        }
        string json;
        using (var stream = typeof(HMContext).Assembly.GetManifestResourceStream(name)!)
        using (var reader = new StreamReader(stream))
            json = reader.ReadToEnd();

        string dir = Path.Combine(Path.GetTempPath(), "hm-s79-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < s_malformed.Length; i++)
            {
                var j = JsonNode.Parse(json)!.AsObject();
                j["id"] = $"s79-malformed-{i}";
                s_malformed[i].Break(j);
                File.WriteAllText(Path.Combine(dir, $"s79-malformed-{i}.json"), j.ToJsonString());
            }
            for (int i = 0; i < s_badHex.Length; i++)
            {
                var j = JsonNode.Parse(json)!.AsObject();
                j["id"] = $"s79-badhex-{i}";
                j["descriptor"] = s_badHex[i].Break((string)j["descriptor"]!);
                File.WriteAllText(Path.Combine(dir, $"s79-badhex-{i}.json"), j.ToJsonString());
            }
            // The positive controls: copies the codec writes correctly.
            for (int i = 0; i < s_accepted.Length; i++)
            {
                var j = JsonNode.Parse(json)!.AsObject();
                j["id"] = $"s79-accepted-{i}";
                s_accepted[i].Change(j);
                File.WriteAllText(Path.Combine(dir, $"s79-accepted-{i}.json"), j.ToJsonString());
            }

            // The context the catalog came from, so the check starts no
            // second context. The copies' ids are their own.
            ctx.LoadProfilesFromDirectory(dir);
            for (int i = 0; i < s_accepted.Length; i++)
            {
                var p = ctx.GetProfile($"s79-accepted-{i}");
                string result = "accepted";
                if (p == null) result = "did not load";
                else
                {
                    try { validate.Invoke(inner.GetValue(p), null); }
                    catch (TargetInvocationException ex) { result = $"{ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}"; }
                }
                Probe.Check($"compiles: {s_accepted[i].Name}", result == "accepted", result);
            }
            for (int i = 0; i < s_malformed.Length; i++)
            {
                var (title, _, expect) = s_malformed[i];
                string id = $"s79-malformed-{i}";
                var p = ctx.GetProfile(id);
                if (p == null)
                {
                    Probe.Check($"refused: {title}", false, "the profile did not load");
                    continue;
                }
                string want = $"Profile '{id}': {expect}";
                try
                {
                    validate.Invoke(inner.GetValue(p), null);
                    Probe.Check($"refused: {title}", false, "accepted");
                }
                catch (TargetInvocationException ex)
                {
                    var e = ex.InnerException!;
                    Probe.Check($"refused: {title}",
                        e.GetType() == typeof(InvalidOperationException) && e.Message.StartsWith(want, StringComparison.Ordinal),
                        $"{e.GetType().Name}: {e.Message}");
                }
            }
            for (int i = 0; i < s_badHex.Length; i++)
            {
                var (title, _, expect) = s_badHex[i];
                string id = $"s79-badhex-{i}";
                var p = ctx.GetProfile(id);
                if (p == null)
                {
                    Probe.Check($"refused: {title}", false, "the profile did not load");
                    continue;
                }
                string want = $"Profile '{id}': {expect}";
                try
                {
                    validate.Invoke(inner.GetValue(p), null);
                    Probe.Check($"refused: {title}", false, $"accepted, {p.GetDescriptorBytes()?.Length} bytes");
                }
                catch (TargetInvocationException ex)
                {
                    var e = ex.InnerException!;
                    Probe.Check($"refused: {title}",
                        e.GetType() == typeof(FormatException) && e.Message.StartsWith(want, StringComparison.Ordinal),
                        $"{e.GetType().Name}: {e.Message}");
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>The bytes the persona serves, from the USB/IP device's own
    /// descriptor set: the real adapter's exactly, full speed and 8 ms
    /// included.</summary>
    static void CheckServed(HMController controller)
    {
        Console.WriteLine("-- what the persona serves");
        object? set = Get(Get(Get(controller, "UsbipHandle"), "Device"), "Descriptors");
        if (set == null)
        {
            Probe.Check("the USB/IP device's descriptor set", false, "absent");
            return;
        }
        byte[]? Desc(byte type, byte index, ushort lang) =>
            (byte[]?)set.GetType().GetMethod("GetDescriptor", Any, new[] { typeof(byte), typeof(byte), typeof(ushort) })
                ?.Invoke(set, new object[] { type, index, lang });
        byte[]? Hid(byte type) =>
            (byte[]?)set.GetType().GetMethod("GetHidDescriptor", Any, new[] { typeof(byte), typeof(byte) })
                ?.Invoke(set, new object[] { type, (byte)0 });

        var dev = Desc(0x01, 0, 0);
        Probe.Check("device descriptor", dev != null && dev.AsSpan().SequenceEqual(GcReal.DeviceDescriptor), Show(dev));
        var cfg = Desc(0x02, 0, 0);
        Probe.Check("configuration descriptor, endpoints at 8 ms", cfg != null && cfg.AsSpan().SequenceEqual(GcReal.ConfigurationDescriptor), Show(cfg));
        Probe.Check("full speed in the import reply", Get(set, "Speed") is uint speed && speed == 2, $"{Get(set, "Speed")}");
        var langs = Desc(0x03, 0, 0);
        Probe.Check("string 0 lists English (US) alone", langs != null && Probe.Hex(langs) == "04030904", Show(langs));
        CheckString("served", Desc(0x03, 1, 0x0409), GcReal.Manufacturer, 1);
        CheckString("served", Desc(0x03, 2, 0x0409), GcReal.Product, 2);
        CheckString("served", Desc(0x03, 3, 0x0409), GcReal.Serial, 3);
        Probe.Check("string 0xEE absent: no Microsoft OS descriptors", Desc(0x03, 0xEE, 0) == null);
        Probe.Check("no device qualifier, as on a full-speed device", Desc(0x06, 0, 0) == null);
        Probe.Check("no other-speed configuration", Desc(0x07, 0, 0) == null);
        var rd = Hid(0x22);
        Probe.Check("report descriptor, all 214 bytes", rd != null && rd.AsSpan().SequenceEqual(GcReal.ReportDescriptor), Show(rd));
        var hidClass = Hid(0x21);
        Probe.Check("HID class descriptor", hidClass != null && Probe.Hex(hidClass) == "09211001000122D600", Show(hidClass));
    }

    /// <summary>What Windows enumerates, through the hub, before any driver:
    /// the device descriptor exactly, and the configuration descriptor as
    /// usbip-win2 hands a full-speed device on, at high speed with bInterval
    /// 7.</summary>
    static void CheckEnumerated(string id)
    {
        Console.WriteLine("-- what Windows enumerates");
        using var hub = HubPort.ForDevice(id);
        Probe.Check("its hub port opens", hub != null);
        if (hub == null) return;
        Probe.Check("the hub reports it at high speed, as USBHUB3 treats every UDE device", hub.Speed() == 2, $"speed {hub.Speed()}");
        var dev = hub.GetDescriptor(0x01, 0, 0, 255);
        Probe.Check("device descriptor", dev != null && dev.AsSpan().SequenceEqual(GcReal.DeviceDescriptor), Show(dev));
        var cfg = hub.GetDescriptor(0x02, 0, 0, 255);
        Probe.Check("configuration descriptor, the real one with usbip-win2's bInterval 7",
            cfg != null && cfg.AsSpan().SequenceEqual(GcReal.ConfigurationDescriptorAtHighSpeed), Show(cfg));
    }

    /// <summary>What libusb reads once WinUSB is bound: every descriptor and
    /// stall, through WinUSB's control pipe.</summary>
    static void CheckThroughWinUsb(HMController controller, string id)
    {
        Console.WriteLine("-- what libusb reads through WinUSB");
        var bind = typeof(HMController).GetMethod("BindWinUsb", Any, Type.EmptyTypes);
        if (bind == null)
        {
            Probe.Check("BindWinUsb binds WinUSB", false, "BindWinUsb is absent");
            return;
        }
        object? result;
        try { result = bind.Invoke(controller, null); }
        catch (TargetInvocationException ex)
        {
            Probe.Check("BindWinUsb binds WinUSB", false, $"{ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
            return;
        }
        Probe.Check("BindWinUsb binds WinUSB", result?.ToString() == "Bound", $"{result}");
        WinUsbDevice? usb = null;
        Probe.WaitFor(() => (usb = WinUsbDevice.Open(id)) != null, 10_000);
        Probe.Check("WinUSB opens the device by the profile's interface GUID", usb != null);
        if (usb == null) return;
        using (usb)
        {
            var dev = usb.Control(0x80, 0x06, 0x0100, 0, 18, null, out int e1);
            Probe.Check("device descriptor", dev != null && dev.AsSpan().SequenceEqual(GcReal.DeviceDescriptor), Show(dev, e1));
            var cfg = usb.Control(0x80, 0x06, 0x0200, 0, 255, null, out int e2);
            Probe.Check("configuration descriptor, as usbip-win2 hands it on",
                cfg != null && cfg.AsSpan().SequenceEqual(GcReal.ConfigurationDescriptorAtHighSpeed), Show(cfg, e2));
            var langs = usb.Control(0x80, 0x06, 0x0300, 0, 255, null, out int e3);
            Probe.Check("string 0 lists English (US) alone", langs != null && Probe.Hex(langs) == "04030904", Show(langs, e3));
            CheckString("WinUSB", usb.Control(0x80, 0x06, 0x0301, 0x0409, 255, null, out _), GcReal.Manufacturer, 1);
            CheckString("WinUSB", usb.Control(0x80, 0x06, 0x0302, 0x0409, 255, null, out _), GcReal.Product, 2);
            CheckString("WinUSB", usb.Control(0x80, 0x06, 0x0303, 0x0409, 255, null, out _), GcReal.Serial, 3);
            Probe.Check("string 0xEE stalls: no Microsoft OS descriptors",
                usb.Control(0x80, 0x06, 0x03EE, 0, 255, null, out _) == null);
            Probe.Check("device qualifier stalls, as on a full-speed device",
                usb.Control(0x80, 0x06, 0x0600, 0, 10, null, out _) == null);
            Probe.Check("other-speed configuration stalls",
                usb.Control(0x80, 0x06, 0x0700, 0, 255, null, out _) == null);
            // GET_DESCRIPTOR(Report) to interface 0, the request a HID host
            // sends.
            var rd = usb.Control(0x81, 0x06, 0x2200, 0, 255, null, out int e4);
            Probe.Check("report descriptor, all 214 bytes", rd != null && rd.AsSpan().SequenceEqual(GcReal.ReportDescriptor), Show(rd, e4));
            var hidClass = usb.Control(0x81, 0x06, 0x2100, 0, 9, null, out int e5);
            Probe.Check("HID class descriptor", hidClass != null && Probe.Hex(hidClass) == "09211001000122D600", Show(hidClass, e5));
        }
    }

    static void CheckString(string view, byte[]? s, string expected, int index)
    {
        string? text = s != null && s.Length >= 2 && s[1] == 0x03 ? Encoding.Unicode.GetString(s, 2, s[0] - 2) : null;
        Probe.Check($"string {index} is \"{expected}\" ({view})", text == expected && s![0] == s.Length,
            s == null ? "stalled" : $"\"{text}\" ({Probe.Hex(s)})");
    }

    static object? Get(object? o, string name) => o?.GetType().GetProperty(name, Any)?.GetValue(o);

    static string Show(byte[]? b, int error = 0)
        => b == null ? (error != 0 ? $"failed, error {error}" : "absent") : $"{b.Length} bytes {Probe.Hex(b.AsSpan(0, Math.Min(48, b.Length)))}";
}
