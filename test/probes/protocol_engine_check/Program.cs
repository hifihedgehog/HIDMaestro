using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;
using HIDMaestro.Internal.Usbip;

// protocol_engine_check: the protocol engine (design note, mechanism 3) and
// the device clock (mechanism 2) offline. Each check states a behavior of a
// test profile built from the Switch 2 Pro composite's descriptors, or of the
// Triton's catalog profile, that the engine before v2.0.0 did not have.
if (Environment.GetEnvironmentVariable("HIDMAESTRO_DIAG") is { Length: > 0 })
{
    // The diagnostic log builds a line per command, which the allocation
    // checks would count. The checks run in a copy of this process without it.
    var start = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
    };
    foreach (var a in args) start.ArgumentList.Add(a);
    start.Environment.Remove("HIDMAESTRO_DIAG");
    using var child = Process.Start(start)!;
    child.ErrorDataReceived += (_, ev) => { if (ev.Data != null) Console.Error.WriteLine(ev.Data); };
    child.BeginErrorReadLine();
    for (string? line; (line = child.StandardOutput.ReadLine()) != null;) Console.WriteLine(line);
    child.WaitForExit();
    return child.ExitCode;
}
Console.WriteLine("=== protocol_engine_check: the protocol engine and the device clock, offline ===");
Engine.Run();
Clock.Run();
DeviceLifetime.Run();
OutputOnce.Run();
Umdf2.Run();
Host.Run();
Deliberate.Run();
Console.WriteLine(Checks.Failed == 0 ? "protocol_engine_check: PASS" : $"protocol_engine_check: {Checks.Failed} FAILED");
return Checks.Failed == 0 ? 0 : 1;

internal static class Checks
{
    public static int Failed;

    public static void Check(string what, bool ok, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
        if (!ok) Failed++;
    }

    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);
    public static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", "", StringComparison.Ordinal));
}

/// <summary>Builds a test device: the composite's profile with its model
/// groups replaced, compiled and validated as a create compiles it, and its
/// engine.</summary>
internal static class Device
{
    public static string Catalog(string id)
    {
        var asm = typeof(HMContext).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)))
        {
            using var st = asm.GetManifestResourceStream(res)!;
            using var sr = new StreamReader(st, Encoding.UTF8);
            string t = sr.ReadToEnd();
            if (JsonNode.Parse(t) is JsonObject o && (string?)o["id"] == id) return t;
        }
        throw new InvalidOperationException($"no catalog profile {id}");
    }

    public static (ProtocolEngine Engine, CompiledModel Model, LayoutRunner? Runner, ControllerProfile Profile) Build(string json)
    {
        var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(json)) ?? throw new InvalidDataException("no profile");
        p.ValidateDeviceModel();
        var model = p.GetOrCompileModel();
        var compiled = CompiledProtocol.Compile(p, new UsbDescriptorSet(p, 0))!;
        var runner = ProgramBinder.BindLayouts(model);
        return (new ProtocolEngine(compiled, "HM-TEST-0001", 0, runner), model, runner, p);
    }

    /// <summary>The test profile: the composite's descriptors and state
    /// block, with registers, counters, a memory image, layouts and rules
    /// written for these checks.</summary>
    public static string TestProfile(string overflow = "dropNewest", int pending = 2)
    {
        var o = JsonNode.Parse(Catalog("switch2-pro-controller-composite"))!.AsObject();
        o["id"] = "engine-test";
        o["inputReports"] = JsonNode.Parse("""
        [
          { "name": "a", "reportId": "0x09", "size": 8, "while": "playerLeds==0x01",
            "program": [ { "const": { "0": "09", "1": "AA" } }, { "counter": "ticks", "at": 2, "width": 32 } ] },
          { "name": "b", "reportId": "0x09", "size": 8, "while": "playerLeds==0x02",
            "program": [ { "const": { "0": "09", "1": "BB" } }, { "copy": "0-1", "to": 2 } ] },
          { "name": "c", "reportId": "0x05", "size": 8, "while": "mode16 in 0x0100,0x0201 || featureMask!=0",
            "program": [ { "const": { "0": "05", "1": "CC" } }, { "counter": "reportsSent", "at": 2, "width": 16 } ] },
          { "name": "d", "reportId": "0x06", "size": 4, "while": "playerLeds==0x03", "send": "change",
            "program": [ { "const": { "0": "06" } }, { "copy": "0-1", "to": 1 } ] },
          { "name": "e", "reportId": "0x07", "size": 4, "while": "playerLeds==0x04", "send": "submit",
            "program": [ { "const": { "0": "07" } }, { "copy": "0-1", "to": 1 } ] }
        ]
        """);
        var pr = o["protocol"]!.AsObject();
        pr["state"] = JsonNode.Parse("""
        [ { "name": "reportsOn" }, { "name": "selectedReport", "bits": 8, "initial": "0x09" }, { "name": "featureMask", "bits": 8 },
          { "name": "featureEnabled", "bits": 8 }, { "name": "playerLeds", "bits": 8 }, { "name": "mode16", "bits": 16 } ]
        """);
        pr["counters"] = JsonNode.Parse("""
        [ { "name": "reportsSent", "bits": 32, "on": "report" }, { "name": "ticks", "bits": 32, "on": "clock", "periodUs": 1000 },
          { "name": "reads", "bits": 8, "on": "read" } ]
        """);
        pr["memory"] = JsonNode.Parse("""
        [ { "name": "m16", "size": 16, "fill": "EE", "segments": [ { "at": 0, "hex": "000102030405060708090A0B0C0D" } ] } ]
        """);
        pr["decoders"] = null;
        pr.Remove("decoders");
        pr["commands"] = JsonNode.Parse($$"""
        {
          "endpoint": "0x02", "minLength": 1, "status": "01", "replyPrefix": [ { "echo": "0" }, { "status": true } ],
          "rules": [
            { "match": "10", "set": { "playerLeds": 1 } },
            { "match": "11", "set": { "playerLeds": 2 } },
            { "match": "12 .. 34", "reply": [ { "hex": "AB" } ] },
            { "match": "13", "minLength": 3, "store": { "mode16": { "byte": 1, "bytes": 2, "order": "be" } } },
            { "match": "14", "minLength": 2, "store": { "selectedReport": { "byte": 1, "in": [ "05", "09" ] } } },
            { "match": "15", "minLength": 2, "store": { "reportsOn": { "byte": 1, "nonzero": true } } },
            { "match": "16", "minLength": 2, "store": { "featureMask": { "byte": 1 } } },
            { "match": "17", "minLength": 2, "or": { "featureEnabled": { "byte": 1, "mask": "featureMask" } } },
            { "match": "18", "minLength": 2, "andNot": { "featureEnabled": { "byte": 1, "mask": "featureMask" } } },
            { "match": "19", "minLength": 3, "reply": [ { "memory": "m16", "address": { "byte": 1 }, "length": { "byte": 2, "max": 8 } } ] },
            { "match": "1A", "minLength": 2, "reply": [ { "bitmap": { "byte": 1, "size": 4, "set": [ { "bit": 0, "at": 0, "hex": "11" }, { "bit": 3, "at": 2, "hex": "2233" } ] } } ] },
            { "match": "1B", "status": "07", "reply": [ { "hex": "01" } ] },
            { "match": "1C", "prefix": false, "reply": [ { "hex": "C0FFEE" } ] },
            { "match": "1D", "reply": [ { "hex": "{{string.Concat(Enumerable.Range(0, 94).Select(i => i.ToString("X2")))}}" } ] },
            { "match": "1E", "reply": [ { "counter": "reads" } ] },
            { "match": "1F", "reply": [ { "register": "mode16", "width": 2, "order": "be" } ] },
            { "match": "42", "at": 1, "reply": [ { "hex": "42" } ] },
            { "match": "20", "reply": [ { "input": "a", "bytes": "0-1" } ] },
            { "match": "21", "reply": [ { "value": "serial", "length": { "value": 4 } } ] },
            { "match": "22", "set": { "playerLeds": 3 } },
            { "match": "23", "set": { "playerLeds": 4 } }
          ]
        }
        """);
        pr["replies"] = JsonNode.Parse($$"""{ "channel": "bulkIn", "endpoint": "0x82", "pending": {{pending}}, "overflow": "{{overflow}}" }""");
        pr["input"] = JsonNode.Parse("""{ "endpoint": "0x81", "while": "reportsOn", "clock": { "periodMs": 4, "origin": "inputOpened", "missedTick": "owe" } }""");
        pr["getReport"] = "deviceReport";
        pr["log"] = 8;
        return o.ToJsonString();
    }
}

