#include "hidapi_backend.h"
#include "sony_layout.h"
#include "usb_names.h"
#include "xbox_wireless_hid.h"
#include <algorithm>
#include <format>
#include <ranges>
#include <string>
#include <utility>
#include <hidusage.h>

// ── lifecycle ────────────────────────────────────────────────

HidApiBackend::~HidApiBackend()
{
	for (auto& d : devices_)
		CloseDevice(*d);
	devices_.clear();
}

void HidApiBackend::CloseDevice(DeviceInfo& dev)
{
	if (dev.handle.is_valid())
	{
		if (dev.readPending)
			CancelIo(dev.handle.get());
		dev.handle.reset();
	}
	dev.readEvent.reset();
	dev.overlapped.hEvent = nullptr;
	dev.readPending = false;
	dev.preparsed.reset();
	dev.readBuf.clear();
	if (dev.slot >= 0 && dev.slot < kMaxDevices)
		states_[dev.slot] = GamepadState{};
}

// ── public interface ─────────────────────────────────────────

void HidApiBackend::Poll()
{
	if (firstPoll_)
	{
		firstPoll_ = false;
		EnumerateDevices();
	}

	if (++pollCounter_ >= 120)
	{
		pollCounter_ = 0;
		EnumerateDevices();
	}

	for (auto it = devices_.begin(); it != devices_.end();)
	{
		auto& dev = **it;
		if (!dev.handle.is_valid())
		{
			++it;
			continue;
		}

		if (!dev.readPending)
		{
			StartRead(dev);
			++it;
			continue;
		}

		DWORD bytesRead = 0;
		BOOL ok = GetOverlappedResult(dev.handle.get(), &dev.overlapped,
		                              &bytesRead, FALSE);
		if (ok)
		{
			dev.readPending = false;
			ParseReport(dev, bytesRead);
			StartRead(dev);
			++it;
		}
		else
		{
			DWORD err = GetLastError();
			if (err == ERROR_IO_INCOMPLETE)
			{
				++it;
			}
			else
			{
				dev.readPending = false;
				CloseDevice(dev);
				it = devices_.erase(it);
			}
		}
	}
}

int HidApiBackend::GetMaxSlots() const { return kMaxDevices; }

const GamepadState& HidApiBackend::GetState(const int slot) const
{
	return states_[slot];
}

/**
 * @brief Returns the backend's identifier string.
 *
 * @return const char* Null-terminated name of this HID backend.
 */
const char* HidApiBackend::GetName() const { return Name; }

/**
 * @brief Retrieve the user-facing display name for a device assigned to a slot.
 *
 * @param slot Slot index to query (valid range: 0 to kMaxDevices - 1).
 * @return const char* Pointer to a friendly name derived from the device's vendor and product IDs,
 *         or `nullptr` if the slot is out of range or no device is assigned to that slot.
 */
const char* HidApiBackend::GetSlotDisplayName(int slot) const
{
	if (slot < 0 || slot >= kMaxDevices) return nullptr;
	const auto it = std::ranges::find_if(devices_, [slot](const std::unique_ptr<DeviceInfo>& d) { return d->slot == slot; });
	if (it == devices_.end()) return nullptr;
	return GetFriendlyName((*it)->vendorId, (*it)->productId);
}

/**
 * @brief Enumerates connected HID devices and synchronizes the backend's device list.
 *
 * @details Scans the system for present HID device interfaces, marks previously-known
 * devices that remain present, attempts to open and initialize newly discovered devices,
 * and removes (and closes) devices that are no longer present. Updates the internal
 * devices_ container and associated per-slot state as devices are added or removed.
 */

