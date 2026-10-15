# HIDMaestro Internals

Technical reference for HIDMaestro. The [README](../README.md) covers what HIDMaestro is and how to use it. This document covers how it works: the descriptor and enumeration techniques, the device topology, the user-mode rationale, the validation methodology, and the timing characteristics.

For the per-release decision log and investigation notes, see [docs/investigations/](investigations/).

---

## Techniques

A few HIDMaestro techniques that are not well documented elsewhere in the virtual controller space.

### Velocity Usage Descriptor Trick

Real Xbox 360 controllers have a combined trigger axis (Z) in DirectInput: both triggers share one axis. Browsers and WGI need separate trigger values. Previous solutions had to choose: correct DI (5 axes, combined) or correct browser (separate triggers, 6 axes).

HIDMaestro uses HID velocity usages (Vx and Vy, Usage Page 0x01, Usages 0x40/0x41) to carry separate trigger values in the same HID report. DirectInput does not map velocity usages to any axis slot, so it sees 5 axes, while a reader of the HID report gets each trigger on its own. WGI reads this profile through its XUSB companion, which reports the two triggers as separate axes.

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

### GameInput Mappings

WGI makes a HID device a `Gamepad` through a mapping keyed by VID and PID. Windows ships its own under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\GameInput\Devices` (279 on Windows 11 26200), and a key at `HKLM\SYSTEM\CurrentControlSet\Control\GameInput\Devices\<VID><PID>00010005` takes priority over them. A mapping names each axis and button by its position in the order WGI enumerates them, so it reads correctly only against the descriptor it was written for.

Through v1.11.0 the SDK wrote one table for every profile Windows had no mapping for, and deleted Windows' entry for the profiles it had one for. The table fit one shape: the `HidDescriptorBuilder` game pad, with X and Y, Z and Rz, Rx, Ry and a hat in that order and ten or more buttons. Measured across the catalog, it made 25 non-Xbox profiles WGI Gamepads read through the wrong axes, 21 of them wrong even at rest: a resting DualSense read RightTrigger 0.498 and RightThumbstickY 1.000 (foundation-sunshine #1056). It also hid Windows' own DualShock 4 v1 mapping. For Xbox profiles it changed nothing WGI read, because WGI reads them through XUSB or xinputhid.

Since v1.12.0 the SDK writes a table only for a profile built in code whose descriptor has exactly that shape, and only when neither Windows nor another program has a mapping for its VID and PID. Catalog profiles get none, the same as the real devices: WGI lists a virtual DualSense as a RawGameController, as it lists a real one, and reads a virtual DualShock 4 v1 as a Gamepad through Windows' own mapping. The table goes when the last controller holding it does. Tables an older SDK left behind are recognized by their exact bytes, and `RemoveAllVirtualControllers` deletes them. Battery scenario S67 reads each of these cases through WGI with an XUSB pad as the positive control.

### Registry Keys Shared With Real Devices

Two keys a virtual controller writes are named by VID and PID, so a real device with the same ids uses the same key: the GameInput mapping above, and joy.cpl's OEM key `HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_xxxx&PID_xxxx`, whose `OEMName` is the name joy.cpl and DirectInput show. Through v1.11.0 the SDK overwrote `OEMName`, deleted `OEMData` and the `Axes` subkey, and left them that way. Its cleanup deleted every OEM key under the Microsoft, Sony and VID 0000 prefixes in HKLM and HKCU and cleared the HKCU joystick slots, so a real pad that shared an identity lost its joy.cpl data (foundation-sunshine #1138). A vendor prefix is not proof of ownership.

Every such write now goes through a ledger at `HKLM\SOFTWARE\HIDMaestroRegistryLedger`. Before writing, the SDK records the key's prior state and the live controllers that hold it, each named by process id, process start time and controller index. When the last holder goes, the prior state comes back, and only while the key still holds what HIDMaestro wrote: a value another program changed in the meantime stays. A key HIDMaestro created is deleted only if nothing else was written under it. A holder whose process has exited counts as released, so a crashed consumer's writes are undone by the next session's first create. The ledger and `HMOemNameOverride` share one mutex and read each other's records, so `OEMName` ends at its original value in any order of claim and release. The `OEMData` value and hidden fifth axis that builds before April 6, 2026 wrote are removed where their bytes match exactly, and nowhere else. Keyboard and mouse profiles write neither key.

The device sweep's ownership guard (v1.3.16, issue #28) now covers the `Device Parameters` cleanup and the interface registrations under `DeviceClasses` as well. An entry under an enumerator real devices share (`VID_`, `XnaComposite`, `HID_IG_00`) goes only when its device is provably HIDMaestro's or has no instance record left. Battery scenario S66 plants keys for real VID and PID pairs in both hives, creates and disposes virtuals over them, runs the full cleanup, starts a fresh context, recovers from a killed consumer, and compares every value.

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

### Keyboards and Mice

A profile whose `type` is `keyboard` or `mouse` is created like any other plain HID profile and reaches Windows as the device its descriptor describes. Windows' keyboard and mouse class drivers open its collections, Raw Input lists them, and the desktop acts on the input. The catalog holds 51 keyboards and 52 mice. Each is the report descriptor of one interface of a real device, taken from public dumps: usbhid-dump output posted in project issues, libinput recordings and the Linux kernel's HID selftests. Two Bluetooth keyboard dumps end with a zero byte after the last End Collection, which Linux ignores and Windows' HID parser rejects, so those two profiles leave it out.

`SubmitMouse`, `SubmitKeyboard` and `SubmitConsumer` pack into the profile's own reports. The SDK reads the descriptor with a parser that follows the HID 1.11 item rules (section 6.2.2): global items persist and stack with Push and Pop, local items reset after each main item, a 4-byte usage carries its own page, and each report ID counts its bits from zero. Every field lands where the descriptor puts it, so a mouse whose buttons and motion sit in separate reports gets both. A relative movement larger than the descriptor's field is split across as many reports as it takes. `HidDescriptorBuilder` has starters for building one in code: `Mouse` (relative 16-bit, or absolute 0 to 32767), `Keyboard` (the boot layout), `KeyboardNkro`, `ConsumerControl` and `VendorButtons`. The packer is built once per profile and shared, and each controller packs into buffers of its own under its submit lock, so a keyboard or mouse submit allocates nothing (battery S84). Through v1.12.0 each one allocated 539 bytes for a keyboard report and 248 for a mouse report.

A relative field whose range excludes zero, such as the relative X and Y of `corsair-k70-rgb-mk2-low-profile` (1 to 32767), cannot carry a click's zero or a movement left or up. The packer writes 0, no motion, for a movement the field cannot carry at all, and owes nothing for it or for a part that overshoots, so every split of a large movement ends. After a submit has sent a long split, the controller keeps 16 frames (`KeyboardMousePacker.MotionFramesKept`) and lets the rest go. Fields whose range holds zero pack the same bytes as before. The release frame is rewritten in place.

RAWMOUSE carries five buttons. A mouse that needs more adds a second, vendor-page collection with `VendorButtons`, and `SubmitMouse` sends buttons six and up there. Raw Input lists that collection as a HID device of its own. An absolute mouse reaches Raw Input flagged `MOUSE_MOVE_ABSOLUTE`, on Windows' 0 to 65535 scale.

The input section holds one report, the newest. That suits a gamepad, whose state replaces the last, and loses data for a mouse, whose movement adds up, and for a keyboard, whose make and break must both arrive. For keyboards and mice the driver acknowledges delivery. When HID class sends its next read after one completed with a report, the driver writes that report's sequence number to `Global\HIDMaestroInputAck<N>` and signals `Global\HIDMaestroInputAckEvent<N>`. HID class sends that next read only after its completion routine has put the report in its buffer, so the report after it cannot overtake it. Acknowledging when the read completed let that happen on a machine with every core busy: with four mice each sending 500 reports at 1,000 a second, adjacent reports of one mouse swapped in 24 of 800 runs, which on a keyboard can leave a key down. Acknowledged on the next read, none of 800 swapped and none lost a report. The driver records the acknowledgement it owes before it completes the read, because HID class can send the next read the moment the first completes. Recorded after, that read can arrive first and find nothing to acknowledge, and the submit call then waits out its full 200 ms. On a busy machine that happened in nearly every run. The SDK creates both objects, since the driver runs as LocalService and cannot create a Global object, and the driver opens them when its worker starts. Before writing a report, each submit method waits for the previous report's acknowledgement and keeps reports at least 1 ms apart, the interval of a full-speed USB device polled every frame. HID class keeps a short queue for each reader. Without the spacing, a burst of 600 one-count moves went out at 27,000 reports a second and Windows dropped counts. With it, the burst takes 601 ms and every count arrives, and four threads driving four mice at 250 Hz lose nothing. A wait gives up after 200 ms. After three misses in a row the controller stops waiting until an acknowledgement arrives again, so a device Windows has stopped reading does not hold up the caller. `DeliversEveryReport` is false on a gamepad and on a driver older than v1.12.0. Windows merges consecutive mouse moves into one WM_INPUT message (RAWMOUSE has `MOUSE_MOVE_NOCOALESCE` for that reason), so the checks compare the sum of movement, not the number of messages.

Windows sends a keyboard its LED state as an output report when the keyboard arrives and on every change, and resends it to the keyboards already present when another one arrives. `KeyboardLedsChanged` raises the decoded state and `KeyboardLeds` keeps the last one. The arrival report comes while `CreateController` is still running, before a handler can be attached. Through v1.11.0 the output reader started at the ring position it found when the controller object was built, after the device had started, so the arrival report went unread, and so did anything else a host sent during creation, such as the feature reads a Sony Bluetooth host makes on arrival, which arm the extended report. Each controller's setup now records the ring position before the device exists, and the reader starts there.

A consumer that goes away with a key or button held would leave it held. Windows repeats a held key about 30 times a second until the device is removed, measured at 32 repeats in 1.5 s after a consumer was killed with F24 down, and a held Shift or Ctrl stays held for the whole desktop. ViGEmBus unplugs a client's devices from its file-close callback, which Windows calls when the client's process exits or closes its handle (`sys/Driver.cpp`, `Bus_FileClose`). A user-mode driver gets no such callback, so the SDK holds the server end of a named pipe for each controller, `\\.\pipe\HIDMaestroOwner<N>`, and the driver connects and keeps one read pending on it. Nothing is ever written to the pipe, so the read completes only when the SDK's end closes, on Dispose or when the process ends however it ends. The driver then sends the release reports the SDK keeps in the acknowledgement section, one for each input report the controller has sent: keys and buttons up, relative motion and the wheel zero, array slots empty, a hat centered, and absolute pointer axes where they were, so the pointer does not jump. Dispose sends the same reports itself before teardown. The device then stays until the next session's cleanup removes it, as an orphaned gamepad does. A keyboard leaves by a different route. Windows will not disable a keyboard, and while one is up it will not disable the device above it either, so the disable and uninstall the cleanup uses on every other device fail on both, as do pnputil and devcon on the root (`ERROR_NOT_DISABLEABLE`). `SetupDiRemoveDevice` still removes the keyboard itself, and the root can be disabled once it is gone. The cleanup removes any device that refuses the disable that way, children first, the order Dispose uses. `ReleasesOnConsumerExit` says whether the driver watches.

A keyboard without report IDs receives its LED report as the LED byte alone. Both backends split a report at its first byte (`HMOutputPacket.ReportId`), so on such a keyboard `ReportId` holds the LED byte and `Data` is empty. The LED decoder puts the two back together.

Four mice with one VID and PID are four Raw Input devices with four paths, and each identity key gets its own path back on every life (see Stable Device Identity Across Lives). Keyboard and mouse profiles get no GameInput mapping and no joy.cpl OEM name. Battery scenario S69 checks the parser, the starters and the packers with no device, and S70 checks the rest through Raw Input, including a consumer killed while it holds a key, a mouse button and an absolute pointer, and the cleanup that removes the devices it left.

The driver also no longer completes a second read with a synthetic report 0x20 built from the trigger values. That path was left from an April 2026 experiment with a second collection for Xbox 360 triggers. It fired on a byte scan for `85 20` in descriptors longer than 130 bytes, which matched only the DualSense family, where 0x20 is a feature report, so the five driver-backed DualSense profiles had an input report their descriptor does not declare put in their read queue on every frame.

### Programs Side by Side

Several programs that embed HIDMaestro share one machine. Through v1.12.0 every machine-wide name was keyed by a controller index that each program numbered for itself, so two programs that each created a first controller wrote the same sections and registry keys, and a driver deploy or sweep in one removed what the other used. The design note, [docs/design/v1.13.0.md](design/v1.13.0.md), has the measurements, the rejected alternatives and the sources behind each choice below.

#### Ownership Claims

A process takes one ownership claim before its first machine-wide write and holds it until it exits. The claim is a named pipe created with `FILE_FLAG_FIRST_PIPE_INSTANCE` and a maximum of one instance. Claiming is the check: the second claimant learns it lost from the same call that would have won. On the devbox it fails with `ERROR_PIPE_BUSY`, because the instance limit is checked before the first-instance flag, and either error means the claim is held. Windows deletes the pipe when the last handle closes, which happens when the process ends however it ends, and a pipe handle has no thread affinity.

Liveness is a `WaitNamedPipe` probe with a 1 ms timeout. A missing pipe fails with `ERROR_FILE_NOT_FOUND` in under 5 µs. A true return and `ERROR_SEM_TIMEOUT` both mean a live holder, and any other error counts as live. The probe opens no handle, so probing cannot keep a claim alive. A named event judged by `ERROR_ALREADY_EXISTS` was the other candidate and lost on one point: a sweep that probes an event holds it open for the length of the probe, and a claimant arriving in that window reads a live owner where there is none.

The claim pipes carry `D:P(A;;0x00100080;;;SY)(A;;0x00100080;;;BA)(A;;0x00100080;;;LS)`, SYNCHRONIZE and FILE_READ_ATTRIBUTES for SYSTEM, Administrators and LocalService only. A non-elevated process can still probe a claim, and its attempts to open the pipe or take the claim fail with `ERROR_ACCESS_DENIED`. Nothing can read or write a claim, and the driver host never opens one. It keeps opening only the owner pipe it already uses.

| Claim | Pipe name | Held for |
|-------|-----------|----------|
| Owner | `\\.\pipe\HIDMaestro.Owner.<owner id>` | the process, from its first machine-wide write |
| Transport index | `\\.\pipe\HIDMaestro.Index.<N>` | the controller's life |
| Identity | `\\.\pipe\HIDMaestro.Identity.<token>` | the controller's life |

The owner id is a GUID the process draws once. Beside the owner claim sits a record at `HKLM\SOFTWARE\HIDMaestroOwners\<owner id>`, readable by everyone: process id, image path, start time, SDK version, USB/IP server port, the name of the staging directory the process uses, and one subkey per live controller with its identity token, transport index, backend, profile id, devnode instance id and vhci port. The record is written with the owner claim and stays until the process exits or is killed, so a program between its `InstallDriver` and its first create is as visible as one with controllers. A process that cannot write HKLM, one that is not elevated, holds the claim and writes the record at its first call that can. A controller's entry is written before any PnP work and removed once its devices are gone. A record whose owner claim no longer exists describes a dead owner, which is what the sweeps act on. The process id and start time are for people reading the record. Nothing decides liveness from them.

#### Transport Index and Slot

`CreateController` numbers controllers by slot within a context. Each controller also holds a machine-wide transport index, which names every section, event, pipe and registry key and is the `ControllerIndex` the driver reads. `HMController.TransportIndex` and `HMController.SectionNames` expose it. The transport index is the slot when that number is free machine-wide and the lowest free number otherwise, so a program running alone gets the numbers it always got and native writers that compute names from the index see no change. Two programs that each create a first controller coexist, the second on index 1. An index N is free when all of these hold:

- its claim `\\.\pipe\HIDMaestro.Index.<N>` can be created,
- no v1.12.0 program holds `\\.\pipe\HIDMaestroOwner<N>`,
- no present HIDMaestro devnode records N as its `ControllerIndex`, unless the devnode carries the identity this create is about to reclaim,
- no USB/IP import uses it whose server port no record names. Such an import belongs to a program on an older SDK, whose composite has no devnode to record the index, and its bus id `1-<N+1>` carries it.

Whether the input section exists is no evidence either way. After an Xbox 360 controller's Dispose returns, `Global\HIDMaestroInput<N>` stays open for as long as the disposing program lives, on v1.12.0 as on this release: the SDK's own XInput calls during the create leave xinput1_4 holding a handle to the XUSB companion, which keeps the removed companion's driver host and its view of the section alive until the program polls XInput again or exits. An allocator that skipped indices with an open section would move a program off its index after such a dispose. A v1.12.0 program in the middle of creating its sections is a race this release does not close. The owner pipe's first instance is created with the first-instance flag and v1.12.0's access mode, type, instance count and timeout, which all instances of one pipe must share, so a v1.12.0 program that starts on the index between the check and the create makes the create fail with `HMResourceInUseException` rather than share the sections.

A keyless controller's default identity derives from its transport index, so when another program holds index 0 it lands on another index and another identity. Durable device paths need an identity key, the contract since v1.8.0.

#### Identity Claims

The identity claim is taken before any PnP work. A program asking for an identity a live program holds gets `HMResourceInUseException`, derived from `InvalidOperationException`, whose message and `OwnerProcessId`, `OwnerImagePath` and `OwnerSdkVersion` name the holder from its record. The holder's devices see no PnP activity. The same check applies across contexts inside one process. A v1.12.0 program takes no identity claim, so when a present devnode carries the token and no record lists it, the create probes the owner pipe of the devnode's `ControllerIndex`, and a held pipe refuses the create with the same exception.

`ClearParentIdPrefixRivals` keeps the 0xCA invariant, never two parents with one ParentIdPrefix. A present rival is removed only when its owner is provably dead. When the owner is running, here or in another program, or cannot be known, the create is refused with `HMResourceInUseException` and the message names the rival devnode. When the owner cannot be known, it also names the cleanup call that removes the device. A rival that is not present is removed as before, because a ROOT record re-enumerates at boot.

#### Census and Sweeps

A sweep removes a present device only on proof that its owner is gone. The census (`OwnerCensus`) groups what it knows about each present HIDMaestro devnode by identity token:

| Present device | Owner | Action |
|----------------|-------|--------|
| In a record whose owner claim is gone | dead | removed with everything the record lists, unless a running program holds the device's index or identity |
| In a live record | running | left alone, the caller's own included |
| In no record, `HIDMaestroOwner<N>` held | a running v1.12.0 program | left alone |
| In no record, no owner pipe, `Global\HIDMaestroInputAck<N>` exists | a v1.12.0 program that has exited | removed |
| In no record, no owner pipe, no InputAck section | a program older than v1.12.0, state unknown | left alone |

Only a v1.12.0 or later SDK creates `HIDMaestroInputAck<N>`, and the driver keeps it open for as long as the device exists. A device with the section and without the pipe therefore belongs to a v1.12.0 program that is gone. A device with neither came from an older SDK whose program leaves no sign of whether it still runs. No sweep without a scope removes it. The uninstall-grade cleanup does, and so does a create that asks for the device's own identity key, because that program is asking for the device back. A live record wins over a dead one. The census binds a present device to the record that lists its identity token or its transport index and takes a live record before a dead one. A dead record names a device only while no running program holds the device's index or identity.

The census reads the instances under the ROOT and SWD keys of `SYSTEM\CurrentControlSet\Enum` that HIDMaestro's enumerators (`HIDMAESTRO*`, `HMCOMPANION*`) or the shared ones (`VID_*`, `XnaComposite`, `HID_IG_00`, `HIDCLASS`, `SYSTEM`) hold, and checks presence with `CM_Locate_DevNode` for HIDMaestro-owned instances only, which finds a present devnode and no absent one. A shared enumerator's instance counts when its hardware ids name HIDMaestro. Past 64 candidate instances, or when a key cannot be read, it goes back to `CM_Get_Device_ID_List` with `CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT` for the two roots, whose cost does not grow with absent devnodes (b470c49). Claiming ownership costs a create 0.57 ms at p50 and 0.66 ms at p95, census included, and its release costs the dispose 0.24 ms. With no dead record and no present device of a dead program, nothing walks the enumerators. The phantom-record cleanup runs on a background thread once per process, started by the first of `InstallDriver`, the first create and `RemoveAllVirtualControllers()`. The call that starts it returns without waiting for it to end, and a later call removes no phantom. The phantom cleanup takes `OwnerSweep.CreateGate` for writing and every create takes it for reading, so it never runs during one of this process's creates, and a create and the uninstall-grade cleanup wait while it runs: one pass over the phantoms, their uninstalls, at most 5 seconds of waiting in all, for the machine-wide PnP lock before each uninstall and for Windows to let the phantom go after it, and two passes over interface registrations that do not wait. A phantom whose identity claim another program holds is that program's create in flight and is skipped. When the process's first claim is taken inside the program's own `ProcessExit` handler, the SDK's exit handler is registered too late to run, so a thread would end with the process, possibly inside an uninstall. `Environment.HasShutdownStarted` returns false on .NET 10, but the runtime raises `AssemblyLoadContext.Default.Unloading` before `ProcessExit` on .NET and NativeAOT (`AppContext.OnProcessExit`), and the SDK's module initializer subscribes to it (`ProcessExitSignal`). Once it has fired, the first call runs the cleanup on the calling thread before it returns, waiting at most `ExitWaitMs` for the gate. Under NativeAOT module initializers run at startup, so the signal always holds. Under .NET the initializer runs when code that refers to the SDK is first compiled, or first run where it was compiled ahead of time, so the signal misses a program in which no such code ran or was compiled before exit began, such as one that uses the SDK only inside its own exit handler, and that cleanup ends with the process. The next process's first cleanup removes what is left. The uninstall-grade cleanup takes `OwnerSweep.CreateGate` for writing before it acts, so it never runs beside the phantom cleanup or one of this process's creates. The reclaim of exited programs runs before a create's lease on both backends, once per process, so a keyless program restarted after a crash gets its index and identity back.

USB/IP imports are matched by server port. A server records its port in its owner's record. An import whose server port has no listener on loopback, read with `IPGlobalProperties.GetActiveTcpListeners`, belongs to a dead server and is detached, and the re-attach attempts against that port are canceled. An import on this process's own port that none of its controllers made was left by the program that held the port before, and is detached.

The public cleanup keeps its overloads and acts on the devices of exited programs: `RemoveAllVirtualControllers()` and `RemoveAllVirtualControllers(bool preserveInstall)`. A running program's controllers, the caller's own included, stay. Package removal (`preserveInstall: false`) happens only while no other program runs and no device remains, and it takes the deploy lock. `RemoveAllVirtualControllers(HMCleanupScope.Everything, bool)` removes every HIDMaestro device and import regardless of owner, which `HIDMaestroTest cleanup` and the battery use. A program that exits without disposing has its devices removed by the SDK's own `ProcessExit` handler, registered with the owner claim. The handler waits at most 10 seconds for the phantom cleanup, which starts no further uninstall once the handler runs and whose own waits stop at 5 seconds, and for a create in flight. Past that it removes nothing and keeps the record, and the devices stay present until a program built on this release next sweeps: a create still holding on at the bound, one phantom uninstall longer than it, or, under .NET, where the handler runs on the finalizer thread, an `Environment.Exit` from a thread inside a create. Otherwise it removes the devices to the end on its own thread, each removal within its own bound, as `Dispose` does, and a device it cannot confirm gone keeps the record the same way. A context dropped without `Dispose` keeps its controllers, their indices and their identity keys, through a static set of leases, until the process exits, and a create of one of those keys in that process is refused until then. A killed program runs no handler, and its devices go with the next sweep of any program, through its record.

#### Driver Deployment

Every driver and transport deploy holds the machine-wide mutex `Global\HIDMaestro.Deploy` for one call. One thread acquires and releases it inside the call, which is what a named mutex needs. An abandoned mutex is acquired and the deploy reads the installed state again before acting. The same-version fast path stays outside the mutex: the manifest hash in `SOFTWARE\HIDMaestro` and the driver store check. When it misses, the deploy takes the mutex and checks again, so of two programs calling `InstallDriver` at once one deploys and the other returns `AlreadyCurrent`.

The installed version is the newest `DriverVer` among the `hidmaestro.inf` packages in the driver store. Two stamps compare by release version, then build date, then build number. `HMContext.LastDriverInstall` reports the outcome:

| Installed driver | Outcome |
|------------------|---------|
| none | `Installed` |
| this SDK's payload | `AlreadyCurrent` |
| this SDK's stamp, record of it missing | `AlreadyCurrent`, the record is restored, nothing is reinstalled under running devices |
| newer | `KeptNewer`, never downgraded |
| older, no present device of any program on it | `Upgraded` |
| older, devices of running programs or of unknown programs on it, v1.12.0 or later | `KeptInUse` |
| older, held the same way, older than v1.12.0 | `HMDriverVersionException` naming the devices and their programs |

`HMDriverVersionException` carries `InstalledVersion`, `MinimumVersion` and `Blockers`. A deploy counts the UMDF2 controller entries of running programs as holders of the driver whether or not their devices are present yet, so it cannot upgrade the driver under a create that has recorded its entry and not yet done its PnP work.

Every UMDF2 create passes a deploy gate after its record entry and before its PnP work (`DriverDeploy.EnsureForCreate`, called from `SetupController`). Under the deploy lock it reads the installed-payload record. When the record is the one this process last confirmed, the gate returns. Otherwise it checks that the driver store holds a package for each of `hidmaestro.inf` and `hidmaestro_xusb.inf`, of any stamp. When one is absent it runs the install policy above, and the create fails if the store still lacks one afterward. The policy adds without removing in two cases. At the installed driver's own stamp it adds whichever of this build's two packages is missing, signed and catalogued as a full deploy does it. With no main package and devices of running programs on a lone XUSB package it adds only what this build lacks, because a full deploy would remove that package and its hosts under them. With nothing on the driver it deploys in full, and with an older driver that no device or pending create holds it upgrades the same way, both of which remove older packages first. A deploy cut off between the two adds, or a package removed since, no longer reads as `AlreadyCurrent`. A USB/IP composite create does not pass this gate. `UsbipBackend.CreateDevice` calls `UsbipDriverInstaller.EnsureInstalled`, which takes the same mutex for the usbip-win2 transport install. A deploy signs in a `package` staging directory of its own, fresh from the embedded bytes, and checks the binaries against their SHA-256 again just before signing.

A create of a plain HID or non-xinputhid controller then installs the driver on its own devnode with `DiInstallDevice` and no driver named (`DeviceNodeCreator.CreateDeviceNode`), which installs the best match the driver store holds on that device and no other. Through v1.12.0 it called `UpdateDriverForPlugAndPlayDevices` with the VID/PID hardware id and the SDK's INF, which installs the package on every present device with a matching id. That staged the SDK's package beside one the policy had kept, and with two programs of one profile it moved the older program's controller onto the newer driver.

#### Version Handshake

`Global\HIDMaestroVersion<N>` is a 64-byte per-controller section the SDK creates beside the input section and before the input event, the pattern v1.12.0 used for `InputAck<N>`. No existing layout changes. The layout is `HIDMAESTRO_VERSION_INFO` in `driver/driver.h`: the layout version (1), the oldest driver protocol the SDK runs on (2), the protocol the SDK speaks (3) and the SDK version, all written by the SDK. The driver writes its protocol (0 until written), its version and its capabilities, the InputAck bits, each time it opens the input section. It writes its version and capabilities first and its protocol last, with a barrier between. Protocol 1 is the layout set through v1.11.0, protocol 2 adds InputAck and the owner pipe (v1.12.0), and protocol 3 adds this section. A driver keeps every older layout, so an SDK runs on any driver at or above its oldest protocol. `HMController.DriverVersion` reads the driver's half, and native writers read both halves directly.

#### Staging

Everything the elevated SDK runs or hands to pnputil is staged per version and per operation under `%ProgramData%\HIDMaestro\Staging\<version>-<hash>\<operation>`, where the hash is the first 16 hex characters of the embedded driver payload's SHA-256. The operations are `driver` (the embedded driver binaries and both INFs, signtool and inf2cat), `package` (one deploy's signed and catalogued package), `swd` (hmswd.exe), `usbip` (the usbip-win2 installer), `usbip-ude` (the host controller package forced onto an older transport) and `winusb-<profile id>` (one profile's WinUSB package). One directory per operation keeps the catalog step's `/driver:` scan and its stale-catalog cleanup away from every other operation's files.

Every directory is created with its security descriptor in the same call, so none exists with an inherited ACL: `O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)` for the staging tree, and the same plus `(A;OICI;FRFX;;;BU)` on `%ProgramData%\HIDMaestro`, which also holds the OpenVR runtime SteamVR loads as the user. Each use checks every directory on the path from `%ProgramData%\HIDMaestro` down. A directory passes when it is not a reparse point, its owner is Administrators or SYSTEM, and no allow ACE grants any write-class right (write data, append data, write attributes or extended attributes, delete, delete child, change permissions, take ownership) to anyone else. A version or operation directory that fails is moved aside to `<name>.untrusted-<guid>` and created again. `%ProgramData%\HIDMaestro` and `Staging` are reset in place, because vrpathreg stores an absolute path into the first, and a reparse point at either is refused, since setting security on it would set it on its target.

SHA-256 is checked against the embedded bytes, or the pinned release hash for the usbip files, when a file is written, when an existing file is reused and immediately before use. A file is written to a temporary name, verified and renamed into place, and reuse trusts no file size. `hmswd.exe` always comes from the embedded resource, never from beside the assembly. Each elevated helper runs with its own staging directory as its working directory. Logs stay in `%TEMP%\HIDMaestro`, since nothing runs from there. A deploy deletes the staging directory of every version that no live owner record names, and every moved-aside directory. The same-version fast path prunes nothing, so a directory left by a version that was running during an upgrade goes at the next deploy.

#### Compatibility

This release judges a v1.12.0 program's liveness by its owner pipes, never removes its controllers while it runs, and runs on the v1.12.0 driver when that program's controllers hold it. The other direction cannot be fixed from here. A v1.12.0 program, and every older one, calls `RemoveAllVirtualControllers(preserveInstall: true)` inside its `InstallDriver`, which removes every HIDMaestro device on the machine, and replaces the installed driver with its own whenever the payload differs. Starting one while a v1.13.0 program runs removes the v1.13.0 program's controllers. A program older than v1.12.0 leaves no sign of whether it still runs, so its devices are never removed on a guess and they block a driver upgrade. When the installed driver predates v1.12.0 and such a device is present, `InstallDriver` throws `HMDriverVersionException` naming it.

### The usbip-win2 Install

The SDK runs the bundled installer only where no copy of usbip-win2 is installed, with the smallest set the installer allows: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /COMPONENTS=main,client /TASKS=vcredist /NOICONS`. The reasons come from the installer's script (`userspace/innosetup/setup.iss` at tag v.0.9.8.1), which matches the compiled scripts inside both pinned installers:

