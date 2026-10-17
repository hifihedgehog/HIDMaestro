using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace HIDMaestro.Internal.Usbip;

/// <summary>Issue #56, v2.0.0: the answers a device gives on request, one
/// table on both transports (design note, mechanism 4).
///
/// <para>Each entry says which GET it answers. A plain entry answers the
/// report it names. A message entry answers it only while the last
/// SET_FEATURE on its <c>onReport</c> carried its message at its offset,
/// and its parameter when it has one: Valve's command channel writes a
/// message with SET_REPORT(Feature) and reads its answer with
/// GET_REPORT(Feature), SET 0x83 then GET 0x83 for attributes, SET 0xAE then
/// GET 0xAE for a string, the shape SDL's own driver uses in
/// <c>SDL_hidapi_steam.c</c>. A GET looks first for a message entry for its
/// report whose message and parameter match, then for one whose message
/// matches and that takes no parameter, then for a plain entry. Nothing
/// matching stalls, as a real device does for a message it does not
/// implement.</para>
///
/// <para>The table compiles once per controller: every patch from the
/// index and the serial and every CRC is written into final bytes then. On
/// USB/IP it serves the answers from those arrays into one buffer, with
/// only a file-backed patch written as the answer is asked for. On UMDF2
/// the SDK writes it as the <c>FeatureAnswers</c> blob
/// (<see cref="ToBlob"/>), which the driver parses once at its add into
/// arrays of the same shape and serves the same way.</para></summary>
internal sealed class FeatureStubTable
{
    // The blob's limits and layout, the same constants driver.h carries
    // (HM_FEATURE_*). profile_model_check's contract mode fails when the two
    // disagree, and a model past a limit is refused at load.
    internal const int MaxEntries = 64;
    internal const int MaxData = 64;
    internal const int MaxPages = 16;
    internal const int MaxOnReports = 4;
    internal const int MaxArena = 16384;
    internal const int MaxMessage = 8;
    internal const int MaxSizes = 4;
    internal const int MaxSeed = 4;
    internal const uint BlobFormat = 4;
    internal const int BlobHeader = 16;
    internal const int BlobEntry = 16;
    internal const byte FlagMessage = 0x01;
    internal const byte FlagParam = 0x02;
    internal const byte FlagEcho = 0x04;
    internal const byte FlagReplyHead = 0x08;
    internal const byte FlagNotify = 0x10;

    /// <summary>One answer, compiled.</summary>
    internal sealed class Entry
    {
        public byte Id;
        public byte Flags;
        public int OnSlot;
        public int At;
        public byte[] Value = Array.Empty<byte>();
        public int ParamAt;
        public byte ParamValue;
        /// <summary>The answer's pages, each <see cref="Size"/> bytes: one
        /// for a plain answer, none for an echo.</summary>
        public byte[][] Pages = Array.Empty<byte[]>();
        public int Size;
        public int MinLength;
        /// <summary>The sizes served, largest first, or empty.</summary>
        public byte[] Sizes = Array.Empty<byte>();
        /// <summary>Patches read from a file as the answer is asked for
        /// (USB/IP), written into the scratch copy of the page.</summary>
        public AnswerPatchSpec[]? FilePatches;
        public byte[]? CrcSeed;
        /// <summary>The next page served, set back by a SET on the
        /// message's report.</summary>
        public int Page;

        public bool IsMessage => (Flags & FlagMessage) != 0;
        public bool HasParam => (Flags & FlagParam) != 0;
        public bool Echo => (Flags & FlagEcho) != 0;
        public bool ReplyHead => (Flags & FlagReplyHead) != 0;
        public bool Notify => (Flags & FlagNotify) != 0;
    }