/// <summary>Builds the next report through the set's own type, as the device
/// clock does.</summary>
internal sealed class Builder : ILayoutVisitor<int>
{
    public ProtocolEngine Engine = null!;
    public byte[] Buffer = new byte[64];
    public int Visit<TSet>(ref TSet set) where TSet : struct, IReportSet => Engine.BuildNext(ref set, Buffer);
}

internal static class Engine
{
    static void Check(string what, bool ok, string detail = "") => Checks.Check(what, ok, detail);

    static ProtocolEngine.CommandResult Send(ProtocolEngine e, string hex) => e.HandleCommand(Checks.Bytes(hex), Stopwatch.GetTimestamp());

    static byte[] Reply(ProtocolEngine e, int capacity = 512)
    {
        var buf = new byte[capacity];
        int n = e.TakeReply(capacity, buf);
        return n < 0 ? Array.Empty<byte>() : buf[..n];
    }

    public static void Run()
    {
        var (e, model, runner, _) = Device.Build(Device.TestProfile());
        int Reg(string name) => model.Registers.IndexOf(name);
        int Ctr(string name) => Array.IndexOf(model.CounterNames, name);
        var build = new Builder { Engine = e };

        Console.WriteLine("-- conditions and layouts");
        Check("no layout's condition holds at rest", e.SelectedLayout == -1, $"selected {e.SelectedLayout}");
        Send(e, "13 0100");
        Check("an in term on a 16-bit register holds for its first value", e.SelectedLayout == 2, $"mode16 0x{e.GetRegister(Reg("mode16")):X4}, selected {e.SelectedLayout}");
        Send(e, "13 0201");
        Check("an in term on a 16-bit register holds for its second value", e.SelectedLayout == 2);
        Send(e, "13 0101");
        Check("an in term on a 16-bit register fails for a value it does not list", e.SelectedLayout == -1);
        Send(e, "16 04");
        Check("|| holds through its second group, a != term", e.SelectedLayout == 2);
        Send(e, "16 00");
        Send(e, "10");
        Check("two layouts on report 0x09: the first's condition selects it", e.SelectedLayout == 0);
        Send(e, "11");
        Check("two layouts on report 0x09: the second's condition selects it", e.SelectedLayout == 1);
        var peek = new byte[16];
        int pn = e.Peek(0x09, peek);
        Check("GET_REPORT of report 0x09 reads the layout whose condition holds", pn == 8 && peek[1] == 0xBB, Checks.Hex(peek.AsSpan(0, pn)));
        Send(e, "10");
        pn = e.Peek(0x09, peek);
        Check("GET_REPORT of report 0x09 follows the condition back to the first", pn == 8 && peek[1] == 0xAA, Checks.Hex(peek.AsSpan(0, pn)));
        pn = e.Peek(0x05, peek);
        Check("GET_REPORT of report 0x05 reads its layout, whose condition does not hold now", pn == 8 && peek[0] == 0x05 && peek[1] == 0xCC, Checks.Hex(peek.AsSpan(0, pn)));

        Console.WriteLine("-- matches");
        Check("a masked match takes any byte under ..", Send(e, "12 FF 34").Rule == 2);
        Check("a masked match still tests its other bytes", Send(e, "12 FF 35").Rule == -1);
        Reply(e);
        Check("a match at an offset", Send(e, "99 42").Rule == 16 && Reply(e) is [0x99, 0x01, 0x42]);

        Console.WriteLine("-- store, or, andNot");
        Send(e, "13 ABCD");
        Check("store reads two bytes high first", e.GetRegister(Reg("mode16")) == 0xABCD, $"0x{e.GetRegister(Reg("mode16")):X4}");
        Send(e, "14 05");
        bool five = e.GetRegister(Reg("selectedReport")) == 5;
        Send(e, "14 07");
        Check("store with in takes a listed value and ignores another", five && e.GetRegister(Reg("selectedReport")) == 5);
        Send(e, "15 80");
        bool on = e.GetRegister(Reg("reportsOn")) == 1;
        Send(e, "15 00");
        Check("store with nonzero writes 1 for any nonzero byte and 0 for zero", on && e.GetRegister(Reg("reportsOn")) == 0);
        Send(e, "16 0F");
        Send(e, "17 FF");
        uint afterOr = e.GetRegister(Reg("featureEnabled"));
        Send(e, "18 05");
        Check("or sets the command's bits under the mask register, andNot clears them", afterOr == 0x0F && e.GetRegister(Reg("featureEnabled")) == 0x0A,
            $"0x{afterOr:X2}, then 0x{e.GetRegister(Reg("featureEnabled")):X2}");
        Send(e, "16 00");

        Console.WriteLine("-- replies");
        Send(e, "19 0A 08");
        Check("a memory reply wraps at the image's size and reads the fill past its segments", Reply(e) is { } m1 && Checks.Hex(m1) == "1901" + "0A0B0C0DEEEE0001", Checks.Hex(Reply(e)));
        Send(e, "19 00 FF");
        var m2 = Reply(e);
        Check("a memory reply's length read from the command is clamped to its max", m2.Length == 2 + 8, $"{m2.Length} bytes");
        Send(e, "1A 09");
        Check("a bitmap writes the entries of the set bits", Checks.Hex(Reply(e)) == "1A01" + "11" + "00" + "2233");
        Send(e, "1A 00");
        Check("a bitmap with no bit set is all zeros", Checks.Hex(Reply(e)) == "1A01" + "00000000");
        Send(e, "1B");
        Check("the prefix writes a rule's own status", Checks.Hex(Reply(e)) == "1B0701");
        Send(e, "1C");
        Check("a rule with prefix false writes its reply alone", Checks.Hex(Reply(e)) == "C0FFEE");
        Send(e, "1F");
        Check("a register part writes the register high first", Checks.Hex(Reply(e)) == "1F01" + "ABCD");
        Send(e, "1E"); Send(e, "1E");
        var r1 = Reply(e); var r2 = Reply(e);
        Check("a read counter goes up each time a reply reads it", r1.Length == 3 && r2.Length == 3 && r2[2] == (byte)(r1[2] + 1), $"{Checks.Hex(r1)} {Checks.Hex(r2)}");
        Send(e, "21");
        Check("a value part writes the device's serial", Encoding.ASCII.GetString(Reply(e).AsSpan(2)) == "HM-T");
        Send(e, "10");
        Send(e, "20");
        Check("an input part writes bytes of a layout as it would go out", Checks.Hex(Reply(e)) == "2001" + "09AA");

        Console.WriteLine("-- bulk replies in read-sized chunks");
        Send(e, "1D");
        Send(e, "1B");
        var c1 = new byte[64]; var c2 = new byte[64]; var c3 = new byte[64];
        int n1 = e.TakeReply(64, c1), n2 = e.TakeReply(64, c2), n3 = e.TakeReply(64, c3);
        Check("a 96-byte reply goes out as 64 and 32 bytes, and the next reply starts a read of its own", n1 == 64 && n2 == 32 && n3 == 3 && c2[0] == 62 - 2 + 2 && c3[0] == 0x1B,
            $"{n1} {n2} {n3}");
        Check("nothing waits after the replies are read", e.TakeReply(64, c1) == -1);
        Check("a read of 0 bytes leaves the reply waiting", Send(e, "1B").Effect.HasFlag(ProtocolEngine.Effect.Reply) && e.TakeReply(0, c1) == 0 && e.TakeReply(64, c1) == 3);

        Console.WriteLine("-- the three overflow rules, two replies pending");
        foreach (var (rule, want) in new[] { ("dropNewest", "1B 1C"), ("dropOldest", "1C 1D"), ("dropOldestUnstarted", "1C 1D") })
        {
            var (o, _, _, _) = Device.Build(Device.TestProfile(rule));
            Send(o, "1B");
            Send(o, "1C");
            Send(o, "1D");
            var got = new List<string>();
            var buf = new byte[512];
            // Each reply named by its command: 1B and 1D echo it first, 1C
            // writes C0FFEE with no prefix.
            for (int n; (n = o.TakeReply(512, buf)) >= 0;) got.Add(buf[0] == 0xC0 ? "1C" : Checks.Hex(buf.AsSpan(0, 1)));
            Check($"{rule}: a third reply leaves {want}", string.Join(" ", got) == want, string.Join(" ", got));
        }
        {
            var (o, _, _, _) = Device.Build(Device.TestProfile("dropOldestUnstarted"));
            Send(o, "1D");
            Send(o, "1B");
            var buf = new byte[64];
            o.TakeReply(64, buf);                      // the head is part read
            Send(o, "1C");                             // full: the started head stays, the new one is dropped
            int a = o.TakeReply(64, buf), b = o.TakeReply(64, buf); byte bh = buf[0];
            int c = o.TakeReply(64, buf);
            Check("dropOldestUnstarted keeps a reply the host is part way through and drops the new one", a == 32 && b == 3 && bh == 0x1B && c == -1, $"{a} {b} {c}");
        }

        Console.WriteLine("-- the device builds its reports");
        Send(e, "15 01");
        Send(e, "10");
        int built = runner!.Visit(build);
        Check("the next report is the selected layout, built through the set's type", built == 8 && build.Buffer[0] == 0x09 && build.Buffer[1] == 0xAA, Checks.Hex(build.Buffer.AsSpan(0, Math.Max(0, built))));
        Send(e, "10");
        Send(e, "13 0100");
        Send(e, "11");
        Send(e, "10");
        Clock(e, model, runner, build, Ctr("ticks"), Reg("playerLeds"));
        SendRules(e, runner, Ctr("reportsSent"));
        Send(e, "10");
        uint before = e.GetCounter(Ctr("reportsSent"));
        runner.Visit(build);
        Check("a report counter advances once per report built", e.GetCounter(Ctr("reportsSent")) == before + 1);
        uint peeked = e.GetCounter(Ctr("reportsSent"));
        e.Peek(0x09, peek);
        Check("a GET_REPORT counts no report", e.GetCounter(Ctr("reportsSent")) == peeked);

        Console.WriteLine("-- no allocation per command, reply or report");
        {
            var cmd = Checks.Bytes("19 0A 08");
            var cmd2 = Checks.Bytes("17 01");
            var dst = new byte[64];
            for (int i = 0; i < 1000; i++) { e.HandleCommand(cmd, i); e.TakeReply(64, dst); e.HandleCommand(cmd2, i); runner.Visit(build); e.Peek(0x09, dst); }
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100_000; i++) { e.HandleCommand(cmd, i); e.TakeReply(64, dst); e.HandleCommand(cmd2, i); }
            long a1 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100_000; i++) { runner.Visit(build); e.Peek(0x09, dst); }
            long a2 = GC.GetAllocatedBytesForCurrentThread();
            Check("100,000 commands with a memory reply and its read allocate nothing", a1 - a0 == 0, $"{a1 - a0} bytes");
            Check("100,000 reports and GET_REPORTs allocate nothing", a2 - a1 == 0, $"{a2 - a1} bytes");
        }

        Console.WriteLine("-- the public view of the registers and counters");
        {
            var view = new HMDeviceProtocol(e);
            bool unknown = false;
            try { view.GetRegister("nosuch"); } catch (ArgumentException) { unknown = true; }
            Check("HMDeviceProtocol reads a register by name, as wide as it is declared", view.GetRegister("mode16") == e.GetRegister(Reg("mode16")) && view.GetRegister("mode16") > 0xFF,
                $"0x{view.GetRegister("mode16"):X}");
            Check("HMDeviceProtocol reads a counter by name", view.CounterNames.Contains("reportsSent") && view.GetCounter("reportsSent") == e.GetCounter(Ctr("reportsSent")));
            Check("HMDeviceProtocol refuses a register the device does not declare", unknown);
        }

        Console.WriteLine("-- the host poll period, on a device that runs a host-poll clock and on two that do not");
        foreach (var (id, polls) in new[] { ("gamecube-adapter", true), ("switch2-pro-controller-composite", false), ("steam-controller-2", false) })
        {
            var view = new HMDeviceProtocol(Device.Build(Device.Catalog(id)).Engine);
            string outcome;
            try { view.HostPollInterval = TimeSpan.FromMilliseconds(2); outcome = $"set, reads {view.HostPollInterval.TotalMilliseconds} ms"; }
            catch (InvalidOperationException ex) { outcome = "refused: " + ex.Message; }
            Check(polls ? $"{id}: HostPollInterval takes a period Windows schedules" : $"{id}: HostPollInterval refuses a period, since no host-poll clock reads it",
                polls ? outcome == "set, reads 2 ms" : outcome.StartsWith("refused: ", StringComparison.Ordinal), outcome);
        }

        Triton();
    }

    sealed class Tick : ILayoutVisitor<int>
    {
        public ProtocolEngine Engine = null!;
        public readonly byte[] Buffer = new byte[64];
        public bool Probe, Submit;
        public int Visit<TSet>(ref TSet set) where TSet : struct, IReportSet =>
            Probe ? (Engine.TickWouldSend(ref set) ? 1 : 0)
            : Submit ? Engine.BuildOnSubmit(ref set, Buffer) : Engine.BuildNext(ref set, Buffer);
    }

    /// <summary>A layout sent on change goes out when its bytes differ from
    /// the ones it last sent, and a tick that would repeat them sends
    /// nothing, counts nothing and owes nothing. A layout sent on submit goes
    /// out on a submit and never on a tick.</summary>
    static void SendRules(ProtocolEngine e, LayoutRunner runner, int sent)
    {
        Console.WriteLine("-- layouts sent on change and on submit");
        var tick = new Tick { Engine = e };
        var probe = new Tick { Engine = e, Probe = true };
        var submit = new Tick { Engine = e, Submit = true };
        var block = new byte[24];
        e.SetStateBlock(block);
        // Layout c, ahead of these, holds while mode16 or featureMask says so.
        Send(e, "13 0000");
        Send(e, "16 00");
        Send(e, "22");
        uint c0 = e.GetCounter(sent);
        int first = runner.Visit(tick);
        bool owesFirst = runner.Visit(probe) == 1;
        int again = runner.Visit(tick);
        uint c1 = e.GetCounter(sent);
        Check("change: the first tick sends, and the next with the same bytes sends nothing and counts nothing",
            first == 4 && tick.Buffer[0] == 0x06 && again == 0 && c1 == c0 + 1 && !owesFirst, $"{first} then {again}, counter +{c1 - c0}");
        block[0] = 0x5A;
        e.SetStateBlock(block);
        bool owes = runner.Visit(probe) == 1;
        int changed = runner.Visit(tick);
        Check("change: new bytes are owed to a tick with no read and sent by the next tick", owes && changed == 4 && tick.Buffer[1] == 0x5A);
        e.Reset();
        e.HandleCommand(Checks.Bytes("15 01"), Stopwatch.GetTimestamp());
        Send(e, "22");
        Check("change: an attach or port reset forgets the bytes last sent", runner.Visit(tick) == 4);
        Send(e, "23");
        bool onSubmit = e.SendsOnSubmit;
        int onTick = runner.Visit(tick);
        bool tickOwes = runner.Visit(probe) == 1;
        uint s0 = e.GetCounter(sent);
        int bySubmit = runner.Visit(submit);
        Check("submit: a tick sends nothing and owes nothing, and a submit sends the report and counts it",
            onSubmit && onTick == 0 && !tickOwes && bySubmit == 4 && submit.Buffer[0] == 0x07 && e.GetCounter(sent) == s0 + 1,
            $"tick {onTick}, submit {bySubmit}");
        Send(e, "22");
        Check("submit: a layout sent on the clock or on change sends nothing on a submit", runner.Visit(submit) == 0 && !e.SendsOnSubmit);
        // Back as the checks after these left them.
        e.HandleCommand(Checks.Bytes("15 01"), Stopwatch.GetTimestamp());
        Send(e, "13 0100");
    }

    /// <summary>A clock counter is the time since the clock's origin over its
    /// period, computed on every write: read far more often than its period,
    /// it matches the elapsed time, never the number of reads.</summary>
    static void Clock(ProtocolEngine e, CompiledModel model, LayoutRunner runner, Builder build, int ticks, int leds)
    {
        e.HandleCommand(Checks.Bytes("15 00"), Stopwatch.GetTimestamp());
        long opened = Stopwatch.GetTimestamp();
        e.HandleCommand(Checks.Bytes("15 01"), opened);
        var sw = Stopwatch.StartNew();
        int reads = 0, off = 0;
        uint last = 0;
        var buf = new byte[16];
        while (sw.ElapsedMilliseconds < 300)
        {
            long before = Stopwatch.GetTimestamp();
            e.Peek(0x09, buf);
            long after = Stopwatch.GetTimestamp();
            uint v = BitConverter.ToUInt32(buf, 2);
            long lo = (before - opened) * 1_000_000 / Stopwatch.Frequency / 1000, hi = (after - opened) * 1_000_000 / Stopwatch.Frequency / 1000;
            if (v < lo || v > hi || v < last) off++;
            last = v;
            reads++;
        }
        Checks.Check($"a clock counter of 1 ms read {reads} times over 300 ms matches the elapsed time on every read", off == 0 && reads > 1000 && last >= 299 && last <= 301,
            $"{off} reads off, last {last}");
    }

    /// <summary>The Triton's IMU register (steam-controller-2.json): SDL's
    /// SET_SETTINGS_VALUES writes, report 0x01, 0x87, a length of three bytes
    /// per setting, then each setting as its ID and a 16-bit value low byte
    /// first (SDL_hidapi_steam_triton.c, SetSettings).</summary>
    static void Triton()
    {
        Console.WriteLine("-- the Triton's IMU register");
        var (e, model, _, _) = Device.Build(Device.Catalog("steam-controller-2"));
        int imu = model.Registers.IndexOf("imuMode");
        Check("imuMode starts at 0", e.GetRegister(imu) == 0);
        Send(e, "01 87 03 30 18 00");
        Check("SET_SETTINGS imu_mode 0x18 sets imuMode to 0x18", e.GetRegister(imu) == 0x18, $"0x{e.GetRegister(imu):X}");
        Send(e, "01 87 03 30 00 00");
        Check("SET_SETTINGS imu_mode 0 clears it", e.GetRegister(imu) == 0);
        Send(e, "01 87 09 2D 01 00 30 18 00 2E 07 00");
        Check("three settings with imu_mode second set it", e.GetRegister(imu) == 0x18, $"0x{e.GetRegister(imu):X}");
        Send(e, "01 87 06 2D 01 00 2E 07 00");
        Check("settings without imu_mode leave it", e.GetRegister(imu) == 0x18);
        Send(e, "01 87 06 2D 01 00 30");
        Check("a record cut short by the command's end is not read", e.GetRegister(imu) == 0x18);
        Send(e, "01 86 03 30 00 00");
        Check("another command leaves it", e.GetRegister(imu) == 0x18);
        // A port reset keeps the setting, which the firmware holds in RAM
        // with no bus-reset handler (sc26re transport_usb.c:261-300), and SDL
        // does not send again while it holds the device open
        // (SDL_hidapi_steam_triton.c:653-677). An attach powers the
        // controller on, which loads the defaults (ibex_settings_registry.c:232-237).
        e.ResetPort();
        Check("a port reset keeps imuMode", e.GetRegister(imu) == 0x18, $"0x{e.GetRegister(imu):X}");
        e.Reset();
        Check("an attach returns imuMode to 0", e.GetRegister(imu) == 0);
        Send(e, "01 87 03 30 18 00");
        Send(e, "01 8E");
        Check("LOAD_DEFAULT_SETTINGS (0x8E) returns imuMode to 0", e.GetRegister(imu) == 0, $"0x{e.GetRegister(imu):X}");
    }
}

