using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

/// <summary>The protocol of a UMDF2 device, which the SDK runs behind the HID
/// driver (design note, decision (b)): its commands are the output reports
/// the driver hands on, and its reports and replies are input reports the
/// HID descriptor declares, each at its declared length. The test profiles
/// are built on the Bluetooth descriptor switch-pro carries (outputs 0x01,
/// 0x10 to 0x12, inputs 0x21, 0x30 to 0x33 and 0x3F) and on the report-ID-less
/// descriptor of dualshock-3-full (a 12-byte input, a 48-byte output).</summary>
internal static class Umdf2
{
    static void Check(string what, bool ok, string detail = "") => Checks.Check(what, ok, detail);

    /// <summary>A UMDF2 test profile: switch-pro's identity and descriptor
    /// with a small protocol of its own, edited by <paramref name="edit"/>.</summary>
    internal static string Profile(Action<JsonObject>? edit = null)
    {
        var o = JsonNode.Parse(Device.Catalog("switch-pro"))!.AsObject();
        o["id"] = "umdf2-test";
        foreach (var k in new[] { "stateBlock", "inputReports", "liveFeatureReports", "protocol" }) o.Remove(k);
        o["stateBlock"] = JsonNode.Parse("""{ "size": 4, "fields": [ { "byte": 0, "type": "button-mask", "buttons": [ "#0", "#1" ] } ] }""");
        o["inputReports"] = JsonNode.Parse("""
        [ { "name": "full", "reportId": "0x30", "size": 49, "while": "seen", "program": [ { "const": { "0": "30" } }, { "copy": "0-1", "to": 3 } ] } ]
        """);
        o["protocol"] = JsonNode.Parse("""
        {
          "state": [ { "name": "seen" } ],
          "input": { "clock": { "periodMs": 15 } },
          "commands": {
            "rules": [
              { "match": "01", "minLength": 11, "set": { "seen": 1 }, "reply": [ { "hex": "21" }, { "echo": "10" } ], "replyLength": 49 },
              { "match": "10" }
            ]
          },
          "replies": { "pending": 4, "overflow": "dropOldest" }
        }
        """);
        edit?.Invoke(o);
        return o.ToJsonString();
    }

