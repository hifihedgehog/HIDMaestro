// Stand-in for Dolphin's Core/CoreTiming.h. GCAdapter::Init reads the tick
// count only while emulation runs, which it never does here.
#pragma once

#include "Common/CommonTypes.h"

namespace CoreTiming
{
class CoreTimingManager
{
public:
  u64 GetTicks() const { return 0; }
};
}  // namespace CoreTiming
