// The S83 harness: an SDL3.dll loaded from the directory named on the
// command line and driven the way a game drives SDL. The probe (Program.cs)
// sends one command per line on stdin and reads one answer per line on
// stdout. Between commands the harness pumps SDL once a millisecond, as a
// game's frame loop would, so a rumble request goes out and a device change
// is seen without a command asking for it. Times are QueryPerformanceCounter
// ticks, the clock the probe's Stopwatch reads.
//
//   hint <name> <value>          SDL_SetHint: "ok"
//   init                         SDL_Init(SDL_INIT_GAMEPAD): "ok <version> <revision>" or "error <text>"
//   list                         "list", then per joystick a tab and id:vid:pid:gamepad:player:name
//   wait <vid> <pid> <n> <ms>    pump until n joysticks of vid:pid are listed, then answer as list
//   open <id>                    SDL_OpenGamepad: "opened" or "error <text>"
//   info <id>                    "info <player> <connection> <rumble> <type>", a tab, the mapping
//   state <id>                   "state <buttons> <six axes> raw <buttons> <six axes>", the gamepad's
//                                and then the joystick's, each button set a hex bit mask
//   rumble <id> <low> <high>     SDL_RumbleGamepad with no expiry: "ok" or "error <text>"
//   watch <id> <axis>            "armed", then pump until the joystick axis changes: "seen <qpc>" or "timeout"
//   count <id> <axis> <ms>       changes of the joystick axis seen over ms: "changes <n>"
//   close <id>                   SDL_CloseGamepad: "ok"
//   quit                         SDL_Quit and exit: "bye"
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <timeapi.h>

#include <deque>
#include <iostream>
#include <map>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>

#include <SDL3/SDL.h>

// Every SDL function the harness calls, resolved from the loaded SDL3.dll.
// decltype reads each signature from SDL's own header.
#define SDL_FUNCTIONS(X)                                                                         \
  X(SDL_SetHint) X(SDL_Init) X(SDL_Quit) X(SDL_GetError) X(SDL_GetVersion) X(SDL_GetRevision)    \
  X(SDL_PumpEvents) X(SDL_FlushEvents) X(SDL_UpdateJoysticks) X(SDL_free) X(SDL_GetJoysticks)    \
  X(SDL_GetJoystickVendorForID) X(SDL_GetJoystickProductForID) X(SDL_GetJoystickNameForID)        \
  X(SDL_IsGamepad) X(SDL_GetJoystickPlayerIndexForID) X(SDL_OpenGamepad) X(SDL_CloseGamepad)      \
  X(SDL_GetGamepadJoystick) X(SDL_GetGamepadMapping) X(SDL_GetGamepadPlayerIndex)                 \
  X(SDL_GetGamepadConnectionState) X(SDL_GetGamepadProperties) X(SDL_GetBooleanProperty)          \
  X(SDL_GetGamepadType) X(SDL_GetGamepadButton) X(SDL_GetGamepadAxis) X(SDL_GetJoystickButton)    \
  X(SDL_GetJoystickAxis) X(SDL_GetNumJoystickButtons) X(SDL_GetNumJoystickAxes)                   \
  X(SDL_RumbleGamepad)

#define DECLARE(name) static decltype(&name) p_##name = nullptr;
SDL_FUNCTIONS(DECLARE)
#undef DECLARE

static std::mutex s_lines_lock;
static std::deque<std::string> s_lines;
static HANDLE s_line_ready;
static bool s_initialized;
static std::map<SDL_JoystickID, SDL_Gamepad*> s_gamepads;

static long long Qpc()
{
  LARGE_INTEGER now;
  QueryPerformanceCounter(&now);
  return now.QuadPart;
}

static long long QpcFrequency()
{
  LARGE_INTEGER f;
  QueryPerformanceFrequency(&f);
  return f.QuadPart;
}

static void Answer(const std::string& line)
{
  std::cout << line << '\n';
  std::cout.flush();
}

static std::string Error()
{
  return std::string("error ") + p_SDL_GetError();
}

// What a game's frame does: pump, then drop the events it has no use for.
static void Pump()
{
  p_SDL_PumpEvents();
  p_SDL_FlushEvents(SDL_EVENT_FIRST, SDL_EVENT_LAST);
}

static std::string List()
{
  std::ostringstream out;
  out << "list";
  int count = 0;
  SDL_JoystickID* ids = p_SDL_GetJoysticks(&count);
  for (int i = 0; ids != nullptr && i < count; ++i)
  {
    const SDL_JoystickID id = ids[i];
    const char* name = p_SDL_GetJoystickNameForID(id);
    char fields[64];
    sprintf_s(fields, "%u:%04x:%04x:%d:%d:", static_cast<unsigned>(id),
              p_SDL_GetJoystickVendorForID(id), p_SDL_GetJoystickProductForID(id),
              p_SDL_IsGamepad(id) ? 1 : 0, p_SDL_GetJoystickPlayerIndexForID(id));
    out << '\t' << fields << (name != nullptr ? name : "");
  }
  p_SDL_free(ids);
  return out.str();
}

