# ETW Extract — msedge.exe gaming device file accesses

Filtered extract from a 1.4 GB kernel ETW trace captured during a Chromium `vibrationActuator.playEffect` click targeting HIDMaestro's virtual Xbox 360 Wired controller on hardwaretester.com.

## Trace parameters

- Tool: `xperf.exe -on PROC_THREAD+LOADER+PROFILE+FILEIO+FILENAME -stackwalk Profile+FileCreate`
- Raw ETL: 1,451,491,328 bytes (`c:\tmp\ChromiumTrace\kstack.etl`, discarded)
- Extraction: `xperf -i kstack.etl -a filename -o file_access.csv` → grep for gaming-device keywords

## What the extract shows

msedge.exe accessed gaming-stack files during the Chromium click:
- `\Windows\System32\drivers\xinputhid.sys`
- `\Windows\System32\GameInputSvc.exe`
- `\Windows\System32\xboxgipsynthetic.dll`
- `\Windows\System32\XboxGipRadioManager.dll`
- `\Windows\INF\xusb22.PNF`
- `\Windows\INF\xinputhid.inf`
- Various DriverStore FileRepository entries for hidmaestro_xusbshim_class.inf (our filter)

## What the extract does NOT show

- Any `\\?\HID#{GUID}` or `\\?\XUSB#...` device-interface symlink opens. DeviceIoControl against already-open handles doesn't trigger a FILEIO event at kernel level, so handle-based IOCTL dispatches aren't captured by `-a filename` summary.
- Any motor-bearing SET_STATE IOCTL content. That's captured separately in our driver's `xusbshim_log.txt` (see `evidence/chromium-silent-sink-log.txt`).

## Cited in finding.md

The extract supports the architectural framing. Full msedge module-load list is also in the finding — `xinput1_4.dll`, `Windows.Gaming.Input.dll`, `HID.DLL` loaded; `GameInput.dll` / `XInputOnGameInput.dll` NOT loaded.

See `msedge-device-files.txt` for the full extracted list.
