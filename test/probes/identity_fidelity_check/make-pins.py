# Writes identity_fidelity_check's pins file from two records of the same
# machine, one per backend. A reading both backends give is one line. A
# reading only one gives is a line that names the backend, under a comment
# that says why the backends differ there, chosen by the kind of profile
# and the shape of the difference. A difference no rule here covers stops
# the run, so every one is looked at once.
#   python -I make-pins.py <umdf2 record> <usbip record> <profiles dir> <out> [<umdf2 later record> <usbip later record>]
import glob, json, os, re, sys
from collections import defaultdict

sys.stdout.reconfigure(encoding='utf-8', errors='backslashreplace')
rec_a, rec_b, profiles_dir, out_path = sys.argv[1:5]
# Later records of some profiles, which replace the first records' entries.
over_a, over_b = (sys.argv[5:7] + [None, None])[:2]
READERS = ('create', 'hid', 'xinput', 'wgi', 'sdl', 'chrome')

def load(path):
    pins, cur, head = {}, None, ''
    for raw in open(path, encoding='utf-8'):
        line = raw.rstrip('\n')
        if not line.strip():
            continue
        if line.startswith('#'):
            head = head or line
            continue
        if not line.startswith(' '):
            cur = pins.setdefault(line.strip(), defaultdict(list))
            continue
        body = line.strip()
        if body.startswith('#'):
            cur['note'].append(body.lstrip('# ').strip())
            continue
        reader, _, value = body.partition(' ')
        cur[reader].append(value)
    return head, pins

head_a, a = load(rec_a)
head_b, b = load(rec_b)
for over, base in ((over_a, a), (over_b, b)):
    if over:
        _, later = load(over)
        for pid, lines in later.items():
            base[pid] = lines
        print(f'{over}: {len(later)} profiles replaced')

profiles = {}
for p in glob.glob(os.path.join(profiles_dir, '**', '*.json'), recursive=True):
    try:
        d = json.load(open(p, encoding='utf-8-sig'))
    except Exception:
        continue
    if isinstance(d, dict) and 'id' in d:
        profiles[d['id']] = d

def kind(pid):
    d = profiles.get(pid, {})
    via = (d.get('xinput') or {}).get('via') or ('xinputhid' if d.get('driverMode') in ('xinputhid', 'xusb22') else None)
    bt = d.get('connection') == 'bluetooth'
    if via == 'xusbCompanion':
        return 'xbox360'
    if via == 'xinputhid':
        return 'xbox-bt' if bt else 'xbox-wired'
    return 'bt' if bt else 'usb'

REASON = {
    'bus': "A UMDF2 device sits on a root-enumerated node, which carries no USB compatible ID, so hidapi's rule "
           "names no bus there. SDL reads the pad as USB on both, as it reads every pad that is not Bluetooth.",
    'wginame': "On UMDF2 the SDK gives the HID child the profile's product name, where a USB device's child keeps "
               "Windows' own description, and Windows.Gaming.Input reads that name.",
    'sdlname': "The UMDF2 driver answers a manufacturer string request with the product string, so SDL builds the "
               "name from the product alone. On USB/IP the device serves its manufacturer string, as the real one does.",
    'xbox360': "On USB/IP Windows' own xusb22 drives the pad and makes its HID device, as for a real pad: HID "
               "version 0000, the revision of the pad's own descriptor, and xusb22.inf's name. On UMDF2 the XUSB "
               "companion answers XInput and the HID device is HIDMaestro's own.",
    'xbox-wired': "On USB/IP Windows' GIP driver makes the pad's HID device as 045E:02FF, as it does for a real pad "
                  "on a cable, and names it. On UMDF2 the HID device carries the profile's own IDs and name.",
    'xbox-bt': "On USB/IP xinputhid sits on the device's own node, as on a real Bluetooth pad's, and XInput and "
               "Windows.Gaming.Input report the pad wireless, with the pad's own descriptor and its collections. "
               "On UMDF2 xinputhid sits on the HID child, the arrangement of a pad on a cable, and they report it wired.",
    'chrome': "Chrome builds the id from the name Windows.Gaming.Input or the HID device gives, which differs as "
              "the line above says.",
    'usbip-only': "Only the USB/IP backend presents this profile.",
}

# Readings both backends give that carry IDs other than the profile's own,
# and why.
OTHER_IDS = {
    ('genesis-nso', 'sdl'): "SDL's Switch driver names a pad by the type it answers over the Switch protocol, 057E:201E for "
                            "the Genesis pad (SDL_hidapi_switch.c, HIDAPI_SetDeviceProduct), as it names the real pad.",
    ('xbox-360-wireless', 'wgi'): "Windows gives the pad behind the Xbox 360 wireless receiver no product ID of its own, "
                                  "so Windows.Gaming.Input reports 045E:0000 for it.",
}

