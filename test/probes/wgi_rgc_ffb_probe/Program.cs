using System;
using System.Linq;
using System.Threading;
using Windows.Gaming.Input;
using Windows.Gaming.Input.ForceFeedback;
using System.Numerics;

// Probe WGI's RawGameController.ForceFeedbackMotors on each connected device.
// If our virtual (045E 028E) exposes zero motors here, hypothesis 1 is
// confirmed: WGI RGC-FFB is the dispatch path Chromium uses, and it silently
// no-ops because our descriptor has no PID TLC.
//
// For any motor we do find, attempt a minimal ConstantForceEffect and
// observe whether LoadEffect/Start land bytes in our driver log.

internal static class P
{
    static void Wait(int ms) => Thread.Sleep(ms);

    static void Main()
    {
        // WGI enumeration is async/lazy. Poll for a few seconds until
        // RawGameControllers is populated.
        var rgcs = RawGameController.RawGameControllers;
        int waited = 0;
        while (rgcs.Count == 0 && waited < 3000)
        {
            Wait(200); waited += 200;
            rgcs = RawGameController.RawGameControllers;
        }
        Console.WriteLine($"WGI sees {rgcs.Count} RawGameController(s) after {waited}ms.");
        if (rgcs.Count == 0) return;

        for (int i = 0; i < rgcs.Count; i++)
        {
            var rgc = rgcs[i];
            Console.WriteLine();
            Console.WriteLine($"=== RGC[{i}] HardwareVendorId=0x{rgc.HardwareVendorId:X4} HardwareProductId=0x{rgc.HardwareProductId:X4} DisplayName='{rgc.DisplayName}' NonRoamableId='{rgc.NonRoamableId}' ===");
            Console.WriteLine($"    ButtonCount={rgc.ButtonCount}  AxisCount={rgc.AxisCount}  SwitchCount={rgc.SwitchCount}");

            var motors = rgc.ForceFeedbackMotors;
            Console.WriteLine($"    ForceFeedbackMotors.Count = {motors.Count}");
            if (motors.Count == 0)
            {
                Console.WriteLine("    ^^^ ZERO motors. Any RGC-FFB API call against this RGC is a silent sink. ^^^");
                continue;
            }

            for (int m = 0; m < motors.Count; m++)
            {
                var motor = motors[m];
                Console.WriteLine($"    Motor[{m}] SupportedAxes={motor.SupportedAxes}  MasterGain={motor.MasterGain}  AreEffectsPaused={motor.AreEffectsPaused}");
                try
                {
                    var effect = new ConstantForceEffect();
                    effect.SetParameters(new Vector3(1.0f, 0f, 0f), TimeSpan.FromMilliseconds(500));
                    Console.WriteLine($"    Motor[{m}] calling LoadEffectAsync(ConstantForceEffect direction=(1,0,0) dur=500ms)...");
                    var loadTask = motor.LoadEffectAsync(effect).AsTask();
                    if (!loadTask.Wait(2000))
                    {
                        Console.WriteLine($"    Motor[{m}] LoadEffectAsync: TIMEOUT (2s)");
                        continue;
                    }
                    Console.WriteLine($"    Motor[{m}] LoadEffectAsync = {loadTask.Result}");

                    if (loadTask.Result == ForceFeedbackLoadEffectResult.Succeeded)
                    {
                        Console.WriteLine($"    Motor[{m}] calling Start()...");
                        effect.Start();
                        Wait(700);
                        effect.Stop();
                        Console.WriteLine($"    Motor[{m}] Effect cycle complete.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    Motor[{m}] Exception: {ex.GetType().Name} \"{ex.Message}\"");
                }
            }
        }

        // Also probe Gamepad.Vibration — the other WGI haptic API that Chromium
        // could plausibly use instead of RGC-FFB. Cross-check via
        // Gamepad.FromGameController(rgc) so we can explicitly attribute the
        // Gamepad instance back to the RGC we already identified as our virtual
        // by VID/PID. This prevents ambiguity about which device put_Vibration
        // targeted.
        Console.WriteLine();
        Console.WriteLine("=== Gamepad.Vibration probe ===");
        var gps = Gamepad.Gamepads;
        int gwaited = 0;
        while (gps.Count == 0 && gwaited < 2000)
        {
            Wait(200); gwaited += 200;
            gps = Gamepad.Gamepads;
        }
        Console.WriteLine($"Gamepad.Gamepads count = {gps.Count}");
        for (int i = 0; i < rgcs.Count; i++)
        {
            var rgc = rgcs[i];
            var gp = Gamepad.FromGameController(rgc);
            if (gp == null)
            {
                Console.WriteLine($"RGC[{i}] 0x{rgc.HardwareVendorId:X4}/0x{rgc.HardwareProductId:X4} '{rgc.DisplayName}' → Gamepad.FromGameController = null (not a Gamepad-classified device)");
                continue;
            }
            Console.WriteLine($"RGC[{i}] 0x{rgc.HardwareVendorId:X4}/0x{rgc.HardwareProductId:X4} '{rgc.DisplayName}' → Gamepad instance obtained. Calling put_Vibration(1.0, 1.0)...");
            var vib = new GamepadVibration { LeftMotor = 1.0, RightMotor = 1.0, LeftTrigger = 1.0, RightTrigger = 1.0 };
            gp.Vibration = vib;
            Wait(700);
            gp.Vibration = new GamepadVibration();
            Console.WriteLine($"RGC[{i}] put_Vibration sequence complete.");
        }

        Console.WriteLine();
        Console.WriteLine("Probe complete.");
    }
}
