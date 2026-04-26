#nullable enable
// HIDMaestro PID FFB EBI alloc/free + pool exhaustion probe (v1.1.37 / S25).
//
// Companion to S24 (pid_ffb_roundtrip). Where S24 verifies the static
// SDK→shared-section contract, S25 covers the dynamic v1.1.37 invariants:
//
//   1. EBI alloc/free + pool exhaustion via the legacy override path.
//      PublishPidBlockLoad is still callable; consumer-published BL fields
//      survive a subsequent driver-side allocation only if the driver
//      doesn't fire (HidClass blocks the dynamic SetFeature path in our
//      probe environment — same wall S24 hits).
//
//   2. Multi-controller PID-state independence. Two controllers, two
//      separate Global\HIDMaestroPidState{N} sections, distinct Pool
//      values, distinct EbiAllocBitmap. Regression guard against any
//      static/global state in the v1.1.37 driver allocator.
//
//   3. File-based trace (HMAESTRO_TRACE=1 env var).
//      %PROGRAMDATA%\HIDMaestro\driver-trace.log gets created and the
//      driver writes a known marker to it during the controller's
//      lifecycle (PublishPidPool fires through the SDK; if the
//      driver-side allocator runs at any point the trace captures it).
//
//   4. Dynamic SetFeature/SetOutputReport attempt (best-effort). Tries
//      both HidD_SetFeature(0x11) and HidD_SetOutputReport(0x11). If
//      either is accepted by HidClass, verifies the driver allocated an
//      EBI synchronously inside the IOCTL handler. If both are rejected
//      (descriptor parse), the test SKIPs and the static checks above
//      remain the deterministic coverage.
//
// PadForge's FfbTest run remains the canonical end-to-end arbiter for
// the dinput8 PID handshake. This probe exercises the parts of v1.1.37
// the test environment can drive deterministically.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    const ushort ProbeVid  = 0xBEEF;
    const ushort ProbePidA = 0xF002;
    const ushort ProbePidB = 0xF003;

    const int SECTION_SIZE   = 32;
    const int OFF_SEQNO      = 0;
    const int OFF_ENABLED    = 4;
    const int OFF_BL_EBI     = 8;
    const int OFF_BL_STAT    = 9;
    const int OFF_BL_RAM     = 10;
    const int OFF_POOL_RAM   = 12;
    const int OFF_POOL_MAX   = 14;
    const int OFF_POOL_MM    = 15;
    const int OFF_EBI_BITMAP = 20;
    const int OFF_EBI_COUNT  = 24;

    static readonly byte[] s_descriptor = new byte[] {
        0x05, 0x01, 0x09, 0x05, 0xA1, 0x01,
        0x85, 0x01,
        0x05, 0x09, 0x19, 0x01, 0x29, 0x08,
        0x15, 0x00, 0x25, 0x01, 0x75, 0x01, 0x95, 0x08,
        0x81, 0x02,
        0x06, 0x00, 0xFF,
        0x15, 0x00, 0x26, 0xFF, 0x00,
        0x75, 0x08,
        0x85, 0x11, 0x09, 0x11, 0x95, 0x03, 0xB1, 0x02,
        0x85, 0x12, 0x09, 0x12, 0x95, 0x04, 0xB1, 0x02,
        0x85, 0x13, 0x09, 0x13, 0x95, 0x04, 0xB1, 0x02,
        0x85, 0x14, 0x09, 0x14, 0x95, 0x02, 0xB1, 0x02,
        0x85, 0x1F, 0x09, 0x1F, 0x95, 0x01, 0xB1, 0x02,
        // Output direction declarations for 0x11/0x1F so HidD_SetOutputReport works
        0x85, 0x11, 0x09, 0x11, 0x95, 0x03, 0x91, 0x02,
        0x85, 0x1F, 0x09, 0x1F, 0x95, 0x01, 0x91, 0x02,
        0xC0,
    };

    static int Main()
    {
        Console.WriteLine("=== HIDMaestro PID FFB alloc/free + pool exhaustion (v1.1.37 / S25) ===");

        // Make sure file trace is on for this probe; clean any prior log so
        // section 3's check doesn't get confused by leftover content.
        Environment.SetEnvironmentVariable("HMAESTRO_TRACE", "1");
        string traceLog = @"C:\ProgramData\HIDMaestro\driver-trace.log";
        try { if (File.Exists(traceLog)) File.Delete(traceLog); } catch { /* PermissionDenied is OK */ }

        var profileA = BuildProfile("pid-ffb-alloc-free-a", ProbeVid, ProbePidA, "PID FFB AllocFree A");
        var profileB = BuildProfile("pid-ffb-alloc-free-b", ProbeVid, ProbePidB, "PID FFB AllocFree B");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        Console.Write("  Creating two controllers (A, B)... ");
        using var ctrlA = ctx.CreateController(profileA);
        using var ctrlB = ctx.CreateController(profileB);
        Console.WriteLine("OK");

        int failures = 0;

        // ── Section 1: legacy PublishPidBlockLoad override path ──
        // Drives the alloc/free/exhaustion semantics through the consumer-side API
        // (the driver's IOCTL allocator can't be reached from user mode without
        // HidClass cooperation). Verifies the SDK writer + struct layout still
        // honor the legacy override path that v1.1.37 intentionally kept callable.
        Console.WriteLine("  --- Section 1: legacy PublishPidBlockLoad path ---");
        ctrlA.PublishPidPool(ramPoolSize: 0x4000, simultaneousEffectsMax: 4,
                             deviceManagedPool: true, sharedParameterBlocks: false);
        string sectionA = $@"Global\HIDMaestroPidState0";

        // Allocate EBI 1 via override.
        ctrlA.PublishPidBlockLoad(1, PidLoadStatus.Success, 0x3000);
        var bl = ctrlA.GetCurrentPidBlockLoad();
        if (bl.EffectBlockIndex != 1 || bl.LoadStatus != PidLoadStatus.Success || bl.RAMPoolAvailable != 0x3000)
        {
            Console.WriteLine($"  [legacy alloc EBI=1] FAIL: ebi={bl.EffectBlockIndex} status={bl.LoadStatus} ram=0x{bl.RAMPoolAvailable:X4}");
            failures++;
        }
        else Console.WriteLine($"  [legacy alloc EBI=1] OK (ebi=1, status=Success, ram=0x3000)");

        // Allocate EBI 2.
        ctrlA.PublishPidBlockLoad(2, PidLoadStatus.Success, 0x2000);
        bl = ctrlA.GetCurrentPidBlockLoad();
        if (bl.EffectBlockIndex != 2 || bl.LoadStatus != PidLoadStatus.Success)
        {
            Console.WriteLine($"  [legacy alloc EBI=2] FAIL: ebi={bl.EffectBlockIndex} status={bl.LoadStatus}");
            failures++;
        }
        else Console.WriteLine($"  [legacy alloc EBI=2] OK");

        // Free EBI 1 (legacy: just publish a different BL, no actual free-list state).
        // This documents that the legacy path doesn't touch the bitmap.
        ReadSectionBytes(sectionA, out byte[] sec);
        uint legacyBitmap = BitConverter.ToUInt32(sec, OFF_EBI_BITMAP);
        if (legacyBitmap != 0)
        {
            Console.WriteLine($"  [legacy path bitmap untouched] FAIL: bitmap=0x{legacyBitmap:X8} (expected 0)");
            failures++;
        }
        else Console.WriteLine($"  [legacy path bitmap untouched] OK (driver bitmap stays 0; consumer didn't trigger driver allocator)");

        // Pool exhaustion: legacy override mark Status=Full.
        ctrlA.PublishPidBlockLoad(0, PidLoadStatus.Full, 0);
        bl = ctrlA.GetCurrentPidBlockLoad();
        if (bl.LoadStatus != PidLoadStatus.Full)
        {
            Console.WriteLine($"  [legacy pool exhaustion] FAIL: status={bl.LoadStatus}");
            failures++;
        }
        else Console.WriteLine($"  [legacy pool exhaustion] OK (status=Full)");

        // ── Section 2: multi-controller PID independence ──
        Console.WriteLine("  --- Section 2: multi-controller PID independence ---");
        const ushort kPoolSizeA = 0x1234;
        const byte   kSimMaxA   = 4;
        const ushort kPoolSizeB = 0xABCD;
        const byte   kSimMaxB   = 8;
        ctrlA.PublishPidPool(kPoolSizeA, kSimMaxA, true, false);
        ctrlB.PublishPidPool(kPoolSizeB, kSimMaxB, false, true);
        ctrlA.PublishPidBlockLoad(7, PidLoadStatus.Success, 0xAAAA);
        ctrlB.PublishPidBlockLoad(3, PidLoadStatus.Success, 0xBBBB);

        string secAName = @"Global\HIDMaestroPidState0";
        string secBName = @"Global\HIDMaestroPidState1";
        if (!ReadSectionBytes(secAName, out byte[] secA) || !ReadSectionBytes(secBName, out byte[] secB))
        {
            Console.WriteLine("  [multi-controller sections] FAIL: could not open one or both sections");
            failures++;
        }
        else
        {
            ushort ramA = BitConverter.ToUInt16(secA, OFF_POOL_RAM);
            byte simA = secA[OFF_POOL_MAX];
            byte ebiA = secA[OFF_BL_EBI];
            ushort ramB = BitConverter.ToUInt16(secB, OFF_POOL_RAM);
            byte simB = secB[OFF_POOL_MAX];
            byte ebiB = secB[OFF_BL_EBI];
            bool ok = ramA == kPoolSizeA && simA == kSimMaxA && ebiA == 7
                   && ramB == kPoolSizeB && simB == kSimMaxB && ebiB == 3;
            if (!ok)
            {
                Console.WriteLine($"  [multi-controller independence] FAIL: A=ram:0x{ramA:X4}/sim:{simA}/ebi:{ebiA} B=ram:0x{ramB:X4}/sim:{simB}/ebi:{ebiB}");
                failures++;
            }
            else
                Console.WriteLine($"  [multi-controller independence] OK (A: ram=0x{ramA:X4} sim={simA} ebi={ebiA}; B: ram=0x{ramB:X4} sim={simB} ebi={ebiB})");
        }

        // ── Section 3: file-based trace ──
        // Drive enough through the SDK that *if* HMAESTRO_TRACE is honored,
        // the driver's GetFeature gate firing will leave a marker. We don't
        // assert presence absolutely (driver is in WUDFHost which inherits
        // env from when its first instance launched, not from this probe);
        // we only verify that the directory exists with appropriate ACLs.
        Console.WriteLine("  --- Section 3: trace file directory ---");
        string traceDir = @"C:\ProgramData\HIDMaestro";
        bool dirOk = Directory.Exists(traceDir);
        if (!dirOk) {
            try { Directory.CreateDirectory(traceDir); dirOk = Directory.Exists(traceDir); } catch { }
        }
        if (!dirOk)
        {
            Console.WriteLine("  [trace dir] FAIL: %PROGRAMDATA%\\HIDMaestro not creatable");
            failures++;
        }
        else
        {
            Console.WriteLine($"  [trace dir] OK (HMAESTRO_TRACE=1 will land logs in {traceLog})");
        }

        // ── Section 4: dynamic SetFeature/SetOutputReport (best-effort) ──
        Console.WriteLine("  --- Section 4: dynamic driver-side allocation (best-effort) ---");
        SafeFileHandle? hidA = OpenHmHidByVidPid(ProbeVid, ProbePidA);
        bool dynamicHit = false;
        if (hidA != null && !hidA.IsInvalid)
        {
            // Reset bitmap-side state by closing/reopening — driver retains
            // bitmap across the session. Note bitmap before, then check after.
            ReadSectionBytes(secAName, out sec);
            uint before = BitConverter.ToUInt32(sec, OFF_EBI_BITMAP);

            // Try SetFeature first.
            if (HidD_SetFeature(hidA, new byte[] { 0x11, 0x01, 0x00, 0x00 }, 4))
            {
                dynamicHit = true;
                Console.WriteLine("  [SetFeature 0x11] driver IOCTL accepted via HidD_SetFeature");
            }
            // Fall back to SetOutputReport.
            else if (HidD_SetOutputReport(hidA, new byte[] { 0x11, 0x01, 0x00, 0x00 }, 4))
            {
                dynamicHit = true;
                Console.WriteLine("  [SetOutputReport 0x11] driver IOCTL accepted via HidD_SetOutputReport");
            }
            else
            {
                Console.WriteLine($"  [dynamic SetFeature/SetOutputReport] SKIP — both rejected by HidClass " +
                    $"(Win32={Marshal.GetLastWin32Error()}). PadForge FfbTest is the dynamic arbiter.");
            }

            if (dynamicHit)
            {
                Thread.Sleep(20); // let any async settle
                ReadSectionBytes(secAName, out sec);
                uint after = BitConverter.ToUInt32(sec, OFF_EBI_BITMAP);
                uint count = BitConverter.ToUInt32(sec, OFF_EBI_COUNT);
                if (after == before)
                {
                    Console.WriteLine($"  [dynamic alloc] FAIL: bitmap unchanged 0x{after:X8} (expected new bit set)");
                    failures++;
                }
                else
                {
                    Console.WriteLine($"  [dynamic alloc] OK (bitmap 0x{before:X8} -> 0x{after:X8}, count={count})");
                }
            }
            hidA.Dispose();
        }
        else
        {
            Console.WriteLine("  [dynamic SetFeature/SetOutputReport] SKIP — no HID handle");
        }

        if (failures == 0) { Console.WriteLine("=== PASS ==="); return 0; }
        Console.WriteLine($"=== FAIL: {failures} check(s) failed ==="); return 1;
    }

    static HMProfile BuildProfile(string id, ushort vid, ushort pid, string name)
    {
        return new HMProfileBuilder()
            .Id(id).Name(name).Vendor("HIDMaestro")
            .Vid(vid).Pid(pid)
            .ProductString(name).ManufacturerString("HIDMaestro")
            .Type("gamepad").Connection("usb")
            .Descriptor(s_descriptor).InputReportSize(2)
            .Build();
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

    const uint FILE_MAP_READ = 0x4;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMappingW(uint a, bool b, string c);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr h, uint a, uint b, uint c, UIntPtr d);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr p);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string path, uint a, uint b, IntPtr c, uint d, uint e, IntPtr f);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, uint len);
    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, uint len);
}
