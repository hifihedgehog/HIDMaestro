// The golden format of device_oracle_check.
//
// A case is a list of streams. A stream is the ordered outputs one subject
// gave for one input sequence: a frame, a reply, a register, a log entry.
// Each output is a tag naming its kind and its bytes. The golden file keeps,
// per stream, the number of outputs, one SHA-256 over all of them and the
// full bytes of the first FirstKept, so a replay that differs inside them
// shows where, and the repository holds kilobytes for millions of outputs.
//
// The SHA-256 of a stream runs over each output as its tag in ASCII, one
// zero byte, its length as a 32-bit little-endian count, then its bytes.
//
// Files: <case>.txt.gz, the text below compressed, and SHA256SUMS, which
// holds the SHA-256 of each file's text as decompressed. Hashing the text
// rather than the compressed bytes keeps the manifest valid whatever
// compression a runtime chooses.
//
//   device-oracle golden 1
//   case <name>
//   seed <seed>
//   stream <name>
//   count <outputs>
//   sha256 <hex>
//   <tag> <hex, or - for no bytes>      up to FirstKept lines
//   end
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

internal sealed class GoldenStream
{
    public const int FirstKept = 100;

    public readonly string Name;
    public long Count { get; private set; }
    public readonly List<(string Tag, byte[] Data)> First = new();
    readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    string? _digest;
    string? _lastTag;
    readonly byte[] _head = new byte[64 + 5];

    public GoldenStream(string name)
    {
        if (name.Length == 0 || name.Any(char.IsWhiteSpace))
            throw new ArgumentException($"A stream name has no spaces: '{name}'.");
        Name = name;
    }

    /// <summary>A stream read back from a golden file.</summary>
    public GoldenStream(string name, long count, string digest, List<(string, byte[])> first)
    {
        Name = name;
        Count = count;
        _digest = digest;
        First = first;
    }

    public void Add(string tag, ReadOnlySpan<byte> data)
    {
        if (_digest != null) throw new InvalidOperationException($"Stream {Name} is closed.");
        if (!ReferenceEquals(tag, _lastTag))
        {
            if (tag.Length == 0 || tag.Length > 64 || tag.Any(c => c <= ' ' || c > '~'))
                throw new ArgumentException($"A tag is 1 to 64 printable ASCII characters without spaces: '{tag}'.");
            _lastTag = tag;
        }
        int n = Encoding.ASCII.GetBytes(tag, _head);
        _head[n] = 0;
        BitConverter.TryWriteBytes(_head.AsSpan(n + 1, 4), (uint)data.Length);
        _sha.AppendData(_head, 0, n + 5);
        _sha.AppendData(data);
        if (Count < FirstKept) First.Add((tag, data.ToArray()));
        Count++;
    }

    public string Digest => _digest ??= Convert.ToHexString(_sha.GetHashAndReset());
}

// Partial: the probe's GoldenExplain.cs adds --explain, which reads the
// SDK's internals. The test app links this file without it, and the
// explain hooks below then compile to nothing.
internal sealed partial class GoldenCase
{
    public readonly string Name;
    public readonly ulong Seed;
    public readonly List<GoldenStream> Streams = new();
    readonly HashSet<string> _names = new(StringComparer.Ordinal);
    GoldenStream? _current;

    /// <summary>When set, every case run also writes each output it gives,
    /// whole, to &lt;dir&gt;\&lt;case&gt;.bin in the native harness's record
    /// format (NativeStreamReader), so a lane can hold a changed tree's
    /// outputs against these one by one (Explain.cs).</summary>
    public static string? DumpDir { get; set; }
    BinaryWriter? _dump;

    // --explain's hooks (GoldenExplain.cs).
    partial void ExplainStart();
    partial void ExplainStream(string name);
    partial void ExplainOut(string tag, ReadOnlySpan<byte> data);
    partial void ExplainNote(string tag, ReadOnlySpan<byte> data);
    partial void ExplainEnd();

    /// <summary>A case's recording. Only the run of a case passes
    /// <paramref name="dump"/>, so a recording a case keeps for its own
    /// comparison, or one read back from its file, writes no dump and
    /// holds nothing against the base.</summary>
    public GoldenCase(string name, ulong seed, bool dump = false)
    {
        Name = name;
        Seed = seed;
        if (dump) ExplainStart();
        if (dump && DumpDir != null)
        {
            Directory.CreateDirectory(DumpDir);
            _dump = new BinaryWriter(new BufferedStream(File.Create(Path.Combine(DumpDir, name + ".bin")), 1 << 20), Encoding.ASCII);
        }
    }

    /// <summary>Ends the dump file with its end record.</summary>
    public void CloseDump()
    {
        ExplainEnd();
        if (_dump == null) return;
        _dump.Write((byte)'E');
        _dump.Dispose();
        _dump = null;
    }

