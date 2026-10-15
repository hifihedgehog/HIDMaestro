# HIDMaestro Probes

Diagnostic and gate probes. The first ten come from the [WGI Silent Sink investigation (2026-04)](../../docs/investigations/wgi-silent-sink-2026-04/finding.md). The rest are release gates that `test/regression/swap_regression.ps1` invokes as scenarios S24 through S84. Each probe answers a specific "does API X reach device Y with value Z" question for gamepad input/haptic dispatch on Windows. Preserved here as a reusable toolkit for future HIDMaestro profile work or Windows build regressions.

The probes in the table below target HIDMaestro virtuals by default and work against physical Xbox-family controllers too. Many of the later gate probes are pure encoder or schema checks that create no device at all. Build artifacts (`bin/`, `obj/`, `.pdb`, `.ilk`) are not committed; run `dotnet build` (C#) or the `build_*.cmd` script (C++) to produce executables.

## Canonical probes by question type

| Question | Canonical probe |
|---|---|
| "What is the XUSB wire format on this driver path?" | [xinput_byte_probe](xinput_byte_probe/): deterministic `XInputSetState` with known wLeft/wRight pairs |
| "Does WGI `put_Vibration` reach this device?" | [wgi_rgc_ffb_probe](wgi_rgc_ffb_probe/) (C#, easier output) |
| "Does this API require a focused-foreground caller?" | [focus_test](focus_test/): Win32 window + `GetForegroundWindow == own_hwnd` gating |
| "Which of WGI `put_Vibration` vs `XInputSetState` reaches this device?" | [wgi_vs_xinput_ab](wgi_vs_xinput_ab/): user-paced A/B test with two time windows |
| "What does GameInput enumerate for this device's rumble support?" | [gi_enumall](gi_enumall/): `IGameInput::RegisterDeviceCallback` with motor bitmap printed |
| "Can I override cross-process gamepad dispatch via a user-mode custom factory?" | [wgi_custom_factory](wgi_custom_factory/): `GameControllerFactoryManager::RegisterCustomFactoryForHardwareId`/`ForXusbType` |
| "Do our HID output report handlers (IOCTL_HID_WRITE_REPORT etc.) fire for this profile?" | [hid_output_report_probe](hid_output_report_probe/) |
| "What source-file strings are embedded in a Windows runtime DLL?" | [wgi_dll_string_scan.ps1](wgi_dll_string_scan.ps1) |
| "Does this profile's VID/PID surface in DirectInput's enumeration?" | [dinput_enum](dinput_enum/): `IDirectInput8::EnumDevices(DI8DEVCLASS_GAMECTRL)` with WPF host (DInput needs window focus) |
| "Does WGI see this device at all (presence-only, no vibration fired)?" | [wgi_read_probe](wgi_read_probe/): native C++ `Windows.Gaming.Input.Gamepad` enumeration sampler |
| "What does WGI read from this profile at rest and on each control, and which GameInput index moves?" | [wgi_mapping_check](wgi_mapping_check/): `--explore <profile>` prints the RawGameController and Gamepad readings side by side |
| "Does a virtual leave the registry keys a real device of the same VID and PID uses as it found them?" | [registry_ownership_check](registry_ownership_check/) |
| "Does a keyboard or mouse reach Raw Input as its kind, and does every report arrive?" | [kbm_live_check](kbm_live_check/): `--key-diag <profile>`, `--wheel-diag <profile>` and `--leds-diag` for one device at a time |
| "Where does a keyboard or mouse descriptor put each field?" | [kbm_offline_check](kbm_offline_check/): no device |
| "Is another OpenVR driver registered under HIDMaestro's driver name, and does HIDMaestro register its own as vrpathreg would without running it?" | [vr_driver_conflict_check](vr_driver_conflict_check/): S68. vrpathreg_golden.json holds the bytes the shipped vrpathreg.exe wrote for each case. Elevated, it also registers with a planted vrpathreg.exe in the SteamVR folder, checks the write has the user's rights, and checks UnregisterDriver throws and keeps the gate when the registry cannot be updated |
| "Do two programs that embed HIDMaestro leave each other's controllers, driver and staging alone?" | [coexist_check](coexist_check/): sections A to H, J to V, X, Y and Z are S71, section I is S72. In sections I, X and Z one thread impersonates a program that is not elevated, and no process is started for it. Section Z covers the transport lock, the registry ledger's lock beside a WinUSB restore, the WinUSB package under the deploy lock, a binding that follows a re-import, and pnputil run from System32. Each program is a peer process, and older releases run as peers built against their own HIDMaestro.Core.dll |
| "What does the bundled usbip-win2 install leave on a clean machine, and does it run over an existing one?" | [usbip_install_check](usbip_install_check/): runs itself inside Windows Sandbox, started without a window. S73. Reads the submitted state back from the composite, with a neutral-versus-neutral control |
| "Does HIDMaestro.Core work in a NativeAOT app, and in a trimmed single-file app?" | [aot_smoke](aot_smoke/): one source published both ways for x64 and ARM64. S74 and S75. Each reads the submitted state back from the HID interface, and the publish fails on any IL warning. Each run also compares two neutral windows, which must differ in nothing. `--neutral-only` submits the neutral state in the pressed window, and the read-back check must then fail. `--startup` times process start to catalog loaded |
| "Does source-generated JSON read every profile the way v1.12.0 did?" | [json_parity_check](json_parity_check/): no device. S76 also fails when an enum the JSON contract reaches has no converter, or a type the profile model can deserialize has no generated metadata. `--time <runs>` times the full-catalog parse both ways |
| "Do PnP notifications reach the SDK's create and dispose waits?" | [pnp_notify_check](pnp_notify_check/): S77. Counts events by filter kind, so the device-interface filter is judged apart from the instance filters |
| "Does a build carry what it reports, and refuse what it lacks?" | [build_info_check](build_info_check/): S78. The release build's four INFs must carry one DriverVer stamp. Its project build also makes a variant without the ARM64, USB/IP and OpenVR payloads |
| "Does the GameCube adapter persona serve a real WUP-028's bytes?" | [gc_descriptor_check](gc_descriptor_check/): S79. As served, as Windows enumerates them before any driver, and through WinUSB. Also refuses one malformed copy of the persona per device-model check, before anything machine-wide. `--offline` runs the catalog and malformed checks alone, unelevated. Builds against b4d29d9, reaching newer members by reflection |
| "Does the persona answer commands and polls as the real adapter does?" | [gc_protocol_check](gc_protocol_check/): S80, through WinUSB. Also drives the generic port and protocol model in process on exact clock ticks, checks a Dispose beside a thread inside the submit lock, and idle frames on a device with ports. `--offline` runs the in-process engine checks alone, unelevated |
| "Does BindWinUsb reach one virtual adapter and no real one, and go back?" | [gc_winusb_check](gc_winusb_check/): S81. `child` mode is the program it kills. Also checks the lock order of the ledger's lock and of the staging lock under the deploy lock |
| "Do Dolphin and Slippi read the persona as they read a real adapter?" | [gc_dolphin_check](gc_dolphin_check/): S82. `native\build.ps1` compiles each emulator's adapter code unmodified from the reference clones. `hold <seconds>` keeps a bound persona up for a manual run, `--libusb-log <level>` writes libusb's log, `measure <runs> [dolphin|slippi]` repeats only the latency and rate measurements and pools them |
| "What does SDL do with the persona?" | [gc_sdl_check](gc_sdl_check/): S83. `native\build.ps1` builds SDL 3.4.18 with libusb and checks the official SDL3.dll's hash. `measure <runs>` pools the latency and rate measurements |
| "Does the in-process USB/IP server answer the wire as usbip-win2 expects, and does a connection's teardown racing Dispose stay safe?" | [usbip_server_check](usbip_server_check/): S43. `teardown` runs the teardown checks alone. Also the handshake: an import from user mode is refused, the device list still answers it, a connection is closed at its handshake deadline, past the limit of connections in their handshake a new one is closed at once, and a failed import reply leaves the device free. `--offline` runs, without elevation, the connection owner lookup under a flood of loopback connections and after the peer closes, the match of an import's service string to a port, and the audio engine's checks, and S43 runs it before the live checks |
| "Does a composite persona work through the real USB stack, and who opens the import?" | [usbip_e2e_check](usbip_e2e_check/): S45. The host controller's end of the import is owned by the System process, and its import attaches while user-mode connections hold every handshake slot. A device attached during the create's stale sweep keeps its import, and a create whose device another import took first leaves no import behind |
| "Do the submit paths allocate, and what do creates, claims and frames cost before and after a change?" | [perf_budget_check](perf_budget_check/): `gate` is S84, a USB/IP device's three send paths and its output and audio transfers included. `census` checks, without elevation, that the census walk over Enum keys it cannot read gives what the configuration manager lists. It tells a fallback from a skip only while a HIDMaestro devnode is present, so S84 runs it with an Xbox 360 up, after checking that the census's own walk of HKLM's Enum keys completes without the fallback. The other modes measure, `usbip` a USB/IP device's frames and transfers and the protocol engine's lock, and `-p:HmBase=true` builds it against an older tree for the comparison |
| "Does an exception a program's event handler throws stop another handler, the SDK's handling of the event, or the program?" | [handler_isolation_check](handler_isolation_check/): S85, for the output thread's events, and for a handler still running when Dispose's join of the output thread gives up. The adapter ports' `OutputChanged` is checked in S80, the USB audio events by `usbip_server_check`, `HapticReceived` by `vr_controller_smoke` (S50), and the Switch 2 Pro's decode by `switch2_composite_check`, each with a throwing handler ahead of its own |
| "Is any DllImport left, does every method an SDK message names exist, and do the XInput slot count and the SetupAPI enumeration still work?" | [interop_decl_check](interop_decl_check/): S87. `static` is offline, `live` needs the bench |

## Near-duplicates and when to use which

- **[native_wgi_vibration](native_wgi_vibration/)** (C++ WRL) and **[wgi_rgc_ffb_probe](wgi_rgc_ffb_probe/)** (C# WinRT projection) both call `Gamepad.Vibration = ...` on every enumerated gamepad. **Use the C# one by default**: easier output, faster to iterate. The C++ one exists specifically to rule out CLR/WinRT-projection as a variable in the dispatch path; keep for methodology-debt reasons.
- **[focus_test](focus_test/)** answers "is the API focus-gated?" **[wgi_vs_xinput_ab](wgi_vs_xinput_ab/)** answers "which API reaches this device?" Different questions, both canonical, both kept.

## Build prerequisites

- **C# probes:** .NET SDK 10+ with `net10.0-windows10.0.26100.0` target framework. `dotnet build -c Release` in each probe directory.
- **C++ probes:** Visual Studio 2022+ with C++ workload and Windows SDK 10.0.26100.0. Each `build_*.cmd` invokes `vcvarsall.bat amd64` and calls `cl.exe` with the WDK include/lib paths hardcoded. Edit the VS path in the .cmd if your VS install differs.
- **PowerShell probe:** `pwsh` or `powershell` 5.1+.

## Running probes safely

- HID/XInput/WGI/GameInput probes that call `SetRumbleState`/`put_Vibration` will actually move motors on connected physical controllers. Keep them at a reasonable vibration level (these probes use 100%) or run with the controller on a soft surface.
- `focus_test` takes over its Win32 window's foreground focus to satisfy the API's focus gate. Switching away stops rumble dispatch (that's the test design).
- `wgi_custom_factory` registers a process-scoped custom factory. Registration is cleared on process exit.

## See also

- [finding.md](../../docs/investigations/wgi-silent-sink-2026-04/finding.md): the formal WGI-silent-sink finding these probes produced
- [investigation-history.md](../../docs/investigations/wgi-silent-sink-2026-04/investigation-history.md): brief-by-brief history including which probe answered which hypothesis
- [evidence/](../../docs/investigations/wgi-silent-sink-2026-04/evidence/): sample probe outputs preserved as investigation receipts
