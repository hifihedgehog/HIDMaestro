#pragma once
#include "input_backend.h"

#include <cstdint>
#include <string>
#ifndef DIRECTINPUT_VERSION
#define DIRECTINPUT_VERSION 0x0800
#endif
#include <dinput.h>
#include <vector>
#include <wil/com.h>

class DInputBackend final : public IInputBackend
{
public:
	static constexpr const char* Name = "DirectInput";
	static constexpr int kMaxDevices = 16;

	~DInputBackend() override;
	void Init(HWND hwnd) override;

	/**
	 * Poll connected DirectInput devices and refresh cached gamepad state.
	 *
	 * This updates device discovery, handles device loss/reacquisition, and
	 * stores normalized state per backend slot.
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
		GUID instanceGuid{};
		wil::com_ptr<IDirectInputDevice8W> device;
		int slot = -1;
		bool found = false;
		uint16_t vendorId = 0;
		uint16_t productId = 0;
		std::wstring productName;
	};

	HWND hwnd_ = nullptr;
	wil::com_ptr<IDirectInput8W> di_;
	std::vector<DeviceInfo> devices_;
	GamepadState states_[kMaxDevices]{};
	int pollCounter_ = 0;

	void EnumerateDevices();
	[[nodiscard]] bool SetupDevice(const DIDEVICEINSTANCEW& inst, DeviceInfo& info);
	[[nodiscard]] int AllocateSlot();
	void ReleaseDevices();

	static BOOL CALLBACK EnumCallback(const DIDEVICEINSTANCEW* inst, VOID* ctx);
	[[nodiscard]] static float NormStick(LONG value);
	[[nodiscard]] static float NormTrigger(LONG value);
	[[nodiscard]] static uint16_t MapButton(int index);
	[[nodiscard]] static uint16_t MapPOV(DWORD pov);
	[[nodiscard]] static bool IsSonyByProductName(const std::wstring& name);
};
