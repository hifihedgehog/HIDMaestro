using System;
using System.Collections.Generic;

namespace HIDMaestro;

/// <summary>v1.3.5 — payload for the <see cref="HMController.OutputDecoded"/>
/// event. Surfaces an inbound vendor-blob output report's parsed-field values
/// (named per the profile's <c>extendedOutputReport</c> spec) plus the raw
/// bytes and the CRC verification result.</summary>
public sealed class HMOutputDecodedEventArgs : EventArgs
{
    /// <summary>The HID report ID (byte 0 of the on-wire packet). Matches
    /// the profile's <c>extendedOutputReport.reportId</c>.</summary>
    public byte ReportId { get; init; }

    /// <summary>Parsed field values keyed by the <c>semantic</c> name from
    /// the profile JSON's <c>extendedOutputReport.fields</c>. Field types
    /// map to runtime types like this:
    /// <list type="bullet">
    /// <item><description><c>uint8</c> / <c>uint8-rolling</c> → <c>byte</c></description></item>
    /// <item><description><c>uint8-axis</c> / <c>uint8-trigger</c> → <c>float</c></description></item>
    /// <item><description><c>rgb24</c> → <c>byte[3]</c> (R, G, B)</description></item>
    /// <item><description><c>bytes-passthrough</c> → <c>byte[]</c> (length per declared range)</description></item>
    /// <item><description><c>button-mask</c> → <c>List&lt;string&gt;</c> of pressed-button names</description></item>
    /// </list></summary>
    public IReadOnlyDictionary<string, object> Fields { get; init; } = null!;

    /// <summary>Raw bytes of the report including the report ID at byte 0.
    /// Provided for consumers that want both the parsed view and the raw
    /// bytes (e.g. forwarding to a real device unchanged).</summary>
    public ReadOnlyMemory<byte> RawBytes { get; init; }

    /// <summary>True if the report's CRC32 footer (when declared by the
    /// profile spec) matched the computed CRC over the declared scope.
    /// Always true when the spec declares no <c>crc32-le</c> field.
    /// Consumers can choose whether to act on a CRC failure (safety) or
    /// continue regardless (diagnostic / lossy environment).</summary>
    public bool CrcValid { get; init; } = true;
}
