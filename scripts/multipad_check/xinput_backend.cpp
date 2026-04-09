#include "xinput_backend.h"

void XInputBackend::Init(HWND)
{
	xinputModule_ = LoadLibraryW(L"xinput1_4.dll");
	if (!xinputModule_)
		xinputModule_ = LoadLibraryW(L"xinput1_3.dll");
	if (xinputModule_)
		getStateEx_ = reinterpret_cast<XInputGetStateEx_t>(
			GetProcAddress(xinputModule_, reinterpret_cast<LPCSTR>(static_cast<uintptr_t>(100))));
}

void XInputBackend::Poll()
{
	const auto now = std::chrono::steady_clock::now();

	for (DWORD i = 0; i < XUSER_MAX_COUNT; ++i)
	{
		auto& gs = states_[i];

		if (!gs.connected && (now - lastProbe_[i]) < kReprobeInterval)
			continue;

		XINPUT_STATE xs{};
		DWORD result = getStateEx_
			? getStateEx_(i, &xs)
			: XInputGetState(i, &xs);
		lastProbe_[i] = now;

		if (result != ERROR_SUCCESS)
		{
			gs = GamepadState{};
			continue;
		}

		gs.connected = true;
		const auto& [
			wButtons,
			bLeftTrigger, 
			bRightTrigger, 
			sThumbLX, 
			sThumbLY, 
			sThumbRX, 
			sThumbRY
		] = xs.Gamepad;

		gs.buttons = wButtons;

		gs.leftTrigger = bLeftTrigger / 255.0f;
		gs.rightTrigger = bRightTrigger / 255.0f;

		gs.leftStickX = NormalizeAxis(sThumbLX, XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE);
		gs.leftStickY = NormalizeAxis(sThumbLY, XINPUT_GAMEPAD_LEFT_THUMB_DEADZONE);
		gs.rightStickX = NormalizeAxis(sThumbRX, XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE);
		gs.rightStickY = NormalizeAxis(sThumbRY, XINPUT_GAMEPAD_RIGHT_THUMB_DEADZONE);
	}
}

int XInputBackend::GetMaxSlots() const { return XUSER_MAX_COUNT; }

const GamepadState& XInputBackend::GetState(int slot) const
{
	return states_[slot];
}

const char* XInputBackend::GetName() const { return Name; }

float XInputBackend::NormalizeAxis(const SHORT value, SHORT deadzone)
{
	if (value > deadzone)
		return static_cast<float>(value - deadzone) / (32767 - deadzone);
	if (value < -deadzone)
		return static_cast<float>(value + deadzone) / (32767 - deadzone);
	return 0.0f;
}
