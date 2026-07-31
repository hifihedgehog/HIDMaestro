using System;
using System.Buffers.Binary;

namespace HIDMaestro.Internal.Usbip;

/// <summary>USB/IP wire protocol constants and codecs (issue #39).
///
/// <para>Grounded byte-for-byte in the peer we speak to, usbip-win2
/// v.0.9.7.7 (the version this backend pins): the 48-byte packed
/// command header and 16-byte isochronous packet descriptor are
/// <c>include/usbip/proto.h</c>, the OP_* handshake structs are
/// <c>include/usbip/proto_op.h</c>, and USBIP_VERSION 0x0111 with the
/// status codes is <c>include/usbip/consts.h</c>. Everything on the wire
/// is big-endian (<c>drivers/libdrv/pdu.cpp</c> byteswaps every 32-bit
/// field both ways).</para>
///
/// <para>Reply rules this file encodes, from the driver's receive path
/// (<c>drivers/ude/wsk_receive.cpp</c>):</para>
/// <list type="bullet">
/// <item>RET_SUBMIT for a non-isochronous transfer carries
/// <c>number_of_packets = -1</c>; the driver's <c>validate_header</c>
/// normalizes exactly that sentinel.</item>
/// <item>A server response's devid / direction / ep are zero; the driver
/// overwrites direction from the seqnum's low bit
/// (<c>context.h extract_dir</c>) and never reads the other two.</item>
/// <item>RET_SUBMIT payload layout: for IN, the transfer data (compacted
/// for isochronous: padding between packets is not transmitted, but the
/// descriptors' offsets stay the submit-time offsets) followed by the
/// isochronous descriptors; for OUT isochronous, the descriptors alone;
/// for OUT non-isochronous, no payload at all.</item>
/// <item>RET_UNLINK status is -ECONNRESET when the victim was still
/// queued, 0 when its RET_SUBMIT had already been sent.</item>
/// </list></summary>
internal static class UsbipProtocol
{
    public const ushort Version = 0x0111;          // consts.h USBIP_VERSION
    public const int HeaderSize = 48;              // proto.h static_assert(sizeof(header) == 48)
    public const int IsoDescriptorSize = 16;       // proto.h iso_packet_descriptor
    public const int BusIdSize = 32;               // consts.h BUS_ID_SIZE
    public const int DevPathMax = 256;             // consts.h DEV_PATH_MAX
    public const int UsbipUsbDeviceSize = DevPathMax + BusIdSize + 4 + 4 + 4 + 2 + 2 + 2 + 6; // 312

    public const uint CmdSubmit = 1;               // proto.h request_type
    public const uint CmdUnlink = 2;
    public const uint RetSubmit = 3;
    public const uint RetUnlink = 4;

    public const int NumberOfPacketsNonIsoch = -1; // proto.h number_of_packets_non_isoch
    public const int MaxIsoPackets = 1024;         // proto.h max_iso_packets

    public const ushort OpReqImport = 0x8003;      // proto_op.h OP_REQUEST | OP_IMPORT
    public const ushort OpRepImport = 0x0003;
    public const ushort OpReqDevlist = 0x8005;
    public const ushort OpRepDevlist = 0x0005;

    public const uint StOk = 0;                    // consts.h op_status_t
    public const uint StNa = 1;
    public const uint StNodev = 4;

    public const uint SpeedHigh = 3;               // ch9.h usb_device_speed USB_SPEED_HIGH

    // Linux errno values the driver maps through to_windows_status /
    // to_windows_status_isoch (libdrv/usbd_helper.cpp). Sent as negative
    // int32 in ret_submit.status / iso descriptor status / ret_unlink.status.
    public const int EPipe = 32;                   // stall: unsupported/refused request
    public const int EConnReset = 104;             // successful unlink
    public const int ENoDev = 19;                  // device gone

    /// <summary>One parsed CMD header (both submit and unlink shapes).</summary>
    public struct CommandHeader
    {
        public uint Command;
        public uint Seqnum;
        public uint Devid;
        public uint Direction;                     // 0 out, 1 in (proto.h enum direction)
        public uint Ep;                            // endpoint NUMBER (no direction bit)

        // cmd_submit
        public uint TransferFlags;
        public int TransferBufferLength;
        public int StartFrame;
        public int NumberOfPackets;
        public int Interval;
        public ulong Setup;                        // 8 setup bytes, wire order preserved

        // cmd_unlink
        public uint UnlinkSeqnum;

        public bool IsIn => Direction == 1;
        public bool IsIsoch => Command == CmdSubmit && NumberOfPackets != NumberOfPacketsNonIsoch;
    }

