using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace HIDMaestro.Internal;

/// <summary>v2.0.0: per-controller values inside feature answers (design
/// note, mechanism 5). A patch writes a value from a named source over
/// bytes of an answer's data: <c>index</c>, the transport index,
/// <c>serial</c>, the serial the device serves, <c>instanceSerial</c>, the
/// answer's own captured text varied per controller by the serial variant
/// that varies a captured USB serial, or a source the profile's
/// <c>valueSources</c> declares. The one declared kind is
/// <c>keyedHexFile</c>, which reads a <c>KEY:HEX</c> line of a file, the
/// form of Steam's updater config. This replaces SteamHardwareUpdater,
/// which held Steam's install and file format in code (issue
/// #62).</summary>
internal static class ValueSources
{
    public const string Index = "index";
    public const string Serial = "serial";
    public const string InstanceSerial = "instanceSerial";
    public const string KeyedHexFile = "keyedHexFile";

    /// <summary>Where the file a keyedHexFile source reads is, and the
    /// text in it. A check stands in its own reader so the answers are the
    /// same on every machine.</summary>
    internal delegate string? FileReader(ValueSourceSpec source);

    /// <summary>The values a table's patches read for one controller.
    /// <c>SerialVariant</c> is the number an instanceSerial patch folds into
    /// its text, the one the controller's captured USB serial takes
    /// (<see cref="DeviceIdentity.SerialVariant"/>).</summary>
    internal readonly record struct Values(int Index, string? Serial, int SerialVariant,
                                           IReadOnlyDictionary<string, ValueSourceSpec>? Sources, FileReader Files);

    // ── Checks at load ───────────────────────────────────────────────────

