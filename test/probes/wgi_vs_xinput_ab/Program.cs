using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Collections.Concurrent;
using Windows.Gaming.Input;

internal static class P
{
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_VIBRATION { public ushort wLeftMotorSpeed; public ushort wRightMotorSpeed; }
    [DllImport("xinput1_4.dll")]
    static extern uint XInputSetState(uint dwUserIndex, ref XINPUT_VIBRATION vibration);

    static ConcurrentBag<Gamepad> g_eventPads = new();

    static void Main()
    {
        Console.WriteLine("==========================================================");
        Console.WriteLine(" WGI put_Vibration: EVENT-SUBSCRIPTION vs direct enumeration");
        Console.WriteLine(" Hypothesis: GameInputSvc gates haptic dispatch based on");
        Console.WriteLine(" whether the caller is subscribed to GamepadAdded events.");
        Console.WriteLine(" Chromium subscribes; my prior tests didn't.");
        Console.WriteLine("==========================================================");
        Console.WriteLine();
        Console.WriteLine("Subscribing to Gamepad.GamepadAdded BEFORE any enumeration...");
        Gamepad.GamepadAdded += OnGamepadAdded;
        Gamepad.GamepadRemoved += OnGamepadRemoved;
        Console.WriteLine("Subscribed. Waiting 2 seconds for any queued events...");
        Thread.Sleep(2000);

        Console.WriteLine();
        Console.WriteLine("Enumerating Gamepad.Gamepads:");
        var gps = Gamepad.Gamepads;
        Console.WriteLine($"  Count: {gps.Count}");
        for (int i = 0; i < gps.Count; i++) Console.WriteLine($"  [{i}] {gps[i]}");
        Console.WriteLine();
        Console.WriteLine($"Events captured {g_eventPads.Count} Gamepad instances.");

        Console.WriteLine();
        Console.WriteLine("Press ENTER when physical controllers are connected and ready, then I'll");
        Console.WriteLine("unplug and replug the physical 360 to trigger fresh GamepadAdded events.");
        Console.WriteLine("(Actually — user will replug manually while this waits.)");
        Console.Write("Press ENTER when ready to test replug-to-event flow...");
        Console.ReadLine();

        Console.WriteLine("Unplug and replug your PHYSICAL Xbox 360 Wired now. I'll capture events for 15 seconds.");
        var end = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < end) Thread.Sleep(500);
        Console.WriteLine();
        Console.WriteLine($"After 15s: events captured {g_eventPads.Count} Gamepad instances.");

        // Now call put_Vibration on EACH event-captured IGamepad
        int idx = 0;
        foreach (var gp in g_eventPads)
        {
            Console.WriteLine();
            Console.WriteLine($">>> Firing put_Vibration(1.0, 1.0, 1.0, 1.0) on EVENT-captured Gamepad [{idx}] for 2s...");
            idx++;
            var vib = new GamepadVibration { LeftMotor = 1.0, RightMotor = 1.0, LeftTrigger = 1.0, RightTrigger = 1.0 };
            var t0 = DateTime.UtcNow;
            while ((DateTime.UtcNow - t0).TotalSeconds < 2.0)
            {
                try { gp.Vibration = vib; } catch (Exception ex) { Console.WriteLine("  exception: " + ex.Message); break; }
                Thread.Sleep(50);
            }
            try { gp.Vibration = new GamepadVibration(); } catch { }
            Console.WriteLine("<<< cleared");
        }

        Console.WriteLine();
        Console.WriteLine("Also firing on Gamepad.Gamepads[] (static enum) for comparison:");
        for (int i = 0; i < gps.Count; i++)
        {
            Console.WriteLine($">>> Firing put_Vibration on Gamepad.Gamepads[{i}] for 2s...");
            var vib = new GamepadVibration { LeftMotor = 1.0, RightMotor = 1.0, LeftTrigger = 1.0, RightTrigger = 1.0 };
            var t0 = DateTime.UtcNow;
            while ((DateTime.UtcNow - t0).TotalSeconds < 2.0)
            {
                try { gps[i].Vibration = vib; } catch { }
                Thread.Sleep(50);
            }
            try { gps[i].Vibration = new GamepadVibration(); } catch { }
            Console.WriteLine("<<< cleared");
        }

        Console.WriteLine();
        Console.WriteLine("Report: did the PHYSICAL 360 rumble during any of the iterations above?");
        Console.WriteLine("If YES on event-captured but NO on Gamepads[]: event subscription is the gate.");
        Console.WriteLine("Press ENTER to exit.");
        Console.ReadLine();
    }

    static void OnGamepadAdded(object? sender, Gamepad e)
    {
        Console.WriteLine($"[event] GamepadAdded fired — captured instance.");
        g_eventPads.Add(e);
    }
    static void OnGamepadRemoved(object? sender, Gamepad e)
    {
        Console.WriteLine($"[event] GamepadRemoved fired.");
    }
}
