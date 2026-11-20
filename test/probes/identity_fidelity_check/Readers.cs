// What the programs that name a controller read for one device: the HID
// class, XInput, Windows.Gaming.Input and SDL, each as one line a device, and
// the id Chrome builds from them.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using HIDMaestro;

using Windows.Gaming.Input;

/// <summary>One device as each reader names it. Every list is sorted, so two
/// readings of the same device compare line for line. Create holds one line
/// when the create failed, as a profile whose descriptor Windows refuses
/// fails on the real device too, and nothing when it returned a
/// controller.</summary>
internal sealed record Reading(List<string> Create, List<string> Hid, List<string> XInput, List<string> Wgi, List<string> Sdl, List<string> Chrome)
{
    public static readonly string[] Readers = { "create", "hid", "xinput", "wgi", "sdl", "chrome" };

    public List<string> Of(string reader) => reader switch
    {
        "create" => Create, "hid" => Hid, "xinput" => XInput, "wgi" => Wgi, "sdl" => Sdl, "chrome" => Chrome,
        _ => throw new ArgumentException(reader),
    };

    /// <summary>The reading of a create that threw: the device's problem
    /// code when Windows refused its start, else the exception's type.</summary>
    public static Reading Failed(Exception e)
    {
        var m = System.Text.RegularExpressions.Regex.Match(e.Message, @"failed its start, problem code (\d+)");
        string line = m.Success ? $"fails its start, problem code {m.Groups[1].Value}" : $"fails: {e.GetType().Name}";
        return new Reading(new() { line }, new(), new(), new(), new(), new());
    }

    public bool Same(Reading other) => Readers.All(r => Of(r).SequenceEqual(other.Of(r)));

    /// <summary>The reading as the pins file holds it: one line a reader's
    /// entry, "reader value".</summary>
    public IEnumerable<(string Reader, string Value)> Lines()
    {
        foreach (string r in Readers)
            foreach (string v in Of(r)) yield return (r, v);
    }
}

