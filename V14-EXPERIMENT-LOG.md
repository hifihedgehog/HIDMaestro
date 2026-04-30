# v1.4.0 — Xbox-controller fast-create breakthrough hunt

Live experiment log. Updated as branches land.

## Empirical results (Win11 26200, Ryzen 9955HX3D)

| Op | Wall time | Notes |
|---|---|---|
| `CreateController(xbox-360-wired)` baseline | **540–580 ms** | Existing SetupDi + UpdateDriver path |
| Main devnode `CM_Disable_DevNode` | **3 ms** | Returns immediately |
| Main devnode `CM_Enable_DevNode` | **26–33 ms** | Driver re-fires `EvtDriverDeviceAdd` |
| **Main devnode disable+enable cycle** | **55–62 ms** | **8.7× faster than full create** |
| HID descriptor change confirmed via `HIDP_CAPS` | YES | InputReportLen 19→64, ButtonCaps 1→2, ValueCaps 8→9 (xbox-360 → DualSense) |
| XUSB companion `CM_Disable_DevNode` | **5489 ms** with `hr=0x00000017 (CR_REMOVE_VETOED)` | Kernel-side filter blocks symmetric disable |
| XUSB companion `CM_Enable_DevNode` | 0 ms | (After veto'd disable) |
| XInput slot occupancy across main-only swap | 1/4 stable | Companion stays alive throughout |

## The breakthrough

**Main devnode `CM_Disable + descriptor registry write + CM_Enable = 60 ms** — and `hidclass.sys` re-queries `IOCTL_HID_GET_REPORT_DESCRIPTOR` on the post-enable cascade, so the device's HID identity changes end-to-end. This is **8.7× faster than the existing `CreateController` path** for Xbox 360 wired.

## v1.4.0 architecture — devnode pool with always-alive companion

```
┌────────────────────────────────────────────────────────────────┐
│  HMContext.InstallDriver (one-time, hidden behind first launch)│
├────────────────────────────────────────────────────────────────┤
│  for each pool slot 0..N-1:                                    │
│    pre-create main devnode at ROOT\VID_045E&PID_028E&IG_00\<i> │
│    pre-create XUSB companion at SWD\HIDMAESTRO\<sid>_<i>       │
│    leave both ENABLED but SDK marks 'free for claim'           │
│  Total cost: ~540 ms × N (paid once)                           │
└────────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────────┐
│  HMContext.CreateController(profile)  — HOT PATH               │
├────────────────────────────────────────────────────────────────┤
│  if profile is xbox-360-wired and pool has free slot:          │
│    claim slot                                                  │
│    write profile descriptor + VID/PID to                       │
│      HKLM\SOFTWARE\HIDMaestro\Controller<i>                    │
│    CM_Disable_DevNode + CM_Enable_DevNode on main (~60 ms)    │
│    write SDK shared-memory ControllerIndex                     │
│  else: existing slow path                                      │
│  Total cost (hot): ~60–100 ms                                  │
└────────────────────────────────────────────────────────────────┘

┌────────────────────────────────────────────────────────────────┐
│  HMController.Dispose                                          │
├────────────────────────────────────────────────────────────────┤
│  CM_Disable_DevNode on main (~3 ms — cuts HID identity)        │
│  Mark slot 'free for claim'                                    │
│  Companion stays alive — XInput sees idle controller           │
│  Total cost: ~5 ms                                             │
└────────────────────────────────────────────────────────────────┘
```

## Tradeoffs

- **XInput "ghost" slot:** the XUSB companion stays alive when an SDK consumer disposes. XInput / WGI / DI see it as a connected controller until process exit (or until pool actually tears down). UX implication: joy.cpl shows the device after Dispose. Mitigated by ensuring the device sends no input frames after disable (driver-side gate via `ReportDescriptorSize=0` or similar).

- **Pool exhaustion:** if consumer creates more than N Xbox 360 wired controllers, fall back to the existing slow path. N=4 matches XInput's hard 4-slot cap.

- **First-launch cost:** install pays ~540 × 4 = 2160 ms for the initial pool. One-time per machine.

## Validation gate

Before merging to master:
1. Probe-level: `descriptor_swap_check` — DONE (PASS).
2. End-to-end multi-controller test: 6× Xbox 360 wired pool fill + dispose + recreate cycle, all <100 ms each.
3. swap_regression battery 26/26 PASS.
4. PadForge-shaped repro: rapid profile-swap cycle, no XInput slot leak, no consumer-side ghost.
