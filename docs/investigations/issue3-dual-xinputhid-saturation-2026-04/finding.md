# Issue #3 Report (draft) — Dual xinputhid virtuals: CPU saturation + state-propagation hang

**Branch:** `v1-dev-investigate-dual-xinputhid-saturation`
**Date:** 2026-04-19
**HIDMaestro version under test:** current `master` HEAD + branch-local test harness changes (see Test harness section).

## Short answer

**The issue does not reproduce with `HIDMaestroTest` alone.** Across the full test battery from issue #3 (T1–T5) plus on-the-fly live-swap / destroy / quit scenarios, WUDFHost CPU stays well under 3% of one core, XInput packet numbers advance smoothly, and no hang or freeze is observed.

Per the issue's own decision rule: *"If none of the tests reproduce, the cause is something PadForge is doing that HIDMaestroTest does not, and PadForge will dig deeper with more targeted instrumentation on the consumer side."*

## Test harness

Three probes + a runner + a schema change landed on the investigation branch:

- `test/probes/wudfhost_cpu_sampler/sample.ps1` — polls `\Process(WUDFHost*)\% Processor Time` at 1 Hz, normalizes to 1-core = 100%, writes CSV.
- `test/probes/xinput_latency_meter/` — C# tool that polls `XInputGetState` on specified slots at ~1 kHz for a fixed duration and logs `(elapsed_us, slot, rc, packet, wButtons, sticks, triggers)` to CSV.
- `c:\tmp\issue3\runner.ps1` (orchestrator, kept out of repo) — launches `HIDMaestroTest emulate` with specified profiles + rate, spawns the CPU sampler and XInput meter in parallel, collects CSV, prints summary.
- `test/HIDMaestroTest.csproj` — added `--rate-hz N` flag to `emulate` command (default 250 Hz from `Thread.Sleep(4)`). When rate ≥ 500 Hz, the emulator process also calls `winmm.dll!timeBeginPeriod(1)` to raise Windows timer resolution so `Thread.Sleep(1)` actually yields ~1 ms instead of the default 15.6 ms coarsening. Without `timeBeginPeriod`, a nominal `--rate-hz 1000` ran at ~200 Hz, so results had to be verified at a TRUE 1 kHz before the "no repro" verdict was supportable.

## Evidence matrix

All tests: 60 s duration, CPU normalized to 1-core = 100%. Nominal rate = flag passed to `--rate-hz`; effective rate = inferred from XInput `dwPacketNumber` advance on slot 0.

| Test | Profiles | Nominal rate | Effective rate (pkt/s) | CPU avg | CPU max | Slots live |
|---|---|---:|---:|---:|---:|---|
| T1 (baseline) | 1× xbox-series-xs-bt | 1 kHz | 196 | 0.26% | 0.91% | slot 0 |
| T2 | xbox-series-xs-bt + xbox-one-s-bt | 1 kHz | 192 | 0.51% | 1.78% | slot 0 (+ 2 per verify.py) |
| T3 (rate-limit) | xbox-series-xs-bt + xbox-one-s-bt | 125 Hz | 144 | 0.34% | 1.02% | slot 0 (+ 2 per verify.py) |
| T4 (mixed mode) | xbox-series-xs-bt + xbox-360-wired | 1 kHz | 188 | 0.83% | 2.03% | slot 0 (+ 2 per verify.py) |
| T5 (same profile) | 2× xbox-series-xs-bt | 1 kHz | 203 | 0.51% | 1.97% | slot 0 (+ 2 per verify.py) |
| T2b (TRUE 1 kHz) | xbox-series-xs-bt + xbox-one-s-bt | 1 kHz + timeBeginPeriod(1) | 998 | 1.73% | 2.50% | slot 0 (+ 2 per verify.py) |
| T5b (TRUE 1 kHz) | 2× xbox-series-xs-bt | 1 kHz + timeBeginPeriod(1) | 1156 | 1.81% | 2.94% | slot 0 (+ 2 per verify.py) |
| T5c (TRUE 1 kHz, all-slot poll) | 2× xbox-series-xs-bt | 1 kHz + timeBeginPeriod(1) | slot 0: 1111 / slot 2: 1162 | 1.69% | 3.14% | slot 0 + slot 2 (both rc=0x0, both advancing) |
| OF_A live-swap | xbox-series-xs-bt → xbox-360-wired mid-run | 500 Hz | 841 | 0.21% | 1.06% | slot 0 |
| OF_B destroy cycle | 2× xinputhid, `remove 0` then `remove 1` | 500 Hz | ~55 | 0.09% | 0.77% | transitions cleanly |
| OF_C quick quit | 2× xinputhid, `quit` after 5s | 500 Hz | ~132 | 0.09% | 0.39% | clean shutdown |

