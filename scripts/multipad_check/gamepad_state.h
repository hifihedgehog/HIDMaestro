#pragma once
#include <cstdint>
#include <utility>

/**
 * Logical gamepad buttons represented as bit flags.
 *
 * These values match the bit layout used by `GamepadState::buttons`.
 */
enum class Button : uint16_t
{
	DPadUp = 0x0001,
	DPadDown = 0x0002,
	DPadLeft = 0x0004,
	DPadRight = 0x0008,
	Start = 0x0010,
	Back = 0x0020,
	LeftThumb = 0x0040,
	RightThumb = 0x0080,
	LeftBumper = 0x0100,
	RightBumper = 0x0200,
	Guide = 0x0400,
	A = 0x1000,
	B = 0x2000,
	X = 0x4000,
	Y = 0x8000,
};

/**
 * Snapshot of a single gamepad's current state.
 *
 * Axis values are normalized to the range shown in the field comments.
 * Buttons are stored as a bitmask of `Button` values.
 */
struct GamepadState
{
	bool connected = false;
	float leftStickX = 0.0f; // -1 .. 1
	float leftStickY = 0.0f; // -1 .. 1
	float rightStickX = 0.0f; // -1 .. 1
	float rightStickY = 0.0f; // -1 .. 1
	float leftTrigger = 0.0f; //  0 .. 1
	float rightTrigger = 0.0f; //  0 .. 1
	uint16_t buttons = 0;

	/**
	 * Check whether a button flag is currently set.
	 *
	 * @param b Button flag to test.
	 * @return true if the button is pressed; otherwise false.
	 */
	[[nodiscard]] bool IsPressed(Button b) const
	{
		return (buttons & std::to_underlying(b)) != 0;
	}
};
