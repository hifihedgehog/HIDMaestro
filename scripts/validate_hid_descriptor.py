"""HID Report Descriptor validator.

Parses a descriptor hex string per USB HID 1.11 section 6.2.2 and verifies:
  - Even length; all hex chars valid
  - Short/long item headers consume exactly their declared payload
  - Balanced Collection (0xA0 prefix tag) and End Collection (0xC0 prefix tag)
  - At least one top-level Application Collection (A1 01)
  - Sensible Report Size * Report Count totals (no absurdly large reports)
  - Optional: derives Input report size in bytes per report ID

Exit code 0 if all checks pass, 1 otherwise. Prints a one-line summary per
descriptor, then a per-issue detail list if any issues were found.

Usage:
  python scripts/validate_hid_descriptor.py <hex-string>
  python scripts/validate_hid_descriptor.py --json <path-to-profile.json>
  python scripts/validate_hid_descriptor.py --batch <json-file-with-id-to-descriptor-map>

The batch JSON is the output of the scraping agents:
  {"profile-id": {"descriptor": "0501...", "inputReportSize": 64, ...}}
"""
import json
import sys
import argparse
import os
from dataclasses import dataclass, field
from typing import List, Optional, Dict

# HID item main tags (after >> 4 & 0xF on the header byte, with type=0=Main)
MAIN_INPUT = 0x8
MAIN_OUTPUT = 0x9
MAIN_FEATURE = 0xB
MAIN_COLLECTION = 0xA
MAIN_END_COLLECTION = 0xC

# Global item tags
GLOBAL_USAGE_PAGE = 0x0
GLOBAL_LOGICAL_MIN = 0x1
GLOBAL_LOGICAL_MAX = 0x2
GLOBAL_PHYSICAL_MIN = 0x3
GLOBAL_PHYSICAL_MAX = 0x4
GLOBAL_UNIT_EXPONENT = 0x5
GLOBAL_UNIT = 0x6
GLOBAL_REPORT_SIZE = 0x7
GLOBAL_REPORT_ID = 0x8
GLOBAL_REPORT_COUNT = 0x9
GLOBAL_PUSH = 0xA
GLOBAL_POP = 0xB

# Local item tags
LOCAL_USAGE = 0x0
LOCAL_USAGE_MIN = 0x1
LOCAL_USAGE_MAX = 0x2


@dataclass
class ValidationResult:
    ok: bool
    errors: List[str] = field(default_factory=list)
    warnings: List[str] = field(default_factory=list)
    byte_count: int = 0
    collections_opened: int = 0
    collections_closed: int = 0
    top_level_app_collections: int = 0
    input_report_bits_by_id: Dict[int, int] = field(default_factory=dict)
    output_report_bits_by_id: Dict[int, int] = field(default_factory=dict)
    feature_report_bits_by_id: Dict[int, int] = field(default_factory=dict)
    declared_report_size: int = 0
    declared_report_count: int = 0
    summary: str = ""


def hex_to_bytes(hex_str: str) -> Optional[bytes]:
    s = ''.join(c for c in hex_str if not c.isspace())
    if len(s) % 2 != 0:
        return None
    try:
        return bytes.fromhex(s)
    except ValueError:
        return None


def validate(hex_str: str) -> ValidationResult:
    r = ValidationResult(ok=True)
    data = hex_to_bytes(hex_str)
    if data is None:
        r.ok = False
        r.errors.append(f'descriptor is not valid hex (len={len(hex_str)})')
        return r

    r.byte_count = len(data)
    if r.byte_count < 10:
        r.ok = False
        r.errors.append(f'descriptor too short ({r.byte_count} bytes; minimum plausible ~30)')
        return r

    # Walk items
    i = 0
    current_report_size = 0
    current_report_count = 0
    current_report_id = 0
    n = len(data)
    while i < n:
        b0 = data[i]
        if b0 == 0xFE:  # long item
            if i + 2 >= n:
                r.ok = False
                r.errors.append(f'long item header at offset {i} truncated')
                return r
            size = data[i + 1]
            if i + 3 + size > n:
                r.ok = False
                r.errors.append(f'long item at offset {i} claims size {size} but descriptor ends')
                return r
            i += 3 + size
            continue

        bSize = b0 & 0x3
        payload = 1 if bSize == 1 else 2 if bSize == 2 else 4 if bSize == 3 else 0
        bType = (b0 >> 2) & 0x3
        bTag = (b0 >> 4) & 0xF

        if i + 1 + payload > n:
            r.ok = False
            r.errors.append(f'item at offset {i} (tag 0x{bTag:X}, type {bType}, size {payload}) truncated; descriptor ends at {n}')
            return r

        val = 0
        for j in range(payload):
            val |= data[i + 1 + j] << (8 * j)

        if bType == 0:  # Main
            if bTag == MAIN_COLLECTION:
                r.collections_opened += 1
                if val == 0x01:  # Application
                    r.top_level_app_collections += 1
            elif bTag == MAIN_END_COLLECTION:
                r.collections_closed += 1
            elif bTag == MAIN_INPUT:
                bits = current_report_size * current_report_count
                r.input_report_bits_by_id[current_report_id] = r.input_report_bits_by_id.get(current_report_id, 0) + bits
            elif bTag == MAIN_OUTPUT:
                bits = current_report_size * current_report_count
                r.output_report_bits_by_id[current_report_id] = r.output_report_bits_by_id.get(current_report_id, 0) + bits
            elif bTag == MAIN_FEATURE:
                bits = current_report_size * current_report_count
                r.feature_report_bits_by_id[current_report_id] = r.feature_report_bits_by_id.get(current_report_id, 0) + bits
        elif bType == 1:  # Global
            if bTag == GLOBAL_REPORT_SIZE:
                current_report_size = val
                r.declared_report_size = val
                if val > 64:
                    r.warnings.append(f'Report Size {val} bits is unusually large (>64)')
            elif bTag == GLOBAL_REPORT_COUNT:
                current_report_count = val
                r.declared_report_count = val
                if val > 256:
                    r.warnings.append(f'Report Count {val} is unusually large (>256)')
            elif bTag == GLOBAL_REPORT_ID:
                current_report_id = val

        i += 1 + payload

    # Balance check
    if r.collections_opened != r.collections_closed:
        r.ok = False
        r.errors.append(f'unbalanced collections: {r.collections_opened} opens vs {r.collections_closed} closes')

    if r.top_level_app_collections < 1:
        r.ok = False
        r.errors.append('no top-level Application Collection (A1 01) found')

    # Input report size sanity
    total_input_bits = sum(r.input_report_bits_by_id.values())
    if total_input_bits == 0:
        r.warnings.append('no Input items found (descriptor may be output-only which is unusual for a game controller)')
    elif total_input_bits % 8 != 0:
        r.warnings.append(f'total Input bits ({total_input_bits}) not byte-aligned; descriptor may use implicit padding')

    # Build summary
    input_byte_total = total_input_bits // 8
    report_id_count = len(r.input_report_bits_by_id)
    if report_id_count == 1 and 0 in r.input_report_bits_by_id:
        id_note = 'no report ID'
    elif 0 in r.input_report_bits_by_id:
        id_note = f'{report_id_count} IDs incl. implicit 0'
    else:
        id_note = f'{report_id_count} report IDs'
    r.summary = f'{r.byte_count}B, {r.top_level_app_collections} app-coll, in={input_byte_total}B ({id_note})'

    return r