- `main` and `client` are `fixed`, so `/COMPONENTS` cannot deselect them. `client` is the drivers themselves, the filter installed with pnputil and the host controller installed by `devnode.exe`. The switch deselects `gui`, `sdk` and `pdb`, which the default `full` type installs.
- `/TASKS=vcredist` selects only that task. `devnode.exe` and `libusbip.dll` import `MSVCP140.dll` and `VCRUNTIME140.dll`, and the runtime installs before the driver steps, so it cannot be left out. `desktopicon` belongs to `gui`.
- `/NOICONS` is the only switch that drops the uninstall shortcut `main` creates.
- `/NORESTART` is required because the script sets `AlwaysRestart=yes`, and without it a very silent setup reboots without asking.

`usbip.exe`, `devnode.exe`, `libusbip.dll`, `resources.dll` and `Readme.md`, the uninstaller and its Apps entry, the scheduled task "USBip Detach All On Reboot Or Shutdown" and the VC++ runtime stay on the machine, because the installer has no command-line switch to leave them out. The script's `PrepareToInstall` runs the uninstaller of any copy whose record exists under `SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{199505b0-b93d-4521-a8c7-897818e0205a}_is1` and whose uninstaller file is still there, which removes the root-hub filter. The SDK reads the same record the way the script does, in the 64-bit view of HKLM, then the 32-bit view, then HKCU, with a quoted or unquoted `UninstallString`, and refuses to run the installer over it. It used to check only the driver store. The bundled install and the move of an older host controller to 0.9.8.1 take `Global\HIDMaestro.Deploy` and read the machine again under it, so two programs whose first composites arrive together find one host controller after one installer run.

