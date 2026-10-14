// Shared by the Wii U GameCube adapter probes (S79 to S83): the real
// adapter's bytes, the checks' bookkeeping, and the Windows calls the probes
// use to look at the device from outside the SDK. The probes find and read
// the device through Windows' own USB stack, never through the SDK's
// internals, so a defect in the SDK cannot hide itself.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

internal static class GcReal
{
    // The device and configuration descriptors every one of the 57
    // linux-hardware.org dumps of 057E:0337 shows, with the 8-byte control
    // endpoint the 19 Nintendo-string units report (docs/design/v1.13.0.md).
    public static readonly byte[] DeviceDescriptor = Convert.FromHexString(
        "12010002000000087E053703000101020301");

    public static readonly byte[] ConfigurationDescriptor = Convert.FromHexString(
        "09022900010100E0FA" + "090400000203000000" + "09211001000122D600" +
        "07058103250008" + "07050203050008");

    // The same descriptor as usbip-win2 v0.9.8.1 hands a full-speed device's
    // on to Windows, which it presents at high speed: each interrupt
    // bInterval of 8 ms becomes 7, 2^(7-1) microframes, the same 8 ms
    // (drivers/ude/wsk_receive.cpp to_high_speed_interval, patch_config).
    public static readonly byte[] ConfigurationDescriptorAtHighSpeed = Convert.FromHexString(
        "09022900010100E0FA" + "090400000203000000" + "09211001000122D600" +
        "07058103250007" + "07050203050007");

    // The 214-byte report descriptor nine linux-hardware.org dumps print,
    // rebuilt item by item, and which HOJA-LIB-ESP32's
    // oem-gamecube-adapter-HID.txt and pico-rectangle carry.
    public static readonly byte[] ReportDescriptor = Convert.FromHexString(
        "05050900" +
        "A101851119002AFF00150026FF00750895059100C0" +
        "A101852119002AFF00150026FF00750895258100C0" +
        "A101851219002AFF00150026FF00750895019100C0" +
        "A101852219002AFF00150026FF00750895198100C0" +
        "A101851319002AFF00150026FF00750895019100C0" +
        "A101852319002AFF00150026FF00750895028100C0" +
        "A101851419002AFF00150026FF00750895019100C0" +
        "A101852419002AFF00150026FF00750895028100C0" +
        "A101851519002AFF00150026FF00750895019100C0" +
        "A101852519002AFF00150026FF00750895028100C0");

    // joypad-os src/usb/usbd/descriptors/gc_adapter_descriptors.h:125-168,
    // the descriptor of joypad-os's own adapter emulation.
    public static readonly byte[] JoypadOsReportDescriptor = Convert.FromHexString(
        "05010905A101" +
        "A103851119002AFF00150026FF00750895059100C0" +
        "A10385210600FF0901150026FF00750895248102C0" +
        "A103851319002AFF00150026FF00750895019100C0" +
        "C0");

    public const string Manufacturer = "Nintendo";
    public const string Product = "WUP-028";
    public const string Serial = "15/07/2014";
    public const string SerialInstance = @"USB\VID_057E&PID_0337\15/07/2014";

    /// <summary>The interface GUID of HIDMaestro's WinUSB binding.</summary>
    public static readonly Guid WinUsbInterface = new("7F216CFC-A59A-41F6-947D-E7BF407BCAD0");
}

internal static class Probe
{
    static int s_total, s_failures;

    public static int Failures => s_failures;

    public static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
        Console.Out.Flush();
    }

    public static int Finish(string title)
    {
        Console.WriteLine($"{title}: {s_total - s_failures}/{s_total} passed");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>Another program that has HIDMaestro.Core.dll loaded shares the
    /// machine-wide devices and would change what the probe reads.</summary>
    public static bool OtherSdkRunning()
    {
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                foreach (ProcessModule m in p.Modules)
                    if (string.Equals(m.ModuleName, "HIDMaestro.Core.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"  [SKIP] {p.ProcessName} ({p.Id}) is running and shares the SDK's devices.");
                        return true;
                    }
            }
            catch { }
            finally { p.Dispose(); }
        }
        return false;
    }

    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);

    /// <summary>Wait this many microseconds by spinning on the
    /// high-resolution clock. Thread.Sleep rounds to the system timer, which
    /// would bunch latency samples at a few offsets from a device's poll
    /// grid.</summary>
    public static void SpinMicros(long micros)
    {
        long end = Stopwatch.GetTimestamp() + micros * Stopwatch.Frequency / 1_000_000;
        while (Stopwatch.GetTimestamp() < end) Thread.SpinWait(64);
    }

    /// <summary>Poll <paramref name="cond"/> every 20 ms until it holds or
    /// <paramref name="timeoutMs"/> passes.</summary>
    public static bool WaitFor(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(20);
        }
        return cond();
    }
}

