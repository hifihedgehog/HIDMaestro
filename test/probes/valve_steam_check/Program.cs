// Valve personas against the real Steam client (issue #56).
//
// S53 proves SDL's Steam drivers read these devices. Those drivers are what
// Steam Input is built on, but that is an argument rather than a
// measurement, so this scenario measures it: it hands each persona to a
// running Steam client and reads Steam's own controller log back.
//
// What the log settles, in Steam's words rather than ours:
//
//   Local Device Found / type: 28de <pid>     Steam saw the device
//   Manufacturer: Valve Software              it read our identity
//   Controller uses V1 HID protocol           it picked Valve's protocol
//   !! Steam controller device opened         it CLAIMED it, which is the
//                                             Valve path; a device filed
//                                             under Generic DirectInput
//                                             never reaches this line
//   configset_controller_<name>.vdf           it classified the device as
//                                             one SPECIFIC Valve model:
//                                             neptune for the Deck,
//                                             steamcontroller_gordon for
//                                             the 2015 unit, triton for
//                                             the 2026 one
//
// And one absence that matters more than any of them: no "Controller device
// closed after hid_read failure" after the open. That line is what an idle
// persona produced before idleFrameIntervalMs existed. Steam claimed the
// device, read nothing, and dropped it seconds later.
//
// Nothing is submitted. The personas stream neutral frames on their own, so
// no axis moves and no button fires, and a Steam desktop layout bound to
// the device cannot reach the desktop. That is deliberate: driving inputs
// here would type into whatever window has focus.
//
// Exit 0 PASS, 1 FAIL, 2 SKIP (no Steam install, or it would not start).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using HIDMaestro;

static class ValveSteamCheck
{
    static int s_fail;

