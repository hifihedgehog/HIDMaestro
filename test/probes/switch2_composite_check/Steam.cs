// Part E of the Switch 2 Pro composite persona check: the Steam client.
//
// Steam added Switch 2 controllers over USB on Windows in November 2025.
// Its start sequence is in no source, so this part hands the persona to a
// running Steam client and reads back what Steam did: Steam's own logs, and
// every command it sent down the bulk pipe.
//
// Nothing but gravity is submitted. No button, no stick: a Steam desktop
// layout bound to the pad cannot reach the desktop.
//
// `--steam-explore` runs the same session and prints all of it, asserting
// nothing. It is the tool for the day Steam sends a command the persona
// lacks.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

using HIDMaestro;

internal static partial class Program
{
    static string? SteamRoot()
    {
        foreach (var p in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam" })
            if (File.Exists(Path.Combine(p, "steam.exe"))) return p;
        return null;
    }

    static bool SteamRunning() => Process.GetProcessesByName("steam").Length > 0;

    static bool AnySteamRunning()
        => new[] { "steam", "steamwebhelper", "steamservice" }.Any(n => Process.GetProcessesByName(n).Length > 0);

    /// <summary>Read a log from an offset, even while Steam holds it open.</summary>
    static string ReadLogFrom(string path, long offset)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > fs.Length) offset = 0;   // Steam rotated it
            fs.Seek(offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd();
        }
        catch { return string.Empty; }
    }

    static long LogLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    /// <summary>Start Steam to the tray when it is not running. Returns
    /// false when it would not come up.</summary>
    static bool EnsureSteam(string root, string log, out bool startedHere)
    {
        startedHere = false;
        if (SteamRunning()) return true;
        Console.WriteLine("  starting Steam (-silent)...");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(root, "steam.exe"),
                Arguments = "-silent",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            startedHere = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [SKIP] could not start Steam: {ex.Message}");
            return false;
        }
        // Far enough up to be writing its controller log.
        for (int i = 0; i < 120 && LogLength(log) == 0; i++) Thread.Sleep(500);
        for (int i = 0; i < 60 && !(SteamRunning() && LogLength(log) > 0); i++) Thread.Sleep(500);
        Thread.Sleep(8000);
        return SteamRunning();
    }

    /// <summary>Shut down the Steam this run started and wait for the whole
    /// tree, so nothing after it finds a pad Steam still holds.</summary>
    static void StopSteam(string root)
    {
        Console.WriteLine("  shutting Steam back down (this run started it)");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(root, "steam.exe"),
                Arguments = "-shutdown",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            for (int i = 0; i < 120 && AnySteamRunning(); i++) Thread.Sleep(500);
            Thread.Sleep(3000);
            Console.WriteLine(AnySteamRunning() ? "  WARNING: Steam processes still present after shutdown" : "  Steam is fully down");
        }
        catch { }
    }

    static void PartE(HMContext ctx)
    {
        Console.WriteLine("\n== Part E: the Steam client ==");
        if (s_liveSkipped) { Console.WriteLine("  [SKIP] see part B"); return; }
        string? root = SteamRoot();
        if (root == null) { Console.WriteLine("  [SKIP] no Steam client installed on this machine."); return; }
        string controllerLog = Path.Combine(root, "logs", "controller.txt");
        string consoleLog = Path.Combine(root, "logs", "console_log.txt");

        if (!EnsureSteam(root, controllerLog, out bool startedHere))
        {
            Console.WriteLine("  [SKIP] Steam did not come up.");
            return;
        }
        Attached? a = null;
        try
        {
            long controllerBase = LogLength(controllerLog), consoleBase = LogLength(consoleLog);
            var profile = ctx.GetProfile(ProfileId)!;
            a = Attached.Create(ctx, profile, null, out string? skip);
            if (a == null) { Console.WriteLine($"  [SKIP] {skip}"); return; }
            a.Stage(new HMGamepadState { AccelGY = 1.0f });
            var device = a.Pad.UsbipHandle!.Device;
            var s2 = device.Switch2!;

            // Steam opens the pad, starts it, counts 100 reports to choose
            // its sensor constants and then sends one more enable. That last
            // command is the proof it read the stream.
            string controller = "", console = "";
            bool opened = false;
            for (int i = 0; i < 90 && !opened; i++)
            {
                Thread.Sleep(1000);
                controller = ReadLogFrom(controllerLog, controllerBase);
                var log = s2.SnapshotBulkLog();
                opened = controller.Contains("Steam controller device opened", StringComparison.Ordinal)
                      && log.Count >= 19 && log.Skip(17).Any(p => p.Length >= 9 && p[0] == 0x0C && p[3] == 0x04);
            }
            console = ReadLogFrom(consoleLog, consoleBase);

            string added = console.Split('\n').FirstOrDefault(l => l.Contains("Added HIDAPI device", StringComparison.Ordinal)
                                                               && l.Contains("PID 0x2069", StringComparison.OrdinalIgnoreCase)) ?? "";
            Check("Steam's SDL adds the persona as 'Nintendo Switch Pro Controller' on its Switch 2 driver",
                  added.Contains("'Nintendo Switch Pro Controller'", StringComparison.Ordinal)
                  && added.Contains("VID 0x057e", StringComparison.OrdinalIgnoreCase)
                  && added.Contains("SDL_JOYSTICK_HIDAPI_SWITCH2 (ENABLED)", StringComparison.Ordinal),
                  added.Trim());
            Check("with the persona's own serial", added.Contains("serial " + a.Serial, StringComparison.Ordinal), a.Serial);
            Check("Steam finds the device and reads its identity",
                  controller.Contains("type: 057e 2069", StringComparison.OrdinalIgnoreCase)
                  && controller.Contains("Product:      Nintendo Switch Pro Controller", StringComparison.Ordinal));
            Check("Steam opens it as a controller of its own, on the HIDAPI driver",
                  controller.Contains("Steam controller device opened", StringComparison.Ordinal)
                  && controller.Contains("Controller using HIDAPI driver, vid=0x057e, pid=0x2069", StringComparison.OrdinalIgnoreCase));
            Check("Steam gives it the Switch Pro configuration set",
                  controller.Contains("configset_controller_switch_pro.vdf", StringComparison.OrdinalIgnoreCase));

            var timed = s2.SnapshotBulkLogTimed();
            string sequence = string.Join(" ", timed.Select(e => LogLine(e.Payload)));
            Console.WriteLine($"  bulk commands received: {sequence}");
            const string start = "02/01 02/01 02/01 02/01 02/01 02/01 02/01 07/01 0C/02 11/01 0A/08 0C/04 01/0C 01/01 08/02 03/0A 03/0D";
            Check("Steam starts the pad with the sequence SDL uses, whole and in order", sequence.StartsWith(start, StringComparison.Ordinal), sequence);
            Check("every command Steam sent is one the persona's table names or SDL sends",
                  timed.All(e => e.Payload.Length >= 8 && KnownCommand(e.Payload)),
                  string.Join(" ", timed.Where(e => e.Payload.Length < 8 || !KnownCommand(e.Payload)).Select(e => X(e.Payload))));
            Check("Steam set its player LED and, after counting 100 reports, sent one more enable",
                  timed.Skip(17).Any(e => e.Payload[0] == 0x09 && e.Payload[3] == 0x07)
                  && timed.Skip(17).Any(e => e.Payload[0] == 0x0C && e.Payload[3] == 0x04));
            Check("the pad is on report 0x05 with the IMU feature enabled",
                  s2.ReportsOn && s2.SelectedReport == 0x05 && (s2.FeatureEnabled & 0x04) != 0,
                  $"report 0x{s2.SelectedReport:X2}, mask 0x{s2.FeatureMask:X2}, enabled 0x{s2.FeatureEnabled:X2}");

            // Hold it and watch for Steam dropping it.
            uint before = s2.ReportsSent;
            var hold = Stopwatch.StartNew();
            Thread.Sleep(8000);
            uint sent = s2.ReportsSent - before;
            double rate = sent * 1000.0 / hold.ElapsedMilliseconds;
            Check("Steam keeps reading: it takes the persona's 250 reports a second for eight more seconds",
                  rate > 237 && rate < 252, $"{sent} reports, {rate:F1} a second");
            string after = ReadLogFrom(controllerLog, controllerBase);
            int openAt = after.LastIndexOf("Steam controller device opened", StringComparison.Ordinal);
            string tail = openAt >= 0 ? after[openAt..] : after;
            Check("Steam does not close it",
                  !tail.Contains("closed after hid_read failure", StringComparison.Ordinal)
                  && !tail.Contains("PollState Changed from 1 to 0", StringComparison.Ordinal)
                  && !tail.Contains("PollState Changed from 2 to 0", StringComparison.Ordinal));
            Check("Steam never left a bulk read to time out", device.BulkReadsCanceled == 0, $"{device.BulkReadsCanceled} canceled");
            Console.WriteLine("  [NOTE] the gyro moving in Steam's own controller settings is a screen check and was not run here.");
        }
        finally
        {
            if (a != null)
            {
                a.Dispose();
                Check("teardown removes the USB device", a.WaitGone(15000));
            }
            if (startedHere) StopSteam(root);
        }
    }

    /// <summary>Commands the persona's table names, plus the two of SDL's
    /// start it answers with no data.</summary>
    static bool KnownCommand(byte[] p) => ((p[0] << 8) | p[3]) switch
    {
        0x0201 or 0x0204 or 0x0303 or 0x030A or 0x030D or 0x0701 or 0x0907 or 0x0A08
            or 0x0C01 or 0x0C02 or 0x0C03 or 0x0C04 or 0x0C05 or 0x1001 or 0x1101 or 0x1103
            or 0x010C or 0x0101 or 0x0802 => true,
        _ => false,
    };

    /// <summary>--steam-explore: attach the persona under a running Steam
    /// and print everything observable, asserting nothing.</summary>
    static int SteamExplore(HMContext ctx, int seconds)
    {
        string? root = SteamRoot();
        if (root == null) { Console.WriteLine("no Steam install"); return 2; }
        string log = Path.Combine(root, "logs", "controller.txt");
        var others = new[] { "controller_ui.txt", "console_log.txt", "steamui_system.txt" }
            .Select(n => Path.Combine(root, "logs", n)).ToArray();

        if (!EnsureSteam(root, log, out bool startedHere)) return 2;
        long baseline = LogLength(log);
        var otherBase = others.Select(LogLength).ToArray();

        var profile = ctx.GetProfile(ProfileId)!;
        var a = Attached.Create(ctx, profile, null, out string? skip);
        if (a == null) { Console.WriteLine($"skip: {skip}"); return 2; }
        try
        {
            a.Stage(new HMGamepadState { AccelGY = 1.0f });
            var device = a.Pad.UsbipHandle!.Device;
            var s2 = device.Switch2!;
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < seconds; i++)
            {
                Thread.Sleep(1000);
                if (i % 5 == 4)
                    Console.WriteLine($"  t+{i + 1}s: reports {(s2.ReportsOn ? "on" : "off")}, report 0x{s2.SelectedReport:X2}, mask 0x{s2.FeatureMask:X2}, " +
                                      $"enabled 0x{s2.FeatureEnabled:X2}, LEDs 0x{s2.PlayerLeds:X2}, {s2.SnapshotBulkLog().Count} bulk commands, " +
                                      $"{s2.ReportsSent} reports sent, {device.BulkReadsCanceled} bulk reads canceled");
            }
            Console.WriteLine("\n-- bulk commands, ms after attach --");
            foreach (var e in s2.SnapshotBulkLogTimed())
                Console.WriteLine($"  {(e.Ticks - t0) * 1000.0 / Stopwatch.Frequency,9:F1}  {X(e.Payload)}");
            Console.WriteLine("\n-- Steam controller.txt since attach --");
            Console.WriteLine(ReadLogFrom(log, baseline));
            for (int i = 0; i < others.Length; i++)
            {
                string fresh = ReadLogFrom(others[i], otherBase[i]);
                var lines = fresh.Split('\n').Where(l => l.Contains("057e", StringComparison.OrdinalIgnoreCase) || l.Contains("2069")
                    || l.Contains("Switch", StringComparison.OrdinalIgnoreCase) || l.Contains("Nintendo", StringComparison.OrdinalIgnoreCase)).ToArray();
                Console.WriteLine($"\n-- {Path.GetFileName(others[i])}: {lines.Length} matching line(s) --");
                foreach (var l in lines.Take(40)) Console.WriteLine("  " + l.TrimEnd());
            }
        }
        finally
        {
            a.Dispose();
            a.WaitGone(15000);
            if (startedHere) StopSteam(root);
        }
        return 0;
    }
}