    /// <summary>Checks a profile's value sources and the patches of its
    /// answers. Throws <see cref="ProfileDefectException"/> with the JSON
    /// path. A keyedHexFile source is refused on a UMDF2 profile, whose
    /// answers the driver serves without calling the SDK.</summary>
    internal static void Validate(Dictionary<string, ValueSourceSpec>? sources, FeatureStubSpec? stubs, bool usbip)
    {
        if (sources != null)
            foreach (var (name, s) in sources)
            {
                string at = HMLayoutValidator.Member("$.valueSources", name);
                if (s == null) throw new ProfileDefectException(at, "is null.");
                if (name is Index or Serial or InstanceSerial)
                    throw new ProfileDefectException(at, $"'{name}' is a built-in source's name. Give the source another.");
                if (s.Kind != KeyedHexFile)
                    throw new ProfileDefectException(at + ".kind", $"\"{s.Kind}\" is not a kind. The one kind is \"{KeyedHexFile}\".");
                if (!usbip)
                    throw new ProfileDefectException(at, "a keyedHexFile source is for the usbip backend only. On UMDF2 the driver serves the answers without calling the SDK.");
                if (s.Roots == null || s.Roots.Count == 0)
                    throw new ProfileDefectException(at + ".roots", "a keyedHexFile source needs a root to find its file under.");
                for (int i = 0; i < s.Roots.Count; i++)
                {
                    var r = s.Roots[i] ?? throw new ProfileDefectException($"{at}.roots[{i}]", "is null.");
                    bool reg = r.Registry != null, known = r.KnownFolder != null;
                    if (reg == known)
                        throw new ProfileDefectException($"{at}.roots[{i}]", "a root is a registry value or a known folder, one of the two.");
                    if (reg && (string.IsNullOrWhiteSpace(r.Value) || RegistryRoot(r.Registry!) == null))
                        throw new ProfileDefectException($"{at}.roots[{i}]", "a registry root needs a key under HKCU or HKLM and a value name.");
                    if (known && !Enum.TryParse<Environment.SpecialFolder>(r.KnownFolder, out _))
                        throw new ProfileDefectException($"{at}.roots[{i}].knownFolder", $"\"{r.KnownFolder}\" is not a known folder.");
                }
                if (string.IsNullOrWhiteSpace(s.Path) || System.IO.Path.IsPathRooted(s.Path))
                    throw new ProfileDefectException(at + ".path", "a keyedHexFile source needs the file's path under its root.");
            }
        if (stubs?.Reports == null) return;
        for (int i = 0; i < stubs.Reports.Count; i++)
        {
            var r = stubs.Reports[i];
            if (r == null) continue;
            if (r.RemovedSteamFirmwareStamp != null)
                throw new ProfileDefectException($"$.featureStubs.reports[{i}].steamFirmwareStamp",
                    "steamFirmwareStamp was removed in v2.0.0. Write the stamp as a patch from a keyedHexFile value source: " +
                    "\"patch\": [{ \"at\": <offset>, \"length\": 4, \"encoding\": \"uint32-le\", \"source\": <the source's name>, \"key\": <the key> }] " +
                    "with the source under the profile's \"valueSources\".");
            if (r.Patch == null) continue;
            int dataLength = string.IsNullOrEmpty(r.Data) ? 0 : r.Data.Length / 2;
            for (int k = 0; k < r.Patch.Count; k++)
            {
                string at = $"$.featureStubs.reports[{i}].patch[{k}]";
                var p = r.Patch[k] ?? throw new ProfileDefectException(at, "is null.");
                if (r.Echo)
                    throw new ProfileDefectException(at, "an echoed answer is the bytes the host wrote, so it has none to patch.");
                // A patch with no source is refused there, before any check
                // that reads the source.
                if (p.Source == null)
                    throw new ProfileDefectException(at + ".source", "is null. Name index, serial, instanceSerial or a valueSources entry.");
                bool text = p.Encoding is "ascii";
                int width = p.Encoding switch { "uint8" => 1, "uint16-le" => 2, "uint32-le" => 4, "ascii" or "ascii-decimal" => 0, _ => -1 };
                if (width < 0)
                    throw new ProfileDefectException(at + ".encoding", $"\"{p.Encoding}\" is not uint8, uint16-le, uint32-le, ascii or ascii-decimal.");
                if (width > 0 && p.Length != width)
                    throw new ProfileDefectException(at + ".length", $"is {p.Length}, and {p.Encoding} writes {width}.");
                if (p.Length < 1)
                    throw new ProfileDefectException(at + ".length", "a patch writes one byte or more.");
                if (p.At < 0 || p.At + p.Length > dataLength)
                    throw new ProfileDefectException(at + ".at",
                        $"bytes {p.At} to {p.At + p.Length - 1} are not inside the answer's {dataLength} bytes of data. A patch replaces captured bytes.");
                if (p.Source == Serial)
                {
                    if (!text) throw new ProfileDefectException(at + ".encoding", "the serial is text, so its encoding is ascii.");
                    if (p.Add != 0) throw new ProfileDefectException(at + ".add", "the serial is text and takes no add.");
                    if (p.Key != null) throw new ProfileDefectException(at + ".key", "only a keyedHexFile source takes a key.");
                    continue;
                }
                if (p.Source == InstanceSerial)
                {
                    CheckInstanceSerial(r.Data, p, at);
                    continue;
                }
                if (text) throw new ProfileDefectException(at + ".encoding", $"{p.Source} is a number. ascii writes text, ascii-decimal writes a number's digits.");
                if (p.Source == Index)
                {
                    if (p.Key != null) throw new ProfileDefectException(at + ".key", "only a keyedHexFile source takes a key.");
                    continue;
                }
                if (sources == null || !sources.ContainsKey(p.Source))
                    throw new ProfileDefectException(at + ".source", $"\"{p.Source}\" is not index, serial, instanceSerial or a valueSources name.");
                if (string.IsNullOrWhiteSpace(p.Key))
                    throw new ProfileDefectException(at + ".key", "a keyedHexFile value needs its key.");
            }
        }
    }

