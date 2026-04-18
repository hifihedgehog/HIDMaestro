using System;
using System.Runtime.InteropServices;
using System.Threading;

// Deterministic XInput wire-format probe. Sends specific (wLeft, wRight)
// pairs and waits, so we can correlate exact input to driver's raw bytes.

internal static class P
{
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed; public ushort wRightMotorSpeed; }
    [DllImport("xinput1_4.dll")]
    static extern uint XInputSetState(uint dwUserIndex, ref XINPUT_VIBRATION vibration);

    static void Fire(uint slot, ushort l, ushort r, string label)
    {
        var v = new XINPUT_VIBRATION { wLeftMotorSpeed = l, wRightMotorSpeed = r };
        uint rc = XInputSetState(slot, ref v);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] slot={slot} wLeft=0x{l:X4} wRight=0x{r:X4}  rc=0x{rc:X8}  label={label}");
        Thread.Sleep(1000);
    }

    static void Main()
    {
        uint slot = 0;  // our virtual on slot 0
        Console.WriteLine("Deterministic XInput wire-format probe on slot 0 (our virtual).");
        Console.WriteLine("Each call holds for 1s. Watch HIDMaestroTest [out0] lines for raw bytes.");
        Console.WriteLine();

        Fire(slot, 0x0000, 0x0000, "CLEAR");
        Fire(slot, 0xFFFF, 0x0000, "LEFT-only max (wLeft=0xFFFF, wRight=0)");
        Fire(slot, 0x0000, 0xFFFF, "RIGHT-only max (wLeft=0, wRight=0xFFFF)");
        Fire(slot, 0x8080, 0x4040, "LEFT=0x80 RIGHT=0x40 (asymmetric)");
        Fire(slot, 0x0100, 0xFF00, "LEFT=0x01 RIGHT=0xFF (minimal vs max)");
        Fire(slot, 0x0000, 0x0000, "CLEAR");
    }
}
