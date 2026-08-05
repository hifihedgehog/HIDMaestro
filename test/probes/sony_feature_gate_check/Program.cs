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

    static SafeFileHandle? Open(ushort vid, ushort pid)
    {
        string? path = null;
        for (int i = 0; i < 50 && path == null; i++)
        {
            path = HidDeviceEnumerator.Enumerate()
                .FirstOrDefault(d => d.VendorId == vid && d.ProductId == pid)?.DevicePath;
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
            }
        }

        // Non-Sony profile that DECLARES feature 0x02 must NOT get the
        // DS4 0x02 stub (this is the reachable collision D3 fixes).
        var pedals = ctx.GetProfile("heusinkveld-ultimate-pedals")!;
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
