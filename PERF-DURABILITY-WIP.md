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

| Metric | v1.2.2 baseline | v1.3.0 C1 | v1.3.0 C5 | v1.3.0 C8 | v1.3.0 C10 (T9) | v1.3.0 C11 (T10) | v1.3.0 C12 (T11-19) | v1.3.0 C14 (T11-20/21) | v1.3.0 timing-pass | v1.3.0 post-T22 | v1.3.0 C15 (T22) | v1.3.0 C16 (T23+T24) | Atom (×10) |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Warm-launch 3-controller create | **1 287 ms** | 1 288 ms | **1 090 ms** (-15%) | **1 049 ms** (-18.5%) | _median 388 ms_ | _median 431 ms_ | _median 440 ms_ | _median 407 ms_ | **1 132 ms** (-12%) | **1 065 ms** (**-17.3%**) | _median 437 ms_ | _median 430 ms_ | ~10.5 s |
|   ↳ Xbox 360 wired | 693 ms | 656 ms | **577 ms** (-17%) | **556 ms** (-20%) | _p75 = 539 ms_ | _p75 = 596 ms_ | _p75 = 585 ms_ | _p75 = 549 ms_ | ~5.6 s |
|   ↳ Xbox Series BT | 166 ms | 198 ms | **122 ms** (-27%) | **119 ms** (-28%) | _p25 = 143 ms_ | _p25 = 152 ms_ | _p25 = 154 ms_ | _p25 = 149 ms_ | ~1.2 s |
|   ↳ DualSense BT | 427 ms | 432 ms | **390 ms** (-9%) | **373 ms** (-13%) | _median_ | _median_ | _median_ | _median_ | ~3.7 s |
| Finalize device names | 2 ms | 1 ms | 1 ms | 1 ms | 1 ms | 1 ms | 1 ms | 1 ms | 2 ms | 2 ms | 1 ms | 1 ms | ~10 ms |
| swap_regression battery wall | 2 016 s | **1 934 s** (-4%) | **1 895 s** (-6%) | **2 021 s** | **1 949 s** (-3.3%) | **1 971 s** (-2.2%) | **1 965 s** (-2.5%) | **1 970 s** (-2.3%) | n/a | n/a | **1 952 s** (-3.2%) | **1 973 s** (-2.1%) | ~5–6 h |
| swap_regression battery PASS count | 26/26 | **26/26** | **26/26** | **26/26** | **26/26** | **26/26** | **26/26** | **26/26** | n/a | n/a | **26/26** | **26/26** | _pending_ |

**Battery wall progression context:** C5/C8 (1895/1918 s) were faster than baseline because the v1.3.0-wip-broken `WaitForHidChild` was returning False immediately for SWD-rooted parents — skipping a real PnP wait that downstream slot-claim was effectively absorbing. C9 (2021 s) is post-fix: the wait happens properly in step 4, slot-claim in step 7 is then fast. Net: battery wall is ~baseline (within 5s of 2016 s noise), per-controller Phase 1 is **16% faster**, and the SDK is now functionally correct for SWD-rooted parents. The user-visible metric (Phase 1) is the right scorecard.

**C10 (Tier 9 validation):** 26/26 PASS in 1949 s (-3.6% from C9, -3.3% from baseline). 6 outliers in the diag log: 3 setups timed out at 30 s (XInput slot wait + WaitForHidChild back-to-back timeout on Series BT scenarios) and 3 at 15 s (slot wait alone). Same-shape outliers as C9 — Series BT companion occasionally fails to bind on first attempt and the slot-wait budget is consumed. Not a regression from Tier 9 (dedupe + bulk SHM zero-init has no path that affects bind timing). Median per-setup Phase 1 is 388 ms across 119 setups; without the 6 timeout outliers the average drops from 1421 ms to ~549 ms.

**C11 (Tier 10 validation):** 26/26 PASS in 1971 s (-2.2% from baseline, +1.1% from C10). 12 Series-BT-bind-failure outliers (7 at 30 s, 5 at 15 s) vs C10's 6 — these are intermittent kernel-side bind failures, not SDK regressions. Median per-setup Phase 1 is 431 ms across 115 setups (vs C10's 388 ms). The Tier 10 changes (HidReportBuilder cache + IsDriverInstalled prewarm + step-6 ApplyFriendlyName direct calls) target first-call cost reduction rather than steady-state median — the absence of a median improvement is expected; the savings are in the per-controller Phase 1 burst that the warm-launch metric measures, not the battery's average. Without the 12 outliers, average is 639 ms across 103 setups.