internal static class Sdl
{
    const string L = "SDL3";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetDllDirectoryW(string path);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);

    [StructLayout(LayoutKind.Sequential)] struct SDL_GUID { public ulong Lo, Hi; }

    [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_Init(uint flags);
    [DllImport(L)] static extern void SDL_Quit();
    [DllImport(L)] static extern IntPtr SDL_GetRevision();
    [DllImport(L)] static extern int SDL_GetVersion();
    [DllImport(L)] static extern void SDL_PumpEvents();
    [DllImport(L)] static extern IntPtr SDL_GetError();
    [DllImport(L)] static extern void SDL_free(IntPtr p);
    [DllImport(L)] static extern IntPtr SDL_GetJoysticks(out int count);
    [DllImport(L)] static extern IntPtr SDL_GetJoystickNameForID(uint id);
    [DllImport(L)] static extern SDL_GUID SDL_GetJoystickGUIDForID(uint id);
    [DllImport(L)] static extern ushort SDL_GetJoystickVendorForID(uint id);
    [DllImport(L)] static extern ushort SDL_GetJoystickProductForID(uint id);
    [DllImport(L)] static extern ushort SDL_GetJoystickProductVersionForID(uint id);
    [DllImport(L)] static extern int SDL_GetJoystickTypeForID(uint id);
    [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_IsGamepad(uint id);
    [DllImport(L)] static extern int SDL_GetGamepadTypeForID(uint id);
    [DllImport(L)] static extern int SDL_GetRealGamepadTypeForID(uint id);
    [DllImport(L)] static extern IntPtr SDL_GetGamepadStringForType(int type);
    [DllImport(L)] static extern IntPtr SDL_OpenJoystick(uint id);
    [DllImport(L)] static extern void SDL_CloseJoystick(IntPtr j);
    [DllImport(L)] static extern int SDL_GetJoystickConnectionState(IntPtr j);

    const uint SDL_INIT_JOYSTICK = 0x00000200, SDL_INIT_GAMEPAD = 0x00002000;

    static string? S(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

    public static bool Ready { get; private set; }
    public static string Version { get; private set; } = "";

    /// <summary>Loads the SDL3.dll in <paramref name="dir"/> and starts its
    /// joystick and gamepad subsystems with SDL's default hints, so the
    /// backend SDL picks for a device is the one an ordinary SDL program
    /// gets in this session. False with the reason when it cannot.</summary>
    public static bool Start(string dir, out string why)
    {
        why = "";
        dir = Path.GetFullPath(dir);
        if (!File.Exists(Path.Combine(dir, "SDL3.dll"))) { why = $"no SDL3.dll in {dir}"; return false; }
        string libusb = Path.Combine(dir, "libusb-1.0.dll");
        if (File.Exists(libusb)) LoadLibraryW(libusb);
        SetDllDirectoryW(dir);
        int v = SDL_GetVersion();
        Version = $"{v / 1000000}.{v / 1000 % 1000}.{v % 1000} {S(SDL_GetRevision())}";
        if (!SDL_Init(SDL_INIT_JOYSTICK | SDL_INIT_GAMEPAD)) { why = $"SDL_Init failed: {S(SDL_GetError())}"; return false; }
        Ready = true;
        return true;
    }

    public static void Stop()
    {
        if (Ready) SDL_Quit();
        Ready = false;
    }

    /// <summary>Lets SDL handle its window's messages and the arrivals and
    /// removals of its devices.</summary>
    public static void Pump()
    {
        if (Ready) SDL_PumpEvents();
    }

    /// <summary>The instance ids of the joysticks SDL lists now.</summary>
    public static HashSet<uint> Ids()
    {
        var set = new HashSet<uint>();
        if (!Ready) return set;
        SDL_PumpEvents();
        IntPtr list = SDL_GetJoysticks(out int n);
        for (int i = 0; i < n; i++) set.Add((uint)Marshal.ReadInt32(list, i * 4));
        if (list != IntPtr.Zero) SDL_free(list);
        return set;
    }

    /// <summary>One joystick as SDL names it: the name, the IDs, the bus and
    /// the driver its GUID carries (SDL_CreateJoystickGUID: bytes 0-1 the
    /// bus, byte 14 the driver), its types, and the connection state once
    /// opened.</summary>
    public static string Describe(uint id)
    {
        var guid = SDL_GetJoystickGUIDForID(id);
        ushort bus = (ushort)(guid.Lo & 0xFFFF);
        byte driver = (byte)((guid.Hi >> 48) & 0xFF);
        string busName = bus switch { 0x03 => "USB", 0x05 => "Bluetooth", 0x06 => "virtual", 0x00 => "unknown", _ => $"0x{bus:X2}" };
        string gamepad = SDL_IsGamepad(id)
            ? $"{S(SDL_GetGamepadStringForType(SDL_GetGamepadTypeForID(id)))}/{S(SDL_GetGamepadStringForType(SDL_GetRealGamepadTypeForID(id)))}"
            : "no";
        string connection = "not opened";
        IntPtr j = SDL_OpenJoystick(id);
        if (j != IntPtr.Zero)
        {
            int cs = SDL_GetJoystickConnectionState(j);
            connection = cs == 1 ? "wired" : cs == 2 ? "wireless" : cs == 0 ? "unknown" : "invalid";
            SDL_CloseJoystick(j);
        }
        return $"\"{S(SDL_GetJoystickNameForID(id))}\" {SDL_GetJoystickVendorForID(id):X4}:{SDL_GetJoystickProductForID(id):X4} version {SDL_GetJoystickProductVersionForID(id):X4} " +
               $"bus {busName} driver {(driver >= 32 && driver < 127 ? ((char)driver).ToString() : $"0x{driver:X2}")} type {SDL_GetJoystickTypeForID(id)} gamepad {gamepad} connection {connection}";
    }
}

internal static class XInput
{
    [StructLayout(LayoutKind.Sequential)] struct XGamepad { public ushort Buttons; public byte LT, RT; public short LX, LY, RX, RY; }
    [StructLayout(LayoutKind.Sequential)] struct XVibration { public ushort L, R; }
    [StructLayout(LayoutKind.Sequential)] struct XCaps { public byte Type, SubType; public ushort Flags; public XGamepad Pad; public XVibration Vib; }
    [StructLayout(LayoutKind.Sequential)] struct XCapsEx { public XCaps Caps; public ushort Vid, Pid, Rev; public uint A4; }
    [DllImport("xinput1_4.dll")] static extern uint XInputGetCapabilities(uint index, uint flags, out XCaps caps);
    [DllImport("xinput1_4.dll", EntryPoint = "#108")] static extern uint XInputGetCapabilitiesEx(uint one, uint index, uint flags, out XCapsEx caps);

    /// <summary>The slots XInput lists a controller in, as a bit mask.</summary>
    public static uint Mask()
    {
        uint mask = 0;
        for (uint i = 0; i < 4; i++)
            if (XInputGetCapabilities(i, 0, out _) == 0) mask |= 1u << (int)i;
        return mask;
    }

    /// <summary>One slot as XInput reports it: type, subtype, the wireless
    /// flag, and the VID, PID and revision the extended call gives (ordinal
    /// 108, which SDL's XInput backend names a pad from).</summary>
    public static string Describe(uint slot)
    {
        if (XInputGetCapabilities(slot, 0, out var c) != 0) return "gone";
        string ex = XInputGetCapabilitiesEx(1, slot, 0, out var cx) == 0 ? $"{cx.Vid:X4}:{cx.Pid:X4} revision {cx.Rev:X4}" : "no extended capabilities";
        return $"type {c.Type} subtype {c.SubType} {((c.Flags & 2) != 0 ? "wireless" : "wired")} {ex}";
    }
}

internal static class Readers
{
    /// <summary>What each reader lists before a controller is created, so
    /// what it lists after is the controller's.</summary>
    public sealed record Baseline(uint XInputMask, HashSet<string> WgiIds, HashSet<uint> SdlIds);

    public static Baseline Take() =>
        new(XInput.Mask(), WgiPins.Raws().Select(r => r.NonRoamableId).ToHashSet(StringComparer.Ordinal), Sdl.Ids());

    /// <summary>Every device Windows.Gaming.Input listed at the last read,
    /// those the baseline held marked: what a failed comparison prints.</summary>
    public static List<string> LastWgi { get; private set; } = new();

    static string DescribeWgi(RawGameController r) =>
        $"\"{r.DisplayName}\" {r.HardwareVendorId:X4}:{r.HardwareProductId:X4} {(r.IsWireless ? "wireless" : "wired")} gamepad {(WgiPins.PadOf(r) != null ? "yes" : "no")} " +
        $"buttons {r.ButtonCount} axes {r.AxisCount} switches {r.SwitchCount}";

    /// <summary>The bus hidapi reads from a node's compatible IDs: each ID
    /// in order, upper-cased, and in each the tests in hidapi's order (SDL
    /// 3.4.18 src/hidapi/windows/hid.c:769-807). No compatible IDs, or none
    /// that names a bus, reads as unknown.</summary>
    public static string HidapiBus(IEnumerable<string> compatibleIds)
    {
        foreach (string id in compatibleIds)
        {
            string up = id.ToUpperInvariant();
            if (up.Contains("USB", StringComparison.Ordinal)) return "USB";
            if (up.Contains("BTHENUM", StringComparison.Ordinal)) return "Bluetooth";
            if (up.Contains("BTHLEDEVICE", StringComparison.Ordinal)) return "Bluetooth LE";
            if (up.Contains("PNP0C50", StringComparison.Ordinal)) return "I2C";
            if (up.Contains("PNP0C51", StringComparison.Ordinal)) return "SPI";
        }
        return "unknown";
    }

    /// <summary>One HID collection as the HID class and hidapi give it: the
    /// attributes, the top-level usage, the product string, the bus hidapi
    /// reads from its parent, and whether its path carries IG_, which
    /// hidapi, SDL and Chrome's Raw Input reader take for an XInput device
    /// and leave to XInput.</summary>
    static string DescribeHid(Collection c) =>
        $"{c.Vid:X4}:{c.Pid:X4} version {c.Version:X4} usage {c.UsagePage:X4}:{c.Usage:X4} product \"{c.Product}\" bus {HidapiBus(Nodes.ParentCompatibleIds(c.Path))}" +
        (c.Path.Contains("IG_", StringComparison.OrdinalIgnoreCase) ? " IG_" : "");

    /// <summary>What every reader lists for <paramref name="parent"/>'s
    /// device beyond <paramref name="before"/>. Reads until
    /// <paramref name="settled"/> accepts a reading, or, with
    /// <paramref name="stableMs"/>, until the device has a HID collection
    /// and the reading has stayed the same that long, or
    /// <paramref name="budgetMs"/> has passed, and at least
    /// <paramref name="minimumMs"/>: Windows.Gaming.Input and SDL list a
    /// device some hundred milliseconds after its HID collection opens, and
    /// a composite's interfaces start after its create returns.</summary>
    public static Reading Read(string parent, Baseline before, int minimumMs, int budgetMs, Func<Reading, bool>? settled, int stableMs = 0)
    {
        var sw = Stopwatch.StartNew();
        Reading? last = null;
        long sameSince = 0;
        while (true)
        {
            var hid = Hid.Read(parent, out _).Select(DescribeHid).OrderBy(x => x, StringComparer.Ordinal).ToList();
            uint mask = XInput.Mask() & ~before.XInputMask;
            var xinput = Enumerable.Range(0, 4).Where(i => (mask & (1u << i)) != 0).Select(i => XInput.Describe((uint)i))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            var listed = WgiPins.Raws();
            LastWgi = listed.Select(r => DescribeWgi(r) + (before.WgiIds.Contains(r.NonRoamableId) ? " (listed before the create)" : "")).ToList();
            var raws = listed.Where(r => !before.WgiIds.Contains(r.NonRoamableId)).ToList();
            var wgi = raws.Select(DescribeWgi).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var sdl = Sdl.Ids().Where(id => !before.SdlIds.Contains(id)).Select(Sdl.Describe).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var chrome = Chromium.Ids(raws, Hid.Read(parent, out _)).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var now = new Reading(new(), hid, xinput, wgi, sdl, chrome);
            if (last == null || !now.Same(last)) sameSince = sw.ElapsedMilliseconds;
            last = now;
            if (sw.ElapsedMilliseconds >= minimumMs)
            {
                if (settled != null && settled(last)) break;
                if (stableMs > 0 && last.Hid.Count > 0 && sw.ElapsedMilliseconds - sameSince >= stableMs) break;
            }
            if (sw.ElapsedMilliseconds >= budgetMs) break;
            Thread.Sleep(50);
        }
        return last;
    }

    /// <summary>Waits until XInput and SDL list nothing beyond
    /// <paramref name="before"/> again. XInput hands a freed slot to the
    /// next pad, and SDL keeps the joystick of a slot it never saw empty,
    /// so a pad created before both dropped the last one would be hidden
    /// by the baseline or read as the pad before it.</summary>
    public static bool Dropped(Baseline before, int budgetMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            if ((XInput.Mask() & ~before.XInputMask) == 0 && Sdl.Ids().All(before.SdlIds.Contains)) return true;
            Thread.Sleep(20);
        }
        return false;
    }
}

/// <summary>The id Chrome gives a pad, computed as Chromium computes it from
/// what Windows reports, with Chromium's own tables
/// (chromium-gamepad-ids.txt).</summary>
internal static class Chromium
{
    static readonly Dictionary<(ushort Vid, ushort Pid), (string XInputType, bool Mapped)> s_ids = new();
    public static string Source { get; private set; } = "";

    public static void Load(string path)
    {
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#'))
            {
                if (Source.Length == 0) Source = line.TrimStart('#', ' ');
                continue;
            }
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) continue;
            var id = parts[0].Split(':');
            s_ids[(Convert.ToUInt16(id[0], 16), Convert.ToUInt16(id[1], 16))] = (parts[1], parts[2] != "-");
        }
    }

    public static int Count => s_ids.Count;

    /// <summary>The ids of one device. A Windows.Gaming.Input Gamepad gets
    /// its id from WgiDataFetcherWin::BuildGamepadIdString: the display name
    /// alone for IDs Chromium does not know, the display name with the IDs
    /// for a known pad that is no XInput device, and the fixed XInput id for
    /// one that is. A HID game controller whose path carries no IG_ gets its
    /// id from RawInputDataFetcher and
    /// GamepadDataFetcher::UpdateGamepadStrings: the product string and the
    /// IDs, with STANDARD GAMEPAD when Chromium has a mapping for them.</summary>
    public static IEnumerable<string> Ids(IEnumerable<RawGameController> raws, IEnumerable<Collection> collections)
    {
        foreach (var r in raws)
        {
            if (WgiPins.PadOf(r) == null) continue;
            ushort vid = r.HardwareVendorId, pid = r.HardwareProductId;
            if (!s_ids.TryGetValue((vid, pid), out var known)) yield return $"wgi: {r.DisplayName} (STANDARD GAMEPAD)";
            else if (known.XInputType == "None") yield return $"wgi: {r.DisplayName} (STANDARD GAMEPAD Vendor: {vid:x4} Product: {pid:x4})";
            else yield return "wgi: Xbox 360 Controller (XInput STANDARD GAMEPAD)";
        }
        foreach (var c in collections)
        {
            // RawInputGamepadDeviceWin::IsGamepadUsageId: joystick, game pad
            // or multi-axis controller on the Generic Desktop page.
            if (c.UsagePage != 0x01 || c.Usage is not (0x04 or 0x05 or 0x08)) continue;
            if (c.Path.Contains("IG_", StringComparison.OrdinalIgnoreCase)) continue;
            bool mapped = s_ids.TryGetValue((c.Vid, c.Pid), out var known) && known.Mapped;
            string name = string.IsNullOrEmpty(c.Product) ? "Unknown Gamepad" : c.Product!;
            yield return $"rawinput: {name} ({(mapped ? "STANDARD GAMEPAD " : "")}Vendor: {c.Vid:x4} Product: {c.Pid:x4})";
        }
    }
}