void HidApiBackend::EnumerateDevices()
{
	for (auto& d : devices_) d->found = false;

	GUID hidGuid;
	HidD_GetHidGuid(&hidGuid);

	HDEVINFO devInfoSet = SetupDiGetClassDevsW(
		&hidGuid, nullptr, nullptr,
		DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
	if (devInfoSet == INVALID_HANDLE_VALUE)
		return;

	SP_DEVICE_INTERFACE_DATA ifData{};
	ifData.cbSize = sizeof(ifData);

	for (DWORD idx = 0;
	     SetupDiEnumDeviceInterfaces(devInfoSet, nullptr, &hidGuid,
	                                 idx, &ifData);
	     ++idx)
	{
		DWORD requiredSize = 0;
		SetupDiGetDeviceInterfaceDetailW(devInfoSet, &ifData,
		                                 nullptr, 0, &requiredSize, nullptr);
		if (requiredSize == 0) continue;

		std::vector<BYTE> detailBuf(requiredSize);
		auto* detail = reinterpret_cast<SP_DEVICE_INTERFACE_DETAIL_DATA_W*>(
			detailBuf.data());
		detail->cbSize = sizeof(SP_DEVICE_INTERFACE_DETAIL_DATA_W);

		if (!SetupDiGetDeviceInterfaceDetailW(devInfoSet, &ifData,
		                                      detail, requiredSize, nullptr, nullptr))
			continue;

		const wchar_t* path = detail->DevicePath;

		bool alreadyKnown = false;
		for (auto& d : devices_)
		{
			if (_wcsicmp(d->path.c_str(), path) == 0)
			{
				d->found = true;
				alreadyKnown = true;
				break;
			}
		}
		if (alreadyKnown) continue;

		auto info = std::make_unique<DeviceInfo>();
		if (OpenAndSetup(path, *info))
		{
			info->found = true;
			devices_.push_back(std::move(info));
		}
	}

	SetupDiDestroyDeviceInfoList(devInfoSet);

	std::erase_if(devices_, [this](const std::unique_ptr<DeviceInfo>& d)
	{
		if (d->found) return false;
		CloseDevice(*d);
		return true;
	});
}

bool HidApiBackend::OpenAndSetup(const wchar_t* path, DeviceInfo& info)
{
	info.path = path;

	HANDLE h = CreateFileW(path,
	                       GENERIC_READ | GENERIC_WRITE,
	                       FILE_SHARE_READ | FILE_SHARE_WRITE,
	                       nullptr, OPEN_EXISTING,
	                       FILE_FLAG_OVERLAPPED, nullptr);

	if (h == INVALID_HANDLE_VALUE)
	{
		h = CreateFileW(path,
		                GENERIC_READ,
		                FILE_SHARE_READ | FILE_SHARE_WRITE,
		                nullptr, OPEN_EXISTING,
		                FILE_FLAG_OVERLAPPED, nullptr);
	}
	if (h == INVALID_HANDLE_VALUE)
		return false;

	info.handle.reset(h);

	HIDD_ATTRIBUTES attrs{};
	attrs.Size = sizeof(attrs);
	if (HidD_GetAttributes(info.handle.get(), &attrs))
	{
		info.vendorId = attrs.VendorID;
		info.productId = attrs.ProductID;
	}

	PHIDP_PREPARSED_DATA pp = nullptr;
	if (!HidD_GetPreparsedData(info.handle.get(), &pp))
	{
		info.handle.reset();
		return false;
	}
	info.preparsed.reset(pp);

	if (HidP_GetCaps(pp, &info.caps) != HIDP_STATUS_SUCCESS)
	{
		info.preparsed.reset();
		info.handle.reset();
		return false;
	}

	if (!IsGamepadOrJoystick(info.caps))
	{
		info.preparsed.reset();
		info.handle.reset();
		return false;
	}

	if (info.caps.NumberInputValueCaps > 0)
	{
		USHORT n = info.caps.NumberInputValueCaps;
		info.valueCaps.resize(n);
		if (HidP_GetValueCaps(HidP_Input, info.valueCaps.data(), &n, pp)
			!= HIDP_STATUS_SUCCESS)
			info.valueCaps.clear();
		else
			info.valueCaps.resize(n);
	}

	int s = AllocateSlot();
	if (s < 0)
	{
		info.preparsed.reset();
		info.handle.reset();
		return false;
	}

	info.slot = s;
	states_[s] = GamepadState{};
	states_[s].connected = true;

	info.readBuf.resize(info.caps.InputReportByteLength);

	info.overlapped = {};
	info.readEvent.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
	info.overlapped.hEvent = info.readEvent.get();

	StartRead(info);
	return true;
}

// ── async I/O ────────────────────────────────────────────────

void HidApiBackend::StartRead(DeviceInfo& dev)
{
	if (!dev.handle.is_valid()) return;

	constexpr int kMaxSyncReads = 16;
	for (int attempt = 0; attempt < kMaxSyncReads; ++attempt)
	{
		ResetEvent(dev.overlapped.hEvent);
		DWORD bytesRead = 0;
		BOOL ok = ReadFile(dev.handle.get(), dev.readBuf.data(),
		                   static_cast<DWORD>(dev.readBuf.size()),
		                   &bytesRead, &dev.overlapped);
		if (ok)
		{
			ParseReport(dev, bytesRead);
			continue;
		}

		DWORD err = GetLastError();
		if (err == ERROR_IO_PENDING)
		{
			dev.readPending = true;
		}
		else
		{
			dev.readPending = false;
		}
		return;
	}

	dev.readPending = false;
}

/**
 * @brief Parse a HID input report and update the corresponding gamepad state.
 *
 * Parses the raw HID report in dev.readBuf (length bytesRead), extracts button,
 * hat, axis, and trigger values (with device-specific mappings for Sony/Xbox),
 * applies Xbox wireless right-trigger adjustment when applicable, and writes the
 * results into the backend's GamepadState for dev.slot. If the report is empty,
 * the preparsed data is missing, or the slot is out of range, no state is modified.
 *
 * @param dev DeviceInfo containing the report buffer, preparsed data, value capabilities,
 *            vendor/product IDs, and the target slot whose state will be updated.
 * @param bytesRead Number of bytes of valid report data in dev.readBuf.
 */

void HidApiBackend::ParseReport(DeviceInfo& dev, DWORD bytesRead)
{
	if (bytesRead == 0) return;
	if (dev.slot < 0 || dev.slot >= kMaxDevices) return;

	PHIDP_PREPARSED_DATA pp = dev.preparsed.get();
	if (!pp) return;

	auto& gs = states_[dev.slot];
	gs.connected = true;

	auto report = reinterpret_cast<PCHAR>(dev.readBuf.data());
	ULONG rLen = bytesRead;

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

		USAGE uMin = vc.IsRange ? vc.Range.UsageMin : vc.NotRange.Usage;
		USAGE uMax = vc.IsRange ? vc.Range.UsageMax : vc.NotRange.Usage;

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

// ── slot management ──────────────────────────────────────────

int HidApiBackend::AllocateSlot()
{
	for (int i : std::views::iota(0, kMaxDevices))
		if (std::ranges::none_of(devices_, [i](const auto& d) { return d->slot == i; }))
			return i;
	return -1;
}

// ── mapping helpers ──────────────────────────────────────────

bool HidApiBackend::IsGamepadOrJoystick(const HIDP_CAPS& caps)
{
	return caps.UsagePage == HID_USAGE_PAGE_GENERIC &&
		(caps.Usage == HID_USAGE_GENERIC_JOYSTICK || caps.Usage == HID_USAGE_GENERIC_GAMEPAD);
}

uint16_t HidApiBackend::MapButton(USAGE u)
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

uint16_t HidApiBackend::MapHat(const ULONG val, const HIDP_VALUE_CAPS& vc)
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

LONG HidApiBackend::ToSigned(const ULONG raw, const HIDP_VALUE_CAPS& vc)
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
 * @brief Converts a raw axis reading into a normalized stick value in the range [-1, 1].
 *
 * Uses the range information from `vc` (LogicalMin/LogicalMax) to center and scale the raw value.
 * If `vc.LogicalMin >= vc.LogicalMax` (invalid or mangled range), treats the input as a 16-bit
 * unsigned axis centered at 32767.5 and applies a fallback normalization.
 *
 * @param raw Raw axis sample value as read from the HID report.
 * @param vc HID value capability describing logical range and bit size for the axis.
 * @return float Normalized axis value clamped to [-1.0, 1.0].
 */
float HidApiBackend::NormStick(const ULONG raw, const HIDP_VALUE_CAPS& vc)
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
 * @brief Normalizes a trigger axis value into the range [0, 1].
 *
 * Maps the raw usage value to a floating-point trigger position where 0.0 is released and 1.0 is fully pressed.
 * When the value capabilities specify a valid logical range (LogicalMin < LogicalMax), the raw value is interpreted
 * according to that range and linearly mapped to [0, 1]. If the logical range is invalid or mangled (LogicalMin >= LogicalMax),
 * a fallback mapping assumes a 16-bit unsigned axis with center at 32768 and maps values from center..max to 0..1.
 *
 * @param raw Raw usage value read from the device.
 * @param vc HID value capabilities describing logical range and signedness.
 * @return float Normalized trigger value in the closed interval [0.0, 1.0].
 */
float HidApiBackend::NormTrigger(const ULONG raw, const HIDP_VALUE_CAPS& vc)
{
	const LONG lo = vc.LogicalMin;
	const LONG hi = vc.LogicalMax;
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
