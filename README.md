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

<p align="center">
  <a href="https://somsubhra.github.io/github-release-stats/?username=hifihedgehog&repository=HIDMaestro"><img src="https://img.shields.io/github/downloads/hifihedgehog/HIDMaestro/total" alt="Total downloads"></a>
  <a href="https://discord.gg/qawTZHVhNH"><img src="https://img.shields.io/discord/1507059039844962425?label=Discord&logo=discord&logoColor=white&color=5865F2" alt="Discord"></a>
  <a href="https://hidmaestro.org/"><img src="https://img.shields.io/badge/website-hidmaestro.org-blue" alt="Website"></a>
  <a href="https://github.com/hifihedgehog"><img src="https://img.shields.io/github/followers/hifihedgehog?style=social&label=Follow" alt="GitHub followers"></a>
  <a href="https://x.com/hifihedgehog"><img src="https://img.shields.io/badge/X-@hifihedgehog-black?logo=x&logoColor=white" alt="Follow on X"></a>
</p>

**Virtual game controllers that look like real hardware to Windows. No kernel driver. No network. No reboot.**

HIDMaestro creates virtual controllers that present the exact identity of real hardware across the whole Windows input stack at once. Pick from 225 built-in profiles or point it at a controller you own and clone it. DirectInput, XInput, SDL3, the browser Gamepad API, and WGI/GameInput all see the VID/PID, product name, HID descriptor, axis and button layout, and bus type the profile defines.

It runs entirely in user mode (UMDF2), signed with a locally trusted self-signed certificate. No EV certificate, no `testsigning` boot mode, no kernel driver that can blue-screen the machine.

```csharp
using var ctx = new HMContext();
ctx.LoadDefaultProfiles();
ctx.InstallDriver();
using var ctrl = ctx.CreateController(ctx.GetProfile("xbox-360-wired")!);
ctrl.SubmitState(new HMGamepadState { Buttons = HMButton.A });
```

## Why HIDMaestro

- **No kernel driver, ever.** Pure user-mode UMDF2. A bug cannot blue-screen the machine, and a locally trusted self-signed certificate is enough. No EV certificate, no `testsigning`, no reboot.
- **No network in the path.** Input travels through shared memory on the same machine. There is no socket, no USBIP stack, and no kernel transport driver between your application and the device.
- **Exact hardware identity, down to the bus.** VID/PID, product string, descriptor, axis and button layout, and bus type all match the real device. A Bluetooth controller reports as Bluetooth, not as a USB device wearing its name.
- **New devices are data, not code.** Add a controller by writing a JSON profile or by capturing one you already own. No per-device source, no recompile.
- **Every API at once.** DirectInput, XInput, SDL3, the browser Gamepad API, and WGI/GameInput all see one correct device.
- **Lower latency, measured.** ~35 µs median single-press, more than 4x faster than the closest alternative, with no batching cap on input.

## Latency

Measured input latency from `SubmitState` to the input surfacing through XInput, single button press, 10,000 iterations on the same host:

| | Single-press latency |
|--|--|
| **HIDMaestro** (measured) | **~35 µs median, worst case under 1 ms** |
| VIIPER (their published Windows figure) | 168 µs |

Reproduce it yourself: `HIDMaestroTest.exe latency xbox-360-wired`. The harness shares one clock between the writer and the reader and detects the actual button bit changing, so the number is real propagation, not poll quantization. Full methodology and per-run numbers: [docs/testing/latency.md](docs/testing/latency.md).

HIDMaestro talks to its driver through a shared-memory section on the same machine. There is no socket and no network stack in the path. Input is event-driven: each `SubmitState` signals the driver immediately, with no fixed batching interval.

A note on the comparison. VIIPER is "Virtual Input **over IP**", built on USBIP and driven over a TCP API or an in-process library. Its sub-millisecond figures are measured over **localhost only**. Its own benchmark doc says so plainly: "remote/network USBIP attachment will add network RTT and jitter which is intentionally excluded from these baseline figures." Round-trip time there is how long an input takes to travel to another machine and come back, and jitter is how much that delay varies from moment to moment. The headline latency excludes the network path the project is named for, and VIIPER batches reports every millisecond, capping the update rate at 1000 Hz.