static int CountOf(unsigned vid, unsigned pid)
{
  int count = 0, found = 0;
  SDL_JoystickID* ids = p_SDL_GetJoysticks(&count);
  for (int i = 0; ids != nullptr && i < count; ++i)
  {
    if (p_SDL_GetJoystickVendorForID(ids[i]) == vid && p_SDL_GetJoystickProductForID(ids[i]) == pid)
      ++found;
  }
  p_SDL_free(ids);
  return found;
}

static SDL_Gamepad* Find(SDL_JoystickID id)
{
  const auto it = s_gamepads.find(id);
  return it == s_gamepads.end() ? nullptr : it->second;
}

static std::string State(SDL_Gamepad* gamepad)
{
  SDL_Joystick* joystick = p_SDL_GetGamepadJoystick(gamepad);
  unsigned buttons = 0, raw_buttons = 0;
  for (int b = 0; b < SDL_GAMEPAD_BUTTON_COUNT; ++b)
  {
    if (p_SDL_GetGamepadButton(gamepad, static_cast<SDL_GamepadButton>(b)))
      buttons |= 1u << b;
  }
  const int joystick_buttons = p_SDL_GetNumJoystickButtons(joystick);
  for (int b = 0; b < joystick_buttons && b < 32; ++b)
  {
    if (p_SDL_GetJoystickButton(joystick, b))
      raw_buttons |= 1u << b;
  }
  std::ostringstream out;
  char mask[16];
  sprintf_s(mask, "%08x", buttons);
  out << "state " << mask;
  for (int a = 0; a < SDL_GAMEPAD_AXIS_COUNT; ++a)
    out << ' ' << p_SDL_GetGamepadAxis(gamepad, static_cast<SDL_GamepadAxis>(a));
  sprintf_s(mask, "%08x", raw_buttons);
  out << " raw " << mask;
  const int joystick_axes = p_SDL_GetNumJoystickAxes(joystick);
  for (int a = 0; a < 6; ++a)
    out << ' ' << (a < joystick_axes ? p_SDL_GetJoystickAxis(joystick, a) : 0);
  return out.str();
}

// One command. Returns false on quit.
static bool Handle(const std::string& line)
{
  std::istringstream in(line);
  std::string command;
  in >> command;

  if (command == "hint")
  {
    std::string name, value;
    in >> name >> value;
    p_SDL_SetHint(name.c_str(), value.c_str());
    Answer("ok");
  }
  else if (command == "init")
  {
    if (!p_SDL_Init(SDL_INIT_GAMEPAD))
    {
      Answer(Error());
      return true;
    }
    s_initialized = true;
    Answer("ok " + std::to_string(p_SDL_GetVersion()) + ' ' + p_SDL_GetRevision());
  }
  else if (command == "list")
  {
    Pump();
    Answer(List());
  }
  else if (command == "wait")
  {
    unsigned vid = 0, pid = 0;
    int want = 0, ms = 0;
    in >> std::hex >> vid >> pid >> std::dec >> want >> ms;
    const ULONGLONG deadline = GetTickCount64() + ms;
    for (;;)
    {
      Pump();
      if (CountOf(vid, pid) >= want || GetTickCount64() >= deadline)
        break;
      Sleep(1);
    }
    Answer(List());
  }
  else if (command == "open")
  {
    SDL_JoystickID id = 0;
    in >> id;
    if (Find(id) != nullptr)
    {
      Answer("opened");
      return true;
    }
    SDL_Gamepad* gamepad = p_SDL_OpenGamepad(id);
    if (gamepad == nullptr)
    {
      Answer(Error());
      return true;
    }
    s_gamepads[id] = gamepad;
    Answer("opened");
  }
  else if (command == "info")
  {
    SDL_JoystickID id = 0;
    in >> id;
    SDL_Gamepad* gamepad = Find(id);
    if (gamepad == nullptr)
    {
      Answer("error not open");
      return true;
    }
    char* mapping = p_SDL_GetGamepadMapping(gamepad);
    const bool rumble = p_SDL_GetBooleanProperty(p_SDL_GetGamepadProperties(gamepad),
                                                 SDL_PROP_GAMEPAD_CAP_RUMBLE_BOOLEAN, false);
    std::ostringstream out;
    out << "info " << p_SDL_GetGamepadPlayerIndex(gamepad) << ' '
        << static_cast<int>(p_SDL_GetGamepadConnectionState(gamepad)) << ' ' << (rumble ? 1 : 0) << ' '
        << static_cast<int>(p_SDL_GetGamepadType(gamepad)) << '\t' << (mapping != nullptr ? mapping : "");
    p_SDL_free(mapping);
    Answer(out.str());
  }
  else if (command == "state")
  {
    SDL_JoystickID id = 0;
    in >> id;
    SDL_Gamepad* gamepad = Find(id);
    if (gamepad == nullptr)
    {
      Answer("error not open");
      return true;
    }
    Pump();
    Answer(State(gamepad));
  }
  else if (command == "rumble")
  {
    SDL_JoystickID id = 0;
    unsigned low = 0, high = 0;
    in >> id >> low >> high;
    SDL_Gamepad* gamepad = Find(id);
    if (gamepad == nullptr)
    {
      Answer("error not open");
      return true;
    }
    // No expiry: the motor runs until the next request changes it.
    const bool ok = p_SDL_RumbleGamepad(gamepad, static_cast<Uint16>(low), static_cast<Uint16>(high), 0);
    Answer(ok ? std::string("ok") : Error());
  }
  else if (command == "watch" || command == "count")
  {
    SDL_JoystickID id = 0;
    int axis = 0, ms = 0;
    in >> id >> axis;
    if (command == "count")
      in >> ms;
    SDL_Gamepad* gamepad = Find(id);
    if (gamepad == nullptr)
    {
      Answer("error not open");
      return true;
    }
    SDL_Joystick* joystick = p_SDL_GetGamepadJoystick(gamepad);
    p_SDL_UpdateJoysticks();
    Sint16 last = p_SDL_GetJoystickAxis(joystick, axis);
    if (command == "watch")
    {
      Answer("armed");
      const long long deadline = Qpc() + 2 * QpcFrequency();
      long long seen = 0;
      while (Qpc() < deadline)
      {
        p_SDL_UpdateJoysticks();
        if (p_SDL_GetJoystickAxis(joystick, axis) != last)
        {
          seen = Qpc();
          break;
        }
        YieldProcessor();
      }
      Answer(seen ? "seen " + std::to_string(seen) : std::string("timeout"));
    }
    else
    {
      const long long end = Qpc() + QpcFrequency() * ms / 1000;
      int changes = 0;
      while (Qpc() < end)
      {
        p_SDL_UpdateJoysticks();
        const Sint16 now = p_SDL_GetJoystickAxis(joystick, axis);
        if (now != last)
        {
          ++changes;
          last = now;
        }
        YieldProcessor();
      }
      Answer("changes " + std::to_string(changes));
    }
  }
  else if (command == "close")
  {
    SDL_JoystickID id = 0;
    in >> id;
    if (SDL_Gamepad* gamepad = Find(id))
    {
      p_SDL_CloseGamepad(gamepad);
      s_gamepads.erase(id);
    }
    Answer("ok");
  }
  else if (command == "quit")
  {
    for (auto& [id, gamepad] : s_gamepads)
      p_SDL_CloseGamepad(gamepad);
    s_gamepads.clear();
    if (s_initialized)
      p_SDL_Quit();
    Answer("bye");
    return false;
  }
  else
  {
    Answer("unknown " + command);
  }
  return true;
}

