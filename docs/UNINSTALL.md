# Removing HIDMaestro from a computer

HIDMaestro 2.0.1 removes itself. `HIDMaestroUninstall.exe` does it from a prompt or an installer, and `HMUninstall` does it from inside a program that embeds HIDMaestro. Both run the same code and report the same items.

The driver, its certificate and its staging belong to every program on the machine that embeds HIDMaestro. A removal takes them from all of them. It refuses while another such program runs, and a program that runs later installs the driver again at its next `InstallDriver` or create. A program on 2.0.1 or later that starts while a removal runs waits for it to end first, as the section on what a removal does describes.

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
| `--include-usbip` | Also remove what usbip-win2's own installer left, its root-hub filter included, as the usbip-win2 section describes |
| `--json` | Write the report as JSON on stdout. Needs `--preview` or `--yes`, since it asks no question |

| Exit code | Meaning |
|--|--|
| 0 | Removed, or a preview that nothing blocks |
| 3 | Removed, with items Windows deletes when it next starts |
| 2 | Blocked by another program that embeds HIDMaestro, which the report lists. Found before the removal started, nothing was changed. Found after a program started during the removal, the removal stopped before the step that would take what that program uses, and the report says what it had done |
| 1 | Failed, or did not run: an item could not be removed, part of the machine could not be read (a preview says so too), an unknown option, the question answered no, or no administrator rights |

The tool checks for administrator rights before anything else, `--preview` included, and without them says so and exits with 1. With no option it shows the preview and asks. A preview that could not read part of the machine does not stop the removal, which reads the machine again and reports those items, exactly as `--yes` does.

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

`status` is `Removed`, `RemovedRestartPending`, `Blocked`, `Failed` or `Previewed`. `exitCode` is the code the tool exits with. `runningPrograms` is empty unless the status is `Blocked`, and then holds one object per program, such as `{ "processId": 4528, "imagePath": "C:\\Program Files\\PadForge\\PadForge.exe", "sdkVersion": "2.0.0" }`. A program's `processId` is 0 when it is known to run but its process could not be named, and `imagePath` and `sdkVersion` are null when unknown. `outcome` is `WouldRemove`, `Removed`, `RemovedAtRestart`, `Kept` or `Failed`, and `reason` says why an item was kept or how it failed. A run blocked before it started lists no entries. A later schema adds fields and raises `schemaVersion` only when an existing field changes.

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

`Run` works on the calling thread to its end: 1.3 s in Windows Sandbox for four controllers, a composite among them, longer with more devices. It throws `UnauthorizedAccessException` in a process that is not elevated. `Preview` runs without elevation and reads less there. A HIDMaestro folder it cannot open, a step that throws and a look for running programs it cannot take each come back as a failed item, and its status is then `Failed`. An account's `%TEMP%` it cannot open comes back kept, with the reason. A package record in the driver database, an account's OpenVR path registry, an account in the profile list and the moved-aside folders at the top of `%ProgramData%` that it cannot read are left out of the list.

The calling program never blocks itself. Its own controllers go with everything else, as `RemoveAllVirtualControllers(HMCleanupScope.Everything, false)` removes them. Each `HMController` it holds stays a usable object with no device behind it, and should be disposed. Its owner record goes too. The program can call `InstallDriver` and create controllers again afterward in the same process.

A program such as PadForge calls it from its own uninstaller or from a settings button. An uninstaller runs `HIDMaestroUninstall.exe --yes --json`, elevated, after the program has exited, and reads the exit code. Code 2 means another HIDMaestro program is running: the uninstaller can leave HIDMaestro in place, or ask the user to close that program and run the tool again. Code 3 needs no restart now, since the remaining files go at the next one. A settings button calls `HMUninstall.Preview()` to show what would go, then `HMUninstall.Run()`, and says that other programs on the machine that use HIDMaestro lose its driver until they next start.

## What a removal does

A run reads every item first, acts in the order below, reads the machine again, and reports each item: `Removed` when it is gone, `RemovedAtRestart` when Windows deletes it at its next start, `Kept` with the reason when it stays on purpose, and `Failed` with the reason otherwise. It looks for running HIDMaestro programs before it starts, again before the devices go, again before the OEM name overrides, and once more under the driver deploy lock before the driver packages. A program found at any of those looks stops the run there.

