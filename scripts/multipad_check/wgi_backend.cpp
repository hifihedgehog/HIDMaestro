#include "wgi_backend.h"
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Gaming.Input.h>
#include <array>
#include <cstdint>
#include <mutex>
#include <optional>
#include <string>
#include <utility>

namespace
{
	using namespace winrt;
	using namespace Windows::Gaming::Input;
	using namespace Windows::Foundation;

	constexpr int kMaxSlots = 4;

	/**
	 * @brief Converts a WinRT GamepadButtons bitmask into the internal 16-bit button mask.
	 *
	 * @param wgi Bitfield of WinRT GamepadButtons flags to map.
	 * @return uint16_t Bitmask where each set bit corresponds to the internal Button enum value for a pressed button.
	 */
	uint16_t MapGamepadButtons(GamepadButtons wgi)
	{
		uint16_t b = 0;
		using enum Button;
		if ((wgi & GamepadButtons::Menu) != GamepadButtons::None) b |= std::to_underlying(Start);
		if ((wgi & GamepadButtons::View) != GamepadButtons::None) b |= std::to_underlying(Back);
		if ((wgi & GamepadButtons::A) != GamepadButtons::None) b |= std::to_underlying(A);
		if ((wgi & GamepadButtons::B) != GamepadButtons::None) b |= std::to_underlying(B);
		if ((wgi & GamepadButtons::X) != GamepadButtons::None) b |= std::to_underlying(X);
		if ((wgi & GamepadButtons::Y) != GamepadButtons::None) b |= std::to_underlying(Y);
		if ((wgi & GamepadButtons::DPadUp) != GamepadButtons::None) b |= std::to_underlying(DPadUp);
		if ((wgi & GamepadButtons::DPadDown) != GamepadButtons::None) b |= std::to_underlying(DPadDown);
		if ((wgi & GamepadButtons::DPadLeft) != GamepadButtons::None) b |= std::to_underlying(DPadLeft);
		if ((wgi & GamepadButtons::DPadRight) != GamepadButtons::None) b |= std::to_underlying(DPadRight);
		if ((wgi & GamepadButtons::LeftShoulder) != GamepadButtons::None) b |= std::to_underlying(LeftBumper);
		if ((wgi & GamepadButtons::RightShoulder) != GamepadButtons::None) b |= std::to_underlying(RightBumper);
		if ((wgi & GamepadButtons::LeftThumbstick) != GamepadButtons::None) b |= std::to_underlying(LeftThumb);
		if ((wgi & GamepadButtons::RightThumbstick) != GamepadButtons::None) b |= std::to_underlying(RightThumb);
		return b;
	}

	/**
	 * @brief Obtains a human-friendly display name for a gamepad, falling back to a numbered default.
	 *
	 * Attempts to retrieve the controller's DisplayName from its RawGameController representation.
	 * If a display name is unavailable or retrieval fails, returns "Gamepad N" where N is
	 * slotIndex + 1.
	 *
	 * @param pad WinRT Gamepad instance to query.
	 * @param slotIndex Zero-based slot index used to generate the fallback name.
	 * @return std::string The resolved display name or a fallback like "Gamepad 1".
	 */
	std::string GetDisplayNameForGamepad(const Gamepad& pad, int slotIndex)
	{
		try
		{
			auto raw = RawGameController::FromGameController(pad);
			if (raw)
			{
				auto name = raw.DisplayName();
				if (!name.empty())
					return to_string(name);
			}
		}
		catch (...)
		{
		}
		return "Gamepad " + std::to_string(slotIndex + 1);
	}