    /// <summary>Start a stream. Outputs go to it until the next one starts.</summary>
    public GoldenStream Stream(string name)
    {
        if (!_names.Add(name)) throw new ArgumentException($"Case {Name} has stream {name} twice.");
        _current = new GoldenStream(name);
        Streams.Add(_current);
        ExplainStream(name);
        if (_dump != null)
        {
            var n = Encoding.ASCII.GetBytes(name);
            _dump.Write((byte)'S');
            _dump.Write((ushort)n.Length);
            _dump.Write(n);
        }
        return _current;
    }

    /// <summary>Applied to every output before it is hashed or kept. A
    /// case whose inputs must not be copied into its golden file replaces
    /// those bytes here with their digest.</summary>
    public Func<byte[], byte[]>? Redact { get; set; }

    public void Out(string tag, ReadOnlySpan<byte> data)
    {
        if (_current == null) throw new InvalidOperationException($"Case {Name}: an output before any stream.");
        if (Redact != null) data = Redact(data.ToArray());
        _current.Add(tag, data);
        ExplainOut(tag, data);
        if (_dump != null)
        {
            _dump.Write((byte)'O');
            _dump.Write((byte)tag.Length);
            _dump.Write(Encoding.ASCII.GetBytes(tag));
            _dump.Write((uint)data.Length);
            _dump.Write(data);
        }
    }

    /// <summary>Data that is not an output: written to the dump as an 'N'
    /// record and held against the base dump's for the stream's explainer,
    /// and never part of the golden file. Case 5 notes its plan's text, of
    /// which the golden file keeps only the digest.</summary>
    public void Note(string tag, ReadOnlySpan<byte> data)
    {
        if (_current == null) throw new InvalidOperationException($"Case {Name}: a note before any stream.");
        ExplainNote(tag, data);
        if (_dump != null)
        {
            _dump.Write((byte)'N');
            _dump.Write((byte)tag.Length);
            _dump.Write(Encoding.ASCII.GetBytes(tag));
            _dump.Write((uint)data.Length);
            _dump.Write(data);
        }
    }

    public long Outputs => Streams.Sum(s => s.Count);

    public string Render()
    {
        var sb = new StringBuilder();
        sb.Append("device-oracle golden 1\n");
        sb.Append("case ").Append(Name).Append('\n');
        sb.Append("seed ").Append(Seed).Append('\n');
        foreach (var s in Streams)
        {
            sb.Append("stream ").Append(s.Name).Append('\n');
            sb.Append("count ").Append(s.Count).Append('\n');
            sb.Append("sha256 ").Append(s.Digest).Append('\n');
            foreach (var (tag, data) in s.First)
                sb.Append(tag).Append(' ').Append(data.Length == 0 ? "-" : Convert.ToHexString(data)).Append('\n');
            sb.Append("end\n");
        }
        return sb.ToString();
    }

    public static GoldenCase Parse(string text)
    {
        var lines = text.Split('\n');
        int i = 0;
        string Next() => i < lines.Length ? lines[i++] : throw new InvalidDataException("A golden file ends early.");
        string Field(string key)
        {
            string line = Next();
            if (!line.StartsWith(key + " ", StringComparison.Ordinal))
                throw new InvalidDataException($"Line {i}: '{key}' expected, found '{line}'.");
            return line.Substring(key.Length + 1);
        }
        if (Next() != "device-oracle golden 1") throw new InvalidDataException("Not a device-oracle golden file.");
        var c = new GoldenCase(Field("case"), ulong.Parse(Field("seed")), dump: false);
        while (i < lines.Length && lines[i].Length > 0)
        {
            string name = Field("stream");
            long count = long.Parse(Field("count"));
            string digest = Field("sha256");
            var first = new List<(string, byte[])>();
            for (string line = Next(); line != "end"; line = Next())
            {
                int sp = line.IndexOf(' ');
                if (sp <= 0) throw new InvalidDataException($"Line {i}: an output line is '<tag> <hex>'.");
                string hex = line.Substring(sp + 1);
                first.Add((line.Substring(0, sp), hex == "-" ? Array.Empty<byte>() : Convert.FromHexString(hex)));
            }
            c.Streams.Add(new GoldenStream(name, count, digest, first));
            c._names.Add(name);
        }
        return c;
    }

