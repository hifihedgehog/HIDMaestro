using System.Collections.Generic;
using System.Linq;

/// <summary>The protocol's rules (DeviceProtocol.cs) and the stream's
/// (StreamClock.cs), each broken by one edit.</summary>
internal static partial class RuleChecks
{
    static IEnumerable<Case> ProtocolCases()
    {
        const string In = "$.protocol.input", R0 = "$.protocol.commands.rules[0]", M = "$.protocol.memory[0]";
        string Rp(int i) => $"{R0}.reply[{i}]";

        // Ports, input and its clock.
        yield return G("a protocol device with 33 ports", "$.extendedReport.ports.count", All(Set("$.extendedReport.ports", "{\"count\":33,\"offset\":1,\"stride\":1}"), Remove("$.extendedOutputReport")));
        yield return E("an input with no endpoint", $"{In}.endpoint", Remove($"{In}.endpoint"));
        yield return E("an input endpoint the descriptor lacks", $"{In}.endpoint", Set($"{In}.endpoint", "\"0x85\""));
        yield return E("an input endpoint of 0-byte packets", $"{In}.endpoint", All(Set("$.usbConfiguration.configurationDescriptor", "\"09025000020104c0fa080b0001030000000904000002030000050921110100012261000705810300000407050103400004080b0101ff0000000904010002ff0000060705020240000007058202400000\""), Set("$.usbConfiguration.interfaces[0].altSettings[0].endpoints[0].maxPacketSize", "0")));
        yield return E("an input endpoint that is bulk", $"{In}.endpoint", Set($"{In}.endpoint", "\"0x82\""));
        yield return E("a device that builds its reports with no period", $"{In}.clock.periodMs", Remove($"{In}.clock.periodMs"));
        yield return E("a device that builds its reports with a source poll", $"{In}.clock", Set($"{In}.clock.sourcePollMs", "1"));
        yield return E("a clock origin that is not one", $"{In}.clock.origin", Set($"{In}.clock.origin", "\"boot\""));
        yield return E("a missed-tick rule that is not one", $"{In}.clock.missedTick", Set($"{In}.clock.missedTick", "\"skip\""));
        yield return G("a period on a device that sends the SDK's report", $"{In}.clock", Set($"{In}.clock.periodMs", "4"));
        yield return G("a source poll of 0 ms", $"{In}.clock.sourcePollMs", Set($"{In}.clock.sourcePollMs", "0"));
        yield return G("source poll rules with no source poll", $"{In}.clock.sourcePollRules", Remove($"{In}.clock.sourcePollMs"));
        yield return G("a null source poll rule", $"{In}.clock.sourcePollRules[0]", Set($"{In}.clock.sourcePollRules[0]", "null"));
        yield return G("a source poll rule with no ports", $"{In}.clock.sourcePollRules[0].portsConnected", Set($"{In}.clock.sourcePollRules[0].portsConnected", "null"));
        yield return G("a source poll rule with no ports to read", $"{In}.clock.sourcePollRules[0].portsConnected", All(Remove("$.extendedReport.ports"), Remove("$.extendedOutputReport")));
        yield return G("a source poll rule of 0 ms", $"{In}.clock.sourcePollRules[0].periodMs", Set($"{In}.clock.sourcePollRules[0].periodMs", "0"));
        yield return G("a source poll rule on port 5", $"{In}.clock.sourcePollRules[0].portsConnected", Set($"{In}.clock.sourcePollRules[0].portsConnected", "[1,5]"));
        yield return E("a device that builds its reports with no input", $"{Pr}.input", Remove(In));

        // Replies.
        yield return E("a reply channel that is not one", $"{Pr}.replies.channel", Set($"{Pr}.replies.channel", "\"interrupt\""));
        yield return E("bulk replies with no endpoint", $"{Pr}.replies.endpoint", Remove($"{Pr}.replies.endpoint"));
        yield return G("an endpoint on input-channel replies", $"{Pr}.replies.endpoint", Set($"{Pr}.replies.endpoint", "\"0x81\""));
        yield return E("2,000 pending replies", $"{Pr}.replies.pending", Set($"{Pr}.replies.pending", "2000"));
        yield return E("an overflow rule that is not one", $"{Pr}.replies.overflow", Set($"{Pr}.replies.overflow", "\"block\""));
        yield return E("a reply with no input endpoint to send it on", Cm, All(Remove(In), Remove("$.inputReports"), Remove("$.liveFeatureReports"), Remove("$.stateBlock"), Set($"{Pr}.replies", "{\"channel\":\"input\"}"), Remove($"{Pr}.getReport")));

        // Commands.
        yield return E("a command endpoint that is IN", $"{Cm}.endpoint", Set($"{Cm}.endpoint", "\"0x81\""));
        yield return E("a command endpoint that is not a byte", $"{Cm}.endpoint", Set($"{Cm}.endpoint", "\"0x1FF\""));
        yield return E("a command endpoint of 0-byte packets", $"{Cm}.endpoint", All(Set("$.usbConfiguration.configurationDescriptor", "\"09025000020104c0fa080b0001030000000904000002030000050921110100012261000705810340000407050103400004080b0101ff0000000904010002ff0000060705020200000007058202400000\""), Set("$.usbConfiguration.interfaces[1].altSettings[0].endpoints[0].maxPacketSize", "0")));
        yield return E("a command source that is not one", $"{Cm}.source", Set($"{Cm}.source", "\"usb\""));
        yield return E("output reports on USB/IP", $"{Cm}.source", Set($"{Cm}.source", "\"outputReport\""));
        yield return E("setReport with an endpoint", $"{Cm}.source", Set($"{Cm}.source", "\"setReport\""));
        yield return E("a source that does not match the endpoint", $"{Cm}.source", Set($"{Cm}.source", "\"interruptOut\""));
        yield return E("commands from nowhere", Cm, Remove($"{Cm}.endpoint"));
        yield return T("setReportStripsId off SET_REPORT", $"{Cm}.setReportStripsId", All(Set($"{Cm}.source", "null"), Set($"{Cm}.endpoint", "\"0x01\""), Set($"{Cm}.setReportStripsId", "true")));
        yield return E("a negative minLength", $"{Cm}.minLength", Set($"{Cm}.minLength", "-1"));
        yield return E("a status that is not one byte", $"{Cm}.status", Set($"{Cm}.status", "\"0101\""));
        yield return E("no rules", $"{Cm}.rules", Remove($"{Cm}.rules"));
        yield return E("a null rule", R0, Set(R0, "null"));
        yield return G("ports past one packet with the ID stripped", "$.extendedOutputReport.ports", All(Set("$.extendedOutputReport.ports.stride", "2"), Set("$.extendedOutputReport.size", "9")));
        yield return G("an output field past one packet with the ID stripped", "$.extendedOutputReport.fields[0]", All(Set("$.extendedOutputReport.size", "9"), Set("$.extendedOutputReport.fields", "[{\"byte\":7,\"type\":\"uint8\",\"semantic\":\"x\"}]")));

        // Rules.
        yield return E("a rule at a negative byte", $"{R0}.at", Set($"{R0}.at", "-1"));
        yield return E("an empty match", $"{R0}.match", Set($"{R0}.match", "\" \""));
        yield return E("a match token that is not hex", $"{R0}.match", Set($"{R0}.match", "\"02 xx\""));
        yield return E("a rule no length admits", R0, Set($"{R0}.maxLength", "10"));
        yield return G("a maxLength shorter than the match", $"{Cm}.rules[1].maxLength", Set($"{Cm}.rules[1].maxLength", "0"));
        yield return G("a match past an interrupt packet", $"{Cm}.rules[0].match", Set($"{Cm}.rules[0].match", "\"11 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00\""));
        yield return G("a minLength past an interrupt packet", $"{Cm}.rules[0].minLength", Set($"{Cm}.rules[0].minLength", "65"));
        yield return E("a set of a register that does not exist", $"{R0}.set", Rule("{\"match\":\"7F\",\"set\":{\"nosuch\":1}}"));
        yield return E("a set wider than its register", $"{R0}.set.selectedReport", Rule("{\"match\":\"7F\",\"set\":{\"selectedReport\":256}}"));
        yield return E("a store of a register that does not exist", $"{R0}.store", Rule("{\"match\":\"7F\",\"minLength\":9,\"store\":{\"nosuch\":{\"byte\":8}}}"));
        yield return E("a null store", $"{R0}.store.playerLeds", Rule("{\"match\":\"7F\",\"minLength\":9,\"store\":{\"playerLeds\":null}}"));
        yield return E("a store of 5 bytes", $"{R0}.store.playerLeds.bytes", Rule("{\"match\":\"7F\",\"minLength\":16,\"store\":{\"playerLeds\":{\"byte\":8,\"bytes\":5}}}"));
        yield return E("a store at a negative byte", $"{R0}.store.playerLeds.byte", Rule("{\"match\":\"7F\",\"minLength\":9,\"store\":{\"playerLeds\":{\"byte\":-1}}}"));
        yield return E("a store past the minLength", $"{R0}.store.playerLeds", Rule("{\"match\":\"7F\",\"minLength\":9,\"store\":{\"playerLeds\":{\"byte\":9}}}"));
        yield return E("a store wider than its register", $"{R0}.store.playerLeds", Rule("{\"match\":\"7F\",\"minLength\":12,\"store\":{\"playerLeds\":{\"byte\":8,\"bytes\":2}}}"));
        yield return E("a store with in and nonzero", $"{R0}.store.playerLeds", Rule("{\"match\":\"7F\",\"minLength\":9,\"store\":{\"playerLeds\":{\"byte\":8,\"nonzero\":true,\"in\":[\"01\"]}}}"));
        yield return E("a store in value the register cannot hold", $"{R0}.store.playerLeds.in[1]", Rule("{\"match\":\"7F\",\"minLength\":9,\"store\":{\"playerLeds\":{\"byte\":8,\"in\":[\"01\",\"100\"]}}}"));
        yield return E("a store order that is not one", $"{R0}.store.playerLeds.order", Rule("{\"match\":\"7F\",\"minLength\":9,\"store\":{\"playerLeds\":{\"byte\":8,\"order\":\"me\"}}}"));
        const string SR = "$.protocol.commands.rules[0].storeRecord";
        string Rec(string body) => $"{{\"match\":\"01 87\",\"minLength\":3,\"storeRecord\":{body}}}";
        Case Tr(string rule, string expect, string body) => T(rule, expect, Set($"{Cm}.rules[0]", Rec(body)));
        yield return Tr("records of 0 bytes", SR, "{\"at\":3,\"size\":0,\"count\":{\"byte\":2},\"key\":{\"offset\":0,\"value\":\"30\"},\"store\":{\"imuMode\":{\"offset\":1,\"bytes\":2}}}");
        yield return Tr("a record count divided by 0", $"{SR}.count.divideBy", "{\"at\":3,\"size\":3,\"count\":{\"byte\":2,\"divideBy\":0},\"key\":{\"offset\":0,\"value\":\"30\"},\"store\":{\"imuMode\":{\"offset\":1,\"bytes\":2}}}");
        yield return Tr("a record count at a negative byte", $"{SR}.count.byte", "{\"at\":3,\"size\":3,\"count\":{\"byte\":-1},\"key\":{\"offset\":0,\"value\":\"30\"},\"store\":{\"imuMode\":{\"offset\":1,\"bytes\":2}}}");
        yield return Tr("a record count past the minLength", $"{SR}.count.byte", "{\"at\":3,\"size\":3,\"count\":{\"byte\":3},\"key\":{\"offset\":0,\"value\":\"30\"},\"store\":{\"imuMode\":{\"offset\":1,\"bytes\":2}}}");
        yield return Tr("a record key outside the record", $"{SR}.key.offset", "{\"at\":3,\"size\":3,\"count\":{\"byte\":2},\"key\":{\"offset\":3,\"value\":\"30\"},\"store\":{\"imuMode\":{\"offset\":1,\"bytes\":2}}}");
        yield return Tr("a record key that is not one byte", $"{SR}.key.value", "{\"at\":3,\"size\":3,\"count\":{\"byte\":2},\"key\":{\"offset\":0,\"value\":\"3030\"},\"store\":{\"imuMode\":{\"offset\":1,\"bytes\":2}}}");
        yield return Tr("a record that stores nothing", $"{SR}.store", "{\"at\":3,\"size\":3,\"count\":{\"byte\":2},\"key\":{\"offset\":0,\"value\":\"30\"},\"store\":{}}");
        yield return Tr("a record field outside the record", $"{SR}.store.imuMode", "{\"at\":3,\"size\":3,\"count\":{\"byte\":2},\"key\":{\"offset\":0,\"value\":\"30\"},\"store\":{\"imuMode\":{\"offset\":2,\"bytes\":2}}}");
        yield return Tr("a record field wider than its register", $"{SR}.store.imuMode", "{\"at\":3,\"size\":5,\"count\":{\"byte\":2},\"key\":{\"offset\":0,\"value\":\"30\"},\"store\":{\"imuMode\":{\"offset\":1,\"bytes\":4}}}");
        yield return Tr("a record field order that is not one", $"{SR}.store.imuMode.order", "{\"at\":3,\"size\":3,\"count\":{\"byte\":2},\"key\":{\"offset\":0,\"value\":\"30\"},\"store\":{\"imuMode\":{\"offset\":1,\"bytes\":2,\"order\":\"x\"}}}");
        yield return Tr("a record of a register that does not exist", $"{SR}.store", "{\"at\":3,\"size\":3,\"count\":{\"byte\":2},\"key\":{\"offset\":0,\"value\":\"30\"},\"store\":{\"nosuch\":{\"offset\":1,\"bytes\":2}}}");
        yield return E("a null or", $"{R0}.or.featureEnabled", Rule("{\"match\":\"7F\",\"minLength\":9,\"or\":{\"featureEnabled\":null}}"));
        yield return E("an or past the minLength", $"{R0}.or.featureEnabled.byte", Rule("{\"match\":\"7F\",\"minLength\":9,\"or\":{\"featureEnabled\":{\"byte\":9,\"mask\":\"featureMask\"}}}"));
        yield return E("an andNot masked by a register that does not exist", $"{R0}.andNot.featureEnabled.mask", Rule("{\"match\":\"7F\",\"minLength\":9,\"andNot\":{\"featureEnabled\":{\"byte\":8,\"mask\":\"nosuch\"}}}"));
        yield return E("a null memory write", $"{R0}.write[0]", Rule("{\"match\":\"7F\",\"minLength\":16,\"write\":[null]}"));
        yield return E("a write to a memory that does not exist", $"{R0}.write[0].memory", Rule("{\"match\":\"7F\",\"minLength\":16,\"write\":[{\"memory\":\"eeprom\",\"address\":{\"le32\":4},\"from\":\"8-15\"}]}"));
        yield return E("a write from past the minLength", $"{R0}.write[0].from", Rule("{\"match\":\"7F\",\"minLength\":16,\"write\":[{\"memory\":\"flash\",\"address\":{\"le32\":4},\"from\":\"8-16\"}]}"));
        yield return E("a decode that does not exist", $"{R0}.decode", Rule("{\"match\":\"7F\",\"decode\":\"nosuch\"}"));
        yield return E("a decode past the minLength", $"{R0}.decode", Rule("{\"match\":\"7F\",\"minLength\":9,\"decode\":\"rumble\"}"));
        yield return E("an empty reply", $"{R0}.reply", All(Remove($"{Cm}.replyPrefix"), Rule("{\"match\":\"7F\",\"reply\":[]}")));
        yield return E("a replyLength shorter than the reply", $"{R0}.replyLength", Rule("{\"match\":\"7F\",\"reply\":[{\"hex\":\"01020304\"}],\"replyLength\":4}"));
        yield return G("a reply past the input channel's reports", $"{Cm}.rules[2].reply", Set($"{Cm}.rules[2].reply[0].hex", "\"" + new string('2', 4000) + "\""));
        yield return E("a status with no reply", R0, Rule("{\"match\":\"7F\",\"status\":\"02\"}"));
        yield return E("a rule status that is not one byte", $"{R0}.status", Rule("{\"match\":\"7F\",\"status\":\"zz\",\"reply\":[{\"hex\":\"01\"}]}"));

        // Reply parts.
        yield return E("a null reply part", Rp(0), Part("null"));
        yield return E("a part of two kinds", Rp(0), Part("{\"hex\":\"01\",\"state\":\"reportsOn\"}"));
        yield return E("a key the part does not take", $"{Rp(0)}.width", Part("{\"hex\":\"01\",\"width\":2}"));
        yield return E("a part's while on a register that does not exist", $"{Rp(0)}.while", Part("{\"hex\":\"01\",\"while\":\"nosuch\"}"));
        yield return E("a hex part that is not hex", $"{Rp(0)}.hex", Part("{\"hex\":\"0G\"}"));
        yield return E("a state part of a register that does not exist", $"{Rp(0)}.state", Part("{\"state\":\"nosuch\"}"));
        yield return E("a snapshot with no ports", $"{Rp(0)}.snapshot", Part("{\"snapshot\":\"origin\"}"));
        yield return G("a snapshot that does not exist", $"{Cm}.rules[2].reply[1].snapshot", Set($"{Cm}.rules[2].reply[1].snapshot", "\"later\""));
        yield return G("snapshot bytes past the port block", $"{Cm}.rules[2].reply[1].bytes", Set($"{Cm}.rules[2].reply[1].bytes", "\"3-9\""));
        yield return E("an echo that is not a range", $"{Rp(0)}.echo", Part("{\"echo\":\"x\"}"));
        yield return E("an echo past the minLength", $"{Rp(0)}.echo", Part("{\"echo\":\"15-16\"}"));
        yield return E("a max on a two-byte echo", $"{Rp(0)}.max", Part("{\"echo\":\"8-9\",\"max\":4}"));
        yield return E("an echo max of 256", $"{Rp(0)}.max", Part("{\"echo\":\"8\",\"max\":256}"));
        yield return E("a register part that does not exist", $"{Rp(0)}.register", Part("{\"register\":\"nosuch\"}"));
        yield return E("a register part 5 bytes wide", $"{Rp(0)}.width", Part("{\"register\":\"playerLeds\",\"width\":5}"));
        yield return E("a register part order that is not one", $"{Rp(0)}.order", Part("{\"register\":\"playerLeds\",\"order\":\"x\"}"));
        yield return E("a counter part that does not exist", $"{Rp(0)}.counter", Part("{\"counter\":\"nosuch\"}"));
        yield return E("a counter part 0 bytes wide", $"{Rp(0)}.width", Part("{\"counter\":\"reportsSent\",\"width\":0}"));
        yield return E("a memory part with no address", $"{Rp(0)}.address", Part("{\"memory\":\"flash\",\"length\":{\"value\":4}}"));
        yield return E("a memory part with no length", $"{Rp(0)}.length", Part("{\"memory\":\"flash\",\"address\":{\"le32\":8}}"));
        yield return E("a memory part longer than 4096", $"{Rp(0)}.length", Part("{\"memory\":\"flash\",\"address\":{\"le32\":8},\"length\":{\"le16\":12}}"));
        yield return E("a memory part of a memory that does not exist", $"{Rp(0)}.memory", Part("{\"memory\":\"eeprom\",\"address\":{\"le32\":8},\"length\":{\"value\":4}}"));
        yield return E("an address of two sources", $"{Rp(0)}.address", Part("{\"memory\":\"flash\",\"address\":{\"le32\":8,\"byte\":2},\"length\":{\"value\":4}}"));
        yield return E("a negative max", $"{Rp(0)}.length.max", Part("{\"memory\":\"flash\",\"address\":{\"le32\":8},\"length\":{\"byte\":8,\"max\":-1}}"));
        yield return E("an address past the minLength", $"{Rp(0)}.address", Part("{\"memory\":\"flash\",\"address\":{\"le32\":14},\"length\":{\"value\":4}}"));
        yield return E("a bitmap at a negative byte", $"{Rp(0)}.bitmap.byte", Part("{\"bitmap\":{\"byte\":-1,\"size\":4,\"set\":[]}}"));
        yield return E("a bitmap of 0 bytes", $"{Rp(0)}.bitmap.size", Part("{\"bitmap\":{\"byte\":8,\"size\":0,\"set\":[]}}"));
        yield return E("a null bitmap entry", $"{Rp(0)}.bitmap.set[0]", Part("{\"bitmap\":{\"byte\":8,\"size\":4,\"set\":[null]}}"));
        yield return E("a bitmap entry that is not hex", $"{Rp(0)}.bitmap.set[0].hex", Part("{\"bitmap\":{\"byte\":8,\"size\":4,\"set\":[{\"bit\":0,\"at\":0,\"hex\":\"zz\"}]}}"));
        yield return E("a bitmap entry on bit 8", $"{Rp(0)}.bitmap.set[0].bit", Part("{\"bitmap\":{\"byte\":8,\"size\":4,\"set\":[{\"bit\":8,\"at\":0,\"hex\":\"01\"}]}}"));
        yield return E("a bitmap entry past the bitmap", $"{Rp(0)}.bitmap.set[0]", Part("{\"bitmap\":{\"byte\":8,\"size\":4,\"set\":[{\"bit\":1,\"at\":3,\"hex\":\"0101\"}]}}"));
        yield return E("an input part of a layout that does not exist", $"{Rp(0)}.input", Part("{\"input\":\"nosuch\"}"));
        yield return E("input bytes past the layout", $"{Rp(0)}.bytes", Part("{\"input\":\"pro\",\"bytes\":\"60-64\"}"));
        yield return E("a value length read from the command", $"{Rp(0)}.length", Part("{\"value\":\"serial\",\"length\":{\"byte\":8}}"));
        yield return E("a value source that is not one", Rp(0) + ".value", Part("{\"value\":\"mac\",\"length\":{\"value\":6}}"));
        yield return E("a value encoding that is not one", Rp(0) + ".encoding", Part("{\"value\":\"index\",\"encoding\":\"uint64-le\"}"));
        yield return E("an ascii value with no length", Rp(0) + ".length", Part("{\"value\":\"serial\"}"));
        yield return E("a value length that does not match its encoding", Rp(0) + ".length", Part("{\"value\":\"index\",\"encoding\":\"uint16-le\",\"length\":{\"value\":3}}"));
        yield return E("a status part in a rule under the prefix", $"{Rp(0)}.status", Part("{\"status\":true}"));
        yield return E("a prefix part that is null", $"{Cm}.replyPrefix[0]", Set($"{Cm}.replyPrefix[0]", "null"));

        // Memory.
        yield return E("a null memory image", M, Set(M, "null"));
        yield return E("a memory image with no name", $"{M}.name", Set($"{M}.name", "\"\""));
        yield return E("two memory images of one name", "$.protocol.memory[1].name", Append($"{Pr}.memory", "{\"name\":\"flash\",\"size\":16}"));
        yield return E("a memory image of 0 bytes", $"{M}.size", Set($"{M}.size", "0"));
        yield return E("a memory fill that is not one byte", $"{M}.fill", Set($"{M}.fill", "\"FFFF\""));
        yield return E("a null segment", $"{M}.segments[0]", Set($"{M}.segments[0]", "null"));
        yield return E("a segment of hex and value", $"{M}.segments[0]", Set($"{M}.segments[0].value", "\"serial\""));
        yield return E("a length on a hex segment", $"{M}.segments[0]", Set($"{M}.segments[0].length", "2"));
        yield return E("a hex segment that is not hex", $"{M}.segments[0].hex", Set($"{M}.segments[0].hex", "\"0Q\""));
        yield return E("a hex segment past the image", $"{M}.segments[0]", Set($"{M}.segments[0].at", "\"0x1FFFFF\""));
        yield return E("a value segment past the image", $"{M}.segments[1]", Set($"{M}.segments[1].at", "\"0x1FFFFF\""));
        yield return E("two segments that overlap", $"{M}.segments", Set($"{M}.segments[0].at", "\"0x13001\""));

        // Halt, setProtocol, getStatus, getReport, log.
        yield return G("a halt with no register", $"{Pr}.halt.while", Remove($"{Pr}.halt.while"));
        yield return G("a halt status that is not one", $"{Pr}.halt.status", Set($"{Pr}.halt.status", "\"EIO\""));
        yield return G("a halt condition on a register that does not exist", $"{Pr}.halt.while", Set($"{Pr}.halt.while", "[\"nosuch\"]"));
        yield return G("a null term in a while list", $"{Pr}.input.while[1]", Set($"{Pr}.input.while", "[\"polling\",null]"));
        yield return G("a null register list", $"{Pr}.state", Set($"{Pr}.state", "null"));
        yield return new Case("a protocol under usbConfiguration, where the tree before v2.0.0 read it", GC, "$.usbConfiguration.protocol", Set("$.usbConfiguration.protocol", "{}")) { Tool = true };
        yield return new Case("a protocol under UsbConfiguration, in another case", GC, "$.UsbConfiguration.Protocol", root =>
        {
            var cfg = root["usbConfiguration"]!.DeepClone().AsObject();
            cfg["Protocol"] = new System.Text.Json.Nodes.JsonObject();
            root.AsObject().Remove("usbConfiguration");
            root["UsbConfiguration"] = cfg;
        }) { Tool = true };
        yield return G("a setProtocol answer that is not one", $"{Pr}.setProtocol", Set($"{Pr}.setProtocol", "\"nak\""));
        yield return G("a getStatus answer that is not one", $"{Pr}.getStatus", Set($"{Pr}.getStatus", "\"zero\""));
        yield return E("a getReport answer that is not one", $"{Pr}.getReport", Set($"{Pr}.getReport", "\"last\""));
        yield return G("deviceReport with no layouts", $"{Pr}.getReport", Set($"{Pr}.getReport", "\"deviceReport\""));
        yield return E("a log of 100,000 entries", $"{Pr}.log", Set($"{Pr}.log", "100000"));
    }

    static IEnumerable<Case> StreamCases()
    {
        yield return E("a stream and an idle interval", "$.stream", All(Set("$.stream", "{\"periodMs\":8}"), Set("$.extendedReport.idleFrameIntervalMs", "8")));
        yield return E("a stream period of 0.1 ms", "$.stream.periodMs", Set("$.stream", "{\"periodMs\":0.1}"));
        yield return E("a stream period register that does not exist", "$.stream.periodRegister.name", Set("$.stream", "{\"periodMs\":8,\"periodRegister\":{\"name\":\"nosuch\",\"unitMs\":1}}"));
        yield return E("a stream period unit of 0", "$.stream.periodRegister.unitMs", Set("$.stream", "{\"periodMs\":8,\"periodRegister\":{\"name\":\"playerLeds\",\"unitMs\":0}}"));
        yield return E("a stream while on a register that does not exist", "$.stream.while", Set("$.stream", "{\"periodMs\":8,\"while\":\"nosuch\"}"));
        yield return E("stream timestamps that are not one", "$.stream.timestamps", Set("$.stream", "{\"periodMs\":8,\"timestamps\":\"skip\"}"));
    }
}