	/**
	 * @brief Retrieve the hardware vendor and product IDs for a gamepad.
	 *
	 * Attempts to obtain the controller's hardware vendor and product identifiers
	 * and writes them to the provided pointers. If retrieval fails or the
	 * identifiers are unavailable, writes 0 to the corresponding outputs.
	 * If both output pointers are null, the function does nothing.
	 *
	 * @param pad The gamepad to query.
	 * @param vendorId Pointer to receive the vendor ID, or nullptr to skip.
	 * @param productId Pointer to receive the product ID, or nullptr to skip.
	 */
	void GetDeviceIdsForGamepad(const Gamepad& pad, uint16_t* vendorId, uint16_t* productId)
	{
		if (!vendorId && !productId) return;
		try
		{
			auto raw = RawGameController::FromGameController(pad);
			if (raw)
			{
				if (vendorId) *vendorId = raw.HardwareVendorId();
				if (productId) *productId = raw.HardwareProductId();
				return;
			}
		}
		catch (...)
		{
		}
		if (vendorId) *vendorId = 0;
		if (productId) *productId = 0;
	}
}

struct WgiBackend::Impl
{
	mutable std::mutex mutex;
	std::array<std::optional<Gamepad>, kMaxSlots> slotGamepads{};
	GamepadState states[kMaxSlots]{};
	std::array<std::string, kMaxSlots> slotDisplayNames;
	std::array<uint16_t, kMaxSlots> slotVendorIds{};
	std::array<uint16_t, kMaxSlots> slotProductIds{};

	event_token addedToken;
	event_token removedToken;

	/**
	 * @brief Assigns a newly connected gamepad to the first available backend slot and records its metadata.
	 *
	 * If a free slot exists, stores the provided gamepad handle in that slot and populates the slot's
	 * display name and vendor/product IDs. If all slots are occupied, the added gamepad is ignored.
	 *
	 * @param pad The gamepad that was added.
	 */
	void OnGamepadAdded(const IInspectable&, const Gamepad& pad)
	{
		std::lock_guard lock(mutex);
		for (int i = 0; i < kMaxSlots; ++i)
		{
			if (!slotGamepads[i])
			{
				slotGamepads[i] = pad;
				slotDisplayNames[i] = GetDisplayNameForGamepad(pad, i);
				GetDeviceIdsForGamepad(pad, &slotVendorIds[i], &slotProductIds[i]);
				break;
			}
		}
	}

	/**
	 * @brief Handles a gamepad removal event by clearing the corresponding slot.
	 *
	 * Searches the tracked slots for the given gamepad handle; when a match is found,
	 * removes the stored handle and resets the slot's vendor and product IDs to zero.
	 *
	 * @param pad The gamepad that was removed.
	 */
	void OnGamepadRemoved(const IInspectable&, const Gamepad& pad)
	{
		std::lock_guard lock(mutex);
		for (int i = 0; i < kMaxSlots; ++i)
		{
			if (slotGamepads[i] && slotGamepads[i] == pad)
			{
				slotGamepads[i].reset();
				slotVendorIds[i] = 0;
				slotProductIds[i] = 0;
				break;
			}
		}
	}
};

WgiBackend::WgiBackend() : impl_(std::make_unique<Impl>())
{
}

WgiBackend::~WgiBackend()
{
	if (impl_)
	{
		Gamepad::GamepadAdded(impl_->addedToken);
		Gamepad::GamepadRemoved(impl_->removedToken);
	}
}

/**
 * @brief Initialize the WinRT gamepad backend, enumerate currently connected gamepads, and subscribe to add/remove events.
 *
 * Initializes the WinRT apartment once per thread, acquires the internal mutex, populates up to kMaxSlots with already-connected gamepads (storing each slot's handle, display name, and vendor/product IDs), and registers handlers for future GamepadAdded and GamepadRemoved events, saving their subscription tokens.
 *
 * @param hwnd Window handle provided for initialization context; currently accepted for API compatibility and not otherwise used.
 */
void WgiBackend::Init(HWND)
{
	thread_local bool winrtInitialized = false;
	if (!winrtInitialized)
	{
		init_apartment();
		winrtInitialized = true;
	}

	std::lock_guard lock(impl_->mutex);
	// Enumerate already-connected gamepads (GetAt in loop avoids IVectorView::Size() auto return type issue with MSVC)
	auto gamepads = Gamepad::Gamepads();
	for (int i = 0; i < kMaxSlots; ++i)
	{
		try
		{
			auto pad = gamepads.GetAt(static_cast<uint32_t>(i));
			impl_->slotGamepads[i] = pad;
			impl_->slotDisplayNames[i] = GetDisplayNameForGamepad(pad, i);
			GetDeviceIdsForGamepad(pad, &impl_->slotVendorIds[i], &impl_->slotProductIds[i]);
		}
		catch (const hresult_out_of_bounds&)
		{
			break;
		}
	}
	impl_->addedToken = Gamepad::GamepadAdded([this](const IInspectable& s, const Gamepad& p)
	{
		impl_->OnGamepadAdded(s, p);
	});
	impl_->removedToken = Gamepad::GamepadRemoved([this](const IInspectable& s, const Gamepad& p)
	{
		impl_->OnGamepadRemoved(s, p);
	});
}