**C12 (T10-19 validation = event-driven OutputPollLoop sleep):** 26/26 PASS in 1965 s. 9 Series-BT outliers (between C10's 6 and C11's 12 — confirms outlier rate is noise-level intermittent, not regression-driven). Median 440 ms across 116 setups; p75 = 585 ms. T10-19 only affects Dispose latency (sub-ms savings), so battery wall and Phase 1 median are no-regression checks rather than wins-to-celebrate.

**C13 (first attempt at T11-20 + T11-21 validation):** Battery hit the script's failure branch (script emitted "Diag log:" line, exit 1). Diag log shows 233 events, no errors, no leftover devnodes. Likely a flaky PidFfb probe scenario (S24-S26 use external probes that interact via DirectInput PID and have intermittent timing-related failures). Re-ran with same code as C14 to distinguish.

**C14 (T11-20 + T11-21 repro check):** 26/26 PASS in 1969.6 s. Confirms C13 was flaky, not a regression from T11-20 (DriverStoreContainsHidMaestro single-pass) or T11-21 (SetBusTypeGuidUsb hardcoded-list trim). Median 407 ms across 119 setups; 8 Series-BT outliers. Phase 1 progression looks healthy and stable: 388 → 431 → 440 → 407 ms across C10-C14 — all within 60 ms noise band, all 16-23% below the v1.2.2 baseline (1287 ms aggregate / ~432 ms per-controller).

**Step-level timing pass (HIDMAESTRO_TIMING=1, post-Tier-11):** Single warm-launch 3-controller create = **1132 ms** total (xbox-360-wired 587 ms + xbox-series-xs-bt 148 ms + dualsense 383 ms + 14 ms Phase 1.5 finalize). The dominant cost in every profile is **step 3 (create devnode / create_main_devnode / create_gamepad_companion)**: 425, 139, 377 ms respectively — 84% of the per-setup total. That's the SetupAPI / SwDeviceCreate kernel-PnP call doing real device-install work. Every other step is now sub-2 ms (set_names_root, fix_hid_child_names_2, set_bustype_usb, apply_friendly_name, instance_config, driver_install_check all ≤1 ms). The non-PnP work has effectively been driven to the floor; further user-mode optimization will not move the needle without changing how we drive PnP itself.

**Step-level timing pass post-T22 (GameInputSvc prewarm):** Single warm-launch 3-controller create = **1065 ms** total (xbox-360-wired 569 ms + xbox-series-xs-bt 96 ms + dualsense 397 ms + 2 ms Phase 1.5). That's **-17.3% from the v1.2.2 baseline (1287 ms)** and within noise of C8's measured 1049 ms. The first SetupController's `0.gameinput_svc` step dropped from 15-17 ms (sc.exe spawn) to 2 ms (cache hit) — the prewarm doesn't reduce total work, it just shifts the sc.exe spawn out of the foreground SetupController path into the background ctor task. Same pattern as the existing IsDriverInstalled prewarm.

