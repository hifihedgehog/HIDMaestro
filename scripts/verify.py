#!/usr/bin/env python3
"""
HIDMaestro API Verification Script

Tests all controller APIs to verify the emulated device works correctly:
  - XInput (via xinput1_4.dll)
  - DirectInput (via WinMM joystick API)
  - HIDAPI/SDL3 (via hidapi Python package)
  - Browser/WGI (via WinExInput interface check)

Usage: python scripts/verify.py [--wait N] [--repeat N]
  --wait N    Wait N seconds before testing (default: 0)
  --repeat N  Read inputs N times per API (default: 4)

Exit code 0 = all pass, 1 = failures detected.
"""

import ctypes
import ctypes.wintypes
from ctypes import wintypes
import subprocess
import sys
import time


# ---------------------------------------------------------------------------
#  XInput
# ---------------------------------------------------------------------------

class XINPUT_STATE(ctypes.Structure):
    _fields_ = [
        ("dwPacketNumber", ctypes.c_ulong),
        ("wButtons", ctypes.c_ushort),
        ("bLeftTrigger", ctypes.c_ubyte),
        ("bRightTrigger", ctypes.c_ubyte),
        ("sThumbLX", ctypes.c_short),
        ("sThumbLY", ctypes.c_short),
        ("sThumbRX", ctypes.c_short),
        ("sThumbRY", ctypes.c_short),
    ]


def _count_xusb_interfaces() -> int:
    """Enumerate GUID_DEVINTERFACE_XUSB {EC87F1E3-...} — total XInput-visible
    devices at the PnP layer. This is what XInput Test and similar tools
    actually count, and it's the correct metric for "how many XInput
    controllers are attached." It can exceed xinput1_4.dll's 4-slot cap
    and doesn't suffer from slot-allocator gaps the way XInputGetState does.
    """
    try:
        setupapi = ctypes.WinDLL("setupapi.dll")
    except OSError:
        return 0

    class _GUID128(ctypes.Structure):
        _fields_ = [("Data1", ctypes.c_uint), ("Data2", ctypes.c_ushort),
                    ("Data3", ctypes.c_ushort),
                    ("Data4", ctypes.c_ubyte * 8)]

    setupapi.SetupDiGetClassDevsW.argtypes = [
        ctypes.POINTER(_GUID128), ctypes.c_wchar_p, ctypes.c_void_p, ctypes.c_uint]
    setupapi.SetupDiGetClassDevsW.restype = ctypes.c_void_p
    setupapi.SetupDiEnumDeviceInterfaces.argtypes = [
        ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(_GUID128),
        ctypes.c_uint, ctypes.c_void_p]
    setupapi.SetupDiEnumDeviceInterfaces.restype = ctypes.c_int
    setupapi.SetupDiDestroyDeviceInfoList.argtypes = [ctypes.c_void_p]
    setupapi.SetupDiDestroyDeviceInfoList.restype = ctypes.c_int

    xusb = _GUID128(0xEC87F1E3, 0xC13B, 0x4100,
                    (ctypes.c_ubyte * 8)(0xB5, 0xF7, 0x8B, 0x84, 0xD5, 0x42, 0x60, 0xCB))
    # DIGCF_DEVICEINTERFACE (0x10) | DIGCF_PRESENT (0x2)
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


def check_xinput(repeats: int) -> dict:
    """Counts XInput-visible controllers two ways and returns BOTH in the
    result. The authoritative count is `interfaces` (XUSB device interfaces
    at the PnP layer — matches XInput Test's view and exceeds 4 when many
    virtuals are present). `slots` is the legacy xinput1_4 slot view, which
    has 4-slot cap and slot-allocator gaps with multi-virtual setups.

    Per-slot moving/static detection still uses XInputGetStateEx (ordinal 100
    of XInput1_4.dll) so the Guide bit + legacy-filter bypass apply.
    """
    # (1) XUSB device interface count — the "XInput Test" view.
    n_interfaces = _count_xusb_interfaces()

    # (2) xinput1_4 slot probe.
    try:
        xinput = ctypes.WinDLL("XInput1_4.dll")
        get_state = xinput[100]
    except (OSError, AttributeError):
        try:
            xinput = ctypes.WinDLL("XInput1_4.dll")
        except OSError:
            xinput = ctypes.WinDLL("XInput9_1_0.dll")
        get_state = xinput.XInputGetState
    get_state.argtypes = [ctypes.c_uint, ctypes.POINTER(XINPUT_STATE)]
    get_state.restype = ctypes.c_uint

    slots = []
    for slot in range(4):
        state = XINPUT_STATE()
        if get_state(slot, ctypes.byref(state)) != 0:
            continue
        first_pkt = state.dwPacketNumber
        moving = False
        for _ in range(repeats):
            get_state(slot, ctypes.byref(state))
            if state.sThumbLX != 0 or state.sThumbLY != 0:
                moving = True
            time.sleep(0.25)
        slots.append({
            "slot": slot,
            "pkt_first": first_pkt,
            "pkt_last": state.dwPacketNumber,
            "moving": moving,
            "lx": state.sThumbLX, "ly": state.sThumbLY,
            "lt": state.bLeftTrigger, "rt": state.bRightTrigger,
        })
    return {"slots": slots, "count": len(slots), "interfaces": n_interfaces}


# ---------------------------------------------------------------------------
#  DirectInput8 (with Acquire — joy.cpl/Dolphin do the same)
# ---------------------------------------------------------------------------
#
# xinputhid eats HID reads, so passive paths (WinMM joyGetPosEx) see only
# 32767 forever. The only working path is DI8 with a real top-level window
# and Acquire — exactly what joy.cpl does.

class _GUID(ctypes.Structure):
    _fields_ = [("d1", ctypes.c_ulong), ("d2", ctypes.c_ushort),
                ("d3", ctypes.c_ushort), ("d4", ctypes.c_ubyte * 8)]