/// <summary>Device tree reads the HID reader does not have.</summary>
internal static class Nodes
{
    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_PropertyW(string path, ref DEVPROPKEY key, out uint type, byte[]? buf, ref uint size, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint dn, string id, uint flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint parent, uint dn, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_DevNode_PropertyW(uint dn, ref DEVPROPKEY key, out uint type, byte[]? buf, ref uint size, uint flags);

    static readonly DEVPROPKEY s_instanceId = new() { fmtid = new Guid("78c34fc8-104a-4aca-9ea4-524d52996e57"), pid = 256 };
    static readonly DEVPROPKEY s_compatibleIds = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 4 };

    /// <summary>The compatible IDs of the parent of the devnode behind a HID
    /// interface path, the property hidapi reads for the bus, in the order
    /// Windows holds them.</summary>
    public static string[] ParentCompatibleIds(string path)
    {
        var key = s_instanceId;
        uint size = 0;
        CM_Get_Device_Interface_PropertyW(path, ref key, out _, null, ref size, 0);
        if (size == 0) return Array.Empty<string>();
        var buf = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(path, ref key, out _, buf, ref size, 0) != 0) return Array.Empty<string>();
        string id = Encoding.Unicode.GetString(buf).TrimEnd('\0');
        if (CM_Locate_DevNodeW(out uint dn, id, 0) != 0 || CM_Get_Parent(out uint parent, dn, 0) != 0) return Array.Empty<string>();
        key = s_compatibleIds;
        size = 0;
        CM_Get_DevNode_PropertyW(parent, ref key, out _, null, ref size, 0);
        if (size == 0) return Array.Empty<string>();
        buf = new byte[size];
        if (CM_Get_DevNode_PropertyW(parent, ref key, out _, buf, ref size, 0) != 0) return Array.Empty<string>();
        return Encoding.Unicode.GetString(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }
}
