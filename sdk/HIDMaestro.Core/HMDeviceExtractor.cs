using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HIDMaestro.Internal;

namespace HIDMaestro;

/// <summary>
/// Extract HIDMaestro profiles from physically-connected HID devices.
///
/// <para>Every field of a HIDMaestro profile JSON (id, name, vendor, vid,
/// pid, productString, manufacturerString, type, connection, descriptor,
/// inputReportSize) is recovered from the real device using Windows'
/// user-mode HID APIs. The descriptor is reconstructed from the opaque
/// preparsed-data blob returned by <c>HidD_GetPreparsedData</c> using
/// the libusb/hidapi algorithm (Chromium WebHID team's reverse
/// engineering). The result is <b>logically equivalent</b> to what the
/// device originally returned over USB — same report IDs, field layouts,
/// logical ranges, usage pages, sizes — but not byte-for-byte identical.
/// For HIDMaestro's purpose (creating a virtual that behaves the same as
/// the physical device), logical equivalence is the correct fidelity
/// bar, because filter drivers can mutate the descriptor before it
/// reaches user mode anyway.</para>
///
/// <para>The same API is used by three surfaces: the standalone
/// <c>HIDMaestroProfileExtractor</c> GUI, the <c>HIDMaestroTest
/// extract-profile</c> CLI subcommand, and directly by consumers like
/// PadForge that want a "scan for connected devices" flow inside their
/// own UI.</para>
///
/// <para>Does not require admin. Read-only inspection of connected HID
/// devices; no virtual controllers are created and no registry is
/// mutated.</para>
/// </summary>
public static class HMDeviceExtractor
{
    /// <summary>Enumerate every HID-class device currently connected.
    /// Returns one <see cref="HMHidDeviceInfo"/> per HID interface (a
    /// single physical device with multiple top-level collections will
    /// appear multiple times, one per collection — pick the one whose
    /// <see cref="HMHidDeviceInfo.TopLevelUsage"/> matches what you
    /// want to emulate).</summary>
    public static IReadOnlyList<HMHidDeviceInfo> ListDevices()
    {
        var raw = HidDeviceEnumerator.Enumerate();
        var result = new List<HMHidDeviceInfo>(raw.Count);
        foreach (var r in raw)
        {
            result.Add(new HMHidDeviceInfo(
                r.VendorId, r.ProductId, r.VersionNumber,
                r.ProductString, r.ManufacturerString, r.SerialNumberString,
                r.TopLevelUsagePage, r.TopLevelUsage, r.InputReportByteLength,
                r.DevicePath));
        }
        return result;
    }

