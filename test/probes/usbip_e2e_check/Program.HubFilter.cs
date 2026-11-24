// Issue #76. Windows' USB audio driver takes the composite's speaker stream
// one 10 ms period at a time only while usbip-win2's filter sits on the USB/IP
// host controller's root hub, and the SDK puts it there, on that hub alone.
// Without the filter it takes the stream in 20 ms pairs, and a game that
// renders to the pad and to its main output from one audio engine drops
// samples on the main output.
//
// DrainCadence is the reporter's measurement: a shared-mode, event-driven
// render of silence, with the padding read at every 10 ms event before the
// free space is filled. A steady drain reads one value at every event. A
// paired drain reads a full buffer at every other event. RootHubs reads every
// USB 3 root hub's parent, driver stack and UpperFilters from Plug and Play.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;

internal static partial class Program
{
    internal readonly record struct Drain(int Buffer, int Events, int Steady, int Full, string Histogram);

    /// <summary>Render silence to <paramref name="render"/> for
    /// <paramref name="length"/> and read the padding at every event. The
    /// first second is the stream's start and is left out of the counts.</summary>
    internal static Drain DrainCadence(MMDevice render, TimeSpan length)
    {
        var ac = render.AudioClient;
        try
        {
            ac.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.EventCallback, 0, 0, ac.MixFormat, Guid.Empty);
            int buffer = ac.BufferSize;
            using var ev = new EventWaitHandle(false, EventResetMode.AutoReset);
            ac.SetEventHandle(ev.SafeWaitHandle.DangerousGetHandle());
            var rc = ac.AudioRenderClient;
            rc.GetBuffer(buffer);
            rc.ReleaseBuffer(buffer, AudioClientBufferFlags.Silent);
            var padding = new List<int>(1024);
            ac.Start();
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < length)
            {
                if (!ev.WaitOne(1000)) break;
                int pad = ac.CurrentPadding;
                padding.Add(pad);
                int free = buffer - pad;
                if (free > 0)
                {
                    rc.GetBuffer(free);
                    rc.ReleaseBuffer(free, AudioClientBufferFlags.Silent);
                }
            }
            ac.Stop();
            int skip = Math.Min(100, padding.Count / 4);
            var steady = padding.Skip(skip).ToList();
            string hist = string.Join(", ", steady.GroupBy(p => p).OrderByDescending(g => g.Count()).Take(4).Select(g => $"{g.Key} x{g.Count()}"));
            return new(buffer, padding.Count, steady.Count, steady.Count(p => p == buffer), hist);
        }
        finally { ac.Dispose(); }
    }

    internal readonly record struct RootHub(string Id, string Parent, bool UnderUsbip, string[] Stack, string[] Upper);

    /// <summary>Every present USB 3 root hub.</summary>
    internal static List<RootHub> RootHubs()
    {
        var hubs = new List<RootHub>();
        foreach (string id in PresentIds("USB"))
        {
            if (!id.StartsWith(@"USB\ROOT_HUB30\", StringComparison.OrdinalIgnoreCase)) continue;
            if (CM_Locate_DevNodeW(out uint dn, id, 0) != 0) continue;
            string parent = string.Join("", PropertyStrings(dn, ParentKey));
            bool underUsbip = false;
            if (parent.Length > 0 && CM_Locate_DevNodeW(out uint pdn, parent, 0) == 0)
                underUsbip = PropertyStrings(pdn, HardwareIdsKey).Any(h => h.Contains("USBIP_WIN2", StringComparison.OrdinalIgnoreCase));
            hubs.Add(new(id, parent, underUsbip, PropertyStrings(dn, StackKey), UpperFilters(dn)));
        }
        return hubs;
    }

    static List<string> PresentIds(string enumerator)
    {
        const uint CM_GETIDLIST_FILTER_ENUMERATOR = 0x00000001, CM_GETIDLIST_FILTER_PRESENT = 0x00000100; // cfgmgr32.h
        uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
        if (CM_Get_Device_ID_List_SizeW(out uint len, enumerator, flags) != 0 || len == 0) return new();
        var buf = new char[len];
        if (CM_Get_Device_ID_ListW(enumerator, buf, len, flags) != 0) return new();
        return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    static string[] PropertyStrings(uint dn, DevPropKey key)
    {
        uint size = 0;
        CM_Get_DevNode_PropertyW(dn, ref key, out _, null, ref size, 0);
        if (size == 0) return Array.Empty<string>();
        var buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(dn, ref key, out _, buf, ref size, 0) != 0) return Array.Empty<string>();
        return Encoding.Unicode.GetString(buf, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    static string[] UpperFilters(uint dn)
    {
        const uint CM_DRP_UPPERFILTERS = 0x00000012; // cfgmgr32.h
        uint type = 0, len = 0;
        CM_Get_DevNode_Registry_PropertyW(dn, CM_DRP_UPPERFILTERS, ref type, null, ref len, 0);
        if (len == 0) return Array.Empty<string>();
        var buf = new byte[len];
        if (CM_Get_DevNode_Registry_PropertyW(dn, CM_DRP_UPPERFILTERS, ref type, buf, ref len, 0) != 0) return Array.Empty<string>();
        return Encoding.Unicode.GetString(buf, 0, (int)len).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DevPropKey { public Guid fmtid; public uint pid; }

    // devpkey.h
    static readonly DevPropKey ParentKey = new() { fmtid = new Guid("4340a6c5-93fa-4706-972c-7b648008a5a7"), pid = 8 };
    static readonly DevPropKey HardwareIdsKey = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 3 };
    static readonly DevPropKey StackKey = new() { fmtid = new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), pid = 14 };

    // CM_Locate_DevNodeW and CM_Get_DevNode_Registry_PropertyW are declared
    // in Program.cs.

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    static extern int CM_Get_Device_ID_List_SizeW(out uint pulLen, string pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_ListW")]
    static extern int CM_Get_Device_ID_ListW(string pszFilter, [Out] char[] Buffer, uint BufferLen, uint ulFlags);

    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    static extern int CM_Get_DevNode_PropertyW(uint dnDevInst, ref DevPropKey PropertyKey, out uint PropertyType, byte[]? PropertyBuffer, ref uint PropertyBufferSize, uint ulFlags);
}
