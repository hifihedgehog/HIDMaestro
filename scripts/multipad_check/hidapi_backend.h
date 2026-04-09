#pragma once
#include "input_backend.h"

#include <cstdint>
#include <winternl.h>
#include <hidusage.h>
#include <hidpi.h>
#include <SetupAPI.h>
#include <hidsdi.h>
#include <memory>
#include <vector>
#include <string>
#include <wil/resource.h>

class HidApiBackend final : public IInputBackend
{
public:
	static constexpr const char* Name = "HIDAPI";
	static constexpr int kMaxDevices = 16;

	~HidApiBackend() override;

	/**
	 * Poll HID devices, read pending reports, and update cached gamepad state.
	 *
	 * This method also advances device discovery and removes disconnected devices
	 * from the backend's internal slot mapping.
	 */
	void Poll() override;

	/**
	 * Return the maximum number of device slots supported by this backend.
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
		std::wstring path;
		wil::unique_hfile handle;
		wil::unique_handle readEvent;
		OVERLAPPED overlapped{};
		std::vector<BYTE> readBuf;
		struct UniquePreparsed
		{
			PHIDP_PREPARSED_DATA p = nullptr;
			UniquePreparsed() = default;
			explicit UniquePreparsed(PHIDP_PREPARSED_DATA q) : p(q) {}
			~UniquePreparsed() { reset(); }
			UniquePreparsed(UniquePreparsed&& other) noexcept : p(other.p) { other.p = nullptr; }
			UniquePreparsed& operator=(UniquePreparsed&& other) noexcept { reset(other.p); other.p = nullptr; return *this; }
			void reset(PHIDP_PREPARSED_DATA q = nullptr) { if (p) HidD_FreePreparsedData(p); p = q; }
			PHIDP_PREPARSED_DATA get() const { return p; }
			explicit operator bool() const { return p != nullptr; }
		} preparsed;
		HIDP_CAPS caps{};
		std::vector<HIDP_VALUE_CAPS> valueCaps;
		int slot = -1;
		bool found = false;
		bool readPending = false;
		uint16_t vendorId = 0;
		uint16_t productId = 0;
	};

	std::vector<std::unique_ptr<DeviceInfo>> devices_;
	GamepadState states_[kMaxDevices]{};
	int pollCounter_ = 0;
	std::vector<USAGE> usageBuf_;
	bool firstPoll_ = true;

	void EnumerateDevices();
	[[nodiscard]] bool OpenAndSetup(const wchar_t* path, DeviceInfo& info);
	void StartRead(DeviceInfo& dev);
	void ParseReport(DeviceInfo& dev, DWORD bytesRead);
	void CloseDevice(DeviceInfo& dev);
	[[nodiscard]] int AllocateSlot();

	[[nodiscard]] static bool IsGamepadOrJoystick(const HIDP_CAPS& caps);
	[[nodiscard]] static uint16_t MapButton(USAGE u);
	[[nodiscard]] static uint16_t MapHat(ULONG val, const HIDP_VALUE_CAPS& vc);
	[[nodiscard]] static LONG ToSigned(ULONG raw, const HIDP_VALUE_CAPS& vc);
	[[nodiscard]] static float NormStick(ULONG raw, const HIDP_VALUE_CAPS& vc);
	[[nodiscard]] static float NormTrigger(ULONG raw, const HIDP_VALUE_CAPS& vc);
};
