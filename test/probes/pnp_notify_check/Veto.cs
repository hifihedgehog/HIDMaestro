// "veto": a Dispose while another program keeps the controller's HID child
// open with no device notification registered, as Steam held the Sony
// personas' children on 2026-10-19. Windows lets such a handle veto a
// removal it queries (PNP_VetoOutstandingOpen), and until v2.0.0 a vetoed
// Dispose took 13 s and left the child's record with no Driver and no
// Service: every later create of that identity met problem 19
// (CM_PROB_REGISTRY), and no user-mode call could remove the record.
//
// A Dispose of this release queries the removal first, as Safely Remove
// Hardware does, which changes nothing when a holder refuses it. Then a
// driver of protocol 5 fails its device when the Dispose asks, and Windows
// removes it with its children by surprise removal, which no handle vetoes.
// Two holders are run, each at an identity of its own made for this run:
//
//   still  never reads and lets go only when told. A create at the same
//          identity while it holds is refused at once with the reason:
//          Windows keeps the previous life's devnodes until the last handle
//          closes, and a create at that instance id gets them back.
//   reads  reads its handle on a thread and closes it when a read fails, as
//          SDL's HIDAPI drivers do ("Read error, device is disconnected",
//          SDL_hidapi_luna.c, then HIDAPI_JoystickDisconnected).
//
// For each: the Dispose removes the parent while the handle is open, a
// create at the same identity once the holder let go starts its HID child
// with no problem code, and at the end no devnode and no record of the
// child is left without its driver. Three Disposes with no holder are timed
// first. Then a record as an older build left it, made in the registry at an
// identity of the run's own: the next create there must start its child on
// a path of its own (DeviceManager.ChildPrefixFor) and its Dispose must end
// within 500 ms. Last, dualshock-4-v2 and dualsense at their index's
// default identity, HM_0000 on the bench machine, whose record a vetoed
// removal of an older build broke on 2026-10-19. The persona is
// amazon-luna-usb, a gamepad (Generic Desktop 01:05).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

