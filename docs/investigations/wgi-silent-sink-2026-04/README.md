# WGI Silent Sink Investigation (2026-04)

Chromium/Edge `vibrationActuator.playEffect` on Win11 26200+ silently no-ops for HIDMaestro virtual controllers while working correctly on physical Xbox-family controllers. Root cause is architectural: WGI's XUSB haptic dispatch (`Windows.Gaming.Input.Gamepad.put_Vibration`) appears hard-gated on USB-bus enumeration of the target device, which ROOT-enumerated UMDF2 virtuals cannot satisfy under the project's "no kernel drivers" constraint. Formal conclusion committed as Option 3 — documented limitation.

Formal finding: [finding.md](finding.md). Brief-by-brief history with retractions and falsification table: [investigation-history.md](investigation-history.md). Preserved source + evidence manifest: [manifest.md](manifest.md).
