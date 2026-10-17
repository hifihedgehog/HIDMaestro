using System;
using System.Collections.Generic;
using System.Linq;

namespace HIDMaestro.Internal.Programs;

/// <summary>The programs program_equivalence_check's one-semantics check and
/// hat8 diagonal check run. They sit beside the corpus so the build tool
/// emits their kernels from the same source.</summary>
internal static class SemanticsCases
{
    /// <summary>One program per op kind, and per kind a conditional block
    /// holds, each small enough to run three ways.</summary>
    public static IEnumerable<(string name, Op op, ProgramSpec program)> Samples()
    {
        int[] r8 = { 8 };
        var always = new Condition(new[] { new[] { new Term(0, 0xFF, 0x01, true) } });
        ProgramSpec P(string name, params Op[] ops) => new(name, 8, 8, r8, ops);
        var hat = new byte[] { 10, 11, 12, 13, 14, 15, 16, 17, 18 };
        yield return ("copy", new CopyOp(0, 1, 2), P("one-copy", new CopyOp(0, 1, 2)));
        yield return ("const", new ConstOp(0, new byte[] { 0x5A }), P("one-const", new ConstOp(0, new byte[] { 0x5A, 0x00, 0xC3 })));
        yield return ("bits", new BitsOp(new[] { 3, 8 }), P("one-bits", new ConstOp(1, new byte[] { 0x02 }), new BitsOp(new[] { 3, 8, 12, 15 })));
        yield return ("xor", new XorOp(0, 0, 1, 0), P("one-xor", new XorOp(1, 2, 2, 0x81)));
        yield return ("swap16", new Swap16Op(0, 0, 1), P("one-swap16", new Swap16Op(2, 0, 2)));
        yield return ("lookup", new LookupOp(0, 1, 0, new byte[2]), P("one-lookup", new LookupOp(4, 3, 3, new byte[] { 1, 2, 4, 8, 16, 32, 64, 128 })));
        yield return ("field", new FieldOp(new Src(0, 8), 0, false, 1, 1, 0, 0, 255, false, 0, 1, false),
            P("one-field", new FieldOp(new Src(3, 12), 2048, true, 3, 2, 7, -3000, 3000, false, 1, 2, true)));
        yield return ("pack12", new Pack12Op(0, 2, 0), P("one-pack12", new Pack12Op(0, 2, 4)));
        yield return ("counter", new CounterOp(0, 0, 1, 1, 0), P("one-counter", new CounterOp(1, 2, 3, 77, 9, true)));
        yield return ("hat8", new Hat8Op(new Src(0, 8), new Src(8, 8), 128, 10, true, hat, 0),
            P("one-hat8", new Hat8Op(new Src(0, 8), new Src(8, 8), 128, 10, true, hat, 6)));
        yield return ("maxOf", new MaxOfOp(Array.Empty<DecodeTerm>(), 0),
            P("one-maxOf", new MaxOfOp(new[] { new DecodeTerm(new[] { new Src(0, 4), new Src(12, 3) }, Enumerable.Range(0, 128).Select(i => (byte)(i * 5)).ToArray()),
                                              new DecodeTerm(new[] { new Src(20, 6) }, Enumerable.Range(0, 64).Select(i => (byte)(255 - i)).ToArray()) }, 7)));
        yield return ("when", new WhenOp(always, Array.Empty<Op>()), P("one-when", new WhenOp(always, new Op[] { new ConstOp(5, new byte[] { 0x77 }) })));
        yield return ("when-copy", new CopyOp(0, 0, 1), P("block-copy", new CopyOp(0, 0, 2), new WhenOp(always, new Op[] { new CopyOp(4, 1, 2) })));
        yield return ("when-counter", new CounterOp(0, 0, 1, 1, 0), P("block-counter", new WhenOp(always, new Op[] { new CounterOp(2, 4, 4, 3, 1, false), new CounterOp(3, 0, 2, 5, 0, true) })));
    }

    /// <summary>hat8 over two 16-bit reads centered at 32768 with a dead
    /// zone of 3,000 counts, Y up, each sector's entry its own number, for the
    /// sector edges.</summary>
    public static readonly ProgramSpec HatEdges = new("hat16-edges", 1, 8, new[] { 8 },
        new Op[] { new Hat8Op(new Src(0, 16), new Src(16, 16), 32768, 3000, true, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, 0) });

    /// <summary>The hat table of <see cref="HatDiagonal"/>.</summary>
    public static readonly byte[] HatTable = { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18 };

    /// <summary>hat8 over two 16-bit reads centered at 0, with a dead zone of
    /// 1,000 counts, written at report byte 1.</summary>
    public static readonly ProgramSpec HatDiagonal = new("hat16-diagonal", 2, 8, new[] { 8 },
        new Op[] { new ConstOp(0, new byte[] { 0xEE }), new Hat8Op(new Src(0, 16), new Src(16, 16), 0, 1000, true, HatTable, 1) });
}
