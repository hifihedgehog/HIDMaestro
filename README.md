<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="docs/logo-light.png">
    <source media="(prefers-color-scheme: light)" srcset="docs/logo.png">
    <img src="docs/logo.png" alt="HIDMaestro" width="140">
  </picture>
</p>

<h1 align="center">HIDMaestro</h1>

*"And we talk of Christ, we rejoice in Christ, we preach of Christ, we prophesy of Christ, and we write according to our prophecies, that our children may know to what source they may look for a remission of their sins."* — 2 Nephi 25:26

*Glory, honor, and praise to the Lord Jesus Christ, the source of all truth, forever and ever.*

---

A user-mode virtual game controller platform that presents like real hardware across the Windows gaming input stack.

HIDMaestro creates profile-driven virtual controllers without a custom kernel driver, EV certificate, or reboot cycle. DirectInput, XInput, SDL3, browser Gamepad, and WGI/GameInput can all see the device identity and behavior the profile defines. The stack builds on the UMDF2 + xinputhid approach that [Nefarius](https://nefarius.at/) used in [DsHidMini](https://github.com/nefarius/DsHidMini).

---

## What it replaces

- **vJoy**: needs a kernel driver and is no longer actively maintained. The existing signed drivers work, but producing new builds requires driver signing infrastructure. Devices show up as "vJoy Device", not as a real controller.
- **ViGEmBus**: needs a kernel driver. Existing signed drivers work, but producing new builds requires an EV code signing certificate ($300+/year). The project is retired.
- **DsHidMini**: supports 5 HID modes (including DS4 and Xbox emulation) but requires a physical DualShock 3 connected. It translates real hardware, not arbitrary input sources.
- **VHF**: a Microsoft kernel framework. Kernel mode only.

HIDMaestro runs entirely in user mode. It works with locally generated self-signed certificates trusted by the target machine: no purchased certificate and no Windows test-signing boot mode required. It creates and removes controllers without rebooting. DirectInput, XInput, SDL3, and the Chrome Gamepad API see the identity and behavior the profile defines.

## Features

### No Kernel Driver
HIDMaestro uses UMDF2 (User-Mode Driver Framework). The driver runs in a regular Windows process, not the kernel. A bug in HIDMaestro cannot blue-screen the machine. No EV certificate, no WHQL. HIDMaestro works with locally generated self-signed certificates trusted by the target machine; no purchased certificate or `testsigning` boot mode is required.

### Exact Hardware Identity
Choose from 225 embedded profiles across 32 vendors (Xbox 360, Xbox Series X|S, DualSense, Thrustmaster wheels, Logitech HOTAS, flight sticks, racing pedals, fight sticks, and more), or extend support through data-driven JSON profiles. Profiles define the public-facing identity and report behavior; vendor-specific extras (LEDs, audio, sensors) may require per-device work. For the public-facing identity and report path defined by the profile, HIDMaestro sets the exact VID/PID, product string, HID descriptor, axis count, button count, trigger behavior, and bus type. SDL3's controller database matches it. Steam recognizes it. Chrome identifies it. joy.cpl shows the right name.

### Cross-API Coverage
Most solutions get one or two APIs right. HIDMaestro targets all of them simultaneously:

| API | What HIDMaestro Delivers |
|-----|-------------------------|
| **DirectInput** | Correct axes, buttons, POV, VID/PID |
| **XInput** | Separate triggers, proper button mapping, single slot |
| **SDL3/HIDAPI** | Correct identity, Bluetooth bus type when tested |
| **Browser Gamepad** | STANDARD GAMEPAD with separate triggers |
| **WGI (GameInput)** | Proper Gamepad promotion via GameInput registry |

### Multi-Controller
Multiple virtual controllers run simultaneously with no hard limit. Tested with 6 mixed (2x Xbox Series BT, 2x Xbox 360 Wired, 2x DualSense) with correct per-controller ordering across all APIs. XInput caps at 4 slots for Xbox-family profiles; non-Xbox profiles are visible through DInput/HIDAPI/WGI/RawInput/Browser without that limit.

### Force Feedback (HID PID 1.0)
DirectInput games that interrogate the device with `HidD_GetFeature` for Block Load (0x12), Pool (0x13), and PID State (0x14) get real answers, not `STATUS_NOT_SUPPORTED`. The consumer fills a per-controller shared state section via `HMController.PublishPidPool` / `PublishPidBlockLoad` / `PublishPidState`; the driver reads from that section synchronously inside the GetFeature IOCTL handler with no IPC round trip. Until `PublishPidPool` is called at least once, the driver returns `STATUS_NO_SUCH_DEVICE` for the Pool Report — matching vJoy's "FFB not enabled" convention so DirectInput concludes "device exists but no FFB" without retrying. Effect-create wire bytes still arrive via `OutputReceived`; the consumer publishes the Block Load synchronously from that handler before it returns. See [example/SdkDemo/Program.cs](example/SdkDemo/Program.cs) section 4b.

### Hot-Plug
Create and remove controllers without reboots. Each controller is independently disposable: remove one while the others keep running, or switch profiles live within the same process. Single-controller creation takes ~200ms on a warm start; 6-controller mixed creation takes ~3.5s total. Subsequent runs on the same boot match fresh-boot timings — see Techniques: Session-Unique Instance-ID Suffix for the kernel-state-pollution fix that closes the gap.

### Profile-Based
Every controller is a JSON file: VID, PID, descriptor, trigger mode, connection type are all data-driven. Adding support for a new controller means writing a JSON file, not modifying code. The profiles directory ships 225 profiles across 32 vendors covering gamepads, racing wheels, HOTAS sticks, flight sticks, pedals, arcade sticks, and more.

### Custom Controllers: build or modify any device
Using the public `HidDescriptorBuilder`, `HMProfileBuilder`, and `HMDeviceExtractor` APIs, consumers can:

- **Clone and modify** an existing profile: e.g. take a DualSense (15 buttons) and create a variant with 16 buttons. Windows, Steam, and games still see "DualSense" because the VID/PID and product string are preserved, but the descriptor declares the extra button.
- **Build new controllers from scratch**: define a custom flight stick, racing wheel, or arcade panel with arbitrary VID/PID, product string, axis count, button count, and axis resolution. No hex editing, no descriptor knowledge required.
- **Spoof an arbitrary controller**: if you know a device's VID, PID, and product string, you can create a virtual copy even if it's not in the 225-profile catalog.
- **Capture a connected device**: `HMDeviceExtractor.Extract` reads the cached HID descriptor Windows parsed from any real HID device you have plugged in and returns a ready-to-deploy `HMProfile`. No JSON authoring, no descriptor reverse engineering — point it at the controller and you get a matching virtual.

The result is an SDK for fully custom virtual controllers that present as real hardware to every API simultaneously, with no kernel driver and no fixed "vJoy Device" identity.

```csharp
// Clone a DualSense and add a button
var custom = new HMProfileBuilder()
    .FromProfile(ctx.GetProfile("dualsense")!)
    .Id("dualsense-16btn")
    .Descriptor(new HidDescriptorBuilder()
        .Gamepad()
        .AddStick("Left", 8).AddStick("Right", 8)
        .AddTrigger("Left", 8).AddTrigger("Right", 8)
        .AddButtons(16).AddHat()
        .Build())
    .InputReportSize(8)
    .Build();
using var ctrl = ctx.CreateController(custom);

// Or build a flight stick from nothing
var stick = new HMProfileBuilder()
    .Id("my-stick").Name("My Flight Stick")
    .Vid(0x0483).Pid(0x0001)
    .ProductString("Custom HOTAS")
    .Descriptor(new HidDescriptorBuilder()
        .Joystick()
        .AddStick("Left", 16)
        .AddTrigger("Left", 8).AddTrigger("Right", 8)
        .AddButtons(12).AddHat()
        .Build())
    .InputReportSize(8)
    .Build();
using var ctrl2 = ctx.CreateController(stick);

// Or capture a physical controller and deploy as a virtual
var device = HMDeviceExtractor.ListDevices()
    .First(d => d.VendorId == 0x046D && d.ProductId == 0xC216);
var extracted = HMDeviceExtractor.Extract(device);
using var ctrl3 = ctx.CreateController(extracted);
// Virtual now presents with the real device's descriptor, VID/PID,
// product string — identical to what the physical device reports.
```

### Profile Extractor (GUI tool)

Every HIDMaestro release ships a standalone WPF app (`HIDMaestroProfileExtractor.exe`) under `HIDMaestroProfileExtractor/` in the release ZIP. It calls the same `HMDeviceExtractor` API from a dropdown-and-save UI:

1. Plug any HID device into Windows.
2. Run `HIDMaestroProfileExtractor.exe` (no admin required).
3. Pick the device from the dropdown, click **Extract**.
4. Save the Profile JSON to disk or copy it to the clipboard.

The output is the exact JSON format shipped in `profiles/<vendor>/<slug>.json`. Drop it in a PR (or an [issue via the profile contribution template](https://github.com/hifihedgehog/HIDMaestro/issues/new?template=profile-contribution.yml)) to add the profile to the catalog so every HIDMaestro user can emulate that controller without owning it themselves.

The extractor reads only the cached HID descriptor Windows has already parsed — no live input capture, no gameplay involvement. Reconstruction uses a C# port of the libusb/hidapi algorithm (Chromium WebHID team's reverse engineering of Microsoft's preparsed-data layout). Output is logically equivalent to the device's real HID report descriptor: same report IDs, field layouts, logical ranges, usage pages, and sizes.

## Techniques

A few HIDMaestro techniques that are not well documented elsewhere in the virtual controller space.

### Velocity Usage Descriptor Trick

Real Xbox 360 controllers have a combined trigger axis (Z) in DirectInput: both triggers share one axis. Browsers and WGI need separate trigger values. Previous solutions had to choose: correct DI (5 axes, combined) or correct browser (separate triggers, 6 axes).

HIDMaestro uses HID velocity usages (Vx and Vy, Usage Page 0x01, Usages 0x40/0x41) to carry separate trigger values in the same HID report. DirectInput does not map velocity usages to any axis slot, so it sees 5 axes. GameInput/WGI enumerates them as additional axes and reads separate trigger data via the GameInput registry mapping.

Result: 5 axes and 10 buttons in DirectInput (matching real xusb22.sys), separate triggers in the browser (matching real XInput), all from one HID descriptor.

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

## Comparison

| Feature | HIDMaestro | ViGEmBus | DsHidMini | vJoy |
|---------|-----------|----------|-----------|------|
| Kernel driver required | **No** | Yes | No (UMDF2) | Yes |
| EV certificate for new builds | **No** | Yes | No | Yes |
| Physical hardware required | **No** | No | Yes (DS3) | No |
| Profile-defined controller identity | **Yes** | No (2 types) | Yes (5 modes) | No (fixed) |
| Bluetooth bus type spoof | **Yes** | No | No | No |
| Single browser gamepad entry | **Yes** | Yes | N/A | No |
| XInput with separate triggers | **Yes** | Yes | Yes | No |
| DirectInput with correct axes | **Yes** | Yes | Yes | Partial |
| SDL3 with correct identity | **Yes** | Yes | Yes (real HW) | No |
| Hot-plug without reboot | **Yes** | Yes | N/A | No |
| Data-driven profiles | **Yes** | No | No | No |
| Custom descriptor builder | **Yes** | No | No | Yes (fixed layout) |
| Clone + modify existing profiles | **Yes** | No | No | No |
| Spoof arbitrary VID/PID | **Yes** | No | No | No |
| Status | **Active** | Retired | Active | Stale |

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

## Getting Started

Requirements: Visual Studio 2022+, Windows SDK/WDK 10.0.26100.0, .NET 10

```bash
# On a fresh clone, build the native driver + companion + SDK together.
# This runs scripts\build.cmd, scripts\build_companion.cmd, and a two-
# phase dotnet build so the driver binaries get embedded in the SDK DLL.
# One command, idempotent — re-run any time you touch driver/ or sdk/.
scripts\build_all.cmd

# Smallest possible SDK consumer (after build_all.cmd)
dotnet run --project example\SdkDemo

# Full test app (self-contained: cert + build + sign + install all automatic)
cd test
dotnet build
bin\Release\net10.0-windows10.0.26100.0\win-x64\HIDMaestroTest.exe emulate xbox-360-wired

# Multiple controllers at once (up to 6 verified, any mix of profiles)
HIDMaestroTest.exe emulate xbox-series-xs-bt xbox-series-xs-bt xbox-360-wired dualsense

# Interactive commands during emulation:
#   remove 2              dispose controller 2 (others stay live)
#   2 dualsense           live-swap controller 2 to a DualSense
#   mark / unmark         toggle static-button diagnostic mode
#   pause / resume        idle CPU test (driver should use ~0%)
#   quit                  graceful shutdown

# List available profiles (225 across 32 vendors)
HIDMaestroTest.exe list

# Search profiles
HIDMaestroTest.exe search thrustmaster

# Validate all APIs (XInput, DirectInput, HIDAPI/SDL3, browser, WGI, HID order)
python scripts\verify.py --controllers 4

# Cross-API ordering diagnostic with correct open-then-trigger semantics
build\multipad_check.exe --trigger --expected 4
```

The test app is fully self-contained. On first run it:
1. Creates a locally trusted self-signed certificate (if needed)
2. Extracts pre-built driver DLLs from the SDK assembly
3. Signs everything
4. Installs driver packages
5. Creates the virtual controller(s)
6. Starts feeding test input data (time-varying pattern by default)

No external scripts, no manual setup, no popups. Just the one console window. Requires elevation (administrator privileges).

### Live-swap regression battery

`test/regression/swap_regression.ps1` is a 23-scenario battery that drives the test app through every interesting create / live-swap / remove / force-kill sequence and verifies no PnP devnodes are left in the `PRESENT` state after each one. Covers all five controller archetypes (Xbox 360 Wired, Xbox Series Bluetooth, DualSense, Switch Pro, plus a runtime-built custom profile authored via `HMProfileBuilder` + `HidDescriptorBuilder`). Run from an elevated PowerShell:

```powershell
./test/regression/swap_regression.ps1                # full battery, ~32 min
./test/regression/swap_regression.ps1 -Filter 'S08*' # one scenario, ~1-2 min
```

Exit code 0 if every scenario passed, 1 if any failed. Useful before tagging a release: catches the `SwDeviceLifetimeParentPresent` resurrection class of bugs and any future regression in the live-swap teardown path.

## Profile System

Controller profiles are JSON files in `profiles/`:

```json
{
  "id": "xbox-360-wired",
  "name": "Xbox 360 Controller (Wired)",
  "vid": "0x045E",
  "pid": "0x028E",
  "productString": "Controller (XBOX 360 For Windows)",
  "descriptor": "05010905a101a100093009311500...",
  "inputReportSize": 18,
  "triggerMode": "combined",
  "connection": "usb"
}
```

The descriptor field contains the raw HID report descriptor as hex. The test app parses it, builds input reports, and feeds data through shared memory. Adding a new controller is a matter of capturing its descriptor and writing a JSON file.

## Validation Results

Tested on Windows 11 IoT Enterprise LTSC 2024 (build 26200) with a locally generated self-signed certificate added to the machine's Root and TrustedPublisher stores (no `bcdedit /set testsigning` required). Each profile was deployed via the test app and validated with `scripts/verify.py` plus manual verification in joy.cpl, PadForge/SDL3, Chrome Gamepad API, and XInput state readers.

Multi-controller verified with 6 simultaneous mixed controllers (2x Xbox Series BT + 2x Xbox 360 Wired + 2x DualSense): all 6 APIs report correct per-controller identity and ordering. Real Microsoft Xbox Series X|S BT controller tested side-by-side: virtual and real exhibit byte-identical behavior across all HID class APIs.

### Summary

| Profile | DirectInput | XInput | SDL3 | Browser | WGI |
|---------|------------|--------|------|---------|-----|
| Xbox 360 Wired | 5 axes, 10 btns | 1 slot, separate triggers, Guide (Ex) | &IG_ path, USB | STANDARD GAMEPAD, separate | 1 interface |
| Xbox Series BT | 5 axes, 16 btns (+ Share at btn 12) | 1 slot, separate triggers, Guide (Ex) | &IG_ path, Bluetooth | STANDARD GAMEPAD, separate | 1+ interfaces |
| DualSense (PS5) | 6 axes, 15 btns | N/A | USB | Detected | N/A |
| **6-controller mixed** | **All 6 visible** | **4 slots (XInput cap)** | **4 IG + 2 live** | **4 pads (Chromium cap)** | **All 6 visible** |

### Xbox 360 Controller (Wired)

| Check | Result |
|-------|--------|
| DirectInput axes | 5 (X, Y, Rx, Ry, Z combined) |
| DirectInput buttons | 10 |
| DirectInput VID:PID | 045E:028E |
| XInput slots | 1, contiguously allocated (no slot-1-skip — see Techniques: SWD Migration) |
| XInput triggers | Separate (LT and RT independent) |
| XInput Guide | Reachable via `XInputGetStateEx` as `XINPUT_GAMEPAD_GUIDE` (0x0400). The XUSB companion packs `HMButton.Guide` into GIP `btnHigh` bit 0x40 and unpacks it back into `wButtons` in `IOCTL_XUSB_GET_STATE`. xinputhid does not apply to the 360 profile, so the companion path carries it. |
| SDL3/HIDAPI | Detected via XInput, &IG_ path |
| Browser Gamepad | "Xbox 360 Controller (XInput STANDARD GAMEPAD)" |
| Browser triggers | Separate (via Vx/Vy + GameInput mapping) |
| Browser vibration | Chromium `put_Vibration` dispatches `IOCTL_XUSB_SET_STATE` to the XUSB companion via the xinputhid UpperFilter tripwire (WGI's XUSB path). The SDK raises `OutputReceived` to the consumer; forwarding to physical hardware is the consumer's job. |
| WGI Gamepad | One entry, XUSB-backed through the SWD-enumerated companion |
| Duplicates | None |

### Xbox Series X|S Controller (Bluetooth)

| Check | Result |
|-------|--------|
| DirectInput axes | 5 |
| DirectInput buttons | 16 (Win11 xinputhid synthesis over a 12-button source descriptor) |
| DirectInput VID:PID | 045E:0B13 |
| XInput slots | 1 |
| XInput triggers | Separate |
| XInput Guide | Reachable via `XInputGetStateEx` as `XINPUT_GAMEPAD_GUIDE` (0x0400). Routed through the System Main Menu HID usage; xinputhid forwards it natively, so this works on every Series/One/Elite BT+USB profile that shares the GIP descriptor. |
| Share button | Present in the source descriptor at button 12 (`HMButton.Share`). Visible through xinputhid's 16-button layout; exposed as a plain discrete button rather than a Consumer Page Record so consumers can layer macros on it. |
| SDL3/HIDAPI | bus_type = Bluetooth |
| BTHLEDEVICE spoof | Confirmed (HIDAPI bus_type=2) |
| Browser Gamepad | Detected, separate triggers |
| WGI Gamepad | One entry, HID-backed. xinputhid.sys binds on the HID child (the real one, not the registry-string tripwire) and drives WGI admission natively. |
| Duplicates | None |

### Screenshots

**Xbox Series X|S Bluetooth** in Device Manager, joy.cpl, Chrome Gamepad Tester, PadForge/SDL3:
![Xbox Series BT across all tools](docs/screenshot-xbox-series-bt.png)

**Xbox 360 Wired** in Device Manager, joy.cpl, Chrome Gamepad Tester, PadForge/SDL3:
![Xbox 360 Wired across all tools](docs/screenshot-xbox-360-wired.png)

**DualSense (PS5)** in Device Manager, joy.cpl, Chrome Gamepad Tester, PadForge/SDL3:
![DualSense across all tools](docs/screenshot-dualsense.png)

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

### Startup and Hot-Plug Timing

| Operation | Measured Time |
|-----------|--------------|
| Cold start (first run: cert + sign + install + create 1) | ~18s |
| Warm start, single controller (drivers cached) | **~200ms** |
| Warm start, 4 mixed controllers (2 BT + 2 Xbox 360 wired) | **~2.2-2.8s** |
| Warm start, 6 mixed controllers (sequential) | **~3.5s** |
| Single dispose: plain HID (DualSense, wheels, etc.) | **~200ms** |
| Single dispose: Xbox 360 Wired (XUSB companion) | ~5.6s |
| Single dispose: Xbox Series BT (xinputhid filter) | ~11s |
| 4-controller cleanup (parallel, batch path) | ~30s |
| 6-controller mixed cleanup (sequential) | ~33s (dominated by Xbox teardown) |

Cold start includes certificate creation, signing, catalog generation, driver package installation, and device creation. This only happens on first run or after SDK updates. Warm start uses event-driven polled waits that exit as soon as PnP is ready. Zero fixed `Thread.Sleep` calls remain in any creation, cleanup, or finalization path. Controllers are independently disposable: removing one does not disturb the others.

**Same-boot run-to-run consistency:** every launch matches the fresh-boot Phase-1 timing. The earlier regression where subsequent same-boot runs took 65s (and lost XInput visibility for the XUSB-companion path) is fixed by the per-process session-id prefix on SWD instance-ids — see Techniques: Session-Unique Instance-ID Suffix.

**Per-step install breakdown** (visible in stdout when `HMContext.InstallDriver` runs): extract ~20ms · remove old packages ~100ms · sign ~130ms · generate catalogs ~840ms (the largest single step, AV-sensitive) · install drivers ~580ms · total ~1.7s on a clean machine. On corporate workstations with hundreds of devices in the PnP tree, total install can stretch to 5-20s; HIDMaestro doesn't run `pnputil /scan-devices` (it's a no-op for our INFs and was the largest variable contributor).

**Batch teardown:** `HMContext.Dispose()` and the public `DisposeControllersInParallel(controllers, perControllerCallback)` parallelize per-controller `DIF_REMOVE` work and run the system-wide HID orphan sweep once at the end instead of per-controller. The kernel still serializes much of the actual filter/companion unload, so the savings is modest (~3s at N=4) — the per-controller wall-clock numbers above are sequential-dispose worst case. Live profile-switch (single `HMController.Dispose()` mid-session) stays synchronous because slot-allocation determinism requires the old devnode fully gone before the new one is created.

### Profile Architecture Groups and Teardown Timing

Disposal speed depends on which kernel-side drivers are in the device stack. Each additional driver in the stack adds its own PnP query-remove handshake, handle release, and notification cascade. HIDMaestro profiles fall into three architecture groups with dramatically different teardown characteristics:

#### 1. Plain HID: generic gamepads, wheels, HOTAS, flight sticks (~200ms)

Profiles where `driverMode` is not `"xinputhid"` and the VID is not Microsoft (`0x045E`). Includes DualSense, DualShock 4, all Logitech wheels, Thrustmaster HOTAS, flight sticks, pedals, arcade sticks, and most of the 225-profile catalog.

```
ROOT\VID_054C&PID_0CE6\NNNN          ← our UMDF2 driver (mshidumdf host)
  └─ HID\VID_054C&PID_0CE6\...       ← raw HID PDO, no upper filter
```

**Lightest stack.** One `DIF_REMOVE` on the ROOT parent tears down the entire tree. No XUSB companion device, no Microsoft upper filter. Creation ~200ms, disposal ~200ms.

#### 2. Non-xinputhid Xbox: Xbox 360 Wired (~5.7s)

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

**Medium stack.** Two device trees to tear down. The XUSB companion runs its own WUDFHost instance hosting `HMXInput.dll`, which needs its own PnP release cycle. Creation ~700ms, disposal ~5.7s.

#### 3. xinputhid Xbox: Xbox Series X|S Bluetooth (~11s)

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

**Heaviest stack.** xinputhid is a Microsoft inbox kernel filter driver. Its teardown goes through the full PnP query-remove → class installer → filter unload chain. This is entirely controlled by Microsoft's driver; HIDMaestro cannot speed it up. Creation ~200ms, disposal ~11s.

#### Why this matters for consumers

If your application needs fast profile switching (e.g. remapping a physical controller to a different virtual identity on the fly), the profile architecture group determines the user-perceived latency:

- **Switching between plain HID profiles** (DualSense ↔ DualShock 4, or any non-Xbox pair): ~400ms round trip (200ms dispose + 200ms create). Essentially instant.
- **Switching to/from Xbox 360 Wired**: ~6-7s (XUSB companion teardown dominates).
- **Switching to/from Xbox Series BT**: ~11-12s (xinputhid teardown dominates).

## Why UMDF2 Is Enough

A common assumption is that virtual game controllers require kernel-mode drivers. Here's why UMDF2 works:

- **HID class driver is already in the kernel.** Windows ships `mshidumdf.sys` which acts as a kernel-mode HID minidriver proxy. Our UMDF2 DLL runs in user mode but the HID class stack sees a real HID device.
- **XInput discovery uses device interfaces, not bus type.** `xinput1_4.dll` finds controllers through the XUSB device interface GUID. A UMDF2 driver can register this interface from user mode.
- **GameInput reads HID reports, not driver internals.** WGI/GameInput reads from the HID preparsed data and report descriptors; it does not care whether the underlying driver is kernel or user mode.
- **SDL3 and HIDAPI check device paths and attributes.** Bus type, VID/PID, and device path strings are all settable from user mode via SetupDI and CM APIs.

The only things UMDF2 *cannot* do: create PDOs (Physical Device Objects) as children of a bus, or intercept internal kernel IOCTLs. HIDMaestro works around this by using a companion device for XUSB and root-enumerated device nodes for the HID stack.

## Known Limitations

- **Output passthrough is delivered but not routed to hardware.** The driver accepts rumble/haptics/FFB SET_STATE IOCTLs and the SDK raises `HMController.OutputReceived` events to the consumer application. Routing those events to a physical controller is the consumer's responsibility (e.g. PadForge handles this).
- **HID PID 1.0 force feedback is consumer-driven.** The driver answers `HidD_GetFeature` for the canonical PID Report IDs (Block Load 0x12, Pool 0x13, State 0x14) directly from a per-controller shared state section. The consumer fills it via `HMController.PublishPidPool` / `PublishPidBlockLoad` / `PublishPidState`. Until `PublishPidPool` is called at least once, the driver returns `STATUS_NO_SUCH_DEVICE` for the Pool Report so DirectInput cleanly concludes "device exists but no FFB" — matching vJoy's "FFB not enabled" convention. No IPC round trip on the GetFeature path.
- **Auth-chip controllers.** Some platforms (PS4/PS5 online, Nintendo Switch Online) require cryptographic authentication from the controller hardware. HIDMaestro cannot replicate authentication chips.
- **Vendor-specific feature reports.** Some controllers use proprietary feature reports for calibration, LED control, or firmware updates. HIDMaestro profiles currently cover standard input/output; vendor extensions require per-controller work.
- **XUSB companion creates a second device node** for non-xinputhid Xbox profiles (e.g. Xbox 360 Wired). Real Xbox controllers have XUSB and HID on the same PDO. HIDMaestro uses a separate companion device because mshidumdf suppresses XUSB IOCTLs. This is invisible to applications but visible in Device Manager. Xbox Series BT profiles use xinputhid natively and do not need a companion.
- **HID class APIs are event-driven, not state-driven.** DirectInput, HIDAPI, WGI, and RawInput only see state changes that occur after a consumer opens the device. Buttons held *before* a consumer opens are invisible until released and re-pressed. This is standard Windows HID class behavior, not a HIDMaestro limitation. Tested and confirmed: a real Microsoft Xbox Series X|S BT controller exhibits byte-identical behavior (holding A before opening joy.cpl shows A as not pressed). XInput is the exception (always-on, polled by the OS).
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

## Security and Scope

HIDMaestro replicates the public-facing identity and input/output behavior of game controllers. It does **not**:

- Replicate cryptographic authentication (PS4/PS5 auth chips, Nintendo Switch pairing)
- Implement vendor-private protocols unless explicitly added to a profile
- Bypass anti-cheat systems (virtual devices are detectable by kernel-level anti-cheat)
- Modify or intercept data from physical controllers

## Credits

- **[DsHidMini](https://github.com/nefarius/DsHidMini)** by [Nefarius Software Solutions](https://nefarius.at/). HIDMaestro builds on the UMDF2 + xinputhid approach that Nefarius used in DsHidMini. DsHidMini demonstrated that a user-mode driver framework can replace kernel-mode drivers for game controller emulation on Windows. The same architecture (`mshidumdf` as the kernel-side HID proxy, `WUDFRd` as the reflector, xinputhid as the XInput bridge) is the foundation HIDMaestro's virtual controller stack is built on.
- **[HIDAPI](https://github.com/libusb/hidapi)**: bus type detection behavior informed the BTHLEDEVICE spoofing technique.
- **[SDL3](https://github.com/libsdl-org/SDL)**: multi-backend fallback behavior informed the &IG_ enumerator trick. SDL3 is not a dependency; HIDMaestro is validated against it.

## Donations

Knowing HIDMaestro is useful is reward enough. If you truly insist on donating, please donate to your charity of choice and bless humanity. If you can't think of one, consider [Humanitarian Services of The Church of Jesus Christ of Latter-day Saints](https://philanthropies.churchofjesuschrist.org/humanitarian-services). Also consider donating directly to the upstream projects listed above; they made all of this possible.

**My promise:** HIDMaestro will never become paid, freemium, or Patreon early-access paywalled. Free means free.

## License

MIT License. See [LICENSE](LICENSE) for details.
