using System;
using System.Collections.Generic;
using System.Linq;

namespace HIDMaestro.Internal.Programs;

/// <summary>The third design review's path check, P1 to P5, restated as
/// programs for the validated form (`SP\rev3\pathcheck\Cases.cs`). P1 to P3
/// are valid under the form's write order. P4 and P5 must be refused.</summary>
internal static class ReviewCases
{
    static readonly Condition RegNonZero = new(new[] { new[] { new Term(0, 0xFF, 0, true) } });

    public static readonly ProgramSpec[] All =
    {
        // P1: a conditional const, then an unconditional copy over the same byte.
        new("P1", 8, 8, new[] { 8 }, new Op[] { new WhenOp(RegNonZero, new Op[] { new ConstOp(5, new byte[] { 0xAA }) }), new CopyOp(0, 5, 1) }),
        // P2: inside one conditional block, a copy then a const on one byte.
        new("P2", 8, 8, new[] { 8 }, new Op[] { new WhenOp(RegNonZero, new Op[] { new CopyOp(0, 5, 1), new ConstOp(5, new byte[] { 0xAA }) }) }),
        // P3: the Switch 2 Pro 0x09 shape: a template const and a conditional const on byte 12.
        new("P3", 16, 16, new[] { 8 }, new Op[] { new ConstOp(12, new byte[] { 0x30 }), new WhenOp(new(new[] { new[] { new Term(0, 0x20, 0, true) } }), new Op[] { new ConstOp(12, new byte[] { 0x38 }) }) }),
        // P4: a 4-bit lookup whose bits span two state bytes (bits 6 to 9).
        new("P4", 4, 8, new[] { 8 }, new Op[] { new LookupOp(6, 4, 0, Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray()) }),
        // P5: a field read of 28 bits at bit offset 6, which a 32-bit load cannot hold.
        new("P5", 8, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(6, 28), 0, false, 1, 1 << 20, 0, -(1 << 29), (1 << 29) - 1, false, 0, 2, false) }),
    };

    /// <summary>One original per mutation class, each built so the class
    /// changes its bytes. The check runs the generated original against the
    /// interpreted mutant and must see a difference.</summary>
    public static readonly (string cls, ProgramSpec original, ProgramSpec mutant)[] Mutations =
    {
        ("a field offset moved by one",
            new("M-field", 8, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(0, 8), 0, false, 1, 1, 0, -1000, 1000, false, 0, 2, false) }),
            new("M-field", 8, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(0, 8), 0, false, 1, 1, 1, -1000, 1000, false, 0, 2, false) })),
        ("two blocks in the other order",
            new("M-order", 8, 8, new[] { 8 }, new Op[] {
                new WhenOp(new(new[] { new[] { new Term(0, 0x01, 1, false) } }), new Op[] { new ConstOp(3, new byte[] { 0x11 }) }),
                new WhenOp(new(new[] { new[] { new Term(0, 0x02, 2, false) } }), new Op[] { new ConstOp(3, new byte[] { 0x22 }) }) }),
            new("M-order", 8, 8, new[] { 8 }, new Op[] {
                new WhenOp(new(new[] { new[] { new Term(0, 0x02, 2, false) } }), new Op[] { new ConstOp(3, new byte[] { 0x22 }) }),
                new WhenOp(new(new[] { new[] { new Term(0, 0x01, 1, false) } }), new Op[] { new ConstOp(3, new byte[] { 0x11 }) }) })),
        ("a template store dropped",
            new("M-template", 8, 8, new[] { 8 }, new Op[] { new ConstOp(6, new byte[] { 0x5C }), new CopyOp(0, 0, 4) }),
            new("M-template", 8, 8, new[] { 8 }, new Op[] { new CopyOp(0, 0, 4) })),
        ("a read width changed",
            new("M-width", 8, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(3, 12), 0, false, 1, 1, 0, 0, 65535, false, 0, 2, false) }),
            new("M-width", 8, 8, new[] { 8 }, new Op[] { new FieldOp(new Src(3, 11), 0, false, 1, 1, 0, 0, 65535, false, 0, 2, false) })),
        ("a condition's polarity flipped",
            new("M-polarity", 8, 8, new[] { 16 }, new Op[] { new WhenOp(new(new[] { new[] { new Term(0, 0x0F0F, 0x0101, false, new uint[] { 0x0101, 0x0F00 }) } }), new Op[] { new CopyOp(2, 4, 2) }) }),
            new("M-polarity", 8, 8, new[] { 16 }, new Op[] { new WhenOp(new(new[] { new[] { new Term(0, 0x0F0F, 0x0101, true, new uint[] { 0x0101, 0x0F00 }) } }), new Op[] { new CopyOp(2, 4, 2) }) })),
    };
}
