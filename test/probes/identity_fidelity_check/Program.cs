// identity_fidelity_check: what the programs that name a controller read for
// each profile, on each backend, against the pinned readings.
//
// A profile is a device's identity, and a create on any backend has to give
// the programs that name controllers what that device gives them: the VID,
// PID, version, product string and bus the HID class and hidapi read, the
// VID, PID and wireless flag XInput reports, the name, IDs and wireless flag
// Windows.Gaming.Input reports, the name, IDs, bus and type SDL reports, and
// the id Chrome builds from those (Chromium's own rules and tables,
// chromium-gamepad-ids.txt). identity-pins.txt holds the reading of every
// deployable profile. A line there holds for both backends unless it names
// one, and on every Windows unless it names the build it holds from. A line
// that names either carries its reason in the comment above it, as does a
// line that carries another device's IDs. real-devices.txt holds what a
// real device read, where a record of one exists. Windows' own drivers bind
// a USB/IP device as they bind the real one, so the USB/IP pins have to
// read the same there.
//
// Two readers take a name from the machine's own records, and the pins hold
// what a Windows without them reads. Windows.Gaming.Input names a pad from
// GameInput's device table (HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\
// GameInput\Devices, which the GameInput redistributable fills) when the
// table has an entry for its IDs and its collection's usage, and lists no
// pad whose entry says HapticFeedback DS5. SDL's DirectInput driver names a pad SDL's hidapi list
// does not hold by DirectInput's product name (SDL_dinputjoystick.c,
// QueryDeviceName), which Windows stores per VID and PID as OEMName. The
// live part reads both records and expects what they make of each pin.
//
// A driver package that is neither part of Windows nor HIDMaestro's own
// binds by a device's IDs, so on a machine that has one it drives the
// device a profile presents as it drives the real one. The readers then
// give that driver's device. The live part names the package and compares
// nothing for that profile.
//
// Windows.Gaming.Input tracks controllers for a process from some time after
// the process starts to listen, and a device that arrives before then is
// never listed in that process. Read 2026-11-20 on Windows 10 19044, in a
// process started as another one ended: nothing listed for the first device
// for 8 s while the HID class and SDL read it, and the next device listed.
// So while it has listed no device of this process, a profile it should list
// and does not is created again, up to twice, with a note.
//
//   offline                       the pins cover every deployable profile,
//                                 every line that names a backend carries a
//                                 reason, no reader is pinned to IDs other
//                                 than the profile's own without one, the
//                                 USB/IP pins read what the real devices
//                                 on record read, and every profile whose
//                                 IDs and connection an inbox INF names
//                                 (xinputhid.inf, dc1-controller.inf,
//                                 xusb22.inf) carries that INF's name as its
//                                 deviceDescription (InboxNames.cs). No
//                                 device.
//   live <backend> <SDL3 dir> [ids]
//                                 each profile (or the ids given) created on
//                                 <backend> (umdf2, usbip, or auto for what
//                                 Auto resolves to here), read, and compared
//                                 with its pins. Elevated.
//   record <backend> <SDL3 dir> <out file> [ids]
//                                 the same creates, written as a pins file.
//                                 Elevated.
//
// Exit 0 PASS, 1 FAIL, 2 bad arguments or skipped, 3 not elevated.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

