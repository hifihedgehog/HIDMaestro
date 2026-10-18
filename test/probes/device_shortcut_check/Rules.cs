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

// A string literal the check allows where rule 3 would count it: the
// literal as written, without its quotes, in the file its path names.
internal sealed record AllowedString(string Path, string Text, string Reason)
{
    public bool Matches(Hit h) =>
        h.Group == HitGroups.DeviceString && h.Name == "\"" + Text + "\""
        && Path.Equals(h.File.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
}

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
        new(new[] { "RemovedSteamFirmwareStamp" }, new[] { @"sdk\HIDMaestro.Core\Internal\ControllerProfile.cs" },
            "the steamFirmwareStamp key v1.9.1 to v1.13.0 wrote, read only so a profile that carries it is refused with its replacement named (ValueSources.Validate). "
            + "No answer, report or device reads it. Design note, decision (d) and \"Where errors appear\""),
    };

    // String literals the check allows where rule 3 would count them.
    public static readonly AllowedString[] Strings =
    {
        new(@"sdk\HIDMaestro.Core\Internal\VendorBlobProgram.cs", "ds3-battery",
            "the codec type v2.0.0 drops, compared only to refuse a profile that carries it with its replacement named, uint8-level-map and the "
            + "table to write (VendorBlobProgram.Compile). No field compiles to it. Design note, mechanism 1 and \"What a program sees change\""),
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

    const string XInputIds = "the IDs and flags an XUSB companion reports apart from its HID child's (PresentationPlan, XusbAnswers.Compile), for any pad behind a receiver: xbox-360-wireless today, and every wireless XUSB device PlasticBand captured (ProductId 0 with WIRELESS, the receiver's 045E:0719 in GET_INFORMATION), the Microsoft Wireless Racing Wheel among them (Microsoft Wireless Racing Wheel.txt:1-17)";

    // Discriminators of rule C's per-kind count (Profiles.cs), as
    // Type.Prop: a key on an object of one kind is counted apart from the
    // same key on another.
    public static readonly (string Property, string Reason)[] Discriminators =
    {
        ("FieldSpec.Type", "a field's type picks the codec op it compiles to (VendorBlobProgram.Compile), and each op reads its own parameters, so a center on int16-le reaches code a center on uint8-axis does not"),
    };

    // Profile properties one catalog profile sets that the check allows:
    // general mechanisms, and data a second device of the same form uses
    // with no new code. Property is Type.Property, * matching any run.
    public static readonly GeneralProperty[] GeneralProperties =
    {
        new("Protocol*Spec.*", "the generic device protocol (ProgramForm/ModelSpec.cs, Usbip/DeviceProtocol.cs, Usbip/ProtocolEngine.cs) the GameCube adapter persona is the model of"),
        // v2.0.0, lane DD-4: the protocol engine and report programs
        // (design note, mechanisms 2 and 3). The Switch 2 Pro composite is
        // their first catalog user. The second device of each is the Switch
        // Pro, whose protocol the design note writes in the same keys
        // (mechanism 3's sketch: registers, memory with a value segment,
        // echo and memory reply parts, a reply prefix, decoders, layouts with
        // counters, copies, bit moves, a lookup and fields), which lane DD-5
        // converts.
        new("ControllerProfile.Protocol", "the device protocol at the profile's top level (mechanism 3), read by the shared parse (ProfilePrograms)"),
        new("ControllerProfile.StateBlock", "the state block of a device that builds its reports (mechanism 2). The Switch Pro's 48-byte body is the second (SwitchProPacker.cs at 9bd6279)"),
        new("ControllerProfile.InputReports", "the layouts of a device that builds its reports (mechanism 2). The Switch Pro's 0x30 and 0x3F are the second"),
        new("ControllerProfile.LiveFeatureReports", "a feature report built from the state block (mechanism 2), the DualShock 3's report 0"),
        new("ControllerProfile.Stream", "the idle repeat at a device's real period (DeviceClock, StreamClock.cs), every streaming profile cadence.json names"),
        new("StreamSpec.*", "the idle repeat at a device's real period (StreamClock.cs)"),
        new("StreamPeriodRegisterSpec.*", "a period a host sets through a register, the DualShock 4 over Bluetooth's output report 0x11 interval"),
        new("RegisterSpec.*", "the protocol's registers (mechanism 3): the GameCube adapter's states, the Switch 2 Pro's, the Triton's imuMode"),
        new("CounterSpec.*", "the protocol's counters (mechanism 2): the Switch 2 Pro's report counter, the Switch Pro's timer byte"),
        new("MemorySpec.*", "a memory image a reply reads (mechanism 3): the Switch 2 Pro's flash, the Switch Pro's SPI image (driver.c:1393-1441 at 9bd6279)"),
        new("MemorySegmentSpec.*", "a memory image's segments, constant or a value source (mechanism 3 and 5): the Switch 2 Pro's serial, the Switch Pro's MAC"),
        new("StoreSpec.*", "a command byte stored into a register (mechanism 3): the Switch 2 Pro's report select, the Switch Pro's input mode (driver.c:1639-1703 at 9bd6279)"),
        new("StoreRecordSpec.*", "a register stored from a list of fixed-size records (mechanism 3): the Triton's SET_SETTINGS_VALUES, and the 2015 Steam Controller's, which SDL writes as the same 3-byte records on report 0 (SDL_hidapi_steam.c:1316-1337 at 804cf65)"),
        new("Record*Spec.*", "a register stored from a list of fixed-size records (mechanism 3)"),
        new("MaskedStoreSpec.*", "a command byte masked by a register (mechanism 3): the Switch 2 Pro's feature enable and disable"),
        new("MemoryWriteSpec.*", "command bytes written into a memory image (mechanism 3): the Switch Pro's SPI write"),
        new("EmitOutputSpec.*", "a translated output report raised in place of the command (mechanism 3): the DualShock 3's folded report 0x01 (driver.c:1293-1324 at 9bd6279)"),
        new("ReplyPartSpec.*", "the parts of a protocol reply (mechanism 3): the GameCube adapter's, the Switch 2 Pro's, the Switch Pro's 0x21"),
        new("ByteSourceSpec.*", "an address or length a reply part reads from the command (mechanism 3): the Switch 2 Pro's flash reads, the Switch Pro's SPI reads (address le32 at 11, length at 15, at most 29)"),
        new("Bitmap*Spec.*", "reply bytes chosen by the bits of a command byte (mechanism 3): the Switch 2 Pro's feature info. The Switch 2 Joy-Cons answer the same command (switch2_controller_research commands.md, Get Feature Info)"),
        new("DecoderSpec.*", "a protocol's output decoders (mechanism 3): the Switch 2 Pro's rumble, the Switch Pro's HD rumble"),
        new("DecodeFieldSpec.*", "a decoded output field (mechanism 3)"),
        new("Packed*Spec.*", "the packed-uint bit groups a decoder reads (mechanism 1): the Switch 2 Pro's and the Switch Pro's rumble amplitudes"),
        new("ScaleSpec.*", "the codec's scale and a decoder part's ratio (mechanism 1): the Switch 2 Pro's motion, the Switch Pro's IMU (13371 / 936)"),
        new("InputReportSpec.*", "a layout of a device that builds its reports (mechanism 2)"),
        new("LiveFeatureReportSpec.*", "a feature report built from the state block (mechanism 2)"),
        new("StateBlockSpec.*", "the state block of a device that builds its reports (mechanism 2)"),
        new("ProgramOpSpec.*", "an op of a report program (mechanism 2), the vocabulary every layout, decoder and emitted report is written in"),
        new("ReadSpec.*", "a value a report program reads from the state block (mechanism 2)"),
        new("FieldSpec.HoldWhile", "a sensor timestamp held while a register's condition holds (mechanism 1): the Triton's, frozen while its IMU is off (sc2-research HID_REPORT_FORMAT.md:83 at b46d517). The Switch 2 Pro's 0x05 motion timestamp is written only while its IMU feature is on"),
        new("Port*Spec.*", "the generic per-port model (PortLayout.cs, HMPort.cs) the GameCube adapter persona is the model of"),
        new("ExtendedReportSpec.Ports", "the generic per-port model (PortLayout.cs, HMPort.cs) the GameCube adapter persona is the model of"),
        new("HMControllerAdapterLayout.*", "the layout of the controller adapter kind (HMLayout.cs), which any adapter profile fills"),
        new("UsbConfigurationSpec.WinUsb", "the generic WinUSB binding (WinUsbBinding.cs)"),
        new("WinUsbSpec.*", "the generic WinUSB binding (WinUsbBinding.cs)"),
        new("UsbConfigurationSpec.SharedSerial", "a USB/IP identity choice any persona may make (UsbDescriptorSet.cs:179)"),
        new("UsbConfigurationSpec.OtherSpeedConfigurationDescriptorHex", "a descriptor served verbatim (UsbDescriptorSet.cs:155)"),
        new("UsbConfigurationSpec.MicrosoftOs", "Microsoft OS descriptors served verbatim (UsbDescriptorSet.cs)"),
        new("UsbMicrosoftOsSpec.*", "Microsoft OS descriptors served verbatim (UsbDescriptorSet.cs)"),
        new("ControllerProfile.ConfigurationString", "the configuration's string descriptor, served verbatim (UsbDescriptorSet.cs:201)"),
        new("ControllerProfile.DataReportId", "the input report a profile's data frames fill, any input report its descriptor declares, checked against the descriptor at load (ControllerProfile.ValidateDataReport). One catalog profile sets it today"),
        new("ControllerProfile.SdlMapping", "a mapping string handed to programs (HMProfile.cs:202), read by no device code"),
        new("XInputSpec.Information", XInputIds),
        new("XInputSpec.Capabilities", XInputIds),
        new("XInputIdSpec.*", XInputIds),
        new("XInputCapabilitiesSpec.*", XInputIds),
        new("ControllerProfile.BluetoothVendorIdSource", "the Device ID's vendor ID source in a Bluetooth device's hardware IDs (PresentationPlan.BluetoothIds), for any Bluetooth device whose VID the Bluetooth SIG assigned: the Luna's 0x0171 today, and Apple's Bluetooth keyboards and mice under 0x004C (Linux hid-ids.h:98, hid-apple.c:1084)"),
        new("FieldSpec.Resolution", "a parameter of the u10be codec op, for any 10-bit sensor (VendorBlobProgram.cs:363)"),
        new("FieldSpec.SourceResolution", "a parameter of the u10be codec op, for any 10-bit sensor (VendorBlobProgram.cs:363)"),
        new("FieldSpec.Levels", "the uint8-level-map codec op's table (VendorBlobProgram.cs). dualshock-3-full sets it, and the PS Move on Bluetooth is the second device: levels 0 to 5, 0xEE charging and 0xEF full (psmoveapi include/psmove.h:194-203 at 97d97e9). The catalog's ps-move is a Move on USB, which sends only 0xEE and 0xEF (moveonpc Input report, Battery level), so its table is 0xEE at every level"),
        new("FieldSpec.Clock", "the timestamp accumulator of a uint16-le or uint32-le sensorTimestamp (VendorBlobCodec.cs, EncoderState.AdvanceTimestamp). steam-controller-2 sets it, and the Switch 2 Pro's report 0x05 carries the second device's: a 32-bit IMU timestamp that advances with time, which SDL calibrates against its 4 ms report period (SDL_hidapi_switch2.c:1148-1157 at 804cf65)"),
        new("FieldSpec.ChargingValue", "the uint8-level-map codec op's charging byte (VendorBlobProgram.cs), 0xEE on the DualShock 3 and on the PS Move (psmoveapi include/psmove.h:201 at 97d97e9)"),
        new("FieldSpec.Scale", "an int16-le field's calibrated motion, counts per g or per degree a second (VendorBlobCodec.WriteCalibratedI16). ps-move sets it, and the Switch Pro's report 0x30 is the second device: g x 4096 and degrees a second x 13371/936 (SwitchProPacker.cs:93-102, the inverse of SDL_hidapi_switch.c:60-64 at 3.4.18), the field lists vendor_blob_golden_check's vocabulary holds against SwitchProPacker"),
        new("ScaleSpec.*", "the counts per unit of FieldSpec.Scale, a number or a ratio"),
        new("FieldSpec.Negate", "an int16-le field's calibrated motion on an axis the device counts the other way. ps-move sets it, and the Switch Pro's report 0x30 is the second device, whose X and Z words run against SDL's frame (vendor_blob_golden_check's Switch Pro field lists)"),
        new("FieldSpec.Repeat", "an int16-le field written in each motion frame a report carries. ps-move sets it for its two half-frames, and the Switch Pro's report 0x30 is the second device, three frames 12 bytes apart (vendor_blob_golden_check's Switch Pro field lists)"),
        new("ControllerProfile.ValueSources", "the value sources of mechanism 5 (ValueSources.cs). steam-controller-2's keyedHexFile reads Steam's updater config, and Valve's puck is the second device: its PROTEUS_FW_TS sits in the same file and takes one patch (design note, mechanism 5)"),
        new("ValueSourceSpec.*", "a keyedHexFile value source (ValueSources.cs), any KEY:HEX line of a file found under registry or known-folder roots. Valve's puck reads PROTEUS_FW_TS from the file steam-controller-2 reads"),
        new("ValueRootSpec.*", "a keyedHexFile source's roots (ValueSources.Locate), any registry value or known folder"),
        new("FeatureStubReport.Patch", "per-controller bytes of a feature answer from a value source (ValueSources.Apply). The Sony answer models patch the index into their MACs, and Valve's puck patches PROTEUS_FW_TS"),
        new("AnswerPatchSpec.*", "per-controller bytes of a feature answer from a value source (ValueSources.Apply). The Sony answer models patch the index into their MACs, and Valve's puck patches PROTEUS_FW_TS"),
        new("AnswerMessageSpec.*", "the message a feature answer follows (FeatureStubTable.Find, design note mechanism 4): any report a SET arrives on, any offset and value. steam-controller-2 frames its command channel on report 1, and dualsense-tester's test channel, a SET on DualSense report 0x80 answered on 0x81, is the second device (ds.util.ts:164-211 at ff340fe)"),
        new("FeatureStubReport.ReplyHead", "an answer that starts with the request's report ID (FeatureStubTable.Answer, mechanism 4), for a command channel on any numbered report. steam-controller-2 sets it on report 1, and the DualSense test channel's answers on 0x81 start with 0x81 the same way (dualsense-tester ds.util.ts:165 at ff340fe)"),
        new("ExtendedReportSpec.Comment", "documentation on a report, read by no code, as on a feature answer"),
        new("HM*Layout.*", "the layout vocabulary of a layout kind (HMLayout.cs), which every profile of that kind fills"),
        new("HMImu.Magnetometer", "the layout vocabulary's IMU description (HMLayout.cs)"),
        new("HMTriggerButton.*", "the layout vocabulary's trigger description (HMLayout.cs)"),
        // v2.0.0, lane DD-7: rule C counts a key per field type and a value
        // code names (Profiles.cs). Each entry below names the second device
        // of its form, as the lanes above did for whole keys.
        new("HM*.Axis=*", "the HID usage a layout control sits on (HMAxis, Generic Desktop X to Dial), read as data: FindByAxis and FindPedal look the control up by it (HidReportBuilder.cs). "
            + "The SDK names X, Y, Z, Rx, Ry and Rz only as a gamepad's default sticks and triggers (HMGamepadState.cs:288-299, HMController.cs:555-556), never a pedal's, an encoder's or a rudder's"),
        new("HMRotaryEncoder.Role=*", "the legend of a wheel's rotary encoder, read by no code. The SDK names Accelerator and Brake only when it maps a wheel's or a pedal set's pedals (HidReportBuilder.cs:310-321, FindPedal over Pedals)"),
        new("ControllerProfile.StateBlockFields", "stateBlock's field list in the codec's vocabulary (mechanism 2), the same key as ControllerProfile.StateBlock. The Switch Pro's and the DualShock 3's state blocks are the second and third (lane DD-5)"),
        new("FieldSpec.Min", "a field's wire value at the SDK's 0 (decision (a), mechanism 1). The Switch 2 Pro's state block runs its sticks 0 to 4095, and the Switch Pro's stick12-pair runs 512 to 3584 (lane DD-5)"),
        new("FieldSpec.Min@int16-axis", "a 12-bit stick held in a 16-bit state block word, 0 to 4095, which a program packs into the report (the Switch 2 Pro composite's pack12). "
            + "The Switch 2 GameCube controller sends its sticks in the same 12-bit form at the same offsets (SDL_hidapi_switch2.c:684-768 at 804cf65, HandleGameCubeState)"),
        new("FieldSpec.Max@int16-axis", "the top of a 12-bit stick held in a 16-bit state block word. The Switch 2 GameCube controller's sticks are the second (SDL_hidapi_switch2.c:684-768 at 804cf65)"),
        new("FieldSpec.Clock=accumulate", "the timestamp accumulator (FieldSpec.Clock's entry): steam-controller-2's report 0x42, and the Switch 2 Pro's report 0x05, whose 32-bit timestamp at 0x2B to 0x2E counts microseconds (SDL_hidapi_switch2.c:1148-1157 at 804cf65)"),
        new("FieldSpec.Clock@uint32-le", "a 32-bit sensor timestamp from the accumulator. The Switch 2 Pro's report 0x05 carries the second (SDL_hidapi_switch2.c:1148-1157 at 804cf65)"),
        new("FieldSpec.Divisor@uint32-le", "the accumulator's ticks per wire count on a 32-bit timestamp: 3 turns the SDK's 1/3 microsecond ticks into microseconds, the unit of the Switch 2 Pro's report 0x05 timestamp (SDL_hidapi_switch2.c:1148-1157 at 804cf65)"),
        new("FieldSpec.HoldWhile@uint32-le", "a 32-bit sensor timestamp held while a register's condition holds (FieldSpec.HoldWhile's entry). The Switch 2 Pro's report 0x05 timestamp is written only while its IMU feature is on"),
        new("FeatureStubReport.ReplyHead=requestReportId", "an answer that starts with the request's report ID (FeatureStubReport.ReplyHead's entry): steam-controller-2 on report 1, and dualsense-tester's test channel on 0x81 (ds.util.ts:165 at ff340fe)"),
        new("FieldSpec.Center@int16-le", "a 16-bit little-endian motion word in offset binary, at rest at its center (VendorBlobCodec, OffsetBinary): ps-move's at 32768. Third-party PS3 controllers send their accelerometer as a 16-bit "
            + "little-endian word at rest at 512 (SDL_hidapi_ps3.c:350-353 and 1025-1031 at 804cf65, value - 512 over 113), as do the PS3 instruments (PlasticBand Docs/Base Reports/PS3.md:144-160 at b129263)"),
        new("FieldSpec.Repeat@int16-le", "an int16-le motion word written in each frame a report carries (FieldSpec.Repeat's entry): ps-move's two half-frames, and the Switch Pro's report 0x30, three frames 12 bytes apart (lane DD-5's switch-pro stateBlock)"),
        new("FieldSpec.Invert@uint8-axis", "an 8-bit axis that grows upward, the gamecube-adapter's Y. EVORETRO's GameCube adapters in PC mode (0079:1843, 1844, 1846) send the same, and SDL reads them as 0xFF minus the byte "
            + "(SDL_hidapi_gamecube.c:81-87 and 344-358 at 804cf65)"),
        new("FieldSpec.Type=uint8-pressure", "a button's pressure, 0 at rest to 0xFF. Third-party PS3 controllers send twelve (SDL_hidapi_ps3.c:992-1023 at 804cf65, button_axis_offsets, read when has_analog_buttons), "
            + "and so do the PS3 instruments (PlasticBand Docs/Base Reports/PS3.md:105-134 at b129263)"),
        new("FieldSpec.Semantic=pressure*", "the SDK's button pressures (HMGamepadState), the values a uint8-pressure field reads. Third-party PS3 controllers and the PS3 instruments send the same twelve "
            + "(SDL_hidapi_ps3.c:992-1023 at 804cf65, PlasticBand Docs/Base Reports/PS3.md:105-134 at b129263)"),
        new("FieldSpec.Semantic@button-mask", "a name on an input report's button mask, read by no code: an input mask writes the buttons it lists (VendorBlobCodec.Encode's button-mask case), and only an output report's decode takes its semantic as the key. "
            + "steam-deck-composite names its 64-bit mask"),
        new("FieldSpec.Type=int16-pad-or-stick", "the 2015 Steam Controller's shared pad and stick pair (VendorBlobCodec's I16PadOrStick). Its wireless receiver (28DE:1142) sends the same report, which hid-steam reads with the wired controller's "
            + "parser, the touch bit choosing pad or stick (linux-hid-ref drivers/hid/hid-steam.c:2038-2072 and 2736-2745 at d24e8ac)"),
        new("FieldSpec.Type=uint10-be", "a 10-bit big-endian motion word at rest at 512, the DualShock 3's (SDL_hidapi_ps3.c:343-348 at 804cf65). The Sixaxis, the controller the DualShock 3 replaced, sends the same report under the same "
            + "054C:0268 (hid-sony.c:2486-2487 at d24e8ac matches both). No device of another line sends this word"),
    };
}
