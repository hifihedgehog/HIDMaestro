// compare-live: the prepared live run against the native driver harness.
//
// The live probes (LiveRecorder.cs) record what the 9bd6279 driver answered
// to requests made through the HID class and the I/O manager. Their inputs
// are not case 5's, so this mode re-drives a harness (hm_script.c, through
// --script) with exactly the requests each live call became, then holds
// every live output against what the harness's driver produced. The harness
// is driver_harness_oracle.exe, 9bd6279's own sources from native\oracle,
// the driver the live run recorded. --driver tree drives
// driver_harness_tree.exe, built from the repository's driver sources,
// instead, which holds the tree's driver to the live 9bd6279 answers. The profile configuration is
// the plan case 5 recorded with (case5-plan.txt in the golden directory),
// so the harness runs the values the SDK writes. v2.0.0: the live S46 run's
// SDK writes each controller's FeatureAnswers for its index and its
// version section under the contract (decision (c)), so each S46 stream
// runs under the harness's tag-own parameter, on a plan profile that
// carries the tree's SDK's values with the FeatureAnswers of that index
// (ContractPlan).
//
// Usage:
//   compare-live <out dir> [--golden <dir>] [--native <dir>] [--driver oracle|tree]
//                [--write-ledger <file>] [--pinned-ledger <file>]
//   compare-live --selftest <scratch dir> [--golden <dir>] [--native <dir>] [--driver oracle|tree]
// <out dir> holds live_batch.ps1's steps: s46\live-s46-sony-features.records,
// switch-pro\live-switch-pro.records, ds3\live-ds3-sixaxis.records and
// xusb\live-xusb-<profile>.records for each XUSB profile of the plan.
// --write-ledger writes what the run settled about each mapping, its
// verdict and every exception, to a file (Report.WriteLedger), and
// --pinned-ledger makes each mapping whose verdict, counts or exceptions
// differ from such a file a [DIFF]: the first live run's ledger is pinned,
// and a later run is held to it. Exit 0 when every compared byte matches,
// 1 on any [DIFF] or a recording that does not parse, 2 when a step's file
// is missing or the plan or the pinned ledger is. --selftest writes
// synthetic recordings into an absent or empty directory and checks this
// mode against them (see SelfTest below).
//
// How a live call maps to a driver request. Each mapping names its source.
// A mapping no source settles is printed as [UNVERIFIED-MAPPING] when a
// recording depends on it, and its observations go to the ledger. Where a
// call surely reached the driver, a difference is a [DIFF] whatever mapping
// it rests on (review finding 10): the error of a call both sides failed,
// the bytes past the driver's report, a window-attributed group's
// notifications, and a write refused at OutputReportByteLength.
//
//   M1 HidD_GetFeature(buffer, L) reaches the driver as
//      IOCTL_UMDF_HID_GET_FEATURE with the report id as a one-byte input
//      buffer and the report as the output buffer (Windows-driver-samples
//      hid/vhidmini2/driver/umdf2/util.c:30-37, RequestGetHidXferPacket_
//      ToReadFromDevice 41-94). With report ids the report buffer starts
//      with the id, and without them it does not (vhidmini.c:765-767: "hidclass
//      would pass on the report ID in the buffer (it wouldn't if report
//      descriptor did not have any report ID)"), and the caller's byte 0
//      stays the id the caller wrote (driver.c:197-205, 3082-3088). The id
//      the caller writes is zero when the collection has no ids (Microsoft
//      Learn, HidD_GetFeature, ReportBufferLength). So with ids the driver
//      gets input [buffer[0]] and an output buffer of L bytes, and the caller
//      sees that buffer. Without ids it gets input [0] and the L - 1 bytes
//      from buffer[1], and the caller sees buffer[0] then those.
//      Unverified: that the output length is L (or L - 1) rather than the
//      collection's FeatureReportByteLength, what a buffer of length 0
//      becomes (not driven here), and what the output buffer holds when it
//      arrives, which vhidmini.c:745-746 says a UMDF driver cannot read ("any read
//      from output buffer would be reading garbage"). The harness hands the
//      driver the caller's bytes.
//      A collection that declares no feature report, FeatureReportByteLength
//      0, passes no GET_FEATURE to the driver. Wine's hidclass.sys finds no
//      feature report and fails the call itself (device.c:484). On the B0
//      session's ps-classic every id at every length from 1 to 65 failed
//      with ERROR_INVALID_FUNCTION, where 9bd6279's GET_FEATURE handler
//      fails only with STATUS_BUFFER_TOO_SMALL, STATUS_NOT_SUPPORTED or
//      STATUS_NO_SUCH_DEVICE (driver.c:3004-3460), so the handler never ran.
//      Such a call is not driven. It must fail with nothing published, and
//      its error goes to the ledger (LM1NoFeature): Wine's is
//      ERROR_INVALID_PARAMETER and the live run's is ERROR_INVALID_FUNCTION,
//      so only a live run settles it.
//   M2 ReadFile of the collection's InputReportByteLength (the read length
//      must be at least that, Microsoft Learn, Obtaining HID Reports) is one
//      IOCTL_HID_READ_REPORT completion. The driver's bytes, up to the
//      information it set, are what the caller gets first. Without report
//      ids the caller's report starts with the id byte 0 (unverified for
//      ReadFile, the same convention as M1), and the bytes past the driver's
//      report are the HID class's padding (unverified, compared: a
//      difference there is a [DIFF]).
//   M3 WriteFile(buffer, L) is IOCTL_HID_WRITE_REPORT and
//      HidD_SetOutputReport(buffer, L) is IOCTL_UMDF_HID_SET_OUTPUT_REPORT
//      (vhidmini.c:389-462), the report as the input buffer
//      (util.c:97-160), id first with ids and without the 0 without them
//      (driver.c:1290-1292). The driver gets the output report's length,
//      whatever the caller's: Wine's hidclass.sys sets the packet's
//      reportBufferLen to the report's OutputLength less the id byte when
//      the collection has no ids, from the caller's buffer past that byte
//      (dlls/hidclass.sys/device.c:485-489, 500-506 at 2df1ee2), so bytes
//      past the report never reach the driver. The live DualShock 3 run shows it:
//      writes of 50 to 65 bytes publish the packet a 49-byte write does.
//      A write of the report's length or more,
//      led with report ids by an output id the descriptor declares, reaches
//      the driver: Wine's hidclass.sys refuses only a shorter write or an
//      undeclared id (dlls/hidclass.sys/device.c:473-484 at 2df1ee2), and
//      its test on Windows writes twice the report's length without report
//      ids and succeeds (dlls/dinput/tests/hid.c:1452-1465). Such a write is
//      driven whatever the live result, and a live refusal the driver did
//      not make is a [DIFF]. Any other write that failed live and raised no
//      output packet is not driven: the HID class may have refused it, or
//      the driver failed it without effect, and the recording cannot tell
//      the two apart.
//   M4 A failure's Win32 error is RtlNtStatusToDosError of the status the
//      driver completed with, when the HID class passes the status on. Where
//      the HID class may refuse a GET_FEATURE first (a buffer below
//      FeatureReportByteLength, or with report ids an id the descriptor does
//      not declare), its error is ERROR_INVALID_PARAMETER: Wine's
//      hidclass.sys returns STATUS_INVALID_PARAMETER for both (device.c:484),
//      and its test expects that error on Windows for each
//      (dlls/dinput/tests/hid.c:1167-1170, 1195-1200). A failed call with
//      any other error is a [DIFF]. The XUSB companion is opened directly, no HID class, and
//      its IOCTLs are METHOD_BUFFERED (Microsoft Learn, Buffer Descriptions
//      for I/O Control Codes): input and output as DeviceIoControl passes
//      them, and the bytes returned are the information.
//   M5 The written count of a WriteFile is the HID class's, not the
//      driver's (the driver sets no information on a write): not compared.
//   M6 What the output buffer holds when a request reaches the driver. The
//      harness hands the driver the caller's bytes for a GET_FEATURE with
//      report ids (the id, then the zeros the probes clear their buffers
//      to), zeros for one without them, and zeros for a READ_REPORT and for
//      an XUSB IOCTL. No source settles it: vhidmini.c:745-746 says a UMDF
//      driver reading its output buffer reads garbage, and METHOD_BUFFERED
//      gives input and output one system buffer that arrives holding the
//      input (Microsoft Learn, Buffer Descriptions for I/O Control Codes).
//      Only a live run settles it, and only where it shows: in the bytes
//      past the driver's report that the caller gets back, and in any
//      returned byte the driver did not write. probe-xusb --record keeps
//      only the returned bytes, so an XUSB run cannot settle it.
//
// After a live run compare-live prints a [MAPPING] line for each
// unverified part of M1 to M6: how many of the run's calls bore on it,
// whether it held in all of them, none or some, and examples where it did
// not. A part no call bore on is printed as still unverified.
//
// Each recording is a subject. Its request count is what its probe makes
// (the record functions named at each Begin below), and a subject whose
// recording holds fewer, or whose recorded calls were not all compared,
// fails: an empty or cut recording cannot pass.
//
// A live call that failed with nothing published, where the harness's
// driver answered, is a [DIFF] when its buffer is at least the
// collection's FeatureReportByteLength, which counts the id byte with or
// without report ids (HIDP_CAPS), the length HidD_GetFeature asks for, and
// with report ids its id is one the descriptor declares. Below that length,
// or for an undeclared id, the HID class may refuse it before the driver
// (M1, M4), so it is printed as unverified and must carry
// ERROR_INVALID_PARAMETER. The S46 recorder sweeps every profile one past
// that length (review finding 9), and the recording must hold each plan
// profile once, with each id and length of its matrix (note N5).
//
// Masked, as the recorders' header comments say: switch_pro_check's timer,
// repeat, skipped run counts (skippedRepeat), timerSteps, and the timer
// byte the recorder already zeroed in each 0x21 and 0x30 frame (the harness
// side is zeroed the same way), and ds3_sixaxis_check's repeat. Frames a live
// read takes ahead of a reply (skipped) may be the previous state's or the
// new one's, which depends on the stream clock: either matches.
//
// Live only. Another program on the machine may open a device the probe
// records, and its requests reach the driver beside the probe's, which the
// harness never sees. Two shapes are another program's for certain and
// print as [LIVE-ONLY], with an exception under LForeign in the ledger, in
// place of a [DIFF]: a feature-read notification of an id other than the
// call's in a group the fence attributes (with report ids the driver
// publishes the id the caller's request carries), and a Switch Pro reply,
// 0x21 or 0x81, read before the probe's first write (the driver queues one
// only in answer to an output report). The frames from that reply on are
// not compared. On the B0 session a subcommand 0x02 reached the Switch Pro
// before the probe's first write, and a read of feature 0x12 reached two
// DualShock 4 profiles during the probe's 0x04 sweep.
//
// The XUSB recordings name no controller index. The emulate holder is the
// only program on the machine, so its controller takes index 0. The
// harness runs index 0 and says so as unverified.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class CompareLive
{
    [DllImport("ntdll.dll")]
    static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);
    const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;

    // ── records ──────────────────────────────────────────────────────────

    internal sealed class Stream
    {
        public readonly string Name;
        public readonly List<(string Tag, byte[] Data)> Outs = new();
        public Stream(string name) { Name = name; }
        public void Out(string tag, byte[] data) => Outs.Add((tag, data));
    }

    internal static List<Stream> ReadRecords(string path)
    {
        var list = new List<Stream>();
        using var br = new BinaryReader(File.OpenRead(path), Encoding.ASCII);
        for (;;)
        {
            int kind = br.BaseStream.ReadByte();
            switch (kind)
            {
                case 'S': list.Add(new Stream(Encoding.ASCII.GetString(br.ReadBytes(br.ReadUInt16())))); break;
                case 'O':
                {
                    string tag = Encoding.ASCII.GetString(br.ReadBytes(br.ReadByte()));
                    int len = checked((int)br.ReadUInt32());
                    byte[] data = br.ReadBytes(len);
                    if (data.Length != len || list.Count == 0) throw new InvalidDataException($"{path}: a cut or orphan output");
                    list[^1].Out(tag, data);
                    break;
                }
                case 'E': return list;
                default: throw new InvalidDataException($"{path}: no end record, or an unknown record {kind}");
            }
        }
    }

    static void WriteRecords(string path, IEnumerable<Stream> streams)
    {
        using var bw = new BinaryWriter(File.Create(path), Encoding.ASCII);
        foreach (var s in streams)
        {
            byte[] n = Encoding.ASCII.GetBytes(s.Name);
            bw.Write((byte)'S'); bw.Write((ushort)n.Length); bw.Write(n);
            foreach (var (tag, data) in s.Outs)
            {
                byte[] t = Encoding.ASCII.GetBytes(tag);
                bw.Write((byte)'O'); bw.Write((byte)t.Length); bw.Write(t);
                bw.Write((uint)data.Length); bw.Write(data);
            }
        }
        bw.Write((byte)'E');
    }

    /// <summary>A stream's outputs read in order, with the tags checked.</summary>
    sealed class Cursor
    {
        readonly Stream _s;
        int _i;
        public Cursor(Stream s) { _s = s; }
        public bool End => _i >= _s.Outs.Count;
        public string? PeekTag => End ? null : _s.Outs[_i].Tag;
        public byte[] Take(string tag)
        {
            if (End || _s.Outs[_i].Tag != tag)
                throw new InvalidDataException($"{_s.Name}: output {_i} is {(End ? "past the end" : _s.Outs[_i].Tag)}, {tag} expected");
            return _s.Outs[_i++].Data;
        }
        public byte[]? TakeIf(string tag) => PeekTag == tag ? Take(tag) : null;
        public uint U32(string tag) => BitConverter.ToUInt32(Take(tag));
    }

    // ── the plan ─────────────────────────────────────────────────────────

    /// <summary>A plan profile. FeatureLength is the HID class's
    /// FeatureReportByteLength for its descriptor: the largest feature
    /// report's data bytes plus the id byte, which the HID class counts as
    /// byte 0 with or without report ids (HidP_GetCaps, Microsoft Learn:
    /// HIDP_CAPS). The S46 recording holds the live value, held against
    /// this one as an M1 observation.</summary>
    sealed record PlanProfile(string Group, string Id, bool UsesReportIds, int ReadLength, byte[] Features, byte[] Declared, int FeatureLength,
                              int OutputLength, IReadOnlyDictionary<byte, int> OutputReports)
    {
        /// <summary>The buffer length from which a live failure the
        /// harness's driver answered is a [DIFF] (review F10):
        /// FeatureReportByteLength, which already counts the id byte with or
        /// without report ids (HIDP_CAPS, above), the length HidD_GetFeature
        /// asks for (Microsoft Learn, HidD_GetFeature, ReportBufferLength).
        /// The review's "plus 1 without report ids" would count that byte
        /// twice.</summary>
        public int RefusalBound => FeatureLength;

        /// <summary>Whether the descriptor declares any feature report. The
        /// HID class passes no GET_FEATURE of a collection without one to the
        /// driver (M1).</summary>
        public bool DeclaresFeature => FeatureLength > 0;

        /// <summary>Whether the HID class may fail a GET_FEATURE before the
        /// driver sees it: a buffer shorter than FeatureReportByteLength
        /// (<paramref name="bound"/>: the recorded live value, or the plan's
        /// where a recording holds none), or,
        /// with report ids, an id the descriptor declares no feature report
        /// for. Wine's hidclass.sys refuses both with STATUS_INVALID_PARAMETER
        /// (dlls/hidclass.sys/device.c:484 at 2df1ee2), and its conformance
        /// test, which runs on Windows, expects ERROR_INVALID_PARAMETER for
        /// each (dlls/dinput/tests/hid.c:1167-1170, 1195-1200).</summary>
        public bool ClassMayRefuseFeature(byte id, int length, int bound) =>
            length < bound || (UsesReportIds && Array.IndexOf(Features, id) < 0);

        /// <summary>Whether a write of these bytes surely reaches the driver:
        /// at least its output report's length long (OutputLengthOf) and,
        /// with report ids, led by an output report id the descriptor
        /// declares. Wine's hidclass.sys
        /// passes every such write and refuses the rest (device.c:473-484),
        /// and its test on Windows writes twice the report's length without
        /// report ids and succeeds (dlls/dinput/tests/hid.c:1452-1465).</summary>
        public bool WriteReachesDriver(byte[] bytes, int length)
        {
            int report = OutputLengthOf(bytes, length);
            return report > 0 && length >= report;
        }

        /// <summary>The length of the output report a write of these bytes
        /// is, its data bytes plus the id byte: the report of the id in
        /// byte 0 with report ids, the one report without them. 0 when the
        /// descriptor declares no such report.</summary>
        public int OutputLengthOf(byte[] bytes, int length) =>
            UsesReportIds ? (length > 0 && OutputReports.TryGetValue(bytes[0], out int n) ? n : 0)
                          : OutputReports.TryGetValue(0, out int m) ? m : 0;

        /// <summary>M3: the bytes a driven write hands the driver, the
        /// output report's length of the caller's buffer, without the id
        /// byte 0 when the collection has no report ids (Wine's hidclass.sys
        /// device.c:485-489, 500-506). A write the descriptor declares no
        /// report for hands over the caller's bytes.</summary>
        public byte[] WriteReport(byte[] bytes)
        {
            int report = OutputLengthOf(bytes, bytes.Length);
            int end = report > 0 ? Math.Min(report, bytes.Length) : bytes.Length;
            int from = UsesReportIds ? 0 : Math.Min(1, end);
            return bytes.AsSpan(from, end - from).ToArray();
        }
    }

    /// <summary>FeatureReportByteLength and OutputReportByteLength of a
    /// descriptor, each the largest report's data bytes plus the id byte
    /// (HIDP_CAPS, above), each output report's own length by its id,
    /// and its feature report ids in the descriptor's order, as the S46
    /// recorder lists them (HidReportMap).</summary>
    static (int Feature, int Output, Dictionary<byte, int> OutputReports, byte[] Declared) LengthsOf(byte[] descriptor)
    {
        var map = HIDMaestro.Internal.HidReportMap.Parse(descriptor);
        int Longest(HIDMaestro.Internal.HidReportKind kind)
        {
            var reports = map.Reports.Where(r => r.Kind == kind).ToList();
            return reports.Count == 0 ? 0 : reports.Max(r => r.DataBytes) + 1;
        }
        var outputReports = map.Reports.Where(r => r.Kind == HIDMaestro.Internal.HidReportKind.Output)
            .GroupBy(r => r.ReportId).ToDictionary(g => g.Key, g => g.Max(r => r.DataBytes) + 1);
        var declared = map.Reports.Where(r => r.Kind == HIDMaestro.Internal.HidReportKind.Feature).Select(r => r.ReportId).ToArray();
        return (Longest(HIDMaestro.Internal.HidReportKind.Feature), Longest(HIDMaestro.Internal.HidReportKind.Output), outputReports, declared);
    }

    static Dictionary<string, PlanProfile> ParsePlan(string text)
    {
        var map = new Dictionary<string, PlanProfile>(StringComparer.Ordinal);
        string? group = null, id = null;
        bool ids = false;
        int read = 0;
        (int Feature, int Output, Dictionary<byte, int> OutputReports, byte[] Declared) lengths = (0, 0, new(), Array.Empty<byte>());
        byte[] feats = Array.Empty<byte>();
        foreach (var raw in text.Split('\n'))
        {
            var w = raw.TrimEnd('\r').Split(' ');
            switch (w[0])
            {
                case "profile": group = w[1]; id = w[2]; ids = false; read = 0; lengths = (0, 0, new(), Array.Empty<byte>()); feats = Array.Empty<byte>(); break;
                case "value" when w.Length == 4 && w[1] == "ReportDescriptor": lengths = LengthsOf(Convert.FromHexString(w[3])); break;
                case "reportids": ids = w[1] == "1"; break;
                case "readlength": read = int.Parse(w[1]); break;
                case "features": feats = w.Skip(1).Select(x => Convert.ToByte(x, 16)).ToArray(); break;
                case "end":
                    if (id != null) map.TryAdd(id, new PlanProfile(group!, id, ids, read, feats, lengths.Declared, lengths.Feature, lengths.Output, lengths.OutputReports));
                    id = null;
                    break;
            }
        }
        return map;
    }

    // ── the harness ──────────────────────────────────────────────────────

    /// <summary>One harness stream's outputs, split at its call marks.</summary>
    sealed class Calls
    {
        readonly Dictionary<int, List<(string Tag, byte[] Data)>> _by = new();
        public Calls(Stream s)
        {
            List<(string, byte[])>? cur = null;
            foreach (var (tag, data) in s.Outs)
            {
                if (tag == "call") { cur = new(); _by[BitConverter.ToInt32(data)] = cur; continue; }
                cur?.Add((tag, data));
            }
        }
        public List<(string Tag, byte[] Data)> Of(int k) => _by.TryGetValue(k, out var l) ? l : new();
    }

    /// <summary>A completion as hm_wdf.c records it in a script run: serial,
    /// status, information, then the whole output buffer.</summary>
    internal readonly record struct Completion(string Tag, int Status, uint Info, byte[] Buffer)
    {
        public bool Ok => Status >= 0;
        public byte[] Reported => Buffer.AsSpan(0, (int)Math.Min(Info, (uint)Buffer.Length)).ToArray();
        public static bool Is(string tag) => tag is "read" or "write" or "setoutput" or "setfeature" or "getfeature" or "getinput" or "ioctl";
        public static Completion From(string tag, byte[] d) =>
            new(tag, BitConverter.ToInt32(d, 4), BitConverter.ToUInt32(d, 8), d.AsSpan(12).ToArray());
    }

    static IEnumerable<Completion> Completions(List<(string Tag, byte[] Data)> outs, string? tag = null) =>
        outs.Where(o => Completion.Is(o.Tag) && (tag == null || o.Tag == tag)).Select(o => Completion.From(o.Tag, o.Data));

    /// <summary>Ring slots as hm_record.c records them: source, report id,
    /// a 16-bit size, the data.</summary>
    static IEnumerable<byte[]> Ring(List<(string Tag, byte[] Data)> outs) =>
        outs.Where(o => o.Tag == "ring").Select(o => o.Data);

    static uint Win32Error(int status) => status >= 0 ? 0 : RtlNtStatusToDosError(status);

    internal static List<Stream> RunHarness(string exe, string plan, string script, string work)
    {
        string output = Path.Combine(work, "harness.bin");
        if (File.Exists(output)) File.Delete(output);
        SetErrorMode(SetErrorMode(0) | SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        foreach (var a in new[] { "--script", plan, script, output }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        var err = p.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(exe)} exited {p.ExitCode}: {err.Result.Trim()}");
        Console.WriteLine($"  {Path.GetFileName(exe)} --script: {stdout.Trim()}");
        return ReadRecords(output);
    }

    static string Hex(ReadOnlySpan<byte> b) => b.Length == 0 ? "-" : Convert.ToHexString(b);

    // ── the report ───────────────────────────────────────────────────────

    sealed class Report
    {
        public int Diffs;
        public int Compared;
        readonly SortedDictionary<string, List<string>> _unverified = new(StringComparer.Ordinal);

        // Per subject: the requests its probe makes, the calls its recording
        // holds, the calls compared and the differences (review F9).
        sealed class Subject { public int Expected, Recorded, Compared, Diffs; public string Basis = ""; }
        readonly List<(string Name, Subject S)> _subjects = new();
        Subject? _cur;

        // What the run showed about each mapping (review F6).
        readonly SortedDictionary<string, (int Held, int Not, List<string> Seen)> _ledger = new(StringComparer.Ordinal);

        /// <summary>A subject: its probe's request count, the calls its
        /// recording holds, and where the count comes from.</summary>
        public void Begin(string subject, int expected, int recorded, string basis)
        {
            _cur = new Subject { Expected = expected, Recorded = recorded, Basis = basis };
            _subjects.Add((subject, _cur));
        }

        /// <summary>One recorded call taken up for comparison.</summary>
        public void Call()
        {
            if (_cur == null) throw new InvalidOperationException("a call before any subject");
            _cur.Compared++;
        }

        public void Diff(string where, string what)
        {
            Diffs++;
            if (_cur != null) _cur.Diffs++;
            if (Diffs <= 200) Console.WriteLine($"  [DIFF] {where}: {what}");
        }

        public void Same() => Compared++;

        /// <summary>Live only: what another program on the machine did to
        /// the device while it was recorded, which the harness, driven only
        /// by the probe's requests, cannot hold. Printed, kept under
        /// LForeign in the ledger, and not a difference.</summary>
        public int LiveOnlyCount;
        readonly List<string> _liveOnly = new();

        public void LiveOnly(string where, string what, string ledgerAt)
        {
            LiveOnlyCount++;
            _liveOnly.Add($"{where}: {what}");
            Mapping(LForeign, false, ledgerAt);
        }

        /// <summary>Gathered by kind and place, printed once each.</summary>
        public void Unverified(string where, string kind, string detail)
        {
            string key = $"{where}: {kind}";
            if (!_unverified.TryGetValue(key, out var l)) _unverified[key] = l = new();
            l.Add(detail);
        }

        /// <summary>One observation of a mapping: whether what the live run
        /// recorded agrees with what the harness assumed. Every exception is
        /// kept for the ledger file, and the first 12 are printed.</summary>
        public void Mapping(string mapping, bool held, string? seen = null)
        {
            var e = _ledger.TryGetValue(mapping, out var v) ? v : (Held: 0, Not: 0, Seen: new List<string>());
            if (held) e.Held++; else e.Not++;
            if (seen != null) e.Seen.Add(seen);
            _ledger[mapping] = e;
        }

        /// <summary>Each subject's counts. A subject whose recording holds
        /// fewer calls than its probe makes fails, so an empty or cut
        /// recording cannot pass (review F9).</summary>
        public void CheckSubjects()
        {
            _cur = null;
            foreach (var (name, x) in _subjects)
            {
                Console.WriteLine($"  {name}: {x.Expected} request(s) its probe makes ({x.Basis}), {x.Recorded} recorded, {x.Compared} compared, {x.Diffs} difference(s)");
                if (x.Recorded < x.Expected)
                    Diff(name, $"the recording holds {x.Recorded} of the {x.Expected} requests its probe makes");
                else if (x.Compared < x.Recorded)
                    Diff(name, $"{x.Recorded - x.Compared} recorded call(s) were not compared");
            }
        }

        public void Print()
        {
            foreach (var (key, details) in _unverified)
            {
                string shown = string.Join(", ", details.Take(12));
                if (details.Count > 12) shown += $", and {details.Count - 12} more";
                Console.WriteLine($"  [UNVERIFIED-MAPPING] {key} ({shown})");
            }
            foreach (var line in _liveOnly) Console.WriteLine($"  [LIVE-ONLY] {line}");
            if (Diffs > 200) Console.WriteLine($"  ... {Diffs - 200} more [DIFF] lines not shown");
        }

        /// <summary>What this run settled about each mapping the harness
        /// assumes, and which it did not exercise (review F6).</summary>
        public void PrintLedger(IEnumerable<string> all)
        {
            foreach (var (mapping, (held, not, seen)) in _ledger)
            {
                string verdict = not == 0 ? $"held in all {held} observations: settled, it holds"
                    : held == 0 ? $"did not hold in any of {not} observations: settled, it does not hold"
                    : $"held in {held}, did not hold in {not}: settled, it holds only in part";
                string shown = string.Join(", ", seen.Take(12));
                if (seen.Count > 12) shown += $", and {seen.Count - 12} more";
                string tail = seen.Count > 0 ? $" ({shown})" : "";
                Console.WriteLine($"  [MAPPING] {mapping}: {verdict}{tail}");
            }
            foreach (var m in all.Where(m => !_ledger.ContainsKey(m)))
                Console.WriteLine($"  [MAPPING] {m}: not exercised by this run, still unverified");
        }

        // The ledger file: what this run settled about each mapping, so a
        // later run can be held to it (review finding 10). One block per
        // mapping in the order of `all`, each mapping's verdict, its counts
        // and every exception in the order seen:
        //   hm-compare-live-ledger 1
        //   mapping <name>
        //   verdict holds | does-not-hold | holds-in-part | not-exercised
        //   observations <held> <not held>
        //   exception <text>
        //   end
        const string LedgerHead = "hm-compare-live-ledger 1";

        /// <summary>Each mapping's block of the ledger file, keyed by the
        /// mapping.</summary>
        public SortedDictionary<string, string> LedgerBlocks(IEnumerable<string> all)
        {
            var blocks = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var m in all.Concat(_ledger.Keys).Distinct())
            {
                var sb = new StringBuilder($"mapping {m}\n");
                if (_ledger.TryGetValue(m, out var e))
                {
                    string verdict = e.Not == 0 ? "holds" : e.Held == 0 ? "does-not-hold" : "holds-in-part";
                    sb.Append($"verdict {verdict}\nobservations {e.Held} {e.Not}\n");
                    foreach (var s in e.Seen) sb.Append($"exception {s.Replace('\n', ' ')}\n");
                }
                else sb.Append("verdict not-exercised\nobservations 0 0\n");
                sb.Append("end\n");
                blocks[m] = sb.ToString();
            }
            return blocks;
        }

        public void WriteLedger(string path, IEnumerable<string> all)
        {
            var sb = new StringBuilder(LedgerHead + "\n");
            foreach (var block in LedgerBlocks(all).Values) sb.Append(block);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Console.WriteLine($"  ledger written: {path}");
        }

        static SortedDictionary<string, string> ReadLedger(string path)
        {
            var lines = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0 || lines[0] != LedgerHead) throw new InvalidDataException($"{path} is not a compare-live ledger");
            var blocks = new SortedDictionary<string, string>(StringComparer.Ordinal);
            StringBuilder? cur = null;
            string? name = null;
            foreach (var line in lines.Skip(1))
            {
                if (line.Length == 0) continue;
                if (line.StartsWith("mapping ", StringComparison.Ordinal))
                {
                    if (cur != null) throw new InvalidDataException($"{path}: a mapping block has no end");
                    name = line.Substring(8);
                    cur = new StringBuilder(line + "\n");
                    continue;
                }
                if (cur == null) throw new InvalidDataException($"{path}: '{line}' outside a mapping block");
                cur.Append(line + "\n");
                if (line == "end")
                {
                    if (!blocks.TryAdd(name!, cur.ToString())) throw new InvalidDataException($"{path}: {name} twice");
                    cur = null;
                }
            }
            if (cur != null) throw new InvalidDataException($"{path}: the last mapping block has no end");
            return blocks;
        }

        /// <summary>A [DIFF] for each mapping whose verdict, counts or
        /// exceptions differ from the pinned ledger's, and for a mapping
        /// only one side has.</summary>
        public void CheckPinned(string path, IEnumerable<string> all)
        {
            var pinned = ReadLedger(path);
            var now = LedgerBlocks(all);
            int before = Diffs;
            foreach (var m in pinned.Keys.Union(now.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                if (!pinned.TryGetValue(m, out var p)) { Diff("ledger", $"{m}: not in the pinned ledger {path}"); continue; }
                if (!now.TryGetValue(m, out var n)) { Diff("ledger", $"{m}: in the pinned ledger, not in this run"); continue; }
                if (p == n) continue;
                var pl = p.Split('\n');
                var nl = n.Split('\n');
                string Line(string[] b, string key) => b.FirstOrDefault(l => l.StartsWith(key, StringComparison.Ordinal)) ?? "";
                var gone = pl.Where(l => l.StartsWith("exception ", StringComparison.Ordinal)).Except(nl).Take(3).Select(l => l.Substring(10));
                var added = nl.Where(l => l.StartsWith("exception ", StringComparison.Ordinal)).Except(pl).Take(3).Select(l => l.Substring(10));
                static string OrNone(IEnumerable<string> l) => l.Any() ? string.Join(" | ", l) : "none";
                Diff("ledger", $"{m}: pinned {Line(pl, "verdict ")}, {Line(pl, "observations ")}, this run {Line(nl, "verdict ")}, {Line(nl, "observations ")}."
                    + $" Exceptions only pinned: {OrNone(gone)}. Only in this run: {OrNone(added)}");
            }
            Console.WriteLine($"  pinned ledger {path}: {(Diffs == before ? "every verdict and exception as pinned" : $"{Diffs - before} mapping(s) differ")}");
        }
    }

    // The unverified parts of M1 to M6 a live run can settle, as the
    // [MAPPING] lines name them (review F6).
    const string LM0Start = "M0 start: the HID class starts a plan profile's device, as the harness assumes";
    const string LM1Length = "M1 output length: a caller's buffer of L bytes reaches the driver as L bytes, L - 1 without report ids";
    const string LM1Caps = "M1 FeatureReportByteLength: the live value is the largest feature report's data bytes plus 1";
    const string LM1Passes = "M1 lengths the HID class passes: a GET_FEATURE the harness's driver answers succeeds live";
    const string LM1Byte0 = "M1 without report ids: the caller's byte 0 stays the id it wrote";
    const string LM2Length = "M2 read length: the caller's report is as long as the harness's read buffer";
    const string LM2Byte0 = "M2 without report ids: a ReadFile report starts with the id 0";
    const string LM3Passes = "M3 lengths the HID class passes: a write live succeeds";
    const string LM4Hid = "M4 through the HID class: a failure's error is RtlNtStatusToDosError of the driver's status";
    const string LM4Xusb = "M4 on the XUSB companion: a failure's error is RtlNtStatusToDosError of its status";
    const string LM6Feature = "M6 on a GET_FEATURE: the bytes past the driver's report are what the harness put in the buffer on arrival";
    const string LM6Read = "M6 on a READ_REPORT: the bytes past the driver's report are the zeros the harness's buffer held on arrival";
    const string LXusbIndex = "XUSB controller index 0: the companion's answers for index 0 are the live ones";
    const string LM1Refused = "M1 refusals: a GET_FEATURE the HID class may refuse, short or of an undeclared id, failed live with ERROR_INVALID_PARAMETER";
    const string LM1Undeclared = "M1 undeclared ids: with report ids, a GET_FEATURE of an id the descriptor does not declare fails live with nothing published";
    const string LForeign = "live only: no other program read or wrote the device while the probe recorded it";
    const string LM1NoFeature = "M1 no feature report: a GET_FEATURE of a collection that declares none fails live before the driver with ERROR_INVALID_FUNCTION";

    static readonly string[] Mappings =
        { LM0Start, LM1Length, LM1Caps, LM1Passes, LM1Byte0, LM1Refused, LM1Undeclared, LM1NoFeature, LForeign, LM2Length, LM2Byte0, LM3Passes, LM4Hid, LM4Xusb,
          LM6Feature, LM6Read, LXusbIndex };

    /// <summary>ERROR_INVALID_PARAMETER: what HidD_GetFeature gives when the
    /// HID class refuses the call before the driver (ClassMayRefuseFeature).</summary>
    const uint ErrorInvalidParameter = 87;

    /// <summary>ERROR_INVALID_FUNCTION: what HidD_GetFeature gave live on a
    /// collection that declares no feature report (LM1NoFeature).</summary>
    const uint ErrorInvalidFunction = 1;

    /// <summary>A failed GET_FEATURE whose error is neither the driver's own
    /// nor, where the HID class may refuse the call, the class's
    /// ERROR_INVALID_PARAMETER, is a [DIFF] (review finding 10). The ledger
    /// records which of the two a refusable call got.</summary>
    static void FeatureError(Report rep, string where, string at, PlanProfile p, byte id, int length, int bound, uint liveError,
                             Completion c, string ledgerAt)
    {
        bool refusable = p.ClassMayRefuseFeature(id, length, bound);
        uint driver = c.Ok ? uint.MaxValue : Win32Error(c.Status);
        if (refusable) rep.Mapping(LM1Refused, liveError == ErrorInvalidParameter, liveError == ErrorInvalidParameter ? null : $"{ledgerAt}: error {liveError}");
        else rep.Mapping(LM4Hid, liveError == driver, liveError == driver ? null : $"{ledgerAt}: live {liveError}, driver 0x{c.Status:X8}");
        if (liveError == driver || (refusable && liveError == ErrorInvalidParameter)) { rep.Same(); return; }
        rep.Diff(where, $"{at}: live failed with error {liveError}, " + (c.Ok
            ? $"the driver answered 0x{c.Status:X8}, and a refusal before it gives {ErrorInvalidParameter}"
            : $"the driver failed with 0x{c.Status:X8}, error {driver}{(refusable ? $", and a refusal before it gives {ErrorInvalidParameter}" : "")}"));
    }

    /// <summary>Holds a live byte run against the harness's: the driver's
    /// own bytes, then the bytes past them up to the shorter of the two
    /// buffers, which M1, M2 and M6 say are what the buffer held when the
    /// request arrived. A byte that differs in either part is a [DIFF]
    /// (review finding 10). A length that differs is a ledger observation
    /// and printed as unverified. lengthMapping and pastMapping name the
    /// ledger entries the run bears on, or are null where the recording
    /// fixes the length.</summary>
    static bool CompareBytes(Report rep, string where, string what, byte[] live, byte[] expected, int driverBytes,
                             string? lengthMapping, string? pastMapping)
    {
        int strict = Math.Min(driverBytes, Math.Min(live.Length, expected.Length));
        if (live.Length < driverBytes)
        {
            rep.Diff(where, $"{what} is {live.Length} bytes live, shorter than the {driverBytes} the driver returned");
            return false;
        }
        if (!live.AsSpan(0, strict).SequenceEqual(expected.AsSpan(0, strict)))
        {
            int at = 0;
            while (live[at] == expected[at]) at++;
            rep.Diff(where, $"{what} byte {at}: live {Hex(live.AsSpan(at, Math.Min(16, strict - at)))}, harness {Hex(expected.AsSpan(at, Math.Min(16, strict - at)))}");
            return false;
        }
        if (lengthMapping != null)
            rep.Mapping(lengthMapping, live.Length == expected.Length,
                live.Length == expected.Length ? null : $"{where} {what}: {live.Length} live, {expected.Length} assumed");
        if (live.Length != expected.Length)
            rep.Unverified(where, $"{what}: the caller got {live.Length} bytes, the harness's buffer is {expected.Length} (M1, M2: the length the HID class passes)",
                $"{live.Length} live");
        int common = Math.Min(live.Length, expected.Length);
        if (strict < common)
        {
            bool past = live.AsSpan(strict, common - strict).SequenceEqual(expected.AsSpan(strict, common - strict));
            if (pastMapping != null)
                rep.Mapping(pastMapping, past,
                    past ? null : $"{where} {what}: {Hex(live.AsSpan(strict, Math.Min(8, common - strict)))} live");
            if (!past)
            {
                int at = strict;
                while (live[at] == expected[at]) at++;
                rep.Diff(where, $"{what} byte {at}, past the driver's {driverBytes}: live {Hex(live.AsSpan(at, Math.Min(16, common - at)))}, "
                    + $"the buffer on arrival {Hex(expected.AsSpan(at, Math.Min(16, common - at)))} (M1, M2, M6)");
                return false;
            }
        }
        rep.Same();
        return true;
    }

    // ── Sony features: sony_feature_gate_check record ────────────────────

    sealed record FeatureCall(byte Id, int Length, bool Ok, uint Error, byte[] Buffer, byte[] Notify, byte[]? Other, byte[]? Overrun);

    sealed class FeatureGroup
    {
        public string Profile = "";
        public int Index;
        public string Head = "";
        public byte[] Declared = Array.Empty<byte>();
        public uint FeatureLength;
        public bool Attributed;
        public byte[] Unattributed = Array.Empty<byte>();
        public readonly List<(string Stream, List<FeatureCall> Calls)> Ids = new();
        // A start stream in place of setup: the devnode did not start, with
        // this problem code and problem status, and no call was made.
        public uint? StartProblem;
        public uint StartStatus;
    }

    static List<FeatureGroup> ParseFeatures(List<Stream> streams)
    {
        var groups = new List<FeatureGroup>();
        FeatureGroup? g = null;
        foreach (var s in streams)
        {
            var parts = s.Name.Split('/');
            if (parts.Length != 4 || parts[0] != "feature" || !parts[2].StartsWith("index"))
                throw new InvalidDataException($"live-s46-sony-features: unexpected stream {s.Name}");
            var c = new Cursor(s);
            string head = string.Join('/', parts.Take(3));
            if (parts[3] == "setup")
            {
                g = new FeatureGroup { Profile = parts[1], Index = int.Parse(parts[2].Substring(5)), Head = head };
                g.Declared = c.Take("declared");
                g.FeatureLength = c.U32("featureLength");
                g.Attributed = c.Take("attribution")[0] == 1;
                groups.Add(g);
                continue;
            }
            if (parts[3] == "start")
            {
                g = new FeatureGroup { Profile = parts[1], Index = int.Parse(parts[2].Substring(5)), Head = head };
                g.StartProblem = c.U32("problem");
                g.StartStatus = c.U32("status");
                groups.Add(g);
                // Nothing else belongs to a group whose device did not start.
                g = null;
                continue;
            }
            if (g == null || g.Head != head) throw new InvalidDataException($"live-s46-sony-features: {s.Name} has no setup stream ahead of it");
            if (parts[3] == "end") { g.Unattributed = c.Take("unattributed"); continue; }
            byte id = Convert.ToByte(parts[3].Substring(2), 16);
            var calls = new List<FeatureCall>();
            while (!c.End)
            {
                bool ok = c.Take("ok")[0] == 1;
                uint err = c.U32("error");
                byte[] buf = c.Take("buffer");
                byte[] notify = c.Take("notify");
                calls.Add(new FeatureCall(id, buf.Length, ok, err, buf, notify, c.TakeIf("other"), c.TakeIf("overrun")));
            }
            g.Ids.Add((s.Name, calls));
        }
        return groups;
    }

    /// <summary>M1: the request a feature call becomes, or null when it is
    /// not driven.</summary>
    static (byte[] Input, int Length, byte[] Init)? FeatureRequest(PlanProfile p, byte id, int len)
    {
        if (len == 0 || !p.DeclaresFeature) return null;
        if (p.UsesReportIds) { var init = new byte[len]; init[0] = id; return (new[] { id }, len, init); }
        return (new byte[] { 0 }, len - 1, new byte[len - 1]);
    }

    static byte[] FeatureSeen(PlanProfile p, byte id, Completion c)
    {
        if (p.UsesReportIds) return c.Buffer;
        var b = new byte[c.Buffer.Length + 1];
        b[0] = id;
        c.Buffer.CopyTo(b, 1);
        return b;
    }

    /// <summary>The groups the harness drives: those of a plan profile, the
    /// first of any that repeat a profile and index. CheckFeatureMatrix
    /// reports the rest.</summary>
    static IEnumerable<FeatureGroup> DrivenGroups(List<FeatureGroup> groups, Dictionary<string, PlanProfile> plan) =>
        groups.Where(g => plan.ContainsKey(g.Profile) && g.StartProblem == null).GroupBy(g => g.Head, StringComparer.Ordinal).Select(x => x.First());

    /// <summary>The plan profile an S46 group runs on under the contract.</summary>
    static string ContractName(FeatureGroup g) => $"{g.Profile}#idx{g.Index}";

    /// <summary>The kept plan, and for each S46 group a profile of its
    /// own: the instance configuration the tree's SDK writes for the
    /// catalog profile, with the FeatureAnswers it writes for the group's
    /// index (DeviceOrchestrator.FeatureAnswersBlob).</summary>
    static string ContractPlan(string keptPlan, Live live, Dictionary<string, PlanProfile> plan, string work)
    {
        var catalog = HIDMaestro.Internal.ProfileDatabase.LoadEmbedded().All;
        var groups = DrivenGroups(live.Features, plan).ToList();
        var profiles = groups.Select(g => catalog.First(p => p.Id == g.Profile)).DistinctBy(p => p.Id).ToList();
        var config = Case5Driver.InstanceConfig(profiles, work);
        var sb = new StringBuilder(keptPlan.EndsWith('\n') ? keptPlan : keptPlan + "\n");
        foreach (var g in groups)
        {
            var p = catalog.First(x => x.Id == g.Profile);
            var values = config[p.Id].Where(v => v.Name != "FeatureAnswers")
                .Append(new Case5Driver.RegValue("FeatureAnswers", 3, HIDMaestro.Internal.DeviceOrchestrator.FeatureAnswersBlob(p, g.Index))).ToList();
            Case5Driver.AddProfile(sb, plan[g.Profile].Group, p, values, ContractName(g));
        }
        // Each XUSB profile's companion as the tree's SDK configures it under
        // the contract, which its stream's version line selects. 9bd6279's
        // companion reads the same IDs and ignores the rest.
        foreach (var (id, _) in live.Xusb)
            XusbContract.AppendContractProfile(sb, keptPlan, XusbContractName(id), catalog.First(x => x.Id == id), 0);
        return sb.ToString();
    }

    static void ScriptFeatures(StringBuilder sc, List<FeatureGroup> groups, Dictionary<string, PlanProfile> plan)
    {
        foreach (var g in DrivenGroups(groups, plan))
        {
            var p = Profile(plan, g.Profile);
            sc.Append($"stream live/s46/{g.Profile}/index{g.Index}\nversion tag-own\ndevice {ContractName(g)} {g.Index} driver\n");
            int k = 0;
            foreach (var (_, calls) in g.Ids)
                foreach (var call in calls)
                {
                    sc.Append($"call {k++}\n");
                    if (FeatureRequest(p, call.Id, call.Length) is var (input, len, init))
                        sc.Append($"getfeature {Hex(input)} {len} {Hex(init)}\n");
                }
        }
    }

    // The S46 recorder's matrix (sony_feature_gate_check Program.cs
    // SonyBlockIds and RecordLengths): every declared feature id and these
    // seven, at every length from 0 to FeatureReportByteLength plus one and
    // at least to 65, on every plan profile of the sony and control groups.
    static readonly byte[] SonyBlockIds = { 0x02, 0x05, 0x09, 0x12, 0x20, 0x22, 0xA3 };
    const int FeatureMinLength = 65;

    /// <summary>The longest buffer the S46 recorder sweeps a profile to: one
    /// past the larger of the descriptor's and the HID class's
    /// FeatureReportByteLength, so every profile has calls at and above the
    /// F10 bound (review finding 9), and never below 65, the length hidapi
    /// and SDL read Sony reports into. sony_feature_gate_check's
    /// RecordLengths computes the same.</summary>
    static int FeatureSweepMax(int descriptorLength, int liveLength) =>
        Math.Max(FeatureMinLength, Math.Max(descriptorLength, liveLength) + 1);

    static IEnumerable<PlanProfile> FeatureSubjects(Dictionary<string, PlanProfile> plan) =>
        plan.Values.Where(p => p.Group is "sony" or "control").OrderBy(p => p.Id, StringComparer.Ordinal);

    static IEnumerable<byte> FeatureIds(byte[] declared) => declared.Concat(SonyBlockIds).Distinct().OrderBy(b => b);

    /// <summary>Per plan profile, the recording must hold it once, with
    /// every id of its matrix in order and every length of its sweep in
    /// order (note N5): a missing or repeated profile, id or length is a
    /// [DIFF] naming the profile, whatever the total.</summary>
    static void CheckFeatureMatrix(Report rep, string Case, List<FeatureGroup> groups, List<PlanProfile> subjects)
    {
        foreach (var p in subjects)
        {
            var mine = groups.Where(g => g.Profile == p.Id).ToList();
            if (mine.Count != 1)
            {
                rep.Diff($"{Case}/feature/{p.Id}", mine.Count == 0 ? "the recording holds no group of this profile" : $"the recording holds {mine.Count} groups of this profile");
                continue;
            }
            var g = mine[0];
            // M0: the harness assumes the HID class starts every plan
            // profile's device. It runs no descriptor parse, so a device the
            // class did not start has no calls to compare. Its problem code
            // and status go to the ledger, which holds a later run to them.
            if (g.StartProblem is uint problem)
            {
                string seen = $"{p.Id}: problem {problem}, status 0x{g.StartStatus:X8}";
                rep.Mapping(LM0Start, false, seen);
                if (problem == 0) rep.Diff($"{Case}/{g.Head}/start", "a start failure recorded with no problem code");
                else rep.Unverified($"{Case}/{g.Head}/start", "the device did not start, so no call was made (M0)", seen);
                continue;
            }
            rep.Mapping(LM0Start, true);
            var want = FeatureIds(p.Features).ToList();
            var got = g.Ids.Select(x => x.Calls.Count > 0 ? x.Calls[0].Id : (byte)0).ToList();
            if (!want.SequenceEqual(got) || g.Ids.Any(x => x.Calls.Any(c => c.Id != x.Calls[0].Id)))
                rep.Diff($"{Case}/{g.Head}", $"ids {Hex(got.ToArray())} recorded, the matrix is {Hex(want.ToArray())}");
            int max = FeatureSweepMax(p.FeatureLength, (int)g.FeatureLength);
            foreach (var (name, calls) in g.Ids)
            {
                bool lengthsOk = calls.Count == max + 1 && calls.Select((c, i) => c.Length == i).All(x => x);
                if (!lengthsOk)
                    rep.Diff($"{Case}/{name}", $"{calls.Count} calls at lengths {(calls.Count == 0 ? "none" : $"{calls[0].Length} to {calls[^1].Length}")}, the sweep is every length 0 to {max}");
            }
        }
        foreach (var g in groups.Where(g => !subjects.Any(p => p.Id == g.Profile)))
            rep.Diff($"{Case}/{g.Head}", $"{g.Profile} is not a profile of the matrix");
    }

    static void CompareFeatures(Report rep, List<FeatureGroup> groups, Dictionary<string, PlanProfile> plan, Dictionary<string, Stream> harness)
    {
        const string Case = "live-s46-sony-features";
        var subjects = FeatureSubjects(plan).ToList();
        int Expected(PlanProfile p)
        {
            var mine = groups.Where(g => g.Profile == p.Id).ToList();
            // A device that did not start gets no request from the probe.
            if (mine.Count == 1 && mine[0].StartProblem != null) return 0;
            int live = mine.Count == 1 ? (int)mine[0].FeatureLength : 0;
            return FeatureIds(p.Features).Count() * (FeatureSweepMax(p.FeatureLength, live) + 1);
        }
        rep.Begin(Case, subjects.Sum(Expected),
            groups.Sum(g => g.Ids.Sum(x => x.Calls.Count)),
            $"sony_feature_gate_check Record: {subjects.Count} profiles, each declared feature id and the seven Sony ids at lengths 0 to FeatureReportByteLength plus one, at least {FeatureMinLength}");
        CheckFeatureMatrix(rep, Case, groups, subjects);
        foreach (var g in DrivenGroups(groups, plan))
        {
            var p = Profile(plan, g.Profile);
            var h = new Calls(harness[$"live/s46/{g.Profile}/index{g.Index}"]);
            string group = $"{Case}/{g.Head}";
            string setup = group + "/setup";
            // The recorder lists the descriptor's feature reports in its
            // order (HidReportMap), and the plan's features line sorts them.
            if (!g.Declared.SequenceEqual(p.Declared))
                rep.Diff(setup, $"declared {Hex(g.Declared)} live, the plan's descriptor declares {Hex(p.Declared)}");
            rep.Mapping(LM1Caps, g.FeatureLength == p.FeatureLength,
                g.FeatureLength == p.FeatureLength ? null : $"{g.Profile}: {g.FeatureLength} live, {p.FeatureLength} from the descriptor");
            if (!p.UsesReportIds)
                rep.Unverified(setup, "no report ids: the driver gets input [0] and the L - 1 bytes from buffer[1] (M1)", $"FeatureReportByteLength {g.FeatureLength}");
            if (!g.Attributed)
                rep.Unverified(setup, "notifications attributed by a fixed window, not a fence", g.Profile);
            // F10: from this length a refusal the driver did not make is a
            // difference. The live FeatureReportByteLength, as recorded.
            int bound = (int)g.FeatureLength;
            var liveNotified = new List<byte>();
            var harnessNotified = new List<byte>();
            int k = 0;
            foreach (var (name, calls) in g.Ids)
            {
                string where = $"{Case}/{name}";
                foreach (var recorded in calls)
                {
                    rep.Call();
                    var outs = h.Of(k++);
                    string at = $"length {recorded.Length}";
                    string idAt = $"id{recorded.Id:X2} {at}";
                    // With report ids the driver publishes the id the HID
                    // class passes in the input buffer, the caller's
                    // (driver.c:3066-3073, 3368), so in a group the fence
                    // attributes, a notification of another id is another
                    // program's feature read.
                    var call = recorded;
                    if (g.Attributed && p.UsesReportIds && recorded.Notify.Any(n => n != recorded.Id))
                    {
                        byte[] foreign = recorded.Notify.Where(n => n != recorded.Id).ToArray();
                        rep.LiveOnly(where, $"{at}: notify {Hex(foreign)}, a feature read another program made", $"{g.Profile} {idAt}: notify {Hex(foreign)}");
                        call = recorded with { Notify = recorded.Notify.Where(n => n == recorded.Id).ToArray() };
                    }
                    else if (g.Attributed && p.UsesReportIds) rep.Mapping(LForeign, true);
                    if (call.Overrun != null) rep.Unverified(group, "bytes past the caller's length changed (M1: the output length)", idAt);
                    if (call.Length > 0 && !p.DeclaresFeature)
                    {
                        // M1: the HID class keeps it from the driver, so it
                        // fails with nothing published. A call that succeeded
                        // or published reached the driver after all.
                        if (call.Ok || call.Notify.Length > 0)
                            rep.Diff(where, $"{at}: ok {(call.Ok ? 1 : 0)} notify {Hex(call.Notify)} live, on a collection that declares no feature report");
                        else
                        {
                            rep.Mapping(LM1NoFeature, call.Error == ErrorInvalidFunction, call.Error == ErrorInvalidFunction ? null : $"{g.Profile} {idAt}: error {call.Error}");
                            rep.Unverified(group, "a collection with no feature report, not driven (M1)", idAt);
                            rep.Same();
                        }
                        continue;
                    }
                    if (FeatureRequest(p, call.Id, call.Length) == null)
                    {
                        if (call.Ok || call.Notify.Length > 0)
                            rep.Unverified(group, "a zero-length call reached the driver (M1)", $"{idAt} ok {(call.Ok ? 1 : 0)} notify {Hex(call.Notify)}");
                        else rep.Unverified(group, "zero length, not driven (M1)", $"id{call.Id:X2}");
                        continue;
                    }
                    var c = Completions(outs, "getfeature").Single();
                    var ring = Ring(outs).ToList();
                    byte[] notify = ring.Where(r => r[0] == 3).Select(r => r[1]).ToArray();
                    byte[] other = ring.Where(r => r[0] != 3).SelectMany(r => new[] { r[0], r[1] }).ToArray();
                    if (c.Ok) rep.Mapping(LM1Passes, call.Ok || call.Notify.Length > 0, call.Ok ? null : $"{g.Profile} {idAt} error {call.Error}");
                    liveNotified.AddRange(call.Notify);
                    harnessNotified.AddRange(notify);
                    bool undeclared = p.UsesReportIds && Array.IndexOf(p.Features, call.Id) < 0;
                    if (undeclared && call.Length >= bound)
                        rep.Mapping(LM1Undeclared, !call.Ok && call.Notify.Length == 0, !call.Ok && call.Notify.Length == 0 ? null : $"{g.Profile} {idAt}: ok {(call.Ok ? 1 : 0)} notify {Hex(call.Notify)}");
                    if (call.Ok && c.Ok)
                    {
                        int driverBytes = (int)c.Info + (p.UsesReportIds ? 0 : 1);
                        if (!p.UsesReportIds && call.Buffer.Length > 0)
                            rep.Mapping(LM1Byte0, call.Buffer[0] == call.Id, call.Buffer[0] == call.Id ? null : $"{g.Profile} {idAt}: {call.Buffer[0]:X2}");
                        CompareBytes(rep, where, $"{at} buffer", call.Buffer, FeatureSeen(p, call.Id, c), driverBytes, LM1Length, LM6Feature);
                        if (!call.Notify.SequenceEqual(notify))
                        {
                            // A fixed window can hand a late notification to
                            // the next call, so a window-attributed group is
                            // held to its whole sequence below.
                            if (g.Attributed) rep.Diff(where, $"{at} notify {Hex(call.Notify)} live, {Hex(notify)} from the harness");
                            else rep.Unverified(group, "notifications by window land on another call", idAt);
                        }
                        if (!(call.Other ?? Array.Empty<byte>()).SequenceEqual(other))
                            rep.Diff(where, $"{at} other packets {Hex(call.Other ?? Array.Empty<byte>())} live, {Hex(other)} from the harness");
                    }
                    else if (call.Ok)
                        rep.Diff(where, $"{at}: live succeeded, the driver failed with 0x{c.Status:X8}");
                    else if (c.Ok)
                    {
                        // F10: a buffer of FeatureReportByteLength or more
                        // with a declared id, or with no report ids, reaches
                        // the driver, so a refusal there is the driver's.
                        // Below the bound, or for an undeclared id, the HID
                        // class may refuse it first, with
                        // ERROR_INVALID_PARAMETER and nothing published.
                        if (call.Notify.Length != 0)
                            rep.Diff(where, $"{at}: live failed with error {call.Error} after the driver published {Hex(call.Notify)}");
                        else if (!p.ClassMayRefuseFeature(call.Id, call.Length, bound))
                            rep.Diff(where, $"{at}: live failed with error {call.Error} and nothing published, the driver answered 0x{c.Status:X8}, at or above FeatureReportByteLength {g.FeatureLength} with a declared id");
                        else
                        {
                            rep.Unverified(group, $"refused before the driver, {(undeclared ? "an undeclared id" : $"below FeatureReportByteLength {g.FeatureLength}")} (M1: what the HID class passes)", idAt);
                            FeatureError(rep, where, at, p, call.Id, call.Length, bound, call.Error, c, $"{g.Profile} {idAt}");
                        }
                    }
                    else
                    {
                        if (call.Notify.Length != 0 || notify.Length != 0)
                            rep.Diff(where, $"{at}: both failed, notify {Hex(call.Notify)} live, {Hex(notify)} from the harness");
                        else FeatureError(rep, where, at, p, call.Id, call.Length, bound, call.Error, c, $"{g.Profile} {idAt}");
                    }
                }
            }
            if (g.Attributed && g.Unattributed.Length > 0)
                rep.Diff($"{Case}/{g.Head}/end", $"notifications no call claimed: {Hex(g.Unattributed)}");
            // Attributed by a window: each notification may be counted
            // against a neighbor, but the group's whole sequence, with what
            // came after the last window, is the driver's (review finding 10).
            if (!g.Attributed)
            {
                liveNotified.AddRange(g.Unattributed);
                if (!liveNotified.SequenceEqual(harnessNotified))
                    rep.Diff($"{Case}/{g.Head}/end", $"the group's notifications in order: {Hex(liveNotified.ToArray())} live, {Hex(harnessNotified.ToArray())} from the harness");
            }
        }
    }

    // ── Switch Pro: switch_pro_check record ──────────────────────────────

    sealed record SwitchWrite(byte[] Bytes, bool Ok, uint Error, List<byte[]> Skipped, byte[]? Reply);

    sealed class SwitchRun
    {
        public int Index;
        public string Head = "";
        public byte[] Body = Array.Empty<byte>();
        public readonly List<byte[]> Pre = new();
        public readonly List<SwitchWrite> Writes = new();
        public readonly List<byte[]> Stream = new();
    }

    static List<byte[]> Runs(Cursor c, string frame, string repeat)
    {
        var l = new List<byte[]>();
        while (c.PeekTag == frame) { l.Add(c.Take(frame)); c.Take(repeat); }
        return l;
    }

    static List<SwitchRun> ParseSwitch(List<Stream> streams)
    {
        var runs = new List<SwitchRun>();
        SwitchRun? r = null;
        foreach (var s in streams)
        {
            var parts = s.Name.Split('/');
            if (parts.Length != 3 || parts[0] != "switch-pro" || !parts[1].StartsWith("index"))
                throw new InvalidDataException($"live-switch-pro: unexpected stream {s.Name}");
            var c = new Cursor(s);
            switch (parts[2])
            {
                case "state":
                    r = new SwitchRun { Index = int.Parse(parts[1].Substring(5)), Head = $"switch-pro/{parts[1]}" };
                    r.Body = c.Take("body");
                    runs.Add(r);
                    break;
                case "pre-handshake":
                    r!.Pre.AddRange(Runs(c, "frame", "repeat"));
                    break;
                case "init":
                    while (!c.End)
                    {
                        byte[] bytes = c.Take("write");
                        bool ok = c.Take("writeOk")[0] == 1;
                        uint err = c.U32("writeError");
                        c.Take("written");
                        var skipped = Runs(c, "skipped", "skippedRepeat");
                        byte[]? reply = c.TakeIf("reply");
                        if (reply != null) c.Take("timer"); else c.Take("noReply");
                        r!.Writes.Add(new SwitchWrite(bytes, ok, err, skipped, reply));
                    }
                    break;
                case "stream":
                    r!.Stream.AddRange(Runs(c, "frame", "repeat"));
                    c.Take("timerSteps");
                    break;
                default: throw new InvalidDataException($"live-switch-pro: unexpected stream {s.Name}");
            }
        }
        return runs;
    }

    const int SwitchFinal = 100000;

    /// <summary>A write the harness's driver gets: one that succeeded live,
    /// and one that failed live but surely reached the driver
    /// (WriteReachesDriver), so a live refusal there is held against the
    /// driver's answer (review finding 10).</summary>
    static bool SwitchDriven(PlanProfile p, SwitchWrite w) => w.Ok || p.WriteReachesDriver(w.Bytes, w.Bytes.Length);

    static void ScriptSwitch(StringBuilder sc, List<SwitchRun> runs, PlanProfile p)
    {
        foreach (var r in runs)
        {
            sc.Append($"stream live/switch/index{r.Index}\ndevice {p.Id} {r.Index} driver\n");
            sc.Append($"frame {Hex(r.Body)} -\ncall 0\nread {p.ReadLength}\ntick\n");
            for (int i = 0; i < r.Writes.Count; i++)
            {
                sc.Append($"call {i + 1}\n");
                if (SwitchDriven(p, r.Writes[i])) sc.Append($"write {Hex(p.WriteReport(r.Writes[i].Bytes))}\nread {p.ReadLength}\ntick\n");
            }
            sc.Append($"call {SwitchFinal}\nread {p.ReadLength}\ntick\n");
        }
    }

    /// <summary>A read's whole buffer with the timer zeroed where the
    /// recorder zeroes it (0x21 and 0x30).</summary>
    static byte[] SwitchMasked(Completion c)
    {
        var b = (byte[])c.Buffer.Clone();
        if (b.Length > 1 && (b[0] == 0x21 || b[0] == 0x30)) b[1] = 0;
        return b;
    }

    static bool IsReply(Completion c) => c.Buffer.Length > 0 && c.Info > 0 && (c.Buffer[0] == 0x21 || c.Buffer[0] == 0x81);

    // switch_pro_check Record: the 0x80 0x02 USB handshake and ten Bluetooth
    // subcommands, with at least one frame read before the handshake and one
    // in the stream (it throws when the interface or a reply is missing).
    const int SwitchWrites = 11;

    static void CompareSwitch(Report rep, List<SwitchRun> runs, PlanProfile p, Dictionary<string, Stream> harness)
    {
        const string Case = "live-switch-pro";
        rep.Begin(Case, SwitchWrites + 2,
            runs.Sum(r => r.Writes.Count + r.Writes.Sum(w => w.Skipped.Count) + r.Pre.Count + r.Stream.Count),
            $"switch_pro_check Record: {SwitchWrites} writes, at least one frame before the handshake and one in the stream");
        foreach (var r in runs)
        {
            var h = new Calls(harness[$"live/switch/index{r.Index}"]);
            if (r.Writes.Count != SwitchWrites) rep.Diff($"{Case}/{r.Head}/init", $"the recording holds {r.Writes.Count} writes, its probe makes {SwitchWrites}");
            if (r.Pre.Count == 0) rep.Diff($"{Case}/{r.Head}/pre-handshake", "the recording holds no frame");
            if (r.Stream.Count == 0) rep.Diff($"{Case}/{r.Head}/stream", "the recording holds no frame");
            Completion? FrameOf(int k) => Completions(h.Of(k), "read").Where(c => c.Ok && !IsReply(c)).Select(c => (Completion?)c).LastOrDefault();

            // The driver queues a reply, 0x81 or 0x21, only in answer to
            // an output report (driver.c SwitchQueueReply, called from
            // SwitchHandleProprietary and SwitchHandleSubcommand on
            // WRITE_REPORT and SET_OUTPUT_REPORT, 2906-2911, 3510-3515), so
            // one read before the probe's first write answers another
            // program's write. That write moved the driver's state, so the
            // frames from it on are not held to the harness, which saw only
            // the probe's.
            var pre = FrameOf(0);
            bool foreign = false;
            foreach (var f in r.Pre)
            {
                rep.Call();
                if (!foreign && f.Length > 0 && (f[0] == 0x21 || f[0] == 0x81))
                {
                    foreign = true;
                    rep.LiveOnly($"{Case}/{r.Head}/pre-handshake", $"frame {Hex(f.AsSpan(0, Math.Min(16, f.Length)))} and the frames after it, answers to another program's write",
                        $"switch-pro pre-handshake: {Hex(f.AsSpan(0, Math.Min(16, f.Length)))}");
                }
                if (foreign) continue;
                rep.Mapping(LForeign, true);
                CompareFrame(rep, $"{Case}/{r.Head}/pre-handshake", "frame", f, pre);
            }

            Completion? previous = pre;
            for (int i = 0; i < r.Writes.Count; i++)
            {
                var w = r.Writes[i];
                rep.Call();
                var outs = h.Of(i + 1);
                string where = $"{Case}/{r.Head}/init";
                string at = $"write {i} ({Hex(w.Bytes.AsSpan(0, Math.Min(11, w.Bytes.Length)))}...)";
                rep.Unverified(where, "written is the HID class's count, not compared (M5)", $"write {i}");
                rep.Mapping(LM3Passes, w.Ok, w.Ok ? null : $"switch-pro WriteFile length {w.Bytes.Length} error {w.Error}");
                if (!SwitchDriven(p, w))
                {
                    rep.Unverified(where, "a write the HID class refused before the driver, not driven (M3)", $"{at} error {w.Error}");
                    continue;
                }
                var wc = Completions(outs, "write").Single();
                if (!w.Ok)
                {
                    // At OutputReportByteLength with a declared output id the
                    // write reaches the driver (WriteReachesDriver).
                    if (wc.Ok) rep.Diff(where, $"{at}: live failed with error {w.Error}, the driver accepted it, and the write reaches the driver at OutputReportByteLength {p.OutputLength}");
                    else if (w.Error != Win32Error(wc.Status)) rep.Diff(where, $"{at}: both failed, error {w.Error} live, the driver's 0x{wc.Status:X8} is error {Win32Error(wc.Status)}");
                    else rep.Same();
                    continue;
                }
                if (!wc.Ok) { rep.Diff(where, $"{at}: live wrote it, the driver failed with 0x{wc.Status:X8}"); continue; }
                var reply = Completions(outs, "read").Where(IsReply).Select(c => (Completion?)c).FirstOrDefault();
                var now = FrameOf(i + 1) ?? previous;
                if (w.Reply != null)
                {
                    if (reply == null) rep.Diff(where, $"{at}: a live reply {Hex(w.Reply.AsSpan(0, Math.Min(16, w.Reply.Length)))}, none from the harness");
                    else CompareBytes(rep, where, $"{at} reply", w.Reply, SwitchMasked(reply.Value), (int)reply.Value.Info, LM2Length, LM6Read);
                }
                else if (reply != null)
                    rep.Diff(where, $"{at}: no live reply, the harness replied {Hex(reply.Value.Reported.AsSpan(0, Math.Min(16, (int)reply.Value.Info)))}");
                foreach (var sk in w.Skipped)
                {
                    rep.Call();
                    bool prev = previous is Completion p0 && sk.AsSpan().SequenceEqual(SwitchMasked(p0));
                    if (!prev) CompareFrame(rep, where, $"{at} skipped", sk, now);
                    else rep.Same();
                }
                previous = now;
            }
            var final = FrameOf(SwitchFinal);
            foreach (var f in r.Stream) { rep.Call(); CompareFrame(rep, $"{Case}/{r.Head}/stream", "frame", f, final); }
        }
    }

    static void CompareFrame(Report rep, string where, string what, byte[] live, Completion? harness)
    {
        if (harness is not Completion c) { rep.Diff(where, $"{what} {Hex(live.AsSpan(0, Math.Min(16, live.Length)))} live, the harness streamed no frame"); return; }
        CompareBytes(rep, where, what, live, SwitchMasked(c), (int)c.Info, LM2Length, LM6Read);
    }

    // ── DualShock 3: ds3_sixaxis_check record ────────────────────────────

    sealed record Ds3Feature(byte Id, int Length, bool Ok, uint Error, byte[] Buffer);
    sealed record Ds3State(string Stream, byte[] Native, List<byte[]> Inputs, List<Ds3Feature> Features);
    sealed record Ds3Write(int Length, byte[] Bytes, bool Ok, uint Error, List<byte[]> Packets);

    sealed class Ds3Run
    {
        public int Index;
        public string Head = "";
        public readonly List<Ds3State> States = new();
        public readonly List<(string Stream, bool SetOutput, List<Ds3Write> Writes)> Outputs = new();
    }

    static List<Ds3Run> ParseDs3(List<Stream> streams)
    {
        var runs = new Dictionary<string, Ds3Run>(StringComparer.Ordinal);
        foreach (var s in streams)
        {
            var parts = s.Name.Split('/');
            if (parts.Length < 5 || parts[0] != "ds3" || !parts[2].StartsWith("index"))
                throw new InvalidDataException($"live-ds3-sixaxis: unexpected stream {s.Name}");
            string head = string.Join('/', parts.Take(3));
            if (!runs.TryGetValue(head, out var r))
                runs[head] = r = new Ds3Run { Index = int.Parse(parts[2].Substring(5)), Head = head };
            var c = new Cursor(s);
            if (parts[3] == "state")
            {
                byte[] native = c.Take("native");
                var inputs = Runs(c, "input", "repeat");
                var feats = new List<Ds3Feature>();
                while (!c.End)
                {
                    byte[] req = c.Take("request");
                    bool ok = c.Take("ok")[0] == 1;
                    uint err = c.U32("error");
                    byte[] buf = c.Take("buffer");
                    feats.Add(new Ds3Feature(req[0], req[1] | (req[2] << 8), ok, err, buf));
                }
                r.States.Add(new Ds3State(s.Name, native, inputs, feats));
            }
            else if (parts[3] == "output" && parts.Length == 6)
            {
                var writes = new List<Ds3Write>();
                while (!c.End)
                {
                    byte[] bytes = c.Take("write");
                    bool ok = c.Take("ok")[0] == 1;
                    uint err = c.U32("error");
                    uint n = c.U32("packets");
                    var packets = new List<byte[]>();
                    for (uint i = 0; i < n; i++) packets.Add(c.Take("output"));
                    writes.Add(new Ds3Write(bytes.Length, bytes, ok, err, packets));
                }
                r.Outputs.Add((s.Name, parts[4] == "HidD_SetOutputReport", writes));
            }
            else throw new InvalidDataException($"live-ds3-sixaxis: unexpected stream {s.Name}");
        }
        return runs.Values.ToList();
    }

    /// <summary>A write the harness's driver gets: one that succeeded or
    /// raised a packet live, and one that failed with none but surely
    /// reached the driver (WriteReachesDriver: OutputReportByteLength or
    /// more), so a live refusal there is held against the driver's answer
    /// (review finding 10).</summary>
    static bool Ds3Driven(PlanProfile p, Ds3Write w) =>
        w.Length >= 1 && (w.Ok || w.Packets.Count > 0 || p.WriteReachesDriver(w.Bytes, w.Length));

    static void ScriptDs3(StringBuilder sc, List<Ds3Run> runs, PlanProfile p)
    {
        foreach (var r in runs)
        {
            sc.Append($"stream live/ds3/index{r.Index}\ndevice {p.Id} {r.Index} driver\n");
            int k = 0;
            foreach (var s in r.States)
            {
                sc.Append($"call {k++}\nread {p.ReadLength}\nframe - {Hex(s.Native)}\n");
                foreach (var f in s.Features)
                {
                    sc.Append($"call {k++}\n");
                    if (FeatureRequest(p, f.Id, f.Length) is var (input, len, init))
                        sc.Append($"getfeature {Hex(input)} {len} {Hex(init)}\n");
                }
            }
            foreach (var (_, setOutput, writes) in r.Outputs)
                foreach (var w in writes)
                {
                    sc.Append($"call {k++}\n");
                    if (!Ds3Driven(p, w)) continue;
                    // M3: the report's length of the caller's bytes, without
                    // the 0 without report ids.
                    sc.Append($"{(setOutput ? "setoutput" : "write")} {Hex(p.WriteReport(w.Bytes))}\n");
                }
        }
    }

    static byte[] Ds3Packet(byte[] ring)
    {
        int size = ring[2] | (ring[3] << 8);
        var b = new byte[2 + size];
        b[0] = ring[0];
        b[1] = ring[1];
        Array.Copy(ring, 4, b, 2, size);
        return b;
    }

    // ds3_sixaxis_check Record: seven states (RecordStates), each read until
    // a 300 ms gap (at least one input report, or it throws) and then three
    // feature reads, 0xF2 into 64, 0x00 into 64 and 0xF2 into 255. Then
    // WriteFile and HidD_SetOutputReport of five commands (RecordCommands)
    // at seventeen lengths (RecordOutputLengths).
    const int Ds3States = 7, Ds3Features = 3, Ds3Commands = 5, Ds3Lengths = 17;

    static void CompareDs3(Report rep, List<Ds3Run> runs, PlanProfile p, Dictionary<string, Stream> harness)
    {
        const string Case = "live-ds3-sixaxis";
        rep.Begin(Case, Ds3States * (1 + Ds3Features) + 2 * Ds3Commands * Ds3Lengths,
            runs.Sum(r => r.States.Sum(s => s.Inputs.Count + s.Features.Count) + r.Outputs.Sum(o => o.Writes.Count)),
            $"ds3_sixaxis_check Record: {Ds3States} states of at least one input and {Ds3Features} feature reads, 2 APIs x {Ds3Commands} commands x {Ds3Lengths} lengths");
        // F10: the collection's FeatureReportByteLength from the plan's
        // descriptor, as the DS3 recording does not hold the live value.
        int bound = p.RefusalBound;
        foreach (var r in runs)
        {
            var h = new Calls(harness[$"live/ds3/index{r.Index}"]);
            if (r.States.Count != Ds3States) rep.Diff($"{Case}/{r.Head}", $"the recording holds {r.States.Count} states, its probe makes {Ds3States}");
            if (r.Outputs.Count != 2 * Ds3Commands) rep.Diff($"{Case}/{r.Head}", $"the recording holds {r.Outputs.Count} output streams, its probe makes {2 * Ds3Commands}");
            int k = 0;
            foreach (var s in r.States)
            {
                string where = $"{Case}/{s.Stream}";
                if (s.Inputs.Count == 0) rep.Diff(where, "the recording holds no input report");
                if (s.Features.Count != Ds3Features) rep.Diff(where, $"the recording holds {s.Features.Count} feature reads, its probe makes {Ds3Features}");
                var read = Completions(h.Of(k++), "read").Select(c => (Completion?)c).FirstOrDefault();
                foreach (var input in s.Inputs)
                {
                    rep.Call();
                    if (read is not Completion rc || !rc.Ok) { rep.Diff(where, $"input {Hex(input)} live, the harness completed no read"); continue; }
                    // M2: the report id 0 in front without report ids.
                    byte[] seen = p.UsesReportIds ? rc.Buffer : new byte[] { 0 }.Concat(rc.Buffer).ToArray();
                    if (!p.UsesReportIds && input.Length > 0)
                    {
                        rep.Mapping(LM2Byte0, input[0] == 0, input[0] == 0 ? null : $"{s.Stream}: {input[0]:X2}");
                        if (input[0] != 0) rep.Unverified(where, "the input report's first byte is not the id 0 (M2)", Hex(input.AsSpan(0, 1)));
                    }
                    if (!p.UsesReportIds) rep.Unverified(Case, "ReadFile without report ids returns the id 0 then the driver's report (M2)", s.Stream.Split('/')[^1]);
                    CompareBytes(rep, where, "input", input, seen, (int)rc.Info + (p.UsesReportIds ? 0 : 1), LM2Length, LM6Read);
                }
                foreach (var f in s.Features)
                {
                    rep.Call();
                    var outs = h.Of(k++);
                    string at = $"feature 0x{f.Id:X2} length {f.Length}";
                    if (FeatureRequest(p, f.Id, f.Length) == null) { rep.Unverified(where, "zero length, not driven (M1)", at); continue; }
                    var c = Completions(outs, "getfeature").Single();
                    if (c.Ok) rep.Mapping(LM1Passes, f.Ok, f.Ok ? null : $"{p.Id} {at} error {f.Error}");
                    if (f.Ok && c.Ok)
                    {
                        if (!p.UsesReportIds && f.Buffer.Length > 0)
                            rep.Mapping(LM1Byte0, f.Buffer[0] == f.Id, f.Buffer[0] == f.Id ? null : $"{p.Id} {at}: {f.Buffer[0]:X2}");
                        CompareBytes(rep, where, at, f.Buffer, FeatureSeen(p, f.Id, c), (int)c.Info + (p.UsesReportIds ? 0 : 1), LM1Length, LM6Feature);
                    }
                    else if (f.Ok) rep.Diff(where, $"{at}: live succeeded, the driver failed with 0x{c.Status:X8}");
                    else if (c.Ok && !p.ClassMayRefuseFeature(f.Id, f.Length, bound))
                        rep.Diff(where, $"{at}: live failed with error {f.Error}, the driver answered 0x{c.Status:X8}, at or above FeatureReportByteLength {p.FeatureLength}");
                    else
                    {
                        if (c.Ok) rep.Unverified(where, $"refused before the driver, below FeatureReportByteLength {p.FeatureLength} or an undeclared id (M1)", at);
                        FeatureError(rep, where, at, p, f.Id, f.Length, bound, f.Error, c, $"{p.Id} {at}");
                    }
                }
            }
            foreach (var (name, _, writes) in r.Outputs)
            {
                string where = $"{Case}/{name}";
                if (writes.Count != Ds3Lengths) rep.Diff(where, $"the recording holds {writes.Count} writes, its probe makes {Ds3Lengths}");
                foreach (var w in writes)
                {
                    rep.Call();
                    var outs = h.Of(k++);
                    string at = $"length {w.Length}";
                    if (w.Length >= 1) rep.Mapping(LM3Passes, w.Ok, w.Ok ? null : $"{name.Split('/')[^2]} length {w.Length} error {w.Error}");
                    if (!Ds3Driven(p, w))
                    {
                        rep.Unverified(where, "failed with no output packet, not driven: refused by the HID class or failed by the driver without effect (M3)", $"{at} error {w.Error}");
                        continue;
                    }
                    var c = Completions(outs).Single();
                    if (c.Ok != w.Ok) rep.Diff(where, $"{at}: live ok {(w.Ok ? 1 : 0)}, the driver completed 0x{c.Status:X8}");
                    else if (!w.Ok && w.Error != Win32Error(c.Status))
                        rep.Diff(where, $"{at}: both failed, error {w.Error} live, the driver's 0x{c.Status:X8} is error {Win32Error(c.Status)}");
                    var packets = Ring(outs).Select(Ds3Packet).ToList();
                    if (packets.Count != w.Packets.Count)
                    {
                        rep.Diff(where, $"{at}: {w.Packets.Count} packets live, {packets.Count} from the harness");
                        continue;
                    }
                    for (int i = 0; i < packets.Count; i++)
                    {
                        if (packets[i].AsSpan().SequenceEqual(w.Packets[i])) { rep.Same(); continue; }
                        rep.Diff(where, $"{at} packet {i}: live {Hex(w.Packets[i])}, harness {Hex(packets[i])}");
                    }
                }
            }
        }
    }

    // ── XUSB companion: HIDMaestroTest probe-xusb --record ───────────────

    /// <summary>The requests probe-xusb --record sends, by stream name
    /// (test\Program.cs ProbeXusbRecord): IOCTL code and output length.</summary>
    static readonly Dictionary<string, (uint Code, int OutLength)> XusbRequests = new(StringComparer.Ordinal)
    {
        ["get-information"] = (0x80006000, 12),
        ["get-capabilities-24"] = (0x8000E004, 24),
        ["get-capabilities-36"] = (0x8000E004, 36),
        ["get-battery-information"] = (0x8000E018, 4),
        ["get-information-ex"] = (0x8000E3FC, 949),
    };

    sealed record XusbCall(string Stream, string Name, byte[] Request, bool Ok, uint Error, uint Returned, byte[] Bytes);

    static List<XusbCall> ParseXusb(List<Stream> streams, string profile)
    {
        var calls = new List<XusbCall>();
        foreach (var s in streams)
        {
            var parts = s.Name.Split('/');
            if (parts.Length != 3 || parts[0] != "xusb" || parts[1] != profile || !XusbRequests.ContainsKey(parts[2]))
                throw new InvalidDataException($"live-xusb-{profile}: unexpected stream {s.Name}");
            var c = new Cursor(s);
            calls.Add(new XusbCall(s.Name, parts[2], c.Take("request"), c.Take("ok")[0] == 1, c.U32("error"), c.U32("returned"), c.Take("bytes")));
        }
        return calls;
    }

    /// <summary>The plan profile an XUSB stream runs on under the contract.</summary>
    static string XusbContractName(string profile) => $"{profile}#xusb";

    static void ScriptXusb(StringBuilder sc, string profile, List<XusbCall> calls)
    {
        sc.Append($"stream live/xusb/{profile}\nversion tag-own\ndevice {XusbContractName(profile)} 0 companion\n");
        for (int k = 0; k < calls.Count; k++)
        {
            var (code, outLen) = XusbRequests[calls[k].Name];
            sc.Append($"call {k}\nioctl 0x{code:X8} {Hex(calls[k].Request)} {outLen}\n");
        }
    }

    static void CompareXusb(Report rep, string profile, List<XusbCall> calls, Dictionary<string, Stream> harness)
    {
        string Case = $"live-xusb-{profile}";
        rep.Begin(Case, XusbRequests.Count, calls.Count, $"HIDMaestroTest probe-xusb --record: {XusbRequests.Count} requests");
        var h = new Calls(harness[$"live/xusb/{profile}"]);
        rep.Unverified(Case, "the controller index is taken as 0, the emulate holder's", profile);
        foreach (var name in XusbRequests.Keys.Where(n => !calls.Any(c => c.Name == n)))
            rep.Diff(Case, $"the recording holds no {name}");
        bool all = true;
        for (int k = 0; k < calls.Count; k++)
        {
            var call = calls[k];
            rep.Call();
            string where = $"{Case}/{call.Stream}";
            var c = Completions(h.Of(k), "ioctl").Single();
            if (call.Ok != c.Ok) { all = false; rep.Diff(where, $"live ok {(call.Ok ? 1 : 0)}, the companion completed 0x{c.Status:X8}"); continue; }
            if (!call.Ok)
            {
                bool same = call.Error == Win32Error(c.Status);
                rep.Mapping(LM4Xusb, same, same ? null : $"{profile} {call.Name}: live {call.Error}, 0x{c.Status:X8}");
                if (!same) { all = false; rep.Diff(where, $"both failed, error {call.Error} live, the companion's 0x{c.Status:X8} is error {Win32Error(c.Status)} (M4)"); }
                else rep.Same();
                continue;
            }
            if (call.Returned != c.Info) { all = false; rep.Diff(where, $"returned {call.Returned} live, information {c.Info} from the harness"); continue; }
            all &= CompareBytes(rep, where, "bytes", call.Bytes, c.Reported, (int)c.Info, null, null);
        }
        if (calls.Count > 0) rep.Mapping(LXusbIndex, all, all ? null : profile);
    }

    // ── the mode ─────────────────────────────────────────────────────────

    static PlanProfile Profile(Dictionary<string, PlanProfile> plan, string id) =>
        plan.TryGetValue(id, out var p) ? p : throw new InvalidDataException($"the plan has no profile {id}");

    sealed record Live(List<FeatureGroup> Features, List<SwitchRun> Switch, List<Ds3Run> Ds3, List<(string Profile, List<XusbCall> Calls)> Xusb);

    public static int Run(string[] args, string defaultGolden, string defaultNative)
    {
        string? outDir = null, selftest = null, writeLedger = null, pinnedLedger = null;
        string golden = defaultGolden, native = defaultNative, driver = "oracle";
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--golden": golden = Path.GetFullPath(Value()); break;
                case "--native": native = Path.GetFullPath(Value()); break;
                case "--selftest": selftest = Path.GetFullPath(Value()); break;
                case "--write-ledger": writeLedger = Path.GetFullPath(Value()); break;
                case "--pinned-ledger": pinnedLedger = Path.GetFullPath(Value()); break;
                case "--driver":
                    driver = Value();
                    if (driver is not ("oracle" or "tree")) throw new ArgumentException($"compare-live: --driver is oracle or tree, not {driver}");
                    break;
                default:
                    if (outDir != null || args[i].StartsWith("--")) throw new ArgumentException($"compare-live: unexpected {args[i]}");
                    outDir = Path.GetFullPath(args[i]);
                    break;
            }
        }
        string planPath = Path.Combine(golden, "case5-plan.txt");
        // The live run records 9bd6279's driver, so its own sources are the
        // oracle by default.
        string exe = Path.Combine(native, $"driver_harness_{driver}.exe");
        Console.WriteLine($"=== device_oracle_check compare-live, plan {planPath} ===");
        foreach (var need in new[] { planPath, exe }.Concat(pinnedLedger != null ? new[] { pinnedLedger } : Array.Empty<string>()))
            if (!File.Exists(need)) { Console.WriteLine($"  [MISSING] {need}"); return 2; }
        string planText = File.ReadAllText(planPath, Encoding.UTF8);
        var plan = ParsePlan(planText);
        if (selftest != null) return SelfTest(selftest, plan, planPath, exe);
        if (outDir == null) throw new ArgumentException("compare-live needs the out directory.");
        return Compare(outDir, plan, planPath, exe, writeLedger, pinnedLedger);
    }

    static string[] XusbProfiles(Dictionary<string, PlanProfile> plan) =>
        plan.Values.Where(p => p.Group == "xusb").Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();

    static Live? Load(string outDir, Dictionary<string, PlanProfile> plan)
    {
        var files = new List<(string Step, string Path)>
        {
            ("s46", Path.Combine(outDir, "s46", "live-s46-sony-features.records")),
            ("switch-pro", Path.Combine(outDir, "switch-pro", "live-switch-pro.records")),
            ("ds3", Path.Combine(outDir, "ds3", "live-ds3-sixaxis.records")),
        };
        files.AddRange(XusbProfiles(plan).Select(id => ("xusb", Path.Combine(outDir, "xusb", $"live-xusb-{id}.records"))));
        var missing = files.Where(f => !File.Exists(f.Path)).ToList();
        foreach (var (step, path) in missing) Console.WriteLine($"  [MISSING] step {step}: {path}");
        if (missing.Count > 0) return null;
        return new Live(
            ParseFeatures(ReadRecords(files[0].Path)),
            ParseSwitch(ReadRecords(files[1].Path)),
            ParseDs3(ReadRecords(files[2].Path)),
            XusbProfiles(plan).Select(id => (id, ParseXusb(ReadRecords(Path.Combine(outDir, "xusb", $"live-xusb-{id}.records")), id))).ToList());
    }

    static string Script(Live live, Dictionary<string, PlanProfile> plan)
    {
        var sc = new StringBuilder("hm-driver-script 1\n");
        ScriptFeatures(sc, live.Features, plan);
        ScriptSwitch(sc, live.Switch, SwitchProfile(plan));
        ScriptDs3(sc, live.Ds3, Ds3Profile(plan));
        foreach (var (id, calls) in live.Xusb) ScriptXusb(sc, id, calls);
        return sc.ToString();
    }

    static PlanProfile SwitchProfile(Dictionary<string, PlanProfile> plan) => plan.Values.First(p => p.Group == "switch");
    static PlanProfile Ds3Profile(Dictionary<string, PlanProfile> plan) => plan.Values.First(p => p.Id == "dualshock-3-full");

    static int s_runs;

    /// <summary>Runs the harness on the live calls' script in a directory
    /// of this run's own under <paramref name="parent"/>, named for the
    /// process and a counter, and removes it once its output is read
    /// (review F16): two runs at once never share the script or the
    /// output.</summary>
    static Dictionary<string, Stream> Drive(Live live, Dictionary<string, PlanProfile> plan, string planPath, string exe, string parent)
    {
        string work = Path.Combine(parent, $"compare-live-{Environment.ProcessId}-{Interlocked.Increment(ref s_runs)}");
        if (Directory.Exists(work)) throw new InvalidOperationException($"compare-live: {work} already exists");
        Directory.CreateDirectory(work);
        try
        {
            string script = Path.Combine(work, "live-script.txt");
            File.WriteAllText(script, Script(live, plan), new UTF8Encoding(false));
            string contractPlan = Path.Combine(work, "live-plan.txt");
            File.WriteAllText(contractPlan, ContractPlan(File.ReadAllText(planPath, Encoding.UTF8), live, plan, work), new UTF8Encoding(false));
            return RunHarness(exe, contractPlan, script, work).ToDictionary(s => s.Name, StringComparer.Ordinal);
        }
        finally
        {
            // The directory is this run's own. The hive case 5's
            // InstanceConfig loaded in it can stay locked a moment after
            // its key closes, which DeleteDirectory waits out.
            Case5Driver.DeleteDirectory(work);
        }
    }

    static int Compare(string outDir, Dictionary<string, PlanProfile> plan, string planPath, string exe,
                       string? writeLedger = null, string? pinnedLedger = null)
    {
        Live? live;
        try { live = Load(outDir, plan); }
        catch (InvalidDataException e)
        {
            // A recording that does not parse is one the probes did not
            // write, which no comparison can pass.
            Console.WriteLine($"  [DIFF] a recording does not parse: {e.Message}");
            Console.WriteLine("device_oracle_check compare-live: FAIL, a malformed recording");
            return 1;
        }
        if (live == null) { Console.WriteLine("device_oracle_check compare-live: MISSING recordings"); return 2; }
        var harness = Drive(live, plan, planPath, exe, outDir);
        var rep = new Report();
        CompareFeatures(rep, live.Features, plan, harness);
        CompareSwitch(rep, live.Switch, SwitchProfile(plan), harness);
        CompareDs3(rep, live.Ds3, Ds3Profile(plan), harness);
        foreach (var (id, calls) in live.Xusb) CompareXusb(rep, id, calls, harness);
        Console.WriteLine("  subjects:");
        rep.CheckSubjects();
        rep.Print();
        Console.WriteLine($"  masked: timer, repeat, skippedRepeat, timerSteps, the 0x21 and 0x30 timer byte");
        Console.WriteLine("  the unverified mappings, as these recordings bear on them:");
        rep.PrintLedger(Mappings);
        if (writeLedger != null) rep.WriteLedger(writeLedger, Mappings);
        if (pinnedLedger != null) rep.CheckPinned(pinnedLedger, Mappings);
        Console.WriteLine($"device_oracle_check compare-live: {(rep.Diffs == 0 ? "PASS" : $"FAIL, {rep.Diffs} difference(s)")}, {rep.Compared} comparison(s) matched"
            + (rep.LiveOnlyCount > 0 ? $", {rep.LiveOnlyCount} live-only" : ""));
        return rep.Diffs == 0 ? 0 : 1;
    }

    // ── selftest: synthetic recordings from the harness itself ───────────
    //
    // Builds live recordings the way the live probes lay them out, from
    // requests of the probes' shapes, with the harness's own answers turned
    // into what a caller would see under M1 to M4. compare-live must pass on
    // them, fail with a [DIFF] when one byte of a compared output changes in
    // each subject's file, and exit 2 when a step's file is missing. It
    // checks the parsing, the scripting and the mapping applied both ways.
    // It cannot check the mapping against Windows: only the live run can.

    static int SelfTest(string dir, Dictionary<string, PlanProfile> plan, string planPath, string exe)
    {
        // It writes into the directory and deletes nothing it did not make.
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new ArgumentException($"compare-live --selftest: {dir} must be absent or empty.");
        Directory.CreateDirectory(dir);
        var live = SyntheticInputs(plan);
        var harness = Drive(live, plan, planPath, exe, dir);
        WriteSynthetic(dir, live, plan, harness);

        int failures = 0;
        void Expect(string what, int want, int got)
        {
            bool ok = want == got;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}: exit {got}, want {want}");
            if (!ok) failures++;
        }

        Console.WriteLine("--- the synthetic recordings as written");
        Expect("compare-live on the synthetic recordings", 0, Compare(dir, plan, planPath, exe));

        var subjects = new (string File, string Tag)[]
        {
            (Path.Combine(dir, "s46", "live-s46-sony-features.records"), "buffer"),
            (Path.Combine(dir, "switch-pro", "live-switch-pro.records"), "reply"),
            (Path.Combine(dir, "ds3", "live-ds3-sixaxis.records"), "input"),
            (Path.Combine(dir, "xusb", $"live-xusb-{XusbProfiles(plan)[0]}.records"), "bytes"),
        };
        foreach (var (file, tag) in subjects)
        {
            byte[] keep = File.ReadAllBytes(file);
            var streams = ReadRecords(file);
            // An output the comparison holds: a buffer only of a call that succeeded.
            var target = streams.SelectMany(s => s.Outs.Select((o, i) => (s, i, o)))
                .First(x => x.o.Tag == tag && x.o.Data.Length > 2
                         && (tag != "buffer" || (x.s.Outs[x.i - 2].Tag == "ok" && x.s.Outs[x.i - 2].Data[0] == 1)));
            target.o.Data[2] ^= 0x01;
            WriteRecords(file, streams);
            Console.WriteLine($"--- one byte changed: {Path.GetFileName(file)}, {target.s.Name}, output {target.i} ({tag}) byte 2");
            Expect($"compare-live with {Path.GetFileName(file)} changed", 1, Compare(dir, plan, planPath, exe));
            File.WriteAllBytes(file, keep);
        }

        // Review F9: a recording with no calls, and one cut short, fail.
        string s46 = subjects[0].File, ds3 = subjects[2].File;
        byte[] keepS46 = File.ReadAllBytes(s46), keepDs3 = File.ReadAllBytes(ds3);
        WriteRecords(s46, Array.Empty<Stream>());
        Console.WriteLine("--- an empty recording: live-s46-sony-features.records holds no stream");
        Expect("compare-live with an empty S46 recording", 1, Compare(dir, plan, planPath, exe));
        File.WriteAllBytes(s46, keepS46);
        var cut = ReadRecords(ds3);
        cut.RemoveAt(cut.Count - 1);
        WriteRecords(ds3, cut);
        Console.WriteLine("--- a cut recording: the last stream of live-ds3-sixaxis.records removed");
        Expect("compare-live with a cut DS3 recording", 1, Compare(dir, plan, planPath, exe));
        File.WriteAllBytes(ds3, keepDs3);

        // A change to one recording, then compare-live, which must exit with
        // want and, for a [DIFF], print a line holding the given text. The
        // file is put back afterward. An edit that finds no target returns
        // null, and the check counts as failed: it did not run.
        void Mutate(string file, string what, int want, string? text, Func<List<Stream>, string?> edit,
                    string? writeLedger = null, string? pinnedLedger = null)
        {
            byte[] keep = File.ReadAllBytes(file);
            var streams = ReadRecords(file);
            string? target = edit(streams);
            if (target == null)
            {
                Console.WriteLine($"--- {what}: no recorded call to change, so that check did not run");
                failures++;
                return;
            }
            WriteRecords(file, streams);
            Console.WriteLine($"--- {what}: {target}");
            var was = Console.Out;
            var captured = new StringWriter();
            Console.SetOut(captured);
            int got;
            try { got = Compare(dir, plan, planPath, exe, writeLedger, pinnedLedger); }
            finally { Console.SetOut(was); }
            Console.Write(captured.ToString());
            File.WriteAllBytes(file, keep);
            bool found = text == null || captured.ToString().Split('\n').Any(l => l.Contains("[DIFF]") && l.Contains(text));
            Expect(what + (text != null ? $" (a [DIFF] naming \"{text}\"{(found ? "" : ", NOT PRINTED")})" : ""), want, found ? got : -1);
        }

        // The S46 calls: the stream, the index of the call's "ok" output,
        // the buffer's length and the profile.
        static IEnumerable<(Stream S, int I, int Length, string Profile)> S46Calls(List<Stream> streams) =>
            streams.Where(x => x.Name.Split('/') is [_, _, _, var last] && last.StartsWith("id"))
                .SelectMany(x => x.Outs.Select((o, i) => (x, i, o)).Where(t => t.o.Tag == "ok")
                    .Select(t => (t.x, t.i, t.x.Outs[t.i + 2].Data.Length, t.x.Name.Split('/')[1])));
        static byte IdOf(Stream x) => Convert.ToByte(x.Name.Split('/')[3].Substring(2), 16);

        // The harness's completion of an S46 call: its group's call number
        // counts every call of the group's id streams before it.
        Completion HarnessFeature(List<Stream> streams, Stream x, int okAt)
        {
            string head = string.Join('/', x.Name.Split('/').Take(3));
            int k = 0;
            foreach (var y in streams.Where(y => y.Name.StartsWith(head + "/id", StringComparison.Ordinal)))
            {
                if (y == x) { k += y.Outs.Take(okAt).Count(o => o.Tag == "ok"); break; }
                k += y.Outs.Count(o => o.Tag == "ok");
            }
            var parts = x.Name.Split('/');
            return Completions(new Calls(harness[$"live/s46/{parts[1]}/{parts[2]}"]).Of(k), "getfeature").Single();
        }

        // Review F10: a call the harness's driver answered, failed live with
        // nothing published, is a difference where the call reaches the
        // driver, and a refusal by the HID class elsewhere, which must give
        // ERROR_INVALID_PARAMETER.
        foreach (var (reach, error, want) in new[] { (true, 31u, 1), (false, ErrorInvalidParameter, 0), (false, 31u, 1) })
            Mutate(s46, $"a refusal live where the driver answered, {(reach ? "reaching the driver" : "one the HID class may refuse")}, error {error}", want,
                want == 1 ? "live failed with error" : null, streams =>
            {
                foreach (var t in S46Calls(streams))
                {
                    var pp = Profile(plan, t.Profile);
                    if (t.S.Outs[t.I].Data[0] != 1 || t.S.Outs[t.I + 3].Data.Length != 0 || t.Length == 0) continue;
                    if (pp.ClassMayRefuseFeature(IdOf(t.S), t.Length, pp.RefusalBound) == reach) continue;
                    t.S.Outs[t.I].Data[0] = 0;
                    BitConverter.GetBytes(error).CopyTo(t.S.Outs[t.I + 1].Data, 0);
                    Array.Clear(t.S.Outs[t.I + 2].Data);
                    return $"{t.S.Name} length {t.Length}, bound {pp.RefusalBound}";
                }
                return null;
            });

        // Review finding 10, each a [DIFF]: a changed error where both sides
        // failed (c5), changed bytes past the driver's report (g3), a changed
        // notification attributed by the window (g7), a DualShock 3 write
        // refused at OutputReportByteLength (g6), a Switch subcommand write
        // that failed with its reply gone (g5).
        Mutate(s46, "a changed error where both sides failed", 1, "live failed with error 1167", streams =>
        {
            var t = S46Calls(streams).FirstOrDefault(t => t.S.Outs[t.I].Data[0] == 0 && t.Length > 0);
            if (t.S == null) return null;
            BitConverter.GetBytes(1167u).CopyTo(t.S.Outs[t.I + 1].Data, 0);
            return $"{t.S.Name} length {t.Length}";
        });
        Mutate(s46, "changed bytes past the driver's report", 1, "past the driver's", streams =>
        {
            foreach (var t in S46Calls(streams).Where(t => t.S.Outs[t.I].Data[0] == 1).OrderByDescending(t => t.Length))
            {
                var pp = Profile(plan, t.Profile);
                int driverBytes = (int)HarnessFeature(streams, t.S, t.I).Info + (pp.UsesReportIds ? 0 : 1);
                if (driverBytes >= t.Length) continue;
                t.S.Outs[t.I + 2].Data[t.Length - 1] ^= 0x5A;
                return $"{t.S.Name} length {t.Length}, byte {t.Length - 1}, the driver returned {driverBytes}";
            }
            return null;
        });
        Mutate(s46, "a changed notification attributed by the window", 1, "the group's notifications in order", streams =>
        {
            var t = S46Calls(streams).FirstOrDefault(t => t.S.Outs[t.I].Data[0] == 1);
            if (t.S == null) return null;
            string head = string.Join('/', t.S.Name.Split('/').Take(3));
            streams.Single(x => x.Name == head + "/setup").Outs.First(o => o.Tag == "attribution").Data[0] = 0;
            int n = t.S.Outs.FindIndex(t.I, o => o.Tag == "notify");
            t.S.Outs[n] = ("notify", t.S.Outs[n].Data.Append((byte)0x05).ToArray());
            return $"{head} by window, {t.S.Name} length {t.Length}, notify 05 added";
        });
        Mutate(ds3, "a DualShock 3 write refused at OutputReportByteLength", 1, "live ok 0", streams =>
        {
            var p3 = Ds3Profile(plan);
            foreach (var x in streams.Where(x => x.Name.EndsWith("/output/WriteFile/cmd01", StringComparison.Ordinal)))
                for (int i = 0; i < x.Outs.Count; i++)
                {
                    if (x.Outs[i].Tag != "write" || x.Outs[i].Data.Length != p3.OutputLength || x.Outs[i + 1].Data[0] != 1) continue;
                    int n = (int)BitConverter.ToUInt32(x.Outs[i + 3].Data);
                    x.Outs[i + 1].Data[0] = 0;
                    x.Outs[i + 2] = ("error", U32(31));
                    x.Outs[i + 3] = ("packets", U32(0));
                    x.Outs.RemoveRange(i + 4, n);
                    return $"{x.Name} length {p3.OutputLength}, {n} packet(s) dropped";
                }
            return null;
        });
        // The setup stream's declared ids are the feature reports of the
        // descriptor the SDK gave the device, in the descriptor's order, as
        // sony_feature_gate_check's RecordProfile lists them. The same ids
        // in another order, or one id changed, is a [DIFF].
        foreach (bool swap in new[] { false, true })
            Mutate(s46, swap ? "two declared ids swapped" : "the declared ids in the descriptor's order", swap ? 1 : 0,
                swap ? "the plan's descriptor declares" : null, streams =>
            {
                foreach (var x in streams.Where(x => x.Name.EndsWith("/setup", StringComparison.Ordinal)))
                {
                    var order = (byte[])Profile(plan, x.Name.Split('/')[1]).Declared.Clone();
                    if (order.Length < 2 || order.SequenceEqual(order.OrderBy(b => b))) continue;
                    if (swap) (order[0], order[1]) = (order[1], order[0]);
                    int at = x.Outs.FindIndex(o => o.Tag == "declared");
                    x.Outs[at] = ("declared", order);
                    return $"{x.Name}: {Hex(order)}";
                }
                return null;
            });

        // M1: a collection that declares no feature report fails each
        // GET_FEATURE before the driver, as the live ps-classic did with
        // ERROR_INVALID_FUNCTION. One that succeeded reached the driver,
        // which the harness does not model, so it is a [DIFF].
        foreach (bool succeed in new[] { false, true })
            Mutate(s46, succeed ? "a GET_FEATURE that succeeded on a collection with no feature report"
                                : "every GET_FEATURE of a collection with no feature report failed with ERROR_INVALID_FUNCTION",
                succeed ? 1 : 0, succeed ? "declares no feature report" : null, streams =>
            {
                int changed = 0;
                foreach (var t in S46Calls(streams))
                {
                    if (Profile(plan, t.Profile).DeclaresFeature || t.Length == 0) continue;
                    if (succeed)
                    {
                        t.S.Outs[t.I].Data[0] = 1;
                        BitConverter.GetBytes(0u).CopyTo(t.S.Outs[t.I + 1].Data, 0);
                        return $"{t.S.Name} length {t.Length} succeeded";
                    }
                    t.S.Outs[t.I].Data[0] = 0;
                    BitConverter.GetBytes(ErrorInvalidFunction).CopyTo(t.S.Outs[t.I + 1].Data, 0);
                    changed++;
                }
                return changed == 0 ? null : $"{changed} call(s) failed with error {ErrorInvalidFunction}";
            });

        // Live only: another program's request on the device. A feature
        // read notification of an id other than the call's, in a group the
        // fence attributes, and a Switch Pro reply among the frames read
        // before the probe's first write, are another program's and pass
        // as [LIVE-ONLY]. The call's own id added, or a full-mode frame
        // with no reply ahead of it, is still a [DIFF].
        foreach (bool own in new[] { false, true })
            Mutate(s46, own ? "a notification of the call's own id added to a failed call" : "a notification of another declared id added to a failed call",
                own ? 1 : 0, own ? "both failed, notify" : null, streams =>
            {
                foreach (var t in S46Calls(streams))
                {
                    var pp = Profile(plan, t.Profile);
                    if (!pp.UsesReportIds || t.S.Outs[t.I].Data[0] != 0 || t.Length == 0 || t.S.Outs[t.I + 3].Data.Length != 0) continue;
                    string head = string.Join('/', t.S.Name.Split('/').Take(3));
                    var att = streams.Single(x => x.Name == head + "/setup").Outs.First(o => o.Tag == "attribution").Data;
                    if (att[0] != 1) continue;
                    byte id = IdOf(t.S);
                    byte other = pp.Features.FirstOrDefault(f => f != id);
                    if (!own && other == id) continue;
                    t.S.Outs[t.I + 3] = ("notify", new[] { own ? id : other });
                    return $"{t.S.Name} length {t.Length}, notify {(own ? id : other):X2}";
                }
                return null;
            });
        foreach (bool reply in new[] { true, false })
            Mutate(Path.Combine(dir, "switch-pro", "live-switch-pro.records"),
                reply ? "a Switch Pro reply and full-mode frames before the probe's first write" : "a full-mode frame with no reply before the probe's first write",
                reply ? 0 : 1, reply ? null : "pre-handshake", streams =>
            {
                var pre = streams.FirstOrDefault(x => x.Name.EndsWith("/pre-handshake", StringComparison.Ordinal));
                var init = streams.FirstOrDefault(x => x.Name.EndsWith("/init", StringComparison.Ordinal));
                var answer = init?.Outs.FirstOrDefault(o => o.Tag == "reply").Data;
                if (pre == null || answer == null || pre.Outs.Count < 2) return null;
                var full = (byte[])answer.Clone();
                full[0] = 0x30;
                if (reply) { pre.Outs.Add(("frame", (byte[])answer.Clone())); pre.Outs.Add(("repeat", U32(3))); }
                pre.Outs.Add(("frame", full));
                pre.Outs.Add(("repeat", U32(18)));
                return reply ? "a 0x21 reply and a 0x30 frame appended" : "a 0x30 frame appended";
            });

        // M3: the HID class hands the driver the output report's length of
        // a longer write, so each write past it publishes the packet the
        // report's length does, as the live DualShock 3 run recorded.
        Mutate(ds3, "DualShock 3 writes past the output report, each publishing the report's packet", 0, null, streams =>
        {
            var p3 = Ds3Profile(plan);
            int changed = 0;
            foreach (var x in streams.Where(x => x.Name.Contains("/output/", StringComparison.Ordinal)))
            {
                byte[][]? atReport = null;
                for (int i = 0; i < x.Outs.Count; i++)
                {
                    if (x.Outs[i].Tag != "write" || x.Outs[i + 1].Data[0] != 1) continue;
                    int n = (int)BitConverter.ToUInt32(x.Outs[i + 3].Data);
                    int length = x.Outs[i].Data.Length;
                    if (length == p3.OutputLength) atReport = x.Outs.GetRange(i + 4, n).Select(o => o.Data).ToArray();
                    else if (length > p3.OutputLength && atReport != null && atReport.Length == n)
                        for (int j = 0; j < n; j++) { x.Outs[i + 4 + j] = ("output", (byte[])atReport[j].Clone()); changed++; }
                }
            }
            return changed == 0 ? null : $"{changed} packet(s) of writes past {p3.OutputLength} bytes set to the {p3.OutputLength}-byte write's";
        });
        Mutate(Path.Combine(dir, "switch-pro", "live-switch-pro.records"), "a Switch subcommand write that failed with its reply gone", 1,
            "the driver accepted it", streams =>
        {
            var init = streams.FirstOrDefault(x => x.Name.EndsWith("/init", StringComparison.Ordinal));
            if (init == null) return null;
            int w = init.Outs.FindIndex(o => o.Tag == "write" && o.Data.Length == SwitchProfile(plan).OutputLength);
            if (w < 0 || init.Outs[w + 1].Data[0] != 1) return null;
            init.Outs[w + 1] = ("writeOk", new byte[] { 0 });
            init.Outs[w + 2] = ("writeError", U32(31));
            init.Outs[w + 3] = ("written", U32(0));
            int r = init.Outs.FindIndex(w + 4, o => o.Tag is "reply" or "noReply");
            if (init.Outs[r].Tag == "reply") { init.Outs.RemoveRange(r, 2); init.Outs.Insert(r, ("noReply", Array.Empty<byte>())); }
            return $"write {Hex(init.Outs[w].Data.AsSpan(0, 11))} failed, no reply";
        });

        // Note N5: the S46 matrix is held per profile, so a profile missing,
        // or one recorded twice in place of another, fails whatever the total.
        foreach (bool twice in new[] { false, true })
            Mutate(s46, twice ? "one profile recorded in place of another" : "a profile missing", 1,
                twice ? "groups of this profile" : "holds no group of this profile", streams =>
            {
                var groups = streams.Select(x => x.Name.Split('/')[1]).Distinct().ToList();
                if (groups.Count < 2) return null;
                string drop = groups[^1], dup = groups[^2];
                streams.RemoveAll(x => x.Name.Split('/')[1] == drop);
                if (twice)
                    streams.AddRange(streams.Where(x => x.Name.Split('/')[1] == dup).ToList().Select(x =>
                    {
                        var c = new Stream(string.Join('/', x.Name.Split('/').Select((part, i) => i == 2 ? "index99" : part)));
                        foreach (var (tag, data) in x.Outs) c.Out(tag, (byte[])data.Clone());
                        return c;
                    }));
                return twice ? $"{drop} dropped, {dup} recorded again at index 99" : $"{drop} dropped";
            });

        // M0: a plan profile whose device did not start is recorded as its
        // problem code and status in place of the matrix, and passes with
        // the verdict in the ledger. A start failure with no problem code
        // fails.
        foreach (uint problem in new uint[] { 10, 0 })
            Mutate(s46, problem != 0 ? "a profile whose device did not start" : "a start failure with no problem code",
                problem != 0 ? 0 : 1, problem != 0 ? null : "no problem code", streams =>
            {
                var heads = streams.Select(x => x.Name.Split('/')).Select(n => (Profile: n[1], Index: n[2])).Distinct().ToList();
                if (heads.Count < 2) return null;
                var (profile, index) = heads[^1];
                int at = streams.FindIndex(x => x.Name.Split('/')[1] == profile);
                streams.RemoveAll(x => x.Name.Split('/')[1] == profile);
                var s = new Stream($"feature/{profile}/{index}/start");
                s.Out("problem", U32(problem));
                s.Out("status", U32(problem != 0 ? 0xC00000B9u : 0u));
                streams.Insert(at, s);
                return $"{profile}'s matrix replaced by a start stream, problem {problem}";
            });

        // Review finding 10: the ledger of a run, pinned, holds a later run to
        // the same verdicts and exceptions.
        string ledger = Path.Combine(dir, "ledger.txt");
        Mutate(s46, "the ledger written", 0, null, _ => "no change", writeLedger: ledger);
        Mutate(s46, "the same recordings against the pinned ledger", 0, null, _ => "no change", pinnedLedger: ledger);
        string keptLedger = File.ReadAllText(ledger);
        int at = keptLedger.IndexOf("verdict holds\n", StringComparison.Ordinal);
        File.WriteAllText(ledger, keptLedger.Substring(0, at) + "verdict holds-in-part\n" + keptLedger.Substring(at + "verdict holds\n".Length));
        Mutate(s46, "a pinned ledger with one verdict changed", 1, "ledger", _ => "no change to the recordings", pinnedLedger: ledger);
        File.Delete(ledger);

        string moved = ds3 + ".moved";
        File.Move(ds3, moved);
        Console.WriteLine("--- a step's file missing");
        Expect("compare-live without the ds3 step", 2, Compare(dir, plan, planPath, exe));
        File.Move(moved, ds3);

        Console.WriteLine($"device_oracle_check compare-live --selftest: {(failures == 0 ? "PASS" : $"FAIL, {failures}")}");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Requests of the live probes' shapes. The Sony matrix for a
    /// profile with report ids, one without and the control. SDL's
    /// Bluetooth init with the USB handshake the HID class refuses. The DS3
    /// states, feature reads and output commands. The XUSB requests.</summary>
    static Live SyntheticInputs(Dictionary<string, PlanProfile> plan)
    {
        var features = new List<FeatureGroup>();
        int index = 0;
        foreach (var p in FeatureSubjects(plan))
        {
            var g = new FeatureGroup { Profile = p.Id, Index = index, Head = $"feature/{p.Id}/index{index}", Declared = p.Declared, FeatureLength = (uint)p.FeatureLength, Attributed = true };
            index++;
            foreach (byte id in FeatureIds(p.Features))
                g.Ids.Add(($"{g.Head}/id{id:X2}", Enumerable.Range(0, FeatureSweepMax(p.FeatureLength, p.FeatureLength) + 1).Select(len => new FeatureCall(id, len, false, 0, new byte[len], Array.Empty<byte>(), null, null)).ToList()));
            features.Add(g);
        }

        var sw = new SwitchRun { Index = 0, Head = "switch-pro/index0", Body = NeutralSwitchBody() };
        var usb = new byte[64]; usb[0] = 0x80; usb[1] = 0x02;
        sw.Writes.Add(new SwitchWrite(usb, false, 1, new(), null));
        byte counter = 0;
        foreach (var (id, a) in new (byte, byte[])[]
        {
            (0x02, Array.Empty<byte>()), (0x10, new byte[] { 0x10, 0x80, 0, 0, 22 }), (0x10, new byte[] { 0x3D, 0x60, 0, 0, 18 }),
            (0x10, new byte[] { 0x20, 0x60, 0, 0, 24 }), (0x10, new byte[] { 0x86, 0x60, 0, 0, 18 }),
            (0x48, new byte[] { 1 }), (0x40, new byte[] { 1 }), (0x30, new byte[] { 1 }), (0x50, Array.Empty<byte>()), (0x03, new byte[] { 0x30 }),
        })
        {
            var pkt = new byte[49];
            pkt[0] = 0x01; pkt[1] = (byte)(counter++ & 0xF);
            new byte[] { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 }.CopyTo(pkt, 2);
            pkt[10] = id;
            a.CopyTo(pkt, 11);
            sw.Writes.Add(new SwitchWrite(pkt, true, 0, new(), null));
        }

        var ds3 = new Ds3Run { Index = 0, Head = "ds3/dualshock-3-full/index0" };
        var rng = new Rng(Program.DefaultSeed, 0x5501);
        for (int i = 0; i < Ds3States; i++)
        {
            var native = new byte[49];
            rng.Fill(native);
            native[0] = 0x01;
            ds3.States.Add(new Ds3State($"{ds3.Head}/state/s{i}", native, new List<byte[]> { Array.Empty<byte>() },
                new[] { (0xF2, 64), (0x00, 64), (0xF2, 255) }.Select(x => new Ds3Feature((byte)x.Item1, x.Item2, true, 0, new byte[x.Item2])).ToList()));
        }
        foreach (bool setOutput in new[] { false, true })
            foreach (byte cmd in new byte[] { 1, 2, 3, 9, 10 })
            {
                var writes = new List<Ds3Write>();
                foreach (int len in new[] { 0, 1, 2, 3, 4, 5, 7, 8, 9, 48, 49, 50, 51, 52, 53, 64, 65 })
                {
                    var b = new byte[len];
                    if (len > 1) b[1] = cmd;
                    for (int i = 9; i < len; i++) b[i] = (byte)(0x80 + i);
                    writes.Add(new Ds3Write(len, b, len >= 2, len >= 2 ? 0u : 87u, new()));
                }
                ds3.Outputs.Add(($"{ds3.Head}/output/{(setOutput ? "HidD_SetOutputReport" : "WriteFile")}/cmd{cmd:D2}", setOutput, writes));
            }

        var xusb = XusbProfiles(plan).Select(id => (id, XusbRequests.Keys.Select(name => new XusbCall($"xusb/{id}/{name}", name,
            name switch
            {
                "get-capabilities-24" => new byte[] { 1, 1, 0 },
                "get-capabilities-36" => new byte[] { 2, 1, 0 },
                "get-battery-information" => new byte[] { 2, 1, 0, 0 },
                "get-information-ex" => new byte[] { 4, 1, 2 }.Concat(new byte[33]).ToArray(),
                _ => Array.Empty<byte>(),
            }, false, 0, 0, Array.Empty<byte>())).ToList())).ToList();
        return new Live(features, new List<SwitchRun> { sw }, new List<Ds3Run> { ds3 }, xusb);
    }

    /// <summary>The neutral state's body, as the live probe's pump submits
    /// it: SwitchProPacker's at 9bd6279, switch-pro's stateBlock from
    /// v2.0.0.</summary>
    static byte[] NeutralSwitchBody()
    {
        var body = new byte[Case2Packers.SwitchPro.BodySize];
        var neutral = new HIDMaestro.HMGamepadState();
        Case2Packers.SwitchPro.BuildBody(in neutral, 0.5, 0.5, 0.5, 0.5, body);
        return body;
    }

    static byte[] U32(uint v) => BitConverter.GetBytes(v);

    /// <summary>The harness's answers as the live probes would have recorded
    /// them, under M1 to M4.</summary>
    static void WriteSynthetic(string dir, Live live, Dictionary<string, PlanProfile> plan, Dictionary<string, Stream> harness)
    {
        // Sony
        var s46 = new List<Stream>();
        foreach (var g in live.Features)
        {
            var p = Profile(plan, g.Profile);
            var h = new Calls(harness[$"live/s46/{g.Profile}/index{g.Index}"]);
            var setup = new Stream(g.Head + "/setup");
            setup.Out("declared", g.Declared);
            setup.Out("featureLength", U32(g.FeatureLength));
            setup.Out("attribution", new byte[] { 1, g.Declared.FirstOrDefault() });
            s46.Add(setup);
            int k = 0;
            foreach (var (name, calls) in g.Ids)
            {
                var s = new Stream(name);
                foreach (var call in calls)
                {
                    var outs = h.Of(k++);
                    bool driven = FeatureRequest(p, call.Id, call.Length) != null;
                    var c = driven ? Completions(outs, "getfeature").Single() : default;
                    bool ok = driven && c.Ok;
                    byte[] buf = new byte[call.Length];
                    if (call.Length > 0) buf[0] = call.Id;
                    if (ok) buf = FeatureSeen(p, call.Id, c);
                    s.Out("ok", new[] { (byte)(ok ? 1 : 0) });
                    s.Out("error", U32(!driven ? (call.Length > 0 && !p.DeclaresFeature ? ErrorInvalidFunction : ErrorInvalidParameter) : Win32Error(c.Status)));
                    s.Out("buffer", buf);
                    s.Out("notify", Ring(outs).Where(r => r[0] == 3).Select(r => r[1]).ToArray());
                }
                s46.Add(s);
            }
            var end = new Stream(g.Head + "/end");
            end.Out("unattributed", Array.Empty<byte>());
            s46.Add(end);
        }
        Directory.CreateDirectory(Path.Combine(dir, "s46"));
        WriteRecords(Path.Combine(dir, "s46", "live-s46-sony-features.records"), s46);

        // Switch Pro
        var swOut = new List<Stream>();
        foreach (var r in live.Switch)
        {
            var h = new Calls(harness[$"live/switch/index{r.Index}"]);
            var state = new Stream(r.Head + "/state"); state.Out("body", r.Body); swOut.Add(state);
            var pre = new Stream(r.Head + "/pre-handshake");
            foreach (var c in Completions(h.Of(0), "read").Where(c => c.Ok)) { pre.Out("frame", SwitchMasked(c)); pre.Out("repeat", U32(3)); }
            swOut.Add(pre);
            var init = new Stream(r.Head + "/init");
            for (int i = 0; i < r.Writes.Count; i++)
            {
                var w = r.Writes[i];
                var outs = h.Of(i + 1);
                init.Out("write", w.Bytes);
                init.Out("writeOk", new[] { (byte)(w.Ok ? 1 : 0) });
                init.Out("writeError", U32(w.Error));
                init.Out("written", U32(w.Ok ? (uint)w.Bytes.Length : 0));
                var reply = Completions(outs, "read").Where(IsReply).Select(c => (Completion?)c).FirstOrDefault();
                if (reply is Completion rc) { init.Out("reply", SwitchMasked(rc)); init.Out("timer", new[] { rc.Buffer[1] }); }
                else init.Out("noReply", Array.Empty<byte>());
            }
            swOut.Add(init);
            var stream = new Stream(r.Head + "/stream");
            foreach (var c in Completions(h.Of(SwitchFinal), "read").Where(c => c.Ok && !IsReply(c))) { stream.Out("frame", SwitchMasked(c)); stream.Out("repeat", U32(200)); }
            stream.Out("timerSteps", new byte[] { 1 });
            swOut.Add(stream);
        }
        Directory.CreateDirectory(Path.Combine(dir, "switch-pro"));
        WriteRecords(Path.Combine(dir, "switch-pro", "live-switch-pro.records"), swOut);

        // DualShock 3
        var p3 = Ds3Profile(plan);
        var dsOut = new List<Stream>();
        foreach (var r in live.Ds3)
        {
            var h = new Calls(harness[$"live/ds3/index{r.Index}"]);
            int k = 0;
            foreach (var st in r.States)
            {
                var s = new Stream(st.Stream);
                s.Out("native", st.Native);
                var rc = Completions(h.Of(k++), "read").First();
                s.Out("input", p3.UsesReportIds ? rc.Buffer : new byte[] { 0 }.Concat(rc.Buffer).ToArray());
                s.Out("repeat", U32(6));
                foreach (var f in st.Features)
                {
                    var c = Completions(h.Of(k++), "getfeature").Single();
                    s.Out("request", new byte[] { f.Id, (byte)f.Length, (byte)(f.Length >> 8) });
                    s.Out("ok", new[] { (byte)(c.Ok ? 1 : 0) });
                    s.Out("error", U32(Win32Error(c.Status)));
                    s.Out("buffer", FeatureSeen(p3, f.Id, c));
                }
                dsOut.Add(s);
            }
            foreach (var (name, _, writes) in r.Outputs)
            {
                var s = new Stream(name);
                foreach (var w in writes)
                {
                    var outs = h.Of(k++);
                    var packets = Ds3Driven(p3, w) ? Ring(outs).Select(Ds3Packet).ToList() : new List<byte[]>();
                    var c = Ds3Driven(p3, w) ? Completions(outs).Single() : default;
                    s.Out("write", w.Bytes);
                    s.Out("ok", new[] { (byte)(Ds3Driven(p3, w) && c.Ok ? 1 : 0) });
                    s.Out("error", U32(Ds3Driven(p3, w) ? Win32Error(c.Status) : w.Error));
                    s.Out("packets", U32((uint)packets.Count));
                    foreach (var pk in packets) s.Out("output", pk);
                }
                dsOut.Add(s);
            }
        }
        Directory.CreateDirectory(Path.Combine(dir, "ds3"));
        WriteRecords(Path.Combine(dir, "ds3", "live-ds3-sixaxis.records"), dsOut);

        // XUSB
        Directory.CreateDirectory(Path.Combine(dir, "xusb"));
        foreach (var (id, calls) in live.Xusb)
        {
            var h = new Calls(harness[$"live/xusb/{id}"]);
            var list = new List<Stream>();
            for (int k = 0; k < calls.Count; k++)
            {
                var c = Completions(h.Of(k), "ioctl").Single();
                var s = new Stream(calls[k].Stream);
                s.Out("request", calls[k].Request);
                s.Out("ok", new[] { (byte)(c.Ok ? 1 : 0) });
                s.Out("error", U32(Win32Error(c.Status)));
                s.Out("returned", U32(c.Ok ? c.Info : 0));
                s.Out("bytes", c.Ok ? c.Reported : Array.Empty<byte>());
                list.Add(s);
            }
            WriteRecords(Path.Combine(dir, "xusb", $"live-xusb-{id}.records"), list);
        }
    }
}
