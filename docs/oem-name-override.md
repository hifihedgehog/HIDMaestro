# DirectInput OEM-name override

`HMOemNameOverride` replaces the label that `joy.cpl` and other DirectInput UIs show for a given USB VID:PID. Use it when you want a HIDMaestro virtual to display a specific name in DirectInput consumers regardless of what Windows' built-in OEM table says.

## Why the override exists

DirectInput reads an OEM-name table at

```
HKLM\SYSTEM\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput\VID_####&PID_####\OEM\OEM Name
```

before it asks the device for its `iProduct` string. Windows ships that table pre-populated for a number of well-known VID:PIDs. The most common example is VID `0x0079` / PID `0x0006`, which is pre-labeled **"PC TWIN SHOCK Gamepad"** no matter what the device's HID descriptor actually reports; this is the canonical DragonRise clone entry, and any virtual emulating that VID:PID inherits the label.

The HID `productString` field on a HIDMaestro profile controls what `IOCTL_HID_GET_STRING` returns, but it does not override DirectInput's pre-populated label. To control the joy.cpl display you have to mutate that registry entry directly.

## API surface

All three methods are static and require admin (HKLM write access). They throw `UnauthorizedAccessException` otherwise.

```csharp
public static class HMOemNameOverride
{
    public static void Set(ushort vid, ushort pid, string label);
    public static void Clear(ushort vid, ushort pid);
    public static int  RecoverOrphans();
    public static IReadOnlyList<HMOemNameOverrideEntry> ListActive();
}
```

- `Set` captures the prior DirectInput value to a HIDMaestro-owned registry record, then writes the new label.
- `Clear` restores whatever was captured for the given VID:PID and drops the record. No-op if no prior `Set` is tracked.
- `RecoverOrphans` replays every pending record from a prior process and restores the corresponding DirectInput keys. Returns how many were restored.
- `ListActive` enumerates tracked overrides for diagnostics.

## Crash-safe state machine

Every `Set` is a two-phase write:

1. Capture the existing DirectInput value (or its absence) into `HKLM\SOFTWARE\HIDMaestroOemOverrides\VID_####&PID_####` with:
   - `OriginalOemName` (REG_SZ, may be absent)
   - `OriginalKeyExisted` (REG_DWORD)
   - `ClaimedAtFileTime` (REG_QWORD)
2. Write the new label to the DirectInput `OEM Name` value.

If a consumer crashes between step 1 and step 2, the record's target equals what DirectInput already has, so `RecoverOrphans` is an idempotent no-op. If a crash happens after step 2, the record drives a real restore on next startup. Every claim, release, and recovery sweep is serialized by the `Global\HIDMaestro-OEM-Recovery` named mutex.

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

// Override joy.cpl label while the virtual is live
HMOemNameOverride.Set(profile.VendorId, profile.ProductId, "My Custom Label");

// ... run ...

// Restore on clean teardown
HMOemNameOverride.Clear(profile.VendorId, profile.ProductId);
```

If the process is force-killed before `Clear` runs, the next startup's `RecoverOrphans` call restores the pre-override label. Safe to call `RecoverOrphans` unconditionally at every startup; it is a no-op if no records exist.

## Caveats

- The override is global per VID:PID. If a real device with the same VID:PID is connected while your override is active, it shares the label. That is almost always fine because the VID:PID is the reason both devices share a label anyway.
- DirectInput caches OEM names per-process on first device enumeration. Games that were already running when the override changes may keep showing the stale label until they re-enumerate. Setting overrides before a game launches is the reliable pattern; consumers that start before games are already in the right order.
- Multi-instance consumers need a single-instance mutex of their own. `RecoverOrphans` assumes every pending record is stale on startup. If a second instance of the consumer could be running concurrently, it would clobber the first instance's overrides. The simplest fix is a consumer-level single-instance check (via named mutex) that prevents the situation.

## Verifying from the CLI

The `HIDMaestroTest` binary exposes the same API for smoke testing:

```
HIDMaestroTest oem set 0x0079 0x0006 "My Custom Label"
HIDMaestroTest oem list
HIDMaestroTest oem clear 0x0079 0x0006
HIDMaestroTest oem recover
```

After `set`, open `joy.cpl` in the Control Panel and plug in any device matching that VID:PID (or create a HIDMaestro virtual with it) — the label will match the `set` argument. `clear` reverts. `recover` is what you want to call after force-killing a prior run to verify the state machine self-heals.
