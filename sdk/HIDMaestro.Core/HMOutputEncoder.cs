using System;
using System.Collections.Generic;
using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>v1.3.5 — inverse of <see cref="HMController.OutputDecoded"/>.
/// Encodes a parsed-field dictionary into a vendor-blob output report's
/// wire-format bytes per the profile's <c>extendedOutputReport</c> spec.
///
/// <para>Used by consumers that drive a real device from synthesized state
/// (audio-reactive rumble, user-configured trigger effects, lightbar mood
/// modes) without reimplementing byte-layout knowledge that the SDK
/// already has via the profile JSON. PadForge's DualSense effect pipeline
/// uses this to replace ~1,400 lines of inline byte packing.</para>
///
/// <para>Stateless: no rolling counters maintained on the SDK side for
/// output-direction consumers. If the wire format requires a sequence
/// counter (DS5 BT output's <c>seqTag</c>), the consumer manages it and
/// passes the current value via the parsed-fields dict.</para>
/// </summary>
public static class HMOutputEncoder
{
    /// <summary>Encode parsed fields into a vendor-blob output report's
    /// byte format per <paramref name="profile"/>'s <c>extendedOutputReport</c>
    /// spec. Returns the full on-wire report buffer including the report
    /// ID at byte 0 and (if the spec declares it) the CRC32 footer.
    ///
    /// <para>Throws <see cref="InvalidOperationException"/> if the profile
    /// has no <c>extendedOutputReport</c> spec.</para></summary>
    public static byte[] Encode(HMProfile profile, IReadOnlyDictionary<string, object> fields)
    {
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        if (fields == null) throw new ArgumentNullException(nameof(fields));

        var spec = profile.ExtendedOutputReport;
        if (spec == null)
            throw new InvalidOperationException(
                $"Profile '{profile.Id}' has no extendedOutputReport spec — nothing to encode against.");

        return VendorBlobCodec.EncodeOutput(spec, fields);
    }
}
