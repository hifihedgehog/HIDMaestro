// Stand-in for Dolphin's Common/Assert.h: Slippi's LibusbUtils.cpp asserts
// that libusb_init succeeded. A failed assertion is logged as an error,
// which the harness reports, where Dolphin would show a panic dialog.
#pragma once

#include "Common/Logging/Log.h"

#define ASSERT_MSG(t, condition, ...)                                                             \
  do                                                                                              \
  {                                                                                               \
    if (!(condition))                                                                             \
      ERROR_LOG_FMT(t, __VA_ARGS__);                                                              \
  } while (0)
