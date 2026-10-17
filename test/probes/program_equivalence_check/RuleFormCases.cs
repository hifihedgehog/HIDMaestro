using System;
using System.Collections.Generic;
using HIDMaestro.Internal.Programs;

/// <summary>An op kind the form does not define, to show validation refuses
/// it rather than passing it to a path that has no stage for it.</summary>
internal sealed record UnknownOp : Op;

internal static partial class RuleChecks
{
    /// <summary>The form's rules that a profile cannot reach, because the
    /// profile's parse refuses the same value first (a field's zero
    /// denominator, an empty block) or cannot write it (a big-endian word
    /// off a byte, a counter slot by number, an empty condition group). Each
    /// program is built in code and breaks one rule.</summary>
    static IEnumerable<(string rule, ProgramSpec program, string path)> FormNegatives()
    {
        int[] r8 = { 8 };
        var always = new Condition(new[] { new[] { new Term(0, 0xFF, 0, true) } });
        ProgramSpec P(int size, int state, int[] regs, params Op[] ops) => new("form-negative", size, state, regs, ops);
        FieldOp Field(Src from, int den = 1) => new(from, 0, false, 1, den, 0, 0, 255, false, 0, 1, false);
        yield return ("a report of 65 bytes", P(65, 8, r8, new CopyOp(0, 0, 1)), "$.size");
        yield return ("a state block of 257 bytes", P(8, 257, r8, new CopyOp(0, 0, 1)), "$.stateSize");
        yield return ("33 registers", P(8, 8, new int[33], new CopyOp(0, 0, 1)), "$.registers");
        yield return ("a register of 0 bits", P(8, 8, new[] { 0 }, new CopyOp(0, 0, 1)), "$.registers[0]");
        yield return ("a big-endian word of 12 bits", P(8, 8, r8, Field(new Src(0, 12, true))), "$.ops[0].from.bits");
        yield return ("a big-endian word off a byte", P(8, 8, r8, Field(new Src(4, 16, true))), "$.ops[0].from.bit");
        yield return ("a counter in slot 8", P(8, 8, r8, new CounterOp(8, 0, 1, 1, 0)), "$.ops[0].slot");
        yield return ("a block with no ops", P(8, 8, r8, new WhenOp(always, Array.Empty<Op>())), "$.ops[0].ops");
        yield return ("a swap16 of no words", P(8, 8, r8, new Swap16Op(0, 0, 0)), "$.ops[0].words");
        yield return ("a field divided by zero", P(8, 8, r8, Field(new Src(0, 8), den: 0)), "$.ops[0].den");
        yield return ("a decode of no terms", P(2, 8, r8, new MaxOfOp(Array.Empty<DecodeTerm>(), 0)), "$.ops[0].terms");
        yield return ("a decode term of no parts", P(2, 8, r8, new MaxOfOp(new[] { new DecodeTerm(Array.Empty<Src>(), new byte[1]) }, 0)), "$.ops[0].terms[0].parts");
        yield return ("a decode part that is a big-endian word", P(2, 8, r8, new MaxOfOp(new[] { new DecodeTerm(new[] { new Src(0, 16, true) }, new byte[1 << 16]) }, 0)), "$.ops[0].terms[0].parts[0]");
        yield return ("an op the form does not define", P(8, 8, r8, new UnknownOp()), "$.ops[0]");
        yield return ("a condition of no groups", P(8, 8, r8, new WhenOp(new Condition(Array.Empty<Term[]>()), new Op[] { new ConstOp(0, new byte[] { 1 }) })), "$.ops[0].when");
        yield return ("a condition group of no terms", P(8, 8, r8, new WhenOp(new Condition(new[] { Array.Empty<Term>() }), new Op[] { new ConstOp(0, new byte[] { 1 }) })), "$.ops[0].when[0]");
        yield return ("an in term of no values", P(8, 8, r8, new WhenOp(new Condition(new[] { new[] { new Term(0, 0xFF, 0, false, Array.Empty<uint>()) } }), new Op[] { new ConstOp(0, new byte[] { 1 }) })), "$.ops[0].when[0][0].in");
    }
}
