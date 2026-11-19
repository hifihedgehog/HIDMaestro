using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

/// <summary>Message streams, timers, packet lists, bit matches,
/// fall-through rules and skipZero counters, each broken by one edit. The
/// base is xbox-one-s-full, the GIP persona, which uses all of them.</summary>
internal static partial class RuleChecks
{
    const string XO = "xbox-one-s-full";

    static Case X(string rule, string expect, Action<JsonNode> edit) => new(rule, XO, expect, edit);

    static IEnumerable<Case> MessageCases()
    {
        const string Tm = "$.protocol.timers", R3 = "$.protocol.commands.rules[3]";
        const string Timer = "[{\"name\":\"hello\",\"periods\":[{\"periodMs\":500}],\"reply\":[{\"hex\":\"02\"}]}]";
        string Hex(int bytes) => "[{\"hex\":\"" + new string('0', bytes * 2) + "\"}]";

        // Streams and the first report (ModelCompiler.cs).
        yield return X("a stream name with a dash", "$.inputReports[0].stream", Set("$.inputReports[0].stream", "\"in-put\""));
        yield return X("a submit layout in a named stream", "$.inputReports[0].send", Set("$.inputReports[0].send", "\"submit\""));
        yield return X("a first rule that is not one", "$.inputReports[1].first", Set("$.inputReports[1].first", "\"later\""));
        yield return X("a first rule on a layout of the unnamed stream", "$.inputReports[1].first", Remove("$.inputReports[1].stream"));
        yield return X("skipZero on a clock counter", "$.protocol.counters[0].skipZero", All(Set("$.protocol.counters[0].on", "\"clock\""), Set("$.protocol.counters[0].periodUs", "1000")));

        // Timers.
        yield return U("timers on a UMDF2 device", Tm, Set(Tm, Timer));
        yield return G("timers on a device that sends the SDK's report", Tm, Set(Tm, Timer));
        yield return E("timers with bulk replies", Tm, Set(Tm, Timer));
        yield return X("a timer with no name", $"{Tm}[0].name", Set($"{Tm}[0].name", "\"\""));
        yield return X("two timers of one name", $"{Tm}[1].name", Set($"{Tm}[1].name", "\"hello\""));
        yield return X("a timer with no periods", $"{Tm}[0].periods", Remove($"{Tm}[0].periods"));
        yield return X("a timer period of 0.5 ms", $"{Tm}[0].periods[0].periodMs", Set($"{Tm}[0].periods[0].periodMs", "0.5"));
        yield return X("a count on a timer's last step", $"{Tm}[0].periods[0].count", Set($"{Tm}[0].periods[0].count", "3"));
        yield return X("no count on a step before the last", $"{Tm}[1].periods[0].count", Remove($"{Tm}[1].periods[0].count"));
        yield return X("a negative firstMs", $"{Tm}[0].firstMs", Set($"{Tm}[0].firstMs", "-1"));
        yield return X("a timer with no reply", $"{Tm}[0].reply", Remove($"{Tm}[0].reply"));
        yield return X("a timer message past the input packet", $"{Tm}[1].reply", Set($"{Tm}[1].reply", Hex(65)));
        yield return X("a reply past the input packet", "$.protocol.commands.rules[0]", Rule("{\"match\":\"7E\",\"reply\":" + Hex(65) + "}"));

        // Packet lists.
        yield return U("a packet list on a UMDF2 device", "$.protocol.commands.rules[0].replies", Rule("{\"match\":\"01\",\"minLength\":11,\"replies\":[[{\"hex\":\"21\"}],[{\"hex\":\"21\"}]]}"));
        yield return X("a reply and a packet list", R3, Set($"{R3}.reply", "[{\"hex\":\"00\"}]"));
        yield return X("a status on a packet list", $"{R3}.replies", Set($"{R3}.status", "\"00\""));
        yield return X("a packet list of one", $"{R3}.replies", Set($"{R3}.replies", "[[{\"hex\":\"04\"}]]"));
        yield return X("a packet list past the pending replies", $"{R3}.replies", Set("$.protocol.replies.pending", "2"));
        yield return X("a null packet", $"{R3}.replies[1]", Set($"{R3}.replies[1]", "null"));
        yield return X("an empty packet", $"{R3}.replies[1]", Set($"{R3}.replies[1]", "[]"));
        yield return X("a packet past the reports the channel carries", $"{R3}.replies[1]", Set($"{R3}.replies[1]", Hex(258)));

        // Bit matches and fall-through.
        yield return X("a bit match whose mask is not hex", "$.protocol.commands.rules[0].match", Set("$.protocol.commands.rules[0].match", "\".. 30/GG\""));
        yield return X("a bit match that sets bits outside its mask", "$.protocol.commands.rules[0].match", Set("$.protocol.commands.rules[0].match", "\".. 30/0F\""));
        yield return E("a rule with a condition that emits", "$.protocol.commands.rules[0].while", Rule("{\"match\":\"7F\",\"while\":\"reportsOn\",\"emitOutput\":{\"reportId\":\"0x01\",\"size\":8,\"memory\":\"flash\"}}"));
        yield return E("a fall-through rule that emits", "$.protocol.commands.rules[0].continue", Rule("{\"match\":\"7F\",\"continue\":true,\"emitOutput\":{\"reportId\":\"0x01\",\"size\":8,\"memory\":\"flash\"}}"));

        // The serial's hex digits.
        yield return E("hex-le of a value that is not the serial", "$.protocol.commands.rules[0].reply[0].encoding", Part("{\"value\":\"index\",\"encoding\":\"hex-le\",\"length\":{\"value\":4}}"));
        yield return E("hex-le with no length", "$.protocol.commands.rules[0].reply[0].length", Part("{\"value\":\"serial\",\"encoding\":\"hex-le\"}"));

        // An emitted report from memory names its ID.
        yield return E("an emitted report from memory with no report ID", "$.protocol.commands.rules[0].emitOutput.reportId", Rule("{\"match\":\"7F\",\"emitOutput\":{\"size\":8,\"memory\":\"flash\"}}"));
    }
}
