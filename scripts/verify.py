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


def check_xinput(repeats: int) -> dict:
    try:
        xinput = ctypes.windll.xinput1_4
    except OSError:
        xinput = ctypes.windll.xinput9_1_0

    result = {"connected": False, "moving": False, "slot": -1, "details": ""}
    for slot in range(4):
        state = XINPUT_STATE()
        if xinput.XInputGetState(slot, ctypes.byref(state)) != 0:
            continue
        result["connected"] = True
        result["slot"] = slot
        for _ in range(repeats):
            xinput.XInputGetState(slot, ctypes.byref(state))
            if state.sThumbLX != 0 or state.sThumbLY != 0:
                result["moving"] = True
            time.sleep(0.25)
        result["details"] = (
            f"slot={slot} pkt={state.dwPacketNumber} "
            f"LX={state.sThumbLX:+d} LY={state.sThumbLY:+d} "
            f"LT={state.bLeftTrigger} RT={state.bRightTrigger}"
        )
        break
    return result


# ---------------------------------------------------------------------------
#  DirectInput (via WinMM)
# ---------------------------------------------------------------------------

class JOYCAPSW(ctypes.Structure):
    _fields_ = [
        ("wMid", ctypes.c_ushort), ("wPid", ctypes.c_ushort),
        ("szPname", ctypes.c_wchar * 32),
        ("wXmin", ctypes.c_uint), ("wXmax", ctypes.c_uint),
        ("wYmin", ctypes.c_uint), ("wYmax", ctypes.c_uint),
        ("wZmin", ctypes.c_uint), ("wZmax", ctypes.c_uint),
        ("wNumButtons", ctypes.c_uint),
        ("wPeriodMin", ctypes.c_uint), ("wPeriodMax", ctypes.c_uint),
        ("wRmin", ctypes.c_uint), ("wRmax", ctypes.c_uint),
        ("wUmin", ctypes.c_uint), ("wUmax", ctypes.c_uint),
        ("wVmin", ctypes.c_uint), ("wVmax", ctypes.c_uint),
        ("wCaps", ctypes.c_uint),
        ("wMaxAxes", ctypes.c_uint), ("wNumAxes", ctypes.c_uint),
        ("wMaxButtons", ctypes.c_uint),
        ("szRegKey", ctypes.c_wchar * 32),
        ("szOEMVxD", ctypes.c_wchar * 260),
    ]


class JOYINFOEX(ctypes.Structure):
    _fields_ = [
        ("dwSize", ctypes.c_ulong), ("dwFlags", ctypes.c_ulong),
        ("dwXpos", ctypes.c_ulong), ("dwYpos", ctypes.c_ulong),
        ("dwZpos", ctypes.c_ulong), ("dwRpos", ctypes.c_ulong),
        ("dwUpos", ctypes.c_ulong), ("dwVpos", ctypes.c_ulong),
        ("dwButtons", ctypes.c_ulong), ("dwButtonNumber", ctypes.c_ulong),
        ("dwPOV", ctypes.c_ulong),
        ("dwReserved1", ctypes.c_ulong), ("dwReserved2", ctypes.c_ulong),
    ]


def check_directinput(repeats: int) -> dict:
    winmm = ctypes.windll.winmm
    devices = []

    for jid in range(16):
        caps = JOYCAPSW()
        info = JOYINFOEX()
        info.dwSize = ctypes.sizeof(info)
        info.dwFlags = 0xFF  # JOY_RETURNALL

        if winmm.joyGetDevCapsW(jid, ctypes.byref(caps), ctypes.sizeof(caps)) != 0:
            continue
        if winmm.joyGetPosEx(jid, ctypes.byref(info)) != 0:
            continue

        moving = False
        for _ in range(repeats):
            winmm.joyGetPosEx(jid, ctypes.byref(info))
            if info.dwXpos != 32767 or info.dwYpos != 32767:
                moving = True
            time.sleep(0.15)

        devices.append({
            "id": jid,
            "vid": caps.wMid,
            "pid": caps.wPid,
            "name": caps.szPname.strip(),
            "axes": caps.wNumAxes,
            "buttons": caps.wNumButtons,
            "pov": bool(caps.wCaps & 0x10),
            "moving": moving,
        })

    return {"count": len(devices), "devices": devices}


# ---------------------------------------------------------------------------
#  HIDAPI / SDL3
# ---------------------------------------------------------------------------

def check_hidapi() -> dict:
    try:
        import hid
    except ImportError:
        return {"available": False, "error": "hidapi not installed (pip install hidapi)"}

    all_devs = list(hid.enumerate(0x045E))
    visible = [d for d in all_devs if "&IG_" not in d["path"].decode().upper()]
    bt_devs = [d for d in visible if d["bus_type"] == 2]

    result = {
        "available": True,
        "total_045e": len(all_devs),
        "visible_to_sdl3": len(visible),
        "bluetooth": len(bt_devs),
        "live_data": False,
        "devices": [],
    }

    for d in all_devs:
        path = d["path"].decode()
        ig = "&IG_" in path.upper()
        bus_names = {0: "UNKNOWN", 1: "USB", 2: "BT", 3: "SPI"}
        result["devices"].append({
            "pid": d["product_id"],
            "bus": bus_names.get(d["bus_type"], str(d["bus_type"])),
            "ig_filtered": ig,
            "usage": f"{d['usage_page']:04X}:{d['usage']:04X}",
            "product": d["product_string"],
        })

    for d in bt_devs:
        try:
            dev = hid.device()
            dev.open_path(d["path"])
            dev.set_nonblocking(1)
            data = dev.read(64)
            if data and any(b != 0 for b in data[1:]):
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

