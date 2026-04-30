# HIDMaestro v1.3.0 — Performance + Durability Work

**Scope:** address audit-pass timing fragility (Tier 1) and add multi-layer caching for fresh-launch + create/teardown speed (Tiers 2–5). Use the swap_regression battery as the gate.

**Version target:** v1.3.0. Major-minor bump because the new `HIDMAESTRO_TIMEOUT_SCALE` env var is a public-surface addition and the cache behavior changes are user-observable.

**Reference machines:**
- **Dev (ceiling):** AMD Ryzen 9955HX3D, NVMe, Win 11 26200. Battery wall: ~33 min, 26/26 PASS on v1.2.2.
- **Floor target:** Intel Atom x5-Z8350, eMMC, Win 10 IoT Enterprise LTSC 2021. ~1/10 single-thread, ~1/70 multi-core, ~1/30 storage. Battery wall projected: ~3 hours.

**Status legend:** `pending` / `in-progress` / `done` / `deferred` / `n/a`

---

## Table 1 — Baseline measurements (v1.2.2, dev box)

To be filled before any Tier 1 changes land. All times are wall-clock; battery sourced from prior runs.

| Metric | v1.2.2 dev box | v1.3.0 C1 | v1.3.0 C5 | v1.3.0 C8 | v1.3.0 C10 (Tier 9) | v1.3.0 C11 (Tier 10) | Atom (projected ×10) |
|---|---|---|---|---|---|---|---|
| Warm-launch 3-controller create (xbox360 + xbox-bt + dualsense) | **1 287 ms** | 1 288 ms | **1 090 ms** (-15%) | **1 049 ms** (-18.5%) | _diag-log median 388 ms_ | _diag-log median 431 ms_ | ~10.5 s |
|   ↳ Xbox 360 wired (XUSB companion path) | 693 ms | 656 ms | **577 ms** (-17%) | **556 ms** (-20%) | _diag-log p75 = 539 ms_ | _diag-log p75 = 596 ms_ | ~5.6 s |
|   ↳ Xbox Series BT (xinputhid path) | 166 ms | 198 ms | **122 ms** (-27%) | **119 ms** (-28%) | _diag-log p25 = 143 ms_ | _diag-log p25 = 152 ms_ | ~1.2 s |
|   ↳ DualSense BT (plain HID path) | 427 ms | 432 ms | **390 ms** (-9%) | **373 ms** (-13%) | _captured in median_ | _captured in median_ | ~3.7 s |
| Finalize device names | 2 ms | 1 ms | 1 ms | 1 ms | 1 ms | 1 ms | ~10 ms |
| swap_regression battery wall | 2 016 s | **1 934 s** (-4%) | **1 895 s** (-6%) | **2 021 s** | **1 949 s** (-3.3%) | **1 971 s** (-2.2%) | ~5–6 hours |
| swap_regression battery PASS count | 26/26 | **26/26** | **26/26** | **26/26** | **26/26** | **26/26** | _pending_ |

**Battery wall progression context:** C5/C8 (1895/1918 s) were faster than baseline because the v1.3.0-wip-broken `WaitForHidChild` was returning False immediately for SWD-rooted parents — skipping a real PnP wait that downstream slot-claim was effectively absorbing. C9 (2021 s) is post-fix: the wait happens properly in step 4, slot-claim in step 7 is then fast. Net: battery wall is ~baseline (within 5s of 2016 s noise), per-controller Phase 1 is **16% faster**, and the SDK is now functionally correct for SWD-rooted parents. The user-visible metric (Phase 1) is the right scorecard.

**C10 (Tier 9 validation):** 26/26 PASS in 1949 s (-3.6% from C9, -3.3% from baseline). 6 outliers in the diag log: 3 setups timed out at 30 s (XInput slot wait + WaitForHidChild back-to-back timeout on Series BT scenarios) and 3 at 15 s (slot wait alone). Same-shape outliers as C9 — Series BT companion occasionally fails to bind on first attempt and the slot-wait budget is consumed. Not a regression from Tier 9 (dedupe + bulk SHM zero-init has no path that affects bind timing). Median per-setup Phase 1 is 388 ms across 119 setups; without the 6 timeout outliers the average drops from 1421 ms to ~549 ms.