### Controller Ports and Device Protocols

A profile can describe a device that carries several controllers in one report, and the commands the device answers, as data. Nothing in the SDK names the Wii U GameCube adapter, checks its VID and PID, or switches on its command bytes. A new device of the same form needs a profile and no code.

`extendedReport.ports` gives the report's port blocks:

| Key | Meaning |
|-----|---------|
| `count`, `offset`, `stride` | how many blocks, where port 1's starts (the report ID is byte 0) and how far apart they are |
| `status` | the block's status byte: its offset, the `connections` a port can hold with the bits each sets (the first is the empty port, the second the default), and device-wide `flags` every status byte carries while on |
| `data` | the block's data bytes, such as `"1-8"`. An empty port's are zero |
| `snapshots` | copies of a block the device keeps, taken `on: "connect"` from the first report in which the port holds something |
| `fields` | the block's fields in the `extendedReport` vocabulary, offsets relative to the block |

`extendedOutputReport.ports` decodes output per port the same way, with whole-number fields (`uint8`, `uint8-axis`, `uint8-trigger`, `uint16-le`, `int16-le`), and `OutputDecoded` carries each as `port1.rumble` and so on. `HMController.Ports` is an `HMPortSet`, empty for a profile with one controller. Each `HMPort` has a fixed `Number`, a `Connection` named by the profile, `Connect(name)` and `Disconnect()`, `SubmitState(in HMGamepadState)` through the port's fields, `SubmitRaw` for the data bytes exactly, `GetOutput(name)` and an `OutputChanged` event raised on the output thread. `Ports.Flags["name"]` sets a device-wide flag. `HMController.SubmitState` on such a device submits to port 1. A port call writes the whole report under the controller's submit lock with no system call or allocation added: the layout is compiled once and the encoder writes into buffers the port set owns. `HMButton` gains `LeftTriggerClick` and `RightTriggerClick`, a trigger's digital click apart from its analog travel, so `HMGamepadState` expresses the GameCube controller's analog L and R, its Z and its C-stick without a device-specific state type.

