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
//
// record-switch-pro <out dir> hands switch-pro to Steam the same way and
// records what Steam does with it, for device_oracle_check (see
// RecordSwitchPro below). Exit 0 recorded, 1 a step failed or bad arguments,
// 2 SKIP as above, 3 refused without elevation, before anything is created
// or started.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    /// <summary>Steam, and the helper processes that outlive it.
    ///
    /// Waiting on steam.exe alone is not enough to call the machine
    /// clean. steamwebhelper survives the parent by seconds, and while
    /// any of it is alive Steam Input still holds its claim on a Valve
    /// device, which makes that device invisible to a separate SDL
    /// process. That is what took the Deck away from S55 when it ran
    /// straight after this scenario: three personas created, two
    /// enumerated, and the missing one was the model Steam grabs
    /// hardest.</summary>
    static bool AnySteamRunning()
    {
        foreach (var n in new[] { "steam", "steamwebhelper", "steamservice" })
            if (Process.GetProcessesByName(n).Length > 0) return true;
        return false;
    }

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

    /// <summary>Start Steam to the tray when it is not running, and wait
    /// until it writes its controller log. False, with the reason printed as
    /// a SKIP, when it cannot.</summary>
    static bool StartSteam(string root, string log, out bool startedHere)
    {
        startedHere = false;
        if (!SteamRunning())
        {
            // -silent starts Steam to the tray with no window, so a battery
            // run never takes focus from whoever is using the machine.
            Console.WriteLine("  starting Steam (-silent)...");
            long before = LogLength(log);
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
                return false;
            }
            // Steam has to be far enough up to be writing its controller
            // log before any of this means anything.
            for (int i = 0; i < 120 && LogLength(log) == 0; i++) Thread.Sleep(500);
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                if (SteamRunning() && LogLength(log) > 0) break;
            }
            // Until its controller log opens this session with the client's
            // version line, for up to the 5 s this waited before.
            var up = Stopwatch.StartNew();
            while (up.ElapsedMilliseconds < 5000
                   && !ReadLogFrom(log, before).Contains("Client version:", StringComparison.Ordinal))
                Thread.Sleep(100);
        }
        if (!SteamRunning())
        {
            Console.WriteLine("[SKIP] Steam did not come up.");
            return false;
        }
        if (!File.Exists(log))
        {
            Console.WriteLine("[SKIP] Steam is running but writes no controller log here.");
            return false;
        }
        return true;
    }

    static void ShutdownSteam(string root)
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
            // Wait for the whole tree, steam.exe and its helpers. The next
            // scenario creates Valve personas and reads them through stock
            // SDL, and it cannot see one Steam still owns. A claim is an
            // open handle, which Windows closes before a process it held
            // in ends.
            var down = Stopwatch.StartNew();
            while (down.ElapsedMilliseconds < 60_000 && AnySteamRunning()) Thread.Sleep(100);
            Console.WriteLine(AnySteamRunning()
                ? "  WARNING: Steam processes still present after shutdown"
                : "  Steam is fully down");
        }
        catch { }
    }

    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "record-switch-pro") return RecordSwitchPro(args[1]);
        if (args.Length != 0)
        {
            Console.WriteLine("usage: ValveSteamCheck [record-switch-pro <out dir>]");
            return 1;
        }
        return Checks();
    }

    /// <summary>How long every persona is held after Steam opens it while
    /// the log is watched for Steam dropping it. Before idleFrameIntervalMs
    /// (068e615b), Steam closed an idle persona after a hid_read failure 1
    /// to 3 s after opening it: controller.txt on 2026-08-21 at 10:26:12,
    /// 10:29:31, 10:30:43, 10:32:16 and 10:58:14. Twice the longest.</summary>
    const int HoldMs = 6000;

    sealed class Persona
    {
        public required string Id, Pid, Product, ConfigSet, Model;
        public HMProfile? Profile;
        public HMController? Pad;
        public long Baseline;
        public long OpenedAt = -1;
        public readonly List<(byte Id, int Len)> Written = new();
        public string? Error;
    }

    static int Checks()
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

        if (!StartSteam(root, log, out bool startedHere)) return 2;

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        // The config-set name is Steam's own per-model classification, and
        // it is the assertion that separates "some Valve device" from "this
        // Valve device".
        var cases = new[]
        {
            new Persona { Id = "steam-deck-composite",       Pid = "1205", Product = "Steam Controller", ConfigSet = "configset_controller_neptune.vdf",                Model = "neptune" },
            new Persona { Id = "steam-controller-composite", Pid = "1102", Product = "Wired Controller", ConfigSet = "configset_controller_steamcontroller_gordon.vdf", Model = "steamcontroller_gordon" },
            new Persona { Id = "steam-controller-2",         Pid = "1302", Product = "Steam Controller", ConfigSet = "configset_controller_triton.vdf",                 Model = "triton" },
        };

        var clock = Stopwatch.StartNew();
        try
        {
            // The three are held together. Each is created once Steam has
            // opened the one before, so each one's lines in the log, from its
            // "Local Device Found" to the next, are its own.
            foreach (var t in cases)
            {
                t.Profile = ctx.GetProfile(t.Id);
                if (t.Profile == null) continue;
                t.Baseline = LogLength(log);
                try
                {
                    var c = ctx.CreateController(t.Profile);
                    t.Pad = c;
                    // Every command Steam writes down to this device, in
                    // arrival order. Valve frames a command-channel write as
                    // [message][length][parameters], so byte 0 of the
                    // payload is the message id.
                    var written = t.Written;
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

                    var open = Stopwatch.StartNew();
                    while (open.ElapsedMilliseconds < 90_000)
                    {
                        string own = OwnLines(ReadLogFrom(log, t.Baseline), t.Pid);
                        if (own.Contains("Steam controller device opened", StringComparison.Ordinal)) { t.OpenedAt = clock.ElapsedMilliseconds; break; }
                        Thread.Sleep(100);
                    }
                }
                catch (Exception ex) { t.Error = ex.Message; }
            }

            // Hold them and watch for Steam dropping one. An idle persona
            // used to die here: claimed, read nothing, closed within
            // seconds. Config sets land after Steam finishes registering a
            // controller against the account, which is a round trip and can
            // arrive well behind the claim, so the wait also runs until each
            // one is in, for up to 30 seconds after that persona's open.
            var held = cases.Where(t => t.OpenedAt >= 0).ToList();
            while (held.Count > 0)
            {
                long now = clock.ElapsedMilliseconds;
                bool done = held.All(t => now - t.OpenedAt >= HoldMs
                                          && (now - t.OpenedAt >= 30_000
                                              || ReadLogFrom(log, t.Baseline).Contains(t.ConfigSet, StringComparison.OrdinalIgnoreCase)));
                if (done) break;
                Thread.Sleep(200);
            }

            foreach (var t in cases)
            {
                Console.WriteLine();
                Console.WriteLine($"-- {t.Id} --");
                Check("profile is in the catalog", t.Profile != null);
                if (t.Profile == null) continue;
                if (t.Error != null) { Check("persona ran without throwing", false, t.Error); continue; }

                string after = ReadLogFrom(log, t.Baseline);
                string own = OwnLines(after, t.Pid);
                string want = $"type: 28de {t.Pid}";
                Check("Steam finds the device and reads its identity",
                      own.Contains(want, StringComparison.OrdinalIgnoreCase), want);
                Check("Steam reads the manufacturer string off the device",
                      own.Contains("Manufacturer: Valve Software", StringComparison.Ordinal));
                Check("Steam reads the product string off the device",
                      own.Contains("Product:      " + t.Product, StringComparison.Ordinal), t.Product);
                Check("Steam selects Valve's HID protocol for it",
                      own.Contains("Controller uses V1 HID protocol", StringComparison.Ordinal));

                // The claim itself. A device Steam files under Generic
                // DirectInput never reaches this line.
                Check("Steam CLAIMS it as a Steam controller",
                      own.Contains("Steam controller device opened", StringComparison.Ordinal));

                // Which Valve device Steam decided this is.
                Check("Steam classifies it as this specific Valve model",
                      after.Contains(t.ConfigSet, StringComparison.OrdinalIgnoreCase), t.ConfigSet);

                // Everything Steam logged from this persona's open on. The
                // close lines name no device, so one from any persona held
                // with it counts against it too.
                int found = Math.Max(0, after.IndexOf(want, StringComparison.OrdinalIgnoreCase));
                int openAt = after.IndexOf("Steam controller device opened", found, StringComparison.Ordinal);
                string tail = openAt >= 0 ? after[openAt..] : after;
                Check("Steam keeps reading it (no hid_read failure close)",
                      !tail.Contains("closed after hid_read failure", StringComparison.Ordinal));
                Check($"the device is still open {HoldMs / 1000} seconds later",
                      !tail.Contains("PollState Changed from 2 to 0", StringComparison.Ordinal));

                // What Steam DOES with the device once it has decided
                // what it is. Steam configures a Valve controller over
                // Valve's own command channel, which is the same channel
                // its haptics ride, so a persona that never receives one
                // of these is one Steam claimed and then ignored.
                List<(byte Id, int Len)> cmds;
                lock (t.Written) cmds = new List<(byte, int)>(t.Written);
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

                // What Steam's interface for this device is built from.
                // The classification above is a name. These are the
                // files Steam ships under that name, and they are what
                // its layout editor and desktop layout render. Read off
                // disk, so nothing opens a window on anybody's screen.
                string cbase = Path.Combine(root, "controller_base");
                string[] shipped;
                try { shipped = Directory.GetFiles(cbase, "*" + t.Model + "*.vdf"); }
                catch { shipped = Array.Empty<string>(); }
                Check("Steam ships an interface definition for that model",
                      shipped.Length > 0,
                      shipped.Length > 0
                          ? string.Join(", ", Array.ConvertAll(shipped, Path.GetFileName))
                          : cbase);

                // Trackpad and stick groups in that definition are the
                // surfaces its configuration UI exposes. A pad-less
                // gamepad template carries neither.
                bool surfaces = false;
                foreach (var f in shipped)
                {
                    string body;
                    try { body = File.ReadAllText(f); } catch { continue; }
                    if (body.Contains("absolute_mouse", StringComparison.Ordinal)
                        || body.Contains("joystick_move", StringComparison.Ordinal))
                    {
                        surfaces = true;
                        break;
                    }
                }
                Check("that definition exposes trackpad and stick surfaces",
                      surfaces, "absolute_mouse / joystick_move groups");
            }
        }
        finally
        {
            var pads = cases.Where(t => t.Pad != null).Select(t => t.Pad!).ToList();
            try { ctx.DisposeControllersInParallel(pads); } catch { }
            if (startedHere) ShutdownSteam(root);
        }

        Console.WriteLine();
        Console.WriteLine(s_fail == 0
            ? "=== STEAM CLAIMS AND CLASSIFIES ALL THREE VALVE PERSONAS ==="
            : $"=== {s_fail} check(s) FAILED ===");
        return s_fail == 0 ? 0 : 1;
    }

    /// <summary>A device's own lines in Steam's log: from the "Local Device
    /// Found" whose type is 28de <paramref name="pid"/> to the next "Local
    /// Device Found", or the end. Empty when Steam has not found it.</summary>
    static string OwnLines(string log, string pid)
    {
        int type = log.IndexOf($"type: 28de {pid}", StringComparison.OrdinalIgnoreCase);
        if (type < 0) return string.Empty;
        int start = log.LastIndexOf("Local Device Found", type, StringComparison.Ordinal);
        if (start < 0) start = type;
        int next = log.IndexOf("Local Device Found", type, StringComparison.Ordinal);
        return next < 0 ? log[start..] : log[start..next];
    }

    // record-switch-pro: Steam's claim of the Switch Pro persona.
    //
    // Steam claims a Switch Pro through SDL's HIDAPI Switch driver, which
    // runs the Bluetooth init against the device's responder, and logs it in
    // controller.txt the way it logs a Valve device. Its lines for the
    // persona, from this machine's controller.txt on 2026-08-21: "type: 057e
    // 2009", "Product:      Nintendo Switch Pro Controller", "!! Steam
    // controller device opened for index <n>.", "Controller using HIDAPI
    // driver, vid=0x057e, pid=0x2009", "Nintendo Controller subtype 3" and
    // the config sets configset_controller_switch_pro.vdf and
    // configset_NSP<MAC>.vdf, the MAC the driver serves. Nothing is
    // submitted, as above.
    //
    // Case live-steam-switch-pro, streams:
    //   steam/switch-pro/index<N>/log     found, product, opened, driver,
    //                                     subtype, configsets (sorted, comma
    //                                     separated) and closed: each the
    //                                     line's text after the timestamp or
    //                                     its absence as an empty output, and
    //                                     closed 1 when Steam logged a
    //                                     hid_read failure close after the
    //                                     open
    //   steam/switch-pro/index<N>/writes  every output report Steam wrote,
    //                                     as runs: write (the source, the
    //                                     report ID and the data, with the
    //                                     packet counter of reports 0x01 and
    //                                     0x10 zeroed) and repeat (u32)
    // How many writes arrive and the counter depend on time: a comparison
    // masks repeat. The counter is the low nibble of the first data byte,
    // which SDL increments per packet (SDL_hidapi_switch.c:476 and 482 at
    // SDL-reference 804cf65).

    static int RecordSwitchPro(string outDir)
    {
        Console.WriteLine("=== Steam's claim of switch-pro, recorded for device_oracle_check ===");
        using (var id = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            if (!new System.Security.Principal.WindowsPrincipal(id)
                    .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [REFUSED] record-switch-pro creates a controller and needs elevation. Nothing was created or started");
                return 3;
            }
        }
        string? root = SteamRoot();
        if (root == null)
        {
            Console.WriteLine("[SKIP] no Steam client installed on this machine.");
            return 2;
        }
        string log = Path.Combine(root, "logs", "controller.txt");
        if (!StartSteam(root, log, out bool startedHere)) return 2;

        using var rec = new LiveRecorder(outDir, "live-steam-switch-pro");
        var written = new List<byte[]>();
        try
        {
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            var prof = ctx.GetProfile("switch-pro") ?? throw new InvalidOperationException("switch-pro is not in the catalog");
            long baseline = LogLength(log);
            string fresh = string.Empty;
            int index;
            using (var c = ctx.CreateController(prof))
            {
                index = c.TransportIndex;
                c.OutputReceived += (_, pkt) =>
                {
                    var w = new byte[pkt.Data.Length + 2];
                    w[0] = (byte)pkt.Source;
                    w[1] = pkt.ReportId;
                    pkt.Data.Span.CopyTo(w.AsSpan(2));
                    if ((pkt.ReportId == 0x01 || pkt.ReportId == 0x10) && w.Length > 2) w[2] &= 0xF0;
                    lock (written) written.Add(w);
                };

                bool opened = false;
                for (int i = 0; i < 90 && !opened; i++)
                {
                    Thread.Sleep(1000);
                    fresh = ReadLogFrom(log, baseline);
                    opened = fresh.Contains("type: 057e 2009", StringComparison.OrdinalIgnoreCase)
                          && fresh.Contains("Steam controller device opened", StringComparison.Ordinal);
                }
                if (!opened) throw new InvalidOperationException("Steam did not open switch-pro in 90 s");
                // Hold it while Steam configures it, as the Valve personas
                // are held above, so its writes and config sets land.
                Thread.Sleep(15000);
                fresh = ReadLogFrom(log, baseline);
            }

            string head = $"steam/switch-pro/index{index}";
            rec.Stream(head + "/log");
            string Line(string contains)
            {
                foreach (var l in fresh.Split('\n'))
                {
                    int at = l.IndexOf(contains, StringComparison.Ordinal);
                    if (at >= 0) return l.Substring(at).TrimEnd('\r');
                }
                return string.Empty;
            }
            void Text(string tag, string s) => rec.Out(tag, System.Text.Encoding.ASCII.GetBytes(s));
            Text("found", Line("type: 057e 2009"));
            Text("product", Line("Product:      Nintendo"));
            Text("opened", fresh.Contains("Steam controller device opened", StringComparison.Ordinal) ? "opened" : "");
            Text("driver", Line("Controller using HIDAPI driver, vid=0x057e, pid=0x2009"));
            Text("subtype", Line("Nintendo Controller subtype"));
            var sets = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var l in fresh.Split('\n'))
            {
                int at = l.IndexOf("configset_", StringComparison.Ordinal);
                if (at < 0 || !l.Contains("found config set file on-disk", StringComparison.Ordinal)) continue;
                int end = l.IndexOf(".vdf", at, StringComparison.Ordinal);
                if (end > at) sets.Add(l.Substring(at, end + 4 - at));
            }
            Text("configsets", string.Join(",", sets));
            int openAt = fresh.IndexOf("Steam controller device opened", StringComparison.Ordinal);
            rec.OutByte("closed", (byte)(openAt >= 0 && fresh.IndexOf("closed after hid_read failure", openAt, StringComparison.Ordinal) >= 0 ? 1 : 0));

            rec.Stream(head + "/writes");
            List<byte[]> all;
            lock (written) all = new List<byte[]>(written);
            byte[]? last = null;
            uint count = 0;
            foreach (var w in all)
            {
                if (last != null && w.AsSpan().SequenceEqual(last)) { count++; continue; }
                if (last != null) { rec.Out("write", last); rec.OutU32("repeat", count); }
                last = w;
                count = 1;
            }
            if (last != null) { rec.Out("write", last); rec.OutU32("repeat", count); }
            Console.WriteLine($"  Steam opened switch-pro and wrote {all.Count} output report(s)");

            rec.Save();
            Console.WriteLine($"  wrote {outDir}");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  [FAIL] {e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            Thread.Sleep(3000);
            if (startedHere) ShutdownSteam(root);
        }
    }
}