    /// <summary>An instanceSerial patch varies the captured text under it,
    /// so that text is printable ASCII and ends in the digit run the variant
    /// is added into, as a captured USB serial does
    /// (<see cref="Usbip.UsbDescriptorSet.InstanceSerial"/>).</summary>
    private static void CheckInstanceSerial(string? data, AnswerPatchSpec p, string at)
    {
        if (p.Encoding != "ascii")
            throw new ProfileDefectException(at + ".encoding", "an instance serial is the answer's own text, so its encoding is ascii.");
        if (p.Add != 0) throw new ProfileDefectException(at + ".add", "an instance serial is text and takes no add.");
        if (p.Key != null) throw new ProfileDefectException(at + ".key", "only a keyedHexFile source takes a key.");
        // The patched bytes alone, decoded without an allocation when the
        // data has no spaces, which is every catalog profile's form. A
        // create compiles this check with its table.
        string hex = data ?? "";
        Span<byte> text = stackalloc byte[Math.Clamp(p.Length, 0, 64)];
        bool decoded;
        if (hex.Contains(' '))
        {
            byte[] bytes;
            try { bytes = Convert.FromHexString(hex.Replace(" ", "")); }
            catch (FormatException) { throw new ProfileDefectException(at, "the answer's data is not whole hex bytes."); }
            decoded = p.At + p.Length <= bytes.Length && p.Length <= text.Length;
            if (decoded) bytes.AsSpan(p.At, p.Length).CopyTo(text);
        }
        else
        {
            decoded = 2 * (p.At + p.Length) <= hex.Length && p.Length <= text.Length
                && Convert.FromHexString(hex.AsSpan(2 * p.At, 2 * p.Length), text, out _, out int written) == System.Buffers.OperationStatus.Done
                && written == p.Length;
        }
        if (!decoded)
            throw new ProfileDefectException(at + ".at", $"bytes {p.At} to {p.At + p.Length - 1} are not whole hex bytes inside the answer's data.");
        text = text[..p.Length];
        foreach (byte b in text)
            if (b is < 0x20 or > 0x7E)
                throw new ProfileDefectException(at + ".at",
                    $"bytes {p.At} to {p.At + p.Length - 1} hold 0x{b:X2}. An instance serial varies captured text, which is printable ASCII.");
        if (text[^1] is < (byte)'0' or > (byte)'9')
            throw new ProfileDefectException(at + ".length",
                $"the captured text ends in '{(char)text[^1]}'. An instance serial adds its variant into the digit run its text ends in.");
    }

    // ── Values ───────────────────────────────────────────────────────────

    /// <summary>True for a patch whose value comes from a file, read when
    /// the answer is asked for.</summary>
    internal static bool ReadsFile(AnswerPatchSpec p) => p.Source is not (null or Index or Serial or InstanceSerial);