`HMPort.Connect(connection)` on an empty port writes the connection with sticks centered and triggers and buttons released, as the profile's fields encode an empty `HMGamepadState`. A host takes a controller's origin from its first report (Dolphin through `SetOrigin`) and SDL widens its calibration on it, so a first report of zeros would read a resting stick as pushed for the whole connection. `Connect(connection, in HMGamepadState)` sends the connection and an input state in one report, for a WaveBird or a controller plugged in with a button held, and throws `ArgumentException` for the empty connection.

`usbConfiguration.protocol` gives a USB/IP device named states, when its input endpoint answers and how often the device reads its controllers, command rules, a halt condition, and its answers to SET_PROTOCOL and GET_STATUS:

| Key | Meaning |
|-----|---------|
| `state` | named on/off states with initial values, restored at attach and at a port reset |
| `input` | the interrupt IN endpoint, the states under which a poll gets a report (`"polling"`, `"!stalled"`), and its `clock`: the device's own controller poll in ms and rules that change it while exactly the listed ports hold something |
| `commands` | the interrupt OUT endpoint, whether SET_REPORT(Output) is read the same way, and rules tried in order. A rule matches the bytes a packet starts with and its length, sets states, and queues a reply built from the state before the rule ran: constant bytes, a state as a byte, or a snapshot's bytes for every port, zeros unless its own condition holds |
| `halt` | the states under which the device answers nothing, and the status transfers fail with until a port reset |
| `setProtocol` | `"ack"`, or `"stall"`, the default |
| `getStatus` | `"descriptor"`: the self-powered bit the configuration declares |

