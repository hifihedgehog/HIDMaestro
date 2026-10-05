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

Sony BT controllers (DualSense, DualSense Edge, DS4 BT) declare their input as a 78-byte vendor-defined "blob": one opaque field with no descriptor-level breakdown of which bytes carry sticks vs buttons vs gyro vs CRC32. Pre-v1.3.5 the SDK couldn't pack this and fell back to emitting basic Report 1 (9 bytes), which Steam Input misclassified as USB and `dualsense-tester` couldn't parse.

v1.3.5 makes the byte layout data: profile JSON declares `extendedReport` (input) and `extendedOutputReport` (output) blocks describing every field's type, byte position, and bit range. The SDK becomes a generic codec that walks the field list. Future profiles with vendor blobs (Switch Pro extended, vendor-specific wheels) add the JSON only: no SDK code changes per profile.

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

The encoder/decoder reaches the public input-state surface too. `HMGamepadState` ships per-frame fields the Sony JSON blocks understand: `TouchpadFinger0Active/X/Y/Id` + `TouchpadFinger1Active/X/Y/Id`, `GyroPitch/Yaw/Roll` + `AccelX/Y/Z` + `SensorTimestamp`, and `BatteryLevel` (0..10) + `BatteryCharging` + `BatteryFull` + `MicMuted` + `HeadphonesConnected`. `SensorTimestamp` counts 1/3 µs ticks, the DualSense's own unit. The DualShock 4 profiles divide it by 16 into that pad's 16/3 µs ticks, and while it is 0 they stamp the time since the controller's first report. `TouchpadPacketCounter` fills the DualShock 4 touch report's packet-counter byte. Profiles that don't declare these regions silently ignore them, so the same caller code works across every controller. `dualsense-tester` (ds.daidr.me) renders touchpad coordinates, the IMU vector, and the battery panel for any DualSense or DualSense Edge virtual (USB or BT) once the consumer fills these fields.

The driver answers the Sony calibration reads with the identity calibration for the pads' nominal units, 8192 counts per g and 16 per degree per second: zero biases, gyro references of plus and minus 8000 at a speed of 500, and accelerometer references of plus and minus 8192. SDL and Linux reduce that to a scale of exactly 1, so a value a consumer submits reaches the game at the unit the profile declares. A DualShock 4's Bluetooth report 0x05 lists the three plus references before the three minus references, unlike its USB report 0x02 and the DualSense, and the driver serves each pad's order. Bluetooth profiles end reports 0x05, 0x09 and 0x20 with the CRC-32 a real pad carries over that link. Linux checks it, and RPCS3 drops a pad whose calibration fails the check three times.

DS4 Bluetooth vibration through the browser Gamepad API is fixed in v1.3.5 by setting the device's `HidD_GetAttributes` `VersionNumber` to 0: the value Chromium's `DualShock4Controller::BusTypeFromVersionNumber` checks for the BT-format report header. Pre-v1.3.5 the SDK hardcoded `0x0100` (USB), Chromium picked the wrong wire layout, and the rumble bytes never reached the device. Profiles can now override `versionNumber` in JSON; `dualshock-4-v2-bt.json` ships with `0`. Steam Input never used this gate so v1.3.5 doesn't change Steam behavior either way.

### Switch Pro Protocol Responder