    private readonly Entry[] _entries;
    private readonly byte[] _onReports;
    // The data of the last SET on each onReport, up to MaxData bytes, and
    // its length. A SET's buffer is the reader's and reused, so the bytes
    // are copied.
    private readonly byte[][] _kept;
    private readonly int[] _keptLength;
    private readonly ValueSources.Values _values;
    // A descriptor without report IDs has one feature report, report 0,
    // whatever ID a request names, as HidClass delivers every such request
    // to the UMDF2 driver.
    private readonly bool _usesReportIds;
    // Each answer is assembled here and handed out as a slice: no
    // allocation per GET.
    private readonly byte[] _scratch = new byte[MaxData];
    private readonly object _lock = new();

    internal IReadOnlyList<Entry> Entries => _entries;
    internal IReadOnlyList<byte> OnReports => _onReports;

    private FeatureStubTable(Entry[] entries, byte[] onReports, ValueSources.Values values, bool usesReportIds)
    {
        _entries = entries;
        _onReports = onReports;
        _values = values;
        _usesReportIds = usesReportIds;
        _kept = new byte[onReports.Length][];
        for (int i = 0; i < _kept.Length; i++) _kept[i] = new byte[MaxData];
        _keptLength = new int[onReports.Length];
    }

    // ── Building ─────────────────────────────────────────────────────────

    /// <summary>The table for one controller of a profile, from the model
    /// the profile resolved to, or null when it has no answers.
    /// <paramref name="serial"/> is the serial the device serves, which a
    /// <c>serial</c> patch writes.</summary>
    public static FeatureStubTable? From(ControllerProfile profile, int index, string? serial) =>
        From(profile, index, serial, ValueSources.ReadFile);

    /// <summary>The same with a given reader for the files value sources
    /// read, so a check can exercise an answer with Steam present and
    /// absent on any machine.</summary>
    internal static FeatureStubTable? From(ControllerProfile profile, int index, string? serial, ValueSources.FileReader files)
    {
        var model = profile.Resolved.Model;
        var spec = model.FeatureStubs;
        if (spec?.Reports == null || spec.Reports.Count == 0) return null;
        List<Normal> entries;
        var map = profile.GetOrBuildReportMap();
        try
        {
            Validate(spec, model.ValueSources, profile.RequiresUsbipBackend);
            entries = Normalize(spec, map, profile.RequiresUsbipBackend);
        }
        catch (ProfileDefectException e)
        {
            throw new InvalidOperationException($"Profile '{profile.Id}': {e.JsonPath}: {e.Reason}", e);
        }
        var values = new ValueSources.Values(index, serial, model.ValueSources, files);
        var onReports = new List<byte>();
        var compiled = new Entry[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var n = entries[i];
            var e = new Entry
            {
                Id = n.Id,
                Size = n.Size,
                MinLength = n.MinLength,
                Sizes = n.Sizes,
                CrcSeed = n.CrcSeed,
            };
            if (n.Message != null)
            {
                int slot = onReports.IndexOf(n.OnReport);
                if (slot < 0) { slot = onReports.Count; onReports.Add(n.OnReport); }
                e.OnSlot = slot;
                e.At = n.At;
                e.Value = n.Message;
                e.Flags |= FlagMessage;
                if (n.ParamAt >= 0) { e.Flags |= FlagParam; e.ParamAt = n.ParamAt; e.ParamValue = n.ParamValue; }
            }
            if (n.Echo) e.Flags |= FlagEcho;
            if (n.ReplyHead) e.Flags |= FlagReplyHead;
            if (n.Notify) e.Flags |= FlagNotify;
            if (!n.Echo)
            {
                List<AnswerPatchSpec>? filePatches = null;
                e.Pages = new byte[n.Pages.Count][];
                for (int k = 0; k < n.Pages.Count; k++)
                {
                    var page = new byte[n.Size];
                    n.Pages[k].CopyTo(page, 0);
                    foreach (var p in n.Patches)
                    {
                        if (ValueSources.ReadsFile(p)) { if (k == 0) (filePatches ??= new()).Add(p); }
                        else ValueSources.Apply(page, p, values);
                    }
                    // A file-backed value and a CRC are refused together at
                    // load, so the CRC is final here.
                    if (n.CrcSeed != null) WriteCrc(page, n.CrcSeed);
                    e.Pages[k] = page;
                }
                e.FilePatches = filePatches?.ToArray();
            }
            compiled[i] = e;
        }
        return new FeatureStubTable(compiled, onReports.ToArray(), values, map.UsesReportIds);
    }

