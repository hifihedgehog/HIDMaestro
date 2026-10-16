# Writes reference_lengths.json: the input, output and feature report
# lengths of every catalog report descriptor, as hid-tools reads them.
#
# hid-tools (gitlab.freedesktop.org/libevdev/hid-tools) is a HID report
# descriptor parser independent of HIDMaestro. Its ReportDescriptor
# (hidtools/hid.py) keeps the global item state on a stack for Push and Pop
# and counts each report's bits per report ID. A length here is the Linux
# kernel's hid_report_len (include/linux/hid.h): the data bits rounded up to
# whole bytes, plus one byte for the report ID when the report has one.
#
# hid-tools keeps the report ID with the local items, so its Pop does not
# restore one set after the Push, where hid-core.c (hid_parser_global) and
# HID 1.11 section 6.2.2.7 do. A descriptor that sets a report ID between a
# Push and its Pop is refused here rather than measured wrongly. So is one
# that hid-tools cannot parse.
#
# Usage: python -I make_reference.py <hid-tools clone> <repository root>
# hid-tools needs the "parse" package. The output replaces the file beside
# this script.

import hashlib
import json
import pathlib
import subprocess
import sys


def sources(profile):
    """The report descriptors the SDK parses: the profile's own, and each
    HID interface's in a USB configuration."""
    if profile.get("descriptor"):
        yield "descriptor", profile["descriptor"]
    for iface in (profile.get("usbConfiguration") or {}).get("interfaces") or []:
        for alt in iface.get("altSettings") or []:
            if alt.get("reportDescriptor"):
                yield f"interface{iface.get('interfaceNumber', 0)}", alt["reportDescriptor"]


def report_id_inside_push(d):
    """True when a Report ID item sits between a Push and its Pop."""
    i, depth = 0, 0
    while i < len(d):
        b = d[i]
        if b == 0xFE:
            i += 3 + (d[i + 1] if i + 1 < len(d) else 0)
            continue
        if b == 0xA4:
            depth += 1
        elif b == 0xB4:
            depth -= 1
        elif (b & 0xFC) == 0x84 and depth > 0:
            return True
        i += 1 + (0, 1, 2, 4)[b & 3]
    return False


def lengths(rd):
    out = {}
    for kind, reports in (("input", rd.input_reports),
                          ("output", rd.output_reports),
                          ("feature", rd.feature_reports)):
        k = {}
        for rid, rep in reports.items():
            numbered = rep.numbered
            data_bits = rep.bitsize - (8 if numbered else 0)
            k[str(rid if numbered else 0)] = (data_bits + 7) // 8 + (1 if numbered else 0)
        out[kind] = dict(sorted(k.items(), key=lambda kv: int(kv[0])))
    return out


def main():
    hid_tools, root = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
    sys.path.insert(0, str(hid_tools))
    from hidtools.hid import ReportDescriptor

    commit = subprocess.run(["git", "-C", str(hid_tools), "rev-parse", "--short", "HEAD"],
                            capture_output=True, text=True, check=True).stdout.strip()
    entries, refused = [], []
    for path in sorted((root / "profiles").rglob("*.json")):
        try:
            profile = json.loads(path.read_text(encoding="utf-8-sig"))
        except ValueError:
            continue
        if not isinstance(profile, dict) or "id" not in profile:
            continue
        for source, hexstr in sources(profile):
            d = bytes.fromhex(hexstr)
            name = f"{profile['id']} {source}"
            if report_id_inside_push(d):
                refused.append(f"{name}: a report ID set inside Push and Pop")
                continue
            try:
                rd = ReportDescriptor.from_bytes(list(d))
            except Exception as ex:
                refused.append(f"{name}: {type(ex).__name__}: {ex}")
                continue
            entry = {"profile": profile["id"], "source": source,
                     "sha256": hashlib.sha256(d).hexdigest().upper()}
            entry.update(lengths(rd))
            entries.append(entry)
    if refused:
        print("refused:\n  " + "\n  ".join(refused))
        return 1
    entries.sort(key=lambda e: (e["profile"], e["source"]))
    # One descriptor per line, so a change to one profile is one line.
    lines = ",\n".join("  " + json.dumps(e) for e in entries)
    text = '{\n "hidTools": "' + commit + '",\n "descriptors": [\n' + lines + "\n ]\n}\n"
    json.loads(text)
    target = pathlib.Path(__file__).with_name("reference_lengths.json")
    target.write_bytes(text.encode("utf-8"))
    print(f"{len(entries)} descriptors from hid-tools {commit}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
