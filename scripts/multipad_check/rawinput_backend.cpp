#include "rawinput_backend.h"
#include "sony_layout.h"
#include "usb_names.h"
#include "xbox_wireless_hid.h"
#include <algorithm>
#include <format>
#include <ranges>
#include <string>
#include <utility>
#include <hidusage.h>


// ── public interface ─────────────────────────────────────────

void RawInputBackend::Init(HWND hwnd)
{
	hwnd_ = hwnd;

	RAWINPUTDEVICE rid[2]{};
	rid[0].usUsagePage = HID_USAGE_PAGE_GENERIC;
	rid[0].usUsage = HID_USAGE_GENERIC_GAMEPAD;
	rid[0].dwFlags = RIDEV_DEVNOTIFY | RIDEV_INPUTSINK;
	rid[0].hwndTarget = hwnd;
	rid[1].usUsagePage = HID_USAGE_PAGE_GENERIC;
	rid[1].usUsage = HID_USAGE_GENERIC_JOYSTICK;
	rid[1].dwFlags = RIDEV_DEVNOTIFY | RIDEV_INPUTSINK;
	rid[1].hwndTarget = hwnd;

	RegisterRawInputDevices(rid, 2, sizeof(RAWINPUTDEVICE));
	EnumerateExistingDevices();
}

bool RawInputBackend::OnWindowMessage(UINT msg, WPARAM wParam, LPARAM lParam)
{
	switch (msg)
	{
	case WM_INPUT:
		HandleRawInput(reinterpret_cast<HRAWINPUT>(lParam));
		return false;
	case WM_INPUT_DEVICE_CHANGE:
		if (wParam == GIDC_ARRIVAL)
			OnDeviceArrival(reinterpret_cast<HANDLE>(lParam));
		else if (wParam == GIDC_REMOVAL)
			OnDeviceRemoval(reinterpret_cast<HANDLE>(lParam));
		return false;
	}
	return false;
}

void RawInputBackend::Poll()
{
}

int RawInputBackend::GetMaxSlots() const { return kMaxDevices; }

const GamepadState& RawInputBackend::GetState(const int slot) const
{
	return states_[slot];
}

/**
 * @brief Get the backend name.
 *
 * @return const char* Pointer to a null-terminated string identifying the backend.
 */
const char* RawInputBackend::GetName() const { return Name; }

/**
 * @brief Retrieves a human-readable display name for the device occupying a slot.
 *
 * @param slot Device slot index (0..kMaxDevices-1).
 * @return const char* Pointer to a null-terminated friendly name for the device in the specified slot, or `nullptr` if the slot is out of range or no device is present.
 */
const char* RawInputBackend::GetSlotDisplayName(int slot) const
{
	if (slot < 0 || slot >= kMaxDevices) return nullptr;
	for (const auto& d : devices_ | std::views::values)
		if (d.slot == slot)
			return GetFriendlyName(d.vendorId, d.productId);
	return nullptr;
}

/**
 * @brief Obtain the device's preparsed HID data as a PHIDP_PREPARSED_DATA pointer.
 *
 * @param d DeviceInfo containing the raw preparsed data buffer.
 * @return PHIDP_PREPARSED_DATA Pointer to the preparsed HID data, or `nullptr` if the device's preparsed buffer is empty.
 */

PHIDP_PREPARSED_DATA RawInputBackend::PP(DeviceInfo& d)
{
	if (d.preparsedBuf.empty())
		return nullptr;
	return reinterpret_cast<PHIDP_PREPARSED_DATA>(d.preparsedBuf.data());
}

int RawInputBackend::AllocateSlot()
{
	for (int i : std::views::iota(0, kMaxDevices))
		if (std::ranges::none_of(devices_, [i](const auto& kv) { return kv.second.slot == i; }))
			return i;
	return -1;
}

bool RawInputBackend::IsGamepadOrJoystick(HANDLE h)
{
	RID_DEVICE_INFO info{};
	info.cbSize = sizeof(info);
	UINT sz = sizeof(info);
	if (GetRawInputDeviceInfo(h, RIDI_DEVICEINFO, &info, &sz) == static_cast<UINT>(-1))
		return false;
	if (info.dwType != RIM_TYPEHID) return false;
	return info.hid.usUsagePage == HID_USAGE_PAGE_GENERIC &&
		(info.hid.usUsage == HID_USAGE_GENERIC_JOYSTICK || info.hid.usUsage == HID_USAGE_GENERIC_GAMEPAD);
}

/**
 * @brief Initializes DeviceInfo for a raw HID device and retrieves its HID descriptors.
 *
 * Populates the provided DeviceInfo with the device handle, vendor/product identifiers (when available),
 * the preparsed HID buffer, HID capabilities, and input value capability descriptors.
 *
 * @param h Raw input device handle to initialize from.
 * @param d Output structure that will be filled with preparsed HID data, caps, value caps, vendorId, productId, and handle.
 * @return true if the device information and required HID descriptors were successfully retrieved and stored in `d`, false if any required data (preparsed buffer or HID capabilities) could not be obtained.
 */
