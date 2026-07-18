# HIDMaestro Internals

Technical reference for HIDMaestro. The [README](../README.md) covers what HIDMaestro is and how to use it. This document covers how it works: the descriptor and enumeration techniques, the device topology, the user-mode rationale, the validation methodology, and the timing characteristics.

For the per-release decision log and investigation notes, see [docs/investigations/](investigations/).

---

## Techniques

A few HIDMaestro techniques that are not well documented elsewhere in the virtual controller space.

### Velocity Usage Descriptor Trick

Real Xbox 360 controllers have a combined trigger axis (Z) in DirectInput: both triggers share one axis. Browsers and WGI need separate trigger values. Previous solutions had to choose: correct DI (5 axes, combined) or correct browser (separate triggers, 6 axes).

HIDMaestro uses HID velocity usages (Vx and Vy, Usage Page 0x01, Usages 0x40/0x41) to carry separate trigger values in the same HID report. DirectInput does not map velocity usages to any axis slot, so it sees 5 axes. GameInput/WGI enumerates them as additional axes and reads separate trigger data via the GameInput registry mapping.

Result: 5 axes and 10 buttons in DirectInput (matching real xusb22.sys), separate triggers in the browser (matching real XInput), all from one HID descriptor.

### Data-Driven Vendor-Blob Codec (Sony USB + BT)

Sony BT controllers (DualSense, DualSense Edge, DS4 BT) declare their input as a 78-byte vendor-defined "blob" — one opaque field with no descriptor-level breakdown of which bytes carry sticks vs buttons vs gyro vs CRC32. Pre-v1.3.5 the SDK couldn't pack this and fell back to emitting basic Report 1 (9 bytes), which Steam Input misclassified as USB and `dualsense-tester` couldn't parse.

v1.3.5 makes the byte layout data: profile JSON declares `extendedReport` (input) and `extendedOutputReport` (output) blocks describing every field's type, byte position, and bit range. The SDK becomes a generic codec that walks the field list. Future profiles with vendor blobs (Switch Pro extended, vendor-specific wheels) add the JSON only — no SDK code changes per profile.

The full Sony catalog ships with v1.3.5 data-driven blocks: DS5 BT (`dualsense-bt`, `dualsense-bt-full`, `dualsense-edge-bt`) gain both input + output (Report 0x31, 78-byte BT-format with `[0xA1,0x31]`/`[0xA2,0x31]` CRC32 prefixes); DS4 BT (`dualshock-4-v2-bt`) gets input + output (Report 0x11, `[0xA1,0x11]`/`[0xA2,0x11]` CRC32 prefixes); DS5 USB (`dualsense`, `dualsense-edge`) and DS4 USB (`dualshock-4-v1`, `dualshock-4-v1-full`, `dualshock-4-v2`) gain output blocks (Report 0x02 / Report 0x05; no CRC since USB is reliable). PadForge can drive any of them via `HMOutputEncoder.Encode(profile, fields)` without inline byte-packing.

```jsonc
"extendedReport": {
  "reportId": "0x31",
  "size": 78,
  "fields": [
    { "byte":  2, "type": "uint8-axis", "semantic": "leftStickX", "center": 128 },
    { "byte":  9, "bits": "0-3", "type": "hat-octant", "neutralValue": 8 },
    { "byte":  9, "bits": "4-7", "type": "button-mask", "buttons": ["X","A","B","Y"] },
    { "bytes": "74-77", "type": "crc32-le",
      "scope": { "prefix": [161, 49], "from": 1, "to": 73 } }
  ]
}
```

Round-trip in both directions: `controller.OutputDecoded` event surfaces incoming output reports as parsed-field dictionaries (rumble amplitudes, lightbar RGB, adaptive-trigger blobs); `HMOutputEncoder.Encode(profile, fields)` produces wire-format bytes from a parsed-field dictionary, used by consumers driving real devices from synthesized state without reimplementing byte layouts. `HMController.EncodeOutput(fields)` is the per-controller variant that auto-advances the rolling-counter state (Sony BT `btTag` cycles 0x00→0x10→…→0xF0→0x00 with stride 16) instead of forcing the consumer to track wraparound. `HMController.OnSubmitLatencyMicros` exposes per-frame submit latency for consumers driving timing-sensitive paths.

The encoder/decoder reaches the public input-state surface too. `HMGamepadState` ships per-frame fields the Sony JSON blocks understand: `TouchpadFinger0Active/X/Y/Id` + `TouchpadFinger1Active/X/Y/Id` + `TouchpadPacketCounter`, `GyroPitch/Yaw/Roll` + `AccelX/Y/Z` + `SensorTimestamp` (DS4 100µs ticks; DS5 microseconds), and `BatteryLevel` (0..10) + `BatteryCharging` + `BatteryFull` + `MicMuted` + `HeadphonesConnected`. Profiles that don't declare these regions silently ignore them, so the same caller code works across every controller. `dualsense-tester` (ds.daidr.me) renders touchpad coordinates, the IMU vector, and the battery panel for any DualSense or DualSense Edge virtual (USB or BT) once the consumer fills these fields.

