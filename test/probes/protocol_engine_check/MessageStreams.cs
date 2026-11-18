using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;
using HIDMaestro.Internal.Usbip;

/// <summary>Six general mechanisms of a device protocol that sends more than
/// one kind of message on its input endpoint: several message streams live at
/// once, timed messages with their own schedule built from reply parts, a
/// rule that queues several packets, a match on bits, a rule after which the
/// later rules are tried too, and a counter that skips 0. The test profile is
/// written for these checks on the Switch 2 Pro composite's descriptors, so
/// nothing in it is a device's: a state block of two button bytes, two
/// layouts in two streams, two registers, three counters, five rules and two
/// timers.</summary>
internal static class MessageStreams
{
    static void Check(string what, bool ok, string detail = "") => Checks.Check(what, ok, detail);

    const string Serial = "HM0123456789AB";

    static string Profile()
    {
        var o = JsonNode.Parse(Device.Catalog("switch2-pro-controller-composite"))!.AsObject();
        o["id"] = "stream-test";
        o["stateBlock"] = JsonNode.Parse("""
        { "size": 2, "fields": [
            { "byte": 0, "type": "button-mask", "buttons": ["A", "B", "X", "Y", "LeftBumper", "RightBumper", "Back", "Start"] },
            { "byte": 1, "type": "button-mask", "buttons": ["Guide", "Share"] } ] }
        """);
        o["inputReports"] = JsonNode.Parse("""
        [
          { "name": "input", "reportId": "0x20", "size": 6, "stream": "input", "while": "started", "send": "change",
            "program": [ { "const": { "0": "20", "1": "00", "3": "02" } }, { "counter": "inputSeq", "at": 2, "width": 8 },
                         { "copy": "0", "to": 4 }, { "bits": [ [ 9, 40 ] ] } ] },
          { "name": "guide", "reportId": "0x07", "size": 6, "stream": "guide", "while": "started", "send": "change", "first": "skip",
            "program": [ { "const": { "0": "07", "1": "20", "3": "02", "5": "5B" } }, { "counter": "globalSeq", "at": 2, "width": 8 },
                         { "bits": [ [ 8, 32 ] ] } ] }
        ]
        """);
        o["protocol"] = JsonNode.Parse("""
        {
          "state": [ { "name": "started" }, { "name": "answered" } ],
          "counters": [
            { "name": "globalSeq", "bits": 8, "on": "frame", "skipZero": true },
            { "name": "inputSeq", "bits": 8, "on": "frame", "skipZero": true },
            { "name": "wrap", "bits": 8, "on": "read", "skipZero": true, "initial": "0xFE" }
          ],
          "input": { "endpoint": "0x81", "clock": { "periodMs": 4, "origin": "attach" } },
          "commands": {
            "endpoint": "0x01",
            "rules": [
              { "match": ".. 30/B0", "minLength": 4, "continue": true,
                "reply": [ { "hex": "0120" }, { "echo": "2" }, { "hex": "0900" }, { "echo": "0" }, { "hex": "20" }, { "echo": "3" }, { "hex": "000000" }, { "hex": "0000" } ] },
              { "match": "05 20/EF .. 01 00", "set": { "started": 1, "answered": 1 }, "consume": true },
              { "match": "05 20/EF .. 01 07", "set": { "started": 0, "answered": 0 }, "consume": true },
              { "match": "04 20/EF .. 00", "minLength": 4, "set": { "answered": 1 }, "consume": true,
                "replies": [ [ { "hex": "04F0" }, { "echo": "2" }, { "hex": "3ADA01" } ], [ { "hex": "04A0" }, { "echo": "2" }, { "hex": "BA003A" } ] ] },
              { "match": "78", "reply": [ { "counter": "wrap" } ] },
              { "match": "..", "consume": true }
            ]
          },
          "replies": { "channel": "input", "pending": 8 },
          "timers": [
            { "name": "hello", "while": "!answered", "firstMs": 500, "periods": [ { "periodMs": 500 } ],
              "reply": [ { "hex": "0220" }, { "counter": "globalSeq" }, { "hex": "08" }, { "value": "serial", "encoding": "hex-le", "length": { "value": 8 } } ] },
            { "name": "status", "while": "started", "firstMs": 0, "periods": [ { "periodMs": 1000, "count": 10 }, { "periodMs": 20000 } ],
              "reply": [ { "hex": "0320" }, { "counter": "globalSeq" }, { "hex": "0480000000" } ] }
          ]
        }
        """);
        return o.ToJsonString();
    }

