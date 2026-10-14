// Stand-in for Dolphin's Core/System.h: the accessors GCAdapter::Init calls.
#pragma once

#include "Core/CoreTiming.h"
#include "Core/HW/SystemTimers.h"

namespace Core
{
class System
{
public:
  static System& GetInstance();

  CoreTiming::CoreTimingManager& GetCoreTiming() { return m_core_timing; }
  SystemTimers::SystemTimersManager& GetSystemTimers() { return m_system_timers; }

private:
  CoreTiming::CoreTimingManager m_core_timing;
  SystemTimers::SystemTimersManager m_system_timers;
};
}  // namespace Core
