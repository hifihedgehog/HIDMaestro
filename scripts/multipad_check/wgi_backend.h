#pragma once
#include "input_backend.h"
#include <Windows.h>
#include <memory>

class WgiBackend final : public IInputBackend
{
public:
	static constexpr const char* Name = "WGI";

	/**
	 * Create a backend instance for the Windows.Gaming.Input API.
	 */
	WgiBackend();

	/**
	 * Release all resources owned by the backend.
	 */
	~WgiBackend() override;

	/**
	 * Initialize the backend.
	 *
	 * @param hwnd Optional window handle used for message integration.
	 */
	void Init(HWND hwnd) override;

	/**
	 * Poll Windows.Gaming.Input controllers and update cached gamepad state.
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

	/**
	 * Get USB vendor and product identifiers for a slot, if available.
	 *
	 * @param slot Zero-based slot index.
	 * @param vendorId Output vendor identifier; set to 0 if unknown.
	 * @param productId Output product identifier; set to 0 if unknown.
	 */
	void GetSlotDeviceIds(int slot, uint16_t* vendorId, uint16_t* productId) const override;

private:
	struct Impl;
	std::unique_ptr<Impl> impl_;
};
