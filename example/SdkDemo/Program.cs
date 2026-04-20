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

// ── 0. Recover any OEM-name overrides left by a prior crash ─────────
// HMOemNameOverride lets consumers overwrite the DirectInput OEM-name
// label that joy.cpl shows for a given VID:PID. Every claim is recorded
// in HKLM\SOFTWARE\HIDMaestroOemOverrides before the DirectInput key is
// mutated, so a crash or force-kill cannot leave joy.cpl showing the
// override forever. Calling RecoverOrphans once at startup replays
// any such records from a prior process and restores the DirectInput
// keys to their pre-override state. Safe to call on every startup; no-op
// if there are no orphan records.
int recovered = HMOemNameOverride.RecoverOrphans();
if (recovered > 0)
    Console.WriteLine($"  Recovered {recovered} OEM-name override(s) from a prior session");

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

// ── 3a. Override the joy.cpl label for ctrl1 ─────────────────────────
// joy.cpl and DirectInput UIs read an OEM-name table that Windows
// pre-populates for many common VID:PIDs. HMOemNameOverride replaces
// the entry for a given VID:PID with a label of your choice. The write
// is crash-safe: the prior value is captured to a HIDMaestro-owned
// registry record before DirectInput is touched, so RecoverOrphans at
// startup can always restore the original.
ushort x360Vid = x360Profile.VendorId;
ushort x360Pid = x360Profile.ProductId;
HMOemNameOverride.Set(x360Vid, x360Pid, "SdkDemo Custom Label");
Console.WriteLine($"  Overrode joy.cpl label for VID_{x360Vid:X4}&PID_{x360Pid:X4} " +
                  $"-> \"SdkDemo Custom Label\" (open joy.cpl in another window to verify)");

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

    // Controller 0 (DualSense): full circle + ramping triggers + toggling A.
    // Note: the DualSense profile has triggerButtons=[6,7], so whenever
    // LeftTrigger or RightTrigger is nonzero, buttons 7/8 (L2/R2 digital)
    // automatically engage — matching real DS4/DualSense hardware behavior.
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

    // Controller 1 (Xbox 360): opposite direction, different buttons.
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
HMOemNameOverride.Clear(x360Vid, x360Pid);  // Restore the pre-override joy.cpl label
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

// ── 11. SubmitRawReport — ViGEmBus DS4 migration pattern ─────────────
// This shows how PadForge (or any app migrating from ViGEmBus) can
// submit full DS4/DualSense reports including touchpad, gyro, and
// battery data using SubmitRawReport. The caller packs the raw byte
// buffer in the device's native wire format — same as ViGEmBus's
// DS4_REPORT_EX — and HIDMaestro passes it through unchanged.
//
// Key difference from ViGEmBus: pass DATA BYTES ONLY (no Report ID
// prefix). The driver prepends the Report ID automatically. For the
// DualShock 4 / DualSense (Report ID 0x01, 64-byte report), pass
// 63 bytes of data.
//
// PadForge migration:
//   Before: _controller.SubmitRawReport(buf63);     // ViGEmBus
//   After:  ctrl.SubmitRawReport(buf63);             // HIDMaestro
//   (Same 63-byte buffer, same byte offsets, same touchpad packing)
Console.WriteLine("\n  SubmitRawReport: DS4/DualSense with touchpad data...");
var ds4Profile = ctx.GetProfile("dualshock-4-v1-full")
    ?? ctx.GetProfile("dualsense")!;
Console.Write($"  Creating {ds4Profile.Name} for raw report demo... ");
using var ctrl4 = ctx.CreateController(ds4Profile);
Console.WriteLine("OK");

