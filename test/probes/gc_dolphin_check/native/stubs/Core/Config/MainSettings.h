// Stand-in for Dolphin's Core/Config/MainSettings.h: the two settings
// GCAdapter.cpp reads per channel (Slippi MainSettings.h:128-129).
#pragma once

#include "Common/Config/Config.h"
#include "Core/HW/SI/SI_Device.h"

namespace Config
{
const Info<SerialInterface::SIDevices>& GetInfoForSIDevice(int channel);
const Info<bool>& GetInfoForAdapterRumble(int channel);
}  // namespace Config
