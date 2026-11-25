// mixed <KbmLiveCheck.exe>: a program on an earlier SDK, on this driver. The
// child is kbm_live_check built at an earlier release, run as
// --held-shift-child: it makes a keyboard (VID BEEF, PID F0CA), holds Left
// Shift and is killed. That SDK writes the release slots of the delivery
// acknowledgement and no wide frames section, and it zeroes the
// acknowledgement, which clears HIDMAESTRO_INPUT_ACK_SDK_WIDE. The driver
// must take the acknowledgement's release. Windows refuses an input report
// read on a keyboard collection to any handle but its own, so the frames are
// read from the sections: the program's last frame with Left Shift down, and
// the release the driver copies, every key up.
using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal static unsafe partial class Program
{
    const string MixedKeyboard = "VID_BEEF&PID_F0CA";

    static int Mixed(string exe)
    {
        Console.WriteLine("=== A program on an earlier SDK, on this driver: its keyboard killed with Left Shift held ===");
        if (SdkConsumers.Find() is { } other) { Console.WriteLine($"  SKIP: {other} has HIDMaestro.Core.dll loaded."); return 2; }
        var errors = new StringBuilder();
        var child = new Process
        {
            StartInfo = new ProcessStartInfo(exe, "--held-shift-child")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            },
        };
        child.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
        child.Start();
        child.BeginErrorReadLine();
        try { MixedWith(child, errors); }
        finally
        {
            if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
        }
        HMContext.RemoveAllVirtualControllers(HMCleanupScope.Orphaned, preserveInstall: true);
        Thread.Sleep(1000);
        Check("a sweep of orphans removes the earlier program's keyboard", HidChildren(MixedKeyboard).Count == 0,
              $"{HidChildren(MixedKeyboard).Count} HID children left");
        Console.WriteLine();
        Console.WriteLine($"=== {s_checks - s_fails}/{s_checks} checks passed ===");
        return s_fails == 0 ? 0 : 1;
    }

    static void MixedWith(Process child, StringBuilder errors)
    {
        string? line;
        while ((line = child.StandardOutput.ReadLine()) != null && !line.StartsWith("HELD")) { }
        if (line == null)
        {
            child.WaitForExit();
            string why;
            lock (errors) why = errors.ToString().Split(Environment.NewLine).FirstOrDefault(l => l.Contains("Exception"))?.Trim() ?? "no error text";
            Check("the earlier program holds Left Shift", false, $"it exited first: {why}");
            return;
        }
        Check("the earlier program's driver watches its exit", line.Contains("release-on-exit on"), line);

        // Its index: the version section its SDK wrote, which lives as long
        // as the program does.
        int index = -1;
        Version? sdk = null, driver = null;
        int driverCaps = 0;
        for (int i = 0; i < 16 && index < 0; i++)
        {
            IntPtr h = OpenFileMapping(FILE_MAP_READ, false, $@"Global\HIDMaestroVersion{i}");
            if (h == IntPtr.Zero) continue;
            try
            {
                IntPtr v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, 64);
                if (v == IntPtr.Zero) continue;
                try
                {
                    Version Read(int at) => new((ushort)Marshal.ReadInt16(v, at), (ushort)Marshal.ReadInt16(v, at + 2), (ushort)Marshal.ReadInt16(v, at + 4));
                    var s = Read(SharedMemoryIO.VERSION_OFFSET_SDK_VERSION);
                    if (s.Major == 0) continue;
                    index = i;
                    sdk = s;
                    driver = Read(SharedMemoryIO.VERSION_OFFSET_DRIVER_VERSION);
                    driverCaps = Marshal.ReadInt32(v, SharedMemoryIO.VERSION_OFFSET_DRIVER_CAPS);
                }
                finally { UnmapViewOfFile(v); }
            }
            finally { CloseHandle(h); }
        }
        var mine = typeof(HMContext).Assembly.GetName().Version!;
        Check($"the program runs an earlier SDK on this driver, {mine.Major}.{mine.Minor}.{mine.Build}, which reads wide frames",
              index >= 0 && sdk < new Version(mine.Major, mine.Minor, 0) && driver != null && driver.Major == mine.Major && driver.Minor == mine.Minor
              && (driverCaps & SharedMemoryIO.INPUT_ACK_CAP_WIDE) != 0,
              index < 0 ? "no version section" : $"index {index}, SDK {sdk}, driver {driver}, driver capabilities 0x{driverCaps:X}");
        if (index < 0) return;

        using var kb = OpenChildPage(MixedKeyboard, "0001", "0006");
        if (kb == null) { Check("the earlier program's keyboard collection opens", false); return; }
        // Windows refuses an input report read on a keyboard collection to
        // any handle but its own (HidD_GetInputReport, Win32 1, seen
        // 2026-11-24 on the Celeron), so the frames are read where the SDK
        // and the driver keep them. HidP counts a report ID byte, 0, for a
        // descriptor without report IDs, which a frame then lacks.
        int length = kb.Caps.InputReportByteLength;
        byte[] AsReport(byte[] f) => f.Length + 1 == length ? new byte[] { 0 }.Concat(f).ToArray() : f;
        bool Shift(byte[] f) => kb.On(AsReport(f), 0x07, 0, 0xE1);
        int Pressed(byte[] f)
        {
            var r = AsReport(f);
            var list = new ushort[64];
            uint n = (uint)list.Length;
            return HidP_GetUsages(HidP_Input, 0x07, 0, list, ref n, kb.Data, r, (uint)r.Length) == HIDP_STATUS_SUCCESS ? (int)n : -1;
        }
        var last = InputSectionFrame(index);
        Check("before the kill, the program's last frame holds Left Shift down (the control)", last != null && Shift(last),
              last == null ? "no extended frame" : $"{Convert.ToHexString(last)}, {Pressed(last)} keys down");
        int capsBefore = AckCaps(index);
        Check("the earlier SDK left the wide flag clear in the acknowledgement, and the driver found no wide section",
              capsBefore != -1 && (capsBefore & (SharedMemoryIO.INPUT_ACK_SDK_WIDE | SharedMemoryIO.INPUT_ACK_CAP_WIDE)) == 0, $"capabilities 0x{capsBefore:X}");
        child.Kill();
        child.WaitForExit();
        Thread.Sleep(1000);
        var set = ReadRelease(index);
        Check("the driver took the acknowledgement's release, the only one that SDK writes: a keyboard frame, every key up",
              set.Layout == "classic" && set.Frames.Count == 1 && AsReport(set.Frames[0]).Length == length && Pressed(set.Frames[0]) == 0,
              $"{set.Layout} layout, {set.Frames.Count} frames, {string.Join(" ", set.Frames.Select(f => $"{Convert.ToHexString(f)} ({Pressed(f)} keys down)"))}, report {length} bytes");
    }

    /// <summary>The extended frame the input section of
    /// <paramref name="index"/> holds, the program's last, or null.</summary>
    static byte[]? InputSectionFrame(int index)
    {
        IntPtr h = OpenFileMapping(FILE_MAP_READ, false, $@"Global\HIDMaestroInput{index}");
        if (h == IntPtr.Zero) return null;
        try
        {
            IntPtr v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, (nuint)SharedMemoryIO.SHARED_INPUT_SIZE);
            if (v == IntPtr.Zero) return null;
            try
            {
                int size = Marshal.ReadInt32(v, SharedMemoryIO.EXTENDED_SIZE_OFFSET);
                if (size <= 0 || size > SharedMemoryIO.EXTENDED_DATA_CAPACITY) return null;
                var f = new byte[size];
                Marshal.Copy(v + SharedMemoryIO.EXTENDED_DATA_OFFSET, f, 0, size);
                return f;
            }
            finally { UnmapViewOfFile(v); }
        }
        finally { CloseHandle(h); }
    }

    /// <summary>The acknowledgement section's capability word, or -1.</summary>
    static int AckCaps(int index)
    {
        IntPtr h = OpenFileMapping(FILE_MAP_READ, false, $@"Global\HIDMaestroInputAck{index}");
        if (h == IntPtr.Zero) return -1;
        try
        {
            IntPtr v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, (nuint)SharedMemoryIO.INPUT_ACK_SIZE);
            if (v == IntPtr.Zero) return -1;
            try { return Marshal.ReadInt32(v, SharedMemoryIO.INPUT_ACK_OFFSET_CAPS); }
            finally { UnmapViewOfFile(v); }
        }
        finally { CloseHandle(h); }
    }

    /// <summary>The HID child of <paramref name="vidPid"/> whose top
    /// collection is <paramref name="page"/>/<paramref name="usage"/>, four hex
    /// digits each.</summary>
    static Child? OpenChildPage(string vidPid, string page, string usage)
    {
        foreach (var n in HidChildren(vidPid))
            if (n.HardwareIds.Any(h => h.Equals($"HID_DEVICE_UP:{page}_U:{usage}", StringComparison.OrdinalIgnoreCase)) && InterfacePath(n.Id) is { } path)
                return new Child(path);
        return null;
    }
}