{
    // Build a raw DS4 report with touchpad finger data.
    // This is the same byte layout PadForge's DS4VirtualController uses.
    // Byte offsets match Sony's DS4 USB wire format (not the HID descriptor's
    // logical field order — SubmitRawReport bypasses descriptor parsing).
    byte[] raw = new byte[63]; // 63 data bytes (no Report ID prefix)

    // Sticks (bytes 0-3): center = 128
    raw[0] = 128; // LX
    raw[1] = 128; // LY
    raw[2] = 128; // RX
    raw[3] = 128; // RY

    // Buttons (bytes 4-5): Cross pressed (bit 5 of byte 4)
    raw[4] = 0x08 | (1 << 5); // Hat=None(8) | Cross
    raw[5] = 0x00;

    // Special (byte 6): PS button = 0x01, Touchpad click = 0x02
    raw[6] = 0x00;

    // Triggers (bytes 7-8)
    raw[7] = 0;   // L2
    raw[8] = 0;   // R2

    // Timestamp (bytes 9-10)
    raw[9] = 0x00;
    raw[10] = 0x00;

    // Battery (byte 11)
    raw[11] = 0xFF;

    // Touchpad (bytes 32-41): one finger touching at center
    raw[32] = 1;   // touch packet count

    // Finger 0: active, ID=0, X=960, Y=471
    raw[33] = 0;   // packet counter
    raw[34] = 0x00; // tracking number (bit 7 clear = finger down)
    int tx = 960, ty = 471;
    raw[35] = (byte)(tx & 0xFF);
    raw[36] = (byte)(((tx >> 8) & 0x0F) | ((ty << 4) & 0xF0));
    raw[37] = (byte)(ty >> 4);

    // Finger 1: not touching
    raw[38] = 0x80; // tracking number (bit 7 set = finger lifted)

    ctrl4.SubmitRawReport(raw);
    Console.WriteLine("  Submitted raw DS4 report with touchpad finger at (960, 471)");
    Thread.Sleep(2000);
}

// ── 12. PadForge migration reference — all four target profiles ──────
// Complete SubmitRawReport examples for the four controller types
// PadForge supports or plans to support. Each shows the exact byte
// layout for the profile's native wire format.

// ── 12a. Xbox 360 Wired (ViGEmBus Xbox360 replacement) ──────────────
// Profile: xbox-360-wired | VID 045E PID 028E | No Report ID | 18 bytes
// Note: Xbox 360 uses SubmitState (not raw) because HMGamepadState
// maps 1:1 to the descriptor's standard fields. No vendor-specific
// touchpad/gyro region. XInput delivery is via HMCOMPANION.
Console.WriteLine("\n  12a. Xbox 360 Wired — via SubmitState");
{
    var x360Ref = ctx.GetProfile("xbox-360-wired")!;
    Console.Write($"  Creating {x360Ref.Name}... ");
    using var x360 = ctx.CreateController(x360Ref);
    Console.WriteLine("OK");

    // Xbox 360 has combined triggers (Z axis), 10 buttons, hat, two 16-bit sticks.
    // SubmitState handles everything — no raw report needed.
    // Guide routes through the XUSB companion (btnHigh 0x40 → wButtons 0x0400),
    // visible via XInputGetStateEx (ordinal 100).
    var x360State = new HMGamepadState
    {
        LeftStickX   =  0.5f,
        LeftStickY   = -0.3f,
        RightStickX  = -0.2f,
        RightStickY  =  0.7f,
        LeftTrigger  =  0.8f,   // Combined Z axis in DI
        RightTrigger =  0.4f,   // Combined Z axis in DI, separate via Vx/Vy for WGI
        Buttons      =  HMButton.A | HMButton.LeftBumper | HMButton.Guide,  // ALLOW-GUIDE: one-shot tour snapshot demonstrating Guide→wButtons 0x0400 routing, not a loop
        Hat          =  HMHat.North,
    };
    x360.SubmitState(in x360State);
    Console.WriteLine("  Submitted Xbox 360 state (A + LB + Guide, hat North)");
    Thread.Sleep(1500);
}

