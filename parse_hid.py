hex_str = '05010905a10185010901a10009300931150027ffff0000950275108102c00901a10009320935150027ffff0000950275108102c0050209c5150026ff039501750a810215002500750695018103050209c4150026ff039501750a81021500250075069501810305010939150125083500463b016614007504950181427504950115002500350045006500810305091901290f150025017501950f810215002500750195018103050c0ab2001500250195017501810215002500750795018103050f09218503a102099715002501750495019102150025007504950191030970150026ff0075089501910209a7150026ff0075089501910265005500097c150026ff00750895019102c0c0'

data = bytes.fromhex(hex_str)
i = 0
indent = 0

usage_pages = {
    0x01: 'Generic Desktop', 0x02: 'Simulation Controls',
    0x05: 'Game Controls', 0x09: 'Button', 0x0C: 'Consumer',
    0x0F: 'Physical Interface Device'
}

generic_desktop_usages = {
    0x01: 'Pointer', 0x04: 'Joystick', 0x05: 'Game Pad',
    0x30: 'X', 0x31: 'Y', 0x32: 'Z', 0x33: 'Rx', 0x34: 'Ry', 0x35: 'Rz',
    0x36: 'Slider', 0x37: 'Dial', 0x38: 'Wheel', 0x39: 'Hat Switch',
    0x90: 'D-pad Up', 0x91: 'D-pad Down', 0x92: 'D-pad Right', 0x93: 'D-pad Left'
}

simulation_usages = {
    0xC4: 'Accelerator', 0xC5: 'Brake', 0xBB: 'Throttle',
    0xBA: 'Rudder', 0xC8: 'Steering'
}

collection_types = {0: 'Physical', 1: 'Application', 2: 'Logical', 3: 'Report'}

global_tags = {
    0: 'Usage Page', 1: 'Logical Minimum', 2: 'Logical Maximum',
    3: 'Physical Minimum', 4: 'Physical Maximum', 5: 'Unit Exponent',
    6: 'Unit', 7: 'Report Size', 8: 'Report ID', 9: 'Report Count',
    10: 'Push', 11: 'Pop'
}
local_tags = {
    0: 'Usage', 1: 'Usage Minimum', 2: 'Usage Maximum',
    3: 'Designator Index', 4: 'Designator Minimum', 5: 'Designator Maximum',
    7: 'String Index', 8: 'String Minimum', 9: 'String Maximum',
    10: 'Delimiter'
}
main_tags = {8: 'Input', 9: 'Output', 10: 'Collection', 11: 'Feature', 12: 'End Collection'}

current_usage_page = 0
state = {
    'usage_page': 0, 'logical_min': 0, 'logical_max': 0,
    'physical_min': 0, 'physical_max': 0,
    'report_size': 0, 'report_count': 0, 'report_id': 0,
    'unit': 0, 'unit_exp': 0
}
local_state = {'usages': [], 'usage_min': None, 'usage_max': None}
inputs = []

def get_usage_name(page, usage):
    if page == 0x01:
        return generic_desktop_usages.get(usage, '0x%02X' % usage)
    elif page == 0x02:
        return simulation_usages.get(usage, '0x%02X' % usage)
    elif page == 0x09:
        return 'Button %d' % usage
    elif page == 0x0C:
        if usage == 0xB2:
            return 'Record (0xB2)'
        return 'Consumer 0x%04X' % usage
    elif page == 0x0F:
        names = {0x21: 'Set Effect Report', 0x97: 'DC Enable Actuators',
                 0x70: 'Magnitude', 0xA7: 'Start Delay', 0x7C: 'Loop Count'}
        return names.get(usage, 'PID 0x%02X' % usage)
    return '0x%02X' % usage

