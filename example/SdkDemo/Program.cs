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
using System.Linq;
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
//
// Cadence (v1.1.40+): the SDK polls the driver's 64-slot output ring
// every ~8 ms and drains every slot written since the last poll, in
// monotonic SeqNo order. Multiple OutputReceived invocations per poll
// are normal — DirectInput PID FFB writes Set Effect → Set Constant
// Force / Set Periodic → Effect Operation Start within 1-3 ms and all
// three surface here as separate packets (the pre-1.1.40 single-slot
// channel coalesced those bursts and silently dropped the magnitude
// packet — see issue #16). Keep handlers cheap (no synchronous I/O,
// no long locks); a stall longer than ~512 ms while the driver is
// writing at burst rate begins overwriting the oldest packets.
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

// ── 4b. PID Force Feedback (HID PID 1.0) ─────────────────────────────
// Profiles with a HID PID 1.0 force-feedback collection in their
// descriptor (Logitech wheels, some HOTAS, DirectInput FFB joysticks)
// expect the device to answer HidD_GetFeature for three Report IDs:
//   0x12  Block Load  — issued after dinput8 sends Create New Effect
//   0x13  Pool         — issued by EnumEffects / IDirectInputDevice8::Initialize
//   0x14  PID State    — issued during effect Start/Stop
//
// HIDMaestro reads these from a per-controller shared section the
// consumer fills via PublishPidPool / PublishPidState. Before the
// first PublishPidPool call, the driver returns STATUS_NO_SUCH_DEVICE
// for the Pool Report (matches vJoy's "FFB not enabled" convention)
// so DirectInput cleanly concludes "device exists but no FFB" rather
// than hanging or retrying.
//
// Block Load is allocated by the driver synchronously inside its
// SetFeature(0x11 Create New Effect) IOCTL handler — by the time the
// SDK's OutputReceived fires for the Create New Effect notification,
// the BL state is already canonical and readable via
// GetCurrentPidBlockLoad. PublishPidBlockLoad is reserved for
// consumers that need to override the driver's auto-allocation
// (e.g., reflecting a real physical device's EBI assignment).
//
// Custom-built profiles needing PID FFB should call
// HidDescriptorBuilder.AddPidFfbBlock when authoring the descriptor —
// it emits the canonical "minimum viable" PID FFB report set
// (one Create New Effect Feature, full Output report set), auto-injects
// the Report ID 0x01 input prefix, and rejects Gamepad-TLC misuse.
// Adding extra Feature reports (0x12/0x13/0x14) inside the same
// Application Collection AVs pid.dll (DirectX 8-era FFB enumeration
// bug, not OS-build-gated) — see the doc on AddPidFfbBlock.
ctrl0.PublishPidPool(
    ramPoolSize:             0xFFFF,
    simultaneousEffectsMax:  16,
    deviceManagedPool:       true,
    sharedParameterBlocks:   false);
ctrl0.PublishPidState(effectBlockIndex: 0,
    PidStateFlags.ActuatorsEnabled | PidStateFlags.ActuatorPower);
Console.WriteLine("  PID FFB enabled on ctrl0 (Pool + initial State published)");

// Read the EBI the driver auto-allocated (after a SetFeature(0x11)
// arrives via OutputReceived):
//   var bl = ctrl.GetCurrentPidBlockLoad();
//   // bl.EffectBlockIndex now holds the EBI dinput8 will reference

