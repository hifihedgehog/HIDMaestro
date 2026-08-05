// Sony feature-stub VID gate check (code-audit finding D3).
//
// The driver serves DS4/DS5 calibration stubs for GET_FEATURE report IDs
// 0x02/0x05/0x09/0x20/0xA3. Those IDs are Sony-specific arm-handshake
// reports, but report 0x02 is ALSO the Feature Report ID the default
// Xbox 360 descriptor declares. Before the fix the stub block matched
// unconditionally, so a HidD_GetFeature(0x02) on a non-Sony profile got
// a 41-byte zeroed DS4 calibration blob instead of that profile's own
// feature report. The fix gates the whole block on Sony VID 0x054C.
//
// Asserts:
//   - dualsense (VID 054C): GetFeature(0x05) returns the 41-byte DS5
//     calibration stub (Sony path still fires, no regression).
//   - heusinkveld-ultimate-pedals (VID 30B7): a NON-Sony profile whose
//     descriptor declares Feature report 0x02 (HidClass only forwards a
//     GetFeature for a report ID the descriptor declares, so the profile
//     must declare 0x02 for the collision to be reachable at all). Its
//     GetFeature(0x02) must NOT return a 41-byte DS4 calibration stub.
//     Before the gate it did; that is the exact collision D3 fixes.
//
// Requires elevation (CreateController). Exit 0 PASS / 1 FAIL.

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using HIDMaestro;
using HIDMaestro.Internal;

using Microsoft.Win32.SafeHandles;

internal static class Program
{
    static int s_total, s_failures;
    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int bufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;

    /// <summary>Device-interface paths matching a VID/PID that already
    /// existed before this probe created anything.</summary>
    static readonly System.Collections.Generic.HashSet<string> s_preexistingHid =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records what is already plugged in, so <see cref="Open"/>
    /// can ignore it.
    ///
    /// <para>Necessary because a real Sony pad reports the identical VID and
    /// PID. Taking the first VID/PID match opened the user's own hardware on
    /// any machine with a DualSense attached, and then every assertion
    /// described that pad rather than the driver: the calibration read came
    /// back with the pad's real gyro denominators instead of the neutral
    /// 20000, and 0x09 returned a genuine Sony OUI MAC instead of the
    /// synthesised locally-administered one. Both were reported as driver
    /// failures. Bluetooth-form filtering does not help here, because a
    /// wired pad enumerates under the same USB naming we do.</para>
    ///
    /// <para>Whatever is present before the create belongs to the machine;
    /// whatever appears after belongs to us.</para></summary>
    static void SnapshotPreexistingHid(ushort vid, ushort pid)
    {
        foreach (var d in HidDeviceEnumerator.Enumerate())
            if (d.VendorId == vid && d.ProductId == pid)
                s_preexistingHid.Add(d.DevicePath);
    }

    static SafeFileHandle? Open(ushort vid, ushort pid)
    {
        string? path = null;
        for (int i = 0; i < 50 && path == null; i++)
        {
            path = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(d => d.VendorId == vid && d.ProductId == pid
                                     && !s_preexistingHid.Contains(d.DevicePath))?.DevicePath;
            if (path == null) Thread.Sleep(100);
        }
        if (path == null) return null;
        var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        return h.IsInvalid ? null : h;
    }

    // A DS4/DS5 stub is exactly N zero bytes with byte[0] == the report ID.
    // Returns true if GetFeature succeeded AND the reply looks like a stub.
    /// <summary>The neutral calibration driver.c serves at offset 1 of the
    /// calibration reports (g_SonyCalibration, issue #43). Kept here as a
    /// literal so this probe fails if the driver's copy ever drifts.</summary>
    static readonly byte[] SonyCalibration =
    {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x10, 0x27, 0xF0, 0xD8, 0x10, 0x27, 0xF0, 0xD8, 0x10, 0x27, 0xF0, 0xD8,
        0xF4, 0x01, 0xF4, 0x01,
        0x10, 0x27, 0xF0, 0xD8, 0x10, 0x27, 0xF0, 0xD8, 0x10, 0x27, 0xF0, 0xD8,
    };

