"""Generate wave 3 — mass VID/PID profiles from all known sources."""
import json, os

PROFILES_DIR = os.path.join(os.path.dirname(os.path.dirname(__file__)), "profiles")

PROFILES = [
    # === More Fanatec ===
    {"dir": "fanatec", "file": "csr-elite.json", "id": "fanatec-csr-elite", "name": "Fanatec CSR Elite Wheelbase", "vid": "0x0EB7", "pid": "0x0011", "vendor": "Fanatec", "ps": "CSR Elite Wheel Base", "ms": "Endor AG", "type": "wheel"},
    {"dir": "fanatec", "file": "csl-elite-pedals-lc.json", "id": "fanatec-csl-elite-pedals-lc", "name": "Fanatec CSL Elite Pedals LC V1", "vid": "0x0EB7", "pid": "0x6204", "vendor": "Fanatec", "ps": "CSL Elite Pedals LC", "ms": "Endor AG", "type": "pedals"},
    {"dir": "fanatec", "file": "csl-elite-pedals-v2.json", "id": "fanatec-csl-elite-pedals-v2", "name": "Fanatec CSL Elite V2 Pedals", "vid": "0x0EB7", "pid": "0x6206", "vendor": "Fanatec", "ps": "CSL Elite Pedals V2", "ms": "Endor AG", "type": "pedals"},
    {"dir": "fanatec", "file": "clubsport-handbrake.json", "id": "fanatec-clubsport-handbrake", "name": "Fanatec ClubSport Handbrake V1.5", "vid": "0x0EB7", "pid": "0x1A93", "vendor": "Fanatec", "ps": "ClubSport Handbrake V1.5", "ms": "Endor AG", "type": "other"},
    # === More MOZA ===
    {"dir": "moza", "file": "moza-r3.json", "id": "moza-r3", "name": "MOZA R3 Racing Wheelbase", "vid": "0x346E", "pid": "0x0005", "vendor": "MOZA", "ps": "MOZA R3", "ms": "Gudsen Technology", "type": "wheel"},
    # === More Asetek ===
    {"dir": "asetek", "file": "asetek-tony-kanaan.json", "id": "asetek-tony-kanaan", "name": "Asetek SimSports Tony Kanaan Wheelbase", "vid": "0x2433", "pid": "0xF306", "vendor": "Asetek", "ps": "Tony Kanaan Edition", "ms": "Asetek SimSports", "type": "wheel"},
    {"dir": "asetek", "file": "asetek-invicta-pedals.json", "id": "asetek-invicta-pedals", "name": "Asetek SimSports Invicta Pedals", "vid": "0x2433", "pid": "0xF100", "vendor": "Asetek", "ps": "Invicta Pedals", "ms": "Asetek SimSports", "type": "pedals"},
    {"dir": "asetek", "file": "asetek-forte-pedals.json", "id": "asetek-forte-pedals", "name": "Asetek SimSports Forte Pedals", "vid": "0x2433", "pid": "0xF101", "vendor": "Asetek", "ps": "Forte Pedals", "ms": "Asetek SimSports", "type": "pedals"},
    # === FFBeast ===
    {"dir": "misc", "file": "ffbeast-wheel.json", "id": "ffbeast-wheel", "name": "FFBeast Force Feedback Wheel", "vid": "0x045B", "pid": "0x59D7", "vendor": "FFBeast", "ps": "FFBeast Wheel", "ms": "FFBeast", "type": "wheel"},
    {"dir": "misc", "file": "ffbeast-joystick.json", "id": "ffbeast-joystick", "name": "FFBeast Force Feedback Joystick", "vid": "0x045B", "pid": "0x58F9", "vendor": "FFBeast", "ps": "FFBeast Joystick", "ms": "FFBeast", "type": "joystick"},
    {"dir": "misc", "file": "ffbeast-rudder.json", "id": "ffbeast-rudder", "name": "FFBeast Force Feedback Rudder", "vid": "0x045B", "pid": "0x5968", "vendor": "FFBeast", "ps": "FFBeast Rudder", "ms": "FFBeast", "type": "pedals"},
    # === More Heusinkveld ===
    {"dir": "heusinkveld", "file": "handbrake-v2.json", "id": "heusinkveld-handbrake-v2", "name": "Heusinkveld Handbrake V2", "vid": "0x30B7", "pid": "0x1002", "vendor": "Heusinkveld", "ps": "Handbrake V2", "ms": "Heusinkveld Engineering", "type": "other"},
    {"dir": "heusinkveld", "file": "sequential-shifter.json", "id": "heusinkveld-sequential-shifter", "name": "Heusinkveld Sequential Shifter", "vid": "0xA020", "pid": "0x3142", "vendor": "Heusinkveld", "ps": "Sequential Shifter", "ms": "Heusinkveld Engineering", "type": "other"},
    # === VRS DirectForce ===
    {"dir": "misc", "file": "vrs-dfp-wheel.json", "id": "vrs-dfp-wheel", "name": "VRS DirectForce Pro Wheelbase", "vid": "0x0483", "pid": "0xA355", "vendor": "VRS", "ps": "DirectForce Pro", "ms": "Virtual Racing School", "type": "wheel"},
    {"dir": "misc", "file": "vrs-dfp-pedals.json", "id": "vrs-dfp-pedals", "name": "VRS DirectForce Pro Pedals", "vid": "0x0483", "pid": "0xA3BE", "vendor": "VRS", "ps": "DirectForce Pro Pedals", "ms": "Virtual Racing School", "type": "pedals"},
    # === Simucube extras ===
    {"dir": "simucube", "file": "simucube-1.json", "id": "simucube-1", "name": "Simucube 1", "vid": "0x16D0", "pid": "0x0D5A", "vendor": "Granite Devices", "ps": "Simucube", "ms": "Granite Devices", "type": "wheel"},
    # === Simagic extras ===
    {"dir": "simagic", "file": "simagic-gt-neo.json", "id": "simagic-gt-neo", "name": "Simagic GT Neo / EVO", "vid": "0x3670", "pid": "0x0500", "vendor": "Simagic", "ps": "GT Neo", "ms": "Simagic", "type": "wheel"},
    # === More PXN ===
    {"dir": "pxn", "file": "pxn-v12-lite.json", "id": "pxn-v12-lite", "name": "PXN V12 Lite Racing Wheel", "vid": "0x11FF", "pid": "0x1112", "vendor": "PXN", "ps": "PXN V12 Lite", "ms": "PXN", "type": "wheel"},
    {"dir": "pxn", "file": "litestar-gt987.json", "id": "litestar-gt987", "name": "Lite Star GT987 Racing Wheel", "vid": "0x11FF", "pid": "0x2141", "vendor": "PXN/Lite Star", "ps": "GT987", "ms": "Lite Star", "type": "wheel"},
    # === Simnet/SimJack/etc. ===
    {"dir": "misc", "file": "oddor-handbrake.json", "id": "oddor-handbrake", "name": "Oddor USB Handbrake", "vid": "0x1021", "pid": "0x1888", "vendor": "Oddor", "ps": "Oddor Handbrake", "ms": "Oddor", "type": "other"},
    {"dir": "misc", "file": "leo-bodnar-pedals.json", "id": "leo-bodnar-pedals", "name": "Leo Bodnar SLI-M Pedals", "vid": "0x1DD2", "pid": "0x100C", "vendor": "Leo Bodnar", "ps": "SLI-M", "ms": "Leo Bodnar Electronics", "type": "pedals"},
    {"dir": "misc", "file": "mmos-ffb.json", "id": "mmos-ffb", "name": "MMOS Force Feedback Wheel", "vid": "0xF055", "pid": "0x0FFB", "vendor": "MMOS", "ps": "MMOS FFB Wheel", "ms": "MMOS", "type": "wheel"},
    # === Thrustmaster extras ===
    {"dir": "thrustmaster", "file": "t500rs.json", "id": "thrustmaster-t500rs", "name": "Thrustmaster T500 RS Racing Wheel", "vid": "0x044F", "pid": "0xB65E", "vendor": "Thrustmaster", "ps": "Thrustmaster T500 RS", "ms": "Thrustmaster", "type": "wheel"},
    {"dir": "thrustmaster", "file": "t80.json", "id": "thrustmaster-t80", "name": "Thrustmaster T80 Racing Wheel", "vid": "0x044F", "pid": "0xB668", "vendor": "Thrustmaster", "ps": "T80 Racing Wheel", "ms": "Thrustmaster", "type": "wheel"},
    # === Logitech extras ===
    {"dir": "logitech", "file": "g-pro-ps.json", "id": "logitech-g-pro-ps", "name": "Logitech G PRO Racing Wheel (PlayStation)", "vid": "0x046D", "pid": "0xC268", "vendor": "Logitech", "ps": "G PRO Racing Wheel", "ms": "Logitech", "type": "wheel"},
    {"dir": "logitech", "file": "g-pro-xbox.json", "id": "logitech-g-pro-xbox", "name": "Logitech G PRO Racing Wheel (Xbox)", "vid": "0x046D", "pid": "0xC272", "vendor": "Logitech", "ps": "G PRO Racing Wheel", "ms": "Logitech", "type": "wheel"},
    # === Switch 2 (from SDL) ===
    {"dir": "nintendo", "file": "switch2-pro.json", "id": "switch2-pro-controller", "name": "Nintendo Switch 2 Pro Controller", "vid": "0x057E", "pid": "0x2069", "vendor": "Nintendo", "ps": "Pro Controller", "ms": "Nintendo Co., Ltd.", "type": "gamepad"},
    {"dir": "nintendo", "file": "switch2-joycon-l.json", "id": "switch2-joycon-l", "name": "Nintendo Switch 2 Joy-Con (L)", "vid": "0x057E", "pid": "0x2067", "vendor": "Nintendo", "ps": "Joy-Con (L)", "ms": "Nintendo Co., Ltd.", "type": "gamepad"},
    {"dir": "nintendo", "file": "switch2-joycon-r.json", "id": "switch2-joycon-r", "name": "Nintendo Switch 2 Joy-Con (R)", "vid": "0x057E", "pid": "0x2066", "vendor": "Nintendo", "ps": "Joy-Con (R)", "ms": "Nintendo Co., Ltd.", "type": "gamepad"},
]