// ── 5. Submit input using the full HMGamepadState surface ────────────
// SubmitState is the canonical input path: caller drives the cadence,
// SDK encodes the abstract state into the active profile's HID
// descriptor and publishes via shared memory.
//
// HMGamepadState fields (v1.3.9):
//   Axes                  Dictionary<HMAxis, float>; values [0..1] uniform.
//                         Drive any descriptor-declared analog input by HID
//                         usage. Centered = 0.5 on signed axes, released = 0
//                         on unsigned. Discovery via Profile.Sticks / .Triggers.
//   Buttons               HMButton flags (A, B, X, Y, LB, RB, …)
//   Hat                   HMHat enum + HatDegrees / HatHundredths / HatRaw
//   Touchpad              TouchpadFinger0Active/X/Y/Id, TouchpadFinger1*,
//                         TouchpadPacketCounter
//   IMU                   GyroPitch/Yaw/Roll, AccelX/Y/Z (int16),
//                         SensorTimestamp (uint32 µs)
//   Battery + housekeeping BatteryLevel (0..10), BatteryCharging,
//                         BatteryFull, MicMuted, HeadphonesConnected
//
// The encoder writes whatever fields the active profile's descriptor or
// extendedReport declares. Profiles that don't declare a touchpad usage
// silently ignore TouchpadFinger0X et al.; the field stays present on
// the struct so the same caller code works across every profile.
Console.WriteLine("\n  Sending input for 5 seconds (sticks + triggers + buttons + hat + touchpad + IMU)...");
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
        // v1.3.9 — single unified Axes dict drives every analog input.
        // Helper resolves the canonical 6-slot convention into the active
        // profile's declared sticks/triggers (Sony's Z=right-stick-X,
        // Rx=left-trigger axisMap is honored automatically).
        Axes = HMGamepadStateHelpers.StandardAxes(ctrl0.Profile,
            leftStickX:   (float)((Math.Cos(angle) + 1) / 2),
            leftStickY:   (float)((Math.Sin(angle) + 1) / 2),
            rightStickX:  (float)((Math.Sin(angle * 0.5) + 1) / 2),
            rightStickY:  (float)((Math.Cos(angle * 0.5) + 1) / 2),
            leftTrigger:  (float)(0.5 + 0.5 * Math.Sin(t * 3)),
            rightTrigger: (float)(0.5 + 0.5 * Math.Cos(t * 3))),
        Buttons      = ((int)t % 2 == 0) ? HMButton.A : HMButton.B,
        Hat          = (HMHat)(1 + ((int)(t * 2) % 8)),  // cycle N through NW

        // v1.3.5 Sony surface — touchpad finger 0 traces a circle, gravity
        // vector at +1g on Y for a face-up controller, battery at 80%
        // charging. ds.daidr.me's touchpad / motion / battery panels render
        // these for any DualSense (USB or BT, post-arm) virtual.
        TouchpadFinger0Active = true,
        TouchpadFinger0X      = (ushort)(960 + 800 * Math.Cos(angle)),
        TouchpadFinger0Y      = (ushort)(540 + 400 * Math.Sin(angle)),
        TouchpadFinger0Id     = 1,
        TouchpadPacketCounter = (byte)((int)(t * 100) & 0xFF),
        GyroPitch             = (short)(2000 * Math.Sin(angle * 0.3)),
        GyroYaw               = (short)(2000 * Math.Cos(angle * 0.3)),
        GyroRoll              = 0,
        AccelX                = 0,
        AccelY                = 8000, // ~1g face-up
        AccelZ                = 0,
        SensorTimestamp       = (uint)(sw.ElapsedTicks * 1_000_000L
                                       / Stopwatch.Frequency),
        BatteryLevel          = 8,
        BatteryCharging       = true,
        HeadphonesConnected   = false,
        MicMuted              = false,
    };
    ctrl0.SubmitState(in state0);

    // Controller 1 (Xbox 360): opposite direction, different buttons.
    var state1 = new HMGamepadState
    {
        Axes = HMGamepadStateHelpers.StandardAxes(ctrl1.Profile,
            leftStickX:   (float)((Math.Cos(-angle) + 1) / 2),
            leftStickY:   (float)((Math.Sin(-angle) + 1) / 2),
            leftTrigger:  (float)(0.5 + 0.5 * Math.Cos(t * 2)),
            rightTrigger: (float)(0.5 + 0.5 * Math.Sin(t * 2))),
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
        Axes = HMGamepadStateHelpers.StandardAxes(ctrl0.Profile,
            leftStickX: (float)((Math.Cos(t * 2 * Math.PI) + 1) / 2),
            leftStickY: (float)((Math.Sin(t * 2 * Math.PI) + 1) / 2)),
        Buttons    = HMButton.A,
    };
    ctrl0.SubmitState(in state);
    Thread.Sleep(4);
}