The device runs it on a modeled host poll. A host controller polls an interrupt endpoint every period while a transfer is queued, on a grid that starts at attach, and fills a transfer one packet of wMaxPacketSize per poll. A short packet or a full buffer ends the transfer, and a packet with no room left is babble, which fails the transfer with `-EOVERFLOW`. The period is the one Windows gives the endpoint descriptor's bInterval, a power of two from 1 to 32 ms at full speed and 2^(bInterval-1) microframes, at most 32, at high speed. `HMController.DeviceProtocol.HostPollInterval` models a faster host, as a filter driver that overclocks the poll makes one, and the descriptors never change. A device with a source poll has a new report for a host poll only once it has read its controllers since its last report.

The SDK compiles all of it once at create: names become bits, conditions become a mask and the values under it, rules become byte patterns, and replies become parts with fixed offsets. A poll takes one lock and copies one packet. Commands are rare and their replies are the only allocations. `CreateController` compiles the whole device model before it takes anything machine-wide, so a mistake in a profile is refused with the JSON path that holds it: ports that do not fit their report, a field outside a block's data bytes, a button name the SDK cannot express, an output port count that differs from the input's, a state a rule names but `state` does not declare, an endpoint the configuration descriptor lacks or of the wrong type, a reply on a device with no input endpoint, a WinUSB GUID that does not parse. Every catalog profile compiles.