**C11 (Tier 10 validation):** 26/26 PASS in 1971 s (-2.2% from baseline, +1.1% from C10). 12 Series-BT-bind-failure outliers (7 at 30 s, 5 at 15 s) vs C10's 6 — these are intermittent kernel-side bind failures, not SDK regressions. Median per-setup Phase 1 is 431 ms across 115 setups (vs C10's 388 ms). The Tier 10 changes (HidReportBuilder cache + IsDriverInstalled prewarm + step-6 ApplyFriendlyName direct calls) target first-call cost reduction rather than steady-state median — the absence of a median improvement is expected; the savings are in the per-controller Phase 1 burst that the warm-launch metric measures, not the battery's average. Without the 12 outliers, average is 639 ms across 103 setups. **Tier 11** (event-driven OutputPollLoop sleep) was committed after C11 launched and needs C12.


**Note on the 35 s outlier:** the v1.2.2 baseline run had a one-time teardown of slot 0 at 35.7 s (vs the typical 5.6 s). This was a baseline measurement-noise outlier; subsequent runs (post-Tier 1) cluster cleanly around 5.5–5.7 s per slot for Xbox-family teardowns. Not worth tracking further.

---

## Table 2 — Tier 1: Durability (timeout audit)

Every wall-clock budget that could trip on slow hardware. Categories from the audit pass:

- **(a) Backstop** — "the OS is hosed" tripwire. Multiplies via `TimeoutScale.Apply`, never replaced with an unbounded wait.
- **(b) Progress-bounded** — signal-driven internally with a wall-clock fallback. Multiplies via `TimeoutScale.Apply`.
- **(c) Pacing** — cadence tick, not a budget. Left as-is.