1. Every HIDMaestro device record, present or not, across every device class: the ROOT and SWD parents, the XUSB companions, keyboards and mice, the HID children, and the forms older releases made. USB/IP imports are unplugged. Records Windows keeps of removed devices, which Device Manager shows only with hidden devices, go in a complete pass before and after the devices themselves. usbip-win2's host controller records are left to the usbip-win2 part.
2. The XUSB and WinExInput interface registrations under `HKLM\SYSTEM\CurrentControlSet\Control\DeviceClasses` that belong to HIDMaestro devices or to no device at all.
3. The joy.cpl `OEMName` values and GameInput mapping tables a controller changed go back to what they held before, each only while it still holds what HIDMaestro wrote. A value another program changed since stays as it is. GameInput tables that releases up to v1.11.0 wrote go.
4. Each `HMOemNameOverride.Set` record is replayed: the DirectInput and joy.cpl labels go back to their earlier values, each only while it still holds the label the record says HIDMaestro wrote. The per-user joy.cpl label is put back only from the account whose program set it. From another account the machine-wide labels are put back, and the record stays with the per-user label for a removal run from that account. A record made before 2.0.1 does not say what label it wrote, so it stays with every label as it is, and the report says so.
5. A DeviceOverrides key, `HKLM\SYSTEM\CurrentControlSet\Control\DeviceOverrides\root#VID_xxxx&PID_yyyy[&IG_00]`, goes when its hardware ID is one the catalog's profiles create, it holds only the `Removable` value HIDMaestro writes, and no root-enumerated device of another driver carries that hardware ID.
6. HIDMaestro's OpenVR driver folder leaves the `external_drivers` list of the `openvrpaths.vrpath` of the account running the removal, and of every other account's at its default place, `AppData\Local\openvr` in its profile, SYSTEM's included. Another account's `VR_PATHREG_OVERRIDE` is not read. The file stays, since SteamVR owns it.
7. usbip-win2, as the next section describes.
8. The driver packages `hidmaestro.inf`, `hidmaestro_xusb.inf`, the XUSB shim `hidmaestro_xusbshim_class.inf` that v1.1.19 to v1.1.21 installed and `hidmaestro_xusbshim.inf`, the shim's name in builds before v1.1.19, and the `hmwinusb_*.inf` packages, read from the driver database, which also lists a package whose `oemNN.inf` file is gone. Each goes with `SetupUninstallOEMInf` without `SUOI_FORCEDELETE`, the delete `pnputil /delete-driver` makes without `/uninstall` or `/force`, after the devices are gone and their driver hosts have exited. A package a device still uses is reported `Failed`. A package whose provider is HIDMaestro under a name no HIDMaestro release used is listed and kept.
9. The WUDF service keys `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\WUDF\Services\HIDMaestro`, `HMXInput` and the shim's `HMXusbShim`, once no HIDMaestro driver package but a WinUSB one is left and the driver store folder their `ImagePath` names is gone, or when the package's removal left them holding no value. Windows' own `mshidumdf` and `WUDFRd` keys are never touched.
10. HIDMaestro's registry keys: `HKLM\SOFTWARE\HIDMaestro`, `HIDMaestroOwners`, `HIDMaestroRegistryLedger`, `HIDMaestroPackagesLeft` and `HIDMaestroOemOverrides`. The usbip-win2 part owns `HKLM\SOFTWARE\HIDMaestroUsbip`.
11. The signing certificate, `CN=HIDMaestroTestCert`, leaves LocalMachine `My`, `Root` and `TrustedPublisher`, once no driver package whose provider is HIDMaestro is left in the driver store, whatever its name. The copies go first, and its private key last, once the copy in `My` is gone.
12. `%ProgramFiles%\HIDMaestro` (staging, the per-process temporary folders, and folders a repair moved aside), `%ProgramData%\HIDMaestro` (the OpenVR driver, and the usbip-win2 package v1.12.0 staged) and the folders a repair moved aside beside it, and, in every account's `%TEMP%` and in `C:\Windows\Temp`, the folders HIDMaestro wrote there through v1.12.0, by their exact names: `HIDMaestro`, `HIDMaestro_<hash>`, `HIDMaestro_usbip_<version>` and the test program's `HIDMaestroEmulate-<guid>`.