class _DIDEVICEINSTANCEW(ctypes.Structure):
    _fields_ = [("dwSize", ctypes.c_ulong), ("guidInstance", _GUID),
                ("guidProduct", _GUID), ("dwDevType", ctypes.c_ulong),
                ("tszInstanceName", ctypes.c_wchar * 260),
                ("tszProductName", ctypes.c_wchar * 260),
                ("guidFFDriver", _GUID),
                ("wUsagePage", ctypes.c_ushort), ("wUsage", ctypes.c_ushort)]

class _DIDEVCAPS(ctypes.Structure):
    _fields_ = [("dwSize", ctypes.c_ulong), ("dwFlags", ctypes.c_ulong),
                ("dwDevType", ctypes.c_ulong), ("dwAxes", ctypes.c_ulong),
                ("dwButtons", ctypes.c_ulong), ("dwPOVs", ctypes.c_ulong),
                ("dwFFSamplePeriod", ctypes.c_ulong),
                ("dwFFMinTimeResolution", ctypes.c_ulong),
                ("dwFirmwareRevision", ctypes.c_ulong),
                ("dwHardwareRevision", ctypes.c_ulong),
                ("dwFFDriverVersion", ctypes.c_ulong)]

class _DIPROPGUIDANDPATH(ctypes.Structure):
    _fields_ = [("dwSize", ctypes.c_ulong), ("dwHeaderSize", ctypes.c_ulong),
                ("dwObj", ctypes.c_ulong), ("dwHow", ctypes.c_ulong),
                ("guidClass", _GUID), ("wszPath", ctypes.c_wchar * 260)]

class _DIJOYSTATE(ctypes.Structure):
    _fields_ = [("lX", ctypes.c_long), ("lY", ctypes.c_long),
                ("lZ", ctypes.c_long), ("lRx", ctypes.c_long),
                ("lRy", ctypes.c_long), ("lRz", ctypes.c_long),
                ("rglSlider", ctypes.c_long * 2),
                ("rgdwPOV", ctypes.c_ulong * 4),
                ("rgbButtons", ctypes.c_ubyte * 32)]

class _DIOBJECTDATAFORMAT(ctypes.Structure):
    _fields_ = [("pguid", ctypes.c_void_p), ("dwOfs", ctypes.c_ulong),
                ("dwType", ctypes.c_ulong), ("dwFlags", ctypes.c_ulong)]

class _DIDATAFORMAT(ctypes.Structure):
    _fields_ = [("dwSize", ctypes.c_ulong), ("dwObjSize", ctypes.c_ulong),
                ("dwFlags", ctypes.c_ulong), ("dwDataSize", ctypes.c_ulong),
                ("dwNumObjs", ctypes.c_ulong),
                ("rgodf", ctypes.POINTER(_DIOBJECTDATAFORMAT))]

class _DIDEVICEOBJECTINSTANCEW(ctypes.Structure):
    _fields_ = [("dwSize", ctypes.c_ulong),
                ("guidType", _GUID),
                ("dwOfs", ctypes.c_ulong),
                ("dwType", ctypes.c_ulong),
                ("dwFlags", ctypes.c_ulong),
                ("tszName", ctypes.c_wchar * 260),
                ("dwFFMaxForce", ctypes.c_ulong),
                ("dwFFForceResolution", ctypes.c_ulong),
                ("wCollectionNumber", ctypes.c_ushort),
                ("wDesignatorIndex", ctypes.c_ushort),
                ("wUsagePage", ctypes.c_ushort),
                ("wUsage", ctypes.c_ushort),
                ("dwDimension", ctypes.c_ulong),
                ("wExponent", ctypes.c_ushort),
                ("wReportId", ctypes.c_ushort)]


# WNDPROC: LRESULT is LONG_PTR. c_ssize_t (NOT c_long) on x64.
_WNDPROC = ctypes.WINFUNCTYPE(ctypes.c_ssize_t, wintypes.HWND,
                              ctypes.c_uint, wintypes.WPARAM, wintypes.LPARAM)

class _WNDCLASSW(ctypes.Structure):
    _fields_ = [("style", ctypes.c_uint), ("lpfnWndProc", _WNDPROC),
                ("cbClsExtra", ctypes.c_int), ("cbWndExtra", ctypes.c_int),
                ("hInstance", wintypes.HINSTANCE), ("hIcon", wintypes.HICON),
                ("hCursor", wintypes.HANDLE), ("hbrBackground", wintypes.HBRUSH),
                ("lpszMenuName", wintypes.LPCWSTR),
                ("lpszClassName", wintypes.LPCWSTR)]


def _vcall(iface, slot_index, sig):
    """Resolve a COM vtable slot to a callable. iface is a ctypes pointer
    whose first field is the vtable pointer; slot_index is the method index;
    sig is the WINFUNCTYPE signature. Returns a bound callable."""
    vtbl_ptr = ctypes.cast(iface, ctypes.POINTER(ctypes.c_void_p))[0]
    fn_ptr = ctypes.cast(vtbl_ptr, ctypes.POINTER(ctypes.c_void_p))[slot_index]
    return sig(fn_ptr)


