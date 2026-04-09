#pragma once
#include "input_backend.h"
#include <Windows.h>
#include <Xinput.h>
#include <chrono>

using XInputGetStateEx_t = DWORD(WINAPI*)(DWORD dwUserIndex, XINPUT_STATE* pState);

class XInputBackend final : public IInputBackend
{
public:
	static constexpr const char* Name = "XInput";

	void Init(HWND hwnd) override;

	/**
	 * Poll XInput controllers and refresh cached gamepad state.
	 *
	 * This probes controller slots, applies deadzone/normalization logic, and
	 * stores the resulting state in the backend's internal cache.
	 */
	void Poll() override;

	/**
	 * Return the number of XInput controller slots.
	 *
	 * @return Always `XUSER_MAX_COUNT`.
	 */
	[[nodiscard]] int GetMaxSlots() const override;

	/**
	 * Retrieve the current gamepad state for a slot.
	 *
	 * @param slot Zero-based controller index.
	 * @return Reference to the cached `GamepadState` for the requested slot.
	 */
	[[nodiscard]] const GamepadState& GetState(int slot) const override;

	/**
	 * Get the backend name used in the UI and logs.
	 *
	 * @return Null-terminated backend name string.
	 */
	[[nodiscard]] const char* GetName() const override;

private:
	static constexpr auto kReprobeInterval = std::chrono::milliseconds(1500);

	HMODULE xinputModule_ = nullptr;
	XInputGetStateEx_t getStateEx_ = nullptr;

	GamepadState states_[XUSER_MAX_COUNT]{};
	std::chrono::steady_clock::time_point lastProbe_[XUSER_MAX_COUNT]{};

	[[nodiscard]] static float NormalizeAxis(SHORT value, SHORT deadzone);
};