    /// <summary>The profile validated as a create validates it, then its
    /// protocol compiled. The refusal's message, or null.</summary>
    static (CompiledProtocol? Protocol, string? Refusal) Compile(string json)
    {
        try
        {
            var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(json)) ?? throw new InvalidOperationException("no profile");
            p.ValidateDeviceModel();
            return (CompiledProtocol.Compile(p, null), null);
        }
        catch (InvalidOperationException e) { return (null, e.Message); }
    }

    static void Refused(string what, Action<JsonObject> edit, string path)
    {
        var (_, refusal) = Compile(Profile(edit));
        Check($"refused with its path: {what}", refusal != null && refusal.Contains(path, StringComparison.Ordinal), refusal ?? "accepted");
    }

    public static void Run()
    {
        Compiles();
        FrameCounter();
        AddressSpace();
    }

    /// <summary>An image only replies read spans the whole 32-bit address
    /// space, so an address and a length wrap as the 32-bit sums of the
    /// Switch Pro's SPI read did (driver.c:1667-1674 at 9bd6279, ULONG
    /// arithmetic), and an address past the flash reads the fill.</summary>
    static void AddressSpace()
    {
        Console.WriteLine("-- a memory image of the whole 32-bit address space");
        string Spi(string size, bool written = false, bool top = true) => Profile(o =>
        {
            var pr = o["protocol"]!.AsObject();
            pr["memory"] = JsonNode.Parse($$"""
            [ { "name": "spi", "size": "{{size}}", "fill": "FF",
                "segments": [ { "at": "0x0", "hex": "AB" }, { "at": "0x6020", "hex": "0102030405" } ] } ]
            """);
            if (top) pr["memory"]![0]!["segments"]!.AsArray().Add(JsonNode.Parse("""{ "at": "0xFFFFFFFE", "hex": "CDEF" }"""));
            var rules = pr["commands"]!["rules"]!.AsArray();
            rules.Insert(0, JsonNode.Parse("""
            { "match": "01 .. .. .. .. .. .. .. .. .. 10", "minLength": 16,
              "reply": [ { "hex": "21" }, { "echo": "11-15" }, { "memory": "spi", "address": { "le32": 11 }, "length": { "byte": 15, "max": 29 } } ],
              "replyLength": 49 }
            """));
            if (written) rules.Insert(0, JsonNode.Parse("""{ "match": "12", "minLength": 4, "write": [ { "memory": "spi", "address": { "value": 0 }, "from": "1-3" } ] }"""));
        });
        byte[] Read(ProtocolEngine e, uint address, byte length)
        {
            var cmd = new byte[49];
            cmd[0] = 0x01; cmd[10] = 0x10;
            cmd[11] = (byte)address; cmd[12] = (byte)(address >> 8); cmd[13] = (byte)(address >> 16); cmd[14] = (byte)(address >> 24);
            cmd[15] = length;
            e.HandleCommand(cmd, Stopwatch.GetTimestamp());
            var reply = new byte[64];
            int n = e.TakeReply(64, reply);
            return n < 6 ? Array.Empty<byte>() : reply.AsSpan(6, length).ToArray();
        }
        var (whole, refusal) = Compile(Spi("0x100000000"));
        if (whole == null) { Check("an image of 0x100000000 bytes compiles", false, refusal ?? ""); return; }
        var e = new ProtocolEngine(whole, "HM-TEST-0001", 0, ProgramBinder.BindLayouts(whole.Model));
        Check("a read inside a segment", Checks.Hex(Read(e, 0x6020, 5)) == "0102030405");
        Check("an address past 0x80000 reads the fill, not a segment it would wrap onto", Checks.Hex(Read(e, 0x01006020, 5)) == "FFFFFFFFFF", Checks.Hex(Read(e, 0x01006020, 5)));
        Check("a read across 0xFFFFFFFF wraps to address 0", Checks.Hex(Read(e, 0xFFFFFFFD, 4)) == "FFCDEFAB", Checks.Hex(Read(e, 0xFFFFFFFD, 4)));
        // The largest image the engine held before: an address wraps at its
        // size, so 0x01006020 reads the segment at 0x6020.
        var (wrapped, why) = Compile(Spi("0x1000000", top: false));
        if (wrapped == null) Check("the contrast's image of 0x1000000 bytes compiles", false, why ?? "");
        else
        {
            var w = new ProtocolEngine(wrapped, "HM-TEST-0001", 0, ProgramBinder.BindLayouts(wrapped.Model));
            Check("the contrast: an image of 0x1000000 bytes wraps 0x01006020 onto 0x6020", Checks.Hex(Read(w, 0x01006020, 5)) == "0102030405", Checks.Hex(Read(w, 0x01006020, 5)));
        }
        var (_, written) = Compile(Spi("0x100000000", written: true));
        Check("refused with its path: a rule that writes an image larger than 0x1000000 bytes", written?.Contains("protocol.memory[0].size", StringComparison.Ordinal) == true, written ?? "accepted");
        var (_, past) = Compile(Spi("0x100000001"));
        Check("refused: a size past 0x100000000", past != null, past ?? "accepted");
    }

    /// <summary>A frame counter numbers the messages that carry it, as the
    /// Switch Pro's timer byte numbers its replies and its 0x30 reports and
    /// never its 0x3F reports (driver.c:1631 and 1855 at 9bd6279).</summary>
    static void FrameCounter()
    {
        Console.WriteLine("-- a frame counter advances on the messages that carry it");
        var json = Profile(o =>
        {
            o["inputReports"] = JsonNode.Parse("""
            [ { "name": "full", "reportId": "0x30", "size": 49, "while": "seen",
                "program": [ { "const": { "0": "30" } }, { "counter": "timer", "at": 1, "width": 8 }, { "copy": "0-1", "to": 3 } ] },
              { "name": "simple", "reportId": "0x3F", "size": 12, "while": "!seen", "program": [ { "const": { "0": "3F" } } ] } ]
            """);
            var pr = o["protocol"]!.AsObject();
            pr["counters"] = JsonNode.Parse("""[ { "name": "timer", "bits": 8, "on": "frame" } ]""");
            pr["commands"]!["replyPrefix"] = JsonNode.Parse("""[ { "hex": "21" }, { "counter": "timer" } ]""");
            pr["commands"]!["rules"]![0]!["reply"] = JsonNode.Parse("""[ { "echo": "10" } ]""");
        });
        var (proto, refusal) = Compile(json);
        if (proto == null) { Check("the frame counter's test profile compiles", false, refusal ?? ""); return; }
        var runner = ProgramBinder.BindLayouts(proto.Model)!;
        var e = new ProtocolEngine(proto, "HM-TEST-0001", 0, runner);
        var build = new Builder { Engine = e };
        int timer = Array.IndexOf(proto.Model.CounterNames, "timer");
        for (int i = 0; i < 3; i++) runner.Visit(build);
        Check("three 0x3F reports, which do not carry the counter, leave it at 0", build.Buffer[0] == 0x3F && e.GetCounter(timer) == 0, $"timer {e.GetCounter(timer)}");
        var cmd = new byte[49];
        cmd[0] = 0x01; cmd[10] = 0x02;
        e.HandleCommand(cmd, Stopwatch.GetTimestamp());
        var reply = new byte[64];
        e.TakeReply(64, reply);
        Check("the first reply carries 0, and the counter moves on", reply[1] == 0 && e.GetCounter(timer) == 1, $"reply byte 1 {reply[1]}, timer {e.GetCounter(timer)}");
        runner.Visit(build);
        runner.Visit(build);
        Check("two 0x30 reports carry 1 and 2", build.Buffer[0] == 0x30 && build.Buffer[1] == 2 && e.GetCounter(timer) == 3, $"last {build.Buffer[1]}, timer {e.GetCounter(timer)}");
        e.HandleCommand(cmd, Stopwatch.GetTimestamp());
        e.TakeReply(64, reply);
        Check("the next reply carries 3", reply[1] == 3, $"{reply[1]}");
    }

    static void Compiles()
    {
        Console.WriteLine("-- a UMDF2 device's protocol");
        var (proto, refusal) = Compile(Profile());
        Check("a UMDF2 profile's protocol compiles, its commands its output reports", proto is { Source: CommandSource.OutputReport }, refusal ?? "");
        if (proto != null)
        {
            var e = new ProtocolEngine(proto, "HM-TEST-0001", 0, ProgramBinder.BindLayouts(proto.Model));
            var cmd = new byte[49];
            cmd[0] = 0x01; cmd[10] = 0x48;
            var r = e.HandleCommand(cmd, Stopwatch.GetTimestamp());
            var reply = new byte[64];
            int n = e.TakeReply(64, reply);
            Check("its engine answers an output report with an input report of the declared length", r.Rule == 0 && n == 49 && reply[0] == 0x21 && reply[1] == 0x48,
                $"rule {r.Rule}, {n} bytes {Checks.Hex(reply.AsSpan(0, Math.Max(0, n)))}");
        }
        Refused("a rule on output report 0x80, which the Bluetooth descriptor does not declare",
            o => o["protocol"]!["commands"]!["rules"]!.AsArray().Add(JsonNode.Parse("""{ "match": "80 02" }""")), "protocol.commands.rules[2].match");
        Refused("a layout of 48 bytes for input report 0x30, declared at 49",
            o => o["inputReports"]![0]!["size"] = 48, "inputReports[0].size");
        Refused("a layout on input report 0x40, which the descriptor does not declare",
            o => { o["inputReports"]![0]!["reportId"] = "0x40"; o["inputReports"]![0]!["program"]![0]!["const"]!["0"] = "40"; }, "inputReports[0].reportId");
        Refused("a reply of 48 bytes for input report 0x21, declared at 49",
            o => o["protocol"]!["commands"]!["rules"]![0]!["replyLength"] = 48, "protocol.commands.rules[0].replyLength");
        Refused("a reply on input report 0x81, which the Bluetooth descriptor does not declare",
            o => o["protocol"]!["commands"]!["rules"]![0]!["reply"]![0]!["hex"] = "81", "protocol.commands.rules[0].reply");
        Refused("an input endpoint", o => o["protocol"]!["input"]!["endpoint"] = "0x81", "protocol.input.endpoint");
        Refused("a command endpoint", o => o["protocol"]!["commands"]!["endpoint"] = "0x01", "protocol.commands.endpoint");
        Refused("commands from an interrupt endpoint", o => o["protocol"]!["commands"]!["source"] = "interruptOut", "protocol.commands.source");
        Refused("replies on a bulk endpoint", o => o["protocol"]!["replies"]!["channel"] = "bulkIn", "protocol.replies.channel");
        Refused("a halt", o => o["protocol"]!["halt"] = JsonNode.Parse("""{ "while": "seen" }"""), "protocol.halt");
        Refused("a getReport answer, which the driver gives from its frames", o => o["protocol"]!["getReport"] = "deviceReport", "protocol.getReport");
        Refused("a clock layout with no clock", o => ((JsonObject)o["protocol"]!).Remove("input"), "protocol.input.clock");

        // The descriptor with no report IDs: one 12-byte input, one 48-byte
        // output.
        string Ds3(Action<JsonObject> edit)
        {
            var o = JsonNode.Parse(Profile())!.AsObject();
            var ds3 = JsonNode.Parse(Device.Catalog("dualshock-3-full"))!.AsObject();
            o["descriptor"] = (string)ds3["descriptor"]!;
            o["inputReportSize"] = null;
            o["inputReports"] = JsonNode.Parse("""[ { "name": "sixaxis", "size": 12, "send": "submit", "program": [ { "copy": "0-1", "to": 0 } ] } ]""");
            o["protocol"] = JsonNode.Parse("""{ "commands": { "rules": [ { "match": "02" } ] } }""");
            edit(o);
            return o.ToJsonString();
        }
        var (ds3, ds3Refusal) = Compile(Ds3(_ => { }));
        Check("a descriptor without report IDs: a submit layout of its 12-byte input compiles with no clock", ds3 != null, ds3Refusal ?? "");
        {
            var (_, r) = Compile(Ds3(o => o["inputReports"] = JsonNode.Parse("""[ { "name": "sixaxis", "reportId": "0x01", "size": 12, "send": "submit", "program": [ { "const": { "0": "01" } }, { "copy": "0-1", "to": 1 } ] } ]""")));
            Check("refused with its path: a report ID on a descriptor without them", r?.Contains("inputReports[0].reportId", StringComparison.Ordinal) == true, r ?? "accepted");
            (_, r) = Compile(Ds3(o => o["inputReports"]![0]!["size"] = 13));
            Check("refused with its path: a 13-byte layout for the 12-byte input report", r?.Contains("inputReports[0].size", StringComparison.Ordinal) == true, r ?? "accepted");
        }
    }
}
