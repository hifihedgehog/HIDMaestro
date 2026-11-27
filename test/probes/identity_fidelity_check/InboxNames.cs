// The names Windows' own INFs give a real device's node, against the
// profiles' deviceDescription: the offline part's inbox name check.
//
// Windows names the node of a real Xbox pad from an inbox INF on the
// machine: xinputhid.inf a Bluetooth pad's node (its Bth_Bus and Btle_Bus
// lines), dc1-controller.inf a wired Xbox One or Series pad's USB node, and
// xusb22.inf an Xbox 360 pad's USB node. A UMDF2 create gives the virtual's
// device node the profile's deviceDescription. So for every profile whose
// IDs and connection a model line of one of these INFs names, the profile's
// deviceDescription must be that line's name, read from the INF's [Strings]
// on this machine. An ID this machine's INF does not name is not checked: a
// Windows whose INF lacks it, as a Windows 10 xinputhid.inf can lack the
// Bluetooth LE IDs, passes on that ID by design, and the output says so.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using HIDMaestro;

internal static class InboxNames
{
    /// <summary>One kind of node an inbox INF names: the INF, the node as
    /// the output calls it, the hardware ID form of its model lines (groups
    /// vid and pid, and src for a Bluetooth vendor ID source), whether a
    /// profile's connection is that node's, the profiles of the family the
    /// INF serves, which the output names when the INF names none of their
    /// IDs, and one ID of the form with the IDs the pattern must read from it,
    /// so a pattern that reads nothing cannot pass for an INF that names
    /// nothing.</summary>
    sealed record Rule(string Inf, string Node, Regex Id, Func<HMProfile, Match, bool> Connection,
                       Func<HMProfile, bool> Family, string Sample, string SampleIds);

    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string HidService = "{00001124-0000-1000-8000-00805f9b34fb}";
    const string HogpService = "{00001812-0000-1000-8000-00805f9b34fb}";

