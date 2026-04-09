// HIDMaestro SDK quickstart — the smallest possible consumer.
//
// What it does:
//   1. Loads the SDK's embedded profile catalog
//   2. Installs the driver if it isn't already
//   3. Creates one virtual DualSense controller
//   4. Submits a left-stick circle pattern for 5 seconds
//   5. Disposes everything cleanly
//
// Run from this directory:
//   dotnet run
//
// Run from the repo root:
//   dotnet run --project example/SdkDemo
//
// Requires admin (virtual device creation needs SeLoadDriverPrivilege).
// You can verify the controller is live in joy.cpl while the demo is
// drawing the circle, or run scripts/verify.py --controllers 1 in a
// separate terminal.

using System;
using System.Diagnostics;
using System.Threading;
using HIDMaestro;

Console.WriteLine("=== HIDMaestro SDK Quickstart ===\n");

// 1. Create a context. The SDK ships its full 222-profile catalog
//    embedded inside HIDMaestro.Core.dll, so consumers don't need to
//    drop JSONs anywhere on disk.
using var ctx = new HMContext();
int loaded = ctx.LoadDefaultProfiles();
Console.WriteLine($"  Loaded {loaded} embedded profiles");

// 2. Install the driver. Idempotent — if a matching package is already
//    in the driver store this returns quickly.
Console.Write("  Installing driver... ");
ctx.InstallDriver();
Console.WriteLine("OK");

// 3. Pick the DualSense profile and create one virtual controller.
//    Other profiles to try: "xbox-360-wired", "xbox-series-xs-bt",
//    "xbox-one", "stadia", etc. — see scripts/check_browser_order.py
//    or the test app's `list` command for the full set.
var profile = ctx.GetProfile("dualsense")
    ?? throw new InvalidOperationException("Profile 'dualsense' not found");
Console.Write($"  Creating controller ({profile.Name})... ");
using var ctrl = ctx.CreateController(profile);
Console.WriteLine("OK");

// 4. Submit a left-stick circle for 5 seconds at ~250 Hz. SubmitState is
//    the canonical input path: caller drives the cadence, SDK encodes
//    the abstract state into the active profile's HID descriptor and
//    publishes via shared memory. Right stick stays centered, no
//    triggers, no buttons.
//
//    Cemu / Citron NOTE: those emulators auto-calibrate on the first
//    poll and freeze whatever the stick is at as the resting "default".
//    A real consumer should submit a centered frame first and wait
//    briefly before starting any motion. This demo skips that step
//    because joy.cpl and verify.py don't auto-calibrate.
Console.WriteLine("  Sending left-stick circle pattern for 5 seconds...");
var sw = Stopwatch.StartNew();
int frames = 0;
while (sw.ElapsedMilliseconds < 5_000)
{
    double t = sw.Elapsed.TotalSeconds * 2 * Math.PI;   // 1 lap per second
    var state = new HMGamepadState
    {
        LeftStickX = (float)Math.Cos(t),
        LeftStickY = (float)Math.Sin(t),
    };
    ctrl.SubmitState(in state);
    frames++;
    Thread.Sleep(4);
}

double hz = frames * 1000.0 / sw.ElapsedMilliseconds;
Console.WriteLine($"  Sent {frames} frames ({hz:F0} Hz)");
Console.WriteLine("\n=== Demo complete — disposing ===");
// using-statements above handle cleanup of ctrl and ctx in reverse order.