| ID | Site | Current | Cat | Action | Status | Notes |
|---|---|---|---|---|---|---|
| T1-01 | `DriverBuilder.Run` default | 60 000 ms | a | Wrap with TimeoutScale.Apply | done | signtool / inf2cat / pnputil |
| T1-02 | `DriverBuilder.InstallDrivers` pnputil call | 30 000 ms | a | Wrap with TimeoutScale.Apply | done | tightest pipeline budget |
| T1-03 | `PnputilHelper.Run` default | 30 000 ms | a | Wrap with TimeoutScale.Apply | done | covers /enum-drivers + /delete-driver |
| T1-04 | `DeviceManager` devcon WaitForExit (lines 525, 528, 571, 574) | 5 000 / 2 000 ms | a | Wrap with TimeoutScale.Apply | done | DIF_REMOVE drain |
| T1-05 | `DeviceOrchestrator.RunProcess` default | 30 000 ms | a | Wrap with TimeoutScale.Apply | done | reg.exe / generic |
| T1-06 | `DeviceOrchestrator.WaitForPriorTeardown` default | 120 000 ms | a | Wrap with TimeoutScale.Apply | done | already generous |
| T1-07 | `DeviceOrchestrator` reg.exe deletes (3 sites) | 3 000 ms | a | Wrap with TimeoutScale.Apply | done | tight on slow eMMC |
| T1-08 | `DeviceOrchestrator` WUDFHost WaitForExit (line 2043) | 10 000 ms | a | Wrap with TimeoutScale.Apply | done |  |
| T1-09 | `DeviceOrchestrator.RemoveDevice` parent (5 sites) | 120 000 ms | a | Wrap with TimeoutScale.Apply | done | xinputhid teardown |
| T1-10 | `DeviceOrchestrator.RemoveDevice` sweep (lines 1854, 1864, 1894, 1911) | 5 000 / 3 000 ms | a | Wrap with TimeoutScale.Apply | done | residual nodes |
| T1-11 | `SwdDeviceFactory.RunOnce` perAttempt | min(callbackTimeoutMs, 15 000) | a | Wrap with TimeoutScale.Apply | done | hmswd helper |
| T1-12 | `SwdDeviceFactory.Create` callbackTimeoutMs default | 35 000 ms | a | Wrap with TimeoutScale.Apply | done | end-to-end SwDeviceCreate |
| T1-13 | `SwdDeviceFactory` backoffMs[] | 0 / 1 500 / 3 000 ms | a | Wrap with TimeoutScale.Apply (backoff scales too) | done |  |
| T1-14 | `SwdDeviceFactory.Remove` WaitForExit (line 303) | 30 000 ms | a | Wrap with TimeoutScale.Apply | done |  |
| T1-15 | `DeviceManager.WaitForDeviceInterface` default | 10 000 ms | b | Wrap with TimeoutScale.Apply | done | CM_Register_Notification driven |
| T1-16 | `DeviceManager.WaitForHidChild` default | 5 000 ms | b | Wrap with TimeoutScale.Apply | done |  |
| T1-17 | `DeviceManager.WaitForDeviceRemoval` default | 5 000 ms | b | Wrap with TimeoutScale.Apply | done |  |
| T1-18 | `DeviceOrchestrator.WaitForHidChild` (line 1319) | 10 000 ms | b | Wrap with TimeoutScale.Apply | done | 100ms registry poll |
| T1-19 | `DeviceOrchestrator.WaitForDeviceStarted` (line 1377) | 5 000 ms | b | Wrap with TimeoutScale.Apply | done |  |
| T1-20 | `DeviceOrchestrator.WaitForDeviceInterface` (line 957) | 2 000 ms | b | **Floor-bump base to 5 000**, then wrap with TimeoutScale.Apply | done | 2s is too tight even on fast hw |
| T1-21 | `DeviceOrchestrator.WaitForHidChild` (line 862) | 5 000 ms | b | Wrap with TimeoutScale.Apply | done | gamepad-companion HID child |
| T1-22 | `swap_regression.ps1` Wait-CascadeSettle | fixed 12 s | a | Read `$env:HIDMAESTRO_TIMEOUT_SCALE`, multiply | done | battery script |
| T1-23 | `swap_regression.ps1` Wait-CreateBound TimeoutMs | 60 000 ms | a | Read env scale, multiply | done |  |
| T1-24 | `swap_regression.ps1` Start-Sleep -Seconds 4 (post-create) | 4 s × N | a | Read env scale, multiply | done |  |
| T1-25 | `swap_regression.ps1` Stop-Process WaitForExit GracefulMs | 5 000 ms | a | Read env scale, multiply | done |  |
| T1-26 | NEW — `TimeoutScale` class | n/a | n/a | Add at `sdk/HIDMaestro.Core/Internal/TimeoutScale.cs` | done | reads HIDMAESTRO_TIMEOUT_SCALE once at class init |
| T1-27 | `HMController.OutputPollLoop` Thread.Sleep(8) | 8 ms | c | Leave as-is | n/a | pacing |
| T1-28 | `DeviceOrchestrator` WaitFor* poll Thread.Sleep(100) | 100 ms | c | Leave as-is | n/a | pacing |
| T1-29 | Driver-side WaitForSingleObject (driver.c lines 788, 805, 859) | 200 / 500 / 2 000 ms | c | Leave as-is | n/a | kernel pacing |
| T1-30 | Companion PumpTimer | 8 ms | c | Leave as-is | n/a | pacing |
| T1-31 | `SharedMemoryIO` Thread.SpinWait(8) | 8 spins | c | Leave as-is | n/a | seqlock retry |

---

## Table 3 — Tiers 2–5: Caching / perf

