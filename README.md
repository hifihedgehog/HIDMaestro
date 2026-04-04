# HIDMaestro

Universal game controller emulation for Windows. One virtual device, every API — DirectInput, XInput, SDL3, and browser Gamepad API — with exact hardware identity match.

## What It Does

HIDMaestro creates a virtual HID controller that is indistinguishable from real hardware across all Windows input APIs. Feed it data from any source — a physical controller, network stream, automation script — and every game, emulator, and browser sees a genuine controller.

- **DirectInput**: Correct axis count, button count, and VID/PID
- **XInput**: Full gamepad with triggers, sticks, and rumble capability
- **SDL3/HIDAPI**: Correct controller identity with Bluetooth bus type
- **Browser Gamepad API**: Single STANDARD GAMEPAD entry with vibration
- **No kernel driver**: Pure UMDF2 user-mode architecture
- **No EV certificate**: Self-signed test certificates work
- **No reboot**: Hot-plug device creation and removal

## Novel Technique: BTHLEDEVICE Bus Type Spoofing

HIDMaestro introduces a previously undocumented technique for making virtual HID devices appear as Bluetooth controllers to HIDAPI and SDL3, **without any Bluetooth hardware, drivers, or kernel components**.

### The Discovery

HIDAPI's Windows backend detects bus type by checking the parent device node's **CompatibleIDs** registry property for the string `BTHLEDEVICE`. This property can be set from user-mode on ROOT-enumerated devices via `SetupDiSetDeviceRegistryPropertyW` with `SPDRP_COMPATIBLEIDS`.

By including `BTHLEDEVICE\{00001812-0000-1000-8000-00805f9b34fb}` in the CompatibleIDs at device creation time, HIDAPI reports `bus_type = HID_API_BUS_BLUETOOTH` — causing SDL3 to use its Bluetooth Xbox controller parsing path (hardcoded byte offsets) instead of the GIP wire protocol parser (which produces zeros for virtual devices).

### Why This Matters

Prior to this discovery, achieving Bluetooth identity on a virtual controller required a kernel-mode bus driver (KMDF) to set `DEVPKEY_Device_BusTypeGuid` via `WdfDeviceSetBusInformationForChildren`. This technique eliminates that requirement entirely.

| Approach | Requires | Bus Type Target |
|----------|----------|-----------------|
| KMDF bus driver | Kernel driver + EV cert | `DEVPKEY_Device_BusTypeGuid` (for GameInput) |
| **BTHLEDEVICE CompatibleIDs** | **User-mode only** | **HIDAPI `bus_type` (for SDL3)** |

No existing open-source project (ViGEmBus, DsHidMini, VHF, or others) uses this technique. HIDAPI performs no validation on the CompatibleIDs — it trusts the string match unconditionally.

### The &IG_ Enumerator Trick

A second discovery enables single-entry browser detection. By using `VID_*&PID_*&IG_00` as the device enumerator name, the HID child's device path contains `&IG_`, which:

- **Chrome RawInput** skips (prevents duplicate raw HID gamepad entry)
- **HIDAPI** skips (by design for XInput-handled devices)
- **SDL3 RawInput backend** still detects the device (falls through from HIDAPI, maps by VID/PID from controller database)
- **WGI** still fires GamepadAdded (uses WinExInput interface, not device path)

The key insight: SDL3 has fallback backends. When HIDAPI skips a device, SDL3's RawInput backend detects it independently by VID/PID.

## Comparison with Existing Projects

