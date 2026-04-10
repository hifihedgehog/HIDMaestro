// HIDMaestro SDK quickstart — demonstrates the full API surface.
//
// What it does:
//   1. Loads the SDK's embedded profile catalog
//   2. Installs the driver if it isn't already
//   3. Creates two virtual controllers (DualSense + Xbox 360)
//   4. Subscribes to output events (rumble/haptics/FFB)
//   5. Submits input using all HMGamepadState fields
//   6. Demonstrates SubmitRawReport for exotic HID features
//   7. Disposes everything cleanly
//
// Run from this directory:
//   dotnet run
//
// Run from the repo root:
//   dotnet run --project example/SdkDemo
//
// Requires admin (virtual device creation needs SeLoadDriverPrivilege).
// You can verify the controllers are live in joy.cpl while the demo
// is running, or run scripts/verify.py --controllers 2 in a separate
// terminal.

using System;
using System.Diagnostics;
using System.Threading;
using HIDMaestro;

Console.WriteLine("=== HIDMaestro SDK Quickstart ===\n");

// ── 1. Context + profiles ────────────────────────────────────────────
// The SDK ships its full profile catalog embedded inside
// HIDMaestro.Core.dll, so consumers don't need to drop JSONs
// anywhere on disk.
using var ctx = new HMContext();
int loaded = ctx.LoadDefaultProfiles();
Console.WriteLine($"  Loaded {loaded} embedded profiles");

// ── 2. Driver install ────────────────────────────────────────────────
// Idempotent — if a matching package is already in the driver
// store this returns quickly.
Console.Write("  Installing driver... ");
ctx.InstallDriver();
Console.WriteLine("OK");

// ── 3. Create two virtual controllers ────────────────────────────────
// Pick any profile ID from the embedded catalog. Use the test
// app's `list` or `search` command to browse:
//   HIDMaestroTest.exe list
//   HIDMaestroTest.exe search thrustmaster
var dsProfile = ctx.GetProfile("dualsense")
    ?? throw new InvalidOperationException("Profile 'dualsense' not found");
var x360Profile = ctx.GetProfile("xbox-360-wired")
    ?? throw new InvalidOperationException("Profile 'xbox-360-wired' not found");

Console.Write($"  Creating controller 0 ({dsProfile.Name})... ");
using var ctrl0 = ctx.CreateController(dsProfile);
Console.WriteLine("OK");

Console.Write($"  Creating controller 1 ({x360Profile.Name})... ");
using var ctrl1 = ctx.CreateController(x360Profile);
Console.WriteLine("OK");

// ── 4. Subscribe to output events (rumble / haptics / FFB) ───────────
// When a game sends rumble or LED commands to the virtual controller,
// the SDK delivers them here. The consumer routes them to physical
// hardware (e.g. PadForge forwards to the real controller).
ctrl0.OutputReceived += (controller, packet) =>
{
    Console.WriteLine($"  [output] ctrl0 source={packet.Source} " +
                      $"reportId=0x{packet.ReportId:X2} len={packet.Data.Length}");
};
ctrl1.OutputReceived += (controller, packet) =>
{
    Console.WriteLine($"  [output] ctrl1 source={packet.Source} " +
                      $"reportId=0x{packet.ReportId:X2} len={packet.Data.Length}");
};

// ── 5. Submit input using the full HMGamepadState surface ────────────
// SubmitState is the canonical input path: caller drives the cadence,
// SDK encodes the abstract state into the active profile's HID
// descriptor and publishes via shared memory.
//
// HMGamepadState fields:
//   LeftStickX/Y, RightStickX/Y  — [-1.0 .. +1.0]
//   LeftTrigger, RightTrigger     — [ 0.0 .. +1.0]
//   Buttons                       — HMButton flags (A, B, X, Y, LB, RB, etc.)
//   Hat                           — HMHat enum (None, N, NE, E, SE, S, SW, W, NW)
Console.WriteLine("\n  Sending input for 5 seconds (sticks + triggers + buttons + hat)...");
var sw = Stopwatch.StartNew();
int frames = 0;
while (sw.ElapsedMilliseconds < 5_000)
{
    double t = sw.Elapsed.TotalSeconds;
    double angle = t * 2 * Math.PI;  // 1 full rotation per second

    // Controller 0 (DualSense): full circle + ramping triggers + toggling A
    var state0 = new HMGamepadState
    {
        LeftStickX   = (float)Math.Cos(angle),
        LeftStickY   = (float)Math.Sin(angle),
        RightStickX  = (float)Math.Sin(angle * 0.5),
        RightStickY  = (float)Math.Cos(angle * 0.5),
        LeftTrigger  = (float)(0.5 + 0.5 * Math.Sin(t * 3)),
        RightTrigger = (float)(0.5 + 0.5 * Math.Cos(t * 3)),
        Buttons      = ((int)t % 2 == 0) ? HMButton.A : HMButton.B,
        Hat          = (HMHat)(1 + ((int)(t * 2) % 8)),  // cycle N through NW
    };
    ctrl0.SubmitState(in state0);

    // Controller 1 (Xbox 360): opposite direction, different buttons
    var state1 = new HMGamepadState
    {
        LeftStickX   = (float)Math.Cos(-angle),
        LeftStickY   = (float)Math.Sin(-angle),
        LeftTrigger  = (float)(0.5 + 0.5 * Math.Cos(t * 2)),
        RightTrigger = (float)(0.5 + 0.5 * Math.Sin(t * 2)),
        Buttons      = ((int)t % 2 == 0) ? HMButton.X : HMButton.Y,
    };
    ctrl1.SubmitState(in state1);

    frames++;
    Thread.Sleep(4);
}

double hz = frames * 1000.0 / sw.ElapsedMilliseconds;
Console.WriteLine($"  Sent {frames} frames per controller ({hz:F0} Hz)");

// ── 6. SubmitRawReport — for exotic HID features ─────────────────────
// Some devices have capabilities that HMGamepadState doesn't model:
// touchpad coordinates, gyroscope data, vendor-specific LED control,
// sensor packets, etc. SubmitRawReport sends an arbitrary HID input
// report. The first byte is the Report ID (or 0 if the descriptor
// declares no Report IDs).
//
// Example: send a single raw report with Report ID 0 and 16 bytes of
// payload. In a real consumer you'd use this for touchpad coordinates,
// motion sensor data, or any feature the profile's HID descriptor
// declares but HMGamepadState doesn't cover.
byte[] rawReport = new byte[17]; // report ID (1 byte) + 16 bytes payload
rawReport[0] = 0x00;             // Report ID 0 (or whatever the descriptor uses)
// ... fill rawReport[1..16] with the desired HID report bytes ...
// ctrl0.SubmitRawReport(rawReport);  // uncommented, this would override the
//                                     // current state with raw bytes
Console.WriteLine("  (SubmitRawReport available for exotic HID features — see source)");

Console.WriteLine("\n=== Demo complete — disposing ===");
// using-statements above handle cleanup of ctrl0, ctrl1, and ctx in
// reverse order. The virtual controllers vanish from Device Manager,
// joy.cpl, and all APIs immediately on dispose.
