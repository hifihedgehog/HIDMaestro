// The probe's tables: the device and vendor words beside the catalog's,
// the generic words left out of them, the device-named declarations and INF
// model lines the check allows, the vendored file whose other hits it
// lists as allowed, the regions of 9bd6279's drivers that v2.0.0 deletes
// (decision (c)) with the dispatch lines the plan keeps, the pinned hash
// of their prints, and the profile properties set by one catalog profile
// that it allows. Each entry carries its reason, and the output prints
// every use of it.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

// Declared names the check allows, each exactly as written, in the files
// named by their path under the tree's root.
internal sealed record AllowedName(string[] Names, string[] Paths, string Reason)
{
    public bool Matches(string name, string relPath) =>
        Names.Contains(name, StringComparer.Ordinal)
        && Paths.Any(p => p.Equals(relPath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase));
}

// A region of 9bd6279's drivers that v2.0.0 deletes, by its file and line
// range at 9bd6279, and the lane of the conversion plan that deletes it.
// removed-regions.txt holds its prints (RegionPrints.cs).
internal sealed record RemovedRegion(string Name, string File, int Begin, int End, string What, string Lane);

// Lines inside a region at 9bd6279 that the plan keeps. They count as code
// outside the regions: no print holds them.
internal sealed record KeptRange(string File, int Begin, int End, string What);

// An INF model line the check allows, by its text without comments.
internal sealed record AllowedInfLine(string File, string Text, string Reason);

// A vendored file the drivers build, by its path and the SHA-256 of its
// text with CRLF read as LF. Rules A to C list its hits as allowed while
// the hash holds. Rule D and the marker rule read it as any other file.
internal sealed record VendoredFile(string Path, string Sha256, string Reason);

// Words left out of the catalog's device and vendor words, with the reason.
internal sealed record GenericWords(string Reason, string[] Words);

// A word a device word sits inside that is not that device word.
internal sealed record CompoundException(string Word, string Reason);

internal sealed record GeneralProperty(string Property, string Reason)
{
    public bool Matches(string key) =>
        Regex.IsMatch(key, "^" + Regex.Escape(Property).Replace("\\*", ".*") + "$", RegexOptions.CultureInvariant);
}

internal static class Rules
{
    /// <summary>The SHA-256 of removed-regions.txt, its CRLF read as LF.
    /// --write-regions prints the value for a new file.</summary>
    public const string RegionPrintsSha256 = "0F1E226A0C6813DB7E961AF990DAC1E8BB95C6DA0D18FC6B576BA0DA5C7D5584";

    /// <summary>The SHA-256 of a text file with CRLF read as LF and a
    /// leading byte order mark dropped, so a checkout's line endings do not
    /// change it.</summary>
    public static string TextSha256(byte[] bytes)
    {
        string s = new UTF8Encoding(false).GetString(bytes);
        if (s.Length > 0 && s[0] == (char)0xFEFF) s = s[1..];
        return Convert.ToHexString(SHA256.HashData(new UTF8Encoding(false).GetBytes(s.Replace("\r\n", "\n"))));
    }

    // ---- rule A's words ----

    // Device and vendor words beside the catalog's: the list rule A had
    // before it read the catalog, and the PlayStation and Switch families,
    // which the catalog writes only inside longer names.
    public static readonly (string Word, string Reason)[] ExtraWords =
    {
        ("switch", "Nintendo Switch"), ("nintendo", "Nintendo"), ("joycon", "Nintendo Joy-Con"), ("gamecube", "Nintendo GameCube"),
        ("wii", "Nintendo Wii"), ("sony", "Sony"), ("dualsense", "Sony DualSense"), ("dualshock", "Sony DualShock"),
        ("ds3", "DualShock 3"), ("ds4", "DualShock 4"), ("ds5", "DualSense"), ("sixaxis", "Sony Sixaxis"),
        ("playstation", "Sony PlayStation"), ("valve", "Valve"), ("steam", "Valve Steam"), ("triton", "the Steam Controller 2's code name"),
        ("deck", "Steam Deck"), ("xbox", "Microsoft Xbox"),
        ("ps", "PlayStation"), ("ps1", "PlayStation"), ("ps2", "PlayStation 2"), ("ps3", "PlayStation 3"), ("ps4", "PlayStation 4"),
        ("ps5", "PlayStation 5"), ("psx", "PlayStation"), ("psvr", "PlayStation VR"), ("psmove", "PlayStation Move"),
        ("ns", "Nintendo Switch"), ("nsw", "Nintendo Switch"), ("nso", "Nintendo Switch Online"), ("ns2", "Nintendo Switch 2"),
        ("switch2", "Nintendo Switch 2"),
    };