    /// <summary>Extract a full HIDMaestro profile from a connected device.
    /// The returned <see cref="HMProfile"/> is ready to pass to
    /// <see cref="HMContext.CreateController(HMProfile)"/> (creates a
    /// virtual with identical HID descriptor and identity) or serialize
    /// to JSON via <see cref="ToJson(HMProfile)"/>.</summary>
    /// <param name="device">Device info from <see cref="ListDevices"/>.</param>
    /// <exception cref="InvalidOperationException">Device disappeared
    /// between enumeration and extraction, or its preparsed data can't
    /// be reconstructed.</exception>
    public static HMProfile Extract(HMHidDeviceInfo device)
    {
        if (device is null) throw new ArgumentNullException(nameof(device));

        // Find the device fresh — the path may still be valid, but this
        // also re-reads the descriptor in case the device reconnected.
        var matches = HidDeviceEnumerator.Enumerate()
            .Where(r => r.DevicePath.Equals(device.DevicePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            throw new InvalidOperationException($"Device at path '{device.DevicePath}' is no longer connected.");
        var raw = matches[0];

        if (raw.ReconstructedDescriptor.Length == 0)
            throw new InvalidOperationException(
                "Descriptor reconstruction returned 0 bytes. The device's preparsed data " +
                "may be non-standard; a byte-level capture (Wireshark + USBPcap on Windows, " +
                "or hidraw on Linux) would be needed for this device.");

        // Infer profile metadata from what the device reports.
        string type = raw.TopLevelUsagePage switch
        {
            0x01 => raw.TopLevelUsage switch
            {
                0x02 => "other",       // Mouse
                0x04 => "joystick",    // Joystick
                0x05 => "gamepad",     // Game Pad
                0x06 => "other",       // Keyboard
                0x08 => "other",       // Multi-axis controller
                _    => "other",
            },
            _ => "other",
        };

        // Connection inference: peek at the DevicePath. Windows-canonical
        // paths contain \\?\HID#VID_...  for direct HID, \\?\HID#{GUID}&...
        // for Bluetooth LE HID. Not perfect; best-effort.
        string connection = raw.DevicePath.Contains("{0000", StringComparison.Ordinal)
                         || raw.DevicePath.Contains("&Dev&", StringComparison.OrdinalIgnoreCase)
            ? "bluetooth"
            : "usb";

        // Slug the ID from the product string or fall back to VID:PID.
        string idSeed = raw.ProductString ?? $"vid{raw.VendorId:x4}pid{raw.ProductId:x4}";
        string id = Slugify(idSeed);
        string name = raw.ProductString ?? $"Device {raw.VendorId:X4}:{raw.ProductId:X4}";
        string vendor = raw.ManufacturerString ?? "Unknown";

        string notes =
            $"Extracted by HMDeviceExtractor on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC. " +
            "Descriptor reconstructed from Windows preparsed data (HIDAPI algorithm); " +
            "logically equivalent to the physical device's HID report descriptor but " +
            "not guaranteed byte-identical.";

        return new HMProfileBuilder()
            .Id(id)
            .Name(name)
            .Vendor(vendor)
            .Vid(raw.VendorId)
            .Pid(raw.ProductId)
            .ProductString(raw.ProductString ?? name)
            .ManufacturerString(raw.ManufacturerString ?? "")
            .Type(type)
            .Connection(connection)
            .Descriptor(raw.ReconstructedDescriptor)
            .InputReportSize(raw.InputReportByteLength == 0 ? 0 : raw.InputReportByteLength)
            .Notes(notes)
            .Build();
    }

    /// <summary>Convenience wrapper: find a connected device by VID:PID
    /// and extract its profile. If multiple devices share the VID:PID
    /// (e.g. two of the same controller plugged in), returns the first
    /// HID interface found. Throws if no match.</summary>
    public static HMProfile ExtractByVidPid(ushort vid, ushort pid)
    {
        var devices = ListDevices();
        var match = devices.FirstOrDefault(d => d.VendorId == vid && d.ProductId == pid);
        if (match is null)
            throw new InvalidOperationException($"No connected HID device with VID_{vid:X4}&PID_{pid:X4}.");
        return Extract(match);
    }

    /// <summary>Serialize an <see cref="HMProfile"/> to the JSON format
    /// used by the shipped profile catalog. The output can be saved to
    /// <c>profiles/&lt;vendor&gt;/&lt;slug&gt;.json</c> and picked up by
    /// <see cref="HMContext.LoadProfilesFromDirectory"/> on next load.</summary>
    public static string ToJson(HMProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));

        var sb = new StringBuilder(2048);
        sb.Append('{').Append('\n');
        AppendField(sb, "id", profile.Id);
        AppendField(sb, "name", profile.Name);
        AppendField(sb, "vendor", profile.Vendor);
        AppendField(sb, "vid", $"0x{profile.VendorId:X4}");
        AppendField(sb, "pid", $"0x{profile.ProductId:X4}");
        AppendField(sb, "productString", profile.ProductString);
        AppendField(sb, "manufacturerString", profile.Inner.ManufacturerString ?? "");
        AppendField(sb, "type", profile.Type);
        AppendField(sb, "connection", profile.Connection);
        AppendField(sb, "descriptor", profile.Inner.Descriptor);
        AppendRawField(sb, "inputReportSize", profile.InputReportSize > 0 ? profile.InputReportSize.ToString() : "null");
        AppendField(sb, "notes", profile.Inner.Notes, trailingComma: false);
        sb.Append('}').Append('\n');
        return sb.ToString();
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static void AppendField(StringBuilder sb, string key, string? value, bool trailingComma = true)
    {
        sb.Append("  \"").Append(key).Append("\": ");
        if (value is null) sb.Append("null");
        else
        {
            sb.Append('"');
            foreach (char ch in value)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (ch < 0x20) sb.Append($"\\u{(int)ch:X4}");
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }
        if (trailingComma) sb.Append(',');
        sb.Append('\n');
    }

    private static void AppendRawField(StringBuilder sb, string key, string rawValue, bool trailingComma = true)
    {
        sb.Append("  \"").Append(key).Append("\": ").Append(rawValue);
        if (trailingComma) sb.Append(',');
        sb.Append('\n');
    }

    private static string Slugify(string input)
    {
        var sb = new StringBuilder(input.Length);
        bool lastWasDash = false;
        foreach (char ch in input.ToLowerInvariant())
        {
            if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
            {
                sb.Append(ch);
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }
        string s = sb.ToString().TrimEnd('-');
        return string.IsNullOrEmpty(s) ? "device" : s;
    }
}
