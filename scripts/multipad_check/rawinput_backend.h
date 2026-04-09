#pragma once
#include "input_backend.h"

#include <cstdint>
#include <winternl.h>
#include <hidusage.h>
#include <hidpi.h>
#include <vector>
#include <unordered_map>

class RawInputBackend final : public IInputBackend
{
public:
	static constexpr const char* Name = "RawInput";
	static constexpr int kMaxDevices = 16;

	void Init(HWND hwnd) override;
	bool OnWindowMessage(UINT msg, WPARAM wParam, LPARAM lParam) override;

	/**
	 * Poll connected raw-input devices and update cached gamepad states.
	 *
	 * This processes pending raw input, refreshes device discovery state, and
	 * keeps per-slot `GamepadState` values in sync with the latest reports.
	 */
	void Poll() override;

	/**
	 * Return the maximum number of gamepad slots this backend can manage.
	 *
	 * @return Maximum supported slot count.
	 */
	[[nodiscard]] int GetMaxSlots() const override;

	/**
	 * Retrieve the current gamepad state for a slot.
	 *
	 * @param slot Zero-based slot index.
	 * @return Reference to the cached `GamepadState` for the requested slot.
	 */
	[[nodiscard]] const GamepadState& GetState(int slot) const override;

	/**
	 * Get the backend name used in the UI and logs.
	 *
	 * @return Null-terminated backend name string.
	 */
	[[nodiscard]] const char* GetName() const override;

	/**
	 * Get the human-readable device name for a slot, if available.
	 *
	 * @param slot Zero-based slot index.
	 * @return Device display name, or `nullptr` if unavailable.
	 */
	[[nodiscard]] const char* GetSlotDisplayName(int slot) const override;

private:
	struct DeviceInfo
	{
		HANDLE handle = nullptr;
		std::vector<BYTE> preparsedBuf;
		HIDP_CAPS caps{};
		std::vector<HIDP_VALUE_CAPS> valueCaps;
		int slot = -1;
		uint16_t vendorId = 0;
		uint16_t productId = 0;
	};

	HWND hwnd_ = nullptr;
	std::unordered_map<HANDLE, DeviceInfo> devices_;
	GamepadState states_[kMaxDevices]{};
	std::vector<BYTE> rawBuf_;
	std::vector<USAGE> usageBuf_;

	[[nodiscard]] static PHIDP_PREPARSED_DATA PP(DeviceInfo& d);
	[[nodiscard]] int AllocateSlot();
	[[nodiscard]] static bool SetupDevice(HANDLE h, DeviceInfo& d);

	void EnumerateExistingDevices();
	void OnDeviceArrival(HANDLE h);
	void OnDeviceRemoval(HANDLE h);

	void HandleRawInput(HRAWINPUT hri);
	void ParseReport(DeviceInfo& dev, RAWHID& hid);

	[[nodiscard]] static bool IsGamepadOrJoystick(HANDLE h);
	[[nodiscard]] static uint16_t MapButton(USAGE u);
	[[nodiscard]] static uint16_t MapHat(ULONG val, const HIDP_VALUE_CAPS& vc);
	[[nodiscard]] static LONG ToSigned(ULONG raw, const HIDP_VALUE_CAPS& vc);
	[[nodiscard]] static float NormStick(ULONG raw, const HIDP_VALUE_CAPS& vc);
	[[nodiscard]] static float NormTrigger(ULONG raw, const HIDP_VALUE_CAPS& vc);
};
