// Stand-in for Dolphin's Core/HW/SystemTimers.h. Read alongside CoreTiming
// only while emulation runs.
#pragma once

#include "Common/CommonTypes.h"

namespace SystemTimers
{
class SystemTimersManager
{
public:
  u32 GetTicksPerSecond() const { return 486000000; }
};
}  // namespace SystemTimers
