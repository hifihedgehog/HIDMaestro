# swap_regression.ps1: live-swap teardown regression battery

Drives `HIDMaestroTest.exe` through 23 single- and multi-controller
create/swap/remove/force-kill sequences and verifies every scenario
leaves zero HIDMaestro PnP devnodes in the `PRESENT` state. Catches
the symptom that v1.1.31 fixed (`SwDeviceLifetimeParentPresent`
resurrection after `DIF_REMOVE`) and any future regression in the
same area.

Covers all five controller archetypes a real consumer (PadForge etc.)
deploys: Xbox 360 Wired (non-xinputhid + XUSB SwDevice companion),
Xbox Series Bluetooth (xinputhid SwDevice gamepad), DualSense (Sony
plain HID), Switch Pro (Nintendo plain HID), and a runtime-built
custom profile (BEEF:F000) authored via the SDK's `HMProfileBuilder`
+ `HidDescriptorBuilder` API surface, mirroring PadForge's
`HMaestroProfileCatalog.BuildCustomProfile` exactly.

## How to run

```powershell
# from an ELEVATED PowerShell, repo root or anywhere
./test/regression/swap_regression.ps1

# specific scenario only (wildcard match on scenario name)
./test/regression/swap_regression.ps1 -Filter 'S08*'

# verbose: prints every stdin command sent to the test app
./test/regression/swap_regression.ps1 -Verbose

# point at a non-default exe (e.g. a published build)
./test/regression/swap_regression.ps1 -Exe C:\path\to\HIDMaestroTest.exe
```

Exit code: `0` if every scenario passed, `1` if any failed.

Total wall time: 20 to 25 minutes for the full battery. Most of which
is deliberate cascade-settle waits (Series BT teardown takes about 10s
of xinputhid filter unbinding regardless of code path). Slow machines
with profile-extraction or PnP-quiesce overhead may push this longer.
Use `-Filter` to run a single scenario in 1-2 minutes when iterating.

## Why elevated

`HIDMaestroTest.exe` self-elevates via UAC if launched without admin
rights. Self-elevation re-launches as a NEW process, breaking the
stdin pipe the regression script depends on for sending swap commands.
The script aborts up front rather than producing silent garbage when
not run elevated.

## What each scenario catches