    /// <summary>Writes a patch's value over its bytes. A keyedHexFile value
    /// that is absent leaves the captured bytes, as Steam's absence left the
    /// captured stamp.</summary>
    internal static void Apply(Span<byte> answer, AnswerPatchSpec p, in Values v)
    {
        if (p.At + p.Length > answer.Length) return;
        var dst = answer.Slice(p.At, p.Length);
        if (p.Source == Serial)
        {
            dst.Clear();
            string s = v.Serial ?? "";
            for (int i = 0; i < s.Length && i < dst.Length; i++) dst[i] = (byte)(s[i] < 0x80 ? s[i] : '?');
            return;
        }
        if (p.Source == InstanceSerial)
        {
            // Issue #72. The captured text, varied in place by the rule a
            // captured USB serial follows (UsbDescriptorSet.InstanceSerial):
            // the variant is added into the trailing digit run, a carry past
            // the run is dropped, and the length stays. Variant 0 keeps the
            // text byte for byte, and each later variant answers another
            // serial. A profile with sharedSerial keeps its USB serial
            // unvaried, and its instanceSerial text is varied all the same:
            // the flag names the USB string only. The load checked that it is printable ASCII ending in a
            // digit. It runs once, when the table compiles.
            int carry = v.SerialVariant;
            for (int i = dst.Length - 1; i >= 0 && carry > 0 && dst[i] is >= (byte)'0' and <= (byte)'9'; i--)
            {
                int d = dst[i] - '0' + carry % 10;
                carry /= 10;
                if (d > 9) { d -= 10; carry++; }
                dst[i] = (byte)('0' + d);
            }
            return;
        }
        long value;
        if (p.Source == Index) value = v.Index;
        else
        {
            if (v.Sources == null || !v.Sources.TryGetValue(p.Source, out var src)) return;
            string? text;
            try { text = v.Files(src); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { text = null; }
            if (text == null || ParseKeyedHex(text, p.Key!) is not uint read) return;
            value = read;
        }
        value += p.Add;
        switch (p.Encoding)
        {
            case "uint8": dst[0] = (byte)value; break;
            case "uint16-le": BinaryPrimitives.WriteUInt16LittleEndian(dst, (ushort)value); break;
            case "uint32-le": BinaryPrimitives.WriteUInt32LittleEndian(dst, (uint)value); break;
            case "ascii-decimal":
            {
                dst.Clear();
                string digits = value.ToString(CultureInfo.InvariantCulture);
                for (int i = 0; i < digits.Length && i < dst.Length; i++) dst[i] = (byte)digits[i];
                break;
            }
        }
    }

    /// <summary>The value of one <c>KEY:HEX</c> line. The key matches whole,
    /// so <c>TRITON_FW_TS</c> never reads the <c>MUST_UPDATE_TRITON_FW_TS</c>
    /// line, and the first line with the key decides. An optional
    /// <c>0x</c>, one to eight hex digits. Null for an absent key, a value
    /// that is not a 32-bit hex number, or zero, as SteamHardwareUpdater's
    /// ParseStamp read Steam's updater config before v2.0.0.</summary>
    internal static uint? ParseKeyedHex(string text, string key)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int colon = line.IndexOf(':');
            if (colon <= 0 || !line.AsSpan(0, colon).Trim().SequenceEqual(key)) continue;

            var value = line.AsSpan(colon + 1).Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
            return value.Length is > 0 and <= 8
                   && uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint stamp)
                   && stamp != 0
                ? stamp : null;
        }
        return null;
    }

    // ── Files ────────────────────────────────────────────────────────────

    /// <summary>The first root of a source whose folder exists, and the
    /// source's file under it, which may not exist. Both null when no root
    /// exists. A registry value that is not a string, or is blank, is no
    /// root. Steam writes its SteamPath with forward slashes in lower case,
    /// which the full-path form reads.</summary>
    internal static (string? Root, string? File) Locate(ValueSourceSpec s)
    {
        foreach (var r in s.Roots ?? new List<ValueRootSpec>())
        {
            string? dir = null;
            if (r.Registry != null)
            {
                var (hive, subKey) = RegistryRoot(r.Registry) ?? (null!, "");
                try
                {
                    using var key = hive?.OpenSubKey(subKey);
                    if (key?.GetValue(r.Value) is string value && !string.IsNullOrWhiteSpace(value))
                        dir = System.IO.Path.GetFullPath(value);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                            or ArgumentException or NotSupportedException)
                {
                    // An unreadable key or a value that is no path is the
                    // same as an absent one.
                }
            }
            else if (r.KnownFolder != null && Enum.TryParse<Environment.SpecialFolder>(r.KnownFolder, out var folder))
            {
                string basePath = Environment.GetFolderPath(folder);
                if (basePath.Length > 0) dir = System.IO.Path.Combine(basePath, r.Path ?? "");
            }
            if (dir != null && Directory.Exists(dir))
                return (dir, System.IO.Path.Combine(dir, s.Path ?? ""));
        }
        return (null, null);
    }

    private static (RegistryKey Hive, string SubKey)? RegistryRoot(string path)
    {
        int slash = path.IndexOf('\\');
        if (slash <= 0) return null;
        string hive = path[..slash];
        RegistryKey? key = hive.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            _ => null,
        };
        return key == null ? null : (key, path[(slash + 1)..]);
    }

    // The text of each file read, by path, with the write time and length it
    // was read at. A GET_REPORT(Feature) reads a file again only after it
    // changed on disk.
    private static readonly Dictionary<string, (DateTime Written, long Length, string Text)> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a file's write time must lie in the past before its
    /// text is cached. Two writes within one tick of the file system's clock
    /// can leave one write time and one length on different text, so a
    /// file written this recently is read again on every request, and the
    /// next write to a cached file gets a later write time.</summary>
    private static readonly TimeSpan Settled = TimeSpan.FromSeconds(2);

    /// <summary>The machine's reader: the source's file as
    /// <see cref="Locate"/> finds it, its text cached until its last write
    /// time changes. Null when the file is absent or cannot be read. Never
    /// throws: it runs on the USB control-transfer path.</summary>
    internal static string? ReadFile(ValueSourceSpec s)
    {
        string? file;
        try { (_, file) = Locate(s); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        return file == null ? null : ReadPath(file);
    }

    /// <summary>One file's text through the cache. Null when the file is
    /// absent or cannot be read.</summary>
    internal static string? ReadPath(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return null;
            DateTime written = info.LastWriteTimeUtc;
            long length = info.Length;
            lock (s_cache)
                if (s_cache.TryGetValue(file, out var hit) && hit.Written == written && hit.Length == length) return hit.Text;
            string text = File.ReadAllText(file);
            if (DateTime.UtcNow - written >= Settled)
                lock (s_cache) s_cache[file] = (written, length, text);
            return text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
