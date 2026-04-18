# WGI Silent Sink on ROOT-Enumerated UMDF2 XUSB Virtuals

**Investigation date:** 2026-04-18
**Windows build:** 11 26200.8115 / 26100 Kits
**Browser build:** Microsoft Edge 147.0.3912.72
**Driver:** HIDMaestro master @ parser-fix commit (xusbshim.c:550-558)

## Executive summary

On Windows 11 build 26200+, `Windows.Gaming.Input.Gamepad.put_Vibration` does not deliver motor bytes to ROOT-enumerated UMDF2 XUSB-class virtuals, even though WGI enumeration probes (`IOCTL_XUSB_SET_STATE 00 0D 00 00 01`) do arrive. Physical USB-enumerated Xbox 360 Wired controllers and Xbox Series BT receive motor bytes correctly on the same call path, same Windows session, same browser process. Direct `xinput1_4.XInputSetState` from any process reaches our virtual's XUSB filter with correct motor bytes.

Chromium/Edge dispatches gamepad haptics via `put_Vibration` (upstream Chromium `device/gamepad/wgi_gamepad_device.cc:SetVibration`); no XInput haptic fallback exists in current Chromium main. The practical outcome: `vibrationActuator.playEffect` in Edge/Chrome silently no-ops for HIDMaestro virtuals while working on physical Xbox-family controllers.

This finding has architectural explanation (no `xusbforcefeedback.cpp` in Windows.Gaming.Input.dll; XUSB vibration dispatch lives inline in `xusbdevice.cpp` and appears to resolve a USB-bus-enumerated target). All four fix vectors considered are either upstream of HIDMaestro or violate the hard "no kernel drivers" constraint. Option 3 — document the limitation — is the committed conclusion.

## Methodology note — what went wrong, what was fixed

This investigation produced multiple premature conclusions before landing on the correct finding. The process failures are listed plainly because they are load-bearing context: the "silent sink" framing was asserted and retracted twice before it was supportable on clean evidence.

### The three failures

1. **Guide regression contaminated the noise floor.** A test-pattern commit reintroduced `HMButton.Guide` into `SdkDemo` and `HIDMaestroTest` continuous pulse loops. Windows Xbox UI fires a Guide-long-press haptic (`hi=0x7F` short burst) in response, which landed in the driver log and was initially misattributed to "explorer.exe ambient noise" via process A/B/A suspension. The correct reading — "our own test pattern is triggering the shell haptic ack" — was flagged by the user, who recognized the `0x7F` pattern signature. A regression guard (`scripts/check_no_guide_in_pulse.ps1`) now fails the build on any `HMButton.Guide` reference in `example/` or `test/` without an explicit `// ALLOW-GUIDE:` allowlist comment.

2. **"Silent sink" claimed on one instrumentation layer, then committed as architectural.** The initial silent-sink finding was asserted after observing zero SDK-level `[out0]` lines during Chromium clicks. This inferred "no bytes arrived at the driver" from "no bytes decoded by the SDK's XInput parser." Under independent oversight review, the claim was retracted: absence at the parser layer is not evidence of absence at the driver reception layer. Re-run required raw-byte dumps at all three driver-side instrumentation points (`SET_STATE-in` xinputhid-filter, `[HMCOMP]` xusb22 companion, HID-WRITE/SETOUT/SETFEAT handlers) filtered by click-window timestamp, independent of parser interpretation.

