using System;
using System.Collections.Generic;
using System.Linq;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

/// <summary>A reply and the report before it, on the input section's one
/// frame. The catalog's switch-pro runs through the SDK's protocol host on a
/// channel that holds one frame, as the input section does: a write replaces
/// it, and the driver delivers the frame it holds when a read comes, which a
/// wait for delivery stands for here (HMController.SectionChannel.Delivered,
/// SharedMemoryIO.WaitInputDelivered). In input mode 0x3F the simple report
/// goes out once per change, and the engine counts it sent as it is built.
/// A reply written before the driver read that report took the frame, and
/// the change never reached the host until the next one. A report on the
/// clock carries the state again on its next tick, so a reply does not wait
/// for one.</summary>
internal static class ReplyOverReport
{
    static void Check(string what, bool ok, string detail = "") => Checks.Check(what, ok, detail);

    sealed class Section : IHostChannel
    {
        public readonly List<byte[]> Read = new();
        public int Waits;
        byte[]? _frame;
        uint _seq, _delivered;

        public uint Write(byte[] data, int dataLen, byte[] extended, int extendedLen)
        {
            _frame = extended[..extendedLen];
            return ++_seq;
        }

        /// <summary>The driver reads the frame the section holds.</summary>
        public void DriverReads()
        {
            if (_frame == null || _delivered == _seq) return;
            Read.Add(_frame);
            _delivered = _seq;
        }

        public bool Delivered(uint seq, int waitMs)
        {
            if (_delivered - seq < 0x80000000u) return true;
            if (waitMs == 0) return false;
            Waits++;
            DriverReads();
            return _delivered - seq < 0x80000000u;
        }

        public bool AcknowledgesDelivery => true;
    }

    static (ProtocolHost Host, Section Channel) Make(int index)
    {
        var p = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == "switch-pro");
        var compiled = CompiledProtocol.Compile(p, null)!;
        var ch = new Section();
        return (new ProtocolHost(p, compiled, index, ch, new object()), ch);
    }

    /// <summary>A subcommand as SDL writes it: report 0x01, a packet number,
    /// neutral rumble, the subcommand and its argument, 49 bytes.</summary>
    static byte[] Subcommand(byte id, byte arg = 0)
    {
        var b = new byte[49];
        b[0] = 0x01;
        new byte[] { 0x00, 0x01, 0x40, 0x40, 0x00, 0x01, 0x40, 0x40 }.CopyTo(b, 2);
        b[10] = id;
        b[11] = arg;
        return b;
    }

    public static void Run()
    {
        Console.WriteLine("-- a reply and the report before it, on the input section's one frame");
        ChangeReport();
        ClockReport();
    }

    static void ChangeReport()
    {
        var (host, ch) = Make(7973);
        using (host)
        {
            host.OnOutput(Subcommand(0x03, 0x3F), 49, ProtocolHost.DeliveryTimeoutMs);
            ch.DriverReads();
            host.Tick(0);
            ch.DriverReads();
            var block = host.Engine.CopyStateBlock();
            block[2] |= 0x08;
            host.OnState(block);
            // The pressed button's report goes out, and a subcommand's reply
            // comes before the driver reads it.
            host.Tick(0);
            host.OnOutput(Subcommand(0x48, 0x01), 49, ProtocolHost.DeliveryTimeoutMs);
            ch.DriverReads();
            for (int i = 0; i < 3; i++) { host.Tick(0); ch.DriverReads(); }
            var simple = ch.Read.Where(f => f.Length > 0 && f[0] == 0x3F).ToList();
            var replies = ch.Read.Count(f => f.Length > 0 && f[0] == 0x21);
            Check("in mode 0x3F a reply waits for the simple report before it, so the host reads the change",
                  simple.Count == 2 && simple[1][1] != simple[0][1] && replies == 2,
                  $"read {string.Join(" ", ch.Read.Select(f => $"{f[0]:X2}:{Convert.ToHexString(f.AsSpan(1, Math.Min(2, f.Length - 1)))}"))}");
        }
    }

    static void ClockReport()
    {
        var (host, ch) = Make(7974);
        using (host)
        {
            host.OnOutput(Subcommand(0x02), 49, ProtocolHost.DeliveryTimeoutMs);
            ch.DriverReads();
            host.Tick(0);
            int before = ch.Waits;
            // A reply right after a 0x30 report, before the driver read it.
            host.OnOutput(Subcommand(0x04), 49, ProtocolHost.DeliveryTimeoutMs);
            int waited = ch.Waits - before;
            ch.DriverReads();
            host.Tick(0);
            ch.DriverReads();
            Check("a reply does not wait for a 0x30 report, whose next tick carries the state again",
                  waited == 0 && ch.Read.Count >= 2 && ch.Read[^1][0] == 0x30 && ch.Read[^2][0] == 0x21,
                  $"waits {waited}, read {string.Join(" ", ch.Read.Select(f => f[0].ToString("X2")))}");
        }
    }
}
