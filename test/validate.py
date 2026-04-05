"""HIDMaestro full fidelity validation script.
Run after deploying any profile to verify all APIs pass.
Usage: python validate.py [profile-id]

Detects the active profile automatically from registry if not specified.
"""
import ctypes, sys, subprocess, os, json, time

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

# Load profile info
profile = None
profile_id = sys.argv[1] if len(sys.argv) > 1 else None
profiles_dir = os.path.join(os.path.dirname(__file__), '..', 'profiles')
for root, dirs, files in os.walk(profiles_dir):
    for f in files:
        if f.endswith('.json') and f not in ('schema.json', 'scraped_descriptors.json'):
            try:
                p = json.load(open(os.path.join(root, f)))
                if profile_id and p.get('id') == profile_id:
                    profile = p
                    break
            except: pass
    if profile: break

# Auto-detect from registry VID/PID if no profile specified
if not profile:
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\HIDMaestro") as k:
            reg_vid = winreg.QueryValueEx(k, "VendorId")[0]
            reg_pid = winreg.QueryValueEx(k, "ProductId")[0]
        for root, dirs, files in os.walk(profiles_dir):
            for f in files:
                if f.endswith('.json') and f not in ('schema.json',):
                    try:
                        p = json.load(open(os.path.join(root, f)))
                        if int(p.get('vid','0'), 16) == reg_vid and int(p.get('pid','0'), 16) == reg_pid:
                            profile = p; break
                    except: pass
            if profile: break
    except: pass

is_xbox = profile and int(profile.get('vid','0'), 16) == 0x045E if profile else False
is_xinputhid = profile and profile.get('driverMode') == 'xinputhid' if profile else False
is_bluetooth = profile and profile.get('connection') == 'bluetooth' if profile else False
expect_xinput = is_xbox  # Xbox profiles get XInput via companion or xinputhid
prof_vid = int(profile['vid'], 16) if profile else 0x045E
prof_pid = int(profile['pid'], 16) if profile else 0x028E
prof_name = profile.get('name', 'Unknown') if profile else 'Unknown'

print("=" * 55)
print(f"  HIDMaestro Validation — {prof_name}")
print("=" * 55)

# 1. XInput
xi = ctypes.windll.xinput1_4
class XS(ctypes.Structure):
    _fields_ = [('p',ctypes.c_ulong),('b',ctypes.c_ushort),('lt',ctypes.c_ubyte),('rt',ctypes.c_ubyte),
                ('lx',ctypes.c_short),('ly',ctypes.c_short),('rx',ctypes.c_short),('ry',ctypes.c_short)]

xi_slots = []
xi_active = []
for i in range(4):
    s = XS()
    if xi.XInputGetState(i, ctypes.byref(s)) == 0:
        xi_slots.append(i)
        if s.p > 1:
            xi_active.append(i)

print("\nXInput:")
if expect_xinput:
    check("At least 1 active slot", len(xi_active) >= 1, f"got {len(xi_active)} active, {len(xi_slots)} total")
else:
    check("0 XInput slots (non-Xbox)", len(xi_slots) == 0, f"got {len(xi_slots)}")

# 1b. XInput trigger separation (Xbox profiles only)
if expect_xinput and xi_active:
    # Read triggers over time to verify LT and RT are independent
    lt_vals, rt_vals = [], []
    for _ in range(20):
        s = XS()
        if xi.XInputGetState(xi_active[0], ctypes.byref(s)) == 0:
            lt_vals.append(s.lt)
            rt_vals.append(s.rt)
        time.sleep(0.1)
    lt_range = max(lt_vals) - min(lt_vals)
    rt_range = max(rt_vals) - min(rt_vals)
    # If triggers are combined/mirrored, LT==RT at every sample
    all_equal = all(lt == rt for lt, rt in zip(lt_vals, rt_vals))
    check("XInput triggers are separate (LT != RT)", not all_equal,
          f"LT range={lt_range} RT range={rt_range}")

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
    # Xbox 360 HID mode: 6 axes (separate Z/Rz triggers required for browser fidelity).
    # Real 360 has 5 via xusb22.sys (no HID) but UMDF2 virtual devices must use HID.
    # xinputhid profiles: 5 axes (xinputhid's XInput mapping suppresses raw HID).
    expected_axes = 5 if (is_xinputhid or not is_xbox) else 6
    check(f"{expected_axes} axes", d['axes'] == expected_axes, f"got {d['axes']}")
    if is_xbox:
        check("VID 045E", d['vid'] == 0x045E, f"got 0x{d['vid']:04X}")

# 3. SDL3 / HIDAPI
all_prof = list(hid.enumerate(prof_vid, prof_pid))
sdl_visible = [d for d in all_prof if '&IG_' not in d['path'].decode().upper()]

print("\nSDL3/HIDAPI:")
check("1 device for profile VID/PID", len(all_prof) >= 1, f"got {len(all_prof)}")
if all_prof:
    d = all_prof[0]
    if is_bluetooth and is_xbox:
        check("Bus type BLUETOOTH", d['bus_type'] == 2, f"got {d['bus_type']}")
    if is_xbox:
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
if is_xbox:
    check("0 Chrome RawInput gamepads", len(raw_gamepads) == 0,
          f"got {len(raw_gamepads)}: {raw_gamepads}" if raw_gamepads else "")
    browser_total = len(raw_gamepads) + (1 if xi_slots else 0)
    check("1 total browser entry", browser_total == 1, f"got {browser_total}")
else:
    # Non-Xbox: 1 RawInput gamepad expected
    check("1 Chrome RawInput gamepad", len(raw_gamepads) == 1, f"got {len(raw_gamepads)}")

# 5. WinExInput
r = subprocess.run(['powershell', '-Command',
    'pnputil /enum-interfaces /class "{6c53d5fd-6480-440f-b618-476750c5e1a6}" 2>&1'],
    capture_output=True, text=True, timeout=10)
winex = sum(1 for l in r.stdout.split('\n') if 'Enabled' in l)

is_companion_only = profile and profile.get('companionOnly', False) if profile else False
print("\nWGI/WinExInput:")
if is_companion_only:
    check("WinExInput N/A (ViGEmBus mode)", True, "ViGEmBus provides WGI directly")
else:
    check("At least 1 enabled", winex >= 1, f"got {winex}")

# 6. BTHLEDEVICE spoof (Xbox BT only)
if is_bluetooth and is_xbox:
    print("\nBTHLEDEVICE spoof:")
    if all_prof:
        check("HIDAPI bus_type=2 (BT)", all_prof[0]['bus_type'] == 2)

# 7. No duplicate XInput
print("\nDuplicates:")
xusb_r = subprocess.run(['powershell', '-Command',
    'pnputil /enum-interfaces /class "{ec87f1e3-c13b-4100-b5f7-8b84d54260cb}" 2>&1'],
    capture_output=True, text=True, timeout=10)
xusb_enabled = sum(1 for l in xusb_r.stdout.split('\n') if 'Enabled' in l)
check("Active XInput slots == 1", len(xi_active) == 1, f"got {len(xi_active)} active, {len(xi_slots)} total")

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