// ── 7b. Enumerate and extract connected HID devices ──────────────────
// HMDeviceExtractor reads the descriptor Windows already has cached for
// every connected HID device and returns a ready-to-deploy HMProfile.
// Consumers that want a "scan my hardware and pick one" flow use this.
// Read-only; no admin required for this call; no input is captured.
Console.WriteLine("\n  Scanning connected HID devices...");
var connected = HMDeviceExtractor.ListDevices();
Console.WriteLine($"  Found {connected.Count} HID interface(s).");
// Show the first five to keep the demo output tight.
foreach (var d in connected.Take(5))
{
    Console.WriteLine($"    VID_{d.VendorId:X4}:PID_{d.ProductId:X4}  " +
                      $"Usage=0x{d.TopLevelUsagePage:X2}:0x{d.TopLevelUsage:X2}  " +
                      $"{d.ProductString ?? "(unknown)"}");
}
// To deploy any of these as a virtual identical to the physical device:
//   var extracted = HMDeviceExtractor.Extract(connected[0]);
//   using var clone = ctx.CreateController(extracted);
// To save the extracted profile as JSON (matches profiles/<vendor>/*.json):
//   string json = HMDeviceExtractor.ToJson(extracted);
//   File.WriteAllText("captured.json", json);

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
var customDsBuilder = new HidDescriptorBuilder()
    .Gamepad()
    .AddStick("Left", bits: 8)       // match DualSense 8-bit sticks
    .AddStick("Right", bits: 8)
    .AddTrigger("Left", bits: 8)
    .AddTrigger("Right", bits: 8)
    .AddButtons(16)                   // 16 buttons (DualSense has 15)
    .AddHat();

var customDs = new HMProfileBuilder()
    .FromProfile(dsProfile)           // inherit VID/PID/name/strings
    .Id("dualsense-16btn")
    .Name("DualSense (16 buttons)")
    .FromDescriptorBuilder(customDsBuilder)  // descriptor + InputReportSize together
    .Notes("Custom variant: 16 buttons instead of 15")
    .Build();

Console.Write($"  Deploying {customDs.Name} ({customDs.ButtonCount} buttons)... ");
using var ctrl2 = ctx.CreateController(customDs);
Console.WriteLine("OK");

// Submit a frame with button 16 held (the extra button)
var customState = new HMGamepadState
{
    Axes = HMGamepadStateHelpers.StandardAxes(ctrl2.Profile,
        leftStickX: 0.75f,    // (0.5 + 1) / 2 in old [-1..+1] convention = 0.75 in [0..1]
        leftStickY: 0.25f),   // (-0.5 + 1) / 2 = 0.25
    Buttons = (HMButton)(1u << 15),  // bit 15 = button 16
};
ctrl2.SubmitState(in customState);
Console.WriteLine("  Submitted frame with button 16 held — check joy.cpl!");
Thread.Sleep(3000);

// ── 10. Fully custom controller from scratch ─────────────────────────
// Build a controller that doesn't exist in the catalog — a simple
// 4-axis, 6-button flight stick with a hat switch and DirectInput PID
// force-feedback. AddPidFfbBlock emits the canonical "minimum viable"
// FFB report set; pair it with PublishPidPool / PublishPidState (and
// optionally GetCurrentPidBlockLoad) to expose Logitech-wheel-style
// FFB to DirectInput consumers.
Console.WriteLine("\n  Building a custom flight stick (with PID FFB) from scratch...");
var stickDescBuilder = new HidDescriptorBuilder()
    .Joystick()                       // Joystick TLC required by AddPidFfbBlock
    .AddStick("Left", bits: 16)       // main stick X/Y
    .AddTrigger("Left", bits: 8)      // throttle (Z)
    .AddTrigger("Right", bits: 8)     // rudder (Rz)
    .AddButtons(6)
    .AddHat()
    .AddPidFfbBlock();                // HID PID 1.0 FFB block; auto-injects
                                      // Report ID 0x01 prefix on the input items