internal static class Clock
{
    static void Check(string what, bool ok, string detail = "") => Checks.Check(what, ok, detail);

    /// <summary>A tick that records when it ran, on a clock whose run a test
    /// starts, stops and restarts.</summary>
    internal struct Probe : IClockTick
    {
        public sealed class Box
        {
            public volatile bool Running;
            public long Origin, Period = 1;
            public long NotBefore = 0;
            public readonly List<(long Due, long At)> Ticks = new();
            public int Ends;
            /// <summary>How often the clock read Running.</summary>
            public int Looks;
            /// <summary>The tick that holds the clock's thread for
            /// StallMs, as a preemption would, or -1.</summary>
            public int StallAt = -1, StallMs;
        }
        public Box B;
        public bool Running(out long origin) { Interlocked.Increment(ref B.Looks); origin = Volatile.Read(ref B.Origin); return B.Running; }
        public long Period => Volatile.Read(ref B.Period);
        public long NotBefore => Volatile.Read(ref B.NotBefore);
        public void Tick(long due)
        {
            int n;
            lock (B.Ticks) { B.Ticks.Add((due, Stopwatch.GetTimestamp())); n = B.Ticks.Count; }
            if (n - 1 == B.StallAt) Thread.Sleep(B.StallMs);
        }
        public void RunEnded() => Interlocked.Increment(ref B.Ends);
    }

