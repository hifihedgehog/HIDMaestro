"""Generate HIDMaestro controller profile JSON files from scraped data."""
import json, os

PROFILES_DIR = os.path.join(os.path.dirname(os.path.dirname(__file__)), "profiles")

# All new profiles to create (or update)
NEW_PROFILES = [
    # === Logitech Wheels (from lgff_wheel_adapter) ===
    {"dir": "logitech", "file": "wingman-formula-gp.json", "data": {
        "id": "logitech-wingman-formula-gp", "name": "Logitech WingMan Formula GP", "vendor": "Logitech",
        "vid": "0x046D", "pid": "0xC20E", "productString": "WingMan Formula GP",
        "manufacturerString": "Logitech", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a101a102150026ff00350046ff007508950109308102a425014501750195028101950605091901290681020501b40931810295020600ff09018102c0a10226ff0046ff00750895040902b102c0c0",
        "inputReportSize": None, "notes": "82-byte descriptor. Source: lgff_wheel_adapter."
    }},
    {"dir": "logitech", "file": "gt-force.json", "data": {
        "id": "logitech-gt-force", "name": "Logitech GT Force (WingMan FFG)", "vendor": "Logitech",
        "vid": "0x046D", "pid": "0xC293", "productString": "WingMan Formula Force GP",
        "manufacturerString": "Logitech", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a101a1029501750a150026ff03350046ff0309308102950675012501450105091901290681029501750826ff0046ff000600ff090181020501093181020600ff090195038102c0a102090295079102c0c0",
        "inputReportSize": None, "notes": "85-byte descriptor. Source: lgff_wheel_adapter / GIMX EMUGTF."
    }},
    {"dir": "logitech", "file": "driving-force.json", "data": {
        "id": "logitech-driving-force", "name": "Logitech Driving Force", "vendor": "Logitech",
        "vid": "0x046D", "pid": "0xC294", "productString": "Logitech Driving Force",
        "manufacturerString": "Logitech", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a101a1029501750a150026ff03350046ff0309308102950c75012501450105091901290c810295020600ff090181020501093126ff0046ff009501750881022507463b0175046514093981427501950465000600ff09012501450181029502750826ff0046ff0009028102c0a10226ff0046ff009507750809039102c0c0",
        "inputReportSize": None, "notes": "130-byte descriptor. Source: lgff_wheel_adapter / GIMX EMUDF."
    }},
    {"dir": "logitech", "file": "driving-force-pro.json", "data": {
        "id": "logitech-driving-force-pro", "name": "Logitech Driving Force Pro", "vendor": "Logitech",
        "vid": "0x046D", "pid": "0xC298", "productString": "Logitech Driving Force Pro",
        "manufacturerString": "Logitech", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a101a1029501750e150026ff3f350046ff3f09308102950e75012501450105091901290e81020501950175042507463b0165140939814265009501750826ff0046ff00093181020600ff0900950375088102c0a102090295079102c0c0",
        "inputReportSize": None, "notes": "97-byte descriptor. Source: lgff_wheel_adapter / GIMX EMUDFP."
    }},
    {"dir": "logitech", "file": "driving-force-gt.json", "data": {
        "id": "logitech-driving-force-gt", "name": "Logitech Driving Force GT", "vendor": "Logitech",
        "vid": "0x046D", "pid": "0xC29A", "productString": "Driving Force GT",
        "manufacturerString": "Logitech", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a101150025073500463b01651409397504950181426500250145010509190129157501951581020600ff09019507810226ff3f46ff3f750e9501050109308102250145010600ff090175019502810226ff0046ff000501093109327508810295070600ff0902910295830903b102c0",
        "inputReportSize": None, "notes": "115-byte descriptor. Source: lgff_wheel_adapter."
    }},
    {"dir": "logitech", "file": "g25.json", "data": {
        "id": "logitech-g25", "name": "Logitech G25 Racing Wheel", "vendor": "Logitech",
        "vid": "0x046D", "pid": "0xC299", "productString": "G25 Racing Wheel",
        "manufacturerString": "Logitech", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a101150025073500463b01651409397504950181426500250145010509190129137501951381020600ff09019503810226ff3f46ff3f750e950105010930810226ff0046ff007508950309320935093181020600ff09049503810295070600ff0902910295900903b102c0",
        "inputReportSize": None, "notes": "111-byte descriptor. Source: lgff_wheel_adapter."
    }},
    {"dir": "logitech", "file": "speed-force-wireless.json", "data": {
        "id": "logitech-speed-force-wireless", "name": "Logitech Speed Force Wireless (Wii)", "vendor": "Logitech",
        "vid": "0x046D", "pid": "0xC29C", "productString": "Speed Force Wireless",
        "manufacturerString": "Logitech", "type": "wheel", "connection": "wireless-adapter",
        "descriptor": "05010904a101a1029501750a150026ff03350046ff03093081020600ff950275012501450109018102950b1901290b050981020600ff95017501090281020501750826ff0046ff000931093295028102c0a1020600ff950709039102c00affff9508b102c0",
        "inputReportSize": None, "notes": "101-byte descriptor. Source: lgff_wheel_adapter. Wii wheel."
    }},
    {"dir": "logitech", "file": "momo-force.json", "data": {
        "id": "logitech-momo-force", "name": "Logitech MOMO Racing Force (Red)", "vendor": "Logitech",
        "vid": "0x046D", "pid": "0xC295", "productString": "MOMO Racing",
        "manufacturerString": "Logitech", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a101a1029501750a150026ff03350046ff0309308102950875012501450105091901290881020600ff0900950681029501750826ff0046ff0005010931810295030600ff09018102c0a102090295079102c0c0",
        "inputReportSize": None, "notes": "87-byte descriptor. Source: lgff_wheel_adapter."
    }},
    {"dir": "logitech", "file": "momo-racing.json", "data": {
        "id": "logitech-momo-racing", "name": "Logitech MOMO Racing (Black)", "vendor": "Logitech",
        "vid": "0x046D", "pid": "0xCA03", "productString": "MOMO Racing",
        "manufacturerString": "Logitech", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a101a1029501750a150026ff03350046ff0309308102950a75012501450105091901290a81020600ff0900950481029501750826ff0046ff0005010931810295030600ff09018102c0a102090295079102c0c0",
        "inputReportSize": None, "notes": "87-byte descriptor. Source: lgff_wheel_adapter."
    }},
    {"dir": "logitech", "file": "formula-vibration.json", "data": {
        "id": "logitech-formula-vibration", "name": "Logitech Formula Vibration Feedback Wheel", "vendor": "Logitech",
        "vid": "0x046D", "pid": "0xCA04", "productString": "Formula Vibration Feedback Wheel",
        "manufacturerString": "Logitech", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a101a1029501750a150026ff03350046ff0309308102950c75012501450105091901290c810295020600ff09018102090226ff0046ff0095017508810205012507463b0175046514093981427501950465000600ff090125014501810205019501750826ff0046ff000931810209328102c0a10226ff0046ff009507750809039102c0c0",
        "inputReportSize": None, "notes": "136-byte descriptor. Source: Linux kernel hid-lg.c fv_rdesc_fixed."
    }},
    # === Thrustmaster Wheels (from hid-tmff2) ===
    {"dir": "thrustmaster", "file": "t300rs-ps3.json", "data": {
        "id": "thrustmaster-t300rs-ps3", "name": "Thrustmaster T300RS (PS3 Normal Mode)", "vendor": "Thrustmaster",
        "vid": "0x044F", "pid": "0xB66E", "productString": "Thrustmaster T300RS Racing wheel",
        "manufacturerString": "Thrustmaster", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a1010901a10085070930150027ffff0000350047ffff0000751095018102093526ff0346ff0381020932810209318102810305091901290d250145017501950d8102750b95018103050109392507463b0155006514750481426500810385600600ff09607508953f26ff7f150046ff7f3600809102850209028102091485148102c0c0",
        "inputReportSize": None, "notes": "135-byte descriptor (driver fixup). Source: hid-tmff2. PID 0xB66E is PS3 normal mode."
    }},
    {"dir": "thrustmaster", "file": "t300rs-ps3-adv.json", "data": {
        "id": "thrustmaster-t300rs-ps3-adv", "name": "Thrustmaster T300RS (PS3 Advanced/F1 Mode)", "vendor": "Thrustmaster",
        "vid": "0x044F", "pid": "0xB66F", "productString": "Thrustmaster T300RS Racing wheel",
        "manufacturerString": "Thrustmaster", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a1010901a10085070930150027ffff0000350047ffff0000751095018102093126ff0346ff0381020935810209368102810305091901291925014501750195198102750395018103050109392507463b015500651475048142650085600600ff09607508953f26ff0046ff009102850209028102091485148102c0c0",
        "inputReportSize": None, "notes": "128-byte descriptor (driver fixup). Source: hid-tmff2. PID 0xB66F is PS3 advanced/F1 mode."
    }},
    {"dir": "thrustmaster", "file": "ts-pc-racer.json", "data": {
        "id": "thrustmaster-ts-pc-racer", "name": "Thrustmaster TS-PC Racer", "vendor": "Thrustmaster",
        "vid": "0x044F", "pid": "0xB689", "productString": "Thrustmaster TS-PC Racer",
        "manufacturerString": "Thrustmaster", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a1010901a10085070930150027ffff0000350047ffff0000751095018102093526ff0346ff0381020932810209318102810305091901290d250145017501950d8102750b95018103050109392507463b0155006514750481426500810385600600ff09607508953f26ff7f150046ff7f3600809102850209028102091485148102c0c0",
        "inputReportSize": None, "notes": "135-byte descriptor (driver fixup). Source: hid-tmff2."
    }},
    {"dir": "thrustmaster", "file": "ts-xw.json", "data": {
        "id": "thrustmaster-ts-xw", "name": "Thrustmaster TS-XW Racer", "vendor": "Thrustmaster",
        "vid": "0x044F", "pid": "0xB692", "productString": "Thrustmaster TS-XW Racer",
        "manufacturerString": "Thrustmaster", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a1010901a10085070930150027ffff0000350047ffff0000751095018102093526ff0346ff0381020932810209318102810305091901290d250145017501950d8102750b95018103050109392507463b0155006514750481426500810385600600ff09607508953f26ff7f150046ff7f3600809102850209028102091485148102c0c0",
        "inputReportSize": None, "notes": "135-byte descriptor (driver fixup). Source: hid-tmff2."
    }},
    {"dir": "thrustmaster", "file": "tx-xbox.json", "data": {
        "id": "thrustmaster-tx-xbox", "name": "Thrustmaster TX Racing Wheel (Xbox)", "vendor": "Thrustmaster",
        "vid": "0x044F", "pid": "0xB669", "productString": "Thrustmaster TX Racing Wheel",
        "manufacturerString": "Thrustmaster", "type": "wheel", "connection": "usb",
        "descriptor": "05010904a1010901a10085070930150027ffff0000350047ffff0000751095018102093526ff0346ff0381020932810209318102810305091901290d250145017501950d8102750b95018103050109392507463b0155006514750481426500810385600600ff09607508953f26ff7f150046ff7f3600809102850209028102091485148102c0c0",
        "inputReportSize": None, "notes": "135-byte descriptor (driver fixup). Source: hid-tmff2."
    }},
    # === Xbox 360 Subtypes (from DJm00n/ControllersInfo) ===
    {"dir": "microsoft", "file": "xbox-360-arcade-stick.json", "data": {
        "id": "xbox-360-arcade-stick", "name": "Xbox 360 Arcade Stick (XInput HID)", "vendor": "Microsoft",
        "vid": "0x045E", "pid": "0x028E", "productString": "Controller (XBOX 360 For Windows)",
        "manufacturerString": "\u00a9Microsoft Corporation", "type": "arcadestick", "connection": "usb",
        "descriptor": "05010905a10105091901290a950a7501810275069501810305010939150125083500463b10660e00750495018142750495018103750895018103c0",
        "inputReportSize": None, "notes": "59-byte descriptor. Source: DJm00n/ControllersInfo xusb_arcadestick. XInput HID view — arcade stick subtype (10 buttons, hat, no axes)."
    }},
    {"dir": "microsoft", "file": "xbox-360-dance-pad.json", "data": {
        "id": "xbox-360-dance-pad", "name": "Xbox 360 Dance Pad (XInput HID)", "vendor": "Microsoft",
        "vid": "0x045E", "pid": "0x028E", "productString": "Controller (XBOX 360 For Windows)",
        "manufacturerString": "\u00a9Microsoft Corporation", "type": "other", "connection": "usb",
        "descriptor": "05010905a10105091901290a950a7501810275069501810305010939150125083500463b10660e00750495018142750495018103750895018103c0",
        "inputReportSize": None, "notes": "59-byte descriptor. Source: DJm00n/ControllersInfo xusb_dancepad. Identical to arcade stick descriptor."
    }},
    {"dir": "microsoft", "file": "xbox-360-flight-stick.json", "data": {
        "id": "xbox-360-flight-stick", "name": "Xbox 360 Flight Stick (XInput HID)", "vendor": "Microsoft",
        "vid": "0x045E", "pid": "0x028E", "productString": "Controller (XBOX 360 For Windows)",
        "manufacturerString": "\u00a9Microsoft Corporation", "type": "flightstick", "connection": "usb",
        "descriptor": "05010904a10105010932150026ff0095017508810205010935150026ff0095017508810205010901a10009300931150026ffff350046ffff950275108102c0a10009330934150026ffff350046ffff950275108102c005091901290a950a7501810205010939150125083500463b10660e00750495018142750295018103c0",
        "inputReportSize": None, "notes": "127-byte descriptor. Source: DJm00n/ControllersInfo xusb_flightstick."
    }},
    {"dir": "microsoft", "file": "xbox-360-guitar-v1.json", "data": {
        "id": "xbox-360-guitar-v1", "name": "Xbox 360 Guitar Controller v1 (XInput HID)", "vendor": "Microsoft",
        "vid": "0x045E", "pid": "0x028E", "productString": "Controller (XBOX 360 For Windows)",
        "manufacturerString": "\u00a9Microsoft Corporation", "type": "other", "connection": "usb",
        "descriptor": "05010905a101a1000935150026ffff350046ffff950175108102c005010936150026ffff95017510810205010937150026ff0095017508810275089501810305091901290a950a7501810275069501810305010939150125083500463b10660e00750495018142750495018103750895018103c0",
        "inputReportSize": None, "notes": "116-byte descriptor. Source: DJm00n/ControllersInfo xusb_guitar1."
    }},
    {"dir": "microsoft", "file": "xbox-360-guitar-v2.json", "data": {
        "id": "xbox-360-guitar-v2", "name": "Xbox 360 Guitar Controller v2 (XInput HID)", "vendor": "Microsoft",
        "vid": "0x045E", "pid": "0x028E", "productString": "Controller (XBOX 360 For Windows)",
        "manufacturerString": "\u00a9Microsoft Corporation", "type": "other", "connection": "usb",
        "descriptor": "05010905a101a1000935150026ffff350046ffff950175108102c005010936150026ffff95017510810275109501810305091901290a950a7501810275069501810305010939150125083500463b10660e00750495018142750495018103750895018103c0",
        "inputReportSize": None, "notes": "101-byte descriptor. Source: DJm00n/ControllersInfo xusb_guitar2."
    }},
    {"dir": "microsoft", "file": "xbox-360-wheel-v1.json", "data": {
        "id": "xbox-360-wheel-v1", "name": "Xbox 360 Racing Wheel v1 (XInput HID)", "vendor": "Microsoft",
        "vid": "0x045E", "pid": "0x028E", "productString": "Controller (XBOX 360 For Windows)",
        "manufacturerString": "\u00a9Microsoft Corporation", "type": "wheel", "connection": "usb",
        "descriptor": "05010905a101a1000930150026ffff350046ffff950175108102c005010932150026ff00950175088102050209bb150026ff0095017508810205091901290a950a7501810275069501810305010939150125083500463b10660e00750495018142750495018103750895018103c0",
        "inputReportSize": None, "notes": "110-byte descriptor. Source: DJm00n/ControllersInfo xusb_wheel1."
    }},
    {"dir": "microsoft", "file": "xbox-360-wheel-v2.json", "data": {
        "id": "xbox-360-wheel-v2", "name": "Xbox 360 Racing Wheel v2 (XInput HID)", "vendor": "Microsoft",
        "vid": "0x045E", "pid": "0x028E", "productString": "Controller (XBOX 360 For Windows)",
        "manufacturerString": "\u00a9Microsoft Corporation", "type": "wheel", "connection": "usb",
        "descriptor": "05010905a101a1000930150026ffff350046ffff950175108102c0a1000932150026ffff350046ffff950175108102c005091901290a950a7501810275069501810305010939150125083500463b10660e00750495018142750495018103750895018103c0",
        "inputReportSize": None, "notes": "101-byte descriptor. Source: DJm00n/ControllersInfo xusb_wheel2."
    }},
    {"dir": "microsoft", "file": "xbox-360-type2.json", "data": {
        "id": "xbox-360-type2", "name": "Xbox 360 Controller Type 2 (XInput HID)", "vendor": "Microsoft",
        "vid": "0x045E", "pid": "0x028E", "productString": "Controller (XBOX 360 For Windows)",
        "manufacturerString": "\u00a9Microsoft Corporation", "type": "gamepad", "connection": "usb",
        "descriptor": "05010905a101a10009300931150026ffff350046ffff950275108102c0a10009330934150026ffff350046ffff950275108102c0a1000932150026ffff350046ffff950175108102c005091901290a950a7501810205010939150125083500463b10660e00750495018142750295018103750895028103c0",
        "inputReportSize": None, "notes": "120-byte descriptor. Source: DJm00n/ControllersInfo xusb_gamepad2. Separate trigger axis collection variant."
    }},
    # === Amazon Luna ===
    {"dir": "amazon", "file": "luna-usb.json", "data": {
        "id": "amazon-luna-usb", "name": "Amazon Luna Controller (USB)", "vendor": "Amazon",
        "vid": "0x1949", "pid": "0x0419", "productString": "Luna Controller",
        "manufacturerString": "Amazon", "type": "gamepad", "connection": "usb",
        "descriptor": "05010905a101a100850105091901290c150025017501950c810275019504810105010939150025073500463b01651475089501814265000930093109320935150026ff00750895048102050109330934150026ff00750895028102c0c0",
        "inputReportSize": None, "notes": "93-byte descriptor. Source: DJm00n/ControllersInfo. VID 0x1949, alternate PID 0x041A also exists."
    }},
    {"dir": "amazon", "file": "luna-ble.json", "data": {
        "id": "amazon-luna-ble", "name": "Amazon Luna Controller (Bluetooth LE)", "vendor": "Amazon",
        "vid": "0x0171", "pid": "0x0419", "productString": "Luna Controller",
        "manufacturerString": "Amazon", "type": "gamepad", "connection": "bluetooth",
        "descriptor": "05010905a10185010901a10009300931150027ffff0000950275108102c00901a10009320935150027ffff0000950275108102c0050209c5150026ff039501750a810215002500750695018103050209c4150026ff039501750a81021500250075069501810305010939150125083500463b01661400750495018142750495011500250035004500650081030509090109021500250175019502810215002500750195018103090409051500250175019502810215002500750195018103090709081500250175019502810215002500750195038103090c1500250175019501810215002500750195018103090e090f1500250175019502810215002500750195018103050c0a24020a21021500250195027501810215002500750695018103050c09018502a101050c0a23021500250195017501810215002500750795018103c0050f09218503a102099715002501750495019102150025007504950191030970150025647508950491020950660110550e150026ff0075089501910209a7150026ff0075089501910265005500097c150026ff00750895019102c0850405060920150026ff00750895018102c00600ff0920a10185f595027510150026ff0719002aff070921810285f095507508150025ff0922810285f1950375080923810285f29501750809249102c0",
        "inputReportSize": None, "notes": "493-byte Bluetooth LE descriptor. Source: DJm00n/ControllersInfo. BLE VID 0x0171."
    }},
    # === Sony extras ===
    {"dir": "sony", "file": "ps-move.json", "data": {
        "id": "ps-move", "name": "PlayStation Move Motion Controller", "vendor": "Sony",
        "vid": "0x054C", "pid": "0x03D5", "productString": "Motion Controller",
        "manufacturerString": "Sony", "type": "other", "connection": "usb",
        "descriptor": "05010904a101a10285017501951515002501350045010509190129158102950b0600ff8103150026ff000501a10075089501350046ff0009308102c00600ff7508950781020501751046ffff27ffff0000950309330934093581020600ff9503810205010901950381020600ff95038102750c46ff0f26ff0f95048102750846ff0026ff00950681027508953009019102750895300901b102c0a1028502750895300901b102c0a10285ee750895300901b102c0a10285ef750895300901b102c0c0",
        "inputReportSize": None, "notes": "194-byte descriptor. Source: Linux kernel hid-sony.c motion_rdesc."
    }},
    {"dir": "sony", "file": "ps3-remote.json", "data": {
        "id": "ps3-remote", "name": "PlayStation 3 Blu-ray Remote", "vendor": "Sony",
        "vid": "0x054C", "pid": "0x0306", "productString": "BD Remote Control",
        "manufacturerString": "Sony", "type": "other", "connection": "bluetooth",
        "descriptor": "0501090505a101a102750808950101810101050919010129181814250101750101951818810202c0a10205091829fefe1426fe000075080895010180ff750808950606810101050609202014250505750808950101810202c0c0",
        "inputReportSize": None, "notes": "90-byte descriptor. Source: Linux kernel hid-sony.c ps3remote_rdesc."
    }},
    {"dir": "sony", "file": "ps-classic.json", "data": {
        "id": "ps-classic", "name": "PlayStation Classic Controller", "vendor": "Sony",
        "vid": "0x054C", "pid": "0x0CDA", "productString": "Controller",
        "manufacturerString": "Sony Interactive Entertainment", "type": "gamepad", "connection": "usb",
        "descriptor": "05010905a101150025017501950a0509190129 0a81020501093009311500250235004502750295028102750195028101c0",
        "inputReportSize": None, "notes": "51-byte descriptor. Source: GP2040-CE PSClassicDescriptors.h. Simple 10-button digital pad."
    }},
    # === Nacon/BigBen ===
    {"dir": "nacon", "file": "bigben-ps4.json", "data": {
        "id": "nacon-bigben-ps4", "name": "Nacon/BigBen PS4 Compact Controller", "vendor": "Nacon",
        "vid": "0x146B", "pid": "0x0902", "productString": "Bigben Interactive Wired Controller",
        "manufacturerString": "BigBen Interactive", "type": "gamepad", "connection": "usb",
        "descriptor": "05010905a10115002501350045017501950d05090905090109020904090709080909090a090b090c090e090f090d810275019503810105012507463b017504950165140939814265009501810126ff0046ff000930093109330934750895048102950a8101050126ff0046ff0009320935950281029508810106 00ffb1020a2126950891020a212695088102c0",
        "inputReportSize": None, "notes": "141-byte descriptor (driver fixup). Source: Linux kernel hid-bigbenff.c. 13 buttons, hat, 4 axes, 2 triggers."
    }},
    # === Saitek ===
    {"dir": "logitech", "file": "x52.json", "data": {
        "id": "saitek-x52", "name": "Saitek X52 Flight Control System", "vendor": "Logitech (Saitek)",
        "vid": "0x06A3", "pid": "0x075C", "productString": "Saitek X52 Flight Control System",
        "manufacturerString": "Saitek", "type": "hotas", "connection": "usb",
        "descriptor": "05010904a10901a100093009311500260007750b9502810209351500260003750a950181020932093309340936150026ff007508950481020509190129221500250195227501810275029501810105010939150125083500463b10661400750495018142050509240926150025 0f750495028102c0c0",
        "inputReportSize": None, "notes": "119-byte descriptor. Source: Ubuntu Bug #492056 / hid-remapper issue. Non-Pro X52 (PID 075C). X52 Pro (0762) descriptor not publicly available."
    }},
    # === GameCube Adapter ===
    {"dir": "nintendo", "file": "gamecube-adapter-hid.json", "data": {
        "id": "gamecube-adapter-hid", "name": "GameCube Controller Adapter (HID)", "vendor": "Nintendo",
        "vid": "0x057E", "pid": "0x0337", "productString": "WUP-028",
        "manufacturerString": "Nintendo Co., Ltd.", "type": "gamepad", "connection": "usb",
        "descriptor": "05050900a10185111900 2aff00150026ff0075089505910 0c0a10185211900 2aff00150026ff0075089525810 0c0a10185121900 2aff00150026ff00750895019100c0a10185221900 2aff00150026ff0075089519810 0c0a10185131900 2aff00150026ff00750895019100c0a10185231900 2aff00150026ff0075089502810 0c0a10185141900 2aff00150026ff00750895019100c0a10185241900 2aff00150026ff0075089502810 0c0a10185151900 2aff00150026ff00750895019100c0a10185251900 2aff00150026ff0075089502810 0c0",
        "inputReportSize": None, "notes": "214-byte descriptor. Source: GBAtemp USB HID dump. Uses vendor Usage Page (0x05, 0x05), report IDs 0x11-0x15 (out) and 0x21-0x25 (in). Known buggy: report 0x21 declares Count=37 but sends 36."
    }},
    # === Valve/Steam ===
    {"dir": "valve", "file": "steam-controller.json", "data": {
        "id": "steam-controller", "name": "Steam Controller (Wired)", "vendor": "Valve",
        "vid": "0x28DE", "pid": "0x1102", "productString": "Steam Controller",
        "manufacturerString": "Valve Software", "type": "gamepad", "connection": "usb",
        "descriptor": "0600ff0901a101150026ff00750895400901810295400901910295400901b102c0",
        "inputReportSize": 64, "notes": "33-byte vendor-specific descriptor. Source: cyrozap/steam-controller-re. Uses Usage Page 0xFF00 with 64-byte raw reports — all gamepad data in proprietary binary format. Wireless dongle PID: 0x1142."
    }},
    {"dir": "valve", "file": "steam-deck.json", "data": {
        "id": "steam-deck", "name": "Steam Deck Controller", "vendor": "Valve",
        "vid": "0x28DE", "pid": "0x1205", "productString": "Steam Deck",
        "manufacturerString": "Valve Software", "type": "gamepad", "connection": "usb",
        "descriptor": "06ffff0901a10109020903150026ff0075089540810209060907150026ff0075089540b102c0",
        "inputReportSize": 64, "notes": "38-byte vendor-specific descriptor. Source: ShadowBlip/InputPlumber. Uses Usage Page 0xFFFF with 64-byte raw reports — all inputs in proprietary binary format."
    }},
    # === HORI Steam ===
    {"dir": "hori", "file": "horipad-steam.json", "data": {
        "id": "hori-horipad-steam", "name": "HORIPAD Steam Controller", "vendor": "HORI",
        "vid": "0x0F0D", "pid": "0x0196", "productString": "HORIPAD Steam",
        "manufacturerString": "HORI CO.,LTD.", "type": "gamepad", "connection": "usb",
        "descriptor": "05010905a1018507a1000930093109320935150026ff00750895048102c00939150025073500463b016514750495018142050919012914150025017501951481020502150026ff0009c409c59502750881020600ff092095268102850509219520910285120922953f81020923910285140926953f81020927910285100924953f8102850f0928953f9102c0",
        "inputReportSize": None, "notes": "140-byte descriptor. Source: ShadowBlip/InputPlumber. Report ID 7 = gamepad (4 axes, hat, 20 buttons, triggers)."
    }},
    # === Razer ===
    {"dir": "razer", "file": "wolverine-v3-pro.json", "data": {
        "id": "razer-wolverine-v3-pro", "name": "Razer Wolverine V3 Pro (Wired)", "vendor": "Razer",
        "vid": "0x1532", "pid": "0x0A3F", "productString": "Razer Wolverine V3 Pro",
        "manufacturerString": "Razer Inc.", "type": "gamepad", "connection": "usb",
        "descriptor": "0600ff0901a1018502150026ff0009027508953f8102150026ff0009037508953f9102c0050c0901a10185011500250109cd09b509b609b77501950481021500250109b009b109b309b47501950481221500250109e909ea09e2750195038122750595018101c0",
        "inputReportSize": None, "notes": "103-byte descriptor. Source: openrazer issue #2364. Vendor-specific 63-byte I/O reports (ID 2) for controller data + Consumer Controls (ID 1) for media keys."
    }},
]

def main():
    created = 0
    for entry in NEW_PROFILES:
        dir_path = os.path.join(PROFILES_DIR, entry["dir"])
        os.makedirs(dir_path, exist_ok=True)
        file_path = os.path.join(dir_path, entry["file"])

        # Clean up descriptor - remove any stray spaces
        data = entry["data"]
        if data.get("descriptor"):
            data["descriptor"] = data["descriptor"].replace(" ", "").lower()

        with open(file_path, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
            f.write("\n")
        created += 1

    print(f"Created {created} profile files.")

    # Count total
    total = 0
    with_desc = 0
    for root, dirs, files in os.walk(PROFILES_DIR):
        for fn in files:
            if fn.endswith(".json") and fn != "schema.json" and fn != "scraped_descriptors.json" and fn != "linux-kernel-fixed-descriptors.json":
                total += 1
                fp = os.path.join(root, fn)
                with open(fp) as f:
                    try:
                        d = json.load(f)
                        if d.get("descriptor"):
                            with_desc += 1
                    except:
                        pass
    print(f"Total profiles: {total} ({with_desc} with descriptors)")

if __name__ == "__main__":
    main()