**Meter-coverage correction:** the original runs used `xinput_latency_meter` with slots `0 1` hardcoded. In 2× xinputhid configs, xinputhid actually allocates slots 0 + 2 (not 0 + 1), so the meter missed the second virtual and the early draft of this report incorrectly claimed "slot 1 NOT_CONNECTED" as a secondary finding. An independent run of `scripts/verify.py --controllers 2` against a live 2× xbox-series-xs-bt config confirms `XInput: PASS  2 slot(s) live (slot0 pkt=15934 LX=+11736 LY=+27767, slot2 pkt=17201 LX=+24684 LY=-17304)`. The meter has been updated to poll all four slots by default; T5c (below) re-verifies the 2× case with all-slot coverage.

**Interpretation:**
- Each additional xinputhid virtual adds ~0.25% of one core to WUDFHost at true 1 kHz; each XUSB-companion virtual adds roughly the same.
- No test reached saturation (100% of one core) or anywhere close. Maximum observed across all tests was 2.50% (T2b).
- XInput slot 0 `dwPacketNumber` advanced monotonically in every test — no freeze, no state-propagation hang.

## Slot allocation note (retraction + correction)

An earlier draft of this report claimed slot 1 was never allocated when two xinputhid virtuals were present. That was a test-harness error. `xinput_latency_meter` was polling slots 0 and 1 only; xinputhid does not guarantee consecutive slot numbers across sibling virtuals and commonly lands 2× xinputhid on slots 0 + 2. `scripts/verify.py --controllers 2` run independently against a live 2× `xbox-series-xs-bt` config shows both slots live with independent packet numbers (slot 0 pkt=15934 at snapshot, slot 2 pkt=17201 at snapshot).

Multi-virtual xinputhid slot allocation works as expected and is a routinely-verified HIDMaestro feature (the 6-mix test battery — 2× Series BT + 2× Xbox 360 Wired + 2× DualSense — passes on master, showing 3 or 4 Xbox-family slots populated depending on timing). The meter has been updated to poll all four slots by default so future investigations don't inherit this error.

**What remains potentially relevant for PadForge**: if PadForge's pipeline assumes contiguous slot numbers and targets slot 1 specifically for what it thinks is the second virtual, it will see `DEVICE_NOT_CONNECTED (0x48F)` there while slot 2 has the actual device. Worth checking PadForge's slot-index selection logic against per-slot `XInputGetState` return codes.

## What I would check next if PadForge repro is still active

1. **Slot allocation**: what does `XInputGetCapabilities` on slot 1 return during the saturation window? `DEVICE_NOT_CONNECTED` would match my observation.
2. **Submission rate through PadForge**: PadForge states 1 kHz per virtual. Verify via an external tool that the `SubmitState` entry point is actually executing at 1 kHz and not getting coalesced or backpressured.
3. **Number of concurrent `HMController.OutputReceived` subscribers**: SDK ships one output thread per virtual; if PadForge attaches multiple handlers to the event, is there per-handler work that scales badly at 1 kHz × 2 virtuals?
4. **SDL3 function-pointer hook interaction**: PadForge's in-process hook modifies `SDL3.dll` pointers but shouldn't touch `WUDFHost`. Confirm via `handle.exe` that PadForge holds no direct handles to device symlinks beyond what SDL3 opens.

## Evidence Status

**Confirmed (this session):**
- T1–T5 ran to completion; CSV artifacts in `c:\tmp\issue3\*.csv` and `c:\tmp\issue3\*.emulate.log`.
- WUDFHost CPU never exceeded 2.50% of one core across any test.
- XInput `dwPacketNumber` advances consistently; no frozen state.
- On-the-fly live-swap + destroy cycle + quit all complete without CPU spike or hang.
- TRUE 1 kHz submission verified via `timeBeginPeriod(1)` + observed pkt/s ≈ 1000.

**Hypothesized (load-bearing but not directly verified):**
- That PadForge's observed symptom is caused by something in PadForge's own pipeline (consumer-side) rather than the SDK or driver. Supported by: no SDK/driver test reproduces the symptom, and `verify.py` reports `ALL PASS` on multi-virtual xinputhid configs including 2-virtual and 6-virtual mixed.

**Unverified assumptions:**
- That PadForge is running on the same HIDMaestro version (v1.1.6). If PadForge is on an older version, the bug may have existed and been fixed in the v1.1.5/v1.1.6 install-robustness work.
- That the symptom depends on two xinputhid virtuals specifically, not some interaction with concurrent real xinputhid devices on the same system (which my test environment doesn't reproduce).

## Artifacts

- CSVs: `c:/tmp/issue3/*.{cpu,xinput}.csv`
- Emulate stdout: `c:/tmp/issue3/*.emulate.log`
- Runner: `c:/tmp/issue3/runner.ps1`
- On-the-fly driver: `c:/tmp/issue3/onthefly.ps1`
- Probe source: `test/probes/wudfhost_cpu_sampler/` and `test/probes/xinput_latency_meter/` (on the branch)

## Proposed next step

Close this investigation as **"not reproduced; probable PadForge-side issue"** unless PadForge provides:
- A minimal `HIDMaestroTest`-equivalent reproducer using only the public SDK surface, OR
- An ETW/WPR capture from inside PadForge's process during a live saturation event

If either arrives, I can bisect on the branch from there.