    // Words a run of several words of a name may spell besides the
    // catalog's own chunks: brands written with a capital inside, and the
    // Ds and Ps digit forms (Ds3Raw, Ps5Thing).
    public static readonly string[] SpanningWords =
    {
        "dualsense", "dualshock", "playstation", "gamecube", "joycon", "xbox", "ds3", "ds4", "ds5",
        "ps1", "ps2", "ps3", "ps4", "ps5", "ns2", "switch2", "psmove", "psvr",
    };

    // Words shorter than four letters matched inside a longer word too.
    public static readonly string[] ShortCompoundWords = { "wii", "ds3", "ds4", "ds5", "ps3", "ps4", "ps5" };

    // An ending that makes a device word an English word, not a compound:
    // switching, switches, decks, steamed.
    public static readonly string[] CompoundEndings =
        { "s", "es", "ed", "er", "ers", "ing", "ings", "able", "ably", "ly", "y", "le", "les", "ness" };

    public const string ShortWordReason =
        "one or two letters, alone or with digits (x, ms, id, pc, gc, V2, M1, G29): too short to tell a device from an axis, a unit, a version, a register or a loop name in code";

    public static readonly GenericWords[] GenericWords =
    {
        new("an English word of product names and descriptions that code uses for other things", new[]
        {
            "action", "adapter", "adaptive", "advanced", "aeronautical", "alloy", "alpha", "and", "anywhere", "arcade", "axis",
            "back", "base", "big", "bit", "black", "blade", "board", "bundle", "button", "cable", "car", "charging",
            "circuit", "city", "classic", "clone", "club", "combat", "combo", "commander", "community", "compact", "compatible",
            "compliant", "composite", "computer", "console", "control", "controller", "controls", "cooler", "cube", "current",
            "dance", "designer", "device", "devices", "direct", "dongle", "drive", "driving", "dual", "edge", "edition",
            "elite", "engine", "ergonomic", "extreme", "feedback", "fighter", "fighting", "firmware", "flight", "for",
            "force", "formula", "full", "game", "gamepad", "gaming", "gear", "general", "generic", "grip", "guitar",
            "handbrake", "handle", "hero", "hood", "hyper", "input", "internal", "joy", "joypad", "joystick", "keyboard",
            "keypad", "laser", "launch", "light", "limited", "lite", "low", "magic", "man", "master", "mechanical",
            "mechanlcal", "mega", "metal", "mini", "minor", "mode", "model", "modern", "module", "motion", "mouse", "move",
            "natural", "normal", "notebook", "numeric", "one", "online", "open", "optical", "original", "pad", "pairing",
            "panel", "pedals", "platinum", "play", "plus", "point", "pro", "professional", "profile", "quadrant", "race",
            "racer", "racing", "ray", "real", "receiver", "red", "remote", "report", "retro", "rise", "rival", "rudder",
            "school", "sense", "sequential", "series", "shifter", "shock", "side", "silent", "sim", "slim", "source", "space",
            "speed", "sport", "sports", "sprint", "standalone", "standard", "star", "station", "steel", "steering", "stick",
            "studio", "system", "think", "throttle", "tilt", "tiny", "touch", "track", "trackball", "transceiver", "truly",
            "twin", "type", "ultimate", "ultra", "unifying", "velocity", "vibration", "virtual", "was", "war", "wheel",
            "wheelbase", "wing", "wired", "wireless", "with", "yoke", "misc", "python", "strafe", "strait", "stalker",
            "podium", "prima", "pendular", "geo", "neo", "adder", "beast", "bite", "bolt", "dart", "death", "dragon",
            "cleave", "granite", "widow", "winder", "beach", "turtle", "forte", "evo", "gila", "egret", "kana",
        }),
        new("a company's legal form or line of business", new[]
        {
            "corp", "corporation", "inc", "llc", "ltd", "industries", "electronics", "technology", "tech", "products",
            "entertainment", "interactive", "software", "engineering",
        }),
        new("a bus, protocol, unit, standard or layout every device of its kind shares", new[]
        {
            "usb", "hid", "bluetooth", "ble", "dinput", "rgb", "pid", "reportid", "windows", "ansi", "hotas", "ffb",
            "directinput", "4ghz", "type2", "rev1",
        }),
        new("an abbreviation or a roman numeral of a model name (Adv, Alt, III, Rev)", new[]
        {
            "adv", "alt", "iii", "rev", "com", "con", "esp", "sem", "sli", "wan", "rap", "kai", "kay", "mcp", "lcm",
            "mpg", "srw", "srws", "tpr", "vrc", "vrs", "dsv", "ffg", "brd", "blu", "cuh", "zct", "csr", "nxt", "dfp",
        }),
        new("a part of a name the catalog writes with a capital inside, matched whole (BigBen, ShanWan, NicoHood, GamaKay, IntelliMouse)", new[]
        {
            "ben", "shan", "nico", "gama", "intelli", "daemon", "fire", "pulse", "rapid",
        }),
    };