using HIDMaestro;
using HIDMaestro.Internal.Usbip;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static string Beside(string file) => Path.Combine(AppContext.BaseDirectory, file);

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "offline";
        try
        {
            switch (mode)
            {
                case "offline":
                    Offline();
                    break;
                case "live" when args.Length >= 3 && ParseBackend(args[1]) is { } lb:
                {
                    if (!Elevated()) { Console.WriteLine("  refused: live needs elevation"); return 3; }
                    if (SdkConsumers.Find() is { } other) { Console.WriteLine($"  SKIP: {other} holds HIDMaestro state on this machine."); return 2; }
                    Live(lb, args[2], args.Skip(3).ToArray(), record: null);
                    break;
                }
                case "record" when args.Length >= 4 && ParseBackend(args[1]) is { } rb:
                {
                    if (!Elevated()) { Console.WriteLine("  refused: record needs elevation"); return 3; }
                    if (SdkConsumers.Find() is { } other) { Console.WriteLine($"  SKIP: {other} holds HIDMaestro state on this machine."); return 2; }
                    Live(rb, args[2], args.Skip(4).ToArray(), record: args[3]);
                    break;
                }
                default:
                    Console.WriteLine("usage: IdentityFidelityCheck offline | live <umdf2|usbip|auto> <SDL3 dir> [profile ids] | record <umdf2|usbip|auto> <SDL3 dir> <out file> [profile ids]");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Check("the run ends without an exception", false, e.ToString());
        }
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static HMBackend? ParseBackend(string s) => s.ToLowerInvariant() switch
    {
        "umdf2" => HMBackend.Umdf2,
        "usbip" => HMBackend.Usbip,
        // What Auto resolves to here, HIDMAESTRO_BACKEND included: the
        // backend the regression battery runs.
        "auto" => HMContext.MachineBackend,
        _ => null,
    };

    static bool Elevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>The USB/IP device's own devnode: the one its serial names,
    /// or, for a device Windows names by its port, the one on the port.</summary>
    static string? UsbDevice(HMController c)
    {
        var device = c.UsbipHandle?.Device;
        if (device == null) return null;
        if (device.Descriptors.UsbInstanceId is { } named) return named;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 3000 * Scale)
        {
            int port = device.VhciPort;
            if (port > 0 && VhciClient.DeviceOnRootHubPort(port) is { } onPort) return onPort;
            System.Threading.Thread.Sleep(25);
        }
        return null;
    }

    static double Scale => double.TryParse(Environment.GetEnvironmentVariable("HIDMAESTRO_TIMEOUT_SCALE"),
        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s >= 1 ? s : 1;

    // ── pins ─────────────────────────────────────────────────────────────

    /// <summary>One pinned line: the reader, the backend it holds for (null
    /// for both), the Windows build it holds from (0 for every build), the
    /// value, and the reason a line that names a backend or a build
    /// carries.</summary>
    sealed record Pin(string Reader, string? Backend, int FromBuild, string Value, string? Reason);

    /// <summary>The pins file: a profile id on a line of its own, then its
    /// lines indented, and "#" comments. A line is "reader value", with
    /// "backend:" before the reader when it holds for one backend and
    /// ">=build" after it when it holds from a Windows build on. The
    /// comment lines right above a line are its reason, and the reason of
    /// the lines of the same reader, backend and build that follow it with
    /// no comment of their own.</summary>
    static Dictionary<string, List<Pin>> LoadPins(string path, List<string> problems)
    {
        var pins = new Dictionary<string, List<Pin>>(StringComparer.OrdinalIgnoreCase);
        List<Pin>? current = null;
        var reason = new StringBuilder();
        // The reason the last line carried, for the lines like it that follow.
        string? carried = null, carriedFor = null;
        int n = 0;
        foreach (string raw in File.ReadAllLines(path))
        {
            n++;
            string line = raw.TrimEnd();
            if (line.Length == 0) { reason.Clear(); carried = carriedFor = null; continue; }
            if (line.TrimStart().StartsWith('#'))
            {
                if (current != null && line.StartsWith(' ')) reason.Append(reason.Length > 0 ? " " : "").Append(line.TrimStart().TrimStart('#').Trim());
                continue;
            }
            if (!line.StartsWith(' '))
            {
                if (pins.ContainsKey(line)) problems.Add($"line {n}: {line} appears twice");
                pins[line] = current = new List<Pin>();
                reason.Clear();
                carried = carriedFor = null;
                continue;
            }
            if (current == null) { problems.Add($"line {n}: a reading before any profile id"); continue; }
            string body = line.TrimStart();
            int space = body.IndexOf(' ');
            if (space < 0) { problems.Add($"line {n}: no value"); continue; }
            string head = body[..space], value = body[(space + 1)..];
            string? backend = null;
            int colon = head.IndexOf(':');
            if (colon >= 0) { backend = head[..colon]; head = head[(colon + 1)..]; }
            int fromBuild = 0;
            int since = head.IndexOf(">=", StringComparison.Ordinal);
            if (since >= 0)
            {
                if (!int.TryParse(head[(since + 2)..], out fromBuild) || fromBuild <= 0) { problems.Add($"line {n}: '{head[(since + 2)..]}' is no Windows build"); continue; }
                head = head[..since];
            }
            if (!Reading.Readers.Contains(head)) { problems.Add($"line {n}: '{head}' is no reader"); continue; }
            if (backend != null && backend is not ("umdf2" or "usbip")) { problems.Add($"line {n}: '{backend}' is no backend"); continue; }
            string kind = backend + ":" + head + ">=" + fromBuild;
            string? why = reason.Length > 0 ? reason.ToString() : kind == carriedFor ? carried : null;
            current.Add(new Pin(head, backend, fromBuild, value, why));
            carried = why;
            carriedFor = kind;
            reason.Clear();
        }
        return pins;
    }

    /// <summary>Windows 11's first build.</summary>
    const int Windows11 = 22000;

    /// <summary>The lines a profile's pins give one backend, reader by
    /// reader, sorted as a reading is. Without a build every line counts.
    /// With one they are the lines of that Windows: a line pinned from a
    /// build holds from it on and is absent on Windows 10. No machine has
    /// been read on the Windows 11 builds before it, so there the line goes
    /// to <paramref name="either"/>, and a reading may hold it or not.</summary>
    static Dictionary<string, List<string>> Expected(List<Pin> pins, HMBackend backend, int? build = null,
                                                     HashSet<(string Reader, string Value)>? either = null)
    {
        string name = backend == HMBackend.Umdf2 ? "umdf2" : "usbip";
        var mine = pins.Where(p => p.Backend == null || p.Backend == name).ToList();
        if (build is { } b)
        {
            foreach (var p in mine.Where(p => p.FromBuild > b && b >= Windows11)) either?.Add((p.Reader, p.Value));
            mine = mine.Where(p => p.FromBuild <= b).ToList();
        }
        return Reading.Readers.ToDictionary(r => r, r => mine.Where(p => p.Reader == r)
            .Select(p => p.Value).OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    static bool Matches(Reading got, Dictionary<string, List<string>> want, HashSet<(string Reader, string Value)> either) =>
        OnThisMachine(want).Any(w => Reading.Readers.All(r => Without(got.Of(r), r, either).SequenceEqual(w[r])));

    /// <summary>A reader's lines without the ones this Windows build may
    /// give or not.</summary>
    static IEnumerable<string> Without(IEnumerable<string> lines, string reader, HashSet<(string Reader, string Value)> either) =>
        either.Count == 0 ? lines : lines.Where(v => !either.Contains((reader, v)));

    static readonly System.Text.RegularExpressions.Regex s_oemInf =
        new(@"^oem\d+\.inf$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A driver package that is neither part of Windows nor
    /// HIDMaestro's own and drives the device or a node under it, as
    /// "oem98.inf (PadForge)". Windows names a package it did not ship
    /// oemN.inf. Null when Windows' own drivers and HIDMaestro's drive
    /// every node.</summary>
    static string? OtherDriver(string parent)
    {
        foreach (string node in new[] { parent }.Concat(Hid.Descendants(parent)))
        {
            var (_, inf, provider) = Hid.Binding(node);
            if (s_oemInf.IsMatch(inf) && !provider.Equals("HIDMaestro", StringComparison.OrdinalIgnoreCase))
                return $"{inf} ({(provider.Length > 0 ? provider : "no provider name")})";
        }
        return null;
    }

    static int s_tableNames, s_tableDrops, s_storedNames;

    static readonly System.Text.RegularExpressions.Regex s_named =
        new(@"^""(?<name>[^""]*)"" (?<vid>[0-9A-F]{4}):(?<pid>[0-9A-F]{4}) (?<rest>.*)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The pinned reading as this machine's own records change it:
    /// one reading, or two when SDL's DirectInput name can come from the
    /// name Windows stored for the IDs. The first is the one a difference
    /// is reported against.</summary>
    static List<Dictionary<string, List<string>>> OnThisMachine(Dictionary<string, List<string>> want, bool count = false)
    {
        var first = want.ToDictionary(k => k.Key, k => new List<string>(k.Value));
        // Windows.Gaming.Input: the table's name, and no listing under DS5.
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var wgi = new List<string>();
        foreach (string line in want["wgi"])
        {
            var m = s_named.Match(line);
            if (!m.Success) { wgi.Add(line); continue; }
            var (description, haptic) = GameInputEntry(m.Groups["vid"].Value, m.Groups["pid"].Value, UsagesOf(want["hid"], m.Groups["vid"].Value, m.Groups["pid"].Value));
            if (haptic == "DS5") { if (count) s_tableDrops++; continue; }
            if (description == null || description == m.Groups["name"].Value) { wgi.Add(line); continue; }
            if (count) s_tableNames++;
            renamed[m.Groups["name"].Value] = description;
            wgi.Add($"\"{description}\" {m.Groups["vid"].Value}:{m.Groups["pid"].Value} {m.Groups["rest"].Value}");
        }
        first["wgi"] = wgi.OrderBy(x => x, StringComparer.Ordinal).ToList();
        // Chrome's id of a Windows.Gaming.Input pad carries that name.
        first["chrome"] = want["chrome"].Select(c =>
        {
            foreach (var (was, now) in renamed)
                if (c.StartsWith($"wgi: {was} (", StringComparison.Ordinal)) return $"wgi: {now} ({c[($"wgi: {was} (").Length..]}";
            return c;
        }).OrderBy(x => x, StringComparer.Ordinal).ToList();

        // SDL's DirectInput name, where Windows stored one for the IDs.
        var second = first.ToDictionary(k => k.Key, k => new List<string>(k.Value));
        bool stored = false;
        second["sdl"] = first["sdl"].Select(line =>
        {
            var m = s_named.Match(line);
            if (!m.Success || !m.Groups["rest"].Value.Contains(" driver 0x00 ", StringComparison.Ordinal)) return line;
            string? oem = StoredName(m.Groups["vid"].Value, m.Groups["pid"].Value);
            if (oem == null || oem == m.Groups["name"].Value) return line;
            stored = true;
            return $"\"{oem}\" {m.Groups["vid"].Value}:{m.Groups["pid"].Value} {m.Groups["rest"].Value}";
        }).OrderBy(x => x, StringComparer.Ordinal).ToList();
        return stored ? new() { first, second } : new() { first };
    }

    static readonly System.Text.RegularExpressions.Regex s_hidLine =
        new(@"^(?<vid>[0-9A-F]{4}):(?<pid>[0-9A-F]{4}) .*?\busage (?<page>[0-9A-F]{4}):(?<usage>[0-9A-F]{4})\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The usages, page then usage as eight hex digits, of the
    /// pinned HID collections that carry a VID and PID, or of every pinned
    /// collection when none carries them: Windows' own drivers give some
    /// pads a HID device with other IDs.</summary>
    static List<string> UsagesOf(List<string> hidLines, string vid, string pid)
    {
        var all = hidLines.Select(l => s_hidLine.Match(l)).Where(m => m.Success).ToList();
        var own = all.Where(m => m.Groups["vid"].Value == vid && m.Groups["pid"].Value == pid).ToList();
        return (own.Count > 0 ? own : all).Select(m => m.Groups["page"].Value + m.Groups["usage"].Value).Distinct().ToList();
    }

    /// <summary>GameInput's device table entry for a collection, which the
    /// table names by VID, PID, usage page and usage: the name
    /// Windows.Gaming.Input shows for the pad and its HapticFeedback value.
    /// Nulls where the table has no entry for one of
    /// <paramref name="usages"/>.</summary>
    static (string? Description, string? Haptic) GameInputEntry(string vid, string pid, List<string> usages)
    {
        try
        {
            using var table = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameInput\Devices");
            if (table == null) return (null, null);
            foreach (string usage in usages)
            {
                using var entry = table.OpenSubKey(vid + pid + usage);
                if (entry == null) continue;
                return (entry.GetValue("Description") is string { Length: > 0 } description ? description : null, entry.GetValue("HapticFeedback") as string);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return (null, null);
    }

    /// <summary>The product name Windows stored for a VID and PID, which
    /// DirectInput gives for every device with those IDs: OEMName under the
    /// user's key, then the machine's.</summary>
    static string? StoredName(string vid, string pid)
    {
        string path = $@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{vid}&PID_{pid}";
        try
        {
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(path);
                if (key?.GetValue("OEMName") is string name && name.Length > 0) return name;
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }

    // ── offline ──────────────────────────────────────────────────────────

    static void Offline()
    {
        Chromium.Load(Beside("chromium-gamepad-ids.txt"));
        Check("Chromium's gamepad tables load", Chromium.Count > 500, $"{Chromium.Count} ids, {Chromium.Source}");
        var problems = new List<string>();
        var pins = LoadPins(Beside("identity-pins.txt"), problems);
        Check("the pins file reads without a problem", problems.Count == 0, string.Join("; ", problems.Take(5)));

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var deployable = ctx.AllProfiles.Where(p => p.IsDeployable).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var unpinned = deployable.Where(p => !pins.ContainsKey(p.Id)).Select(p => p.Id).ToList();
        Check($"every one of the {deployable.Count} deployable profiles has pins", unpinned.Count == 0, string.Join(", ", unpinned.Take(12)));
        var stray = pins.Keys.Where(id => deployable.All(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))).ToList();
        Check("every pinned id is a deployable profile", stray.Count == 0, string.Join(", ", stray.Take(12)));

        // A line that holds for one backend only is a difference between the
        // backends, and a line that holds from a build on is one between
        // Windows versions. Each says why.
        var unexplained = pins.SelectMany(k => k.Value.Where(p => (p.Backend != null || p.FromBuild > 0) && string.IsNullOrWhiteSpace(p.Reason)).Select(p => $"{k.Key} {p.Backend}:{p.Reader}")).ToList();
        Check("every line that names a backend or a Windows build carries its reason in the comment above it", unexplained.Count == 0, string.Join(", ", unexplained.Take(8)));
        int split = pins.Count(k => k.Value.Any(p => p.Backend != null));
        Console.WriteLine($"  [note] {pins.Count} profiles pinned, {split} with a line that names a backend");

        // No reader is pinned to another device's IDs without a reason: the
        // HID class, XInput, Windows.Gaming.Input and SDL each read the
        // profile's own VID and PID, or the line says why not.
        var foreign = new List<string>();
        foreach (var p in deployable)
        {
            if (!pins.TryGetValue(p.Id, out var lines)) continue;
            string own = $"{p.VendorId:X4}:{p.ProductId:X4}";
            foreach (var pin in lines)
            {
                if (pin.Reader == "chrome") continue;
                var ids = System.Text.RegularExpressions.Regex.Matches(pin.Value, @"\b[0-9A-F]{4}:[0-9A-F]{4}\b").Select(m => m.Value).Where(v => !v.StartsWith("00", StringComparison.Ordinal) || v != "0000:0000").ToList();
                // The usage of a hid line reads as page:usage too.
                if (pin.Reader == "hid") ids = ids.Take(1).ToList();
                if (ids.Count > 0 && ids.Any(v => v != own) && string.IsNullOrWhiteSpace(pin.Reason) && !pin.Value.Contains(own, StringComparison.Ordinal))
                    foreign.Add($"{p.Id} {pin.Reader}: {ids.First(v => v != own)}, own {own}");
            }
        }
        Check("no reader is pinned to IDs other than the profile's own without a reason", foreign.Count == 0, string.Join("; ", foreign.Take(8)));

        // What a real device read. A * stands for a field the record does
        // not hold.
        int realLines = 0;
        string? realId = null, source = null;
        foreach (string raw in File.ReadAllLines(Beside("real-devices.txt")))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) { source = null; continue; }
            if (!line.StartsWith(' ')) { if (!line.StartsWith('#')) realId = line; source = null; continue; }
            string body = line.TrimStart();
            if (body.StartsWith('#')) { source = (source == null ? "" : source + " ") + body.TrimStart('#').Trim(); continue; }
            int space = body.IndexOf(' ');
            if (realId == null || space < 0) { Check($"real-devices.txt: \"{body}\" follows a profile id and has a value", false); continue; }
            string reader = body[..space], pattern = body[(space + 1)..];
            realLines++;
            Check($"{realId}: the real device's {reader} line says where it was read", !string.IsNullOrWhiteSpace(source), pattern);
            if (!pins.TryGetValue(realId, out var pinned) || !Reading.Readers.Contains(reader))
            {
                Check($"{realId}: the real device's {reader} line names a pinned profile and a reader", false);
                continue;
            }
            var usbip = Expected(pinned, HMBackend.Usbip)[reader];
            var like = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$");
            Check($"{realId}: on USB/IP the {reader} line is the real device's", usbip.Any(v => like.IsMatch(v)),
                  $"real: {pattern} | pinned: {string.Join(" | ", usbip)}");
            source = null;
        }
        Check("real-devices.txt holds a real device's reading", realLines > 0, $"{realLines} lines");

        // The device node's name Windows' own INF gives a real device of a
        // profile's IDs and connection (InboxNames.cs).
        InboxNames.Run(ctx.AllProfiles, Check);
    }

    // ── live and record ──────────────────────────────────────────────────

    static void Live(HMBackend backend, string sdlDir, string[] ids, string? record)
    {
        Chromium.Load(Beside("chromium-gamepad-ids.txt"));
        var problems = new List<string>();
        var pins = record == null ? LoadPins(Beside("identity-pins.txt"), problems) : new();
        if (record == null) Check("the pins file reads without a problem", problems.Count == 0, string.Join("; ", problems.Take(5)));
        bool sdl = Sdl.Start(sdlDir, out string why);
        Check("SDL starts", sdl, sdl ? Sdl.Version : why);
        if (!sdl) return;
        WgiPins.Listen();
        _ = WgiPins.Raws();

        using var ctx = new HMContext(backend);
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        Console.WriteLine($"  backend {backend}, machine backend {HMContext.MachineBackend}, session {Process.GetCurrentProcess().SessionId}, {Environment.OSVersion.Version}, SDL {Sdl.Version}");
        var profiles = (ids.Length > 0
                ? ids.Select(i => ctx.GetProfile(i) ?? throw new ArgumentException($"no profile {i}"))
                : ctx.AllProfiles.Where(p => p.IsDeployable && p.SupportsBackend(backend)))
            .OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        Console.WriteLine($"  {profiles.Count} profiles");

        using var output = record != null ? new StreamWriter(record, append: false, new UTF8Encoding(false)) { NewLine = "\n" } : null;
        output?.WriteLine($"# identity_fidelity_check record: backend {backend}, {Environment.OSVersion.Version}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}, session {Process.GetCurrentProcess().SessionId}, SDL {Sdl.Version}");
        var total = Stopwatch.StartNew();
        int matched = 0, uncompared = 0;
        // True once Windows.Gaming.Input has listed a device of this process.
        bool wgiListed = false;
        int build = Environment.OSVersion.Version.Build;
        var before = Readers.Take();
        foreach (var p in profiles)
        {
            var sw = Stopwatch.StartNew();
            Dictionary<string, List<string>>? want = null;
            var either = new HashSet<(string Reader, string Value)>();
            if (record == null)
            {
                if (!pins.TryGetValue(p.Id, out var lines)) { Check($"{p.Id}: has pins", false); continue; }
                want = Expected(lines, backend, build, either);
            }
            Reading? got = null;
            string? other = null;
            long createMs = 0, readMs = 0, disposeMs = 0;
            bool nameless = false;
            for (int attempt = 1; ; attempt++)
            {
                sw.Restart();
                got = null;
                HMController? c = null;
                Exception? failure = null;
                try { c = ctx.CreateController(p, "fidelity:" + p.Id, backend); }
                catch (Exception e) when (e is InvalidOperationException or TimeoutException) { failure = e; }
                createMs = sw.ElapsedMilliseconds;
                if (c == null)
                {
                    // A create that fails is a reading too: the pins say which
                    // profiles Windows refuses, as it refuses the real device.
                    got = Reading.Failed(failure!);
                    Console.WriteLine($"  [note] {p.Id}: the create failed: {failure!.GetType().Name}: {failure.Message.ReplaceLineEndings(" ")}");
                    break;
                }
                try
                {
                    string? parent = c.Backend == HMBackend.Usbip ? UsbDevice(c) : c.InstanceId;
                    if (parent == null) { nameless = true; break; }
                    other = OtherDriver(parent);
                    // A record waits until the readers have agreed with
                    // themselves for a while. A check stops at the first
                    // reading that is the pinned one.
                    if (other == null)
                        got = record != null
                            ? Readers.Read(parent, before, minimumMs: (int)(2000 * Scale), budgetMs: (int)(10000 * Scale), settled: null, stableMs: (int)(1500 * Scale))
                            : Readers.Read(parent, before, minimumMs: 0, budgetMs: (int)(8000 * Scale), settled: r => Matches(r, want!, either));
                    readMs = sw.ElapsedMilliseconds - createMs;
                }
                finally
                {
                    long at = sw.ElapsedMilliseconds;
                    // Windows asks the programs that hold a device before it
                    // removes one, and SDL's window, which this thread owns,
                    // answers only while this thread pumps SDL's events. A
                    // dispose on this thread left the question unanswered,
                    // and the removal of a UMDF2 device SDL had open waited
                    // ten seconds for it (read 2026-11-20). So the dispose
                    // runs on another thread while this one pumps.
                    var gone = System.Threading.Tasks.Task.Run(c.Dispose);
                    while (!gone.Wait(5)) Sdl.Pump();
                    disposeMs = sw.ElapsedMilliseconds - at;
                }

                // Windows.Gaming.Input listed nothing for a device it lists:
                // the device arrived before it tracked this process, or it
                // stayed silent through the whole read, as on the PCG02 GLE
                // on 2026-11-25 for amazon-luna-usb, 16 s with nothing
                // listed, right after amazon-luna-ble of the same VID and PID
                // went, while every other reader read the device. A silent
                // read says nothing about the identity, so the profile is
                // created again, up to three times. A check knows from the
                // pins that it lists the device, a record takes every
                // profile but a keyboard or a mouse for one it lists.
                if (got == null || got.Wgi.Count > 0 || attempt == 3) break;
                bool listed = want != null ? OnThisMachine(want)[0]["wgi"].Count > 0 : p.Type is not ("keyboard" or "mouse");
                if (!listed) break;
                Console.WriteLine(wgiListed
                    ? $"  [note] {p.Id}: Windows.Gaming.Input listed nothing for it in {readMs} ms. The profile is created again."
                    : $"  [note] {p.Id}: Windows.Gaming.Input listed nothing for it in {readMs} ms and has listed no device of this process yet. The profile is created again.");
                Readers.Dropped(before, (int)(8000 * Scale));
                before = Readers.Take();
            }
            if (nameless) { Check($"{p.Id}: its device has an instance id", false); continue; }
            wgiListed |= got is { Wgi.Count: > 0 };

            if (got == null)
            {
                uncompared++;
                Console.WriteLine($"  [note] {p.Id}: not compared. {other} drives the device on this machine, as it drives a real device with these IDs.");
                if (record != null) Check($"{p.Id}: a record is made where Windows' own drivers and HIDMaestro's drive the device", false, other ?? "");
            }
            else if (output != null)
            {
                output.WriteLine(p.Id);
                foreach (var (reader, value) in got.Lines()) output.WriteLine($"  {reader} {value}");
                output.Flush();
                Console.WriteLine($"  {p.Id,-44} recorded: {string.Join(" ", Reading.Readers.Select(r => $"{r} {got.Of(r).Count}"))}  create {createMs} ms");
            }
            else
            {
                var diff = new List<string>();
                if (!Matches(got, want!, either))
                {
                    // Against the pins as this machine's records change them.
                    var here = OnThisMachine(want!)[0];
                    foreach (string r in Reading.Readers)
                    {
                        var read = Without(got.Of(r), r, either).ToList();
                        foreach (string v in here[r].Except(read)) diff.Add($"{r} pinned, not read: {v}");
                        foreach (string v in read.Except(here[r])) diff.Add($"{r} read, not pinned: {v}");
                    }
                    if (diff.Count == 0) diff.Add("the readers' lines are the pinned ones in another number");
                }
                else
                {
                    var here = OnThisMachine(want!, count: true);
                    if (here.Count > 1 && !Without(got.Of("sdl"), "sdl", either).SequenceEqual(here[0]["sdl"])) s_storedNames++;
                }
                if (either.Count > 0)
                {
                    var held = either.Where(e => got.Of(e.Reader).Contains(e.Value)).Select(e => $"{e.Reader} {e.Value}").ToList();
                    Console.WriteLine($"  [note] {p.Id}: no machine has been read on Windows build {build} for {either.Count} line(s) pinned from a later build. This one read {(held.Count == 0 ? "none of them" : string.Join(" | ", held))}.");
                }
                if (diff.Count == 0) matched++;
                Check($"{p.Id}: every reader gives the pinned reading", diff.Count == 0,
                      diff.Count == 0 ? $"create {createMs} ms, read {readMs} ms, dispose {disposeMs} ms" : string.Join(" | ", diff));
                // What Windows.Gaming.Input listed at the last read, the
                // devices the baseline held included: a device it lists under
                // an id it listed before the create reads as none.
                if (diff.Count > 0)
                    Console.WriteLine($"  [note] {p.Id}: read for {readMs} ms. Windows.Gaming.Input listed at the last read: {(Readers.LastWgi.Count == 0 ? "nothing" : string.Join(" | ", Readers.LastWgi))}");
            }
            // The next device's reading must hold none of this one's. XInput
            // gives a slot again, and SDL's XInput driver keeps a slot's
            // joystick while the slot holds a pad of the same subtype
            // (SDL_xinputjoystick.c, AddXInputDevice), so both have to have
            // dropped this pad first. Windows.Gaming.Input names each device
            // anew, so a baseline taken now covers what it still lists.
            if (!Readers.Dropped(before, (int)(8000 * Scale)))
                Console.WriteLine($"  [note] {p.Id}: XInput or SDL still lists the device {(int)(8000 * Scale)} ms after its dispose. The next baseline includes it.");
            before = Readers.Take();
        }
        Sdl.Stop();
        Console.WriteLine($"  {profiles.Count} profiles in {total.Elapsed.TotalSeconds:N0} s" + (record == null ? $", {matched} gave the pinned reading" : "") +
                          (uncompared > 0 ? $", {uncompared} not compared" : ""));
        if (record == null && s_tableNames + s_tableDrops > 0)
            Console.WriteLine($"  [note] this machine's GameInput device table names {s_tableNames} of the Windows.Gaming.Input lines read, and {s_tableDrops} pinned lines are of pads it keeps Windows.Gaming.Input from listing (HapticFeedback DS5)");
        if (record == null && s_storedNames > 0)
            Console.WriteLine($"  [note] SDL's DirectInput name of {s_storedNames} profile(s) is the one Windows stored for the IDs on this machine");
    }
}
