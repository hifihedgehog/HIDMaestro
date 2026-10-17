using System;
using System.Collections.Generic;
using System.Linq;

namespace HIDMaestro.Internal.Programs;

/// <summary>The fourth review's adversarial programs (`SP\rev4\form\shared\Rev4Cases.cs`)
/// under round 5's rules, and the path mutations the check plants.</summary>
internal static class Round5Cases
{
    static byte[] Id16() => Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    static readonly byte[] Hat9 = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };

    /// <summary>A template bit of 1 under an OR op: the review's A1 to A3,
    /// which validation now refuses, each with its path.</summary>
    public static readonly (ProgramSpec program, string path)[] RefusedOverTemplate =
    {
        (new("A1-bits-over-template", 4, 8, new[] { 8 }, new Op[] { new ConstOp(0, new byte[] { 0xFF }), new BitsOp(new[] { 0, 0 }) }), "$.ops[1].moves[1]"),
        (new("A2-xor-over-template", 4, 8, new[] { 8 }, new Op[] { new ConstOp(1, new byte[] { 0xAA }), new XorOp(0, 1, 1, 0x00) }), "$.ops[1]"),
        (new("A3-lookup-over-template", 4, 8, new[] { 8 }, new Op[] { new ConstOp(2, new byte[] { 0x0F }), new LookupOp(8, 4, 2, Id16()) }), "$.ops[1]"),
    };

    /// <summary>The same shapes made valid: the template's bits beside the
    /// op's bits, never under them. ORing into the byte and writing over the
    /// op's bits give one result here, which the naive evaluator, writing
    /// over, checks against the fast paths, which OR.</summary>
    public static readonly ProgramSpec[] ValidBesideTemplate =
    {
        new("A1v-bits-beside-template", 4, 8, new[] { 8 }, new Op[] { new ConstOp(0, new byte[] { 0xFE }), new BitsOp(new[] { 0, 0 }) }),
        new("A2v-xor-over-zero-template", 4, 8, new[] { 8 }, new Op[] { new ConstOp(1, new byte[] { 0x00 }), new XorOp(0, 1, 1, 0x00) }),
        new("A3v-lookup-beside-template", 4, 8, new[] { 8 }, new Op[] { new ConstOp(2, new byte[] { 0xF0 }), new LookupOp(8, 4, 2, Id16()) }),
    };

    /// <summary>Centers outside the read's raw range: the review's B1 to B3,
    /// refused now, and the field and hat edges just past the range.</summary>
    public static readonly (ProgramSpec program, string path)[] RefusedCenters =
    {
        (new("B1-field-center-min", 2, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(0, 8), int.MinValue, false, 1, int.MaxValue, 0, -10, 10, false, 0, 1, false) }), "$.ops[0].center"),
        (new("B2-hat-center-min", 2, 8, new[] { 8 }, new Op[] { new Hat8Op(new Src(0, 8), new Src(8, 8), int.MinValue, 0, true, Hat9, 0) }), "$.ops[0].center"),
        (new("B3-hat-center-max", 2, 8, new[] { 8 }, new Op[] { new Hat8Op(new Src(0, 8), new Src(8, 8), int.MaxValue, 65535, false, Hat9, 0) }), "$.ops[0].center"),
        (new("field-center-256-on-8-bits", 2, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(0, 8), 256, false, 1, 1, 0, -1000, 1000, false, 0, 2, false) }), "$.ops[0].center"),
        (new("field-center-minus-1", 2, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(0, 8), -1, false, 1, 1, 0, -1000, 1000, false, 0, 2, false) }), "$.ops[0].center"),
        (new("hat-center-past-the-narrower-read", 2, 8, new[] { 8 }, new Op[] { new Hat8Op(new Src(0, 12), new Src(16, 8), 256, 0, true, Hat9, 0) }), "$.ops[0].center"),
        (new("hat-deadzone-65536", 2, 8, new[] { 8 }, new Op[] { new Hat8Op(new Src(0, 16), new Src(16, 16), 32768, 65536, true, Hat9, 0) }), "$.ops[0].deadzone"),
    };

    /// <summary>Domain edges, valid: the review's C1 to C3, and centers and
    /// dead zones at both ends of their domains.</summary>
    public static readonly ProgramSpec[] Edges =
    {
        new("C1-read25-shift7", 4, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(7, 25), 0, false, 31, 1, 0, -((1 << 30) - 1), (1 << 30) - 1, false, 0, 2, false) }),
        new("C2-decode12-shift7", 2, 8, new[] { 8 }, new Op[] { new MaxOfOp(new[] { new DecodeTerm(new[] { new Src(7, 12) }, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray()) }, 0) }),
        new("C3-read25-negscale", 4, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(7, 25), 1 << 24, true, -31, 1, 1 << 20, -((1 << 30) - 1), (1 << 30) - 1, false, 0, 2, true) }),
        new("field-center-at-top-of-25-bits", 4, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(7, 25), (1 << 25) - 1, false, 31, 1, 0, -((1 << 30) - 1), (1 << 30) - 1, false, 0, 2, false) }),
        new("hat-25-bit-center-at-top", 2, 8, new[] { 8 }, new Op[] { new Hat8Op(new Src(7, 25), new Src(32, 25), (1 << 25) - 1, 65535, false, Hat9, 0) }),
        new("hat-25-bit-center-zero", 2, 8, new[] { 8 }, new Op[] { new Hat8Op(new Src(7, 25), new Src(32, 25), 0, 0, true, Hat9, 0) }),
    };

    /// <summary>Byte 7 is a zero template byte nothing else writes (the
    /// review's U).</summary>
    public static readonly ProgramSpec U = new("U-zero-template-byte", 8, 8, new[] { 8 }, new Op[] { new ConstOp(6, new byte[] { 0x5C, 0x00 }), new CopyOp(0, 0, 4) });

    /// <summary>Path mutations: one path of one program carries a planted
    /// defect, and the check must see it against the other two paths.
    /// Each program is chosen so its mutation changes bytes.</summary>
    public static readonly (string cls, ProgramSpec program, PathMutation gen, InterpMutation interp)[] PathMutations =
    {
        ("the emitter places blocks before the unconditional ops", ReviewCases.All[0], PathMutation.BlocksFirst, InterpMutation.None),
        ("the emitter skips a zero template store", U, PathMutation.ZeroTemplateSkipped, InterpMutation.None),
        ("the emitter drops one masked shift",
            new("M-shift", 4, 8, new[] { 8 }, new Op[] { new BitsOp(new[] { 3, 8, 1, 9, 12, 10 }) }),
            PathMutation.MaskedShiftDropped, InterpMutation.None),
        ("the emitter reads one bit narrower",
            new("M-width", 8, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(3, 12), 0, false, 1, 1, 0, 0, 65535, false, 0, 2, false) }),
            PathMutation.ReadOneBitNarrower, InterpMutation.None),
        ("the interpreter ignores a term's NotEqual",
            new("M-polarity", 8, 8, new[] { 16 }, new Op[] { new WhenOp(new(new[] { new[] { new Term(0, 0x0F0F, 0x0101, true, new uint[] { 0x0101, 0x0F00 }) } }), new Op[] { new CopyOp(2, 4, 2) }) }),
            PathMutation.None, InterpMutation.NotEqualIgnored),
        ("the interpreter adds one to a field's offset",
            new("M-field", 8, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(0, 8), 0, false, 1, 1, 0, -1000, 1000, false, 0, 2, false) }),
            PathMutation.None, InterpMutation.FieldOffsetPlusOne),
    };
}