DS4 Bluetooth vibration through the browser Gamepad API is fixed in v1.3.5 by setting the device's `HidD_GetAttributes` `VersionNumber` to 0 — the value Chromium's `DualShock4Controller::BusTypeFromVersionNumber` checks for the BT-format report header. Pre-v1.3.5 the SDK hardcoded `0x0100` (USB), Chromium picked the wrong wire layout, and the rumble bytes never reached the device. Profiles can now override `versionNumber` in JSON; `dualshock-4-v2-bt.json` ships with `0`. Steam Input never used this gate so v1.3.5 doesn't change Steam behavior either way.

### Switch Pro Protocol Responder

The Switch Pro Controller is not a passive HID device: hosts (SDL's `HIDAPI_DriverSwitch`, Steam, BetterJoy) drive a Nintendo init and subcommand protocol and stall without a device that answers. The generic report-builder cannot express request-reply, so `driver.c` carries a hardcoded responder keyed on VID 0x057E PID 0x2009 (protocol lives in code, layout in JSON, the same split as the Sony vendor-blob work).

Three pieces. USB init commands (`80 01/02/03`) get their `81 xx` replies, with device type Pro and a stable fabricated MAC. Subcommands (output 0x01) get input-report 0x21 replies per the nxbt responder table, including SPI flash reads served from a fabricated image: factory stick calibration with center 0x800 and range 0x600, and IMU coefficients (0x4000 accel, 0x343B gyro) chosen so SDL's calibration math reduces exactly to its own default scales. Unknown subcommands get a generic ACK rather than nxbt's silent ignore, because SDL retries unanswered subcommands for ~500 ms where the Switch console does not. Input report 0x30 streams from a dedicated driver thread at the wire's ~60 Hz cadence, with the driver stamping timer and battery bytes over the consumer-submitted body.

Consumers submit through the normal `SubmitState`: `SwitchProPacker` converts the layout-mapped buttons, 12-bit packed sticks, and the calibrated IMU channel (`HMGamepadState.AccelG*` in g, `GyroDps*` in deg/s) into the wire body. HD rumble comes back decoded to coarse `leftMotor`/`rightMotor` amplitudes on `OutputDecoded`, the same lane Sony rumble rides. `test/probes/switch_pro_check` replays SDL's exact init sequence over raw HID as the release gate.

### BTHLEDEVICE Bus Type Spoofing

HIDAPI detects Bluetooth controllers by checking for `BTHLEDEVICE` in the device's CompatibleIDs. HIDMaestro sets this property from user mode during device creation, without Bluetooth hardware and without a kernel bus driver.

SDL3 then uses its Bluetooth-specific controller parsing path, which handles the descriptor correctly. Without this spoof, SDL3's default parser produces zeros for certain virtual device configurations.

### &IG_ Enumerator Trick

By using `VID_*&PID_*&IG_00` as the device enumerator, the HID child's device path contains `&IG_`. This has three simultaneous effects:

- **Chrome RawInput** skips it (prevents duplicate gamepad entries)
- **HIDAPI** skips it (by design for XInput-handled devices)
- **SDL3** still detects it (falls through to RawInput backend, maps by VID/PID)

One string in a device path controls three different detection paths across three different libraries.

### GameInput Registry Override

Windows has a built-in GameInput mapping database for known VID/PIDs. HIDMaestro writes custom mappings that point the trigger axes to the velocity usage indices (5 and 6 instead of the default combined axis 4). This makes WGI's Gamepad object read actual separate trigger values from the Vx/Vy fields.

### xinputhid UpperFilter Tripwire

WGI (`Windows.Gaming.Input.dll`) admits devices into its provider graph through `ProviderManagerWorker::OnPnpDeviceAdded`. A Ghidra decomp of that function on Win11 26200 showed the gate: WGI accepts a device only if its ClassGuid is in a hard-coded four-entry pass-list (`HIDClass`, `XnaComposite`, one other setup class, one GameInput class) OR if `IsDeviceOrAncestorFilteredBy(path, L"xinputhid")` returns true. The fallback check is a literal `wcsncmp` against strings in the device's (or any ancestor's) `UpperFilters` MULTI_SZ.

HIDMaestro's XUSB companion (`SWD\HIDMAESTRO\<sid>_NNNN`) runs under the System class `{4d36e97d-...}`. That class is not on the pass-list, so before this work WGI silently skipped the companion despite it publishing the XUSB device interface — Chromium's `put_Vibration` went nowhere for Xbox 360 Wired.

The fix writes the string `"xinputhid"` to the companion's `UpperFilters` registry value via the INF's `HKR` AddReg. `xinputhid.sys` is a HID-class filter, so it never actually attaches to the System-class companion; the string sits inert in the registry and WGI's wstring compare passes anyway. The companion enters WGI via the XUSB dispatch path, and `IOCTL_XUSB_SET_STATE` starts reaching the driver with real motor bytes on `put_Vibration`.

The same string gets written per-instance to the HID parent by `DeviceOrchestrator` for XUSB-companion profiles only. That second write blocks WGI's `HidClient::CreateProvider` from synthesizing a duplicate HID-backed Gamepad for the same logical controller, so WGI shows exactly one Gamepad with live input and working vibration instead of two pads splitting the responsibilities.

