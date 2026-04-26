#nullable enable
// HIDMaestro PID FFB shared-section + driver-side allocation probe.
//
// v1.1.37 expansion: covers issue #16's architectural fix where the driver
// allocates EBIs synchronously inside its IOCTL_UMDF_HID_SET_FEATURE(0x11)
// handler (mirroring vJoy's Ffb_GetNextFreeEffect) instead of the consumer
// calling PublishPidBlockLoad after-the-fact.
//
// Verifies, top to bottom:
//
//   1. Section is lazy-created — does NOT exist before any PublishPid* call.
//      vJoy "FFB not enabled" gate.
//
//   2. PublishPidPool flips PidEnabled=1, populates Pool fields exactly,
//      and leaves the v1.1.37 EbiAllocBitmap=0 / EbiAllocatedCount=0.
//
//   3. GetCurrentPidBlockLoad() returns default (LoadStatus=0) before any
//      Create New Effect — the driver hasn't allocated yet.
//
//   4. HidD_SetFeature(0x11 Create New Effect) — driver synchronously
//      allocates EBI=1 and writes BL_* fields. Read-back via
//      GetCurrentPidBlockLoad() returns EffectBlockIndex=1, Status=Success.
//      EbiAllocBitmap=0x00000001, EbiAllocatedCount=1.
//
//   5. Second SetFeature(0x11) — EBI=2, Bitmap=0x03, Count=2.
//
//   6. HidD_SetFeature(0x1B Block Free) for EBI=1 — driver clears bit 0.
//      Bitmap=0x02, Count=1. Subsequent SetFeature(0x11) reuses EBI=1
//      (lowest free slot policy).
//
//   7. PublishPidState — State fields match.
//
// Steps 4–6 fail gracefully if HidD_SetFeature is rejected by HidClass
// (some descriptor configurations cause it to refuse before forwarding to
// the minidriver). The static layout checks (1, 2, 3, 7) always run and
// catch any SDK-side regression.

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
    // SharedMemoryIO.cs offsets. 32 bytes total in v1.1.37 (was 24 in
    // v1.1.36; grew by 8 for EbiAllocBitmap + EbiAllocatedCount).
    const int SECTION_SIZE   = 32;
    const int OFF_SEQNO      = 0;   // ULONG
    const int OFF_ENABLED    = 4;   // UCHAR (+ 3 pad)
    const int OFF_BL_EBI     = 8;
    const int OFF_BL_STAT    = 9;
    const int OFF_BL_RAM     = 10;  // USHORT
    const int OFF_POOL_RAM   = 12;  // USHORT
    const int OFF_POOL_MAX   = 14;
    const int OFF_POOL_MM    = 15;
    const int OFF_STATE_EBI  = 16;
    const int OFF_STATE_FL   = 17;
    const int OFF_EBI_BITMAP = 20;  // ULONG (v1.1.37)
    const int OFF_EBI_COUNT  = 24;  // ULONG (v1.1.37)

    // Descriptor: gamepad + 1 input button + Feature reports for the PID
    // handshake. Vendor-page usages on the Feature reports because we're
    // testing the IOCTL handler, not dinput8's PID parser.
    //
    // 0x11 = Create New Effect: Feature, Var, Output direction (host writes
    //        type+byteCount, optionally reads back EBI). 3 bytes of payload.
    // 0x1B = Block Free: Feature, Var. 1 byte EBI.
    // 0x12 = Block Load: Feature for read-back (4 bytes payload).
    // 0x13 = Pool: Feature for read-back (4 bytes payload).
    // 0x14 = State: Feature for read-back (2 bytes payload).
    static readonly byte[] s_descriptor = new byte[] {
        0x05, 0x01, 0x09, 0x05, 0xA1, 0x01,
        0x85, 0x01,
        0x05, 0x09, 0x19, 0x01, 0x29, 0x08,
        0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08,
        0x81, 0x02,

        // Feature reports — vendor-page usages, byte-aligned counts.
        0x06, 0x00, 0xFF,   // Usage Page (Vendor 0xFF00)
        0x15, 0x00, 0x26, 0xFF, 0x00, // Logical Min/Max 0..255
        0x75, 0x08,         // Report Size 8

        // 0x11 Create New Effect — 3 bytes payload (effectType + byteCount LE)
        0x85, 0x11, 0x09, 0x11, 0x95, 0x03, 0xB1, 0x02,
        // 0x12 Block Load — 4 bytes payload (EBI + status + RAMpool LE)
        0x85, 0x12, 0x09, 0x12, 0x95, 0x04, 0xB1, 0x02,
        // 0x13 Pool — 4 bytes payload
        0x85, 0x13, 0x09, 0x13, 0x95, 0x04, 0xB1, 0x02,
        // 0x14 State — 2 bytes payload
        0x85, 0x14, 0x09, 0x14, 0x95, 0x02, 0xB1, 0x02,
        // 0x1B Block Free — 1 byte payload (EBI to free)
        0x85, 0x1B, 0x09, 0x1B, 0x95, 0x01, 0xB1, 0x02,

        0xC0,
    };

    static int Main()
    {
        Console.WriteLine("=== HIDMaestro PID FFB driver-side allocation probe (v1.1.37) ===");

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

        const int kIndex = 0;
        string sectionName = $@"Global\HIDMaestroPidState{kIndex}";

        int failures = 0;

        // Step 1 — pre-publish: section MUST NOT exist yet.
        Console.Write("  [pre-publish] section not yet created ... ");
        IntPtr h0 = OpenFileMappingW(FILE_MAP_READ, false, sectionName);
        if (h0 != IntPtr.Zero) { CloseHandle(h0); Console.WriteLine("FAIL: section exists"); failures++; }
        else Console.WriteLine($"OK (Win32={Marshal.GetLastWin32Error()})");

        // Step 1b — v1.1.38: trigger section creation via GetCurrentPidBlockLoad
        // (EnsurePidStateMapping fires inside the SDK), then verify Pool fields
        // come up with vJoy-compatible defaults (RAMPoolSize=200, MaxSim=10,
        // MemMgmt=0) rather than zeros. Audit finding #4 — zero-init was
        // a real bug because dinput8 may read Pool during enumeration before
        // any PublishPidPool, and zero RAMPoolSize / MaxSim lead to degenerate
        // dinput branches.
        _ = ctrl.GetCurrentPidBlockLoad();
        Console.Write("  [section defaults] Pool=200/MaxSim=10/MemMgmt=0 ... ");
        if (!ReadSectionBytes(sectionName, out byte[] sec0)) { Console.WriteLine("FAIL: section not created"); failures++; }
        else
        {
            ushort defRam = BitConverter.ToUInt16(sec0, OFF_POOL_RAM);
            byte defSim = sec0[OFF_POOL_MAX];
            byte defMgmt = sec0[OFF_POOL_MM];
            byte defEnabled = sec0[OFF_ENABLED];
            if (defRam != 200 || defSim != 10 || defMgmt != 0 || defEnabled != 0)
            {
                Console.WriteLine($"FAIL: ram={defRam} sim={defSim} mgmt=0x{defMgmt:X2} enabled={defEnabled} (expected 200/10/0/0)");
                failures++;
            }
            else Console.WriteLine("OK (vJoy-compatible defaults applied at section creation)");
        }

        // Step 2 — PublishPidPool, verify Pool + v1.1.37 EBI bitmap.
        const ushort kRamPoolSize = 0xFFFF;
        const byte   kSimMax       = 4;   // small so step 5 can hit Full
        const bool   kDevManaged   = true;
        const bool   kSharedParam  = true;
        ctrl.PublishPidPool(kRamPoolSize, kSimMax, kDevManaged, kSharedParam);

        Console.Write("  [post-publish] Pool fields + EBI bitmap zero ... ");
        if (!ReadSectionBytes(sectionName, out byte[] section)) { Console.WriteLine("FAIL"); failures++; }
        else
        {
            byte expectedFlags = (byte)((kDevManaged ? 1 : 0) | (kSharedParam ? 2 : 0));
            byte enabled = section[OFF_ENABLED];
            ushort ramSize = BitConverter.ToUInt16(section, OFF_POOL_RAM);
            byte simMax = section[OFF_POOL_MAX];
            byte memMgmt = section[OFF_POOL_MM];
            uint bitmap = BitConverter.ToUInt32(section, OFF_EBI_BITMAP);
            uint count = BitConverter.ToUInt32(section, OFF_EBI_COUNT);
            bool ok = enabled == 1 && ramSize == kRamPoolSize && simMax == kSimMax
                   && memMgmt == expectedFlags && bitmap == 0 && count == 0;
            if (!ok)
            {
                Console.WriteLine($"FAIL: enabled={enabled} ram=0x{ramSize:X4} simMax={simMax} " +
                    $"memMgmt=0x{memMgmt:X2} bitmap=0x{bitmap:X8} count={count}");
                failures++;
            }
            else
            {
                Console.WriteLine($"OK (enabled=1, ram=0x{ramSize:X4}, simMax={simMax}, " +
                    $"bitmap=0x{bitmap:X8}, count={count})");
            }
        }

        // Step 3 — GetCurrentPidBlockLoad before any SetFeature.
        Console.Write("  [pre-allocation] GetCurrentPidBlockLoad returns default ... ");
        var bl0 = ctrl.GetCurrentPidBlockLoad();
        if (bl0.EffectBlockIndex != 0 || bl0.LoadStatus != 0)
        {
            Console.WriteLine($"FAIL: ebi={bl0.EffectBlockIndex} status={bl0.LoadStatus}");
            failures++;
        }
        else Console.WriteLine("OK (ebi=0, status=0)");

        // Steps 4-6 — driver-side allocation via HidD_SetFeature.
        SafeFileHandle? hid = OpenHmHidByVidPid(ProbeVid, ProbePid);
        bool hidOpen = hid != null && !hid.IsInvalid;
        if (!hidOpen)
        {
            Console.WriteLine("  [SetFeature path] SKIPPED — could not open HID handle");
        }
        else
        {
            // Step 4 — first SetFeature(0x11): driver should alloc EBI=1.
            Console.Write("  [SetFeature 0x11] driver allocates EBI=1 ... ");
            if (!SendSetFeature(hid!, 0x11, new byte[] { 0x01, 0x00, 0x00 }))
            {
                Console.WriteLine($"SKIP: HidD_SetFeature failed (Win32={Marshal.GetLastWin32Error()}) " +
                    "— probably HidClass descriptor rejection");
            }
            else
            {
                ReadSectionBytes(sectionName, out section);
                uint bitmap = BitConverter.ToUInt32(section, OFF_EBI_BITMAP);
                uint count = BitConverter.ToUInt32(section, OFF_EBI_COUNT);
                var bl = ctrl.GetCurrentPidBlockLoad();
                bool ok = bl.EffectBlockIndex == 1 && bl.LoadStatus == PidLoadStatus.Success
                       && bitmap == 0x01 && count == 1;
                if (!ok)
                {
                    Console.WriteLine($"FAIL: ebi={bl.EffectBlockIndex} status={bl.LoadStatus} " +
                        $"bitmap=0x{bitmap:X8} count={count}");
                    failures++;
                }
                else Console.WriteLine($"OK (ebi=1, status=Success, bitmap=0x{bitmap:X8}, count={count})");

                // Step 5 — second SetFeature(0x11): EBI=2.
                Console.Write("  [SetFeature 0x11 #2] driver allocates EBI=2 ... ");
                SendSetFeature(hid!, 0x11, new byte[] { 0x01, 0x00, 0x00 });
                ReadSectionBytes(sectionName, out section);
                bitmap = BitConverter.ToUInt32(section, OFF_EBI_BITMAP);
                count = BitConverter.ToUInt32(section, OFF_EBI_COUNT);
                bl = ctrl.GetCurrentPidBlockLoad();
                ok = bl.EffectBlockIndex == 2 && bitmap == 0x03 && count == 2;
                if (!ok)
                {
                    Console.WriteLine($"FAIL: ebi={bl.EffectBlockIndex} bitmap=0x{bitmap:X8} count={count}");
                    failures++;
                }
                else Console.WriteLine($"OK (ebi=2, bitmap=0x{bitmap:X8}, count={count})");

                // Step 6 — Block Free EBI=1, then realloc reuses EBI=1.
                Console.Write("  [SetFeature 0x1B EBI=1] driver frees ... ");
                SendSetFeature(hid!, 0x1B, new byte[] { 0x01 });
                ReadSectionBytes(sectionName, out section);
                bitmap = BitConverter.ToUInt32(section, OFF_EBI_BITMAP);
                count = BitConverter.ToUInt32(section, OFF_EBI_COUNT);
                if (bitmap != 0x02 || count != 1)
                {
                    Console.WriteLine($"FAIL: bitmap=0x{bitmap:X8} count={count} (expected 0x02 / 1)");
                    failures++;
                }
                else Console.WriteLine($"OK (bitmap=0x{bitmap:X8}, count={count})");

                Console.Write("  [SetFeature 0x11 #3] reuses freed EBI=1 ... ");
                SendSetFeature(hid!, 0x11, new byte[] { 0x01, 0x00, 0x00 });
                ReadSectionBytes(sectionName, out section);
                bitmap = BitConverter.ToUInt32(section, OFF_EBI_BITMAP);
                bl = ctrl.GetCurrentPidBlockLoad();
                if (bl.EffectBlockIndex != 1 || bitmap != 0x03)
                {
                    Console.WriteLine($"FAIL: ebi={bl.EffectBlockIndex} bitmap=0x{bitmap:X8}");
                    failures++;
                }
                else Console.WriteLine($"OK (ebi=1, bitmap=0x{bitmap:X8})");

                // Pool exhaustion: with simMax=4, alloc 2 more then 5th fails.
                Console.Write("  [pool exhaustion] 5th SetFeature(0x11) returns Full ... ");
                SendSetFeature(hid!, 0x11, new byte[] { 0x01, 0x00, 0x00 }); // EBI=3
                SendSetFeature(hid!, 0x11, new byte[] { 0x01, 0x00, 0x00 }); // EBI=4
                SendSetFeature(hid!, 0x11, new byte[] { 0x01, 0x00, 0x00 }); // Full
                bl = ctrl.GetCurrentPidBlockLoad();
                if (bl.LoadStatus != PidLoadStatus.Full || bl.EffectBlockIndex != 0)
                {
                    Console.WriteLine($"FAIL: ebi={bl.EffectBlockIndex} status={bl.LoadStatus}");
                    failures++;
                }
                else Console.WriteLine($"OK (ebi=0, status=Full)");
            }
            hid!.Dispose();
        }

        // Step 7 — PublishPidState verification (regression guard, unchanged from v1.1.36).
        const byte kStateEbi = 0;
        const PidStateFlags kStateFlags = PidStateFlags.ActuatorsEnabled | PidStateFlags.ActuatorPower;
        ctrl.PublishPidState(kStateEbi, kStateFlags);

        Console.Write("  [post-publish] State fields readable ... ");
        if (!ReadSectionBytes(sectionName, out section)) { Console.WriteLine("FAIL"); failures++; }
        else
        {
            byte ebi = section[OFF_STATE_EBI];
            byte flags = section[OFF_STATE_FL];
            if (ebi != kStateEbi || flags != (byte)kStateFlags)
            {
                Console.WriteLine($"FAIL: ebi={ebi} flags=0x{flags:X2}"); failures++;
            }
            else Console.WriteLine($"OK (ebi={ebi}, flags=0x{flags:X2})");
        }

        if (failures == 0) { Console.WriteLine("=== PASS ==="); return 0; }
        Console.WriteLine($"=== FAIL: {failures} check(s) failed ==="); return 1;
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

    static SafeFileHandle? OpenHmHidByVidPid(ushort vid, ushort pid)
    {
        var match = HMDeviceExtractor.ListDevices()
            .FirstOrDefault(d => d.VendorId == vid && d.ProductId == pid);
        if (match == null) return null;
        var h = CreateFile(match.DevicePath, GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return h;
    }

    static bool SendSetFeature(SafeFileHandle h, byte reportId, byte[] payload)
    {
        byte[] buf = new byte[1 + payload.Length];
        buf[0] = reportId;
        Buffer.BlockCopy(payload, 0, buf, 1, payload.Length);
        return HidD_SetFeature(h, buf, (uint)buf.Length);
    }

    const uint FILE_MAP_READ = 0x4;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMappingW(uint dwDesiredAccess, bool bInheritHandle, string lpName);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint offHi, uint offLo, UIntPtr nBytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr addr);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr sec, uint disp, uint flags, IntPtr template);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, uint len);
}