bool RawInputBackend::SetupDevice(HANDLE h, DeviceInfo& d)
{
	d.handle = h;

	RID_DEVICE_INFO devInfo{};
	devInfo.cbSize = sizeof(devInfo);
	UINT devInfoSz = sizeof(devInfo);
	if (GetRawInputDeviceInfo(h, RIDI_DEVICEINFO, &devInfo, &devInfoSz) != static_cast<UINT>(-1)
		&& devInfo.dwType == RIM_TYPEHID)
	{
		d.vendorId = static_cast<uint16_t>(devInfo.hid.dwVendorId);
		d.productId = static_cast<uint16_t>(devInfo.hid.dwProductId);
	}

	UINT ppSz = 0;
	if (GetRawInputDeviceInfo(h, RIDI_PREPARSEDDATA, nullptr, &ppSz) != 0)
		return false;
	if (ppSz == 0) return false;

	d.preparsedBuf.resize(ppSz);
	if (GetRawInputDeviceInfo(h, RIDI_PREPARSEDDATA,
	                          d.preparsedBuf.data(), &ppSz) == static_cast<UINT>(-1))
		return false;

	auto pp = PP(d);
	if (HidP_GetCaps(pp, &d.caps) != HIDP_STATUS_SUCCESS)
		return false;

	if (d.caps.NumberInputValueCaps > 0)
	{
		USHORT n = d.caps.NumberInputValueCaps;
		d.valueCaps.resize(n);
		if (HidP_GetValueCaps(HidP_Input, d.valueCaps.data(), &n, pp)
			!= HIDP_STATUS_SUCCESS)
			d.valueCaps.clear();
		else
			d.valueCaps.resize(n);
	}

	return true;
}

// ── device lifecycle ─────────────────────────────────────────

void RawInputBackend::EnumerateExistingDevices()
{
	UINT n = 0;
	GetRawInputDeviceList(nullptr, &n, sizeof(RAWINPUTDEVICELIST));
	if (n == 0) return;

	std::vector<RAWINPUTDEVICELIST> list(n);
	if (GetRawInputDeviceList(list.data(), &n, sizeof(RAWINPUTDEVICELIST))
		== static_cast<UINT>(-1))
		return;

	for (UINT i = 0; i < n; ++i)
		if (list[i].dwType == RIM_TYPEHID)
			OnDeviceArrival(list[i].hDevice);
}

void RawInputBackend::OnDeviceArrival(HANDLE h)
{
	if (devices_.contains(h)) return;
	if (!IsGamepadOrJoystick(h)) return;

	DeviceInfo d;
	if (!SetupDevice(h, d)) return;

	int s = AllocateSlot();
	if (s < 0) return;

	d.slot = s;
	states_[s] = GamepadState{};
	states_[s].connected = true;
	devices_[h] = std::move(d);
}

void RawInputBackend::OnDeviceRemoval(HANDLE h)
{
	const auto it = devices_.find(h);
	if (it == devices_.end()) return;
	const int s = it->second.slot;
	if (s >= 0 && s < kMaxDevices)
		states_[s] = GamepadState{};
	devices_.erase(it);
}

// ── input parsing ────────────────────────────────────────────

void RawInputBackend::HandleRawInput(const HRAWINPUT hri)
{
	UINT sz = 0;
	GetRawInputData(hri, RID_INPUT, nullptr, &sz, sizeof(RAWINPUTHEADER));
	if (sz == 0) return;

	rawBuf_.resize(sz);
	if (GetRawInputData(hri, RID_INPUT, rawBuf_.data(), &sz,
	                    sizeof(RAWINPUTHEADER)) != sz)
		return;

	auto* raw = reinterpret_cast<RAWINPUT*>(rawBuf_.data());
	if (raw->header.dwType != RIM_TYPEHID) return;

	auto it = devices_.find(raw->header.hDevice);
	if (it == devices_.end()) return;

	auto& dev = it->second;
	if (dev.slot < 0 || dev.slot >= kMaxDevices) return;

	ParseReport(dev, raw->data.hid);
}

/**
 * @brief Parse raw HID reports for a device and update its gamepad state.
 *
 * Processes each report in the provided RAWHID packet, extracts button usages,
 * axis and hat values (honoring per-report-ID filtering), applies vendor-specific
 * mappings (including Sony mappings and vendor-specific right-trigger adjustments),
 * and updates the backend's GamepadState for the device's assigned slot.
 *
 * @param dev DeviceInfo for the source device; its assigned slot is used to locate
 *            the GamepadState that will be updated.
 * @param hid Raw HID packet containing one or more HID reports to parse.
 */
