"""Cemu-equivalent XInput readback diagnostic.

The user reports that Cemu's controller-config visualizer shows our virtual
Xbox Series X|S BT controller drawing weird shapes (changing per-run) on
the LEFT-STICK Y axis under the XINPUT backend, while a real Xbox Series X
controller renders the same circle pattern correctly. So the bug is in the
chain  SDK → driver → xinputhid filter → XInput service → consumer.

Cemu's XInput backend (src/input/api/XInput/XInputController.cpp) does:

    state.Gamepad.sThumbLY > 0 -> result.axis.y = sThumbLY / 32767
    state.Gamepad.sThumbLY < 0 -> result.axis.y = -sThumbLY / sint16::min()

That is the entire transformation. Then the visualizer paints whatever
result.axis.y is (after deadzone). So if XInputGetState returns the wrong
sThumbLY for our virtual device, the visualizer draws garbage.

This script calls XInputGetState directly via ctypes — the same Win32 API
Cemu's m_XInputGetState resolves to — and prints sThumbLX/sThumbLY for the
slot the user nominates. Run it AFTER 'park'ing the SDK at known left-stick
positions and verify that XInput returns the expected values at each.

Usage:
  python scripts/xinput_park_readback.py [slot]   # poll forever, slot defaults to 0
  python scripts/xinput_park_readback.py [slot] --once   # one read and exit
"""
from __future__ import annotations

import argparse
import ctypes
import ctypes.wintypes as wt
import sys
import time


# XInput structures (XINPUT_STATE / XINPUT_GAMEPAD) — shape is fixed across
# every xinput*.dll Microsoft has shipped, so the layout is safe to hardcode.
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
    _fields_ = [
        ("dwPacketNumber", wt.DWORD),
        ("Gamepad", XINPUT_GAMEPAD),
    ]


# Cemu links against XInput1_4. Try the same DLL first, fall back to 9_1_0
# if 1_4 isn't present (extremely rare on Win10+).
def load_xinput():
    for name in ("XInput1_4.dll", "xinput1_4.dll", "XInput9_1_0.dll"):
        try:
            return ctypes.WinDLL(name), name
        except OSError:
            pass
    raise RuntimeError("no XInput DLL found")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("slot", nargs="?", type=int, default=0, help="XInput user slot 0..3")
    ap.add_argument("--once", action="store_true", help="single read and exit")
    ap.add_argument("--interval", type=float, default=0.1, help="poll interval seconds")
    args = ap.parse_args()

    if not (0 <= args.slot <= 3):
        print(f"ERROR: slot must be 0..3, got {args.slot}", file=sys.stderr)
        return 2

    dll, name = load_xinput()
    print(f"Using {name}, slot={args.slot}")
    XInputGetState = dll.XInputGetState
    XInputGetState.argtypes = [wt.DWORD, ctypes.POINTER(XINPUT_STATE)]
    XInputGetState.restype = wt.DWORD

    def read_once() -> XINPUT_STATE | None:
        st = XINPUT_STATE()
        rc = XInputGetState(args.slot, ctypes.byref(st))
        return st if rc == 0 else None

    # Header
    print(f"{'time':>8s}  {'pkt':>8s}  {'thLX':>8s}  {'thLY':>8s}  {'thLY/32767':>11s}  buttons    triggers")

    last_pkt = None
    t0 = time.time()
    try:
        while True:
            st = read_once()
            if st is None:
                print(f"{time.time()-t0:8.2f}  --DISCONNECTED-- (slot {args.slot})")
                if args.once:
                    return 1
                time.sleep(0.5)
                continue
            # Cemu's normalization for the negative half divides by the
            # type's MIN value (= -32768) so the ratio is positive again.
            # Replicate it exactly for like-for-like comparison.
            if st.Gamepad.sThumbLY > 0:
                ax_y = st.Gamepad.sThumbLY / 32767.0
            elif st.Gamepad.sThumbLY < 0:
                ax_y = -st.Gamepad.sThumbLY / -32768.0
            else:
                ax_y = 0.0

            t = time.time() - t0
            print(f"{t:8.2f}  {st.dwPacketNumber:8d}  {st.Gamepad.sThumbLX:+8d}  "
                  f"{st.Gamepad.sThumbLY:+8d}  {ax_y:+11.4f}  "
                  f"0x{st.Gamepad.wButtons:04X}     "
                  f"L={st.Gamepad.bLeftTrigger:3d} R={st.Gamepad.bRightTrigger:3d}")
            last_pkt = st.dwPacketNumber
            if args.once:
                return 0
            time.sleep(args.interval)
    except KeyboardInterrupt:
        return 0


if __name__ == "__main__":
    sys.exit(main())