    // Words that hold a device word and name no device.
    public static readonly CompoundException[] CompoundExceptions =
    {
        new("deckle", "a paper-making word, the fixture's example of a word holding deck"),
        new("horizontal", "holds hori (HORI), and names an axis direction"),
        new("vendor", "holds endor (Endor AG, Fanatec's maker), and is the USB vendor of every ID"),
        new("channel", "holds anne (the Obins Anne Pro), and is an audio or IPC channel"),
    };

    // ---- allowed names ----

    public static readonly AllowedName[] Names =
    {
        new(new[] { "HatSwitch" }, new[] { @"sdk\HIDMaestro.Core\Internal\HidReportBuilder.cs" },
            "the HID usage Hat Switch (Generic Desktop 0x39), as linux-hid-ref drivers/hid/hid-bigbenff.c:136 names it"),
        new(new[] { "SafetySwitch", "ActuatorOverrideSwitch" }, new[] { @"sdk\HIDMaestro.Core\HMPidState.cs" },
            "flags of the PID State report, named for their HID PID usages (HMPidState.cs:42, 45)"),
        new(new[] { "ProfileSwitch" }, new[] { @"sdk\HIDMaestro.Core\HMLayout.cs" },
            "a control role of the public layout vocabulary (HMButtonRole), a profile selector switch"),
        new(new[] { "ModeSwitchLow", "ModeSwitchMid", "ModeSwitchHigh" }, new[] { @"sdk\HIDMaestro.Core\HMLayout.cs" },
            "control roles of the public layout vocabulary (HMButtonRole), a flight stick's mode switch positions"),
        new(new[] { "Xbox" }, new[] { @"sdk\HIDMaestro.Core\HMLayout.cs" },
            "HMButtonRole.Xbox: shipped in v1.12.0's stable namespace, names the legend a layout prints on a system button as Guide, Home and Capture do, and no SDK or driver code reads it"),
        new(new[] { "Ps" }, new[] { @"sdk\HIDMaestro.Core\HMLayout.cs" },
            "HMButtonRole.Ps: shipped in v1.12.0's stable namespace, names the legend a layout prints on a system button as Guide, Home and Capture do, and no SDK or driver code reads it"),
        new(new[] { "IsSteamVRInstalled", "IsSteamVRRunning", "SteamVRPath", "SetSteamVRPathHint", "steamVrDir" },
            new[] { @"sdk\HIDMaestro.Core\HMVR.cs" },
            "SteamVR, the OpenVR host HIDMaestro's own VR controller runs under, not an impersonated device"),
        new(new[] { "SteamVrPathRegValue", "FindSteamVR", "SetSteamVRPathHint", "IsSteamVRInstalled", "IsSteamVRRunning", "steamVrDir", "steamVr" },
            new[] { @"sdk\HIDMaestro.Core\Internal\VrDriverBuilder.cs" },
            "SteamVR, the OpenVR host HIDMaestro's own VR controller runs under, not an impersonated device"),
        new(new[] { "UsbMicrosoftOsSpec", "MicrosoftOs" }, new[] { @"sdk\HIDMaestro.Core\Internal\UsbConfigurationSpec.cs" },
            "Microsoft OS descriptors, a USB descriptor standard any device may serve, which the profile's data holds and the persona serves verbatim (UsbDescriptorSet.cs)"),
        new(new[] { "CheckMicrosoftOsFeature", "GetMicrosoftOsFeature" }, new[] { @"sdk\HIDMaestro.Core\Internal\Usbip\UsbDescriptorSet.cs" },
            "Microsoft OS descriptors, a USB descriptor standard any device may serve, which the profile's data holds and the persona serves verbatim"),
        new(new[] { "steam" }, new[] { @"sdk\HIDMaestro.Core\Internal\VrDriverBuilder.cs" },
            "the Steam client's registry key, read only to find the library that holds SteamVR (VrDriverBuilder.cs:103)"),
        new(new[] { "steamRoot" }, new[] { @"sdk\HIDMaestro.Core\Internal\VrDriverBuilder.cs" },
            "the Steam client's install directory, read only to find the library that holds SteamVR (VrDriverBuilder.cs:105)"),
    };

