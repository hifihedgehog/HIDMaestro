# Investigation History — WGI Silent Sink on HIDMaestro Virtuals

Brief-by-brief sequence of the investigation with retractions marked. Read in conjunction with [finding.md](finding.md) for the formal conclusion and [manifest.md](manifest.md) for preserved artifacts.

## Timeline

### 2026-04-18 morning — initial symptom

- User reports Chromium `vibrationActuator.playEffect` on hardwaretester.com silently no-ops for HIDMaestro virtuals despite producing physical rumble on real Xbox-family controllers.
- Initial framing: "Chromium dispatch path for XUSB-class virtuals."

### Early hypothesis ladder (briefs 1–2)

Hypotheses tested and falsified by driver-side log observation:

- H-VID/PID allowlist (Chromium gating on device identity) — falsified: same VID/PID on real and virtual, real rumbles
- H-motor-max caps gate — falsified: our CAPABILITIES reply reports correct motor maxes
- H-Chromium WGI fetcher disable — `--disable-features` flags do not help
- H-user-activation (Trial A vs B) — identical behavior with/without user gesture

### First "silent sink" conclusion — RETRACTED

- User flags Guide-long-press haptic pattern (`hi=0x7F` short burst) in the log. Author had misattributed this to "explorer.exe ambient noise" via process A/B/A suspension.
- Root cause: test pattern in `SdkDemo` and `HIDMaestroTest` was cycling `HMButton.Guide`, triggering Windows Xbox UI Guide haptic ack back to our slot. Pattern looked identical to what the author was labeling as "dispatch signal" during Chromium click trials.
- Guide references removed from test pulse loops. `scripts/check_no_guide_in_pulse.ps1` added as a regression guard. Build-time check fails on any `HMButton.Guide` under `example/` or `test/` without a `// ALLOW-GUIDE:` allowlist comment.

### Post-Guide trials — Chromium source review

- Upstream Chromium `device/gamepad/wgi_data_fetcher_win.cc` reviewed via agent. Finding: only `WgiGamepadDevice::SetVibration → IGamepad::put_Vibration` is on the haptic dispatch path. `XInputDataFetcherWin::PlayEffect` exists but its Factory is not registered; XInput is used only for meta-button (Guide) polling via `XInputGetStateEx`.
- This resolves Y-alt-C (make Chromium use XInput for haptics): dead. Not a user-mode lever without upstream Chromium patch.

### Second "silent sink" conclusion — RETRACTED

- After a clean baseline run (Edge closed, 10s, 0 bytes) and a test run (Edge + playEffect, 0 bytes at SDK `[out0]` layer), author concluded "Chromium dispatches zero bytes to our virtual across all three instrumented paths."
- Opus (external oversight) flagged: driver reception layer was not verified directly; the "silent" claim was inferred from SDK-level parser output. A parser bug could produce apparent silence. Request: raw-byte dump at all three driver instrumentation layers, filtered by click-window timestamp, independent of parser interpretation.
- Required a new instrumentation pass and Chromium retrial.

### Process-context experiments (brief 5 — all falsified)

With SDK `[out0]` instrumentation still under suspicion, author tested hypotheses for why Chromium might pass a gate plain C# console processes don't:

| Test | Result |
|---|---|
| Elevated C# `put_Vibration` | silent |
| `[STAThread]` apartment + STA-apt worker | silent |
| `xinput1_4` priming (20× XInputGetState before put_Vibration) | silent |
| Native C++ WRL `IGamepadStatics` → `put_Vibration` (CLR bypass) | silent |
| Event-subscription context (`Gamepad.GamepadAdded` → event-delivered IGamepad) | silent |
| WGI Custom factory (`RegisterCustomFactoryForHardwareId` + `RegisterCustomFactoryForXusbType`) | factory CreateGameController fires, but `Gamepad.Gamepads[i]` returns built-in type; HidGameControllerProvider.SendOutputReport returns ERROR_NOT_FOUND |
| GameInput direct (`IGameInputDevice::SetRumbleState`) from console | silent on all devices including physicals (!); `supportedRumbleMotors = 0x0` for virtual, `0x3` for physicals |

### Focus-gate discovery (partial, then retracted)

- Opus flagged Microsoft docs: "SetRumbleState will only be applied to the device while the application is in focus."
- Author built `focus_test.exe` — real Win32 window, GetForegroundWindow gating.
- With focus satisfied: GameInput SetRumbleState rumbles physical 360 and Series BT.
- User then tested targeting our virtual with focused GameInput. Physical rumbles (expected, bytes go to xusb22.sys on USB parent). Virtual showed no bytes at the SDK `[out0]` layer.
- Initial read: "focus resolves physicals, virtual still silent" — consistent with architectural framing.

### Third "silent sink" conclusion — draft committed, then RETRACTED

- Author began drafting Option 3 (document limitation) based on the focus-gate finding plus cumulative evidence.
- Then user ran XInputTester and reported "hi stuck at 0x02 across entire sweep." Author explained this as "user's right slider at 0.78% intensity."
- Opus flagged (third time): same failure pattern. Narrative explanation instead of confirmation test. Quote: "Third time this investigation. The 30-second slider-isolation test resolves this; run it."
- User ran the test: right slider 0→max→0 with left pinned → `byte[3]` varied 0→0x7D→0 in the log. Left slider hypothesis dead. byte[3] confirmed as RIGHT motor position; `byte[4] = 0x02` is packet trailer.