    static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) s_fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static string? SteamRoot()
    {
        foreach (var p in new[]
                 {
                     @"C:\Program Files (x86)\Steam",
                     @"C:\Program Files\Steam",
                 })
            if (File.Exists(Path.Combine(p, "steam.exe"))) return p;
        return null;
    }

    /// <summary>Read the log even while Steam holds it open.</summary>
    static string ReadLogFrom(string path, long offset)
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

    static long LogLength(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    static bool SteamRunning() => Process.GetProcessesByName("steam").Length > 0;

    // Valve's feature-message ids, from SDL's controller_constants.h. These
    // are what Steam writes back down to a device it has claimed, and they
    // are the only autonomous view of what Steam does with the thing after
    // it decides what it is.
    const byte ID_CLEAR_DIGITAL_MAPPINGS = 0x81;
    const byte ID_GET_ATTRIBUTES_VALUES  = 0x83;
    const byte ID_SET_SETTINGS_VALUES    = 0x87;
    const byte ID_TRIGGER_HAPTIC_PULSE   = 0x8F;
    const byte ID_TRIGGER_HAPTIC_CMD     = 0xEA;
    const byte ID_TRIGGER_RUMBLE_CMD     = 0xEB;

    static string NameMessage(byte id) => id switch
    {
        ID_CLEAR_DIGITAL_MAPPINGS => "CLEAR_DIGITAL_MAPPINGS",
        ID_GET_ATTRIBUTES_VALUES  => "GET_ATTRIBUTES_VALUES",
        ID_SET_SETTINGS_VALUES    => "SET_SETTINGS_VALUES",
        ID_TRIGGER_HAPTIC_PULSE   => "TRIGGER_HAPTIC_PULSE",
        ID_TRIGGER_HAPTIC_CMD     => "TRIGGER_HAPTIC_CMD",
        ID_TRIGGER_RUMBLE_CMD     => "TRIGGER_RUMBLE_CMD",
        _                         => $"0x{id:X2}",
    };

    static int Main()
    {
        string? root = SteamRoot();
        if (root == null)
        {
            Console.WriteLine("[SKIP] no Steam client installed on this machine.");
            return 2;
        }
        string log = Path.Combine(root, "logs", "controller.txt");
        Console.WriteLine("=== Valve personas against the real Steam client ===");
        Console.WriteLine($"  Steam: {root}");

        bool startedHere = false;
        if (!SteamRunning())
        {
            // -silent starts Steam to the tray with no window, so a battery
            // run never takes focus from whoever is using the machine.
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
                Console.WriteLine($"[SKIP] could not start Steam: {ex.Message}");
                return 2;
            }
            // Steam has to be far enough up to be writing its controller
            // log before any of this means anything.
            for (int i = 0; i < 120 && LogLength(log) == 0; i++) Thread.Sleep(500);
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                if (SteamRunning() && LogLength(log) > 0) break;
            }
            Thread.Sleep(5000);
        }
        if (!SteamRunning())
        {
            Console.WriteLine("[SKIP] Steam did not come up.");
            return 2;
        }
        if (!File.Exists(log))
        {
            Console.WriteLine("[SKIP] Steam is running but writes no controller log here.");
            return 2;
        }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        // The config-set name is Steam's own per-model classification, and
        // it is the assertion that separates "some Valve device" from "this
        // Valve device".
        var cases = new (string Id, string Pid, string Product, string ConfigSet)[]
        {
            ("steam-deck-composite",       "1205", "Steam Controller", "configset_controller_neptune.vdf"),
            ("steam-controller-composite", "1102", "Wired Controller", "configset_controller_steamcontroller_gordon.vdf"),
            ("steam-controller-2",         "1302", "Steam Controller", "configset_controller_triton.vdf"),
        };

        try
        {
            foreach (var t in cases)
            {
                Console.WriteLine();
                Console.WriteLine($"-- {t.Id} --");
                var prof = ctx.GetProfile(t.Id);
                Check("profile is in the catalog", prof != null);
                if (prof == null) continue;

                long baseline = LogLength(log);
                HMController? c = null;
                var written = new List<(byte Id, int Len)>();
                try
                {
                    c = ctx.CreateController(prof);
                    // Every command Steam writes down to this device, in
                    // arrival order. Valve frames a command-channel write as
                    // [message][length][parameters], so byte 0 of the
                    // payload is the message id.
                    c.OutputReceived += (_, pkt) =>
                    {
                        // Where the message id sits depends on whether the
                        // device's feature channel carries report ids at
                        // all, the same split FeatureStubTable.MessageByte
                        // encodes. The Deck and the 2015 controller declare
                        // none, so the transfer's first byte IS the message
                        // and the SDK surfaces it as ReportId. Triton rides
                        // report id 0x42, so its message is the first
                        // payload byte. Valve's ids are all 0x80 and up,
                        // which tells the two apart without guessing.
                        var span = pkt.Data.Span;
                        byte msg = span.Length > 0 && span[0] >= 0x80 ? span[0] : pkt.ReportId;
                        lock (written) written.Add((msg, span.Length));
                    };

                    string want = $"type: 28de {t.Pid}";
                    string fresh = string.Empty;
                    bool found = false;
                    for (int i = 0; i < 90 && !found; i++)
                    {
                        Thread.Sleep(1000);
                        fresh = ReadLogFrom(log, baseline);
                        found = fresh.Contains(want, StringComparison.OrdinalIgnoreCase)
                             && fresh.Contains("Steam controller device opened", StringComparison.Ordinal);
                    }

                    Check("Steam finds the device and reads its identity",
                          fresh.Contains(want, StringComparison.OrdinalIgnoreCase), want);
                    Check("Steam reads the manufacturer string off the device",
                          fresh.Contains("Manufacturer: Valve Software", StringComparison.Ordinal));
                    Check("Steam reads the product string off the device",
                          fresh.Contains("Product:      " + t.Product, StringComparison.Ordinal), t.Product);
                    Check("Steam selects Valve's HID protocol for it",
                          fresh.Contains("Controller uses V1 HID protocol", StringComparison.Ordinal));

                    // The claim itself. A device Steam files under Generic
                    // DirectInput never reaches this line.
                    Check("Steam CLAIMS it as a Steam controller",
                          fresh.Contains("Steam controller device opened", StringComparison.Ordinal));

                    // Hold the device and watch for Steam dropping it. An
                    // idle persona used to die here: claimed, read nothing,
                    // closed within seconds.
                    int openAt = fresh.LastIndexOf("Steam controller device opened", StringComparison.Ordinal);
                    string after = fresh;
                    for (int i = 0; i < 30; i++)
                    {
                        Thread.Sleep(1000);
                        after = ReadLogFrom(log, baseline);
                        // Config sets land after Steam finishes registering
                        // the controller against the account, which is a
                        // round trip and arrives well behind the claim.
                        if (i >= 11 && after.Contains(t.ConfigSet, StringComparison.OrdinalIgnoreCase)) break;
                    }

                    // Which Valve device Steam decided this is.
                    Check("Steam classifies it as this specific Valve model",
                          after.Contains(t.ConfigSet, StringComparison.OrdinalIgnoreCase), t.ConfigSet);

                    string tail = openAt >= 0 && openAt < after.Length ? after[openAt..] : after;
                    Check("Steam keeps reading it (no hid_read failure close)",
                          !tail.Contains("closed after hid_read failure", StringComparison.Ordinal));
                    Check("the device is still open twelve seconds later",
                          !tail.Contains("PollState Changed from 2 to 0", StringComparison.Ordinal));

                    // What Steam DOES with the device once it has decided
                    // what it is. Steam configures a Valve controller over
                    // Valve's own command channel, which is the same channel
                    // its haptics ride, so a persona that never receives one
                    // of these is one Steam claimed and then ignored.
                    List<(byte Id, int Len)> cmds;
                    lock (written) cmds = new List<(byte, int)>(written);
                    var kinds = new SortedSet<string>();
                    foreach (var w in cmds) kinds.Add(NameMessage(w.Id));
                    Console.WriteLine($"     Steam wrote {cmds.Count} command(s): "
                                      + (kinds.Count > 0 ? string.Join(", ", kinds) : "none"));

                    Check("Steam drives the device over Valve's command channel",
                          cmds.Count > 0, $"{cmds.Count} writes");
                    Check("Steam configures it rather than only opening it",
                          cmds.Exists(w => w.Id == ID_SET_SETTINGS_VALUES
                                        || w.Id == ID_CLEAR_DIGITAL_MAPPINGS
                                        || w.Id == ID_GET_ATTRIBUTES_VALUES),
                          "settings, mappings or attributes");
                }
                catch (Exception ex) { Check("persona ran without throwing", false, ex.Message); }
                finally { c?.Dispose(); Thread.Sleep(3000); }
            }
        }
        finally
        {
            if (startedHere)
            {
                Console.WriteLine();
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
                    for (int i = 0; i < 40 && SteamRunning(); i++) Thread.Sleep(500);
                }
                catch { }
            }
        }

        Console.WriteLine();
        Console.WriteLine(s_fail == 0
            ? "=== STEAM CLAIMS AND CLASSIFIES ALL THREE VALVE PERSONAS ==="
            : $"=== {s_fail} check(s) FAILED ===");
        return s_fail == 0 ? 0 : 1;
    }
}