The 29-byte `IOCTL_XUSB_WAIT_FOR_INPUT` reply format was nailed down in the same decomp pass: `state[9] = 0x00` so `XusbInputParser`'s built-in Gamepad template matches (a prior 0x14 value produced an all-zero `GetCurrentReading` despite input arriving), plus the `state[10] = 0x14` non-zero gate byte, `state[2] = 0x03 RESUMED` on every completion, and version bytes `0x01 0x03` at `state[0..1]`.

### SWD Migration: the XInput slot-1-skip fix

Pre-fix, HIDMaestro created its devnodes via `SetupDiCreateDeviceInfoW` under `ROOT\` — the standard root-enumerated path. Windows assigns the null-sentinel ContainerID `{00000000-0000-0000-FFFF-FFFFFFFFFFFF}` to ROOT-enumerated devices unless overridden, and the SetupAPI path provides no way to override it.

Ghidra decomp of `xinput1_4.dll` on Win11 26200 traced the consequence. `FUN_18000de2c` returns 1 when ContainerID matches the null sentinel OR when HardwareIds contains the literal `XINPUT_EMBEDDED_DEVICE` substring. Caller `FUN_18000c728` at `0x18000C8AE` does `test al, al; jne → or dword ptr [rbx], 4`, setting bit 2 on the device struct. `FUN_18000f85c`'s fallback allocator at `0x18000F9C3-C7` skips internal slot 0 for bit-2 devices when Feature Manager flag `0x39EB83D` is on; `FUN_18000f178` then promotes the first bit-2 slot to "primary" and the query-time swap at `FUN_18000f08c` surfaces an empty slot 1 to consumers.

The fix is a one-line API switch: use `SwDeviceCreate(pContainerId = real-per-controller-GUID, ...)` instead of `SetupDiCreateDeviceInfoW`. The SwDevice API takes an explicit container GUID; we pass `{48494430-4D41-4553-5452-4F000000<idx:X4>}` (ASCII "HIDMAESTRO" + 16-bit controller index) so each virtual gets a deterministic non-sentinel container shared by its main + companion devnodes. `de2c` returns 0, bit 2 stays clear, slot allocator fills 0..3 contiguously.

The xinputhid-path profiles (Xbox Series BT etc.) moved fully to `SWD\HIDMAESTRO_VID_<vid>_PID_<pid>&IG_00\` because the SwDevice path is the only reliable way to inject a real ContainerID. The non-xinputhid Xbox path keeps its main HID device on `ROOT\VID_*&PID_*&IG_00\` (existing INF binding) and moves only the XUSB companion to `SWD\HIDMAESTRO\`. Both companion paths share the same per-controller container GUID with the main HID, which is what xinput1_4 dedups against.

The underscore between VID and PID in the gamepad-companion enumerator (`HIDMAESTRO_VID_045E_PID_0B13&IG_00`, not `...VID_045E&PID_0B13...`) avoids a Windows PnP edge case in which any SWD enumerator name matching the substring `VID_*&PID_*&IG_*` registers in the registry but never enumerates as a live devnode. The `&IG_00` suffix is preserved because the HID child inherits its parent's enumerator name as the first segment of its instance path, and HIDAPI/SDL3/Chromium all blocklist `&IG_` substrings to avoid duplicating XInput-claimed devices.

### Session-Unique Instance-ID Suffix

The SWD migration immediately exposed a second Windows PnP behavior: after `SwDeviceClose` finalizes a devnode with `SWDeviceLifetimeParentPresent`, the kernel retains a sticky per-`(enumerator + instanceId + ContainerId)` record. A subsequent `SwDeviceCreate` with the identical tuple takes a "reuse-existing" fast path that creates an empty registry shell — no Service or Driver bound, no device-interface class registered — and reports success to the caller. The sticky state survives across processes and across same-boot uninstall + reinstall of the INF.

Symptoms before the fix: first run after a fresh boot was fast and all APIs passed, but every subsequent run on the same boot saw `SwDeviceCreate` return `S_OK` synchronously while the devnode never materialized. `CM_Locate_DevNodeW` returned `CR_NO_SUCH_DEVNODE` the entire time the SDK waited; the creation callback timed out at 30s with `E_FAIL`. Phase-1 creation ballooned from ~2s to 65s (15s callback wait × 2 BT slots + 15s XInput slot-claim wait × 2 Xbox 360 slots), and XInput lost visibility for the XUSB-companion path because the empty-shell devnode never bound `HMXInput.dll` and so never registered the XUSB device-interface class.

Fix: prepend the current process's PID in hex to every SwD instance-id suffix, e.g. `SWD\HIDMAESTRO\A7B4_0002`. Each launch gets a unique tuple, the kernel runs a fresh full install, and the devnode binds correctly. `FindExistingCompanion` matches by `Device Parameters\ControllerIndex` (not by suffix) so cleanup and teardown sweep across instances regardless of which session created them. Verified on this machine: 5 back-to-back same-boot 4-controller runs all complete Phase 1 in 2.2-2.8s with `verify.py` ALL PASS and zero registry-carcass accumulation post-teardown.

## Architecture

```
User-Mode Test App
  │ Writes input data to per-controller shared memory section
  │ Manages device lifecycle (create, configure, remove)
  │
  ├──► Shared Memory (per-controller, pagefile-backed)
  │     SeqNo(4) + DataSize(4) + Data[256] + GipData[14] = 278 bytes
  │     Data[256] carries HID input reports up to 256 bytes (DualSense BT
  │       report 0x31 = 78 bytes, Switch Pro motion-IMU reports, etc.).
  │     Event-driven: SDK signals InputDataEvent on each write.
  │
  ├──► Main HID Device (HIDMaestro.dll via mshidumdf)
  │     Xbox 360 Wired:    ROOT\VID_045E&PID_028E&IG_00\NNNN
  │     Xbox Series BT:    SWD\HIDMAESTRO_VID_045E_PID_0B13&IG_00\<sid>_NNNN
  │     Plain HID:         ROOT\VID_xxxx&PID_yyyy&IG_00\NNNN
  │     ├─ HID descriptor with Vx/Vy velocity triggers
  │     ├─ Event-driven worker reads shared memory → HID READ_REPORT
  │     │   (seqno-gated: idle CPU cost ~0.04% per controller)
  │     ├─ Explicit non-sentinel ContainerID via SwDeviceCreate's
  │     │   pContainerId (xinputhid path only) so xinput1_4!FUN_18000de2c
  │     │   does not flag the devnode as embedded/primary and skip slot 0.
  │     │   See Techniques: SWD Migration for the slot-1-skip fix.
  │     ├─ Per-process session-id prefix on instance-id suffix
  │     │   (`<pid-hex>_NNNN`) so Windows PnP's sticky per-container
  │     │   reuse-fast-path doesn't leave subsequent-run devnodes as
  │     │   empty registry shells. See Techniques: Session-Unique Suffix.
  │     ├─ USB interface (XUSB-companion profiles also get the xinputhid
  │     │   UpperFilter written per-instance by the SDK — see Techniques)
  │     ├─ Legacy WinExInput interface registration retained for historical
  │     │   compatibility; Ghidra decomp of Windows.Gaming.Input.dll showed
  │     │   zero references to its GUID, so it is not WGI's actual hook
  │     └─ BTHLEDEVICE CompatibleIDs (Bluetooth profiles)
  │
  └──► XUSB Companion (HMXInput.dll, System class)
        SWD\HIDMAESTRO\<sid>_NNNN  (non-xinputhid Xbox profiles only)
        ├─ XUSB interface {EC87F1E3-...} → XInput discovery + WGI dispatch
        ├─ UpperFilters = "xinputhid" (pure registry-string tripwire that
        │     admits the device to WGI's XUSB path without xinputhid.sys
        │     actually attaching — see Techniques below)
        ├─ Same explicit non-sentinel ContainerID as the main device
        │   (per-controller GUID derived from the controller index) so
        │   the two devnodes group as one logical controller in Settings
        │   and xinput1_4 dedups them into a single slot.
        ├─ Event-driven: reads GipData from shared memory
        └─ Handles GET_STATE/GET_CAPABILITIES/SET_STATE IOCTLs; returns
           29-byte WAIT_FOR_INPUT frames with state[9]=0x00 so WGI's
           XusbInputParser matches the Gamepad template's reportId=0