def check_directinput(repeats: int) -> dict:
    try:
        dinput8 = ctypes.windll.dinput8
    except OSError as e:
        return {"available": False, "error": f"dinput8.dll: {e}"}

    user32 = ctypes.windll.user32
    kernel32 = ctypes.windll.kernel32

    # x64 handles are 64-bit; default restype c_int truncates them. Pin types.
    kernel32.GetModuleHandleW.restype = wintypes.HINSTANCE
    kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
    user32.CreateWindowExW.restype = wintypes.HWND
    user32.CreateWindowExW.argtypes = [
        ctypes.c_ulong, wintypes.LPCWSTR, wintypes.LPCWSTR, ctypes.c_ulong,
        ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
        wintypes.HWND, wintypes.HMENU, wintypes.HINSTANCE, wintypes.LPVOID]
    user32.DestroyWindow.argtypes = [wintypes.HWND]
    user32.SetForegroundWindow.argtypes = [wintypes.HWND]
    user32.RegisterClassW.restype = ctypes.c_ushort
    user32.RegisterClassW.argtypes = [ctypes.POINTER(_WNDCLASSW)]
    user32.PeekMessageW.argtypes = [
        ctypes.c_void_p, wintypes.HWND, ctypes.c_uint, ctypes.c_uint, ctypes.c_uint]
    user32.DispatchMessageW.argtypes = [ctypes.c_void_p]

    # --- Build c_dfDIJoystick equivalent: 6 axes + 2 sliders + 4 POVs + 32 btns
    AXIS = 3 | 0x00FFFF00 | 0x80000000   # DIDFT_AXIS|ANYINSTANCE|OPTIONAL
    BTN  = 0x0C | 0x00FFFF00 | 0x80000000
    POV  = 0x10 | 0x00FFFF00 | 0x80000000
    objs = []
    for ofs in (0,4,8,12,16,20,24,28): objs.append((ofs, AXIS))
    for ofs in (32,36,40,44):           objs.append((ofs, POV))
    for i in range(32):                 objs.append((48+i, BTN))
    arr = (_DIOBJECTDATAFORMAT * len(objs))(
        *(_DIOBJECTDATAFORMAT(None, o, t, 0) for (o, t) in objs))
    fmt = _DIDATAFORMAT(ctypes.sizeof(_DIDATAFORMAT),
                       ctypes.sizeof(_DIOBJECTDATAFORMAT), 1,
                       ctypes.sizeof(_DIJOYSTATE), len(objs), arr)

    # --- Top-level window (NULL/desktop hwnds fail SetCooperativeLevel)
    user32.DefWindowProcW.restype = ctypes.c_ssize_t
    user32.DefWindowProcW.argtypes = [wintypes.HWND, ctypes.c_uint,
                                       wintypes.WPARAM, wintypes.LPARAM]
    wndproc = _WNDPROC(lambda h,m,w,l: user32.DefWindowProcW(h,m,w,l))
    wc = _WNDCLASSW()
    wc.lpfnWndProc = wndproc
    wc.hInstance = kernel32.GetModuleHandleW(None)
    wc.lpszClassName = "HMVerifyDIWindow"
    user32.RegisterClassW(ctypes.byref(wc))  # ignore "already exists"
    hwnd = user32.CreateWindowExW(
        0, "HMVerifyDIWindow", "HMVerifyDI",
        0x80000000 | 0x10000000,  # WS_POPUP | WS_VISIBLE
        -32000, -32000, 1, 1, None, None, wc.hInstance, None)
    if not hwnd:
        return {"available": False, "error": "CreateWindowExW failed"}
    user32.SetForegroundWindow(hwnd)

    devices = []
    di8 = ctypes.c_void_p()
    try:
        IID_IDI8W = _GUID(0xBF798031, 0x483A, 0x4DA2,
                          (ctypes.c_ubyte*8)(0xAA,0x99,0x5D,0x64,0xED,0x36,0x97,0x00))
        dinput8.DirectInput8Create.restype = ctypes.c_long
        dinput8.DirectInput8Create.argtypes = [
            ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(_GUID),
            ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p]
        hr = dinput8.DirectInput8Create(
                wc.hInstance, 0x0800, ctypes.byref(IID_IDI8W),
                ctypes.byref(di8), None)
        if hr != 0:
            return {"available": False,
                    "error": f"DirectInput8Create hr=0x{hr&0xFFFFFFFF:08X}"}

        # IDirectInput8 vtbl: 3=CreateDevice, 4=EnumDevices, 2=Release
        EnumSig = ctypes.WINFUNCTYPE(
            ctypes.c_long, ctypes.c_void_p, ctypes.c_ulong,
            ctypes.c_void_p, ctypes.c_void_p, ctypes.c_ulong)
        EnumCb = ctypes.WINFUNCTYPE(
            wintypes.BOOL, ctypes.POINTER(_DIDEVICEINSTANCEW), ctypes.c_void_p)
        CreateDevSig = ctypes.WINFUNCTYPE(
            ctypes.c_long, ctypes.c_void_p, ctypes.POINTER(_GUID),
            ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p)

        found = []
        def cb(p, _r):
            i = p.contents
            g = _GUID(i.guidInstance.d1, i.guidInstance.d2, i.guidInstance.d3,
                      (ctypes.c_ubyte*8)(*i.guidInstance.d4))
            # guidProduct.Data1 packs (PID << 16) | VID for HID-class devices.
            # Used as VID/PID fallback when DIPROP_GUIDANDPATH path doesn't
            # contain vid_xxxx&pid_xxxx (e.g. ROOT\HIDClass\* devices).
            found.append((g, i.tszProductName, i.guidProduct.d1))
            return True
        _vcall(di8, 4, EnumSig)(di8, 4, EnumCb(cb), None, 1)  # GAMECTRL, ATTACHEDONLY

        # IDirectInputDevice8 vtbl indexes: 3=GetCaps, 4=EnumObjects,
        # 5=GetProp, 7=Acq, 8=Unacq, 9=GetState, 11=SetDataFormat,
        # 13=SetCoopLevel, 25=Poll, 2=Release
        GetCapsSig  = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p,
                                          ctypes.POINTER(_DIDEVCAPS))
        EnumObjsSig = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p,
                                          ctypes.c_void_p, ctypes.c_void_p,
                                          ctypes.c_ulong)
        EnumObjsCb  = ctypes.WINFUNCTYPE(
            wintypes.BOOL, ctypes.POINTER(_DIDEVICEOBJECTINSTANCEW), ctypes.c_void_p)
        GetPropSig  = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p,
                                          ctypes.c_void_p, ctypes.c_void_p)
        SDFSig      = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p,
                                          ctypes.POINTER(_DIDATAFORMAT))
        SCLSig      = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p,
                                          wintypes.HWND, ctypes.c_ulong)
        VoidSig     = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p)
        GetStateSig = ctypes.WINFUNCTYPE(ctypes.c_long, ctypes.c_void_p,
                                          ctypes.c_ulong, ctypes.c_void_p)

        for idx, (g, name, prod_d1) in enumerate(found):
            dev = ctypes.c_void_p()
            if _vcall(di8, 3, CreateDevSig)(
                    di8, ctypes.byref(g), ctypes.byref(dev), None) != 0:
                continue
            try:
                caps = _DIDEVCAPS(); caps.dwSize = ctypes.sizeof(caps)
                if _vcall(dev, 3, GetCapsSig)(dev, ctypes.byref(caps)) != 0:
                    continue

                # caps.dwAxes counts every Generic Desktop axis usage in the
                # HID descriptor. For our Xbox 360 wired profile, the
                # descriptor uses Vx/Vy (usages 0x40/0x41) to carry trigger
                # data for the browser side, but those are NOT DirectInput
                # axes — joy.cpl ignores them. Count only standard joystick
                # usages (0x30 X .. 0x35 Rz). This matches joy.cpl.
                axis_count = [0]
                def axis_cb(p, _r):
                    o = p.contents
                    if o.wUsagePage == 0x01 and 0x30 <= o.wUsage <= 0x35:
                        axis_count[0] += 1
                    return True
                # DIDFT_AXIS = DIDFT_RELAXIS | DIDFT_ABSAXIS = 3
                _vcall(dev, 4, EnumObjsSig)(
                    dev, EnumObjsCb(axis_cb), None, 3)
                di_axes = axis_count[0]

                # VID/PID via DIPROP_GUIDANDPATH = MAKEDIPROP(12) — works for
                # devices whose path contains "vid_xxxx&pid_xxxx" (HID#VID_*).
                vid = pid = 0
                pgp = _DIPROPGUIDANDPATH()
                pgp.dwSize = ctypes.sizeof(_DIPROPGUIDANDPATH)
                pgp.dwHeaderSize = 16  # sizeof DIPROPHEADER
                pgp.dwHow = 0  # DIPH_DEVICE
                if _vcall(dev, 5, GetPropSig)(
                        dev, ctypes.cast(12, ctypes.c_void_p),
                        ctypes.byref(pgp)) == 0:
                    import re
                    m = re.search(r"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})",
                                  pgp.wszPath.lower())
                    if m: vid, pid = int(m.group(1),16), int(m.group(2),16)
                # Fallback: extract from guidProduct.Data1 which DInput packs as
                # (PID << 16) | VID for any HID gamepad. Required for devices
                # under ROOT\HIDClass enumerator (e.g. DualSense) whose paths
                # don't contain vid_/pid_ tokens.
                if vid == 0 and pid == 0 and prod_d1:
                    vid = prod_d1 & 0xFFFF
                    pid = (prod_d1 >> 16) & 0xFFFF

                hr_fmt  = _vcall(dev, 11, SDFSig)(dev, ctypes.byref(fmt))
                hr_coop = _vcall(dev, 13, SCLSig)(dev, hwnd, 0x08 | 0x02)  # BG|NX
                hr_acq  = _vcall(dev, 7, VoidSig)(dev)

                moving = False
                if hr_fmt == 0 and hr_coop == 0 and hr_acq == 0:
                    state = _DIJOYSTATE()
                    prev = None
                    for _ in range(repeats):
                        # Pump messages so DI8 can deliver state
                        msg = (ctypes.c_byte * 48)()
                        while user32.PeekMessageW(msg, hwnd, 0, 0, 1):
                            user32.DispatchMessageW(msg)
                        _vcall(dev, 25, VoidSig)(dev)  # Poll
                        if _vcall(dev, 9, GetStateSig)(
                                dev, ctypes.sizeof(state), ctypes.byref(state)) == 0:
                            cur = (state.lX, state.lY, state.lZ,
                                   state.lRx, state.lRy, state.lRz)
                            if prev is not None and cur != prev: moving = True
                            prev = cur
                        time.sleep(0.15)
                    _vcall(dev, 8, VoidSig)(dev)  # Unacquire

                devices.append({
                    "id": idx, "vid": vid, "pid": pid, "name": name,
                    "axes": di_axes, "buttons": caps.dwButtons,
                    "pov": caps.dwPOVs > 0, "moving": moving,
                    "errors": [f"fmt={hr_fmt&0xFFFFFFFF:08X}",
                               f"coop={hr_coop&0xFFFFFFFF:08X}",
                               f"acq={hr_acq&0xFFFFFFFF:08X}"]
                              if (hr_fmt or hr_coop or hr_acq) else [],
                })
            finally:
                _vcall(dev, 2, VoidSig)(dev)
    finally:
        if di8: _vcall(di8, 2, VoidSig)(di8)
        user32.DestroyWindow(hwnd)
        del wndproc

    return {"available": True, "count": len(devices), "devices": devices}


