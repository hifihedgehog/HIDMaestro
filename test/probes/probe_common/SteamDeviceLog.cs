// The lines Steam's controller.txt holds about one device, and whether
// Steam still holds that device open.
//
// Steam logs each HID device it finds as a "Local Device Found" block, and
// the lines after a block, up to the next one, are about that device:
//
//   [2026-10-19 05:47:50] Local Device Found
//     type: 28de 1205
//     path: \\?\HID#VID_28DE&PID_1205&MI_02#4&29c808b9&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}
//     serial_number: FX1A00000002 - 0
//
// A device Steam reaches through SDL logs "path: sdl://<n>" instead. The
// close line, "Controller device closed after hid_read failure", names no
// device at all. At 05:47:51 that day Steam logged it for a Switch Pro
// persona another program had made, removed one second after the Deck
// persona above was opened, and the Deck stayed open. A probe that read
// the close as its own failed on a device it never made.
//
// So a probe finds its block by its own device's interface path, and reads
// a close off Windows instead of the log. While Steam holds an interface,
// an open that shares nothing fails with ERROR_SHARING_VIOLATION. SDL's
// hidapi, which Steam's input is built on, opens with FILE_SHARE_READ |
// FILE_SHARE_WRITE (SDL-reference src/hidapi/windows/hid.c:393-401).
// DS4Windows' exclusive mode is that same open with no sharing, and it
// fails while another program holds the pad, so DS4Windows restarts the
// device to take it (DS4Windows HidLibrary/HidDevice.cs:209-222 and
// DS4Library/DS4Devices.cs:284-311).

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class SteamDeviceLog
{
    static readonly Guid HidInterface = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    const string Found = "Local Device Found";

    /// <summary>The log from <paramref name="offset"/> on, read while
    /// Steam holds the file open. Empty when it cannot be read.</summary>
    public static string Read(string path, long offset)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            if (offset > fs.Length) offset = 0;   // Steam rotated it
            fs.Seek(offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return string.Empty; }
    }

    public static long Length(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    /// <summary>Every block in <paramref name="log"/>, in log order. Each
    /// runs from its "Local Device Found" to the next one, or the
    /// end.</summary>
    public static List<string> All(string log)
    {
        var blocks = new List<string>();
        int at = log.IndexOf(Found, StringComparison.Ordinal);
        while (at >= 0)
        {
            int next = log.IndexOf(Found, at + Found.Length, StringComparison.Ordinal);
            blocks.Add(next < 0 ? log[at..] : log[at..next]);
            at = next;
        }
        return blocks;
    }

    /// <summary>The blocks in <paramref name="log"/> whose path line names
    /// one of <paramref name="paths"/>, in log order.</summary>
    public static List<string> Blocks(string log, IReadOnlyCollection<string> paths)
    {
        var blocks = new List<string>();
        foreach (var block in All(log))
        {
            string? path = PathOf(block);
            if (path == null) continue;
            foreach (var p in paths)
                if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) { blocks.Add(block); break; }
        }
        return blocks;
    }

    /// <summary>The device's first block, or an empty string when Steam
    /// has not found it.</summary>
    public static string Own(string log, IReadOnlyCollection<string> paths)
    {
        var blocks = Blocks(log, paths);
        return blocks.Count > 0 ? blocks[0] : string.Empty;
    }

    /// <summary>The path a block names, or null.</summary>
    public static string? PathOf(string block)
    {
        foreach (var line in block.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("path:", StringComparison.Ordinal)) return t[5..].Trim();
        }
        return null;
    }

    /// <summary>Whether some program holds the interface at
    /// <paramref name="path"/> open: an open that shares nothing fails
    /// with ERROR_SHARING_VIOLATION while it does. When the open succeeds
    /// nobody held it, and the handle is closed at once. Any other failure
    /// (the device is gone, for one) counts as not held, and its error
    /// number is in <paramref name="error"/>.</summary>
    public static bool HeldOpen(string path, out int error)
    {
        using SafeFileHandle h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, 0, IntPtr.Zero,
                                             OPEN_EXISTING, 0, IntPtr.Zero);
        error = h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return h.IsInvalid && error == ERROR_SHARING_VIOLATION;
    }

    /// <summary>The HID interface paths of every HID device below the device
    /// <paramref name="instanceId"/>, at any depth. Empty until Windows has
    /// enumerated them.</summary>
    public static List<string> HidPathsUnder(string instanceId)
    {
        var paths = new List<string>();
        if (CM_Locate_DevNodeW(out uint root, instanceId, 0) != 0) return paths;
        var pending = new Stack<uint>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            if (CM_Get_Child(out uint child, pending.Pop(), 0) != 0) continue;
            while (true)
            {
                pending.Push(child);
                string? id = DeviceId(child);
                if (id != null && id.StartsWith(@"HID\", StringComparison.OrdinalIgnoreCase))
                    paths.AddRange(Interfaces(HidInterface, id));
                if (CM_Get_Sibling(out uint next, child, 0) != 0) break;
                child = next;
            }
        }
        return paths;
    }

    /// <summary>The HID interface paths whose serial string is
    /// <paramref name="serial"/>. A driver-backed controller's is
    /// HM-CTL- and its four-digit transport index
    /// (DeviceOrchestrator.DriverSerial).</summary>
    public static List<string> HidPathsWithSerial(string serial)
    {
        var paths = new List<string>();
        foreach (var path in Interfaces(HidInterface, null))
        {
            // No access is asked for, so a collection Windows holds
            // exclusively, a keyboard's for one, still answers.
            using SafeFileHandle h = CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero,
                                                 OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            var buf = new byte[256];
            if (!HidD_GetSerialNumberString(h, buf, buf.Length)) continue;
            string s = Encoding.Unicode.GetString(buf).Split('\0')[0];
            if (s == serial) paths.Add(path);
        }
        return paths;
    }

    static string? DeviceId(uint node)
    {
        var sb = new char[512];
        return CM_Get_Device_IDW(node, sb, sb.Length, 0) == 0 ? new string(sb).Split('\0')[0] : null;
    }

    static List<string> Interfaces(Guid guid, string? instanceId)
    {
        var list = new List<string>();
        if (CM_Get_Device_Interface_List_SizeW(out int len, ref guid, instanceId, 0) != 0 || len <= 1) return list;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref guid, instanceId, buf, len, 0) != 0) return list;
        foreach (var p in new string(buf).Split('\0'))
            if (p.Length > 0) list.Add(p);
        return list;
    }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, OPEN_EXISTING = 3;
    const int ERROR_SHARING_VIOLATION = 32;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetSerialNumberString(SafeFileHandle h, byte[] buffer, int length);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Child(out uint child, uint node, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Sibling(out uint sibling, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint node, char[] buffer, int length, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid guid, string? deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid guid, string? deviceId, char[] buffer, int length, uint flags);
}