    // The OpenVR SDK header the OpenVR driver builds against. It is Valve's
    // file, copied unedited (vrcontroller.vcxproj:28-31), so its own names
    // (k_pch_SteamVR_*, the Valve copyright) are not HIDMaestro code.
    public static readonly VendoredFile[] Vendored =
    {
        new(@"driver\openvr\third_party\openvr\openvr_driver.h", "1036EFE998D63E82D1D3DB2B32A2F58DF4A8EEAF5280F50AAF28220FF60A40AB",
            "Valve's OpenVR driver API header, copied unedited (driver\\openvr\\vrcontroller.vcxproj:28-31). Its names are the API's own. An edit changes its hash and every rule then applies"),
    };

    // The regions v2.0.0 deletes, at 9bd6279: the 24 of decision (c), and the
    // default device the drivers start as when their configuration is
    // missing (the 045E:028E descriptor, attributes and product string, and
    // the companion's 045E:028E), which DD-6 deletes. The default's lines
    // that describe whatever descriptor is loaded (the HID descriptor's
    // fields, driver.c:2519-2527) stay out, since the driver keeps them.
    // None is exempt from the other rules, and each fails while any of its
    // printed code lines is still in a driver source.
    public static readonly RemovedRegion[] Regions =
    {
        new("sony-calibration-crc", @"driver\driver.c", 24, 127, "Sony calibration tables and the feature CRC", "DD-2"),
        new("ds3-helpers", @"driver\driver.c", 129, 215, "the DualShock 3 sixaxis helpers", "DD-5"),
        new("switch-config", @"driver\driver.c", 472, 518, "the Switch configuration, MAC and idle hold", "DD-5"),
        new("bluetooth-read", @"driver\driver.c", 528, 533, "the Bluetooth value read", "DD-2"),
        new("ds3-sixaxis-read", @"driver\driver.c", 535, 539, "the Ds3Sixaxis value read", "DD-5"),
        new("ds3-output", @"driver\driver.c", 1273, 1324, "Ds3HandleSixaxisOutput", "DD-5"),
        new("switch-responder", @"driver\driver.c", 1357, 1869, "the Switch responder", "DD-5"),
        new("switch-input-return", @"driver\driver.c", 1892, 1901, "ProcessSharedInput's Switch early return", "DD-5"),
        new("ds3-input", @"driver\driver.c", 1960, 1974, "the DualShock 3 input derivation", "DD-5"),
        new("ds3-raw-store", @"driver\driver.c", 2009, 2010, "the Ds3Raw store", "DD-5"),
        new("switch-worker-mapping", @"driver\driver.c", 2275, 2298, "the worker's Switch mapping exception", "DD-5"),
        new("switch-stream-cleanup", @"driver\driver.c", 2328, 2337, "the stream thread's cleanup", "DD-5"),
        new("switch-stream-start", @"driver\driver.c", 2708, 2717, "the stream thread's start", "DD-5"),
        new("switch-read-report", @"driver\driver.c", 2821, 2835, "READ_REPORT's Switch branch", "DD-5"),
        new("ds3-write-report", @"driver\driver.c", 2885, 2889, "WRITE_REPORT's DualShock 3 branch", "DD-5"),
        new("switch-write-report", @"driver\driver.c", 2897, 2912, "WRITE_REPORT's Switch interception", "DD-5"),
        new("ds3-feature", @"driver\driver.c", 3079, 3100, "the DualShock 3 feature report 0", "DD-5"),
        new("sony-feature-table", @"driver\driver.c", 3102, 3372, "the Sony feature table", "DD-2"),
        new("ds3-set-output", @"driver\driver.c", 3491, 3495, "SET_OUTPUT_REPORT's DualShock 3 branch", "DD-5"),
        new("switch-set-output", @"driver\driver.c", 3503, 3516, "SET_OUTPUT_REPORT's Switch interception", "DD-5"),
        new("bluetooth-ds3-fields", @"driver\driver.h", 96, 113, "the Bluetooth and DualShock 3 context fields", "DD-2 and DD-5"),
        new("switch-fields", @"driver\driver.h", 258, 312, "the Switch context fields", "DD-5"),
        new("xusb-gate-state", @"driver\companion.c", 69, 76, "the rumble gate's state", "DD-3"),
        new("xusb-answers", @"driver\companion.c", 834, 1039, "the XUSB answers and the vendor-ID rumble gate", "DD-3"),
        new("default-descriptor", @"driver\driver.h", 589, 650, "the default device's 045E:028E report descriptor", "DD-6"),
        new("default-descriptor-copy", @"driver\driver.c", 2513, 2517, "the copy of the default descriptor at add", "DD-6"),
        new("default-attributes", @"driver\driver.c", 2528, 2533, "the default device's 045E:028E attributes and input length", "DD-6"),
        new("default-product-string", @"driver\driver.c", 2535, 2540, "the default device's product string", "DD-6"),
        new("companion-default-identity", @"driver\companion.c", 458, 459, "the companion's default 045E:028E", "DD-6"),
    };

