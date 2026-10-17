using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

/// <summary>The broken profiles, one rule each. "every-op" is
/// user/every-op.json (the Switch 2 Pro composite's protocol with a layout
/// that uses every op), "gamecube-adapter" the host-polled protocol with
/// ports, "steam-controller-2" the setReport protocol with a record store.</summary>
internal static partial class RuleChecks
{
    const string EO = "every-op", GC = "gamecube-adapter", SC2 = "steam-controller-2";
    const string L0 = "$.inputReports[0]", P0 = "$.inputReports[0].program", Pr = "$.protocol", Cm = "$.protocol.commands";

    static Case E(string rule, string expect, Action<JsonNode> edit) => new(rule, EO, expect, edit);
    static Case G(string rule, string expect, Action<JsonNode> edit) => new(rule, GC, expect, edit);
    static Case T(string rule, string expect, Action<JsonNode> edit) => new(rule, SC2, expect, edit);

    /// <summary>A rule appended to the every-op protocol, ahead of its
    /// catch-all last rule.</summary>
    static Action<JsonNode> Rule(string json) => root =>
    {
        var rules = root["protocol"]!["commands"]!["rules"]!.AsArray();
        rules.Insert(0, JsonNode.Parse(json));
    };

    /// <summary>A reply part as the only part of a new first rule.</summary>
    static Action<JsonNode> Part(string json, int minLength = 16) => Rule($"{{\"match\":\"7F\",\"minLength\":{minLength},\"reply\":[{json}]}}");

    /// <summary>An op appended to the every-op layout's program.</summary>
    static Action<JsonNode> Op(string json) => Append(P0, json);