Steps 8 to 12 run under the driver deploy lock. A program's driver install takes that lock when it copies or installs a package, so none installs the driver between the last look for running programs and the last file deleted. An install that finds the driver already in place takes no lock.

A program on 2.0.1 or later takes its owner claim before the first thing it does that would put HIDMaestro back or take its share of it: `InstallDriver`, every create, a cleanup, the prewarm before it stages the driver, `HMOemNameOverride.Set`, and `Clear` and `RecoverOrphans` when a record is pending, `HMVR.EnsureDriverRegistered` once it has found SteamVR, `HMVR.SetSteamVRPathHint`, `HMVR.UnregisterDriver` and `InstallUsbipBackend`. While a removal runs, that first claim waits for it to end, on the removal's own machine-wide lock, and then the program installs the driver again. The wait costs one acquire and release of that lock when no removal runs, once per process, and a process never waits on a removal it runs itself. Once claimed, the program counts as running: a removal started while it runs is blocked by it, a program that only set an OEM name override, registered the OpenVR driver or installed the USB/IP transport included. On a machine without SteamVR, `EnsureDriverRegistered` returns false and claims nothing. While a program runs `HMUninstall.Run` itself, those calls write nothing: `Set`, `HMVR.SetSteamVRPathHint`, `InstallUsbipBackend` and, with SteamVR present, `EnsureDriverRegistered` throw `InvalidOperationException`, and the others return, since the removal takes what they would. A program on v1.12.0 or 2.0.0 does not wait, and the looks for running programs above find it instead.

Files and folders are deleted from a fixed starting folder down: the profile folder, `C:\Users\Public`, `%ProgramData%`, `%SystemRoot%` or `%ProgramFiles%`. Each folder below it is opened as itself, never through a link, held so it cannot be renamed or replaced, and checked to resolve where it should. A folder on the way that is a link stops the walk, and the report keeps that `%TEMP%` with the reason. A link found under one of HIDMaestro's names, `%ProgramData%\HIDMaestro` among them, is removed as a link, and what it points to stays.

A file or folder another program has open inside `%ProgramFiles%\HIDMaestro` is deleted when Windows next starts, when every folder from `%ProgramFiles%\HIDMaestro` down to the one holding it is one only administrators can change. Each folder is checked through its own handle. A folder that cannot be opened for delete, the one the removal starts from included, is emptied through a handle that does not delete, and then waits for the next start too. A folder that keeps an item the removal could not take stays. Anything held open anywhere else, such as the OpenVR driver while SteamVR runs, stays and is reported `Failed` with the Windows error, such as `in use (Win32 error 32)`: close that program and run the removal again.

A run never restarts the computer and never creates a virtual keyboard or mouse. It restarts USB root hubs only when it takes usbip-win2's root-hub filter, as the next section describes. A driver host that runs only HIDMaestro's driver and outlives its devices is ended, as the `Everything` cleanup ends it, and a host that runs any other driver is never touched.

### usbip-win2

USB/IP composite controllers ride usbip-win2's virtual host controller, which several programs install: VIIPER, DS4Windows, Handheld Companion, and users themselves. 2.0.1 installs the host controller from the SDK's own copy of its driver package, records the install under `HKLM\SOFTWARE\HIDMaestroUsbip`, and never runs usbip-win2's installer or installs its root-hub filter.

A removal unplugs HIDMaestro's own imports. With the install record, it also takes the host controller, its driver package and its service. With `--include-usbip` (`IncludeUsbip`) it also takes what usbip-win2's own installer left, as from an SDK through 2.0.0 that ran it: the detach task, `C:\Program Files\USBip`, the Start menu entries, the Apps entry and the root-hub filter. It never runs that installer's uninstaller. Either way it takes usbip-win2 only when nothing else uses it. A host controller that has carried a device since Windows started stays, and so do its package, the install record and what the installer left: run the removal again after a restart.