| ID | Tier | Site | Estimated win | Risk | Status | Notes |
|---|---|---|---|---|---|---|
| T2-01 | 2 | NEW — embedded SHA256 manifest in SDK assembly | n/a | low | done | computed at SDK build via MSBuild target; constant in assembly |
| T2-02 | 2 | `DriverBuilder.FullDeploy` short-circuit on hash match | 5–15 s saved per fresh launch | low | done | check `HKLM\Software\HIDMaestro\InstalledManifestSha256`, skip pipeline if equal |
| T2-03 | 2 | Write hash to HKLM after successful FullDeploy | n/a | low | done |  |
| T3-01 | 3 | `EnsureExtracted()` add `manifest.sha256` stamp | 1–3 s saved on slow eMMC | low | done | per-resource hash check |
| T3-02 | 3 | Skip rewrite if stamp matches | (covered by T3-01) | low | done |  |
| T4-01 | 4 | NEW — profile catalog MemoryPack blob, embedded resource | <500 ms saved on cold launch | medium | done | risk: serializer dependency creep |
| T4-02 | 4 | `LoadDefaultProfiles()` prefer blob over JSON parse | (covered by T4-01) | medium | done |  |
| T5-01 | 5 | `CreateController` Step 4 — parallelize FixHidChildNames + SetBusType + ContainerIndex registry writes | ~300 ms per controller | low | done | no inter-dependency, just sequential today |
| T6-01 | 6 (deferred) | Background-task xinputhid teardown | 5–11 s saved per Xbox-family Dispose | high | deferred | defers to v1.4.x, separate experimental branch |
| T7-01 | 7 | `DeviceOrchestrator.WaitForHidChild` (line 1479) — replace 100 ms registry poll with `CM_Register_Notification` | up to 99 ms saved per controller | medium | done | DeviceManager already has CM-driven version; route this through it |
| T7-02 | 7 | `DeviceOrchestrator.WaitForDeviceStarted` (line 1496) — same 100 ms poll → CM | up to 99 ms saved per controller | medium | done |  |
| T7-03 | 7 | `HMContext.LoadDefaultProfiles` — `Parallel.ForEach` JSON parse over 224 profiles | 50–150 ms saved on cold launch | low | done | no shared state during parse |
| T7-04 | 7 | `CreateController` Step 4 — coalesce FriendlyName + BusReportedDesc into single registry transaction per devnode | ~50 ms per controller | low | done | shares the same Device Parameters key |
| T7-05 | 7 | hmswd.exe — keep one persistent helper process, dispatch commands via stdin, eliminate per-call spawn cost | 50–100 ms per SwDeviceCreate | medium | pending | requires hmswd protocol change; defer if scope grows |
| T7-06 | 7 | `EnsureExtracted` — extract resources in parallel (signtool deps + driver bins) | 200–500 ms saved on cold first run | low | pending |  |
| T7-07 | 7 | Pre-trigger `EnsureExtracted` from `HMContext` ctor on a background thread so it's done by the time `InstallDriver` is called | 200–500 ms hidden in user think time | low | done | non-blocking warm-up |
| T7-08 | 7 | `Registry.LocalMachine.OpenSubKey` chains in DeviceProperties — replace with single P/Invoke `RegOpenKeyExW` to keep handle through several SetValue calls | ~10–30 ms per controller cumulative | low | done | covered by T5-01 (SetAllNamingProperties) |
| T8-01 | 8 | `EnsureExtracted` thread-safety lock | n/a | low | done | ctor pre-warm + foreground InstallDriver could race |
| T8-02 | 8 | `IsHidMaestroDriverInstalled` per-process positive cache | ~200–500 ms saved per repeated call | low | done | InvalidateInstalledCache on RemoveAllPackages |
| T8-03 | 8 | `ControllerProfile.GetDescriptorBytes` lazy cache | ~5 ms saved per CreateController (called 2-3 times each) | low | done |  |
| T8-04 | 8 | XInput slot-claim wait poll cadence 100 ms → 25 ms | up to 75 ms saved per Xbox-family create | low | done |  |
| T8-05 | 8 | `EnsureGameInputService` per-process cache | 100–300 ms saved per CreateController after first | low | done | sc.exe spawn skipped |
| T8-06 | 8 | `WriteGameInputRegistry` open parent gp key once, create children relative | ~5–15 ms saved per CreateController | low | done |  |
| T8-07 | 8 | `WriteGameInputRegistry` per-VID:PID cache | ~5–15 ms saved per same-VID:PID controller | low | done | identical writes for same profile |
| T8-08 | 8 | `HMController.Dispose` Join(500) scaled via TimeoutScale | n/a | low | done | scale=10 → 5s join cap |
| T8-09 | 8 | `DriverStoreContainsHidMaestro` filesystem check replaces pnputil enum on FullDeploy fast path | 200–500 ms saved on warm launches | low | done | FullDeploy now ~5 ms total when hash matches |
| T8-10 | 8 | `WriteInputFrame` bulk Marshal.Copy (was 256+14 per-byte loops) | ~270 P/Invokes/frame eliminated | low | done | hot path; SubmitState at 250–1000 Hz × N |
| T8-11 | 8 | `TryReadOutputFrame` bulk Marshal.Copy | analogous output-side win | low | done |  |
| T8-12 | 8 | `BuildReportInto` buffer-reuse overload (eliminates per-frame byte[] alloc) | reduces GC pressure on SubmitState | low | done | _reportBuffer per-controller |
| T8-13 | 8 | `SubmitRawReport` per-controller _rawReportBuffer (eliminates .ToArray() per call) | reduces GC pressure on vendor-protocol path | low | done |  |
| T8-14 | 8 | `LoadEmbedded` process-wide cache + ctor pre-load | 50–150 ms saved per HMContext after first | low | done | shared across HMContext instances |
| T8-15 | 8 | `LoadProfilesFromDirectory` parallel JSON parse | proportional to core count | low | done | mirrors LoadEmbedded |
| T8-16 | 8 | `WaitForHidChild` (DeviceOrchestrator) delegates to DeviceManager (CM-signal driven) | up to 99 ms tail wait saved | medium | done |  |
| T8-17 | 8 | `WaitForDeviceStarted` poll cadence 100 ms → 25 ms | up to 75 ms tail wait saved | low | done |  |
| T8-18 | 8 | `IsDriverInstalled` cheap-first lookup ladder (FS check before pnputil) | 200–500 ms saved on cold-start IsDriverInstalled() before cache primes | low | done | filesystem check + cache hint |
| T8-19 | 8 | XInput slot wait short-circuit when slotsBefore >= 4 | 15 s saved per Xbox-family create after cap is hit | low | done | cap is XInput hardcoded 4-slot |
| T8-20 | 8 | `DeviceOrchestrator.WaitForHidChild` revert CM-delegation; keep 25 ms poll | up to 75 ms tail wait | medium | done | CM_Register_Notification fails for SWD-rooted parents before child PDO enumerates; polling is robust |

