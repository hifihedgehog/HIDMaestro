# Removing HIDMaestro from a computer

HIDMaestro 2.0.1 removes itself. `HIDMaestroUninstall.exe` does it from a prompt or an installer, and `HMUninstall` does it from inside a program that embeds HIDMaestro. Both run the same code and report the same items.

The driver, its certificate and its staging belong to every program on the machine that embeds HIDMaestro. A removal takes them from all of them. It refuses while another such program runs, and a program that runs later installs the driver again at its next `InstallDriver` or create.

## The tool

`HIDMaestroUninstall.exe` ships in the release ZIP as a NativeAOT build for x64 and for ARM64. It needs no .NET runtime. Windows asks for administrator rights when it starts.

```
HIDMaestroUninstall [--preview] [--yes] [--include-usbip] [--json]
```

| Option | Effect |
|--|--|
| none | List what would be removed and kept, ask, then remove |
| `--preview` | List only. Nothing on the machine changes |
| `--yes` | Remove without asking |
| `--include-usbip` | Also remove usbip-win2 when no install record shows HIDMaestro installed it. Never its root-hub filter |
| `--json` | Write the report as JSON on stdout. Needs `--preview` or `--yes`, since it asks no question |

| Exit code | Meaning |
|--|--|
| 0 | Removed, or a preview that nothing blocks |
| 3 | Removed, with items Windows deletes when it next starts |
| 2 | Blocked. Other programs that embed HIDMaestro are running, and nothing was changed. The report lists them |
| 1 | Failed, or did not run: an unknown option, the question answered no, or no administrator rights |

### JSON

```json
{
  "schemaVersion": 1,
  "preview": false,
  "status": "Removed",
  "exitCode": 0,
  "runningPrograms": [],
  "entries": [
    { "kind": "driver package", "location": "oem5.inf (hidmaestro.inf)", "outcome": "Removed", "reason": null }
  ]
}
```

`status` is `Removed`, `RemovedRestartPending`, `Blocked`, `Failed` or `Previewed`. `exitCode` is the code the tool exits with. `runningPrograms` is empty unless the status is `Blocked`, and then holds one object per program, such as `{ "processId": 4528, "imagePath": "C:\\Program Files\\PadForge\\PadForge.exe", "sdkVersion": "2.0.0" }`. A program's `processId` is 0 when it is known to run but its process could not be named, and `imagePath` and `sdkVersion` are null when unknown. `outcome` is `WouldRemove`, `Removed`, `RemovedAtRestart`, `Kept` or `Failed`, and `reason` says why an item was kept or how it failed. A blocked run lists no entries. A later schema adds fields and raises `schemaVersion` only when an existing field changes.

## From a program

```csharp
HMUninstallReport preview = HMUninstall.Preview();               // changes nothing
HMUninstallReport report  = HMUninstall.Run();                   // needs elevation
HMUninstallReport all     = HMUninstall.Run(new HMUninstallOptions { IncludeUsbip = true });

switch (report.Status)
{
    case HMUninstallStatus.Blocked:
        foreach (var p in report.RunningPrograms)
            Console.WriteLine($"Close {p.ImagePath ?? "process " + p.ProcessId} first.");
        break;
    case HMUninstallStatus.Failed:
        foreach (var e in report.Entries.Where(e => e.Outcome == HMUninstallOutcome.Failed))
            Console.WriteLine($"{e.Kind} {e.Location}: {e.Reason}");
        break;
}
```

`Run` works on the calling thread to its end: 1.8 s in Windows Sandbox with three controllers, longer with more devices. It throws `UnauthorizedAccessException` in a process that is not elevated. `Preview` runs without elevation and lists less there: a folder or key it cannot read is left out.

The calling program never blocks itself. Its own controllers go with everything else, as `RemoveAllVirtualControllers(HMCleanupScope.Everything, false)` removes them. Each `HMController` it holds stays a usable object with no device behind it, and should be disposed. Its owner record goes too. The program can call `InstallDriver` and create controllers again afterward in the same process.

