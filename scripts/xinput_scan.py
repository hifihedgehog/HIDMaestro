"""Scan XInput-visible controllers from two angles:

  1. GUID_DEVINTERFACE_XUSB ({EC87F1E3}) enumeration — ground truth for
     "how many XInput-compatible devices does Windows see". This is what
     "XInput Test" and similar 3rd-party tools show. Can exceed 4.

  2. xinput1_4.dll slot probe (XInputGetStateEx, ordinal 100) — shows which
     of the 4 user-mode XInput slots currently have live state. Gaps here
     don't mean a device is missing; they mean xinputhid's per-boot slot
     allocator hasn't assigned a contiguous range.

The two views are complementary: slot view tells you what xinput1_4-using
games will see; interface view tells you what xinputhid+XUSB drivers have
actually published.
"""
import ctypes
import ctypes.wintypes as wt


class XINPUT_GAMEPAD(ctypes.Structure):
    _fields_ = [
        ("wButtons", wt.WORD),
        ("bLeftTrigger", ctypes.c_ubyte),
        ("bRightTrigger", ctypes.c_ubyte),
        ("sThumbLX", ctypes.c_short),
        ("sThumbLY", ctypes.c_short),
        ("sThumbRX", ctypes.c_short),
        ("sThumbRY", ctypes.c_short),
    ]


class XINPUT_STATE(ctypes.Structure):
    _fields_ = [("dwPacketNumber", wt.DWORD), ("Gamepad", XINPUT_GAMEPAD)]


# SetupDi imports for XUSB interface enumeration.
setupapi = ctypes.WinDLL("setupapi.dll")


class GUID(ctypes.Structure):
    _fields_ = [("Data1", wt.DWORD), ("Data2", wt.WORD), ("Data3", wt.WORD),
                ("Data4", ctypes.c_ubyte * 8)]


setupapi.SetupDiGetClassDevsW.argtypes = [
    ctypes.POINTER(GUID), wt.LPCWSTR, wt.HWND, wt.DWORD]
setupapi.SetupDiGetClassDevsW.restype = ctypes.c_void_p
setupapi.SetupDiEnumDeviceInterfaces.argtypes = [
    ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(GUID), wt.DWORD, ctypes.c_void_p]
setupapi.SetupDiEnumDeviceInterfaces.restype = wt.BOOL
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [ctypes.c_void_p]
setupapi.SetupDiDestroyDeviceInfoList.restype = wt.BOOL


def count_xusb_interfaces() -> int:
    """Enumerate GUID_DEVINTERFACE_XUSB via SetupDi. Returns the total count
    of distinct XUSB device interfaces on the system — every xinputhid-bound
    HID child + every root-enumerated XUSB publisher."""
    xusb = GUID(0xEC87F1E3, 0xC13B, 0x4100,
                (ctypes.c_ubyte * 8)(0xB5, 0xF7, 0x8B, 0x84, 0xD5, 0x42, 0x60, 0xCB))
    # 0x12 = DIGCF_DEVICEINTERFACE | DIGCF_PRESENT
    dev_info = setupapi.SetupDiGetClassDevsW(ctypes.byref(xusb), None, None, 0x12)
    INVALID = (1 << 64) - 1  # INVALID_HANDLE_VALUE on x64
    if dev_info is None or dev_info == INVALID:
        return 0
    try:
        count = 0
        size = 32 if ctypes.sizeof(ctypes.c_void_p) == 8 else 28
        while True:
            data = (ctypes.c_ubyte * 32)()
            ctypes.memmove(data, ctypes.byref(ctypes.c_ulong(size)), 4)
            if not setupapi.SetupDiEnumDeviceInterfaces(
                    dev_info, None, ctypes.byref(xusb), count, data):
                break
            count += 1
        return count
    finally:
        setupapi.SetupDiDestroyDeviceInfoList(dev_info)


def main() -> int:
    n_interfaces = count_xusb_interfaces()
    print(f"XUSB device interfaces (XInput Test's view): {n_interfaces}")

    dll = ctypes.WinDLL("XInput1_4.dll")
    GetStateEx = dll[100]
    GetStateEx.argtypes = [wt.DWORD, ctypes.POINTER(XINPUT_STATE)]
    GetStateEx.restype = wt.DWORD

    claimed = 0
    for slot in range(4):
        st = XINPUT_STATE()
        rc = GetStateEx(slot, ctypes.byref(st))
        if rc == 0:
            claimed += 1
            print(f"slot {slot}: CONNECTED pkt={st.dwPacketNumber:8d} "
                  f"LX={st.Gamepad.sThumbLX:+7d} LY={st.Gamepad.sThumbLY:+7d} "
                  f"RX={st.Gamepad.sThumbRX:+7d} RY={st.Gamepad.sThumbRY:+7d} "
                  f"btns=0x{st.Gamepad.wButtons:04X} "
                  f"LT={st.Gamepad.bLeftTrigger:3d} RT={st.Gamepad.bRightTrigger:3d}")
        else:
            print(f"slot {slot}: disconnected (rc={rc})")

    print(f"\nSummary: {n_interfaces} XInput device(s) present; {claimed}/4 xinput1_4 slots claimed")
    if n_interfaces > claimed:
        gap = n_interfaces - claimed
        print(f"         ({gap} XInput device(s) published but not bound to a xinput1_4 slot — "
              f"xinputhid's per-boot slot allocator leaves gaps when multiple virtuals coexist)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
