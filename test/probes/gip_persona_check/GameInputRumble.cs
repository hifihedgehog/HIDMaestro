// GameInput's SetRumbleState with trigger levels on the gamepad of one VID
// and PID. GameInput is the inbox interface that drives trigger motors from
// a program with no window: Windows.Gaming.Input sends vibration only for a
// program the session gives input to. The calls go through the COM vtables
// the Windows SDK's um\GameInput.h declares (10.0.26100.0): IGameInput's
// RegisterDeviceCallback is slot 9 and UnregisterCallback slot 13,
// IGameInputDevice's GetDeviceInfo slot 3 and SetRumbleState slot 10, and
// IUnknown's AddRef and Release slots 1 and 2.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

internal static unsafe class GameInputRumble
{
    [DllImport("GameInput.dll")]
    static extern int GameInputCreate(out IntPtr gameInput);

    const uint GameInputKindGamepad = 0x00040000;
    const uint GameInputDeviceConnected = 0x00000001;
    const uint GameInputBlockingEnumeration = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct RumbleParams
    {
        public float LowFrequency, HighFrequency, LeftTrigger, RightTrigger;
    }

    // The device the callback found, and the IDs it looks for.
    static IntPtr s_found;
    static ushort s_vid, s_pid;

    static IntPtr Slot(IntPtr com, int slot) => (*(IntPtr**)com)[slot];

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static void OnDevice(ulong token, IntPtr context, IntPtr device, ulong timestamp, uint current, uint previous)
    {
        if (s_found != IntPtr.Zero || (current & GameInputDeviceConnected) == 0) return;
        // GameInputDeviceInfo: infoSize (4 bytes), vendorId, productId.
        var info = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Slot(device, 3))(device);
        if (info == IntPtr.Zero) return;
        ushort vid = (ushort)Marshal.ReadInt16(info, 4), pid = (ushort)Marshal.ReadInt16(info, 6);
        if (vid != s_vid || pid != s_pid) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(device, 1))(device);
        s_found = device;
    }

    /// <summary>Sets the rumble state of the first connected gamepad of
    /// <paramref name="vid"/>:<paramref name="pid"/>, holds it for
    /// <paramref name="holdMs"/>, then clears it. Returns 0 when both calls
    /// were made, 2 when no such gamepad is present, else the failing
    /// HRESULT.</summary>
    public static uint Pulse(ushort vid, ushort pid, float low, float high, float leftTrigger, float rightTrigger, int holdMs, Action<string> log)
    {
        int hr = GameInputCreate(out IntPtr gi);
        if (hr < 0) { log($"GameInputCreate failed: 0x{hr:X8}"); return (uint)hr; }
        try
        {
            s_found = IntPtr.Zero;
            s_vid = vid;
            s_pid = pid;
            ulong token = 0;
            var register = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, IntPtr, delegate* unmanaged[Stdcall]<ulong, IntPtr, IntPtr, ulong, uint, uint, void>, ulong*, int>)Slot(gi, 9);
            hr = register(gi, IntPtr.Zero, GameInputKindGamepad, GameInputDeviceConnected, GameInputBlockingEnumeration, IntPtr.Zero, &OnDevice, &token);
            if (hr < 0) { log($"RegisterDeviceCallback failed: 0x{hr:X8}"); return (uint)hr; }
            ((delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, byte>)Slot(gi, 13))(gi, token, 5_000_000);
            IntPtr device = s_found;
            if (device == IntPtr.Zero) { log($"no gamepad {vid:X4}:{pid:X4}"); return 2; }
            try
            {
                var set = (delegate* unmanaged[Stdcall]<IntPtr, RumbleParams*, void>)Slot(device, 10);
                var p = new RumbleParams { LowFrequency = low, HighFrequency = high, LeftTrigger = leftTrigger, RightTrigger = rightTrigger };
                set(device, &p);
                log($"SetRumbleState low {low:F2} high {high:F2} left trigger {leftTrigger:F2} right trigger {rightTrigger:F2} on {vid:X4}:{pid:X4}");
                Thread.Sleep(holdMs);
                var zero = new RumbleParams();
                set(device, &zero);
                log("SetRumbleState cleared");
                return 0;
            }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(device, 2))(device); }
        }
        finally { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(gi, 2))(gi); }
    }
}
