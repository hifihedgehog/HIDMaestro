# Writes chromium-gamepad-ids.txt beside this script from a checkout of
# Chromium's device/gamepad folder: every VID and PID Chromium's gamepad code
# knows, its XInput type, and its standard mapping on Windows.
#
#   python -I make-chromium-tables.py <chromium checkout>
#
# The probe computes the id Chrome gives a pad from these tables and from
# what Windows.Gaming.Input and the HID class report, as Chromium does
# (wgi_data_fetcher_win.cc BuildGamepadIdString, gamepad_data_fetcher.cc
# UpdateGamepadStrings).
import os, re, subprocess, sys

root = sys.argv[1]
pad = os.path.join(root, 'device', 'gamepad')
commit = subprocess.run(['git', '-C', root, 'log', '-1', '--format=%H %ad', '--date=short'],
                        capture_output=True, text=True).stdout.strip()

ids = open(os.path.join(pad, 'gamepad_id_list.cc'), encoding='utf-8').read()
info = {}
for vid, pid, kind in re.findall(r'\{\{0x([0-9a-fA-F]{4}), 0x([0-9a-fA-F]{4})\}, kXInputType(\w+)\}', ids):
    info[(vid.lower(), pid.lower())] = kind

header = open(os.path.join(pad, 'gamepad_id_list.h'), encoding='utf-8').read()
names = {name: value.lower() for name, value in re.findall(r'\b(k\w+) = 0x([0-9a-fA-F]{8}),', header)}

maps = open(os.path.join(pad, 'gamepad_standard_mappings_win.cc'), encoding='utf-8').read()
table = maps[maps.index('kAvailableMappings[] = {'):]
table = table[:table.index('};')]
mapped = {}
for name, mapper in re.findall(r'\{GamepadId::(k\w+), (Mapper\w+)\}', table):
    if name in names:
        mapped[(names[name][:4], names[name][4:])] = mapper

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'chromium-gamepad-ids.txt')
with open(out, 'w', encoding='ascii', newline='\n') as f:
    f.write('# Chromium device/gamepad at ' + commit + ': gamepad_id_list.cc kGamepadInfo and\n')
    f.write('# gamepad_standard_mappings_win.cc kAvailableMappings. make-chromium-tables.py writes this file.\n')
    f.write('# vid:pid xinput-type mapper-on-windows\n')
    for (vid, pid), kind in sorted(info.items()):
        f.write(f'{vid}:{pid} {kind} {mapped.get((vid, pid), "-")}\n')
print(len(info), 'ids,', len(mapped), 'with a Windows mapping, written to', out)
