// Stand-in for Dolphin's Common/Config/Config.h: the calls GCAdapter.cpp
// makes, with the signatures Dolphin 0d93fc0355 and Slippi 9d13b39 declare
// (Config.h:18-35, 72-73). Settings come from the harness, not from INI
// layers.
#pragma once

#include <cstddef>
#include <functional>

namespace Config
{
template <typename T>
struct Info
{
  T value;
};

struct ConfigChangedCallbackID
{
  std::size_t id = 0;

  bool operator==(const ConfigChangedCallbackID&) const = default;
};

using ConfigChangedCallback = std::function<void()>;

[[nodiscard]] ConfigChangedCallbackID AddConfigChangedCallback(ConfigChangedCallback func);
void RemoveConfigChangedCallback(ConfigChangedCallbackID callback_id);

template <typename T>
T Get(const Info<T>& info)
{
  return info.value;
}
}  // namespace Config
