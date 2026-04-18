# HIDMaestro Probes

Diagnostic tools built during the [WGI Silent Sink investigation (2026-04)](../../docs/investigations/wgi-silent-sink-2026-04/finding.md). Each probe answers a specific "does API X reach device Y with value Z" question for gamepad input/haptic dispatch on Windows. Preserved here as a reusable toolkit for future HIDMaestro profile work or Windows build regressions.

All probes target HIDMaestro virtuals by default but work against physical Xbox-family controllers too. Build artifacts (`bin/`, `obj/`, `.pdb`, `.ilk`) are not committed; run `dotnet build` (C#) or the `build_*.cmd` script (C++) to produce executables.

## Canonical probes by question type

| Question | Canonical probe |
|---|---|
| "What is the XUSB wire format on this driver path?" | [xinput_byte_probe](xinput_byte_probe/) — deterministic `XInputSetState` with known wLeft/wRight pairs |
| "Does WGI `put_Vibration` reach this device?" | [wgi_rgc_ffb_probe](wgi_rgc_ffb_probe/) (C#, easier output) |
| "Does this API require a focused-foreground caller?" | [focus_test](focus_test/) — Win32 window + `GetForegroundWindow == own_hwnd` gating |
| "Which of WGI `put_Vibration` vs `XInputSetState` reaches this device?" | [wgi_vs_xinput_ab](wgi_vs_xinput_ab/) — user-paced A/B test with two time windows |
| "What does GameInput enumerate for this device's rumble support?" | [gi_enumall](gi_enumall/) — `IGameInput::RegisterDeviceCallback` with motor bitmap printed |
| "Can I override cross-process gamepad dispatch via a user-mode custom factory?" | [wgi_custom_factory](wgi_custom_factory/) — `GameControllerFactoryManager::RegisterCustomFactoryForHardwareId`/`ForXusbType` |
| "Do our HID output report handlers (IOCTL_HID_WRITE_REPORT etc.) fire for this profile?" | [hid_output_report_probe](hid_output_report_probe/) |
| "What source-file strings are embedded in a Windows runtime DLL?" | [wgi_dll_string_scan.ps1](wgi_dll_string_scan.ps1) |

## Near-duplicates and when to use which

- **[native_wgi_vibration](native_wgi_vibration/)** (C++ WRL) and **[wgi_rgc_ffb_probe](wgi_rgc_ffb_probe/)** (C# WinRT projection) both call `Gamepad.Vibration = ...` on every enumerated gamepad. **Use the C# one by default** — easier output, faster to iterate. The C++ one exists specifically to rule out CLR/WinRT-projection as a variable in the dispatch path; keep for methodology-debt reasons.
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

- [finding.md](../../docs/investigations/wgi-silent-sink-2026-04/finding.md) — the formal WGI-silent-sink finding these probes produced
- [investigation-history.md](../../docs/investigations/wgi-silent-sink-2026-04/investigation-history.md) — brief-by-brief history including which probe answered which hypothesis
- [evidence/](../../docs/investigations/wgi-silent-sink-2026-04/evidence/) — sample probe outputs preserved as investigation receipts
