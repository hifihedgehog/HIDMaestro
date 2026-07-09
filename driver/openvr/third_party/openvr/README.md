# Vendored OpenVR driver header

`openvr_driver.h` and `LICENSE` (BSD-3-Clause) are vendored verbatim from
ValveSoftware/openvr at commit `0924064316de3effbcd1acf1e309182a2deb1c05`
(OpenVR SDK 2.15.6).

The header is fully self-contained for server drivers: every driver-context
accessor (`VRServerDriverHost()`, `VRDriverInput()`, `VRProperties()`, ...)
is an inline function over the header-static `VRDriverContext()`, and
`COpenVRDriverContext::InitServer` is defined inline. No `openvr_api.lib`
link and no `openvr_api.dll` runtime dependency.

Vendoring one header replaces the alternative of a 635 MB submodule.
To update: copy `headers/openvr_driver.h` from the desired upstream tag and
record the new commit here.
