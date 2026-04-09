#pragma once
#include "gamepad_state.h"
#include <cstdint>

constexpr uint16_t XboxWireless_VendorId = 0x045e;
constexpr uint16_t XboxWireless_ProductId = 0x02ff;

[[nodiscard]] bool XboxWireless_IsDevice(uint16_t vendorId, uint16_t productId);

// pp: PHIDP_PREPARSED_DATA from Raw Input or HIDAPI. Included code includes hidpi.h and casts.
void XboxWireless_ApplyRightTrigger(uint16_t vendorId, uint16_t productId, bool sony,
                                    GamepadState& gs, void* pp,
                                    const char* report, unsigned long rLen, unsigned short numLinkCollectionNodes);
