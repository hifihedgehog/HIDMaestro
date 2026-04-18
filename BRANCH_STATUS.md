# Branch status: `v1-dev-experiment-xusb-child-pdo`

**Created to answer:** can HIDMaestro virtuals receive browser-dispatched rumble on Win11 26200+ by registering the XUSB interface class (and related WGI interface GUIDs) on the virtual's HID child via a UMDF2 upper filter?

**Answer:** No. See [docs/investigations/wgi-silent-sink-2026-04/finding.md](docs/investigations/wgi-silent-sink-2026-04/finding.md).

## What this branch adds over master

- **`driver/xusbshim.c`** (new, 744 lines) — UMDF2 HID upper filter that registers `GUID_DEVINTERFACE_XUSB`, `GUID_DEVINTERFACE_WINEXINPUT`, and additional WGI enumeration GUIDs on the virtual's HID child. Implements XUSB IOCTL handlers (SET_STATE / GET_STATE / GET_CAPABILITIES / GET_INFORMATION / WAIT_GUIDE) that mirror `xusb22.sys` + `xinputhid.sys` behavior.
- **`driver/hidmaestro_xusbshim.inf` + `hidmaestro_xusbshim_class.inf`** — INFs that bind xusbshim as an extension INF on HID children matching VID 045E & select PIDs.
- **`driver/companion.c` changes** — HMCOMPANION also registers additional WGI enumeration GUIDs; diagnostic logging to `xusbshim_log.txt` for cross-path visibility with the filter side.
- **`driver/driver.c` changes** — `LogHidOutputReport` helper for HID output IOCTL instrumentation; claims `XINPUT_CAPS_FFB_SUPPORTED` flag in GET_CAPABILITIES (experiment-specific).
- **`profiles/microsoft/xbox-360-wired-ffb.json`** — experimental Xbox 360 Wired profile variant with a 22-byte PID force-feedback collection appended to the HID descriptor. **Unexplored** — starting point for Y-alt-A in the finding.
- **SDK internal changes** — `DeviceOrchestrator`, `DriverBuilder`, `PnputilHelper` adjustments for multi-INF install orchestration and the xusbshim Extension INF pipeline.
- **`scripts/build.cmd` + `build_all.cmd`** — build rules for the xusbshim DLL and its INF packaging.

## What works (post-fix, on this branch)

- Direct `xinput1_4.XInputSetState` dispatches reach our virtual via xinputhid kernel filter → xusbshim upper filter. Motor bytes decode correctly at `in[2]`/`in[3]` (xusb22-style; see `driver/xusbshim.c:550-558`).
- Capability probes from WGI arrive at both instrumentation layers.

## What does NOT work (per the finding)

- `Windows.Gaming.Input.Gamepad::put_Vibration` (including Chromium/Edge `vibrationActuator.playEffect`) produces zero motor-bearing bytes at any driver layer despite the interface registration. WGI's XUSB dispatch appears hard-gated on USB-bus enumeration, which this branch does not address.
- `IGameInputDevice::SetRumbleState` similarly silent-sinks the virtual; `supportedRumbleMotors = 0x0` regardless of the interface registration.

## Residual experimental value

- **Y-alt-A starting point:** `profiles/microsoft/xbox-360-wired-ffb.json` + the xusbshim infrastructure could be adapted to explore "reclassify virtual as HID with PID TLC so WGI's `hidforcefeedback.cpp` backend populates motors." The finding discusses this as a blocked fix vector (loses XUSB slot visibility for XInput consumers), but if someone wants to explore it anyway, start here.
- **Interface-registration testbed:** xusbshim registers 4 WGI enumeration interface GUIDs on the HID child. Useful as a diagnostic harness for future "does WGI behave differently when interface X is registered" questions.
- **Branch-local canonicalization is fixed:** `driver/xusbshim.c:550-558` uses the empirically verified wire layout (`lo = in[2]; hi = in[3];`). Post-parser-fix state, not the original heuristic.

## Status

**Dead end for the original browser-vibration objective.** Infrastructure kept available for future Y-alt-A exploration or interface-registration experiments. Not recommended for merge to master in its current form — the xusbshim layer is redundant with HMCOMPANION for direct XInput dispatch, and the WGI silent-sink is architectural per the finding.

## Tag

Snapshot tagged at this branch state: `archive/wgi-silent-sink-closeout-2026-04`.

## See also

- [docs/investigations/wgi-silent-sink-2026-04/finding.md](docs/investigations/wgi-silent-sink-2026-04/finding.md) — formal finding this branch produced
- [docs/investigations/wgi-silent-sink-2026-04/investigation-history.md](docs/investigations/wgi-silent-sink-2026-04/investigation-history.md) — session-by-session narrative
- [test/probes/](test/probes/) — diagnostic tools developed on this branch, promoted to master