TARGET_AXES = 5  # Xbox controllers expose 5 axes (X,Y,Z,Rx,Ry); DualSense=6


def main():
    wait = 0
    repeats = 4
    controllers = 1   # number of HIDMaestro controllers expected to be running
    target_axes = TARGET_AXES

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
            target_axes = int(args[i + 1]); i += 2
        else:
            i += 1

    target_di_count = controllers
    target_browser = controllers

    if wait > 0:
        print(f"Waiting {wait}s...")
        time.sleep(wait)

    print("=" * 56)
    print("  HIDMaestro API Verification")
    print("=" * 56)
    print()

    failures = []

    # -- XInput --
    xi = check_xinput(repeats)
    if xi["connected"] and xi["moving"]:
        print(f"  XInput:      PASS  ({xi['details']})")
    elif xi["connected"]:
        print(f"  XInput:      WARN  connected but static ({xi['details']})")
    else:
        print("  XInput:      FAIL  not connected")
        failures.append("XInput not connected")

    # -- DirectInput --
    di = check_directinput(repeats)
    print(f"  DirectInput: ", end="")
    if di["count"] == target_di_count:
        # All devices must have correct axes count and at least one must show movement
        wrong_axes = [d for d in di["devices"] if d["axes"] != target_axes]
        any_moving = any(d["moving"] for d in di["devices"])
        if not wrong_axes and any_moving:
            ids = ", ".join(f"VID=0x{d['vid']:04X} PID=0x{d['pid']:04X}" for d in di["devices"])
            print(f"PASS  {di['count']} device(s) Axes={target_axes} ({ids})")
        elif not wrong_axes:
            print(f"WARN  {di['count']} device(s) {target_axes} axes but no movement")
        else:
            print(f"FAIL  axes mismatch: {[d['axes'] for d in di['devices']]} (want {target_axes})")
            failures.append(f"DirectInput axes mismatch (want {target_axes})")
    elif di["count"] == 0:
        print("FAIL  no devices")
        failures.append("DirectInput: no devices")
    else:
        print(f"FAIL  {di['count']} devices (want {target_di_count})")
        for d in di["devices"]:
            print(f"    Joy{d['id']}: VID=0x{d['vid']:04X} PID=0x{d['pid']:04X} "
                  f"Axes={d['axes']}")
        failures.append(f"DirectInput: {di['count']} devices (want {target_di_count})")

    # -- HIDAPI / SDL3 --
    hi = check_hidapi()
    print(f"  HIDAPI/SDL3: ", end="")
    if not hi.get("available"):
        print(f"SKIP  ({hi.get('error', 'unavailable')})")
    else:
        # For xinputhid profiles: HIDAPI sees the device with IG=True (skipped by SDL3).
        # SDL3 uses XInput backend instead. This is correct behavior.
        ig_devs = [d for d in hi["devices"] if d["ig_filtered"]]
        non_ig = [d for d in hi["devices"] if not d["ig_filtered"]]
        if len(non_ig) == 0 and len(ig_devs) > 0:
            ids = ", ".join(f"PID=0x{d['pid']:04X}" for d in ig_devs)
            print(f"OK    {len(ig_devs)} dev IG=True ({ids}) — SDL3 uses XInput backend")
        elif len(non_ig) == controllers and hi["live_data"]:
            ids = ", ".join(f"PID=0x{d['pid']:04X} Bus={d['bus']}" for d in non_ig)
            print(f"PASS  {len(non_ig)} dev live ({ids})")
        elif len(non_ig) > controllers:
            print(f"FAIL  {len(non_ig)} visible devices (want {controllers} or all IG-filtered)")
            failures.append(f"HIDAPI: {len(non_ig)} non-IG devices")
        else:
            print(f"WARN  {hi['total_045e']} device(s), no live data")

    # -- HID enumeration order (creation-order check, dead simple) --
    print("  HID order:   ", end="")
    ho = check_hid_order(controllers)
    if not ho.get("available"):
        print(f"SKIP  ({ho.get('error', 'unavailable')})")
    elif ho["count"] == 0:
        print("SKIP  (no HM-CTL-* serials found — driver pre-serial-fix?)")
    elif ho["count"] != controllers:
        print(f"WARN  found {ho['count']} HIDMaestro device(s), expected {controllers}")
    elif ho["in_order"]:
        print(f"PASS  {ho['count']} device(s) in creation order: {ho['indices']}")
    else:
        print(f"FAIL  enumeration out of order: {ho['indices']}")
        failures.append(f"HID order mismatch: {ho['indices']} (want {list(range(controllers))})")

    # -- Browser (real Chromium navigator.getGamepads via headless launcher) --
    print("  Browser:     ", end="", flush=True)
    br = check_browser()
    if not br.get("available"):
        print(f"SKIP  ({br.get('error', 'unavailable')})")
    else:
        pads = br.get("pads", 0)
        live = br.get("live", 0)
        snap = br.get("snapshot", [])
        if pads == target_browser and live > 0:
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
                print(f"PASS  {pads} pad(s) live ({br['browser']}) {first}")
                for line in rest:
                    print(f"                                   {line}")
            else:
                print(f"PASS  {pads} pad(s) live ({br['browser']})")
        elif pads == target_browser and live == 0:
            print(f"FAIL  {pads} pad(s) detected but axes static (no live data)")
            failures.append("Browser: gamepad present but data is static")
        elif pads == 0:
            print("FAIL  no gamepad detected")
            failures.append("Browser: no gamepad detected")
        else:
            print(f"FAIL  {pads} pads detected (want {target_browser})")
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