void RawInputBackend::ParseReport(DeviceInfo& dev, RAWHID& hid)
{
	const auto pp = PP(dev);
	auto& gs = states_[dev.slot];
	gs.connected = true;

	for (DWORD i = 0; i < hid.dwCount; ++i)
	{
		auto report = reinterpret_cast<PCHAR>(hid.bRawData + i * hid.dwSizeHid);
		ULONG rLen = hid.dwSizeHid;

		uint16_t btns = 0;
		const bool sony = IsSonyGamepad(dev.vendorId, dev.productId);

		ULONG maxU = HidP_MaxUsageListLength(HidP_Input, HID_USAGE_PAGE_BUTTON, pp);
		if (maxU > 0)
		{
			usageBuf_.resize(maxU);
			ULONG cnt = maxU;
			if (HidP_GetUsages(HidP_Input, HID_USAGE_PAGE_BUTTON, 0, usageBuf_.data(),
			                   &cnt, pp, report, rLen) == HIDP_STATUS_SUCCESS)
			{
				for (ULONG j = 0; j < cnt; ++j)
					btns |= sony ? MapSonyHidButton(usageBuf_[j]) : MapButton(usageBuf_[j]);
			}
		}

		const UCHAR reportId = (rLen > 0) ? static_cast<UCHAR>(report[0]) : 0;
		for (const auto& vc : dev.valueCaps)
		{
			if (vc.UsagePage != HID_USAGE_PAGE_GENERIC) continue;
			if (vc.ReportID != 0 && (rLen == 0 || reportId != vc.ReportID)) continue;

			const USAGE uMin = vc.IsRange ? vc.Range.UsageMin : vc.NotRange.Usage;
			const USAGE uMax = vc.IsRange ? vc.Range.UsageMax : vc.NotRange.Usage;

			for (USAGE u = uMin; u <= uMax; ++u)
			{
				ULONG val = 0;
				if (HidP_GetUsageValue(HidP_Input, vc.UsagePage, vc.LinkCollection, u,
				                       &val, pp, report, rLen) != HIDP_STATUS_SUCCESS)
					continue;

				if (sony)
				{
					switch (u)
					{
					case HID_USAGE_GENERIC_X: gs.leftStickX = NormStick(val, vc);
						break;
					case HID_USAGE_GENERIC_Y: gs.leftStickY = -NormStick(val, vc);
						break;
					case HID_USAGE_GENERIC_Z: gs.rightStickX = NormStick(val, vc);
						break;
					case HID_USAGE_GENERIC_RX: gs.leftTrigger = NormTrigger(val, vc);
						break;
					case HID_USAGE_GENERIC_RY: gs.rightTrigger = NormTrigger(val, vc);
						break;
					case HID_USAGE_GENERIC_RZ: gs.rightStickY = -NormStick(val, vc);
						break;
					case HID_USAGE_GENERIC_HATSWITCH: btns |= MapHat(val, vc);
						break;
					}
				}
				else
				{
					switch (u)
					{
					case HID_USAGE_GENERIC_X: gs.leftStickX = NormStick(val, vc);
						break;
					case HID_USAGE_GENERIC_Y: gs.leftStickY = -NormStick(val, vc);
						break;
					case HID_USAGE_GENERIC_Z: gs.leftTrigger = NormTrigger(val, vc);
						break;
					case HID_USAGE_GENERIC_RX: gs.rightStickX = NormStick(val, vc);
						break;
					case HID_USAGE_GENERIC_RY: gs.rightStickY = -NormStick(val, vc);
						break;
					case HID_USAGE_GENERIC_RZ: gs.rightTrigger = NormTrigger(val, vc);
						break;
					case HID_USAGE_GENERIC_HATSWITCH: btns |= MapHat(val, vc);
						break;
					}
				}
			}
		}

		XboxWireless_ApplyRightTrigger(dev.vendorId, dev.productId, sony, gs, pp, report, rLen,
			(dev.caps.NumberLinkCollectionNodes > 0) ? dev.caps.NumberLinkCollectionNodes : 1);

		gs.buttons = btns;
	}
}

// ── mapping helpers ──────────────────────────────────────────

uint16_t RawInputBackend::MapButton(const USAGE u)
{
	using enum Button;
	switch (u)
	{
	case 1: return std::to_underlying(A);
	case 2: return std::to_underlying(B);
	case 3: return std::to_underlying(X);
	case 4: return std::to_underlying(Y);
	case 5: return std::to_underlying(LeftBumper);
	case 6: return std::to_underlying(RightBumper);
	case 7: return std::to_underlying(Back);
	case 8: return std::to_underlying(Start);
	case 9: return std::to_underlying(LeftThumb);
	case 10: return std::to_underlying(RightThumb);
	default: return 0;
	}
}

