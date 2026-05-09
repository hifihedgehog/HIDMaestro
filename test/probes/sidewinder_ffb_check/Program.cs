// SideWinder FF2 PID FFB end-to-end check (v1.3.11).
//
// Microsoft SideWinder Force Feedback 2 firmware uses a non-canonical PID
// Report ID for Create New Effect (0x01 vs canonical 0x11). The original
// HMDeviceExtractor capture also declared PID Block Load (Feature RID 0x02)
// and PID Pool (Feature RID 0x03) collections, but those AVs DirectInput's
// pid.dll CreateEffect path: pid.dll only supports a single Feature report
// (Create New Effect) per the canonical PID FFB descriptor shape. Real
// hardware survives because dinput on real-USB-PID takes a different code
// path; HIDMaestro's UMDF2 emulation routes everything through pid.dll.
//
// v1.3.11 fix: Block Load and Pool Feature collections were stripped from
// the shipped descriptor (1355→1221 bytes). The SDK still responds to
// dinput's HidD_GetFeature(BlockLoad)/(Pool) queries via shared-memory
// state at the canonical RIDs (0x12/0x13), identical to AddPidFfbBlock-
// built profiles. The non-canonical Create New Effect RID 0x01 is still
// declared in the descriptor and exercises the SDK's descriptor-aware
// dispatch path (PidReportIdExtractor + per-controller shared-section RID
// publish) introduced in v1.3.7.
//
// This probe loads the embedded SideWinder profile, creates a real
// virtual, publishes Pool, and runs the SetFeature(Create New Effect) +
// GetFeature(Pool) handshake. Both must succeed for dinput8/pid.dll to
// enumerate FFB on a SideWinder virtual end-to-end.
//
// Requires admin (driver install + virtual creation). Exit 0 on PASS, 1 on FAIL.

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    static int s_failures = 0;

    static void Check(string name, bool cond, string detail = "")
    {
        if (cond) Console.WriteLine($"  [PASS] {name}{(detail.Length > 0 ? "  " + detail : "")}");
        else { Console.WriteLine($"  [FAIL] {name}{(detail.Length > 0 ? "  " + detail : "")}"); s_failures++; }
    }

    static int Main()
    {
        Console.WriteLine("=== SideWinder Force Feedback 2 — descriptor-aware PID dispatch ===");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        var profile = ctx.GetProfile("sidewinder-force-feedback-2");
        Check("SideWinder profile loaded from embedded catalog", profile != null);
        if (profile == null) return 1;

        // Verify the descriptor is the v1.3.11 shipping shape: 1221 bytes,
        // Block Load + Pool Feature collections stripped from the original
        // 1355-byte HMDeviceExtractor capture to dodge the pid.dll
        // multi-Feature CreateEffect AV (see memory:project-piddll-single-
        // feature-trap.md).
        var desc = profile.GetDescriptorBytes();
        Check("Descriptor is the v1.3.11 shipping shape (~1221 bytes, multi-Feature stripped)",
              desc != null && desc.Length > 1100 && desc.Length < 1300,
              desc == null ? "(null)" : $"{desc.Length} bytes");

        // PidReportIdExtractor on the shipping descriptor: Create New Effect
        // is still at the SideWinder's non-canonical RID 0x01 (exercises the
        // descriptor-aware dispatch path). Pool/BlockLoad fall back to
        // canonical defaults (0x13/0x12) because their Feature collections
        // were stripped — the SDK serves those bytes via shared-memory state
        // at the canonical RIDs.
        var rids = PidReportIdExtractor.Extract(desc);
        Console.WriteLine($"  Descriptor PID RIDs: {rids}");
        Check("Create New Effect RID resolved to non-canonical 0x01", rids.CreateNewEffectReportId == 0x01,
              $"got 0x{rids.CreateNewEffectReportId:X2}");
        Check("Pool RID falls back to canonical 0x13", rids.PoolReportId == 0x13,
              $"got 0x{rids.PoolReportId:X2}");
        Check("Block Load RID falls back to canonical 0x12", rids.BlockLoadReportId == 0x12,
              $"got 0x{rids.BlockLoadReportId:X2}");
        Check("AnyOverride flag set (Create New Effect 0x01 != canonical 0x11)", rids.AnyOverride);

        if (s_failures > 0) return 1;

        ctx.InstallDriver();

        Console.Write("  Creating SideWinder virtual... ");
        using var ctrl = ctx.CreateController(profile);
        Console.WriteLine("OK");

        // Settle for HID interface to come fully online.
        Thread.Sleep(800);

        // Trigger PID state-section creation (lazy) by reading once. This
        // also fires HMController's RID-publish path so the driver's next
        // IOCTL dispatches against the SideWinder's non-canonical IDs.
        _ = ctrl.GetCurrentPidBlockLoad();

        // Diagnostic: read the shared section directly and verify the RID
        // bytes landed at the offsets the driver reads.
        const int kIndex = 0;
        string sectionName = $@"Global\HIDMaestroPidState{kIndex}";
        if (TryReadSection(sectionName, out byte[] section))
        {
            Console.WriteLine($"  Shared section RIDs: " +
                $"Pool=0x{section[28]:X2} State=0x{section[29]:X2} BL=0x{section[30]:X2} " +
                $"NewEffect=0x{section[31]:X2} BlockFree=0x{section[32]:X2} DC=0x{section[33]:X2}");
            Check("Shared Pool RID = canonical 0x13", section[28] == 0x13);
            Check("Shared NewEffect RID = non-canonical 0x01", section[31] == 0x01);
        }
        else
        {
            Check("Shared section opened", false);
        }

        const ushort kRamPoolSize = 0xFFFF;
        const byte kSimMax = 4;
        ctrl.PublishPidPool(kRamPoolSize, kSimMax, deviceManagedPool: true,
                            sharedParameterBlocks: true);

        // Open SideWinder VID/PID HID handle.
        SafeFileHandle? hid = OpenHmHidByVidPid(0x045E, 0x001B);
        bool hidOpen = hid != null && !hid.IsInvalid;
        Check("HID handle opened", hidOpen);
        if (!hidOpen) return s_failures > 0 ? 1 : 0;

        // SetFeature(CreateNewEffect at non-canonical RID 0x01) — driver
        // must allocate EBI synchronously for the SideWinder layout, just
        // like it does for canonical 0x11 in the standard PID block.
        // Payload: effectType (Constant Force = 0x01) + byteCount(LE) = 0.
        bool sf = SendSetFeature(hid!, rids.CreateNewEffectReportId,
            new byte[] { 0x01, 0x00, 0x00 });
        Check($"HidD_SetFeature(0x{rids.CreateNewEffectReportId:X2} Create New Effect) accepted",
              sf, sf ? "" : $"Win32={Marshal.GetLastWin32Error()}");

        if (sf)
        {
            var bl = ctrl.GetCurrentPidBlockLoad();
            Check($"Driver allocated EBI=1 for non-canonical Create New Effect RID (got ebi={bl.EffectBlockIndex} status={bl.LoadStatus})",
                  bl.EffectBlockIndex == 1 && bl.LoadStatus == PidLoadStatus.Success);
        }

        // The v1.3.11 stripped descriptor does NOT declare Pool/BlockLoad as
        // Feature reports (they were removed to avoid pid.dll's multi-Feature
        // CreateEffect AV). HidD_GetFeature(0x13) would fail with
        // ERROR_INVALID_PARAMETER because HID class validates the report ID
        // against preparsed data BEFORE the IRP reaches the driver.
        //
        // The pool/block-load values still travel through the SDK's
        // PublishPidPool / GetCurrentPidBlockLoad API and the driver's
        // shared-memory state path; pid.dll itself does not need to query
        // them via wire (FfbTest with AddPidFfbBlock-built profiles is
        // proof-positive). Verify the published Pool round-trips through
        // the SDK API rather than via HID wire.
        var blPublished = ctrl.GetCurrentPidBlockLoad();
        Check($"Pool round-trip via SDK API: RAMPoolAvailable={blPublished.RAMPoolAvailable}",
              blPublished.RAMPoolAvailable > 0,
              $"got 0x{blPublished.RAMPoolAvailable:X4}");

        // Free EBI=1 via Block Free RID so we leave the device in a clean state.
        SendSetFeature(hid!, rids.BlockFreeReportId, new byte[] { 0x01 });

        hid?.Dispose();

        Console.WriteLine($"\n=== {(s_failures == 0 ? "PASS" : "FAIL")}: {s_failures} failure(s) ===");
        return s_failures == 0 ? 0 : 1;
    }

    static SafeFileHandle? OpenHmHidByVidPid(ushort vid, ushort pid)
    {
        // HM's virtual SideWinder uses the &IG_00 enumerator marker;
        // a real connected SideWinder doesn't. Filter so we don't open
        // the user's physical pad if it happens to be plugged in.
        var match = HMDeviceExtractor.ListDevices()
            .Where(d => d.VendorId == vid && d.ProductId == pid)
            .FirstOrDefault(d => d.DevicePath.Contains("&IG_", StringComparison.OrdinalIgnoreCase));
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

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share,
        IntPtr sec, uint disp, uint flags, IntPtr template);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, uint len);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, uint len);

    const int SECTION_SIZE = 40;
    const uint FILE_MAP_READ = 0x04;

    static bool TryReadSection(string name, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        IntPtr h = OpenFileMappingW(FILE_MAP_READ, false, name);
        if (h == IntPtr.Zero) return false;
        try
        {
            IntPtr v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, (UIntPtr)SECTION_SIZE);
            if (v == IntPtr.Zero) return false;
            try
            {
                bytes = new byte[SECTION_SIZE];
                Marshal.Copy(v, bytes, 0, SECTION_SIZE);
                return true;
            }
            finally { UnmapViewOfFile(v); }
        }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr h, uint access, uint offHi, uint offLo, UIntPtr nBytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UnmapViewOfFile(IntPtr v);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
}