    public static CommandHeader ParseHeader(ReadOnlySpan<byte> h)
    {
        var c = new CommandHeader
        {
            Command = BinaryPrimitives.ReadUInt32BigEndian(h),
            Seqnum = BinaryPrimitives.ReadUInt32BigEndian(h[4..]),
            Devid = BinaryPrimitives.ReadUInt32BigEndian(h[8..]),
            Direction = BinaryPrimitives.ReadUInt32BigEndian(h[12..]),
            Ep = BinaryPrimitives.ReadUInt32BigEndian(h[16..]),
        };
        if (c.Command == CmdSubmit)
        {
            c.TransferFlags = BinaryPrimitives.ReadUInt32BigEndian(h[20..]);
            c.TransferBufferLength = BinaryPrimitives.ReadInt32BigEndian(h[24..]);
            c.StartFrame = BinaryPrimitives.ReadInt32BigEndian(h[28..]);
            c.NumberOfPackets = BinaryPrimitives.ReadInt32BigEndian(h[32..]);
            c.Interval = BinaryPrimitives.ReadInt32BigEndian(h[36..]);
            c.Setup = BinaryPrimitives.ReadUInt64LittleEndian(h[40..]); // raw bytes, not a number
        }
        else if (c.Command == CmdUnlink)
        {
            c.UnlinkSeqnum = BinaryPrimitives.ReadUInt32BigEndian(h[20..]);
        }
        return c;
    }

    /// <summary>Write a RET_SUBMIT header. devid/direction/ep are zeroed
    /// per the protocol ("server's responses always have zeroes", see the
    /// comment in usbip-win2 libdrv/pdu.cpp get_isoc_descr).</summary>
    public static void WriteRetSubmit(Span<byte> h, uint seqnum, int status, int actualLength,
                                      int startFrame, int numberOfPackets, int errorCount)
    {
        h[..HeaderSize].Clear();
        BinaryPrimitives.WriteUInt32BigEndian(h, RetSubmit);
        BinaryPrimitives.WriteUInt32BigEndian(h[4..], seqnum);
        BinaryPrimitives.WriteInt32BigEndian(h[20..], status);
        BinaryPrimitives.WriteInt32BigEndian(h[24..], actualLength);
        BinaryPrimitives.WriteInt32BigEndian(h[28..], startFrame);
        BinaryPrimitives.WriteInt32BigEndian(h[32..], numberOfPackets);
        BinaryPrimitives.WriteInt32BigEndian(h[36..], errorCount);
    }

    public static void WriteRetUnlink(Span<byte> h, uint seqnum, int status)
    {
        h[..HeaderSize].Clear();
        BinaryPrimitives.WriteUInt32BigEndian(h, RetUnlink);
        BinaryPrimitives.WriteUInt32BigEndian(h[4..], seqnum);
        BinaryPrimitives.WriteInt32BigEndian(h[20..], status);
    }

    public static void WriteIsoDescriptor(Span<byte> d, uint offset, uint length, uint actualLength, uint status)
    {
        BinaryPrimitives.WriteUInt32BigEndian(d, offset);
        BinaryPrimitives.WriteUInt32BigEndian(d[4..], length);
        BinaryPrimitives.WriteUInt32BigEndian(d[8..], actualLength);
        BinaryPrimitives.WriteUInt32BigEndian(d[12..], status);
    }

    public static (uint offset, uint length) ReadIsoDescriptor(ReadOnlySpan<byte> d)
        => (BinaryPrimitives.ReadUInt32BigEndian(d), BinaryPrimitives.ReadUInt32BigEndian(d[4..]));

    /// <summary>op_common: version, code, status. 8 bytes.</summary>
    public static void WriteOpCommon(Span<byte> b, ushort code, uint status)
    {
        BinaryPrimitives.WriteUInt16BigEndian(b, Version);
        BinaryPrimitives.WriteUInt16BigEndian(b[2..], code);
        BinaryPrimitives.WriteUInt32BigEndian(b[4..], status);
    }

    /// <summary>The 312-byte usbip_usb_device block used by both the import
    /// reply and each devlist row (proto_op.h). Strings are UTF-8, padded
    /// with zeroes; multi-byte integers big-endian.</summary>
    public static void WriteUsbipUsbDevice(Span<byte> b, string path, string busid,
        uint busnum, uint devnum, uint speed, ushort vid, ushort pid, ushort bcdDevice,
        byte devClass, byte devSubClass, byte devProtocol,
        byte configurationValue, byte numConfigurations, byte numInterfaces)
    {
        b[..UsbipUsbDeviceSize].Clear();
        WriteFixedUtf8(b[..DevPathMax], path);
        WriteFixedUtf8(b.Slice(DevPathMax, BusIdSize), busid);
        var t = b[(DevPathMax + BusIdSize)..];
        BinaryPrimitives.WriteUInt32BigEndian(t, busnum);
        BinaryPrimitives.WriteUInt32BigEndian(t[4..], devnum);
        BinaryPrimitives.WriteUInt32BigEndian(t[8..], speed);
        BinaryPrimitives.WriteUInt16BigEndian(t[12..], vid);
        BinaryPrimitives.WriteUInt16BigEndian(t[14..], pid);
        BinaryPrimitives.WriteUInt16BigEndian(t[16..], bcdDevice);
        t[18] = devClass;
        t[19] = devSubClass;
        t[20] = devProtocol;
        t[21] = configurationValue;
        t[22] = numConfigurations;
        t[23] = numInterfaces;
    }

    private static void WriteFixedUtf8(Span<byte> dst, string s)
    {
        int n = System.Text.Encoding.UTF8.GetBytes(s.AsSpan(), dst[..(dst.Length - 1)]);
        _ = n; // remainder already zeroed by caller
    }

    public static string ReadFixedUtf8(ReadOnlySpan<byte> src)
    {
        int end = src.IndexOf((byte)0);
        if (end < 0) end = src.Length;
        return System.Text.Encoding.UTF8.GetString(src[..end]);
    }
}
