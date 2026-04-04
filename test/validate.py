"""HIDMaestro full fidelity validation script.
Run after deploying any profile to verify all APIs pass.
Usage: python validate.py
"""
import ctypes, sys, subprocess, os

try:
    import hid
except ImportError:
    print("ERROR: pip install hidapi")
    sys.exit(1)

CHROME_GAMEPAD_USAGES = {0x04, 0x05, 0x08}
PASS = True

def check(name, condition, detail=""):
    global PASS
    icon = "PASS" if condition else "FAIL"
    if not condition: PASS = False
    print(f"  [{icon}] {name}{(' - ' + detail) if detail else ''}")
    return condition


print("=" * 55)
print("  HIDMaestro Validation")
print("=" * 55)

# 1. XInput
xi = ctypes.windll.xinput1_4
class XS(ctypes.Structure):
    _fields_ = [('p',ctypes.c_ulong),('b',ctypes.c_ushort),('lt',ctypes.c_ubyte),('rt',ctypes.c_ubyte),
                ('lx',ctypes.c_short),('ly',ctypes.c_short),('rx',ctypes.c_short),('ry',ctypes.c_short)]

xi_slots = []
for i in range(4):
    s = XS()
    if xi.XInputGetState(i, ctypes.byref(s)) == 0:
        xi_slots.append(i)

print("\nXInput:")
check("Exactly 1 slot", len(xi_slots) == 1, f"got {len(xi_slots)} {xi_slots}")

# 2. DirectInput
wm = ctypes.windll.winmm
class JC(ctypes.Structure):
    _fields_ = [('mid',ctypes.c_ushort),('pid',ctypes.c_ushort),('nm',ctypes.c_wchar*32),
                ('xn',ctypes.c_uint),('xx',ctypes.c_uint),('yn',ctypes.c_uint),('yx',ctypes.c_uint),
                ('zn',ctypes.c_uint),('zx',ctypes.c_uint),('nb',ctypes.c_uint),
                ('pn',ctypes.c_uint),('px',ctypes.c_uint),('rn',ctypes.c_uint),('rx',ctypes.c_uint),
                ('un',ctypes.c_uint),('ux',ctypes.c_uint),('vn',ctypes.c_uint),('vx',ctypes.c_uint),
                ('cp',ctypes.c_uint),('ma',ctypes.c_uint),('na',ctypes.c_uint),('mb',ctypes.c_uint),
                ('rk',ctypes.c_wchar*32),('ov',ctypes.c_wchar*260)]

di_devs = []
for j in range(16):
    c = JC()
    if wm.joyGetDevCapsW(j, ctypes.byref(c), ctypes.sizeof(c)) == 0:
        di_devs.append({'joy': j, 'vid': c.mid, 'pid': c.pid, 'axes': c.na, 'btns': c.nb})

print("\nDirectInput:")
check("Exactly 1 device", len(di_devs) == 1, f"got {len(di_devs)}")
if di_devs:
    d = di_devs[0]
    check("5 axes", d['axes'] == 5, f"got {d['axes']}")
    check("VID 045E", d['vid'] == 0x045E, f"got 0x{d['vid']:04X}")

# 3. SDL3 / HIDAPI
all_045e = list(hid.enumerate(0x045E))
sdl_visible = [d for d in all_045e if '&IG_' not in d['path'].decode().upper()]
sdl_045e = [d for d in all_045e if d['product_id'] in (0x0B13, 0x02FD, 0x028E)]

print("\nSDL3/HIDAPI:")
check("1 device for profile VID/PID", len(sdl_045e) >= 1, f"got {len(sdl_045e)}")
if sdl_045e:
    d = sdl_045e[0]
    check("Bus type BLUETOOTH", d['bus_type'] == 2, f"got {d['bus_type']}")
    check("Path has &IG_", '&IG_' in d['path'].decode().upper())
check("0 visible to HIDAPI (no &IG_)", len(sdl_visible) == 0, f"got {len(sdl_visible)}")

# 4. Browser (Chrome RawInput simulation)
raw_gamepads = []
for d in hid.enumerate():
    if d['usage_page'] == 0x01 and d['usage'] in CHROME_GAMEPAD_USAGES:
        path = d['path'].decode()
        if '&IG_' not in path.upper():
            raw_gamepads.append(f"VID={d['vendor_id']:04X} PID={d['product_id']:04X}")

print("\nBrowser:")
check("0 Chrome RawInput gamepads", len(raw_gamepads) == 0,
      f"got {len(raw_gamepads)}: {raw_gamepads}" if raw_gamepads else "")
browser_total = len(raw_gamepads) + (1 if xi_slots else 0)
check("1 total browser entry", browser_total == 1, f"got {browser_total}")

# 5. WinExInput
r = subprocess.run(['powershell', '-Command',
    'pnputil /enum-interfaces /class "{6c53d5fd-6480-440f-b618-476750c5e1a6}" 2>&1'],
    capture_output=True, text=True, timeout=10)
winex = sum(1 for l in r.stdout.split('\n') if 'Enabled' in l)

print("\nWGI/WinExInput:")
check("At least 1 enabled", winex >= 1, f"got {winex}")

# 6. BTHLEDEVICE spoof
print("\nBTHLEDEVICE spoof:")
if sdl_045e:
    check("HIDAPI bus_type=2 (BT)", sdl_045e[0]['bus_type'] == 2)

# 7. No duplicate XInput
print("\nDuplicates:")
xusb_r = subprocess.run(['powershell', '-Command',
    'pnputil /enum-interfaces /class "{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}" 2>&1'],
    capture_output=True, text=True, timeout=10)
xusb_enabled = sum(1 for l in xusb_r.stdout.split('\n') if 'Enabled' in l)
check("XInput slots == 1", len(xi_slots) == 1)

# 8. Nefarius devices intact
r2 = subprocess.run(['powershell', '-Command',
    'Get-PnpDevice -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -like "*Nefarius*" -and $_.Status -eq "OK" } | Measure-Object | Select-Object -ExpandProperty Count'],
    capture_output=True, text=True, timeout=10)
nef = int(r2.stdout.strip()) if r2.stdout.strip().isdigit() else 0

print("\nSystem integrity:")
check("Nefarius devices OK", nef >= 2, f"got {nef} (ViGEmBus + HidHide)")

# Summary
print("\n" + "=" * 55)
if PASS:
    print("  ALL PASS - FULL FIDELITY")
else:
    print("  ISSUES FOUND - SEE ABOVE")
print("=" * 55)
sys.exit(0 if PASS else 1)
