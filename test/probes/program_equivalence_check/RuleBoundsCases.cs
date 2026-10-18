using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using HIDMaestro.Internal.Programs;

internal static partial class RuleChecks
{
    const string D0 = "$.protocol.decoders.rumble.fields[0].parts[0]";

    /// <summary>A case the build tool's parse must refuse at the same path.</summary>
    static Case Bound(string rule, string expect, Action<JsonNode> edit) => E(rule, expect, edit) with { Tool = true };

    /// <summary>Offsets and lengths near int.MaxValue, one per op and read
    /// kind. A bound checked in int wraps past 2^31 and passes: each of these
    /// must be refused with its path, at load and in the build tool, and never
    /// reach a path that writes or reads at the offset.</summary>
    static IEnumerable<Case> BoundsCases()
    {
        const string Max = "2147483647";
        yield return Bound("a copy to byte 2^31-1", $"{P0}[2]", Set($"{P0}[2].to", Max));
        yield return Bound("a copy of bytes 1 to 2^31-1", $"{P0}[2].copy", Set($"{P0}[2].copy", $"\"1-{Max}\""));
        yield return Bound("a masked copy of bytes 0 to 2^31-1", $"{P0}[15].copy", Op($"{{\"copy\":\"0-{Max}\",\"to\":50,\"mask\":\"0F\"}}"));
        yield return Bound("a masked copy to byte 2^31-1", $"{P0}[15].to", Op($"{{\"copy\":\"22\",\"to\":{Max},\"mask\":\"0F\"}}"));
        yield return Bound("a field read at bit 2^31-1", $"{P0}[6].field.bit", Set($"{P0}[6].field", $"{{\"bit\":{Max},\"bits\":1}}"));
        yield return Bound("a field read at byte 2^29+1, whose bit offset wraps to 8", $"{P0}[6].field.byte", Set($"{P0}[6].field", "{\"byte\":536870913}"));
        yield return Bound("a field read at a negative byte", $"{P0}[6].field.byte", Set($"{P0}[6].field", "{\"byte\":-1}"));
        yield return Bound("a hat read at word 2^29, whose bit offset wraps to 0", $"{P0}[8].hat8[0].word", Set($"{P0}[8].hat8[0]", "{\"word\":536870912}"));
        yield return Bound("a hat to byte 2^31-1", $"{P0}[8]", Set($"{P0}[8].to", Max));
        yield return Bound("a pack12 read at byte 2^31-2", $"{P0}[9].pack12[0]", Set($"{P0}[9].pack12", "[2147483646,0]"));
        yield return Bound("a counter at byte 2^31-2", $"{P0}[1]", Set($"{P0}[1].at", "2147483646"));
        yield return Bound("a lookup at bit 0x7FFFFFFC", $"{P0}[7].lookup.bit", Set($"{P0}[7].lookup", "{\"bit\":2147483644,\"bits\":2}"));
        yield return Bound("an xor of byte 2^31-1", $"{P0}[4].xor", Set($"{P0}[4].xor", $"\"{Max}\""));
        yield return Bound("a swap16 of 2^30-1 words", $"{P0}[5].swap16", Set($"{P0}[5].swap16", "\"0-2147483645\""));
        yield return Bound("a bit move to report bit 2^31-1", $"{P0}[3].bits[0][1]", Set($"{P0}[3].bits[0]", $"[16,{Max}]"));
        yield return Bound("a const at byte 2^31-1", $"{P0}[0].const['{Max}']", Set($"{P0}[0].const[\"{Max}\"]", "\"01\""));
        yield return Bound("a block const at byte 2^31-1", $"{P0}[13].ops[0].const['{Max}']", Set($"{P0}[13].ops[0].const", $"{{\"{Max}\":\"38\"}}"));
        yield return Bound("a destination past int", $"{P0}[2].to", Set($"{P0}[2].to", "2147483648"));
        yield return Bound("a decoder piece at bit 2^31-1", $"{D0}.pieces[0].at", Set($"{D0}.pieces[0].at", Max));
        yield return Bound("decoder pieces whose bit count wraps", $"{D0}.pieces[0].count", Set($"{D0}.pieces", $"[{{\"at\":46,\"count\":{Max}}},{{\"at\":46,\"count\":2}}]"));
        yield return Bound("a decoder read over 2^31-1 frames", $"{D0}.frames", Set($"{D0}.frames", $"[{Max},5]"));
        yield return Bound("a decoder frame stride whose bit offset wraps", $"{D0}.frames", Set($"{D0}.frames", "[2,268435456]"));
        yield return Bound("a decoder read over several frames of stride 0", $"{D0}.frames", Set($"{D0}.frames", "[3,0]"));
        // The state block's codec fields, which the codec's check refuses at
        // create. The build tool does not run that check, so these are not
        // held to the tool.
        yield return E("a state block field at byte 2^31-1", "$.stateBlock.fields[3]", Set("$.stateBlock.fields[3]", $"{{\"byte\":{Max},\"type\":\"int16-le\"}}"));
        yield return E("a state block CRC at byte 2^31-1", "$.stateBlock.fields[3]", Set("$.stateBlock.fields[3]", $"{{\"byte\":{Max},\"type\":\"crc32-le\",\"scope\":{{\"from\":0,\"to\":1}}}}"));
        yield return E("an output packed part at bit 2^31-1", "$.extendedOutputReport.fields[0].parts[0]", Set("$.extendedOutputReport", "{\"reportId\":\"0x03\",\"size\":33,\"fields\":[{\"type\":\"packed-uint\",\"semantic\":\"leftMotor\",\"parts\":[{\"pieces\":[{\"at\":2147483647,\"count\":8}]}]}]}"));
        yield return E("an output packed part whose frames run past 2^31 bits", "$.extendedOutputReport.fields[0].parts[0]", Set("$.extendedOutputReport", "{\"reportId\":\"0x03\",\"size\":33,\"fields\":[{\"type\":\"packed-uint\",\"semantic\":\"leftMotor\",\"parts\":[{\"pieces\":[{\"at\":0,\"count\":8}],\"frames\":[3,268435455]}]}]}"));
        yield return E("an output packed part whose stride in bits passes int", "$.extendedOutputReport.fields[0].parts[0]", Set("$.extendedOutputReport", "{\"reportId\":\"0x03\",\"size\":33,\"fields\":[{\"type\":\"packed-uint\",\"semantic\":\"leftMotor\",\"parts\":[{\"pieces\":[{\"at\":0,\"count\":8}],\"frames\":[2,536870912]}]}]}"));
        yield return E("a state block field repeated over a stride of 2^31-1", "$.stateBlock.fields[3].repeat",
            Set("$.stateBlock.fields[3]", $"{{\"byte\":4,\"type\":\"int16-le\",\"semantic\":\"accelGX\",\"scale\":1,\"repeat\":[3,{Max}]}}"));
    }

