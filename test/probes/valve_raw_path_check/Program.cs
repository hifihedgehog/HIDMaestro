// The Triton raw path lands on the controller report, not the lizard mouse
// (issue #58).
//
// The reported symptom was a virtual steam-controller-2 tearing the host's
// mouse cursor across the screen: SubmitRawReport prepended the descriptor's
// FIRST report id, and that descriptor puts the lizard-mode mouse (0x40) and
// keyboard (0x41) ahead of the controller state (0x42), exactly as the real
// hardware does. Every frame went out re-headed as a mouse report, with the
// rolling sequence number landing on relative X at 250 Hz.
//
// No wire capture was ever taken of that, so this probe takes one. Windows
// publishes the mouse, keyboard and controller reports on a SINGLE HID
// collection here, whose input length is the largest of the three, so the
// discriminator is byte 0 of each frame rather than which handle it arrived
// on. Every frame the device emits is read back and its report id checked.
//
// A device-free negative control runs first, and it is what makes the live
// half meaningful: the same descriptor is parsed both ways, and the old
// position rule is asserted to still pick the mouse while the profile's own
// declaration picks the controller state. Without it, a green live run could
// equally mean the bug had never existed.
//
// Three submit forms are checked, because a consumer can use any of them:
//
//   1. SubmitRawReport, data-only      -> declared id prepended
//   2. SubmitRawReport, full frame     -> passed through unshifted
//   3. SubmitRawExtendedReport         -> always verbatim
//
// Requires admin (device creation). Exit 0 PASS, 1 FAIL.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;