# ---------------------------------------------------------------------------
#  HIDAPI / SDL3
# ---------------------------------------------------------------------------

def check_hidapi() -> dict:
    try:
        import hid
    except ImportError:
        return {"available": False, "error": "hidapi not installed (pip install hidapi)"}

    # Enumerate ALL HID devices and filter to HIDMaestro-only via the
    # HM-CTL-* serial prefix (same approach as check_hid_order). The old
    # 0x045E-only enumeration missed every non-Xbox profile (DualSense, etc.),
    # which caused false negatives when a multi-profile run included non-Xbox
    # controllers — they would never appear in `non_ig` and `live_data` would
    # be checked against an empty set.
    all_devs = [d for d in hid.enumerate(0, 0)
                if (d.get("serial_number") or "").startswith("HM-CTL-")]
    visible = [d for d in all_devs if "&IG_" not in d["path"].decode().upper()]

    result = {
        "available": True,
        "total": len(all_devs),
        "visible_to_sdl3": len(visible),
        "live_data": False,
        "devices": [],
    }

    bus_names = {0: "UNKNOWN", 1: "USB", 2: "BT", 3: "SPI"}
    for d in all_devs:
        path = d["path"].decode()
        ig = "&IG_" in path.upper()
        result["devices"].append({
            "vid": d["vendor_id"],
            "pid": d["product_id"],
            "bus": bus_names.get(d["bus_type"], str(d["bus_type"])),
            "ig_filtered": ig,
            "usage": f"{d['usage_page']:04X}:{d['usage']:04X}",
            "product": d["product_string"],
        })

    # Live-data check: try to read from EVERY non-IG device regardless of
    # bus_type. Older code only checked bus_type==2 (BT), which missed USB
    # DualSense devices that report bus_type=0 (UNKNOWN) for ROOT-enumerated
    # virtual devices. Movement detection works on raw HID bytes regardless
    # of how SDL3 would later classify the bus type.
    for d in visible:
        try:
            dev = hid.device()
            dev.open_path(d["path"])
            dev.set_nonblocking(1)
            # Drain a few reads — the test pattern is ~80Hz so 200ms is enough
            # to see something for an actively-driven controller.
            saw_data = False
            import time as _t
            deadline = _t.time() + 0.4
            while _t.time() < deadline:
                data = dev.read(64)
                if data and any(b != 0 for b in data[1:]):
                    saw_data = True
                    break
                _t.sleep(0.05)
            if saw_data:
                result["live_data"] = True
            dev.close()
        except Exception:
            pass

    return result


