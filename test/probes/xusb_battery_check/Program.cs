// XUSB battery reply reads as wired and full (issue #61). Elevation required.
//
// A virtual Xbox 360 reported a dead battery to every XInput caller, because
// IOCTL_XUSB_GET_BATTERY_INFO packed the type and level at bytes 1 and 2 when
// the reply carries a two-byte version word first. Callers copy from byte 2,
// so the pair decoded as NIMH at EMPTY, and SDL maps that to on-battery at 10
// percent: a low-battery warning the moment the pad appears.
//
// Two reads per cycle, because each catches a different regression:
//
//   1. The raw four bytes off the XUSB interface, asserted position by
//      position. A future edit that moves the pair, or that answers with a
//      short buffer, fails here with the bytes printed.
//   2. XInputGetBatteryInformation, which is what a game actually calls. It
//      reads the type and level from bytes 2 and 3, so this is the assertion
//      that the consumer-visible answer is WIRED and FULL rather than the
//      byte layout the driver happens to write.
//
// The SDL mapping the issue names is asserted as pure arithmetic over the
// values read back, since it is a switch in SDL_xinputjoystick.c with no
// device state of its own: WIRED becomes CHARGING, FULL becomes 100 percent.
//
// Runs on xbox-360-wired, the XUSB-companion family this reply serves. Three
// cycles, because the XUSB interface arrives asynchronously and a single
// create could pass on a stale handle from a prior life.
//
// Exit 0 PASS, 1 FAIL, 2 environment (not elevated).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;

internal static class Program
{
    // OpenXInput's OpenXInput.h and SDL's SDL_xinput.h agree on these.
    const byte BATTERY_TYPE_DISCONNECTED = 0x00;
    const byte BATTERY_TYPE_WIRED = 0x01;
    const byte BATTERY_TYPE_ALKALINE = 0x02;
    const byte BATTERY_TYPE_NIMH = 0x03;
    const byte BATTERY_TYPE_UNKNOWN = 0xFF;
    const byte BATTERY_LEVEL_EMPTY = 0x00;
    const byte BATTERY_LEVEL_FULL = 0x03;