while i < len(data):
    prefix = data[i]
    bSize = prefix & 0x03
    bType = (prefix >> 2) & 0x03
    bTag = (prefix >> 4) & 0x0F
    if bSize == 3:
        bSize = 4
    if i + 1 + bSize > len(data):
        break

    vb = data[i+1:i+1+bSize]
    if bSize == 0:
        value = 0
    elif bSize == 1:
        value = vb[0]
    elif bSize == 2:
        value = int.from_bytes(vb, 'little')
    else:
        value = int.from_bytes(vb, 'little')

    if bSize == 1:
        value_signed = value - 256 if value >= 128 else value
    elif bSize == 2:
        value_signed = value - 65536 if value >= 32768 else value
    elif bSize == 4:
        value_signed = value - 4294967296 if value >= 2147483648 else value
    else:
        value_signed = 0

    pad = '  ' * indent

    if bType == 1:  # Global
        tname = global_tags.get(bTag, 'Global_%d' % bTag)
        if bTag == 0:
            state['usage_page'] = value
            current_usage_page = value
            print('%s%s: %s (0x%02X)' % (pad, tname, usage_pages.get(value, '?'), value))
        elif bTag == 1:
            state['logical_min'] = value_signed
            print('%s%s: %d' % (pad, tname, value_signed))
        elif bTag == 2:
            lmax = value_signed if value_signed >= state['logical_min'] else value
            state['logical_max'] = lmax
            print('%s%s: %d' % (pad, tname, lmax))
        elif bTag == 3:
            state['physical_min'] = value_signed
            print('%s%s: %d' % (pad, tname, value_signed))
        elif bTag == 4:
            pmax = value_signed if value_signed >= state['physical_min'] else value
            state['physical_max'] = pmax
            print('%s%s: %d' % (pad, tname, pmax))
        elif bTag == 5:
            state['unit_exp'] = value_signed
            print('%s%s: %d' % (pad, tname, value_signed))
        elif bTag == 6:
            state['unit'] = value
            print('%s%s: 0x%04X' % (pad, tname, value))
        elif bTag == 7:
            state['report_size'] = value
            print('%s%s: %d' % (pad, tname, value))
        elif bTag == 8:
            state['report_id'] = value
            print('%s%s: %d' % (pad, tname, value))
        elif bTag == 9:
            state['report_count'] = value
            print('%s%s: %d' % (pad, tname, value))
        else:
            print('%s%s: %d' % (pad, tname, value))
    elif bType == 2:  # Local
        tname = local_tags.get(bTag, 'Local_%d' % bTag)
        if bTag == 0:
            local_state['usages'].append(value)
            print('%s%s: %s' % (pad, tname, get_usage_name(current_usage_page, value)))
        elif bTag == 1:
            local_state['usage_min'] = value
            print('%s%s: %d' % (pad, tname, value))
        elif bTag == 2:
            local_state['usage_max'] = value
            print('%s%s: %d' % (pad, tname, value))
        else:
            print('%s%s: %d' % (pad, tname, value))
    elif bType == 0:  # Main
        tname = main_tags.get(bTag, 'Main_%d' % bTag)
        if bTag == 10:  # Collection
            ct = collection_types.get(value, '?')
            print('%s%s (%s)' % (pad, tname, ct))
            indent += 1
        elif bTag == 12:
            indent = max(0, indent - 1)
            pad = '  ' * indent
            print('%sEnd Collection' % pad)
        elif bTag == 8:  # Input
            flags = []
            if value & 0x01: flags.append('Const')
            else: flags.append('Data')
            if value & 0x02: flags.append('Var')
            else: flags.append('Array')
            if value & 0x04: flags.append('Rel')
            else: flags.append('Abs')
            if value & 0x10: flags.append('NonLin')
            if value & 0x20: flags.append('NoPref')
            if value & 0x40: flags.append('Null')
            fstr = ', '.join(flags)
            print('%sInput (0x%02X) [%s]  -- %d bits x %d' % (pad, value, fstr, state['report_size'], state['report_count']))

            inputs.append({
                'usage_page': state['usage_page'],
                'usages': local_state['usages'][:],
                'usage_min': local_state['usage_min'],
                'usage_max': local_state['usage_max'],
                'logical_min': state['logical_min'],
                'logical_max': state['logical_max'],
                'physical_min': state['physical_min'],
                'physical_max': state['physical_max'],
                'report_size': state['report_size'],
                'report_count': state['report_count'],
                'report_id': state['report_id'],
                'flags': value,
                'flags_str': fstr
            })
            local_state = {'usages': [], 'usage_min': None, 'usage_max': None}
        elif bTag == 9:  # Output
            flags = []
            if value & 0x01: flags.append('Const')
            else: flags.append('Data')
            if value & 0x02: flags.append('Var')
            else: flags.append('Array')
            fstr = ', '.join(flags)
            print('%sOutput (0x%02X) [%s]  -- %d bits x %d' % (pad, value, fstr, state['report_size'], state['report_count']))
            local_state = {'usages': [], 'usage_min': None, 'usage_max': None}
        elif bTag == 11:  # Feature
            flags = []
            if value & 0x01: flags.append('Const')
            else: flags.append('Data')
            if value & 0x02: flags.append('Var')
            else: flags.append('Array')
            fstr = ', '.join(flags)
            print('%sFeature (0x%02X) [%s]' % (pad, value, fstr))
            local_state = {'usages': [], 'usage_min': None, 'usage_max': None}
        else:
            print('%s%s: 0x%02X' % (pad, tname, value))
            local_state = {'usages': [], 'usage_min': None, 'usage_max': None}

    i = i + 1 + bSize

