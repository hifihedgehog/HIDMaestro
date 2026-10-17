// The intermediate form of a report program (design note, mechanism 2).
// This folder is compiled twice from the same files: into the SDK, which
// interprets a program at run time, and into the build tool
// (sdk/HIDMaestro.KernelGen), which emits generated kernels for the
// catalog's programs. Neither path takes a program until ProgramForm.Validate
// has turned it into a ValidProgram, so these records are inputs, never run
// directly. ModelCompiler lowers a profile's JSON into them.
using System;

namespace HIDMaestro.Internal.Programs;

/// <summary>A value read from the state block: <see cref="Bits"/> bits at a
/// bit offset, bit i of the value being state bit offset + i, or a 16-bit
/// word whose first byte is its high byte.</summary>
internal readonly record struct Src(int BitOffset, int Bits, bool BigEndianWord = false);

internal abstract record Op;

/// <summary>Whole bytes copied from the state block.</summary>
internal sealed record CopyOp(int Src, int Dst, int Len) : Op;

/// <summary>At the top level, template bytes under every other write. Inside
/// a conditional block, a write like any other.</summary>
internal sealed record ConstOp(int At, byte[] Bytes) : Op;

/// <summary>Bit moves: pairs of (state bit, report bit). An OR op: each report
/// bit it names is ORed into its byte.</summary>
internal sealed record BitsOp(int[] Moves) : Op;

/// <summary>dst[i] = src[i] ^ mask over a range. An OR op over all eight bits
/// of each byte.</summary>
internal sealed record XorOp(int Src, int Dst, int Len, byte Mask) : Op;

/// <summary>16-bit words with their two bytes exchanged.</summary>
internal sealed record Swap16Op(int Src, int Dst, int Words) : Op;

/// <summary>An N-bit group inside one state byte through a 2^N-byte table. An
/// OR op over every bit some table entry sets.</summary>
internal sealed record LookupOp(int SrcBit, int Bits, int Dst, byte[] Table) : Op;

/// <summary>v = read(src) - center, negated if asked, times num/den with C
/// truncation, plus offset, then clamped to [min, max] or wrapped to 16 bits,
/// written as 1 or 2 bytes in either order.</summary>
internal sealed record FieldOp(Src From, int Center, bool Negate, int Num, int Den, int Offset,
                               int Min, int Max, bool Wrap16, int Dst, int OutBytes, bool OutBigEndian) : Op;

/// <summary>Two 16-bit little-endian state values. The low 12 bits of each
/// form a 24-bit little-endian value, the first value lowest.</summary>
internal sealed record Pack12Op(int SrcX, int SrcY, int Dst) : Op;

/// <summary>A counter slot written as <see cref="Width"/> bytes: value times
/// scale plus add, modulo 2^32, low byte first unless
/// <see cref="BigEndian"/>.</summary>
internal sealed record CounterOp(int Slot, int Dst, int Width, uint Scale, uint Add, bool BigEndian = false) : Op;

/// <summary>A stick as an eight-way hat: two values around a center, a dead
/// zone radius in counts, eight sectors from up clockwise, and a 9-byte table
/// (8 sectors, then the center). The design note states the sector boundary.</summary>
internal sealed record Hat8Op(Src X, Src Y, int Center, int Deadzone, bool YUp, byte[] Table9, int Dst) : Op;

/// <summary>A decoded value: each term concatenates its bit parts, the first
/// part lowest, into an index and maps it through a byte table of 2^bits
/// entries. The largest term's value is written as one byte.</summary>
internal sealed record MaxOfOp(DecodeTerm[] Terms, int Dst) : Op;

internal sealed record DecodeTerm(Src[] Parts, byte[] Table);

/// <summary>A conditional block: constant bytes, copies and counters that run
/// after every unconditional op, in list order, while the condition holds.</summary>
internal sealed record WhenOp(Condition Cond, Op[] Ops) : Op;

/// <summary>One term: (register AND mask) equals value, or with
/// <see cref="In"/> set, is one of its values. <see cref="NotEqual"/>
/// negates either.</summary>
internal readonly record struct Term(int Register, uint Mask, uint Value, bool NotEqual, uint[]? In = null);

/// <summary>Disjunctive form: the condition holds when any group's terms all
/// hold.</summary>
internal sealed record Condition(Term[][] Groups)
{
    /// <summary>The condition evaluated on <paramref name="registers"/>.
    /// The protocol engine selects layouts with this, and a register a term
    /// names past the array's end reads as 0.</summary>
    public bool Holds(ReadOnlySpan<uint> registers)
    {
        for (int g = 0; g < Groups.Length; g++)
        {
            var group = Groups[g];
            bool all = true;
            for (int t = 0; t < group.Length && all; t++)
            {
                ref readonly var term = ref group[t];
                uint masked = (term.Register < registers.Length ? registers[term.Register] : 0) & term.Mask;
                bool hit;
                if (term.In == null) hit = masked == term.Value;
                else
                {
                    hit = false;
                    foreach (uint v in term.In) if (masked == v) { hit = true; break; }
                }
                if (hit == term.NotEqual) all = false;
            }
            if (all) return true;
        }
        return false;
    }
}

/// <summary>A report program: the report it builds, the state block it reads,
/// the width of each register its conditions test, and its ops. A
/// <see cref="Gamepad"/> program is a state program: it reads HMGamepadState
/// and the six slots, not a state block, and builds a state block of
/// <see cref="Size"/> bytes.</summary>
internal sealed record ProgramSpec(string Name, int Size, int StateSize, int[] RegisterBits, Op[] Ops, bool Gamepad = false);