var flightStick = new HMProfileBuilder()
    .Id("custom-flight-stick")
    .Name("Custom Flight Stick")
    .Vendor("Custom")
    .Vid(0x0483).Pid(0x0001)          // arbitrary VID/PID
    .ProductString("My Flight Stick")
    .ManufacturerString("Homebrew")
    .Type("flightstick")
    .Connection("usb")
    .FromDescriptorBuilder(stickDescBuilder)  // descriptor + InputReportSize together
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
        Axes = HMGamepadStateHelpers.StandardAxes(ctrl3.Profile,
            leftStickX:   (float)((Math.Sin(t * 1.5) + 1) / 2),    // roll
            leftStickY:   (float)((Math.Cos(t * 1.5) + 1) / 2),    // pitch
            leftTrigger:  (float)(0.5 + 0.5 * Math.Sin(t)),         // throttle
            rightTrigger: (float)(0.5 + 0.5 * Math.Cos(t * 2))),    // rudder
        Buttons      = ((int)t % 3 == 0) ? HMButton.A : HMButton.None, // trigger button
        Hat          = (HMHat)(1 + ((int)(t * 3) % 8)),
    };
    ctrl3.SubmitState(in fs);
    Thread.Sleep(4);
}

// ── 10b. High-resolution hat — HOTAS / pro flight-stick targets ──────
// HMGamepadState ships four input shapes for the hat field; the encoder
// picks the highest-priority non-null one and ignores the rest. The
// chain (highest → lowest priority):
//   HatDegrees     float?  continuous angle, 0=North, clockwise
//   HatHundredths  int?    hundredths of a degree (0..35999)
//   HatRaw         ushort? clamped to descriptor LogicalMin..LogicalMax
//   Hat            HMHat   8-octant enum (back-compat)
// Use HatDegrees when the source produces an angle. Use HatHundredths
// for vJoy migrations or hot paths that need integer math. Use HatRaw
// when you've queried HMProfile.HatLogicalMin/Max and want exact bits.
// Use Hat for XInput-style 8-way sources (still the only thing the
// XUSB companion's wButtons.DPAD_* can carry).
Console.WriteLine("\n  10b. Building a 16-position hat (22.5° per click) for HOTAS targets...");
var hotasDescBuilder = new HidDescriptorBuilder()
    .Joystick()
    .AddStick("Left", bits: 16)
    .AddTrigger("Left", bits: 8)
    .AddTrigger("Right", bits: 8)
    .AddButtons(8)
    .AddHat(positions: 16);            // 16-position hat: 0..16 with null=0

var hotas = new HMProfileBuilder()
    .Id("custom-hotas")
    .Name("Custom HOTAS (16-position hat)")
    .Vendor("Custom")
    .Vid(0x0483).Pid(0x0002)
    .ProductString("Custom HOTAS")
    .ManufacturerString("Homebrew")
    .Type("flightstick")
    .Connection("usb")
    .FromDescriptorBuilder(hotasDescBuilder)
    .Build();

Console.Write($"  Deploying {hotas.Name} (LogicalMin={hotas.HatLogicalMin}, " +
              $"LogicalMax={hotas.HatLogicalMax})... ");
using var ctrlHotas = ctx.CreateController(hotas);
Console.WriteLine("OK");

// Demonstrate all four hat-input shapes in sequence
Console.WriteLine("  Cycling through the four hat-input shapes (1 sec each)...");

// 1. Octant enum — limited to 8 cardinal/diagonal positions
ctrlHotas.SubmitState(new HMGamepadState { Hat = HMHat.NorthEast });
Console.WriteLine("    [Hat = HMHat.NorthEast]      → octant input");
Thread.Sleep(1000);

// 2. HatDegrees — float angle, snaps to nearest of 16 positions (22.5° each)
ctrlHotas.SubmitState(new HMGamepadState { HatDegrees = 67.5f });
Console.WriteLine("    [HatDegrees = 67.5f]         → 67.5° → idx 3 (ENE)");
Thread.Sleep(1000);

// 3. HatHundredths — same idea as HatDegrees but integer-only (hundredths)
ctrlHotas.SubmitState(new HMGamepadState { HatHundredths = 22500 }); // 225.00°
Console.WriteLine("    [HatHundredths = 22500]      → 225.00° → idx 10 (SW-ish)");
Thread.Sleep(1000);

