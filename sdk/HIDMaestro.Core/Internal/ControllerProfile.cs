using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal;

/// <summary>
/// A controller profile loaded from the profiles database.
/// Contains everything needed to masquerade as a specific real controller.
/// </summary>
public sealed class ControllerProfile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("vendor")]
    public string Vendor { get; set; } = "";

    [JsonPropertyName("vid")]
    public string Vid { get; set; } = "";

    [JsonPropertyName("pid")]
    public string Pid { get; set; } = "";

    [JsonPropertyName("productString")]
    public string ProductString { get; set; } = "";

    [JsonPropertyName("manufacturerString")]
    public string? ManufacturerString { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("connection")]
    public string Connection { get; set; } = "";

    [JsonPropertyName("descriptor")]
    public string? Descriptor { get; set; }

    [JsonPropertyName("inputReportSize")]
    public int? InputReportSize { get; set; }

    [JsonPropertyName("deviceDescription")]
    public string? DeviceDescription { get; set; }

    [JsonPropertyName("triggerMode")]
    public string? TriggerMode { get; set; }

    [JsonPropertyName("driverMode")]
    public string? DriverMode { get; set; }

    /// <summary>PID override for hardware ID (driver matching only). Apps still see real PID.</summary>
    [JsonPropertyName("driverPid")]
    public string? DriverPid { get; set; }

    /// <summary>PID override for HID attributes. Prevents GameInput from matching the real PID.
    /// Used when we need WGI to use our custom GameInput mapping instead of the built-in one.</summary>
    [JsonPropertyName("hidPid")]
    public string? HidPid { get; set; }

    /// <summary>If true, delete the GameInput registry entry for this VID/PID.
    /// Prevents WGI from claiming the HID device as Gamepad, forcing Chrome to use XInput only.
    /// This gives separate browser triggers without needing 6 axes in the HID descriptor.</summary>
    [JsonPropertyName("skipGameInput")]
    public bool SkipGameInput { get; set; }

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>Optional button remapping table. Maps HMButton bit positions
    /// (index) to descriptor button indices (value). When present, BuildReport
    /// uses this to place semantic buttons (A, B, X, Y, LB, RB, etc.) at the
    /// correct descriptor positions for the profile's controller family.
    /// When null, identity mapping is assumed (bit N → descriptor button N).
    /// Example: Sony DS4 maps HMButton.A (bit 0) to descriptor button 2 (Cross).
    /// </summary>
    [JsonPropertyName("buttonMap")]
    public int[]? ButtonMap { get; set; }

    /// <summary>Optional trigger-to-button derivation. When a DS4 or DualSense
    /// trigger is nonzero, the corresponding digital button should also engage
    /// (real hardware reports both the analog axis and a digital button for L2/R2).
    /// Array of two descriptor button indices: [LT_button, RT_button].
    /// When present, BuildReport sets these buttons whenever the corresponding
    /// trigger axis is nonzero. When null, no derivation occurs.</summary>
    [JsonPropertyName("triggerButtons")]
    public int[]? TriggerButtons { get; set; }

    /// <summary>Optional axis semantic override. Maps HID usage codes to
    /// semantic roles when the default heuristic gets it wrong. Keys are
    /// HID usage codes (e.g. "0x32" for Z), values are semantic names:
    /// "leftStickX", "leftStickY", "rightStickX", "rightStickY",
    /// "leftTrigger", "rightTrigger". When present, overrides
    /// ResolveSemantics for the specified usages. When null, the default
    /// heuristic applies (which assumes Z=trigger, Rz=trigger).
    /// Sony profiles need this because Z/Rz = right stick, Rx/Ry = triggers.
    /// </summary>
    [JsonPropertyName("axisMap")]
    public Dictionary<string, string>? AxisMap { get; set; }

    /// <summary>If true, skip main HID device — use XUSB companion only.
    /// DI reads from XInput (5 axes), browser reads from XInput (separate triggers).
    /// Used for Xbox 360 where real hardware uses xusb22.sys (no HID).</summary>
    [JsonPropertyName("companionOnly")]
    public bool CompanionOnly { get; set; }

    /// <summary>
    /// Whether triggers are combined into a single Z axis (true for Xbox on Windows).
    /// Combined: Z centers at 50%, LT pulls toward 0%, RT pulls toward 100%.
    /// Separate: Z and Rz each go 0-100% independently.
    /// </summary>
    [JsonIgnore]
    public bool HasCombinedTriggers => TriggerMode?.Equals("combined", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether this controller uses an upper filter driver for XInput.
    /// xinputhid: Xbox One+ controllers (GIP descriptor, xinputhid filter)
    /// xusb22: Xbox 360 controllers (xusb22 filter)
    /// hid: no filter, direct HID access only
    /// </summary>
    [JsonIgnore]
    public bool UsesXinputhid => DriverMode?.Equals("xinputhid", StringComparison.OrdinalIgnoreCase) == true;

    [JsonIgnore]
    public bool UsesXusb22 => DriverMode?.Equals("xusb22", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Whether this profile uses any upper filter (xinputhid or xusb22).</summary>
    [JsonIgnore]
    public bool UsesUpperFilter => UsesXinputhid || UsesXusb22;

    /// <summary>The upper filter service name, or null.</summary>
    [JsonIgnore]
    public string? UpperFilterName => UsesXinputhid ? "xinputhid" : UsesXusb22 ? "xusb22" : null;

    /// <summary>Parsed VID as ushort.</summary>
    [JsonIgnore]
    public ushort VendorId => Convert.ToUInt16(Vid, 16);

    /// <summary>Parsed PID as ushort.</summary>
    [JsonIgnore]
    public ushort ProductId => Convert.ToUInt16(Pid, 16);

    /// <summary>Effective PID for HID attributes. Uses hidPid override if set, otherwise real PID.</summary>
    [JsonIgnore]
    public ushort HidProductId => HidPid != null ? Convert.ToUInt16(HidPid, 16) : ProductId;

    /// <summary>Device Manager display name. Uses deviceDescription if set, otherwise productString.</summary>
    [JsonIgnore]
    public string DisplayName => DeviceDescription ?? ProductString;

    /// <summary>True if this profile has a HID descriptor ready to use.</summary>
    [JsonIgnore]
    public bool HasDescriptor => !string.IsNullOrEmpty(Descriptor);

    /// <summary>Parses the hex descriptor string into raw bytes.</summary>
    public byte[]? GetDescriptorBytes()
    {
        if (string.IsNullOrEmpty(Descriptor)) return null;
        var hex = Descriptor.Replace(" ", "").Replace("-", "");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}

/// <summary>
/// Loads and queries controller profiles from the profiles/ directory.
/// </summary>
public sealed class ProfileDatabase
{
    private readonly List<ControllerProfile> _profiles = new();

    public IReadOnlyList<ControllerProfile> All => _profiles;

    public int Count => _profiles.Count;

    /// <summary>
    /// Loads all .json profile files from the given directory (recursively).
    /// Skips schema.json and any files that fail to parse.
    /// </summary>
    public static ProfileDatabase Load(string profilesDir)
    {
        var db = new ProfileDatabase();

        if (!Directory.Exists(profilesDir))
            throw new DirectoryNotFoundException($"Profiles directory not found: {profilesDir}");

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        foreach (var file in Directory.EnumerateFiles(profilesDir, "*.json", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Equals("schema.json", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<ControllerProfile>(json, options);
                if (profile != null && !string.IsNullOrEmpty(profile.Id))
                    db._profiles.Add(profile);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Warning: Failed to load {file}: {ex.Message}");
            }
        }

        db._profiles.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        return db;
    }

    /// <summary>Loads every profile JSON embedded in the HIDMaestro.Core
    /// assembly under the logical-name prefix "HIDMaestro.Profiles.". This
    /// is the no-disk path used by HMContext.LoadDefaultProfiles() — the
    /// SDK ships with the entire profile catalog baked in so consumers
    /// don't need to ship a sibling profiles/ directory.</summary>
    public static ProfileDatabase LoadEmbedded()
    {
        var db = new ProfileDatabase();
        var asm = typeof(ProfileDatabase).Assembly;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        const string prefix = "HIDMaestro.Profiles.";

        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                using var s = asm.GetManifestResourceStream(name);
                if (s == null) continue;
                using var reader = new StreamReader(s);
                string json = reader.ReadToEnd();
                var profile = JsonSerializer.Deserialize<ControllerProfile>(json, options);
                if (profile != null && !string.IsNullOrEmpty(profile.Id))
                    db._profiles.Add(profile);
            }
            catch
            {
                // Silent — embedded resources should always parse, but if a
                // future profile has bad JSON we don't want to take down
                // every consumer.
            }
        }

        db._profiles.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        return db;
    }

    /// <summary>Find a profile by its exact ID slug.</summary>
    public ControllerProfile? GetById(string id) =>
        _profiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Find profiles by vendor name (case-insensitive partial match).</summary>
    public IEnumerable<ControllerProfile> FindByVendor(string vendor) =>
        _profiles.Where(p => p.Vendor.Contains(vendor, StringComparison.OrdinalIgnoreCase));

    /// <summary>Find profiles by controller type (gamepad, wheel, joystick, etc.).</summary>
    public IEnumerable<ControllerProfile> FindByType(string type) =>
        _profiles.Where(p => p.Type.Equals(type, StringComparison.OrdinalIgnoreCase));

    /// <summary>Find profiles by VID/PID.</summary>
    public ControllerProfile? FindByVidPid(ushort vid, ushort pid) =>
        _profiles.FirstOrDefault(p => p.VendorId == vid && p.ProductId == pid);

    /// <summary>Search profiles by name (case-insensitive partial match).</summary>
    public IEnumerable<ControllerProfile> Search(string query) =>
        _profiles.Where(p =>
            p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            p.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            p.ProductString.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            p.Vendor.Contains(query, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns only profiles that have a HID descriptor ready to deploy.</summary>
    public IEnumerable<ControllerProfile> GetDeployable() =>
        _profiles.Where(p => p.HasDescriptor);

    /// <summary>Prints a formatted table of all profiles.</summary>
    public void PrintAll()
    {
        Console.WriteLine($"\n  {"ID",-35} {"Name",-45} {"VID:PID",-12} {"Type",-12} {"Ready"}");
        Console.WriteLine($"  {new string('-', 35)} {new string('-', 45)} {new string('-', 12)} {new string('-', 12)} {new string('-', 5)}");

        foreach (var p in _profiles)
        {
            string vidpid = $"{p.VendorId:X4}:{p.ProductId:X4}";
            string ready = p.HasDescriptor ? "YES" : "---";
            Console.WriteLine($"  {p.Id,-35} {p.Name,-45} {vidpid,-12} {p.Type,-12} {ready}");
        }

        Console.WriteLine($"\n  Total: {_profiles.Count} profiles ({_profiles.Count(p => p.HasDescriptor)} with descriptors)");
    }
}
