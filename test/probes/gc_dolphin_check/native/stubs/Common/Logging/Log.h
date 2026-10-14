// Stand-in for Dolphin's Common/Logging/Log.h: the *_LOG_FMT macros format
// with fmt, as Dolphin's do, and the harness writes each line to stderr with
// its time, where the probe shows it beside its own checks.
#pragma once

#include <string>

#include <fmt/format.h>

namespace Common::Log
{
enum class LogType
{
  CONTROLLERINTERFACE,
  IOS_USB,
};

void Write(char level, LogType type, std::string&& text);
}  // namespace Common::Log

#define HM_LOG_FMT(level, t, ...)                                                                 \
  ::Common::Log::Write(level, ::Common::Log::LogType::t, ::fmt::format(__VA_ARGS__))
#define ERROR_LOG_FMT(t, ...) HM_LOG_FMT('E', t, __VA_ARGS__)
#define WARN_LOG_FMT(t, ...) HM_LOG_FMT('W', t, __VA_ARGS__)
#define NOTICE_LOG_FMT(t, ...) HM_LOG_FMT('N', t, __VA_ARGS__)
#define INFO_LOG_FMT(t, ...) HM_LOG_FMT('I', t, __VA_ARGS__)
#define DEBUG_LOG_FMT(t, ...) HM_LOG_FMT('D', t, __VA_ARGS__)