| Scenario | Pattern | Catches |
|---|---|---|
| `S01_Single_360_BT_360`           | 360 -> BT -> 360 | The original bug user reported. SwDevice teardown leaving a phantom xinputhid-bound BT child. |
| `S02_Single_360_BT_360_BT`        | + -> BT      | Secondary regression: leftover surfacing only after a 4th swap. |
| `S03_Single_LongCycle_8swaps`     | 8 alternating swaps | Suffix-allocator stress + repeated DIF_REMOVE+hmswd-remove path. |
| `S04_Single_BT_360_BT`            | BT first    | Initial xinputhid bind path before any non-xinputhid create. |
| `S05_Single_Mixed_Families`       | 360 -> DS -> Switch -> BT -> 360 | Cross-family swaps (XUSB companion, plain HID, xinputhid gamepad). |
| `S06_Single_SameProfileSwap`      | BT -> BT (same id) | Per-call unique suffix lets identical-profile recreation work. |
| `S07_Multi_CreateAll_Idle`        | 4 mixed, idle, quit | Baseline multi-slot teardown via clean process exit. |
| `S08_Multi_SwapOneSlot`           | 4 wired, swap slot 1 | Single-slot swap does not leak across siblings. |
| `S09_Multi_SwapAllSlots`          | 4 wired, swap each slot to a different family | Concurrent live-swap of every slot. |
| `S10_Multi_RemoveOne`             | 3 mixed, `remove 1` | `HMController.Dispose` without replacement leaves no residue. |
| `S11_Multi_MultipleXinputhid`     | 3 different xinputhid profiles + swap | xinputhid INF-match handling under multiple concurrent xinputhid binds. |
| `S12_ForceKill_Recovery`          | Hard-kill, then clean session | Next session's `RemoveAllVirtualControllers` purges orphans from a force-kill. |
| `S13_AcrossProcess_Recreation`    | proc1 (BT+360) -> quit -> proc2 (BT+360) + swaps | Per-process suffix prefix actually varies. Identical (enumerator+suffix+ContainerID) tuple across processes is the kernel's reuse-existing trap; v1.1.20 fix. |
| `S14_Single_RapidSwaps_NoSettle`  | 4 swaps queued back-to-back, no inter-command sleep | Per-controllerIndex teardown gate + reentrancy in Setup/Teardown. |
| `S15_Multi_SixControllers`        | 6 mixed (beyond XInput's 4) + swap slot 5 | Slot-allocator skip + ContainerID encoding for high indices. The HM "6-controller baseline" use case. |
| `S16_Single_SameVidPid`           | xbox-360-wired <-> xbox-360-arcade-stick (both 045E:028E) | Registry-reuse path when only profile-level metadata differs. |
| `S17_ForceKill_MidCascade`        | Hard-kill 5s into a Series BT teardown's xinputhid filter unbind | Worst-case force-kill timing; phantoms left in mid-cascade state. |
| `S18_Single_AlternatingPattern`   | A->B->A->C->A->B->A | Suffix allocator state when revisiting prior profiles after intervening swaps. |
| `S19_Multi_RapidMultiSlotSwap`    | 4 controllers, swap each slot's profile back-to-back, no settle | Closest stdin proxy for PadForge's `ApplyAscendingIndexPreemption` async-dispose path. |
| `S20_Multi_HeterogeneousCascade`  | 4 controllers, every family in one batch, then `quit` | `HMContext.DisposeControllersInParallel` correctness with all four families simultaneously: ROOT-enumerated, SWD-XUSB, SWD-gamepad-xinputhid, plain HID. |
| `S21_Custom_CreateIdle`           | Custom (BEEF:F000) create + idle + quit | `HMProfileBuilder` + `HidDescriptorBuilder` round-trip — runtime-built profile loads, binds, and tears down through the same path as embedded profiles. |
| `S22_Custom_SwapCycle`            | Custom <-> 360 -> Custom <-> BT -> Custom <-> DualSense | Cross-family swaps to/from a non-embedded faux-VID profile; suffix allocator handles BEEF:F000 alongside real VIDs. |
| `S23_Multi_CustomInMix`           | 5 mixed: 360 + Series BT + DualSense + Switch Pro + Custom, then swap the custom slot | Real PadForge-shape consumer config: every archetype the SDK supports, all live, plus a swap on the custom slot. |

## What "PASS" means

Per scenario:

1. Snapshot every `HIDMAESTRO*` / `VID_045E&PID_028E*` PnP devnode that
   `CM_Locate_DevNodeW(NORMAL)` reports as `PRESENT` BEFORE the scenario
   runs (the baseline).
2. Run the scenario, then sleep 12s for kernel cascade to settle.
3. Snapshot the same set AFTER.
4. PASS iff `(after \ before) == empty` (i.e. no new `PRESENT` entries
   leaked across the scenario).

`PHANTOM` entries (registry residue with no live devnode) are ignored.
They do not occupy XInput slots, do not show in active-controller
lists, and are cosmetic-only registry leftovers from the historic
SwDevice behavior. Only `PRESENT` is what consumers actually see.

## Diagnosing a FAIL

The script prints the leftover instance IDs when a scenario fails:

```
[FAIL] S08_Multi_SwapOneSlot 47832ms
       Leftover: SWD\HIDMAESTRO_VID_045E_PID_0B13&IG_00\<suffix>_0001
```

For deeper inspection, every test process runs with `HIDMAESTRO_DIAG=1`
in its environment, so `%TEMP%\HIDMaestro\teardown_diag.log` records
every `TeardownController` call (entry/exit/timing) and every
`SwdDeviceFactory.Remove` outcome (`hr` plus `present`-after-remove).
On a FAIL, grep that log for the leftover instance ID to see exactly
what the SDK did.

## Adding a new scenario

1. Add a `Scenario-...` function near the existing ones. It must:
   - spawn a test process via `Start-HMTestProcess`
   - drive its stdin via `Send-Cmd`
   - end with `Stop-HMTestProcess` in a `finally`
2. Append an entry to the `$scenarios` array at the bottom of the
   runner.
3. Run with `-Filter` matching just the new scenario to iterate.

The verification (`Test-Scenario`) wraps your scenario function
automatically; you do not write the assertions yourself.

## Profile coverage

The battery exercises every meaningful teardown code path through
`$Profiles`:

- **Xbox 360 wired**: non-xinputhid, ROOT main HID + SWD XUSB companion
- **Xbox Series BT, Xbox One S BT, Xbox Elite v2 BT**: three different
  xinputhid INF matches, all SWD gamepad-companion path
- **DualSense (USB), DualSense (BT), Switch Pro**: plain HID, no
  companions, simplest teardown path
- **Custom (PadForge-style, BEEF:F000)**: runtime-synthesized HID
  descriptor (2x16-bit sticks + 2x8-bit triggers + hat + 11 buttons)
  built via `HidDescriptorBuilder`, wrapped in an `HMProfile` via
  `HMProfileBuilder`, written to disk by `HIDMaestroTest
  make-custom-profile <out-dir>`, and loaded by the test app via
  `emulate --profile-dir <dir>`. Mirrors PadForge's
  `HMaestroProfileCatalog.BuildCustomProfile` exactly.

Add more profile slots in `$Profiles` as new families warrant testing.