uint16_t RawInputBackend::MapHat(const ULONG val, const HIDP_VALUE_CAPS& vc)
{
	using enum Button;
	LONG v = static_cast<LONG>(val);
	if (v < vc.LogicalMin || v > vc.LogicalMax) return 0;

	int dir = v - vc.LogicalMin;
	int range = vc.LogicalMax - vc.LogicalMin + 1;

	constexpr uint16_t N = std::to_underlying(DPadUp);
	constexpr uint16_t S = std::to_underlying(DPadDown);
	constexpr uint16_t W = std::to_underlying(DPadLeft);
	constexpr uint16_t E = std::to_underlying(DPadRight);

	if (range == 8)
	{
		constexpr uint16_t t[] = {N, N | E, E, S | E, S, S | W, W, N | W};
		return (dir >= 0 && dir < 8) ? t[dir] : 0;
	}
	if (range == 4)
	{
		constexpr uint16_t t[] = {N, E, S, W};
		return (dir >= 0 && dir < 4) ? t[dir] : 0;
	}
	return 0;
}

// ── axis normalization ───────────────────────────────────────

LONG RawInputBackend::ToSigned(const ULONG raw, const HIDP_VALUE_CAPS& vc)
{
	if (vc.LogicalMin >= 0) return static_cast<LONG>(raw);
	USHORT bits = vc.BitSize;
	if (bits == 0 || bits >= 32) return static_cast<LONG>(raw);
	ULONG sign = 1UL << (bits - 1);
	if (raw & sign)
		return static_cast<LONG>(raw | (~0UL << bits));
	return static_cast<LONG>(raw);
}

/**
 * @brief Normalizes a stick axis value to the range [-1, 1].
 *
 * Converts a raw HID axis reading to a centered floating-point value using the logical
 * minimum and maximum from the provided value capability. If the capability's logical
 * range is invalid (LogicalMin >= LogicalMax), treats the input as a 16-bit unsigned
 * axis with midpoint 32767.5 as a fallback. The result is clamped to [-1, 1].
 *
 * @param raw Raw axis value from the HID report.
 * @param vc HID value capability describing the axis's logical range and bit size.
 * @return float Normalized axis value in the range [-1, 1].
 */
float RawInputBackend::NormStick(const ULONG raw, const HIDP_VALUE_CAPS& vc)
{
	LONG lo = vc.LogicalMin, hi = vc.LogicalMax;
	if (lo >= hi) {
		// Fallback for invalid/mangled range: derive unsigned range from vc.BitSize.
		ULONG maxVal = 0xFFFFu;
		if (vc.BitSize > 0u && vc.BitSize <= 31u)
			maxVal = (1u << vc.BitSize) - 1u;
		const float maxF = static_cast<float>(maxVal);
		const float mid = maxF * 0.5f, half = maxF * 0.5f;
		float v = static_cast<float>(raw);
		return std::clamp((v - mid) / half, -1.0f, 1.0f);
	}
	float v = static_cast<float>(ToSigned(raw, vc));
	float mid = (lo + hi) / 2.0f;
	float half = (hi - lo) / 2.0f;
	return std::clamp((v - mid) / half, -1.0f, 1.0f);
}

/**
 * @brief Normalizes a raw trigger axis value into the range [0, 1].
 *
 * Uses the HID value cap's logical minimum and maximum to map the axis so that the logical
 * minimum becomes 0 and the logical maximum becomes 1. If the value cap contains an invalid
 * range (logical minimum greater than or equal to logical maximum), falls back to treating
 * the input as a 16-bit axis with rest at 32768 and maps that center-based range into [0, 1].
 *
 * @param raw The raw axis value read from the HID report.
 * @param vc  The HIDP_VALUE_CAPS describing the axis (logical range, bit size, etc.).
 * @return float A clamped value between 0.0 and 1.0 representing the normalized trigger position.
 */
float RawInputBackend::NormTrigger(const ULONG raw, const HIDP_VALUE_CAPS& vc)
{
	LONG lo = vc.LogicalMin, hi = vc.LogicalMax;
	if (lo >= hi) {
		// Fallback: derive unsigned range from vc.BitSize, center at max/2; map upper half to [0,1] (rest=0, full=1).
		ULONG maxVal = 0xFFFFu;
		if (vc.BitSize > 0u && vc.BitSize <= 31u)
			maxVal = (1u << vc.BitSize) - 1u;
		const float maxF = static_cast<float>(maxVal);
		const float mid = maxF * 0.5f, half = maxF * 0.5f;
		float n = (static_cast<float>(raw) - mid) / half;
		return std::clamp(n, 0.0f, 1.0f);
	}
	float v = static_cast<float>(ToSigned(raw, vc));
	return std::clamp((v - lo) / static_cast<float>(hi - lo), 0.0f, 1.0f);
}
