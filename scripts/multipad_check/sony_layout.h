#pragma once

#include "gamepad_state.h"
#include <cstdint>
#include <utility>

// Sony DualShock 4 / DualSense / DualSense Edge: VID 054C, known PIDs.
// DS4: 0x05C4 (v1), 0x09CC (v2 / Pro/Slim). DualSense: 0x0CE6. DualSense Edge: 0x0DF2.
[[nodiscard]] inline bool IsSonyGamepad(uint16_t vid, uint16_t pid)
{
	if (vid != 0x054C) return false;
	switch (pid)
	{
	case 0x05C4: // DS4 v1
	case 0x09CC: // DS4 v2 / Pro / Slim
	case 0x0CE6: // DualSense
	case 0x0DF2: // DualSense Edge
		return true;
	default:
		return false;
	}
}

// HID usage 0x09 (Button): DualSense report order: 1=Square, 2=Cross, 3=Circle, 4=Triangle, 5=L1, 6=R1,
// 7=left trigger (digital), 8=right trigger (digital) -> ignore (axes handle triggers), 9=Create, 10=Options, 11=L3, 12=R3.
[[nodiscard]] inline uint16_t MapSonyHidButton(uint16_t usage)
{
	using enum Button;
	switch (usage)
	{
	case 1:  return std::to_underlying(X);
	case 2:  return std::to_underlying(A);
	case 3:  return std::to_underlying(B);
	case 4:  return std::to_underlying(Y);
	case 5:  return std::to_underlying(LeftBumper);
	case 6:  return std::to_underlying(RightBumper);
	case 7:  return 0;                             // left trigger digital
	case 8:  return 0;                             // right trigger digital
	case 9:  return std::to_underlying(Back);      // Create
	case 10: return std::to_underlying(Start);     // Options
	case 11: return std::to_underlying(LeftThumb); // L3
	case 12: return std::to_underlying(RightThumb);// R3
	case 13: return std::to_underlying(Guide);    // PS / Home
	default: return 0;
	}
}

// DirectInput rgbButtons[] index -> Xbox. Sony driver: 0=Square..3=Triangle, 4=L1, 5=R1,
// 6=LT digital, 7=RT digital (ignore), 8=Create, 9=Options, 10=L3, 11=R3.
[[nodiscard]] inline uint16_t MapButtonSonyDInput(int index)
{
	using enum Button;
	switch (index)
	{
	case 0:  return std::to_underlying(X);
	case 1:  return std::to_underlying(A);
	case 2:  return std::to_underlying(B);
	case 3:  return std::to_underlying(Y);
	case 4:  return std::to_underlying(LeftBumper);
	case 5:  return std::to_underlying(RightBumper);
	case 6:  return 0;                               // left trigger digital
	case 7:  return 0;                               // right trigger digital
	case 8:  return std::to_underlying(Back);         // Create / Share
	case 9:  return std::to_underlying(Start);       // Options
	case 10: return std::to_underlying(LeftThumb);   // L3
	case 11: return std::to_underlying(RightThumb);  // R3
	case 12: return std::to_underlying(Guide);       // PS / Home
	default: return 0;
	}
}

// Sony HID axis usage -> GamepadState. DualSense/Edge use different order than generic:
// 0x30=X, 0x31=Y = left stick (same as generic).
// 0x32, 0x35 = right stick X, Y (generic uses 0x32=Z/leftTrigger, 0x35=Rz/rightTrigger).
// 0x33, 0x34 = left trigger, right trigger (generic uses 0x33=Rx, 0x34=Ry for right stick).
// Backends must branch on IsSonyGamepad and apply this axis map when parsing usage 0x01 values.