    public static void Run()
    {
        Console.WriteLine("-- the device clock's grid");
        long P = 1000;
        Check("owe: the first tick is one period after the origin", DeviceClock<Probe>.FirstDue(MissedTick.Owe, 5000, 5100, P) == 6000);
        Check("nextPoll: the first tick is the next grid point after now", DeviceClock<Probe>.FirstDue(MissedTick.NextPoll, 5000, 7250, P) == 8000);
        Check("nextPoll: a start on a grid point waits for the next", DeviceClock<Probe>.FirstDue(MissedTick.NextPoll, 5000, 7000, P) == 8000);
        Check("drop: the first tick is the origin", DeviceClock<Probe>.FirstDue(MissedTick.Drop, 5000, 7250, P) == 5000);
        Check("a tick on time is followed by the next grid point", DeviceClock<Probe>.NextDue(MissedTick.Owe, 6000, 6000, P) == 7000);
        Check("a tick just before its successor's time keeps the grid", DeviceClock<Probe>.NextDue(MissedTick.Owe, 6000, 6999, P) == 7000);
        Check("owe: a tick held up past three grid points skips them and stays on the grid", DeviceClock<Probe>.NextDue(MissedTick.Owe, 6000, 9500, P) == 10000);
        Check("owe: held up to exactly a grid point, the next is the one after", DeviceClock<Probe>.NextDue(MissedTick.Owe, 6000, 9000, P) == 10000);
        Check("nextPoll: held up, it keeps the grid too", DeviceClock<Probe>.NextDue(MissedTick.NextPoll, 6000, 9500, P) == 10000);
        Check("drop: held up, the period restarts from now", DeviceClock<Probe>.NextDue(MissedTick.Drop, 6000, 9500, P) == 10500);
        long f = Stopwatch.Frequency;
        long p125 = (long)Math.Round(1.25 * f / 1000.0);
        long due = 0;
        for (int i = 0; i < 800; i++) due = DeviceClock<Probe>.NextDue(MissedTick.Owe, due, due, p125);
        Check("a 1.25 ms period runs 800 ticks in one second of Stopwatch time", Math.Abs(due - f) <= 800, $"{due} against {f}");

        Console.WriteLine("-- the device clock running: start, stop and restart");
        // Each check holds however the threads are scheduled: a wait is for
        // a condition, with a long timeout, and a preempted clock thread may
        // skip grid points (owe, checked above) but never ticks off its grid,
        // early, twice on one point or more often than the grid allows.
        // The fifth tick holds the thread 7 ms, over three periods.
        var box = new Probe.Box { Period = f / 500, StallAt = 4, StallMs = 7 };    // 2 ms
        var clock = new DeviceClock<Probe>(new Probe { B = box }, MissedTick.Owe, "engine check clock");
        clock.Start();
        Thread.Sleep(30);
        Check("a clock whose run has not started ticks nothing", box.Ticks.Count == 0);
        static bool Until(Func<bool> done, int ms = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (!done()) { if (sw.ElapsedMilliseconds > ms) return false; Thread.Sleep(1); }
            return true;
        }
        int Count() { lock (box.Ticks) return box.Ticks.Count; }
        long origin = Stopwatch.GetTimestamp();
        box.Origin = origin; box.Running = true;
        clock.Wake();
        bool ticked = Until(() => Count() >= 20);
        box.Running = false;
        long stopped = Stopwatch.GetTimestamp();
        clock.Wake();
        bool ended = Until(() => Volatile.Read(ref box.Ends) == 1);
        List<(long Due, long At)> run1;
        lock (box.Ticks) { run1 = box.Ticks.ToList(); box.Ticks.Clear(); box.StallAt = -1; }
        bool onGrid = run1.All(t => (t.Due - origin) % box.Period == 0 && t.Due > origin);
        bool notEarly = run1.All(t => t.At >= t.Due);
        bool increasing = run1.Zip(run1.Skip(1)).All(x => x.Second.Due > x.First.Due);
        long points = (stopped - origin) / box.Period;
        Check("a run ticks within five seconds of its start", ticked, $"{run1.Count} ticks");
        Check("a run's first tick is one period after the origin", run1.Count > 0 && run1[0].Due == origin + box.Period);
        Check("a run ticks on its grid, each grid point at most once and in order", onGrid && increasing, $"{run1.Count} ticks");
        Check("a run never ticks more often than its grid allows", run1.Count <= points + 1, $"{run1.Count} ticks for {points} grid points");
        // Owe: one grid point that fell due during the stall is owed and
        // sent at once, and the points after it that passed during the
        // stall are skipped. Held to what any schedule keeps: the stall ends
        // no sooner than 7 ms after the fifth tick began, so the owed tick
        // was due before that end, and the tick after it is due after it,
        // more than 5 ms and so at least three periods on. A clock late to
        // the fifth tick itself owes a later point, still due in the stall.
        long stallEnd = run1.Count > 4 ? run1[4].At + box.StallMs * f / 1000 : 0;
        bool owed = run1.Count > 6 && run1[5].Due > run1[4].Due && run1[5].Due < stallEnd;
        long gap = run1.Count > 6 ? (run1[6].Due - run1[5].Due) / box.Period : 0;
        Check("a tick held up over three periods owes one report and skips the grid points that passed, with no burst",
            owed && gap >= 3 && run1[6].Due >= stallEnd, $"owed {owed}, then {gap} periods on");
        Check("no tick runs before its time", notEarly);
        Check("the end of a run is seen once", ended && box.Ends == 1, $"{box.Ends}");
        Thread.Sleep(20);
        Check("a stopped run ticks nothing", Count() == 0);
        long origin2 = Stopwatch.GetTimestamp() + f / 1000 * 7 / 10;  // a grid offset from the first run's
        box.Origin = origin2; box.Running = true;
        clock.Wake();
        bool ticked2 = Until(() => Count() >= 15);
        box.Running = false;
        clock.Wake();
        Until(() => Volatile.Read(ref box.Ends) == 2);
        List<(long Due, long At)> run2;
        lock (box.Ticks) run2 = box.Ticks.ToList();
        Check("a restart runs on its own origin's grid", ticked2 && run2.Count >= 15 && run2.All(t => (t.Due - origin2) % box.Period == 0 && t.Due > origin2), $"{run2.Count} ticks");
        clock.Stop(1000);
        Check("Stop ends the clock's thread", !clock.Thread.IsAlive);

        // A clock that every change wakes waits for its next tick in one
        // wait. Waits capped at 100 ms would look at a 500 ms tick six
        // times a period, 18 or more times for three ticks.
        var slow = new Probe.Box { Period = f / 2 };
        var slowClock = new DeviceClock<Probe>(new Probe { B = slow }, MissedTick.Owe, "engine check slow clock", everyChangeWakes: true);
        slowClock.Start();
        slow.Origin = Stopwatch.GetTimestamp(); slow.Running = true;
        slowClock.Wake();
        bool slowTicked = Until(() => { lock (slow.Ticks) return slow.Ticks.Count >= 3; });
        int looks = Volatile.Read(ref slow.Looks);
        slow.Running = false;
        slowClock.Stop(1000);
        Check("a clock that every change wakes looks at a 500 ms tick a few times a period, not every 100 ms",
            slowTicked && looks <= 12, $"{looks} looks for three ticks");

        Console.WriteLine("-- the idle repeat's clock (stream)");
        Stream();
    }

