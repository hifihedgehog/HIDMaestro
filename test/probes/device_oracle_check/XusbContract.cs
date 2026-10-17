// The XUSB companion under v2.0.0's version contract (the design note's
// decision (c), mechanism 4), run in the native harness (hm_script.c's
// script mode) on both builds of companion.c: 9bd6279's kept in
// native\oracle and the tree's.
//
// 1. Answers. For every catalog profile that presents an XUSB companion, at
//    indexes 0 and 3: the oracle companion as a 9bd6279-era SDK left it
//    (version section at protocols 2 to 3, VendorId and ProductId in the
//    controller's key) and the tree companion as the v2.0.0 SDK leaves it
//    (protocols 4 to 4 with the devnode's identity tag, the index under
//    SOFTWARE\HIDMaestro\Identity\<token>, XusbAnswers from
//    XusbAnswers.Compile and XusbCompanion 1) get the same requests: every
//    companion IOCTL at output sizes across each boundary, then SET_STATE.
//    Every completion's status, information and bytes, the XUSB interface
//    and the rumble the output ring receives must be equal, except the
//    bytes xusb-answer-changes.txt lists, and each listed byte must differ.
// 2. The contract, on the tree companion with the same stale values in the
//    key (XusbAnswers and XusbCompanion 1), as a killed v2.0.0 program
//    leaves them: with no version section and with a 9bd6279-era one it
//    registers no XUSB interface, answers no IOCTL and publishes no rumble.
//    With a section at 4 it writes CompanionProtocol and
//    CompanionProtocolMin 4 and nothing else of the section. With another
//    token's tag, or an XusbAnswers of another format or past a limit, its
//    add fails with STATUS_DEVICE_CONFIGURATION_ERROR, and for the blob the
//    error word at 60 names XusbAnswers. With XusbCompanion 0 it publishes
//    no rumble.
//
// The oracle companion registers its interface and answers from VendorId
// in the stale cases, so part 2 fails on 9bd6279's code, which the check
// shows by running part 2 on the oracle build too and expecting it to fail.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class XusbContract
{
    const uint GetInformation = 0x80006000, GetCapabilities = 0x8000E004, GetLedState = 0x8000E008,
               GetState = 0x8000E00C, SetState = 0x8000A010, WaitGuide = 0x8000E014,
               GetBattery = 0x8000E018, GetInformationEx = 0x8000E3FC, PowerInfo = 0x80006380;
    static readonly uint[] Ioctls = { GetInformation, GetCapabilities, GetLedState, GetState, WaitGuide, GetBattery, GetInformationEx, PowerInfo, 0x80006004, 0x8000E3B0 };
    static readonly uint[] Sizes = { 0, 1, 2, 3, 11, 12, 23, 24, 28, 29, 35, 36, 63, 64, 65 };
    const int StatusConfigError = unchecked((int)0xC0000182), StatusInvalidRequest = unchecked((int)0xC0000010);
    static readonly byte[] XusbGuid = new Guid("EC87F1E3-C13B-4100-B5F7-8B84D54260CB").ToByteArray();
    const string Rest = "FF7FFF7FFF7FFF7F000000000000";

    internal static int Run(string native, string probeDir)
    {
        string oracle = Path.Combine(native, "driver_harness_oracle.exe"), tree = Path.Combine(native, "driver_harness_tree.exe");
        if (!File.Exists(oracle) || !File.Exists(tree))
        {
            Console.WriteLine($"  [FAIL] xusb-contract: the driver harnesses are not in {native}");
            return 1;
        }
        string work = Path.Combine(Path.GetTempPath(), "hm-xusb-contract-" + Environment.ProcessId);
        Directory.CreateDirectory(work);
        try
        {
            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            var profiles = ctx.AllProfiles.Select(p => p.Inner).Where(p => PresentationPlan.Of(p).XusbCompanion)
                .OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            var changes = LoadChanges(Path.Combine(probeDir, "xusb-answer-changes.txt"));
            s_keptIds = KeptIds(Path.Combine(probeDir, "golden", "case5-plan.txt"));
            int fails = 0;
            void Check(string name, bool ok, string detail = "")
            {
                if (!ok) fails++;
                Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] xusb-contract: {name}{(detail.Length > 0 ? "  " + detail : "")}");
            }
            Check($"{profiles.Count} catalog profiles present an XUSB companion", profiles.Count == 10);

            // Part 1: the answers, oracle against tree.
            var plan = new StringBuilder("hm-driver-plan 1\n");
            var oracleScript = new StringBuilder("hm-driver-script 1\n");
            var treeScript = new StringBuilder("hm-driver-script 1\n");
            var requests = new List<(uint Code, uint Size)>();
            foreach (uint code in Ioctls) foreach (uint size in Sizes) requests.Add((code, size));
            foreach (var p in profiles)
                foreach (int index in new[] { 0, 3 })
                {
                    string id = $"{p.Id}@{index}";
                    PlanProfile(plan, id, p, index, XusbAnswers.Compile(p, index)!, 1);
                    foreach (var (sb, version) in new[] { (oracleScript, "sdk"), (treeScript, "tag-own") })
                    {
                        sb.Append($"stream xusb-contract/{id}\nversion {version}\ndevice {id} {index} companion\ngip {Rest}\n");
                        foreach (var (code, size) in requests) sb.Append($"ioctl 0x{code:X8} - {size}\n");
                        sb.Append($"ioctl 0x{SetState:X8} 0008001020 0\nremove\n");
                    }
                }
            string planPath = Path.Combine(work, "plan.txt");
            File.WriteAllText(planPath, plan.ToString());
            var want = Run(oracle, planPath, oracleScript, work, "oracle-answers");
            var got = Run(tree, planPath, treeScript, work, "tree-answers");
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in profiles)
                foreach (int index in new[] { 0, 3 })
                {
                    string stream = $"xusb-contract/{p.Id}@{index}";
                    var w = want.Single(s => s.Name == stream);
                    var g = got.Single(s => s.Name == stream);
                    Check($"{p.Id} idx{index}: the XUSB interface is registered on both",
                          w.Outs.Count(o => o.Tag == "device.interface" && o.Data.AsSpan(0, 16).SequenceEqual(XusbGuid)) == 1
                          && g.Outs.Count(o => o.Tag == "device.interface" && o.Data.AsSpan(0, 16).SequenceEqual(XusbGuid)) == 1);
                    var wc = w.Outs.Where(o => o.Tag == "ioctl").ToList();
                    var gc = g.Outs.Where(o => o.Tag == "ioctl").ToList();
                    Check($"{p.Id} idx{index}: {requests.Count + 1} completions on both", wc.Count == requests.Count + 1 && gc.Count == wc.Count,
                          $"{wc.Count} and {gc.Count}");
                    int diffs = 0, listed = 0;
                    for (int i = 0; i < Math.Min(wc.Count, gc.Count); i++)
                    {
                        byte[] a = wc[i].Data, b = gc[i].Data;
                        uint code = i < requests.Count ? requests[i].Code : SetState;
                        if (a.Length != b.Length || !a.AsSpan(0, 12).SequenceEqual(b.AsSpan(0, 12)))
                        {
                            diffs++;
                            Console.WriteLine($"    {p.Id} idx{index} request {i} 0x{code:X8}: oracle {Convert.ToHexString(a)}, tree {Convert.ToHexString(b)}");
                            continue;
                        }
                        for (int k = 12; k < a.Length; k++)
                        {
                            if (a[k] == b[k]) continue;
                            string key = $"{p.Id} 0x{code:X8} {k - 12}";
                            if (changes.TryGetValue(key, out var c) && c.Value == b[k]) { listed++; used.Add(key); continue; }
                            diffs++;
                            Console.WriteLine($"    {p.Id} idx{index} request {i} 0x{code:X8} size {(i < requests.Count ? requests[i].Size : 0)} byte {k - 12}: oracle {a[k]:X2}, tree {b[k]:X2}");
                        }
                    }
                    Check($"{p.Id} idx{index}: every answer equals 9bd6279's but the listed bytes", diffs == 0, $"{diffs} difference(s), {listed} listed byte(s)");
                    var wr = w.Outs.Where(o => o.Tag == "ring").Select(o => Convert.ToHexString(o.Data)).ToList();
                    var gr = g.Outs.Where(o => o.Tag == "ring").Select(o => Convert.ToHexString(o.Data)).ToList();
                    Check($"{p.Id} idx{index}: SET_STATE publishes the same rumble", wr.Count == 1 && wr.SequenceEqual(gr), $"{wr.Count} and {gr.Count}");
                }
            foreach (var key in changes.Keys.Where(k => !used.Contains(k)))
                Check($"the listed byte {key} ({changes[key].Why}) differs", false);

            // Part 2: the contract, on the tree companion, and on the oracle,
            // which must fail it.
            fails += Contract(tree, profiles[0], work, "tree", Check, expectPass: true);
            int oracleFails = 0;
            Contract(oracle, profiles[0], work, "oracle", (name, ok, detail) => { if (!ok) oracleFails++; }, expectPass: false);
            Check("9bd6279's companion fails the contract checks", oracleFails > 0, $"{oracleFails} of them fail");
            return fails;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static int Contract(string exe, ControllerProfile p, string work, string label, Action<string, bool, string> check, bool expectPass)
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) fails++;
            check($"{label}: {name}", ok, detail);
        }
        byte[] blob = XusbAnswers.Compile(p, 0)!;
        byte[] format3 = (byte[])blob.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(format3.AsSpan(4), 3);
        byte[] tooMany = (byte[])blob.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(tooMany.AsSpan(8), (uint)XusbAnswers.MaxEntries + 1);
        byte[] cut = blob.AsSpan(0, blob.Length - 1).ToArray();
        var plan = new StringBuilder("hm-driver-plan 1\n");
        PlanProfile(plan, "stale", p, 0, blob, 1);
        PlanProfile(plan, "gate-closed", p, 0, blob, 0);
        PlanProfile(plan, "format3", p, 0, format3, 1);
        PlanProfile(plan, "too-many", p, 0, tooMany, 1);
        PlanProfile(plan, "cut", p, 0, cut, 1);
        var script = new StringBuilder("hm-driver-script 1\n");
        void Device(string stream, string version, string id, bool requests)
        {
            script.Append($"stream {stream}\nversion {version}\ndevice {id} 0 companion\n");
            if (!requests) return;
            script.Append($"gip {Rest}\n");
            foreach (uint code in new[] { GetInformation, GetCapabilities, GetBattery, GetInformationEx })
                script.Append($"ioctl 0x{code:X8} - 64\n");
            script.Append($"ioctl 0x{SetState:X8} 0008001020 0\nremove\n");
        }
        Device("absent", "absent", "stale", true);
        Device("v1", "sdk", "stale", true);
        Device("own", "tag-own", "stale", true);
        Device("gate-closed", "tag-own", "gate-closed", true);
        Device("other", "tag-other", "stale", false);
        Device("format3", "tag-own", "format3", false);
        Device("too-many", "tag-own", "too-many", false);
        Device("cut", "tag-own", "cut", false);
        string planPath = Path.Combine(work, $"plan-contract-{label}.txt");
        File.WriteAllText(planPath, plan.ToString());
        List<CompareLive.Stream> got;
        try { got = Run(exe, planPath, script, work, "contract-" + label); }
        catch (InvalidOperationException e) when (!expectPass)
        {
            check($"{label}: the script runs", false, e.Message);
            return 1;
        }
        CompareLive.Stream S(string n) => got.Single(s => s.Name == n);
        bool Interface(CompareLive.Stream s) => s.Outs.Any(o => o.Tag == "device.interface" && o.Data.AsSpan(0, 16).SequenceEqual(XusbGuid));
        int Status(byte[] d) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4));
        int AddStatus(CompareLive.Stream s) => BinaryPrimitives.ReadInt32LittleEndian(s.Outs.First(o => o.Tag == "device.add").Data);
        byte[]? SectionAfterAdd(CompareLive.Stream s) => s.Outs.FirstOrDefault(o => o.Tag == "section.version").Data;

        foreach (var name in new[] { "absent", "v1" })
        {
            var s = S(name);
            var answers = s.Outs.Where(o => o.Tag == "ioctl").Take(4).ToList();
            Check($"{name}: stale XusbAnswers and XusbCompanion 1, and no XUSB interface", !Interface(s));
            Check($"{name}: no answer", answers.Count == 4 && answers.All(a => Status(a.Data) == StatusInvalidRequest),
                  string.Join(" ", answers.Select(a => $"0x{Status(a.Data):X8}")));
            Check($"{name}: no rumble published", !s.Outs.Any(o => o.Tag == "ring"));
        }
        {
            var s = S("own");
            Check("own: the XUSB interface registered", Interface(s));
            var answers = s.Outs.Where(o => o.Tag == "ioctl").Take(4).ToList();
            Check("own: every answer served", answers.Count == 4 && answers.All(a => Status(a.Data) == 0));
            Check("own: the rumble published", s.Outs.Count(o => o.Tag == "ring") == 1);
            byte[]? sec = SectionAfterAdd(s);
            Check("own: CompanionProtocol 4 at 40 and CompanionProtocolMin 4 at 44",
                  sec != null && BinaryPrimitives.ReadInt32LittleEndian(sec.AsSpan(40)) == 4 && BinaryPrimitives.ReadInt32LittleEndian(sec.AsSpan(44)) == 4);
            Check("own: nothing of the section written but its own field (DriverProtocol and the rest at 0)",
                  sec != null && sec.AsSpan(20, 20).IndexOfAnyExcept((byte)0) < 0 && BinaryPrimitives.ReadInt32LittleEndian(sec.AsSpan(60)) == 0);
            byte[]? afterRemove = s.Outs.Where(o => o.Tag == "section.version").Select(o => o.Data).LastOrDefault();
            Check("own: nothing cleared at removal", afterRemove != null && BinaryPrimitives.ReadInt32LittleEndian(afterRemove.AsSpan(40)) == 4);
        }
        Check("gate-closed: XusbCompanion 0 publishes no rumble", !S("gate-closed").Outs.Any(o => o.Tag == "ring"));
        Check("other: another token's tag fails the add with STATUS_DEVICE_CONFIGURATION_ERROR",
              AddStatus(S("other")) == StatusConfigError, $"0x{AddStatus(S("other")):X8}");
        foreach (var name in new[] { "format3", "too-many", "cut" })
        {
            var s = S(name);
            byte[]? sec = SectionAfterAdd(s);
            Check($"{name}: an XusbAnswers it cannot read fails the add with STATUS_DEVICE_CONFIGURATION_ERROR",
                  AddStatus(s) == StatusConfigError, $"0x{AddStatus(s):X8}");
            Check($"{name}: CompanionConfigError names XusbAnswers (1)",
                  sec != null && BinaryPrimitives.ReadInt32LittleEndian(sec.AsSpan(60)) == 1);
        }
        return fails;
    }

    /// <summary>The VendorId and ProductId values 9bd6279's SDK wrote for each
    /// profile, from the kept plan (golden\case5-plan.txt), which the oracle
    /// companion answers from: a profile whose identity this release
    /// changes is compared with what 9bd6279 gave the profile as it was.</summary>
    static Dictionary<string, (string Vid, string Pid)> s_keptIds = new();

    static Dictionary<string, (string Vid, string Pid)> KeptIds(string path)
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        string? id = null, vid = null, pid = null;
        foreach (var line in File.ReadAllLines(path))
        {
            string[] w = line.Split(' ');
            if (w[0] == "profile") { id = w[2]; vid = pid = null; }
            else if (w[0] == "value" && w[1] == "VendorId") vid = w[3];
            else if (w[0] == "value" && w[1] == "ProductId") pid = w[3];
            else if (w[0] == "end" && id != null && vid != null && pid != null) d[id] = (vid, pid);
        }
        return d;
    }

    static void PlanProfile(StringBuilder plan, string id, ControllerProfile p, int index, byte[] answers, int companion)
    {
        plan.Append($"profile xusb {id}\n");
        var kept = s_keptIds.TryGetValue(p.Id, out var k) ? k
            : (Convert.ToHexString(BitConverter.GetBytes((uint)p.VendorId)), Convert.ToHexString(BitConverter.GetBytes((uint)p.ProductId)));
        plan.Append($"value VendorId 4 {kept.Item1}\n");
        plan.Append($"value ProductId 4 {kept.Item2}\n");
        plan.Append($"value XusbAnswers 3 {Convert.ToHexString(answers)}\n");
        plan.Append($"value XusbCompanion 4 {Convert.ToHexString(BitConverter.GetBytes((uint)companion))}\n");
        plan.Append("end\n");
    }

    static List<CompareLive.Stream> Run(string exe, string plan, StringBuilder script, string work, string name)
    {
        string dir = Path.Combine(work, name);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "script.txt");
        File.WriteAllText(path, script.ToString());
        return CompareLive.RunHarness(exe, plan, path, dir);
    }

    /// <summary>xusb-answer-changes.txt: "&lt;profile&gt; 0x&lt;ioctl&gt; &lt;offset&gt;=&lt;byte&gt; | &lt;lane&gt; | &lt;reason&gt;",
    /// a byte of an answer the tree gives on purpose where 9bd6279 gave
    /// another.</summary>
    static Dictionary<string, (byte Value, string Why)> LoadChanges(string path)
    {
        var d = new Dictionary<string, (byte, string)>(StringComparer.Ordinal);
        if (!File.Exists(path)) return d;
        foreach (var raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] parts = line.Split(" | ");
            if (parts.Length != 3) throw new FormatException($"{path}: '{line}' is not '<profile> 0x<ioctl> <offset>=<byte> | <lane> | <reason>'.");
            int eq = parts[0].LastIndexOf('=');
            d.Add(parts[0][..eq], (Convert.ToByte(parts[0][(eq + 1)..], 16), parts[1] + ": " + parts[2]));
        }
        return d;
    }
}
