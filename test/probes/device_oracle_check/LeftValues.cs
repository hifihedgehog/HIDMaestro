// What a killed v2.0.0 program leaves in SOFTWARE\HIDMaestro\Controller<N>
// (design note, decision (c)), shared by case 5's stale-value plan and
// coexist_check's capture: FeatureAnswers, XusbAnswers, XusbCompanion 1,
// LiveFeatureReport 1 and a cached input report ID list. The value that
// caches input report ids carries the name given here until the SDK names
// it.
//
// The design note fixes one thing about a blob: its header carries its
// length and a format number, 4 for every blob of protocol 4
// (docs/design/data-driven-devices.md:320). The layout after the header is
// DD-2's. So each blob here is that header, the blob's total length in
// bytes and then 4, each a 32-bit little-endian value, ahead of a byte
// pattern: a driver that checks only the header takes it for a valid
// v2.0.0 blob. Where the tree's SDK writes one of these values itself for
// a stale profile, case 5 uses the SDK's value in its place
// (Case5Driver.StaleLeftValues). Under the version contract no v1
// controller's driver reads any of them, whatever their bytes.
using System;
using System.Buffers.Binary;
using System.Linq;

internal static class LeftValues
{
    public const int RegBinary = 3, RegDword = 4;

    /// <summary>The format number of every protocol 4 blob.</summary>
    public const uint BlobFormat = 4;

    public static readonly (string Name, int Kind, byte[] Data)[] All =
    {
        ("FeatureAnswers", RegBinary, Blob(0x46, 96)),
        ("XusbAnswers", RegBinary, Blob(0x58, 64)),
        ("XusbCompanion", RegDword, new byte[] { 1, 0, 0, 0 }),
        ("LiveFeatureReport", RegDword, new byte[] { 1, 0, 0, 0 }),
        ("CachedInputReportIds", RegBinary, new byte[] { 0x01, 0x31 }),
    };

    /// <summary>The header (total length, format 4) and a pattern of
    /// <paramref name="length"/> bytes after it.</summary>
    static byte[] Blob(byte tag, int length)
    {
        var b = new byte[8 + length];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0, 4), (uint)b.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4, 4), BlobFormat);
        Enumerable.Range(0, length).Select(i => (byte)(tag ^ i)).ToArray().CopyTo(b, 8);
        return b;
    }
}