The root-hub filter, `usbip2_filter`, is an extension driver registered on every USB 3 root hub. The removal deletes its package with `pnputil /delete-driver /uninstall`, after the host controller, as usbip-win2's own uninstaller does. Windows reinstalls each USB 3 root hub without the filter, which restarts the hub, so the USB devices on it disconnect for a moment. A hub Windows cannot restart while a program holds a device below it finishes at the next start, and a hub that is not plugged in is reinstalled when it next arrives. Windows marks the `usbip2_filter` service for deletion and deletes it at the next start, and the report lists it as `RemovedAtRestart`. A hub record that still names the filter with no reinstall pending is reported `Failed`, and the service then stays, since that hub would not start without it.

## What stays

- The GameInputSvc start type. HIDMaestro sets the service to start automatically when it finds it stopped, and never recorded what it was before, so it stays. The report lists it as `Kept`.
- The `hmwinusb_gamecube-adapter.inf` package of a Wii U GameCube adapter HIDMaestro bound to WinUSB, with the signing certificate in `My`, `Root` and `TrustedPublisher`, the private key of the one in `My`, and `HKLM\SOFTWARE\HIDMaestroPackagesLeft`, which names the package. They stay while a device record Windows keeps for the adapter, present or not, still names the package. On Windows 10, where the persona binds WinUSB through the package's own INF, the records outlast the persona: `USB\VID_057E&PID_0337\15/07/2014` and the record of the port it sat on. A real adapter gets the same instance ID, since every unit carries the serial 15/07/2014, so HIDMaestro removes neither record, and unplugging changes nothing. Each record is marked to install again at its next arrival. A real adapter plugged in later then gets the driver Windows ranks first for it, its HID driver, which fails with Code 10 on a WUP-028 as it does before Zadig, since the package matches no ID Windows 10 gives the adapter. Once no record names the package, a removal takes it with the certificate, its key and `HIDMaestroPackagesLeft`, and the first sweep of a later HIDMaestro program removes the package as well. Removing both records in Device Manager, with hidden devices shown, frees the package the same way.
- The device record of a composite persona whose serial is a real device's, such as the Wii U GameCube adapter's `15/07/2014`. A real device of that VID, PID and serial gets the same record, so a removal leaves it, not present, and the report lists it as `Kept`.
- A shared value, an OEM label or a DeviceOverrides key that another program changed since HIDMaestro wrote it. A folder of HIDMaestro's that also holds files HIDMaestro did not write keeps those files and itself, and the report names them.
- An OEM override record made before 2.0.1, and the per-user label of an override another account's program set, until the removal runs from that account.
- The per-user label of a second account, when two accounts both set an override for the same VID:PID under 2.0.0. Such a record names no account, and both hives hold the label, so the account that first sets or clears it from 2.0.1 on, or runs a removal, is taken as the one whose original the record holds. The other account's original was never recorded.
- Another account's OpenVR registration in a path registry its `VR_PATHREG_OVERRIDE` names.
- The `%TEMP%` folders of a profile reached through a junction or other link. The removal does not walk a link, and the report lists that `%TEMP%` as `Kept` with the reason.
- The audio endpoint records Windows keeps under `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices` for a composite's speaker and microphone. Windows' audio service owns them.
- The VC++ runtime usbip-win2's installer added, which other programs share.
- usbip-win2's root-hub filter, unless the removal includes usbip-win2.
- What Windows records about drivers and devices: `C:\Windows\INF\setupapi.dev.log`, the Kernel-PnP and DriverFrameworks-UserMode event logs, Prefetch and Amcache. HIDMaestro never edits Windows' own logs.

A program built on HIDMaestro older than v1.12.0 leaves no sign that it runs, so a removal cannot see it, and the program does not wait for the removal.

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

The usbip-win2 part reports `devnode`, `driver package`, `service`, `registry key`, `registry value`, `scheduled task`, `folder`, `file`, `Apps entry`, `hardware id` and `USB/IP import`.