    const uint IOCTL_XUSB_GET_BATTERY_INFO = 0x8000E018;
    const uint IOCTL_XUSB_GET_LED_STATE = 0x8000E008;

    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static int Main()
    {
        Console.WriteLine("=== XUSB battery reads as wired and full (issue #61) ===");
        if (!IsElevated())
        {
            Console.WriteLine("  not elevated; this probe creates a device");
            return 2;
        }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var profile = ctx.GetProfile("xbox-360-wired");
        if (profile == null) { Console.WriteLine("  xbox-360-wired missing"); return 1; }
        ctx.InstallDriver();

        var slotsBefore = XInputSlots();
        var xusbBefore = XusbInterfaces();

        for (int cycle = 1; cycle <= 3; cycle++)
        {
            Console.WriteLine();
            Console.WriteLine($"-- cycle {cycle} --");
            HMController? c = null;
            try
            {
                c = ctx.CreateController(profile);

                // The companion's XUSB interface, isolated from anything on the
                // bench by taking the one that was not there before the create.
                // It arrives when the companion's driver host starts the device,
                // which other work on the machine slows, so the wait is a 30 s
                // hang bound on the clock.
                string? iface = null;
                var arrival = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    iface = XusbInterfaces().FirstOrDefault(p => !xusbBefore.Contains(p, StringComparer.OrdinalIgnoreCase));
                    if (iface != null || arrival.ElapsedMilliseconds > 30_000) break;
                    Thread.Sleep(100);
                }
                Check("the companion published an XUSB interface", iface != null);
                if (iface == null) continue;

                // 1. The raw reply, byte by byte.
                var raw = Ioctl(iface, IOCTL_XUSB_GET_BATTERY_INFO, 4, out uint returned, out int err);
                Check("GET_BATTERY_INFO answers four bytes", raw != null && returned == 4,
                      raw == null ? $"DeviceIoControl failed, err={err}" : $"returned={returned}");
                if (raw != null && returned == 4)
                {
                    Console.WriteLine($"     raw reply: {BitConverter.ToString(raw)}");
                    Check("byte 2 is the battery type, and it is WIRED", raw[2] == BATTERY_TYPE_WIRED,
                          $"0x{raw[2]:X2} ({TypeName(raw[2])})");
                    Check("byte 3 is the battery level, and it is FULL", raw[3] == BATTERY_LEVEL_FULL,
                          $"0x{raw[3]:X2} ({LevelName(raw[3])})");
                    Check("bytes 0 and 1 are the version word, left zero like the LED reply",
                          raw[0] == 0 && raw[1] == 0, $"{raw[0]:X2}-{raw[1]:X2}");
                    // The exact shape of the pre-fix defect, named so a failure
                    // here says which regression came back.
                    Check("the pair is not packed one byte early (the #61 defect)",
                          !(raw[1] == BATTERY_TYPE_WIRED && raw[2] == BATTERY_TYPE_NIMH));
                }

                // The sibling reply that had the header right all along. If a
                // future edit "fixes" both to one convention, this catches it.
                var led = Ioctl(iface, IOCTL_XUSB_GET_LED_STATE, 3, out uint ledRet, out _);
                Check("the LED reply still carries its version word first",
                      led != null && ledRet == 3 && led[0] == 0 && led[1] == 0 && led[2] == 0x06,
                      led == null ? "no reply" : BitConverter.ToString(led));

                // 2. What a consumer reads. Find the slot this pad claimed by
                // submitting a deflected stick, so a physical pad on the bench
                // cannot be mistaken for ours.
                int slot = FindSlot(c, slotsBefore);
                Check("the pad claimed an XInput slot", slot >= 0, slot >= 0 ? $"slot {slot}" : "none");
                if (slot >= 0)
                {
                    uint rc = XInputGetBatteryInformation((uint)slot, 0 /* BATTERY_DEVTYPE_GAMEPAD */, out var info);
                    Check("XInputGetBatteryInformation succeeds", rc == 0, $"rc=0x{rc:X8}");
                    Check("a consumer reads BatteryType WIRED", info.BatteryType == BATTERY_TYPE_WIRED,
                          $"0x{info.BatteryType:X2} ({TypeName(info.BatteryType)})");
                    Check("a consumer reads BatteryLevel FULL", info.BatteryLevel == BATTERY_LEVEL_FULL,
                          $"0x{info.BatteryLevel:X2} ({LevelName(info.BatteryLevel)})");

                    // SDL_xinputjoystick.c UpdateXInputJoystickBatteryInformation,
                    // applied to the values just read.
                    var (state, percent) = SdlPowerInfo(info.BatteryType, info.BatteryLevel);
                    Check("SDL would report CHARGING at 100 percent, not ON_BATTERY at 10",
                          state == "SDL_POWERSTATE_CHARGING" && percent == 100, $"{state} {percent}%");
                }
            }
            catch (Exception ex)
            {
                Check($"cycle {cycle} runs without throwing", false, ex.Message);
            }
            finally
            {
                try { c?.Dispose(); } catch { }
                for (int i = 0; i < 30 && XusbInterfaces().Count > xusbBefore.Count; i++) Thread.Sleep(500);
            }
        }

        Console.WriteLine();
        Console.WriteLine(s_failures == 0
            ? $"=== {s_total}/{s_total} PASS ==="
            : $"=== {s_failures} of {s_total} FAILED ===");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>SDL's mapping, transcribed from
    /// SDL_xinputjoystick.c's UpdateXInputJoystickBatteryInformation.</summary>
    static (string State, int Percent) SdlPowerInfo(byte type, byte level)
    {
        string state = type switch
        {
            BATTERY_TYPE_WIRED => "SDL_POWERSTATE_CHARGING",
            BATTERY_TYPE_UNKNOWN or BATTERY_TYPE_DISCONNECTED => "SDL_POWERSTATE_UNKNOWN",
            _ => "SDL_POWERSTATE_ON_BATTERY",
        };
        if (state == "SDL_POWERSTATE_UNKNOWN") return (state, -1);
        int percent = level switch
        {
            BATTERY_LEVEL_EMPTY => 10,
            0x01 => 40,
            0x02 => 70,
            _ => 100,
        };
        return (state, percent);
    }