    static readonly Rule[] Rules =
    {
        new("xinputhid.inf", "Bluetooth node",
            new Regex(@"^BTHENUM\\" + Regex.Escape(HidService) + @"_VID&(?<src>[0-9A-F]{4})(?<vid>[0-9A-F]{4})_PID&(?<pid>[0-9A-F]{4})$", Options),
            (p, m) => Bluetooth(p, "classic", m), p => Bluetooth(p, "classic", null) && Xinputhid(p),
            @"BTHENUM\" + HidService + "_VID&0002045e_PID&02fd", "045E:02FD"),
        new("xinputhid.inf", "Bluetooth LE node",
            new Regex(@"^BTHLEDevice\\" + Regex.Escape(HogpService) + @"_Dev_VID&(?<src>[0-9A-F]{2})(?<vid>[0-9A-F]{4})_PID&(?<pid>[0-9A-F]{4})$", Options),
            (p, m) => Bluetooth(p, "le", m), p => Bluetooth(p, "le", null) && Xinputhid(p),
            @"BTHLEDevice\" + HogpService + "_Dev_VID&02045e_PID&0b13", "045E:0B13"),
        new("dc1-controller.inf", "USB node",
            new Regex(@"^USB\\VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})$", Options),
            (p, _) => Usb(p), p => Usb(p) && Xinputhid(p),
            @"USB\VID_045E&PID_02EA", "045E:02EA"),
        new("xusb22.inf", "USB node",
            new Regex(@"^USB\\VID_(?<vid>[0-9A-F]{4})&PID_(?<pid>[0-9A-F]{4})$", Options),
            (p, _) => Usb(p), p => XusbCompanion(p),
            @"USB\Vid_045E&Pid_028E", "045E:028E"),
    };

    static bool Usb(HMProfile p) => string.Equals(p.Connection, "usb", StringComparison.OrdinalIgnoreCase);

    /// <summary>A Bluetooth profile of the transport, and with
    /// <paramref name="m"/> the vendor ID source its line carries: 2 for the
    /// USB-IF, the default, 1 for the Bluetooth SIG.</summary>
    static bool Bluetooth(HMProfile p, string transport, Match? m)
    {
        if (!string.Equals(p.Connection, "bluetooth", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(p.Inner.BluetoothTransport ?? "classic", transport, StringComparison.OrdinalIgnoreCase)) return false;
        if (m == null) return true;
        int source = string.Equals(p.Inner.BluetoothVendorIdSource, "bluetooth", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        return Convert.ToInt32(m.Groups["src"].Value, 16) == source;
    }

    static bool Xinputhid(HMProfile p)
    {
        try { return p.Inner.UsesXinputhid; }
        catch (InvalidOperationException) { return false; }
    }

    static bool XusbCompanion(HMProfile p)
    {
        try { return p.Inner.RequiresXusbCompanion; }
        catch (InvalidOperationException) { return false; }
    }

    static ushort Hex(Match m, string group) => Convert.ToUInt16(m.Groups[group].Value, 16);

    static string Ids(Match m) => $"{Hex(m, "vid"):X4}:{Hex(m, "pid"):X4}";

    sealed record Node(string HardwareId, ushort Vid, ushort Pid, Match M, SortedSet<string> Names);

    /// <summary>Checks every profile of <paramref name="profiles"/> against
    /// the inbox INFs in the machine's Windows\INF folder.</summary>
    public static void Run(IReadOnlyList<HMProfile> profiles, Action<string, bool, string> check)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF");
        var infs = new Dictionary<string, Inf?>(StringComparer.OrdinalIgnoreCase);
        int read = 0, compared = 0;
        foreach (var rule in Rules)
        {
            var sample = rule.Id.Match(rule.Sample);
            check($"{rule.Inf} {rule.Node}: the pattern reads {rule.SampleIds} from {rule.Sample}",
                  sample.Success && Ids(sample) == rule.SampleIds, sample.Success ? Ids(sample) : "no match");
            if (!infs.TryGetValue(rule.Inf, out var inf))
            {
                inf = Inf.Read(Path.Combine(dir, rule.Inf));
                infs[rule.Inf] = inf;
                if (inf == null)
                    Console.WriteLine($"  [note] {rule.Inf} is not in {dir}, so no rule of it checks a profile on this machine");
                else
                {
                    read++;
                    check($"{rule.Inf}: its model lines and [Strings] read, and every model line's name is in [Strings]",
                          inf.Models.Count > 0 && inf.Strings.Count > 0 && inf.Unresolved.Count == 0,
                          $"{inf.Models.Count} model lines, {inf.Strings.Count} strings, DriverVer {inf.DriverVer ?? "none"}" +
                          (inf.Unresolved.Count > 0 ? ", no string for " + string.Join(", ", inf.Unresolved.Take(5)) : ""));
                }
            }
            if (inf == null) continue;

            // The nodes of this rule's form the INF names, each with every
            // name a model line gives it.
            var nodes = new List<Node>();
            foreach (var (name, ids) in inf.Models)
                foreach (string id in ids)
                {
                    var m = rule.Id.Match(id);
                    if (!m.Success) continue;
                    var node = nodes.FirstOrDefault(n => string.Equals(n.HardwareId, id, StringComparison.OrdinalIgnoreCase));
                    if (node == null) nodes.Add(node = new Node(id, Hex(m, "vid"), Hex(m, "pid"), m, new SortedSet<string>(StringComparer.Ordinal)));
                    node.Names.Add(name);
                }
            Console.WriteLine($"  [note] {rule.Inf} names {nodes.Count} {rule.Node}(s) here: {(nodes.Count == 0 ? "none" : string.Join(" ", nodes.Select(n => $"{n.Vid:X4}:{n.Pid:X4}").Distinct()))}");

            foreach (var p in profiles)
            {
                var mine = nodes.Where(n => n.Vid == p.VendorId && n.Pid == p.ProductId && rule.Connection(p, n.M)).ToList();
                if (mine.Count == 0)
                {
                    if (rule.Family(p))
                        Console.WriteLine($"  [note] {p.Id}: {rule.Inf} names no {rule.Node} with {p.VendorId:X4}:{p.ProductId:X4} here, so this rule does not check its deviceDescription");
                    continue;
                }
                compared++;
                var names = mine.SelectMany(n => n.Names).Distinct(StringComparer.Ordinal).ToList();
                string? description = p.Inner.DeviceDescription;
                check($"{p.Id}: deviceDescription is {rule.Inf}'s name for its {rule.Node} {mine[0].HardwareId}",
                      description != null && names.Contains(description, StringComparer.Ordinal),
                      $"deviceDescription {(description == null ? "none" : "\"" + description + "\"")}, the INF's {string.Join(" or ", names.Select(n => "\"" + n + "\""))}");
            }
        }
        if (read > 0)
            check("the inbox INFs name the node of at least one profile", compared > 0, $"{compared} compared");
        Console.WriteLine("  [note] an ID this machine's INF does not name is not checked: a Windows whose INF lacks the ID, as a " +
                          "Windows 10 xinputhid.inf can lack the Bluetooth LE IDs, passes on that ID by design.");
    }

    /// <summary>An INF as this check reads it: every model line, a line
    /// <c>%token% = install section, hardware ID, ...</c> in any section,
    /// with its token's value from [Strings], and the DriverVer. Section
    /// names and tokens match without case, as Windows matches them, and a
    /// model section's platform decoration is not read, so the check reads
    /// an INF of any architecture.</summary>
    sealed class Inf
    {
        public readonly List<(string Name, string[] Ids)> Models = new();
        public readonly Dictionary<string, string> Strings = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Unresolved = new();
        public string? DriverVer;

        public static Inf? Read(string path)
        {
            if (!File.Exists(path)) return null;
            // ReadAllText reads the byte order mark: the inbox INFs are
            // UTF-16 LE.
            string text = File.ReadAllText(path);
            var lines = new List<(string Section, string Key, string Value)>();
            string section = "", pending = "";
            foreach (string raw in text.Split('\n'))
            {
                string line = pending + WithoutComment(raw.TrimEnd('\r'));
                pending = "";
                // A backslash that ends a line continues it on the next.
                if (line.EndsWith('\\')) { pending = line[..^1]; continue; }
                line = line.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                lines.Add((section, line[..eq].Trim(), line[(eq + 1)..].Trim()));
            }
            var inf = new Inf();
            foreach (var (s, key, value) in lines)
            {
                if (s.Equals("Strings", StringComparison.OrdinalIgnoreCase)) inf.Strings.TryAdd(key, Unquoted(value));
                else if (s.Equals("Version", StringComparison.OrdinalIgnoreCase) && key.Equals("DriverVer", StringComparison.OrdinalIgnoreCase)) inf.DriverVer = value;
            }
            foreach (var (s, key, value) in lines)
            {
                if (s.Equals("Strings", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.Length < 3 || key[0] != '%' || key[^1] != '%') continue;
                string[] parts = value.Split(',').Select(x => x.Trim()).ToArray();
                if (parts.Length < 2) continue;
                string token = key[1..^1];
                if (!inf.Strings.TryGetValue(token, out string? name)) { inf.Unresolved.Add(token); continue; }
                inf.Models.Add((name, parts[1..].Where(x => x.Length > 0).ToArray()));
            }
            return inf;
        }

        /// <summary>The line up to a semicolon outside a quoted string.</summary>
        static string WithoutComment(string line)
        {
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') quoted = !quoted;
                else if (line[i] == ';' && !quoted) return line[..i];
            }
            return line;
        }

        /// <summary>A [Strings] value without its quotes, two quotes inside
        /// read as one.</summary>
        static string Unquoted(string value) =>
            value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Replace("\"\"", "\"") : value;
    }
}