    static byte[]? GetFeature(SafeFileHandle h, byte reportId, int len)
    {
        var buf = new byte[Math.Max(len, 64)];
        buf[0] = reportId;
        if (!HidD_GetFeature(h, buf, buf.Length)) return null;
        if (buf[0] != reportId) return null;
        return buf;
    }

    /// <summary>True when the report carries the Sony calibration payload.
    /// This replaced a zero-fill check: the payload used to be all zeros,
    /// which is exactly the defect #43 fixed, so "looks like our stub" can
    /// no longer mean "is empty".</summary>
    static bool IsSonyCalibration(SafeFileHandle h, byte reportId, int len)
    {
        var buf = GetFeature(h, reportId, len);
        if (buf == null) return false;
        for (int i = 0; i < SonyCalibration.Length; i++)
            if (buf[1 + i] != SonyCalibration[i]) return false;
        return true;
    }

    static int Main()
    {
        Console.WriteLine("=== Sony feature-stub VID gate (audit D3) ===");
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (!new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [SKIP] requires elevation");
                return 0;
            }
        }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        // Sony path still fires.
        var ds = ctx.GetProfile("dualsense")!;
        SnapshotPreexistingHid(ds.VendorId, ds.ProductId);
        using (var dsCtrl = ctx.CreateController(ds))
        using (var h = Open(ds.VendorId, ds.ProductId))
        {
            Check("dualsense HID opens", h != null);
            if (h != null)
            {
                Check("dualsense GetFeature(0x05) carries the neutral calibration (Sony path intact)",
                    IsSonyCalibration(h, 0x05, 41));

                // The whole point of #43: a zero denominator makes SDL's
                // sensitivity NaN and makes hid-playstation.c declare the
                // calibration invalid, so compute the denominators here the
                // way those parsers do and require every one to be non-zero.
                var c = GetFeature(h, 0x05, 41);
                if (c == null)
                {
                    Check("dualsense calibration readable", false);
                }
                else
                {
                    short LE(int o) => (short)(c[o] | (c[o + 1] << 8));
                    int gPitch = Math.Abs(LE(7) - LE(1)) + Math.Abs(LE(9) - LE(1));
                    int gYaw = Math.Abs(LE(11) - LE(3)) + Math.Abs(LE(13) - LE(3));
                    int gRoll = Math.Abs(LE(15) - LE(5)) + Math.Abs(LE(17) - LE(5));
                    int speed2x = LE(19) + LE(21);
                    int rx = LE(23) - LE(25), ry = LE(27) - LE(29), rz = LE(31) - LE(33);
                    Check("driver-lane gyro denominators non-zero", gPitch != 0 && gYaw != 0 && gRoll != 0,
                          $"pitch {gPitch}, yaw {gYaw}, roll {gRoll}");
                    Check("driver-lane accel ranges non-zero", rx != 0 && ry != 0 && rz != 0,
                          $"x {rx}, y {ry}, z {rz}");
                    Check("driver-lane speed_2x non-zero", speed2x != 0, $"{speed2x}");
                }

                var pair = GetFeature(h, 0x09, 20);
                Check("dualsense GetFeature(0x09) returns a non-zero locally administered MAC",
                      pair != null && (pair[1] & 0x02) != 0
                      && (pair[1] | pair[2] | pair[3] | pair[4] | pair[5] | pair[6]) != 0,
                      pair == null ? "read failed"
                          : string.Join(":", pair.Skip(1).Take(6).Select(b => b.ToString("X2"))));

                // #43 second round: F1 22 reads 0x20 and abandons the pad on
                // the zeros this used to serve, before it ever asks for
                // calibration. Assert the real blob, decoded at the offsets
                // hid-playstation.c and dualsense-tester agree on, rather
                // than just "not all zero".
                var fw = GetFeature(h, 0x20, 64);
                if (fw == null)
                {
                    Check("dualsense GetFeature(0x20) readable", false);
                }
                else
                {
                    int U16(int o) => fw[o] | (fw[o + 1] << 8);
                    uint U32(int o) => (uint)(fw[o] | (fw[o + 1] << 8) | (fw[o + 2] << 16) | (fw[o + 3] << 24));
                    string date = Encoding.ASCII.GetString(fw, 1, 11);
                    string time = Encoding.ASCII.GetString(fw, 12, 8);
                    int fwType = U16(20);
                    uint hwInfo = U32(24);
                    uint mainFw = U32(28);

                    // Spelled out in usbip_server_check too, against the
                    // composite lane. Both backends must serve this same
                    // literal, so drift in either one fails a test rather
                    // than shipping two different DualSense identities.
                    byte[] expect20 = {
                        0x20, 0x4A, 0x75, 0x6C, 0x20, 0x20, 0x34, 0x20,
                        0x32, 0x30, 0x32, 0x35, 0x31, 0x30, 0x3A, 0x31,
                        0x30, 0x3A, 0x33, 0x32, 0x03, 0x00, 0x04, 0x00,
                        0x10, 0x13, 0x00, 0x00, 0x2A, 0x00, 0x10, 0x01,
                        0x01, 0xC8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00, 0x30, 0x06, 0x00, 0x00,
                        0x3C, 0x00, 0x01, 0x00, 0x0A, 0x00, 0x02, 0x00,
                        0x06, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
                    };
                    Check("0x20 matches the composite backend byte for byte",
                          fw.SequenceEqual(expect20),
                          $"{fw.Zip(expect20, (a, b) => a == b).Count(x => x)}/64 bytes equal");
                    Check("0x20 is not the old zero stub",
                          fw.Skip(1).Any(b => b != 0) && fw.Count(b => b != 0) > 8,
                          $"{fw.Count(b => b != 0)} non-zero bytes of 64");
                    Check("0x20 build date is printable ASCII",
                          date.All(ch => ch >= 0x20 && ch < 0x7F), $"\"{date}\"");
                    Check("0x20 build time is printable ASCII",
                          time.All(ch => ch >= 0x20 && ch < 0x7F), $"\"{time}\"");
                    // dualsense-tester renders Factory Info only for
                    // fwType 2 or 3. WinUHid's own default blob reports 4,
                    // which is why it is not used verbatim.
                    Check("0x20 fwType satisfies the dualsense-tester render gate",
                          fwType == 2 || fwType == 3, $"fwType {fwType}");
                    Check("0x20 hwInfo non-zero", hwInfo != 0, $"0x{hwInfo:X8}");
                    Check("0x20 mainFwVersion non-zero", mainFw != 0, $"0x{mainFw:X8}");

                    // Serving real values above turns on dualsense-tester's
                    // traceability branch, whose first act is reading 0x22.
                    // Every ID outside the gate returns STATUS_NOT_SUPPORTED,
                    // so if that read fails the panel that renders today
                    // starts failing: fixing F1 22 would have broken it.
                    bool traceOn = (hwInfo & 0xFFFF) >= 777 && mainFw >= 65655;
                    var patch = GetFeature(h, 0x22, 64);
                    Check("0x22 is readable, so the traceability branch cannot fault",
                          patch != null,
                          traceOn ? "branch is ON for this blob" : "branch off, still must not error");
                    // getBtPatchInfo bails unless byte 0 is the report ID.
                    Check("0x22 carries its report ID", patch != null && patch[0] == 0x22,
                          patch == null ? "read failed" : $"0x{patch[0]:X2}");
                }
            }
        }

