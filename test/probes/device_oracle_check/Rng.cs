// SplitMix64 (Steele, Lea and Flood, "Fast splittable pseudorandom number
// generators", OOPSLA 2014), the generator the native harness runs too, so
// one seed means the same sequence in C# and in C and on any runtime.
// System.Random's sequence is not promised across .NET versions.
using System;

internal sealed class Rng
{
    ulong _state;

    /// <summary>A generator for one case: the run's seed mixed with the
    /// case's own constant, so adding a case changes no other.</summary>
    public Rng(ulong seed, ulong stream) { _state = seed ^ (stream * 0x9E3779B97F4A7C15UL); }

    public ulong NextU64()
    {
        ulong z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    public uint NextU32() => (uint)(NextU64() >> 32);

    /// <summary>0 to <paramref name="max"/> - 1, by the high bits, which a
    /// modulo of a power of two would not use.</summary>
    public int Next(int max) => max <= 0 ? 0 : (int)((NextU64() >> 33) % (ulong)max);

    public bool Bool() => (NextU64() >> 63) != 0;

    public byte Byte() => (byte)(NextU64() >> 56);

    /// <summary>A double in [0, 1) with 53 random bits.</summary>
    public double Double() => (NextU64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>A float in [0, 1) with 24 random bits.</summary>
    public float Float01() => (NextU32() >> 8) * (1.0f / (1 << 24));

    public void Fill(Span<byte> dst)
    {
        for (int i = 0; i < dst.Length; i++) dst[i] = Byte();
    }
}
