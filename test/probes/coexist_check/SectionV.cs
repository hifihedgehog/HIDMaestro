// V. USB/IP re-attach attempts a killed program leaves behind. usbip-win2's
//    host controller starts them when an import's connection drops without
//    a plugout, keys them by host, port and bus id, and keeps them for about
//    two hours (persistent.cpp start_attach_attempts, vhci.cpp
//    init_constants). The probe counts them with STOP_ATTACH_ATTEMPTS,
//    whose reply carries the number it canceled, and a probe-made attempt
//    against a port outside the SDK's range is the positive control.
//      2. A program on this SDK is killed with a composite on transport
//         index 8 (bus id 1-9), and a v1.12.0 program takes its port.
//         Another program's sweep cancels the killed program's attempt and
//         leaves the v1.12.0 imports attached.
//      3. That v1.12.0 program, with composites on slots 0 and 9, is
//         killed, and a program on this SDK takes its port. Its create
//         leaves no attempt on bus id 1-1.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

internal static partial class Program
{
    const int UsbipBasePort = 18509;

    static void SectionV()
    {
        var touched = new HashSet<(int Port, string BusId)>();
        try
        {
            if (!Vhci.Available()) { Check("V: the usbip-win2 host controller answers", false); return; }

            // Positive control: an attach that fails starts attempts
            // (vhci_ioctl.cpp complete), and the count reports them.
            const int controlPort = UsbipBasePort - 1;
            touched.Add((controlPort, "1-1"));
            bool attached = Vhci.Attach("127.0.0.1", controlPort, "1-1");
            int control = 0;
            WaitFor(() => (control += Math.Max(0, Vhci.Stop("127.0.0.1", controlPort, "1-1"))) > 0, 5000);
            Check("V: an attempt the probe starts is counted", !attached && control == 1, $"attach {attached}, counted {control}");

            VLegacyTakesPort(touched);
        }
        finally
        {
            foreach (var (port, busId) in touched) Vhci.Stop("127.0.0.1", port, busId);
        }
    }

    // ── V2 and V3 ──────────────────────────────────────────────────────

