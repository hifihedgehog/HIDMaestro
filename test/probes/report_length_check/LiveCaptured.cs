// The live part of ReportLengthCheck for the profiles that carry a
// captured descriptor (capture_fidelity_check): ps-move and
// xbox-360-flight-stick, each read through HidP_GetCaps on every top-level
// collection Windows makes of it, then through its input report.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32.SafeHandles;

internal static partial class Live
{
    /// <summary>One top-level collection as HidP_GetCaps reads it: usage
    /// page, usage and the three report lengths with the report ID byte
    /// (0 for a report the collection does not have).</summary>
    readonly record struct Caps(ushort Page, ushort Usage, int Input, int Output, int Feature)
    {
        public override string ToString() => $"{Page:X4}:{Usage:X4} in {Input} out {Output} feature {Feature}";
    }

    /// <summary>The collections each profile's captured descriptor makes.
    /// ps-move: the Game Pad with input 0x01 (49), outputs 0x02 (49) and
    /// 0x06 (9), the longest of its features 0x03, 0x10, 0x11 and 0xE0 (49),
    /// and two vendor collections whose longest features are 0x05 (23) and
    /// 0xA0 (35). The flight stick: one Joystick with a 12-byte input report
    /// and no report ID, 13 with the zero ID byte HID class puts first.</summary>
    static void CapturedDescriptors(HMContext ctx)
    {
        Captured(ctx, "ps-move",
            new[] { new Caps(0x0001, 0x0005, 49, 49, 49), new Caps(0xFF02, 0x0020, 0, 0, 23), new Caps(0xFF03, 0x0020, 0, 0, 35) },
            new HMGamepadState { Buttons = HMButton.A, Axes = new Dictionary<HMAxis, float> { [HMAxis.Z] = 1.0f } },
            r => r.Length >= 11 && r[0] == 0x01 && (r[2] & 0x40) != 0 && r[5] == 0xFF && r[6] == 0xFF
                 && r[7] == 0x7F && r[8] == 0x7F && r[9] == 0x7F && r[10] == 0x7F,
            "report 0x01 with Cross at byte 2 bit 6, the trigger 0xFF at bytes 5 and 6 and 0x7F at 7 to 10",
            new byte[] { 0x03, 0x04, 0x05, 0x10, 0x11, 0xA0, 0xA1, 0xE0 });
        Captured(ctx, "xbox-360-flight-stick",
            new[] { new Caps(0x0001, 0x0004, 13, 0, 0) },
            new HMGamepadState { Axes = new Dictionary<HMAxis, float> { [HMAxis.Z] = 1.0f, [HMAxis.Rz] = 0.0f } },
            r => r.Length >= 13 && r[0] == 0x00 && r[1] == 0xFF && r[2] == 0x00,
            "the 12-byte report with Z 0xFF at byte 1 and Rz 0 at byte 2, after HID class's zero ID byte",
            Array.Empty<byte>());
    }