# ---------------------------------------------------------------------------
#  HID enumeration order
# ---------------------------------------------------------------------------
#
# Dead-simple ordering check: walk every HID device whose serial starts with
# "HM-CTL-" (our virtual-controller serial prefix) and confirm they appear in
# the same order they were created. The hid.enumerate() iteration order is
# the same one Windows.Devices.HumanInterfaceDevice.HidDevice.FindAllAsync
# uses, which is the same enumerator WGI consumes internally — so this is
# the canonical "what every well-behaved consumer sees" order.
#
# A consumer that displays them in a different order (e.g. Dolphin's
# remove+re-insert-on-event behavior) is the consumer's bug, not ours.

def check_hid_order(expected_count: int) -> dict:
    try:
        import hid
    except ImportError:
        return {"available": False, "error": "hidapi not installed"}

    # Walk every HID device, regardless of VID/PID, and pick the ones our
    # driver claims via the HM-CTL- serial prefix.
    found = []
    for d in hid.enumerate(0, 0):
        sn = d.get("serial_number") or ""
        if sn.startswith("HM-CTL-"):
            try:
                idx = int(sn[len("HM-CTL-"):])
            except ValueError:
                idx = -1
            found.append({
                "serial": sn,
                "expected_index": idx,
                "vid": d["vendor_id"],
                "pid": d["product_id"],
                "path": d["path"].decode(errors="replace"),
            })

    # Note: hid.enumerate may return the same device multiple times if it
    # has multiple top-level collections. Dedupe by serial keeping the
    # first occurrence (which preserves enumeration order for the FIRST
    # interface of each device).
    seen = set()
    deduped = []
    for f in found:
        if f["serial"] in seen:
            continue
        seen.add(f["serial"])
        deduped.append(f)

    indices = [f["expected_index"] for f in deduped]
    in_order = (indices == sorted(indices)) and (indices == list(range(len(indices))))

    return {
        "available": True,
        "count": len(deduped),
        "expected_count": expected_count,
        "in_order": in_order,
        "indices": indices,
        "devices": deduped,
    }


# ---------------------------------------------------------------------------
#  GameInput / Windows.Gaming.Input — RawGameController enumeration
# ---------------------------------------------------------------------------
#
# Walks Windows.Gaming.Input.RawGameController.RawGameControllers — the same
# WGI source Dolphin's WGInput backend uses ("WGInput/N/<DisplayName>"). This
# sees ANY HID-class gamepad-like device (gamepad OR joystick usage), unlike
# Gamepad.Gamepads which only sees devices WGI has promoted to the Gamepad
# class via XInput / internal mapping rules.
#
# RawGameController is what UWP / Game Bar / Xbox app / Dolphin / browser-WGI
# all consume, so this is the canonical "is the WGI path working" check.
#
# Returns: {available, count, controllers, live_data}