    // The dispatch of xusb-answers the plan keeps (DD2.plan.md, "The device
    // code that leaves the drivers"): GET_STATE, SET_STATE's case and its
    // publish without the vendor-ID gate around it, POWER_INFO,
    // GET_LED_STATE, WAIT_FOR_INPUT and WAIT_GUIDE, which no device owns.
    public static readonly KeptRange[] Kept =
    {
        new(@"driver\companion.c", 921, 928, "GET_STATE, and SET_STATE's case"),
        new(@"driver\companion.c", 973, 975, "SET_STATE's input buffer"),
        new(@"driver\companion.c", 977, 978, "SET_STATE's publish"),
        new(@"driver\companion.c", 980, 993, "SET_STATE's completion, POWER_INFO and GET_LED_STATE"),
        new(@"driver\companion.c", 1010, 1026, "WAIT_FOR_INPUT and WAIT_GUIDE"),
    };

    // INF model lines the check allows.
    public static readonly AllowedInfLine[] InfLines =
    {
        new(@"driver\hidmaestro_xusb.inf", @"%DeviceDesc% = XUSB_Install, root\VID_045E&PID_028E&XI_00", Companion),
        new(@"driver\hidmaestro_xusb.inf", @"%DeviceDesc% = XUSB_Install, root\VID_045E&PID_0291&XI_00", Companion),
        new(@"driver\hidmaestro_xusb.inf", @"%DeviceDesc% = XUSB_Install, root\VID_045E&PID_0719&XI_00", Companion),
    };

