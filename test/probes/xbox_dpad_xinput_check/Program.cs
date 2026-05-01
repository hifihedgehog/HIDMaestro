// v1.3.3 — Xbox-VID d-pad regression probe (#19).
//
// Submits each HMHat direction via HMController.SubmitState on an
// xbox-360-wired virtual, reads XInputGetState, and asserts that
// wButtons.DPAD_* matches the expected XInput direction bits. This
// hits the XUSB companion driver (HMXInput.dll) via xinput1_4 →
// xusb22.sys → IOCTL_XUSB_GET_STATE — the path that issue #19
// reported broken (bug was that the SDK never wrote the 4-bit hat
// into the GIP buffer's btnHigh, so the companion's
// (btnHigh >> 2) & 0x0F always read zero and wButtons.DPAD_* never
// fired).
//
// Exit code 0 on all PASS, 1 if any direction fails.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;

internal sealed class Program
{
    // XInput button masks — XInputGetState wButtons (xinput1_4 / xusb22).
    private const ushort XINPUT_GAMEPAD_DPAD_UP    = 0x0001;
    private const ushort XINPUT_GAMEPAD_DPAD_DOWN  = 0x0002;
    private const ushort XINPUT_GAMEPAD_DPAD_LEFT  = 0x0004;
    private const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
    private const ushort DPAD_MASK                 = 0x000F;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    private const uint ERROR_SUCCESS = 0;
    private const uint ERROR_DEVICE_NOT_CONNECTED = 0x048F;

    public static int Main(string[] args)
    {
        Console.WriteLine("=== v1.3.3 Xbox 360 wired d-pad XInput probe (#19) ===\n");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        Console.Write("  Installing driver... ");
        ctx.InstallDriver();
        Console.WriteLine("OK");

        var profile = ctx.GetProfile("xbox-360-wired")
                      ?? throw new Exception("missing xbox-360-wired");

        Console.Write("  Creating xbox-360-wired controller... ");
        var ctrl = ctx.CreateController(profile);
        Console.WriteLine("OK");

        // Find the slot the new virtual claimed. SetupController has its own
        // 500 ms slot-claim wait but it's non-fatal; if the slot didn't
        // appear inside the SDK we poll once more here for safety.
        int slot = WaitForSlot(timeoutMs: 3000);
        if (slot < 0)
        {
            Console.WriteLine("  FAIL: no XInput slot claimed for the virtual within 3 s — cannot exercise XUSB companion path.");
            try { ctrl.Dispose(); } catch { }
            return 1;
        }
        Console.WriteLine($"  Virtual claimed XInput slot {slot}\n");

        // Each row: HMHat direction → expected wButtons DPAD_* mask.
        // Mapping matches companion.c:421-426's switch table directly.
        var cases = new (HMHat dir, ushort expected, string name)[]
        {
            (HMHat.None,      0,                                                                "None"),
            (HMHat.North,     XINPUT_GAMEPAD_DPAD_UP,                                           "North"),
            (HMHat.NorthEast, (ushort)(XINPUT_GAMEPAD_DPAD_UP   | XINPUT_GAMEPAD_DPAD_RIGHT),    "NorthEast"),
            (HMHat.East,      XINPUT_GAMEPAD_DPAD_RIGHT,                                        "East"),
            (HMHat.SouthEast, (ushort)(XINPUT_GAMEPAD_DPAD_DOWN | XINPUT_GAMEPAD_DPAD_RIGHT),    "SouthEast"),
            (HMHat.South,     XINPUT_GAMEPAD_DPAD_DOWN,                                         "South"),
            (HMHat.SouthWest, (ushort)(XINPUT_GAMEPAD_DPAD_DOWN | XINPUT_GAMEPAD_DPAD_LEFT),     "SouthWest"),
            (HMHat.West,      XINPUT_GAMEPAD_DPAD_LEFT,                                         "West"),
            (HMHat.NorthWest, (ushort)(XINPUT_GAMEPAD_DPAD_UP   | XINPUT_GAMEPAD_DPAD_LEFT),     "NorthWest"),
            (HMHat.None,      0,                                                                "None (post-cycle clear)"),
        };

        int failures = 0;
        foreach (var c in cases)
        {
            var state = new HMGamepadState { Hat = c.dir };
            ctrl.SubmitState(in state);

            // Companion publishes via shared memory + IOCTL completion. A
            // few poll iterations covers the kernel-side propagation; the
            // SDK already wrote shared memory before SubmitState returned.
            ushort observed = 0;
            bool match = false;
            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(10);
                if (XInputGetState((uint)slot, out var xs) != ERROR_SUCCESS) continue;
                observed = (ushort)(xs.Gamepad.wButtons & DPAD_MASK);
                if (observed == c.expected) { match = true; break; }
            }

            string verdict = match ? "PASS" : "FAIL";
            Console.WriteLine(
                $"  [{verdict}] {c.name,-22} expected=0x{c.expected:X4}  observed=0x{observed:X4}");
            if (!match) failures++;
        }

        Console.WriteLine();
        try { ctrl.Dispose(); } catch { }

        if (failures == 0)
        {
            Console.WriteLine($"=== ALL PASS ({cases.Length} directions) ===");
            return 0;
        }
        Console.WriteLine($"=== FAIL: {failures}/{cases.Length} directions did not match ===");
        return 1;
    }

    /// <summary>Poll XInputGetState across slots 0..3 until one returns
    /// SUCCESS, indicating xinputhid (or our companion's HMXInput.dll)
    /// has bound a slot to the new virtual. Returns -1 on timeout.</summary>
    private static int WaitForSlot(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            for (uint i = 0; i < 4; i++)
            {
                if (XInputGetState(i, out _) == ERROR_SUCCESS)
                    return (int)i;
            }
            Thread.Sleep(10);
        }
        return -1;
    }
}
