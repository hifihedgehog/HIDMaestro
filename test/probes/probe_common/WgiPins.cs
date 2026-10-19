// Windows.Gaming.Input objects a probe touches stay referenced until the
// probe exits. Left to the finalizer, the probe's reference to a controller
// whose device has gone can be the last one, released at any moment. On
// 2026-10-19 one such release ran Windows.Gaming.Input's GameController
// destructor on the finalizer thread while the probe's main thread was
// installing the next device's driver, and the destructor faulted (S59,
// exit 0xC0000005: WinRT.IObjectReference.Finalize, Marshal.Release,
// Windows_Gaming_Input!GameController's vector deleting destructor). A
// pinned reference is never released, so no controller is destroyed while
// a probe runs, and process exit runs no finalizers. Each probe holds a few
// hundred controllers at most.

using System;
using System.Collections.Generic;
using Windows.Gaming.Input;
using WinRT;

internal static class WgiPins
{
    /// <summary>Keeps <paramref name="o"/>'s native reference for the life
    /// of the process. Every interface reference the object hands out later
    /// is released as usual: the pinned one keeps its count above zero.</summary>
    public static T Pin<T>(T o) where T : class
    {
        if (o is IWinRTObject w) GC.SuppressFinalize(w.NativeObject);
        return o;
    }

    /// <summary>RawGameController.RawGameControllers, each one pinned.</summary>
    public static List<RawGameController> Raws()
    {
        var view = Pin(RawGameController.RawGameControllers);
        var list = new List<RawGameController>(view.Count);
        foreach (var r in view) list.Add(Pin(r));
        return list;
    }

    /// <summary>Gamepad.Gamepads, each one pinned.</summary>
    public static List<Gamepad> Pads()
    {
        var view = Pin(Gamepad.Gamepads);
        var list = new List<Gamepad>(view.Count);
        foreach (var g in view) list.Add(Pin(g));
        return list;
    }

    /// <summary>Gamepad.FromGameController, pinned.</summary>
    public static Gamepad? PadOf(IGameController c)
    {
        var g = Gamepad.FromGameController(c);
        return g == null ? null : Pin(g);
    }

    /// <summary>RawGameController.FromGameController, pinned.</summary>
    public static RawGameController? RawOf(IGameController c)
    {
        var r = RawGameController.FromGameController(c);
        return r == null ? null : Pin(r);
    }

    /// <summary>Subscribes to GamepadAdded and RawGameControllerAdded, which
    /// WGI's broker needs before it tracks controllers for this process, and
    /// pins each controller the events hand over.</summary>
    public static void Listen()
    {
        Gamepad.GamepadAdded += (_, g) => Pin(g);
        RawGameController.RawGameControllerAdded += (_, r) => Pin(r);
    }
}