The moment you run it over an actual network, that excluded round-trip time dominates: roughly 1 to 5 ms added over wired LAN, and 10 to 50 ms over Wi-Fi, on top of the localhost figure rather than instead of it. That is one to two orders of magnitude above the sub-millisecond number the docs lead with, and the caveat that explains the gap is a single line buried in a testing doc. Anyone who reads "well below 1 millisecond" and pictures networked play is being pointed at the wrong number.

![Input latency, lower is better: HIDMaestro 0.035 ms versus VIIPER at 0.168 ms localhost, 1-5 ms over wired LAN, and 10-50 ms over Wi-Fi](docs/latency-chart.svg)

The network bars above use the optimistic end of each LAN range. Even granting VIIPER the best case, its Wi-Fi path is hundreds of times longer than HIDMaestro's shared-memory path.

There is also a layering question worth naming. Networking is a transport concern that belongs in the application, not in the virtual-device driver. Building USBIP-over-IP into the emulator puts a kernel USBIP driver and a listening socket in front of every user, including the ones who only ever drive a controller on the same machine. HIDMaestro keeps the device layer local: the SDK writes input to a shared-memory section, and nothing in the driver knows or cares whether that input came from the local process or was relayed by the consumer from another machine. If you want input over a network, that is the application's job to own and secure, without dragging a network stack and a kernel transport driver through every local use of the device. Consumers already do this at the right layer: PadForge, built on HIDMaestro, streams controllers over a network through its web controller interface, with zero latency added for anyone playing locally.

And there is the matter of what USBIP can represent at all. USB/IP transports USB, so every device VIIPER creates is a USB device to Windows. The controllers people actually use often are not: an Xbox or DualSense paired over Bluetooth enumerates as a Bluetooth device, and SDL3 and Chromium parse it through different code paths because of that bus type. A USB-only emulation reports the wrong bus for those controllers. HIDMaestro sets bus type per profile, so a Bluetooth controller presents as Bluetooth (HIDAPI reports `bus_type = Bluetooth`), matching the hardware it stands in for.

## How it compares

| | HIDMaestro | VIIPER | ViGEmBus | vJoy |
|--|--|--|--|--|
| Kernel driver required | **No** (UMDF2 user mode) | Yes (USBIP) | Yes | Yes |
| Network play | **App layer, zero local penalty (e.g. PadForge web controller)** | In the driver: +1-5 ms wired, +10-50 ms Wi-Fi | No | No |
| EV certificate for new builds | **No** | No (uses signed usbip-win2) | Yes ($300+/yr) | Yes |
| Identity per controller | **Exact, 225 profiles** | 6 fixed device types | 2 fixed types | Fixed "vJoy Device" |
| Bus type fidelity | **Per-profile, incl. Bluetooth** | USB only (USBIP) | USB only | USB only |
| Add a new device | **JSON file, or capture one you own** | Write Go (a few hundred lines/device) | N/A | N/A |
| Local single-press latency | **~35 µs measured** | 168 µs published (localhost) | — | — |
| Input update rate | Event-driven, no fixed cap | 1000 Hz (1 ms batching) | — | — |
| License | MIT | GPL-3.0 (clients MIT) | — | — |
| Status | **Active** | Active | Retired | Stale |

VIIPER describes itself as running entirely in userspace. On Windows that holds only for its device code: the USB/IP transport still requires installing usbip-win2, a third-party kernel-mode driver. HIDMaestro installs no kernel-mode driver. It rides the UMDF2 host that already ships with Windows, so the "no kernel driver" row above is literal, not a footnote.

HIDMaestro is Windows optimized and focused on game controllers and HID game devices. Within that scope it gives you exact hardware identity with no kernel driver, no network layer, and no per-device code. That combination is what HIDMaestro is built for, and nothing else on this list delivers it.

## What it replaces

- **VIIPER**: needs a kernel-mode USB/IP driver on Windows despite the userspace billing, presents every controller as USB so Bluetooth devices report the wrong bus type, and its sub-millisecond latency is localhost-only. The network it is named for adds 1 to 50 ms.
- **vJoy**: kernel driver, no longer actively maintained, shows up as "vJoy Device" instead of real hardware.
- **ViGEmBus**: kernel driver, retired, new builds need an EV code-signing certificate.
- **DsHidMini**: user-mode, but translates a physically connected DualShock 3 rather than arbitrary input.

## Features