static bool TakeLine(std::string& line)
{
  std::lock_guard lock(s_lines_lock);
  if (s_lines.empty())
    return false;
  line = std::move(s_lines.front());
  s_lines.pop_front();
  return true;
}

int wmain(int argc, wchar_t** argv)
{
  if (argc != 2)
  {
    std::cerr << "usage: sdl_harness <directory holding SDL3.dll>\n";
    return 2;
  }
  // SDL3.dll from the named directory, and libusb-1.0.dll from beside it
  // when SDL loads it by name.
  SetDllDirectoryW(argv[1]);
  const std::wstring path = std::wstring(argv[1]) + L"\\SDL3.dll";
  HMODULE sdl = LoadLibraryW(path.c_str());
  if (sdl == nullptr)
  {
    std::cerr << "cannot load SDL3.dll, error " << GetLastError() << '\n';
    return 2;
  }
#define LOAD(name)                                                                               \
  p_##name = reinterpret_cast<decltype(&name)>(GetProcAddress(sdl, #name));                      \
  if (p_##name == nullptr)                                                                        \
  {                                                                                               \
    std::cerr << "SDL3.dll has no " #name "\n";                                                   \
    return 2;                                                                                     \
  }
  SDL_FUNCTIONS(LOAD)
#undef LOAD

  // A 1 ms pump needs a 1 ms timer.
  timeBeginPeriod(1);
  s_line_ready = CreateEventW(nullptr, FALSE, FALSE, nullptr);
  std::ios::sync_with_stdio(false);
  std::thread reader([] {
    std::string line;
    while (std::getline(std::cin, line))
    {
      {
        std::lock_guard lock(s_lines_lock);
        s_lines.push_back(line);
      }
      SetEvent(s_line_ready);
    }
    {
      std::lock_guard lock(s_lines_lock);
      s_lines.push_back("quit");
    }
    SetEvent(s_line_ready);
  });
  reader.detach();

  for (;;)
  {
    std::string line;
    if (TakeLine(line))
    {
      if (!Handle(line))
        break;
      continue;
    }
    if (s_initialized)
      Pump();
    WaitForSingleObject(s_line_ready, 1);
  }
  timeEndPeriod(1);
  return 0;
}
