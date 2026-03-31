"""Generate flight sim controller profiles from research."""
import json, os

PROFILES_DIR = os.path.join(os.path.dirname(os.path.dirname(__file__)), "profiles")

PROFILES = [
    # === VKBSim (VID: 0x231D) ===
    {"dir": "vkbsim", "id": "vkbsim-gladiator-evo-r", "name": "VKBsim Gladiator NXT/EVO (R)", "vid": "0x231D", "pid": "0x0200", "vendor": "VKBSim", "ps": "VKBsim Gladiator NXT/EVO R", "ms": "VKB-Sim", "type": "joystick"},
    {"dir": "vkbsim", "id": "vkbsim-gladiator-evo-l", "name": "VKBsim Gladiator NXT/EVO (L)", "vid": "0x231D", "pid": "0x0201", "vendor": "VKBSim", "ps": "VKBsim Gladiator NXT/EVO L", "ms": "VKB-Sim", "type": "joystick"},
    {"dir": "vkbsim", "id": "vkbsim-gunfighter-sce-r", "name": "VKBsim Gunfighter Mk.III SCE (R)", "vid": "0x231D", "pid": "0x0126", "vendor": "VKBSim", "ps": "Gunfighter Mk.III 'Space Combat Edition' (R)", "ms": "VKB-Sim", "type": "joystick"},
    {"dir": "vkbsim", "id": "vkbsim-gunfighter-sce-l", "name": "VKBsim Gunfighter Mk.III SCE (L)", "vid": "0x231D", "pid": "0x0127", "vendor": "VKBSim", "ps": "Gunfighter Mk.III 'Space Combat Edition' (L)", "ms": "VKB-Sim", "type": "joystick"},
    {"dir": "vkbsim", "id": "vkbsim-gunfighter", "name": "VKBsim Gunfighter (R)", "vid": "0x231D", "pid": "0x0125", "vendor": "VKBSim", "ps": "VKBsim Gunfighter (R)", "ms": "VKB-Sim", "type": "joystick"},
    {"dir": "vkbsim", "id": "vkbsim-gladiator-k-r", "name": "VKBsim Gladiator K (R)", "vid": "0x231D", "pid": "0x0132", "vendor": "VKBSim", "ps": "VKBsim Gladiator K (R)", "ms": "VKB-Sim", "type": "joystick"},
    {"dir": "vkbsim", "id": "vkbsim-gladiator-k-l", "name": "VKBsim Gladiator K (L)", "vid": "0x231D", "pid": "0x0133", "vendor": "VKBSim", "ps": "VKBsim Gladiator K (L)", "ms": "VKB-Sim", "type": "joystick"},
    {"dir": "vkbsim", "id": "vkbsim-gladiator-mcp", "name": "VKBsim Gladiator Modern Combat Pro", "vid": "0x231D", "pid": "0x0131", "vendor": "VKBSim", "ps": "VKBsim Gladiator Modern Combat Pro", "ms": "VKB-Sim", "type": "joystick"},
    {"dir": "vkbsim", "id": "vkbsim-stecs", "name": "VKBsim STECS Throttle", "vid": "0x231D", "pid": "0x012C", "vendor": "VKBSim", "ps": "VKBsim STECS", "ms": "VKB-Sim", "type": "other"},
    {"dir": "vkbsim", "id": "vkbsim-sem-thq", "name": "VKBsim NXT SEM THQ Module", "vid": "0x231D", "pid": "0x2214", "vendor": "VKBSim", "ps": "VKBSim NXT SEM THQ", "ms": "VKB-Sim", "type": "other"},
    # === Virpil (VID: 0x3344) ===
    {"dir": "virpil", "id": "virpil-mongoost-50cm3-throttle", "name": "Virpil VPC MongoosT-50CM3 Throttle", "vid": "0x3344", "pid": "0x0194", "vendor": "Virpil", "ps": "L-VPC MongoosT-50CM3 Throttle", "ms": "VIRPIL Controls", "type": "other"},
    {"dir": "virpil", "id": "virpil-constellation-alpha-cm3", "name": "Virpil VPC Constellation Alpha (CM3 base)", "vid": "0x3344", "pid": "0x0391", "vendor": "Virpil", "ps": "VPC Stick MT-50CM3", "ms": "VIRPIL Controls", "type": "joystick"},
    {"dir": "virpil", "id": "virpil-constellation-alpha-cm2", "name": "Virpil VPC Constellation Alpha (CM2 base)", "vid": "0x3344", "pid": "0x412F", "vendor": "Virpil", "ps": "R-VPC Stick MT-50CM2", "ms": "VIRPIL Controls", "type": "joystick"},
    {"dir": "virpil", "id": "virpil-constellation-alpha-warbrd", "name": "Virpil VPC Constellation Alpha (WarBRD-D base)", "vid": "0x3344", "pid": "0x43F5", "vendor": "Virpil", "ps": "R-VPC Stick WarBRD-D", "ms": "VIRPIL Controls", "type": "joystick"},
    {"dir": "virpil", "id": "virpil-control-panel-1", "name": "Virpil VPC Control Panel #1", "vid": "0x3344", "pid": "0x0259", "vendor": "Virpil", "ps": "VPC Control Panel #1", "ms": "VIRPIL Controls", "type": "other"},
    {"dir": "virpil", "id": "virpil-control-panel-2", "name": "Virpil VPC Control Panel #2", "vid": "0x3344", "pid": "0x025B", "vendor": "Virpil", "ps": "VPC Control Panel #2", "ms": "VIRPIL Controls", "type": "other"},
    {"dir": "virpil", "id": "virpil-mongoost-50cm2-throttle", "name": "Virpil VPC MongoosT-50CM2 Throttle", "vid": "0x3344", "pid": "0x8193", "vendor": "Virpil", "ps": "VPC MongoosT-50CM2 Throttle", "ms": "VIRPIL Controls", "type": "other"},
    # === Winwing (VID: 0x4098) ===
    {"dir": "winwing", "id": "winwing-orion2-f18", "name": "Winwing Orion Throttle Base II + F18 Handle", "vid": "0x4098", "pid": "0xBE62", "vendor": "Winwing", "ps": "Orion Throttle Base II + F18 HANDLE", "ms": "Winwing", "type": "other"},
    {"dir": "winwing", "id": "winwing-orion2-f16ex", "name": "Winwing Orion Throttle Base II + F16EX Handle", "vid": "0x4098", "pid": "0xBE68", "vendor": "Winwing", "ps": "Orion Throttle Base II + F16EX", "ms": "Winwing", "type": "other"},
    {"dir": "winwing", "id": "winwing-orion2-joystick", "name": "Winwing Orion 2 Joystick Base", "vid": "0x4098", "pid": "0xBEA1", "vendor": "Winwing", "ps": "Orion 2 Joystick Base", "ms": "Winwing", "type": "joystick"},
    {"dir": "winwing", "id": "winwing-ursa-minor", "name": "Winwing Ursa Minor Fighter Stick (R)", "vid": "0x4098", "pid": "0xBC2A", "vendor": "Winwing", "ps": "Ursa Minor Fighter Stick R", "ms": "Winwing", "type": "joystick"},
    {"dir": "winwing", "id": "winwing-skywalker-pedals", "name": "Winwing Skywalker Metal Rudder Pedals", "vid": "0x4098", "pid": "0xBEF0", "vendor": "Winwing", "ps": "SKYWALKER Metal Rudder Pedals", "ms": "Winwing", "type": "pedals"},
    # === CH Products (VID: 0x068E) ===
    {"dir": "ch-products", "id": "ch-fighterstick", "name": "CH Products Fighterstick", "vid": "0x068E", "pid": "0x00F3", "vendor": "CH Products", "ps": "Fighterstick", "ms": "CH Products, Inc.", "type": "joystick"},
    {"dir": "ch-products", "id": "ch-pro-throttle", "name": "CH Products Pro Throttle", "vid": "0x068E", "pid": "0x00F1", "vendor": "CH Products", "ps": "Pro Throttle", "ms": "CH Products, Inc.", "type": "other"},
    {"dir": "ch-products", "id": "ch-pro-pedals", "name": "CH Products Pro Pedals", "vid": "0x068E", "pid": "0x0501", "vendor": "CH Products", "ps": "CH Pro Pedals", "ms": "CH Products, Inc.", "type": "pedals"},
    {"dir": "ch-products", "id": "ch-combatstick", "name": "CH Products Combatstick", "vid": "0x068E", "pid": "0x00F4", "vendor": "CH Products", "ps": "Combatstick", "ms": "CH Products, Inc.", "type": "joystick"},
    {"dir": "ch-products", "id": "ch-throttle-quadrant", "name": "CH Products Throttle Quadrant", "vid": "0x068E", "pid": "0x00FA", "vendor": "CH Products", "ps": "Ch Throttle Quadrant", "ms": "CH Products, Inc.", "type": "other"},
    {"dir": "ch-products", "id": "ch-flight-sim-yoke", "name": "CH Products Flight Sim Yoke", "vid": "0x068E", "pid": "0x00FF", "vendor": "CH Products", "ps": "Flight Sim Yoke", "ms": "CH Products, Inc.", "type": "other"},
    {"dir": "ch-products", "id": "ch-f16-combat-stick", "name": "CH Products F-16 Combat Stick", "vid": "0x068E", "pid": "0x0504", "vendor": "CH Products", "ps": "F-16 Combat Stick", "ms": "CH Products, Inc.", "type": "joystick"},
    # === Turtle Beach / VelocityOne (VID: 0x10F5) ===
    {"dir": "turtle-beach", "id": "velocityone-flightstick", "name": "Turtle Beach VelocityOne Flightstick", "vid": "0x10F5", "pid": "0x7084", "vendor": "Turtle Beach", "ps": "VelocityOne Flightstick", "ms": "Voyetra Turtle Beach, Inc.", "type": "flightstick"},
    {"dir": "turtle-beach", "id": "velocityone-throttle", "name": "Turtle Beach VelocityOne Throttle", "vid": "0x10F5", "pid": "0x7085", "vendor": "Turtle Beach", "ps": "VelocityOne Throttle", "ms": "Voyetra Turtle Beach, Inc.", "type": "other"},
    {"dir": "turtle-beach", "id": "velocityone-race", "name": "Turtle Beach VelocityOne Race (Steering Wheel)", "vid": "0x10F5", "pid": "0x7077", "vendor": "Turtle Beach", "ps": "VelocityOne Race", "ms": "Voyetra Turtle Beach, Inc.", "type": "wheel"},
    # === Honeycomb (VID: 0x294B) ===
    {"dir": "honeycomb", "id": "honeycomb-alpha", "name": "Honeycomb Alpha Flight Controls (Yoke)", "vid": "0x294B", "pid": "0x1900", "vendor": "Honeycomb", "ps": "Alpha Flight Controls", "ms": "Honeycomb Aeronautical", "type": "other"},
    {"dir": "honeycomb", "id": "honeycomb-bravo", "name": "Honeycomb Bravo Throttle Quadrant", "vid": "0x294B", "pid": "0x1901", "vendor": "Honeycomb", "ps": "Bravo Throttle Quadrant", "ms": "Honeycomb Aeronautical", "type": "other"},
]

def main():
    created = 0
    for entry in PROFILES:
        dir_path = os.path.join(PROFILES_DIR, entry["dir"])
        os.makedirs(dir_path, exist_ok=True)
        fn = entry["id"].replace("vkbsim-","").replace("virpil-","").replace("winwing-","").replace("ch-","").replace("velocityone-","").replace("honeycomb-","")
        file_path = os.path.join(dir_path, fn + ".json")
        data = {
            "id": entry["id"], "name": entry["name"], "vendor": entry["vendor"],
            "vid": entry["vid"], "pid": entry["pid"],
            "productString": entry["ps"], "manufacturerString": entry["ms"],
            "type": entry["type"], "connection": "usb",
            "descriptor": None, "inputReportSize": None,
            "notes": "VID/PID confirmed from Linux USB database / kernel sources / SDL. Descriptor requires physical hardware capture."
        }
        with open(file_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
            f.write("\n")
        created += 1
    print(f"Created {created} flight sim profile files.")

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
