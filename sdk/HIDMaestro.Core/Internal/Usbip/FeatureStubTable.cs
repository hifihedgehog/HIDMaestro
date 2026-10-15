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

    /// <summary>The build Steam ships for a config key, or null. The product
    /// reads Steam's own config, and a check supplies a fixed answer.</summary>
    private readonly Func<string, uint?> _steamStamp;

    private readonly record struct Entry(byte[] Data, int Size, bool Echo, int StampOffset, string? StampKey);

    private FeatureStubTable(bool matchesLastMessage, int messageByte, Func<string, uint?> steamStamp)
    {
        MatchesLastMessage = matchesLastMessage;
        MessageByte = messageByte;
        _steamStamp = steamStamp;
    }

    /// <summary>Build the table for a profile, or null when it declares
    /// none (every profile shipping before #56).</summary>
    public static FeatureStubTable? From(ControllerProfile profile) =>
        From(profile, SteamHardwareUpdater.CurrentStamp);

    /// <summary>Build the table with a given source for Steam's firmware
    /// builds, so a check can exercise an answer with Steam present and
    /// absent on any machine.</summary>
    internal static FeatureStubTable? From(ControllerProfile profile, Func<string, uint?> steamStamp)
    {
        var spec = profile.FeatureStubs;
        if (spec?.Reports == null || spec.Reports.Count == 0) return null;

        var table = new FeatureStubTable(
            string.Equals(spec.Match, "lastMessage", StringComparison.OrdinalIgnoreCase),
            spec.MessageByte, steamStamp);
        foreach (var r in spec.Reports)
        {
            if (string.IsNullOrEmpty(r.Data) && !r.Echo) continue;
            var bytes = string.IsNullOrEmpty(r.Data)
                ? Array.Empty<byte>() : Convert.FromHexString(r.Data);
            // The declared size is the report's wire length; the data is
            // usually shorter because the tail is zeros, exactly as the
            // real device pads its 64-byte feature reports.
            int size = r.Size > 0 ? r.Size : bytes.Length;
            if (bytes.Length > size)
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}' featureStub {r.Id}: {bytes.Length} bytes of data " +
                    $"exceed the declared {size}-byte report size.");

            // A tracked stamp replaces four captured bytes, so it has to lie
            // inside them, and an echoed answer has none to replace.
            var stamp = r.SteamFirmwareStamp;
            if (stamp != null && (r.Echo || string.IsNullOrWhiteSpace(stamp.Key)
                                  || stamp.Offset < 0 || stamp.Offset + 4 > bytes.Length))
                throw new InvalidOperationException(
                    $"Profile '{profile.Id}' featureStub {r.Id}: steamFirmwareStamp needs a key and " +
                    $"four bytes of data at offset {stamp.Offset}, in an answer that is not echoed.");

            table._reports[(r.IdByte, r.Param ?? -1)] =
                new Entry(bytes, size, r.Echo, stamp?.Offset ?? -1, stamp?.Key);
        }
        return table._reports.Count > 0 ? table : null;
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
        // Issue #62. Answer with the build Steam currently ships, so its
        // updater sees a unit that has already taken the update it offers.
        if (entry.StampKey != null && _steamStamp(entry.StampKey) is uint current)
            BinaryPrimitives.WriteUInt32LittleEndian(full.AsSpan(entry.StampOffset, 4), current);
        if (wLength >= full.Length) return full;
        var cut = new byte[wLength];
        Array.Copy(full, cut, wLength);
        return cut;
    }
}
