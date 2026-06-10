// Issue #28 regression probe (v1.3.16).
//
// Verifies that an HM SetupController + Teardown cycle does NOT mutate a
// coexisting foreign root-enumerated HIDClass device — the vJoy / PadForge-v2
// repro shape from the original report.
//
// The probe:
//   1. Creates a foreign devnode at ROOT\HIDClass\NNNN with HardwareID
//      "root\VID_1234&PID_BEAD&REV_0222" — same enumerator and HardwareID
//      shape vJoy uses on the reporter's machine. The devnode has no INF /
//      driver bound, so it lands in the "no driver, but registered" state
//      that's safe for a unit-test fixture.
//   2. Snapshots the foreign node's pre-state: HardwareID, FriendlyName,
//      DeviceDesc, BusReportedDeviceDesc, Device Parameters\ControllerIndex
//      (expected absent), and the device's enumerated-interfaces list under
//      HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses.
//   3. Runs a full HM CreateController(hidmaestro-generic) + Dispose cycle.
//      This fires every once-per-session sweep in DeviceOrchestrator
//      (CleanupGhostDevices, DisableGhostXusbInterfaces, SetBusTypeGuidUsb,
//      the SYSTEM-companion HM cleanup) plus the pre-UpdateDriver claim-walk
//      in DeviceNodeCreator and the FinalizeNames pass in DeviceProperties.
//   4. Re-snapshots and asserts every field matches the pre-state:
//      - device still present (CM_Locate succeeds)
//      - HardwareID unchanged
//      - FriendlyName / DeviceDesc / BusReportedDeviceDesc unchanged
//      - no ControllerIndex value injected
//      - interface keys still intact
//   5. Removes the foreign devnode via DIF_REMOVE.
//
// Exit 0 on PASS, 1 on FAIL. Idempotent — leaves no devnode behind.

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using HIDMaestro;
using HIDMaestro.Internal;

internal sealed class Program
{
    static int s_total = 0;
    static int s_failures = 0;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static readonly Guid HIDClassGuid =
        new Guid("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

    const int DICD_GENERATE_ID = 0x00000001;
    const int SPDRP_HARDWAREID = 1;
    const int DIF_REGISTERDEVICE = 0x00000019;
    const int DIF_REMOVE = 0x00000005;

    [DllImport("SetupAPI.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid ClassGuid, IntPtr hwndParent);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiCreateDeviceInfoW")]
    static extern bool SetupDiCreateDeviceInfoW(IntPtr DeviceInfoSet, string DeviceName,
        ref Guid ClassGuid, string DeviceDescription, IntPtr hwndParent, int CreationFlags, IntPtr DeviceInfoData);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiSetDeviceRegistryPropertyW")]
    static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        int Property, byte[] PropertyBuffer, uint PropertyBufferSize);

