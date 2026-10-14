// Stand-in for Dolphin's Core/Core.h: emulation state as GCAdapter.cpp asks
// for it. The harness runs no emulation, so the core is uninitialized and
// wants no determinism, the state of Dolphin's menus and controller
// settings.
#pragma once

namespace Core
{
class System;

enum class State
{
  Uninitialized,
  Paused,
  Running,
  Stopping,
  Starting,
};

State GetState(System& system);
bool WantsDeterminism();
}  // namespace Core
