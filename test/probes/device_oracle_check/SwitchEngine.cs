// switch-engine: the Switch Pro's protocol as profile data, against the
// responder 9bd6279's driver ran (design note, decision (b)).
//
// Each sequence below is a host's traffic shaped as Windows hands it to a
// driver: an output report the descriptor declares at the descriptor's
// length (49 bytes on Bluetooth, 64 on USB), reads at the input length,
// SubmitState's 48-byte bodies and the stream's 15 ms periods. The same
// sequence runs twice. driver_harness_oracle.exe, 9bd6279's driver.c,
// runs it through hm_script.c on case 5's plan. The engine runs it on the
// catalog's switch-pro.json or switch-pro-usb.json, with a read taking a
// queued reply first and a period's frame reaching a read only when one
// is parked, as the responder delivered them (driver.c:1489-1548 and
// 1812-1868 at 9bd6279). Every read's bytes must match: each 0x21 and
// 0x81 reply, each 0x30 and 0x3F frame and with them the timer byte. On
// USB the engine's 64-byte report must equal the responder's 49 bytes,
// then zeros.
//
// Case 5's own Switch streams also hold writes Windows never hands a
// driver: a 0x80 report on the Bluetooth descriptor, lengths other than
// the descriptor's, and reads at other lengths. DD-0's live round showed
// HID class refusing or cutting those, so they are not here.
//
// Then the check runs with one byte of the SPI image changed, which must
// make it fail, and checks that switch-pro-usb loads on its own descriptor
// and a profile with Bluetooth's protocol on that descriptor is refused
// with the path.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;
using HIDMaestro.Internal.Usbip;

internal static class SwitchEngine
{
    const int BtPacket = 49, UsbPacket = 64, OracleRead = 49;
    static readonly byte[] NeutralRumble = { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 };