Both INFs set `UmdfHostProcessSharing = ProcessSharingDisabled`, so every
device instance gets its own WUDFHost process (~8 MB RSS, ~10 threads).
With 6 simultaneous controllers that's 8 per-instance hosts in place of
the default 1 shared host. The per-controller IO paths run in parallel
instead of serializing through one host's thread pool; idle CPU stays
near zero and peak throughput scales with controller count.
```

**Data flows:**
- **DirectInput** ← HID READ_REPORT ← shared memory (combined Z + Vx/Vy in descriptor)
- **XInput** ← XUSB GET_STATE ← companion reads GipData from shared memory
- **SDL3** ← HIDAPI skips (&IG_) → RawInput fallback → maps by VID/PID
- **Browser (plain HID / Xbox Series BT)** ← WGI Gamepad ← GameInput reads Vx/Vy via registry mapping
- **Browser (Xbox 360 Wired)** ← WGI Gamepad ← XUSB companion's interface, admitted via the xinputhid UpperFilter tripwire. Chromium `put_Vibration` dispatches `IOCTL_XUSB_SET_STATE` with motor bytes back through this path, where the SDK raises `OutputReceived` to the consumer.
- **Bluetooth ID**: HIDAPI checks CompatibleIDs, reports bus_type=BT

## Why UMDF2 Is Enough

A common assumption is that virtual game controllers require kernel-mode drivers. Here's why UMDF2 works:

- **HID class driver is already in the kernel.** Windows ships `mshidumdf.sys` which acts as a kernel-mode HID minidriver proxy. Our UMDF2 DLL runs in user mode but the HID class stack sees a real HID device.
- **XInput discovery uses device interfaces, not bus type.** `xinput1_4.dll` finds controllers through the XUSB device interface GUID. A UMDF2 driver can register this interface from user mode.
- **GameInput reads HID reports, not driver internals.** WGI/GameInput reads from the HID preparsed data and report descriptors; it does not care whether the underlying driver is kernel or user mode.
- **SDL3 and HIDAPI check device paths and attributes.** Bus type, VID/PID, and device path strings are all settable from user mode via SetupDI and CM APIs.

The only things UMDF2 *cannot* do: create PDOs (Physical Device Objects) as children of a bus, or intercept internal kernel IOCTLs. HIDMaestro works around this by using a companion device for XUSB and root-enumerated device nodes for the HID stack.

## Validation Results

Full per-profile results, device-tree dumps, HIDAPI enumeration logs, and timing characteristics.

### Startup and Hot-Plug Timing

| Operation | Measured Time |
|-----------|--------------|
| Cold start (first run: cert + sign + install + create 1) | ~18s |
| Warm start, single controller (drivers cached) | **~200ms** |
| Warm start, 4 mixed controllers (2 BT + 2 Xbox 360 wired) | **~2.2-2.8s** |
| Warm start, 6 mixed controllers (sequential) | **~3.5s** |
| Single dispose: plain HID (DualSense, wheels, etc.) | **~80ms** |
| Single dispose: Xbox 360 Wired (XUSB companion) | **~135 ms** (was ~5,700 ms pre-v1.3.1) |
| Single dispose: Xbox Series BT (xinputhid + SwD parent) | **~500 ms** (was ~11 s pre-v1.3.1) |
| Single create: Xbox 360 Wired | **~200-700 ms** (was 5-15+ s worst-case pre-v1.3.2) |
| Single create: Xbox Series BT | **~150-600 ms** (was 5-15+ s worst-case pre-v1.3.2) |
| 4-controller cleanup (parallel, batch path) | ~1.5 s |
| 6-controller mixed cleanup (sequential) | ~3-4 s |

Cold start includes certificate creation, signing, catalog generation, driver package installation, and device creation. This only happens on first run or after SDK updates. Warm start uses event-driven polled waits that exit as soon as PnP is ready. Zero fixed `Thread.Sleep` calls remain in any creation, cleanup, or finalization path. Controllers are independently disposable: removing one does not disturb the others.

**Same-boot run-to-run consistency:** every launch matches the fresh-boot Phase-1 timing. The earlier regression where subsequent same-boot runs took 65s (and lost XInput visibility for the XUSB-companion path) is fixed by the per-process session-id prefix on SWD instance-ids — see Techniques: Session-Unique Instance-ID Suffix.

**Per-step install breakdown** (visible in stdout when `HMContext.InstallDriver` runs): extract ~20ms · remove old packages ~100ms · sign ~130ms · generate catalogs ~840ms (the largest single step, AV-sensitive) · install drivers ~580ms · total ~1.7s on a clean machine. On corporate workstations with hundreds of devices in the PnP tree, total install can stretch to 5-20s; HIDMaestro doesn't run `pnputil /scan-devices` (it's a no-op for our INFs and was the largest variable contributor).

**Batch teardown:** `HMContext.Dispose()` and the public `DisposeControllersInParallel(controllers, perControllerCallback)` parallelize per-controller `DIF_REMOVE` work and run the system-wide HID orphan sweep once at the end instead of per-controller. With v1.3.1's SwD-first ordering the per-controller cost is already ~135–500ms, so the batch path's wall-clock benefit is now mostly avoiding the per-controller orphan-sweep duplication; for 4-6 mixed controllers the cleanup typically completes in 1.5-4s end to end. Live profile-switch (single `HMController.Dispose()` mid-session) stays synchronous because slot-allocation determinism requires the old devnode fully gone before the new one is created.

**Self-healing on init:** `HMContext.InstallDriver` calls `RemoveAllVirtualControllers` first thing, so any orphans left by a prior crashed session are cleaned up before the new install runs. The same call is exposed publicly as `HMContext.RemoveAllVirtualControllers()` for consumers who want explicit defensive cleanup (e.g. on app exit). In normal operation, individual `HMController.Dispose()` is sufficient — there is no per-process cleanup obligation on shutdown.

### Profile Architecture Groups and Teardown Timing

Disposal speed depends on which kernel-side drivers are in the device stack. Each additional driver in the stack adds its own PnP query-remove handshake, handle release, and notification cascade. HIDMaestro profiles fall into three architecture groups with dramatically different teardown characteristics:

#### 1. Plain HID: generic gamepads, wheels, HOTAS, flight sticks (~200ms)

Profiles where `driverMode` is not `"xinputhid"` and the VID is not Microsoft (`0x045E`). Includes DualSense, DualShock 4, all Logitech wheels, Thrustmaster HOTAS, flight sticks, pedals, arcade sticks, and most of the 224-profile catalog.

```
ROOT\VID_054C&PID_0CE6\NNNN          ← our UMDF2 driver (mshidumdf host)
  └─ HID\VID_054C&PID_0CE6\...       ← raw HID PDO, no upper filter