A program such as PadForge calls it from its own uninstaller or from a settings button. An uninstaller runs `HIDMaestroUninstall.exe --yes --json`, elevated, after the program has exited, and reads the exit code. Code 2 means another HIDMaestro program is still running: the uninstaller can leave HIDMaestro in place, or ask the user to close that program and run the tool again. Code 3 needs no restart now, since the remaining files go at the next one. A settings button calls `HMUninstall.Preview()` to show what would go, then `HMUninstall.Run()`, and says that other programs on the machine that use HIDMaestro lose its driver until they next start.

## What a removal does

A run reads every item first, acts in the order below, reads the machine again, and reports each item: `Removed` when it is gone, `RemovedAtRestart` when Windows deletes it at its next start, `Kept` with the reason when it stays on purpose, and `Failed` with the reason otherwise.

1. Every HIDMaestro device record, present or not, across every device class: the ROOT and SWD parents, the XUSB companions, keyboards and mice, the HID children, and the forms older releases made. USB/IP imports are unplugged. Records Windows keeps of removed devices, which Device Manager shows only with hidden devices, go in a complete pass before and after the devices themselves.
2. The XUSB and WinExInput interface registrations under `HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses` that belong to HIDMaestro devices or to no device at all.
3. The joy.cpl `OEMName` values and GameInput mapping tables a controller changed go back to what they held before, each only while it still holds what HIDMaestro wrote. A value another program changed since stays as it is. GameInput tables that releases up to v1.11.0 wrote go.
4. Each `HMOemNameOverride.Set` record is replayed: the DirectInput and joy.cpl labels go back to their earlier values, each only while it still holds the label HIDMaestro wrote. The per-user joy.cpl label is put back in the profile of the account that runs the removal, which is the account a program's `Set` wrote to when it ran there.
5. A DeviceOverrides key, `HKLM\SYSTEM\CurrentControlSet\Control\DeviceOverrides\root#VID_xxxx&PID_yyyy[&IG_00]`, goes when its hardware ID is one the catalog's profiles create, it holds only the `Removable` value HIDMaestro writes, and no root-enumerated device of another driver carries that hardware ID.
6. HIDMaestro's OpenVR driver folder leaves the `external_drivers` list of every account's `openvrpaths.vrpath`, SYSTEM's included. The file stays, since SteamVR owns it.
7. usbip-win2, as the next section describes.
8. The driver packages `hidmaestro.inf`, `hidmaestro_xusb.inf`, the older `hidmaestro_xusbshim.inf` and the `hmwinusb_*.inf` packages, read from the driver database, which also lists a package whose `oemNN.inf` file is gone. Each goes with a plain `pnputil /delete-driver`, after the devices are gone and their driver hosts have exited, never with `/uninstall` or `/force`. A package still in use is reported `Failed` with pnputil's answer.
9. The WUDF service keys `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\HIDMaestro` and `HMXInput`, once the driver store folder their `ImagePath` names is gone, or when the package's removal left them holding no value. Windows' own `mshidumdf` and `WUDFRd` keys are never touched.
10. HIDMaestro's registry keys: `HKLM\SOFTWARE\HIDMaestro`, `HIDMaestroOwners`, `HIDMaestroRegistryLedger`, `HIDMaestroPackagesLeft` and `HIDMaestroOemOverrides`.
11. The signing certificate, `CN=HIDMaestroTestCert`, leaves LocalMachine `My`, `Root` and `TrustedPublisher`, and its private key is deleted, once no HIDMaestro driver package is left in the driver store.
12. `%ProgramFiles%\HIDMaestro` (staging, the per-process temporary folders, and folders a repair moved aside), `%ProgramData%\HIDMaestro` (the OpenVR driver, and the usbip-win2 package v1.12.0 staged), and, in every account's `%TEMP%` and in `C:\Windows\Temp`, the folders HIDMaestro wrote there through v1.12.0, by their exact names: `HIDMaestro`, `HIDMaestro_<hash>`, `HIDMaestro_usbip_<version>` and the test program's `HIDMaestroEmulate-<guid>`. Nothing is deleted through a link: a link is removed as a link. A file another program has open, such as the OpenVR driver while SteamVR runs, is deleted when Windows next starts.