        // DS4 over USB: 0x12 is the pairing-info read, the DS4's 0x09. Our
        // USB DS4 descriptors have always declared it and the driver never
        // served it, so it answered STATUS_NOT_SUPPORTED. That is the one
        // Sony read whose absence is fatal rather than cosmetic:
        // hid-playstation's dualshock4_get_mac_address caller returns
        // ERR_PTR on failure and never instantiates the device. SDL's
        // ReadWiredSerial additionally rejects an all-zero MAC, so "present
        // but zeroed" would not have been enough either.
        var ds4 = ctx.GetProfile("dualshock-4-v2")!;
        SnapshotPreexistingHid(ds4.VendorId, ds4.ProductId);
        using (var d4Ctrl = ctx.CreateController(ds4))
        using (var h = Open(ds4.VendorId, ds4.ProductId))
        {
            Check("dualshock-4-v2 HID opens", h != null);
            if (h != null)
            {
                var pair = GetFeature(h, 0x12, 16);
                Check("DS4 GetFeature(0x12) is served at all (was NOT_SUPPORTED)", pair != null);
                Check("DS4 0x12 MAC is non-zero (SDL ReadWiredSerial rejects all-zero)",
                      pair != null && (pair[1] | pair[2] | pair[3] | pair[4] | pair[5] | pair[6]) != 0,
                      pair == null ? "read failed"
                          : string.Join(":", pair.Skip(1).Take(6).Select(b => b.ToString("X2"))));
                Check("DS4 0x12 MAC is locally administered (cannot collide with a real pad)",
                      pair != null && (pair[1] & 0x02) != 0);
                // DS4 must NOT answer the DS5-only firmware report.
                Check("DS4 does not answer DS5's 0x20", GetFeature(h, 0x20, 64) == null);
            }
        }

