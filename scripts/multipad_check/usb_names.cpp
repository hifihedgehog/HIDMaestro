#include "usb_names.h"
#include <algorithm>
#include <utility>

namespace {

struct Entry
{
	uint16_t vid;
	uint16_t pid;
	const char* name;
};

// Microsoft (045e) Xbox-related devices and Sony (054c) gamepads from usb.ids / sony_layout.
// Sorted by (vid, pid) for binary search.
static constexpr Entry kTable[] = {
	// Microsoft Corp. (045e) - Xbox
	{0x045e, 0x0202, "Xbox Controller"},
	{0x045e, 0x0280, "Xbox Memory Unit (8MB)"},
	{0x045e, 0x0283, "Xbox Communicator"},
	{0x045e, 0x0284, "Xbox DVD Playback Kit"},
	{0x045e, 0x0285, "Xbox Controller S"},
	{0x045e, 0x0288, "Xbox Controller S Hub"},
	{0x045e, 0x0289, "Xbox Controller S"},
	{0x045e, 0x028b, "Xbox360 DVD Emulator"},
	{0x045e, 0x028d, "Xbox360 Memory Unit 64MB"},
	{0x045e, 0x028e, "Xbox360 Controller"},
	{0x045e, 0x028f, "Xbox360 Wireless Controller via Plug & Charge Cable"},
	{0x045e, 0x0290, "Xbox360 Performance Pipe (PIX)"},
	{0x045e, 0x0291, "Xbox 360 Wireless Receiver for Windows"},
	{0x045e, 0x0292, "Xbox360 Wireless Networking Adapter"},
	{0x045e, 0x029c, "Xbox360 HD-DVD Drive"},
	{0x045e, 0x029d, "Xbox360 HD-DVD Drive"},
	{0x045e, 0x029e, "Xbox360 HD-DVD Memory Unit"},
	{0x045e, 0x02a0, "Xbox360 Big Button IR"},
	{0x045e, 0x02a8, "Xbox360 Wireless N Networking Adapter [Atheros AR7010+AR9280]"},
	{0x045e, 0x02ad, "Xbox NUI Audio"},
	{0x045e, 0x02ae, "Xbox NUI Camera"},
	{0x045e, 0x02b0, "Xbox NUI Motor"},
	{0x045e, 0x02b6, "Xbox360 Bluetooth Wireless Headset"},
	{0x045e, 0x02bb, "Kinect Audio"},
	{0x045e, 0x02be, "Kinect for Windows NUI Audio"},
	{0x045e, 0x02bf, "Kinect for Windows NUI Camera"},
	{0x045e, 0x02c2, "Kinect for Windows NUI Motor"},
	{0x045e, 0x02d1, "Xbox One Controller"},
	{0x045e, 0x02d5, "Xbox One Digital TV Tuner"},
	{0x045e, 0x02dd, "Xbox One Controller (Firmware 2015)"},
	{0x045e, 0x02e0, "Xbox One Wireless Controller"},
	{0x045e, 0x02e3, "Xbox One Elite Controller"},
	{0x045e, 0x02e6, "Wireless XBox Controller Dongle"},
	{0x045e, 0x02ea, "Xbox One S Controller"},
	{0x045e, 0x02f3, "Xbox One Chatpad"},
	{0x045e, 0x02fd, "Xbox One S Controller [Bluetooth]"},
	{0x045e, 0x02fe, "Xbox Wireless Adapter for Windows"},
	{0x045e, 0x02ff, "Xbox Wireless Controller"},
	{0x045e, 0x0b00, "Xbox Elite Series 2 Controller (model 1797)"},
	{0x045e, 0x0b12, "Xbox Controller"},
	// Sony (054c) - DualShock / DualSense
	{0x054c, 0x05c4, "DualShock 4"},
	{0x054c, 0x09cc, "DualShock 4 (v2 / Pro/Slim)"},
	{0x054c, 0x0ce6, "DualSense"},
	{0x054c, 0x0df2, "DualSense Edge"},
};

} /**
 * @brief Lookup a human-friendly name for a USB device by vendor and product ID.
 *
 * @param vid USB vendor ID.
 * @param pid USB product ID.
 * @return const char* Pointer to a null-terminated string containing the device name if a matching VID/PID is found, `nullptr` otherwise. The returned pointer refers to static storage owned by this module. 
 */

const char* GetFriendlyName(uint16_t vid, uint16_t pid)
{
	const auto it = std::lower_bound(std::begin(kTable), std::end(kTable),
		std::pair{vid, pid},
		[](const Entry& e, const std::pair<uint16_t, uint16_t>& vp) {
			if (e.vid != vp.first) return e.vid < vp.first;
			return e.pid < vp.second;
		});
	if (it != std::end(kTable) && it->vid == vid && it->pid == pid)
		return it->name;
	return nullptr;
}
