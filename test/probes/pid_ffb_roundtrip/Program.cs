#nullable enable
// HIDMaestro PID FFB shared-section round-trip probe.
//
// Closes the v1.1.35 release blindspot at the SDK-writer + shared-section
// layer (issue #16). Verifies that:
//
//   1. Before any PublishPid* call, the named section does NOT exist —
//      so the driver's OpenFileMappingW returns failure and falls through
//      to STATUS_NO_SUCH_DEVICE / STATUS_NOT_SUPPORTED. The PidEnabled
//      gate (matching vJoy's "FFB not enabled" convention) is intact.
//
//   2. After PublishPidPool, the named section EXISTS, PidEnabled=1, and
//      the Pool fields contain exactly the bytes the consumer published.
//
//   3. After PublishPidBlockLoad, BL_LoadStatus is in the spec-valid 1..3
//      range and the BL fields match the consumer's publish. (v1.1.36
//      driver gates GetFeature(0x12) on this — until the consumer
//      publishes a real BL, BL_LoadStatus stays 0 and the driver returns
//      STATUS_NOT_SUPPORTED for 0x12 even when PidEnabled=1.)
//
//   4. After PublishPidState, the State fields match.
//
// This is the layer the v1.1.36 BL gate fix actually protects. The driver
// IOCTL handler itself is exercised end-to-end by a real DirectInput PID
// FFB consumer (FfbTest, PadForge). That validation runs externally.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using HIDMaestro;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    const ushort ProbeVid = 0xBEEF;
    const ushort ProbePid = 0xF001;

    // Layout matches HIDMAESTRO_SHARED_PID_STATE in driver/driver.h and
    // SharedMemoryIO.cs offsets. 24 bytes total.
    const int SECTION_SIZE = 24;
    const int OFF_SEQNO    = 0;   // ULONG
    const int OFF_ENABLED  = 4;   // UCHAR (+ 3 pad)
    const int OFF_BL_EBI   = 8;
    const int OFF_BL_STAT  = 9;
    const int OFF_BL_RAM   = 10;  // USHORT
    const int OFF_POOL_RAM = 12;  // USHORT
    const int OFF_POOL_MAX = 14;
    const int OFF_POOL_MM  = 15;
    const int OFF_STATE_EBI = 16;
    const int OFF_STATE_FL  = 17;

    // Minimal HID descriptor — gamepad with 1 input button. The probe does
    // not depend on Feature reports being declared; the SDK shared-section
    // path is independent of HidClass's descriptor parsing.
    static readonly byte[] s_descriptor = new byte[] {
        0x05, 0x01, 0x09, 0x05, 0xA1, 0x01,
        0x85, 0x01,
        0x05, 0x09, 0x19, 0x01, 0x29, 0x08,
        0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08,
        0x81, 0x02,
        0xC0,
    };

    static int Main()
    {
        Console.WriteLine("=== HIDMaestro PID FFB shared-section round-trip probe ===");

        var profile = new HMProfileBuilder()
            .Id("pid-ffb-probe")
            .Name("PID FFB Probe")
            .Vendor("HIDMaestro")
            .Vid(ProbeVid).Pid(ProbePid)
            .ProductString("HIDMaestro PID FFB Probe")
            .ManufacturerString("HIDMaestro")
            .Type("gamepad")
            .Connection("usb")
            .Descriptor(s_descriptor)
            .InputReportSize(2)
            .Build();

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        Console.Write("  Creating PID FFB Probe controller... ");
        using var ctrl = ctx.CreateController(profile);
        Console.WriteLine("OK");

        // Section is named by controller index. Index 0 is the first
        // controller in this process.
        const int kIndex = 0;
        string sectionName = $@"Global\HIDMaestroPidState{kIndex}";

        int failures = 0;

        // Step 1 — pre-publish: section MUST NOT exist yet (lazy create).
        Console.Write("  [pre-publish] section not yet created ... ");
        IntPtr h0 = OpenFileMappingW(FILE_MAP_READ, false, sectionName);
        if (h0 != IntPtr.Zero)
        {
            CloseHandle(h0);
            Console.WriteLine("FAIL: section exists before any PublishPid* call");
            failures++;
        }
        else
        {
            Console.WriteLine($"OK (Win32={Marshal.GetLastWin32Error()})");
        }

        // Step 2 — PublishPidPool, then verify section bytes.
        const ushort kRamPoolSize = 0xFFFF;
        const byte   kSimMax       = 16;
        const bool   kDevManaged   = true;
        const bool   kSharedParam  = true;
        ctrl.PublishPidPool(kRamPoolSize, kSimMax, kDevManaged, kSharedParam);

        Console.Write("  [post-publish] Pool fields readable ... ");
        if (!ReadSectionBytes(sectionName, out byte[] section))
        {
            Console.WriteLine("FAIL: section not openable after PublishPidPool");
            failures++;
        }
        else
        {
            byte expectedFlags = (byte)((kDevManaged ? 1 : 0) | (kSharedParam ? 2 : 0));
            uint seqNo = BitConverter.ToUInt32(section, OFF_SEQNO);
            byte enabled = section[OFF_ENABLED];
            ushort ramSize = BitConverter.ToUInt16(section, OFF_POOL_RAM);
            byte simMax = section[OFF_POOL_MAX];
            byte memMgmt = section[OFF_POOL_MM];
            bool ok = enabled == 1
                   && ramSize == kRamPoolSize
                   && simMax == kSimMax
                   && memMgmt == expectedFlags
                   && (seqNo & 1) == 0;
            if (!ok)
            {
                Console.WriteLine($"FAIL: seqNo={seqNo} enabled={enabled} " +
                    $"ram={ramSize:X4} simMax={simMax} memMgmt=0x{memMgmt:X2} " +
                    $"(expected ram={kRamPoolSize:X4} simMax={kSimMax} memMgmt=0x{expectedFlags:X2} enabled=1)");
                failures++;
            }
            else
            {
                Console.WriteLine($"OK (seqNo={seqNo}, enabled=1, " +
                    $"ram=0x{ramSize:X4}, simMax={simMax}, memMgmt=0x{memMgmt:X2})");
            }
        }

        // Step 3 — pre-BL-publish: BL_LoadStatus must still be 0 (the
        // v1.1.36 driver gate that returns STATUS_NOT_SUPPORTED for 0x12
        // until the consumer publishes a real Block Load).
        Console.Write("  [BL pre-publish gate] BL_LoadStatus == 0 ... ");
        if (!ReadSectionBytes(sectionName, out section))
        {
            Console.WriteLine("FAIL: section not readable");
            failures++;
        }
        else
        {
            byte blStatus = section[OFF_BL_STAT];
            if (blStatus != 0)
            {
                Console.WriteLine($"FAIL: BL_LoadStatus={blStatus} but expected 0 (gate broken)");
                failures++;
            }
            else
            {
                Console.WriteLine("OK (driver returns STATUS_NOT_SUPPORTED for 0x12 in this state)");
            }
        }

        // Step 4 — PublishPidBlockLoad, verify BL fields.
        const byte   kBlEbi      = 7;
        const PidLoadStatus kBlStatus = PidLoadStatus.Success;
        const ushort kBlRamAvail = 0xF1F1;
        ctrl.PublishPidBlockLoad(kBlEbi, kBlStatus, kBlRamAvail);

        Console.Write("  [post-publish] BL fields readable ... ");
        if (!ReadSectionBytes(sectionName, out section))
        {
            Console.WriteLine("FAIL: section not readable");
            failures++;
        }
        else
        {
            byte ebi = section[OFF_BL_EBI];
            byte stat = section[OFF_BL_STAT];
            ushort ram = BitConverter.ToUInt16(section, OFF_BL_RAM);
            bool ok = ebi == kBlEbi && stat == (byte)kBlStatus && ram == kBlRamAvail
                   && stat >= 1 && stat <= 3;
            if (!ok)
            {
                Console.WriteLine($"FAIL: ebi={ebi} stat={stat} ram={ram:X4} " +
                    $"(expected ebi={kBlEbi} stat={(byte)kBlStatus} ram={kBlRamAvail:X4})");
                failures++;
            }
            else
            {
                Console.WriteLine($"OK (ebi={ebi}, stat={stat} (Success), ram=0x{ram:X4})");
            }
        }

        // Step 5 — PublishPidState, verify State fields.
        const byte kStateEbi = 0;
        const PidStateFlags kStateFlags = PidStateFlags.ActuatorsEnabled | PidStateFlags.ActuatorPower;
        ctrl.PublishPidState(kStateEbi, kStateFlags);

        Console.Write("  [post-publish] State fields readable ... ");
        if (!ReadSectionBytes(sectionName, out section))
        {
            Console.WriteLine("FAIL: section not readable");
            failures++;
        }
        else
        {
            byte ebi = section[OFF_STATE_EBI];
            byte flags = section[OFF_STATE_FL];
            if (ebi != kStateEbi || flags != (byte)kStateFlags)
            {
                Console.WriteLine($"FAIL: ebi={ebi} flags=0x{flags:X2} " +
                    $"(expected ebi={kStateEbi} flags=0x{(byte)kStateFlags:X2})");
                failures++;
            }
            else
            {
                Console.WriteLine($"OK (ebi={ebi}, flags=0x{flags:X2})");
            }
        }

        if (failures == 0)
        {
            Console.WriteLine("=== PASS: SDK writer + shared-section round-trip verified ===");
            return 0;
        }
        Console.WriteLine($"=== FAIL: {failures} check(s) failed ===");
        return 1;
    }

    static bool ReadSectionBytes(string name, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        IntPtr h = OpenFileMappingW(FILE_MAP_READ, false, name);
        if (h == IntPtr.Zero) return false;
        try
        {
            IntPtr view = MapViewOfFile(h, FILE_MAP_READ, 0, 0, (UIntPtr)SECTION_SIZE);
            if (view == IntPtr.Zero) return false;
            try
            {
                bytes = new byte[SECTION_SIZE];
                Marshal.Copy(view, bytes, 0, SECTION_SIZE);
                return true;
            }
            finally { UnmapViewOfFile(view); }
        }
        finally { CloseHandle(h); }
    }

    const uint FILE_MAP_READ = 0x4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, bool bInheritHandle, string lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr hFileMappingObject, uint dwDesiredAccess,
        uint dwFileOffsetHigh, uint dwFileOffsetLow, UIntPtr dwNumberOfBytesToMap);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);
}
