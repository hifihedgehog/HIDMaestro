// signal [runs]: live and elevated, the part of a frame mode call the
// SDK's own code does not decide. On a DualShock 4 and an Xbox 360 pad,
// each created as a program creates it, the probe times, with the
// controller's own section and events:
//   - SetEvent on the input event alone, which wakes the driver's worker
//   - SetEvent on the XUSB companion's doorbell alone (Xbox 360)
//   - a frame written to the section with no signal
//   - the same frame written by the same writer to a buffer of this
//     process's own, which no other process maps: the positive control
//     that tells the writer's cost from what other readers of the section
//     add to it
//   - a frame written and signaled, as every submit ends
// The frames are the controller's own last report and continue its
// sequence numbers, so the driver sends each as it sends a submit's. It
// builds against b4d29d9 too, whose writer takes the same arguments, so the
// two releases' drivers and companions are timed by the same calls.
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using HIDMaestro;
using HIDMaestro.Internal;

internal static partial class Program
{
    static int Signal(int runs)
    {
        if (!Elevated("signal")) return 3;
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var a = new HMGamepadState { Buttons = HMButton.A };
        var b = new HMGamepadState { Buttons = HMButton.B };
        foreach (string id in new[] { Ds4, Xbox360 })
        {
            using var c = ctx.CreateController(ctx.GetProfile(id)!);
            if (!OnEmbeddedDriver(c, id)) return 5;
            // A submit first, so the section holds this controller's frame.
            c.SubmitState(a);
            c.SubmitState(b);
            var t = typeof(HMController);
            IntPtr view = (IntPtr)t.GetField("_inputView", Any)!.GetValue(c)!;
            IntPtr input = (IntPtr)t.GetField("_inputEvent", Any)!.GetValue(c)!;
            IntPtr companion = (IntPtr)t.GetField("_companionInputEvent", Any)!.GetValue(c)!;
            var seqField = t.GetField("_inputSeqNo", Any)!;
            uint seq = (uint)seqField.GetValue(c)!;
            // The data lane as the last submit left it.
            int dataLen = Marshal.ReadInt32(view, 4);
            var frame = new byte[Math.Max(1, dataLen)];
            Marshal.Copy(view + 8, frame, 0, frame.Length);
            var gip = new byte[14];
            Marshal.Copy(view + 8 + 256, gip, 0, gip.Length);
            byte[]? gipOrNull = companion != IntPtr.Zero ? gip : null;
            Measure($"SetEvent, the input event, {id}", runs, 20_000, () => SignalSetEvent(input), gate: false);
            if (companion != IntPtr.Zero)
                Measure($"SetEvent, the companion's doorbell, {id}", runs, 20_000, () => SignalSetEvent(companion), gate: false);
            Measure($"a frame written with no signal, {id}", runs, 20_000, () =>
                SharedMemoryIO.WriteInputFrame(view, IntPtr.Zero, ref seq, frame, dataLen, gipOrNull), gate: false);
            IntPtr own = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
            try
            {
                uint ownSeq = 0;
                Measure($"a frame written to this process's own memory, {id}", runs, 20_000, () =>
                    SharedMemoryIO.WriteInputFrame(own, IntPtr.Zero, ref ownSeq, frame, dataLen, gipOrNull), gate: false);
            }
            finally { Marshal.FreeHGlobal(own); }
            Measure($"a frame written and signaled, {id}", runs, 20_000, () =>
                SharedMemoryIO.WriteInputFrame(view, input, ref seq, frame, dataLen, gipOrNull, companionEvent: companion), gate: false);
            // The controller's next frame follows the probe's.
            seqField.SetValue(c, seq);
        }
        return 0;
    }

    [DllImport("kernel32.dll", EntryPoint = "SetEvent")]
    static extern bool SignalSetEvent(IntPtr handle);
}
