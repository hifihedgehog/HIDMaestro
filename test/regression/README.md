# regression_battery.ps1: the regression battery

Runs 100 scenarios and verifies every one leaves zero HIDMaestro PnP
devnodes in the `PRESENT` state. S01 to S23 drive `HIDMaestroTest.exe`
through single- and multi-controller create/swap/remove/force-kill
sequences, and S24 and up run the probe programs under `test/probes`.
The sequences catch the symptom that v1.1.31 fixed
(`SwDeviceLifetimeParentPresent` resurrection after `DIF_REMOVE`) and
any future regression in the same area.

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
./test/regression/regression_battery.ps1

# specific scenario only (wildcard match on scenario name)
./test/regression/regression_battery.ps1 -Filter 'S08*'

# verbose: prints every stdin command sent to the test app
./test/regression/regression_battery.ps1 -Verbose

# point at a non-default exe (e.g. a published build)
./test/regression/regression_battery.ps1 -Exe C:\path\to\HIDMaestroTest.exe

# stop at the first failure, as the release gate does
./test/regression/regression_battery.ps1 -StopOnFailure
```

Exit code: `0` if every scenario passed, `1` if any failed.

`scripts\pre-tag-validate.cmd` runs the battery with `-StopOnFailure`: the
first scenario that fails ends the run, with its transcript, and the
rest do not run.

## Sharing the machine

The battery runs beside whatever else the machine is doing. Load,
Steam, browsers and other controllers never stop a scenario, and the
probes find their own devices by their own identity, never by VID and PID
alone or by a fixed transport index.

Another program that embeds HIDMaestro is different: it changes the
machine-wide state the scenarios measure (devnodes, sweeps, XInput slots,
Windows.Gaming.Input lists, the registry ledger). Before each scenario
the battery waits for every such program to exit, printing
`waiting: <program> (<pid>) embeds HIDMaestro and is running` once a
minute. A scenario during which one appeared prints `[RERUN]` instead of
a verdict and runs again once it has gone. `HMForeign.cs` finds them: a
process outside the battery's own process tree with `HIDMaestro.Core.dll`
loaded. A process the battery started stays its own after its parent
exits, so one a crashed probe leaves running fails the scenario that left
it and is stopped, instead of holding the battery back.

Every step ends at a deadline: a probe's run, the test app's answer to a
command, and the test app's exit after quit get 3600 s, S71 6300 s and S73
2700 s from its start, all scaled by `HIDMAESTRO_TIMEOUT_SCALE`. A step
still running then fails its scenario as a hang, with the probe's
transcript, and its process tree is stopped.

Total wall time: 26 minutes for all 100
scenarios, every section and check, on the development laptop, a 16-core
Lenovo Legion Pro 7, and 38 minutes on a 4-core Celeron J4125 at
`HIDMAESTRO_TIMEOUT_SCALE=2`. Use `-Filter` to run one scenario when
iterating.

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
| `S17_ForceKill_MidCascade`        | Hard-kill as a Series BT teardown starts: the first devnode change after the swap command | Worst-case force-kill timing: phantoms left in mid-cascade state. |
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
| `S37_Layout_Audit_Check`          | Layout schema against descriptor, no device | A profile's declared layout and its HID descriptor cannot disagree. The presentation plan of every profile (enumerator, hardware and compatible IDs, companion) changes only as `presentation-plan-changes.txt` lists. Then, with no device, every catalog report descriptor's input, output and feature lengths against hid-tools', the report and length the driver is given for each profile, and hand-made descriptors for Push and Pop, long items and the other items a walker can get wrong, and every catalog descriptor through the machine's own hidparse.sys, which HID class runs at a device's start, where only gamecube-adapter and gamecube-adapter-hid are refused, as the real adapter is, for collections with Usage 0. Last, live: genesis-nso arrives as report 0x3F and never 0x21, microsoft-designer-keyboard's raw frame as its keyboard report 0x01, beside dualshock-4-v2's unchanged 0x01, then logitech-wingman-formula-gp's 5-byte report, then ps-move's and xbox-360-flight-stick's collections, report lengths and trigger bytes against their captured descriptors. |
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
| `S57_Xusb_Wgi_Single`             | One XUSB pad through WGI | The tripwire admits exactly one WGI Gamepad for a single XUSB controller. In thirty quick create-and-dispose cycles under one identity key, no XInput slot reads connected again after Dispose returns. |
| `S58_Identity_Derivation`         | Identity key derivation, no device (issue #60) | The default key reproduces the index-shaped ids, a consumer key derives deterministic collision-free ids, and persona serials derive as documented. The 0xCA rival scan on recorded device trees: a parent on the identity's ParentIdPrefix, or with its token, under any ROOT enumerator HIDMaestro creates parents under (`HIDClass`, every `VID_xxxx&PID_yyyy&IG_00`) is removed, refused when its owner runs, and left alone with the create refused when HIDMaestro does not own it. Every enumerator a catalog profile's plan makes a parent under is walked by the full cleanup's Device Parameters and HID orphan phases. A ROOT parent whose identity's child path holds a record left without its driver takes the next prefix of its identity whose path holds none, `1&hash&1` to `1&hash&3`, and keeps its own when all four paths hold one. No prefix or alternate of 16 indexes and 20,000 keys belongs to two tokens, and once a parent's record is gone the sweep owns each index's and each recorded token's alternates up to the third. |
| `S59_Identity_Battery`            | One controller per family across five lives (issue #60) | Parent id, ParentIdPrefix, ContainerId, HID children, interface paths, DirectInput GUID, SDL3 path and USB serial stay identical across every life. Also empty shells, two pads of one VID/PID overlapping, and a profile change at one key. |
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
| `S71_Coexistence`                 | Programs that embed HIDMaestro, side by side (coexist_check sections A to H and J to Z) | Two keyless programs get their own transport indices and identities, and each device carries only its own program's input. A create of an identity key a live program holds throws `HMResourceInUseException` and leaves the holder's devices untouched. A second program's `InstallDriver` and create leave the first program's devnodes, XInput slot, WGI and Raw Input entries and frame gaps as they were. A killed program's devices go at the next sweep while a live program's stay. Peers built on v1.12.0 and v1.11.0 run beside this release through each branch of the install policy. Also: two `InstallDriver` calls at once on a machine without the driver, two programs' USB/IP composites, records a killed program left, sweeps and the exit handler beside running programs and a create in flight, a running v1.12.0 program's later creates, deploys against other programs' creates and the public `DriverBuilder` steps, machine-wide names a program that is not elevated creates first, the driver's StopEvent and OutputEvent, and transport and WinUSB work under their locks. Each program is a peer process, and the older releases are built against their own HIDMaestro.Core.dll. |
| `S72_Protected_Staging`           | Staging of everything the SDK runs elevated (coexist_check section I) | The driver payload and `hmswd.exe` stage under `%ProgramFiles%\HIDMaestro\Staging` in directories only Administrators and SYSTEM can write, and nothing is left in `%TEMP%`. A `%ProgramFiles%\HIDMaestro` with a wrong ACL is reset in place when Administrators or SYSTEM own it, and refused when another program does. A staged tool overwritten with other bytes of the same length is restored from the embedded copy, a directory replaced by a link is moved aside, and a deploy prunes the staging of versions no running program uses and never what another build's program reaches while the prune runs. |
| `S73_Usbip_Clean_Install`         | usbip-win2's host controller installed, used and removed with no vendor installer and no root-hub filter, in Windows Sandbox started without a window | The first sandbox: a composite create installs the host controller from the package the SDK carries. The result is one `ROOT\USBIP_WIN2\UDE` devnode with HIDMaestro's stamp, the package in the driver store, and an install record naming the devnode, its owner token and the package's DriverVer. No usbip2_filter package or service, Apps entry, program folder, task, shortcut or KMDF debug value is left. A planted usbip-win2 uninstall entry is left as it was, and its uninstaller never runs. When two programs create their first composites at the same moment, the host controller is installed once. S45's and S65's probes then run over that install with no filter on the root hub. S45's microphone energy check is a skip there, since the sandbox's audio engine silences every stream. The second sandbox runs the usbip-win2 part of a full removal. When a driver install binds after its timeout, the next create records the package, unless the record names another DriverVer (another date or another version) or usbip-win2's Apps entry changed since. When another release's package is forced onto HIDMaestro's devnode, neither the next create nor the record update after a move records it, and a default removal keeps the devnode and the package. That package is usbip-win2 0.9.8.0, which the probe's build fetches and checks against `ForeignUsbipPackage.json`. An install cut short before its driver install leaves a driverless devnode, which a default removal takes with its record. Without a record, or with one a program on another pin wrote, the next install finishes the same devnode, and the record then carries this release's DriverVer. When the record is lost and the next install writes it again beside a user's identical package, a default removal keeps that package. R1 to R4: a preview changes nothing, a removal the record proves takes everything, and a reinstall works. An install no record proves is kept, taken when usbip-win2 is included, and refused while another program that uses usbip-win2 is installed. A host controller used since Windows started is kept until a restart, with its persona's records. A held file or folder under usbip-win2's folders, or its held link on the Public desktop, fails the removal with nothing queued for the next start, and the next removal takes it. The host part never loads HIDMaestro.Core.dll, so the probes that run beside it do not skip. Skips where Windows Sandbox is not available. The battery starts it before S01 and collects its result at its slot, since the sandbox is a machine of its own and sits idle through most of the minute its two sandboxes take. |
| `S74_NativeAot_Smoke`             | HIDMaestro.Core in a NativeAOT app | The whole catalog loads, the driver installs, `dualshock-4-v2` on the UMDF2 driver and `dualsense-composite` on USB/IP each deliver the submitted state to a HID read and raise `OutputReceived` for an output report, and both go away on dispose. Delivery is judged on content, against a neutral window. |
| `S75_SingleFile_Smoke`            | HIDMaestro.Core in a trimmed, self-contained, single-file app | The S74 checks in the other kind of app. |
| `S76_Json_Parity`                 | Source-generated JSON, no device | Every embedded profile reads into the same objects v1.12.0's reflection-based reading built. Also fails when an enum the JSON contract reaches has no converter, or a model type has no generated metadata. |
| `S77_Pnp_Notify`                  | PnP notifications reach the SDK's create and dispose waits | First, with no device, feeds the removal test and the registration gate recorded sequences through the SDK's hooks, and fails when either answers before the DEVICEINSTANCEREMOVED notification and the PHANTOM record's removal, or a locate cancels a removal. It also checks the PnP lock's bounds during the exit and that no devnode change is made outside the lock except the listed ones. Then counts the events the SDK's waits receive, inside its own callback and by filter kind, while a plain HID controller is created and disposed, and requires every removal of a DualShock 4, an Xbox 360 wired and an Xbox Series BT dispose to end with its devnode gone, and each devnode in the tree when its removal test started to leave on DEVICEINSTANCEREMOVED. A HID child removed with its parent counts on the parent's notification, and a registration held open through each dispose fails the run when a child's own notification comes after the removal let the PnP lock go. |
| `S78_Build_Info`                  | What a build of the SDK carries | The release build carries the driver and the USB/IP transport for x64 and ARM64 and the OpenVR driver, its four INFs carry one DriverVer stamp, and its Inf2Cat set holds the six files Inf2Cat loads. A variant built with `HMSkipArm64` and `HMSkipUsbip` embeds nothing stale, refuses what it lacks before any machine-wide step, and still runs a UMDF2 controller on x64. |
| `S79_GameCube_Descriptors`        | The Wii U GameCube adapter persona's bytes | Every catalog profile compiles as a device model, and copies of the persona with one mistake each are refused before anything machine-wide with a message naming the profile, the JSON path and the limit. The three 057E:0337 profiles carry the corrected descriptors and one Full. The persona's descriptors are a real WUP-028's as it serves them, as Windows enumerates them before any driver loads, and as libusb reads them through WinUSB. |
| `S80_GameCube_Protocol`           | The adapter's protocol through WinUSB | SET_PROTOCOL, no input before 0x13, each port's input, connection and rumble apart from the others, the origins, stalls and ignored commands, SET_REPORT as one command, GET_REPORT, 0x15 and the port reset, and the 8 ms and 1 ms report rates. Also runs the generic port and protocol model in process on exact clock ticks. |
| `S81_GameCube_WinUsb`             | `BindWinUsb` on one virtual adapter and no real one | The package ranks below input.inf, a second adapter stays on input.inf, and the bound devnode carries no reinstall flag. Dispose, and the reclaim after a killed program, flag the instance once its devnode is gone, so the next adapter on it installs by rank. An adapter arrives before the reclaim and after it. |
| `S82_GameCube_Dolphin`            | Dolphin and Slippi | Their own adapter code, compiled unmodified with the libusb each pins, reads all four ports, sends rumble to each wired port alone and none to a WaveBird, and sees a port leave and return. Measures submit-to-read latency and the delivered report rate at 8 ms and 1 ms. |
| `S83_GameCube_Sdl`                | SDL | The official SDL3 3.4.18 lists nothing for the adapter beside a DualShock 4 it lists. SDL 3.4.18 built with libusb lists one gamepad per port with SDL's mapping, refuses rumble on a WaveBird and without rumble power, and sends each port's rumble to that port. Measures latency and the report rate as S82 does. |
| `S84_Perf_Budget`                 | No submit path allocates | `SubmitState` and `SubmitRawReport` on a DualShock 4, `SubmitState` on an Xbox 360, `SubmitKeyboard`, `SubmitMouse`, and a GameCube adapter port's `SubmitState` and the controller's, each called thousands of times on a live controller. A USB/IP device's frames on its three send paths, a DualSense composite's output and audio transfers, and the control requests a host sends a DualSense and a Switch 2 Pro composite, answered or stalled, allocate nothing in the median run. With an Xbox 360 up, the census finds what the configuration manager lists. |
| `S85_Handler_Isolation`           | An exception an event handler throws stops nothing else | A throwing handler ahead of a counting one on `OutputReceived`, `OutputDecoded` and `KeyboardLedsChanged`: the counting handler gets every event, the keyboard's LED state follows Scroll Lock, and a Bluetooth DualSense still switches to report 0x31. A handler still running when Dispose's join gives up meets nothing Dispose released, and a handler that disposes its own controller does not wait for its own thread. Offline, a `DisposeControllersInParallel` callback that throws for each of 256 controllers, or for a set of one, still has every controller disposed and comes out as one `AggregateException`. |
| `S87_Interop_Decl`                | Interop declarations and the method names in the SDK's text | No `DllImport` is left, `SetupDiGetClassDevsW` takes its Enumerator as a pointer, and every method a message or XML doc names exists as written. 33 string-form fixtures check the argument counter. The live run reads each XInput slot of an Xbox 360 persona through the SDK's `XInputGetState` declaration and through xinput.h's, which must agree with a slot connected, then disposes it and runs the phantom sweep, which must remove at least the record the dispose leaves. There is no S86. |
| `S88_Device_Shortcut`             | No device shortcut in the SDK or the drivers | No declared name, file name or string carries a device or vendor word from the catalog outside the probe's allowed names, no vendor or product ID is compared, switched on or assigned in any form, no device name is searched, no profile field set by one catalog profile alone is read, and no region of the 24 device regions and the default device's five is left in a driver source. It failed by design until the last device code became profile data, and passes at 2.0.0. Offline |
| `S89_Device_Oracle`               | The device code gives the bytes it gave before the conversion | Replays nine recorded cases, the submit and decode paths, the packers, the Switch 2 Pro responder, the USB/IP feature answers, the UMDF2 driver and companion compiled natively, and the physical descriptors of the reference clones, and passes a difference only when `changes.txt` lists it with its lane and reason. Offline |
| `S90_Arith_Roundtrip`             | Every raw value comes back | Every raw value of every catalog conversion, normalized three ways, returns through the SDK's own code. A NaN writes the rest, an unwritten stick rests at its center, and the rounding matches `Math.Round`. Offline. Its `diff` mode holds every float in [0, 1] to the design note's table, in about 10 seconds |
| `S91_Vendor_Blob_Golden`          | Vendor-blob reports against golden bytes | Every Sony, Valve and Nintendo vendor-blob report encodes and decodes to its golden SHA-256, and the codec vocabulary refuses the removed `ds3-battery` type, a missing `max` or `invert`, and parameters an output field ignores. Offline |
| `S92_Profile_Model`               | Profiles and device models as data, no device | The catalog and a user's directory load strictly: every defect a file can carry is refused at its JSON path and listed in `HMContext.ProfileLoadIssues`, a create and a build meet `HMProfileException` for a bad profile, and the catalog files of every earlier v1.x tag load but for the removed spellings. Answer tables, value sources and patches, model resolution by name and by identity (no catalog profile takes a group from an identity default), the SDK's half of the version contract, the 2.0.0 deploy floor and its policy, the start wait's hold of a failed start while Windows has a device installation pending, and `FromProfile`'s whole copy. Offline |
| `S93_Contract_Live`               | The version contract on the live driver | A fresh-token controller's driver reads its index from the identity entry before its first start, on the ROOT path and the SWD xinputhid path, a device without report IDs answers its message after HID class's leading 0, and each catalog profile with a model, read back by `HMDeviceExtractor`, names the model its identity gives it, and created on the same index answers each feature report as the device did. ps3-remote, tinyusb-generic-gamepad and nacon-bigben-ps4 start, and a create whose descriptor Windows' parser refuses fails with problem code 10 and removes the device it made. Live, elevated |
| `S94_Program_Equivalence`         | Every report program three ways | Each catalog program, decoder and layout set through its generated kernel, the interpreter and a naive evaluator written from the design note, on random states and recorded inputs, a seeded corpus of 1,000 random programs, the negative corpus and planted defects, every validation rule with its JSON path, under the JIT and NativeAOT, where every catalog program must bind its generated kernel. Offline |
| `S95_Protocol_Engine`             | The protocol engine and the device clock | Conditions, matches, register stores, memory, bitmap and prefix replies, bulk replies in read-sized chunks, the overflow rules, GET_REPORT of the layout that would go out, counters, no allocation per command, reply or report, the Triton's IMU register, the clock's grid under each missed-tick rule, and the idle repeat's period, condition and timestamps. Offline |
| `S96_Capture_Fidelity`            | Catalog data and driver text against their sources | The INF comments are ASCII, the Xbox 360 profiles carry a real pad's manufacturer string, and xbox-360-flight-stick and ps-move carry their captured descriptors, with the Move's controls, battery and motion where psmoveapi and RPCS3 read them. Offline |
| `S97_Switch_Family`               | A second device on the Switch Pro's mechanisms | `joycon-l-proof`, a Joy-Con (L) built from `switch-pro`'s protocol, state block and report programs, loads, answers SDL's Joy-Con open with nxbt's device info, and its 0x3F stick hat follows dekuNukem's grid in all eight directions and reads centered inside its dead zone. Offline |
| `S98_Switch_Pro_Protocol`         | The Switch Pro's protocol, as SDL3 drives it over Bluetooth | Over raw HID under the real Bluetooth descriptor, the init SDL3's `HIDAPI_DriverSwitch` runs (device info, stick and IMU calibration, vibration, input mode) gets a real pad's answers, the 0x30 stream runs, input and IMU arrive as submitted, rumble decodes, and the USB-only 0x80 family answers nothing, as on a real Bluetooth pad. Live, elevated |
| `S99_Switch_Descriptor_Idle`      | The Switch Pro before its handshake | The idle stream is the real pad's 12-byte 0x3F simple-mode report, the one report DirectInput can parse through the Bluetooth descriptor, and the first 0x01 subcommand switches the stream to the 49-byte 0x30. Live, elevated |
| `S100_Switch_Pro_Sdl3`            | The Switch Pro through real SDL3 | SDL3's `HIDAPI_DriverSwitch`, from the unfiltered SDL3 build beside the repository, opens the virtual pad, completes init, reads calibrated sticks and buttons, and reports gyro and accel through `SDL_GetGamepadSensorData`. Prints `[SKIP]` where no SDL3 build is beside the repository. Live, elevated |
| `S101_Uninstall`                  | Removing HIDMaestro: `HIDMaestroUninstall.exe` and `HMUninstall` | Every machine: the tool's `--preview --json` and `HMUninstall.Preview` change nothing against a snapshot of devices, packages, the certificate, registry keys and files, and the preview lists every HIDMaestro package the snapshot finds. Where Windows Sandbox exists, the real removal runs inside it, started after S73 and collected at this slot: the desktop link usbip-win2's installer leaves is removed from the Public profile. With a program running that holds four controllers (a composite among them), an OEM name override and the OpenVR registration, the tool exits 2, names it and changes nothing. Once it is killed, the removal leaves no device record, package, certificate, private key, registry key or folder, puts the OEM targets back, takes the OpenVR registration, restarts no USB root hub, keeps a `%TEMP%` that is a junction and its target's file, and deletes a junction under a HIDMaestro name as a link. With the removal held at the driver deploy lock, a program that installs and one that creates from two threads at once both wait at the removal gate, hold no owner record and have installed and created nothing until the lock goes, and do not block the removal. Each install and create then returns with the removal's lock free, and both programs keep their controllers. The installing program calls back into the gate from the gate's own wait, and the call returns. A running program that only set an OEM name override, registered the OpenVR driver or installed the USB/IP transport blocks a removal, and one that asked for the OpenVR registration with no SteamVR present does not. A junction at `%ProgramData%\HIDMaestro` goes as a link with its target's file kept. A program removes HIDMaestro in its own process with its controller open, then installs and creates again. A file held open under `%ProgramFiles%\HIDMaestro` waits for the next start and one under `%ProgramData%\HIDMaestro` stays, reported failed. OEM override records older than 2.0.1, or set from another account, keep what they cannot prove. A record as 2.0.0 wrote it gets no write to this account's hive from a Set or a Clear unless that hive holds its label, and one whose label it holds is taken over by a Set and put back by a removal. With `HIDMAESTRO_UNINSTALL_FIXTURE=1` on a machine without Windows Sandbox: the real removal, a clean read of the machine, and a reinstall with one controller created and disposed. The sandbox part skips where `wsb.exe` is missing, and the fixture part where Windows Sandbox exists or the variable is not 1. Live, elevated |

## What "PASS" means

Per scenario:

1. Snapshot every HIDMaestro controller devnode that
   `CM_Locate_DevNodeW(NORMAL)` reports as `PRESENT` BEFORE the scenario
   runs (the baseline): each `ROOT` or `SWD` parent whose hardware IDs
   include `root\HIDMaestro`, `root\HIDMaestroGamepad` or
   `root\HIDMaestroXUSB`, whatever its enumerator (`ROOT\HIDClass`, any
   `ROOT\VID_xxxx&PID_yyyy&IG_00`, `SWD\HIDMAESTRO*`), and its children.
   The usbip-win2 host controllers (`ROOT\HIDMAESTRO_UDE`) are the
   transport and are left out. `HMDevnodes.ps1` holds the rule, and
   `devnode_owner_check.ps1` checks it offline (no device, no
   elevation): a `ROOT\HIDClass` parent and an `IG_00` parent other than
   045E:028E are reported with their HID children, and a foreign
   `ROOT\HIDClass` device, a phantom and the host controller are not.
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