// 4. HatRaw — bit-exact descriptor value
ctrlHotas.SubmitState(new HMGamepadState
{
    HatRaw = (ushort)(hotas.HatLogicalMin!.Value + 7) // mid-range position
});
Console.WriteLine($"    [HatRaw = LogicalMin + 7]    → exact descriptor bits");
Thread.Sleep(1000);

// Wrap-around case: 350° on the 16-position hat is closer to 0° (North)
// than to 337.5° (NNW), so it correctly snaps to North (idx 0).
ctrlHotas.SubmitState(new HMGamepadState { HatDegrees = 350f });
Console.WriteLine("    [HatDegrees = 350f]          → wraps to North (the % range protects LogicalMax)");
Thread.Sleep(1000);

// Null state — Hat=None, all other fields null → descriptor null state
ctrlHotas.SubmitState(new HMGamepadState { Hat = HMHat.None });
Console.WriteLine("    [Hat = HMHat.None]           → null state (no direction)");
Thread.Sleep(500);

// ── 10c. v1.3.5: data-driven vendor-blob input + output ──────────────
// Sony BT profiles (DualSense BT, DualSense Edge BT, etc.) use a 78-byte
// vendor-blob HID report (Report 0x31) that the descriptor declares as
// one opaque field. Pre-v1.3.5 the SDK couldn't pack this — it locked to
// the descriptor's first input report (Report 1, 9 bytes) and Steam Input
// / dualsense-tester saw a broken DualSense.
//
// v1.3.5 adds extendedReport / extendedOutputReport to the profile JSON
// describing the vendor blob's inner byte layout. The SDK becomes a
// generic codec that walks the JSON for both encode and decode.
Console.WriteLine("\n  10c. Sony BT vendor-blob path (Report 0x31)...");
{
    var dsBtProfile = ctx.GetProfile("dualsense-bt-full")!;
    Console.WriteLine($"    Profile: {dsBtProfile.Id}");
    Console.WriteLine($"    HasExtendedInput:  {dsBtProfile.HasExtendedInput}");
    Console.WriteLine($"    HasExtendedOutput: {dsBtProfile.HasExtendedOutput}");
    if (dsBtProfile.ExtendedReport is { } extIn)
    {
        Console.WriteLine($"    Input  reportId=0x{extIn.ReportIdByte:X2}, size={extIn.Size}, fields={extIn.Fields.Count}");
    }
    if (dsBtProfile.ExtendedOutputReport is { } extOut)
    {
        Console.WriteLine($"    Output reportId=0x{extOut.ReportIdByte:X2}, size={extOut.Size}, fields={extOut.Fields.Count}");
    }

    // HMOutputEncoder.Encode produces wire-format bytes from parsed fields.
    // Used by consumers (PadForge) to drive a real DualSense without
    // reimplementing byte layouts.
    var outputFields = new System.Collections.Generic.Dictionary<string, object>
    {
        { "btTag",         (byte)0x02 },
        { "validFlag0",    (byte)0xFF },
        { "validFlag1",    (byte)0xF7 },
        { "rightMotor",    (byte)128  },
        { "leftMotor",     (byte)64   },
        { "lightbarSetup", (byte)0x02 },
        { "lightbar",      new byte[] { 0xFF, 0x00, 0x80 } },
    };
    byte[] outputBytes = HMOutputEncoder.Encode(dsBtProfile, outputFields);
    Console.WriteLine($"    HMOutputEncoder.Encode produced {outputBytes.Length} bytes (Report ID 0x{outputBytes[0]:X2})");
    Console.WriteLine($"    rumble bytes: right={outputBytes[5]} left={outputBytes[6]}");
    Console.WriteLine($"    lightbar RGB at bytes 47-49: {outputBytes[47]:X2} {outputBytes[48]:X2} {outputBytes[49]:X2}");
    Console.WriteLine($"    CRC32 footer at bytes 74-77 (computed automatically per the JSON spec)");

    // Decode-side subscription. When a deployed dualsense-bt virtual receives
    // an output report 0x31 from a host (game / Steam Input / DS5 effect tool),
    // the SDK decodes it via the JSON spec and raises OutputDecoded with
    // parsed fields keyed by the JSON `semantic` names. Consumers subscribe
    // and read named values (e.LeftMotor, e.RightMotor, e.LightbarRGB, ...)
    // instead of byte offsets.
    using (var ctrlDsBt = ctx.CreateController(dsBtProfile))
    {
        ctrlDsBt.OutputDecoded += (sender, e) =>
        {
            if (e.Fields.TryGetValue("leftMotor", out var lm)
                && e.Fields.TryGetValue("rightMotor", out var rm))
            {
                Console.WriteLine($"    [decoded] rumble L={lm} R={rm}");
            }
            if (e.Fields.TryGetValue("lightbar", out var rgb) && rgb is byte[] c)
            {
                Console.WriteLine($"    [decoded] lightbar RGB=#{c[0]:X2}{c[1]:X2}{c[2]:X2}");
            }
        };
        Console.WriteLine($"    Subscribed to OutputDecoded — waiting briefly for any host output...");
        Thread.Sleep(800);
        // No host is sending output during this demo, so the event won't
        // fire. In a real consumer (PadForge), this is where game rumble
        // / lightbar / adaptive-trigger commands surface as named values.
    }
}