    static int s_failures;
    static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    /// <summary>A catalog profile's JSON as the SDK embeds it.</summary>
    internal static string CatalogJson(string id)
    {
        var asm = typeof(HMContext).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)))
        {
            using var st = asm.GetManifestResourceStream(res)!;
            using var sr = new StreamReader(st, Encoding.UTF8);
            string t = sr.ReadToEnd();
            if (JsonNode.Parse(t) is JsonObject o && (string?)o["id"] == id) return t;
        }
        throw new InvalidOperationException($"no catalog profile {id}");
    }

    /// <summary>The profile validated as a create validates it, its
    /// protocol compiled for UMDF2, or the refusal.</summary>
    internal static (ControllerProfile? Profile, CompiledProtocol? Protocol, string? Refusal) Load(string json)
    {
        try
        {
            var p = ControllerProfile.Parse(Encoding.UTF8.GetBytes(json)) ?? throw new InvalidOperationException("no profile");
            p.ValidateDeviceModel();
            return (p, CompiledProtocol.Compile(p, null) ?? throw new InvalidOperationException("no protocol"), null);
        }
        catch (InvalidOperationException e) { return (null, null, e.Message); }
    }

    sealed class Builder : ILayoutVisitor<int>
    {
        public ProtocolEngine Engine = null!;
        public readonly byte[] Buffer = new byte[64];
        public int Visit<TSet>(ref TSet set) where TSet : struct, IReportSet => Engine.BuildNext(ref set, Buffer);
    }

    /// <summary>One sequence: the script line for the responder, and the
    /// engine run beside it, recording what each read returns.</summary>
    sealed class Sim
    {
        readonly ProtocolEngine _e;
        readonly LayoutRunner _r;
        readonly Builder _b;
        readonly bool _usb;
        readonly byte[] _reply = new byte[64];
        public readonly StringBuilder Script = new();
        public readonly List<byte[]> Reads = new();
        public int Parked;
        byte _number;

        public Sim(ControllerProfile p, CompiledProtocol proto, string name, int index, bool usb)
        {
            _usb = usb;
            _r = ProgramBinder.BindLayouts(proto.Model)!;
            _e = new ProtocolEngine(proto, p.SerialString, index, _r);
            _e.SetStateBlock(p.StateBlockAtRest());
            _b = new Builder { Engine = _e };
            Script.Append($"stream switch-engine/{name}\ndevice switch-pro {index} driver\n");
        }

        public void Body(byte[] body)
        {
            Script.Append($"frame {Convert.ToHexString(body)} -\n");
            _e.SetStateBlock(body);
        }

        /// <summary>One period of the stream: the frame the registers
        /// select, built and counted, to a parked read or to none.</summary>
        public void Tick()
        {
            Script.Append("tick\n");
            int n = _r.Visit(_b);
            if (n > 0 && Parked > 0) { Parked--; Reads.Add(_b.Buffer[..n]); }
        }

        public void Read()
        {
            Script.Append($"read {OracleRead}\n");
            if (_e.HasReply) Reads.Add(Take());
            else Parked++;
        }

        public void ReadIfNone() { if (Parked == 0) Read(); }

        /// <summary>A period with a read parked first, as HID class keeps
        /// one pending.</summary>
        public void Ticks(int n) { for (int i = 0; i < n; i++) { ReadIfNone(); Tick(); } }

        byte[] Take()
        {
            int n = _e.TakeReply(_reply.Length, _reply);
            return _reply[..n];
        }

        /// <summary>An output report as Windows hands it on: the
        /// descriptor's length, zero-padded.</summary>
        public void Write(byte[] report, bool setOutput = false)
        {
            var pkt = new byte[_usb ? UsbPacket : BtPacket];
            report.AsSpan(0, Math.Min(report.Length, pkt.Length)).CopyTo(pkt);
            Script.Append($"{(setOutput ? "setoutput" : "write")} {Convert.ToHexString(pkt)}\n");
            _e.HandleCommand(pkt, 0);
            if (_e.HasReply && Parked > 0) { Parked--; Reads.Add(Take()); }
        }

        /// <summary>SDL's ConstructSubcommand (SDL_hidapi_switch.c:468-500
        /// at 3.4.18): the report ID, a packet number, neutral rumble, the
        /// subcommand and its data, at the packet length of the transport,
        /// with a read parked first.</summary>
        public void Subcommand(byte id, ReadOnlySpan<byte> data, bool setOutput = false)
        {
            var pkt = new byte[_usb ? UsbPacket : BtPacket];
            pkt[0] = 0x01;
            pkt[1] = _number;
            NeutralRumble.CopyTo(pkt, 2);
            pkt[10] = id;
            data.CopyTo(pkt.AsSpan(11));
            _number = (byte)((_number + 1) & 0xF);
            ReadIfNone();
            Write(pkt, setOutput);
        }

        public void Spi(uint address, byte length) =>
            Subcommand(0x10, new[] { (byte)address, (byte)(address >> 8), (byte)(address >> 16), (byte)(address >> 24), length });

        /// <summary>SDL's WriteProprietary: 0x80 and the command.</summary>
        public void Proprietary(byte cmd, ReadOnlySpan<byte> tail = default)
        {
            var pkt = new byte[2 + tail.Length];
            pkt[0] = 0x80;
            pkt[1] = cmd;
            tail.CopyTo(pkt.AsSpan(2));
            ReadIfNone();
            Write(pkt);
        }

        /// <summary>SDL's open on a Pro Controller, as hm_case_switch.c's
        /// SdlInit sends it.</summary>
        public void SdlInit(bool setOutput = false)
        {
            Subcommand(0x02, default, setOutput);
            Tick();
            Spi(0x8010, 0x8025 - 0x8010 + 1);
            Spi(0x603D, 0x604E - 0x603D + 1);
            Spi(0x6020, 0x6037 - 0x6020 + 1);
            Spi(0x8026, 0x8039 - 0x8026 + 1);
            Subcommand(0x48, new byte[] { 1 }, setOutput);
            Subcommand(0x30, new byte[] { 1 }, setOutput);
            Ticks(2);
            Subcommand(0x40, new byte[] { 1 }, setOutput);
        }

        /// <summary>BTrySetupUSB (SDL_hidapi_switch.c:689-707): status,
        /// handshake, high speed, handshake, force USB.</summary>
        public void UsbSetup()
        {
            Proprietary(0x01);
            Proprietary(0x02);
            Proprietary(0x03);
            Proprietary(0x02);
            Proprietary(0x04);
        }
    }

    sealed record Sequence(string Name, bool Usb, Sim Sim);

    static List<Sequence> Sequences(ControllerProfile bt, CompiledProtocol btp, ControllerProfile usb, CompiledProtocol usbp, ulong seed)
    {
        var list = new List<Sequence>();
        var rng = new Rng(seed, 0x5E01);
        byte[] RandomBody() { var b = new byte[48]; rng.Fill(b); return b; }
        var bodies = Enumerable.Range(0, 8).Select(_ => RandomBody()).ToArray();
        Sim New(string name, int index, bool isUsb)
        {
            var s = isUsb ? new Sim(usb, usbp, name, index, true) : new Sim(bt, btp, name, index, false);
            list.Add(new Sequence(name, isUsb, s));
            return s;
        }

        foreach (var (index, via) in new[] { (0, false), (1, false), (0, true) })
        {
            var s = New($"bt/sdl-init{(via ? "-setoutput" : "")}-idx{index}", index, false);
            s.Ticks(3);
            s.Body(bodies[0]);
            s.Ticks(2);
            s.SdlInit(via);
            foreach (var b in bodies) { s.Body(b); s.Ticks(1); }
        }
        for (int id = 0; id < 256; id++)
        {
            var s = New($"bt/subcommand-{id:X2}", 0, false);
            s.Body(bodies[1]);
            for (int len = 0; len <= BtPacket - 11; len++)
            {
                var data = new byte[len];
                rng.Fill(data);
                s.Subcommand((byte)id, data);
            }
            s.Ticks(2);
        }
        foreach (int index in Enumerable.Range(0, 16).Append(255))
            New($"bt/mac-idx{index}", index, false).Subcommand(0x02, default);
        {
            var s = New("bt/spi-read", 0, false);
            for (uint a = 0x5FF0; a < 0x60C0; a++)
                for (uint len = 0; len <= 0x20; len += (a & 7) == 0 ? 1u : 7u)
                    s.Spi(a, (byte)len);
            foreach (uint a in new uint[] { 0x00000000, 0x00007FFF, 0x00008010, 0x00008026, 0x0007FFFF, 0x00080000, 0xFFFFFFF0, 0x01006020 })
                s.Spi(a, 0x1D);
        }
        {
            var s = New("bt/input-mode", 0, false);
            s.SdlInit();
            s.Body(bodies[2]);
            foreach (byte m in new byte[] { 0x3F, 0x30, 0x31, 0x00, 0x21, 0x32, 0x33, 0x3F, 0x80, 0xFF, 0x31, 0x30 })
            {
                s.Subcommand(0x03, new[] { m });
                s.Ticks(3);
            }
            s.Subcommand(0x03, default);
            s.Ticks(2);
        }
        {
            var s = New("bt/imu", 0, false);
            s.SdlInit();
            foreach (byte arg in new byte[] { 0x00, 0x01, 0x02, 0x00, 0xFF, 0x00, 0x03 })
            {
                s.Subcommand(0x40, new[] { arg });
                foreach (var b in bodies)
                {
                    s.Body(b);
                    s.Ticks(1);
                    s.ReadIfNone();
                    s.Subcommand(0x02, default);
                }
            }
            s.Subcommand(0x40, default);
            s.Ticks(1);
        }
        {
            // More replies than the four the responder queues, before any
            // read: the oldest go (driver.c:1496-1500).
            var s = New("bt/reply-overflow", 1, false);
            s.Body(bodies[4]);
            var ids = new byte[] { 0x02, 0x10, 0x48, 0x30, 0x04, 0x21, 0x40, 0x22 };
            for (int round = 0; round < 3; round++)
            {
                int n = 3 + round * 2;
                for (int i = 0; i < n; i++)
                {
                    var pkt = new byte[BtPacket];
                    pkt[0] = 0x01;
                    pkt[1] = (byte)i;
                    NeutralRumble.CopyTo(pkt, 2);
                    pkt[10] = ids[i % ids.Length];
                    pkt[11] = (byte)(0x20 + i);
                    pkt[12] = 0x60;
                    pkt[15] = 0x10;
                    s.Write(pkt);
                }
                for (int i = 0; i < n + 2; i++) s.Read();
                s.Ticks(2);
                while (s.Parked > 0) s.Tick();
            }
        }
        // Seeded traffic: the four output reports the Bluetooth descriptor
        // declares, bodies, periods and reads, in random order.
        for (int part = 0; part < 10; part++)
            Random(New($"bt/random-{part}", part & 1, false), rng, bodies, new byte[] { 0x01, 0x10, 0x11, 0x12 }, 4000);

        // USB: the 0x80 family and the subcommands, at 64 bytes.
        {
            var s = New("usb/sdl-init-idx0", 0, true);
            s.UsbSetup();
            s.SdlInit();
            foreach (var b in bodies) { s.Body(b); s.Ticks(1); }
        }
        for (int cmd = 0; cmd < 256; cmd++)
        {
            var s = New($"usb/usb-command-{cmd:X2}", 0, true);
            for (int len = 0; len < UsbPacket - 1; len += 7)
            {
                var tail = new byte[len];
                rng.Fill(tail);
                s.Proprietary((byte)cmd, tail);
            }
            s.Ticks(2);
        }
        for (int id = 0; id < 256; id++)
        {
            var s = New($"usb/subcommand-{id:X2}", 0, true);
            s.Proprietary(0x02);
            s.Body(bodies[1]);
            for (int len = 0; len <= UsbPacket - 11; len += 3)
            {
                var data = new byte[len];
                rng.Fill(data);
                s.Subcommand((byte)id, data);
            }
            s.Ticks(2);
        }
        foreach (int index in Enumerable.Range(0, 16).Append(255))
        {
            var s = New($"usb/mac-idx{index}", index, true);
            s.Proprietary(0x01);
            s.Subcommand(0x02, default);
        }
        for (int part = 0; part < 4; part++)
        {
            var s = New($"usb/random-{part}", part & 1, true);
            s.Proprietary(0x02);
            Random(s, rng, bodies, new byte[] { 0x01, 0x10, 0x80, 0x82 }, 4000);
        }
        return list;
    }

    static void Random(Sim s, Rng rng, byte[][] bodies, byte[] outputs, int reports)
    {
        var known = new byte[] { 0x02, 0x03, 0x04, 0x10, 0x21, 0x40, 0x48, 0x30, 0x38 };
        int sent = 0;
        while (sent < reports)
        {
            int r = rng.Next(100);
            if (r < 50)
            {
                var pkt = new byte[UsbPacket];
                rng.Fill(pkt);
                pkt[0] = outputs[rng.Next(outputs.Length)];
                if (pkt[0] == 0x01 && rng.Bool()) pkt[10] = known[rng.Next(known.Length)];
                s.Write(pkt, setOutput: rng.Next(10) >= 7);
                sent++;
            }
            else if (r < 62)
            {
                var body = new byte[48];
                if (rng.Next(4) == 0) bodies[rng.Next(bodies.Length)].CopyTo(body, 0);
                else rng.Fill(body);
                s.Body(body);
            }
            else if (r < 80) s.Tick();
            else if (s.Parked < 8) s.Read();
        }
        while (s.Parked > 0 && sent < reports + 64) { s.Tick(); sent++; }
    }

    /// <summary>The responder's reads against the engine's, per sequence.
    /// Returns the sequences that differ.</summary>
    static int Compare(List<Sequence> seqs, Dictionary<string, CompareLive.Stream> oracle, bool quiet)
    {
        int differ = 0;
        long reads = 0;
        foreach (var q in seqs)
        {
            if (!oracle.TryGetValue("switch-engine/" + q.Name, out var stream)) { differ++; Console.WriteLine($"  [DIFF] {q.Name}: the responder wrote no stream"); continue; }
            var got = stream.Outs.Where(o => o.Tag == "read").Select(o => CompareLive.Completion.From(o.Tag, o.Data)).ToList();
            string? first = null;
            if (got.Count != q.Sim.Reads.Count) first = $"{got.Count} reads completed by the responder, {q.Sim.Reads.Count} by the engine";
            for (int i = 0; first == null && i < got.Count; i++)
            {
                var want = got[i].Reported;
                var mine = q.Sim.Reads[i];
                bool same = q.Usb
                    ? mine.Length == UsbPacket && want.Length == OracleRead && mine.AsSpan(0, OracleRead).SequenceEqual(want) && mine.AsSpan(OracleRead).IndexOfAnyExcept((byte)0) < 0
                    : mine.AsSpan().SequenceEqual(want);
                if (!same) first = $"read {i}: responder {Convert.ToHexString(want)}, engine {Convert.ToHexString(mine)}";
            }
            reads += got.Count;
            if (first != null)
            {
                differ++;
                if (!quiet) Console.WriteLine($"  [DIFF] {q.Name}: {first}");
            }
        }
        if (!quiet) Console.WriteLine($"  {seqs.Count} sequences, {reads} reads, {differ} differ");
        return differ;
    }

    static int Drive(List<Sequence> seqs, string native, string planPath, out Dictionary<string, CompareLive.Stream> oracle)
    {
        string exe = Path.Combine(native, "driver_harness_oracle.exe");
        string work = Path.Combine(AppContext.BaseDirectory, "switch-engine", $"run-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var sc = new StringBuilder("hm-driver-script 1\n");
            foreach (var q in seqs) sc.Append(q.Sim.Script);
            string script = Path.Combine(work, "script.txt");
            File.WriteAllText(script, sc.ToString(), new UTF8Encoding(false));
            oracle = CompareLive.RunHarness(exe, planPath, script, work).ToDictionary(s => s.Name, StringComparer.Ordinal);
            return 0;
        }
        finally
        {
            foreach (var f in Directory.EnumerateFiles(work)) File.Delete(f);
            Directory.Delete(work);
        }
    }

    public static int Run(string golden, string native, ulong seed)
    {
        s_failures = 0;
        Console.WriteLine("=== device_oracle_check switch-engine: switch-pro and switch-pro-usb against 9bd6279's responder ===");
        string planPath = Path.Combine(golden, "case5-plan.txt");
        foreach (var need in new[] { planPath, Path.Combine(native, "driver_harness_oracle.exe") })
            if (!File.Exists(need)) { Console.WriteLine($"  [MISSING] {need}"); return 2; }

        string btJson = CatalogJson("switch-pro"), usbJson = CatalogJson("switch-pro-usb");
        var (bt, btp, btRefusal) = Load(btJson);
        var (usb, usbp, usbRefusal) = Load(usbJson);
        Check("switch-pro loads and its protocol compiles for UMDF2", btp != null, btRefusal ?? "");
        Check("switch-pro-usb loads on its own descriptor and its protocol compiles for UMDF2", usbp != null, usbRefusal ?? "");
        if (btp == null || usbp == null) return Done();

        // Bluetooth's layouts and protocol on the USB descriptor.
        var mixed = JsonNode.Parse(usbJson)!.AsObject();
        var btNode = JsonNode.Parse(btJson)!.AsObject();
        foreach (var k in new[] { "inputReports", "protocol" }) mixed[k] = btNode[k]!.DeepClone();
        var (_, _, mixedRefusal) = Load(mixed.ToJsonString());
        Check("switch-pro's protocol on switch-pro-usb's descriptor is refused with the path",
            mixedRefusal?.Contains("inputReports[0].size 49", StringComparison.Ordinal) == true, mixedRefusal ?? "accepted");

        var seqs = Sequences(bt!, btp, usb!, usbp, seed);
        Drive(seqs, native, planPath, out var oracle);
        int differ = Compare(seqs, oracle, quiet: false);
        Check("every read the responder completed, the engine's profile data completed with the same bytes", differ == 0, $"{differ} of {seqs.Count} sequences differ");
        bool sawUsbFrames = seqs.Where(q => q.Usb).SelectMany(q => q.Sim.Reads).Any(r => r[0] == 0x30);
        bool sawBtSimple = seqs.Where(q => !q.Usb).SelectMany(q => q.Sim.Reads).Any(r => r[0] == 0x3F);
        Check("the sequences reach 0x3F frames on Bluetooth and 0x30 frames on USB", sawUsbFrames && sawBtSimple);

        // One byte of the SPI image changed: the IMU calibration's first.
        var mutated = JsonNode.Parse(btJson)!.AsObject();
        var seg = mutated["protocol"]!["memory"]![0]!["segments"]!.AsArray().First(s => (string?)s!["at"] == "0x6020")!;
        string hex = (string)seg["hex"]!;
        seg["hex"] = (Convert.ToByte(hex[..2], 16) ^ 0x01).ToString("X2") + hex[2..];
        var (mp, mproto, mRefusal) = Load(mutated.ToJsonString());
        if (mproto == null) Check("the mutated profile loads", false, mRefusal ?? "");
        else
        {
            var mseqs = Sequences(mp!, mproto, usb!, usbp, seed).Where(q => !q.Usb).ToList();
            int mdiffer = Compare(mseqs, oracle, quiet: true);
            Check("with one SPI byte changed at 0x6020 the comparison fails", mdiffer > 0, $"{mdiffer} sequences differ");
        }
        return Done();
    }

    static int Done()
    {
        Console.WriteLine($"device_oracle_check switch-engine: {(s_failures == 0 ? "PASS" : $"FAIL, {s_failures} check(s)")}");
        return s_failures == 0 ? 0 : 1;
    }
}