def main():
    created = 0
    for entry in PROFILES:
        dir_path = os.path.join(PROFILES_DIR, entry["dir"])
        os.makedirs(dir_path, exist_ok=True)
        file_path = os.path.join(dir_path, entry["file"])
        data = {
            "id": entry["id"], "name": entry["name"], "vendor": entry["vendor"],
            "vid": entry["vid"], "pid": entry["pid"],
            "productString": entry["ps"], "manufacturerString": entry["ms"],
            "type": entry["type"], "connection": "usb",
            "descriptor": None, "inputReportSize": None,
            "notes": "VID/PID confirmed. Descriptor requires physical hardware capture."
        }
        with open(file_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
            f.write("\n")
        created += 1
    print(f"Created {created} profile files.")

    total = 0; with_desc = 0
    for root, dirs, files in os.walk(PROFILES_DIR):
        for fn in files:
            if fn.endswith(".json") and fn not in ("schema.json","scraped_descriptors.json","linux-kernel-fixed-descriptors.json"):
                total += 1
                with open(os.path.join(root, fn)) as f:
                    try:
                        d = json.load(f)
                        if d.get("descriptor"): with_desc += 1
                    except: pass
    print(f"Total profiles: {total} ({with_desc} with descriptors)")

if __name__ == "__main__":
    main()