static class ValveRawPathCheck
{
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
    static readonly IntPtr INVALID = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string p, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, int n, out int read, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CancelIoEx(IntPtr h, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid g);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, byte[] caps);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid g, IntPtr e, IntPtr h, uint f);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, uint i,
                                                   ref SP_DEVICE_INTERFACE_DATA a);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref SP_DEVICE_INTERFACE_DATA a,
        IntPtr d, uint size, out uint need, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize; public Guid Class; public uint Flags; public IntPtr Reserved;
    }

    const byte StateReport = 0x42;
    const byte MouseReport = 0x40;
    const byte KeyboardReport = 0x41;
    const int FrameSize = 54;

    static int s_fail;

    static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) s_fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static IEnumerable<string> HidPaths()
    {
        HidD_GetHidGuid(out Guid hid);
        IntPtr set = SetupDiGetClassDevsW(ref hid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == INVALID) yield break;
        try
        {
            var d = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>()
            };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, i, ref d); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref d, IntPtr.Zero, 0, out uint need, IntPtr.Zero);
                if (need == 0) continue;
                IntPtr buf = Marshal.AllocHGlobal((int)need);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref d, buf, need, out _, IntPtr.Zero))
                        yield return Marshal.PtrToStringUni(buf + 4) ?? "";
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    /// <summary>Every HID collection this VID/PID publishes, with the input
    /// report length each declares.</summary>
    static List<(string Path, int InLen)> Collections(ushort vid, ushort pid)
    {
        var found = new List<(string, int)>();
        foreach (var p in HidPaths())
        {
            if (p.IndexOf($"vid_{vid:x4}", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (p.IndexOf($"pid_{pid:x4}", StringComparison.OrdinalIgnoreCase) < 0) continue;
            IntPtr h = CreateFileW(p, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                                   IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h == INVALID) continue;
            try
            {
                if (!HidD_GetPreparsedData(h, out IntPtr pp)) continue;
                var caps = new byte[64];
                int st = HidP_GetCaps(pp, caps);
                HidD_FreePreparsedData(pp);
                if (st != 0x00110000) continue;
                found.Add((p, BitConverter.ToUInt16(caps, 4)));
            }
            finally { CloseHandle(h); }
        }
        return found;
    }

    /// <summary>Reads a collection on a background thread, keeping both the
    /// recent frames and the report id of every frame seen since the device
    /// came up. The id history is separate so the per-step reset cannot erase
    /// what the final assertion reads.</summary>
    sealed class Listener : IDisposable
    {
        readonly IntPtr _h;
        readonly List<byte[]> _frames = new();
        readonly List<byte> _ids = new();
        volatile bool _stop;

        public Listener(string path, int inLen)
        {
            _h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                             IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (_h == INVALID) return;
            new Thread(() =>
            {
                var buf = new byte[Math.Max(inLen, 1)];
                while (!_stop)
                {
                    if (!ReadFile(_h, buf, buf.Length, out int n, IntPtr.Zero) || n <= 0) break;
                    lock (_frames)
                    {
                        _frames.Add(buf.Take(n).ToArray());
                        _ids.Add(buf[0]);
                    }
                }
            })
            { IsBackground = true }.Start();
        }

        public bool Opened => _h != INVALID;
        public void Reset() { lock (_frames) _frames.Clear(); }
        public byte[]? FirstFrame()
        {
            lock (_frames) return _frames.FirstOrDefault(f => f.Length > 1);
        }
        public byte[] AllReportIds()
        {
            lock (_frames) return _ids.Distinct().OrderBy(x => x).ToArray();
        }

        public void Dispose()
        {
            _stop = true;
            if (_h != INVALID) { CancelIoEx(_h, IntPtr.Zero); CloseHandle(_h); }
        }
    }

    static byte[] BuildFrame(byte seq)
    {
        var f = new byte[FrameSize];
        f[0] = StateReport;
        f[1] = seq;                  // the rolling byte that was driving the cursor
        f[10] = 0x00; f[11] = 0x40;  // left stick X
        return f;
    }

    /// <summary>Submit for a while, then hand back the first real frame the
    /// host read.</summary>
    static byte[]? DriveAndRead(Listener l, Action submit)
    {
        l.Reset();
        for (int i = 0; i < 40; i++) { submit(); Thread.Sleep(5); }
        Thread.Sleep(200);
        return l.FirstFrame();
    }

    static int Main()
    {
        Console.WriteLine("=== Triton raw path lands on the controller report (issue #58) ===");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var prof = ctx.GetProfile("steam-controller-2");
        if (prof == null) { Console.WriteLine("  steam-controller-2 missing from the catalog"); return 1; }

        // ── Negative control, no device needed ───────────────────────────
        Console.WriteLine();
        Console.WriteLine("-- the descriptor's own report order --");
        var desc = prof.GetDescriptorBytes()!;
        byte byPosition = HIDMaestro.Internal.HidReportBuilder.Parse(desc).InputReportId;
        byte byDeclaration = HIDMaestro.Internal.HidReportBuilder
            .Parse(desc, null, StateReport).InputReportId;
        Check("selecting by position still picks the lizard mouse, as reported",
              byPosition == MouseReport, $"0x{byPosition:X2}");
        Check("selecting by the profile's declaration picks the controller state",
              byDeclaration == StateReport, $"0x{byDeclaration:X2}");
        var spec = prof.Inner.ExtendedReport;
        Check("the profile is what declares it",
              spec is { AlwaysArmed: true } && spec.ReportIdByte == StateReport,
              $"alwaysArmed={spec?.AlwaysArmed} reportId=0x{spec?.ReportIdByte:X2}");

        // ── Live wire ────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("-- the frames a consumer's submissions put on the wire --");
        HMController? c = null;
        Listener? listener = null;
        try
        {
            c = ctx.CreateController(prof);

            List<(string Path, int InLen)> cols = new();
            for (int i = 0; i < 60 && cols.Count == 0; i++)
            {
                Thread.Sleep(500);
                cols = Collections(prof.VendorId, prof.ProductId);
            }
            Console.WriteLine($"     collections: {string.Join(", ", cols.Select(x => x.InLen + "B"))}");
            var col = cols.FirstOrDefault(x => x.InLen == FrameSize);
            Check($"a {FrameSize}-byte input collection exists", col.Path != null,
                  col.Path != null ? $"{col.InLen}B" : $"found {cols.Count}");
            if (col.Path == null) return 1;

            listener = new Listener(col.Path, col.InLen);
            Check("the collection opened for reading", listener.Opened);

            // 1. Data-only, the contract SubmitRawReport documents.
            var frame = BuildFrame(0x11);
            var got = DriveAndRead(listener, () => c.SubmitRawReport(frame.AsSpan(1)));
            Check("data-only SubmitRawReport reaches the host", got != null,
                  got != null ? $"len={got.Length}" : "no frame");
            if (got != null)
            {
                Check("it carries the declared report id", got[0] == StateReport,
                      $"id=0x{got[0]:X2}, want 0x{StateReport:X2}");
                Check("the sequence byte stayed inside the frame", got[1] == 0x11,
                      $"byte1=0x{got[1]:X2}");
            }

            // 2. Full frame, report id already present.
            frame = BuildFrame(0x22);
            got = DriveAndRead(listener, () => c.SubmitRawReport(frame));
            Check("a full frame arrives unshifted",
                  got is { Length: > 1 } && got[0] == StateReport && got[1] == 0x22,
                  got is { Length: > 1 } ? $"id=0x{got[0]:X2} byte1=0x{got[1]:X2}" : "no frame");

            // 3. The explicit always-verbatim entry point.
            frame = BuildFrame(0x33);
            got = DriveAndRead(listener, () => c.SubmitRawExtendedReport(frame));
            Check("SubmitRawExtendedReport arrives verbatim",
                  got is { Length: > 1 } && got[0] == StateReport && got[1] == 0x33,
                  got is { Length: > 1 } ? $"id=0x{got[0]:X2} byte1=0x{got[1]:X2}" : "no frame");

            // The whole issue in one assertion, over every frame the device
            // emitted across all three submissions.
            var ids = listener.AllReportIds();
            Check("not one frame went out as the lizard mouse or keyboard",
                  ids.Length > 0 && ids.All(id => id != MouseReport && id != KeyboardReport),
                  "ids seen: " + string.Join(",", ids.Select(x => $"0x{x:X2}")));
        }
        catch (Exception ex)
        {
            Check("the raw path drives without throwing", false, ex.Message);
        }
        finally
        {
            listener?.Dispose();
            try { c?.Dispose(); } catch { }
            Thread.Sleep(500);
        }

        Console.WriteLine();
        Console.WriteLine(s_fail == 0
            ? "=== THE RAW PATH LANDS ON THE CONTROLLER REPORT ==="
            : $"=== {s_fail} FAILED ===");
        return s_fail == 0 ? 0 : 1;
    }
}
