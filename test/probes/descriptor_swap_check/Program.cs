// v1.4.0 Experiment 1 — descriptor-swap viability probe.
//
// Hypothesis: a CM_Disable_DevNode + ReportDescriptor registry rewrite +
// CM_Enable_DevNode cycle is fast enough to count as "near-instant" devnode
// reuse on the Xbox 360 wired path, AND the driver re-reads the descriptor
// at re-init (driver.c:206 RegQueryValueExW(L"ReportDescriptor")), so we
// effectively swap the device's identity without a full PnP create cycle.
//
// Pass criteria:
//   - Disable + Enable wall time < 200 ms on dev box for Xbox 360 wired
//   - HID descriptor returned via IOCTL_HID_GET_REPORT_DESCRIPTOR after Enable
//     matches the second profile's bytes (proves the driver re-read).
//
// Fail outcome: descriptor cached at hidclass.sys above us, or the cycle
// triggers a full kernel cascade. Move to Experiment 2 (multi-TLC).

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using HIDMaestro;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal sealed class Program
{
    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
    [DllImport("CfgMgr32.dll")]
    private static extern uint CM_Disable_DevNode(uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll")]
    private static extern uint CM_Enable_DevNode(uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll")]
    private static extern uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);
    [DllImport("CfgMgr32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_Device_IDW(uint dnDevInst, char[] Buffer, uint BufferLen, uint ulFlags);
    [DllImport("CfgMgr32.dll")]
    private static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblem, uint dnDevInst, uint ulFlags);
    private const uint DN_STARTED = 0x00000008;

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle HidDeviceObject, out IntPtr PreparsedData);
    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr PreparsedData);

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        // ... + many fields. Truncated; struct lookup uses HidP_GetCaps which
        // takes the first ~~bytes of the layout that matches the public API.
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr PreparsedData, out HIDP_CAPS Capabilities);

    public static int Main(string[] args)
    {
        Console.WriteLine("=== v1.4.0 Experiment 1: descriptor-swap viability probe ===\n");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        Console.Write("  Installing driver... ");
        ctx.InstallDriver();
        Console.WriteLine("OK");

        var profileA = ctx.GetProfile("xbox-360-wired") ?? throw new Exception("Missing xbox-360-wired profile");
        var profileB = ctx.GetProfile("dualsense") ?? throw new Exception("Missing dualsense profile");

        var profileADescriptor = profileA.GetDescriptorBytes() ?? throw new Exception("xbox-360-wired has no descriptor");
        var profileBDescriptor = profileB.GetDescriptorBytes() ?? throw new Exception("dualsense has no descriptor");

        Console.WriteLine($"  Profile A: xbox-360-wired (HID descriptor {profileADescriptor.Length} bytes, top-level usage 0x05/Gamepad)");
        Console.WriteLine($"  Profile B: dualsense (HID descriptor {profileBDescriptor.Length} bytes, top-level usage 0x05/Gamepad)");

        // ── Phase 1: create with profile A ──────────────────────────────────
        var phase1Sw = Stopwatch.StartNew();
        var ctrl = ctx.CreateController(profileA);
        Console.WriteLine($"  CreateController(profileA) -> {phase1Sw.ElapsedMilliseconds} ms");

        // Locate the main devnode.
        string mainInstId = $@"ROOT\VID_045E&PID_028E&IG_00\0000";
        if (CM_Locate_DevNodeW(out uint mainDevInst, mainInstId, 0) != 0)
        {
            Console.WriteLine($"FAIL — could not locate {mainInstId}");
            ctrl.Dispose();
            return 1;
        }

        // Sanity: check the HID child descriptor matches profile A.
        string? hidChildIdA = GetFirstHidChildId(mainDevInst);
        Console.WriteLine($"  HID child after profile A create: {hidChildIdA ?? "(none)"}");
        var capsA = TryReadHidCaps(hidChildIdA);
        Console.WriteLine($"  HIDP_CAPS (profile A): UsagePage=0x{capsA?.UsagePage:X4} Usage=0x{capsA?.Usage:X4} InputReportLen={capsA?.InputReportByteLength} ButtonCaps={capsA?.NumberInputButtonCaps} ValueCaps={capsA?.NumberInputValueCaps}");

        // ── Phase 2: write profile B's descriptor to registry ───────────────
        // Driver reads from HKLM\SOFTWARE\HIDMaestro\Controller{N}\ReportDescriptor
        // (per ctx->ConfigRegPath in driver.c → InitInstancePaths).
        // Controller index is 0 since this is the only/first controller this
        // process created. (Index getter is internal so we know it's 0 by
        // construction — first allocate-next-free in HMContext.CreateController.)
        int controllerIndex = 0;
        string dpPath = $@"SOFTWARE\HIDMaestro\Controller{controllerIndex}";
        try
        {
            using var dp = Registry.LocalMachine.OpenSubKey(dpPath, writable: true);
            if (dp == null)
            {
                Console.WriteLine($"FAIL — Controller config key not found at HKLM\\{dpPath}");
                ctrl.Dispose();
                return 1;
            }
            dp.SetValue("ReportDescriptor", profileBDescriptor, RegistryValueKind.Binary);
            // Also rewrite VendorId / ProductId / ProductString so HIDP_CAPS
            // diff is unambiguous (DualSense = VID 0x054C / PID 0x0CE6).
            dp.SetValue("VendorId", (int)profileB.VendorId, RegistryValueKind.DWord);
            dp.SetValue("ProductId", (int)profileB.ProductId, RegistryValueKind.DWord);
            if (profileB.ProductString != null)
                dp.SetValue("ProductString", profileB.ProductString, RegistryValueKind.String);
            Console.WriteLine($"  Wrote profile B's descriptor ({profileBDescriptor.Length} bytes) + VID/PID to HKLM\\{dpPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL — registry write threw: {ex.Message}");
            ctrl.Dispose();
            return 1;
        }

        // ── Phase 3: disable + enable the devnode, measure cycle time ───────
        Console.WriteLine($"\n--- Disable + Enable cycle ---");

        var disableSw = Stopwatch.StartNew();
        uint disableHr = CM_Disable_DevNode(mainDevInst, 0);
        long disableMs = disableSw.ElapsedMilliseconds;
        Console.WriteLine($"  CM_Disable_DevNode -> hr=0x{disableHr:X8} in {disableMs} ms");

        // Wait for DN_STARTED to clear (device is fully disabled).
        var stopWaitSw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            if (CM_Get_DevNode_Status(out uint status, out _, mainDevInst, 0) == 0
                && (status & DN_STARTED) == 0) break;
            System.Threading.Thread.Sleep(10);
        }
        Console.WriteLine($"  Waited for DN_STARTED clear: {stopWaitSw.ElapsedMilliseconds} ms");

        var enableSw = Stopwatch.StartNew();
        uint enableHr = CM_Enable_DevNode(mainDevInst, 0);
        long enableMs = enableSw.ElapsedMilliseconds;
        Console.WriteLine($"  CM_Enable_DevNode -> hr=0x{enableHr:X8} in {enableMs} ms");

        // Wait for DN_STARTED to set (device fully back).
        var startWaitSw = Stopwatch.StartNew();
        for (int i = 0; i < 500; i++)
        {
            if (CM_Get_DevNode_Status(out uint status, out _, mainDevInst, 0) == 0
                && (status & DN_STARTED) != 0) break;
            System.Threading.Thread.Sleep(10);
        }
        Console.WriteLine($"  Waited for DN_STARTED set: {startWaitSw.ElapsedMilliseconds} ms");

        long totalCycleMs = disableMs + stopWaitSw.ElapsedMilliseconds + enableMs + startWaitSw.ElapsedMilliseconds;
        Console.WriteLine($"  TOTAL cycle (disable + wait-stop + enable + wait-start): {totalCycleMs} ms");

        // ── Phase 4: re-read HID child descriptor and verify it changed ─────
        System.Threading.Thread.Sleep(100); // small settle for HID child PDO
        string? hidChildIdB = GetFirstHidChildId(mainDevInst);
        Console.WriteLine($"\n  HID child after disable+enable: {hidChildIdB ?? "(none)"}");
        var capsB = TryReadHidCaps(hidChildIdB);
        Console.WriteLine($"  HIDP_CAPS (after swap): UsagePage=0x{capsB?.UsagePage:X4} Usage=0x{capsB?.Usage:X4} InputReportLen={capsB?.InputReportByteLength} ButtonCaps={capsB?.NumberInputButtonCaps} ValueCaps={capsB?.NumberInputValueCaps}");

        // ── Phase 5: verdict ────────────────────────────────────────────────
        Console.WriteLine($"\n=== Verdict ===");
        bool cycleFastEnough = totalCycleMs < 200;
        bool descriptorChanged = capsA != null && capsB != null
            && (capsA.Value.InputReportByteLength != capsB.Value.InputReportByteLength
                || capsA.Value.NumberInputButtonCaps != capsB.Value.NumberInputButtonCaps
                || capsA.Value.NumberInputValueCaps != capsB.Value.NumberInputValueCaps);

        Console.WriteLine($"  Cycle < 200 ms: {(cycleFastEnough ? "YES" : "NO")} ({totalCycleMs} ms)");
        Console.WriteLine($"  Descriptor changed (caps A != caps B): {(descriptorChanged ? "YES" : "NO")}");

        if (cycleFastEnough && descriptorChanged)
        {
            Console.WriteLine($"  PASS — descriptor swap viable on Xbox 360 wired path");
            ctrl.Dispose();
            return 0;
        }
        else
        {
            Console.WriteLine($"  FAIL — Experiment 1 hypothesis falsified. Move to Experiment 2 (multi-TLC).");
            ctrl.Dispose();
            return 1;
        }
    }

    private static string? GetFirstHidChildId(uint parentDevInst)
    {
        if (CM_Get_Child(out uint childInst, parentDevInst, 0) != 0) return null;
        var buf = new char[512];
        if (CM_Get_Device_IDW(childInst, buf, (uint)buf.Length, 0) != 0) return null;
        int len = 0;
        while (len < buf.Length && buf[len] != 0) len++;
        return new string(buf, 0, len);
    }

    private static HIDP_CAPS? TryReadHidCaps(string? hidInstId)
    {
        if (hidInstId == null) return null;
        try
        {
            // Symbolic link form: \\?\<inst-id-with-#-instead-of-\>#{HID interface GUID}
            // HID interface class GUID: {4d1e55b2-f16f-11cf-88cb-001111000030}
            string symLink = @"\\?\" + hidInstId.Replace('\\', '#') + "#{4d1e55b2-f16f-11cf-88cb-001111000030}";

            IntPtr handle = NativeOpenHidDevice(symLink);
            if (handle == IntPtr.Zero || handle.ToInt64() == -1) return null;

            using var sfh = new SafeFileHandle(handle, ownsHandle: true);
            if (!HidD_GetPreparsedData(sfh, out IntPtr preparsed) || preparsed == IntPtr.Zero)
                return null;
            try
            {
                if (HidP_GetCaps(preparsed, out HIDP_CAPS caps) != 0x110000 /*HIDP_STATUS_SUCCESS*/)
                    return null;
                return caps;
            }
            finally { HidD_FreePreparsedData(preparsed); }
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    private static IntPtr NativeOpenHidDevice(string symLink) =>
        CreateFileW(symLink, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
}
