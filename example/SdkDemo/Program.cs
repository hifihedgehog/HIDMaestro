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

// ── 7. Dispose a single controller while others stay live ────────────
// Each HMController is independently disposable. Disposing one removes
// just that virtual device from the system — the others keep running.
// This is the pattern PadForge uses when a user disconnects one physical
// controller while others remain active.
Console.WriteLine("\n  Disposing controller 1 (Xbox 360) — controller 0 stays live...");
ctrl1.Dispose();
Console.WriteLine("  Controller 1 removed. Controller 0 still active for 2 more seconds...");
sw.Restart();
while (sw.ElapsedMilliseconds < 2_000)
{
    double t = sw.Elapsed.TotalSeconds;
    var state = new HMGamepadState
    {
        LeftStickX = (float)Math.Cos(t * 2 * Math.PI),
        LeftStickY = (float)Math.Sin(t * 2 * Math.PI),
        Buttons    = HMButton.A,
    };
    ctrl0.SubmitState(in state);
    Thread.Sleep(4);
}

// ── 8. Inspect profile characteristics ───────────────────────────────
// Every profile's descriptor layout is publicly accessible — buttons,
// axes, hat, bit sizes, connection type, driver mode. PadForge uses
// this to show users what a profile provides before deploying it.
Console.WriteLine("\n  Inspecting DualSense profile characteristics:");
Console.WriteLine($"    Buttons:    {dsProfile.ButtonCount}");
Console.WriteLine($"    Axes:       {dsProfile.AxisCount}");
Console.WriteLine($"    Has hat:    {dsProfile.HasHat}");
Console.WriteLine($"    Stick bits: {dsProfile.StickBits}-bit");
Console.WriteLine($"    Trigger:    {dsProfile.TriggerBits}-bit");
Console.WriteLine($"    Connection: {dsProfile.Connection}");
Console.WriteLine($"    Report:     {dsProfile.InputReportSize} bytes");

// ── 9. Custom profile — DualSense with 16 buttons ───────────────────
// Clone an existing profile and modify it. Here we take the DualSense
// (15 buttons) and create a variant with 16 buttons by building a
// custom descriptor. The virtual controller spoofs as a DualSense to
// every API but has an extra button that the original doesn't.
Console.WriteLine("\n  Creating custom DualSense variant with 16 buttons...");
byte[] customDsDesc = new HidDescriptorBuilder()
    .Gamepad()
    .AddStick("Left", bits: 8)       // match DualSense 8-bit sticks
    .AddStick("Right", bits: 8)
    .AddTrigger("Left", bits: 8)
    .AddTrigger("Right", bits: 8)
    .AddButtons(16)                   // 16 buttons (DualSense has 15)
    .AddHat()
    .Build();

var customDs = new HMProfileBuilder()
    .FromProfile(dsProfile)           // inherit VID/PID/name/strings
    .Id("dualsense-16btn")
    .Name("DualSense (16 buttons)")
    .Descriptor(customDsDesc)
    .InputReportSize(new HidDescriptorBuilder()
        .Gamepad().AddStick("Left",8).AddStick("Right",8)
        .AddTrigger("Left",8).AddTrigger("Right",8)
        .AddButtons(16).AddHat().InputReportByteSize)
    .Notes("Custom variant: 16 buttons instead of 15")
    .Build();

Console.Write($"  Deploying {customDs.Name} ({customDs.ButtonCount} buttons)... ");
using var ctrl2 = ctx.CreateController(customDs);
Console.WriteLine("OK");

// Submit a frame with button 16 held (the extra button)
var customState = new HMGamepadState
{
    LeftStickX = 0.5f,
    LeftStickY = -0.5f,
    Buttons = (HMButton)(1u << 15),  // bit 15 = button 16
};
ctrl2.SubmitState(in customState);
Console.WriteLine("  Submitted frame with button 16 held — check joy.cpl!");
Thread.Sleep(3000);

// ── 10. Fully custom controller from scratch ─────────────────────────
// Build a controller that doesn't exist in the catalog — a simple
// 4-axis, 6-button flight stick with a hat switch.
Console.WriteLine("\n  Building a custom flight stick from scratch...");
byte[] stickDesc = new HidDescriptorBuilder()
    .Joystick()
    .AddStick("Left", bits: 16)      // main stick X/Y
    .AddTrigger("Left", bits: 8)     // throttle (Z)
    .AddTrigger("Right", bits: 8)    // rudder (Rz)
    .AddButtons(6)
    .AddHat()
    .Build();

var flightStick = new HMProfileBuilder()
    .Id("custom-flight-stick")
    .Name("Custom Flight Stick")
    .Vendor("Custom")
    .Vid(0x0483).Pid(0x0001)         // arbitrary VID/PID
    .ProductString("My Flight Stick")
    .ManufacturerString("Homebrew")
    .Type("flightstick")
    .Connection("usb")
    .Descriptor(stickDesc)
    .InputReportSize(new HidDescriptorBuilder()
        .Joystick().AddStick("Left",16).AddTrigger("Left",8)
        .AddTrigger("Right",8).AddButtons(6).AddHat().InputReportByteSize)
    .Build();

Console.Write($"  Deploying {flightStick.Name} (VID={flightStick.VendorId:X4} PID={flightStick.ProductId:X4}, " +
              $"{flightStick.ButtonCount} buttons, {flightStick.AxisCount} axes)... ");
using var ctrl3 = ctx.CreateController(flightStick);
Console.WriteLine("OK");

// Fly around for 3 seconds
Console.WriteLine("  Sending flight stick input for 3 seconds...");
sw.Restart();
while (sw.ElapsedMilliseconds < 3_000)
{
    double t = sw.Elapsed.TotalSeconds;
    var fs = new HMGamepadState
    {
        LeftStickX   = (float)Math.Sin(t * 1.5),    // roll
        LeftStickY   = (float)Math.Cos(t * 1.5),    // pitch
        LeftTrigger  = (float)(0.5 + 0.5 * Math.Sin(t)),  // throttle
        RightTrigger = (float)(0.5 + 0.5 * Math.Cos(t * 2)), // rudder
        Buttons      = ((int)t % 3 == 0) ? HMButton.A : HMButton.None, // trigger button
        Hat          = (HMHat)(1 + ((int)(t * 3) % 8)),
    };
    ctrl3.SubmitState(in fs);
    Thread.Sleep(4);
}

Console.WriteLine("\n=== Demo complete — disposing all controllers ===");
// using-statements handle cleanup of ctrl0, ctrl2, ctrl3, and ctx.