3. **Branch-local canonicalization bug contaminated instrumentation for hours.** The xusbshim canonicalization heuristic in [driver/xusbshim.c:550-558](../../../driver/xusbshim.c#L550-L558) preferred "xinputhid-style motors at `in[3]/in[4]`" when those bytes were non-zero. For every rumble frame on the real xinputhid path, `in[4]` is the packet trailer `0x02`, which the heuristic treated as the high motor value. This produced the "hi stuck at 2" symptom during XInputTester slider sweeps. The author explained the symptom as "user's right slider was at 0.78% intensity" rather than running the 30-second slider-isolation test that would have falsified that explanation immediately. Third identical failure mode in one investigation. Fixed by replacing the heuristic with a deterministic `lo = in[2]; hi = in[3];` decode after an XInputSetState probe confirmed the wire layout (see Evidence Matrix below).

   **Scope note:** `driver/xusbshim.c` is new on the `v1-dev-experiment-xusb-child-pdo` branch (the branch on which this investigation was conducted). It does not exist on master. The "parser bug" was an in-experiment regression in branch-local code, not a pre-existing bug in master's driver. Master's `driver/companion.c` publishes raw XUSB IOCTL buffers without canonicalization and has never had this class of wire-format-detection bug. The methodology lesson applies identically: raw-byte verification at the reception layer catches instrumentation contamination regardless of whether the contamination came from a pre-existing bug or an experimental code change. Future experimental branches that introduce new canonicalization/decode logic should verify it against a deterministic probe (see [test/probes/xinput_byte_probe](../../../test/probes/xinput_byte_probe/)) before any dispatch-silence claim rests on its output.

### The three standing rules that emerged

These are transferable beyond this investigation. They are now recorded in the author's global `~/.claude/CLAUDE.md` as permanent practice.

1. **Anomalies in driver-side data get a 30-second confirmation test before any narrative explanation.** The parser-bug symptom ("hi stuck at 0x02") had an obvious check — move only one slider, watch the byte — that would have ruled out the "user's slider was at 0.78%" story in under a minute. When an anomaly appears, the first move is "what test would distinguish the explanations?" not "what explanation fits?"

2. **Raw-byte dumps at the driver's reception layer are ground truth; parser-filtered views are not.** Every "silent sink" hypothesis inherits uncertainty from the layer through which silence is observed. Driver-side logs (SET_STATE-in, HMCOMP, HID-WRITE/SETOUT/SETFEAT) record the raw IOCTL input buffers unconditionally of parser interpretation. Use them.

3. **Every "silent" claim requires a same-window positive control.** "Zero bytes for X" is uninformative without "N bytes for Y, same click, same log, same conditions." Without the positive control, the silence may reflect instrumentation inactivity or test-setup error rather than dispatch absence.

## Post-fix evidence matrix

All observations after xusbshim canonicalization fix, with raw-byte dumps at driver reception layer.

| Dispatch path | Our virtual (ROOT / UMDF2) | Physical Xbox 360 Wired (USB) | Xbox Series BT (BT + xinputhid) |
|---|---|---|---|
| Direct `xinput1_4.XInputSetState` from C# P/Invoke | SET_STATE-in arrives; `lo/hi` decode correctly (verified via deterministic probe: wLeft=0xFFFF → `byte[2]=0xFF`; wRight=0xFFFF → `byte[3]=0xFF`) | Physical rumbles | Physical rumbles |
| GameInput `IGameInputDevice::SetRumbleState` from focused Win32 GUI | SET_STATE-in arrives (focus gate satisfied) | Physical rumbles | Physical rumbles |
| WGI `Gamepad::put_Vibration` from focused Win32 GUI | Probe `00 0D 00 00 01` arrives; idle-clear `00 00 00 00 02` arrives; **zero motor-bearing bytes at any layer** | Physical rumbles | Physical rumbles |
| Chromium Edge 147 `vibrationActuator.playEffect` on hardwaretester.com | Same as above: probes + idle-clears only, zero motor bytes at any of three driver layers | Physical rumbles (user tactile confirmation) | Physical rumbles (user tactile confirmation) |

### Three-layer log excerpt — Chromium click window (18:46:04 → 18:48:00)

Chromium click targeted physical 360 first, then our virtual, within one truncated log window:

```
[HMCOMP] IOCTL 8000A010 [00 0D 00 00 01]      # WGI enumeration probe
[18:47:48.480] SET_STATE-in 00 0D 00 00 01    # same probe on xinputhid path
[HMCOMP] IOCTL 8000A010 [00 00 00 00 02]      # idle-state clear
[18:47:51.995] SET_STATE-in 00 00 00 00 02
[HMCOMP] IOCTL 8000A010 [00 00 00 00 02]
[18:47:51.995] SET_STATE-in 00 00 00 00 02
[HMCOMP] IOCTL 8000A010 [00 0D 00 00 01]      # subsequent probe
[18:47:59.991] SET_STATE-in 00 0D 00 00 01
```

Chromium talks to the virtual (probes arrive at both instrumentation layers). Physical 360 user-tactile-confirmed rumble during this window. **Zero motor-bearing SET_STATE bytes for our virtual at any driver layer.** Positive control (physical) and target (virtual) both in the same Chromium process, same click sequence.

### Parser-fix before/after (xusbshim canonicalization)

Before fix — XInputTester right slider 0→max→0 (left pinned):
```
raw[5B]=00 00 FD 02 00 | XInput rumble lo=253 hi=2    # hi STUCK at 2 (trailer misread)
raw[5B]=00 00 00 02 00 | XInput rumble lo=0 hi=2
```

After fix — deterministic `XInputSetState` probe:
```
wLeft=0xFFFF wRight=0x0000 → 00 00 FF 00 02   (byte[2]=left, byte[3]=right, byte[4]=trailer)
wLeft=0x0000 wRight=0xFFFF → 00 00 00 FF 02
wLeft=0x8080 wRight=0x4040 → 00 00 80 40 02
```

After fix — XInputTester 2D sweep (both sliders independently):
```
raw[5B]=00 00 00 7D 00 | XInput rumble lo=0 hi=125    # right slider alone
raw[5B]=00 00 FD 00 00 | XInput rumble lo=253 hi=0    # left slider alone
raw[5B]=00 00 FD FD 00 | XInput rumble lo=253 hi=253  # both maxed
```

## Architectural explanation

HID-child device stacks:
- **Physical 360:** `HidHide → HidUsb`, with `xusb22.sys` service bound to the USB composite device parent (`USB\VID_045E&PID_028E\...`).
- **Our virtual:** `HidHide → WUDFRd → mshidumdf`, no USB bus parent. HMCOMPANION (our UMDF2 service) registers the XUSB interface class via `hidmaestro_xusbshim_class.inf` extension INF.

Source-file footprint scan of `Windows.Gaming.Input.dll` (Win11 26200, 2026-04-14 build; embedded `__FILE__` strings extracted from the binary): there are dedicated `hidforcefeedback.cpp`, `gipforcefeedback.cpp`, and `hapticfeedbackmotor.cpp` modules, but **no `xusbforcefeedback.cpp`**. XUSB vibration dispatch lives inline in `xusbdevice.cpp`. Empirically, this inline handler appears to resolve the dispatch target by querying `xusb22.sys` on a USB composite parent. Our ROOT-enumerated virtual has no USB parent, so the resolution returns nothing and the call is silently dropped — the caller observes `put_Vibration` returning success with no observable side effects.

Direct `xinput1_4.XInputSetState` uses a different path: it opens the XUSB interface class symlink (which our xusbshim Extension INF registers for HID\VID_045E&PID_028E&IG_00) and sends IOCTL_XUSB_SET_STATE through it. That reaches our UMDF2 upper filter, which is why XInputSetState works while `put_Vibration` does not.

## Fix vectors considered and why each is blocked

1. **Reclassify virtual as HID with PID force-feedback TLC** — add a PID (Physical Interface Device) collection to the HID descriptor so `hidforcefeedback.cpp` routes dispatch via HID output reports. Blocked because reclassifying the virtual out of XUSB class loses XInput slot visibility, breaks `XInputGetState` consumers, and we have no evidence WGI's HID-FFB backend would route to our descriptor correctly.

2. **Surgical fix of WGI's XUSB dispatch gate** — if the gate is a specific registry key or device interface property, satisfy it from UMDF2. Blocked because the inline dispatch in `xusbdevice.cpp` appears to call into `xusb22.sys` on a USB-bus-enumerated target; neither the USB-bus-presence check nor the xusb22 service binding is addressable from UMDF2 without a kernel bus driver.

3. **Chromium uses XInputDataFetcher for haptics** — Chromium source review confirmed `XInputDataFetcherWin::PlayEffect` exists (`device/gamepad/xinput_data_fetcher_win.cc:237`) but is unreachable because its Factory is not registered in `gamepad_platform_data_fetcher.h:46-55`. Only `WgiDataFetcher::PlayEffect` is on the dispatch path. Changing this requires an upstream Chromium feature request, not a user-mode driver lever.

4. **Kernel bus driver** — provide a USB-bus-enumerated parent for our virtual. Violates the hard "no kernel drivers" constraint that defines the project.

## Open questions (Microsoft-facing, drafted)

### To Windows Driver Developer feedback

We have a ROOT-enumerated UMDF2 virtual HID controller (VID 045E / PID 028E) that registers the XUSB interface class via a class-filter extension INF. Direct `xinput1_4.XInputSetState` dispatches reach our filter successfully with the expected xusb22-style wire layout `[0, 0, LEFT_motor, RIGHT_motor, 0x02]`.

WGI enumeration probes also reach us (`IOCTL_XUSB_SET_STATE 00 0D 00 00 01` arrives on both the xinputhid-filter path and the xusb22-replica path):

```
[HMCOMP] IOCTL 8000A010 [00 0D 00 00 01]      # WGI enumeration probe
[18:47:48.480] SET_STATE-in 00 0D 00 00 01    # same probe on xinputhid-filter path
[HMCOMP] IOCTL 8000A010 [00 00 00 00 02]      # idle-state clear
```

But `Windows.Gaming.Input.Gamepad.put_Vibration` from a focused caller — including Chromium's `vibrationActuator.playEffect` — produces zero motor-bearing bytes at either instrumented path. The probe succeeds; the motor-byte dispatch never occurs. For a physical USB-enumerated Xbox 360 Wired on the same machine, the same caller path produces physical rumble.

**Question:** Is WGI's XUSB haptic dispatch hard-gated on USB-bus enumeration of the target device? If so, is there a documented mechanism for a UMDF2 virtual device registered under the ROOT enumerator to appear as a valid WGI haptic dispatch target without adding a kernel-mode bus driver? If the gate is not USB-enumeration per se, what is it, and can it be satisfied from UMDF2?

### To GDK team (Discord / developer forums)

We observe that `IGameInputDevice::SetRumbleState` from a focused Win32 GUI process dispatches to physical Xbox 360 Wired and Xbox Series BT successfully but does not reach our ROOT-enumerated UMDF2 virtual. GameInput enumeration returns the device correctly (`GetDeviceInfo` reports `VID=0x045E`, `PID=0x028E`, `supportedInput=0x0004000E`), but `supportedRumbleMotors = 0x0` despite our device registering the XUSB interface class. Physical Xbox-family controllers on the same machine report `supportedRumbleMotors = 0x3`.

**Question:** How does GameInput populate `supportedRumbleMotors` for XUSB-class devices? Is the value derived from a descriptor or interface property we can set, or is it tied to device enumeration (USB-bus presence)? Is there a supported pattern for a ROOT-enumerated UMDF2 virtual to declare haptic motor support to GameInput?

## Caveats

- **HID-path instrumentation not applicable to xbox-360-wired profile.** Our driver has hooks for `IOCTL_HID_WRITE_REPORT`, `IOCTL_UMDF_HID_SET_OUTPUT_REPORT`, and `IOCTL_UMDF_HID_SET_FEATURE`, but the Xbox 360 Wired profile's HID descriptor declares no output report collection. `HidD_SetOutputReport` is rejected at the HidClass layer with `ERROR_INVALID_FUNCTION` before reaching our driver. The "zero HID-WRITE during Chromium click" observation does not depend on these hooks and instead reflects the descriptor's design. Profiles that declare output reports (e.g., DualSense) would be the appropriate test surface for the HID-FFB path if that question arises for a different profile.
- **Physical 360 rumble verified by user tactile confirmation only.** Bytes route through `xusb22.sys` on the USB composite parent, which HIDMaestro does not instrument. Apparent "silence" at our log layer for physical-360-dispatched rumble is expected and does not imply dispatch failure.
- **Parser-bug retraction.** The xusbshim canonicalization heuristic was incorrect for several hours of investigation before the fix landed. See Methodology Note above for the failure mode and the standing rules derived from it.

## Consumer impact

- Native XInput consumers (XInputSetState callers): unaffected, continue to receive rumble via our xusbshim filter.
- Native GameInput consumers: currently blocked. Our virtual enumerates but `supportedRumbleMotors = 0x0`.
- Native WGI `Gamepad.Vibration` consumers: currently blocked. Same gate.
- Chromium-family browsers (`vibrationActuator.playEffect`): blocked. Routes via WGI `put_Vibration`.
- Games using dinput8 / RawInput / HID-output-reports: profile-dependent (works for HID-PID profiles like DualSense, inapplicable for XUSB profiles).

## Evidence appendix

Raw logs, ETW extracts, and probe source code preserved under `evidence/` in this directory. Full manifest in `../manifest.md`.

---

**Author:** HIDMaestro project
**Review:** conclusion reached under external oversight; see `investigation-history.md` for the sequence of briefs and retractions that led here.
