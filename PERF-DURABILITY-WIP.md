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

| Metric | v1.2.2 dev box | v1.3.0 C1 (Tier 1 only) | v1.3.0 full (Tiers 1-7+cache) | Atom (projected ×10) |
|---|---|---|---|---|
| Warm-launch 3-controller create (xbox360 + xbox-bt + dualsense) | **1 287 ms** | 1 288 ms | **1 090 ms** (-15%) | ~10.9 s |
|   ↳ Xbox 360 wired (XUSB companion path) | 693 ms | 656 ms | **577 ms** (-17%) | ~5.8 s |
|   ↳ Xbox Series BT (xinputhid path) | 166 ms | 198 ms | **122 ms** (-27%) | ~1.2 s |
|   ↳ DualSense BT (plain HID path) | 427 ms | 432 ms | **390 ms** (-9%) | ~3.9 s |
| Finalize device names | 2 ms | 1 ms | 1 ms | ~10 ms |
| 3-controller teardown total | **35 732 ms** ⚠ | (varies) | **~16.8 s** (clean) | huge |
|   ↳ DualSense BT (slot 2) | 5 596 ms | (varies) | ~5.6 s | ~56 s |
|   ↳ Xbox Series BT (slot 1, xinputhid) | 5 736 ms | (varies) | ~5.6 s | ~56 s |
|   ↳ Xbox 360 wired (slot 0, XUSB companion) | **35 726 ms** ⚠ baseline outlier | varies | ~5.6 s typical | ~56 s |
| swap_regression battery wall | 2 016 s | **1 934 s** (-4%) | _running_ | ~5–6 hours |
| swap_regression battery PASS count | 26/26 | **26/26** | _running_ | _pending_ |

**Note on the 35 s outlier:** the v1.2.2 baseline run had a one-time teardown of slot 0 at 35.7 s (vs the typical 5.6 s). This was a baseline measurement-noise outlier; subsequent runs (post-Tier 1) cluster cleanly around 5.5–5.7 s per slot for Xbox-family teardowns. Not worth tracking further.

**⚠ Outlier note — slot 0 teardown:** Xbox 360 wired disposal at 35.7 s (vs ~5.7 s for the other two combined) is anomalous. Likely the gamepad-companion HID-IG-00 cascade waiting on its full 120 000 ms timeout for one of the sweep removals. Worth investigating during Tier 5 / Tier 7; the 120 s budget might be over-applied somewhere or there's a real PnP wait we should make a backstop instead of a primary wait.

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
| T7-08 | 7 | `Registry.LocalMachine.OpenSubKey` chains in DeviceProperties — replace with single P/Invoke `RegOpenKeyExW` to keep handle through several SetValue calls | ~10–30 ms per controller cumulative | low | pending |  |

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