    const string Companion = "the companion's own hardware IDs, which only HIDMaestro devnodes carry. They came from the WGI experiments, no code reads the MatchingDeviceId they set, and the wireless profile's companion keeps PID_0719 when its HID child moves to 0x02A1";

    // Profile properties one catalog profile sets that the check allows:
    // general mechanisms, and data a second device of the same form uses
    // with no new code. Property is Type.Property, * matching any run.
    public static readonly GeneralProperty[] GeneralProperties =
    {
        new("DeviceProtocolSpec.*", "the generic device protocol (DeviceModelSpec.cs, Usbip/DeviceProtocol.cs) the GameCube adapter persona is the model of"),
        new("Protocol*Spec.*", "the generic device protocol (DeviceModelSpec.cs, Usbip/DeviceProtocol.cs) the GameCube adapter persona is the model of"),
        new("Port*Spec.*", "the generic per-port model (PortLayout.cs, HMPort.cs) the GameCube adapter persona is the model of"),
        new("ExtendedReportSpec.Ports", "the generic per-port model (PortLayout.cs, HMPort.cs) the GameCube adapter persona is the model of"),
        new("HMControllerAdapterLayout.*", "the layout of the controller adapter kind (HMLayout.cs), which any adapter profile fills"),
        new("UsbConfigurationSpec.Protocol", "the generic device protocol on a USB/IP configuration"),
        new("UsbConfigurationSpec.WinUsb", "the generic WinUSB binding (WinUsbBinding.cs)"),
        new("WinUsbSpec.*", "the generic WinUSB binding (WinUsbBinding.cs)"),
        new("UsbConfigurationSpec.SharedSerial", "a USB/IP identity choice any persona may make (UsbDescriptorSet.cs:179)"),
        new("UsbConfigurationSpec.OtherSpeedConfigurationDescriptorHex", "a descriptor served verbatim (UsbDescriptorSet.cs:155)"),
        new("UsbConfigurationSpec.MicrosoftOs", "Microsoft OS descriptors served verbatim (UsbDescriptorSet.cs)"),
        new("UsbMicrosoftOsSpec.*", "Microsoft OS descriptors served verbatim (UsbDescriptorSet.cs)"),
        new("ControllerProfile.ConfigurationString", "the configuration's string descriptor, served verbatim (UsbDescriptorSet.cs:201)"),
        new("ControllerProfile.DataReportId", "the input report a profile's data frames fill, any input report its descriptor declares, checked against the descriptor at load (ControllerProfile.ValidateDataReport). One catalog profile sets it today"),
        new("ControllerProfile.SdlMapping", "a mapping string handed to programs (HMProfile.cs:202), read by no device code"),
        new("FieldSpec.Resolution", "a parameter of the u10be codec op, for any 10-bit sensor (VendorBlobProgram.cs:363)"),
        new("FieldSpec.SourceResolution", "a parameter of the u10be codec op, for any 10-bit sensor (VendorBlobProgram.cs:363)"),
        new("FieldSpec.Levels", "the uint8-level-map codec op's table (VendorBlobProgram.cs). dualshock-3-full sets it, and the PS Move's battery byte is the second device: levels 0 to 5, 0xEE charging and 0xEF full (psmoveapi include/psmove.h:194-203 at 97d97e9)"),
        new("FieldSpec.ChargingValue", "the uint8-level-map codec op's charging byte (VendorBlobProgram.cs), 0xEE on the DualShock 3 and on the PS Move (psmoveapi include/psmove.h:201 at 97d97e9)"),
        new("HM*Layout.*", "the layout vocabulary of a layout kind (HMLayout.cs), which every profile of that kind fills"),
        new("HMImu.Magnetometer", "the layout vocabulary's IMU description (HMLayout.cs)"),
        new("HMTriggerButton.*", "the layout vocabulary's trigger description (HMLayout.cs)"),
    };
}