def check_gameinput(repeats: int, expected: int = 1) -> dict:
    try:
        from winrt.windows.gaming.input import RawGameController  # type: ignore
    except ImportError:
        return {"available": False,
                "error": "winrt-Windows-Gaming-Input not installed "
                         "(pip install winrt-Windows-Gaming-Input winrt-Windows-Foundation)"}
    except Exception as e:
        return {"available": False, "error": f"{type(e).__name__}: {e}"}

    # RawGameController.raw_game_controllers is populated by an internal
    # watcher that runs after the namespace is first touched, so the very
    # first read in this process can return an empty list even when the
    # device is fully present. Poll briefly until the count reaches the
    # expected value (or stabilizes), capped well under the 3s soft limit.
    deadline = time.time() + 2.5
    try:
        ctrls = list(RawGameController.raw_game_controllers)
        while len(ctrls) < expected and time.time() < deadline:
            time.sleep(0.1)
            ctrls = list(RawGameController.raw_game_controllers)
    except Exception as e:
        return {"available": False, "error": f"RawGameController enumeration failed: {e}"}

    # Presence-only check. RawGameController.get_current_reading via
    # winrt-python returns ts=0/all-zero values regardless of buffer format,
    # event subscription, or message pump — even when XInput, DI8, browser,
    # and Dolphin all see live data on the same device. The wrapper appears
    # to not properly marshal the out-buffer reads. Movement is verified
    # canonically by check_xinput / check_directinput / check_browser, so
    # GameInput here only confirms WGI sees the device.
    result = {"available": True, "count": len(ctrls), "controllers": []}
    for c in ctrls:
        try:
            result["controllers"].append({
                "name": c.display_name,
                "axes": c.axis_count,
                "buttons": c.button_count,
                "switches": c.switch_count,
            })
        except Exception as e:
            result["controllers"].append({"error": str(e)})
    return result


# ---------------------------------------------------------------------------
#  Browser (real Chromium navigator.getGamepads via headless launcher)
# ---------------------------------------------------------------------------

def check_browser() -> dict:
    """Launch a real browser at scripts/browser_check/index.html and read what
    navigator.getGamepads() returns. This is the only way to know whether the
    browser path is actually working — different controllers use different
    Chromium gamepad source backends (XInput / WGI / RawInput / HID), and the
    backend choice can't be inferred from any single OS API.

    Cold-start cost: a fresh Edge profile takes ~6-8s to initialize before
    JavaScript starts running, so the timeout has headroom."""
    try:
        # Inline import so verify.py still runs even if browser_check is missing
        sys.path.insert(0, str(__import__("pathlib").Path(__file__).parent / "browser_check"))
        from launcher import run_browser_check  # type: ignore
        return run_browser_check(timeout_s=20.0)
    except Exception as e:
        return {"available": False, "error": f"{type(e).__name__}: {e}"}


# ---------------------------------------------------------------------------
#  Main
# ---------------------------------------------------------------------------

# Per-VID expectations. Used to auto-skip XInput for non-Xbox profiles and
# auto-pick the right axis count when multi-controller mixes Xbox + Sony.
VID_AXES = {
    0x045E: 5,   # Microsoft Xbox: 5 axes (X, Y, Z=combined trigger, Rx, Ry)
    0x054C: 6,   # Sony PlayStation: 6 axes (X, Y, Z, Rx, Ry, Rz)
}
DEFAULT_AXES = 5

# VIDs that route through XInput. Anything else is excluded from the XInput
# check entirely (Sony controllers correctly do NOT occupy XInput slots).
XINPUT_VIDS = {0x045E}

SERIAL_PREFIX = "HM-CTL-"
TARGET_AXES = 5  # legacy default for --axes when not auto-detecting