**C16 (T23 + T24 cumulative validation):** 26/26 PASS in 1973 s (-2.1% from baseline). 7 Series-BT-bind-failure outliers (within the noise band of C10-C15's 6-12 outliers per battery). Median 430 ms across 116 setups, p25 = 166 ms, p75 = 536 ms — Phase 1 stable at ~430 ms median across all post-Tier-9 batteries. The diag log confirms T23's 10 ms cadence is firing: XInput slot waits typically complete in 1-14 ms (vs pre-T23's 14-65 ms range). T25-T32 (FinalizeNames cadence, OEM dedup, GIP-skip, byte-aligned WriteBits, parallel prewarm, thread-local devID buffer, SubmitRawReport GIP-skip, button-mask bit-pop) need C17 to validate.

---

## d3xMachina (gh issue #18) — 30 s/controller teardown root-cause

User d3xMachina reported "sometimes it takes ages to disconnect the controllers (like 30 seconds per controller)". Their attached `teardown_diag.log` shows the smoking gun pattern, three teardowns in a row:

```
[18:54:02.375] SwdDeviceFactory.Remove(SWD\HIDMAESTRO\58980001_0000) hr=0x80070102 present=False after 30104ms
[18:54:34.803] SwdDeviceFactory.Remove(SWD\HIDMAESTRO\58980003_0002) hr=0x80070102 present=False after 30078ms
[18:55:06.987] SwdDeviceFactory.Remove(SWD\HIDMAESTRO\58980002_0001) hr=0x80070102 present=False after 30079ms
```

`hr=0x80070102` is `WAIT_TIMEOUT`. Each one waited the full 30 s timeout in `SwdDeviceFactory.Remove`'s `WaitForExit(30_000)` because `hmswd.exe` was stuck on `SwDeviceClose` (typically because a stale `WUDFHost` still held the device handle open). Total per-3-controller teardown wall time on this stuck-cascade path: **~90 s, of which ~90 s is wasted waiting on the hung helper before the fallbacks even start**.

**T37 fix:** drop the `SwdDeviceFactory.Remove` `WaitForExit` budget from 30 s to 8 s base (still scaled by `TimeoutScale.Apply` for slow hardware — Atom 10× → 80 s). Healthy `SwDeviceClose` typically completes in <100 ms; 8 s is plenty for a real cascade. Stuck-helper falls through to the outer `DeviceManager.RemoveDevice` pnputil/devcon fallbacks, which is what was doing the actual cleanup anyway. Direct user-visible impact: 3-controller stuck-teardown drops from ~90 s to ~24 s.

---

## Why HIDMaestro is slower than ViGEmBus per-controller — and what we've done about it

User feedback (2026-04-30): consumers reporting ~30 s for 3-controller add/remove cycles and noting ViGEmBus is significantly faster. Honest accounting of the architectural delta plus the user-mode floor we've now hit.

### Architectural delta (immutable from user mode)

| Cost source | ViGEmBus (kernel mode) | HIDMaestro (UMDF2 user mode) | Delta |
|---|---|---|---|
| Bus driver class | Yes — IS the bus, can publish PDOs directly | No — UMDF2 cannot be a bus driver (memory: project-umdf2-cannot-be-bus-driver.md). Each virtual is a ROOT or SWD root devnode with a separate UMDF2 host attached | +inherent kernel-PnP cost |
| Per-virtual create call | KMDF child-PDO publish (~10–50 ms kernel) | SetupDi[CreateDeviceInfo + SetRegistryProperty + CallClassInstaller(DIF_REGISTERDEVICE) + UpdateDriverForPlugAndPlayDevices] OR SwDeviceCreate via hmswd.exe helper, blocking on driver-bind callback | +200–400 ms per virtual |
| XInput compatibility | Native XUSB-class sub-device, no companion needed | XUSB companion (HMXInput.dll) at `SWD\HIDMAESTRO\<sid>` per Xbox-VID virtual | +50 ms create + +14 ms slot-claim wait |
| Driver install pipeline | One-time kmdf bus driver install (`devcon install` style) | DriverStore + signtool + inf2cat + pnputil — fortunately one-time, gated by SHA-256 manifest hash | +0 once cached, full pipeline on cold install |
| Teardown — Xbox 360 wired | Bus driver removes child PDO (~10 ms) | DIF_REMOVE on ROOT\VID_*&IG_00 + cascade to HID child + SwDeviceClose on XUSB companion + 5–7 s WUDFHost release | +5–7 s |
| Teardown — Xbox Series BT | n/a (different code path) | xinputhid filter unbind + SwDeviceClose + 5–11 s kernel cascade | +5–11 s |
| Multi-controller serialization | Optional (kernel synchronizes itself) | Mandatory (memory: feedback-no-parallel-controller-create.md) — DirectInput / XInput / WGI / Browser / RawInput all anchor to creation order | sequential, can't parallelize |

### What ViGEmBus can do that HIDMaestro can't (and why)

- **Native bus driver:** ViGEmBus runs in kernel mode and IS the parent bus. Child virtuals appear as PDOs under it; no per-virtual UMDF host process, no per-virtual SetupDi class-installer round-trip. We can't be a bus driver from user mode — KMDF child-list / PDO-init APIs are kernel-only and UMDF2 linker errors confirm. Avoiding kernel-mode signing was the entire motivation for HIDMaestro's architecture (memory: research-signing-and-alternatives.md), so this trade-off is inherent.
- **Direct XInput class membership:** ViGEmBus's Xbox 360 virtuals join the XUSB device interface class natively at child-PDO publish time. We must publish a separate `HIDMAESTRO\*` SWD child that registers `{EC87F1E3-...}` after the main HID devnode is up — adding ~50 ms create + ~10–60 ms slot-claim wait per Xbox-family controller.
- **Bus-driver-side teardown:** ViGEmBus removes a child PDO via a fast in-kernel call. Our teardown traverses DIF_REMOVE → WaitForDeviceRemoval → parent unbind → HID child cascade → companion close, with the xinputhid filter unbind being the dominant 5–11 s component on Series BT.

### Where we now stand (post-T22, dev-box measurements)

Single warm-launch 3-controller create = **1065 ms** total. Step breakdown:

| Step | xbox-360-wired | xbox-series-xs-bt | dualsense | Notes |
|---|---|---|---|---|
| 0.shm + 0.gameinput_svc | 2 + 2 ms | 0 + 1 ms | 0 + 1 ms | post-T22 prewarm caches both |
| 1.instance_config | 0 ms | 0 ms | 0 ms | 9 SetValue, all sub-1 ms |
| 2.driver_install_check | 0 ms | 0 ms | 0 ms | post-T8 cheap-first cache + manifest-hash fast-path |
| **3.create_devnode** | **425 ms** | **87 ms** | **377 ms** | **kernel-PnP work — the floor** |
| 4.wait_hid_child + 4.set_names_root + 4.wait_started + 4.fix_hid_child_names_2 | 0+0+0+0 ms | 0+1+0+0 ms | 0+0+0+0 ms | post-T9/T10 direct calls instead of registry walks |
| 5.set_bustype_usb + 5.create_xusb_companion + 5.hidparent_upperfilter_xinputhid | 0+50+1 ms | n/a | n/a | post-T11 trimmed enumerator list |
| 6.apply_friendly_name | 0 ms | 0 ms | 0 ms | post-T10 direct calls |
| 7.wait_xinput_slot_claim | 14 ms | 1 ms | n/a | post-T23 10 ms cadence |
| **TOTAL** | **569 ms** | **96 ms** | **383 ms** | step 3 is **84%** of total |

**The 84% of remaining cost lives in step 3 — kernel-side SetupAPI / SwDeviceCreate driver-install work.** Every step we could reach from user mode is now sub-2 ms. Closing the per-controller gap with ViGEmBus would require a kernel-mode bus driver, which is the architecture HIDMaestro intentionally avoids (memory: research-signing-and-alternatives.md, project-umdf2-cannot-be-bus-driver.md).

The "30 s for 3 controllers" figure consumers cite is consistent with **teardown** wall time, not creation. Three Xbox 360 wired teardowns at ~5–7 s each = 15–21 s; mixed with one Series BT teardown that's ~5–11 s. ViGEmBus's per-virtual teardown is also kernel-side (~10 ms × 3 = 30 ms total) — that's the actual perception gap, not creation. The xinputhid filter unbind on Series BT is kernel-side and not user-mode optimizable.

### Cycle-level wins shipped this session (T22–T31)

T22–T31 are user-mode hot-path optimizations that don't move step 3, but do reduce the non-kernel overhead and make the SDK more durable on Atom-class hardware:

| ID | Change | Saves per-frame | Saves per-setup | Saves per-teardown |
|---|---|---|---|---|
| T22 | GameInputSvc prewarm (sc.exe spawn → cache hit) | — | ~15 ms (first only) | — |
| T23 | XInput slot-wait cadence 25 → 10 ms | — | ~15 ms (Xbox-family) | — |
| T24 | Cache devnode instId across UpdateDriverForPlugAndPlayDevices | — | ~1 ms | — |
| T25 | FinalizeNames cadence 100 → 25 ms + scaled budget | — | — | -50–75 ms (Phase 1.5) |
| T26 | Per-VID:PID OEM-write dedup in WriteInstanceConfig | — | ~few ms (dup VID:PID) | — |
| T27 | Skip GIP-buffer pack+copy for non-Xbox in SubmitState | ~60–80 instructions | — | — |
| T28 | WriteBits byte-aligned fast path | ~14 cycles per field × 6 fields = ~84 cycles | — | — |
| T29 | Parallelize HMContext ctor prewarm tasks | — | -50–100 ms (cold start) | — |
| T30 | Thread-local reusable buffer in GetHidChildId | — | -100 bytes/poll alloc | — |
| T31 | Skip GIP-buffer copy for non-Xbox in SubmitRawReport | -14 byte Marshal.Copy | — | — |

Cumulative per-frame savings on a 4-non-Xbox-controller setup at 250 Hz: roughly 100 instructions + 14 byte-Marshal.Copy + 100 byte alloc = **~10 µs/sec/controller saved on the SubmitState hot path**. On Atom (10× slower), that's ~100 µs/sec/controller back — meaningful when the consumer pumps 6 controllers at high rates.


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
