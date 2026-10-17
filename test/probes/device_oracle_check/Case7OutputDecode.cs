// Case 7: what a program receives for a host's output report at 9bd6279:
// OutputReceived, OutputDecoded with each field's name, boxed type and
// value, and each port's OutputChanged. The decode runs on the controller's
// own output thread, as it does on a device: the case builds the SDK's
// controller as case 1 does, hands it an output ring of process memory, and
// writes slots into that ring the way the driver's PublishOutput does
// (driver.c:1252-1261: data, then the slot's SeqNo, then Head).
//
// The boxed type is part of the contract: a consumer reads leftMotor and
// rightMotor with "is byte", so a decode that boxes another type stops its
// rumble with no error (design review FB10, VendorBlobCodec.cs:703-760 for
// the codec's types, HMController.cs:1797-1848 for the Switch decoders).
//
// The profiles: every catalog profile with an extendedOutputReport,
// switch-pro, whose controller decodes rumble by its vendor and product ID
// at 9bd6279, and xbox-360-wired and xbox-360-wireless, whose XInput
// rumble packets the test client decodes by vendor ID.
//
// Beside the SDK's events, each packet's line from the test client's own
// decode, HIDMaestroTest's DecodeOutputPacket (test/Program.cs:871-910),
// which keys its rumble labels on vendor IDs and report IDs. It is loaded
// from the test client's build (test\bin\Release\<tfm>\win-x64), so that project is
// built before this case runs. Each gets its output report at every length from empty to two
// bytes past its size, zeroed, all 0xFF, each byte alone at 0x80 and 0xFF,
// then random reports, and reports with other IDs and sources, which must
// decode nothing.
//
// The output thread polls the ring every 8 ms here, because the case gives
// it no doorbell: it opens none, since no object of the index's name
// exists. A batch ends with a sentinel slot the case waits for, so every
// event of the batch has run when the case reads them.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal sealed class Case7OutputDecode : IOracleCase
{
    public string Name => "case7-output-decode";

    const ulong CaseConstant = 7;
    const int FirstIndex = 7600;
    const int RandomReports = 2000;
    const int Batch = 32;
    const byte SentinelSource = 0xEE, SentinelId = 0xEE;
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    public void Record(GoldenCase golden, Options options)
    {
        var all = ProfileDatabase.LoadEmbedded().All;
        var testDecode = TestClientDecode();
        var profiles = all.Where(p => p.ExtendedOutputReport != null
                                      || p.Id is "switch-pro" or "xbox-360-wired" or "xbox-360-wireless")
                          .OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        IntPtr view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
        IntPtr ring = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_OUTPUT_SIZE);
        try
        {
            int n = 0;
            foreach (var p in profiles)
            {
                Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
                Marshal.Copy(new byte[SharedMemoryIO.SHARED_OUTPUT_SIZE], 0, ring, SharedMemoryIO.SHARED_OUTPUT_SIZE);
                int index = FirstIndex + n++;
                var c = Case1Submit.Create(p, index, view, ring);
                var hp = new HMProfile(p);
                var events = new ConcurrentQueue<(string Tag, byte[] Data)>();
                var sentinel = new SemaphoreSlim(0);
                c.OutputReceived += (_, pkt) =>
                {
                    if ((byte)pkt.Source == SentinelSource && pkt.ReportId == SentinelId) { sentinel.Release(); return; }
                    var o = new byte[2 + pkt.Data.Length];
                    o[0] = (byte)pkt.Source; o[1] = pkt.ReportId;
                    pkt.Data.Span.CopyTo(o.AsSpan(2));
                    events.Enqueue(("rx", o));
                    events.Enqueue(("testapp", Encoding.UTF8.GetBytes(testDecode(hp, pkt))));
                };
                c.OutputDecoded += (_, e) => events.Enqueue(("decoded", Decoded(e)));
                foreach (var port in c.Ports)
                {
                    var pt = port;
                    pt.OutputChanged += (_, e) => events.Enqueue(("port", Encoding.ASCII.GetBytes($"{pt.Number}:{e.Name}={e.Value}")));
                }

                golden.Stream($"output/{p.Id}");
                uint head = 0;
                var pending = new List<(byte Source, byte Id, byte[] Data)>();
                void Flush()
                {
                    if (pending.Count == 0) return;
                    foreach (var (s, id, d) in pending) Write(ring, ref head, s, id, d);
                    Write(ring, ref head, SentinelSource, SentinelId, Array.Empty<byte>());
                    if (!sentinel.Wait(10_000))
                        throw new TimeoutException($"{p.Id}: the output thread did not reach the batch's end in 10 s.");
                    foreach (var (s, id, d) in pending)
                    {
                        var input = new byte[2 + d.Length];
                        input[0] = s; input[1] = id; d.CopyTo(input, 2);
                        golden.Out("in", input);
                    }
                    while (events.TryDequeue(out var ev)) golden.Out(ev.Tag, ev.Data);
                    pending.Clear();
                }
                void Send(byte source, byte id, byte[] data)
                {
                    pending.Add((source, id, data));
                    if (pending.Count == Batch) Flush();
                }

                var rng = new Rng(options.Seed, CaseConstant ^ Case1Submit.Fnv(p.Id));
                const byte Out = (byte)HMOutputSource.HidOutput;
                var ids = new List<(byte Id, int Size)>();
                if (p.ExtendedOutputReport is { } eo) ids.Add((eo.ReportIdByte, eo.Size));
                // The two reports the Switch Pro decode reads, 0x01 rumble
                // with a subcommand and 0x10 rumble alone, at the 49 bytes a
                // Bluetooth host writes (SDL_hidapi_switch.c, the report the
                // driver sees with its ID stripped).
                if (p.Id == "switch-pro") { ids.Add((0x01, 49)); ids.Add((0x10, 49)); }
                foreach (var (id, size) in ids)
                {
                    int dataSize = Math.Max(0, size - 1);
                    for (int len = 0; len <= dataSize + 2; len++) Send(Out, id, new byte[len]);
                    Send(Out, id, Enumerable.Repeat((byte)0xFF, dataSize).ToArray());
                    for (int b = 0; b < dataSize; b++)
                        foreach (byte v in new byte[] { 0x80, 0xFF })
                        {
                            var d = new byte[dataSize];
                            d[b] = v;
                            Send(Out, id, d);
                        }
                    for (int i = 0; i < RandomReports; i++)
                    {
                        var d = new byte[rng.Next(8) == 0 ? rng.Next(dataSize + 3) : dataSize];
                        rng.Fill(d);
                        Send(Out, id, d);
                    }
                    // The same ID from the other sources a ring slot carries.
                    foreach (byte src in new byte[] { (byte)HMOutputSource.HidFeature, 2, (byte)HMOutputSource.HidFeatureRead })
                    {
                        var d = new byte[dataSize];
                        rng.Fill(d);
                        Send(src, id, d);
                    }
                }
                // XInput rumble as the companion publishes it (source 2,
                // companion.c:976-979): every length to 8, then random
                // five-byte packets.
                if (p.VendorId == 0x045E)
                {
                    for (int len = 0; len <= 8; len++)
                    {
                        Send((byte)HMOutputSource.XInput, 0, new byte[len]);
                        Send((byte)HMOutputSource.XInput, 0, Enumerable.Repeat((byte)0xFF, len).ToArray());
                    }
                    for (int i = 0; i < 200; i++)
                    {
                        var d = new byte[5];
                        rng.Fill(d);
                        Send((byte)HMOutputSource.XInput, 0, d);
                    }
                }
                // Other report IDs, which no decode of this profile reads.
                for (int i = 0; i < 64; i++)
                {
                    byte id = rng.Byte();
                    if (id == SentinelId) continue;
                    var d = new byte[rng.Next(65)];
                    rng.Fill(d);
                    Send(Out, id, d);
                }
                Flush();

                Stop(c);
                Case1Submit.Forget(index);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(view);
            Marshal.FreeHGlobal(ring);
        }
    }

    /// <summary>HIDMaestroTest's DecodeOutputPacket, from the test client's
    /// Release build beside this tree, loaded into the default context so it
    /// binds to the SDK this probe has loaded.</summary>
    static Func<HMProfile, HMOutputPacket, string> TestClientDecode()
    {
        string root = Path.GetFullPath(Path.Combine(ProbeDir(), "..", "..", ".."));
        string dll = Path.Combine(root, "test", "bin", "Release", "net10.0-windows10.0.26100.0", "win-x64", "HIDMaestroTest.dll");
        if (!File.Exists(dll))
            throw new FileNotFoundException($"{dll} is not built: build test\\HIDMaestroTest.csproj in Release first.");
        // A build older than any file it is built from records or replays
        // a decode the sources no longer give (review N3): the project's
        // own .cs files (everything under test\ but bin\, obj\, and the
        // probes\ and sandbox\ folders its project removes), the files its
        // project includes by name, and the project file.
        string test = Path.Combine(root, "test");
        string project = Path.Combine(test, "HIDMaestroTest.csproj");
        DateTime built = File.GetLastWriteTimeUtc(dll);
        var linked = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(project), "<Compile Include=\"([^\"]+)\"")
            .Select(m => Path.GetFullPath(Path.Combine(test, m.Groups[1].Value)));
        var newer = Directory.EnumerateFiles(test, "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetRelativePath(test, f).Split(Path.DirectorySeparatorChar)[0]
                is not ("probes" or "sandbox" or "bin" or "obj"))
            .Concat(linked)
            .Append(project)
            .Where(f => File.Exists(f) && File.GetLastWriteTimeUtc(f) > built)
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        if (newer.Count > 0)
            throw new InvalidOperationException($"{dll} is older than {newer.Count} file(s) it is built from ({string.Join(", ", newer)}): " +
                "build test\\HIDMaestroTest.csproj in Release again.");
        var asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        var m = asm.GetType("HIDMaestroTest.Program")?.GetMethod("DecodeOutputPacket", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("HIDMaestroTest's Program.DecodeOutputPacket changed: update case 7.");
        return (profile, pkt) => (string)m.Invoke(null, new object[] { profile, pkt })!;
    }

    static string ProbeDir() =>
        typeof(Case7OutputDecode).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "ProbeDir").Value!;

    /// <summary>One slot, as the driver publishes it: the fields, the
    /// slot's SeqNo, then Head (SharedMemoryIO.cs:81-89 for the layout).</summary>
    internal static void Write(IntPtr ring, ref uint head, byte source, byte id, byte[] data)
    {
        uint seq = head + 1;
        int slot = SharedMemoryIO.OUTPUT_HEADER_SIZE + (int)((seq - 1) % SharedMemoryIO.OUTPUT_RING_SLOTS) * SharedMemoryIO.OUTPUT_SLOT_SIZE;
        int size = Math.Min(data.Length, SharedMemoryIO.OUTPUT_SLOT_SIZE - SharedMemoryIO.OUTPUT_SLOT_OFFSET_DATA);
        Marshal.WriteByte(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SOURCE, source);
        Marshal.WriteByte(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_REPORT_ID, id);
        Marshal.WriteInt16(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SIZE, (short)size);
        if (size > 0) Marshal.Copy(data, 0, ring + slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_DATA, size);
        Thread.MemoryBarrier();
        Marshal.WriteInt32(ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SEQNO, (int)seq);
        Thread.MemoryBarrier();
        Marshal.WriteInt32(ring, 0, (int)seq);
        head = seq;
    }

    /// <summary>OutputDecoded as text: the report ID, CRC flag, raw bytes,
    /// then each field sorted by name as name, the boxed value's type and
    /// the value, invariant culture, floats round-trip.</summary>
    internal static byte[] Decoded(HMOutputDecodedEventArgs e)
    {
        var sb = new StringBuilder();
        sb.Append(e.ReportId.ToString("X2")).Append(e.CrcValid ? " crc" : " nocrc").Append(' ').Append(Convert.ToHexString(e.RawBytes.Span));
        foreach (var (k, v) in (e.Fields ?? new Dictionary<string, object>()).OrderBy(f => f.Key, StringComparer.Ordinal))
            sb.Append(' ').Append(k).Append(':').Append(v?.GetType().Name ?? "null").Append('=').Append(Value(v));
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    static string Value(object? v) => v switch
    {
        null => "null",
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        byte[] b => Convert.ToHexString(b),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>Stop the controller's output thread. The controller is never
    /// disposed: its context is an empty stand-in (case 1).</summary>
    internal static void Stop(HMController c)
    {
        ((CancellationTokenSource)typeof(HMController).GetField("_outputCts", Instance)!.GetValue(c)!).Cancel();
        ((Thread?)typeof(HMController).GetField("_outputThread", Instance)!.GetValue(c))?.Join(5000);
    }
}
