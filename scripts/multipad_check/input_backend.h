#pragma once
#include "gamepad_state.h"
#include <cstdint>
#include <Windows.h>

/**
 * Abstract interface for input backends that provide per-slot gamepad state.
 *
 * Implementations poll devices and expose state via `GetState(slot)`.
 * Optional hooks are provided for initialization, message handling, and
 * device metadata used by the UI.
 */
class IInputBackend
{
public:
	/** Virtual destructor for correct cleanup of derived implementations. */
	virtual ~IInputBackend() = default;

	/**
	 * Initialize the backend with an optional window handle.
	 *
	 * @param hwnd Window handle for backend setup; may be ignored by the implementation.
	 */
	virtual void Init(HWND /*hwnd*/)
	{
	}

	/**
	 * Process a window message.
	 *
	 * Used by backends that need Win32 message handling.
	 *
	 * @param msg Window message identifier.
	 * @param wParam Message wParam.
	 * @param lParam Message lParam.
	 * @return true if the message was handled, false otherwise.
	 */
	virtual bool OnWindowMessage(UINT /*msg*/, WPARAM /*wParam*/, LPARAM /*lParam*/) { return false; }

	/**
	 * Poll and update input state for all slots.
	 *
	 * Call once per frame.
	 */
	virtual void Poll() = 0;

	/**
	 * Maximum number of input slots this backend supports.
	 *
	 * @return Number of slots.
	 */
	[[nodiscard]] virtual int GetMaxSlots() const = 0;

	/**
	 * Current gamepad state for a slot.
	 *
	 * @param slot Slot index in `[0, GetMaxSlots())`.
	 * @return Const reference to the `GamepadState` for that slot.
	 */
	[[nodiscard]] virtual const GamepadState& GetState(int slot) const = 0;

	/**
	 * Backend identifier used in logs and UI.
	 *
	 * @return Null-terminated C-string, never `nullptr`.
	 */
	[[nodiscard]] virtual const char* GetName() const = 0;

	/**
	 * Human-readable name for a slot, if available.
	 *
	 * @param slot Slot index.
	 * @return Null-terminated C-string, or `nullptr` if not available.
	 */
	[[nodiscard]] virtual const char* GetSlotDisplayName([[maybe_unused]] int slot) const { return nullptr; }

	/**
	 * USB vendor and product ID for a slot.
	 *
	 * @param slot Slot index.
	 * @param vendorId Output vendor ID; set to 0 if unknown. May be nullptr.
	 * @param productId Output product ID; set to 0 if unknown. May be nullptr.
	 */
	virtual void GetSlotDeviceIds([[maybe_unused]] int slot, uint16_t* vendorId, uint16_t* productId) const
	{
		if (vendorId) *vendorId = 0;
		if (productId) *productId = 0;
	}
};