    /// <summary>The same bounds on programs built in code, where no parse
    /// stands in front of the form.</summary>
    static IEnumerable<(string rule, ProgramSpec program, string path)> BoundsNegatives()
    {
        int[] r8 = { 8 };
        var always = new Condition(new[] { new[] { new Term(0, 0xFF, 0, true) } });
        ProgramSpec P(params Op[] ops) => new("bounds-negative", 8, 8, r8, ops);
        const int Max = int.MaxValue;
        yield return ("a copy to byte 2^31-1", P(new CopyOp(0, Max, 1)), "$.ops[0]");
        yield return ("a copy from byte 2^31-1", P(new CopyOp(Max, 0, 1)), "$.ops[0].src");
        yield return ("a copy of 2^31-1 bytes", P(new CopyOp(1, 0, Max)), "$.ops[0].src");
        yield return ("a const at byte 2^31-1", P(new ConstOp(Max, new byte[] { 1 })), "$.ops[0]");
        yield return ("a block const at byte 2^31-1", P(new WhenOp(always, new Op[] { new ConstOp(Max, new byte[] { 1 }) })), "$.ops[0].ops[0]");
        yield return ("a block copy to byte 2^31-1", P(new WhenOp(always, new Op[] { new CopyOp(0, Max, 1) })), "$.ops[0].ops[0]");
        yield return ("a block counter at byte 2^31-2", P(new WhenOp(always, new Op[] { new CounterOp(0, Max - 1, 4, 1, 0) })), "$.ops[0].ops[0]");
        yield return ("a counter at byte 2^31-2", P(new CounterOp(0, Max - 1, 4, 1, 0)), "$.ops[0]");
        yield return ("a field read at bit 2^31-1", P(new FieldOp(new Src(Max, 1), 0, false, 1, 1, 0, 0, 1, false, 0, 1, false)), "$.ops[0].from.bit");
        yield return ("a big-endian word at bit 2^31-8", P(new FieldOp(new Src(Max - 7, 16, true), 0, false, 1, 1, 0, 0, 255, false, 0, 1, false)), "$.ops[0].from");
        yield return ("a field to byte 2^31-1", P(new FieldOp(new Src(0, 8), 0, false, 1, 1, 0, 0, 255, false, Max, 2, false)), "$.ops[0]");
        yield return ("a lookup at bit 2^31-4", P(new LookupOp(Max - 3, 2, 0, new byte[4])), "$.ops[0].bit");
        yield return ("a lookup to byte 2^31-1", P(new LookupOp(0, 2, Max, new byte[] { 0, 1, 2, 3 })), "$.ops[0]");
        yield return ("an xor of 2^31-1 bytes", P(new XorOp(1, 0, Max, 0xFF)), "$.ops[0].src");
        yield return ("a swap16 of 2^30-1 words", P(new Swap16Op(2, 0, (Max - 1) / 2)), "$.ops[0].src");
        yield return ("a swap16 of 2^31-1 words", P(new Swap16Op(0, 0, Max)), "$.ops[0].src");
        yield return ("a pack12 to byte 2^31-2", P(new Pack12Op(0, 2, Max - 1)), "$.ops[0]");
        yield return ("a hat to byte 2^31-1", P(new Hat8Op(new Src(0, 8), new Src(8, 8), 128, 0, false, new byte[9], Max)), "$.ops[0]");
        yield return ("a decode part at bit 2^31-1", P(new MaxOfOp(new[] { new DecodeTerm(new[] { new Src(Max, 1) }, new byte[2]) }, 0)), "$.ops[0].terms[0].parts[0].bit");
        yield return ("a decode to byte 2^31-1", P(new MaxOfOp(new[] { new DecodeTerm(new[] { new Src(0, 1) }, new byte[2]) }, Max)), "$.ops[0]");
    }
}
