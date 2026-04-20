using System;
using System.Runtime.InteropServices;

namespace HIDMaestro.Internal;

/// <summary>
/// Binary layout of the Windows HID preparsed data blob returned by
/// <c>HidD_GetPreparsedData</c>. This format is undocumented by Microsoft
/// but was reverse-engineered by the Chromium WebHID team and published
/// by the libusb/hidapi project. The first 8 bytes are the magic
/// "HidP KDR" signature. The rest is a self-describing header pointing
/// at two parallel arrays (caps + link collection nodes) that share the
/// trailing variable-length region.
///
/// This file is a data-layout mirror of libusb/hidapi's
/// <c>hidapi_descriptor_reconstruct.h</c> structs, ported to C# under
/// the hidapi project's BSD-style license. Upstream source:
/// https://github.com/libusb/hidapi/blob/master/windows/hidapi_descriptor_reconstruct.h
/// </summary>
internal static class HidPreparsedLayout
{
    /// <summary>Windows HID report type enum, matching <c>HIDP_REPORT_TYPE</c>.</summary>
    public const int HidPInput = 0;
    public const int HidPOutput = 1;
    public const int HidPFeature = 2;
    public const int NumReportTypes = 3;

    /// <summary>Size of the <see cref="HidPpCap"/> struct. The preparsed
    /// data array of caps is laid out with this stride.</summary>
    public const int CapSize = 104;

    /// <summary>Size of <see cref="HidPpLinkCollectionNode"/>.</summary>
    public const int LinkCollectionNodeSize = 16;

    // Layout offsets inside the preparsed-data header (before the caps array).
    public const int OffsetMagic = 0;           // 8 bytes
    public const int OffsetUsage = 8;           // 2 bytes
    public const int OffsetUsagePage = 10;      // 2 bytes
    public const int OffsetReserved = 12;       // 4 bytes (USHORT[2])
    public const int OffsetCapsInfoStart = 16;  // 3 * 8 bytes (hid_pp_caps_info[3])
    public const int OffsetFirstByteOfLinkCollectionArray = 40; // USHORT
    public const int OffsetNumberLinkCollectionNodes = 42;      // USHORT
    public const int OffsetCapsArrayStart = 44;                 // hid_pp_cap[]
}

/// <summary>Per-report-type cap range header. Three of these live in the
/// preparsed data at offset 16, one for each of Input/Output/Feature.
/// <c>FirstCap</c>..<c>LastCap</c> indexes into the shared caps array.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct HidPpCapsInfo
{
    public ushort FirstCap;
    public ushort NumberOfCaps;
    public ushort LastCap;
    public ushort ReportByteLength;
}

/// <summary>Microsoft's internal cap record. 104 bytes. Layout mirrors
/// <c>hid_pp_cap</c> from hidapi_descriptor_reconstruct.h.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct HidPpCap
{
    public ushort UsagePage;
    public byte ReportID;
    public byte BitPosition;
    public ushort ReportSize;
    public ushort ReportCount;
    public ushort BytePosition;
    public ushort BitCount;
    public uint BitField;
    public ushort NextBytePosition;
    public ushort LinkCollection;
    public ushort LinkUsagePage;
    public ushort LinkUsage;

    // Packed flags byte:
    //   bit 0 IsMultipleItemsForArray
    //   bit 1 IsPadding
    //   bit 2 IsButtonCap
    //   bit 3 IsAbsolute
    //   bit 4 IsRange
    //   bit 5 IsAlias
    //   bit 6 IsStringRange
    //   bit 7 IsDesignatorRange
    public byte Flags;
    public byte Reserved1a;
    public byte Reserved1b;
    public byte Reserved1c;

    // UnknownTokens[4] — each 8 bytes: Token(1) + Reserved[3] + BitField(4).
    public ulong UnknownToken0;
    public ulong UnknownToken1;
    public ulong UnknownToken2;
    public ulong UnknownToken3;

    // Union Range / NotRange (16 bytes). We read Range view; NotRange
    // overlays Usage/Reserved1/StringIndex/Reserved2/DesignatorIndex/Reserved3/DataIndex/Reserved4
    // in the same slots. Field names here preserve the Range spelling for clarity.
    public ushort UsageMin;       // NotRange.Usage
    public ushort UsageMax;       // NotRange.Reserved1
    public ushort StringMin;      // NotRange.StringIndex
    public ushort StringMax;      // NotRange.Reserved2
    public ushort DesignatorMin;  // NotRange.DesignatorIndex
    public ushort DesignatorMax;  // NotRange.Reserved3
    public ushort DataIndexMin;   // NotRange.DataIndex
    public ushort DataIndexMax;   // NotRange.Reserved4

    // Union Button / NotButton (20 bytes).
    //   Button.LogicalMin/Max = LONG/LONG
    //   NotButton = HasNull(1) + Reserved4[3] + LogicalMin + LogicalMax + PhysicalMin + PhysicalMax
    // We read both through the same offsets: interpret as NotButton unless IsButtonCap.
    public byte HasNull;          // NotButton.HasNull
    public byte Reserved4a;
    public byte Reserved4b;
    public byte Reserved4c;
    public int LogicalMin;        // shared: Button.LogicalMin == NotButton.LogicalMin offset
    public int LogicalMax;
    public int PhysicalMin;       // NotButton only
    public int PhysicalMax;       // NotButton only

    public uint Units;
    public uint UnitsExp;

    public bool IsMultipleItemsForArray => (Flags & 0x01) != 0;
    public bool IsPadding              => (Flags & 0x02) != 0;
    public bool IsButtonCap            => (Flags & 0x04) != 0;
    public bool IsAbsolute             => (Flags & 0x08) != 0;
    public bool IsRange                => (Flags & 0x10) != 0;
    public bool IsAlias                => (Flags & 0x20) != 0;
    public bool IsStringRange          => (Flags & 0x40) != 0;
    public bool IsDesignatorRange      => (Flags & 0x80) != 0;
}

/// <summary>Link collection tree node. 16 bytes. Mirrors
/// <c>hid_pp_link_collection_node</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct HidPpLinkCollectionNode
{
    public ushort LinkUsage;
    public ushort LinkUsagePage;
    public ushort Parent;
    public ushort NumberOfChildren;
    public ushort NextSibling;
    public ushort FirstChild;
    // Packed: CollectionType:8, IsAlias:1, Reserved:23
    public uint TypeAndFlags;

    public byte CollectionType => (byte)(TypeAndFlags & 0xFF);
    public bool IsAlias        => ((TypeAndFlags >> 8) & 0x1) != 0;
}