    static void Stream()
    {
        var o = JsonNode.Parse(Device.Catalog("switch2-pro-controller-composite"))!.AsObject();
        o["stream"] = JsonNode.Parse("""{ "periodMs": 1.25, "while": "reportsOn && playerLeds!=0", "timestamps": "advance", "periodRegister": { "name": "playerLeds", "unitMs": 2 } }""");
        var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()))!;
        p.ValidateDeviceModel();
        var model = p.GetOrCompileModel();
        var s = StreamClock.Compile(p, model.Registers)!;
        long f = Stopwatch.Frequency;
        Check("a period of 1.25 ms is 1.25 ms of Stopwatch ticks, not a whole millisecond", s.PeriodTicks == (long)Math.Round(1.25 * f / 1000.0), $"{s.PeriodTicks} ticks at {f} Hz");
        var regs = new uint[model.Registers.Names.Length];
        int on = model.Registers.IndexOf("reportsOn"), leds = model.Registers.IndexOf("playerLeds");
        Check("the period register at 0 leaves the profile's period", s.Period(regs) == s.PeriodTicks);
        regs[leds] = 4;
        Check("the period register sets the period in its unit", s.Period(regs) == (long)(4 * 2 * f / 1000.0), $"{s.Period(regs)}");
        regs[leds] = uint.MaxValue;
        Check("a period register past a minute is held to a minute", s.Period(regs) == (long)Math.Round(60_000.0 * f / 1000.0), $"{s.Period(regs)}");
        regs[leds] = 0;
        var tick = new StreamTick(s, regs, () => { }, () => 0);
        Check("the stream does not run while its condition fails", !tick.Running(out _));
        regs[on] = 1; regs[leds] = 1;
        Check("the stream runs once its condition holds", tick.Running(out _));
        Check("timestamps advance on a repeat", s.AdvanceTimestamps);
        Check("a repeat moves a stamp by the time since the frame, in thirds of a microsecond", StreamClock.Advance(1000, f / 1000) == 1000 + 3000);
        Check("a stamp of 0 stays 0, the codec's own clock stamps it", StreamClock.Advance(0, f) == 0);
        Check("a stamp that wraps to 0 becomes 1, since 0 is no stamp", StreamClock.Advance(uint.MaxValue - 2999, f / 1000) == 1);
        long last = 123456;
        var paced = new StreamTick(s, regs, () => { }, () => last);
        Check("a consumer's frame puts the next repeat one period after it", paced.NotBefore == last + s.Period(regs));
        o["stream"] = JsonNode.Parse("""{ "periodMs": 8 }""");
        var plain = ControllerProfile.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()))!;
        var ps = StreamClock.Compile(plain, plain.GetOrCompileModel().Registers)!;
        Check("a stream without timestamps holds them, as the idle repeat before v2.0.0 did", !ps.AdvanceTimestamps);
        Check("a stream with a condition or a period register keeps the clock's capped wait", s.ReadsRegisters);
        Check("a stream that reads no register waits for its next repeat in one wait", !ps.ReadsRegisters);

        // The catalog's idle repeats: cadence.json's recommended interval for
        // each profile whose real device streams at rest. dualshock-4-v2-bt
        // starts in report 0x01 mode, and no source settles whether that mode
        // repeats, so it has none.
        var expected = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["dualsense"] = 4, ["dualsense-composite"] = 4, ["dualsense-bt"] = 1.25, ["dualsense-bt-full"] = 1.25, ["dualsense-edge-bt"] = 1.25,
            ["dualsense-edge"] = 1, ["dualsense-edge-composite"] = 1,
            ["dualshock-4-v1"] = 4, ["dualshock-4-v1-full"] = 4, ["dualshock-4-v2"] = 4, ["dualshock-4-v2-composite"] = 4, ["steam-deck"] = 4,
            ["thrustmaster-t-gt"] = 250, ["thrustmaster-t150"] = 250, ["thrustmaster-t248"] = 250, ["thrustmaster-t300rs-ps3"] = 250,
            ["thrustmaster-t300rs-ps3-adv"] = 250, ["openffboard"] = 101,
            // The Valve personas' extendedReport.idleFrameIntervalMs, as
            // v1.12.0 repeated them.
            ["steam-controller-2"] = 4, ["steam-controller-composite"] = 8, ["steam-deck-composite"] = 4,
        };
        var actual = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var cp in ProfileDatabase.LoadEmbedded().All)
            if (StreamClock.Compile(cp, cp.GetOrCompileModel().Registers) is { } sc)
                actual[cp.Id] = Math.Round(sc.PeriodTicks * 1000.0 / f, 3);
        var wrong = expected.Where(kv => !actual.TryGetValue(kv.Key, out var ms) || ms != kv.Value).Select(kv => $"{kv.Key} {(actual.TryGetValue(kv.Key, out var ms) ? ms + " ms" : "none")}, not {kv.Value} ms")
            .Concat(actual.Keys.Except(expected.Keys).Select(id => $"{id} repeats every {actual[id]} ms, which the table does not list")).ToList();
        Check($"the catalog's {expected.Count} idle repeats run at cadence.json's intervals, and no other profile repeats", wrong.Count == 0, string.Join("; ", wrong));
        Check("dualshock-4-v2-bt does not repeat: no source settles its 0x01 mode", !actual.ContainsKey("dualshock-4-v2-bt"));
    }
}
