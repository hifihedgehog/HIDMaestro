"""Scan all 4 XInput slots once and print sThumbLX/LY for each."""
import ctypes
import ctypes.wintypes as wt


class XINPUT_GAMEPAD(ctypes.Structure):
    _fields_ = [
        ("wButtons", wt.WORD),
        ("bLeftTrigger", wt.BYTE),
        ("bRightTrigger", wt.BYTE),
        ("sThumbLX", ctypes.c_short),
        ("sThumbLY", ctypes.c_short),
        ("sThumbRX", ctypes.c_short),
        ("sThumbRY", ctypes.c_short),
    ]


class XINPUT_STATE(ctypes.Structure):
    _fields_ = [("dwPacketNumber", wt.DWORD), ("Gamepad", XINPUT_GAMEPAD)]


def main() -> int:
    dll = ctypes.WinDLL("XInput1_4.dll")
    dll.XInputGetState.argtypes = [wt.DWORD, ctypes.POINTER(XINPUT_STATE)]
    dll.XInputGetState.restype = wt.DWORD
    for slot in range(4):
        st = XINPUT_STATE()
        rc = dll.XInputGetState(slot, ctypes.byref(st))
        if rc == 0:
            print(f"slot {slot}: CONNECTED pkt={st.dwPacketNumber:8d} "
                  f"LX={st.Gamepad.sThumbLX:+7d} LY={st.Gamepad.sThumbLY:+7d} "
                  f"RX={st.Gamepad.sThumbRX:+7d} RY={st.Gamepad.sThumbRY:+7d} "
                  f"btns=0x{st.Gamepad.wButtons:04X} "
                  f"LT={st.Gamepad.bLeftTrigger:3d} RT={st.Gamepad.bRightTrigger:3d}")
        else:
            print(f"slot {slot}: disconnected (rc={rc})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