| Feature | HIDMaestro | ViGEmBus | DsHidMini | vJoy | VHF |
|---------|-----------|----------|-----------|------|-----|
| **Kernel driver required** | No | Yes (KMDF) | No (UMDF2) | Yes (KMDF) | Yes (KMDF) |
| **EV certificate required** | No | Yes | No | Yes | Yes |
| **Physical hardware required** | No | No | Yes (DS3) | No | No |
| **Arbitrary controller identity** | Yes (JSON profiles) | No (Xbox 360 + DS4 only) | No (DS3 modes only) | No (fixed joystick) | Yes (but kernel-only) |
| **Bluetooth bus type spoofing** | Yes | No | No (real BT via BthPS3) | No | No |
| **Single browser gamepad entry** | Yes | Yes | N/A | No | N/A |
| **XInput** | Yes | Yes | Yes (XIH mode) | No | Depends on descriptor |
| **DirectInput** | Yes (5 axes) | Yes | Yes | Yes | Depends on descriptor |
| **SDL3/HIDAPI with correct identity** | Yes (BT bus type) | Partial | Yes (real HW) | Generic only | No |
| **Hot-plug (no reboot)** | Yes | Yes | N/A (real HW) | No | Yes |
| **Data-driven profiles** | Yes (any controller) | No (2 types) | No (5 fixed modes) | No (1 type) | N/A (framework) |
| **Open source** | Yes (MIT) | Yes (BSD-3) | Yes (BSD-3) | Yes (MIT) | No (Microsoft) |
| **Status** | Active | Retired | Active | Stale | Shipping (in-box) |

**What only HIDMaestro does:**
- Spoofs Bluetooth bus type from user-mode (BTHLEDEVICE CompatibleIDs)
- Achieves single browser entry without kernel driver (&IG_ enumerator trick)
- Emulates any controller from a JSON profile without kernel components
- Full API coverage (DirectInput + XInput + SDL3 + Browser) from pure user-mode

## Architecture

```
                    +---------------------------+
                    |    Shared File (input.bin) |
                    |  SeqNo + HID Data + GIP   |
                    +---------------------------+
                           |             |
                    +------+------+  +---+----+
                    | Companion   |  | XUSB   |
                    | HIDMaestro  |  | HMXInput|
                    | .dll        |  | .dll   |
                    +------+------+  +---+----+
                           |             |
                    +------+------+  +---+----+
                    | ROOT\VID_*  |  | ROOT\  |
                    | &IG_00\0000 |  | SYSTEM |
                    | (HIDClass)  |  | (System)|
                    +------+------+  +--------+
                           |             |
                      SDL3/DInput     XInput
                      WGI/Browser     Browser
```

**Gamepad Companion** (`HIDMaestro.dll` via `mshidumdf`):
- UMDF2 HID minidriver with timer-based input from shared file
- Registers USB + WinExInput interfaces (no XUSB — prevents duplicate XInput)
- BLE-compatible HID descriptor with Report ID 0x01
- BTHLEDEVICE CompatibleIDs for Bluetooth bus type spoofing

**XUSB Companion** (`HMXInput.dll`):
- Separate System-class UMDF2 function driver
- Handles XUSB IOCTLs (GET_STATE, GET_CAPABILITIES, GET_INFORMATION)
- Reads GIP-format data from shared file for XInput delivery
- Registers XUSB interface only

## Building

Requirements: Visual Studio 2022+, Windows SDK/WDK 10.0.26100.0

```batch
# Build main driver
scripts\build.cmd

# Build XUSB companion
# (see tmp\build_companion.cmd for reference)
```

## Testing

```batch
# Run Xbox Series X|S BT profile
test\HIDMaestroTest\bin\Debug\net10.0-windows\win-x64\HIDMaestroTest.exe emulate xbox-series-xs-bt

# Validate all APIs
python test\validate.py
```

## Profile System

Controller profiles are JSON files in `profiles/` containing:
- VID/PID and product strings
- Raw HID report descriptor
- Trigger mode (separate/combined)
- Driver mode (xinputhid/hid)

## Credits

- **[DsHidMini](https://github.com/nefarius/DsHidMini)** by Nefarius Software Solutions — Pioneered the UMDF2 + xinputhid architecture for virtual game controllers on Windows. HIDMaestro builds on this foundational approach.
- **[HIDAPI](https://github.com/libusb/hidapi)** — The bus type detection mechanism that enables the BTHLEDEVICE spoofing technique.
- **[SDL3](https://github.com/libsdl-org/SDL)** — Multi-backend fallback architecture that enables the &IG_ trick.

## License

MIT License. See [LICENSE](LICENSE) for details.
