using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HIDMaestroTest;

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

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    /// <summary>
    /// Whether triggers are combined into a single Z axis (true for Xbox on Windows).
    /// Combined: Z centers at 50%, LT pulls toward 0%, RT pulls toward 100%.
    /// Separate: Z and Rz each go 0-100% independently.
    /// </summary>
    [JsonIgnore]
    public bool HasCombinedTriggers => TriggerMode?.Equals("combined", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Whether this controller uses xinputhid (Xbox One+) or direct HID (Xbox 360, non-Xbox).
    /// xinputhid: IG_00 enumerator, GIP descriptor, combined DirectInput + separate XInput triggers
    /// hid: standard HIDCLASS enumerator, controller's native descriptor, direct HID access
    /// </summary>
    [JsonIgnore]
    public bool UsesXinputhid => DriverMode?.Equals("xinputhid", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>Parsed VID as ushort.</summary>
    [JsonIgnore]
    public ushort VendorId => Convert.ToUInt16(Vid, 16);

    /// <summary>Parsed PID as ushort.</summary>
    [JsonIgnore]
    public ushort ProductId => Convert.ToUInt16(Pid, 16);

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