### Parser bug found and fixed (branch-local regression)

- xusbshim canonicalization heuristic at [driver/xusbshim.c:550-558](../../../driver/xusbshim.c#L550-L558) preferred "xinputhid-style motors at in[3]/in[4]" when those bytes were non-zero. For every rumble frame on the xinputhid path, in[4] is the packet trailer `0x02`, which was misidentified as the high motor.
- Replaced heuristic with deterministic `lo = in[2]; hi = in[3];` decode (xusb22-style, confirmed empirically via XiByteProbe deterministic XInputSetState probe).
- Verified post-fix via full 2D XInputTester sweep: both motors decode correctly at both byte positions across 0–0xFD range.
- **Scope**: `driver/xusbshim.c` is branch-local to `v1-dev-experiment-xusb-child-pdo`. Master does not have xusbshim — master's XUSB dispatch goes via `driver/companion.c` which publishes raw IOCTL buffers without canonicalization. The bug was an in-experiment regression introduced by the xusbshim canonicalization logic, not a pre-existing master bug. Master is unaffected and doesn't need this fix. The methodology rules (raw-byte verification at reception) still apply — they would have caught the contamination whether pre-existing or experiment-local.

### Post-fix clean matrix and final commit

- Re-ran Chromium trial with fixed driver + HIDMaestroTest raw-byte dump in OutputReceived hook.
- Chromium click window (18:46:04 → 18:48:00), physical targeted first, virtual targeted second:
  - Physical rumbled (user-tactile confirmed)
  - Virtual log: probe `00 0D 00 00 01` at both xusbshim `SET_STATE-in` and `[HMCOMP] IOCTL 8000A010` layers, plus idle-clear `00 00 00 00 02`, and **zero motor-bearing SET_STATE bytes at any of the three driver instrumentation layers.**
- Silent sink reconfirmed under trustworthy instrumentation with positive control (physical rumble in same click window).
- Option 3 committable. See [finding.md](finding.md).

## Falsification summary

Hypotheses falsified empirically across the investigation:

| Hypothesis | Falsifying evidence |
|---|---|
| Chromium has XInput haptic fallback (Y-alt-C) | Chromium source review — Factory not registered |
| CLR / .NET WinRT projection quirk | Native C++ WRL `IGamepadStatics → put_Vibration` also silent |
| Admin privilege gate | Elevated C# still silent |
| STA apartment gate | STA-apt C# still silent |
| xinput1_4 initialization gate | 20× XInputGetState prime then put_Vibration still silent |
| WGI Custom factory (`Custom.GameControllerFactoryManager`) | `Gamepad.Gamepads[i]` returns built-in type; Custom factory doesn't override consumer view |
| Event-subscription context | Event-delivered IGamepad same silent sink as enumeration-delivered |
| Driver parser misreading bytes (final uncertainty before fix) | Parser was wrong — fixed — silent sink still holds post-fix |
| Focus gate alone resolves virtual | Focus works for physicals, not for our virtual (virtual has additional architectural gate) |

## External oversight

The investigation was conducted under rotating oversight review between the author (in-session) and Opus 4.7 via Claude Online, with each substantive brief reviewed before the next experimental step. The three retractions were all driven by Opus's pushback on premature conclusions. The methodology-debt rules in [finding.md](finding.md#the-three-standing-rules-that-emerged) are recorded because this review cycle surfaced a failure pattern the author would not have caught unaided.

## Related memory files

- `memory/project-wgi-silent-sink-finding.md` — project-level memory snapshot with decision tree
- `memory/feedback-never-suggest-kernel-drivers.md` — hard constraint
- `memory/project-wgi-vibration-architecture.md` — pre-investigation memory on WGI paths
- `memory/project-xinputhid-shim-dead-end.md` — prior related dead end

## Tooling used

For reproducibility — binaries were not preserved but can be re-obtained from these sources:

- **Sysinternals Handle** (`handle.exe` / `handle64.exe`) — downloaded during the investigation from https://live.sysinternals.com/handle64.exe. Used to enumerate open file handles in `msedge.exe` processes.
- **Windows Performance Toolkit** (`xperf.exe`, `wpr.exe`) — part of the Windows SDK, at `C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\`. Used for ETW stack-sampled kernel traces with FILEIO + FILENAME capture.
- **Process Monitor** (Sysinternals) — considered but not decisive. ProcMon's minifilter is filesystem-centric; XUSB IOCTLs are not captured at that layer on modern Windows. Use xperf for IOCTL-level work on gaming input.
- **MSVC cl.exe** + Windows SDK `GameInput.h` / `gameinput.lib` — used for the C++ GameInput probes (`focus_test`, `enumall`, `native_wgi`). SDK path: `C:\Program Files (x86)\Windows Kits\10\{Include,Lib}\10.0.26100.0\um\`.
- **.NET SDK 10 / C#** — used for all other probes. No external NuGet dependencies; all WinRT types via `Microsoft.Windows.SDK.NET` projections.

## Evidence preserved

See [manifest.md](manifest.md) for the preserve/discard list across `c:\tmp\` artifacts at investigation closeout.