// ── 12b. Xbox Series X|S Bluetooth ──────────────────────────────────
// Profile: xbox-series-xs-bt | VID 045E PID 0B13 | No Report ID | 17 bytes
// Uses xinputhid for XInput + 16-button synthesis. SubmitState is the
// primary path. No vendor touchpad/gyro region.
Console.WriteLine("\n  12b. Xbox Series X|S Bluetooth — via SubmitState");
{
    var xsBtProfile = ctx.GetProfile("xbox-series-xs-bt")!;
    Console.Write($"  Creating {xsBtProfile.Name}... ");
    using var xsBt = ctx.CreateController(xsBtProfile);
    Console.WriteLine("OK");

    // 17-byte GIP descriptor: two 16-bit sticks, two 10-bit triggers,
    // 12 buttons (extended from DsHidMini's 10 to include Share at button 12;
    // xinputhid synthesizes 16 for DInput), hat, guide via System Main Menu.
    // Guide routes to the HID System Main Menu field → xinputhid → XInput 0x0400.
    // Share routes via buttonMap to descriptor button 12 → visible in joy.cpl/DI.
    var xsBtState = new HMGamepadState
    {
        LeftStickX   =  0.7f,
        LeftStickY   =  0.3f,
        RightStickX  = -0.5f,
        RightStickY  = -0.1f,
        LeftTrigger  =  1.0f,
        RightTrigger =  0.0f,
        Buttons      =  HMButton.X | HMButton.Y | HMButton.RightBumper
                      | HMButton.Guide | HMButton.Share,  // ALLOW-GUIDE: one-shot tour snapshot showing Series-BT Guide via HID System Main Menu, not a loop
        Hat          =  HMHat.SouthEast,
    };
    xsBt.SubmitState(in xsBtState);
    Console.WriteLine("  Submitted Xbox Series BT state (X + Y + RB + Guide + Share, hat SE)");
    Thread.Sleep(1500);
}

// ── 12c. DualSense (PS5) — with touchpad via SubmitRawReport ────────
// Profile: dualsense | VID 054C PID 0CE6 | Report ID 0x01 | 64 bytes
// Standard fields work via SubmitState. For touchpad/gyro/battery,
// use SubmitRawReport with the 63-byte data buffer (no Report ID
// prefix — the driver adds 0x01 automatically).
Console.WriteLine("\n  12c. DualSense — SubmitState + SubmitRawReport for touchpad");
{
    var dsProfile2 = ctx.GetProfile("dualsense")!;
    Console.Write($"  Creating {dsProfile2.Name}... ");
    using var ds = ctx.CreateController(dsProfile2);
    Console.WriteLine("OK");

    // Option A: SubmitState for standard fields only (no touchpad)
    ds.SubmitState(new HMGamepadState
    {
        LeftStickX  = -0.3f,
        LeftStickY  =  0.6f,
        LeftTrigger =  0.5f,
        Buttons     =  HMButton.A | HMButton.B,  // Cross + Circle
        Hat         =  HMHat.West,
    });
    Console.WriteLine("  Submitted DualSense state via SubmitState (Cross + Circle)");

    // Option B: SubmitRawReport for full control including touchpad.
    // 63 bytes data (Sony USB wire format). Byte offsets:
    //   0-3:   LX, LY, RX, RY (0-255, center=128)
    //   4-5:   hat(4b) + buttons(14b) packed LE
    //   6:     special (PS=0x01, touchpad click=0x02)
    //   7-8:   L2, R2 (0-255)
    //   9-10:  timestamp (LE, 10µs ticks)
    //   11:    battery level
    //   12-31: IMU (gyro 3x16b LE + accel 3x16b LE) + padding
    //   32:    touch packet count
    //   33-41: touch finger data (packet counter + 2 fingers)
    //   42-62: reserved
    byte[] dsRaw = new byte[63];
    dsRaw[0] = 100; dsRaw[1] = 150; dsRaw[2] = 128; dsRaw[3] = 128; // sticks
    dsRaw[4] = 0x08 | (1 << 5) | (1 << 6);  // hat=None + Cross + Circle
    dsRaw[7] = 128; // L2 half-pressed
    dsRaw[11] = 0xFF; // battery full
    dsRaw[32] = 1; // one touch packet
    dsRaw[34] = 0x00; // finger 0 down
    int dsTx = 960, dsTy = 471;
    dsRaw[35] = (byte)(dsTx & 0xFF);
    dsRaw[36] = (byte)(((dsTx >> 8) & 0x0F) | ((dsTy << 4) & 0xF0));
    dsRaw[37] = (byte)(dsTy >> 4);
    dsRaw[38] = 0x80; // finger 1 lifted
    ds.SubmitRawReport(dsRaw);
    Console.WriteLine("  Submitted DualSense raw report with touchpad at (960, 471)");
    Thread.Sleep(1500);
}

