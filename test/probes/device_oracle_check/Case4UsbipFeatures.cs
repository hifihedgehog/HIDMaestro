// Case 4: feature answers on the USB/IP backend at 9bd6279, for every Valve
// and Sony USB/IP composite in the catalog.
//
// The device is the one usbip_server_check --offline builds: a
// UsbipEmulatedDevice made without its constructor, which would map the
// controller's Global\ sections, holding only what the control path reads
// (usbip_server_check Program.cs:1229-1257, InputPumpWakeOffline). Its
// feature table is built as the constructor builds it
// (UsbipEmulatedDevice.cs:279), through
// FeatureStubTable.From with a source for Steam's firmware builds, the seam
// valve_persona_check and valve_firmware_check already use
// (FeatureStubTable.cs:59-62). Each request goes in through HandleSubmit,
// endpoint 0, as the connection's reader hands it over, and its RET_SUBMIT
// comes back over a connected pair of local (AF_UNIX) sockets standing in
// for the TCP connection, so the case reads the status and bytes the host
// would. The output ring is a block of process memory the device publishes
// into as it would into the section (PublishOutput, 631-661): a SET's data
// and each answered GET's feature-read notice are outputs too.
//
// Steam's firmware stamp. SteamHardwareUpdater.CurrentStamp finds Steam
// through HKCU\Software\Valve\Steam SteamPath, then HKLM\SOFTWARE\
// WOW6432Node\Valve\Steam InstallPath, then Program Files (x86)\Steam
// (SteamHardwareUpdater.cs:70-95), and reads bin\hardwareupdater\
// hardwareupdater.cfg under it on every call (40-53). The case writes
// neither the registry nor Steam's folder. It keeps the same relative
// layout under a temporary folder of its own and hands the table a source
// that reads that file the way CurrentStamp reads Steam's, through
// SteamHardwareUpdater.ParseStamp. Three states: no file, the config as
// Steam ships it with TRITON_FW_TS 6A628345 (valve_persona_check
// Program.cs:487-488), and the same config with TRITON_FW_TS FFFFFFFF.
//
// Only NewDevice and StampSource below touch the device's fields and the
// stamp seam, so a lane that replaces either replays this case by changing
// those two.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal sealed class Case4UsbipFeatures : IOracleCase
{
    public string Name => "case4-usbip-features";

    /// <summary>The case's own stream of the run's generator.</summary>
    const ulong RngStream = 0x5553_4246_4541_5434; // "USBFEAT4"

    const ushort SonyVid = 0x054C, ValveVid = 0x28DE;
    static readonly int[] Indexes = { 0, 1, 15 };
    const int RandomPairs = 10_000;

    // The report IDs the Sony code answers (UsbipEmulatedDevice.cs:1367-1440),
    // asked of every profile beside the IDs its descriptor declares.
    static readonly byte[] SonyCodeIds = { 0x02, 0x05, 0x09, 0x12, 0x20, 0x22, 0xA3 };

    // Every message and parameter the Valve profiles at 9bd6279 declare in
    // featureStubs (profiles/valve/*.json): 0x83 attributes, 0xAE strings at
    // indexes 0, 1 and 3 and the entry for any other index (2 and 0xFF
    // reach it), 0xB4, and 0x87 answered by echo. Asked of every Valve
    // profile, so the request list never follows the table it tests.
    static readonly (byte Message, int Param)[] ValveMessages =
    {
        (0x83, -1), (0xAE, 0), (0xAE, 1), (0xAE, 2), (0xAE, 3), (0xAE, 0xFF), (0xB4, -1), (0x87, -1),
    };

    // The messages Steam wrote to each persona in S54, by kind, from the
    // probe's log (%TEMP%\HIDMaestro_probe_ValveSteamCheck.log lines 15, 31
    // and 47, the same sets in the run's session record). The probe records
    // no payload, no order and no GET, so each is sent as the message alone
    // with a zero length byte, and read back.
    static readonly Dictionary<string, byte[]> S54Messages = new()
    {
        ["steam-deck-composite"] = new byte[] { 0x00, 0xAE, 0xC1, 0xDC, 0xE2, 0x81, 0x83, 0x87, 0x8F },
        ["steam-controller-composite"] = new byte[] { 0x00, 0xAE, 0x81, 0x83, 0x87 },
        ["steam-controller-2"] = new byte[] { 0x01, 0xAE, 0xC1, 0xDC, 0xE2, 0xF2, 0x81, 0x83, 0x87 },
    };

    // Where Steam keeps its updater's config under its install, which
    // steam-controller-2's keyedHexFile source names
    // (profiles/valve/steam-controller-2.json, valueSources.steamUpdater).
    const string UpdaterRelativeDirectory = @"bin\hardwareupdater";
    const string UpdaterConfigName = "hardwareupdater.cfg";

    static readonly (string Name, string? Cfg)[] StampStates =
    {
        ("absent", null),
        ("6A628345", "MUST_UPDATE_TRITON_FW_TS:6A18D057\r\nMUST_UPDATE_PROTEUS_FW_TS:6A18D053\r\n"
                   + "TRITON_FW_TS:6A628345\r\nPROTEUS_FW_TS:6A628359"),
        ("FFFFFFFF", "MUST_UPDATE_TRITON_FW_TS:6A18D057\r\nMUST_UPDATE_PROTEUS_FW_TS:6A18D053\r\n"
                   + "TRITON_FW_TS:FFFFFFFF\r\nPROTEUS_FW_TS:6A628359"),
    };

    public void Record(GoldenCase golden, Options options)
    {
        var rng = new Rng(options.Seed, RngStream);
        var profiles = ProfileDatabase.LoadEmbedded().All
            .Where(p => p.RequiresUsbipBackend && (p.VendorId == SonyVid || p.VendorId == ValveVid))
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
        if (profiles.Count == 0) throw new InvalidOperationException("No Valve or Sony USB/IP composite in the catalog.");

        string root = Path.Combine(Path.GetTempPath(), $"hm-device-oracle-{Environment.ProcessId}");
        string cfg = Path.Combine(root, UpdaterRelativeDirectory, UpdaterConfigName);
        using var host = new Host(Path.Combine(Path.GetTempPath(), $"hmoracle{Environment.ProcessId}.sock"));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
            foreach (var profile in profiles)
            {
                bool valve = profile.VendorId == ValveVid;
                var featureIds = FeatureReportIds(profile.GetDescriptorBytes()!);
                // The ID a Valve command-channel write carries: the
                // descriptor's first nonzero feature report ID, or none.
                byte channel = featureIds.FirstOrDefault(id => id != 0);
                foreach (var (stampName, stampText) in StampStates)
                {
                    if (stampText == null) File.Delete(cfg);
                    else File.WriteAllText(cfg, stampText);
                    var source = StampSource(cfg);
                    var devices = Indexes.Select(i => new Device(golden, host, profile, i, source)).ToArray();
                    string at = $"usbip-feature/{profile.Id}/stamp-{stampName}";
                    try
                    {
                        Declared(golden, at, devices, featureIds, valve, channel);
                        Sweep(golden, at, devices, valve, channel);
                        if (valve)
                        {
                            SdlResetSteamController(golden, at, devices);
                            SdlDeck(golden, at, devices);
                            SdlTriton(golden, at, devices);
                            S54(golden, at, devices, S54Messages.TryGetValue(profile.Id, out var m) ? m : Array.Empty<byte>(), channel);
                        }
                        Random(golden, at, devices, rng, featureIds, valve, channel);
                    }
                    finally
                    {
                        foreach (var d in devices) d.Dispose();
                    }
                }
            }
            // Round 2. Nothing below draws on the generator, so the streams
            // above keep their bytes.
            foreach (var profile in profiles.Where(p => p.Id.StartsWith("dualsense", StringComparison.Ordinal)))
                TestChannel(golden, host, profile);
            foreach (var (baseId, pid) in OtherSonyPids)
            {
                var basis = profiles.FirstOrDefault(p => p.Id == baseId)
                    ?? throw new InvalidOperationException($"No profile {baseId} among the composites.");
                SonyPid(golden, host, basis, pid);
            }
            StampParse(golden);
            // Round 3.
            var triton = profiles.First(p => p.Id == "steam-controller-2");
            StampLookup(golden, triton);
            StampRefusal(golden, triton);
        }
        finally
        {
            if (File.Exists(cfg)) File.Delete(cfg);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ── Round 2 ──────────────────────────────────────────────────────────

    /// <summary>dualsense-tester's test channel (ff340fe): sendTestCommand
    /// writes feature 0x80 with [device][action], zero-padded to the report
    /// count of 0x80's first item (ds.util.ts:93-118, 194-211), then reads
    /// feature 0x81 until it answers (163-189). The case writes SYSTEM (1)
    /// READ_PCBAID (4) (ds.util.ts:342, ds.type.ts:53-54, 111) and reads 0x81
    /// at every wLength, after a write each time and with no write at all,
    /// on each index. Whatever answers today is recorded.</summary>
    static void TestChannel(GoldenCase g, Host host, ControllerProfile profile)
    {
        g.Stream($"usbip-feature/{profile.Id}/test-channel");
        var desc = profile.GetDescriptorBytes()!;
        int count80 = FirstFeatureItemCount(desc, 0x80);
        g.Out("count-80", BitConverter.GetBytes(count80));
        foreach (int index in Indexes)
        {
            var d = new Device(g, host, profile, index, _ => null);
            try
            {
                d.Marker();
                for (int w = 0; w <= 65; w++) d.Get(0x81, (ushort)w);
                for (int w = 0; w <= 65; w++)
                {
                    var payload = new byte[1 + count80];
                    payload[0] = 0x80;
                    payload[1] = 1;   // DualSenseTestDeviceId.SYSTEM
                    payload[2] = 4;   // DualSenseTestActionId.READ_PCBAID
                    d.Set(0x80, payload);
                    d.Get(0x81, (ushort)w);
                }
            }
            finally
            {
                d.Dispose();
            }
        }
    }

    /// <summary>The report count of the first main item of a feature report
    /// ID, which dualsense-tester takes as the report's length
    /// (ds.util.ts:99). 0 when the descriptor has no such report.</summary>
    static int FirstFeatureItemCount(byte[] desc, byte wanted)
    {
        var stack = new Stack<(byte, uint)>();
        byte reportId = 0;
        uint count = 0;
        for (int i = 0; i < desc.Length;)
        {
            byte prefix = desc[i];
            if (prefix == 0xFE)
            {
                i += 3 + (i + 1 < desc.Length ? desc[i + 1] : 0);
                continue;
            }
            int size = (prefix & 0x03) == 3 ? 4 : prefix & 0x03;
            uint value = 0;
            for (int k = 0; k < size && i + 1 + k < desc.Length; k++) value |= (uint)desc[i + 1 + k] << (8 * k);
            switch (prefix & 0xFC)
            {
                case 0x84: reportId = (byte)value; break;                    // Report ID
                case 0x94: count = value; break;                             // Report Count
                case 0xA4: stack.Push((reportId, count)); break;             // Push
                case 0xB4: if (stack.Count > 0) (reportId, count) = stack.Pop(); break; // Pop
                case 0xB0: if (reportId == wanted) return (int)count; break; // Feature
            }
            i += 1 + size;
        }
        return 0;
    }

    // Product IDs the USB/IP Sony code tests (UsbipEmulatedDevice.cs:1375-1377,
    // 1401) that no composite in the catalog carries: 0x0BA0 (in the
    // DualShock 4 calibration order set) and 0x05C4 (the first DualShock 4)
    // on the DS4 composite, and 0x0BA0 on the DualSense composite, whose
    // calibration order follows the ID alone.
    static readonly (string BaseId, ushort Pid)[] OtherSonyPids =
    {
        ("dualshock-4-v2-composite", 0x0BA0),
        ("dualshock-4-v2-composite", 0x05C4),
        ("dualsense-composite", 0x0BA0),
    };

    /// <summary>A catalog composite configured with another product ID: its
    /// JSON with pid and the device descriptor's idProduct changed, which
    /// UsbDescriptorSet requires to agree (UsbDescriptorSet.cs:204-209). Every
    /// declared and Sony-coded ID at wLength 0 to 65 on each index, with no
    /// Steam stamp source.</summary>
    static void SonyPid(GoldenCase g, Host host, ControllerProfile basis, ushort pid)
    {
        var json = JsonSerializer.Serialize(basis, HMJsonContext.Default.ControllerProfile);
        var p = JsonSerializer.Deserialize(json, HMJsonContext.Default.ControllerProfile)!;
        p.Pid = $"0x{pid:X4}";
        var dd = Convert.FromHexString(p.UsbConfiguration!.DeviceDescriptorHex
            ?? throw new InvalidDataException($"{basis.Id} has no device descriptor."));
        dd[10] = (byte)pid;
        dd[11] = (byte)(pid >> 8);
        p.UsbConfiguration.DeviceDescriptorHex = Convert.ToHexString(dd).ToLowerInvariant();
        g.Stream($"usbip-feature/{basis.Id}/pid-{pid:X4}");
        var featureIds = FeatureReportIds(p.GetDescriptorBytes()!);
        g.Out("feature-ids", featureIds);
        var ids = featureIds.Concat(SonyCodeIds).Distinct().OrderBy(b => b).ToArray();
        foreach (int index in Indexes)
        {
            var d = new Device(g, host, p, index, _ => null);
            try
            {
                d.Marker();
                foreach (byte id in ids)
                    for (int w = 0; w <= 65; w++) d.Get(id, (ushort)w);
            }
            finally
            {
                d.Dispose();
            }
        }
    }

    /// <summary>SteamHardwareUpdater.ParseStamp, the part of the Steam stamp
    /// source that runs the same on every machine, over the config as Steam
    /// ships it (valve_persona_check Program.cs:487-488) and variants of each
    /// rule (SteamHardwareUpdater.cs:97-117): case, whitespace, the 0x
    /// prefix, nine digits, zero, a non-hex value, the MUST_UPDATE_ prefix,
    /// line ends, a missing colon. The lookup of Steam's folder and file is
    /// not recorded: it reads this machine's registry and Steam install
    /// (70-95).</summary>
    static void StampParse(GoldenCase g)
    {
        g.Stream("steam-stamp/parse/corpus");
        const string Shipped = "MUST_UPDATE_TRITON_FW_TS:6A18D057\r\nMUST_UPDATE_PROTEUS_FW_TS:6A18D053\r\n"
                             + "TRITON_FW_TS:6A628345\r\nPROTEUS_FW_TS:6A628359";
        string[] texts =
        {
            Shipped,
            Shipped.Replace("\r\n", "\n"),
            "TRITON_FW_TS:6A628345",
            "triton_fw_ts:6A628345",
            "TRITON_FW_TS:6a628345",
            "  TRITON_FW_TS  :  6A628345  ",
            "\tTRITON_FW_TS:\t6A628345\t",
            "TRITON_FW_TS:0x6A628345",
            "TRITON_FW_TS:0X6A628345",
            "TRITON_FW_TS:0x",
            "TRITON_FW_TS:",
            "TRITON_FW_TS:1",
            "TRITON_FW_TS:FFFFFFFF",
            "TRITON_FW_TS:0FFFFFFFF",
            "TRITON_FW_TS:123456789",
            "TRITON_FW_TS:0",
            "TRITON_FW_TS:00000000",
            "TRITON_FW_TS:zz",
            "TRITON_FW_TS:-1",
            "TRITON_FW_TS:+1",
            "TRITON_FW_TS 6A628345",
            "TRITON_FW_TS=6A628345",
            ":6A628345",
            "MUST_UPDATE_TRITON_FW_TS:6A18D057",
            "TRITON_FW_TS:1\nTRITON_FW_TS:2",
            "TRITON_FW_TS:zz\nTRITON_FW_TS:2",
            "X_TRITON_FW_TS:5\nTRITON_FW_TS:6",
            "TRITON_FW_TS:6A628345:7",
            "",
        };
        foreach (string text in texts)
            foreach (string key in new[] { "TRITON_FW_TS", "PROTEUS_FW_TS", "NEREID_FW_TS", "triton_fw_ts" })
            {
                uint? v = ValueSources.ParseKeyedHex(text, key);
                var o = new byte[5];
                o[0] = (byte)(v.HasValue ? 1 : 0);
                BinaryPrimitives.WriteUInt32LittleEndian(o.AsSpan(1), v ?? 0);
                g.Out("parse", System.Text.Encoding.ASCII.GetBytes(key + "|" + text).Concat(o).ToArray());
            }
    }

    /// <summary>Steam's firmware builds as the table asks for them: the
    /// config file read on every call, as SteamHardwareUpdater.CurrentStamp
    /// reads Steam's (SteamHardwareUpdater.cs:42-52), and null without one.</summary>
    static ValueSources.FileReader StampSource(string cfg) =>
        _ => File.Exists(cfg) ? File.ReadAllText(cfg) : null;

    // ── Sequences ────────────────────────────────────────────────────────

    /// <summary>Every declared ID at wLength 0 to 65 on each index. On a
    /// Valve profile, first with no message written, then after a SET of
    /// each declared message, as SDL frames one: 64 bytes, the message, its
    /// length, its parameter.</summary>
    static void Declared(GoldenCase g, string at, Device[] devices, byte[] featureIds, bool valve, byte channel)
    {
        g.Stream($"{at}/declared");
        g.Out("feature-ids", featureIds);
        var ids = featureIds.Concat(SonyCodeIds).Distinct().OrderBy(b => b).ToArray();
        foreach (var d in devices)
        {
            d.Marker();
            foreach (byte id in ids)
                for (int w = 0; w <= 65; w++) d.Get(id, (ushort)w);
            if (!valve) continue;
            foreach (var (message, param) in ValveMessages)
            {
                var payload = Framed(channel, message, param >= 0 ? new[] { (byte)param } : Array.Empty<byte>());
                // 0x87 is answered by echo: write a settings block, SDL's own
                // (SDL_hidapi_steam.c:539-553).
                if (message == 0x87) payload = Framed(channel, 0x87, SdlSettings);
                d.Set(channel, payload);
                for (int w = 0; w <= 65; w++) d.Get(channel, (ushort)w);
            }
        }
    }

    /// <summary>Every report ID at wLength 64 and 65, and on a Valve
    /// profile every message byte written and read back at 64, on each
    /// index. A GET on each secondary HID interface.</summary>
    static void Sweep(GoldenCase g, string at, Device[] devices, bool valve, byte channel)
    {
        g.Stream($"{at}/sweep");
        foreach (var d in devices)
        {
            d.Marker();
            for (int id = 0; id < 256; id++)
            {
                d.Get((byte)id, 64);
                d.Get((byte)id, 65);
            }
            foreach (byte iface in d.SecondaryInterfaces) d.Get(0, 64, iface);
            if (!valve) continue;
            for (int m = 0; m < 256; m++)
            {
                d.Set(channel, Framed(channel, (byte)m, Array.Empty<byte>()));
                d.Get(channel, 64);
            }
        }
    }

    // SDL_hidapi_steam.c:539-553 with ENABLE_MOUSE_MODE undefined (187):
    // SETTING_WIRELESS_PACKET_VERSION (49) 2, SETTING_LEFT_TRACKPAD_MODE (7)
    // and SETTING_RIGHT_TRACKPAD_MODE (8) TRACKPAD_NONE (7),
    // SETTING_SMOOTH_ABSOLUTE_MOUSE (24) 0, three bytes each
    // (steam/controller_constants.h:401-410 and 421-480, controller_structs.h:36-40).
    static readonly byte[] SdlSettings = { 49, 2, 0, 7, 7, 0, 8, 7, 0, 24, 0, 0 };

    /// <summary>SDL's ResetSteamController over USB, SDL-reference 804cf65
    /// SDL_hidapi_steam.c:441-615, with 560-612 left out by ENABLE_MOUSE_MODE
    /// undefined (187). Each write is a 65-byte buffer with
    /// report ID 0 (SetFeatureReport, 293-341, sends 65 bytes on USB), which
    /// reaches the device as a 64-byte data stage, wValue 0x0300. Each read
    /// asks 65 bytes with ID 0, wLength 64 (GetFeatureReport, 343-413), and
    /// ReadResponse (415-433) reads up to 10 times until byte 1 of SDL's
    /// buffer, byte 0 of the data, is the message.</summary>
    static void SdlResetSteamController(GoldenCase g, string at, Device[] devices)
    {
        g.Stream($"{at}/sdl-reset-steam-controller");
        foreach (var d in devices)
        {
            d.Marker();
            d.Set(0, Sdl65(0x83, Array.Empty<byte>(), 0));          // 454-457, ID_GET_ATTRIBUTES_VALUES
            ReadResponse(d, 0x83);                                  // 467
            d.Set(0, Sdl65(0x81, Array.Empty<byte>(), 0));          // 508-511, ID_CLEAR_DIGITAL_MAPPINGS
            d.Set(0, Sdl65(0x8E, new byte[] { 0 }, 0));             // 520-523, ID_LOAD_DEFAULT_SETTINGS
            d.Set(0, Sdl65(0x87, SdlSettings, (byte)SdlSettings.Length)); // 538-553, ID_SET_SETTINGS_VALUES
            // What a host reads after it: not SDL's, the effect of the writes.
            d.Get(0, 64);
            d.Get(0, 65);
        }
    }

    static void ReadResponse(Device d, byte message)
    {
        for (int tries = 0; tries < 10; tries++)
        {
            var (status, data) = d.Get(0, 64);
            if (status < 0) continue;
            if (data.Length > 0 && data[0] == message) return;
        }
    }

    /// <summary>SDL's Steam Deck start, SDL_hidapi_steamdeck.c:95-129
    /// (DisableDeckLizardMode, called at 345) and the watchdog it repeats
    /// (131-157, called at 381). A 65-byte buffer with report ID 0 for each
    /// write and read. The second write reuses the first's buffer. Settings: 24 to 0, 7 and
    /// 8 to TRACKPAD_NONE (7), 52 and 53 (SETTING_LEFT_ and
    /// RIGHT_TRACKPAD_CLICK_PRESSURE) to 0xFFFF.</summary>
    static void SdlDeck(GoldenCase g, string at, Device[] devices)
    {
        g.Stream($"{at}/sdl-deck");
        foreach (var d in devices)
        {
            d.Marker();
            d.Set(0, Sdl65(0x81, Array.Empty<byte>(), 0));
            d.Set(0, Sdl65(0x87, new byte[] { 24, 0, 0, 7, 7, 0, 8, 7, 0, 52, 0xFF, 0xFF, 53, 0xFF, 0xFF }, 15));
            d.Get(0, 64);
            d.Set(0, Sdl65(0x81, Array.Empty<byte>(), 0));
            d.Set(0, Sdl65(0x87, new byte[] { 8, 7, 0 }, 3));
            d.Get(0, 64);
        }
    }

    /// <summary>SDL's Triton writes, SDL_hidapi_steam_triton.c: a 64-byte
    /// buffer whose byte 0 is report ID 1, so the data stage carries it,
    /// wValue 0x0301. Lizard mode off, SETTING_LIZARD_MODE (9) 0 (127-142),
    /// then sensors on and off, SETTING_IMU_MODE (48) to
    /// SEND_RAW_ACCEL | SEND_RAW_GYRO (0x18) and to 0 (653-677,
    /// controller_constants.h:542-543). SDL reads nothing back, so each write
    /// is followed by a read of report 1 at 64.</summary>
    static void SdlTriton(GoldenCase g, string at, Device[] devices)
    {
        g.Stream($"{at}/sdl-triton");
        foreach (var d in devices)
        {
            d.Marker();
            foreach (var settings in new[] { new byte[] { 9, 0, 0 }, new byte[] { 48, 0x18, 0 }, new byte[] { 48, 0, 0 } })
            {
                var b = new byte[64];
                b[0] = 1;
                b[1] = 0x87;
                b[2] = 3;
                settings.CopyTo(b, 3);
                d.Set(1, b);
                d.Get(1, 64);
            }
        }
    }

    /// <summary>Each message S54 recorded Steam writing, framed as the
    /// profile's channel frames it, then read back at 64.</summary>
    static void S54(GoldenCase g, string at, Device[] devices, byte[] messages, byte channel)
    {
        g.Stream($"{at}/s54-messages");
        foreach (var d in devices)
        {
            d.Marker();
            foreach (byte m in messages)
            {
                d.Set(channel, Framed(channel, m, Array.Empty<byte>()));
                d.Get(channel, 64);
            }
        }
    }

    /// <summary>10^4 random SET_REPORTs, each followed by a random
    /// GET_REPORT, on a random one of the three indexes.</summary>
    static void Random(GoldenCase g, string at, Device[] devices, Rng rng, byte[] featureIds, bool valve, byte channel)
    {
        g.Stream($"{at}/random");
        var known = ValveMessages.Select(v => v.Message).Concat(new byte[] { 0x81, 0x8E, 0x8F, 0xC1, 0xDC, 0xE2, 0xF2 }).ToArray();
        for (int n = 0; n < RandomPairs; n++)
        {
            var d = devices[rng.Next(devices.Length)];
            byte setId = rng.Next(4) == 0 ? rng.Byte() : rng.Bool() ? channel : PickId(rng, featureIds);
            byte setType = rng.Next(10) == 0 ? (byte)2 : (byte)3;
            byte[] payload;
            if (valve && rng.Bool())
            {
                byte m = rng.Bool() ? known[rng.Next(known.Length)] : rng.Byte();
                var param = new byte[rng.Next(8)];
                rng.Fill(param);
                payload = Framed(channel, m, param);
                if (rng.Next(4) == 0) payload = payload.AsSpan(0, rng.Next(payload.Length + 1)).ToArray();
            }
            else
            {
                payload = new byte[rng.Next(66)];
                rng.Fill(payload);
            }
            d.Set(setId, payload, setType);
            byte getId = rng.Next(4) == 0 ? rng.Byte() : rng.Bool() ? channel : PickId(rng, featureIds);
            byte getType = rng.Next(20) == 0 ? (byte)2 : (byte)3;
            d.Get(getId, (ushort)rng.Next(66), getType: getType);
        }
    }

    static byte PickId(Rng rng, byte[] featureIds)
    {
        int n = featureIds.Length + SonyCodeIds.Length;
        int i = rng.Next(n);
        return i < featureIds.Length ? featureIds[i] : SonyCodeIds[i - featureIds.Length];
    }

    // ── Framing ──────────────────────────────────────────────────────────

    /// <summary>A Valve command-channel write of 64 bytes: the channel's
    /// report ID when it has one, then [message][length][parameters]
    /// (SDL_hidapi_steam.c:454-457, controller_structs.h:28-33, 137-155).</summary>
    static byte[] Framed(byte channel, byte message, byte[] param)
    {
        var b = new byte[64];
        int at = 0;
        if (channel != 0) b[at++] = channel;
        b[at] = message;
        b[at + 1] = (byte)param.Length;
        param.AsSpan(0, Math.Min(param.Length, 64 - at - 2)).CopyTo(b.AsSpan(at + 2));
        return b;
    }

    /// <summary>SDL's 65-byte buffer with report ID 0, as the 64-byte data
    /// stage the device receives: buf[1] the message, buf[2] the length,
    /// then the payload.</summary>
    static byte[] Sdl65(byte message, byte[] payload, byte length)
    {
        var b = new byte[64];
        b[0] = message;
        b[1] = length;
        payload.CopyTo(b, 2);
        return b;
    }

    /// <summary>The feature report IDs a HID report descriptor declares:
    /// the report ID in force at each Feature main item, 0 for a
    /// descriptor without IDs. Short items as HID 1.11 6.2.2.2 lays them
    /// out, long items skipped (6.2.2.3), Push and Pop kept (6.2.2.7).</summary>
    static byte[] FeatureReportIds(byte[] desc)
    {
        var ids = new SortedSet<byte>();
        var stack = new Stack<byte>();
        byte reportId = 0;
        for (int i = 0; i < desc.Length;)
        {
            byte prefix = desc[i];
            if (prefix == 0xFE)
            {
                int longSize = i + 1 < desc.Length ? desc[i + 1] : 0;
                i += 3 + longSize;
                continue;
            }
            int size = (prefix & 0x03) == 3 ? 4 : prefix & 0x03;
            uint value = 0;
            for (int k = 0; k < size && i + 1 + k < desc.Length; k++) value |= (uint)desc[i + 1 + k] << (8 * k);
            switch (prefix & 0xFC)
            {
                case 0x84: reportId = (byte)value; break;      // Report ID
                case 0xA4: stack.Push(reportId); break;         // Push
                case 0xB4: if (stack.Count > 0) reportId = stack.Pop(); break; // Pop
                case 0xB0: ids.Add(reportId); break;            // Feature
            }
            i += 1 + size;
        }
        return ids.ToArray();
    }

    // ── The device and the host ──────────────────────────────────────────

    /// <summary>One UsbipEmulatedDevice built as the offline harness builds
    /// one, the requests it answers, and the outputs they give.</summary>
    sealed class Device : IDisposable
    {
        const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        readonly Host _host;
        readonly GoldenCase _g;
        readonly UsbipEmulatedDevice _dev;
        readonly IntPtr _ring;
        readonly int _index;
        readonly byte _hidInterface;
        uint _published;

        public readonly byte[] SecondaryInterfaces;

        public Device(GoldenCase g, Host host, ControllerProfile profile, int index, ValueSources.FileReader stamp)
        {
            _g = g;
            _host = host;
            _index = index;
            _ring = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_OUTPUT_SIZE);
            Marshal.Copy(new byte[SharedMemoryIO.SHARED_OUTPUT_SIZE], 0, _ring, SharedMemoryIO.SHARED_OUTPUT_SIZE);
            _dev = NewDevice(profile, index, stamp, host.Connection, _ring);
            _hidInterface = _dev.Descriptors.HidInterfaceNumber;
            SecondaryInterfaces = _dev.Descriptors.SecondaryHidInterfaces.OrderBy(b => b).ToArray();
        }

        /// <summary>The fields the control path reads, set as the
        /// constructor sets them (UsbipEmulatedDevice.cs:272-337),
        /// with the stamp source and the ring in place of Steam's and the
        /// section's. No thread, section or event.</summary>
        static UsbipEmulatedDevice NewDevice(ControllerProfile profile, int index, ValueSources.FileReader stamp,
                                             UsbipServer.Connection connection, IntPtr ring)
        {
            var t = typeof(UsbipEmulatedDevice);
            var dev = (UsbipEmulatedDevice)RuntimeHelpers.GetUninitializedObject(t);
            var descriptors = new UsbDescriptorSet(profile, index);
            OracleProtocol.Set(dev, profile, descriptors, index);
            void Set(string field, object? value) =>
                (t.GetField(field, Instance) ?? throw new MissingFieldException(t.Name, field)).SetValue(dev, value);
            Set("<Descriptors>k__BackingField", descriptors);
            Set("_index", index);
            Set("_stubs", FeatureStubTable.From(profile, index, descriptors.SerialString, stamp, descriptors.SerialVariant));
            var slots = new int[256];
            Array.Fill(slots, -1);
            Set("_inputSlot", slots);
            Set("_lifetimeLock", new object());
            Set("_publishLock", new object());
            Set("_hidLock", new object());
            Set("_outputView", ring);
            Set("_connection", connection);
            return dev;
        }

        /// <summary>The index the outputs after it belong to.</summary>
        public void Marker() => _g.Out("index", new[] { (byte)_index });

        public (int Status, byte[] Data) Get(byte reportId, ushort wLength, byte? iface = null, byte getType = 3)
        {
            ushort wValue = (ushort)((getType << 8) | reportId);
            var (status, data) = _host.Control(_dev, 0xA1, 0x01, wValue, iface ?? _hidInterface, wLength, ReadOnlySpan<byte>.Empty);
            string type = getType == 3 ? "" : $".t{getType}";
            string where = iface == null ? "" : $".i{iface}";
            Emit($"get{type}{where}.{reportId:X2}.w{wLength}", status, data);
            Published();
            return (status, data);
        }

        public void Set(byte reportId, byte[] payload, byte setType = 3)
        {
            ushort wValue = (ushort)((setType << 8) | reportId);
            var (status, _) = _host.Control(_dev, 0x21, 0x09, wValue, _hidInterface, (ushort)payload.Length, payload);
            var o = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(o, status);
            payload.CopyTo(o, 4);
            _g.Out($"set{(setType == 3 ? "" : $".t{setType}")}.{reportId:X2}.n{payload.Length}", o);
            Published();
        }

        void Emit(string tag, int status, byte[] data)
        {
            var o = new byte[4 + data.Length];
            BinaryPrimitives.WriteInt32LittleEndian(o, status);
            data.CopyTo(o, 4);
            _g.Out(tag, o);
        }

        /// <summary>Each ring slot published since the last request: source,
        /// report ID, size and data (SharedMemoryIO.cs:81-89).</summary>
        void Published()
        {
            uint head = (uint)Marshal.ReadInt32(_ring);
            for (; _published != head; _published++)
            {
                int slot = SharedMemoryIO.OUTPUT_HEADER_SIZE + (int)(_published % SharedMemoryIO.OUTPUT_RING_SLOTS) * SharedMemoryIO.OUTPUT_SLOT_SIZE;
                int size = Marshal.ReadInt16(_ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SIZE);
                var o = new byte[4 + size];
                o[0] = Marshal.ReadByte(_ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_SOURCE);
                o[1] = Marshal.ReadByte(_ring, slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_REPORT_ID);
                BinaryPrimitives.WriteUInt16LittleEndian(o.AsSpan(2), (ushort)size);
                Marshal.Copy(_ring + slot + SharedMemoryIO.OUTPUT_SLOT_OFFSET_DATA, o, 4, size);
                _g.Out("pub", o);
            }
        }

        public void Dispose() => Marshal.FreeHGlobal(_ring);
    }

    /// <summary>The host side: a connected pair of local sockets, the
    /// device's end wrapped in a connection made without its constructor
    /// (UsbipServer.cs:270-291), holding only the socket and the send lock
    /// its sends take (575-593, 632-644).</summary>
    sealed class Host : IDisposable
    {
        readonly string _path;
        readonly Socket _listener, _client, _server;
        readonly byte[] _header = new byte[UsbipProtocol.HeaderSize];
        uint _seq;

        public readonly UsbipServer.Connection Connection;

        public Host(string path)
        {
            _path = path;
            if (File.Exists(path)) File.Delete(path);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(path));
            _listener.Listen(1);
            _client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _client.Connect(new UnixDomainSocketEndPoint(path));
            _server = _listener.Accept();
            // A request the device never answers fails the run instead of
            // hanging it.
            _client.ReceiveTimeout = 5000;
            var t = typeof(UsbipServer.Connection);
            Connection = (UsbipServer.Connection)RuntimeHelpers.GetUninitializedObject(t);
            const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
            (t.GetField("_socket", f) ?? throw new MissingFieldException(t.Name, "_socket")).SetValue(Connection, _server);
            (t.GetField("_sendLock", f) ?? throw new MissingFieldException(t.Name, "_sendLock")).SetValue(Connection, new object());
            // The reply buffer its field initializer makes (UsbipServer.cs,
            // Connection._tx), which no constructor run sets here.
            (t.GetField("_tx", f) ?? throw new MissingFieldException(t.Name, "_tx")).SetValue(Connection, new byte[UsbipProtocol.HeaderSize + 4096]);
        }

        /// <summary>One control transfer on endpoint 0: a CMD_SUBMIT through
        /// HandleSubmit, then its RET_SUBMIT. Status and, for an IN
        /// transfer, the data.</summary>
        public (int Status, byte[] Data) Control(UsbipEmulatedDevice dev, byte bmRequestType, byte bRequest,
                                                 ushort wValue, ushort wIndex, ushort wLength, ReadOnlySpan<byte> outPayload)
        {
            bool isIn = (bmRequestType & 0x80) != 0;
            ulong setup = bmRequestType | ((ulong)bRequest << 8) | ((ulong)wValue << 16)
                        | ((ulong)wIndex << 32) | ((ulong)wLength << 48);
            var cmd = new UsbipProtocol.CommandHeader
            {
                Command = UsbipProtocol.CmdSubmit,
                Seqnum = ++_seq,
                Direction = isIn ? 1u : 0u,
                Ep = 0,
                TransferBufferLength = wLength,
                NumberOfPackets = UsbipProtocol.NumberOfPacketsNonIsoch,
                Setup = setup,
            };
            dev.HandleSubmit(cmd, outPayload, ReadOnlySpan<(uint, uint)>.Empty);
            ReadExactly(_header);
            uint kind = BinaryPrimitives.ReadUInt32BigEndian(_header);
            uint seq = BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4));
            if (kind != UsbipProtocol.RetSubmit || seq != _seq)
                throw new InvalidDataException($"RET_SUBMIT {_seq} expected, got command {kind} for {seq}.");
            int status = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(20));
            int actual = BinaryPrimitives.ReadInt32BigEndian(_header.AsSpan(24));
            var data = new byte[isIn ? actual : 0];
            ReadExactly(data);
            return (status, data);
        }

        void ReadExactly(Span<byte> dst)
        {
            for (int got = 0; got < dst.Length;)
            {
                int n = _client.Receive(dst[got..], SocketFlags.None);
                if (n <= 0) throw new EndOfStreamException("The device's end of the connection closed.");
                got += n;
            }
        }

        public void Dispose()
        {
            _server.Dispose();
            _client.Dispose();
            _listener.Dispose();
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    // ── Round 3: Steam's stamp through its real lookup ───────────────────
    //
    // SteamHardwareUpdater.CurrentStamp finds Steam through HKCU SteamPath,
    // then HKLM WOW6432Node InstallPath, then Program Files (x86)\Steam
    // (SteamHardwareUpdater.cs:70-95). The case loads two application hives,
    // files in a folder of its own that RegLoadAppKey creates and no other
    // process sees, and maps HKEY_CURRENT_USER and HKEY_LOCAL_MACHINE to them
    // for this process with RegOverridePredefKey, as Case5Driver.cs:275-310
    // maps HKLM (Microsoft Learn, RegLoadAppKeyW, RegOverridePredefKey).
    // Before any value is set it checks the mapping holds: neither key shows
    // the machine's own subkeys. Nothing is written outside the hive files.
    // Every state sets a value that names an existing folder of the case's,
    // so the lookup never reaches Program Files (x86)\Steam, which reads this
    // machine's install and is the live run S62's. A check after each state
    // stops the case if the lookup returned a folder outside its own.

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegLoadAppKeyW(string file, out IntPtr key, int samDesired, int options, int reserved);
    [DllImport("advapi32.dll")]
    static extern int RegOverridePredefKey(IntPtr key, IntPtr newKey);
    [DllImport("advapi32.dll")]
    static extern int RegCloseKey(IntPtr key);

    static readonly IntPtr HKEY_CURRENT_USER = unchecked((IntPtr)(int)0x80000001);
    static readonly IntPtr HKEY_LOCAL_MACHINE = unchecked((IntPtr)(int)0x80000002);
    const int KEY_ALL_ACCESS = 0xF003F;

    /// <summary>The lookup in each state: which folder it found, the
    /// updater folder, the stamp for three keys, and the 0x83 answer the
    /// table bound to it serves (FeatureStubTable.cs:56-57).</summary>
    static void StampLookup(GoldenCase g, ControllerProfile triton)
    {
        g.Stream("steam-stamp/lookup/states");
        string root = Path.Combine(Path.GetTempPath(), $"hm-device-oracle-reg-{Environment.ProcessId}");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        string a = Path.Combine(root, "SteamA"), b = Path.Combine(root, "SteamB"), bare = Path.Combine(root, "SteamBare");
        string Cfg(string steam) => Path.Combine(steam, UpdaterRelativeDirectory, UpdaterConfigName);
        // The profile's own source: its roots and path are the lookup
        // SteamHardwareUpdater made before v2.0.0.
        var source = triton.ValueSources!["steamUpdater"];
        foreach (var d in new[] { a, b }) Directory.CreateDirectory(Path.GetDirectoryName(Cfg(d))!);
        Directory.CreateDirectory(bare);
        const string Shipped = "MUST_UPDATE_TRITON_FW_TS:6A18D057\r\nMUST_UPDATE_PROTEUS_FW_TS:6A18D053\r\n"
                             + "TRITON_FW_TS:6A628345\r\nPROTEUS_FW_TS:6A628359";
        File.WriteAllText(Cfg(b), "TRITON_FW_TS:FFFFFFFF\r\nPROTEUS_FW_TS:00000001");

        IntPtr cuKey = IntPtr.Zero, lmKey = IntPtr.Zero;
        try
        {
            int rc = RegLoadAppKeyW(Path.Combine(root, "hkcu.hive"), out cuKey, KEY_ALL_ACCESS, 0, 0);
            if (rc != 0) throw new InvalidOperationException($"RegLoadAppKeyW failed with {rc}");
            rc = RegLoadAppKeyW(Path.Combine(root, "hklm.hive"), out lmKey, KEY_ALL_ACCESS, 0, 0);
            if (rc != 0) throw new InvalidOperationException($"RegLoadAppKeyW failed with {rc}");
            using var cu = RegistryKey.FromHandle(new SafeRegistryHandle(cuKey, ownsHandle: false));
            using var lm = RegistryKey.FromHandle(new SafeRegistryHandle(lmKey, ownsHandle: false));
            rc = RegOverridePredefKey(HKEY_CURRENT_USER, cuKey);
            if (rc != 0) throw new InvalidOperationException($"RegOverridePredefKey(HKCU) failed with {rc}");
            try
            {
                rc = RegOverridePredefKey(HKEY_LOCAL_MACHINE, lmKey);
                if (rc != 0) throw new InvalidOperationException($"RegOverridePredefKey(HKLM) failed with {rc}");
                try
                {
                    using (var m = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft"))
                        if (m != null) throw new InvalidOperationException("HKCU is not redirected: nothing was set");
                    using (var m = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                        if (m != null) throw new InvalidOperationException("HKLM is not redirected: nothing was set");

                    void State(string name, object? steamPath, object? installPath, string? cfgA)
                    {
                        using (var k = cu.CreateSubKey(@"Software\Valve\Steam"))
                        {
                            if (steamPath == null) k.DeleteValue("SteamPath", throwOnMissingValue: false);
                            else if (steamPath is int dw) k.SetValue("SteamPath", dw, RegistryValueKind.DWord);
                            else k.SetValue("SteamPath", (string)steamPath, RegistryValueKind.String);
                        }
                        using (var k = lm.CreateSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                        {
                            if (installPath == null) k.DeleteValue("InstallPath", throwOnMissingValue: false);
                            else k.SetValue("InstallPath", (string)installPath, RegistryValueKind.String);
                        }
                        if (cfgA == null) File.Delete(Cfg(a));
                        else File.WriteAllText(Cfg(a), cfgA);

                        var (install, file) = ValueSources.Locate(source);
                        string? updater = file == null || !Directory.Exists(Path.GetDirectoryName(file)) ? null : Path.GetDirectoryName(file);
                        string Label(string? path)
                        {
                            if (path == null) return "none";
                            string full = Path.GetFullPath(path).TrimEnd('\\');
                            foreach (var (label, dir) in new[] { ("A", a), ("B", b), ("Bare", bare) })
                            {
                                if (string.Equals(full, dir, StringComparison.OrdinalIgnoreCase)) return label;
                                if (string.Equals(full, Path.Combine(dir, UpdaterRelativeDirectory), StringComparison.OrdinalIgnoreCase)) return label + "-updater";
                            }
                            throw new InvalidOperationException($"State {name}: the lookup left the case's folders. Stopped.");
                        }
                        var o = new List<byte>();
                        o.AddRange(System.Text.Encoding.ASCII.GetBytes($"{Label(install)}|{Label(updater)}|"));
                        foreach (string key in new[] { "TRITON_FW_TS", "PROTEUS_FW_TS", "NEREID_FW_TS" })
                        {
                            uint? v = ValueSources.ReadFile(source) is string text ? ValueSources.ParseKeyedHex(text, key) : null;
                            var e = new byte[5];
                            e[0] = (byte)(v.HasValue ? 1 : 0);
                            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(1), v ?? 0);
                            o.AddRange(e);
                        }
                        g.Out($"state.{name}", o.ToArray());
                        g.Out($"answer-83.{name}", Answer83(FeatureStubTable.From(triton, 0, null, 0)));
                    }

                    // Steam writes SteamPath with forward slashes in lower case
                    // (SteamHardwareUpdater.cs:65-69).
                    string aSteamForm = a.Replace('\\', '/').ToLowerInvariant();
                    State("cu-a-lm-b", a, b, Shipped);
                    State("cu-steam-form-lm-b", aSteamForm, b, Shipped);
                    State("cu-absent-lm-a", null, a, Shipped);
                    State("cu-missing-folder-lm-a", Path.Combine(root, "NoSuchFolder"), a, Shipped);
                    State("cu-empty-lm-a", "", a, Shipped);
                    State("cu-blank-lm-a", "   ", a, Shipped);
                    State("cu-dword-lm-a", 5, a, Shipped);
                    State("cu-bare-lm-a", bare, a, Shipped);
                    State("cu-a-cfg-absent", a, b, null);
                    foreach (var (name, text) in new[]
                    {
                        ("empty", ""), ("garbage", "not a config"), ("non-hex", "TRITON_FW_TS:zz"),
                        ("zero", "TRITON_FW_TS:0"), ("nine-digits", "TRITON_FW_TS:123456789"),
                        ("prefix-only", "MUST_UPDATE_TRITON_FW_TS:6A18D057"), ("lf", "PROTEUS_FW_TS:1\nTRITON_FW_TS:0x2A"),
                        ("nul", "TRITON_FW_TS:\u00006A628345"),
                    })
                        State($"cu-a-cfg-{name}", a, b, text);
                }
                finally { RegOverridePredefKey(HKEY_LOCAL_MACHINE, IntPtr.Zero); }
            }
            finally { RegOverridePredefKey(HKEY_CURRENT_USER, IntPtr.Zero); }
        }
        finally
        {
            if (cuKey != IntPtr.Zero) RegCloseKey(cuKey);
            if (lmKey != IntPtr.Zero) RegCloseKey(lmKey);
            // A hive can stay locked a moment after its key closes, which
            // DeleteDirectory waits out, as case 5 does for its hive.
            Case5Driver.DeleteDirectory(root);
        }
    }

    /// <summary>Triton's 0x83 answer as Steam asks for it: the message on
    /// feature report 1, then a GET on that report (empty when it stalls).
    /// At 9bd6279 this was the table's Lookup(0x83, 64).</summary>
    static byte[] Answer83(FeatureStubTable? t)
    {
        if (t == null) return Array.Empty<byte>();
        t.OnSetFeature(1, new byte[] { 0x01, 0x83, 0x00 });
        int n = t.Lookup(1, 64, out _);
        return n < 0 ? Array.Empty<byte>() : t.Answer(n).ToArray();
    }

    // ── Round 3: the table's refusal of a stamp it cannot place ──────────

    /// <summary>FeatureStubTable.From refuses a stamp on an echoed answer, a
    /// blank key, and an offset whose four bytes are not inside the data
    /// (FeatureStubTable.cs:84-91 at 9bd6279). From v2.0.0 the stamp is a
    /// patch from a keyedHexFile source, refused for the same edits
    /// (ValueSources.Validate). Each on a copy of steam-controller-2 whose
    /// 0x83 entry is changed, with the edges that pass beside them. The
    /// exception's type and message, or the 0x83 answer the table
    /// built.</summary>
    static void StampRefusal(GoldenCase g, ControllerProfile triton)
    {
        g.Stream("usbip-feature/steam-controller-2/stamp-refusal");
        var cases = new (string Name, Action<FeatureStubReport> Edit)[]
        {
            ("as-shipped", r => { }),
            ("echo", r => r.Echo = true),
            ("echo-no-data", r => { r.Echo = true; r.Data = null; }),
            ("key-empty", r => r.Patch![0].Key = ""),
            ("key-blank", r => r.Patch![0].Key = "   "),
            ("offset-negative", r => r.Patch![0].At = -1),
            ("offset-zero", r => r.Patch![0].At = 0),
            ("offset-last-four", r => r.Patch![0].At = r.Data!.Length / 2 - 4),
            ("offset-past-data", r => r.Patch![0].At = r.Data!.Length / 2 - 3),
            ("offset-in-padding", r => r.Patch![0].At = 60),
            ("no-data", r => r.Data = null),
        };
        foreach (var (name, edit) in cases)
        {
            var json = JsonSerializer.Serialize(triton, HMJsonContext.Default.ControllerProfile);
            var p = JsonSerializer.Deserialize(json, HMJsonContext.Default.ControllerProfile)!;
            var entry = p.FeatureStubs!.Reports.First(r => r.Message?.Value == "83" && r.Patch != null);
            edit(entry);
            try
            {
                var table = FeatureStubTable.From(p, 0, null, _ => "TRITON_FW_TS:6A628345");
                g.Out($"ok.{name}", Answer83(table));
            }
            catch (Exception e)
            {
                g.Out($"throw.{name}", System.Text.Encoding.ASCII.GetBytes($"{e.GetType().FullName}|{e.Message}"));
            }
        }
    }
}