    static void Captured(HMContext ctx, string id, Caps[] want, HMGamepadState state, Func<byte[], bool> match,
                         string what, byte[] features)
    {
        var prof = ctx.GetProfile(id) ?? throw new InvalidOperationException($"no profile {id}");
        var before = Present(prof.VendorId, prof.ProductId);
        HMController? c = null;
        try
        {
            c = ctx.CreateController(prof);
            // Every collection of the new device, which arrive one by one.
            var paths = new List<string>();
            var sw = Stopwatch.StartNew();
            while (paths.Count < want.Length && sw.ElapsedMilliseconds < 15000)
            {
                paths = HidDeviceEnumerator.Enumerate()
                    .Where(x => x.VendorId == prof.VendorId && x.ProductId == prof.ProductId && !before.Contains(x.DevicePath))
                    .Select(x => x.DevicePath).ToList();
                if (paths.Count < want.Length) Thread.Sleep(100);
            }
            var got = paths.Select(ReadCaps).Where(x => x != null).Select(x => x!.Value)
                .OrderBy(x => x.Page).ThenBy(x => x.Usage).ToList();
            string Table(IEnumerable<Caps> t) => string.Join("; ", t.Select(x => x.ToString()));
            var expected = want.OrderBy(x => x.Page).ThenBy(x => x.Usage).ToList();
            Check($"{id}: HidP_GetCaps of each collection is {Table(expected)}", got.SequenceEqual(expected), Table(got));
            string? game = paths.FirstOrDefault(p => ReadCaps(p) is { } k && k.Page == want[0].Page && k.Usage == want[0].Usage);
            if (game == null) return;

            // What the driver answers for each declared feature, for the
            // record: no answer is asserted, since the answers belong to
            // the feature tables lane DD-2 moves into profile data.
            foreach (byte f in features)
            {
                string? path = paths.FirstOrDefault(p => ReadCaps(p) is { Feature: > 0 } && HasFeature(p, f));
                if (path == null) { Console.WriteLine($"  [INFO] {id}: feature 0x{f:X2} is in no collection"); continue; }
                var (ok, err, bytes) = GetFeature(path, f);
                Console.WriteLine($"  [INFO] {id}: GET_FEATURE 0x{f:X2}: {(ok ? "answered " + Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 16))) : "error " + err)}");
            }

            using var rd = new HidReader(game, 64);
            Check($"{id}: the input collection opens for reading", rd.IsOpen, rd.OpenError);
            if (!rd.IsOpen) return;
            byte[]? last = null;
            var report = DriveUntil(c, rd, state, r => { last = r; return match(r); }, 3000);
            Check($"{id}: {what}", report != null,
                  report != null ? Convert.ToHexString(report.AsSpan(0, Math.Min(report.Length, 16)))
                                 : last == null ? "no report" : "last " + Convert.ToHexString(last.AsSpan(0, Math.Min(last.Length, 16))) + $" ({last.Length} bytes)");
        }
        finally
        {
            c?.Dispose();
            Thread.Sleep(500);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
                      NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
                      NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);

    // HIDP_REPORT_TYPE HidP_Feature, and the size of HIDP_VALUE_CAPS and
    // HIDP_BUTTON_CAPS (72 bytes each, hidpi.h), whose ReportID is byte 2.
    const int HidP_Feature = 2, CapsSize = 72;
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, IntPtr caps, ref ushort length, IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetButtonCaps(int type, IntPtr caps, ref ushort length, IntPtr data);
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    static Caps? ReadCaps(string path)
    {
        using var h = CreateFileW(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid || !HidD_GetPreparsedData(h, out IntPtr pp)) return null;
        try
        {
            if (HidP_GetCaps(pp, out var k) != HIDP_STATUS_SUCCESS) return null;
            return new Caps(k.UsagePage, k.Usage, k.InputReportByteLength, k.OutputReportByteLength, k.FeatureReportByteLength);
        }
        finally { HidD_FreePreparsedData(pp); }
    }

    /// <summary>Whether the collection declares feature report
    /// <paramref name="id"/>, by its feature value and button caps.</summary>
    static bool HasFeature(string path, byte id)
    {
        using var h = CreateFileW(path, 0, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid || !HidD_GetPreparsedData(h, out IntPtr pp)) return false;
        IntPtr buf = Marshal.AllocHGlobal(CapsSize * 64);
        try
        {
            foreach (bool values in new[] { true, false })
            {
                ushort n = 64;
                int st = values ? HidP_GetValueCaps(HidP_Feature, buf, ref n, pp) : HidP_GetButtonCaps(HidP_Feature, buf, ref n, pp);
                if (st != HIDP_STATUS_SUCCESS) continue;
                for (int i = 0; i < n; i++)
                    if (Marshal.ReadByte(buf, i * CapsSize + 2) == id) return true;
            }
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            HidD_FreePreparsedData(pp);
        }
    }

    static (bool Ok, int Error, byte[] Bytes) GetFeature(string path, byte id)
    {
        using var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return (false, Marshal.GetLastWin32Error(), Array.Empty<byte>());
        int len = ReadCaps(path)?.Feature ?? 0;
        var buf = new byte[Math.Max(len, 2)];
        buf[0] = id;
        bool ok = HidD_GetFeature(h, buf, buf.Length);
        return (ok, ok ? 0 : Marshal.GetLastWin32Error(), buf);
    }
}
