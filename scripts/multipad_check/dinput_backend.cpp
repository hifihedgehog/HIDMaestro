#include "dinput_backend.h"
#include "sony_layout.h"
#include "usb_names.h"
#include "xbox_wireless_hid.h"
#include <algorithm>
#include <ranges>
#include <utility>

// ── lifecycle ────────────────────────────────────────────────

DInputBackend::~DInputBackend()
{
	ReleaseDevices();
	di_.reset();
}

void DInputBackend::Init(const HWND hwnd)
{
	hwnd_ = hwnd;

	auto hInst = reinterpret_cast<HINSTANCE>(
		GetWindowLongPtr(hwnd, GWLP_HINSTANCE));

	if (FAILED(DirectInput8Create(hInst, DIRECTINPUT_VERSION,
		IID_IDirectInput8W, reinterpret_cast<void**>(di_.put()), nullptr)))
		return;

	EnumerateDevices();
}

void DInputBackend::ReleaseDevices()
{
	for (auto& d : devices_)
	{
		if (d.device)
		{
			d.device->Unacquire();
			d.device.reset();
		}
		if (d.slot >= 0 && d.slot < kMaxDevices)
			states_[d.slot] = GamepadState{};
	}
	devices_.clear();
}

// ── public interface ─────────────────────────────────────────

void DInputBackend::Poll()
{
	if (!di_) return;

	if (++pollCounter_ >= 120)
	{
		pollCounter_ = 0;
		EnumerateDevices();
	}

	for (auto& dev : devices_)
	{
		if (!dev.device || dev.slot < 0) continue;
		auto& gs = states_[dev.slot];

		HRESULT hr = dev.device->Poll();
		if (FAILED(hr))
		{
			hr = dev.device->Acquire();
			if (FAILED(hr))
			{
				gs = GamepadState{};
				continue;
			}
			dev.device->Poll();
		}

		DIJOYSTATE2 js{};
		hr = dev.device->GetDeviceState(sizeof(js), &js);
		if (FAILED(hr))
		{
			gs = GamepadState{};
			continue;
		}

		gs.connected = true;

		const bool sony = IsSonyGamepad(dev.vendorId, dev.productId)
			|| IsSonyByProductName(dev.productName);
		if (sony)
		{
			// Sony DInput: triggers on lRx/lRy, right stick on lZ/lRz (LT moves lRx, RT moves lRy).
			gs.leftStickX = NormStick(js.lX);
			gs.leftStickY = -NormStick(js.lY);
			gs.rightStickX = NormStick(js.lZ);
			gs.rightStickY = -NormStick(js.lRz);
			gs.leftTrigger = NormTrigger(js.lRx);
			gs.rightTrigger = NormTrigger(js.lRy);
		}
		else
		{
			gs.leftStickX = NormStick(js.lX);
			gs.leftStickY = -NormStick(js.lY);
			gs.rightStickX = NormStick(js.lRx);
			gs.rightStickY = -NormStick(js.lRy);
			if (XboxWireless_IsDevice(dev.vendorId, dev.productId))
			{
				// Xbox Wireless exposes a single combined axis on lZ: center=rest, upper half=LT, lower half=RT.
				const float lZ = static_cast<float>(js.lZ);
				if (lZ >= 32768.0f)
				{
					gs.leftTrigger = std::clamp((lZ - 32768.0f) / 32767.0f, 0.0f, 1.0f);
					gs.rightTrigger = 0.0f;
				}
				else
				{
					gs.leftTrigger = 0.0f;
					gs.rightTrigger = std::clamp((32768.0f - lZ) / 32768.0f, 0.0f, 1.0f);
				}
			}
			else
			{
				gs.leftTrigger = NormTrigger(js.lZ);
				gs.rightTrigger = NormTrigger(js.lRz);
			}
		}

		uint16_t btns = 0;
		const int maxBtn = sony ? 13 : 10;
		for (int i = 0; i < maxBtn; ++i)
			if (js.rgbButtons[i] & 0x80)
				btns |= sony ? MapButtonSonyDInput(i) : MapButton(i);

		btns |= MapPOV(js.rgdwPOV[0]);
		gs.buttons = btns;
	}
}

int DInputBackend::GetMaxSlots() const { return kMaxDevices; }

const GamepadState& DInputBackend::GetState(int slot) const
{
	return states_[slot];
}

/**
 * Retrieve the backend's short name.
 *
 * @return Pointer to a null-terminated C-string with the backend name.
 */
const char* DInputBackend::GetName() const { return Name; }

/**
 * @brief Retrieve a human-friendly display name for the device occupying a slot.
 *
 * @param slot Device slot index (valid range: 0..kMaxDevices-1).
 * @return const char* Null-terminated display name for the device, or `nullptr` if the slot is out of range or no device is present.
 */
const char* DInputBackend::GetSlotDisplayName(int slot) const
{
	if (slot < 0 || slot >= kMaxDevices) return nullptr;
	const auto it = std::ranges::find_if(devices_, [slot](const DeviceInfo& d) { return d.slot == slot; });
	if (it == devices_.end()) return nullptr;
	return GetFriendlyName(it->vendorId, it->productId);
}

/**
 * @brief Callback invoked for each DirectInput device during enumeration.
 *
 * Marks an already-known device as found or attempts to create and register a new
 * DeviceInfo for the discovered device. When SetupDevice succeeds, the new device
 * is appended to the backend's device list and marked as found.
 *
 * @param inst Pointer to the enumerated device instance information.
 * @param ctx  User context passed to the enumerator; expected to be a pointer to
 *             the DInputBackend instance.
 * @return BOOL Always returns DIENUM_CONTINUE to continue device enumeration.
 */