- **No kernel driver.** UMDF2 runs the driver in a normal user-mode process. A bug cannot blue-screen the machine. Self-signed certificate trusted by the local machine is enough.
- **Exact hardware identity.** VID/PID, product string, HID descriptor, axis and button counts, trigger behavior, and bus type all come from the profile. SDL3's controller database matches it, Steam recognizes it, Chrome identifies it, joy.cpl shows the right name.
- **One device, every API.** DirectInput sees correct axes and buttons. XInput sees separate triggers in one slot. SDL3/HIDAPI sees the right identity and bus type. The browser sees a STANDARD GAMEPAD with separate triggers. WGI sees one Gamepad. [How this works](docs/INTERNALS.md#techniques).
- **Multiple controllers at once.** No hard limit. Verified with 6 mixed controllers, correct per-controller ordering across all APIs. XInput caps Xbox-family profiles at its own 4 slots.
- **Force feedback.** HID PID 1.0 answers for DirectInput FFB games, plus rumble/haptic output events the consumer routes to real hardware.
- **Hot-plug.** Create and remove controllers with no reboot. Live-swap a controller's profile mid-session. Warm single-controller create is ~200 ms.
- **Data-driven profiles.** Every controller is a JSON file. Adding support for a new one means writing JSON, not modifying code.

### Custom controllers

The `HidDescriptorBuilder`, `HMProfileBuilder`, and `HMDeviceExtractor` APIs let you build or modify any device:

- **Clone and modify.** Take a DualSense (15 buttons) and ship a 16-button variant. Windows, Steam, and games still see "DualSense" because the VID/PID and product string are preserved.
- **Build from scratch.** Define a flight stick, racing wheel, or arcade panel with arbitrary VID/PID, axis count, button count, and resolution. No hex editing.
- **Capture a real device.** `HMDeviceExtractor.Extract` reads the HID descriptor Windows parsed from any device you have plugged in and returns a ready-to-deploy profile. Point it at the controller, get a matching virtual.

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
    .InputReportSize(9)
    .Build();
using var ctrl = ctx.CreateController(custom);
```

Adding a controller to HIDMaestro is a data change. Adding one to a code-per-device emulator means writing and compiling a new device implementation. That difference is the whole point of the profile system.

## Getting Started

Requirements: Visual Studio 2022+, Windows SDK/WDK 10.0.26100.0, .NET 10.

```bash
# Build the native driver + companion + SDK together (idempotent).
scripts\build_all.cmd

# Minimal SDK consumer
dotnet run --project example\SdkDemo

# Full test app: cert + build + sign + install all automatic, requires elevation
cd test
dotnet build
bin\Release\net10.0-windows10.0.26100.0\win-x64\HIDMaestroTest.exe emulate xbox-360-wired

# Several controllers at once, any mix of profiles
HIDMaestroTest.exe emulate xbox-series-xs-bt xbox-360-wired dualsense

# List or search the 225 profiles
HIDMaestroTest.exe list
HIDMaestroTest.exe search thrustmaster

# Measure input latency
HIDMaestroTest.exe latency xbox-360-wired

# Validate every API (XInput, DirectInput, HIDAPI/SDL3, browser, WGI, HID order)
python scripts\verify.py --controllers 4
```

The test app is self-contained. First run creates a locally trusted certificate, extracts and signs the driver, installs it, creates the controllers, and starts feeding a test pattern. One console window, no popups, requires administrator.

During emulation you can `remove 2` to dispose one controller, `2 dualsense` to live-swap controller 2, or `quit` to shut down cleanly.

## Validation

Tested on Windows 11 IoT Enterprise LTSC 2024 (build 26200) with a self-signed certificate in the machine's Root and TrustedPublisher stores. Each profile is deployed and checked with `scripts/verify.py` plus manual verification in joy.cpl, PadForge/SDL3, the Chrome Gamepad API, and XInput readers. A real Xbox Series X|S Bluetooth controller tested side by side shows byte-identical behavior across the HID class APIs.

| Profile | DirectInput | XInput | SDL3 | Browser | WGI |
|--|--|--|--|--|--|
| Xbox 360 Wired | 5 axes, 10 btns | 1 slot, separate triggers | &IG_ path, USB | STANDARD GAMEPAD | 1 entry |
| Xbox Series BT | 5 axes, 16 btns | 1 slot, separate triggers | &IG_ path, Bluetooth | STANDARD GAMEPAD | 1 entry |
| DualSense (PS5) | 6 axes, 15 btns | N/A | USB | Detected | N/A |
| 6-controller mixed | All 6 visible | 4 slots (XInput cap) | 4 IG + 2 live | 4 pads (Chrome cap) | All 6 visible |

The Xbox Series BT row shows 16 buttons because Windows' `xinputhid` synthesizes a 16-button layout over the 12-button source descriptor. [Details](docs/INTERNALS.md#validation-results).

A 41-scenario [live-swap regression battery](test/regression/swap_regression.ps1) drives every create / swap / remove / force-kill sequence, the FFB round-trip, and the Sony vendor-blob encode/decode, verifying no PnP devnodes are left behind. 41/41 PASS on a Ryzen-class dev box and on an Intel Atom Z8350 fixture.

Full device-tree dumps, HIDAPI enumeration logs, per-profile results, and startup/teardown timing are in [docs/INTERNALS.md](docs/INTERNALS.md#validation-results).

**Xbox Series X|S BT, Xbox 360 Wired, and DualSense across Device Manager, joy.cpl, Chrome Gamepad Tester, and PadForge/SDL3:**

![Xbox Series BT across all tools](docs/screenshot-xbox-series-bt.png)
![Xbox 360 Wired across all tools](docs/screenshot-xbox-360-wired.png)
![DualSense across all tools](docs/screenshot-dualsense.png)

## How it works

HIDMaestro is a UMDF2 HID minidriver hosted by Windows' own `mshidumdf.sys`, fed input through a per-controller shared-memory section. XInput for Xbox profiles comes from a companion device that registers the XUSB interface. Bluetooth identity, the &IG_ enumerator behavior, the separate-trigger descriptor trick, the WGI admission path, and the XInput slot allocator are all documented in [docs/INTERNALS.md](docs/INTERNALS.md).

Why user mode is enough: the HID class driver already lives in the kernel (`mshidumdf.sys`), XInput discovery uses a device interface a user-mode driver can register, GameInput reads HID reports rather than driver internals, and bus type and VID/PID are settable from user mode. See [Why UMDF2 Is Enough](docs/INTERNALS.md#why-umdf2-is-enough).

## Known Limitations

- **Windows optimized.** HIDMaestro is built on UMDF2, `mshidumdf`, and the Windows HID/XInput/WGI stack. There is no Linux or macOS build.
- **Output is delivered, not routed to hardware.** The driver accepts rumble/haptic/FFB writes and raises `OutputReceived` to the consumer. Sending those to a physical controller is the consumer's job (PadForge does this).
- **Auth-chip controllers.** PS4/PS5 online and Nintendo Switch Online require cryptographic authentication from real controller hardware. HIDMaestro cannot replicate authentication chips.
- **Vendor-specific feature reports.** LED control, calibration, and firmware-update reports vary per device and need per-controller work.
- **Anti-cheat.** Virtual devices are detectable by kernel-level anti-cheat. HIDMaestro does not hide that it is virtual.

## Security and Scope

HIDMaestro replicates the public-facing identity and input/output behavior of game controllers. It does **not** replicate cryptographic authentication, implement vendor-private protocols unless a profile adds them, bypass anti-cheat, or modify data from physical controllers.

## Credits

- **[DsHidMini](https://github.com/nefarius/DsHidMini)** by [Nefarius Software Solutions](https://nefarius.at/). HIDMaestro builds on the UMDF2 + xinputhid approach Nefarius pioneered in DsHidMini, which demonstrated that a user-mode driver framework can replace kernel-mode drivers for controller emulation on Windows. The `mshidumdf` HID proxy, `WUDFRd` reflector, and xinputhid XInput bridge are the foundation of HIDMaestro's stack.
- **[HIDAPI](https://github.com/libusb/hidapi)**: bus-type detection behavior informed the BTHLEDEVICE spoofing technique.
- **[SDL3](https://github.com/libsdl-org/SDL)**: multi-backend fallback behavior informed the &IG_ enumerator trick. SDL3 is not a dependency. HIDMaestro is validated against it.

## Donations

Knowing HIDMaestro is useful is reward enough. If you truly insist on donating, please donate to your charity of choice and bless humanity. If you can't think of one, consider [Humanitarian Services of The Church of Jesus Christ of Latter-day Saints](https://philanthropies.churchofjesuschrist.org/humanitarian-services). Also consider donating directly to the upstream projects listed above. They made all of this possible.

**My promise:** HIDMaestro will never become paid, freemium, or Patreon early-access paywalled. Free means free.

## License

MIT License. See [LICENSE](LICENSE) for details.