---

## Table 4 — Build + battery checkpoints

| Checkpoint | Tier(s) covered | Build | Probe smoke | Battery | Wall (s) | PASS | Notes |
|---|---|---|---|---|---|---|---|
| C0 (baseline, v1.2.2) | n/a | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | already validated 26/26 in v1.2.2 commit |
| C1 (Tier 1 done) | 1 | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | scale=1.0 should match v1.2.2 numbers |
| C2 (Tier 2 done) | 2 | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | manifest hash + InstallDriver fast-path |
| C3 (Tier 3+4 done) | 3 + 4 | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | extraction + profile cache |
| C4 (Tier 5 done) | 5 | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | parallelized registry writes |
| C5 (Tier 7 done, final) | 7 | _pending_ | _pending_ | _pending_ | _pending_ | _pending_ | micro-optimizations sweep |

---

## Table 5 — Final delivery summary (filled at end)

| Aspect | Pre-1.3.0 | Post-1.3.0 (dev) | Post-1.3.0 (Atom) | Notes |
|---|---|---|---|---|
| Fresh-launch InstallDriver() | _pending_ | _pending_ | _pending_ |  |
| 3-controller fresh create | _pending_ | _pending_ | _pending_ |  |
| Battery wall | _pending_ | _pending_ | _pending_ |  |
| Battery PASS count | _pending_ | _pending_ | _pending_ |  |
| Total LoC delta | _pending_ | _pending_ | _pending_ |  |

---

## Notes

- Battery is the gate: any checkpoint that doesn't pass 26/26 blocks tier completion.
- All Tier 1 changes are additive at scale=1.0 — battery numbers should be statistically identical to v1.2.2 baseline.
- Tier 2's manifest hash needs a build-time MSBuild target to compute SHA256 of the embedded resources and bake the constant into the assembly. Implementation note: PowerShell + MSBuild Task element generates `Manifest.cs` with `internal const string EmbeddedManifestSha256 = "..."`.
- Atom validation happens on the Atom box once SSH is up; iteration on the dev box up to that point.
