# swap_regression.ps1: live-swap teardown regression battery

Drives `HIDMaestroTest.exe` through 70 single- and multi-controller
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

Total wall time: about 16 minutes for the full 70-scenario battery on a
16-core desktop. Most of it is deliberate cascade-settle waits (Series BT teardown takes about 10s
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
| `S03_Single_LongCycle_8swaps`     | 8 alternating swaps | Repeated DIF_REMOVE and hmswd-remove path under a fixed identity token. |
| `S04_Single_BT_360_BT`            | BT first    | Initial xinputhid bind path before any non-xinputhid create. |
| `S05_Single_Mixed_Families`       | 360 -> DS -> Switch -> BT -> 360 | Cross-family swaps (XUSB companion, plain HID, xinputhid gamepad). |
| `S06_Single_SameProfileSwap`      | BT -> BT (same id) | Recreating the identical profile at the same identity binds again at the same paths. |
| `S07_Multi_CreateAll_Idle`        | 4 mixed, idle, quit | Baseline multi-slot teardown via clean process exit. |
| `S08_Multi_SwapOneSlot`           | 4 wired, swap slot 1 | Single-slot swap does not leak across siblings. |
| `S09_Multi_SwapAllSlots`          | 4 wired, swap each slot to a different family | Concurrent live-swap of every slot. |
| `S10_Multi_RemoveOne`             | 3 mixed, `remove 1` | `HMController.Dispose` without replacement leaves no residue. |
| `S11_Multi_MultipleXinputhid`     | 3 different xinputhid profiles + swap | xinputhid INF-match handling under multiple concurrent xinputhid binds. |
| `S12_ForceKill_Recovery`          | Hard-kill, then clean session | Next session's `RemoveAllVirtualControllers` purges orphans from a force-kill. |
| `S13_AcrossProcess_Recreation`    | proc1 (BT+360) -> quit -> proc2 (BT+360) + swaps | The same identity token recreates across processes. An identical (enumerator + token + ContainerID) tuple is the kernel's reuse-existing trap. |
| `S14_Single_RapidSwaps_NoSettle`  | 4 swaps queued back-to-back, no inter-command sleep | Per-controllerIndex teardown gate + reentrancy in Setup/Teardown. |
| `S15_Multi_SixControllers`        | 6 mixed (beyond XInput's 4) + swap slot 5 | Slot-allocator skip and ContainerID encoding for high indices. The HM "6-controller baseline" use case. |
| `S16_Single_SameVidPid`           | xbox-360-wired <-> xbox-360-arcade-stick (both 045E:028E) | Registry-reuse path when only profile-level metadata differs. |
| `S17_ForceKill_MidCascade`        | Hard-kill 5s into a Series BT teardown's xinputhid filter unbind | Worst-case force-kill timing; phantoms left in mid-cascade state. |
| `S18_Single_AlternatingPattern`   | A->B->A->C->A->B->A | Registry and devnode state when revisiting prior profiles at one identity. |
| `S19_Multi_RapidMultiSlotSwap`    | 4 controllers, swap each slot's profile back-to-back, no settle | Closest stdin proxy for PadForge's `ApplyAscendingIndexPreemption` async-dispose path. |
| `S20_Multi_HeterogeneousCascade`  | 4 controllers, every family in one batch, then `quit` | `HMContext.DisposeControllersInParallel` correctness with all four families simultaneously: ROOT-enumerated, SWD-XUSB, SWD-gamepad-xinputhid, plain HID. |
| `S21_Custom_CreateIdle`           | Custom (BEEF:F000) create + idle + quit | `HMProfileBuilder` + `HidDescriptorBuilder` round-trip: runtime-built profile loads, binds, and tears down through the same path as embedded profiles. |
| `S22_Custom_SwapCycle`            | Custom <-> 360 -> Custom <-> BT -> Custom <-> DualSense | Cross-family swaps to/from a non-embedded faux-VID profile; the create path handles BEEF:F000 alongside real VIDs. |
| `S23_Multi_CustomInMix`           | 5 mixed: 360 + Series BT + DualSense + Switch Pro + Custom, then swap the custom slot | Real PadForge-shape consumer config: every archetype the SDK supports, all live, plus a swap on the custom slot. |
| `S24_PidFfb_RoundTrip`            | DI PID FFB shared-section round-trip on a custom HOTAS profile | `PublishPidPool` / `PublishPidState` reach the driver's HID feature replies; `Block Load` auto-allocation lands in the shared section. |
| `S25_PidFfb_AllocFree`            | PID FFB allocate-then-free under burst load + multi-controller | Two controllers' independent EBI tables; pool exhaustion path returns the right HID error. |
| `S26_PidFfb_FfbTest`              | DI PID FFB end-to-end via SharpDX/DI8 (`FfbTest`) | The PID FFB invariants S24/S25 cover at the SDK boundary actually deliver to a real DI consumer. |
| `S27_Xbox360_Dpad_XInput`         | xbox-360-wired d-pad through the XUSB companion (`XInputGetState`) | Closes #19: `wButtons.DPAD_*` matches the expected mask for each `HMHat` direction. |
| `S28_Hat_Resolution_Encoder`      | Pure encoder unit-test across hat resolutions 8 / 16 / 360 | v1.3.4 hat-input priority chain: each of `HMHat` / `HatRaw` / `HatHundredths` / `HatDegrees` produces the correct descriptor field value. |
| `S29_Sony_BT_Report31_Encoder`    | Encoder unit-test, `dualsense-bt-full` report 0x31 | The extended input encoder's byte layout for the 78-byte Sony BT vendor blob. |
| `S30_Sony_BT_Output_RoundTrip`    | Sony BT output codec, encode then decode | The output codec round-trips cleanly instead of drifting a field. |
| `S31_Sony_Data_Driven_Coverage`   | DS4 BT and USB output codec | Every shipped Sony output block round-trips, not just the DS5 ones. |
| `S32_V135_Features_Check`         | The v1.3.5 fix set, no device | Each v1.3.5 invariant (vendor blob, versionNumber gate, output encoder) still holds. |
| `S33_Trigger_Classifier_Check`    | Trigger classifier, no device | `triggerMode` classification stays correct across every profile shape. |
| `S34_Trigger_Live_Check`          | Trigger wire fidelity end to end | What a consumer reads back matches what was submitted, through the real stack. |
| `S35_Sidewinder_Ffb_Check`        | SideWinder PID FFB end to end | The PID FFB path on a classic Microsoft force-feedback stick. |
| `S36_Axis_Addressing_Check`       | `HMAxis` / `ExtraAxes` / `AvailableAxes` / `AddAxis` | Arbitrary-axis addressing keeps its contract across the dictionary lanes. |
| `S37_Layout_Audit_Check`          | Layout schema against descriptor, no device | A profile's declared layout and its HID descriptor cannot disagree. |
| `S38_Sony_BT_Axis_Role_Check`     | Sony BT axis roles | Stick and trigger roles do not swap or mis-resolve on the BT blob. |
| `S39_Foreign_Devnode_Survival`    | A foreign root devnode present during a sweep | The ownership guard: our sweep never mutates a devnode we do not own (issue #28). |
| `S40_Xbox_Combined_Trigger`       | Xbox 360 split-trigger synthesis | The combined-Z workaround still writes both the DI view and Vx/Vy (PadForge#130). |
| `S41_Xbox_Gip_Trigger_Resolver`   | GIP-side trigger resolver | The GIP buffer's trigger resolution matches the HID side. |
| `S42_Usb_Composite_Schema`        | Composite persona schema, no device | Each composite persona matches its hardware dump, and none leaks into the UMDF2 path. |
| `S43_Usbip_Server_Protocol`       | USB/IP wire contract, no device | Descriptors, HID in both directions, isochronous pacing, unlink handling, and stream state both with SET_INTERFACE and with only the audio traffic to go on. |
| `S44_Usbip_Bundle_Deploy`         | The bundled transport | It is present, its hash matches the pinned SHA-256, and the deploy path refuses tampered bytes. |
| `S45_Usbip_E2E_Composite`         | A composite persona through the real USB stack | Enumeration, HID, and the audio endpoints end to end. |
| `S46_Sony_Feature_Gate`           | Sony feature reports and the VID gate | Calibration never goes degenerate on the driver lane and is the identity for 8192 per g and 16 per degree/second. Bluetooth personas end their calibration, pairing and firmware reports in a valid CRC, the DS4's Bluetooth calibration is in its own field order, and the VID gate holds (issue #64). |
| `S47_Switch2_Pro_Profile`         | Switch 2 Pro profile against its references | The reconstructed profile has not drifted from the two sources it came from. |
| `S48_Switch2_Pro_Sdl3`            | Stock SDL3 reading a Switch 2 Pro | SDL still sees it as a full gamepad, reaching the joystick layer and the mapping. |
| `S49_Sony_Extra_Buttons`          | Sony extra-button wire placement | Mic mute (0x04) and the Edge paddles and Fn buttons (0x10-0x80) stay where they belong. |
| `S50_Vr_Controller_Smoke`         | The OpenVR controller subsystem, live SteamVR | IPC protocol parity between the C# and C++ mirrors, driver registration, enumeration, hand roles, haptics. |
| `S51_Valve_Personas`              | Valve persona descriptors, no device | Descriptor sets, endpoints, declared report ids and the feature stubs Steam interrogates. |
| `S52_Valve_Wire`                  | Valve personas emitting input | Each persona still puts correct frames on the wire, reports at its declared idle cadence, and carries only a raw consumer's own frames. |
| `S53_Valve_Sdl`                   | Stock SDL reading a Valve persona | Enumeration, the Valve driver binding, sticks, triggers and buttons. |
| `S54_Valve_Steam`                 | The Steam client claiming a Valve persona | Steam claims it, classifies it as the right model, and keeps it. |
| `S55_Valve_Multi`                 | Three Valve models at once, and two of one | Personas coexist, and the per-instance identity holds. |
| `S56_Valve_Raw_Path`              | The Triton raw path | Frames land on the declared report id instead of falling back to the lizard-mode layout. |
| `S57_Xusb_Wgi_Single`             | One XUSB pad through WGI | The tripwire admits exactly one WGI Gamepad for a single XUSB controller. |
| `S58_Identity_Derivation`         | Identity key derivation, no device (issue #60) | The default key reproduces the index-shaped ids, a consumer key derives deterministic collision-free ids, and persona serials derive as documented. |
| `S59_Identity_Battery`            | One controller per family across nine lives (issue #60) | Parent id, ParentIdPrefix, ContainerId, HID children, interface paths, DirectInput GUID, SDL3 path and USB serial stay identical across every life. Also empty shells, two pads of one VID/PID overlapping, and a profile change at one key. |
| `S60_Xusb_Battery`                | The XUSB battery reply on xbox-360-wired (issue #61) | The four bytes position by position, the LED reply's own version word, what `XInputGetBatteryInformation` hands a caller, and SDL's power-state mapping over those values. |
| `S61_Driver_Catalog`              | Driver catalogs for both architectures, no device (issue #63) | The embedded Inf2Cat catalogs the x64 and the ARM64 INFs with the `/os:` value `GenerateCatalogs` picks for each, one catalog per INF. The v1.9.0 value `10_ARM64` is rejected, and each architecture's INFs are refused under the other's value. |
| `S62_Valve_Firmware`              | steam-controller-2 against Steam's firmware updater (issue #62) | Steam's own `hardwareupdater.exe` enumerates the persona with the build its config names and does not offer it an update. The same persona answering with the v1.9.0 capture is offered one, in the same run. Skips without a Steam install. |
| `S63_Ds4_Report`                  | DualShock 4 reports against SDL and Linux (issue #64) | All five DS4 maps put every field where SDL's `PS4StatePacket_t` and Linux's `dualshock4_input_report_common` read it, with the Bluetooth flag and CRC, battery and cable bits, the touch report and an advancing sensor timestamp. The live Bluetooth persona sends report 0x11 once armed, and stock SDL3 keeps reading it after arming the persona itself, with 1 g and no rotation at rest and a near-zero trigger read as released. Skips the SDL part without the stock SDL3 build. |
| `S64_Ds3_Sixaxis`                 | The DualShock 3 with pressure-sensitive buttons (PadForge discussion 476) | `dualshock-3-full` presents the form sixaxis.sys and DsHidMini's SXS mode present. Its native report matches the offsets SDL, Linux and RPCS3 read, including the twelve pressure bytes and the motion RPCS3 derives from a DS4. The live persona's joystick report and report 0 feature reply match DsHidMini's conversions, RPCS3's read sequence and output reports work, and stock SDL3 with PCSX2's sixaxis hint opens a PS3 controller with 16 axes and 11 buttons and reads every pressure axis. The native-descriptor `dualshock-3` decodes the output report 0x01 a host writes through `WriteFile` and `HidD_SetOutputReport` onto `OutputDecoded` (issue #65). Skips the SDL part without the stock SDL3 build. |
| `S65_Switch2_Composite`           | The Switch 2 Pro Controller with motion (issue #66) | `switch2-pro-controller-composite` against a link-layer capture of a console driving a real pad: both descriptors, the Microsoft OS 1.0 requests, silence until command 03/0D, the command table, the flash image, one report every 4 ms, reports 0x09 and 0x05 and the motion gate, with the probe in the driver's place. The live persona comes up with HidUsb on interface 0 and WinUSB on interface 1 from its own descriptors, answers over WinUSB and decodes rumble. SDL3 with libusb opens it as a Nintendo Switch Pro Controller and reads 1 g and 100 deg/s back within 1 percent on every axis. Two personas each answer their own handle. Steam adds it and keeps reading its 250 reports a second. The SDL parts need the sibling `SDL3-build/build-unfiltered` build, which is PadForge's SDL fork compiled with libusb and with its HIDMaestro device filter off, and the Steam part needs a Steam install. Each skips without its dependency. |
| `S66_Registry_Ownership`          | Registry keys a virtual shares with real devices (foundation-sunshine #1138) | A real pad's joy.cpl OEM key (OEMName, OEMData, an Axes subkey) under an identity a virtual uses, OEM keys of pads the run never creates under the vendor prefixes the cleanup once deleted wholesale in HKLM and HKCU, a vendor's GameInput mapping, and a control key outside those prefixes all read back as planted after virtuals of the same identities are created and disposed, `RemoveAllVirtualControllers` runs and a fresh context starts. A GameInput table written the way v1.11.0 wrote it is removed. A pad built in code holds its table while it lives, shares it with a second pad and drops it with the last one. `HMOemNameOverride` claimed before or after a live virtual and released in either order leaves the original `OEMName`. A consumer killed with its virtual alive has its writes undone by the next consumer's first create. |
| `S67_Wgi_Mapping`                 | What WGI reads from a virtual pad (foundation-sunshine #1056) | With the XUSB Xbox 360 pad as the same-window positive control: a DualSense stays a RawGameController with no GameInput key, as a real one does. A DualShock 4 v1 reads correctly at rest and on every control through the mapping Windows ships for it. A game pad built in code with the one shape the table fits holds the table while it lives, reads correctly through it and drops it on dispose. A joystick built in code gets neither. |
| `S68_Vr_Driver_Conflict`          | A foreign OpenVR driver under HIDMaestro's driver name | `HMVR.FindDriverNameConflicts` lists another driver folder registered under `hidmaestro` in the user's OpenVR path registry or SteamVR's drivers folder, and never HIDMaestro's own. The probe points `VR_PATHREG_OVERRIDE` at a registry file of its own, so the real one is never read or written. Needs neither SteamVR nor elevation. |
| `S69_Kbm_Descriptors`             | Keyboard and mouse descriptors and packers, no device (issue #67) | `HidDescriptorBuilder` recipes that worked before v1.12.0 build the same bytes, pinned against what v1.11.0 built. The Mouse, Keyboard, KeyboardNkro, ConsumerControl and VendorButtons starters and the guards on combining them. The packers against bytes worked out by hand from each descriptor, relative movement split across reports, ErrorRollOver, extra buttons in a second collection and LED decoding. Every catalog keyboard and mouse parses, packs, reads back and fits the 80-byte extended lane. |
| `S70_Kbm_Raw_Input`               | Keyboards and mice through Raw Input (issue #67) | Four mice with one VID and PID are four Raw Input devices with four paths, and each identity key gets its own path back when the four are created again in reverse order. A 600-report burst and four threads at 250 Hz lose no counts. An absolute mouse arrives flagged `MOUSE_MOVE_ABSOLUTE`, and buttons six and eight arrive through the vendor collection. Two keyboards, one without report IDs and one with Report ID 2, each hold the LED state Windows sent on arrival, and a Scroll Lock pressed on one reaches both as the new state, then is restored. A consumer usage arrives as a HID report. An output or feature report written through `HidD_SetFeature`, `HidD_SetOutputReport` and `WriteFile` reaches `OutputReceived` split at its first byte, with and without report IDs. Every catalog keyboard and mouse starts as its kind and acknowledges delivery. `Dispose` with a key held sends it up first, and a consumer killed while holding F24, mouse button 6 and an absolute pointer leaves both released and the pointer where it was. The cleanup after it removes all three of that consumer's devices, the keyboard included, which Windows will not disable. Moves the cursor and puts it back. |

## What "PASS" means

Per scenario:

1. Snapshot every `HIDMAESTRO*` / `VID_045E&PID_028E*` PnP devnode that
   `CM_Locate_DevNodeW(NORMAL)` reports as `PRESENT` BEFORE the scenario
   runs (the baseline).
2. Run the scenario, then call `Wait-CascadeSettle`: wait for the devnode
   set to change, then for two consecutive 200 ms samples to agree (about
   600 ms on a fast box, 60 s scaled cap).
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
  descriptor (2x16-bit sticks + 2x16-bit triggers + hat + 11 buttons)
  built via `HidDescriptorBuilder`, wrapped in an `HMProfile` via
  `HMProfileBuilder`, written to disk by `HIDMaestroTest
  make-custom-profile <out-dir>`, and loaded by the test app via
  `emulate --profile-dir <dir>`. Mirrors PadForge's
  `HMaestroProfileCatalog.BuildCustomProfile` exactly.

Add more profile slots in `$Profiles` as new families warrant testing.
