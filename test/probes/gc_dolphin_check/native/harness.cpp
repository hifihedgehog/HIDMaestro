// The S82 harness: Dolphin's or Slippi's GCAdapter.cpp and LibusbUtils.cpp,
// compiled unmodified with the libusb and fmt each one pins, driven through
// GCAdapter's own interface the way the emulator's serial interface drives
// it. The probe (Program.cs) sends one command per line on stdin and reads
// one answer per line on stdout. Times are QueryPerformanceCounter ticks,
// the clock the probe's Stopwatch reads.
//
//   init                 GCAdapter::Init, then wait for the adapter: "detected" or "error <text>"
//   input <c>            GCAdapter::Input(c): "pad <button> <sx> <sy> <cx> <cy> <l> <r>"
//   connected <c>        GCAdapter::DeviceConnected(c): "connected 0|1"
//   output <c> <cmd>     GCAdapter::Output(c, cmd): "ok"
//   watch <c> <sx>       "armed", then poll Input(c) until stickX is sx: "seen <qpc>" or "timeout"
//   count <c> <ms>       changes of Input(c)'s stick X seen over ms: "changes <n>"
//   rate                 the emulator's own poll rate figure: "rate <value>"
//   loglevel <n>         libusb's log level for contexts made after it: "ok"
//   quit                 GCAdapter::Shutdown and exit
//
// Settings stand in for Dolphin's: every channel's SI device is the Wii U
// adapter and rumble is on, the setup Dolphin's guide asks for.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>

#include <atomic>
#include <cstdio>
#include <iostream>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>

#include <libusb.h>

#include "Common/Config/Config.h"
#include "Common/Logging/Log.h"
#include "Common/StringUtil.h"
#include "Common/Thread.h"
#include "Core/Config/MainSettings.h"
#include "Core/Core.h"
#include "Core/System.h"
#include "InputCommon/GCAdapter.h"
#include "InputCommon/GCPadStatus.h"

// ── Stand-in implementations ─────────────────────────────────────────────

namespace Config
{
static std::mutex s_callbacks_lock;
static ConfigChangedCallback s_callback;

ConfigChangedCallbackID AddConfigChangedCallback(ConfigChangedCallback func)
{
  std::lock_guard lock(s_callbacks_lock);
  s_callback = std::move(func);
  return {1};
}

void RemoveConfigChangedCallback(ConfigChangedCallbackID)
{
  std::lock_guard lock(s_callbacks_lock);
  s_callback = nullptr;
}

const Info<SerialInterface::SIDevices>& GetInfoForSIDevice(int)
{
  static const Info<SerialInterface::SIDevices> adapter{SerialInterface::SIDEVICE_WIIU_ADAPTER};
  return adapter;
}

const Info<bool>& GetInfoForAdapterRumble(int)
{
  static const Info<bool> on{true};
  return on;
}
}  // namespace Config

namespace Core
{
State GetState(System&)
{
  return State::Uninitialized;
}

bool WantsDeterminism()
{
  return false;
}

System& System::GetInstance()
{
  static System system;
  return system;
}
}  // namespace Core

namespace Common
{
// Dolphin's Windows definitions, Common/Thread.cpp.
void SleepCurrentThread(int ms)
{
  Sleep(ms);
}

void SetCurrentThreadName(const char* name)
{
  wchar_t wide[64]{};
  MultiByteToWideChar(CP_UTF8, 0, name, -1, wide, 63);
  SetThreadDescription(GetCurrentThread(), wide);
}

namespace Log
{
static std::mutex s_log_lock;

void Write(char level, LogType, std::string&& text)
{
  LARGE_INTEGER now;
  QueryPerformanceCounter(&now);
  std::lock_guard lock(s_log_lock);
  std::fprintf(stderr, "[%lld %c] %s\n", now.QuadPart, level, text.c_str());
  std::fflush(stderr);
}
}  // namespace Log
}  // namespace Common

std::string UTF16ToUTF8(std::u16string_view input)
{
  const auto* wide = reinterpret_cast<const wchar_t*>(input.data());
  const int length = static_cast<int>(input.size());
  const int size = WideCharToMultiByte(CP_UTF8, 0, wide, length, nullptr, 0, nullptr, nullptr);
  std::string out(size > 0 ? size : 0, '\0');
  if (size > 0)
    WideCharToMultiByte(CP_UTF8, 0, wide, length, out.data(), size, nullptr, nullptr);
  return out;
}