void WgiBackend::Poll()
{
	std::lock_guard lock(impl_->mutex);
	for (int i = 0; i < kMaxSlots; ++i)
	{
		auto& gs = impl_->states[i];
		if (!impl_->slotGamepads[i])
		{
			gs = GamepadState{};
			continue;
		}
		try
		{
			auto reading = impl_->slotGamepads[i]->GetCurrentReading();
			gs.connected = true;
			gs.buttons = MapGamepadButtons(reading.Buttons);
			gs.leftTrigger = static_cast<float>(reading.LeftTrigger);
			gs.rightTrigger = static_cast<float>(reading.RightTrigger);
			gs.leftStickX = static_cast<float>(reading.LeftThumbstickX);
			gs.leftStickY = static_cast<float>(reading.LeftThumbstickY);
			gs.rightStickX = static_cast<float>(reading.RightThumbstickX);
			gs.rightStickY = static_cast<float>(reading.RightThumbstickY);
		}
		catch (...)
		{
			gs = GamepadState{};
		}
	}
}

int WgiBackend::GetMaxSlots() const { return kMaxSlots; }

const GamepadState& WgiBackend::GetState(int slot) const
{
	static constexpr GamepadState empty{};
	if (slot < 0 || slot >= kMaxSlots)
		return empty;
	return impl_->states[slot];
}

const char* WgiBackend::GetName() const { return Name; }

/**
 * @brief Retrieves the human-readable display name for a gamepad slot.
 *
 * @param slot Zero-based slot index.
 * @return const char* Pointer to a null-terminated C string containing the display name for the given slot, or `nullptr` if the slot index is out of range or no gamepad is present in that slot. The returned pointer is valid until the next call to GetSlotDisplayName on the same thread.
 */
const char* WgiBackend::GetSlotDisplayName(int slot) const
{
	if (slot < 0 || slot >= kMaxSlots) return nullptr;
	std::lock_guard lock(impl_->mutex);
	if (!impl_->slotGamepads[slot]) return nullptr;
	thread_local std::array<std::string, kMaxSlots> slotResultBuffers;
	slotResultBuffers[slot] = impl_->slotDisplayNames[slot];
	return slotResultBuffers[slot].c_str();
}

/**
 * @brief Retrieve the vendor and product identifiers for a slot's gamepad.
 *
 * Writes the 16-bit vendor and product IDs for the gamepad in the given slot into the provided output pointers. If the slot index is out of range or no gamepad is present in that slot, `0` is written for each ID. Passing a `nullptr` for either output pointer suppresses writing that value.
 *
 * @param slot Slot index (0 .. kMaxSlots - 1) to query.
 * @param vendorId Pointer that receives the vendor ID, or `nullptr` to ignore.
 * @param productId Pointer that receives the product ID, or `nullptr` to ignore.
 */
void WgiBackend::GetSlotDeviceIds(int slot, uint16_t* vendorId, uint16_t* productId) const
{
	if (slot < 0 || slot >= kMaxSlots)
	{
		if (vendorId) *vendorId = 0;
		if (productId) *productId = 0;
		return;
	}
	std::lock_guard lock(impl_->mutex);
	uint16_t vid = impl_->slotGamepads[slot] ? impl_->slotVendorIds[slot] : 0;
	uint16_t pid = impl_->slotGamepads[slot] ? impl_->slotProductIds[slot] : 0;
	if (vendorId) *vendorId = vid;
	if (productId) *productId = pid;
}
