// kill <umdf2|usbip>: what is left when a program goes away with contacts
// down. A one-contact-per-report touchscreen, in process: three contacts
// take three reports in Windows' hybrid form, so the release the driver
// keeps must be three lift frames, the first with Contact Count 3. Then a
// child process of this probe holds two contacts on the spike touchscreen
// and is killed. On UMDF2 the driver sends the release frames when the
// child's owner pipe closes: the touch report reads back lifted, ids
// kept. On USB/IP the device goes with the child's process.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HIDMaestro;

internal static partial class Program
{
    // One finger a report, ids 0 to 255, up to five contacts (Contact Count
    // and its maximum 0 to 5), on report 1 so UMDF2's input report read
    // answers it.
    const string HybridDescriptor =
        "050d0904a1018501" + "0922a102" + "09421500250175019501810209328102094781029505810375080951950126ff008102" +
        "050126ff7f7510550065003500460000" + "0930810209318102" + "c0" +
        "050d09549501750815002505810285020955b102" + "c0" +
        "090ea1018503" + "0923a102" + "09520953150025" + "0a75089502b102" + "c0" + "c0";

    static string KillProfiles()
    {
        string dir = SpikeProfiles();
        string stubs = "{ \"reports\": [ { \"id\": \"0x02\", \"size\": 2, \"data\": \"0205\" }, { \"id\": \"0x03\", \"size\": 3, \"data\": \"030000\" } ] }";
        System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "spike-touch-hybrid.json"),
            Profile("spike-touch-hybrid", "HIDMaestro spike hybrid touchscreen", "0x000A", HybridDescriptor, stubs, "touchscreen"));
        return dir;
    }

    /// <summary>The child: two contacts down on the spike touchscreen until
    /// it is killed.</summary>
    static int HoldTouch(HMBackend backend)
    {
        using var ctx = new HMContext(backend);
        ctx.LoadProfilesFromDirectory(KillProfiles());
        var profile = ctx.GetProfile("spike-touchscreen")!;
        var c = ctx.CreateController(profile, "digitizer-held-touch", backend);
        Thread.Sleep(1500);
        var t = profile.Touch!;
        int y = t.Y.Minimum + (t.Y.Maximum - t.Y.Minimum) / 10;
        c.SubmitTouch(new[] { new HMTouchContact { Id = 0, X = At(t.X, 2), Y = y }, new HMTouchContact { Id = 1, X = At(t.X, 2) + 200, Y = y } });
        Thread.Sleep(300);
        Console.WriteLine($"HELD backend {c.Backend} release-on-exit {(c.ReleasesOnConsumerExit ? "on" : "off")}");
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
        }

        // A child killed with two contacts down.
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"hold-touch {backend}")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        string? line;
        while ((line = child.StandardOutput.ReadLine()) != null && !line.StartsWith("HELD")) { }
        if (line == null) { Check("the child holds its contacts", false, "it exited first"); return 1; }
        bool usbip = line.Contains(" backend Usbip ");
        Check(usbip ? "on USB/IP the device goes with the child, so nothing announces a release on exit" : "the driver watches the child's exit",
              line.EndsWith(usbip ? "release-on-exit off" : "release-on-exit on"), line);
        using (var touch = OpenChild("VID_1209&PID_0007", "0004"))
        {
            if (touch == null) { Check("the child's touch collection opens", false); child.Kill(); return 1; }
            ushort[] fingers = Enumerable.Range(0, touch.Links.Length).Where(i => touch.Links[i].LinkUsagePage == 0x0D && touch.Links[i].LinkUsage == 0x22)
                                         .Select(i => (ushort)i).ToArray();
            string Show(byte[]? rep) => rep == null ? "no read" : string.Join(", ", fingers.Select(f =>
                $"id {touch.Value(rep, 0x0D, f, 0x51)} tip {(touch.On(rep, 0x0D, f, 0x42) ? 1 : 0)}")) + $", count {touch.Value(rep, 0x0D, 0, 0x54)}";
            var held = touch.Input(0x01);
            Check("before the kill, both contacts read down (the control)", held != null && fingers.All(f => touch.On(held, 0x0D, f, 0x42)), Show(held));
            child.Kill();
            child.WaitForExit();
            Thread.Sleep(1000);
            var after = touch.Input(0x01);
            if (usbip)
                Check("after the kill, the USB/IP device is gone", after == null && HidChildren("VID_1209&PID_0007").Count == 0,
                      after == null ? "gone" : Show(after));
            else
                Check("after the kill, the driver's release frame lifts both, ids 0 and 1 and Contact Count 2 kept",
                      after != null && touch.Value(after, 0x0D, 0, 0x54) == 2 && fingers.All(f => !touch.On(after, 0x0D, f, 0x42))
                      && fingers.Select(f => touch.Value(after, 0x0D, f, 0x51)).SequenceEqual(new[] { 0, 1 }), Show(after));
        }
        // The killed child's device goes with a sweep of orphans, which
        // leaves every running program's controllers alone.
        HMContext.RemoveAllVirtualControllers(HMCleanupScope.Orphaned, preserveInstall: true);
        Thread.Sleep(1000);
        Check("a sweep of orphans removes the killed child's touchscreen", HidChildren("VID_1209&PID_0007").Count == 0,
              $"{HidChildren("VID_1209&PID_0007").Count} HID children left");
        Console.WriteLine();
        Console.WriteLine($"=== {s_checks - s_fails}/{s_checks} checks passed ===");
        return s_fails == 0 ? 0 : 1;
    }
}