BOOL CALLBACK DInputBackend::EnumCallback(
	const DIDEVICEINSTANCEW* inst, VOID* ctx)
{
	auto* self = static_cast<DInputBackend*>(ctx);

	for (auto& d : self->devices_)
	{
		if (d.instanceGuid == inst->guidInstance)
		{
			d.found = true;
			return DIENUM_CONTINUE;
		}
	}

	DeviceInfo info;
	if (self->SetupDevice(*inst, info))
	{
		info.found = true;
		self->devices_.push_back(std::move(info));
	}
	return DIENUM_CONTINUE;
}

void DInputBackend::EnumerateDevices()
{
	for (auto& d : devices_) d.found = false;

	di_->EnumDevices(DI8DEVCLASS_GAMECTRL, EnumCallback,
	                 this, DIEDFL_ATTACHEDONLY);

	std::erase_if(devices_, [this](DeviceInfo& d)
	{
		if (d.found) return false;
		if (d.slot >= 0 && d.slot < kMaxDevices)
			states_[d.slot] = GamepadState{};
		if (d.device)
		{
			d.device->Unacquire();
			d.device.reset();
		}
		return true;
	});
}

bool DInputBackend::SetupDevice(
	const DIDEVICEINSTANCEW& inst, DeviceInfo& info)
{
	info.instanceGuid = inst.guidInstance;
	info.vendorId = static_cast<uint16_t>(LOWORD(inst.guidProduct.Data1));
	info.productId = static_cast<uint16_t>(HIWORD(inst.guidProduct.Data1));
	info.productName = inst.tszProductName;

	if (FAILED(di_->CreateDevice(inst.guidInstance, info.device.put(), nullptr)))
		return false;

	if (FAILED(info.device->SetDataFormat(&c_dfDIJoystick2)))
	{
		info.device.reset();
		return false;
	}

	if (FAILED(info.device->SetCooperativeLevel(
		hwnd_, DISCL_BACKGROUND | DISCL_NONEXCLUSIVE)))
	{
		info.device.reset();
		return false;
	}

	DIPROPRANGE range{};
	range.diph.dwSize = sizeof(DIPROPRANGE);
	range.diph.dwHeaderSize = sizeof(DIPROPHEADER);
	range.diph.dwHow = DIPH_DEVICE;
	range.diph.dwObj = 0;
	range.lMin = 0;
	range.lMax = 65535;
	info.device->SetProperty(DIPROP_RANGE, &range.diph);

	int s = AllocateSlot();
	if (s < 0)
	{
		info.device.reset();
		return false;
	}
	info.slot = s;
	states_[s] = GamepadState{};

	info.device->Acquire();
	return true;
}

int DInputBackend::AllocateSlot()
{
	for (int i : std::views::iota(0, kMaxDevices))
		if (std::ranges::none_of(devices_, [i](const auto& d) { return d.slot == i; }))
			return i;
	return -1;
}

// ── mapping helpers ──────────────────────────────────────────

float DInputBackend::NormStick(const LONG value)
{
	return std::clamp((static_cast<float>(value) - 32767.5f) / 32767.5f,
	                  -1.0f, 1.0f);
}

float DInputBackend::NormTrigger(const LONG value)
{
	return std::clamp(static_cast<float>(value) / 65535.0f, 0.0f, 1.0f);
}

uint16_t DInputBackend::MapButton(const int index)
{
	using enum Button;
	switch (index)
	{
	case 0: return std::to_underlying(A);
	case 1: return std::to_underlying(B);
	case 2: return std::to_underlying(X);
	case 3: return std::to_underlying(Y);
	case 4: return std::to_underlying(LeftBumper);
	case 5: return std::to_underlying(RightBumper);
	case 6: return std::to_underlying(Back);
	case 7: return std::to_underlying(Start);
	case 8: return std::to_underlying(LeftThumb);
	case 9: return std::to_underlying(RightThumb);
	default: return 0;
	}
}

uint16_t DInputBackend::MapPOV(DWORD pov)
{
	using enum Button;
	if (LOWORD(pov) == 0xFFFF) return 0;

	constexpr uint16_t N = std::to_underlying(DPadUp);
	constexpr uint16_t S = std::to_underlying(DPadDown);
	constexpr uint16_t W = std::to_underlying(DPadLeft);
	constexpr uint16_t E = std::to_underlying(DPadRight);

	// POV is in hundredths of degrees: 0=N, 4500=NE, 9000=E, ...
	switch (pov)
	{
	case 0: return N;
	case 4500: return N | E;
	case 9000: return E;
	case 13500: return S | E;
	case 18000: return S;
	case 22500: return S | W;
	case 27000: return W;
	case 31500: return N | W;
	default: return 0;
	}
}

bool DInputBackend::IsSonyByProductName(const std::wstring& name)
{
	if (name.empty()) return false;
	wchar_t buf[260];
	size_t n = (name.size() < 259u) ? name.size() : 259u;
	for (size_t i = 0; i < n; ++i)
		buf[i] = name[i];
	buf[n] = L'\0';
	CharUpperBuffW(buf, static_cast<DWORD>(n));
	return wcsstr(buf, L"SONY") != nullptr
		|| wcsstr(buf, L"DUALSENSE") != nullptr
		|| wcsstr(buf, L"DUALSHOCK") != nullptr
		|| wcsstr(buf, L"WIRELESS CONTROLLER") != nullptr; // DS4
}