    /// <summary>Compare a replay against the golden case. Prints one line per
    /// differing stream and returns how many differ. <paramref name="accept"/>
    /// is asked about each differing stream with the replay's stream, null
    /// when the replay lacks it: null when no intended change names it, or
    /// whether the change it names is this one and why. A stream it accepts
    /// is printed as [CHANGED] and not counted.</summary>
    public static int Compare(GoldenCase golden, GoldenCase replay, TextWriter log,
                              Func<string, GoldenStream?, (bool Ok, string Why)?>? accept = null)
    {
        int bad = 0;
        void Differ(string stream, GoldenStream? r, string how)
        {
            var verdict = accept?.Invoke(stream, r);
            if (verdict is { Ok: true } yes) { log.WriteLine($"  [CHANGED] {golden.Name}/{stream}: {how} ({yes.Why})"); return; }
            log.WriteLine($"  [DIFF] {golden.Name}/{stream}: {how}{(verdict is { } no ? $". {no.Why}" : "")}");
            bad++;
        }
        var mine = replay.Streams.ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (var g in golden.Streams)
        {
            if (!mine.Remove(g.Name, out var r))
            {
                Differ(g.Name, null, "missing from the replay");
                continue;
            }
            if (g.Count == r.Count && g.Digest == r.Digest) continue;
            int n = Math.Min(g.First.Count, r.First.Count);
            int at = -1;
            for (int k = 0; k < n && at < 0; k++)
                if (g.First[k].Tag != r.First[k].Tag || !g.First[k].Data.AsSpan().SequenceEqual(r.First[k].Data)) at = k;
            if (at >= 0)
                Differ(g.Name, r, $"output {at} golden {g.First[at].Tag} {Hex(g.First[at].Data)}, replay {r.First[at].Tag} {Hex(r.First[at].Data)}");
            else
                Differ(g.Name, r, $"the first {n} outputs agree, counts {g.Count} and {r.Count}, digests {g.Digest[..16]} and {r.Digest[..16]}");
        }
        foreach (var (extra, r) in mine) Differ(extra, r, "not in the golden file");
        return bad;
    }

    static string Hex(byte[] b) => b.Length == 0 ? "-" : Convert.ToHexString(b);
}

/// <summary>The golden directory: one compressed text per case and the
/// manifest of their SHA-256s.</summary>
internal static class GoldenStore
{
    public const string Manifest = "SHA256SUMS";

    public static string FileName(string caseName) => caseName + ".txt.gz";

    public static void Save(string dir, IEnumerable<GoldenCase> cases)
    {
        Directory.CreateDirectory(dir);
        var sums = ReadManifest(dir);
        foreach (var c in cases)
        {
            string text = c.Render();
            byte[] raw = Encoding.ASCII.GetBytes(text);
            using (var fs = File.Create(Path.Combine(dir, FileName(c.Name))))
            using (var gz = new GZipStream(fs, CompressionLevel.SmallestSize))
                gz.Write(raw);
            sums[FileName(c.Name)] = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        }
        var sb = new StringBuilder();
        foreach (var (file, sum) in sums.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(sum).Append("  ").Append(file).Append('\n');
        File.WriteAllText(Path.Combine(dir, Manifest), sb.ToString(), new UTF8Encoding(false));
    }

    public static Dictionary<string, string> ReadManifest(string dir)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        string path = Path.Combine(dir, Manifest);
        if (!File.Exists(path)) return sums;
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0) continue;
            int sp = line.IndexOf("  ", StringComparison.Ordinal);
            if (sp != 64) throw new InvalidDataException($"{Manifest}: '{line}' is not '<sha256>  <file>'.");
            sums[line.Substring(sp + 2)] = line.Substring(0, sp);
        }
        return sums;
    }

    /// <summary>The decompressed text of a case's file, after checking it
    /// against the manifest.</summary>
    public static string ReadVerified(string dir, string caseName)
    {
        var sums = ReadManifest(dir);
        string file = FileName(caseName);
        if (!sums.TryGetValue(file, out var want))
            throw new InvalidDataException($"{Manifest} in {dir} has no entry for {file}.");
        byte[] raw = Decompress(Path.Combine(dir, file));
        string got = Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
        if (got != want)
            throw new InvalidDataException($"{file}: SHA-256 {got} does not match {Manifest} ({want}).");
        return Encoding.ASCII.GetString(raw);
    }

    public static byte[] Decompress(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>Reads what the native driver harness writes: little-endian
/// records, 'S' then a 16-bit length and a stream name, 'O' then an 8-bit
/// length and a tag, a 32-bit length and the output's bytes, and 'E' at the
/// end. A file without its 'E' came from a harness that stopped early. A
/// dump (GoldenCase.DumpDir) may also hold 'N' notes, laid out as outputs,
/// which are not outputs and are skipped.</summary>
internal static class NativeStreamReader
{
    public static void ReadInto(string path, GoldenCase target)
    {
        using var br = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
        for (;;)
        {
            int kind = br.BaseStream.ReadByte();
            switch (kind)
            {
                case 'S':
                {
                    int n = br.ReadUInt16();
                    target.Stream(Encoding.ASCII.GetString(br.ReadBytes(n)));
                    break;
                }
                case 'O':
                {
                    int t = br.ReadByte();
                    string tag = Encoding.ASCII.GetString(br.ReadBytes(t));
                    int len = checked((int)br.ReadUInt32());
                    byte[] data = br.ReadBytes(len);
                    if (data.Length != len) throw new EndOfStreamException($"{path}: an output is cut short.");
                    target.Out(tag, data);
                    break;
                }
                case 'N':
                {
                    br.ReadBytes(br.ReadByte());
                    br.ReadBytes(checked((int)br.ReadUInt32()));
                    break;
                }
                case 'E':
                    return;
                case -1:
                    throw new EndOfStreamException($"{path} ends without its end record: the harness stopped early.");
                default:
                    throw new InvalidDataException($"{path}: unknown record kind {kind} at offset {br.BaseStream.Position - 1}.");
            }
        }
    }
}
