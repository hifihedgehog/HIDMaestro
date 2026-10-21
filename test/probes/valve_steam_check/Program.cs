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
//   configset_<unit serial>.vdf               the unit's own configuration,
//                                             named after the serial it
//                                             read over 0xAE index 1
//
// A second Deck and a second 2026 controller follow the three (issue #72):
// each serves a unit serial of its own and gets its own config set, never
// the first unit's. The Deck also gets TRIGGER_HAPTIC_PULSE writes.
//
// And one absence that matters more than any of them: Steam does not close
// the device. A close is what an idle persona got before idleFrameIntervalMs
// existed. Steam claimed the device, read nothing, logged "Controller
// device closed after hid_read failure" and dropped it seconds later. That
// line names no device, so the close is read off Windows instead: Steam
// found the device once and still holds it open (probe_common/
// SteamDeviceLog.cs).
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
    const byte ID_GET_STRING_ATTRIBUTE   = 0xAE;
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
            long before = SteamDeviceLog.Length(log);
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
            for (int i = 0; i < 120 && SteamDeviceLog.Length(log) == 0; i++) Thread.Sleep(500);
            for (int i = 0; i < 60; i++)
            {
                Thread.Sleep(500);
                if (SteamRunning() && SteamDeviceLog.Length(log) > 0) break;
            }
            // Until its controller log opens this session with the client's
            // version line, for up to the 5 s this waited before.
            var up = Stopwatch.StartNew();
            while (up.ElapsedMilliseconds < 5000
                   && !SteamDeviceLog.Read(log, before).Contains("Client version:", StringComparison.Ordinal))
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
        /// <summary>Steam names this model's per-unit config set after the
        /// unit serial it reads with 0xAE index 1. It refuses the 2015
        /// persona's and names that one after its USB ids.</summary>
        public bool KeysByUnitSerial;
        /// <summary>Steam sends this model TRIGGER_HAPTIC_PULSE while it
        /// configures it (issue #72).</summary>
        public bool Haptics;
        /// <summary>The earlier unit of the same model in this run, for a
        /// second one.</summary>
        public Persona? First;
        /// <summary>The unit serial the device serves at 0xAE index 1, read
        /// off its compiled answers.</summary>
        public string? UnitSerial;
        public HMProfile? Profile;
        public HMController? Pad;
        public long Baseline;
        public long OpenedAt = -1;
        /// <summary>USB\VID_28DE&amp;PID_&lt;pid&gt;\&lt;serial&gt;, and the HID
        /// interfaces under it, which is how Steam's log names the
        /// device.</summary>
        public string UsbId = "";
        public List<string> Paths = new();
        public readonly List<(byte Id, int Len)> Written = new();
        /// <summary>Every write in full: the report id, then the data.</summary>
        public readonly List<byte[]> Payloads = new();
        public string? Error;
    }

    /// <summary>The settings of one SET_SETTINGS_VALUES write, as Valve's
    /// ControllerSetting records (settingNum u8, settingValue u16-LE, SDL's
    /// controller_structs.h). The message id is the report id on a channel
    /// without report ids (Deck, 2015) and the first data byte on Triton's.
    /// The length byte follows it.</summary>
    static List<(byte Num, ushort Value)> Settings(byte[] w)
    {
        var list = new List<(byte, ushort)>();
        int msgAt = w.Length > 1 && w[1] == ID_SET_SETTINGS_VALUES ? 1 : 0;
        if (w[msgAt] != ID_SET_SETTINGS_VALUES || w.Length < msgAt + 2) return list;
        int len = w[msgAt + 1];
        for (int at = msgAt + 2; at + 3 <= w.Length && at + 3 <= msgAt + 2 + len; at += 3)
            list.Add((w[at], (ushort)(w[at + 1] | (w[at + 2] << 8))));
        return list;
    }

    /// <summary>The string a device's compiled answers serve for 0xAE
    /// index 1: the bytes after [0xAE][length][index], from the message's
    /// own offset, up to the first zero. Null when it has no such
    /// answer.</summary>
    static string? ServedUnitSerial(HMController c)
    {
        var table = c.UsbipHandle?.Device.FeatureAnswers;
        if (table == null) return null;
        foreach (var e in table.Entries)
        {
            if (!e.IsMessage || !e.HasParam || e.ParamValue != 1 || e.Value.Length != 1 || e.Value[0] != ID_GET_STRING_ATTRIBUTE
                || e.Pages.Length == 0)
                continue;
            var page = e.Pages[0];
            int at = e.At + 3, end = at;
            while (end < page.Length && page[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(page, at, end - at);
        }
        return null;
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
        Persona Deck() => new Persona { Id = "steam-deck-composite",       Pid = "1205", Product = "Steam Controller", ConfigSet = "configset_controller_neptune.vdf",                Model = "neptune", KeysByUnitSerial = true, Haptics = true };
        Persona Gordon() => new Persona { Id = "steam-controller-composite", Pid = "1102", Product = "Wired Controller", ConfigSet = "configset_controller_steamcontroller_gordon.vdf", Model = "steamcontroller_gordon" };
        Persona Triton() => new Persona { Id = "steam-controller-2",         Pid = "1302", Product = "Steam Controller", ConfigSet = "configset_controller_triton.vdf",                 Model = "triton", KeysByUnitSerial = true };
        // A second Deck and a second 2026 controller after the three
        // (issue #72). Steam names a unit's config set after the unit serial
        // it reads, so each second unit has to land on its own.
        var deck = Deck();
        var triton = Triton();
        var deck2 = Deck();
        deck2.First = deck;
        var triton2 = Triton();
        triton2.First = triton;
        var cases = new[] { deck, Gordon(), triton, deck2, triton2 };

        var clock = Stopwatch.StartNew();
        try
        {
            // The five are held together. Each is created once Steam has
            // opened the one before and named its config set. Each one's
            // lines in the log run from
            // the "Local Device Found" block that names one of its own
            // interface paths to the next block, so another program's
            // device, opened or closed meanwhile, is never read as one of
            // these.
            foreach (var t in cases)
            {
                t.Profile = ctx.GetProfile(t.Id);
                if (t.Profile == null) continue;
                t.Baseline = SteamDeviceLog.Length(log);
                try
                {
                    var c = ctx.CreateController(t.Profile);
                    t.Pad = c;
                    t.UnitSerial = ServedUnitSerial(c);
                    t.UsbId = $@"USB\VID_28DE&PID_{t.Pid}\{c.UsbipHandle?.Device.Descriptors.SerialString}";
                    // Every command Steam writes down to this device, in
                    // arrival order. Valve frames a command-channel write as
                    // [message][length][parameters], so byte 0 of the
                    // payload is the message id.
                    var written = t.Written;
                    var payloads = t.Payloads;
                    c.OutputReceived += (_, pkt) =>
                    {
                        var full = new byte[pkt.Data.Length + 1];
                        full[0] = pkt.ReportId;
                        pkt.Data.Span.CopyTo(full.AsSpan(1));
                        lock (payloads) payloads.Add(full);
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
                        // Windows adds the interfaces one at a time, so the
                        // paths are read again until Steam names one.
                        t.Paths = SteamDeviceLog.HidPathsUnder(t.UsbId);
                        string own = SteamDeviceLog.Own(SteamDeviceLog.Read(log, t.Baseline), t.Paths);
                        if (own.Contains("Steam controller device opened", StringComparison.Ordinal)) { t.OpenedAt = clock.ElapsedMilliseconds; break; }
                        Thread.Sleep(100);
                    }
                    // The next unit is created once Steam has named this
                    // one's config set, so each set lands in its own
                    // device's block: for a model Steam keys by unit serial,
                    // the set named after the serial this unit serves.
                    string naming = t.KeysByUnitSerial && t.UnitSerial != null ? $"configset_{t.UnitSerial}.vdf" : "configset_";
                    var named = Stopwatch.StartNew();
                    while (t.OpenedAt >= 0 && named.ElapsedMilliseconds < 30_000
                           && !SteamDeviceLog.Own(SteamDeviceLog.Read(log, t.Baseline), t.Paths).Contains(naming, StringComparison.Ordinal))
                        Thread.Sleep(200);
                }
                catch (Exception ex) { t.Error = ex.Message; }
            }

            // Hold them and watch for Steam dropping one. An idle persona
            // used to die here: claimed, read nothing, closed within
            // seconds. Config sets land after Steam finishes registering a
            // controller against the account, which is a round trip and can
            // arrive well behind the claim, so the wait also runs until each
            // one is in, for up to 90 seconds after that persona's open, the
            // bound the open itself gets. A slow network or a busy Steam
            // delays the round trip without any defect, and a pass ends the
            // wait as soon as every config set is in.
            var held = cases.Where(t => t.OpenedAt >= 0).ToList();
            while (held.Count > 0)
            {
                long now = clock.ElapsedMilliseconds;
                bool done = held.All(t => now - t.OpenedAt >= HoldMs
                                          && (now - t.OpenedAt >= 90_000
                                              || (SteamDeviceLog.Read(log, t.Baseline).Contains(t.ConfigSet, StringComparison.OrdinalIgnoreCase)
                                                  && (!t.KeysByUnitSerial
                                                      || SteamDeviceLog.Own(SteamDeviceLog.Read(log, t.Baseline), t.Paths)
                                                             .Contains($"configset_{t.UnitSerial}.vdf", StringComparison.OrdinalIgnoreCase)))));
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

                string after = SteamDeviceLog.Read(log, t.Baseline);
                string own = SteamDeviceLog.Own(after, t.Paths);
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

                // Issue #72. Steam keeps a unit's configuration in a set
                // named after the unit serial it reads over 0xAE, so each
                // unit has to serve its own and get its own set.
                if (t.KeysByUnitSerial)
                {
                    string mine = $"configset_{t.UnitSerial}.vdf";
                    Check("Steam names this unit's config set after the unit serial it serves",
                          t.UnitSerial != null && own.Contains(mine, StringComparison.OrdinalIgnoreCase), mine);
                }
                if (t.First != null)
                {
                    string theirs = $"configset_{t.First.UnitSerial}.vdf";
                    Check("the second unit serves a unit serial of its own",
                          t.UnitSerial != null && t.UnitSerial != t.First.UnitSerial, $"{t.UnitSerial} and the first unit's {t.First.UnitSerial}");
                    Check("Steam never gives the second unit the first unit's config set",
                          !own.Contains(theirs, StringComparison.OrdinalIgnoreCase), theirs);
                }

                // Steam's close line names no device, and another program's
                // device closing beside this one writes it too (05:47:51 on
                // 2026-10-19). So a close of this device is read off Windows:
                // after a close Steam finds a device that is still there
                // again, and a device it closed for good is one nobody holds.
                int finds = SteamDeviceLog.Blocks(after, t.Paths).Count;
                Check("Steam keeps reading it (no hid_read failure close)",
                      finds == 1, $"found {finds} time(s)");
                string? opened = SteamDeviceLog.PathOf(own);
                int error = 0;
                bool kept = opened != null && SteamDeviceLog.HeldOpen(opened, out error);
                Check($"the device is still open {HoldMs / 1000} seconds later", kept,
                      kept ? "Steam holds its interface" : $"an open that shares nothing got error {error}");

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
                Console.WriteLine($"     TRIGGER_HAPTIC_PULSE writes: {cmds.Count(w => w.Id == ID_TRIGGER_HAPTIC_PULSE)}");
                List<byte[]> all;
                lock (t.Payloads) all = new List<byte[]>(t.Payloads);
                var seenSettings = new HashSet<string>();
                foreach (var w in all)
                {
                    var s = Settings(w);
                    if (s.Count == 0) continue;
                    string line = string.Join(" ", s.Select(x => $"{x.Num:X2}={x.Value:X4}"));
                    if (seenSettings.Add(line)) Console.WriteLine($"     SET_SETTINGS_VALUES {line}");
                }
                foreach (var l in own.Split('\n'))
                    if (l.Contains("configset_", StringComparison.Ordinal) || l.Contains("unit serial", StringComparison.Ordinal)
                        || l.Contains("serial_number", StringComparison.Ordinal))
                        Console.WriteLine($"     log: {l.Trim()}");

                Check("Steam drives the device over Valve's command channel",
                      cmds.Count > 0, $"{cmds.Count} writes");
                Check("Steam configures it rather than only opening it",
                      cmds.Exists(w => w.Id == ID_SET_SETTINGS_VALUES
                                    || w.Id == ID_CLEAR_DIGITAL_MAPPINGS
                                    || w.Id == ID_GET_ATTRIBUTES_VALUES),
                      "settings, mappings or attributes");
                // Issue #72: Steam sends a Deck haptic pulses while it
                // configures it, and the 2015 and 2026 personas none.
                if (t.Haptics)
                    Check("Steam sends it TRIGGER_HAPTIC_PULSE",
                          cmds.Exists(w => w.Id == ID_TRIGGER_HAPTIC_PULSE),
                          $"{cmds.Count(w => w.Id == ID_TRIGGER_HAPTIC_PULSE)} writes");

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
            ? "=== STEAM CLAIMS AND CLASSIFIES EVERY VALVE PERSONA, EACH UNIT ON ITS OWN CONFIG SET ==="
            : $"=== {s_fail} check(s) FAILED ===");
        return s_fail == 0 ? 0 : 1;
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
    //                                     closed 1 when Steam no longer held
    //                                     the persona's HID interface open at
    //                                     the end of the hold
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
            long baseline = SteamDeviceLog.Length(log);
            string fresh = string.Empty;
            int index;
            bool held = false;
            using (var c = ctx.CreateController(prof))
            {
                index = c.TransportIndex;
                // The persona's own interface is the HID one whose serial is
                // HM-CTL- and its transport index.
                string serial = $"HM-CTL-{index:D4}";
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
                    fresh = SteamDeviceLog.Read(log, baseline);
                    opened = fresh.Contains("type: 057e 2009", StringComparison.OrdinalIgnoreCase)
                          && fresh.Contains("Steam controller device opened", StringComparison.Ordinal);
                }
                if (!opened) throw new InvalidOperationException("Steam did not open switch-pro in 90 s");
                // Hold it while Steam configures it, as the Valve personas
                // are held above, so its writes and config sets land.
                Thread.Sleep(15000);
                fresh = SteamDeviceLog.Read(log, baseline);
                // A recording is this persona's alone. Steam reaches a Switch
                // Pro through SDL and logs it as path: sdl://<n>, which names
                // no interface, so the window may hold Steam's block for this
                // device and nothing else: another device found meanwhile
                // would put its lines in the golden file.
                var blocks = SteamDeviceLog.All(fresh);
                if (blocks.Count != 1 || !blocks[0].Contains("type: 057e 2009", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Steam found {blocks.Count} device(s) while switch-pro was held, so the log is not this persona's alone");
                var paths = SteamDeviceLog.HidPathsWithSerial(serial);
                if (paths.Count != 1)
                    throw new InvalidOperationException($"{paths.Count} HID interfaces carry the serial {serial}");
                held = SteamDeviceLog.HeldOpen(paths[0], out _);
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
            rec.OutByte("closed", (byte)(held ? 0 : 1));

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