    /// <summary>The CRC-32 (reflected, 0xEDB88320) of the seed then the
    /// answer up to its last four bytes, written little-endian in them, as
    /// a Sony pad ends a feature report over Bluetooth (hid-playstation.c
    /// ps_get_report and PS_FEATURE_CRC32_SEED).</summary>
    internal static void WriteCrc(Span<byte> answer, ReadOnlySpan<byte> seed)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in seed) crc = CrcByte(crc, b);
        for (int i = 0; i < answer.Length - 4; i++) crc = CrcByte(crc, answer[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(answer[^4..], ~crc);
    }

    private static uint CrcByte(uint crc, byte b)
    {
        crc ^= b;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }

    // ── Normal form and checks ───────────────────────────────────────────

    /// <summary>One entry with every key explicit, the table-level form of
    /// earlier releases expanded.</summary>
    internal sealed class Normal
    {
        public int Index;
        public byte Id;
        public byte[]? Message;
        public byte OnReport;
        public int At;
        public int ParamAt = -1;
        public byte ParamValue;
        public List<byte[]> Pages = new();
        public bool Echo;
        public bool ReplyHead;
        public bool Notify;
        public int Size;
        public int MinLength;
        public byte[] Sizes = Array.Empty<byte>();
        public byte[]? CrcSeed;
        public List<AnswerPatchSpec> Patches = new();
    }

    /// <summary>The checks an answer table meets that need no descriptor:
    /// every key's form and range, the limits the driver holds, and the
    /// value sources and patches (<see cref="ValueSources.Validate"/>).
    /// Throws <see cref="ProfileDefectException"/> with the JSON path.</summary>
    internal static void Validate(FeatureStubSpec? spec, Dictionary<string, ValueSourceSpec>? sources, bool usbip)
    {
        if (spec != null)
        {
            if (spec.Match != null && spec.Match is not ("lastMessage" or "reportId"))
                throw new ProfileDefectException("$.featureStubs.match", $"\"{spec.Match}\" is not lastMessage or reportId.");
            if (spec.Match == null && spec.MessageByte != 0)
                throw new ProfileDefectException("$.featureStubs.messageByte", "goes with the table-level match of earlier releases. Give each entry its message.");
            if (spec.MessageByte is < 0 or >= MaxData)
                throw new ProfileDefectException("$.featureStubs.messageByte", $"{spec.MessageByte} is not an offset inside a report.");
            if (spec.Reports == null) throw new ProfileDefectException("$.featureStubs.reports", "is null.");
            if (spec.Reports.Count > MaxEntries)
                throw new ProfileDefectException("$.featureStubs.reports", $"{spec.Reports.Count} answers, past the {MaxEntries} the driver holds.");
            int arena = 0;
            var onReports = new HashSet<byte>();
            for (int i = 0; i < spec.Reports.Count; i++)
            {
                var r = spec.Reports[i] ?? throw new ProfileDefectException($"$.featureStubs.reports[{i}]", "is null.");
                string at = $"$.featureStubs.reports[{i}]";
                byte id = Hex(r.Id, at + ".id");
                var data = HexBytes(r.Data, at + ".data");
                if (r.Size is < 0 or > MaxData) throw new ProfileDefectException(at + ".size", $"{r.Size} is not a size from 1 to {MaxData}.");
                var sizes = r.Sizes ?? new List<int>();
                if (sizes.Count > MaxSizes) throw new ProfileDefectException(at + ".sizes", $"{sizes.Count} sizes, past the {MaxSizes} the driver holds.");
                foreach (int s in sizes)
                    if (s is < 1 or > MaxData) throw new ProfileDefectException(at + ".sizes", $"{s} is not a size from 1 to {MaxData}.");
                if (sizes.Distinct().Count() != sizes.Count) throw new ProfileDefectException(at + ".sizes", "names a size twice.");
                int size = sizes.Count > 0 ? Math.Max(sizes.Max(), r.Size) : r.Size > 0 ? r.Size : data.Length;
                if (sizes.Count > 0 && r.Size > 0 && r.Size != sizes.Max())
                    throw new ProfileDefectException(at + ".size", $"{r.Size} is not the largest of sizes, {sizes.Max()}.");
                var pages = r.Pages ?? new List<string>();
                if (r.Pages != null && (r.Data != null || r.Echo))
                    throw new ProfileDefectException(at + ".pages", "an entry gives its answer as data, as pages or by echo, one of the three.");
                if (r.Echo && r.Data != null)
                    throw new ProfileDefectException(at + ".echo", "an echoed answer is the bytes the host wrote, so it takes no data.");
                if (pages.Count > MaxPages) throw new ProfileDefectException(at + ".pages", $"{pages.Count} pages, past the {MaxPages} the driver holds.");
                for (int k = 0; k < pages.Count; k++)
                {
                    var page = HexBytes(pages[k], $"{at}.pages[{k}]");
                    if (size == 0) size = page.Length;
                    if (page.Length > size) throw new ProfileDefectException($"{at}.pages[{k}]", $"{page.Length} bytes exceed the {size}-byte report size.");
                }
                if (size is < 1 or > MaxData)
                    throw new ProfileDefectException(at + ".size", $"the answer is {size} bytes, not 1 to {MaxData}.");
                if (data.Length > size)
                    throw new ProfileDefectException(at + ".data", $"{data.Length} bytes of data exceed the declared {size}-byte report size.");
                if (r.MinLength is { } min && (min < 0 || min > size))
                    throw new ProfileDefectException(at + ".minLength", $"{min} is not 0 to the answer's {size} bytes.");
                if (r.MinLength != null && sizes.Count > 0)
                    throw new ProfileDefectException(at + ".minLength", "sizes sets the smallest request itself.");
                if (r.ReplyHead != null && r.ReplyHead != "requestReportId")
                    throw new ProfileDefectException(at + ".replyHead", $"\"{r.ReplyHead}\" is not a value it takes. The one value is \"requestReportId\".");
                bool legacy = spec.Match == "lastMessage" && r.Message == null;
                var message = r.Message;
                if (message != null)
                {
                    if (spec.Match == "lastMessage")
                        throw new ProfileDefectException(at + ".message", "the table-level lastMessage form makes the message the id. Give the table no match, or the entry no message.");
                    byte on = Hex(message.OnReport, at + ".message.onReport");
                    var value = HexBytes(message.Value, at + ".message.value");
                    if (value.Length is < 1 or > MaxMessage)
                        throw new ProfileDefectException(at + ".message.value", $"a message is 1 to {MaxMessage} bytes, not {value.Length}.");
                    if (message.At is < 0 || message.At + value.Length > MaxData)
                        throw new ProfileDefectException(at + ".message.at", $"{message.At} puts the message outside a SET's {MaxData} bytes.");
                    onReports.Add(on);
                    arena += value.Length;
                }
                else if (legacy)
                {
                    onReports.Add(0xFF);
                    arena += 1;
                }
                if (r.Param != null)
                {
                    if (message == null && !legacy)
                        throw new ProfileDefectException(at + ".param", "a parameter belongs to a message. Give the entry its message.");
                    if (r.Param.Value is < 0 or > 255)
                        throw new ProfileDefectException(at + ".param.value", $"{r.Param.Value} is not a byte.");
                    int pAt = r.Param.At ?? (message != null ? message.At + 2 : spec.MessageByte + 2);
                    if (pAt is < 0 or >= MaxData)
                        throw new ProfileDefectException(at + ".param.at", $"{pAt} is not an offset inside a SET's {MaxData} bytes.");
                }
                if (pages.Count > 0 && message == null && !legacy)
                    throw new ProfileDefectException(at + ".pages", "pages follow a SET, so they belong to a message entry.");
                if (r.Echo && message == null && !legacy)
                    throw new ProfileDefectException(at + ".echo", "an echo answers the SET a message entry follows. Give the entry its message.");
                if (r.Crc32 != null)
                {
                    var seed = HexBytes(r.Crc32.Seed, at + ".crc32.seed");
                    if (seed.Length is < 1 or > MaxSeed)
                        throw new ProfileDefectException(at + ".crc32.seed", $"a seed is 1 to {MaxSeed} bytes.");
                    if (size < 5) throw new ProfileDefectException(at + ".crc32", "a CRC-32 needs an answer of five bytes or more.");
                    if (r.Echo) throw new ProfileDefectException(at + ".crc32", "an echoed answer is the bytes the host wrote, so it takes no CRC.");
                    if (sizes.Count > 0) throw new ProfileDefectException(at + ".crc32", "a CRC ends one size of answer, so it takes no sizes.");
                    if (r.Patch != null && r.Patch.Any(p => p != null && ValueSources.ReadsFile(p)))
                        throw new ProfileDefectException(at + ".crc32", "a value read from a file changes after the CRC is computed.");
                }
                if (r.Patch != null && pages.Count > 0)
                    throw new ProfileDefectException(at + ".patch", "a patch writes over data, so it takes no pages.");
                int pageCount = r.Echo ? 0 : Math.Max(1, pages.Count);
                arena += sizes.Count + pageCount * size;
            }
            if (onReports.Count > MaxOnReports)
                throw new ProfileDefectException("$.featureStubs.reports", $"{onReports.Count} reports carry messages, past the {MaxOnReports} the driver keeps.");
            if (BlobHeader + spec.Reports.Count * BlobEntry + arena > BlobHeader + MaxEntries * BlobEntry + MaxArena)
                throw new ProfileDefectException("$.featureStubs.reports", $"the answers take {arena} bytes, past the {MaxArena} the driver holds.");
        }
        ValueSources.Validate(sources, spec, usbip);
    }

    /// <summary>The checks against the profile's descriptor, and the
    /// entries in their normal form. For the profile's own keys an entry
    /// for a report the descriptor does not declare as a feature report is
    /// refused. In a model the profile takes by name or by identity it is
    /// kept and noted, since HidClass refuses that GET before a driver sees
    /// it, as it does today.</summary>
    internal static List<Normal> ValidateAgainst(ControllerProfile p, FeatureStubSpec spec, bool taken, List<string> notes)
    {
        var map = p.GetOrBuildReportMap();
        var list = Normalize(spec, map, p.RequiresUsbipBackend);
        var pid = PidReportIds(map);
        foreach (var n in list)
        {
            string at = $"$.featureStubs.reports[{n.Index}]";
            if (!DeclaresFeature(map, n.Id))
            {
                string why = map.UsesReportIds
                    ? $"answers report 0x{n.Id:X2}, which the descriptor does not declare as a feature report"
                    : $"answers report 0x{n.Id:X2}, and a descriptor without report IDs is asked only on report 0";
                if (!taken) throw new ProfileDefectException(at + ".id", why + ".");
                notes.Add($"{at}: {why}. HidClass refuses that GET before the driver sees it, so the answer is kept and never served.");
            }
            if (pid.Contains(n.Id))
                throw new ProfileDefectException(at + ".id", $"0x{n.Id:X2} is a report of the PID block, which the driver answers.");
            if (n.Message == null) continue;
            if (!DeclaresFeature(map, n.OnReport))
            {
                string why = $"message.onReport 0x{n.OnReport:X2} is not a feature report the descriptor declares";
                if (!taken) throw new ProfileDefectException(at + ".message.onReport", why + ".");
                notes.Add($"{at}: {why}, so no SET arrives on it and the entry never answers.");
                continue;
            }
            int onSize = FeatureSize(map, n.OnReport);
            if (n.At + n.Message.Length > onSize)
                throw new ProfileDefectException(at + ".message.at",
                    $"the message's bytes {n.At} to {n.At + n.Message.Length - 1} are not inside report 0x{n.OnReport:X2}'s {onSize} bytes.");
            if (n.ParamAt >= onSize)
                throw new ProfileDefectException(at + ".param.at", $"{n.ParamAt} is not inside report 0x{n.OnReport:X2}'s {onSize} bytes.");
        }
        return list;
    }

    private static bool DeclaresFeature(HidReportMap map, byte id) =>
        map.UsesReportIds ? id != 0 && map.Find(HidReportKind.Feature, id) != null
                          : id == 0 && map.Find(HidReportKind.Feature, 0) != null;

    /// <summary>A feature report's bytes as a SET carries them: the report
    /// ID and the data for a descriptor with IDs, the data for one
    /// without.</summary>
    private static int FeatureSize(HidReportMap map, byte id) =>
        map.Find(HidReportKind.Feature, id) is { } r ? r.FrameBytes : 0;

    /// <summary>The report IDs of the PID block a descriptor declares: the
    /// reports with a field on the PID page (0x0F), or in a collection
    /// of it.</summary>
    private static HashSet<byte> PidReportIds(HidReportMap map)
    {
        var set = new HashSet<byte>();
        foreach (var r in map.Reports)
            foreach (var f in r.Fields)
                if (f.UsagePage == 0x0F || f.Usages.Any(u => HidField.PageOf(u.Min) == 0x0F)
                    || (f.Collection >= 0 && f.Collection < map.Collections.Count && map.Collections[f.Collection].UsagePage == 0x0F))
                {
                    if (r.ReportId != 0) set.Add(r.ReportId);
                    break;
                }
        return set;
    }

    /// <summary>The entries with every key explicit. The table-level
    /// <c>lastMessage</c> form of earlier releases becomes message entries
    /// on the channel report: report 0 for a descriptor without report IDs,
    /// else the first feature report it declares.</summary>
    internal static List<Normal> Normalize(FeatureStubSpec spec, HidReportMap map, bool usbip)
    {
        bool lastMessage = spec.Match == "lastMessage";
        byte channel = 0;
        if (lastMessage && map.UsesReportIds)
        {
            var first = map.Reports.Where(r => r.Kind == HidReportKind.Feature && r.ReportId != 0).Select(r => r.ReportId).OrderBy(b => b).FirstOrDefault();
            if (first == 0)
                throw new ProfileDefectException("$.featureStubs.match", "lastMessage needs a feature report for its channel, and the descriptor declares none.");
            channel = first;
        }
        var list = new List<Normal>();
        for (int i = 0; i < spec.Reports.Count; i++)
        {
            var r = spec.Reports[i];
            string at = $"$.featureStubs.reports[{i}]";
            // Without notify an answer publishes on usbip, as every answered
            // GET did through v1.13.0, a table-level match included, and not
            // on UMDF2, whose v1 driver had no tables.
            var n = new Normal { Index = i, Echo = r.Echo, ReplyHead = r.ReplyHead != null, Notify = r.Notify ?? usbip };
            byte id = Hex(r.Id, at + ".id");
            if (lastMessage && r.Message == null)
            {
                n.Id = channel;
                n.Message = new[] { id };
                n.OnReport = channel;
                n.At = spec.MessageByte;
                n.ReplyHead |= spec.MessageByte > 0;
            }
            else
            {
                n.Id = id;
                if (r.Message != null)
                {
                    n.Message = HexBytes(r.Message.Value, at + ".message.value");
                    n.OnReport = Hex(r.Message.OnReport, at + ".message.onReport");
                    n.At = r.Message.At;
                }
            }
            if (r.Param != null)
            {
                n.ParamAt = r.Param.At ?? n.At + 2;
                n.ParamValue = (byte)r.Param.Value;
            }
            var data = HexBytes(r.Data, at + ".data");
            var sizes = r.Sizes ?? new List<int>();
            if (r.Pages != null)
                foreach (string page in r.Pages) n.Pages.Add(HexBytes(page, at + ".pages"));
            else if (!r.Echo)
                n.Pages.Add(data);
            int pageMax = n.Pages.Count == 0 ? 0 : n.Pages.Max(x => x.Length);
            n.Size = sizes.Count > 0 ? sizes.Max() : r.Size > 0 ? r.Size : Math.Max(data.Length, pageMax);
            n.Sizes = sizes.OrderByDescending(s => s).Select(s => (byte)s).ToArray();
            n.MinLength = sizes.Count > 0 ? sizes.Min() : r.MinLength ?? 0;
            n.CrcSeed = r.Crc32 == null ? null : HexBytes(r.Crc32.Seed, at + ".crc32.seed");
            if (r.Patch != null) n.Patches.AddRange(r.Patch);
            list.Add(n);
        }
        return list;
    }

    private static byte Hex(string? s, string at)
    {
        try { return ReportIds.Parse(s); }
        catch (Exception e) when (e is FormatException or OverflowException)
        {
            throw new ProfileDefectException(at, $"\"{s}\" is not a hex byte.");
        }
    }

    private static byte[] HexBytes(string? s, string at)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
        try { return Convert.FromHexString(s.Replace(" ", "")); }
        catch (FormatException) { throw new ProfileDefectException(at, "is not whole hex bytes."); }
    }

    // ── USB/IP ───────────────────────────────────────────────────────────

    /// <summary>A SET_REPORT(Feature) on <paramref name="reportId"/>: its
    /// data, from the report ID for a descriptor that declares report IDs
    /// and from the first data byte for one that declares none, is kept
    /// for the entries that follow that report, up to 64 bytes, and their
    /// pages start again. On a descriptor without report IDs every SET is
    /// report 0's.</summary>
    public void OnSetFeature(byte reportId, ReadOnlySpan<byte> data)
    {
        int slot = Array.IndexOf(_onReports, _usesReportIds ? reportId : (byte)0);
        if (slot < 0) return;
        lock (_lock)
        {
            int n = Math.Min(data.Length, MaxData);
            data[..n].CopyTo(_kept[slot]);
            _keptLength[slot] = n;
            foreach (var e in _entries)
                if (e.IsMessage && e.OnSlot == slot) e.Page = 0;
        }
    }

    /// <summary>The answer to a GET_REPORT(Feature) on
    /// <paramref name="reportId"/> for a caller whose buffer holds
    /// <paramref name="requested"/> bytes, written into <see cref="Answer"/>'s
    /// buffer. Returns its length, -1 when no entry matches and -2 when the
    /// request is shorter than the entry serves. <paramref name="notify"/>
    /// says to publish a feature-read notification. On a descriptor without
    /// report IDs every GET is report 0's.</summary>
    public int Lookup(byte reportId, int requested, out bool notify)
    {
        notify = false;
        lock (_lock)
        {
            var e = Find(_usesReportIds ? reportId : (byte)0);
            if (e == null) return -1;
            int n;
            if (e.Sizes.Length > 0)
            {
                n = -2;
                foreach (byte s in e.Sizes)
                    if (s <= requested) { n = s; break; }
                if (n < 0) return -2;
            }
            else
            {
                if (requested < e.MinLength) return -2;
                n = Math.Min(e.Size, requested);
            }
            Span<byte> full = _scratch.AsSpan(0, e.Size);
            if (e.Echo)
            {
                full.Clear();
                int k = Math.Min(_keptLength[e.OnSlot], e.Size);
                _kept[e.OnSlot].AsSpan(0, k).CopyTo(full);
            }
            else
            {
                e.Pages[Math.Min(e.Page, e.Pages.Length - 1)].CopyTo(full);
                if (e.IsMessage && e.Pages.Length > 1 && e.Page < e.Pages.Length - 1) e.Page++;
                // Issue #62. A value read from a file follows the file, so
                // Steam's updater sees a unit that has already taken the
                // build it offers.
                if (e.FilePatches != null)
                    foreach (var p in e.FilePatches) ValueSources.Apply(full, p, _values);
            }
            if (e.ReplyHead && e.Size > 0) full[0] = reportId;
            notify = e.Notify;
            return n;
        }
    }

    /// <summary>The bytes of the last answer <see cref="Lookup"/> wrote.
    /// The caller sends them before its next lookup: the connection's
    /// reader serves one control transfer at a time.</summary>
    public ReadOnlySpan<byte> Answer(int length) => _scratch.AsSpan(0, length);

    /// <summary>The entry a GET on a report takes: a message entry whose
    /// message and parameter match the last SET on its report, then one
    /// whose message matches and that takes no parameter, then a plain
    /// entry. Null when none does.</summary>
    private Entry? Find(byte reportId)
    {
        Entry? noParam = null, plain = null;
        foreach (var e in _entries)
        {
            if (e.Id != reportId) continue;
            if (!e.IsMessage) { plain ??= e; continue; }
            int len = _keptLength[e.OnSlot];
            var kept = _kept[e.OnSlot];
            if (len < e.At + e.Value.Length || !kept.AsSpan(e.At, e.Value.Length).SequenceEqual(e.Value)) continue;
            if (!e.HasParam) { noParam ??= e; continue; }
            if (e.ParamAt < len && kept[e.ParamAt] == e.ParamValue) return e;
        }
        return noParam ?? plain;
    }

    // ── UMDF2 ────────────────────────────────────────────────────────────

    /// <summary>The blob the UMDF2 driver reads as <c>FeatureAnswers</c>:
    /// a 16-byte header (length, format 4, entry count, report count and
    /// the reports messages arrive on), 16-byte entries (ID, flags, report
    /// slot, message offset and length, parameter offset and byte, page
    /// count, size, minimum length, size count, arena offset), then the
    /// arena: each entry's message, sizes largest first and pages. Every
    /// value is little-endian. driver.h's HM_FEATURE_* constants describe
    /// the same layout.</summary>
    internal byte[] ToBlob()
    {
        int arena = 0;
        foreach (var e in _entries) arena += e.Value.Length + e.Sizes.Length + e.Pages.Length * e.Size;
        var blob = new byte[BlobHeader + _entries.Length * BlobEntry + arena];
        var s = blob.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, (uint)blob.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], BlobFormat);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], (ushort)_entries.Length);
        s[10] = (byte)_onReports.Length;
        _onReports.CopyTo(s[12..]);
        int at = 0;
        int arenaStart = BlobHeader + _entries.Length * BlobEntry;
        for (int i = 0; i < _entries.Length; i++)
        {
            var e = _entries[i];
            var h = s.Slice(BlobHeader + i * BlobEntry, BlobEntry);
            h[0] = e.Id;
            h[1] = e.Flags;
            h[2] = (byte)e.OnSlot;
            h[3] = (byte)e.At;
            h[4] = (byte)e.Value.Length;
            h[5] = (byte)e.ParamAt;
            h[6] = e.ParamValue;
            h[7] = (byte)e.Pages.Length;
            h[8] = (byte)e.Size;
            h[9] = (byte)e.MinLength;
            h[10] = (byte)e.Sizes.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(h[12..], (ushort)at);
            e.Value.CopyTo(s[(arenaStart + at)..]);
            at += e.Value.Length;
            e.Sizes.CopyTo(s[(arenaStart + at)..]);
            at += e.Sizes.Length;
            foreach (var page in e.Pages)
            {
                page.CopyTo(s[(arenaStart + at)..]);
                at += page.Length;
            }
        }
        return blob;
    }

    /// <summary>The blob of a controller with no answers: the header
    /// alone.</summary>
    internal static byte[] EmptyBlob()
    {
        var blob = new byte[BlobHeader];
        BinaryPrimitives.WriteUInt32LittleEndian(blob, BlobHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(blob.AsSpan(4), BlobFormat);
        return blob;
    }
}