```

**Lightest stack.** One `DIF_REMOVE` on the ROOT parent tears down the entire tree. No XUSB companion device, no Microsoft upper filter. Creation ~200ms, disposal ~80ms.

#### 2. Non-xinputhid Xbox: Xbox 360 Wired (~135ms post-v1.3.1)

Xbox-VID profiles (`0x045E`) where xinputhid is not in the path. XInput is delivered via a separate SWD-enumerated XUSB companion device running `HMXInput.dll`. WGI dispatch also runs through that companion, admitted by the xinputhid UpperFilter tripwire described in Techniques.

```
ROOT\VID_045E&PID_028E&IG_00\NNNN    ← our UMDF2 driver (main HID device)
  │                                    UpperFilters += "xinputhid" per-instance
  │                                    (SDK-written; blocks WGI from building
  │                                    a second HID-backed Gamepad for this
  │                                    logical controller)
  └─ HID\VID_045E&PID_028E&IG_00\... ← HID child (raw PDO, input.inf)
SWD\HIDMAESTRO\<sid>_NNNN            ← XUSB companion (HMXInput.dll)
  │                                    SwDeviceCreate, System class, explicit
  │                                    per-controller ContainerID (shared with
  │                                    main HID for xinput1_4 dedup).
  │                                    UpperFilters = "xinputhid" from INF
  │                                    (admits the companion to WGI's XUSB
  │                                    dispatch; xinputhid.sys does not
  │                                    actually attach — wrong device class).
  │                                    `<sid>` = parent process PID in hex,
  │                                    bypasses Windows' sticky per-container
  │                                    fast-path that would empty-shell the
  │                                    devnode on subsequent same-boot runs.
  └─ XUSB interface → XInput slot + WGI Gamepad (one entry, live input +
                                     working put_Vibration on Chromium)
