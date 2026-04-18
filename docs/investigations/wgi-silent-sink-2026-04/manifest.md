# /tmp Manifest — Preserve / Discard Recommendations

Survey of `c:\tmp\` at investigation closeout (2026-04-18 19:00). For each entry: recommendation + rationale. Awaiting user approval before moving.

Target layout for preserves:
- Source code → `test/probes/<name>/` (clean, no bin/obj)
- Evidence logs / traces → `docs/investigations/wgi-silent-sink-2026-04/evidence/`

## HIGH VALUE — preserve (source only, drop bin/obj)

### Probes that became the investigation's diagnostic toolkit

| /tmp path | Destination | Rationale |
|---|---|---|
| `c:\tmp\GameInputTest\focus_test.cpp`, `build_focus.cmd` | `test/probes/focus_test/` | Focus-gated GameInput rumble test. Win32 window + `GetForegroundWindow == own_hwnd` gating. Reusable for any WGI/GameInput haptic question. |
| `c:\tmp\GameInputTest\enumall.cpp`, `build_enumall.cmd` | `test/probes/gi_enumall/` | Enumerates all GameInput gamepads + their `supportedRumbleMotors` bitmasks. Produced the virtual=0x0 / physicals=0x3 evidence. |
| `c:\tmp\GameInputTest\native_wgi.cpp`, `build_native_wgi.cmd` | `test/probes/native_wgi_vibration/` | Native C++ `IGamepadStatics → put_Vibration` (no CLR). Rules out .NET/WinRT projection as a gate. |
| `c:\tmp\WgiRgcFfbProbe\Program.cs`, `WgiRgcFfbProbe.csproj` | `test/probes/wgi_rgc_ffb_probe/` | C# RawGameController.ForceFeedbackMotors + Gamepad.Vibration probe. Load-bearing evidence for "zero motors on virtual." |
| `c:\tmp\XiByteProbe\Program.cs`, `XiByteProbe.csproj` | `test/probes/xinput_byte_probe/` | Deterministic `XInputSetState(slot, wLeft, wRight)` probe. Parser-fix verification. |
| `c:\tmp\TargetPhysical360\Program.cs`, `TargetPhysical360.csproj` | `test/probes/wgi_vs_xinput_ab/` | User-paced A/B test: Window A (WGI `put_Vibration`) vs Window B (`XInputSetState`) on any chosen device. |
| `c:\tmp\XusbProvider\Program.cs`, `XusbProvider.csproj` | `test/probes/wgi_custom_factory/` | `Custom.GameControllerFactoryManager.RegisterCustomFactoryForHardwareId` + `RegisterCustomFactoryForXusbType` test. Falsified Custom-factory lever. |
| `c:\tmp\HidOutProbe\Program.cs`, `HidOutProbe.csproj`, `minimal.ps1` | `test/probes/hid_output_report_probe/` | HID output report sender via `HidD_SetOutputReport`. Produced the "xbox-360-wired descriptor doesn't declare output reports" caveat. |

Write a single `test/probes/README.md` pointing back to `docs/investigations/wgi-silent-sink-2026-04/finding.md` and listing each probe's intended use. The README should explicitly note the near-duplicate pairs so future users reach for the canonical one:

- **`native_wgi_vibration`** (C++ WRL) vs **`wgi_rgc_ffb_probe`** (C# WinRT projection): both call `Gamepad.Vibration = ...` on each enumerated gamepad. The C# version is the canonical "does WGI `put_Vibration` reach this device" probe because its output is easier to read; the C++ version exists to rule out CLR/WinRT-projection as a variable. If you're not specifically testing the CLR/native split, use the C# one.
- **`wgi_vs_xinput_ab`** (user-paced A/B on any device) vs **`focus_test`** (focus-gated rumble with Win32 window): `focus_test` is the canonical "is the API call focus-gated" probe; `wgi_vs_xinput_ab` is the canonical "which of these two APIs reaches this device" probe. Different questions, keep both.
- **`xinput_byte_probe`** (deterministic wLeft/wRight pairs) — no duplicate. This is the go-to for "what is the wire format on this path" questions.

### Evidence artifacts — raw logs + traces worth keeping

| /tmp path | Destination | Size | Rationale |
|---|---|---|---|
| Chromium click window log (18:46:04 → 18:48:00 excerpt) | `evidence/chromium-silent-sink-log.txt` | <1 KB | Three-layer log excerpt quoted in finding.md. Should be an artifact you can grep, not just inline text. |
| Parser-fix 2D sweep log (XInputTester before/after) | `evidence/parser-fix-verification.txt` | <5 KB | The "receipt" for the parser-bug fix. Full sweep data with both motors ramping. |
| Deterministic XiByteProbe output + matching xusbshim log | `evidence/wire-format-confirmation.txt` | <1 KB | Records `wLeft=0xFFFF → byte[2]=0xFF` etc. for the formal finding. |
| `c:\tmp\WgiRgcFfbProbe_elevated.log` | `evidence/wgi-rgc-motors-enumeration.txt` | 1.7 KB | Produces `ForceFeedbackMotors.Count=0` finding on virtual. |
| `c:\tmp\ChromiumTrace\gamepad.wprp` + start/stop .cmd | `evidence/etw-profile/` | <5 KB | WPR profile definition. Reusable. Drop the raw .etl (see discard below). |

## MEDIUM VALUE — preserve selectively

| /tmp path | Decision | Rationale |
|---|---|---|
| `c:\tmp\hm_trace_divergence.md` | preserve as `evidence/notes/hm_trace_divergence.md` | Investigation-era notes on trace divergence. Small. |
| `c:\tmp\wgi_scan.ps1` | preserve as `test/probes/wgi_dll_string_scan.ps1` | Scans `Windows.Gaming.Input.dll` for source-file footprint. Produced the "no xusbforcefeedback.cpp" Z finding. |
| `c:\tmp\GameInput.h` copy | discard | Already in WDK at `C:\Program Files (x86)\Windows Kits\10\Include\...\um\GameInput.h`. Probe build scripts reference the WDK path. |
| `c:\tmp\handle64.exe`, `handle.exe` | discard | Sysinternals tool, downloadable from live.sysinternals.com. Don't commit binaries we don't own. |

## LOW VALUE — discard

### Early-stage probes superseded by later work

These were exploratory tools whose results are captured in the falsification table. Source is reproducible from the finding document if ever needed again.

- `c:\tmp\XusbOpen\`, `c:\tmp\XusbProbe.*` (5 orphan processes from earlier kernel-stuck state, unkillable without reboot)
- `c:\tmp\XiSlotVibe\`, `c:\tmp\XiCapsProbe\`, `c:\tmp\XiCaps\`, `c:\tmp\XiTestSetState\`, `c:\tmp\DirectCapsProbe\`, `c:\tmp\SetStateProbe\`
- `c:\tmp\DevPkeyDump\`, `c:\tmp\ServiceSpoof\` (DEVPKEY spoof experiments, falsified)
- `c:\tmp\VibeAll\`, `c:\tmp\TargetedVibe\`, `c:\tmp\SingleVibe\`, `c:\tmp\WgiVibe\`, `c:\tmp\WgiId\` (superseded by TargetPhysical360/focus_test)
- `c:\tmp\GuideBitCheck\` (Guide-bit read-back tool, findings captured in investigation history)
- `c:\tmp\HmSharedReader\` (MMF reader — low utility without matching investigation)

### Intermediate logs and profile data

- `c:\tmp\ChromiumTrace\kstack.etl` (1.4 GB), `kernel.etl`, `file_access.csv`, `file_access.txt` — too large to archive; filtered excerpts already quoted in finding.md
- `c:\tmp\procmon\` (1.9 GB of ProcMon PMLs) — superseded by ETW approach; not cited in finding
- `c:\tmp\edge-test-profile\`, `c:\tmp\edge_fresh_test\`, `c:\tmp\edge_trial_profile\` (1.5 GB total Edge profiles) — ephemeral browser state
- `c:\tmp\edge_stderr.log` (21 MB), `edge-stderr.log` (188 KB) — mostly tracking-prevention noise; verbose logging flags didn't surface gamepad subsystem output in release build
- `c:\tmp\trace_copy.log` (174 MB), `trace_new.txt` (18 MB) — old pre-session trace files
- `c:\tmp\wgi_kio.etl` (8 KB) — empty/unused ETW
- `c:\tmp\hm_*.out`, `c:\tmp\hm_*.ps1`, `c:\tmp\hm_*.log`, `c:\tmp\hm_*.md` — investigation-era scratch files; content rolled into finding/history docs
- `c:\tmp\f310-*.txt`, `c:\tmp\series_bt_*.txt`, `c:\tmp\spoof-*.txt`, `c:\tmp\enum-*.txt`, `c:\tmp\*-reg.txt`, `c:\tmp\svc*.txt`, `c:\tmp\gisvc.txt`, `c:\tmp\phaseA/B/C.txt`, `c:\tmp\trial_baseline.txt`, `c:\tmp\trial_test.txt`, `c:\tmp\openfiles_dump.txt`, `c:\tmp\msedge_handles.txt`, `c:\tmp\hmshared-out.txt`, `c:\tmp\pnputil.txt`, `c:\tmp\vibe_test.html`, `c:\tmp\explorer_suspend.txt`, `c:\tmp\devpkey-dump*.ps1`, `c:\tmp\devpkey-dumps\` — per-step scratch, content in finding/history
- `c:\tmp\hidmaestro-org\` (19 MB), `c:\tmp\release-v2.0.0.md`, `c:\tmp\release_notes\`, `c:\tmp\wiki-*.md` — unrelated to this investigation (website / release work)

### Intermediate build artifacts inside every preserved probe

`bin/`, `obj/`, `.pdb`, `.ilk`, `.obj`, `.exp`, `.lib`, `vc140.pdb` — regenerate from source.

## Security

No test certs, signing keys, or credentials were generated in `/tmp` during this investigation. The HIDMaestro SDK's embedded signing tools use cert material from `sdk/HIDMaestro.Core/Resources/` (already in the repo) and a per-install self-signed `HIDMaestroTestCert`. Nothing from `/tmp` carries secret material.

## Approval needed

I'll move the HIGH-VALUE and MEDIUM-VALUE items into the repo on your approval. Everything else either gets deleted or left alone at `/tmp`'s natural decay schedule. Respond:

- **"approve"** — I proceed with the manifest as written
- **"discard <item>"** or **"preserve <item>"** — for any individual override
- **"hold"** — nothing moves; I stand down on this step

Standing by.
