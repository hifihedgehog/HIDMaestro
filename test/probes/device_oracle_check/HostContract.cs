// host-contract: the UMDF2 driver's half of a device whose protocol and
// reports the SDK builds (the design note's decision (b) and mechanism 2),
// and the data path decision (c) leaves a v1 program, natively.
//
// The protocol host runs in the SDK. The driver keeps no device code: it
// sends each frame the SDK writes, acknowledges it on HID class's next read,
// keeps the polled input report for the IDs CachedInputReportIds lists, and
// for a profile with LiveFeatureReport serves report 0's GET_FEATURE from a
// store the SDK's data lane fills. Every case runs on the tree's driver,
// which must pass it, and on 9bd6279's, which fails it, each failure shown:
//   - a v1 program's switch-pro and dualshock-3-full, with no version
//     section and with a section of protocol 3: no thread but the worker, no
//     reply to a subcommand or a 0x80 command, the data lane under report
//     0x21, no frame for the DualShock 3's 49-byte native report, report 0's
//     GET_FEATURE refused, and output reports published as written
//   - under the contract, the configuration the tree's SDK writes for
//     switch-pro: no thread but the worker, the SDK's 0x30, 0x21 and 0x3F
//     frames sent whole, each acknowledged on the next read, and the polled
//     input report of IDs 0 and 1 the last 0x30 frame after a 0x21 reply,
//     where an empty CachedInputReportIds would give the reply (the control)
//   - under the contract, dualshock-3-full: the extended lane's 12-byte
//     input report sent, and report 0's GET_FEATURE the data lane's 49 bytes,
//     at every buffer length, the latest frame's
// The configuration the tree's SDK writes for the two is checked too:
// CachedInputReportIds 30 3F for switch-pro, LiveFeatureReport 1 and no
// Ds3Sixaxis for dualshock-3-full.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HIDMaestro.Internal;
using static Case5Driver;
using static FeatureContract;