// ── 12d. Nintendo Switch Pro Controller — with gyro via SubmitRawReport
// Profile: switch-pro | VID 057E PID 2009 | Report ID 0x30 | 64 bytes
// The Switch Pro uses Nintendo's vendor-specific report format (0x30
// full input mode). Sticks are 12-bit packed (3 bytes per stick).
// IMU data (gyro + accel) is at bytes 13-24 (3x16b gyro + 3x16b accel).
Console.WriteLine("\n  12d. Switch Pro Controller — SubmitRawReport with gyro");
{
    var swProfile = ctx.GetProfile("switch-pro")!;
    Console.Write($"  Creating {swProfile.Name}... ");
    using var sw2 = ctx.CreateController(swProfile);
    Console.WriteLine("OK");

    // 63 bytes data (no Report ID prefix — driver adds 0x30).
    // Nintendo Switch Pro 0x30 report format:
    //   0:     timer counter (increments per report)
    //   1:     battery + connection info
    //   2-4:   button state (3 bytes: Y/X/B/A, triggers, hat, sticks click, etc.)
    //   5-7:   left stick (12-bit X in bits 0-11, 12-bit Y in bits 12-23, packed LE)
    //   8-10:  right stick (same packing)
    //   11:    vibration report ACK
    //   12:    sub-command reply ID
    //   13-24: IMU data (3 frames × 2 samples: gyro XYZ + accel XYZ, 16-bit LE each)
    //   25-62: sub-command reply data / NFC/IR data
    byte[] swRaw = new byte[63];
    swRaw[0] = 0x42; // timer counter
    swRaw[1] = 0x8E; // battery full, USB connected

    // Buttons: A pressed (byte 2, bit 3 in Nintendo layout)
    swRaw[2] = 0x08; // A button

    // Left stick centered: 12-bit X=2048, Y=2048
    // Packed as: byte5 = X[7:0], byte6 = X[11:8] | Y[3:0], byte7 = Y[11:4]
    int lsx = 2048, lsy = 2048;
    swRaw[5] = (byte)(lsx & 0xFF);
    swRaw[6] = (byte)(((lsx >> 8) & 0x0F) | ((lsy & 0x0F) << 4));
    swRaw[7] = (byte)(lsy >> 4);

    // Right stick centered
    int rsx = 2048, rsy = 2048;
    swRaw[8] = (byte)(rsx & 0xFF);
    swRaw[9] = (byte)(((rsx >> 8) & 0x0F) | ((rsy & 0x0F) << 4));
    swRaw[10] = (byte)(rsy >> 4);

    // IMU: gyro X/Y/Z at bytes 13-18 (16-bit LE, signed)
    // Small rotation around Y axis (simulating a gentle tilt)
    short gyroX = 0, gyroY = 500, gyroZ = 0;
    swRaw[13] = (byte)(gyroX & 0xFF); swRaw[14] = (byte)(gyroX >> 8);
    swRaw[15] = (byte)(gyroY & 0xFF); swRaw[16] = (byte)(gyroY >> 8);
    swRaw[17] = (byte)(gyroZ & 0xFF); swRaw[18] = (byte)(gyroZ >> 8);

    // Accel X/Y/Z at bytes 19-24 (gravity on Y = -4096 typical)
    short accelX = 0, accelY = -4096, accelZ = 0;
    swRaw[19] = (byte)(accelX & 0xFF); swRaw[20] = (byte)((ushort)accelX >> 8);
    swRaw[21] = (byte)(accelY & 0xFF); swRaw[22] = (byte)((ushort)accelY >> 8);
    swRaw[23] = (byte)(accelZ & 0xFF); swRaw[24] = (byte)((ushort)accelZ >> 8);

    sw2.SubmitRawReport(swRaw);
    Console.WriteLine("  Submitted Switch Pro raw report with gyro tilt + A button");
    Thread.Sleep(1500);
}

Console.WriteLine("\n=== Demo complete — disposing all controllers ===");
// using-statements handle cleanup of all controllers and ctx.