// ── 10d. Arbitrary analog axes via HMAxis ────────────────────────────
// For HOTAS sticks with throttle sliders, racing wheels with separate
// brake/throttle/clutch pedals, flight-stick rudder pedals: drive any
// descriptor-declared usage directly through state.Axes by HMAxis key.
//
//   • HMAxis enum               every recognized HID Generic Desktop /
//                                Simulation Controls analog usage
//   • state.Axes                Dictionary<HMAxis, float> [0..1] uniform.
//                                Drive by HID usage; null = no allocation,
//                                no hot-path cost
//   • HMProfile.AvailableAxes   discovery: list every HMAxis the
//                                descriptor declares
//   • HidDescriptorBuilder.AddAxis emit any HMAxis-keyed input field
//   • HMGamepadStateHelpers.StandardAxes(profile, ...) ergonomic shortcut
//                                for the common 6-slot LX/LY/RX/RY/LT/RT
//                                convention; resolves axis keys from
//                                Profile.Sticks / .Triggers automatically
Console.WriteLine("\n  10d. Custom HOTAS with throttle slider + rudder pedal...");
var hotasAxisBuilder = new HidDescriptorBuilder()
    .Joystick()
    .AddStick("Left", bits: 16)
    .AddAxis(HMAxis.Slider,   bits: 8)   // throttle slider on the stick base
    .AddAxis(HMAxis.Rudder,   bits: 8)   // separate rudder pedal
    .AddAxis(HMAxis.Throttle, bits: 8)   // secondary throttle (autopilot)
    .AddButtons(12)
    .AddHat(positions: 8);

var hotasFull = new HMProfileBuilder()
    .Id("custom-hotas-full")
    .Name("Custom HOTAS (Slider + Rudder + Throttle)")
    .Vendor("Custom")
    .Vid(0x0483).Pid(0x0003)
    .ProductString("Custom HOTAS Full")
    .ManufacturerString("Homebrew")
    .Type("hotas")
    .Connection("usb")
    .FromDescriptorBuilder(hotasAxisBuilder)
    .Build();

// Discovery: enumerate every analog axis the descriptor declares.
Console.WriteLine($"    AvailableAxes ({hotasFull.AxisCount}): " +
                  string.Join(", ", hotasFull.AvailableAxes));