#### Event Handlers on SDK Threads

The nine events the SDK raises on its own threads are `OutputReceived`, `OutputDecoded` and `KeyboardLedsChanged` on the output reader, a port's `OutputChanged`, the USB audio events and `HapticReceived`. Each goes through `EventRaise`, which runs the handlers in turn, drops what one throws and writes it to the diagnostic log under `HIDMAESTRO_DIAG=1`. Before b7951e2 a throwing handler kept the handlers after it from running and made the output reader skip the rest of that packet (the keyboard's LED state, `OutputDecoded`, a port's outputs, a Bluetooth DualSense's switch to report 0x31). On a composite's USB/IP connection it closed the connection and unplugged the controller, and on the audio pump, the thread pool or the VR haptic reader it ended the program. Walking the handlers with `Delegate.EnumerateInvocationList` allocates nothing, so the output reader still allocates 0 bytes a report. Report IDs parse without allocating too: a profile's report ID is text, `0x31` or `31`, and `ReportIds.Parse` reads its hex digits from a span where the codec and the output reader had cut the `0x` off with `Substring` on every frame and every output packet.

### WinUSB on Request

A libusb host on Windows opens a device only through WinUSB, libusbK or libusb0, and a real WUP-028 gets Windows' HID driver, which fails it with Code 10. `HMController.BindWinUsb()` builds a driver package from the profile's `usbConfiguration.winUsb` interface GUID: an INF that includes and needs winusb.inf, generated per profile as `hmwinusb_<profile id>.inf`, and matches only a vendor-scoped compatible ID `USB\COMPAT_VID_vvvv&Class_cc&SubClass_ss&Prot_pp` as the sixteenth of its compatible IDs, behind fifteen no device carries. It therefore ranks below input.inf for every real device (0x00FF3F00 against input.inf's 0x00FF3205 on the devbox). The SDK installs it on the one virtual devnode with `DiInstallDevice`, which installs the named driver on the named device and nowhere else, and records the binding in the registry ledger first. Nothing installs a driver on a present devnode afterward. When the last holder lets go, at dispose or at the next program's reclaim after a crash, the ledger waits until the devnode is gone (`CM_Get_DevNode_Status` returning `CR_NO_SUCH_DEVINST`) and then sets `CONFIGFLAG_REINSTALL`, so its next arrival installs by rank. A device present on the devnode meanwhile, the virtual one on its way out or a real adapter that took the instance, keeps WinUSB until it is unplugged and the devnode is marked. Only a controller teardown, the cleanup, a reclaim of the controllers of a program that has ended, a binding that moves to the devnode its dropped adapter came back on, or the follow-up marks it: a program that starts and runs sweeps the ledger only when it finds such a program. One follow-up per entry, on any program, watches for the devnode to go for five minutes, its deadline kept in wall-clock time so a reboot does not stretch it, and a teardown pays nothing for an entry another program's follow-up watches.

Before it installs, the step reads the devnode's driver list and confirms that another driver ranks above the package, and it waits until Windows has settled the devnode's driver: no installation pending in the PnP manager (`CM_WaitNoPendingInstallEvents`) and the devnode started or stopped on a decided driver. It binds only a devnode on a driver that ships with Windows. A devnode on WinUSB, libusbK or libusb0, which includes an instance Zadig bound, returns `AlreadyBound` untouched. A devnode on one of HIDMaestro's own packages, which a killed program's binding leaves until a sweep, also returns `AlreadyBound`, and the controller holds it in the ledger from then on. A devnode on any other driver is left alone and reported as `OtherDriver`. A bind takes the machine-wide WinUSB lock, then the ledger's, the order a restore uses, so a bind and a restore on two threads cannot wait on each other.

The bound devnode carries no reinstall flag, because Windows reports `CONFIGFLAG_REINSTALL` as install state 1 and libusb skips any device whose install state is not 0. The restore sets the flag only on a devnode that is gone instead, so the next arrival on that instance installs again by rank. When the last holder lets go, a present devnode keeps its driver and its entry, whichever driver that is, and one that was moved to another driver meanwhile is left as it is. The uninstall-grade cleanup removes no devnode: it flags a gone devnode still on a package and keeps that package, and removes the packages no devnode uses. A package a phantom devnode keeps can stay in the driver store after an uninstall, ranked below the driver Windows gives the device.

### NativeAOT, Trimming and Single-File

The project sets `IsAotCompatible`, and `aot-analyzers.globalconfig` raises the `Trimming`, `SingleFile` and `AOT` diagnostic categories to errors in Release. Profiles and layouts load through `HMJsonContext`, a source-generated `JsonSerializerContext` with no reflection resolver behind it. Its options are the ones `HMLayoutJsonOptions.Default` carried: camelCase names read case-insensitively, comments and trailing commas allowed, `HMAxis` through its own converter and every other enum as a snake_case string, each through a `JsonStringEnumConverter<TEnum>`, since the non-generic converter needs runtime code generation. The layout `kind` stays with `HMLayoutJsonConverter`, which matches it in any letter case and with or without underscores and reads each concrete layout through the `JsonTypeInfo` the options carry. `[JsonPolymorphic]` does not fit, because its discriminator matches exactly and `LoadProfilesFromDirectory` reads profiles consumers write themselves.

Generated code builds a record whose properties are init-only with one object initializer that assigns all of them, so a key the JSON leaves out arrives as `default(T)`: null for the empty lists and `new()` objects the layout records initialize, 0 for `HMTriggerButton.Stages` (initialized to 1) and `Unknown` for `HMRotaryEncoder.Role` (initialized to `Dial`). Reflection kept those initializers, and 68 of the 336 catalog profiles depend on them. The converter therefore writes the JSON out again with each such key filled from a freshly constructed record. Which keys need it is planned once per type, by comparing a fresh record with the one the same metadata builds from `{}`. `json_parity_check` reads every embedded profile through `HMJsonContext` and through v1.12.0's converter and options and requires the same contents. With the fill step disabled it fails on those 68 profiles.

Every `DllImport` is a `LibraryImport`. A declaration that took a string or `char` gets `StringMarshalling.Utf16`, and every `bool` gets `MarshalAs(UnmanagedType.Bool)`, the 4-byte Win32 `BOOL`, except the results of hid.dll's `HidD_*` functions. Those return `BOOLEAN`, one byte (`hidsdi.h`), and are read as `UnmanagedType.U1`: on an invalid handle `HidD_GetAttributes` returns FALSE in AL with the rest of EAX nonzero, which a 4-byte read takes as TRUE. Three structs were not blittable and changed layout without changing size: `HIDP_CAPS.Reserved` is a 17-element fixed buffer, the OpenVR section's `SECURITY_ATTRIBUTES.InheritHandle` is the 4-byte `BOOL`, and `CM_NOTIFY_FILTER`'s instance ID is a 200-character fixed buffer. The struct stays 416 bytes, the size `CM_Register_Notification` requires. `LibraryImport` does not marshal a managed delegate, so each wait's handler travels as a `GCHandle` in the registration's context argument and one `[UnmanagedCallersOnly]` dispatcher hands every event to it and lets no exception reach cfgmgr32. The handle is freed after `CM_Unregister_Notification`, which waits for pending callbacks to finish. The stubs of the per-frame calls (`SetEvent`, `WaitForSingleObject`, `WaitForMultipleObjects` over a preallocated handle array) convert `BOOL` in place, pin the array with `fixed` and save the last error with no allocation.

`aot_smoke` publishes one source four ways, NativeAOT and trimmed self-contained single-file for x64 and ARM64, and each app loads the catalog, installs the driver, creates `dualshock-4-v2` on the UMDF2 driver and `dualsense-composite` on USB/IP, submits input, waits for `OutputReceived` and disposes. At v1.12.0 both kinds of app load 0 of 336 profiles. The first source-generated parse in a JIT process costs about 8 ms more at the median than the reflection parse (119.9 ms against 111.9 ms over 30 runs) because the generated metadata is compiled on first use, and the second parse in the process is 1.5 ms against 1.3 ms.

### Build Settings and Embedded Payloads

The environment variables that build the SDK (`HMSkipArm64`, `HMSkipUsbip`, `HM_SDK_ROOT`, `HM_WDK_ROOT`, `HM_WDK_ROOT_ARM64`, `HM_WDK_VERSION`, `HMWdkBin`, `HMInf2catBin`) are in the [README](../README.md#build-settings). `Resources\` is a cache that outlives a build, so a skip build in a tree that once built everything would still find ARM64 and USB/IP files there. The resource items therefore no longer come from globs over it. Each payload file is listed when the project is evaluated with the path the pre-build target stages it at and the name it is embedded under, and the target embeds exactly the files it staged for the settings in force. A glob was also evaluated before the target staged anything, which is why the SDK needed two builds on a fresh tree. It needs one now. `HMBuildInfo` reads the embedded resource names, so it describes the bytes held whatever the settings were, and `build_info_check` (S78) fails an official release build unless `IsComplete`.

The SDK embeds six Inf2Cat files where it embedded eight. `inf2cat.exe.manifest` and `Microsoft.Kits.Logger.dll` are not in the NuGet WDK and neither does anything: no Inf2Cat library references the logger, Inf2Cat embeds its own `asInvoker` manifest, which Windows prefers over an external one, and catalogs built with and without both files are identical apart from the list identifier.

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
  ├──► Input acknowledgement (per-controller, keyboards and mice)
  │     DeliveredSeqNo(4) + Capabilities(4) + ReleaseSeq(4) +
  │       ReleaseCount(4) + 8 release frames x (Size(4) + Data[80])
  │       = 688 bytes. The driver writes the sequence number of each
  │       report HID class has taken and signals InputAckEvent. The SDK
  │       waits on it before the next report, and keeps the release
  │       frames the driver sends when the owner pipe closes. Created by
  │       the SDK, opened by the driver.
  │
  ├──► Version section Global\HIDMaestroVersion<N> (per-controller, 64 bytes)
  │     Layout version, protocol range and SDK version written by the
  │       SDK. Driver protocol, version and capabilities written by the
  │       driver each time it opens the input section.
  │
  ├──► Owner pipe \\.\pipe\HIDMaestroOwner<N> (per-controller)
  │     Server end held by the SDK, never written. The driver's pending
  │       read completes when the consuming process ends. <N> is the
  │       transport index. The SDK's ownership claims are separate pipes
  │       (\\.\pipe\HIDMaestro.Owner/.Index/.Identity) the driver never opens.
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
- **Browser (Xbox Series BT)** ← WGI Gamepad ← xinputhid
- **Browser (plain HID)** ← Chromium's Raw Input or WGI fetcher. WGI makes a plain HID profile a Gamepad only where Windows ships a mapping for its VID and PID (see Techniques: GameInput Mappings)
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

**Self-healing on init:** `HMContext.InstallDriver` sweeps the devices of programs that have exited before it deploys, so orphans left by a prior crashed session are cleaned up before the new install runs, and it leaves the controllers of running programs alone. The same sweep is public as `HMContext.RemoveAllVirtualControllers()` for consumers who want explicit defensive cleanup (e.g. on app exit). In normal operation, individual `HMController.Dispose()` is sufficient, and a program that exits without disposing has its devices removed by the SDK's exit handler.

### Profile Architecture Groups and Teardown Timing

Disposal speed depends on which kernel-side drivers are in the device stack. Each additional driver in the stack adds its own PnP query-remove handshake, handle release, and notification cascade. HIDMaestro profiles fall into three architecture groups with dramatically different teardown characteristics:

#### 1. Plain HID: generic gamepads, wheels, HOTAS, flight sticks (~200ms)

Profiles where `driverMode` is not `"xinputhid"` and the VID is not Microsoft (`0x045E`). Includes DualSense, DualShock 4, all Logitech wheels, Thrustmaster HOTAS, flight sticks, pedals, arcade sticks, and most of the 336-profile catalog.

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

Until October 2026 that block never happened. `CM_Register_Notification` refused the SDK's filter struct, declared at 432 bytes against the header's 416, with `CR_INVALID_DATA`, so every wait in `DeviceManager` returned at once and `WaitForDeviceRemoval` reported a device that was still present as not removed. Dispose went on while the SwD device was still leaving, and in 12 of 30 quick create-and-dispose cycles an Xbox 360 Wired controller's XInput slot read connected again 8 to 24 ms after Dispose returned, for about 15 ms. With the struct declared as the header defines it, the wait blocks until Windows reports the device gone. In those quick cycles Dispose took about 15 ms longer and no slot came back (S57 checks it). In create-and-dispose timing runs on five profiles, medians moved by no more than run-to-run noise.

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
| **Ownership claim** | A named pipe created with `FILE_FLAG_FIRST_PIPE_INSTANCE` and one instance. A process holds one for itself, and each controller holds one for its transport index and one for its identity. Windows deletes it when the holder ends, so a missing claim is proof the holder is gone. See Techniques: Programs Side by Side. |
| **Transport index** | The machine-wide number that names a controller's sections, events, owner pipe and registry key and is the `ControllerIndex` the driver reads. Equal to the context's slot when that number is free on the machine. |
| **Identity token** | The instance-name segment every devnode of one virtual controller carries: `HM_0000` for the default key of index 0, `HM_` plus sixteen hex digits for a consumer key. Fixed across lives so the HID child keeps its path. Replaced the per-process session-id prefix in v1.8.0. See Techniques: Stable Device Identity. |
| **ParentIdPrefix** | The `level&hash&n` value in a parent's instance key that PnP prepends to the instance id of every child that reports a non-unique id (HID children do). Read from the key when the child first arrives, minted with a fresh counter only when absent. HIDMaestro writes the identity's value so the child path is the same on every life. |
| **ContainerID slot-1 skip** | Pre-fix bug in `xinput1_4!FUN_18000de2c`: a null-sentinel ContainerID `{00000000-...-FFFF-FFFFFFFFFFFF}` triggered a code path that set bit 2 on the device struct, made the fallback slot allocator skip iter 0, and surfaced an empty slot 1 to consumers. The SWD migration's explicit `pContainerId` closes the path. |
| **GameInput mapping** | Registry entries at `HKLM\...\GameInput\Devices\{VID}{PID}...` that tell WGI how to map HID axes/buttons to the Gamepad interface. Windows ships its own. HIDMaestro writes one only for a code-built game pad of the one shape it fits. |
| **&IG_** | "Interface Group" marker in Xbox device paths. Chrome and HIDAPI skip devices with this in the path; SDL3 falls through to its RawInput backend. |
| **Vx/Vy** | HID velocity usages (0x40/0x41). Invisible to DirectInput's axis mapper but enumerated by GameInput; used to carry separate trigger values. |
| **mshidumdf** | Microsoft's kernel-mode HID minidriver proxy that hosts UMDF2 HID drivers. |
