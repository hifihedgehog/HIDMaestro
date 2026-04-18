# WGI XUSB haptic dispatch gating on ROOT-enumerated UMDF2 virtuals

**Windows build:** 11 26200.8115 (Kits 10.0.26100.0)
**Device class:** XUSB (Xbox 360 / Xbox One compatible controllers)
**Component:** `Windows.Gaming.Input.dll` → `Gamepad::put_Vibration` dispatch path

## Setup

We maintain a user-mode virtual HID controller project (HIDMaestro) that creates ROOT-enumerated UMDF2 virtuals with the VID/PID of supported physical controllers. For the Xbox 360 Wired profile (VID 045E / PID 028E), we register the XUSB interface class via a class-filter extension INF (`hidmaestro_xusbshim_class.inf`) on our HID child (`HID\VID_045E&PID_028E&IG_00\...`). A companion UMDF2 service (`HMCOMPANION`) implements the xusb22-equivalent IOCTL handler at the ROOT level.

**Driver stack (our virtual):** `HidHide → WUDFRd → mshidumdf`, with HMCOMPANION bound as xusb22 replica.
**Driver stack (physical Xbox 360 Wired):** `HidHide → HidUsb`, with xusb22.sys on USB composite parent.

## Observation

Direct `xinput1_4.dll::XInputSetState(slot, &vibration)` works correctly against our virtual. We capture the raw IOCTL_XUSB_SET_STATE input buffer at our UMDF2 upper filter with the expected wire layout `[0x00, 0x00, LEFT_motor, RIGHT_motor, 0x02]` and dispatch motor bytes to the consumer application.

However, `Windows.Gaming.Input.Gamepad::put_Vibration` from any caller — including Chromium Edge 147 `GamepadHapticActuator.playEffect` — produces **zero motor-bearing SET_STATE bytes at our driver** across all three instrumentation points (xinputhid-filter path, xusb22-replica path, HID output report handlers), even with the caller satisfying the documented focus gate.

WGI enumeration probes DO arrive at our driver. Three-layer log excerpt during a Chromium click window:

```
[HMCOMP] IOCTL 8000A010 [00 0D 00 00 01]      # WGI capability probe (xusb22-replica path)
[18:47:48.480] SET_STATE-in 00 0D 00 00 01    # same probe on xinputhid-filter path
[HMCOMP] IOCTL 8000A010 [00 00 00 00 02]      # idle-state clear
[18:47:51.995] SET_STATE-in 00 00 00 00 02
[HMCOMP] IOCTL 8000A010 [00 0D 00 00 01]      # subsequent probe
[18:47:59.991] SET_STATE-in 00 0D 00 00 01
```

A physical USB-enumerated Xbox 360 Wired on the same machine, same browser session, same click sequence, receives motor bytes correctly (user-verified by tactile rumble).

## Hypothesis

Our source-file footprint scan of `Windows.Gaming.Input.dll` (via embedded `__FILE__` strings) shows dedicated modules for `hidforcefeedback.cpp`, `gipforcefeedback.cpp`, and `hapticfeedbackmotor.cpp`, but **no `xusbforcefeedback.cpp`**. XUSB vibration dispatch appears to live inline in `xusbdevice.cpp`, and empirically appears to resolve the dispatch target by querying `xusb22.sys` on a USB composite parent. Our ROOT-enumerated virtual has no USB parent, so this resolution returns nothing and `put_Vibration` silently no-ops.

## Question

Is WGI's XUSB haptic dispatch hard-gated on USB-bus enumeration of the target device?

If so, is there a documented mechanism for a UMDF2 virtual device registered under the ROOT enumerator to appear as a valid WGI haptic dispatch target — for example, by satisfying a specific device interface property, a registry value under the driver's device key, or a DEVPKEY we can set from the filter driver?

If the gate is not USB-enumeration per se, what is it, and can it be satisfied from UMDF2 without a kernel-mode bus driver?

## Contact

HIDMaestro project (open-source, github.com/<project owner>/HIDMaestro). Happy to share the full investigation trace, reproducer, or coordinate on an empirical test case if useful.
