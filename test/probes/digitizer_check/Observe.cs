// observe <umdf2|usbip> <observer log>: what Windows' pointer stack does with
// the devices, read by an observer in the signed-in user's console session
// (observer.ps1), which records the cursor and the pointer device list. A
// pen goes in range, moves and leaves range, never touching: the cursor
// should follow it. Then one touchscreen tap, on the Elan screen of the
// Dell XPS 13 9360 and its 116-byte report, at a point the observer found
// bare desktop within the last two seconds, or no tap. Each step prints its
// UTC millisecond stamp, so the observer's lines can be read against it.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using HIDMaestro;

internal static partial class Program
{
    static int Observe(HMBackend backend, string obsLog)
    {
        static long Stamp() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        static void Mark(string s) { Console.WriteLine($"{Stamp()} {s}"); Console.Out.Flush(); }
        using var ctx = new HMContext(backend);
        ctx.InstallDriver();
        ctx.LoadProfilesFromDirectory(KillProfiles());

        var penProfile = ctx.GetProfile("spike-pen")!;
        var p = penProfile.Pen!;
        int Px(int pct) => p.X.Minimum + (int)((long)(p.X.Maximum - p.X.Minimum) * pct / 100);
        int Py(int pct) => p.Y.Minimum + (int)((long)(p.Y.Maximum - p.Y.Minimum) * pct / 100);
        using (var pen = ctx.CreateController(penProfile, null, backend))
        {
            Mark($"PEN CREATED on {pen.Backend}");
            Thread.Sleep(4000);
            Mark("PEN IN RANGE 50 20");
            pen.SubmitPen(new HMPenState { InRange = true, X = Px(50), Y = Py(20) });
            Thread.Sleep(1000);
            Mark("PEN MOVE 55 25");
            pen.SubmitPen(new HMPenState { InRange = true, X = Px(55), Y = Py(25) });
            Thread.Sleep(1000);
            Mark("PEN OUT");
            pen.SubmitPen(new HMPenState { InRange = false, X = Px(55), Y = Py(25) });
            Thread.Sleep(1000);
        }
        Mark("PEN DISPOSED");

        var screenProfile = ctx.GetProfile("kill-elan-xps9360")!;
        var t = screenProfile.Touch!;
        using (var screen = ctx.CreateController(screenProfile, null, backend))
        {
            Mark($"SCREEN CREATED on {screen.Backend}");
            Thread.Sleep(4000);
            // The newest check of each point, if no older than two seconds.
            long now = Stamp();
            string[]? bare = null;
            if (File.Exists(obsLog))
            {
                // The observer holds the log open for writing.
                string text;
                using (var fs = new FileStream(obsLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(fs)) text = reader.ReadToEnd();
                var lines = text.Split('\n').Select(l => l.TrimEnd('\r').Split(' ')).Where(f => f.Length > 3 && (f[1] == "BARE" || f[1] == "NOTBARE")).ToList();
                var newest = lines.GroupBy(f => f[2] + "," + f[3]).Select(g => g.Last());
                bare = newest.Where(f => f[1] == "BARE" && now - long.Parse(f[0]) <= 2000).OrderBy(f => long.Parse(f[0])).FirstOrDefault();
            }
            if (bare == null) Mark("NO BARE POINT in the last two seconds: no tap");
            else
            {
                int px = int.Parse(bare[2]), py = int.Parse(bare[3]);
                int x = t.X.Minimum + (int)((long)(t.X.Maximum - t.X.Minimum) * px / 100);
                int y = t.Y.Minimum + (int)((long)(t.Y.Maximum - t.Y.Minimum) * py / 100);
                Mark($"TAP DOWN {px} {py}, bare at {bare[0]}");
                screen.SubmitTouch(new[] { new HMTouchContact { Id = 0, X = x, Y = y } });
                Thread.Sleep(60);
                Mark("TAP UP");
                screen.SubmitTouch(ReadOnlySpan<HMTouchContact>.Empty);
                Thread.Sleep(1500);
            }
        }
        Mark("SCREEN DISPOSED");
        return 0;
    }
}
