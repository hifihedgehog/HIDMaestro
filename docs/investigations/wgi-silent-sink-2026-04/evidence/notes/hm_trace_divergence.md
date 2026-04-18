# WGI 045E-branch divergence — three hypotheses to differentiate via F310 vs real 360 trace

Per Opus external review (2026-04-17, corrected):
- F310 (046D:C21D) in XInput mode binds to xusb22 via USB\MS_COMP_XUSB10.
- Real Xbox 360 wired (045E:028E) binds to xusb22 via USB\VID_045E&PID_028E directly.
- Both present XUSB interface {EC87F1E3-...}, both go through xusb22 kernel driver.
- A/B trace tells us WHERE WGI's 045E hardcoded branch (if any) actually checks.

## If WGI creates Gamepad wrapper + working put_Vibration for BOTH devices
→ No VID-specific branch. Our xusbshim approach is viable; we just need to match
  the interface-class + xusb22-compatible-IOCTL-response contract. Any fix that
  makes our virtual behave identically at the IOCTL surface should work.

## If WGI wraps real 360 as Gamepad but F310 only as RawGameController
→ There IS a hardcoded 045E branch. The gate could be at any of:

### Gate candidate 1: IOCTL_XUSB_GET_INFORMATION response bytes
F310's xusb22 might return different bytes than a real 360's xusb22:
- Version word (should be 0x0101 for both)
- Subtype byte (info[3]) — real 360 returns 0x01; F310 may return 0x01 or something else
- Capability flags (if any in this struct)
→ Test: API Monitor or ETW capture of DeviceIoControl output buffer. Compare.
→ Fix if divergent: our xusbshim/HMCOMPANION returns EXACTLY the real-360 bytes.

### Gate candidate 2: CM_Get_Device_Interface_PropertyW on DEVPKEY_Device_HardwareIds
WGI may pull hardware IDs from the interface and look for VID_045E specifically:
- F310's interface path: `\\?\USB#VID_046D&PID_C21D\...#{EC87F1E3-...}`
- Real 360 path: `\\?\USB#VID_045E&PID_028E\...#{EC87F1E3-...}`
- Our virtual: `\\?\HID#VID_045E&PID_028E&IG_00\...#{EC87F1E3-...}` (via xusbshim)
→ Test: ProcMon capture with filter "Operation contains CM_Get" during enumeration.
→ Fix: ensure our virtual's interface path contains VID_045E in the symlink.
  Our xusbshim on HID child registers the interface with that VID embedded in the
  HID child's hardware ID — should already be present. Verify via enumeration.

### Gate candidate 3: Parent device class
F310 sits under "USB (Universal Serial Bus controllers)" / usbhub.sys.
Real 360 wired sits under **XboxComposite class** (ClassGuid {D61CA365-...}).
→ Test: Device Manager → F310 → Parent; real 360 → Parent. Note device class
  of each parent.
→ If real 360's parent is XboxComposite and F310's is plain USB, WGI may check
  parent class as part of its Gamepad criteria for 045E.
→ Fix: our virtual's HMCOMPANION is already Class=XnaComposite (same parent
  class as xusb22-bound devices). xusbshim on HID child has HidClass parent.
  If WGI walks up to find XboxComposite, xusbshim would need its parent
  (our ROOT device) to also be XboxComposite-class — not HIDClass.

## How to capture the evidence

Filter the ProcMon capture for these specific operations:
- `CreateFile` with Path containing `ec87f1e3`
- `DeviceIoControl` with Path containing `ec87f1e3`
- `RegOpenKey` containing `Enum\USB\VID_` (VID lookups)
- `CM_Get_Device_Interface_PropertyW` calls
- `CM_Get_Class_Property` calls (class-level checks)

API Monitor hook for:
- `DeviceIoControl` (buffer contents)
- `SetupDiGetDeviceProperty*`
- `CM_Get_DevNode_PropertyW`

Focus on **first 2 seconds** after connecting the device — that's when WGI's
gamepad factory runs its criteria and decides whether to create a Gamepad
wrapper. Vibration dispatch only matters if wrapper exists.

## A/B procedure (tomorrow, F310) + (Monday, real 360)

1. Close Edge / Chrome completely (forces re-enumeration on next launch).
2. Start ProcMon with filter preset.
3. Plug in F310.
4. Wait for "Device ready" tone.
5. Launch Edge. Navigate to gamepad-tester.com vibration test.
6. Press a button on F310 (user-gesture gate).
7. Click "Vibration, infinite".
8. Wait ~3 seconds. Stop ProcMon.
9. Save as `trace-f310.PML`.
10. Unplug F310. Plug real 360. Repeat from step 2-8.
11. Save as `trace-360.PML`.
12. `windbg` or ProcMon compare: diff the two captures. Any WGI DLL call
    that fires for 360 but NOT F310 is the 045E-branch evidence.

The answer to whether user-mode 045E impersonation is viable comes out of
this comparison. If they trace identically (both work), xusbshim is on the
right track. If they diverge, the divergence point tells us what else to
satisfy.