// ── The command loop ─────────────────────────────────────────────────────

static long long Qpc()
{
  LARGE_INTEGER now;
  QueryPerformanceCounter(&now);
  return now.QuadPart;
}

static void Answer(const std::string& line)
{
  std::cout << line << '\n';
  std::cout.flush();
}

int main()
{
  std::ios::sync_with_stdio(false);
  std::string line;
  while (std::getline(std::cin, line))
  {
    std::istringstream in(line);
    std::string command;
    in >> command;

    if (command == "init")
    {
      GCAdapter::Init();
      // The scan thread tries again every 500 ms after an error, as it does
      // in the emulator, so the wait outlasts a transient one.
      const char* error = nullptr;
      std::string last_error = "not detected in 10 s";
      bool detected = false;
      const ULONGLONG deadline = GetTickCount64() + 10000;
      while (!detected && GetTickCount64() < deadline)
      {
        detected = GCAdapter::IsDetected(&error);
        if (!detected && error != nullptr)
          last_error = error;
        if (!detected)
          Sleep(10);
      }
      Answer(detected ? std::string("detected") : "error " + last_error);
    }
    else if (command == "input")
    {
      int chan = 0;
      in >> chan;
      const GCPadStatus pad = GCAdapter::Input(chan);
      std::ostringstream out;
      out << "pad " << pad.button << ' ' << int(pad.stickX) << ' ' << int(pad.stickY) << ' '
          << int(pad.substickX) << ' ' << int(pad.substickY) << ' ' << int(pad.triggerLeft) << ' '
          << int(pad.triggerRight);
      Answer(out.str());
    }
    else if (command == "connected")
    {
      int chan = 0;
      in >> chan;
      Answer(std::string("connected ") + (GCAdapter::DeviceConnected(chan) ? "1" : "0"));
    }
    else if (command == "output")
    {
      int chan = 0, cmd = 0;
      in >> chan >> cmd;
      GCAdapter::Output(chan, static_cast<u8>(cmd));
      Answer("ok");
    }
    else if (command == "watch")
    {
      int chan = 0, want = 0;
      in >> chan >> want;
      Answer("armed");
      const long long deadline = Qpc() + 2 * [] {
        LARGE_INTEGER f;
        QueryPerformanceFrequency(&f);
        return f.QuadPart;
      }();
      long long seen = 0;
      while (Qpc() < deadline)
      {
        if (GCAdapter::Input(chan).stickX == want)
        {
          seen = Qpc();
          break;
        }
        YieldProcessor();
      }
      Answer(seen ? "seen " + std::to_string(seen) : std::string("timeout"));
    }
    else if (command == "count")
    {
      int chan = 0, ms = 0;
      in >> chan >> ms;
      LARGE_INTEGER f;
      QueryPerformanceFrequency(&f);
      const long long end = Qpc() + f.QuadPart * ms / 1000;
      int changes = 0;
      u8 last = GCAdapter::Input(chan).stickX;
      while (Qpc() < end)
      {
        const u8 now = GCAdapter::Input(chan).stickX;
        if (now != last)
        {
          ++changes;
          last = now;
        }
        YieldProcessor();
      }
      Answer("changes " + std::to_string(changes));
    }
    else if (command == "rate")
    {
#if defined(HM_FLAVOR_SLIPPI)
      // Slippi's figure is the last read's time in ms, and its warning
      // after 80 slow reads in a row.
      Answer("rate " + std::to_string(GCAdapter::ReadRate()) + " reduced " +
             (GCAdapter::IsReadingAtReducedRate() ? "1" : "0"));
#else
      Answer("rate " + std::to_string(GCAdapter::GetCurrentPollRate()));
#endif
    }
    else if (command == "loglevel")
    {
      int level = 0;
      in >> level;
      // With no context, the option applies to every context libusb makes
      // afterward, GCAdapter::Init's included.
      const int r = libusb_set_option(nullptr, LIBUSB_OPTION_LOG_LEVEL, level);
      Answer(r == LIBUSB_SUCCESS ? std::string("ok") : "error " + std::to_string(r));
    }
    else if (command == "quit")
    {
      GCAdapter::Shutdown();
      Answer("bye");
      return 0;
    }
    else
    {
      Answer("unknown " + command);
    }
  }
  GCAdapter::Shutdown();
  return 0;
}
