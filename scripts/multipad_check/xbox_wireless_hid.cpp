#include "xbox_wireless_hid.h"
#include <algorithm>
#include <windows.h>
#include <winternl.h>
#include <hidusage.h>
#include <hidpi.h>

/**
 * @brief Determines whether the given USB vendor and product IDs identify an Xbox Wireless device.
 *
 * @param vendorId USB vendor identifier to check.
 * @param productId USB product identifier to check.
 * @return true if the IDs match the known Xbox Wireless vendor and product identifiers, false otherwise.
 */
bool XboxWireless_IsDevice(uint16_t vendorId, uint16_t productId)
{
	return vendorId == XboxWireless_VendorId && productId == XboxWireless_ProductId;
}

/**
 * @brief Extracts the right trigger value from an HID input report and updates the provided gamepad state for Xbox Wireless devices.
 *
 * Attempts to read the right trigger via HID usages across link-collection indices; if a valid usage value is obtained the function normalizes it to the range [0.0, 1.0] and assigns it to gs.rightTrigger. If no usage value is available and the report length is sufficient, a device-specific fallback parsing is applied to derive and clamp a trigger value. If neither method yields a value, gs.rightTrigger is left unchanged.
 *
 * @param vendorId USB vendor ID of the device.
 * @param productId USB product ID of the device.
 * @param sony If true, the function does nothing (used to skip processing for Sony devices).
 * @param gs Mutable reference to the GamepadState to update the rightTrigger field.
 * @param pp Pointer to HID preparsed data (PHIDP_PREPARSED_DATA).
 * @param report Pointer to the HID input report buffer.
 * @param rLen Length of the HID input report in bytes.
 * @param numLinkCollectionNodes Number of link-collection nodes to query for usage values (uses 1 if zero).
 */
void XboxWireless_ApplyRightTrigger(uint16_t vendorId, uint16_t productId, bool sony,
                                    GamepadState& gs, void* pp,
                                    const char* report, unsigned long rLen, unsigned short numLinkCollectionNodes)
{
	if (sony || !XboxWireless_IsDevice(vendorId, productId))
		return;

	auto* preparsed = static_cast<PHIDP_PREPARSED_DATA>(pp);
	ULONG rzVal = 0;
	NTSTATUS rzStatus = 1;
	const USHORT maxLink = (numLinkCollectionNodes > 0) ? numLinkCollectionNodes : 1;
	for (USHORT lc = 0; lc < maxLink && lc < 32u; ++lc)
	{
		rzStatus = HidP_GetUsageValue(HidP_Input, HID_USAGE_PAGE_GENERIC, lc, HID_USAGE_GENERIC_RZ,
		                              &rzVal, preparsed, const_cast<char*>(report), rLen);
		if (rzStatus == HIDP_STATUS_SUCCESS)
		{
			// Xbox Wireless reports Rz as a centered 16-bit value (center ≈ 32768); normalize with
			// (rzVal - 32768) / 32767 and clamp to [0,1] so rest=0 and full press=1.
			gs.rightTrigger = std::clamp((static_cast<float>(rzVal) - 32768.0f) / 32767.0f, 0.0f, 1.0f);
			return;
		}
	}

	if (rLen >= 11)
	{
		// Fallback when HidP_GetUsageValue fails: the device's raw HID report encodes trigger
		// pressure inverted (raw 0 -> 1.0, 128 -> 0.0). Values >128 are left-trigger territory
		// and are treated as invalid for right trigger (gs.rightTrigger reset to 0 per device spec).
		const size_t dataStart = 1u;
		const size_t triggerWordOffset = dataStart + 8u;
		uint8_t combined = static_cast<unsigned char>(report[triggerWordOffset + 1]);
		if (combined <= 128u)
			gs.rightTrigger = std::clamp((128.0f - static_cast<float>(combined)) / 128.0f, 0.0f, 1.0f);
		else
			gs.rightTrigger = 0.0f;
	}
}
