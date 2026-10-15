// Dispose timing, per profile type, seen from outside the SDK: for each
// devnode a controller owns, when it left the device tree (the first failed
// CM_LOCATE_DEVNODE_NORMAL locate), when its DEVICEINSTANCEREMOVED
// notification arrived, and when its PHANTOM record went, against the start
// of Dispose, and the whole Dispose. It uses only what master 238bd2b already
// has, so the same binary times that build's SDK when its HIDMaestro.Core.dll
// is put beside it.
//
// Creates and removes devices and needs elevation: run it through the bench,
// never by hand. Exit 0 when every cycle ran, 1 when one failed, 2 when
// another program that embeds HIDMaestro is running.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;

internal static unsafe partial class Measure
{
    const uint CR_SUCCESS = 0;
    const uint CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE = 2;
    const uint CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED = 9;

    [StructLayout(LayoutKind.Sequential)]
    struct CM_NOTIFY_FILTER_INSTANCE
    {
        public uint cbSize;
        public uint Flags;
        public uint FilterType;
        public uint Reserved;
        public fixed char InstanceId[200];
    }

    [LibraryImport("CfgMgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint pdnDevInst, string pDeviceID, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Get_Child(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Get_Sibling(out uint pdnDevInst, uint dnDevInst, uint ulFlags);

    [LibraryImport("CfgMgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static partial uint CM_Get_Device_ID(uint dnDevInst, char* buffer, uint bufferLen, uint ulFlags);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Register_Notification(ref CM_NOTIFY_FILTER_INSTANCE pFilter, IntPtr pContext,
        delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, IntPtr, uint, uint> pCallback, out IntPtr pNotifyContext);

    [LibraryImport("CfgMgr32.dll")]
    private static partial uint CM_Unregister_Notification(IntPtr NotifyContext);

    /// <summary>One devnode's removal as seen from here, in Stopwatch ticks
    /// from the start of Dispose. -1 until seen.</summary>
    sealed class Node
    {
        public required string Id;
        public required string Role;
        /// <summary>A software device and its children keep their records
        /// after SwDeviceClose, so their records are not waited for.</summary>
        public required bool KeepsRecord;
        public long Left = -1, Removed = -1, RecordGone = -1;
        public IntPtr Registration;
        public GCHandle Self;
    }

    static long s_start;

    /// <summary>An exception must not unwind into cfgmgr32, and one left
    /// unhandled here ends the process, so none escapes.</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static uint OnEvent(IntPtr hNotify, IntPtr context, uint action, IntPtr data, uint size)
    {
        try
        {
            if (action == CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED && context != IntPtr.Zero
                && GCHandle.FromIntPtr(context).Target is Node n)
                Interlocked.CompareExchange(ref n.Removed, Stopwatch.GetTimestamp() - Volatile.Read(ref s_start), -1);
        }
        catch { }
        return 0;
    }

    /// <summary>The CONFIGRET of an instance-filter registration for
    /// <paramref name="id"/>, unregistered again at once.</summary>
    internal static uint RegisterResult(string id)
    {
        var filter = new CM_NOTIFY_FILTER_INSTANCE
        {
            cbSize = (uint)sizeof(CM_NOTIFY_FILTER_INSTANCE),
            FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE,
        };
        int len = Math.Min(id.Length, 199);
        id.AsSpan(0, len).CopyTo(new Span<char>(filter.InstanceId, 200));
        filter.InstanceId[len] = '\0';
        uint cr = CM_Register_Notification(ref filter, IntPtr.Zero, &OnEvent, out IntPtr h);
        if (cr == CR_SUCCESS) CM_Unregister_Notification(h);
        return cr;
    }

    static List<string> Children(string parent)
    {
        var ids = new List<string>();
        if (CM_Locate_DevNodeW(out uint p, parent, 0) != CR_SUCCESS || CM_Get_Child(out uint c, p, 0) != CR_SUCCESS)
            return ids;
        char* buf = stackalloc char[512];
        do
        {
            if (CM_Get_Device_ID(c, buf, 512, 0) == CR_SUCCESS) ids.Add(new string(buf));
        } while (CM_Get_Sibling(out c, c, 0) == CR_SUCCESS);
        return ids;
    }

    static bool Located(string id, uint flags) => CM_Locate_DevNodeW(out _, id, flags) == CR_SUCCESS;

    static string Stats(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0) return "n=0";
        return $"n={v.Length} p50={v[v.Length / 2]:F1} p95={v[(int)(v.Length * 0.95)]:F1} max={v[^1]:F1} ms";
    }

    internal static int Run(int cycles)
    {
        Console.WriteLine($"=== Dispose timing per profile type, {cycles} cycles each ===");
        double ms = 1000.0 / Stopwatch.Frequency;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        int failed = 0, missing = 0;
        foreach (string profileId in new[] { "dualsense", "xbox-360-wired", "xbox-series-xs-bt" })
        {
            var profile = ctx.GetProfile(profileId)!;
            var dispose = new List<double>();
            var rows = new Dictionary<string, (List<double> Gap, List<double> Removed, List<double> Record, int Missing)>();
            for (int i = 0; i < cycles; i++)
            {
                HMController c;
                try { c = ctx.CreateController(profile, "pnp-notify-measure"); }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [FAIL] {profileId} cycle {i}: create threw {ex.GetType().Name}: {ex.Message}");
                    failed++;
                    continue;
                }
                var nodes = new List<Node>();
                string? main = c.InstanceId;
                if (main != null)
                {
                    bool software = main.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase);
                    nodes.Add(new Node { Id = main, Role = "parent", KeepsRecord = software });
                    foreach (string child in Children(main))
                        nodes.Add(new Node { Id = child, Role = "child", KeepsRecord = software });
                }
                string? token = c.Lease?.Identity.Token;
                string companion = $@"SWD\HIDMAESTRO\{token}";
                if (token != null && !string.Equals(companion, main, StringComparison.OrdinalIgnoreCase) && Located(companion, 0))
                    nodes.Add(new Node { Id = companion, Role = "companion", KeepsRecord = true });

                foreach (var n in nodes)
                {
                    n.Self = GCHandle.Alloc(n);
                    var filter = new CM_NOTIFY_FILTER_INSTANCE
                    {
                        cbSize = (uint)sizeof(CM_NOTIFY_FILTER_INSTANCE),
                        FilterType = CM_NOTIFY_FILTER_TYPE_DEVICEINSTANCE,
                    };
                    int len = Math.Min(n.Id.Length, 199);
                    n.Id.AsSpan(0, len).CopyTo(new Span<char>(filter.InstanceId, 200));
                    filter.InstanceId[len] = '\0';
                    if (CM_Register_Notification(ref filter, GCHandle.ToIntPtr(n.Self), &OnEvent, out n.Registration) != CR_SUCCESS)
                        n.Registration = IntPtr.Zero;
                }

                // A poller notes when each devnode leaves the tree and when
                // its record goes, while this thread disposes.
                using var stop = new ManualResetEventSlim(false);
                var poller = new Thread(() =>
                {
                    var until = long.MaxValue;
                    while (Stopwatch.GetTimestamp() < until)
                    {
                        long now = Stopwatch.GetTimestamp() - Volatile.Read(ref s_start);
                        foreach (var n in nodes)
                        {
                            if (n.Left < 0 && !Located(n.Id, 0)) n.Left = now;
                            if (n.RecordGone < 0 && !Located(n.Id, 1)) n.RecordGone = now;
                        }
                        if (until == long.MaxValue && stop.IsSet)
                            until = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
                        if (nodes.All(n => n.Left >= 0 && Volatile.Read(ref n.Removed) >= 0
                                           && (n.RecordGone >= 0 || n.KeepsRecord)) && stop.IsSet)
                            break;
                        Thread.SpinWait(200);
                    }
                }) { IsBackground = true };

                Volatile.Write(ref s_start, Stopwatch.GetTimestamp());
                poller.Start();
                long t0 = Stopwatch.GetTimestamp();
                c.Dispose();
                dispose.Add((Stopwatch.GetTimestamp() - t0) * ms);
                stop.Set();
                poller.Join();

                foreach (var n in nodes)
                {
                    if (n.Registration != IntPtr.Zero) CM_Unregister_Notification(n.Registration);
                    n.Self.Free();
                    if (!rows.TryGetValue(n.Role, out var r)) rows[n.Role] = r = (new(), new(), new(), 0);
                    long removed = Volatile.Read(ref n.Removed);
                    if (removed < 0 || n.Left < 0) { rows[n.Role] = (r.Gap, r.Removed, r.Record, r.Missing + 1); continue; }
                    r.Gap.Add((removed - n.Left) * ms);
                    r.Removed.Add(removed * ms);
                    if (n.RecordGone >= 0) r.Record.Add(n.RecordGone * ms);
                }
            }

            Console.WriteLine($"  {profileId}");
            Console.WriteLine($"    Dispose                              {Stats(dispose)}");
            foreach (var (role, r) in rows)
            {
                Console.WriteLine($"    {role,-9} REMOVED after leaving the tree {Stats(r.Gap)}");
                Console.WriteLine($"    {role,-9} REMOVED after Dispose began    {Stats(r.Removed)}");
                Console.WriteLine($"    {role,-9} record gone after Dispose began {Stats(r.Record)}");
                if (r.Missing > 0)
                    Console.WriteLine($"  [FAIL] {profileId} {role}: {r.Missing} devnode(s) with no REMOVED or no departure seen within 10 s of Dispose");
                missing += r.Missing;
            }
        }
        Console.WriteLine();
        Console.WriteLine(failed == 0 && missing == 0
            ? "=== every cycle ran, and every devnode left the tree and got its REMOVED ==="
            : $"=== {failed} cycle(s) failed, {missing} devnode(s) without a departure or a REMOVED ===");
        return failed == 0 && missing == 0 ? 0 : 1;
    }
}