    static (ProtocolEngine Engine, CompiledModel Model, LayoutRunner Runner)? Build(out string error)
    {
        error = "";
        try
        {
            var p = ControllerProfile.Parse(System.Text.Encoding.UTF8.GetBytes(Profile())) ?? throw new InvalidOperationException("no profile");
            p.ValidateDeviceModel();
            var model = p.GetOrCompileModel();
            var compiled = CompiledProtocol.Compile(p, new UsbDescriptorSet(p, 0))!;
            var runner = ProgramBinder.BindLayouts(model)!;
            return (new ProtocolEngine(compiled, Serial, 0, runner), model, runner);
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    static ProtocolEngine.CommandResult Send(ProtocolEngine e, string hex) => e.HandleCommand(Checks.Bytes(hex), Stopwatch.GetTimestamp());

    static string Take(ProtocolEngine e)
    {
        var buf = new byte[64];
        int n = e.TakeReply(64, buf);
        return n < 0 ? "-" : Checks.Hex(buf.AsSpan(0, n));
    }

    static long Ms(long origin, double ms) => origin + (long)(ms * Stopwatch.Frequency / 1000.0);

    public static void Run()
    {
        Console.WriteLine("-- several message streams, timed messages, several packets, bit matches, fall-through, a counter that skips 0");
        var built = Build(out string error);
        Check("the test profile with streams, timers, replies, bit matches, continue and skipZero compiles", built != null, error);
        if (built is not { } b) return;
        var (e, model, runner) = b;
        int Reg(string name) => model.Registers.IndexOf(name);
        var tick = new Builder { Engine = e };
        string Next() { int n = runner.Visit(tick); return n == 0 ? "-" : Checks.Hex(tick.Buffer.AsSpan(0, n)); }

        // A counter that skips 0.
        string w1 = Take(Fire(e, "78")), w2 = Take(Fire(e, "78")), w3 = Take(Fire(e, "78"));
        Check("a counter that skips 0 goes from its largest value to 1", w1 == "FE" && w2 == "FF" && w3 == "01", $"{w1} {w2} {w3}");

        // Timed messages.
        long t0 = Stopwatch.GetTimestamp();
        bool q0 = e.RunTimers(t0), q499 = e.RunTimers(Ms(t0, 499));
        bool q500 = e.RunTimers(Ms(t0, 500));
        string hello1 = Take(e);
        Check("a timer's first message comes firstMs after its condition begins to hold, and not before",
              !q0 && !q499 && q500, $"{q0} {q499} {q500}");
        Check("a timer's message is built from reply parts: a counter that starts at 1 under skipZero, and the serial's hex digits low byte first",
              hello1 == "02200108AB89674523010000", hello1);
        bool q1000 = e.RunTimers(Ms(t0, 1000));
        bool q1100 = e.RunTimers(Ms(t0, 1100));
        bool waiting = e.HasReply;
        string hello2 = Take(e);
        Check("a timer sends again one period later", q1000 && !q1100 && waiting && hello2 == "02200208AB89674523010000", hello2);
        e.RunTimers(Ms(t0, 1500));
        bool skipped = !e.RunTimers(Ms(t0, 2000));
        string hello3 = Take(e), none = Take(e);
        Check("a message that comes due while an earlier reply waits is not queued, and the schedule moves on",
              skipped && hello3 == "02200308AB89674523010000" && none == "-", $"{hello3} {none}");

        // A rule that queues several packets.
        var meta = Send(e, "04 20 09 00");
        string f1 = Take(e), f2 = Take(e), f3 = Take(e);
        Check("a rule with replies queues each as a message of its own, in order",
              meta.Effect.HasFlag(ProtocolEngine.Effect.Reply) && f1 == "04F0093ADA01" && f2 == "04A009BA003A" && f3 == "-", $"{f1} {f2} {f3}");
        Check("a timer whose condition stops holding sends nothing more", !e.RunTimers(Ms(t0, 2500)) && !e.RunTimers(Ms(t0, 3000)));

        // A match on bits, and fall-through.
        Send(e, "05 21 03 01 00");
        Check("a match on bits refuses a byte whose bits under the mask differ", e.GetRegister(Reg("started")) == 0);
        var plain = Send(e, "05 20 04 01 00");
        Check("a match on bits takes the byte with the masked bit clear", e.GetRegister(Reg("started")) == 1 && !e.HasReply && plain.Consume);
        Send(e, "05 20 05 01 07");
        var acme = Send(e, "05 30 06 01 00");
        string ack = Take(e);
        Check("a rule marked continue replies, and the rule after it still applies to the same command",
              ack == "0120060900052001000000" + "0000" && e.GetRegister(Reg("started")) == 1 && acme.Consume, $"{ack}, started {e.GetRegister(Reg("started"))}");
        Check("the command's rule is the one Match returns, and a decode or emit would be its",
              e.Match(Checks.Bytes("05 30 06 01 00")) == 1, $"rule {e.Match(Checks.Bytes("05 30 06 01 00"))}");
        var other = Send(e, "0A 30 07 03 00 01 14");
        string ack2 = Take(e);
        Check("a command only the last rule matches gets the acknowledgement and is consumed",
              ack2 == "01200709000A2003000000" + "0000" && other.Consume, ack2);

        // Several message streams.
        long t1 = Stopwatch.GetTimestamp();
        e.RunTimers(t1);
        string status1 = Take(e);
        Check("a timer with firstMs 0 sends as its condition begins to hold", status1.StartsWith("0320", StringComparison.Ordinal) && status1.EndsWith("0480000000", StringComparison.Ordinal), status1);
        int statuses = 0;
        for (int s = 1; s <= 40; s++)
        {
            if (e.RunTimers(Ms(t1, s * 1000))) statuses++;
            Take(e);
        }
        Check("a timer runs its steps in turn: ten at one second, then one every twenty seconds", statuses == 10 + 1, $"{statuses} messages from 1 s to 40 s");

        e.SetStateBlock(new byte[] { 0x01, 0x00 });
        string first = Next(), quiet = Next();
        Check("a stream's first report after its layout becomes selected goes out, and the next with the same bytes does not",
              first == "200001020100" && quiet == "-", $"{first} {quiet}");
        e.SetStateBlock(new byte[] { 0x01, 0x01 });
        string guide = Next(), after = Next();
        Check("a second stream sends its own change beside the first, and first skip kept the state it started in as sent",
              guide.StartsWith("0720", StringComparison.Ordinal) && guide.EndsWith("02015B", StringComparison.Ordinal) && after == "-", $"{guide} {after}");
        e.SetStateBlock(new byte[] { 0x03, 0x02 });
        string a = Next(), c = Next(), d = Next();
        bool both = (a.StartsWith("20", StringComparison.Ordinal) && c.StartsWith("07", StringComparison.Ordinal))
                    || (a.StartsWith("07", StringComparison.Ordinal) && c.StartsWith("20", StringComparison.Ordinal));
        Check("two streams that change together each send once", both && d == "-", $"{a} {c} {d}");
        string input2 = a.StartsWith("20", StringComparison.Ordinal) ? a : c;
        Check("a layout sent on change compares its bytes apart from its counter, which numbers each message it sends",
              input2 == "200002020301", input2);
        Send(e, "05 20 08 01 07");
        string stopped = Next();
        Send(e, "05 20 09 01 00");
        string again = Next();
        Check("a layout that becomes selected again forgets the bytes it last sent, so the first report after a new start goes out",
              stopped == "-" && again.StartsWith("200003", StringComparison.Ordinal), $"{stopped} {again}");
    }

    static ProtocolEngine Fire(ProtocolEngine e, string hex)
    {
        Send(e, hex);
        return e;
    }
}
