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
//     calibration stub (Sony path still fires, no regression), at the
//     identity scale for 16 per degree/second and 8192 per g, with no
//     CRC over USB (issue #64).
//   - dualsense-bt and dualshock-4-v2-bt: the Bluetooth reports end in the
//     CRC-32 hid-playstation.c and RPCS3 check (seed 0xA3), and the DS4's
//     0x05 is in its Bluetooth field order, which DS4Windows reads without
//     absolute values (issue #64).
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
    /// back with the pad's real gyro denominators instead of the driver's
    /// 16000, and 0x09 returned a genuine Sony OUI MAC instead of the
    /// synthesized locally-administered one. Both were reported as driver
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
    /// <summary>The calibration driver.c serves at offset 1 of the
    /// calibration reports (g_SonyCalibration, issues #43 and #64): gyro
    /// +-8000 with speed 500, accel +-8192, the identity for 16 per
    /// degree/second and 8192 per g. Kept here as a literal so this probe
    /// fails if the driver's copy ever drifts.</summary>
    static readonly byte[] SonyCalibration =
    {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x40, 0x1F, 0xC0, 0xE0, 0x40, 0x1F, 0xC0, 0xE0, 0x40, 0x1F, 0xC0, 0xE0,
        0xF4, 0x01, 0xF4, 0x01,
        0x00, 0x20, 0x00, 0xE0, 0x00, 0x20, 0x00, 0xE0, 0x00, 0x20, 0x00, 0xE0,
    };

    static byte[]? GetFeature(SafeFileHandle h, byte reportId, int len)
    {
        var buf = new byte[Math.Max(len, 64)];
        buf[0] = reportId;
        if (!HidD_GetFeature(h, buf, buf.Length)) return null;
        if (buf[0] != reportId) return null;
        return buf;
    }

    /// <summary>The same calibration in a DualShock 4's Bluetooth report
    /// 0x05 order, pitch+ yaw+ roll+ then pitch- yaw- roll-
    /// (g_SonyCalibrationDs4Bt, issue #64).</summary>
    static readonly byte[] SonyCalibrationDs4Bt =
    {
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x40, 0x1F, 0x40, 0x1F, 0x40, 0x1F, 0xC0, 0xE0, 0xC0, 0xE0, 0xC0, 0xE0,
        0xF4, 0x01, 0xF4, 0x01,
        0x00, 0x20, 0x00, 0xE0, 0x00, 0x20, 0x00, 0xE0, 0x00, 0x20, 0x00, 0xE0,
    };

    /// <summary>True when the report carries the Sony calibration payload.
    /// This replaced a zero-fill check: the payload used to be all zeros,
    /// which is exactly the defect #43 fixed, so "looks like our stub" can
    /// no longer mean "is empty".</summary>
    static bool IsSonyCalibration(SafeFileHandle h, byte reportId, int len)
        => Carries(GetFeature(h, reportId, len), SonyCalibration);

    static bool Carries(byte[]? buf, byte[] payload)
    {
        if (buf == null) return false;
        for (int i = 0; i < payload.Length; i++)
            if (buf[1 + i] != payload[i]) return false;
        return true;
    }

    // CRC-32, reflected polynomial 0xEDB88320, written out here rather than
    // shared with the driver so a wrong driver table cannot agree with
    // itself.
    static readonly uint[] s_crcTable = BuildCrcTable();
    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>True when the last four bytes of a <paramref name="size"/>
    /// byte feature report hold the CRC-32 of 0xA3 followed by the bytes
    /// before them: the check hid-playstation.c ps_get_report applies over
    /// Bluetooth (PS_FEATURE_CRC32_SEED) and RPCS3's GetCalibrationData
    /// applies (btHdr 0xA3).</summary>
    static bool FeatureCrcValid(byte[]? buf, int size, out string detail)
    {
        detail = "read failed";
        if (buf == null || buf.Length < size) return false;
        uint c = 0xFFFFFFFFu;
        c = s_crcTable[(c ^ 0xA3) & 0xFF] ^ (c >> 8);
        for (int i = 0; i < size - 4; i++) c = s_crcTable[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
        c = ~c;
        uint got = (uint)(buf[size - 4] | (buf[size - 3] << 8) | (buf[size - 2] << 16) | (buf[size - 1] << 24));
        detail = $"computed 0x{c:X8}, report 0x{got:X8}";
        return c == got;
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
                Check("dualsense GetFeature(0x05) carries the identity calibration (Sony path intact)",
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

                    // Issue #64: identity for the units consumers submit,
                    // 16 per degree/second and 8192 per g. SDL and
                    // hid-playstation.c scale a gyro axis by
                    // speed_2x * 16 / denominator and an accel axis by
                    // 16384 / range_2g, so both ratios must be exactly 1.
                    Check("gyro denominators equal 16 * speed_2x (16 per degree/second)",
                          gPitch == 16 * speed2x && gYaw == 16 * speed2x && gRoll == 16 * speed2x,
                          $"16 * {speed2x} vs {gPitch}/{gYaw}/{gRoll}");
                    Check("accel ranges equal 16384 (8192 per g)",
                          rx == 16384 && ry == 16384 && rz == 16384, $"x {rx}, y {ry}, z {rz}");
                    // A USB report carries no CRC, so nothing may be
                    // written past the payload.
                    Check("USB 0x05 carries no Bluetooth CRC (bytes 35..40 zero)",
                          c.Skip(35).Take(6).All(b => b == 0),
                          string.Join(" ", c.Skip(35).Take(6).Select(b => b.ToString("X2"))));
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

        // Bluetooth personas (issue #64). Over Bluetooth a Sony pad ends
        // its feature reports with a CRC-32 seeded with 0xA3, and
        // hid-playstation.c checks it on 0x05 for both pads and on 0x09 and
        // 0x20 for the DualSense. RPCS3 closes a pad whose calibration
        // fails it. A DS4 lays out its Bluetooth calibration as pitch+ yaw+
        // roll+ pitch- yaw- roll-, and DS4Windows reads it in that order
        // without absolute values.
        var dsBt = ctx.GetProfile("dualsense-bt")!;
        SnapshotPreexistingHid(dsBt.VendorId, dsBt.ProductId);
        using (var btCtrl = ctx.CreateController(dsBt))
        using (var h = Open(dsBt.VendorId, dsBt.ProductId))
        {
            Check("dualsense-bt HID opens", h != null);
            if (h != null)
            {
                var c05 = GetFeature(h, 0x05, 41);
                Check("dualsense-bt 0x05 carries the DualSense-order calibration",
                      Carries(c05, SonyCalibration));
                Check("dualsense-bt 0x05 ends in a valid Bluetooth CRC",
                      FeatureCrcValid(c05, 41, out string d05), d05);
                var c09 = GetFeature(h, 0x09, 20);
                Check("dualsense-bt 0x09 ends in a valid Bluetooth CRC",
                      FeatureCrcValid(c09, 20, out string d09), d09);
                Check("dualsense-bt 0x09 still carries the locally administered MAC",
                      c09 != null && (c09[1] & 0x02) != 0 && c09[2] == 0x48 && c09[3] == 0x4D);
                var c20 = GetFeature(h, 0x20, 64);
                Check("dualsense-bt 0x20 ends in a valid Bluetooth CRC",
                      FeatureCrcValid(c20, 64, out string d20), d20);
                Check("dualsense-bt 0x20 keeps the firmware fields (version 0x0630 at 44)",
                      c20 != null && (c20[44] | (c20[45] << 8)) == 0x0630 && (c20[20] | (c20[21] << 8)) == 3);
            }
        }

        var ds4Bt = ctx.GetProfile("dualshock-4-v2-bt")!;
        SnapshotPreexistingHid(ds4Bt.VendorId, ds4Bt.ProductId);
        using (var d4BtCtrl = ctx.CreateController(ds4Bt))
        using (var h = Open(ds4Bt.VendorId, ds4Bt.ProductId))
        {
            Check("dualshock-4-v2-bt HID opens", h != null);
            if (h != null)
            {
                var c05 = GetFeature(h, 0x05, 41);
                Check("dualshock-4-v2-bt 0x05 carries the DS4 Bluetooth-order calibration",
                      Carries(c05, SonyCalibrationDs4Bt));
                Check("dualshock-4-v2-bt 0x05 ends in a valid Bluetooth CRC",
                      FeatureCrcValid(c05, 41, out string d05), d05);
                if (c05 != null)
                {
                    // DS4SixAxis.setCalibrationData, useAltGyroCalib false:
                    // plus at 7/9/11, minus at 13/15/17, denominator
                    // plus - minus with no absolute value. Each must be
                    // positive or that axis turns backwards.
                    short LE(int o) => (short)(c05[o] | (c05[o + 1] << 8));
                    int pitch = LE(7) - LE(13), yaw = LE(9) - LE(15), roll = LE(11) - LE(17);
                    Check("DS4Windows' Bluetooth read gives positive denominators on every gyro axis",
                          pitch > 0 && yaw > 0 && roll > 0, $"pitch {pitch}, yaw {yaw}, roll {roll}");
                }
                // SDL reads 0x02 first to switch the pad to report 0x11 and
                // needs at least 35 bytes. The payload stays in the USB order.
                Check("dualshock-4-v2-bt 0x02 still answers with the USB-order calibration",
                      IsSonyCalibration(h, 0x02, 41));
            }
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