def validate_profile_file(path: str) -> ValidationResult:
    with open(path) as f:
        p = json.load(f)
    desc = p.get('descriptor')
    if not desc:
        r = ValidationResult(ok=False)
        r.errors.append(f'{path}: profile has no descriptor field')
        return r
    return validate(desc)


def validate_batch(path: str) -> Dict[str, dict]:
    with open(path) as f:
        data = json.load(f)
    results = {}
    for pid, entry in data.items():
        if entry.get('status') == 'no_source':
            results[pid] = {'ok': None, 'status': 'no_source', 'reason': entry.get('reason', '')}
            continue
        desc = entry.get('descriptor')
        if not desc:
            results[pid] = {'ok': False, 'errors': ['missing descriptor field'], 'warnings': [], 'summary': ''}
            continue
        r = validate(desc)
        results[pid] = {
            'ok': r.ok,
            'errors': r.errors,
            'warnings': r.warnings,
            'summary': r.summary,
            'source': entry.get('source', ''),
            'confidence': entry.get('confidence', ''),
            'declared_inputReportSize': entry.get('inputReportSize'),
            'derived_input_byte_total': sum(r.input_report_bits_by_id.values()) // 8,
        }
    return results


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--json', help='Path to profile .json to validate its descriptor field')
    ap.add_argument('--batch', help='Path to {id: {descriptor: ...}} JSON (agent output)')
    ap.add_argument('hex', nargs='?', help='Raw hex string to validate')
    args = ap.parse_args()

    if args.batch:
        results = validate_batch(args.batch)
        ok_count = sum(1 for v in results.values() if v.get('ok') is True)
        fail_count = sum(1 for v in results.values() if v.get('ok') is False)
        skip_count = sum(1 for v in results.values() if v.get('ok') is None)
        print(f'Batch: {ok_count} pass, {fail_count} fail, {skip_count} no_source (of {len(results)})')
        print()
        for pid, r in sorted(results.items()):
            if r.get('status') == 'no_source':
                print(f'  [SKIP] {pid:50s} — {r["reason"][:80]}')
                continue
            tag = '[PASS]' if r['ok'] else '[FAIL]'
            print(f'  {tag} {pid:50s} {r["summary"]}  conf={r.get("confidence","?"):6s}')
            for e in r.get('errors', []):
                print(f'         ERROR: {e}')
            for w in r.get('warnings', []):
                print(f'         WARN:  {w}')
        sys.exit(0 if fail_count == 0 else 1)
    elif args.json:
        r = validate_profile_file(args.json)
        print(f'{"PASS" if r.ok else "FAIL"}: {args.json} — {r.summary}')
        for e in r.errors:
            print(f'  ERROR: {e}')
        for w in r.warnings:
            print(f'  WARN:  {w}')
        sys.exit(0 if r.ok else 1)
    elif args.hex:
        r = validate(args.hex)
        print(f'{"PASS" if r.ok else "FAIL"}: {r.summary}')
        for e in r.errors:
            print(f'  ERROR: {e}')
        for w in r.warnings:
            print(f'  WARN:  {w}')
        sys.exit(0 if r.ok else 1)
    else:
        ap.print_help()
        sys.exit(2)


if __name__ == '__main__':
    main()
