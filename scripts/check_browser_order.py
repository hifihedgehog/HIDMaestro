"""Browser-order diagnostic for HIDMaestro virtual controllers.

Assumes the test app is running 'emulate <profile> ... <profile>' with the
console set to 'mark' mode (each controller holds button N where N is its
creation index). Launches the headless browser check, reads
navigator.getGamepads(), and prints the mapping browser_index -> creation_index
deduced from which button each pad is holding down.

Output format (one line per pad):

  browser[0] -> creation[N] [id="..."]
  ...

Followed by a verdict line:

  ORDER: PASS  (browser order matches creation order)
or
  ORDER: FAIL  (mapping is reversed/scrambled)

The mapping detection is unambiguous because button indices in the Chromium
Standard Gamepad mapping are integers (buttons[0]=A, [1]=B, [2]=X, [3]=Y, [4]=LB,
[5]=RB) — no float tolerance needed.

Run via:  sudo --inline python scripts/check_browser_order.py [--browser-cap 4]
"""
from __future__ import annotations

import sys
from pathlib import Path

HERE = Path(__file__).parent
sys.path.insert(0, str(HERE / "browser_check"))

from launcher import run_browser_check  # type: ignore  # noqa: E402


def find_pressed_button(pad: dict) -> int:
    """Returns the index of the first pressed button in the pad, or -1 if none."""
    btns = pad.get("buttons", [])
    for i, b in enumerate(btns):
        if isinstance(b, dict) and b.get("pressed"):
            return i
    return -1


def hid_order() -> list[int]:
    """Returns the creation indices of HIDMaestro devices in the order
    Windows HID enumeration returns them. Chromium's HID gamepad source
    consumes the same enumerator, so this is the canonical "what order
    does HID give Chromium" answer. Empty list if hidapi unavailable."""
    try:
        import hid  # type: ignore
    except ImportError:
        return []
    indices: list[int] = []
    seen: set[str] = set()
    for d in hid.enumerate(0, 0):
        sn = d.get("serial_number") or ""
        if not sn.startswith("HM-CTL-"):
            continue
        if sn in seen:
            continue
        seen.add(sn)
        try:
            indices.append(int(sn[len("HM-CTL-"):]))
        except ValueError:
            pass
    return indices


def main() -> int:
    # First: HID enumeration order. If THIS is scrambled, it's our bug
    # (PnP / ContainerID / driver-load order). If HID is in creation order
    # but the browser is scrambled, it's Chromium reordering — not us.
    hid_idx = hid_order()
    if hid_idx:
        print(f"HID enumeration order: {hid_idx}")
        if hid_idx == sorted(hid_idx) and hid_idx == list(range(len(hid_idx))):
            print("  HID order: CORRECT  (creation order [0..N])")
        else:
            print("  HID order: WRONG  (Chromium can't fix what we hand it)")
    else:
        print("HID enumeration: unavailable (hidapi not installed)")
    print()

    result = run_browser_check(timeout_s=20.0)
    if not result.get("available"):
        print(f"ERROR: browser check unavailable: {result.get('error', '?')}")
        return 2

    snap = result.get("snapshot", [])
    if not snap:
        print("ERROR: no gamepads in browser snapshot — is the test app running with 'mark'?")
        return 2

    print(f"Browser: {result.get('browser', '?')}, {len(snap)} pad(s) visible")
    print()

    rows: list[tuple[int, int, str]] = []  # (browser_index, creation_index, id)
    for pad in snap:
        bidx = pad.get("index", -1)
        creation = find_pressed_button(pad)
        ident = pad.get("id", "?")
        rows.append((bidx, creation, ident))

    rows.sort(key=lambda r: r[0])
    for bidx, creation, ident in rows:
        ident_short = ident[:60] + ("..." if len(ident) > 60 else "")
        if creation < 0:
            print(f"  browser[{bidx}] -> creation[??]   (no button pressed!)  id={ident_short}")
        else:
            print(f"  browser[{bidx}] -> creation[{creation}]   id={ident_short}")

    print()
    # Verdict: each browser slot i should report creation i (i.e. correct order).
    # If any slot reports a different creation index, ordering is wrong.
    expected = list(range(len(rows)))
    actual = [r[1] for r in rows]
    if any(c < 0 for c in actual):
        print("ORDER: INCONCLUSIVE  (one or more pads not holding any button)")
        return 3
    if actual == expected:
        print("ORDER: PASS  (browser order matches creation order)")
        return 0
    if actual == list(reversed(expected)):
        print("ORDER: FAIL  REVERSED  (browser order is exactly creation order reversed)")
        return 1
    print(f"ORDER: FAIL  SCRAMBLED  expected={expected} actual={actual}")
    return 1


if __name__ == "__main__":
    sys.exit(main())
