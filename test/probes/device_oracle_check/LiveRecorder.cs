// What a live probe records for device_oracle_check's compare-live mode.
//
// A probe that drives the live driver records the same subjects the native
// harness records offline, in the golden format of Golden.cs, and also
// every output in full in the record format NativeStreamReader reads. The
// golden text keeps only the first GoldenStream.FirstKept outputs of a
// stream, and a comparison that masks a time-dependent tag (a timer byte, a
// repeat count) needs every output to recompute what is left. Linked into
// sony_feature_gate_check, switch_pro_check, ds3_sixaxis_check and
// HIDMaestroTest.
//
// Files in the output directory:
//   <case>.txt.gz, SHA256SUMS   GoldenStore.Save, seed 0 (a live run draws
//                               no random input)
//   <case>.records              'S' u16 length and the stream name, 'O' u8
//                               length and the tag, u32 length and the
//                               bytes, little-endian, 'E' at the end
// A .records file without its 'E' comes from a run that stopped early, and
// NativeStreamReader refuses it.
using System;
using System.IO;
using System.Text;

internal sealed class LiveRecorder : IDisposable
{
    readonly string _dir;
    readonly GoldenCase _golden;
    readonly BinaryWriter _raw;
    readonly byte[] _word = new byte[4];

    public LiveRecorder(string dir, string caseName)
    {
        _dir = Path.GetFullPath(dir);
        Directory.CreateDirectory(_dir);
        _golden = new GoldenCase(caseName, 0);
        _raw = new BinaryWriter(File.Create(Path.Combine(_dir, caseName + ".records")), Encoding.ASCII);
    }

    /// <summary>Start a stream. Outputs go to it until the next one starts.</summary>
    public void Stream(string name)
    {
        _golden.Stream(name);
        byte[] b = Encoding.ASCII.GetBytes(name);
        _raw.Write((byte)'S');
        _raw.Write(checked((ushort)b.Length));
        _raw.Write(b);
    }

    public void Out(string tag, ReadOnlySpan<byte> data)
    {
        _golden.Out(tag, data);
        byte[] t = Encoding.ASCII.GetBytes(tag);
        _raw.Write((byte)'O');
        _raw.Write((byte)t.Length);
        _raw.Write(t);
        _raw.Write((uint)data.Length);
        _raw.Write(data);
    }

    public void OutByte(string tag, byte value)
    {
        _word[0] = value;
        Out(tag, _word.AsSpan(0, 1));
    }

    /// <summary>A 32-bit value, little-endian.</summary>
    public void OutU32(string tag, uint value)
    {
        BitConverter.TryWriteBytes(_word, value);
        Out(tag, _word);
    }

    /// <summary>Close the record file with its end mark and write the golden
    /// text and manifest. Call once, after the last output.</summary>
    public void Save()
    {
        _raw.Write((byte)'E');
        _raw.Flush();
        GoldenStore.Save(_dir, new[] { _golden });
    }

    public void Dispose() => _raw.Dispose();
}