# Readings a Windows gives only from a build on, by profile, reader and a
# part of the line, and why. The records are of a newer Windows, so they
# hold the line, and the pins give it the build.
KEYBOARD = ("Windows starts the pad's keyboard collection, beside the game controller's, from build 26100. "
            "Windows 10 leaves it not started with problem 10 (read on build 19044), so nothing reads it there. "
            "The Windows 11 builds before 26100 have not been read.")
FROM_BUILD = {
    ('xbox-elite-v2-bt', 'hid', 'usage 0001:0006'): (26100, KEYBOARD),
    ('xbox-elite-v2-bt-v2', 'hid', 'usage 0001:0006'): (26100, KEYBOARD),
}

def pinned(pid, tag, reader, v):
    # One value's lines: its own, or its own with the build under the reason
    # for the build.
    head = (tag + ':' if tag else '') + reader
    for (p, r, part), (build, why) in FROM_BUILD.items():
        if p == pid and r == reader and part in v:
            return ['  # ' + why, f'  {head}>={build} {v}']
    return [f'  {head} {v}']

def unquoted(v):
    return re.sub(r'"[^"]*"', '"?"', v)

def classify(pid, reader, va, vb):
    k = kind(pid)
    if k in ('xbox360', 'xbox-wired', 'xbox-bt'):
        return k
    if reader == 'hid':
        if sorted(x.replace('bus unknown', 'bus USB') for x in va) == sorted(vb):
            return 'bus'
    if reader == 'wgi':
        if sorted(unquoted(x) for x in va) == sorted(unquoted(x) for x in vb):
            return 'wginame'
    if reader == 'sdl':
        if sorted(unquoted(x) for x in va) == sorted(unquoted(x) for x in vb):
            return 'sdlname'
    if reader == 'chrome':
        return 'chrome'
    return None

lines = [
    '# identity_fidelity_check pins: what each reader names every deployable profile, on each backend.',
    '# A line holds for both backends unless it names one ("usbip:hid"), and on every Windows unless it',
    '# names the build it holds from ("hid>=26100"). A line that names either follows a comment that says',
    '# why, and so does a line that carries IDs other than the profile\'s own. A comment holds for the',
    '# lines of the same reader, backend and build after it. Written by make-pins.py beside this file',
    '# from two records:',
    '#   ' + head_a.lstrip('# '),
    '#   ' + head_b.lstrip('# '),
    '',
]
unknown = []
counts = defaultdict(int)
for pid in sorted(set(a) | set(b)):
    lines.append(pid)
    in_a, in_b = pid in a, pid in b
    for note_src, tag in ((a.get(pid, {}), 'umdf2'), (b.get(pid, {}), 'usbip')):
        for n in note_src.get('note', []):
            lines.append(f'  # {tag}: {n}')
    for reader in READERS:
        va = sorted(a[pid].get(reader, [])) if in_a else []
        vb = sorted(b[pid].get(reader, [])) if in_b else []
        if in_a and in_b and va == vb:
            if va and (pid, reader) in OTHER_IDS:
                lines.append('  # ' + OTHER_IDS[(pid, reader)])
            for v in va:
                lines.extend(pinned(pid, '', reader, v))
            continue
        if not in_a:
            if vb:
                counts['usbip-only'] += 1
            again = True
            for v in vb:
                own = pinned(pid, 'usbip', reader, v)
                if again or len(own) > 1:
                    lines.append('  # ' + REASON['usbip-only'])
                lines.extend(own)
                again = len(own) > 1
            continue
        reason = classify(pid, reader, va, vb)
        if reason is None:
            unknown.append((pid, reader, va, vb))
            continue
        counts[reason] += 1
        # The lines both give stay plain. The rest name their backend.
        common = [v for v in va if v in vb]
        rest_a = list(va)
        rest_b = list(vb)
        for v in common:
            if v in rest_a and v in rest_b:
                rest_a.remove(v); rest_b.remove(v)
                lines.append(f'  {reader} {v}')
        for tag, rest in (('umdf2', rest_a), ('usbip', rest_b)):
            # The reason goes above each backend's group: the check reads
            # the comment above a line as the reason of that line and of
            # the lines of the same reader, backend and build after it. A
            # line with a build carries both reasons, and the line after it
            # the group's again.
            again = True
            for v in rest:
                own = pinned(pid, tag, reader, v)
                if again or len(own) > 1:
                    lines.append('  # ' + REASON[reason])
                lines.extend(own)
                again = len(own) > 1
    lines.append('')

if unknown:
    print(f'{len(unknown)} differences no rule covers:')
    for pid, reader, va, vb in unknown:
        print(f'  {pid} {reader} ({kind(pid)})')
        for v in va: print(f'      umdf2 {v[:200]}')
        for v in vb: print(f'      usbip {v[:200]}')
    sys.exit(1)
open(out_path, 'w', encoding='utf-8', newline='\n').write('\n'.join(lines))
print(f'wrote {out_path}: {len(set(a) | set(b))} profiles, {len(lines)} lines')
for k, n in sorted(counts.items(), key=lambda kv: -kv[1]):
    print(f'  {k}: {n} reader differences')