    static IEnumerable<Case> Cases()
    {
        // ── The shared parse (ModelJson.cs) ─────────────────────────────
        yield return new("a file that is not JSON", EO, "$", Text: t => t[..(t.Length / 2)]) { Shared = true };
        yield return new("a model group written twice", EO, "$.stateBlock", Text: t => t.Replace("\"stateBlock\":", "\"stateBlock\":{\"size\":1},\"stateBlock\":"));
        yield return E("a key a group does not define", "$.inputReports[0].sise", Set($"{L0}.sise", "1"));
        // The SDK's own context reads a profile's keys in any case and skips
        // the four group keys, so a case variant must be refused here.
        yield return new Case("a group key written InputReports", EO, "$.InputReports", Text: t => t.Replace("\"inputReports\":", "\"InputReports\":")) { Tool = true };
        yield return new Case("a group key written PROTOCOL", EO, "$.PROTOCOL", Text: t => t.Replace("\"protocol\":", "\"PROTOCOL\":")) { Tool = true };
        yield return new Case("a group key written Stateblock", EO, "$.Stateblock", Text: t => t.Replace("\"stateBlock\":", "\"Stateblock\":")) { Tool = true };
        yield return new Case("a group key written LiveFeatureReports", EO, "$.LiveFeatureReports", Set("$.LiveFeatureReports", "[]")) { Tool = true };

        // ── Registers and counters ───────────────────────────────────────
        yield return E("33 registers", "$.protocol.state", Set($"{Pr}.state", "[" + string.Join(",", Enumerable.Range(0, 33).Select(i => $"{{\"name\":\"r{i}\"}}")) + "]"));
        yield return E("a null register", "$.protocol.state[0]", Set($"{Pr}.state[0]", "null"));
        yield return E("a register name that starts with a digit", "$.protocol.state[0].name", Set($"{Pr}.state[0].name", "\"9on\""));
        yield return E("two registers of one name", "$.protocol.state[1].name", Set($"{Pr}.state[1].name", "\"reportsOn\""));
        yield return E("a register of 33 bits", "$.protocol.state[1].bits", Set($"{Pr}.state[1].bits", "33"));
        yield return E("an initial value wider than its register", "$.protocol.state[1].initial", Set($"{Pr}.state[1].initial", "\"0x100\""));
        yield return E("9 counters", "$.protocol.counters", Set($"{Pr}.counters", "[" + string.Join(",", Enumerable.Range(0, 9).Select(i => $"{{\"name\":\"c{i}\",\"on\":\"report\"}}")) + "]"));
        yield return E("a null counter", "$.protocol.counters[0]", Set($"{Pr}.counters[0]", "null"));
        yield return E("a counter name with a dash", "$.protocol.counters[0].name", Set($"{Pr}.counters[0].name", "\"reports-sent\""));
        yield return E("two counters of one name", "$.protocol.counters[1].name", Append($"{Pr}.counters", "{\"name\":\"reportsSent\",\"on\":\"report\"}"));
        yield return E("a counter of 0 bits", "$.protocol.counters[0].bits", Set($"{Pr}.counters[0].bits", "0"));
        yield return E("a counter on no event", "$.protocol.counters[0].on", Set($"{Pr}.counters[0].on", "\"never\""));
        yield return E("a clock counter with no period", "$.protocol.counters[0].periodUs", Set($"{Pr}.counters[0].on", "\"clock\""));
        yield return E("a period on a report counter", "$.protocol.counters[0].periodUs", Set($"{Pr}.counters[0].periodUs", "1000"));
        yield return E("a counter's initial value wider than it", "$.protocol.counters[0].initial", All(Set($"{Pr}.counters[0].bits", "8"), Set($"{Pr}.counters[0].initial", "\"0x100\"")));

        // ── The state block and layouts ──────────────────────────────────
        yield return E("a state block of 0 bytes", "$.stateBlock.size", Set("$.stateBlock.size", "0"));
        yield return E("layouts with no state block", "$.inputReports", Remove("$.stateBlock"));
        yield return E("a decoder on the report extendedOutputReport decodes", "$.protocol.decoders.rumble.reports[0]", Set("$.extendedOutputReport", "{\"reportId\":\"0x02\",\"size\":33,\"fields\":[{\"byte\":1,\"type\":\"uint8\",\"semantic\":\"first\"}]}"));
        yield return E("a state block that no layout or live feature report reads", "$.stateBlock", All(Remove("$.inputReports"), Remove("$.protocol.input.clock"), Remove("$.protocol.getReport")));
        yield return E("an empty layout list", "$.inputReports", Set("$.inputReports", "[]"));
        yield return E("a null layout", "$.inputReports[0]", Set(L0, "null"));
        yield return E("a layout with no name", "$.inputReports[0].name", Set($"{L0}.name", "\"\""));
        yield return E("two layouts of one name", "$.inputReports[1].name", Set("$.inputReports[1].name", "\"pro\""));
        yield return E("a report ID past a byte", "$.inputReports[0].reportId", Set($"{L0}.reportId", "\"0x109\""));
        yield return E("a send rule that is not one", "$.inputReports[0].send", Set($"{L0}.send", "\"sometimes\""));
        yield return E("a while on a register that does not exist", "$.inputReports[0].while", Set($"{L0}.while", "\"nosuch==1\""));
        yield return E("a layout whose program is null", "$.inputReports[0].program", Set(P0, "null"));
        yield return E("a layout of 0 bytes", "$.inputReports[0].size", Set($"{L0}.size", "0"));
        yield return E("a layout past 64 bytes", "$.inputReports[0].size", Set($"{L0}.size", "65"));
        yield return E("byte 0 that is not the report ID", "$.inputReports[0].program", Set($"{P0}[0].const[\"0\"]", "\"08\""));
        yield return E("a block that writes byte 0 over the report ID", $"{P0}[13].ops[2]", Append($"{P0}[13].ops", "{\"const\":{\"0\":\"05\"}}"));
        yield return E("a field that writes byte 0, the report ID's, with no const", $"{P0}[15]",
            All(Set($"{P0}[0].const", "{\"2\":\"25\",\"5\":\"0C\"}"), Op("{\"field\":{\"byte\":0},\"to\":0}")));
        yield return E("a counter that writes byte 0 beside the report ID's const", $"{P0}[15]", Op("{\"counter\":\"reportsSent\",\"at\":0,\"width\":8}"));
        yield return E("two layouts on one ID, one with no condition", "$.inputReports[1].while", All(Set("$.inputReports[1].reportId", "\"0x09\""), Set("$.inputReports[1].program[0].const[\"0\"]", "\"09\""), Remove("$.inputReports[1].while")));
        yield return E("two layouts on one ID whose conditions hold together", "$.inputReports[1].while", All(Set("$.inputReports[1].reportId", "\"0x09\""), Set("$.inputReports[1].program[0].const[\"0\"]", "\"09\""), Set("$.inputReports[1].while", "\"featureEnabled!=0\"")));
        yield return E("two layouts on one ID testing 24 bits", "$.inputReports[1].while", All(Set("$.inputReports[1].reportId", "\"0x09\""), Set("$.inputReports[1].program[0].const[\"0\"]", "\"09\""),
            Set($"{L0}.while", "\"featureMask!=0 && playerLeds!=0\""), Set("$.inputReports[1].while", "\"featureEnabled==0\"")));
        yield return E("layouts with no protocol", "$.inputReports", All(Remove("$.protocol"), Set("$.inputReports", "[{\"name\":\"pro\",\"reportId\":\"0x09\",\"size\":2,\"program\":[{\"const\":{\"0\":\"09\"}}]}]")));
        yield return E("a state block with no fields", "$.stateBlock.fields", Remove("$.stateBlock.fields"));

        // ── Ops as a profile writes them ─────────────────────────────────
        yield return E("a null op", $"{P0}[0]", Set($"{P0}[0]", "null"));
        yield return E("an op of no kind", $"{P0}[15]", Op("{\"to\":3}"));
        yield return E("an op of two kinds", $"{P0}[15]", Op("{\"copy\":\"0\",\"xor\":\"1\",\"to\":3}"));
        yield return E("a parameter the op does not take", $"{P0}[2].deadzone", Set($"{P0}[2].deadzone", "3"));
        yield return E("a const with no bytes", $"{P0}[0].const", Set($"{P0}[0].const", "{}"));
        yield return E("a const keyed by a word", $"{P0}[0].const[\"x\"]", Set($"{P0}[0].const[\"x\"]", "\"01\""));
        yield return E("a const that is not hex", $"{P0}[0].const[\"2\"]", Set($"{P0}[0].const[\"2\"]", "\"2G\""));
        yield return E("a copy range that is not one", $"{P0}[2].copy", Set($"{P0}[2].copy", "\"0-x\""));
        yield return E("a copy with no destination", $"{P0}[2].to", Remove($"{P0}[2].to"));
        yield return E("a masked copy inside a block", $"{P0}[13].ops[1].mask", Set($"{P0}[13].ops[1].mask", "\"0F\""));
        yield return E("a copy mask that moves no bit", $"{P0}[15].mask", Op("{\"copy\":\"22\",\"to\":50,\"mask\":\"00\"}"));
        yield return E("a copy mask of two bytes", $"{P0}[15].mask", Op("{\"copy\":\"22\",\"to\":50,\"mask\":\"0F0F\"}"));
        yield return E("a bit move that is not a pair", $"{P0}[3].bits[0]", Set($"{P0}[3].bits[0]", "[16]"));
        yield return E("an xor with no mask", $"{P0}[4].mask", Remove($"{P0}[4].mask"));
        yield return E("a swap16 of an odd byte count", $"{P0}[5].swap16", Set($"{P0}[5].swap16", "\"12-14\""));
        yield return E("a field scale with a zero denominator", $"{P0}[6].scale", Set($"{P0}[6].scale", "[1,0]"));
        yield return E("a field overflow that is not wrap16", $"{P0}[6].overflow", Set($"{P0}[6].overflow", "\"wrap8\""));
        yield return E("a clamp on a field that wraps", $"{P0}[10].clamp", Set($"{P0}[10].clamp", "[0,5]"));
        yield return E("a clamp of one value", $"{P0}[6].clamp", Set($"{P0}[6].clamp", "[0]"));
        yield return E("a byte order that is not one", $"{P0}[10].order", Set($"{P0}[10].order", "\"middle\""));
        yield return E("a lookup that reads a word", $"{P0}[7].lookup", Set($"{P0}[7].lookup", "{\"word\":2}"));
        yield return E("a lookup table that is not hex", $"{P0}[7].table", Set($"{P0}[7].table", "\"0Z\""));
        yield return E("a hat with one read", $"{P0}[8].hat8", Set($"{P0}[8].hat8", "[{\"word\":16}]"));
        yield return E("a hat with no center", $"{P0}[8].center", Remove($"{P0}[8].center"));
        yield return E("a hat with no dead zone", $"{P0}[8].deadzone", Remove($"{P0}[8].deadzone"));
        yield return E("a pack12 of one offset", $"{P0}[9].pack12", Set($"{P0}[9].pack12", "[4]"));
        yield return E("a counter protocol.counters does not declare", $"{P0}[1].counter", Set($"{P0}[1].counter", "\"nosuch\""));
        yield return E("a counter 12 bits wide", $"{P0}[1].width", Set($"{P0}[1].width", "12"));
        yield return E("a counter with no offset", $"{P0}[1].at", Remove($"{P0}[1].at"));
        yield return E("a counter scale that is a fraction", $"{P0}[12].scale", Set($"{P0}[12].scale", "[1,2]"));
        yield return E("a block inside a block", $"{P0}[13].ops[0].when", Set($"{P0}[13].ops[0]", "{\"when\":\"reportsOn\",\"ops\":[{\"const\":{\"30\":\"01\"}}]}"));
        yield return E("a block with no ops", $"{P0}[13].ops", Set($"{P0}[13].ops", "[]"));
        yield return E("a field inside a block", $"{P0}[13].ops[0]", Set($"{P0}[13].ops[0]", "{\"field\":{\"byte\":0},\"to\":30}"));
        yield return E("a read of a word and a bit", $"{P0}[6].field", Set($"{P0}[6].field", "{\"word\":2,\"bit\":3}"));
        yield return E("a word order that is not one", $"{P0}[10].field.order", Set($"{P0}[10].field.order", "\"xe\""));
        yield return E("an order on a bit read", $"{P0}[6].field.order", Set($"{P0}[6].field.order", "\"le\""));
        yield return E("a byte read with bits", $"{P0}[6].field", Set($"{P0}[6].field", "{\"byte\":2,\"bits\":3}"));
        yield return E("a read of nothing", $"{P0}[6].field", Set($"{P0}[6].field", "{}"));
        yield return E("a null read", $"{P0}[8].hat8[0]", Set($"{P0}[8].hat8[0]", "null"));

        // ── The form's rules, through a profile's paths ──────────────────
        yield return E("an unconditional byte written twice", $"{P0}[15]", Op("{\"copy\":\"0\",\"to\":3}"));
        yield return E("a template bit under an OR op", $"{P0}[3].bits[0][1]", Set($"{P0}[0].const[\"5\"]", "\"0D\""));
        yield return E("a read of 26 bits", $"{P0}[11].field.bits", Set($"{P0}[11].field.bits", "26"));
        yield return E("a read past the state block", $"{P0}[6].field.bit", Set($"{P0}[6].field.bit", "184"));
        yield return E("a field center outside its read", $"{P0}[6].center", Set($"{P0}[6].center", "65536"));
        yield return E("a field scaled past 2^30", $"{P0}[6].scale", Set($"{P0}[6].scale", "[40000,1]"));
        yield return E("field bounds out of order", $"{P0}[11].clamp", Set($"{P0}[11].clamp", "[5,1]"));
        yield return E("a field of 3 bytes", $"{P0}[6].bytes", Set($"{P0}[6].bytes", "3"));
        yield return E("a hat dead zone of 65,536", $"{P0}[8].deadzone", Set($"{P0}[8].deadzone", "65536"));
        yield return E("a hat table of 8 entries", $"{P0}[8].table", Set($"{P0}[8].table", "\"0001020304050607\""));
        yield return E("a hat center outside its reads", $"{P0}[8].center", Set($"{P0}[8].center", "70000"));
        yield return E("a lookup across two state bytes", $"{P0}[7].lookup.bits", Set($"{P0}[7].lookup.bit", "23"));
        yield return E("a lookup of 9 bits", $"{P0}[7].lookup.bits", All(Set($"{P0}[7].lookup.bits", "9"), Set($"{P0}[7].lookup.bit", "16")));
        yield return E("a lookup table of the wrong size", $"{P0}[7].table", Set($"{P0}[7].table", "\"010204\""));
        yield return E("a lookup past the state block", $"{P0}[7].lookup.bit", Set($"{P0}[7].lookup.bit", "200"));
        yield return E("a bit move from past the state block", $"{P0}[3].bits[0][0]", Set($"{P0}[3].bits[0]", "[400,40]"));
        yield return E("a bit move past the report", $"{P0}[3].bits[0][1]", Set($"{P0}[3].bits[0]", "[16,600]"));
        yield return E("an empty bit move list", $"{P0}[3].bits", Set($"{P0}[3].bits", "[]"));
        yield return E("a write past the report", $"{P0}[2]", Set($"{P0}[2].to", "63"));
        yield return E("a template byte set twice", $"{P0}[15]", Op("{\"const\":{\"2\":\"26\"}}"));
        yield return E("a condition with a mask outside its register", $"{P0}[13].when", Set($"{P0}[13].when", "\"featureEnabled&0x100\""));
        yield return E("a value outside its mask", $"{P0}[13].when", Set($"{P0}[13].when", "\"featureEnabled==0x1FF\""));
        yield return E("an empty condition", $"{P0}[13].when", Set($"{P0}[13].when", "\" \""));
        yield return E("an empty term", $"{P0}[13].when", Set($"{P0}[13].when", "\"reportsOn && \""));
        yield return E("a term value that is not hex", $"{P0}[13].when", Set($"{P0}[13].when", "\"featureEnabled==zz\""));
        yield return E("nine groups", $"{P0}[13].when", Set($"{P0}[13].when", "\"" + string.Join(" || ", Enumerable.Range(0, 9).Select(i => $"playerLeds=={i}")) + "\""));
        yield return E("an in value outside its register", $"{P0}[13].when", Set($"{P0}[13].when", "\"featureEnabled in 1,100\""));

        // ── Decoders ─────────────────────────────────────────────────────
        const string D = "$.protocol.decoders.rumble";
        yield return E("a null decoder", D, Set(D, "null"));
        yield return E("a decoder name with a dash", "$.protocol.decoders.rum-ble", Set("$.protocol.decoders[\"rum-ble\"]", "{\"minLength\":2,\"fields\":[{\"semantic\":\"x\",\"type\":\"packed-uint\",\"parts\":[{\"pieces\":[{\"at\":8,\"count\":4}]}]}]}"));
        yield return E("a decoder that reads no byte", $"{D}.minLength", Set($"{D}.minLength", "0"));
        yield return E("a decoder with no fields", $"{D}.fields", Set($"{D}.fields", "[]"));
        yield return E("a decoder report that is not a byte", $"{D}.reports[0]", Set($"{D}.reports[0]", "\"0x102\""));
        yield return E("a null decoded field", $"{D}.fields[0]", Set($"{D}.fields[0]", "null"));
        yield return E("a decoded field of another type", $"{D}.fields[0].type", Set($"{D}.fields[0].type", "\"uint8\""));
        yield return E("a decoded field with no name", $"{D}.fields[0].semantic", Set($"{D}.fields[0].semantic", "\" \""));
        yield return E("two decoded fields of one name", $"{D}.fields[1].semantic", Set($"{D}.fields[1].semantic", "\"leftMotor\""));
        yield return E("a decoded field with no parts", $"{D}.fields[0].parts", Set($"{D}.fields[0].parts", "[]"));
        yield return E("a null part", $"{D}.fields[0].parts[0]", Set($"{D}.fields[0].parts[0]", "null"));
        yield return E("a part with no pieces", $"{D}.fields[0].parts[0].pieces", Set($"{D}.fields[0].parts[0].pieces", "[]"));
        yield return E("frames of one number", $"{D}.fields[0].parts[0].frames", Set($"{D}.fields[0].parts[0].frames", "[3]"));
        yield return E("a null piece", $"{D}.fields[0].parts[0].pieces[0]", Set($"{D}.fields[0].parts[0].pieces[0]", "null"));
        yield return E("a piece of 0 bits", $"{D}.fields[0].parts[0].pieces[0].count", Set($"{D}.fields[0].parts[0].pieces[0].count", "0"));
        yield return E("a piece before bit 0", $"{D}.fields[0].parts[0].pieces[0].at", Set($"{D}.fields[0].parts[0].pieces[0].at", "-1"));
        yield return E("a part of 13 bits", $"{D}.fields[0].parts[0].pieces", Set($"{D}.fields[0].parts[0].pieces", "[{\"at\":46,\"count\":7},{\"at\":53,\"count\":6}]"));
        yield return E("a piece of 13 bits", $"{D}.fields[0].parts[0].pieces[0].count", Set($"{D}.fields[0].parts[0].pieces[0].count", "13"));
        yield return E("a range high first", $"{D}.fields[0].parts[0].range", Set($"{D}.fields[0].parts[0].range", "[9,1]"));
        yield return E("a part scale over 0", $"{D}.fields[0].parts[0].scale", Set($"{D}.fields[0].parts[0].scale", "[1,0]"));
        yield return E("a rounding that is not one", $"{D}.fields[0].parts[0].round", Set($"{D}.fields[0].parts[0].round", "\"up\""));
        yield return E("a map read over frames that decreases", $"{D}.fields[0].parts[0].frames", Set($"{D}.fields[0].parts[0].range", "[0,500]"));
        yield return E("a part read past the report", $"{D}.fields[0].parts[0].pieces[0].at", Set($"{D}.fields[0].parts[0].pieces[0].at", "400"));

        // ── Emitted output and live feature reports ──────────────────────
        yield return E("a rule that emits a report with no minLength", "$.protocol.commands.rules[0].minLength", Rule("{\"match\":\"7E\",\"emitOutput\":{\"reportId\":\"0x01\",\"size\":2,\"program\":[{\"const\":{\"0\":\"01\"}}]}}"));
        yield return E("an emitted report ID that is not a byte", "$.protocol.commands.rules[0].emitOutput.reportId", Rule("{\"match\":\"7E\",\"minLength\":4,\"emitOutput\":{\"reportId\":\"zz\",\"size\":2,\"program\":[{\"const\":{\"0\":\"01\"}}]}}"));
        yield return E("an emitted report of 0 bytes", "$.protocol.commands.rules[0].emitOutput.size", Rule("{\"match\":\"7E\",\"minLength\":4,\"emitOutput\":{\"reportId\":\"0x01\",\"size\":0,\"program\":[{\"const\":{\"0\":\"01\"}}]}}"));
        yield return E("live feature reports with no state block", "$.liveFeatureReports", All(Remove("$.stateBlock"), Remove("$.inputReports"), Set("$.liveFeatureReports", "[{\"reportId\":\"0x01\",\"size\":2,\"program\":[{\"copy\":\"0\",\"to\":1}]}]")));
        yield return E("a null live feature report", "$.liveFeatureReports[0]", Set("$.liveFeatureReports", "[null]"));
        yield return E("a live feature report ID that is not a byte", "$.liveFeatureReports[0].reportId", Set("$.liveFeatureReports", "[{\"reportId\":\"0x1FF\",\"size\":2,\"program\":[{\"copy\":\"0\",\"to\":1}]}]"));

        foreach (var c in ProtocolCases()) yield return c;
        foreach (var c in StreamCases()) yield return c;
        foreach (var c in BoundsCases()) yield return c;
    }
}