print()
print('=' * 70)
print('INPUT ITEMS DETAIL')
print('=' * 70)

axes = []
buttons_count = 0
hat_count = 0
total_bits = 0

for idx, inp in enumerate(inputs):
    pg = usage_pages.get(inp['usage_page'], '0x%02X' % inp['usage_page'])
    is_const = inp['flags'] & 0x01
    is_var = inp['flags'] & 0x02
    is_null = inp['flags'] & 0x40
    bits = inp['report_size'] * inp['report_count']
    total_bits += bits

    print()
    print('--- Input #%d: [%s] ReportID=%d ---' % (idx+1, inp['flags_str'], inp['report_id']))
    print('  Page: %s | Size: %d bits x %d = %d bits' % (pg, inp['report_size'], inp['report_count'], bits))
    print('  Logical: %d..%d | Physical: %d..%d' % (inp['logical_min'], inp['logical_max'], inp['physical_min'], inp['physical_max']))

    if is_const:
        print('  --> PADDING')
        continue

    if inp['usages']:
        for u in inp['usages']:
            name = get_usage_name(inp['usage_page'], u)
            print('  Usage: %s' % name)

    if inp['usage_min'] is not None:
        print('  Usage Range: %d..%d' % (inp['usage_min'], inp['usage_max']))

    if inp['usage_page'] == 0x09:
        if inp['usage_min'] is not None:
            n = inp['usage_max'] - inp['usage_min'] + 1
            buttons_count += n
            print('  --> %d BUTTONS (%d..%d)' % (n, inp['usage_min'], inp['usage_max']))
    elif inp['usage_page'] == 0x01 and 0x39 in inp.get('usages', []):
        hat_count += inp['report_count']
        print('  --> HAT SWITCH (Null State=%s)' % bool(is_null))
    elif inp['usage_page'] in (0x01, 0x02) and is_var and not is_const:
        for u in inp['usages']:
            name = get_usage_name(inp['usage_page'], u)
            axes.append('%s / %s (%d-bit, %d..%d)' % (pg, name, inp['report_size'], inp['logical_min'], inp['logical_max']))
            print('  --> AXIS: %s' % name)
    elif inp['usage_page'] == 0x0C:
        for u in inp['usages']:
            print('  --> CONSUMER: %s' % get_usage_name(0x0C, u))

print()
print('=' * 70)
print('FINAL SUMMARY')
print('=' * 70)
print()
print('AXES (%d total):' % len(axes))
for ai, a in enumerate(axes):
    print('  %d. %s' % (ai+1, a))
print()
print('BUTTONS: %d' % buttons_count)
print('HAT SWITCHES: %d' % hat_count)
print()
print('Report ID 1 payload: %d bits = %.1f bytes' % (total_bits, total_bits/8))
print()
print('--- joy.cpl visibility ---')
gd_axes = [a for a in axes if 'Generic Desktop' in a]
sim_axes = [a for a in axes if 'Simulation' in a]
print('Generic Desktop axes: %d' % len(gd_axes))
for a in gd_axes:
    print('    %s' % a)
print('Simulation Controls axes: %d' % len(sim_axes))
for a in sim_axes:
    print('    %s' % a)
print()
print('joy.cpl maps BOTH Generic Desktop and Simulation Controls usages to')
print('axis bars. The Simulation page Brake and Accelerator usages are')
print('recognized by DirectInput and joy.cpl as additional axes.')
print()
print('ANSWER: joy.cpl would show %d axes total (not just 4).' % len(axes))