/// <summary>Devnodes, read through cfgmgr32.</summary>
internal static class Devnodes
{
    public const int CR_SUCCESS = 0;
    const uint CM_LOCATE_DEVNODE_NORMAL = 0, CM_LOCATE_DEVNODE_PHANTOM = 1;
    const uint CM_GETIDLIST_FILTER_ENUMERATOR = 1, CM_GETIDLIST_FILTER_PRESENT = 0x100;
    const uint CM_DRP_HARDWAREID = 0x02, CM_DRP_SERVICE = 0x05, CM_DRP_CONFIGFLAGS = 0x0B, CM_DRP_ADDRESS = 0x1D;

    /// <summary>Every present 057E:0337 devnode.</summary>
    public static List<string> PresentAdapters()
    {
        var list = new List<string>();
        foreach (var id in DeviceIds("USB", present: true))
            if (id.StartsWith(@"USB\VID_057E&PID_0337\", StringComparison.OrdinalIgnoreCase)) list.Add(id);
        return list;
    }

    /// <summary>Every 057E:0337 devnode, present or not.</summary>
    public static List<string> AllAdapters()
    {
        var list = new List<string>();
        foreach (var id in DeviceIds("USB", present: false))
            if (id.StartsWith(@"USB\VID_057E&PID_0337\", StringComparison.OrdinalIgnoreCase)) list.Add(id);
        return list;
    }

    /// <summary>The present adapter on usbip-win2's host controller at
    /// <paramref name="port"/>, as the vhci plugs a full-speed device in at
    /// root hub port number equal to its vhci port.</summary>
    public static string? AdapterOnVhciPort(int port)
    {
        foreach (var id in PresentAdapters())
        {
            if (CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) continue;
            if (Dword(dev, CM_DRP_ADDRESS) != (uint)port) continue;
            if (CM_Get_Parent(out uint hub, dev, 0) != CR_SUCCESS || CM_Get_Parent(out uint host, hub, 0) != CR_SUCCESS) continue;
            if (MultiSz(host, CM_DRP_HARDWAREID).Exists(h => h.Contains("USBIP_WIN2", StringComparison.OrdinalIgnoreCase)))
                return id;
        }
        return null;
    }

    public static bool IsPresent(string id) => CM_Locate_DevNodeW(out _, id, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS;

    public static string? Service(string id, bool phantom = false)
        => CM_Locate_DevNodeW(out uint dev, id, phantom ? CM_LOCATE_DEVNODE_PHANTOM : CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS
            ? Str(dev, CM_DRP_SERVICE) : null;

    public static string? InfPath(string id, bool phantom = false)
    {
        if (CM_Locate_DevNodeW(out uint dev, id, phantom ? CM_LOCATE_DEVNODE_PHANTOM : CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return null;
        var key = new DEVPROPKEY { fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 5 };
        uint len = 0;
        CM_Get_DevNode_PropertyW(dev, ref key, out _, null, ref len, 0);
        if (len == 0) return null;
        var buf = new byte[len];
        if (CM_Get_DevNode_PropertyW(dev, ref key, out _, buf, ref len, 0) != CR_SUCCESS) return null;
        return Encoding.Unicode.GetString(buf, 0, (int)len).TrimEnd('\0');
    }

    public static uint? ConfigFlags(string id)
        => CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_PHANTOM) == CR_SUCCESS ? Dword(dev, CM_DRP_CONFIGFLAGS) : null;

    public static int SetConfigFlags(string id, uint flags)
    {
        if (CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_PHANTOM) != CR_SUCCESS) return -1;
        var b = BitConverter.GetBytes(flags);
        return CM_Set_DevNode_Registry_PropertyW(dev, CM_DRP_CONFIGFLAGS, b, 4, 0);
    }

    /// <summary>(status, problem) of a present devnode.</summary>
    public static (uint Status, uint Problem)? Status(string id)
    {
        if (CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return null;
        return CM_Get_DevNode_Status(out uint s, out uint p, dev, 0) == CR_SUCCESS ? (s, p) : null;
    }

    public static string? ParentId(string id)
    {
        if (CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS) return null;
        if (CM_Get_Parent(out uint parent, dev, 0) != CR_SUCCESS) return null;
        var buf = new char[512];
        return CM_Get_Device_IDW(parent, buf, (uint)buf.Length, 0) == CR_SUCCESS ? new string(buf).TrimEnd('\0') : null;
    }

    public static uint? Address(string id)
        => CM_Locate_DevNodeW(out uint dev, id, CM_LOCATE_DEVNODE_NORMAL) == CR_SUCCESS ? Dword(dev, CM_DRP_ADDRESS) : null;

    static List<string> DeviceIds(string enumerator, bool present)
    {
        uint flags = CM_GETIDLIST_FILTER_ENUMERATOR | (present ? CM_GETIDLIST_FILTER_PRESENT : 0);
        var list = new List<string>();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out uint len, enumerator, flags) != CR_SUCCESS || len == 0) return list;
            var buf = new char[len];
            int cr = CM_Get_Device_ID_ListW(enumerator, buf, len, flags);
            if (cr == 0x1A) continue;
            if (cr != CR_SUCCESS) return list;
            foreach (var s in new string(buf).Split('\0')) if (s.Length > 0) list.Add(s);
            return list;
        }
        return list;
    }

    static uint? Dword(uint dev, uint prop)
    {
        var b = new byte[4];
        uint len = 4;
        return CM_Get_DevNode_Registry_PropertyW(dev, prop, out _, b, ref len, 0) == CR_SUCCESS && len >= 4 ? BitConverter.ToUInt32(b) : null;
    }

    static string? Str(uint dev, uint prop)
    {
        uint len = 0;
        CM_Get_DevNode_Registry_PropertyW(dev, prop, out _, null, ref len, 0);
        if (len == 0) return null;
        var b = new byte[len];
        return CM_Get_DevNode_Registry_PropertyW(dev, prop, out _, b, ref len, 0) == CR_SUCCESS
            ? Encoding.Unicode.GetString(b, 0, (int)len).TrimEnd('\0') : null;
    }

    static List<string> MultiSz(uint dev, uint prop)
        => new((Str(dev, prop) ?? "").Split('\0', StringSplitOptions.RemoveEmptyEntries));

    [StructLayout(LayoutKind.Sequential)] struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint dn, string id, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_Parent(out uint parent, uint dn, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint dn, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_IDW(uint dn, char[] buf, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_Registry_PropertyW(uint dn, uint prop, out uint type, byte[]? buf, ref uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Set_DevNode_Registry_PropertyW(uint dn, uint prop, byte[] buf, uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_DevNode_PropertyW(uint dn, ref DEVPROPKEY key, out uint type, byte[]? buf, ref uint len, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_SizeW(out uint len, string filter, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_ListW(string filter, char[] buf, uint len, uint flags);
}

/// <summary>A USB hub's view of one of its ports, through the hub driver's
/// own IOCTLs. Needs no driver on the device itself.</summary>
internal sealed class HubPort : IDisposable
{
    const uint IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION = 0x220410;
    const uint IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX = 0x220448;
    static readonly Guid GUID_DEVINTERFACE_USB_HUB = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");

    readonly SafeFileHandle _hub;
    public int Port { get; }

    /// <summary>The Win32 error of the last failed request, 0 after a success.</summary>
    public int LastError { get; private set; }

    HubPort(SafeFileHandle hub, int port) { _hub = hub; Port = port; }

    /// <summary>The hub port a present device sits on.</summary>
    public static HubPort? ForDevice(string instanceId)
    {
        string? hubId = Devnodes.ParentId(instanceId);
        uint? port = Devnodes.Address(instanceId);
        if (hubId == null || port == null) return null;
        string? path = InterfacePath(GUID_DEVINTERFACE_USB_HUB, hubId);
        if (path == null) return null;
        var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        return new HubPort(h, (int)port.Value);
    }

    /// <summary>A GET_DESCRIPTOR the hub sends the device. Null when the
    /// device stalls it or the hub fails it.</summary>
    public byte[]? GetDescriptor(byte type, byte index, ushort langId, ushort length, byte bmRequest = 0x80)
    {
        var buf = new byte[12 + length];
        BitConverter.GetBytes((uint)Port).CopyTo(buf, 0);
        buf[4] = bmRequest;
        buf[5] = 0x06;
        BitConverter.GetBytes((ushort)((type << 8) | index)).CopyTo(buf, 6);
        BitConverter.GetBytes(langId).CopyTo(buf, 8);
        BitConverter.GetBytes(length).CopyTo(buf, 10);
        if (!DeviceIoControl(_hub, IOCTL_USB_GET_DESCRIPTOR_FROM_NODE_CONNECTION, buf, (uint)buf.Length, buf, (uint)buf.Length, out uint got, IntPtr.Zero))
        {
            LastError = Marshal.GetLastWin32Error();
            return null;
        }
        LastError = 0;
        if (got < 12) return null;
        return buf.AsSpan(12, (int)got - 12).ToArray();
    }

    /// <summary>The connection's speed as the hub reports it: 0 low, 1 full,
    /// 2 high, 3 super.</summary>
    public int? Speed()
    {
        var buf = new byte[35 + 11 * 32];
        BitConverter.GetBytes((uint)Port).CopyTo(buf, 0);
        if (!DeviceIoControl(_hub, IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX, buf, (uint)buf.Length, buf, (uint)buf.Length, out _, IntPtr.Zero))
            return null;
        return buf[23];
    }

    public void Dispose() => _hub.Dispose();

    public static string? InterfacePath(Guid guid, string instanceId)
    {
        IntPtr set = SetupDiGetClassDevsW(ref guid, instanceId, IntPtr.Zero, 0x12 /* PRESENT | DEVICEINTERFACE */);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, 0, ref data)) return null;
            SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int need, IntPtr.Zero);
            IntPtr detail = Marshal.AllocHGlobal(need);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, need, out _, IntPtr.Zero)) return null;
                return Marshal.PtrToStringUni(detail + 4);
            }
            finally { Marshal.FreeHGlobal(detail); }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevsW(ref Guid g, string? enumerator, IntPtr h, int f);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr s, IntPtr d, ref Guid g, int i, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr s, ref SP_DEVICE_INTERFACE_DATA d, IntPtr detail, int size, out int need, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr s);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFileW(string p, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, uint inLen, byte[] outBuf, uint outLen, out uint got, IntPtr ov);
}

/// <summary>A WinUSB handle on the adapter, as libusb opens one.</summary>
internal sealed class WinUsbDevice : IDisposable
{
    readonly SafeFileHandle _file;
    readonly IntPtr _h;

    WinUsbDevice(SafeFileHandle file, IntPtr h) { _file = file; _h = h; }

    public static WinUsbDevice? Open(string instanceId)
    {
        string? path = HubPort.InterfacePath(GcReal.WinUsbInterface, instanceId);
        if (path == null) return null;
        var file = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000080, IntPtr.Zero);
        if (file.IsInvalid) return null;
        if (!WinUsb_Initialize(file, out IntPtr h)) { file.Dispose(); return null; }
        return new WinUsbDevice(file, h);
    }

    /// <summary>WinUSB's per-pipe transfer timeout, in ms. 0 waits forever.</summary>
    public void SetTimeout(byte pipe, uint ms) => WinUsb_SetPipePolicy(_h, pipe, 3 /* PIPE_TRANSFER_TIMEOUT */, 4, ref ms);

    /// <summary>A control transfer. Returns the bytes read for an IN
    /// transfer, an empty array for an OUT one, or null with the Win32 error
    /// in <paramref name="error"/>.</summary>
    public byte[]? Control(byte requestType, byte request, ushort value, ushort index, ushort length, byte[]? outData, out int error)
    {
        var setup = new WINUSB_SETUP_PACKET { RequestType = requestType, Request = request, Value = value, Index = index, Length = length };
        var buf = outData ?? new byte[length];
        bool ok = WinUsb_ControlTransfer(_h, setup, buf, (uint)buf.Length, out uint got, IntPtr.Zero);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        if (!ok) return null;
        return (requestType & 0x80) != 0 ? buf.AsSpan(0, (int)got).ToArray() : Array.Empty<byte>();
    }

    public byte[]? Read(byte pipe, int length, out int error)
    {
        var buf = new byte[length];
        bool ok = WinUsb_ReadPipe(_h, pipe, buf, (uint)length, out uint got, IntPtr.Zero);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok ? buf.AsSpan(0, (int)got).ToArray() : null;
    }

    public bool Write(byte pipe, byte[] data, out int error)
    {
        bool ok = WinUsb_WritePipe(_h, pipe, data, (uint)data.Length, out _, IntPtr.Zero);
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    /// <summary>Clear a pipe's halt, as libusb's reset does on WinUSB
    /// (windows_winusb.c winusbx_reset_device: abort, flush, reset).</summary>
    public bool ResetPipe(byte pipe)
    {
        WinUsb_AbortPipe(_h, pipe);
        WinUsb_FlushPipe(_h, pipe);
        return WinUsb_ResetPipe(_h, pipe);
    }

    public void Dispose()
    {
        WinUsb_Free(_h);
        _file.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct WINUSB_SETUP_PACKET { public byte RequestType; public byte Request; public ushort Value; public ushort Index; public ushort Length; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFileW(string p, uint a, uint s, IntPtr sa, uint c, uint f, IntPtr t);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_Initialize(SafeFileHandle f, out IntPtr h);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_Free(IntPtr h);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_SetPipePolicy(IntPtr h, byte pipe, uint policy, uint len, ref uint value);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_ReadPipe(IntPtr h, byte pipe, byte[] buf, uint len, out uint got, IntPtr ov);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_WritePipe(IntPtr h, byte pipe, byte[] buf, uint len, out uint wrote, IntPtr ov);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_ControlTransfer(IntPtr h, WINUSB_SETUP_PACKET setup, byte[] buf, uint len, out uint got, IntPtr ov);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_AbortPipe(IntPtr h, byte pipe);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_FlushPipe(IntPtr h, byte pipe);
    [DllImport("winusb.dll", SetLastError = true)] static extern bool WinUsb_ResetPipe(IntPtr h, byte pipe);
}

/// <summary>One native harness process (S82's emulator harnesses, S83's SDL
/// harness): a command per line in, an answer per line out, and the program's
/// own log on stderr.</summary>
internal sealed class Harness : IDisposable
{
    readonly Process _p;
    readonly System.Collections.Concurrent.BlockingCollection<string> _lines = new();
    readonly StringBuilder _log = new();

    Harness(Process p)
    {
        _p = p;
        _p.OutputDataReceived += (_, e) => { if (e.Data != null) _lines.Add(e.Data); };
        _p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_log) _log.AppendLine(e.Data); };
        _p.BeginOutputReadLine();
        _p.BeginErrorReadLine();
    }

    public static Harness Start(string exe, params string[] arguments)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        return new Harness(Process.Start(psi)!);
    }

    public string? Ask(string command, int timeoutMs = 5000)
    {
        _p.StandardInput.WriteLine(command);
        _p.StandardInput.Flush();
        return Next(timeoutMs);
    }

    public string? Next(int timeoutMs) => _lines.TryTake(out var line, timeoutMs) ? line : null;

    public string FullLog()
    {
        lock (_log) return _log.ToString();
    }

    /// <summary>The last lines of the program's log, for a failing run.</summary>
    public string TakeLog(int lines)
    {
        string[] all;
        lock (_log) all = _log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        for (int i = Math.Max(0, all.Length - lines); i < all.Length; i++) sb.Append("    | ").AppendLine(all[i].TrimEnd('\r'));
        return sb.ToString();
    }

    public void Dispose()
    {
        try
        {
            if (!_p.HasExited)
            {
                _p.StandardInput.WriteLine("quit");
                _p.StandardInput.Flush();
                if (!_p.WaitForExit(5000)) _p.Kill();
            }
        }
        catch (InvalidOperationException) { }
        catch (System.IO.IOException) { }
        _p.Dispose();
    }
}