```

**Medium stack, fast on both sides post-v1.3.2.** Two device trees to tear down. The XUSB companion runs its own WUDFHost instance hosting `HMXInput.dll`, which needs its own PnP release cycle. v1.3.1's SwD-first ordering brought disposal to ~135 ms (down from ~5,700 ms). v1.3.2's `WaitForXInputSlotClaim` 500 ms cap brought worst-case creation to ~700 ms (typical ~200 ms). Round-trip create + dispose is ~350-850 ms.

#### 3. xinputhid Xbox: Xbox Series X|S Bluetooth (~500ms post-v1.3.1)

Profiles with `driverMode: "xinputhid"`. These match `xinputhid.inf [GIP_Hid]` by hardware ID (`HID\VID_045E&PID_0B13&IG_00`), which binds Microsoft's `xinputhid.sys` as an upper filter on the HID child. xinputhid provides XInput delivery + 16-button descriptor synthesis natively: no XUSB companion needed, single Device Manager entry.

```
SWD\HIDMAESTRO_VID_045E_PID_0B13&IG_00\<sid>_NNNN
  │                                  ← our UMDF2 driver via SwDeviceCreate
  │                                    (mshidumdf host). Explicit non-sentinel
  │                                    ContainerID closes the slot-1-skip
  │                                    bit-2 path in xinput1_4!FUN_18000de2c.
  │                                    Underscore between VID and PID avoids
  │                                    the `VID_*&PID_*&IG_*` PnP edge case;
  │                                    `&IG_00` retained because the HID
  │                                    child inherits this name and HIDAPI/
  │                                    SDL3/Chromium substring-match `&IG_`.
  └─ HID\HIDMAESTRO_VID_045E_PID_0B13&IG_00\...
        │                            ← HID child (xinputhid.inf, xinputhid
        │                              upper filter)
        ├─ xinputhid.sys              ← Microsoft inbox kernel filter
        ├─ XInput delivery (internal)
        └─ 16-button HID synthesis
