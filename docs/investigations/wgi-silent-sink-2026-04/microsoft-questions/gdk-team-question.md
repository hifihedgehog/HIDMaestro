# GameInput `supportedRumbleMotors = 0` on ROOT-enumerated UMDF2 XUSB-class virtual

**Windows build:** 11 26200.8115
**GameInput runtime:** `C:\Windows\System32\GameInput.dll` (in-box, 2026-04-14 build)
**Language:** C++ via `GameInput.h` (Windows SDK 10.0.26100.0)

## Setup

We have a ROOT-enumerated UMDF2 virtual controller that declares the XUSB interface class via a class-filter extension INF. It enumerates correctly in GameInput via `IGameInput::RegisterDeviceCallback(GameInputKindGamepad, ...)`:

```
GameInputDeviceInfo returned for our virtual:
  vendorId:               0x045E
  productId:              0x028E
  supportedInput:         0x0004000E  (GameInputKindGamepad | GameInputKindController)
  controllerButtonCount:  10
  controllerAxisCount:    7
  controllerSwitchCount:  1
  supportedRumbleMotors:  0x0  ← ZERO
```

A physical Xbox 360 Wired on the same machine enumerates with identical VID/PID/button/axis counts but `supportedRumbleMotors = 0x3` (LowFrequency | HighFrequency).

## Observation

Calling `IGameInputDevice::SetRumbleState(&params)` from a focused Win32 GUI process:

- Against the physical 360 or Xbox Series BT: physical rumbles.
- Against our virtual: no physical effect, and zero motor bytes arrive at our driver's instrumented IOCTL handlers (xusb22-replica path, xinputhid-filter path).

The virtual is fully addressable via direct `xinput1_4.XInputSetState` (motor bytes arrive at our driver with correct xusb22-style wire layout). It's only the GameInput `SetRumbleState` and the equivalent WGI `Gamepad::put_Vibration` paths that silently drop dispatch.

## Question

How does GameInput populate `supportedRumbleMotors` for XUSB-class devices?

Specifically:

1. Is the value derived from a property on the device's XUSB interface registration (DEVPKEY, device interface property, INF directive) that we can set from our class-filter extension INF?
2. Is it tied to device enumeration path (USB-bus presence vs. ROOT enumerator), with no user-mode override?
3. If (2), is there a supported pattern for a ROOT-enumerated UMDF2 virtual to declare haptic motor support — perhaps via a specific XUSB interface property, a device capability interface, or registration through the `Windows.Gaming.Input.Custom` namespace in a way that propagates to cross-process consumers?

We have tested `GameControllerFactoryManager::RegisterCustomFactoryForHardwareId` and `RegisterCustomFactoryForXusbType`. The factory's `CreateGameController` is invoked, but `Gamepad.Gamepads[i]` returns the built-in `Windows.Gaming.Input.Gamepad` type rather than our custom controller — so consumer processes don't see the override. Is this expected, and if so, is there a different Custom-namespace API that IS visible across process boundaries?

## Context

HIDMaestro is an open-source user-mode virtual controller project. The constraint that forces a ROOT-enumerated UMDF2 design (rather than a kernel bus driver) is intentional — it's what makes HIDMaestro installable without EV cert signing. We're specifically looking for a user-mode-only path to declare haptic motor support to GameInput.

Happy to share full investigation trace, a minimal reproducer, or coordinate on an empirical test case.
