using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace HIDMaestro.Internal.Usbip;

/// <summary>Issue #56. The feature-report answers a persona serves, as
/// profile data rather than as code.
///
/// <para>A composite persona has to answer the host's GET_REPORT(Feature)
/// exchanges or the software that claims the device gives up on it. Until
/// now the only such table was Sony's, hardcoded in
/// <see cref="UsbipEmulatedDevice"/> because it synthesizes per-controller
/// values. A persona whose answers are constants carries them here
/// instead.</para>
///
/// <para>Two keying rules, because two protocols in scope disagree about
/// what identifies a feature report. Sony's reports carry a report id, so
/// <c>match: "reportId"</c> looks up by it. Valve's Steam Deck protocol
/// declares no report ids at all: the host writes a message with
/// SET_REPORT(Feature) and then reads its answer with GET_REPORT(Feature),
/// so <c>match: "lastMessage"</c> looks up by the message id that write
/// carried. That is the exchange a real Deck performs (SET 0x83 then GET
/// 0x83 for attributes, SET 0xAE then GET 0xAE for the serial string), and
/// the shape SDL's own driver uses in
/// <c>SDL_hidapi_steam.c</c>.</para></summary>
internal sealed class FeatureStubTable
{
    /// <summary>True when lookups key off the preceding SET_REPORT's message
    /// id rather than the request's report id.</summary>
    public bool MatchesLastMessage { get; }

    /// <summary>Payload byte carrying the message id. See
    /// <see cref="FeatureStubSpec.MessageByte"/>.</summary>
    public int MessageByte { get; }

    // Keyed by (message id, parameter). A parameter of -1 is the entry that
    // answers the message whatever parameter it carried.
    private readonly Dictionary<(byte Key, int Param), Entry> _reports = new();

    /// <summary>The controller's values the patches read (mechanism 5).</summary>
    private readonly ValueSources.Values _values;

    /// <summary>An answer: its bytes padded to the report's size with every
    /// patch from the index and the serial already written, and the patches
    /// read from a file, which are written as the answer is asked for.</summary>
    private readonly record struct Entry(byte[] Data, int Size, bool Echo, AnswerPatchSpec[]? FilePatches);

    private FeatureStubTable(bool matchesLastMessage, int messageByte, ValueSources.Values values)
    {
        MatchesLastMessage = matchesLastMessage;
        MessageByte = messageByte;
        _values = values;
    }

    /// <summary>Build the table for one controller of a profile, or null
    /// when the profile declares none (every profile shipping before #56).
    /// <paramref name="serial"/> is the serial the device serves, which a
    /// <c>serial</c> patch writes.</summary>
    public static FeatureStubTable? From(ControllerProfile profile, int index, string? serial) =>
        From(profile, index, serial, ValueSources.ReadFile);

    /// <summary>Build the table with a given reader for the files value
    /// sources read, so a check can exercise an answer with Steam present
    /// and absent on any machine.</summary>
    internal static FeatureStubTable? From(ControllerProfile profile, int index, string? serial, ValueSources.FileReader files)
    {
        var spec = profile.FeatureStubs;
        if (spec?.Reports == null || spec.Reports.Count == 0) return null;
        try { Validate(profile); }
        catch (ProfileDefectException e)
        {
            throw new InvalidOperationException($"Profile '{profile.Id}': {e.JsonPath}: {e.Reason}", e);
        }

        var values = new ValueSources.Values(index, serial, profile.ValueSources, files);
        var table = new FeatureStubTable(
            string.Equals(spec.Match, "lastMessage", StringComparison.OrdinalIgnoreCase),
            spec.MessageByte, values);
        foreach (var r in spec.Reports)
        {
            if (string.IsNullOrEmpty(r.Data) && !r.Echo) continue;
            var bytes = string.IsNullOrEmpty(r.Data)
                ? Array.Empty<byte>() : Convert.FromHexString(r.Data);
            // The declared size is the report's wire length; the data is
            // usually shorter because the tail is zeros, exactly as the
            // real device pads its 64-byte feature reports.
            int size = r.Size > 0 ? r.Size : bytes.Length;
            var full = new byte[size];
            bytes.CopyTo(full, 0);
            List<AnswerPatchSpec>? filePatches = null;
            foreach (var p in r.Patch ?? new List<AnswerPatchSpec>())
            {
                if (ValueSources.ReadsFile(p)) (filePatches ??= new()).Add(p);
                else ValueSources.Apply(full, p, values);
            }
            table._reports[(r.IdByte, r.Param ?? -1)] = new Entry(full, size, r.Echo, filePatches?.ToArray());
        }
        return table._reports.Count > 0 ? table : null;
    }

