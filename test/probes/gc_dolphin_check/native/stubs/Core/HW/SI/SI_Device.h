// Stand-in for Dolphin's Core/HW/SI/SI_Device.h: GCAdapter.cpp compares a
// channel's configured device with the Wii U adapter, and nothing else.
#pragma once

namespace SerialInterface
{
enum SIDevices : int
{
  SIDEVICE_NONE,
  SIDEVICE_WIIU_ADAPTER,
};
}  // namespace SerialInterface