internal static class HostContract
{
    public static int Run(string nativeDir, string goldenDir)
    {
        string tree = Path.Combine(nativeDir, "driver_harness_tree.exe"), oracle = Path.Combine(nativeDir, "driver_harness_oracle.exe");
        foreach (var exe in new[] { tree, oracle })
            if (!File.Exists(exe)) { Console.WriteLine($"  [FAIL] host-contract: {exe} is not built"); return 1; }
        string work = Path.Combine(AppContext.BaseDirectory, "host-contract", $"run-{Environment.ProcessId}");
        Directory.CreateDirectory(work);
        var c = new Checks { Mode = "host-contract" };
        try
        {
            var catalog = ProfileDatabase.LoadEmbedded().All;
            var kept = PlanValues(File.ReadAllText(Path.Combine(goldenDir, "case5-plan.txt"), Encoding.UTF8));
            Cases(c, catalog, kept, tree, oracle, work);
        }
        catch (Exception e)
        {
            c.Check("host-contract runs", false, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        Console.WriteLine($"  host-contract: {(c.Failures == 0 ? "PASS" : $"FAIL, {c.Failures}")}");
        return c.Failures;
    }

    static string Hex(IEnumerable<byte> b) => Convert.ToHexString(b.ToArray());

    /// <summary>n bytes: the given head, then i * step + seed for each byte
    /// after it.</summary>
    static byte[] Bytes(int n, byte seed, int step, params byte[] head)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = i < head.Length ? head[i] : (byte)(seed + i * step);
        return b;
    }

    // SDL's subcommand 0x02 at the Bluetooth packet length: the report ID,
    // a packet number, two neutral rumble frames, the subcommand
    // (SDL_hidapi_switch.c:468-500, 604-608 at 804cf65).
    static readonly byte[] Subcommand02 = Bytes(49, 0, 0, 0x01, 0x00, 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40, 0x02);
    // The proprietary handshake at the USB packet length (:276-282, 530-557).
    static readonly byte[] Handshake = Bytes(64, 0, 0, 0x80, 0x02);
    // A 48-byte state body, the data lane a v1 program writes.
    static readonly byte[] Body48 = Bytes(48, 0x10, 3);
    // Frames the SDK's protocol host writes on the extended lane: a 0x30
    // stream frame, a 0x21 reply and a 0x3F simple frame, each at its
    // report's declared length (49, 49 and 12).
    static readonly byte[] Full30 = Bytes(49, 0x40, 1, 0x30, 0x05, 0x91);
    static readonly byte[] Reply21 = Bytes(49, 0x80, 1, 0x21, 0x06, 0x91);
    static readonly byte[] Simple3F = Bytes(12, 0x20, 5, 0x3F);
    // The DualShock 3: the native 49-byte report a v1 program writes on the
    // extended lane, and under the contract the 12-byte input report on the
    // extended lane and the 49-byte live feature report on the data lane.
    static readonly byte[] Native49 = Bytes(49, 0x33, 7, 0x01, 0x00);
    static readonly byte[] Data12 = Bytes(12, 0x50, 9);
    static readonly byte[] Input12 = Bytes(12, 0x60, 11);
    static readonly byte[] Live49 = Bytes(49, 0x70, 13, 0x00, 0x3F);
    static readonly byte[] Live49b = Bytes(49, 0x05, 17, 0x00, 0x3F);
    // Output report 0 with sixaxis.sys command 2, as SDL's motor command
    // lays it out (SDL_hidapi_ps3.c), without a report ID.
    static readonly byte[] MotorCommand = Bytes(48, 0, 0, 0x02, 0x00, 0x00, 0x00, 0x40, 0x00, 0x80);

    static void Cases(Checks c, IReadOnlyList<ControllerProfile> catalog, Dictionary<string, List<RegValue>> kept,
                      string tree, string oracle, string work)
    {
        var sw = catalog.First(p => p.Id == "switch-pro");
        var ds = catalog.First(p => p.Id == "dualshock-3-full");
        var config = InstanceConfig(new List<ControllerProfile> { sw, ds }, work);

        // What the tree's SDK writes for the two.
        byte[]? Value(List<RegValue> v, string name) => v.Where(x => x.Name == name).Select(x => x.Data).FirstOrDefault();
        var swIds = Value(config[sw.Id], "CachedInputReportIds");
        c.Check("the SDK writes CachedInputReportIds 30 3F for switch-pro, its layouts' report IDs",
                swIds != null && Hex(swIds) == "303F", swIds == null ? "absent" : Hex(swIds));
        var live = Value(config[ds.Id], "LiveFeatureReport");
        c.Check("the SDK writes LiveFeatureReport 1 for dualshock-3-full",
                live != null && BitConverter.ToUInt32(live) == 1, live == null ? "absent" : Hex(live));
        c.Check("and no Ds3Sixaxis, which no driver reads", Value(config[ds.Id], "Ds3Sixaxis") == null,
                Value(config[ds.Id], "Ds3Sixaxis") is { } d3 ? Hex(d3) : "");
        c.Check("and no LiveFeatureReport for switch-pro", Value(config[sw.Id], "LiveFeatureReport") == null);

        var plan = new StringBuilder()
            .Append(Block("switch", sw, kept[sw.Id], "sw-v1"))
            .Append(Block("ds3", ds, kept[ds.Id], "ds-v1"))
            .Append(Block("switch", sw, config[sw.Id], "sw-v2"))
            .Append(Block("ds3", ds, config[ds.Id], "ds-v2"))
            .Append(Block("switch", sw, With(config[sw.Id], "CachedInputReportIds", RegBinary, Array.Empty<byte>()), "sw-v2-nolist"))
            .ToString();

        var s = new StringBuilder();
        foreach (var v in new[] { "absent", "max3" })
        {
            s.Append($"stream v1-switch-{v}\nversion {v}\ndevice sw-v1 0 driver\nthreads\n")
             .Append($"read 362\nwrite {Hex(Subcommand02)}\ncall 1\nwrite {Hex(Handshake)}\ncall 2\n")
             .Append($"read 362\nframe {Hex(Body48)} -\ncall 3\nremove\n");
            s.Append($"stream v1-ds3-{v}\nversion {v}\ndevice ds-v1 0 driver\nthreads\n")
             .Append($"read 12\nframe - {Hex(Native49)}\ncall 1\ngetfeature 00 49 00\ncall 2\n")
             .Append($"read 12\nframe {Hex(Data12)} -\ncall 3\nwrite {Hex(MotorCommand)}\ncall 4\nremove\n");
        }
        string SwitchContract(string device) =>
            $"version tag-own\ndevice {device} 0 driver\nthreads\n" +
            $"read 362\nwrite {Hex(Subcommand02)}\ncall 1\n" +
            $"frame - {Hex(Full30)}\ncall 2\nread 362\nframe - {Hex(Reply21)}\ncall 3\n" +
            "read 362\ngetinput 00 64 00\ngetinput 01 64 01\ngetinput 30 64 30\ncall 4\n" +
            $"frame - {Hex(Simple3F)}\ncall 5\nremove\n";
        s.Append("stream v2-switch\n").Append(SwitchContract("sw-v2"));
        s.Append("stream v2-switch-nolist\n").Append(SwitchContract("sw-v2-nolist"));
        s.Append("stream v2-ds3\nversion tag-own\ndevice ds-v2 0 driver\nthreads\n")
         .Append($"read 12\nframe {Hex(Live49)} {Hex(Input12)}\ncall 1\n");
        foreach (int len in new[] { 49, 64, 20, 1 }) s.Append($"getfeature 00 {len} 00\n");
        s.Append($"call 2\nread 12\nframe {Hex(Live49b)} {Hex(Input12)}\ncall 3\ngetfeature 00 49 00\ncall 4\nremove\n");

        var t = RunScript(tree, plan, s.ToString(), work, "host-tree");
        var o = RunScript(oracle, plan, s.ToString(), work, "host-oracle");
        int oracleFails = 0, total = 0;
        foreach (var (label, run) in new[] { ("tree", t), ("9bd6279", o) })
        {
            bool isTree = label == "tree";
            foreach (var (name, ok, detail) in Expectations(run))
            {
                if (isTree) { c.Check(name, ok, detail); total++; }
                else if (!ok) { oracleFails++; c.Info($"9bd6279 fails: {name}  {detail}"); }
            }
        }
        c.Check($"9bd6279's driver fails these cases, so they can fail: {oracleFails} of {total}", oracleFails > 0);
    }

    /// <summary>The outputs of a stream between two calls: before the first
    /// call for 0, between call n and call n + 1 for n.</summary>
    static List<(string Tag, byte[] Data)> Segment(List<(string Tag, byte[] Data)> s, uint n)
    {
        var seg = new List<(string, byte[])>();
        uint at = 0;
        foreach (var o in s)
        {
            if (o.Tag == "call") { at = BitConverter.ToUInt32(o.Data); continue; }
            if (at == n) seg.Add(o);
        }
        return seg;
    }

    static List<byte[]> Reads(List<(string Tag, byte[] Data)> s) => Of(s, "read").Where(x => Status(x) == 0).ToList();

    static string Show(byte[] completion) =>
        Status(completion) != 0 ? Status(completion).ToString("X8") : $"{Info(completion)}:{Convert.ToHexString(Body(completion)[..(int)Math.Min(Info(completion), 16)])}";

    /// <summary>The ring slots of a stream: the source, the report ID and the
    /// data (hm_record.c RecFlushRing).</summary>
    static List<(byte Id, byte[] Data)> Ring(List<(string Tag, byte[] Data)> s) =>
        Of(s, "ring").Select(r => (r[1], r[4..])).ToList();

    static uint Threads(List<(string Tag, byte[] Data)> s) => Of(s, "threads") is { Count: 1 } t ? BitConverter.ToUInt32(t[0]) : uint.MaxValue;

    static List<(string Name, bool Ok, string Detail)> Expectations(Dictionary<string, List<(string Tag, byte[] Data)>> r)
    {
        var res = new List<(string, bool, string)>();
        void Add(string name, Func<(bool, string)> f)
        {
            try { var (ok, d) = f(); res.Add((name, ok, d)); }
            catch (Exception e) { res.Add((name, false, $"{e.GetType().Name}: {e.Message}")); }
        }

        foreach (var v in new[] { "absent", "max3" })
        {
            var sw = r[$"v1-switch-{v}"];
            Add($"v1 switch-pro, section {v}: no thread but the worker", () => (Threads(sw) == 1, $"{Threads(sw)} threads"));
            Add($"v1 switch-pro, section {v}: no reply to subcommand 0x02 or to 0x80 0x02, and both reach the output ring", () =>
            {
                // The subcommand is written before call 1 and the handshake
                // before call 2. The read parked first completes with the
                // data lane frame after call 2.
                var reads = Reads(Segment(sw, 0).Concat(Segment(sw, 1)).ToList());
                var ring = Ring(sw);
                bool published = ring.Any(x => x.Id == 0x01) && ring.Any(x => x.Id == 0x80);
                return (reads.Count == 0 && published, $"reads {string.Join(" ", reads.Select(Show))}, ring {string.Join(" ", ring.Select(x => x.Id.ToString("X2")))}");
            });
            Add($"v1 switch-pro, section {v}: the data lane goes out under report 0x21", () =>
            {
                var reads = Reads(Segment(sw, 2));
                bool ok = reads.Count == 1 && Info(reads[0]) >= 49 && Body(reads[0])[0] == 0x21
                       && Body(reads[0]).Slice(1, 48).SequenceEqual(Body48);
                return (ok, string.Join(" ", reads.Select(Show)));
            });

            var d3 = r[$"v1-ds3-{v}"];
            Add($"v1 dualshock-3-full, section {v}: no thread but the worker", () => (Threads(d3) == 1, $"{Threads(d3)} threads"));
            Add($"v1 dualshock-3-full, section {v}: the 49-byte native report on the extended lane sends no frame", () =>
            {
                var reads = Reads(Segment(d3, 0));
                return (reads.Count == 0, string.Join(" ", reads.Select(Show)));
            });
            Add($"v1 dualshock-3-full, section {v}: GET_FEATURE of report 0 is refused", () =>
            {
                var g = Of(Segment(d3, 1), "getfeature");
                return (g.Count == 1 && Status(g[0]) == NotSupported, string.Join(" ", g.Select(Show)));
            });
            Add($"v1 dualshock-3-full, section {v}: a 12-byte data lane frame goes out, the control that reads complete", () =>
            {
                var reads = Reads(Segment(d3, 2));
                return (reads.Count == 1 && Info(reads[0]) == 12 && Body(reads[0])[..12].SequenceEqual(Data12), string.Join(" ", reads.Select(Show)));
            });
            Add($"v1 dualshock-3-full, section {v}: an output report reaches the ring as written, not folded into report 0x01", () =>
            {
                var ring = Ring(Segment(d3, 3));
                bool ok = ring.Count == 1 && ring[0].Id == MotorCommand[0] && ring[0].Data.AsSpan().SequenceEqual(MotorCommand.AsSpan(1));
                return (ok, string.Join(" ", ring.Select(x => $"{x.Id:X2}:{Convert.ToHexString(x.Data)}")));
            });
        }

        var s2 = r["v2-switch"];
        Add("contract switch-pro: no thread but the worker", () => (Threads(s2) == 1, $"{Threads(s2)} threads"));
        Add("contract switch-pro: the driver sends nothing of its own for subcommand 0x02, and publishes it", () =>
        {
            // The subcommand is written before call 1. The SDK's 0x30
            // frame after it completes the read parked first.
            var reads = Reads(Segment(s2, 0));
            var ring = Ring(s2);
            return (reads.Count == 0 && ring.Any(x => x.Id == 0x01 && x.Data.AsSpan().SequenceEqual(Subcommand02.AsSpan(1))),
                    $"reads {string.Join(" ", reads.Select(Show))}, ring {string.Join(" ", ring.Select(x => x.Id.ToString("X2")))}");
        });
        Add("contract switch-pro: the SDK's 0x30 frame goes out whole, 49 bytes", () =>
        {
            var reads = Reads(Segment(s2, 1));
            return (reads.Count == 1 && Info(reads[0]) == 49 && Body(reads[0])[..49].SequenceEqual(Full30), string.Join(" ", reads.Select(Show)));
        });
        Add("contract switch-pro: the SDK's 0x21 reply goes out whole, and is acknowledged on the next read", () =>
        {
            var reads = Reads(Segment(s2, 2));
            var acks = Of(Segment(s2, 3), "ack").Select(a => BitConverter.ToUInt32(a)).ToList();
            bool ok = reads.Count == 1 && Info(reads[0]) == 49 && Body(reads[0])[..49].SequenceEqual(Reply21) && acks.Contains(4u);
            return (ok, $"{string.Join(" ", reads.Select(Show))}, acks {string.Join(" ", acks)}");
        });
        Add("contract switch-pro: after the 0x21 reply the polled input report of IDs 0 and 1 is the last 0x30 frame, and 0x30 is refused", () =>
        {
            var g = Of(Segment(s2, 3), "getinput");
            bool ok = g.Count == 3 && Status(g[0]) == 0 && Body(g[0])[..49].SequenceEqual(Full30)
                   && Status(g[1]) == 0 && Body(g[1])[..49].SequenceEqual(Full30) && Status(g[2]) == NotSupported;
            return (ok, string.Join(" ", g.Select(Show)));
        });
        Add("contract switch-pro: the control, with CachedInputReportIds empty the polled report is the reply", () =>
        {
            var g = Of(Segment(r["v2-switch-nolist"], 3), "getinput");
            return (g.Count == 3 && Status(g[0]) == 0 && Body(g[0])[0] == 0x21, string.Join(" ", g.Select(Show)));
        });
        Add("contract switch-pro: a 0x3F frame goes out at its 12 bytes", () =>
        {
            var reads = Reads(Segment(s2, 4));
            return (reads.Count == 1 && Info(reads[0]) == 12 && Body(reads[0])[..12].SequenceEqual(Simple3F), string.Join(" ", reads.Select(Show)));
        });

        var d2 = r["v2-ds3"];
        Add("contract dualshock-3-full: no thread but the worker", () => (Threads(d2) == 1, $"{Threads(d2)} threads"));
        Add("contract dualshock-3-full: the extended lane's 12-byte input report goes out", () =>
        {
            var reads = Reads(Segment(d2, 0));
            return (reads.Count == 1 && Info(reads[0]) == 12 && Body(reads[0])[..12].SequenceEqual(Input12), string.Join(" ", reads.Select(Show)));
        });
        Add("contract dualshock-3-full: GET_FEATURE of report 0 is the data lane's 49 bytes at 49, 64, 20 and 1", () =>
        {
            var g = Of(Segment(d2, 1), "getfeature");
            int[] lengths = { 49, 64, 20, 1 };
            bool ok = g.Count == 4 && g.Select((x, i) => (x, n: Math.Min(lengths[i], 49)))
                .All(p => Status(p.x) == 0 && Info(p.x) == p.n && Body(p.x)[..p.n].SequenceEqual(Live49.AsSpan(0, p.n)));
            return (ok, string.Join(" ", g.Select(Show)));
        });
        Add("contract dualshock-3-full: the next frame's live report replaces it", () =>
        {
            var g = Of(Segment(d2, 3), "getfeature");
            return (g.Count == 1 && Status(g[0]) == 0 && Body(g[0])[..49].SequenceEqual(Live49b), string.Join(" ", g.Select(Show)));
        });
        return res;
    }
}
