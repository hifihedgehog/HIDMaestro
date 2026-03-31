"""Generate wave 2 profiles from all remaining scraped data."""
import json, os

PROFILES_DIR = os.path.join(os.path.dirname(os.path.dirname(__file__)), "profiles")

PROFILES = [
    # === GP2040-CE retro mini consoles ===
    {"dir": "sega", "file": "astro-city-mini.json", "data": {
        "id": "sega-astro-city-mini", "name": "Sega Astro City Mini Controller", "vendor": "Sega",
        "vid": "0x0CA3", "pid": "0x0027", "productString": "6B Controller",
        "manufacturerString": "Sega", "type": "gamepad", "connection": "usb",
        "descriptor": "05010904a101a10275089505150026ff00350046ff00093009300930093009318102750495012507463b0165140900814265007501950a2501450105091901290a81020600ff7501950a2501450109018102c0a1027508950446ff0026ff0009029102c0c0",
        "inputReportSize": None, "notes": "101-byte descriptor. Source: GP2040-CE AstroDescriptors.h. Sega Astro City Mini 6-button controller."
    }},
    {"dir": "taito", "file": "egret-ii-mini.json", "data": {
        "id": "taito-egret-ii-mini", "name": "Taito Egret II Mini Controller", "vendor": "Taito",
        "vid": "0x0AE4", "pid": "0x0702", "productString": "TAITO USB Control Pad",
        "manufacturerString": "Taito", "type": "gamepad", "connection": "usb",
        "descriptor": "05010904a10115002501350045017501950c05091901290c810295048101050126ff0046ff0009300931750895028102c0",
        "inputReportSize": None, "notes": "49-byte descriptor. Source: GP2040-CE EgretDescriptors.h. 12 buttons, 2 axes."
    }},
    {"dir": "sega", "file": "mega-drive-mini.json", "data": {
        "id": "sega-mega-drive-mini", "name": "Sega Mega Drive Mini Gamepad", "vendor": "Sega",
        "vid": "0x0CA3", "pid": "0x0024", "productString": "MEGA DRIVE mini GAMEPAD",
        "manufacturerString": "SEGA CORP.", "type": "gamepad", "connection": "usb",
        "descriptor": "05010904a101a10275089505150026ff00350046ff00093009300930093009318102750495012507463b0165140900814265007501950a2501450105091901290a81020600ff7501950a2501450109018102c0a1027508950446ff0026ff0009029102c0c0",
        "inputReportSize": None, "notes": "101-byte descriptor. Source: GP2040-CE MDMiniDescriptors.h. Identical to Astro City Mini descriptor."
    }},
    {"dir": "snk", "file": "neogeo-mini.json", "data": {
        "id": "snk-neogeo-mini", "name": "SNK Neo Geo Mini Controller", "vendor": "SNK",
        "vid": "0x20BC", "pid": "0x5500", "productString": "Neo Geo Mini Pad",
        "manufacturerString": "SNK", "type": "gamepad", "connection": "usb",
        "descriptor": "05010905a10115002501350045017501950f05091901290f81029501810105012507463b017504950165140939814265009501810126ff0046ff0009300931093209357508950481020502150026ff0009c509c49502750881020600ff26ff0346ff0309200921092209230924092575109506810205080943150026ff00350046ff00750895019182094491820945918209469182c0",
        "inputReportSize": None, "notes": "150-byte descriptor. Source: GP2040-CE NeoGeoDescriptors.h. 15 buttons, hat, 4 axes, 2 triggers, 6 vibration axes, force feedback outputs."
    }},
    {"dir": "hori", "file": "pcengine-mini.json", "data": {
        "id": "hori-pcengine-mini", "name": "HORI PC Engine Mini Pad", "vendor": "HORI",
        "vid": "0x0F0D", "pid": "0x0138", "productString": "PCEngine PAD",
        "manufacturerString": "HORI CO.,LTD.", "type": "gamepad", "connection": "usb",
        "descriptor": "05010905a10115002501350045017501950e05091901290e81029502810105012507463b017504950165140939814265009501810126ff0046ff0009300931093209357508950481027508950181010a4f4875089508b1020a4f489102c0",
        "inputReportSize": None, "notes": "94-byte descriptor. Source: GP2040-CE PCEngineDescriptors.h. 14 buttons, hat, 4 axes, vendor feature/output reports."
    }},
    # === Razer Panthera PS4-compatible ===
    {"dir": "razer", "file": "panthera-ps4.json", "data": {
        "id": "razer-panthera-ps4", "name": "Razer Panthera (PS4 Mode)", "vendor": "Razer",
        "vid": "0x1532", "pid": "0x0401", "productString": "Razer Panthera",
        "manufacturerString": "Razer Inc.", "type": "arcadestick", "connection": "usb",
        "descriptor": "05010905a10185010930093109320935150026ff007508950481020939150025073500463b016514750495018142650005091901290e150025017501950e81020600ff0920750695018102050109330934150026ff007508950281020600ff09219536810285050922951f910285030a2127952fb102850209249524b102850809259503b102851009269504b102851109279502b10285120602ff0921950fb102851309229516b10285140605ff09209510b10285150921952cb1020680ff858009209506b102858109219506b102858209229505b102858309239501b102858409249504b102858509259506b102858609269506b102858709279523b102858809289522b102858909299502b102859009309505b102859109319503b102859209329503b10285930933950cb10285a009409506b10285a109419501b10285a209429501b10285a309439530b10285a40944950db10285a509459515b10285a609469515b10285a7094a9501b10285a8094b9501b10285a9094c9508b10285aa094e9501b10285ab094f9539b10285ac09509539b10285ad0951950bb10285ae09529501b10285af09539502b10285b00954953fb102c006f0ff0940a10185f00947953fb10285f10948953fb10285f20949950fb10285f30a01479507b102c0",
        "inputReportSize": None, "notes": "481-byte PS4-compatible descriptor. Source: GP2040-CE PS4Descriptors.h. Full DS4-compatible with all feature reports for PS4 authentication. Also used by Brook UFB, Qanba, and many third-party PS4 arcade sticks."
    }},
    # === PS5 General / Activtor ===
    {"dir": "misc", "file": "p5general-activtor.json", "data": {
        "id": "p5general-activtor", "name": "P5 General (PS5-compatible Activtor)", "vendor": "Activtor",
        "vid": "0x2B81", "pid": "0x0101", "productString": "P5General",
        "manufacturerString": "Activtor", "type": "gamepad", "connection": "usb",
        "descriptor": "05010905a1018501093009310932093509330934150026ff007508950681020600ff09209501810205010939150025073500463b016514750495018142650005091901290e150025017501950e81020600ff0921950e81020600ff0922150026ff0075089534810285020923952f910285030a2128952fb1020680ff85e009579502b102c006f0ff0940a10185f00947953fb10285f10948953fb10285f20949950fb102c0",
        "inputReportSize": None, "notes": "165-byte PS5-compatible descriptor. Source: GP2040-CE P5GeneralDescriptors.h. DualSense-like with auth feature reports."
    }},
    # === Generic/DIY gamepads ===
    {"dir": "misc", "file": "dragonrise-0006.json", "data": {
        "id": "dragonrise-0006", "name": "DragonRise Generic Gamepad (PC TWIN SHOCK)", "vendor": "DragonRise",
        "vid": "0x0079", "pid": "0x0006", "productString": "PC TWIN SHOCK Gamepad",
        "manufacturerString": "DragonRise Inc.", "type": "gamepad", "connection": "usb",
        "descriptor": "05010904a101a10275089505150026ff00350046ff00093009300930093009318102750495012507463b0165140900814265007501950a2501450105091901290a81020600ff7501950a2501450109018102c0a1027508950446ff0026ff0009029102c0c0",
        "inputReportSize": None, "notes": "101-byte descriptor. Reconstructed from kernel hid-dr.c. THE most common cheap USB gamepad chipset — used by millions of generic USB gamepads worldwide."
    }},
    {"dir": "misc", "file": "ibuffalo-snes.json", "data": {
        "id": "ibuffalo-snes", "name": "iBuffalo Classic USB SNES Gamepad", "vendor": "iBuffalo",
        "vid": "0x0583", "pid": "0x2060", "productString": "USB,2-axis 8-button gamepad",
        "manufacturerString": "iBuffalo", "type": "gamepad", "connection": "usb",
        "descriptor": "05010904a101a10275089502150026ff00350046ff000930093181027501950815002501350045010509190129088102c0c0",
        "inputReportSize": None, "notes": "50-byte descriptor. Reconstructed from known device characteristics. 2 axes, 8 buttons — the most popular retro USB SNES pad."
    }},
    {"dir": "misc", "file": "generic-dinput-gamepad.json", "data": {
        "id": "generic-dinput-gamepad", "name": "Generic DirectInput USB Gamepad", "vendor": "Generic",
        "vid": "0x0079", "pid": "0x0006", "productString": "USB Gamepad",
        "manufacturerString": "Generic", "type": "gamepad", "connection": "usb",
        "descriptor": "05010905a10105091901292015002501952075018102050109392507950175048142950175048101050126ff0046ff000930093109320935750895048102c0",
        "inputReportSize": None, "notes": "63-byte descriptor. Source: GP2040-CE HIDDescriptors.h. Standard DirectInput gamepad: 32 buttons, hat, 4 8-bit axes. Used by many third-party arcade sticks and DIY controllers in PC/DInput mode."
    }},
    # === Sim racing VID/PIDs (descriptor capture needed) ===
    {"dir": "fanatec", "file": "csl-elite.json", "data": {
        "id": "fanatec-csl-elite", "name": "Fanatec CSL Elite Wheelbase", "vendor": "Fanatec",
        "vid": "0x0EB7", "pid": "0x0E03", "productString": "CSL Elite Wheel Base",
        "manufacturerString": "Endor AG", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "133-byte descriptor reported by lsusb. Requires physical hardware dump. Linux driver hid-fanatecff injects PID FFB collection at runtime."
    }},
    {"dir": "fanatec", "file": "csl-dd.json", "data": {
        "id": "fanatec-csl-dd", "name": "Fanatec CSL DD Wheelbase", "vendor": "Fanatec",
        "vid": "0x0EB7", "pid": "0x0020", "productString": "CSL DD",
        "manufacturerString": "Endor AG", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Requires physical hardware dump. Linux driver hid-fanatecff."
    }},
    {"dir": "fanatec", "file": "podium-dd1.json", "data": {
        "id": "fanatec-podium-dd1", "name": "Fanatec Podium DD1", "vendor": "Fanatec",
        "vid": "0x0EB7", "pid": "0x0006", "productString": "Podium Wheel Base DD1",
        "manufacturerString": "Endor AG", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Requires physical hardware dump."
    }},
    {"dir": "fanatec", "file": "podium-dd2.json", "data": {
        "id": "fanatec-podium-dd2", "name": "Fanatec Podium DD2", "vendor": "Fanatec",
        "vid": "0x0EB7", "pid": "0x0007", "productString": "Podium Wheel Base DD2",
        "manufacturerString": "Endor AG", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Requires physical hardware dump."
    }},
    {"dir": "fanatec", "file": "clubsport-v2.json", "data": {
        "id": "fanatec-clubsport-v2", "name": "Fanatec ClubSport V2 Wheelbase", "vendor": "Fanatec",
        "vid": "0x0EB7", "pid": "0x0001", "productString": "ClubSport Wheel Base V2",
        "manufacturerString": "Endor AG", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Requires physical hardware dump."
    }},
    {"dir": "fanatec", "file": "clubsport-v25.json", "data": {
        "id": "fanatec-clubsport-v25", "name": "Fanatec ClubSport V2.5 Wheelbase", "vendor": "Fanatec",
        "vid": "0x0EB7", "pid": "0x0004", "productString": "ClubSport Wheel Base V2.5",
        "manufacturerString": "Endor AG", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Requires physical hardware dump."
    }},
    {"dir": "fanatec", "file": "csl-elite-ps4.json", "data": {
        "id": "fanatec-csl-elite-ps4", "name": "Fanatec CSL Elite PS4 Wheelbase", "vendor": "Fanatec",
        "vid": "0x0EB7", "pid": "0x0005", "productString": "CSL Elite Wheel Base PS4",
        "manufacturerString": "Endor AG", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Requires physical hardware dump."
    }},
    {"dir": "fanatec", "file": "clubsport-pedals-v3.json", "data": {
        "id": "fanatec-clubsport-pedals-v3", "name": "Fanatec ClubSport Pedals V3", "vendor": "Fanatec",
        "vid": "0x0EB7", "pid": "0x183B", "productString": "ClubSport Pedals V3",
        "manufacturerString": "Endor AG", "type": "pedals", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Requires physical hardware dump."
    }},
    {"dir": "fanatec", "file": "csl-pedals.json", "data": {
        "id": "fanatec-csl-pedals", "name": "Fanatec CSL Pedals", "vendor": "Fanatec",
        "vid": "0x0EB7", "pid": "0x6205", "productString": "CSL Pedals",
        "manufacturerString": "Endor AG", "type": "pedals", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Requires physical hardware dump."
    }},
    {"dir": "fanatec", "file": "clubsport-shifter.json", "data": {
        "id": "fanatec-clubsport-shifter", "name": "Fanatec ClubSport Shifter SQ V1.5", "vendor": "Fanatec",
        "vid": "0x0EB7", "pid": "0x1A92", "productString": "ClubSport Shifter SQ V1.5",
        "manufacturerString": "Endor AG", "type": "other", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Requires physical hardware dump."
    }},
    # === MOZA ===
    {"dir": "moza", "file": "moza-r5.json", "data": {
        "id": "moza-r5", "name": "MOZA R5 Racing Wheelbase", "vendor": "MOZA",
        "vid": "0x346E", "pid": "0x0004", "productString": "MOZA R5",
        "manufacturerString": "Gudsen Technology", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "1243-byte PID-compliant descriptor. Full structure documented (8x 16-bit axes, 128 buttons, hat, PID FFB). Alternate PID 0x0014. Requires physical dump for exact hex."
    }},
    {"dir": "moza", "file": "moza-r9.json", "data": {
        "id": "moza-r9", "name": "MOZA R9 Racing Wheelbase", "vendor": "MOZA",
        "vid": "0x346E", "pid": "0x0002", "productString": "MOZA R9",
        "manufacturerString": "Gudsen Technology", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Same PID-compliant descriptor structure as R5. Alternate PID 0x0012."
    }},
    {"dir": "moza", "file": "moza-r12.json", "data": {
        "id": "moza-r12", "name": "MOZA R12 Racing Wheelbase", "vendor": "MOZA",
        "vid": "0x346E", "pid": "0x0006", "productString": "MOZA R12",
        "manufacturerString": "Gudsen Technology", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Same PID-compliant descriptor structure as R5. Alternate PID 0x0016."
    }},
    {"dir": "moza", "file": "moza-r16.json", "data": {
        "id": "moza-r16", "name": "MOZA R16/R21 Racing Wheelbase", "vendor": "MOZA",
        "vid": "0x346E", "pid": "0x0000", "productString": "MOZA R16",
        "manufacturerString": "Gudsen Technology", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Same PID-compliant descriptor structure as R5. Alternate PID 0x0010."
    }},
    # === Simucube ===
    {"dir": "simucube", "file": "simucube-2-pro.json", "data": {
        "id": "simucube-2-pro", "name": "Simucube 2 Pro", "vendor": "Granite Devices",
        "vid": "0x16D0", "pid": "0x0D60", "productString": "Simucube 2 Pro",
        "manufacturerString": "Granite Devices", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "PID-compliant. 8 axes (16-bit unsigned), 128 buttons. Requires physical dump."
    }},
    {"dir": "simucube", "file": "simucube-2-sport.json", "data": {
        "id": "simucube-2-sport", "name": "Simucube 2 Sport", "vendor": "Granite Devices",
        "vid": "0x16D0", "pid": "0x0D61", "productString": "Simucube 2 Sport",
        "manufacturerString": "Granite Devices", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "PID-compliant. Same structure as SC2 Pro."
    }},
    {"dir": "simucube", "file": "simucube-2-ultimate.json", "data": {
        "id": "simucube-2-ultimate", "name": "Simucube 2 Ultimate", "vendor": "Granite Devices",
        "vid": "0x16D0", "pid": "0x0D5F", "productString": "Simucube 2 Ultimate",
        "manufacturerString": "Granite Devices", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "PID-compliant. Same structure as SC2 Pro."
    }},
    # === Cammus ===
    {"dir": "cammus", "file": "cammus-c5.json", "data": {
        "id": "cammus-c5", "name": "Cammus C5 Wheelbase", "vendor": "Cammus",
        "vid": "0x3416", "pid": "0x0301", "productString": "CAMMUS C5",
        "manufacturerString": "Cammus", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "PID-compliant but MISSING Start Delay field (0xA7). Requires universal-pidff driver on Linux."
    }},
    {"dir": "cammus", "file": "cammus-c12.json", "data": {
        "id": "cammus-c12", "name": "Cammus C12 Wheelbase", "vendor": "Cammus",
        "vid": "0x3416", "pid": "0x0302", "productString": "CAMMUS C12",
        "manufacturerString": "Cammus", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Same structure as C5."
    }},
    # === Simagic ===
    {"dir": "simagic", "file": "simagic-alpha.json", "data": {
        "id": "simagic-alpha", "name": "Simagic Alpha Wheelbase", "vendor": "Simagic",
        "vid": "0x0483", "pid": "0x0522", "productString": "Simagic Alpha",
        "manufacturerString": "Simagic", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Pre-fw171: PID-compliant. Post-fw171: proprietary protocol. Shared VID with STMicroelectronics."
    }},
    # === Asetek SimSports ===
    {"dir": "asetek", "file": "asetek-invicta.json", "data": {
        "id": "asetek-invicta", "name": "Asetek SimSports Invicta Wheelbase", "vendor": "Asetek",
        "vid": "0x2433", "pid": "0xF300", "productString": "Invicta",
        "manufacturerString": "Asetek SimSports", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "PID-compliant. From universal-pidff hid-ids.h."
    }},
    {"dir": "asetek", "file": "asetek-forte.json", "data": {
        "id": "asetek-forte", "name": "Asetek SimSports Forte Wheelbase", "vendor": "Asetek",
        "vid": "0x2433", "pid": "0xF301", "productString": "Forte",
        "manufacturerString": "Asetek SimSports", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "PID-compliant. From universal-pidff hid-ids.h."
    }},
    {"dir": "asetek", "file": "asetek-la-prima.json", "data": {
        "id": "asetek-la-prima", "name": "Asetek SimSports La Prima Wheelbase", "vendor": "Asetek",
        "vid": "0x2433", "pid": "0xF303", "productString": "La Prima",
        "manufacturerString": "Asetek SimSports", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "PID-compliant. From universal-pidff hid-ids.h."
    }},
    # === PXN ===
    {"dir": "pxn", "file": "pxn-v10.json", "data": {
        "id": "pxn-v10", "name": "PXN V10 Racing Wheel", "vendor": "PXN",
        "vid": "0x11FF", "pid": "0x3245", "productString": "PXN V10",
        "manufacturerString": "PXN", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "From universal-pidff hid-ids.h."
    }},
    {"dir": "pxn", "file": "pxn-v12.json", "data": {
        "id": "pxn-v12", "name": "PXN V12 Racing Wheel", "vendor": "PXN",
        "vid": "0x11FF", "pid": "0x1212", "productString": "PXN V12",
        "manufacturerString": "PXN", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "From universal-pidff hid-ids.h. Lite variant PIDs: 0x1112, 0x1211."
    }},
    # === Heusinkveld ===
    {"dir": "heusinkveld", "file": "sprint-pedals.json", "data": {
        "id": "heusinkveld-sprint-pedals", "name": "Heusinkveld Sprint Pedals", "vendor": "Heusinkveld",
        "vid": "0x30B7", "pid": "0x1001", "productString": "Sprint Pedals",
        "manufacturerString": "Heusinkveld Engineering", "type": "pedals", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "From simracing-hwdb."
    }},
    {"dir": "heusinkveld", "file": "ultimate-pedals.json", "data": {
        "id": "heusinkveld-ultimate-pedals", "name": "Heusinkveld Ultimate Pedals", "vendor": "Heusinkveld",
        "vid": "0x30B7", "pid": "0x1003", "productString": "Ultimate Pedals",
        "manufacturerString": "Heusinkveld Engineering", "type": "pedals", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "From simracing-hwdb."
    }},
    # === Thrustmaster pedals ===
    {"dir": "thrustmaster", "file": "t-lcm-pedals.json", "data": {
        "id": "thrustmaster-t-lcm-pedals", "name": "Thrustmaster T-LCM Pedals", "vendor": "Thrustmaster",
        "vid": "0x044F", "pid": "0xB371", "productString": "T-LCM Pedals",
        "manufacturerString": "Thrustmaster", "type": "pedals", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "From simracing-hwdb."
    }},
    {"dir": "thrustmaster", "file": "tpr-pedals.json", "data": {
        "id": "thrustmaster-tpr-pedals", "name": "Thrustmaster TPR Rudder Pedals", "vendor": "Thrustmaster",
        "vid": "0x044F", "pid": "0xB68F", "productString": "TPR Pendular Rudder",
        "manufacturerString": "Thrustmaster", "type": "pedals", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "From simracing-hwdb."
    }},
    # === PS3 Alt Descriptor ===
    {"dir": "misc", "file": "ps3-alt-gamepad.json", "data": {
        "id": "ps3-alt-gamepad", "name": "PS3-compatible Alt Gamepad (GP2040-CE)", "vendor": "Generic",
        "vid": "0x10C4", "pid": "0x82C0", "productString": "Gamepad",
        "manufacturerString": "Open Stick Community", "type": "gamepad", "connection": "usb",
        "descriptor": "05010905a101a10215002501350045017501950d05091901290d81029503810105012507463b017504950165140939814265009501810126ff0046ff0009300931093209357508950481020600ff0920092109220923092409250926092709280929092a092b950c81020a21269508b1020a2126910226ff0346ff03092c092d092e092f751095048102c0a10226ff0046ff009507750809039102c0c0",
        "inputReportSize": None, "notes": "157-byte PS3-alt descriptor. Source: GP2040-CE PS3Descriptors.h. 13 buttons, hat, 4 axes, 12 vendor inputs, 4x 16-bit axes, 7-byte vendor output."
    }},
    # === Logitech RS Pedals ===
    {"dir": "logitech", "file": "rs-pedals.json", "data": {
        "id": "logitech-rs-pedals", "name": "Logitech RS Pedals (Standalone)", "vendor": "Logitech",
        "vid": "0x046D", "pid": "0xC277", "productString": "RS Pedals",
        "manufacturerString": "Logitech", "type": "pedals", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "From simracing-hwdb. Standalone pedal set."
    }},
    # === OpenFFBoard ===
    {"dir": "misc", "file": "openffboard.json", "data": {
        "id": "openffboard", "name": "OpenFFBoard (Open-Source FFB Wheel)", "vendor": "OpenFFBoard",
        "vid": "0x1209", "pid": "0xFFB0", "productString": "OpenFFBoard",
        "manufacturerString": "OpenFFBoard", "type": "wheel", "connection": "usb",
        "descriptor": None, "inputReportSize": None, "notes": "Open-source force feedback wheel controller. PID-compliant. From universal-pidff hid-ids.h."
    }},
]

def main():
    created = 0
    for entry in PROFILES:
        dir_path = os.path.join(PROFILES_DIR, entry["dir"])
        os.makedirs(dir_path, exist_ok=True)
        file_path = os.path.join(dir_path, entry["file"])
        data = entry["data"]
        if data.get("descriptor"):
            data["descriptor"] = data["descriptor"].replace(" ", "").lower()
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