        // DualSense Edge is a different firmware line. Sony's updater data
        // records the base pad as type 0x0004 and the Edge as type 0x0044,
        // and our captured base blob carries 0x0004 with version 0x0630,
        // matching Sony's "DualSense, Type 0004, 0x0630" entry exactly.
        var edge = ctx.GetProfile("dualsense-edge")!;
        SnapshotPreexistingHid(edge.VendorId, edge.ProductId);
        using (var eCtrl = ctx.CreateController(edge))
        using (var h = Open(edge.VendorId, edge.ProductId))
        {
            Check("dualsense-edge HID opens", h != null);
            if (h != null)
            {
                var fw = GetFeature(h, 0x20, 64);
                if (fw == null) { Check("edge 0x20 readable", false); }
                else
                {
                    int U16(int o) => fw[o] | (fw[o + 1] << 8);
                    Check("edge 0x20 swSeries is the Edge line 0x0044, not the base pad's 0x0004",
                          U16(22) == 0x0044, $"0x{U16(22):X4}");
                    Check("edge 0x20 updateVersion is an Edge firmware 0x0217",
                          U16(44) == 0x0217, $"0x{U16(44):X4}");
                    Check("edge 0x20 still satisfies the dualsense-tester render gate",
                          U16(20) == 2 || U16(20) == 3, $"fwType {U16(20)}");
                    Check("edge 0x22 is readable (its traceability branch is always on)",
                          GetFeature(h, 0x22, 64) != null);
                }
            }
        }

        // The base pad must NOT pick up the Edge's firmware line.
        using (var dsCtrl2 = ctx.CreateController(ds))
        using (var h = Open(ds.VendorId, ds.ProductId))
        {
            var fw = h != null ? GetFeature(h, 0x20, 64) : null;
            Check("base dualsense keeps swSeries 0x0004 (Edge patch is PID-scoped)",
                  fw != null && (fw[22] | (fw[23] << 8)) == 0x0004,
                  fw == null ? "read failed" : $"0x{fw[22] | (fw[23] << 8):X4}");
            Check("base dualsense keeps updateVersion 0x0630",
                  fw != null && (fw[44] | (fw[45] << 8)) == 0x0630,
                  fw == null ? "read failed" : $"0x{fw[44] | (fw[45] << 8):X4}");
        }

        // Non-Sony profile that DECLARES feature 0x02 must NOT get the
        // DS4 0x02 stub (this is the reachable collision D3 fixes).
        var pedals = ctx.GetProfile("heusinkveld-ultimate-pedals")!;
        SnapshotPreexistingHid(pedals.VendorId, pedals.ProductId);
        using (var pCtrl = ctx.CreateController(pedals))
        using (var h = Open(pedals.VendorId, pedals.ProductId))
        {
            Check("ultimate-pedals HID opens", h != null);
            if (h != null)
                Check("non-Sony GetFeature(0x02) is NOT a DS4 calibration stub (collision fixed)",
                    !IsSonyCalibration(h, 0x02, 41));
        }

        try { HMContext.RemoveAllVirtualControllers(); } catch { }
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