    static string TypeName(byte t) => t switch
    {
        BATTERY_TYPE_DISCONNECTED => "DISCONNECTED",
        BATTERY_TYPE_WIRED => "WIRED",
        BATTERY_TYPE_ALKALINE => "ALKALINE",
        BATTERY_TYPE_NIMH => "NIMH",
        BATTERY_TYPE_UNKNOWN => "UNKNOWN",
        _ => "?",
    };

    static string LevelName(byte l) => l switch
    {
        BATTERY_LEVEL_EMPTY => "EMPTY",
        0x01 => "LOW",
        0x02 => "MEDIUM",
        BATTERY_LEVEL_FULL => "FULL",
        _ => "?",
    };

    // ── the XUSB interface ─────────────────────────────────────────────

    static readonly Guid XusbGuid = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");

    static List<string> XusbInterfaces()
    {
        var found = new List<string>();
        var g = XusbGuid;
        if (CM_Get_Device_Interface_List_SizeW(out uint len, ref g, null, 0) != 0 || len <= 1) return found;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref g, null, buf, len, 0) != 0) return found;
        int i = 0;
        while (i < buf.Length)
        {
            int e = Array.IndexOf(buf, '\0', i);
            if (e < 0 || e == i) break;
            found.Add(new string(buf, i, e - i));
            i = e + 1;
        }
        return found;
    }

    /// <summary>One buffered IOCTL against the interface, opened the way
    /// xinput1_4 opens it (shared read and write, no input buffer).</summary>
    static byte[]? Ioctl(string path, uint code, int outLen, out uint returned, out int err)
    {
        returned = 0; err = 0;
        IntPtr h = CreateFileW(path, 0xC0000000 /* GENERIC_READ|WRITE */, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (h == new IntPtr(-1)) { err = Marshal.GetLastWin32Error(); return null; }
        try
        {
            var outBuf = new byte[Math.Max(outLen, 8)];
            var pin = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
            try
            {
                bool ok = DeviceIoControl(h, code, IntPtr.Zero, 0, pin.AddrOfPinnedObject(), (uint)outLen,
                                          out returned, IntPtr.Zero);
                if (!ok) { err = Marshal.GetLastWin32Error(); return null; }
                return outBuf.Take(outLen).ToArray();
            }
            finally { pin.Free(); }
        }
        finally { CloseHandle(h); }
    }

    // ── XInput ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_BATTERY_INFORMATION { public byte BatteryType; public byte BatteryLevel; }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD
    {
        public ushort wButtons; public byte bLeftTrigger, bRightTrigger;
        public short sThumbLX, sThumbLY, sThumbRX, sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }

    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint idx, out XINPUT_STATE st);
    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetBatteryInformation(uint idx, byte devType, out XINPUT_BATTERY_INFORMATION info);

    static List<uint> XInputSlots()
    {
        var l = new List<uint>();
        for (uint s = 0; s < 4; s++) if (XInputGetState(s, out _) == 0) l.Add(s);
        return l;
    }

    /// <summary>The slot carrying our deflected stick. Submitting a value no
    /// resting pad reports keeps a physical controller on the bench from
    /// being read as ours. XInput takes the pad once it sees the interface
    /// arrive, which other work on the machine delays, so the wait is a 30 s
    /// hang bound on the clock, not 150 tries of 20 ms.</summary>
    static int FindSlot(HMController c, List<uint> before)
    {
        var state = new HMGamepadState { Axes = new Dictionary<HMAxis, float> { [HMAxis.X] = 1.0f, [HMAxis.Y] = 0.5f } };
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 30_000)
        {
            c.SubmitState(state);
            Thread.Sleep(20);
            for (uint s = 0; s < 4; s++)
                if (XInputGetState(s, out var st) == 0 && st.Gamepad.sThumbLX > 30000 && !before.Contains(s))
                    return (int)s;
        }
        return -1;
    }

    static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // ── P/Invoke ───────────────────────────────────────────────────────

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string p, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inLen,
                                       IntPtr outBuf, uint outLen, out uint returned, IntPtr overlapped);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid g, string? id, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_ListW(ref Guid g, string? id, char[] buf, uint len, uint flags);
}
