# HIDMaestro

**The first virtual game controller that's indistinguishable from real hardware — without kernel drivers, EV certificates, or reboots.**

HIDMaestro creates virtual game controllers that work perfectly across every Windows input API. Games, emulators, browsers, and input libraries all see genuine hardware. No compromises, no workarounds, no kernel mode.

## Why This Matters

Every existing virtual controller solution requires you to give something up:

- **vJoy** needs a kernel driver and the original developer abandoned it years ago. Community forks have to maintain driver signing. And it always shows up as "vJoy Device" — never as a real controller.
- **ViGEmBus** needs a kernel driver AND an EV code signing certificate ($300+/year). It was retired because of these maintenance burdens.
- **DsHidMini** creates virtual controllers but requires a physical DualShock 3 connected — it translates real hardware, not arbitrary input sources.
- **VHF** is a Microsoft kernel framework. Kernel mode, full stop.

**HIDMaestro needs none of that.** It runs entirely in user mode. It signs with any code signing certificate. It creates and removes controllers instantly without rebooting. And every API — DirectInput, XInput, SDL3, Chrome Gamepad API — sees exactly what real hardware would produce.

## What Makes It Different

### No Kernel Driver
HIDMaestro uses UMDF2 (User-Mode Driver Framework). Your driver runs in a regular Windows process, not the kernel. A bug in HIDMaestro can't blue-screen your machine. You don't need an EV certificate. You don't need WHQL. A standard code signing certificate (or even a self-signed test cert during development) is all it takes.

### Exact Hardware Identity
Pick any controller — Xbox 360, Xbox Series X, DualSense, flight stick, racing wheel — and HIDMaestro becomes that controller. Not "similar to" or "compatible with." The exact VID/PID, product string, HID descriptor, axis count, button count, trigger behavior, and bus type. SDL3's controller database matches it. Steam recognizes it. Chrome identifies it. joy.cpl shows the right name.

### Every API at Once
Most solutions get one or two APIs right. HIDMaestro gets all of them simultaneously:

| API | What HIDMaestro Delivers |
|-----|-------------------------|
| **DirectInput** | Correct axes, buttons, POV, VID/PID |
| **XInput** | Separate triggers, proper button mapping, single slot |
| **SDL3/HIDAPI** | Correct identity, Bluetooth bus type spoof |
| **Browser Gamepad** | STANDARD GAMEPAD with separate triggers |
| **WGI (GameInput)** | Proper Gamepad promotion via GameInput registry |

### Instant Hot-Plug
Create a controller in milliseconds. Remove it in milliseconds. Switch between profiles — Xbox 360 to Xbox Series X to DualSense — with zero downtime and zero reboots. The previous controller vanishes cleanly and the new one appears immediately.

### Profile-Based
Every controller is a JSON file. VID, PID, descriptor, trigger mode, connection type — all data-driven. Adding support for a new controller means writing a JSON file, not modifying code. The profiles directory already includes Xbox 360, Xbox One, Xbox Series X|S, Elite controllers, and more.

## Novel Techniques

HIDMaestro introduces several techniques that have never been published or used in any open-source project.

### Velocity Usage Descriptor Trick

**The breakthrough that makes Xbox 360 emulation perfect.**

Real Xbox 360 controllers have a combined trigger axis (Z) in DirectInput — both triggers share one axis. But browsers and WGI need separate trigger values. Every previous solution had to choose: correct DI (5 axes, combined) or correct browser (separate triggers, 6 axes).

HIDMaestro uses HID velocity usages (Vx and Vy, Usage Page 0x01, Usages 0x40/0x41) to carry separate trigger values in the same HID report. DirectInput doesn't map velocity usages to any axis slot — it sees 5 axes. GameInput/WGI enumerates them as additional axes and reads real separate trigger data via the GameInput registry mapping.

**Result: 5 axes and 10 buttons in DirectInput (matching real xusb22.sys), separate triggers in the browser (matching real XInput), all from one HID descriptor. No other virtual controller has achieved this.**

### BTHLEDEVICE Bus Type Spoofing

HIDAPI detects Bluetooth controllers by checking for `BTHLEDEVICE` in the device's CompatibleIDs. HIDMaestro sets this property from user mode during device creation — no Bluetooth hardware, no kernel bus driver.

SDL3 then uses its Bluetooth-specific controller parsing path, which handles the descriptor correctly. Without this spoof, SDL3's default parser produces zeros for certain virtual device configurations.

### &IG_ Enumerator Trick

By using `VID_*&PID_*&IG_00` as the device enumerator, the HID child's device path contains `&IG_`. This has three simultaneous effects:

- **Chrome RawInput** skips it (prevents duplicate gamepad entries)
- **HIDAPI** skips it (by design for XInput-handled devices)
- **SDL3** still detects it (falls through to RawInput backend, maps by VID/PID)

One string in a device path controls three different detection paths across three different libraries.

### GameInput Registry Override

