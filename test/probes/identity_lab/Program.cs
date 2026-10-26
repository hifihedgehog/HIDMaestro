// Identity lab (issue #60). Measures, per creation path, what Windows does
// to a virtual controller's instance ids when the same identity is created,
// removed, and created again. Every variant runs three lives and records
// the parent instance id, its ParentIdPrefix, the HID child ids (or the XUSB
// interface for the companion shape), the bound Service, and DN_STARTED.
//
// Shapes:
//   A  SWD gamepad companion (hidmaestro.inf, HID child, xinputhid)
//   B  SWD XUSB companion (hidmaestro_xusb.inf, XUSB interface, no child)
//   R  ROOT plain-HID parent (hidmaestro.inf, HID child)
//
// Axes varied per shape: fixed vs unique instance id, fixed vs changing
// ContainerId, phantom record retained vs purged between lives, and a
// ParentIdPrefix pre-written into the instance key before creation.
//
// Elevation required. Leaves nothing behind: every device it creates is
// removed and its phantom records purged at the end.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    const int Index = 7;
    const uint DN_STARTED = 0x00000008;
    static readonly Guid XusbIf = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
    static readonly Guid HidClassGuid = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");

    [DllImport("CfgMgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);
    [DllImport("CfgMgr32.dll")]
    static extern uint CM_Get_DevNode_Status(out uint pulStatus, out uint pulProblemNumber, uint dnDevInst, uint ulFlags);

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
    [DllImport("newdev.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwndParent, string HardwareId,
        string FullInfPath, int InstallFlags, out bool RebootRequired);

    sealed record Variant(string Name, char Shape, bool FixedSuffix, bool FixedContainer,
                          bool PurgePhantom, string? PreKeyPrefix,
                          bool PostWritePrefix = false, bool FastRemoveNoWait = false);

    sealed record Life(int N, string? Parent, bool Present, bool Started, uint Problem,
                       string? Service, string[] Children, bool ChildStarted, bool Interface,
                       string? Prefix, long CreateMs, long RemoveMs, bool KeyAfterRemove, string Note);

    static string EnumA = "HIDMAESTRO_VID_045E_PID_0B13&IG_00";
    static string EnumB = "HIDMAESTRO";
    static string LabPrefix = "1&1ab1ab1a&0";

    static int Main(string[] args)
    {
        Console.WriteLine("=== Identity lab (issue #60) ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        Console.Write("Installing driver... ");
        ctx.InstallDriver();
        Console.WriteLine("OK");
        SharedMemoryIO.EnsureInputMapping(Index);
        try { SharedMemoryIO.EnsureOutputMapping(Index); } catch { }

        string only = args.Length > 0 ? args[0] : "*";
        var variants = new List<Variant>
        {
            new("A1 unique suffix, fixed container (today)",           'A', false, true,  false, null),
            new("A2 fixed suffix, fixed container, phantom retained",  'A', true,  true,  false, null),
            new("A3 fixed suffix, fixed container, phantom purged",    'A', true,  true,  true,  null),
            new("A4 fixed suffix, fixed container, purged, pre-key",   'A', true,  true,  true,  LabPrefix),
            new("A5 unique suffix, fixed container, pre-key",          'A', false, true,  false, LabPrefix),
            new("A6 fixed suffix, changing container, retained",       'A', true,  false, false, null),
            new("B1 unique suffix, fixed container (today)",           'B', false, true,  false, null),
            new("B2 fixed suffix, fixed container, phantom retained",  'B', true,  true,  false, null),
            new("B3 fixed suffix, fixed container, phantom purged",    'B', true,  true,  true,  null),
            new("B6 fixed suffix, changing container, retained",       'B', true,  false, false, null),
            new("A7 fixed suffix, purged, post-create prefix write + restart", 'A', true, true, true, null, PostWritePrefix: true),
            new("A8 fixed suffix, retained, post-create prefix write + restart", 'A', true, true, false, null, PostWritePrefix: true),
            new("A9 fixed suffix, fast remove without wait, immediate recreate", 'A', true, true, false, null, PostWritePrefix: true, FastRemoveNoWait: true),
            new("B9 fixed suffix, fast remove without wait, immediate recreate", 'B', true, true, false, null, FastRemoveNoWait: true),
            new("R0 explicit id, no pre-key",                          'R', true,  true,  true,  null),
            new("R1 explicit id, pre-key before registration",         'R', true,  true,  true,  LabPrefix),
            new("R2 explicit id, pre-key, fast remove without wait",   'R', true,  true,  true,  LabPrefix, FastRemoveNoWait: true),
        };
        PurgeResidue();

        var summary = new List<string>();
        foreach (var v in variants)
        {
            if (only != "*" && !only.Split(',').Any(o => v.Name.StartsWith(o, StringComparison.OrdinalIgnoreCase))) continue;
            Console.WriteLine();
            Console.WriteLine($"--- {v.Name} ---");
            var lives = new List<Life>();
            for (int n = 1; n <= 3; n++)
            {
                Life life;
                try { life = v.Shape == 'R' ? RunRootLife(v, n) : RunSwdLife(v, n); }
                catch (Exception ex) { life = new Life(n, null, false, false, 0, null, Array.Empty<string>(), false, false, null, 0, 0, false, "EXC " + ex.Message); }
                lives.Add(life);
                Console.WriteLine($"  life {n}: parent={life.Parent ?? "(none)"} present={life.Present} started={life.Started} problem={life.Problem} service={life.Service ?? "(none)"} prefix={life.Prefix ?? "(none)"}");
                Console.WriteLine($"          children=[{string.Join(", ", life.Children)}] childStarted={life.ChildStarted} iface={life.Interface} create={life.CreateMs}ms remove={life.RemoveMs}ms keyAfterRemove={life.KeyAfterRemove} {life.Note}");
            }
            bool allBound = lives.All(l => l.Present && l.Started && l.Service != null &&
                (v.Shape == 'B' ? l.Interface : l.Children.Length > 0 && l.ChildStarted));
            bool sameParent = lives.Select(l => l.Parent).Distinct().Count() == 1;
            bool sameChildren = v.Shape == 'B' || lives.Select(l => string.Join("|", l.Children)).Distinct().Count() == 1;
            string verdict = $"{v.Name}: bound={allBound} sameParent={sameParent} sameChildren={sameChildren}";
            Console.WriteLine("  => " + verdict);
            summary.Add(verdict);
        }

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        foreach (var s in summary) Console.WriteLine("  " + s);
        try { DeviceManager.RemoveAccumulatedHmPhantoms(); } catch { }
        return 0;
    }

    // ── SWD shapes ─────────────────────────────────────────────────────

    static Life RunSwdLife(Variant v, int n)
    {
        string enumName = v.Shape == 'A' ? EnumA : EnumB;
        string suffix = v.FixedSuffix ? $"HMLAB_{v.Shape}" : $"HMLAB_{v.Shape}_{Guid.NewGuid():N}".Substring(0, 24);
        Guid container = v.FixedContainer ? SwdDeviceFactory.ContainerIdFor(Index) : Guid.NewGuid();
        string[] hw, compat;
        if (v.Shape == 'A')
        {
            hw = new[] { "root\\VID_045E&PID_0B13&IG_00", "root\\HIDMaestroGamepad", "root\\HIDMaestro" };
            compat = new[] { "BTHLEDEVICE\\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&02045E_PID&0B13", "root\\HIDMaestroGamepad", "root\\HIDMaestro" };
        }
        else
        {
            hw = new[] { "root\\VID_045E&PID_028E&XI_00", "root\\HIDMaestroXUSB" };
            compat = new[] { "USB\\MS_COMP_XUSB10", "USB\\Class_FF&SubClass_5D&Prot_01", "USB\\Class_FF&SubClass_5D", "USB\\Class_FF" };
        }
        string expected = $@"SWD\{enumName}\{suffix}";
        string note = "";
        if (v.PreKeyPrefix != null)
        {
            try
            {
                using var k = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Enum\{expected}");
                k.SetValue("ParentIdPrefix", v.PreKeyPrefix, RegistryValueKind.String);
                note += "prekey=ok ";
            }
            catch (Exception ex) { note += "prekey=FAIL(" + ex.GetType().Name + ") "; }
        }

        var sw = Stopwatch.StartNew();
        var r = SwdDeviceFactory.Create(suffix, hw, compat, container, $"Identity lab {v.Shape}", enumName);
        if (!r.Success || r.InstanceId == null)
            return new Life(n, null, false, false, 0, null, Array.Empty<string>(), false, false, null, sw.ElapsedMilliseconds, 0, false, note + $"create FAILED hr=0x{r.HResult:X8}");
        string inst = r.InstanceId;
        try
        {
            using var dp = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Enum\{inst}\Device Parameters");
            dp.SetValue("ControllerIndex", Index, RegistryValueKind.DWord);
        }
        catch { }
        if (v.PostWritePrefix)
        {
            try
            {
                using var pk = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{inst}", writable: true);
                string? cur = pk?.GetValue("ParentIdPrefix") as string;
                if (pk != null && !string.Equals(cur, LabPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    pk.SetValue("ParentIdPrefix", LabPrefix, RegistryValueKind.String);
                    note += $"postwrite(was {cur ?? "(none)"}) ";
                }
                else note += "postwrite=skip(equal) ";
            }
            catch (Exception ex) { note += "postwrite=FAIL(" + ex.GetType().Name + ") "; }
        }
        DeviceManager.RestartDevice(inst);
        if (v.Shape == 'A') DeviceManager.WaitForHidChild(inst, 5000);
        else DeviceManager.WaitForDeviceInterface(inst, XusbIf, 5000);
        System.Threading.Thread.Sleep(300);
        long createMs = sw.ElapsedMilliseconds;

        var snap = Snapshot(inst, v.Shape);

        sw.Restart();
        if (v.FastRemoveNoWait) DeviceManager.RemoveDevice(inst, timeoutMs: 5000, fast: true, forceFallbacks: true);
        else DeviceManager.RemoveDevice(inst, timeoutMs: 120_000, forceFallbacks: true);
        long removeMs = sw.ElapsedMilliseconds;
        bool keyAfter = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{inst}") != null;
        if (v.PurgePhantom)
        {
            int purged = DeviceManager.RemoveAccumulatedHmPhantoms();
            note += $"purged={purged} ";
            keyAfter = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{inst}") != null;
        }
        return new Life(n, inst, snap.present, snap.started, snap.problem, snap.service, snap.children,
                        snap.childStarted, snap.iface, snap.prefix, createMs, removeMs, keyAfter, note);
    }

    // ── ROOT shape ─────────────────────────────────────────────────────

    static Life RunRootLife(Variant v, int n)
    {
        string instId = @"ROOT\HIDClass\HMLAB_R";
        string hwId = "root\\VID_BEEF&PID_F0F0";
        string hwMulti = $"{hwId}\0root\\HIDMaestro\0\0";
        string infPath = System.IO.Path.Combine(DriverBuilder.BuildDir, "hidmaestro.inf");
        string note = "";
        Guid cls = HidClassGuid;
        IntPtr dis = SetupDiCreateDeviceInfoList(ref cls, IntPtr.Zero);
        if (dis == new IntPtr(-1)) throw new InvalidOperationException("SetupDiCreateDeviceInfoList");
        var sw = Stopwatch.StartNew();
        try
        {
            byte[] buf = new byte[32];
            BitConverter.GetBytes(32).CopyTo(buf, 0);
            var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                if (!SetupDiCreateDeviceInfoW(dis, instId, ref cls, "Identity lab R", IntPtr.Zero, 0, h.AddrOfPinnedObject()))
                    throw new InvalidOperationException($"SetupDiCreateDeviceInfoW err={Marshal.GetLastWin32Error()}");
                byte[] hwBytes = Encoding.Unicode.GetBytes(hwMulti);
                if (!SetupDiSetDeviceRegistryPropertyW(dis, h.AddrOfPinnedObject(), 1, hwBytes, (uint)hwBytes.Length))
                    throw new InvalidOperationException($"SPDRP_HARDWAREID err={Marshal.GetLastWin32Error()}");
                bool keyBefore = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instId}") != null;
                note += $"keyBeforeRegister={keyBefore} ";
                if (v.PreKeyPrefix != null)
                {
                    try
                    {
                        using var k = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Enum\{instId}");
                        k.SetValue("ParentIdPrefix", v.PreKeyPrefix, RegistryValueKind.String);
                        note += "prekey=ok ";
                    }
                    catch (Exception ex) { note += "prekey=FAIL(" + ex.GetType().Name + ") "; }
                }
                if (!SetupDiCallClassInstaller(0x19, dis, h.AddrOfPinnedObject()))
                    throw new InvalidOperationException($"DIF_REGISTERDEVICE err={Marshal.GetLastWin32Error()}");
            }
            finally { h.Free(); }

            try
            {
                using var dp = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Enum\{instId}\Device Parameters");
                dp.SetValue("ControllerIndex", Index, RegistryValueKind.DWord);
            }
            catch { }
            string? prefixAfterRegister = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instId}")?.GetValue("ParentIdPrefix") as string;
            note += $"prefixAfterRegister={prefixAfterRegister ?? "(none)"} ";
            if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, hwId, infPath, 0, out _))
                note += $"UpdateDriver err={Marshal.GetLastWin32Error()} ";
            DeviceManager.WaitForHidChild(instId, 10000);
            System.Threading.Thread.Sleep(300);
        }
        finally { SetupDiDestroyDeviceInfoList(dis); }
        long createMs = sw.ElapsedMilliseconds;
        var snap = Snapshot(instId, 'R');

        sw.Restart();
        if (v.FastRemoveNoWait) DeviceManager.RemoveDevice(instId, timeoutMs: 5000, fast: true, forceFallbacks: true);
        else DeviceManager.RemoveDevice(instId, timeoutMs: 120_000, forceFallbacks: true);
        long removeMs = sw.ElapsedMilliseconds;
        bool keyAfter = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instId}") != null;
        return new Life(n, instId, snap.present, snap.started, snap.problem, snap.service, snap.children,
                        snap.childStarted, snap.iface, snap.prefix, createMs, removeMs, keyAfter, note);
    }

    /// <summary>Delete lab keys that are neither present nor phantom (pure
    /// registry residue left by a pre-created key that never enumerated).</summary>
    static void PurgeResidue()
    {
        foreach (var enumName in new[] { EnumA, EnumB })
        {
            try
            {
                using var ek = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\SWD\{enumName}", writable: true);
                if (ek == null) continue;
                foreach (var sub in ek.GetSubKeyNames())
                {
                    if (!sub.StartsWith("HMLAB", StringComparison.OrdinalIgnoreCase)) continue;
                    string id = $@"SWD\{enumName}\{sub}";
                    if (CM_Locate_DevNodeW(out _, id, 0) == 0 || CM_Locate_DevNodeW(out _, id, 1) == 0) continue;
                    try { ek.DeleteSubKeyTree(sub); Console.WriteLine($"  purged residue {id}"); } catch { }
                }
            }
            catch { }
        }
    }

    // ── observation ────────────────────────────────────────────────────

    static (bool present, bool started, uint problem, string? service, string[] children, bool childStarted, bool iface, string? prefix)
        Snapshot(string inst, char shape)
    {
        bool present = CM_Locate_DevNodeW(out uint di, inst, 0) == 0;
        bool started = false; uint problem = 0;
        if (present && CM_Get_DevNode_Status(out uint st, out problem, di, 0) == 0) started = (st & DN_STARTED) != 0;
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{inst}");
        string? service = k?.GetValue("Service") as string;
        string? prefix = k?.GetValue("ParentIdPrefix") as string;
        string[] children = present ? DeviceManager.GetAllHidChildIds(inst).ToArray() : Array.Empty<string>();
        bool childStarted = false;
        foreach (var c in children)
        {
            if (CM_Locate_DevNodeW(out uint ci, c, 0) == 0 && CM_Get_DevNode_Status(out uint cs, out _, ci, 0) == 0 && (cs & DN_STARTED) != 0)
                childStarted = true;
        }
        bool iface = false;
        if (shape == 'B')
        {
            using var cl = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\DeviceClasses\{{{XusbIf}}}");
            string tag = inst.Replace('\\', '#').ToLowerInvariant();
            iface = cl != null && cl.GetSubKeyNames().Any(s => s.ToLowerInvariant().Contains(tag));
        }
        return (present, started, problem, service, children, childStarted, iface, prefix);
    }
}