using (var ctrlHotasFull = ctx.CreateController(hotasFull))
{
    // v1.3.9 — single Axes dict drives every analog input. The classifier
    // resolved Slider→LeftTrigger via the v1.3.8 case-0x36 extension; the
    // helper auto-fills the canonical 6-slot mapping from the profile's
    // Sticks/Triggers (here just LeftStickX/Y from the X+Y stick). The
    // remaining sim axes (Rudder, Throttle) get written by direct dict
    // entry since they're outside the 6-slot helper.
    var axes = HMGamepadStateHelpers.StandardAxes(ctrlHotasFull.Profile,
        leftStickX: 0.55f,                          // 0.10 in [-1..+1] = 0.55 in [0..1]
        leftStickY: 0.35f,                          // -0.30 in [-1..+1] = 0.35
        leftTrigger: 0.75f);                        // Slider→LeftTrigger via classifier
    axes[HMAxis.Rudder]   = 0.25f;                  // rudder pedal slight left
    axes[HMAxis.Throttle] = 1.00f;                  // secondary throttle full
    ctrlHotasFull.SubmitState(new HMGamepadState { Axes = axes });
    Console.WriteLine($"    Submitted: stick + Slider=0.75 + Rudder=0.25 + Throttle=1.00");
    Thread.Sleep(500);
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
// touchpad/gyro region. XInput delivery is via the XUSB companion.
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
        Axes = HMGamepadStateHelpers.StandardAxes(x360.Profile,
            leftStickX:   0.75f,    // 0.5 in [-1..+1] = 0.75 in [0..1]
            leftStickY:   0.35f,    // -0.3 in [-1..+1] = 0.35
            rightStickX:  0.40f,    // -0.2 in [-1..+1] = 0.40
            rightStickY:  0.85f,    // 0.7 in [-1..+1] = 0.85
            leftTrigger:  0.8f,     // Combined Z synthesis fills Z in DI; Vx/Vy carry separate
            rightTrigger: 0.4f),
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
        Axes = HMGamepadStateHelpers.StandardAxes(xsBt.Profile,
            leftStickX:   0.85f,    // 0.7 in [-1..+1] = 0.85 in [0..1]
            leftStickY:   0.65f,    // 0.3 in [-1..+1] = 0.65
            rightStickX:  0.25f,    // -0.5 in [-1..+1] = 0.25
            rightStickY:  0.45f,    // -0.1 in [-1..+1] = 0.45
            leftTrigger:  1.0f,
            rightTrigger: 0.0f),
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
        Axes = HMGamepadStateHelpers.StandardAxes(ds.Profile,
            leftStickX:  0.35f,    // -0.3 in [-1..+1] = 0.35 in [0..1]
            leftStickY:  0.80f,    // 0.6 in [-1..+1] = 0.80
            leftTrigger: 0.5f),
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
// The Switch Pro is a FUNCTIONAL protocol device (issue #33): the driver
// answers the Nintendo init + subcommand stream (0x80 handshake, device
// info, SPI calibration reads, input-mode switch) and streams input
// report 0x30 at the wire's 60 Hz, so SDL's HIDAPI driver and Steam
// complete their handshake against it. Consumers just SubmitState:
// buttons/sticks map through the profile layout, and the calibrated IMU
// channel (AccelG* in g, GyroDps* in deg/s, Switch frame: +X toward the
// player, +Y left, +Z up) rides the same call. Rumble comes back on
// OutputDecoded as leftMotor/rightMotor bytes.
Console.WriteLine("\n  12d. Switch Pro Controller: SubmitState with IMU");
{
    var swProfile = ctx.GetProfile("switch-pro")!;
    Console.Write($"  Creating {swProfile.Name}... ");
    using var sw2 = ctx.CreateController(swProfile);
    Console.WriteLine("OK");

    sw2.OutputDecoded += (_, e) =>
    {
        if (e.Fields.TryGetValue("leftMotor", out var l))
            Console.WriteLine($"  [switch rumble] left={l} right={e.Fields["rightMotor"]}");
    };

    // A pressed (layout buttonIndex 1 = face_a), gentle yaw, gravity at
    // rest. The SDK's SwitchProPacker converts to the 0x30 wire format
    // (12-bit packed sticks, int16 IMU at Switch scaling) and the
    // driver's streamer serves it with live timer/battery bytes.
    var swState = new HMGamepadState
    {
        Buttons = (HMButton)(1u << 1),
        AccelGZ = 1.0f,      // 1 g: flat on the table
        GyroDpsY = 35.0f,    // gentle tilt around Y
    };
    for (int i = 0; i < 60; i++)
    {
        sw2.SubmitState(swState);
        Thread.Sleep(15);
    }
    Console.WriteLine("  Streamed Switch Pro input with A + IMU for ~1s");
}

Console.WriteLine("\n=== Demo complete — disposing all controllers ===");
// using-statements handle cleanup of all controllers and ctx.
