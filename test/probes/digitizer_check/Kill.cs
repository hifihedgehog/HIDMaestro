// kill <umdf2|usbip>: what is left when a program goes away with contacts
// down. A one-contact-per-report touchscreen, in process: three contacts
// take three reports in Windows' hybrid form, so the release the driver
// keeps must be three lift frames, the first with Contact Count 3. Then a
// child process of this probe holds contacts on a touchscreen and is
// killed, three times: two contacts on the spike touchscreen (two a
// report), three on the Elan touchscreen of the Dell XPS 13 9360 (ten a
// report, a 116-byte report), and ten on a one-contact-per-report
// touchscreen (ten reports). On UMDF2 the driver sends the release frames
// when the child's owner pipe closes. Windows' touch stack holds a touch
// collection for exclusive reads, as it holds a keyboard's or a mouse's, so
// no reader of this probe can see the reports: the release section the
// driver copies them from is read after the kill instead, every lift frame
// in order and at the report's full length, the first with the whole count,
// and the touch report must read back lifted. On USB/IP the device goes
// with the child's process.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal static unsafe partial class Program
{
    // One finger a report, ids 0 to 255, up to five contacts (Contact Count
    // and its maximum 0 to 5), on report 1 so UMDF2's input report read
    // answers it.
    const string HybridDescriptor =
        "050d0904a1018501" + "0922a102" + "09421500250175019501810209328102094781029505810375080951950126ff008102" +
        "050126ff7f7510550065003500460000" + "0930810209318102" + "c0" +
        "050d09549501750815002505810285020955b102" + "c0" +
        "090ea1018503" + "0923a102" + "09520953150025" + "0a75089502b102" + "c0" + "c0";

    // The same with twelve contacts at most: ten down take ten reports,
    // more than the driver's eight release slots of 2.3.0.
    static readonly string Hybrid12Descriptor = HybridDescriptor.Replace("75081500250581028502", "75081500250c81028502");

    static string KillProfiles()
    {
        string dir = SpikeProfiles();
        string Stubs(params (string Id, string Data)[] reports) => "{ \"reports\": [ " + string.Join(", ", reports.Select(r =>
            $"{{ \"id\": \"{r.Id}\", \"size\": {r.Data.Length / 2}, \"data\": \"{r.Data}\" }}")) + " ] }";
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "spike-touch-hybrid.json"),
            Profile("spike-touch-hybrid", "HIDMaestro spike hybrid touchscreen", "0x000A", HybridDescriptor,
                    Stubs(("0x02", "0205"), ("0x03", "030000")), "touchscreen"));
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "spike-touch-hybrid12.json"),
            Profile("spike-touch-hybrid12", "HIDMaestro spike hybrid touchscreen 12", "0x000C", Hybrid12Descriptor,
                    Stubs(("0x02", "020c"), ("0x03", "030000")), "touchscreen"));
        // The Elan touchscreen (hid-tools tests_kernel test_multitouch.py):
        // Contact Count Maximum 10 on report 0x0A, Microsoft's default
        // certification blob on report 0x44.
        string data = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "kernel-digitizers.json");
        using (var doc = JsonDocument.Parse(System.IO.File.ReadAllText(data)))
        {
            string elan = doc.RootElement.EnumerateArray().First(e => e.GetProperty("name").GetString() == "TestElanXPS9360")
                                         .GetProperty("descriptor").GetString()!;
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "kill-elan-xps9360.json"),
                Profile("kill-elan-xps9360", "HIDMaestro Elan touchscreen", "0x000B", elan,
                        Stubs(("0x0A", "0a0a"), ("0x44", "44" + PtpBlob)), "touchscreen"));
        }
        return dir;
    }

    /// <summary>The child: <paramref name="n"/> contacts down on
    /// <paramref name="profileId"/>, ids 0 up, until it is killed.</summary>
    static int HoldTouch(HMBackend backend, string profileId, int n)
    {
        using var ctx = new HMContext(backend);
        ctx.LoadProfilesFromDirectory(KillProfiles());
        var profile = ctx.GetProfile(profileId)!;
        var c = ctx.CreateController(profile, "digitizer-held-touch", backend);
        Thread.Sleep(1500);
        var t = profile.Touch!;
        int y = t.Y.Minimum + (t.Y.Maximum - t.Y.Minimum) / 10;
        int step = Math.Max(1, (t.X.Maximum - t.X.Minimum) / 40);
        int x0 = At(t.X, 2) - step * (n / 2);
        c.SubmitTouch(Enumerable.Range(0, n).Select(i => new HMTouchContact { Id = i, X = x0 + i * step, Y = y }).ToArray());
        Thread.Sleep(300);
        Console.WriteLine($"HELD backend {c.Backend} release-on-exit {(c.ReleasesOnConsumerExit ? "on" : "off")} index {c.Index}");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    static int Kill(HMBackend backend)
    {
        Console.WriteLine($"=== Contacts down when a program goes away, on {backend} ===");
        if (SdkConsumers.Find() is { } other) { Console.WriteLine($"  SKIP: {other} has HIDMaestro.Core.dll loaded."); return 2; }
        string dir = KillProfiles();

        // The hybrid release, in process.
        using (var ctx = new HMContext(backend))
        {
            ctx.InstallDriver();
            ctx.LoadProfilesFromDirectory(dir);
            var profile = ctx.GetProfile("spike-touch-hybrid")!;
            var t = profile.Touch!;
            Check("the hybrid touchscreen: one contact a report, five at most", t is { ContactsPerReport: 1, MaxContacts: 5 },
                  $"{t.ContactsPerReport} per report, {t.MaxContacts} at most");
            using var c = ctx.CreateController(profile, null, backend);
            Thread.Sleep(1500);
            int y = t.Y.Minimum + (t.Y.Maximum - t.Y.Minimum) / 10;
            c.SubmitTouch(new[] { 7, 8, 9 }.Select((id, i) => new HMTouchContact { Id = id, X = At(t.X, 2) + i * 300, Y = y }).ToArray());
            var kept = (List<byte[]>)typeof(HMController).GetField("_releaseFrames", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(c)!;
            var frames = kept.Select(f => f.ToArray()).ToList();
            var desc = Convert.FromHexString(HybridDescriptor);
            var read = frames.Select(f => Read(f, desc)).ToList();
            Check("three contacts in hybrid form leave three lift frames: Contact Count 3, then 0 and 0, ids 7, 8 and 9, every tip off",
                  read.Count == 3 && read.Select(r => r.Count).SequenceEqual(new[] { 3, 0, 0 })
                  && read.Select(r => r.Fingers[0].Id).SequenceEqual(new[] { 7, 8, 9 }) && read.All(r => r.Fingers[0].Tip == 0),
                  string.Join(" | ", read.Select(r => $"count {r.Count} id {(r.Fingers.Count > 0 ? r.Fingers[0].Id : -1)} tip {(r.Fingers.Count > 0 ? r.Fingers[0].Tip : -1)}")));
            c.SubmitTouch(ReadOnlySpan<HMTouchContact>.Empty);
            Check("once every contact lifts, no touch release frame is left", kept.Count == 0, $"{kept.Count} left");

            // A touchpad button pressed with no contact down is let go too.
            var padProfile = ctx.GetProfile("spike-touchpad")!;
            using var pad = ctx.CreateController(padProfile, null, backend);
            Thread.Sleep(1500);
            pad.SubmitTouch(ReadOnlySpan<HMTouchContact>.Empty, HMMouseButtons.Left);
            var padKept = (List<byte[]>)typeof(HMController).GetField("_releaseFrames", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pad)!;
            var padDesc = Convert.FromHexString(PadDescriptor);
            const uint Button1 = 0x00090001;
            string PadShow() => string.Join(" | ", padKept.Select(f => $"count {Read(f, padDesc).Count} button {UsageValue(f, padDesc, Button1)}"));
            Check("a touchpad button pressed with no contact down leaves one release frame, no contact and the button up",
                  padKept.Count == 1 && Read(padKept[0], padDesc).Count == 0 && UsageValue(padKept[0], padDesc, Button1) == 0, PadShow());
            pad.SubmitTouch(ReadOnlySpan<HMTouchContact>.Empty);
            Check("once the button is up, no touchpad release frame is left", padKept.Count == 0, $"{padKept.Count} left");
        }

        KillCase(backend, "spike-touchscreen", "0007", 2);
        KillCase(backend, "kill-elan-xps9360", "000B", 3);
        KillCase(backend, "spike-touch-hybrid12", "000C", 10);
        Console.WriteLine();
        Console.WriteLine($"=== {s_checks - s_fails}/{s_checks} checks passed ===");
        return s_fails == 0 ? 0 : 1;
    }

    /// <summary>A child killed with <paramref name="n"/> contacts down on
    /// <paramref name="profileId"/>, whose devices carry PID
    /// <paramref name="pid"/>.</summary>
    static void KillCase(HMBackend backend, string profileId, string pid, int n)
    {
        Console.WriteLine($"-- {n} contacts down on {profileId} when its program is killed --");
        string vidPid = "VID_1209&PID_" + pid;
        // The child's errors come back here: inherited, a crash's text would
        // reach the run script's own error stream, which ends the run.
        var errors = new System.Text.StringBuilder();
        var child = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.ProcessPath!, $"hold-touch {backend} {profileId} {n}")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            },
        };
        child.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
        child.Start();
        child.BeginErrorReadLine();
        // The child holds contacts on the display, so it never outlives the
        // case, whatever this probe throws.
        try { KillCaseWith(child, backend, profileId, vidPid, n, errors); }
        finally
        {
            if (!child.HasExited) { child.Kill(); child.WaitForExit(); }
        }
        // The killed child's device goes with a sweep of orphans, which
        // leaves every running program's controllers alone.
        HMContext.RemoveAllVirtualControllers(HMCleanupScope.Orphaned, preserveInstall: true);
        Thread.Sleep(1000);
        Check($"{profileId}: a sweep of orphans removes the killed child's touchscreen", HidChildren(vidPid).Count == 0,
              $"{HidChildren(vidPid).Count} HID children left");
    }

    static void KillCaseWith(Process child, HMBackend backend, string profileId, string vidPid, int n, System.Text.StringBuilder errors)
    {
        string? line;
        while ((line = child.StandardOutput.ReadLine()) != null && !line.StartsWith("HELD")) { }
        if (line == null)
        {
            child.WaitForExit();
            string why;
            lock (errors) why = errors.ToString().Split(Environment.NewLine).FirstOrDefault(l => l.Contains("Exception"))?.Trim() ?? "no error text";
            Check($"{profileId}: the child holds its contacts", false, $"it exited first: {why}");
            return;
        }
        bool usbip = line.Contains(" backend Usbip ");
        int index = int.Parse(line[(line.LastIndexOf(' ') + 1)..]);
        Check(usbip ? $"{profileId}: on USB/IP the device goes with the child, so nothing announces a release on exit" : $"{profileId}: the driver watches the child's exit",
              line.Contains(usbip ? "release-on-exit off" : "release-on-exit on"), line);
        using (var touch = OpenChild(vidPid, "0004"))
        {
            if (touch == null) { Check($"{profileId}: the child's touch collection opens", false); return; }
            ushort[] fingers = Enumerable.Range(0, touch.Links.Length).Where(i => touch.Links[i].LinkUsagePage == 0x0D && touch.Links[i].LinkUsage == 0x22)
                                         .Select(i => (ushort)i).ToArray();
            int per = fingers.Length, length = touch.Caps.InputReportByteLength;
            string Show(byte[]? rep) => rep == null ? "no read" : string.Join(", ", fingers.Take(Math.Min(per, 3)).Select(f =>
                $"id {touch.Value(rep, 0x0D, f, 0x51)} tip {(touch.On(rep, 0x0D, f, 0x42) ? 1 : 0)}")) + $", count {touch.Value(rep, 0x0D, 0, 0x54)}, {rep.Length} bytes";
            var held = touch.Input(0x01);
            Check($"{profileId}: before the kill, the touch report reads a contact down (the control)",
                  held != null && fingers.Any(f => touch.On(held, 0x0D, f, 0x42)), Show(held));
            child.Kill();
            child.WaitForExit();
            Thread.Sleep(1000);
            var after = touch.Input(0x01);
            if (usbip)
            {
                Check($"{profileId}: after the kill, the USB/IP device is gone", after == null && HidChildren(vidPid).Count == 0,
                      after == null ? "gone" : Show(after));
            }
            else
            {
                var set = ReadRelease(index);
                // Every frame of the touch report, decoded at the report's
                // full length (a short one with its tail zero).
                byte[] Full(byte[] f) { var b = new byte[length]; Array.Copy(f, b, Math.Min(f.Length, length)); return b; }
                var lifts = set.Frames.Where(f => f.Length > 0 && f[0] == 0x01).ToList();
                int want = (n + per - 1) / per;
                var counts = lifts.Select(f => touch.Value(Full(f), 0x0D, 0, 0x54)).ToList();
                var ids = new List<int>();
                for (int k = 0; k < lifts.Count; k++)
                    for (int j = 0; j < per && k * per + j < n; j++) ids.Add(touch.Value(Full(lifts[k]), 0x0D, fingers[j], 0x51));
                bool whole = lifts.Count == want && lifts.All(f => f.Length == length)
                             && counts.SequenceEqual(new[] { n }.Concat(Enumerable.Repeat(0, want - 1)))
                             && ids.SequenceEqual(Enumerable.Range(0, n)) && lifts.All(f => fingers.All(fi => !touch.On(Full(f), 0x0D, fi, 0x42)));
                Check($"{profileId}: the driver holds {want} lift frame{(want == 1 ? "" : "s")} for the touch report, each the full {length} bytes, Contact Count {n}{(want > 1 ? " then 0" : "")}, ids 0 to {n - 1} in order, every tip off",
                      whole, $"{set.Layout} layout, {set.Frames.Count} frames, {lifts.Count} of the touch report, sizes {string.Join(" ", lifts.Select(f => f.Length).Distinct())}, " +
                             $"counts {string.Join(" ", counts)}, ids {string.Join(" ", ids)}");
                if (set.Served >= 0)
                    Check($"{profileId}: the driver served every release frame to HID class", set.Served == set.Frames.Count,
                          $"{set.Served} of {set.Frames.Count} served");
                else
                    Check($"{profileId}: the driver keeps its release frames in the wide section", false, "the classic section only, which carries no served count");
                Check($"{profileId}: the touch report reads back lifted, the first lift frame's count kept",
                      after != null && touch.Value(after, 0x0D, 0, 0x54) == n && fingers.All(f => !touch.On(after, 0x0D, f, 0x42)), Show(after));
            }
        }
    }

    /// <summary>The release slots, offline: each report's frames go whole or
    /// not at all (SharedMemoryIO.PickReleaseFrames).</summary>
    static void PickChecks()
    {
        Console.WriteLine("-- release slots: each report's frames whole or not at all --");
        byte[] F(byte id, int size) { var f = new byte[size]; f[0] = id; return f; }
        string Taken(List<byte[]> frames, bool[] take) => string.Join(" ", frames.Select((f, i) => take[i] ? $"{f[0]:X2}/{f.Length}" : "-"));
        bool[] Pick(List<byte[]> frames, bool withIds, int slots, int cap)
        {
            var take = new bool[frames.Count];
            SharedMemoryIO.PickReleaseFrames(frames, withIds, slots, cap, take);
            return take;
        }
        // A keyboard frame, then ten lift frames of one touch report: eight
        // slots hold the keyboard's alone.
        var ten = new List<byte[]> { F(0x05, 9) };
        for (int i = 0; i < 10; i++) ten.Add(F(0x01, 8));
        var t1 = Pick(ten, true, SharedMemoryIO.RELEASE_SLOTS, SharedMemoryIO.RELEASE_FRAME_CAP);
        Check("ten touch lift frames and a keyboard frame in eight slots: the keyboard's alone, no touch frame", t1[0] && t1.Skip(1).All(t => !t), Taken(ten, t1));
        // Five lift frames fit beside it.
        var five = ten.Take(6).ToList();
        var t2 = Pick(five, true, SharedMemoryIO.RELEASE_SLOTS, SharedMemoryIO.RELEASE_FRAME_CAP);
        Check("five touch lift frames and a keyboard frame in eight slots: all six", t2.All(t => t), Taken(five, t2));
        // A 116-byte frame does not fit an 80-byte slot, and the report
        // after it still goes.
        var wide = new List<byte[]> { F(0x01, 116), F(0x04, 20) };
        var t3 = Pick(wide, true, SharedMemoryIO.RELEASE_SLOTS, SharedMemoryIO.RELEASE_FRAME_CAP);
        Check("a 116-byte touch frame beside a 20-byte mouse frame in 80-byte slots: the mouse's alone", !t3[0] && t3[1], Taken(wide, t3));
        // Without report IDs every frame is one report's.
        var plain = new List<byte[]> { F(0x00, 8), F(0x00, 8), F(0x00, 8) };
        var t4 = Pick(plain, false, 2, SharedMemoryIO.RELEASE_FRAME_CAP);
        Check("three frames of a descriptor without report IDs in two slots: none", t4.All(t => !t), Taken(plain, t4));
        var t5 = Pick(plain, false, 3, SharedMemoryIO.RELEASE_FRAME_CAP);
        Check("the same in three slots: all three, in order", t5.All(t => t), Taken(plain, t5));
        // Interleaved reports keep their order and go by report.
        var mixed = new List<byte[]> { F(0x01, 8), F(0x05, 9), F(0x01, 8), F(0x01, 90) };
        var t6 = Pick(mixed, true, SharedMemoryIO.RELEASE_SLOTS, SharedMemoryIO.RELEASE_FRAME_CAP);
        Check("a touch report with one frame too long for its slot goes out whole: the keyboard's alone", !t6[0] && t6[1] && !t6[2] && !t6[3], Taken(mixed, t6));
    }

    /// <summary>The value of the first top-level field of
    /// <paramref name="usage"/> in <paramref name="frame"/>'s report, by this
    /// probe's own walk of <paramref name="descriptor"/>, or -1.</summary>
    static int UsageValue(byte[] frame, byte[] descriptor, uint usage)
    {
        byte rid = Walk(descriptor).Any(it => it.ReportId != 0) ? frame[0] : (byte)0;
        var data = rid != 0 ? frame.AsSpan(1) : frame.AsSpan();
        foreach (var it in Walk(descriptor))
        {
            if (it.ReportId != rid || it.Constant || it.Finger >= 0) continue;
            for (int e = 0; e < it.Count; e++)
                if (it.Usages[Math.Min(e, it.Usages.Length - 1)] == usage)
                    return ReadBits(data, it.Bit + e * it.Size, it.Size, it.LogicalMin < 0);
        }
        return -1;
    }

    const uint FILE_MAP_READ = 4;

    [LibraryImport("kernel32.dll", EntryPoint = "OpenFileMappingW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenFileMapping(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr MapViewOfFile(IntPtr mapping, uint access, uint high, uint low, nuint bytes);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnmapViewOfFile(IntPtr view);

    /// <summary>The release frames a controller's driver copies when its
    /// program goes away, read from the section it copies them from: the
    /// wide release section when the index has one, else the release slots
    /// of the acknowledgement section. Served is the driver's count of
    /// release frames HID class took, -1 on the classic layout.</summary>
    static (string Layout, List<byte[]> Frames, int Served) ReadRelease(int index)
    {
        var frames = new List<byte[]>();
        IntPtr h = OpenFileMapping(FILE_MAP_READ, false, $@"Global\HIDMaestroInputAck{index}");
        if (h == IntPtr.Zero) return ("none", frames, -1);
        try
        {
            IntPtr v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, (nuint)SharedMemoryIO.INPUT_ACK_SIZE);
            if (v == IntPtr.Zero) return ("unmapped", frames, -1);
            try
            {
                // The driver's own rule (driver.c OwnerGone): the wide set
                // when the program's SDK wrote it.
                if ((Marshal.ReadInt32(v, SharedMemoryIO.INPUT_ACK_OFFSET_CAPS) & SharedMemoryIO.INPUT_ACK_SDK_WIDE) != 0
                    && ReadWideRelease(index) is { } wide)
                    return wide;
                int count = Math.Min(Marshal.ReadInt32(v, SharedMemoryIO.INPUT_ACK_OFFSET_RELEASE_COUNT), SharedMemoryIO.RELEASE_SLOTS);
                for (int i = 0; i < count; i++)
                {
                    int at = SharedMemoryIO.INPUT_ACK_OFFSET_RELEASE + i * SharedMemoryIO.RELEASE_SLOT_SIZE;
                    int size = Math.Clamp(Marshal.ReadInt32(v, at), 0, SharedMemoryIO.RELEASE_FRAME_CAP);
                    var f = new byte[size];
                    Marshal.Copy(v + at + 4, f, 0, size);
                    frames.Add(f);
                }
            }
            finally { UnmapViewOfFile(v); }
        }
        finally { CloseHandle(h); }
        return ("classic", frames, -1);
    }

    /// <summary>The wide frames section's release set and the driver's count
    /// of the frames served, or null when the index has no such section.</summary>
    static (string Layout, List<byte[]> Frames, int Served)? ReadWideRelease(int index)
    {
        IntPtr h = OpenFileMapping(FILE_MAP_READ, false, $@"Global\HIDMaestroWideFrames{index}");
        if (h == IntPtr.Zero) return null;
        try
        {
            IntPtr v = MapViewOfFile(h, FILE_MAP_READ, 0, 0, (nuint)SharedMemoryIO.WIDE_SIZE);
            if (v == IntPtr.Zero) return null;
            try
            {
                var frames = new List<byte[]>();
                int count = Math.Min(Marshal.ReadInt32(v, SharedMemoryIO.WIDE_OFFSET_RELEASE_COUNT), SharedMemoryIO.WIDE_RELEASE_SLOTS);
                for (int i = 0; i < count; i++)
                {
                    int at = SharedMemoryIO.WIDE_OFFSET_RELEASE + i * SharedMemoryIO.WIDE_RELEASE_SLOT_SIZE;
                    int size = Math.Clamp(Marshal.ReadInt32(v, at), 0, SharedMemoryIO.WIDE_FRAME_CAP);
                    var f = new byte[size];
                    Marshal.Copy(v + at + 4, f, 0, size);
                    frames.Add(f);
                }
                return ("wide", frames, Marshal.ReadInt32(v, SharedMemoryIO.WIDE_OFFSET_RELEASE_SERVED));
            }
            finally { UnmapViewOfFile(v); }
        }
        finally { CloseHandle(h); }
    }
}