Windows has a built-in GameInput mapping database for known VID/PIDs. HIDMaestro writes custom mappings that point the trigger axes to the velocity usage indices (5 and 6 instead of the default combined axis 4). This makes WGI's Gamepad object read actual separate trigger values from the Vx/Vy fields.

## Comparison

| Feature | HIDMaestro | ViGEmBus | DsHidMini | vJoy |
|---------|-----------|----------|-----------|------|
| Kernel driver required | **No** | Yes | No (UMDF2) | Yes |
| EV certificate required | **No** | Yes | No | Yes |
| Physical hardware required | **No** | No | Yes (DS3) | No |
| Arbitrary controller identity | **Yes** | No (2 types) | No (DS3 only) | No (fixed) |
| Bluetooth bus type spoof | **Yes** | No | No | No |
| Single browser gamepad entry | **Yes** | Yes | N/A | No |
| XInput with separate triggers | **Yes** | Yes | Yes | No |
| DirectInput with correct axes | **Yes** | Yes | Yes | Partial |
| SDL3 with correct identity | **Yes** | Partial | Yes (real HW) | No |
| Hot-plug without reboot | **Yes** | Yes | N/A | No |
| Data-driven profiles | **Yes** | No | No | No |
| Status | **Active** | Retired | Active | Stale |

## Architecture

```
User-Mode Test App
  │ Writes input data to per-controller shared file
  │ Manages device lifecycle (create, configure, remove)
  │
  ├──► Shared File (input_0.bin)
  │     SeqNo(4) + HID Report(64) + GIP Data(14) = 86 bytes
  │
  ├──► Main HID Device (HIDMaestro.dll via mshidumdf)
  │     ROOT\VID_045E&PID_028E&IG_00\0000
  │     ├─ HID descriptor with Vx/Vy velocity triggers
  │     ├─ Timer reads shared file → HID READ_REPORT
  │     ├─ USB + WinExInput interfaces
  │     └─ BTHLEDEVICE CompatibleIDs (Bluetooth profiles)
  │
  └──► XUSB Companion (HMXInput.dll, XnaComposite class)
        ROOT\HMCompanion\0000
        ├─ XUSB interface → XInput discovery
        ├─ WinExInput interface → WGI GamepadAdded
        ├─ Timer reads GipData from shared file
        └─ Handles GET_STATE/GET_CAPABILITIES/SET_STATE IOCTLs
```

**Data flows:**
- **DirectInput** ← HID READ_REPORT ← shared file (combined Z + Vx/Vy in descriptor)
- **XInput** ← XUSB GET_STATE ← companion reads GipData from shared file
- **SDL3** ← HIDAPI skips (&IG_) → RawInput fallback → maps by VID/PID
- **Browser** ← WGI Gamepad ← GameInput reads Vx/Vy via registry mapping
- **Bluetooth ID** ��� HIDAPI checks CompatibleIDs → reports bus_type=BT

## Getting Started

Requirements: Visual Studio 2022+, Windows SDK/WDK 10.0.26100.0, .NET 10

```bash
# Build and run (self-contained — handles cert, build, sign, install automatically)
cd test\HIDMaestroTest
dotnet build -c Debug
bin\Debug\net10.0-windows10.0.26100.0\win-x64\HIDMaestroTest.exe emulate xbox-360-wired

# List available profiles
HIDMaestroTest.exe list

# Validate all APIs
python test\validate.py xbox-360-wired
```

The test app is fully self-contained. On first run it:
1. Creates a test signing certificate (if needed)
2. Builds the driver DLLs from source
3. Signs everything
4. Installs driver packages
5. Creates the virtual controller
6. Starts feeding test input data

No external scripts, no manual setup, no popups. Just the one console window.

## Profile System

Controller profiles are JSON files in `profiles/`:

```json
{
  "id": "xbox-360-wired",
  "name": "Xbox 360 Controller (Wired)",
  "vid": "0x045E",
  "pid": "0x028E",
  "productString": "Controller (XBOX 360 For Windows)",
  "descriptor": "05010905a101a100093009311500...",
  "inputReportSize": 18,
  "triggerMode": "combined",
  "connection": "usb"
}
```

The descriptor field contains the raw HID report descriptor as hex. The test app parses it, builds input reports, and feeds data through the shared file. Adding a new controller is a matter of capturing its descriptor and writing a JSON file.

## Credits

- **[DsHidMini](https://github.com/nefarius/DsHidMini)** by Nefarius Software Solutions — Pioneered the UMDF2 + xinputhid architecture for virtual game controllers on Windows. HIDMaestro builds on this foundational approach.
- **[HIDAPI](https://github.com/libusb/hidapi)** — The bus type detection mechanism that enables the BTHLEDEVICE spoofing technique.
- **[SDL3](https://github.com/libsdl-org/SDL)** — Multi-backend fallback architecture that enables the &IG_ trick.

## License

MIT License. See [LICENSE](LICENSE) for details.
