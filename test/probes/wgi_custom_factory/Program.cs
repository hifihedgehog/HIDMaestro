using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Gaming.Input;
using Windows.Gaming.Input.Custom;

// Spike: register a custom ICustomGameControllerFactory for VID 045E PID 028E via
// GameControllerFactoryManager.RegisterCustomFactoryForHardwareId.
// Per-process historically. Goal tonight: see whether, while THIS process holds the
// registration live, a SEPARATE process (WgiId / Edge) observes the custom
// dispatch — i.e. WGI routes put_Vibration through our factory.

internal sealed class MyFactory : ICustomGameControllerFactory
{
    public object CreateGameController(IGameControllerProvider provider)
    {
        Console.WriteLine($"[factory] CreateGameController called. provider={provider?.GetType().FullName}");
        // Empirical test: if provider is HidGameControllerProvider, fire a test output
        // report immediately. If SDK's OutputReceived sees this, we have a working
        // user-mode dispatch path through WGI's Custom namespace.
        if (provider is HidGameControllerProvider hid) {
            // Parameter sweep per Opus: rule out the API surface before CsWinRT build
            var attempts = new (byte id, byte[] buf, string note)[] {
                (0x0F, new byte[] { 0x00, 0x00, 0x7F, 0x7F, 0xFF, 0x00, 0xEB }, "id=0x0F 7B canonical"),
                (0x00, new byte[] { 0x00, 0x00, 0x7F, 0x7F, 0xFF, 0x00, 0xEB }, "id=0x00 7B"),
                (0x0F, new byte[0], "id=0x0F 0B"),
                (0x00, new byte[0], "id=0x00 0B"),
                (0x0F, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, "id=0x0F all-FF"),
                (0x01, new byte[] { 0x00 }, "id=0x01 1B"),
                (0x03, new byte[] { 0x00, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00 }, "id=0x03 Xbox-BT-rumble"),
            };
            foreach (var (id, buf, note) in attempts) {
                try {
                    hid.SendOutputReport(id, buf);
                    Console.WriteLine($"[factory] SendOutputReport {note}: OK");
                } catch (Exception ex) {
                    Console.WriteLine($"[factory] SendOutputReport {note}: HR=0x{ex.HResult:X8} {ex.GetType().Name} msg='{ex.Message}'");
                }
            }
        }
        return new MyController(provider!);
    }

    public void OnGameControllerAdded(IGameController value) { Console.WriteLine($"[factory] Added: {value?.GetType().FullName}"); }
    public void OnGameControllerRemoved(IGameController value) { Console.WriteLine($"[factory] Removed: {value?.GetType().FullName}"); }
}

internal sealed class MyController : IGameController
{
    private readonly IGameControllerProvider _p;
    public MyController(IGameControllerProvider p) { _p = p; }
    public bool IsWireless => false;
    public Headset? Headset => null;
    public Windows.System.User? User => null;
    public event Windows.Foundation.TypedEventHandler<IGameController, Headset>? HeadsetConnected;
    public event Windows.Foundation.TypedEventHandler<IGameController, Headset>? HeadsetDisconnected;
    public event Windows.Foundation.TypedEventHandler<IGameController, Windows.System.UserChangedEventArgs>? UserChanged;
}

internal static class P
{
    static void Main()
    {
        Console.WriteLine("XUSB custom-factory spike — registering for VID 045E PID 028E");
        try
        {
            var factory = new MyFactory();
            GameControllerFactoryManager.RegisterCustomFactoryForHardwareId(factory, 0x045E, 0x028E);
            Console.WriteLine("RegisterCustomFactoryForHardwareId OK");
        }
        catch (Exception ex) { Console.WriteLine("Register ERR: " + ex); return; }

        Console.WriteLine("Also registering for XusbType=Gamepad/Gamepad");
        try
        {
            var factory2 = new MyFactory();
            GameControllerFactoryManager.RegisterCustomFactoryForXusbType(factory2, XusbDeviceType.Gamepad, XusbDeviceSubtype.Gamepad);
            Console.WriteLine("RegisterCustomFactoryForXusbType OK");
        }
        catch (Exception ex) { Console.WriteLine("Register2 ERR: " + ex); }

        // Gate test per Opus: what does Gamepad.Gamepads[0] actually return?
        // If it's my MyController, custom override is viable. If it's built-in
        // Gamepad, overrides never fire for consumers — the whole plan is dead.
        Thread.Sleep(800);
        Console.WriteLine("\n[gate-test] Gamepad.Gamepads enumeration:");
        Console.WriteLine($"  Count = {Gamepad.Gamepads.Count}");
        int i = 0;
        foreach (var pad in Gamepad.Gamepads) {
            Console.WriteLine($"  [{i++}] type={pad?.GetType().FullName}");
        }
        Console.WriteLine("[gate-test] If type is MyController -> custom-class intercept is viable.");
        Console.WriteLine("[gate-test] If type is Gamepad -> custom override never fires for app consumers.");

        Console.WriteLine("\nHolding registration live. Press Ctrl+C to exit.");
        while (true) Thread.Sleep(1000);
    }
}
