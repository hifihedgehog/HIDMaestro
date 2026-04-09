#pragma once

#include <cstdint>

// Returns a static friendly name for known VID+PID (e.g. from usb.ids); nullptr if unknown.
[[nodiscard]] const char* GetFriendlyName(uint16_t vid, uint16_t pid);