A run never restarts the computer or a USB root hub, and never creates a virtual keyboard or mouse. A driver host that runs only HIDMaestro's driver and outlives its devices is ended, as the `Everything` cleanup ends it, and a host that runs any other driver is never touched.

### usbip-win2

USB/IP composite controllers ride usbip-win2's virtual host controller, which several programs install: VIIPER, DS4Windows, Handheld Companion, and users themselves. A removal takes usbip-win2's host controller, its driver package, its scheduled task, `C:\Program Files\USBip` and its Apps entry only when HIDMaestro's install record shows HIDMaestro installed it, or with `--include-usbip` (`IncludeUsbip`), and in both cases only when no other program uses it. Otherwise it unplugs HIDMaestro's own imports, removes HIDMaestro's records of them, and leaves usbip-win2 alone. A host controller that carried a device since Windows started is reported as left until the next restart.

The root-hub filter, `usbip2_filter`, always stays: its package, its service and its registration on each USB 3 root hub. Removing it restarts every USB root hub on the machine, and disabling it while it is registered stops every root hub at the next start. On a physical hub it attaches to nothing. From 2.0.1 on, HIDMaestro installs usbip-win2's host controller without the vendor installer and never installs the filter.

## What stays

- The GameInputSvc start type. HIDMaestro sets the service to start automatically when it finds it stopped, and never recorded what it was before, so it stays. The report lists it as `Kept`.
- The WinUSB package of a Wii U GameCube adapter HIDMaestro bound, while the adapter is plugged in, and while a device record Windows keeps for it names the package. Every real adapter of that model has the same instance ID, so HIDMaestro does not remove that record. Unplug the adapter and run the removal again.
- A shared value, an OEM label or a DeviceOverrides key that another program changed since HIDMaestro wrote it. A folder of HIDMaestro's that also holds files HIDMaestro did not write keeps those files and itself, and the report names them.
- The per-user joy.cpl label of another account. An OEM override set from that account's elevated program wrote its own profile, which a removal run from this account does not reach.
- usbip-win2's root-hub filter, and the VC++ runtime its installer added, which other programs share.
- What Windows records about drivers and devices: `C:\Windows\INF\setupapi.dev.log`, the Kernel-PnP and DriverFrameworks-UserMode event logs, Prefetch and Amcache. HIDMaestro never edits Windows' own logs.

A program built on HIDMaestro older than v1.12.0 leaves no sign that it runs, so a removal cannot see it and does not wait for it.

## Report kinds

| Kind | Location |
|--|--|
| `devnode` | Device instance ID |
| `interface registration` | Registry path under `HKLM` |
| `shared registry key` | A GameInput mapping table |
| `shared registry value` | A joy.cpl `OEMName` value |
| `WinUSB binding` | The adapter's device instance ID |
| `OEM name override` | The override's record under `HKLM\SOFTWARE\HIDMaestroOemOverrides` |
| `DeviceOverrides key` | Registry path under `HKLM` |
| `OpenVR registration` | The `openvrpaths.vrpath` file |
| `driver package` | `oemNN.inf (original name)` |
| `WUDF service key` | Registry path under `HKLM` |
| `registry key` | Registry path under `HKLM` |
| `certificate` | `LocalMachine\<store>\<thumbprint>` |
| `folder` | Path |
| `service setting` | GameInputSvc's start type |
| `removal step` | A step that could not run or list, with the exception |

usbip-win2's part of the removal reports its own items with kinds of its own.