The Switch Pro Controller is not a passive HID device: hosts (SDL's `HIDAPI_DriverSwitch`, Steam, BetterJoy) drive a Nintendo init and subcommand protocol and stall without a device that answers. The generic report-builder cannot express request-reply, so `driver.c` carries a hardcoded responder keyed on VID 0x057E PID 0x2009 (protocol lives in code, layout in JSON, the same split as the Sony vendor-blob work).

Since v1.3.21 (issue #37) the shipped descriptor is the real BLUETOOTH one, extracted byte-exact from a live Pro's SDP cache. It declares no 0x80/0x81 USB-init family, so those writes fail at HidClass exactly as on real Bluetooth hardware and SDL initializes over its Bluetooth path. The driver's `80 01/02/03` responder (`81 xx` replies with device type Pro and a stable fabricated MAC) remains for custom profiles that declare the USB family, for example one cloned from the profile's `nativeDescriptor`.

Subcommands (output 0x01, padded to the 49-byte Bluetooth write size) get input-report 0x21 replies per the nxbt responder table, including SPI flash reads served from a fabricated image: factory stick calibration with center 0x800 and range 0x600, and IMU coefficients (0x4000 accel, 0x343B gyro) chosen so SDL's calibration math reduces exactly to its own default scales. Unknown subcommands get a generic ACK rather than nxbt's silent ignore, because SDL retries unanswered subcommands for ~500 ms where the Switch console does not. A dedicated driver thread streams at the wire's ~60 Hz cadence: genuine 12-byte 0x3F simple-mode frames (the only report DirectInput can parse) until the first subcommand arrives, then the 49-byte Bluetooth full-mode 0x30 with the driver stamping timer and battery bytes over the consumer-submitted body.

Consumers submit through the normal `SubmitState`: `SwitchProPacker` converts the layout-mapped buttons, 12-bit packed sticks, and the calibrated IMU channel (`HMGamepadState.AccelG*` in g, `GyroDps*` in deg/s, defined in the SDL-standard sensor frame) into the wire body. The packer owns the SDL-to-Switch frame permutation, so a consumer reading motion from SDL submits it verbatim and the client-side SDL reconstructs the identical vector. HD rumble comes back decoded to coarse `leftMotor`/`rightMotor` amplitudes on `OutputDecoded`, the same lane Sony rumble rides. `test/probes/switch_pro_check` replays SDL's exact init sequence over raw HID as the release gate.

### Switch 2 Pro Command Responder and Report Clock

A Switch 2 Pro Controller over USB is two interfaces, and a host starts it through the second. Interface 0 is HID. Interface 1 is vendor class with a bulk pair, where SDL's Switch 2 driver and Steam send an 8-byte header and data (command, `0x91`, transport, subcommand, 0, length, 0, 0) and read back the command, a status, the transport, the subcommand, `00 F8 00 00` and data. The pad answers every poll of its interrupt endpoint with NAK until command 03/0D arrives, then sends one report every 4.000 ms. `switch2-pro-controller-composite` reproduces that device on the USB/IP backend from a link-layer capture of a console driving a pad on firmware 1.1.5.

Windows has to bind WinUSB to interface 1 before libusb can claim it, and a real pad arranges that itself with Microsoft OS 1.0 descriptors. The profile's `usbConfiguration.microsoftOs` block carries three blobs: the string Windows reads at index 0xEE (`MSFT100` and a vendor code), the extended compatible ID naming `WINUSB` for interface 1, and the extended properties holding a `DeviceInterfaceGUID`. Windows asks for the last two with a vendor request whose `wIndex` is 4 or 5 and whose `bRequest` is the vendor code it stored for this VID, PID and revision. The backend answers on `wIndex` alone, since a real pad of the same revision may have stored a different code on that PC before the persona was ever attached. A profile without the block stalls 0xEE and every vendor request, as before.

`Switch2ProDevice` holds the protocol state: whether reports are on, the selected report, the feature mask and the enabled features, the player LEDs, a flash image and the queue of replies. Each bulk OUT payload is logged raw before anything decodes it, then answered at once, because SDL waits 100 ms for each reply. A reply longer than one read spans several, so the pad's 80-byte flash reply goes out as 64 bytes and then 16 to a host that reads in 64s. The flash image holds the persona's serial at the address SDL reads a pad's serial from, stick calibration with center 0x800 over the whole 12-bit range, and a zero gyro bias, which SDL subtracts. The same serial is USB string 3 and the HID serial string, the pair PadForge's SDL fork matches to join the two interfaces of one pad. Every byte no segment names reads 0xFF, as erased flash does on the pad.

The report clock belongs to the device. Each report's motion timestamp advances 4000 microseconds, the step a real pad's reports carry. SDL counts 100 reports and takes this pad's sensor constants, a gyro scale of 34.8 rad/s, when the timestamp advanced 3600 to 4399 a report, then hands the timestamp on as each sample's time, 4,000,000 ns apart. That holds only if the reports really are 4 ms apart. Reports that followed the consumer's submit rate would carry timestamps that run fast or slow against real time. A thread on a high-resolution waitable timer sends one report every 4 ms from the command that started the pad, and builds it at that moment from the consumer's newest state and the protocol state. One count of reports sent drives report `0x09`'s 8-bit counter, report `0x05`'s millisecond field (4 a report) and its motion timestamp (4000 a report, starting at 1, since SDL reads 0 as no sample). A tick that finds no read parked leaves one report owed, and the next read is answered as it arrives. A clock that was held up skips the ticks it missed and sends no burst. `SubmitState` packs a 24-byte body of buttons, 12-bit sticks and motion counts through `Switch2ProPacker` and only replaces what the next report is built from, which is why the raw submit methods throw on this persona.

Motion converts with the constants SDL's driver applies to this pad: 32767 counts per 8 g and per 34.8 rad/s. The pad's three fields are SDL's X, minus Z and Y, in that order. Rumble is output report `0x02`, one 16-byte block per actuator of three 40-bit frames. Each frame holds two amplitudes. SDL writes its high-frequency level in the first and its low-frequency level in the second, so `OutputDecoded` reports the largest first amplitude over the six frames as `rightMotor` and the largest second as `leftMotor`, scaled back from the 29000 that SDL scales a full motor value to.

usbip-win2 0.9.8.1 presents the full-speed bulk endpoints to Windows with a 512-byte packet size, and it completes a canceled transfer through `UdecxUrbCompleteWithNtStatus` without a call to `UdecxUrbSetBytesCompleted` (`drivers/ude/request_list.cpp`). A WinUSB read that times out on this persona therefore returns success with the previous reply's bytes, and later reads are served from the same stale buffer without a transfer reaching the device. The device's own trace shows the canceled read unlinked and no bulk IN request after it. A host that reads only after it has sent a command never abandons a read. `UsbipEmulatedDevice.BulkReadsCanceled` counts the ones that were, and S65 asserts that Steam leaves it at 0. `test/probes/switch2_composite_check` (S65) plays the driver's side of the wire against the capture, then reads the live persona through HidUsb, WinUSB, SDL3 with libusb and the Steam client.

### Composite Stream State Without SET_INTERFACE

A composite persona learns that the host opened or parked an audio stream from SET_INTERFACE, which usbip-win2 sends on behalf of its upper filter driver. The filter intercepts each SELECT_INTERFACE and tells the host controller driver with a marker request: GET_FIRMWARE_STATUS, `wIndex` 0xFFFF, with the URB function in the timeout's low word. usbip-win2 0.9.8.1 compares the marker's `bmRequestType` against 0x80. Every earlier filter sends 0x00, because the 0.9.7.x and 0.9.8.0 headers initialize `BM_REQUEST_TYPE` as `.bmRequestType = 0x80`, which sets the union's first member, the 2-bit `Recipient` field, to 0. 0.9.8.1 writes `.bmRequestType{.B = 0x80}`. The binaries agree. The 0.9.7.5 host controller driver, both 0.9.8.0 drivers and the 0.9.7.7 filter carry 0x00. The 0.9.8.1 host controller driver and filter carry 0x80.

HIDMaestro's in-place move to 0.9.8.1 replaces the host controller driver and leaves the filter alone, since updating the filter restarts every USB root hub and drops the physical controllers a consumer reads. On a moved machine the 0.9.8.1 driver passes the old filter's marker to the device as an ordinary request, so the persona receives `00 1A 0000 FFFF 0000` where SET_CONFIGURATION and SET_INTERFACE belong, while the audio flows. The audio engine therefore reads stream state from the traffic for any interface that has never received a SET_INTERFACE: the first isochronous transfer opens it at the endpoint's alternate setting, and 200 ms without one closes it. An interface that has received a SET_INTERFACE follows SET_INTERFACE alone, so a matched transport behaves exactly as before. S43 covers both paths over loopback, and S45 covers the live stack.

### DualShock 3 Sixaxis Mode

On Windows, PCSX2 and RPCS3 read a DualShock 3 through Sony's sixaxis.sys driver, or through DsHidMini in its SXS mode, which presents the same device: a 12-byte joystick report with no report id, and the pad's whole native 49-byte report as feature report 0. PCSX2 reads it through SDL with `SDL_HINT_JOYSTICK_HIDAPI_PS3_SIXAXIS_DRIVER` set and accepts only a pad with SDL's 16 axes, ten of which are button pressures. RPCS3 reads feature report 0xF2 and falls back to 0.

The `dualshock-3-full` profile sets `sixaxisCompatible`, which the SDK passes to the driver as the `Ds3Sixaxis` registry value. Its `extendedReport` is the native report 0x01 with `alwaysArmed`, so the codec fills it every frame: buttons, sticks, the twelve pressure bytes (`HMGamepadState.Pressure*` and the triggers), the battery byte, and the 10-bit big-endian motion sensors at 113 counts per g and 123 counts per 90 degrees per second. The driver keeps the latest report and derives everything else from it the way DsHidMini's SXS mode does: the 12-byte input report, and the feature reply, which turns the motion fields little-endian and mirrors accelerometer X. The descriptor declares no report ids, so the HID class delivers every feature read to the driver as report 0, whatever id the reader asked for, and leaves the reader's id byte in place. A read of 0xF2 therefore returns the same report, as it does on DsHidMini.

Output arrives as report 0 with a sixaxis.sys command in its first byte. For command 2, which sets the motors, everything after the three-byte command header is the native output report's body. DsHidMini applies it that way, and RPCS3 lays out its Windows report to match. Command 1 sets the four player LEDs, which the driver writes into the native report's LED bitmap. The driver publishes the result as native output report 0x01, so `OutputDecoded` reports the same fields whichever form the reader wrote. `test/probes/ds3_sixaxis_check` (S64) drives stock SDL with PCSX2's hints against a live persona and replays RPCS3's reads and writes.

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

HIDMaestro's XUSB companion (`SWD\HIDMAESTRO\<token>`) runs under the System class `{4d36e97d-...}`. That class is not on the pass-list, so before this work WGI silently skipped the companion despite it publishing the XUSB device interface: Chromium's `put_Vibration` went nowhere for Xbox 360 Wired.

The fix writes the string `"xinputhid"` to the companion's `UpperFilters` registry value via the INF's `HKR` AddReg. `xinputhid.sys` is a HID-class filter, so it never actually attaches to the System-class companion; the string sits inert in the registry and WGI's wstring compare passes anyway. The companion enters WGI via the XUSB dispatch path, and `IOCTL_XUSB_SET_STATE` starts reaching the driver with real motor bytes on `put_Vibration`.

The same string gets written per-instance to the HID parent by `DeviceOrchestrator` for XUSB-companion profiles only. That second write blocks WGI's `HidClient::CreateProvider` from synthesizing a duplicate HID-backed Gamepad for the same logical controller, so WGI shows exactly one Gamepad with live input and working vibration instead of two pads splitting the responsibilities.

The 29-byte `IOCTL_XUSB_WAIT_FOR_INPUT` reply format was nailed down in the same decomp pass: `state[9] = 0x00` so `XusbInputParser`'s built-in Gamepad template matches (a prior 0x14 value produced an all-zero `GetCurrentReading` despite input arriving), plus the `state[10] = 0x14` non-zero gate byte, `state[2] = 0x03 RESUMED` on every completion, and version bytes `0x01 0x03` at `state[0..1]`.

The companion's other two replies share one header rule: a two-byte XUSBVersion word comes first, and the payload follows it. `IOCTL_XUSB_GET_BATTERY_INFO` answers `00 00 01 03`, which is the type at byte 2 and the level at byte 3, WIRED and FULL. A caller reads the pair from byte 2 onward, because `XInputGetBatteryInformation` copies out of a struct whose first member is that version word. Packing the pair at bytes 1 and 2 instead made every virtual pad read back as NiMH at EMPTY, and SDL maps any type other than WIRED, UNKNOWN or DISCONNECTED to on-battery and EMPTY to 10 percent, so games showed a flat battery on a pad that has none (issue #61). `IOCTL_XUSB_GET_LED_STATE` follows the same shape and always did.

### SWD Migration: the XInput slot-1-skip fix

Pre-fix, HIDMaestro created its devnodes via `SetupDiCreateDeviceInfoW` under `ROOT\`: the standard root-enumerated path. Windows assigns the null-sentinel ContainerID `{00000000-0000-0000-FFFF-FFFFFFFFFFFF}` to ROOT-enumerated devices unless overridden, and the SetupAPI path provides no way to override it.

Ghidra decomp of `xinput1_4.dll` on Win11 26200 traced the consequence. `FUN_18000de2c` returns 1 when ContainerID matches the null sentinel OR when HardwareIds contains the literal `XINPUT_EMBEDDED_DEVICE` substring. Caller `FUN_18000c728` at `0x18000C8AE` does `test al, al; jne → or dword ptr [rbx], 4`, setting bit 2 on the device struct. `FUN_18000f85c`'s fallback allocator at `0x18000F9C3-C7` skips internal slot 0 for bit-2 devices when Feature Manager flag `0x39EB83D` is on; `FUN_18000f178` then promotes the first bit-2 slot to "primary" and the query-time swap at `FUN_18000f08c` surfaces an empty slot 1 to consumers.

The fix is a one-line API switch: use `SwDeviceCreate(pContainerId = real-per-controller-GUID, ...)` instead of `SetupDiCreateDeviceInfoW`. The SwDevice API takes an explicit container GUID; we pass `{48494430-4D41-4553-5452-4F000000<idx:X4>}` (ASCII "HIDMAESTRO" + 16-bit controller index) so each virtual gets a deterministic non-sentinel container shared by its main + companion devnodes. `de2c` returns 0, bit 2 stays clear, slot allocator fills 0..3 contiguously.

The xinputhid-path profiles (Xbox Series BT etc.) moved fully to `SWD\HIDMAESTRO_VID_<vid>_PID_<pid>&IG_00\` because the SwDevice path is the only reliable way to inject a real ContainerID. The non-xinputhid Xbox path keeps its main HID device on `ROOT\VID_*&PID_*&IG_00\` (existing INF binding) and moves only the XUSB companion to `SWD\HIDMAESTRO\`. On the xinputhid path, main and companion share the per-controller container GUID. On the non-xinputhid path they do not: the ROOT-enumerated main keeps the null-sentinel container `{00000000-0000-0000-FFFF-FFFFFFFFFFFF}` because SetupAPI provides no override, while its companion carries the per-controller HIDMAESTRO container (measured on 26200, issue #59). No consumer that dedups on container can relate that pair; keeping the 360 family to one WGI entity rests on the xinputhid tripwire instead.

The underscore between VID and PID in the gamepad-companion enumerator (`HIDMAESTRO_VID_045E_PID_0B13&IG_00`, not `...VID_045E&PID_0B13...`) avoids a Windows PnP edge case in which any SWD enumerator name matching the substring `VID_*&PID_*&IG_*` registers in the registry but never enumerates as a live devnode. The `&IG_00` suffix is preserved because the HID child inherits its parent's enumerator name as the first segment of its instance path, and HIDAPI/SDL3/Chromium all blocklist `&IG_` substrings to avoid duplicating XInput-claimed devices.

### Stable Device Identity Across Lives

Since v1.8.0 (issue #60) every virtual controller has a durable identity, and every id a consumer can key on comes back the same on each life of that controller: the same process recreating it, a process restart, a reboot, and a driver upgrade. The identity is the consumer's key, passed as the second argument of `CreateController` and `CreateControllerAt`. A caller that passes none gets the controller index as its key, which is what every caller got before.

What consumers key on, measured on 26200: DirectInput's instance GUID is a per-VID/PID ordinal in `HKCU` and was stable already. SDL's joystick path is the HID interface path. Steam keys a USB/IP persona's configuration on its USB serial. Windows.Gaming.Input and GameInput key on the ContainerId and the device path. XInput keys on the slot only. Before v1.8.0 the HID interface path changed on every life, because every parent devnode was created with a Windows-generated instance name and its `ParentIdPrefix`, the `1&hash&n` value the HID child's instance id is built from, was minted again each time: one reporter's pad had counted 74 lives.

Every derived value is a pure function of the key, nothing is stored, and the mechanism differs per family:

| Family | Parent | How the child path stays put |
|--------|--------|------------------------------|
| Plain HID (Sony UMDF2, generic pads, wheels) | `ROOT\HIDClass\HM_0000` (SetupAPI, explicit instance id) | The identity's `ParentIdPrefix` is written into the instance key between `SetupDiCreateDeviceInfo` and `DIF_REGISTERDEVICE`. PnP reads the value from the parent's key when the HID child first arrives and mints a counter only when it is absent. |
| Non-xinputhid Xbox (Xbox 360 wired) | `ROOT\VID_045E&PID_028E&IG_00\HM_0000` (SetupAPI) plus `SWD\HIDMAESTRO\HM_0000` (SwDevice) | Main devnode as above. The XUSB companion has no HID child, so a fixed SwDevice tuple is the whole fix. Its interface path is `SWD#HIDMAESTRO#HM_0000#{EC87F1E3-...}` on every life. |
| xinputhid Xbox (Xbox Series, One, Elite over Bluetooth) | `SWD\HIDMAESTRO_VID_045E_PID_0B13&IG_00\HM_0000` (SwDevice) | Fixed SwDevice tuple. `SwDeviceCreate` binds and starts the driver inside the call, so the child has already enumerated by the time the SDK can write anything. The identity's prefix is reconciled into the key afterwards, and the restart the creation path already performs re-keys the child under it. |
| USB/IP composite personas | `USB\VID_054C&PID_0CE6\<serial>` (usbccgp) | A profile without a captured serial (the Sony composites) serves the identity's synthetic serial at a string index the descriptor did not use, so Windows keys the USB instance on the serial instead of the vhci port. Profiles with a captured serial keep it, varied by identity. The composite parent's own `ParentIdPrefix` persists in its phantom record the way it does for real hardware. |

`HM_0000` is the token for the default key of index 0. A consumer key derives `HM_` plus sixteen hex digits, a container GUID of the same `HIDMAESTRO` family, a `1&hash&0` prefix and a `HM` plus twelve hex digit serial, all from one SHA-256 of the key.

The session-unique instance-id suffix that v1.1.30 through v1.7.3 put on every SwDevice call is gone. It existed because a `DIF_REMOVE` on a `SWDeviceLifetimeParentPresent` device left the software device alive in the kernel, and a create with the same tuple reconnected to that half-removed shell: `S_OK` with no Service, no driver, no interface. Teardown has gone through `SwDeviceSetLifetime(Handle)` plus `SwDeviceClose` since v1.1.31, which destroys the software device, and the reuse works after it. The identity lab (`test/probes/identity_lab`, run on 26200, three lives per variant) measured the matrix the issue asked for:

| Variant | Bound | Same parent | Same child |
|---------|-------|-------------|------------|
| Unique SwDevice suffix, fixed container (v1.7.3 behavior) | yes | no | no |
| Fixed suffix, fixed container, phantom record retained | yes | yes | yes |
| Fixed suffix, fixed container, phantom record purged between lives | yes | yes | no (counter 0, 1, 2) |
| Fixed suffix, purged, prefix written after create plus restart | yes | yes | yes |
| Fixed suffix, changing container, retained | yes | yes | yes |
| Fixed suffix, fast remove without waiting for the cascade, immediate recreate | yes | yes | yes |
| XUSB companion, fixed suffix, retained or purged or changing container | yes | yes | (no child) |
| ROOT parent, explicit instance id, no prefix written | yes | yes | no (counter 0, 1, 2) |
| ROOT parent, explicit id, prefix written before registration | yes | yes | yes |
| Instance key created BEFORE `SwDeviceCreate` | **no** | | |

The last row is the one hazard: a `SWD` instance key that exists before the software device does leaves a record PnP stamps with a SYSTEM-only `Properties` subkey and never enumerates, and an administrator cannot delete it. Nothing in the SDK creates one. The ROOT path may write into its key before registration, because SetupAPI itself creates that key.

Two consequences for consumers. A DirectInput ordinal still moves when a same-VID/PID pad in front of it leaves (that is DirectInput's rule, not ours), while the path and serial of the remaining pad do not. And a different profile at the same key keeps the identity and refreshes the descriptor: the battery swaps a DualShock 4 in at a DualSense's key and reads the new attributes at the old paths.

## Architecture

```
User-Mode Test App
  │ Writes input data to per-controller shared memory section
  │ Manages device lifecycle (create, configure, remove)
  │
  ├──► Shared Memory (per-controller, pagefile-backed)
  │     SeqNo(4) + DataSize(4) + Data[256] + GipData[14] +
  │       ExtendedReportSize(4) + ExtendedReportData[80] = 362 bytes
  │     Data[256] carries HID input reports up to 256 bytes (DualSense BT
  │       report 0x31 = 78 bytes, Switch Pro motion-IMU reports, etc.).
  │     Event-driven: SDK signals InputDataEvent on each write.
  │
  ├──► Main HID Device (HIDMaestro.dll via mshidumdf)
  │     Xbox 360 Wired:    ROOT\VID_045E&PID_028E&IG_00\HM_0000
  │     Xbox Series BT:    SWD\HIDMAESTRO_VID_045E_PID_0B13&IG_00\HM_0000
  │     Plain HID:         ROOT\HIDClass\HM_0000
  │     (HM_0000 is the identity token of index 0; a consumer key
  │      derives HM_ plus sixteen hex digits. See Techniques: Stable
  │      Device Identity.)
  │     ├─ HID descriptor with Vx/Vy velocity triggers
  │     ├─ Event-driven worker reads shared memory → HID READ_REPORT
  │     │   (seqno-gated: idle CPU cost ~0.04% per controller)
  │     ├─ Explicit non-sentinel ContainerID via SwDeviceCreate's
  │     │   pContainerId (xinputhid path only) so xinput1_4!FUN_18000de2c
  │     │   does not flag the devnode as embedded/primary and skip slot 0.
  │     │   See Techniques: SWD Migration for the slot-1-skip fix.
  │     ├─ Identity token as the instance name and the identity's
  │     │   ParentIdPrefix on the parent, so the HID child's path is the
  │     │   same on every life. See Techniques: Stable Device Identity.
  │     ├─ USB interface (XUSB-companion profiles also get the xinputhid
  │     │   UpperFilter written per-instance by the SDK: see Techniques)
  │     ├─ No WinExInput interface: registering it produced duplicate
  │     │   browser gamepad entries (issue #6), and Ghidra decomp of
  │     │   Windows.Gaming.Input.dll found zero references to its GUID
  │     └─ BTHLEDEVICE CompatibleIDs (Bluetooth profiles)
  │
  └──► XUSB Companion (HMXInput.dll, System class)
        SWD\HIDMAESTRO\HM_0000  (non-xinputhid Xbox profiles only)
        ├─ XUSB interface {EC87F1E3-...} → XInput discovery + WGI dispatch
        ├─ UpperFilters = "xinputhid" (pure registry-string tripwire that
        │     admits the device to WGI's XUSB path without xinputhid.sys
        │     actually attaching: see Techniques below)
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

**Same-boot run-to-run consistency:** every launch matches the fresh-boot Phase-1 timing. The earlier regression where subsequent same-boot runs took 65s (and lost XInput visibility for the XUSB-companion path) came from reconnecting to a half-removed software device; the handle-lifetime teardown removed the cause, and since v1.8.0 every life reuses one fixed instance name. See Techniques: Stable Device Identity.

**Per-step install breakdown** (visible in stdout when `HMContext.InstallDriver` runs): extract ~20ms · remove old packages ~100ms · sign ~130ms · generate catalogs ~840ms (the largest single step, AV-sensitive) · install drivers ~580ms · total ~1.7s on a clean machine. On corporate workstations with hundreds of devices in the PnP tree, total install can stretch to 5-20s; HIDMaestro doesn't run `pnputil /scan-devices` (it's a no-op for our INFs and was the largest variable contributor).

**Batch teardown:** `HMContext.Dispose()` and the public `DisposeControllersInParallel(controllers, perControllerCallback)` parallelize per-controller `DIF_REMOVE` work and run the system-wide HID orphan sweep once at the end instead of per-controller. With v1.3.1's SwD-first ordering the per-controller cost is already ~135–500ms, so the batch path's wall-clock benefit is now mostly avoiding the per-controller orphan-sweep duplication; for 4-6 mixed controllers the cleanup typically completes in 1.5-4s end to end. Live profile-switch (single `HMController.Dispose()` mid-session) stays synchronous because slot-allocation determinism requires the old devnode fully gone before the new one is created.

**Self-healing on init:** `HMContext.InstallDriver` calls `RemoveAllVirtualControllers` first thing, so any orphans left by a prior crashed session are cleaned up before the new install runs. The same call is exposed publicly as `HMContext.RemoveAllVirtualControllers()` for consumers who want explicit defensive cleanup (e.g. on app exit). In normal operation, individual `HMController.Dispose()` is sufficient: there is no per-process cleanup obligation on shutdown.

### Profile Architecture Groups and Teardown Timing

Disposal speed depends on which kernel-side drivers are in the device stack. Each additional driver in the stack adds its own PnP query-remove handshake, handle release, and notification cascade. HIDMaestro profiles fall into three architecture groups with dramatically different teardown characteristics:

#### 1. Plain HID: generic gamepads, wheels, HOTAS, flight sticks (~200ms)

Profiles where `driverMode` is not `"xinputhid"` and the VID is not Microsoft (`0x045E`). Includes DualSense, DualShock 4, all Logitech wheels, Thrustmaster HOTAS, flight sticks, pedals, arcade sticks, and most of the 231-profile catalog.

```
ROOT\HIDClass\HM_0000               ← our UMDF2 driver (mshidumdf host)
  └─ HID\HIDCLASS\1&hash&0&0000     ← raw HID PDO, no upper filter; the
                                       prefix is the identity's, written
                                       before registration
```

**Lightest stack.** One `DIF_REMOVE` on the ROOT parent tears down the entire tree. No XUSB companion device, no Microsoft upper filter. Creation ~200ms, disposal ~80ms.

#### 2. Non-xinputhid Xbox: Xbox 360 Wired (~135ms post-v1.3.1)

Xbox-VID profiles (`0x045E`) where xinputhid is not in the path. XInput is delivered via a separate SWD-enumerated XUSB companion device running `HMXInput.dll`. WGI dispatch also runs through that companion, admitted by the xinputhid UpperFilter tripwire described in Techniques.

```
ROOT\VID_045E&PID_028E&IG_00\HM_0000 ← our UMDF2 driver (main HID device)
  │                                    UpperFilters += "xinputhid" per-instance
  │                                    (in the SetupDi property state BEFORE
  │                                    DIF_REGISTERDEVICE, plus one deliberate
  │                                    devnode restart after the companion
  │                                    exists: WGI only honors the tripwire on
  │                                    a RE-arrival, never at first arrival,
  │                                    measured both ways on 26200 - issue #59)
  │                                    (SDK-written; blocks WGI from building
  │                                    a second HID-backed Gamepad for this
  │                                    logical controller)
  └─ HID\VID_045E&PID_028E&IG_00\... ← HID child (raw PDO, input.inf)
SWD\HIDMAESTRO\HM_0000               ← XUSB companion (HMXInput.dll)
  │                                    SwDeviceCreate, System class, explicit
  │                                    per-controller ContainerID (shared with
  │                                    main HID for xinput1_4 dedup).
  │                                    UpperFilters = "xinputhid" from INF
  │                                    (admits the companion to WGI's XUSB
  │                                    dispatch; xinputhid.sys does not
  │                                    actually attach, wrong device class).
  │                                    `HM_0000` = the identity token, the
  │                                    same on every life of this controller.
  └─ XUSB interface → XInput slot + WGI Gamepad (one entry, live input +
                                     working put_Vibration on Chromium)
```

**Medium stack, fast on both sides post-v1.3.2.** Two device trees to tear down. The XUSB companion runs its own WUDFHost instance hosting `HMXInput.dll`, which needs its own PnP release cycle. v1.3.1's SwD-first ordering brought disposal to ~135 ms (down from ~5,700 ms). v1.3.2's `WaitForXInputSlotClaim` 500 ms cap brought worst-case creation to ~700 ms (typical ~200 ms). Round-trip create + dispose is ~350-850 ms.

#### 3. xinputhid Xbox: Xbox Series X|S Bluetooth (~500ms post-v1.3.1)

Profiles with `driverMode: "xinputhid"`. These match `xinputhid.inf [GIP_Hid]` by hardware ID (`HID\VID_045E&PID_0B13&IG_00`), which binds Microsoft's `xinputhid.sys` as an upper filter on the HID child. xinputhid provides XInput delivery + 16-button descriptor synthesis natively: no XUSB companion needed, single Device Manager entry.

```
SWD\HIDMAESTRO_VID_045E_PID_0B13&IG_00\HM_0000
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

v1.3.2 fixes the *creation* side too. `SetupController` runs three wait budgets after `CreateGamepadCompanion`: `WaitForHidChild` (10 s), `WaitForDeviceStarted` (5 s), and `WaitForXInputSlotClaim` (15 s pre-v1.3.2, **500 ms** post). The slot-claim wait was the dominant cost: distribution is bimodal (xinputhid publishes the slot in <100 ms when healthy, never publishes when xinputhid's allocator is in a stuck state: kernel state issue, prior-session residue), so the prior 15 s budget burned the full duration on every stuck case. PadForge users observed 13-14 s freezes on a single Xbox Series BT create when this hit. The 500 ms cap sits ~5x above the slowest observed healthy claim (giving slow-but-working cases full headroom) and degrades the stuck case to a near-imperceptible pause. Controller stays functional via DI/HIDAPI/Browser/WGI when XInput doesn't pick it up; XInput consumers see the slot appear lazily on their next poll cycle. Creation latency for Xbox Series BT is ~150 ms healthy / ~600 ms worst case post-fix.

#### SwD-first removal ordering (v1.3.1)

Two of the three architecture groups (Xbox 360 Wired and Xbox Series BT) own a SwDevice-enumerated parent. SwDevice lifetimes are anchored to the HSWDEVICE handle, not the PnP devnode: children of a SwD parent cannot fully unwind their query-remove cascade until the parent's handle drops its kernel refcount. Pre-v1.3.1, `DeviceManager.RemoveDevice` issued `DIF_REMOVE` on every HID child first (each followed by a 2,000ms `WaitForDeviceRemoval` that timed out because the parent was still holding the lifetime lock), then closed the SwDevice handle. Net cost: ~5,700ms for Xbox 360 Wired, ~11,000ms for Xbox Series BT, scaling worse with more children.

v1.3.1 inverts the order: for any `SWD\` parent, close the SwDevice handle FIRST via `SwdDeviceFactory.Remove`, block on `CM_NOTIFY_ACTION_DEVICEINSTANCEREMOVED` for the parent (so callers know the kernel has actually propagated removal, not just that the handle closed), then mop up any HID children that survived the cascade: usually none, because the SwD parent's release fires its children's removal in one cascade.

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

The `A7B4` prefix on the companion's instance-id suffix in this capture is the per-process session id that releases v1.1.30 through v1.7.3 used; since v1.8.0 the suffix is the identity token. See Techniques: Stable Device Identity.
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

Only one device interface is registered on the XUSB companion. Publishing a second interface would create a duplicate WGI provider arrival and classifier confusion: the tripwire plus the single XUSB registration is what produces exactly one Gamepad.
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
| **WinExInput** | Windows Extended Input. A device interface GUID (`{6C53D5FD-...}`) HIDMaestro no longer registers anywhere. Older builds put it on HID parents and the SDK still sweeps those entries away. Ghidra decomp of `Windows.Gaming.Input.dll` (Win11 26200) found zero references to this GUID; it is not actually WGI's `GamepadAdded` source. WGI admission comes from the HIDClass pass-list (plain HID profiles) or the xinputhid UpperFilter tripwire (Xbox XUSB-companion profiles). |
| **xinputhid UpperFilter tripwire** | Registry string `"xinputhid"` written to a device's `DEVPKEY_Device_UpperFilters` (via INF HKR AddReg or SetupAPI) to satisfy WGI's `IsDeviceOrAncestorFilteredBy` wstring compare. Does not load `xinputhid.sys`: the filter only attaches to HID-class devices. Admits a System-class device (the XUSB companion at `SWD\HIDMAESTRO`) to WGI's XUSB dispatch path. See Techniques. |
| **XUSB Companion** | A separate UMDF2 device (`HMXInput.dll`) that handles XUSB IOCTLs for XInput. Lives at `SWD\HIDMAESTRO\<token>`. Needed because `mshidumdf` suppresses XUSB on HID devices. |
| **SWD enumerator** | "Software-device" PnP enumerator. Devices created via `SwDeviceCreate` (cfgmgr32) appear under `HKLM\SYSTEM\CurrentControlSet\Enum\SWD\<enumerator>\<instance>`. The SwDevice API lets us specify an explicit non-sentinel `pContainerId`, which is the linchpin of the slot-1-skip fix. |
| **Identity token** | The instance-name segment every devnode of one virtual controller carries: `HM_0000` for the default key of index 0, `HM_` plus sixteen hex digits for a consumer key. Fixed across lives so the HID child keeps its path. Replaced the per-process session-id prefix in v1.8.0. See Techniques: Stable Device Identity. |
| **ParentIdPrefix** | The `level&hash&n` value in a parent's instance key that PnP prepends to the instance id of every child that reports a non-unique id (HID children do). Read from the key when the child first arrives, minted with a fresh counter only when absent. HIDMaestro writes the identity's value so the child path is the same on every life. |
| **ContainerID slot-1 skip** | Pre-fix bug in `xinput1_4!FUN_18000de2c`: a null-sentinel ContainerID `{00000000-...-FFFF-FFFFFFFFFFFF}` triggered a code path that set bit 2 on the device struct, made the fallback slot allocator skip iter 0, and surfaced an empty slot 1 to consumers. The SWD migration's explicit `pContainerId` closes the path. |
| **GameInput mapping** | Registry entries at `HKLM\...\GameInput\Devices\{VID}{PID}...` that tell WGI how to map HID axes/buttons to the Gamepad interface. |
| **&IG_** | "Interface Group" marker in Xbox device paths. Chrome and HIDAPI skip devices with this in the path; SDL3 falls through to its RawInput backend. |
| **Vx/Vy** | HID velocity usages (0x40/0x41). Invisible to DirectInput's axis mapper but enumerated by GameInput; used to carry separate trigger values. |
| **mshidumdf** | Microsoft's kernel-mode HID minidriver proxy that hosts UMDF2 HID drivers. |