```

**Both sides fast post-v1.3.2.** xinputhid is a Microsoft inbox kernel filter driver. Pre-v1.3.1 *teardown* went through the full PnP query-remove → class installer → filter unload chain on every Dispose because `DeviceManager.RemoveDevice` removed HID children before the SwD parent (each child's `WaitForDeviceRemoval` then timed out at 2,000ms because the children couldn't unwind while the parent's HSWDEVICE refcount was still held). v1.3.1 closes the SwD parent first via `SwdDeviceFactory.Remove` and blocks on `CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED`; the children cascade automatically once the kernel releases the parent. Disposal ~500ms.

v1.3.2 fixes the *creation* side too. `SetupController` runs three wait budgets after `CreateGamepadCompanion`: `WaitForHidChild` (10 s), `WaitForDeviceStarted` (5 s), and `WaitForXInputSlotClaim` (15 s pre-v1.3.2, **500 ms** post). The slot-claim wait was the dominant cost: distribution is bimodal (xinputhid publishes the slot in <100 ms when healthy, never publishes when xinputhid's allocator is in a stuck state — kernel state issue, prior-session residue), so the prior 15 s budget burned the full duration on every stuck case. PadForge users observed 13-14 s freezes on a single Xbox Series BT create when this hit. The 500 ms cap sits ~5x above the slowest observed healthy claim (giving slow-but-working cases full headroom) and degrades the stuck case to a near-imperceptible pause. Controller stays functional via DI/HIDAPI/Browser/WGI when XInput doesn't pick it up; XInput consumers see the slot appear lazily on their next poll cycle. Creation latency for Xbox Series BT is ~150 ms healthy / ~600 ms worst case post-fix.

#### SwD-first removal ordering (v1.3.1)

Two of the three architecture groups (Xbox 360 Wired and Xbox Series BT) own a SwDevice-enumerated parent. SwDevice lifetimes are anchored to the HSWDEVICE handle, not the PnP devnode — children of a SwD parent cannot fully unwind their query-remove cascade until the parent's handle drops its kernel refcount. Pre-v1.3.1, `DeviceManager.RemoveDevice` issued `DIF_REMOVE` on every HID child first (each followed by a 2,000ms `WaitForDeviceRemoval` that timed out because the parent was still holding the lifetime lock), then closed the SwDevice handle. Net cost: ~5,700ms for Xbox 360 Wired, ~11,000ms for Xbox Series BT, scaling worse with more children.

v1.3.1 inverts the order: for any `SWD\` parent, close the SwDevice handle FIRST via `SwdDeviceFactory.Remove`, block on `CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED` for the parent (so callers know the kernel has actually propagated removal, not just that the handle closed), then mop up any HID children that survived the cascade — usually none, because the SwD parent's release fires its children's removal in one cascade.

A second optimization in the same change: when a HIDMAESTRO sweep walks registry entries that exist only as PHANTOM (registry residue from prior sessions, no live devnode), skip the `hmswd.exe` SwDeviceCreate-reconnect roundtrip entirely. Saves ~50-75ms per stale entry and prevents creep across same-process recreation cycles.

#### Why this matters for consumers

If your application needs fast profile switching (e.g. remapping a physical controller to a different virtual identity on the fly), the profile architecture group determines the user-perceived latency:

- **Switching between plain HID profiles** (DualSense ↔ DualShock 4, or any non-Xbox pair): ~280 ms round trip (~80 ms dispose + ~200 ms create). Essentially instant.
- **Switching to/from Xbox 360 Wired**: ~135 ms dispose (down from ~5,700 ms pre-v1.3.1) + ~200-700 ms create (slot-claim wait capped at 500 ms post-v1.3.2). Round-trip ~350-850 ms vs ~6.4 s pre-v1.3.1.
- **Switching to/from Xbox Series BT**: ~500 ms dispose (down from ~11 s pre-v1.3.1) + ~150-600 ms create (slot-claim wait capped at 500 ms post-v1.3.2). Round-trip ~650-1,100 ms vs ~11+ s pre-v1.3.1. PadForge user-reported: virtually instantaneous create and swap.

### Tool Output Logs

<details>
<summary>HIDAPI enumeration: Xbox 360 Wired (click to expand)</summary>

```
VID=0x045E PID=0x028E
  Product: Controller (XBOX 360 For Windows)
  Usage: page=0x0001 usage=0x0005
  Bus type: 1 (USB)
  Path: \\?\HID#VID_045E&PID_028E&IG_00#...
  &IG_ in path: True
```
</details>

<details>
<summary>HIDAPI enumeration: Xbox Series BT (click to expand)</summary>

```
VID=0x045E PID=0x0B13
  Product: HID-compliant game controller
  Bus type: 2 (Bluetooth)
  &IG_ in path: True
```
</details>

<details>
<summary>XInput state: Xbox 360 Wired (click to expand)</summary>

```
Slot 0: Connected  LT=87 RT=87 LX=3080 LY=29988 Buttons=0x1000
Slot 1: Not connected
Slot 2: Not connected
Slot 3: Not connected
```
</details>

<details>
<summary>PnP device tree: Xbox 360 Wired (click to expand)</summary>

```
Status Class    FriendlyName                  InstanceId
------ -----    ------------                  ----------
OK     HIDClass Game Controller               ROOT\VID_045E&PID_028E&IG_00\0000
OK     System   HIDMaestro XInput Companion   SWD\HIDMAESTRO\A7B4_0002
OK     HIDClass HID-compliant game controller HID\VID_045E&PID_028E&IG_00\...
```

The `A7B4` prefix on the companion's instance-id suffix is the parent process's PID in hex, applied per-launch to bypass Windows PnP's sticky per-container fast-path. See Techniques: Session-Unique Instance-ID Suffix.
</details>

<details>
<summary>XUSB companion device interfaces + UpperFilters (click to expand)</summary>

```
XUSB Interface:
  Path:   \\?\SWD#HIDMAESTRO#A7B4_0002#{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}
  Device: SWD\HIDMAESTRO\A7B4_0002
  Status: Enabled

Registry:
  HKLM\SYSTEM\CurrentControlSet\Enum\SWD\HIDMAESTRO\A7B4_0002
    UpperFilters = "xinputhid"       ← WGI dispatch tripwire (INF-written)
    DEVPKEY_Device_ContainerId = {48494430-4D41-4553-5452-4F0000000002}
                                     ← explicit per-controller GUID via
                                       SwDeviceCreate's pContainerId,
                                       shared with the main HID device

Main HID device:
  HKLM\SYSTEM\CurrentControlSet\Enum\ROOT\VID_045E&PID_028E&IG_00\0000
    UpperFilters = "xinputhid"       ← prevents duplicate HID-backed
                                       WGI Gamepad (SDK-written per-instance,
                                       only for profiles with an XUSB
                                       companion)