    static void VLegacyTakesPort(HashSet<(int Port, string BusId)> touched)
    {
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "legacy", "v1.12.0", "CoexistPeer.exe")))
        {
            Check("V2: the v1.12.0 peer is built", false, "build CoexistCheck.csproj");
            return;
        }

        int port;
        string busId;
        using (var a = PeerProcess.Start("V2-killed"))
        {
            string ca = a.Send("createat 8 composite -");
            if (!Ok(ca)) { Check("V2: the program to be killed creates a composite at index 8", false, ca); return; }
            busId = "1-" + (int.Parse(PeerProcess.Field(ca, "transport")!) + 1);
            port = RecordUsbipPort(a.Pid);
            touched.Add((port, busId));
            a.Kill();
        }
        WaitFor(() => !Vhci.Imported().Any(r => r.Port == port && r.BusId == busId), 10_000);

        using (var old = PeerProcess.Start("V2-v1.12.0", "v1.12.0"))
        {
            string c0 = old.Send("createat 0 composite -");
            string c9 = old.Send("createat 9 composite -");
            if (!Ok(c0) || !Ok(c9)) { Check("V2: the v1.12.0 program creates composites on slots 0 and 9", false, $"{c0} / {c9}"); return; }
            touched.Add((port, "1-1"));
            touched.Add((port, "1-10"));
            var mine = Vhci.Imported().Where(r => r.Port == port && (r.BusId == "1-1" || r.BusId == "1-10")).ToList();
            Check($"V2: the v1.12.0 program's server takes the killed program's port {port}", mine.Count == 2,
                  string.Join(", ", Vhci.Imported().Select(r => $"{r.Port}/{r.BusId}")));

            using (var sweeper = PeerProcess.Start("V2-sweeper"))
            {
                string s = sweeper.Send("sweep");
                Check("V2: another program sweeps", Ok(s), s);
            }
            int left = Vhci.Stop("127.0.0.1", port, busId);
            Check($"V2: the sweep cancels the killed program's attempt for {busId} at {port}", left == 0, $"{left} canceled by the probe");
            Check("V2: and leaves the v1.12.0 program's imports attached",
                  Vhci.Imported().Count(r => r.Port == port && (r.BusId == "1-1" || r.BusId == "1-10")) == 2);

            old.Kill();
        }
        Check("V3: the host controller drops the v1.12.0 imports once the program is killed",
              WaitFor(() => !Vhci.Imported().Any(r => r.Port == port), 10_000));

        using (var nu = PeerProcess.Start("V3-new"))
        {
            string cn = nu.Send("create composite -");
            if (!Ok(cn)) { Check("V3: a program on this SDK creates a composite", false, cn); return; }
            string nuBus = "1-" + (int.Parse(PeerProcess.Field(cn, "transport")!) + 1);
            int nuPort = RecordUsbipPort(nu.Pid);
            Check($"V3: its server takes the v1.12.0 program's port {port} and its composite bus id 1-1",
                  nuPort == port && nuBus == "1-1", $"{nuPort}/{nuBus}");
            int left1 = Vhci.Stop("127.0.0.1", port, "1-1");
            Check($"V3: no re-attach attempt for 1-1 at {port} outlives the create", left1 == 0, $"{left1} canceled by the probe");
            Check("V3: the new program's composite is still attached",
                  Vhci.Imported().Any(r => r.Port == port && r.BusId == "1-1"));
        }
    }

    /// <summary>The USB/IP server port a program's owner record names.</summary>
    static int RecordUsbipPort(int pid)
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroOwners");
        if (root == null) return 0;
        foreach (string id in root.GetSubKeyNames())
        {
            using var r = root.OpenSubKey(id);
            if (r?.GetValue("Pid") is int p && p == pid) return r.GetValue("UsbipPort") is int port ? port : 0;
        }
        return 0;
    }

    /// <summary>usbip-win2's host controller through its ioctl API, read
    /// from include/usbip/vhci.h of 0.9.7.x, 0.9.8.0 and 0.9.8.1. The layout
    /// is found the way the SDK's VhciClient finds it.</summary>
    static class Vhci
    {
        static readonly Guid Interface = new(0xB4030C06, 0xDC5F, 0x4FCC, 0x87, 0xEB, 0xE5, 0x51, 0x5A, 0x09, 0x35, 0xC0);
        const uint PLUGIN_HARDWARE = 0x0022E000, GET_IMPORTED_DEVICES = 0x0022E008, STOP_ATTACH_ATTEMPTS = 0x0022E014;

        // plugin_hardware, stop_attach_attempts and imported_device sizes,
        // and where busid starts in the location. stop_attach_attempts ends
        // with int count, the attempts it canceled.
        static readonly (int Plugin, int Stop, int Row, int BusId)[] Layouts =
        {
            (1124, 1108, 1132, 8),
            (1120, 1104, 1128, 4),
            (1100, 1104, 1108, 4),
        };

        public static bool Available() => Path() != null;

        static string? Path()
        {
            var g = Interface;
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref g, null, 0) != 0 || len <= 1) return null;
            var buf = new char[len];
            if (CM_Get_Device_Interface_ListW(ref g, null, buf, len, 0) != 0) return null;
            int end = Array.IndexOf(buf, '\0');
            return end > 0 ? new string(buf, 0, end) : null;
        }

        static IntPtr Open() =>
            Path() is string p ? Native.CreateFileW(p, Native.GENERIC_READ | Native.GENERIC_WRITE, 0, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero) : Native.INVALID;

        static (int Plugin, int Stop, int Row, int BusId)? Layout(IntPtr h, out byte[] rows, out uint written)
        {
            foreach (var l in Layouts)
            {
                var buf = new byte[4 + l.Row * 64];
                BitConverter.GetBytes((uint)(4 + l.Row)).CopyTo(buf, 0);
                if (DeviceIoControl(h, GET_IMPORTED_DEVICES, buf, buf.Length, buf, buf.Length, out written, IntPtr.Zero))
                {
                    rows = buf;
                    return l;
                }
            }
            rows = Array.Empty<byte>();
            written = 0;
            return null;
        }

        static void Location(byte[] buf, int at, string host, int port, string busId)
        {
            Encoding.ASCII.GetBytes(busId).CopyTo(buf, at);
            Encoding.ASCII.GetBytes(port.ToString()).CopyTo(buf, at + 32);
            Encoding.ASCII.GetBytes(host).CopyTo(buf, at + 64);
        }

        /// <summary>The imports: vhci port, server port and bus id.</summary>
        public static List<(int VhciPort, int Port, string BusId)> Imported()
        {
            var list = new List<(int, int, string)>();
            IntPtr h = Open();
            if (h == Native.INVALID) return list;
            try
            {
                if (Layout(h, out byte[] buf, out uint written) is not { } l) return list;
                for (int off = 4; off + l.Row <= written; off += l.Row)
                {
                    int vp = BitConverter.ToInt32(buf, off);
                    int at = off + l.BusId;
                    string bus = Z(buf, at, 32), service = Z(buf, at + 32, 32);
                    if (vp >= 1 && int.TryParse(service, out int port)) list.Add((vp, port, bus));
                }
            }
            finally { Native.CloseHandle(h); }
            return list;
        }

        /// <summary>STOP_ATTACH_ATTEMPTS for one location: the number of
        /// attempts it canceled, or -1 when the call failed.</summary>
        public static int Stop(string host, int port, string busId)
        {
            IntPtr h = Open();
            if (h == Native.INVALID) return -1;
            try
            {
                if (Layout(h, out _, out _) is not { } l) return -1;
                var buf = new byte[l.Stop];
                BitConverter.GetBytes((uint)l.Stop).CopyTo(buf, 0);
                Location(buf, 4 + l.BusId, host, port, busId);
                if (!DeviceIoControl(h, STOP_ATTACH_ATTEMPTS, buf, buf.Length, buf, buf.Length, out _, IntPtr.Zero)) return -1;
                return BitConverter.ToInt32(buf, l.Stop - 4);
            }
            finally { Native.CloseHandle(h); }
        }

        /// <summary>PLUGIN_HARDWARE, the attach that starts re-attach
        /// attempts when it fails. True when it attached.</summary>
        public static bool Attach(string host, int port, string busId)
        {
            IntPtr h = Open();
            if (h == Native.INVALID) return false;
            try
            {
                if (Layout(h, out _, out _) is not { } l) return false;
                var buf = new byte[l.Plugin];
                BitConverter.GetBytes((uint)l.Plugin).CopyTo(buf, 0);
                Location(buf, 4 + l.BusId, host, port, busId);
                return DeviceIoControl(h, PLUGIN_HARDWARE, buf, buf.Length, buf, buf.Length, out _, IntPtr.Zero);
            }
            finally { Native.CloseHandle(h); }
        }

        static string Z(byte[] b, int at, int max)
        {
            int end = Array.IndexOf(b, (byte)0, at, max);
            return Encoding.ASCII.GetString(b, at, (end < 0 ? at + max : end) - at);
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern uint CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid g, string? id, uint flags);
        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
        static extern uint CM_Get_Device_Interface_ListW(ref Guid g, string? id, [Out] char[] buf, uint len, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(IntPtr h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out uint returned, IntPtr overlapped);
    }
}