internal static class Veto
{
    const string ProfileId = "amazon-luna-usb";
    const uint DN_STARTED = 0x8, DN_HAS_PROBLEM = 0x400;

    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    public static int Run()
    {
        Console.WriteLine("=== A Dispose while another program keeps the HID child open ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var profile = ctx.GetProfile(ProfileId) ?? throw new InvalidOperationException($"no {ProfileId}");
        string run = $"pnp-notify-veto-{Environment.ProcessId:x}-{Environment.TickCount64:x}";

        var times = new List<long>();
        for (int i = 0; i < 3; i++)
        {
            var free = ctx.CreateController(profile, run + "-free");
            var sw = Stopwatch.StartNew();
            free.Dispose();
            times.Add(sw.ElapsedMilliseconds);
        }
        Check("a Dispose with no holder ends within 500 ms", times.Max() < 500, $"{string.Join(", ", times)} ms");

        Hold(ctx, profile, run + "-still", reads: false);
        Hold(ctx, profile, run + "-reads", reads: true);
        Broken(ctx, profile, run + "-broken");

        // The default identity of index 0, HM_0000, whose child record a
        // vetoed removal of an older build left without its driver on the
        // bench machine on 2026-10-19. Elsewhere these pass as plain
        // creates.
        foreach (string id in new[] { "dualshock-4-v2", "dualsense" })
            Starts(ctx, ctx.GetProfile(id) ?? throw new InvalidOperationException($"no {id}"), null, $"{id} at its index's default identity");

        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static void Hold(HMContext ctx, HMProfile profile, string key, bool reads)
    {
        string kind = reads ? "reading holder" : "holder that never reads";
        Console.WriteLine($"  -- {kind}, identity {key}");
        var c = ctx.CreateController(profile, key);
        string parent = c.InstanceId!;
        string? child = Children(parent).FirstOrDefault();
        Check("the controller's HID child starts", child != null && WaitStarted(child),
              child == null ? "no child" : $"{child}: {Describe(Status(child))}");

        SafeFileHandle? holder = child == null ? null : OpenChild(child);
        Check("the holder opened the HID child with no device notification", holder is { IsInvalid: false });
        System.Threading.Thread? reader = null;
        int readError = 0;
        long closedAt = -1;
        var clock = Stopwatch.StartNew();
        if (reads && holder is { IsInvalid: false } h)
        {
            reader = new System.Threading.Thread(() =>
            {
                var buf = new byte[512];
                while (ReadFile(h, buf, (uint)buf.Length, out _, IntPtr.Zero)) { }
                readError = Marshal.GetLastWin32Error();
                h.Dispose();
                closedAt = clock.ElapsedMilliseconds;
            }) { IsBackground = true };
            reader.Start();
        }

        long disposeAt = clock.ElapsedMilliseconds;
        var sw = Stopwatch.StartNew();
        c.Dispose();
        long disposeMs = sw.ElapsedMilliseconds;
        if (reader != null)
        {
            reader.Join(5000);
            Check("the reading holder's read failed and it closed its handle", closedAt >= 0,
                  closedAt >= 0 ? $"Win32 {readError}, {closedAt - disposeAt} ms after the Dispose began" : "still reading");
        }
        // A holder that never lets go has Windows refuse the removal query only
        // after waiting for it, 5,376 ms on the bench machine on 2026-10-19, and
        // the surprise removal after it took 2,081 ms. A reading holder lets go
        // inside that wait. Before v2.0.0 this Dispose took 13 s and broke the
        // child's record.
        int disposeBoundMs = reads ? 3000 : 10_000;
        Check($"the Dispose removed the parent within {disposeBoundMs / 1000} s while the handle was open", !Present(parent) && disposeMs < disposeBoundMs,
              $"{disposeMs} ms, parent {Describe(Status(parent))}, child record {Record(child)}");

        if (!reads)
        {
            const string refusal = "a create at the same identity while the holder still holds is refused within 3 s and says why";
            sw.Restart();
            try
            {
                ctx.CreateController(profile, key).Dispose();
                Check(refusal, false, $"the create succeeded in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception e)
            {
                long ms = sw.ElapsedMilliseconds;
                Check(refusal, ms < 3000 && e.Message.Contains("holds its HID child open", StringComparison.Ordinal),
                      $"{ms} ms, {e.GetType().Name}: {e.Message}");
            }
            holder?.Dispose();
            var gone = Stopwatch.StartNew();
            while (gone.ElapsedMilliseconds < 10_000 && DeviceOrchestrator.RemovalPending(parent))
                System.Threading.Thread.Sleep(25);
            Console.WriteLine($"     the holder let go, and the previous life's devnode left {gone.ElapsedMilliseconds} ms later");
        }

        const string second = "a create at the same identity, once the holder let go, starts its HID child with no problem code";
        HMController? again = null;
        try
        {
            sw.Restart();
            again = ctx.CreateController(profile, key);
            long createMs = sw.ElapsedMilliseconds;
            string? child2 = Children(again.InstanceId!).FirstOrDefault();
            var (p2, s2, prob2) = child2 == null ? (false, 0u, 0u) : Status(child2);
            Check(second, child2 != null && p2 && (s2 & DN_STARTED) != 0 && (s2 & DN_HAS_PROBLEM) == 0,
                  $"{child2 ?? "no child"}: {Describe(p2, s2, prob2)}, create {createMs} ms");
        }
        catch (Exception e)
        {
            Check(second, false, $"{e.GetType().Name}: {e.Message}");
        }

        holder?.Dispose();
        string? last = again != null ? Children(again.InstanceId!).FirstOrDefault() ?? child : child;
        again?.Dispose();
        var settle = Stopwatch.StartNew();
        while (settle.ElapsedMilliseconds < 10_000 && (Present(parent) || (last != null && Present(last))))
            System.Threading.Thread.Sleep(100);
        Check("at the end the identity's devnodes are gone", !Present(parent) && (last == null || !Present(last)),
              $"parent {Describe(Status(parent))}, child {(last == null ? "none" : Describe(Status(last)))}");
        Check("no record of the identity's HID child is left without its driver", last == null || !BrokenRecord(last),
              last == null ? "" : Record(last));
    }

    /// <summary>A record as an older build's vetoed removal left it on
    /// 2026-10-19: the identity's HID child record, out of the tree, with
    /// the persona's hardware ids and no Driver, Service or ClassGUID. Made
    /// here in the registry at an identity of this run's own, since a
    /// vetoed removal of this build no longer leaves one. The next create at
    /// that identity must start its child on a path of its own, its Dispose
    /// must not wait on the record, and the record is deleted after.</summary>
    static void Broken(HMContext ctx, HMProfile profile, string key)
    {
        Console.WriteLine($"  -- a record an older build left without its driver, identity {key}");
        string prefix = DeviceIdentity.ForKey(key, 0).ParentIdPrefix;
        string record = $@"HID\HIDCLASS\{prefix}&0000";
        using (var k = Registry.LocalMachine.CreateSubKey($@"SYSTEM\CurrentControlSet\Enum\{record}"))
        {
            k.SetValue("HardwareID", new[] { "HID\\VID_1949&PID_0419", "HID\\HIDMaestro", "HID_DEVICE_SYSTEM_GAME", "HID_DEVICE" },
                       RegistryValueKind.MultiString);
            k.SetValue("Capabilities", 0xE0, RegistryValueKind.DWord);
            k.SetValue("ConfigFlags", 0, RegistryValueKind.DWord);
        }
        Check("the identity's child record is out of the tree with no driver", BrokenRecord(record), $"{record}: {Record(record)}");
        Starts(ctx, profile, key, "the next create at that identity", notOn: record);
        try { Registry.LocalMachine.DeleteSubKeyTree($@"SYSTEM\CurrentControlSet\Enum\{record}", throwOnMissingSubKey: false); }
        catch (Exception e) { Console.WriteLine($"     the record was not deleted: {e.GetType().Name}"); }
        Check("the record made for the check is deleted", Record(record) == "gone", Record(record));
    }

    /// <summary>A create that must start its HID child with no problem code
    /// and leave no record without its driver, and a Dispose with no holder
    /// that ends within 500 ms.</summary>
    static void Starts(HMContext ctx, HMProfile profile, string? key, string what, string? notOn = null)
    {
        var sw = Stopwatch.StartNew();
        HMController? c = null;
        try { c = ctx.CreateController(profile, key); }
        catch (Exception e) { Check($"{what} starts its HID child with no problem code", false, $"{e.GetType().Name}: {e.Message}"); return; }
        long createMs = sw.ElapsedMilliseconds;
        string? child = Children(c.InstanceId!).FirstOrDefault();
        bool started = child != null && WaitStarted(child);
        var st = child == null ? (false, 0u, 0u) : Status(child);
        Check($"{what} starts its HID child with no problem code", started && st.Item3 == 0
              && (notOn == null || !string.Equals(child, notOn, StringComparison.OrdinalIgnoreCase)),
              $"{child ?? "no child"}: {Describe(st)}, create {createMs} ms");
        sw.Restart();
        c.Dispose();
        long disposeMs = sw.ElapsedMilliseconds;
        Check($"{what}: its Dispose with no holder ends within 500 ms and leaves no record without its driver",
              disposeMs < 500 && (child == null || !BrokenRecord(child)), $"{disposeMs} ms, child record {Record(child)}");
    }

    /// <summary>A record of a HIDMaestro HID child that is not in the tree
    /// and has no Driver value.</summary>
    static bool BrokenRecord(string id)
    {
        if (Present(id)) return false;
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{id}");
        if (k == null) return false;
        bool ours = k.GetValue("HardwareID") is string[] hw && hw.Any(h => h.Contains("HIDMaestro", StringComparison.OrdinalIgnoreCase));
        return ours && string.IsNullOrEmpty(k.GetValue("Driver") as string);
    }

    static string Record(string? id)
    {
        if (id == null) return "none";
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{id}");
        if (k == null) return "gone";
        return $"Driver='{k.GetValue("Driver")}' Service='{k.GetValue("Service")}' ConfigFlags={k.GetValue("ConfigFlags")}";
    }

    static SafeFileHandle? OpenChild(string child)
    {
        var guid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, child, 0) == 0 && len > 1)
            {
                var buf = new char[len];
                if (CM_Get_Device_Interface_ListW(ref guid, child, buf, len, 0) == 0)
                {
                    string path = new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                    var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (!h.IsInvalid) return h;
                    h.Dispose();
                }
            }
            System.Threading.Thread.Sleep(50);
        }
        return null;
    }

    /// <summary>Up to 3 s for the child to start.</summary>
    static bool WaitStarted(string id)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var st = Status(id);
            if (st.Present && (st.Status & DN_STARTED) != 0) return true;
            if (sw.ElapsedMilliseconds > 3_000) return false;
            System.Threading.Thread.Sleep(50);
        }
    }

    static List<string> Children(string parent)
    {
        var list = new List<string>();
        if (CM_Locate_DevNodeW(out uint p, parent, 0) != 0 || CM_Get_Child(out uint c, p, 0) != 0) return list;
        do
        {
            var buf = new char[400];
            if (CM_Get_Device_IDW(c, buf, buf.Length, 0) == 0) list.Add(new string(buf, 0, Array.IndexOf(buf, '\0')));
        }
        while (CM_Get_Sibling(out c, c, 0) == 0);
        return list;
    }

    static bool Present(string id) => CM_Locate_DevNodeW(out _, id, 0) == 0;

    static (bool Present, uint Status, uint Problem) Status(string id)
    {
        // A child Windows enumerated but could not configure is not found
        // by a normal locate. A phantom locate finds it, and the status call
        // fails only for a devnode that is not in the tree.
        if (CM_Locate_DevNodeW(out uint d, id, 1) != 0) return (false, 0, 0);
        return CM_Get_DevNode_Status(out uint s, out uint prob, d, 0) == 0 ? (true, s, prob) : (false, 0, 0);
    }

    static string Describe((bool Present, uint Status, uint Problem) s) => Describe(s.Present, s.Status, s.Problem);

    static string Describe(bool present, uint status, uint problem) =>
        !present ? "not in the tree" : $"in the tree, status 0x{status:X8}, problem {problem}";

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Locate_DevNodeW(out uint dn, string id, uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Child(out uint dn, uint parent, uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_Sibling(out uint dn, uint dev, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Get_Device_IDW(uint dn, char[] buf, int len, uint flags);
    [DllImport("cfgmgr32.dll")] static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint dn, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid g, string id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern uint CM_Get_Device_Interface_ListW(ref Guid g, string id, char[] buf, uint len, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadFile(SafeFileHandle h, byte[] buf, uint len, out uint read, IntPtr ov);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFileW(string p, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
}