```

Only one device interface is registered on the XUSB companion. Publishing a second interface would create a duplicate WGI provider arrival and classifier confusion — the tripwire plus the single XUSB registration is what produces exactly one Gamepad.
</details>

## How to Reproduce the Validation

Each validation result above was produced with these tools:

| Check | Tool | Command / Method |
|-------|------|-----------------|
| DirectInput axes/buttons | Python `ctypes` + DirectInput8 + `winmm.joyGetDevCapsW` | `scripts/verify.py` |
| XInput slots/triggers | Python `ctypes` + `xinput1_4.XInputGetState` | `scripts/verify.py` |
| SDL3/HIDAPI identity | Python `hid.enumerate()` | `scripts/verify.py` |
| Browser Gamepad | Headless Edge/Chrome → `navigator.getGamepads()` | `scripts/verify.py` (via `scripts/browser_check/`) |
| GameInput / WGI | `winrt.windows.gaming.input.RawGameController` | `scripts/verify.py` |
| HID enumeration order | Python `hid.enumerate()` filtered by `HM-CTL-` serial | `scripts/verify.py` |
| Cross-API mark-mode ordering | C++ multi-backend harness (MPT 1:1) | `build/multipad_check.exe --trigger` |
| Real vs virtual HID stream diff | C++ HID capture tool | `build/hid_capture.exe <vid> <pid>` |
| Device tree | `Get-PnpDevice` (PowerShell) | Manual |
| joy.cpl | Windows Game Controllers control panel | Manual |

To reproduce: run `HIDMaestroTest.exe emulate <profile-id>`, then run `python scripts/verify.py` in a separate terminal. For multi-controller validation: `HIDMaestroTest.exe emulate <id1> <id2> ...` then `python scripts/verify.py --controllers N`.

## Glossary

| Term | Meaning |
|------|---------|
| **XUSB** | Xbox USB protocol. The device interface GUID (`{EC87F1E3-...}`) that `xinput1_4.dll` discovers to find Xbox controllers, and the one WGI walks for XUSB-backed Gamepads. |
| **WinExInput** | Windows Extended Input. A device interface GUID (`{6C53D5FD-...}`) registered on HID parents by HIDMaestro for historical reasons. Ghidra decomp of `Windows.Gaming.Input.dll` (Win11 26200) found zero references to this GUID; it is not actually WGI's `GamepadAdded` source. WGI admission comes from the HIDClass pass-list (plain HID profiles) or the xinputhid UpperFilter tripwire (Xbox XUSB-companion profiles). |
| **xinputhid UpperFilter tripwire** | Registry string `"xinputhid"` written to a device's `DEVPKEY_Device_UpperFilters` (via INF HKR AddReg or SetupAPI) to satisfy WGI's `IsDeviceOrAncestorFilteredBy` wstring compare. Does not load `xinputhid.sys` — the filter only attaches to HID-class devices. Admits a System-class device (the XUSB companion at `SWD\HIDMAESTRO`) to WGI's XUSB dispatch path. See Techniques. |
| **XUSB Companion** | A separate UMDF2 device (`HMXInput.dll`) that handles XUSB IOCTLs for XInput. Lives at `SWD\HIDMAESTRO\<sid>_NNNN`. Needed because `mshidumdf` suppresses XUSB on HID devices. |
| **SWD enumerator** | "Software-device" PnP enumerator. Devices created via `SwDeviceCreate` (cfgmgr32) appear under `HKLM\SYSTEM\CurrentControlSet\Enum\SWD\<enumerator>\<instance>`. The SwDevice API lets us specify an explicit non-sentinel `pContainerId`, which is the linchpin of the slot-1-skip fix. |
| **Session-id prefix** | Per-process unique token (the launching process's PID in hex) prepended to every SwD instance-id suffix. Bypasses Windows PnP's sticky per-`(enumerator + suffix + ContainerId)` reuse-fast-path that would otherwise leave subsequent same-boot devnodes as empty registry shells with no driver bound. |
| **ContainerID slot-1 skip** | Pre-fix bug in `xinput1_4!FUN_18000de2c`: a null-sentinel ContainerID `{00000000-...-FFFF-FFFFFFFFFFFF}` triggered a code path that set bit 2 on the device struct, made the fallback slot allocator skip iter 0, and surfaced an empty slot 1 to consumers. The SWD migration's explicit `pContainerId` closes the path. |
| **GameInput mapping** | Registry entries at `HKLM\...\GameInput\Devices\{VID}{PID}...` that tell WGI how to map HID axes/buttons to the Gamepad interface. |
| **&IG_** | "Interface Group" marker in Xbox device paths. Chrome and HIDAPI skip devices with this in the path; SDL3 falls through to its RawInput backend. |
| **Vx/Vy** | HID velocity usages (0x40/0x41). Invisible to DirectInput's axis mapper but enumerated by GameInput; used to carry separate trigger values. |
| **mshidumdf** | Microsoft's kernel-mode HID minidriver proxy that hosts UMDF2 HID drivers. |