    /// <summary>The checks a profile's answers meet when it loads: each
    /// answer's data is hex no longer than its size, and its value sources
    /// and patches fit (<see cref="ValueSources.Validate"/>). Throws
    /// <see cref="ProfileDefectException"/> with the JSON path.</summary>
    internal static void Validate(ControllerProfile profile)
    {
        var spec = profile.FeatureStubs;
        if (spec?.Reports != null)
            for (int i = 0; i < spec.Reports.Count; i++)
            {
                var r = spec.Reports[i] ?? throw new ProfileDefectException($"$.featureStubs.reports[{i}]", "is null.");
                string at = $"$.featureStubs.reports[{i}]";
                try { _ = r.IdByte; }
                catch (Exception e) when (e is FormatException or OverflowException)
                {
                    throw new ProfileDefectException(at + ".id", $"\"{r.Id}\" is not a hex byte.");
                }
                byte[] bytes;
                try { bytes = string.IsNullOrEmpty(r.Data) ? Array.Empty<byte>() : Convert.FromHexString(r.Data); }
                catch (FormatException) { throw new ProfileDefectException(at + ".data", "is not whole hex bytes."); }
                int size = r.Size > 0 ? r.Size : bytes.Length;
                if (bytes.Length > size)
                    throw new ProfileDefectException(at + ".data", $"{bytes.Length} bytes of data exceed the declared {size}-byte report size.");
            }
        ValueSources.Validate(profile.ValueSources, spec, profile.RequiresUsbipBackend);
    }

    /// <summary>The answer for a key, padded to the declared report size and
    /// truncated to what the host asked for. Null when the table has no
    /// entry, which stalls, as a real device does for a message it does not
    /// implement.</summary>
    public byte[]? Lookup(byte key, ushort wLength) => Lookup(key, -1, wLength, default);

    public byte[]? Lookup(byte key, int param, ushort wLength) => Lookup(key, param, wLength, default);

    /// <summary>The answer for a message and the parameter it carried. An
    /// entry declared for that exact parameter wins; otherwise the entry
    /// declared for the message alone answers, as it does for every message
    /// that takes no parameter.</summary>
    /// <param name="written">The message the host wrote, for an entry that
    /// answers by echoing it.</param>
    public byte[]? Lookup(byte key, int param, ushort wLength, ReadOnlySpan<byte> written)
    {
        if (!_reports.TryGetValue((key, param), out var entry)
            && !_reports.TryGetValue((key, -1), out entry)) return null;
        var full = new byte[entry.Size];
        ReadOnlySpan<byte> body = entry.Echo && written.Length > 0 ? written : entry.Data;
        body[..Math.Min(body.Length, full.Length)].CopyTo(full);
        // Issue #62. A value read from a file follows the file, so Steam's
        // updater sees a unit that has already taken the build it offers.
        if (entry.FilePatches != null)
            foreach (var p in entry.FilePatches) ValueSources.Apply(full, p, _values);
        if (wLength >= full.Length) return full;
        var cut = new byte[wLength];
        Array.Copy(full, cut, wLength);
        return cut;
    }
}