    [DllImport("SetupAPI.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(int InstallFunction, IntPtr DeviceInfoSet, IntPtr DeviceInfoData);

    [DllImport("SetupAPI.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

    [DllImport("SetupAPI.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    static extern bool SetupDiGetDeviceInstanceIdW(IntPtr DeviceInfoSet, IntPtr DeviceInfoData,
        StringBuilder DeviceInstanceId, uint DeviceInstanceIdSize, out uint RequiredSize);

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    record Snapshot(
        bool Present,
        string[]? HardwareID,
        string? FriendlyName,
        string? DeviceDesc,
        string? BusReportedDeviceDesc,
        int? ControllerIndex,
        string[] InterfaceSubkeys);

    static Snapshot Capture(string instId)
    {
        bool present = CM_Locate_DevNodeW(out _, instId, 0) == 0;
        string regPath = $@"SYSTEM\CurrentControlSet\Enum\{instId}";
        using var k = Registry.LocalMachine.OpenSubKey(regPath);
        string[]? hwIds = k?.GetValue("HardwareID") as string[];
        string? fn = k?.GetValue("FriendlyName") as string;
        string? dd = k?.GetValue("DeviceDesc") as string;
        string? brdd = k?.GetValue("BusReportedDeviceDesc") as string;
        int? ci = null;
        using (var dp = Registry.LocalMachine.OpenSubKey(regPath + @"\Device Parameters"))
        {
            if (dp?.GetValue("ControllerIndex") is int v) ci = v;
        }

        // Capture the interface-class subkeys that reference this instance.
        // Encoding: instance ID with `\` replaced by `#` then prefixed with
        // `##?#`. We just look for any DeviceClasses key whose entries
        // mention the instance ID's last component.
        string instTail = instId.Replace('\\', '#');
        var classRoot = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\DeviceClasses");
        var matchingInterfaces = new System.Collections.Generic.List<string>();
        if (classRoot != null)
        {
            foreach (var guid in classRoot.GetSubKeyNames())
            {
                using var gk = classRoot.OpenSubKey(guid);
                if (gk == null) continue;
                foreach (var sub in gk.GetSubKeyNames())
                {
                    if (sub.IndexOf(instTail, StringComparison.OrdinalIgnoreCase) >= 0)
                        matchingInterfaces.Add($"{guid}/{sub}");
                }
            }
            classRoot.Dispose();
        }
        return new Snapshot(present, hwIds, fn, dd, brdd, ci, matchingInterfaces.ToArray());
    }

    static (IntPtr dis, byte[] devInfoBuf, GCHandle devInfoHandle, string instId) CreateForeignDummy()
    {
        Guid classGuid = HIDClassGuid;
        IntPtr dis = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (dis == new IntPtr(-1))
            throw new InvalidOperationException(
                $"SetupDiCreateDeviceInfoList failed: 0x{Marshal.GetLastWin32Error():X8}");

        byte[] devInfoBuf = new byte[32];
        int devInfoSize = IntPtr.Size == 8 ? 32 : 28;
        BitConverter.GetBytes(devInfoSize).CopyTo(devInfoBuf, 0);
        var devInfoHandle = GCHandle.Alloc(devInfoBuf, GCHandleType.Pinned);

        try
        {
            // Create the foreign devnode at ROOT\HIDClass\NNNN with a hwid that
            // looks like vJoy's. The "ForeignVJoyShim" device-description is a
            // marker so a human can spot it in joy.cpl / Device Manager if the
            // probe somehow exits early without cleanup.
            if (!SetupDiCreateDeviceInfoW(dis, "HIDClass", ref classGuid,
                    "ForeignVJoyShim (HM #28 regression probe)",
                    IntPtr.Zero, DICD_GENERATE_ID, devInfoHandle.AddrOfPinnedObject()))
                throw new InvalidOperationException(
                    $"SetupDiCreateDeviceInfoW failed: 0x{Marshal.GetLastWin32Error():X8}");

            // HardwareID multi-sz — vJoy-shape.
            string hwMulti = "root\\VID_1234&PID_BEAD&REV_0222\0\0";
            byte[] hwBytes = Encoding.Unicode.GetBytes(hwMulti);
            if (!SetupDiSetDeviceRegistryPropertyW(dis, devInfoHandle.AddrOfPinnedObject(),
                    SPDRP_HARDWAREID, hwBytes, (uint)hwBytes.Length))
                throw new InvalidOperationException(
                    $"SetupDiSetDeviceRegistryPropertyW failed: 0x{Marshal.GetLastWin32Error():X8}");

            // DIF_REGISTERDEVICE actually creates the PnP node.
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, dis,
                    devInfoHandle.AddrOfPinnedObject()))
                throw new InvalidOperationException(
                    $"DIF_REGISTERDEVICE failed: 0x{Marshal.GetLastWin32Error():X8}");

            // Read back the generated instance ID.
            var sb = new StringBuilder(256);
            if (!SetupDiGetDeviceInstanceIdW(dis, devInfoHandle.AddrOfPinnedObject(),
                    sb, (uint)sb.Capacity, out _))
                throw new InvalidOperationException(
                    $"SetupDiGetDeviceInstanceIdW failed: 0x{Marshal.GetLastWin32Error():X8}");
            return (dis, devInfoBuf, devInfoHandle, sb.ToString());
        }
        catch
        {
            devInfoHandle.Free();
            SetupDiDestroyDeviceInfoList(dis);
            throw;
        }
    }

    static void RemoveForeignDummy(IntPtr dis, GCHandle devInfoHandle)
    {
        try { SetupDiCallClassInstaller(DIF_REMOVE, dis, devInfoHandle.AddrOfPinnedObject()); }
        catch { }
        try { devInfoHandle.Free(); } catch { }
        try { SetupDiDestroyDeviceInfoList(dis); } catch { }
    }

    public static int Main()
    {
        Console.WriteLine("=== Issue #28 foreign-devnode-survival probe ===\n");

        IntPtr dis = IntPtr.Zero;
        GCHandle devInfoHandle = default;
        string? foreignId = null;
        try
        {
            (dis, _, devInfoHandle, foreignId) = CreateForeignDummy();
            Console.WriteLine($"Foreign devnode created at: {foreignId}");

            var pre = Capture(foreignId);
            Check("Foreign device present pre-test", pre.Present);
            Check("Foreign device HardwareID has vJoy-shape entry",
                pre.HardwareID != null && pre.HardwareID.Any(
                    s => s.Contains("VID_1234&PID_BEAD", StringComparison.OrdinalIgnoreCase)));
            Check("Foreign device has no ControllerIndex pre-test",
                pre.ControllerIndex == null);

            // Run a full HM controller setup + teardown cycle. Use a generic
            // profile so we hit every once-per-session sweep without the
            // Xbox-specific XUSB companion path.
            using (var ctx = new HMContext())
            {
                ctx.LoadDefaultProfiles();
                // Find a plain-HID profile (DualSense USB or similar). We don't
                // need anything specific — only the create/teardown sweeps
                // matter for issue #28.
                var profile = ctx.GetProfile("dualsense")
                           ?? ctx.GetProfile("xbox-360-wired")
                           ?? ctx.AllProfiles.FirstOrDefault(p => p.IsDeployable);
                if (profile == null)
                {
                    Console.WriteLine("[FAIL] no profile available for SetupController");
                    s_total++; s_failures++;
                }
                else
                {
                    using var ctrl = ctx.CreateController(profile);
                    System.Threading.Thread.Sleep(500);
                }
            }

            var post = Capture(foreignId);
            Check("Foreign device still present post-test", post.Present);
            Check("Foreign device HardwareID unchanged",
                pre.HardwareID != null && post.HardwareID != null
                && pre.HardwareID.SequenceEqual(post.HardwareID),
                $"pre={string.Join(',', pre.HardwareID ?? Array.Empty<string>())} post={string.Join(',', post.HardwareID ?? Array.Empty<string>())}");
            Check("Foreign device FriendlyName unchanged",
                pre.FriendlyName == post.FriendlyName,
                $"pre='{pre.FriendlyName ?? "(null)"}' post='{post.FriendlyName ?? "(null)"}'");
            Check("Foreign device DeviceDesc unchanged",
                pre.DeviceDesc == post.DeviceDesc,
                $"pre='{pre.DeviceDesc ?? "(null)"}' post='{post.DeviceDesc ?? "(null)"}'");
            Check("Foreign device BusReportedDeviceDesc unchanged",
                pre.BusReportedDeviceDesc == post.BusReportedDeviceDesc,
                $"pre='{pre.BusReportedDeviceDesc ?? "(null)"}' post='{post.BusReportedDeviceDesc ?? "(null)"}'");
            Check("Foreign device has no ControllerIndex post-test",
                post.ControllerIndex == null,
                post.ControllerIndex == null ? "" : $"injected ControllerIndex={post.ControllerIndex}");
            Check("Foreign device interface subkeys preserved",
                pre.InterfaceSubkeys.SequenceEqual(post.InterfaceSubkeys),
                $"pre={pre.InterfaceSubkeys.Length} post={post.InterfaceSubkeys.Length}");
        }
        finally
        {
            if (dis != IntPtr.Zero)
                RemoveForeignDummy(dis, devInfoHandle);
        }

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} {(s_failures == 0 ? "PASS" : "FAIL")} ===");
        return s_failures == 0 ? 0 : 1;
    }
}