def main():
    wait = 0
    repeats = 4
    controllers = 1
    explicit_axes = None
    force_no_xinput = False
    force_no_browser = False

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--wait" and i + 1 < len(args):
            wait = int(args[i + 1]); i += 2
        elif args[i] == "--repeat" and i + 1 < len(args):
            repeats = int(args[i + 1]); i += 2
        elif args[i] in ("--controllers", "-n") and i + 1 < len(args):
            controllers = int(args[i + 1]); i += 2
        elif args[i] == "--axes" and i + 1 < len(args):
            explicit_axes = int(args[i + 1]); i += 2
        elif args[i] == "--no-xinput":
            force_no_xinput = True; i += 1
        elif args[i] == "--no-browser":
            force_no_browser = True; i += 1
        else:
            i += 1

    if wait > 0:
        print(f"Waiting {wait}s...")
        time.sleep(wait)

    print("=" * 56)
    print("  HIDMaestro API Verification")
    print("=" * 56)
    print()

    failures = []

    # DirectInput8 must run FIRST in the process. xinputhid freezes the
    # joystick state buffer for any in-process DI8 client once ANY other
    # HID/XInput consumer in the same process has touched the device — even
    # hidapi's enumerate-and-open-strings cycle is enough to freeze it.
    # GetDeviceState then returns 32767 forever. So check_directinput runs
    # before anything else that touches HID.
    di = check_directinput(repeats)

    # Now enumerate HIDMaestro devices via HM-CTL-* serial. The result drives
    # auto-skip decisions for XInput (no Xbox device → skip) and per-device
    # axis expectations for DirectInput.
    ho = check_hid_order(controllers) if 'check_hid_order' in globals() else {"available": False}
    hm_devices = ho.get("devices", []) if ho.get("available") else []
    xbox_count = sum(1 for d in hm_devices if d["vid"] in XINPUT_VIDS)
    expect_xinput = xbox_count > 0 and not force_no_xinput

    # -- XInput --
    #
    # SKIP only when the XInput library itself can't load. Otherwise: Xbox
    # devices must show live data (PASS), non-Xbox-only configs must show no
    # XInput slot occupied (PASS by virtue of being not-applicable), and any
    # mismatch is a FAIL.
    print("  XInput:      ", end="")
    if force_no_xinput:
        print("SKIP  (--no-xinput)")
    elif not hm_devices:
        # No HIDMaestro devices visible at all — there's nothing to test against.
        # Caller almost certainly forgot to start the test app.
        print("FAIL  no HIDMaestro devices found (test app not running?)")
        failures.append("No HIDMaestro devices visible")
    else:
        xi = check_xinput(repeats)
        n_ifaces = xi.get("interfaces", 0)
        if expect_xinput:
            # Xbox profile present — at least one slot must be live AND the
            # XUSB device interface count should match the number of Xbox
            # profiles (2 xinputhid-bound HID children per Xbox Series BT +
            # 2 HIDMAESTRO XUSB companions per Xbox 360 wired, etc.).
            if xi["count"] >= 1 and any(s["moving"] for s in xi["slots"]):
                slot_summary = ", ".join(
                    f"slot{s['slot']} pkt={s['pkt_last']} LX={s['lx']:+d} LY={s['ly']:+d}"
                    for s in xi["slots"]
                )
                gap_note = ""
                if n_ifaces > xi["count"]:
                    gap_note = (f"  [{n_ifaces} XUSB interface(s) present; "
                                f"{n_ifaces - xi['count']} not bound to a xinput1_4 slot — "
                                f"xinputhid per-boot allocator gap, harmless]")
                print(f"PASS  {xi['count']}/{n_ifaces} slot(s) live ({slot_summary}){gap_note}")
            elif xi["count"] >= 1:
                print(f"FAIL  {xi['count']} slot(s) connected but static")
                failures.append(f"XInput: {xi['count']} slot(s) connected but no movement")
            else:
                print(f"FAIL  no slot connected (expected {xbox_count} Xbox slot(s))")
                failures.append("XInput: no slot connected")
        else:
            # No Xbox profile — XInput should NOT be occupied by HIDMaestro.
            # If a slot is occupied, that's the user's real Xbox controller; we
            # have no way to attribute. Treat as PASS (not applicable).
            if xi["count"] == 0:
                print(f"PASS  not applicable ({len(hm_devices)} non-Xbox HIDMaestro device(s))")
            else:
                print(f"PASS  not applicable (other Xbox device on slot {xi['slots'][0]['slot']})")

    # -- DirectInput (DI8 with Acquire — see note above; called before XInput) --
    print(f"  DirectInput: ", end="")
    if not di.get("available"):
        print(f"SKIP  ({di.get('error', 'unavailable')})")
    else:
        # Filter to HIDMaestro devices: match by (VID, PID) against the HM-CTL-* set
        hm_vidpids = {(d["vid"], d["pid"]) for d in hm_devices}
        if hm_vidpids:
            hm_di = [d for d in di["devices"] if (d["vid"], d["pid"]) in hm_vidpids]
        else:
            # No HM-CTL-* devices found (legacy driver, or before serial fix). Fall
            # back to total count of all DI joysticks.
            hm_di = di["devices"]

        target_di_count = controllers
        if len(hm_di) == target_di_count:
            # Per-device axes: explicit override wins, otherwise look up by VID
            wrong_axes = []
            for d in hm_di:
                expected = explicit_axes if explicit_axes is not None else VID_AXES.get(d["vid"], DEFAULT_AXES)
                if d["axes"] != expected:
                    wrong_axes.append((d["id"], d["vid"], d["axes"], expected))
            any_moving = any(d["moving"] for d in hm_di)
            if not wrong_axes and any_moving:
                ids = ", ".join(
                    f"VID=0x{d['vid']:04X} PID=0x{d['pid']:04X} Axes={d['axes']}"
                    for d in hm_di
                )
                print(f"PASS  {len(hm_di)} device(s) ({ids})")
            elif not wrong_axes:
                ax_summary = ",".join(str(d["axes"]) for d in hm_di)
                err_summary = "; ".join(
                    f"joy{d['id']}: {','.join(d.get('errors', []))}"
                    for d in hm_di if d.get("errors"))
                hr_tail = f" [{err_summary}]" if err_summary else ""
                print(f"FAIL  {len(hm_di)} device(s) [{ax_summary}ax] but no movement{hr_tail}")
                failures.append(f"DirectInput: {len(hm_di)} device(s) static (no movement)")
            else:
                mism = ", ".join(
                    f"joy{j} VID=0x{v:04X}: {a} (want {e})" for j, v, a, e in wrong_axes
                )
                print(f"FAIL  axes mismatch: {mism}")
                failures.append(f"DirectInput axes mismatch ({mism})")
        elif len(hm_di) == 0:
            print(f"FAIL  no HIDMaestro DI devices (di.count={di['count']})")
            failures.append("DirectInput: no HIDMaestro devices")
        else:
            print(f"FAIL  {len(hm_di)} HIDMaestro DI devices (want {target_di_count})")
            for d in hm_di:
                print(f"    Joy{d['id']}: VID=0x{d['vid']:04X} PID=0x{d['pid']:04X} Axes={d['axes']}")
            failures.append(f"DirectInput: {len(hm_di)} devices (want {target_di_count})")

    # -- HIDAPI / SDL3 (heterogeneous-aware) --
    #
    # Xbox controllers are IG-filtered → SDL3 routes them through XInput backend.
    # Non-Xbox controllers are visible to SDL3 directly → expect live data.
    hi = check_hidapi()
    print(f"  HIDAPI/SDL3: ", end="")
    if not hi.get("available"):
        print(f"SKIP  ({hi.get('error', 'unavailable')})")
    else:
        ig_devs = [d for d in hi["devices"] if d["ig_filtered"]]
        non_ig = [d for d in hi["devices"] if not d["ig_filtered"]]
        # Expected counts based on what's actually running
        non_xbox_expected = sum(1 for d in hm_devices if d["vid"] not in XINPUT_VIDS)
        xbox_expected = sum(1 for d in hm_devices if d["vid"] in XINPUT_VIDS)
        if not hm_devices:
            # Legacy fallback (pre-serial-fix): use the old check
            if len(non_ig) == 0 and len(ig_devs) > 0:
                ids = ", ".join(f"PID=0x{d['pid']:04X}" for d in ig_devs)
                print(f"OK    {len(ig_devs)} dev IG-filtered ({ids}) — SDL3 falls through to XInput backend")
            elif len(non_ig) == controllers and hi.get("live_data"):
                ids = ", ".join(f"PID=0x{d['pid']:04X} Bus={d['bus']}" for d in non_ig)
                print(f"PASS  {len(non_ig)} dev live ({ids})")
            else:
                print(f"WARN  {hi.get('total_045e', 0)} 045E device(s), no live data")
        else:
            total_ok = (len(ig_devs) == xbox_expected and len(non_ig) == non_xbox_expected)
            live_ok = (non_xbox_expected == 0) or hi.get("live_data")
            if total_ok and live_ok:
                parts = []
                if ig_devs:
                    parts.append(f"{len(ig_devs)} IG-filtered (XInput backend)")
                if non_ig:
                    ids = ", ".join(f"PID=0x{d['pid']:04X} Bus={d['bus']}" for d in non_ig)
                    parts.append(f"{len(non_ig)} live ({ids})")
                print(f"PASS  {', '.join(parts)}")
            elif non_xbox_expected > 0 and not hi.get("live_data"):
                print(f"FAIL  {non_xbox_expected} non-Xbox device(s) but no live data")
                failures.append("HIDAPI: non-Xbox device has no live data")
            else:
                print(f"FAIL  {len(ig_devs)} IG, {len(non_ig)} non-IG (want {xbox_expected} IG, {non_xbox_expected} non-IG)")
                failures.append(f"HIDAPI: {len(ig_devs)} IG, {len(non_ig)} non-IG (want {xbox_expected}/{non_xbox_expected})")

    # -- HID enumeration order (creation-order check, dead simple) --
    print("  HID order:   ", end="")
    if not ho.get("available"):
        print(f"SKIP  ({ho.get('error', 'unavailable')})")
    elif ho["count"] == 0:
        print("FAIL  no HM-CTL-* serials found — driver pre-serial-fix or no devices")
        failures.append("HID order: no HIDMaestro devices visible")
    elif ho["count"] != controllers:
        print(f"FAIL  found {ho['count']} HIDMaestro device(s), expected {controllers}")
        failures.append(f"HID order: {ho['count']} devices (want {controllers})")
    elif ho["in_order"]:
        print(f"PASS  {ho['count']} device(s) in creation order: {ho['indices']}")
    else:
        print(f"FAIL  enumeration out of order: {ho['indices']}")
        failures.append(f"HID order mismatch: {ho['indices']} (want {list(range(controllers))})")

    # -- GameInput / Windows.Gaming.Input.RawGameController (Dolphin's source) --
    # Presence-only: see check_gameinput for why movement isn't sampled here.
    # Live data is canonically verified by XInput / DirectInput / Browser.
    print("  GameInput:   ", end="")
    gi = check_gameinput(repeats, expected=controllers)
    if not gi.get("available"):
        print(f"SKIP  ({gi.get('error', 'unavailable')})")
    else:
        gi_count = gi["count"]
        if gi_count == controllers:
            names = ", ".join(c.get("name","?") for c in gi["controllers"])
            print(f"PASS  {gi_count} controller(s) visible to WGI ({names})")
        elif gi_count == 0:
            print(f"FAIL  no RawGameControllers in WGI (want {controllers})")
            failures.append("GameInput: no controllers")
        else:
            print(f"FAIL  {gi_count} controller(s) (want {controllers})")
            failures.append(f"GameInput: {gi_count} controllers (want {controllers})")

    # -- Browser (real Chromium navigator.getGamepads via headless launcher) --
    # Chromium hard-caps navigator.getGamepads() at 4 slots for legacy spec
    # compatibility, regardless of how many gamepads are physically present.
    # This is a Chromium limit, not a HIDMaestro limit. Cap our expectation.
    print("  Browser:     ", end="", flush=True)
    BROWSER_HARD_CAP = 4
    target_browser = min(controllers, BROWSER_HARD_CAP)
    if force_no_browser:
        print("SKIP  (--no-browser)")
        br = {"available": False}
    else:
        br = check_browser()
    if not br.get("available") and not force_no_browser:
        print(f"SKIP  ({br.get('error', 'unavailable')})")
    elif br.get("available"):
        pads = br.get("pads", 0)
        live = br.get("live", 0)
        snap = br.get("snapshot", [])
        if pads == target_browser and live > 0:
            cap_note = (f" [Chromium 4-slot cap; "
                        f"{controllers - BROWSER_HARD_CAP} more present]"
                        if controllers > BROWSER_HARD_CAP else "")
            if snap:
                summary_lines = []
                for s in snap[:target_browser]:
                    ident = s.get("id", "?")
                    ident_short = ident[:55] + ("..." if len(ident) > 55 else "")
                    axes = s.get("axes", [])
                    axes_str = ",".join(f"{a:+.2f}" for a in axes[:2])
                    summary_lines.append(f"[{axes_str}] {ident_short}")
                first = summary_lines[0]
                rest = summary_lines[1:]
                print(f"PASS  {pads} pad(s) live ({br['browser']}){cap_note} {first}")
                for line in rest:
                    print(f"                                   {line}")
            else:
                print(f"PASS  {pads} pad(s) live ({br['browser']}){cap_note}")
        elif pads == target_browser and live == 0:
            print(f"FAIL  {pads} pad(s) detected but axes static (no live data)")
            failures.append("Browser: gamepad present but data is static")
        elif pads == 0:
            print("FAIL  no gamepad detected")
            failures.append("Browser: no gamepad detected")
        else:
            note = ""
            if controllers > BROWSER_HARD_CAP:
                note = f" [Chromium 4-slot cap; {controllers} controllers connected]"
            print(f"FAIL  {pads} pads detected (want {target_browser}){note}")
            failures.append(f"Browser: {pads} pads (want {target_browser})")

    # -- Summary --
    print()
    if failures:
        print(f"  RESULT: FAIL ({len(failures)} issue(s))")
        for f in failures:
            print(f"    - {f}")
        return 1
    else:
        print("  RESULT: ALL PASS")
        return 0


if __name__ == "__main__":
    sys.exit(main())
