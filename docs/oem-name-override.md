# joy.cpl / DirectInput OEM-name override

`HMOemNameOverride` replaces the label that `joy.cpl` and DirectInput consumers show for a given USB VID:PID. Use it when you want a HIDMaestro virtual (or any device sharing that VID:PID) to display a specific name in joy.cpl and DirectInput, overriding any Windows-shipped pre-populated label for that VID:PID.

## Why the override exists

The label is sourced from **three** registry locations, not one, and Windows pre-populates at least one of them for common clone PIDs. Writing just one path isn't enough — the preload in a higher-priority path wins.

```
(1) HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput\VID_####&PID_####\OEM\"OEM Name"
    value name has a SPACE. Read by DirectInput consumers (DIPROP_PRODUCTNAME).

(2) HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_####&PID_####\"OEMName"
    value name has NO space. Read by the MME joy.cpl applet.

(3) HKCU\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_####&PID_####\"OEMName"
    Per-user. Takes PRECEDENCE over (2) for joy.cpl. Windows preloads this
    for common clone VID:PIDs (e.g. 0079:0006 -> "PC TWIN SHOCK Gamepad").
```

Empirical finding on Windows 11: writing only (1) updates `DIPROP_PRODUCTNAME` for native DirectInput consumers but leaves joy.cpl showing the HKCU preload. `HMOemNameOverride.Set` writes all three targets in a single transaction so joy.cpl and DirectInput agree.

The HID `productString` on a HIDMaestro profile controls what `IOCTL_HID_GET_STRING` returns, but it does not override any of the three OEM table paths. To control the joy.cpl / DirectInput display you have to write those entries directly.

## API surface

All methods are static and require admin (HKLM + HKCU write). They throw `UnauthorizedAccessException` otherwise.

```csharp
public static class HMOemNameOverride
{
    public static void Set(ushort vid, ushort pid, string label);
    public static void Clear(ushort vid, ushort pid);
    public static int  RecoverOrphans();
    public static IReadOnlyList<HMOemNameOverrideEntry> ListActive();
}
```

- `Set` captures the prior value of each of the three targets into a HIDMaestro-owned pending record, then writes the new label to all three.
- `Clear` restores each of the three targets independently from the pending record and drops the record. No-op if no prior `Set` is tracked.
- `RecoverOrphans` replays every pending record from a prior process and restores the corresponding targets. Returns how many were restored.
- `ListActive` enumerates tracked overrides for diagnostics.

## Crash-safe state machine

Every `Set` is a two-phase write:

1. Capture the existing values of all three target paths into `HKLM\SOFTWARE\HIDMaestroOemOverrides\VID_####&PID_####` with:
   - `OriginalOemName` / `OriginalKeyExisted` (target 1 — DirectInput)
   - `OriginalJoystickOemName_HKLM` / `OriginalJoystickKeyExisted_HKLM` (target 2)
   - `OriginalJoystickOemName_HKCU` / `OriginalJoystickKeyExisted_HKCU` (target 3)
   - `ClaimedAtFileTime` (REG_QWORD)
2. Write the new label to all three target paths.

If a consumer crashes between step 1 and step 2, the record's targets match what's already on disk, so `RecoverOrphans` is an idempotent no-op for each field. If a crash happens after step 2, the record drives a full three-way restore on next startup. Every claim, release, and recovery sweep is serialized by the `Global\HIDMaestro-OEM-Recovery` named mutex.

**Backward compatibility**: records written by a previous build that only tracked the DirectInput target are still replayed correctly — missing Joystick fields are treated as "no restore needed" for that target (which is the correct default, since that earlier build did not write those targets to begin with).

**Sibling-path design**: the pending hive lives at `HKLM\SOFTWARE\HIDMaestroOemOverrides`, deliberately NOT under `HKLM\SOFTWARE\HIDMaestro`. `DeviceOrchestrator.RemoveAllVirtualControllers` recursively wipes the HIDMaestro subtree as part of normal cleanup, which would otherwise silently drop pending records on every SDK restart.

## Usage pattern

```csharp
using HIDMaestro;

// Once at consumer startup, before creating any virtuals:
int recovered = HMOemNameOverride.RecoverOrphans();

using var ctx = new HMContext();
ctx.LoadDefaultProfiles();
ctx.InstallDriver();

var profile = ctx.GetProfile("generic-dinput-gamepad")!;
using var ctrl = ctx.CreateController(profile);

// Override joy.cpl / DirectInput label while the virtual is live
HMOemNameOverride.Set(profile.VendorId, profile.ProductId, "My Custom Label");

// ... run ...

// Restore on clean teardown
HMOemNameOverride.Clear(profile.VendorId, profile.ProductId);
```

If the process is force-killed before `Clear` runs, the next startup's `RecoverOrphans` call restores all three target paths to their pre-override state. Safe to call `RecoverOrphans` unconditionally at every startup.

## Interaction with default HIDMaestro profile behavior

When a virtual is created, `DeviceOrchestrator` also writes both HKLM and HKCU Joystick\OEM paths using the profile's `DeviceDescription ?? ProductString` as the label. That sets a sensible default so joy.cpl shows the profile's intended name for fresh virtuals, without any explicit `HMOemNameOverride.Set` call. `HMOemNameOverride.Set` is still needed to force a label different from the profile's own string, or to override what Windows has preloaded before the virtual first came up.

## Caveats

- **Scope of the HKCU write**: per-calling-user. On a single-user workstation this is invisible. On a multi-user machine, only the user who called `Set` sees the joy.cpl label change; other users still see their own HKCU preloads. The HKLM DirectInput path still carries the override for DirectInput consumers regardless of user.
- **Global scope of the HKLM writes**: per VID:PID, not per device instance. If a real device with the same VID:PID is plugged in while the override is active, DirectInput consumers see the override label for it too. That is usually fine, because the VID:PID is why both devices share a label anyway.
- **Caching**: joy.cpl and DirectInput cache OEM names per-process on first device enumeration. A joy.cpl window that was already open when the override changes keeps showing the stale label until it is closed and re-opened. Games that were already running behave the same way. Set before opening joy.cpl or launching a game.
- **Multi-instance consumers**: `RecoverOrphans` assumes every pending record on startup is stale. If a second instance of the consumer could be running concurrently, it would clobber the first instance's overrides. Enforce single-instance execution in the consumer (named mutex) and the situation cannot arise.

## Verifying from the CLI

The `HIDMaestroTest` binary exposes the same API for smoke testing:

```
HIDMaestroTest oem set 0x0079 0x0006 "My Custom Label"
HIDMaestroTest oem list
HIDMaestroTest oem clear 0x0079 0x0006
HIDMaestroTest oem recover
```

After `set`, check all three paths:

```
reg query "HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput\VID_0079&PID_0006\OEM" /v "OEM Name"
reg query "HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_0079&PID_0006" /v OEMName
reg query "HKCU\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_0079&PID_0006" /v OEMName
```

All three should show your label. Open joy.cpl (after closing any pre-existing window) and it will reflect the HKCU value. `clear` reverts all three to their captured originals.
