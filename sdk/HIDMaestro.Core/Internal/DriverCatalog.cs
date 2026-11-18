using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HIDMaestro.Internal;

/// <summary>
/// Writes the catalogs of a driver package through wintrust.dll's catalog
/// functions, in this process, with no tool from a Windows kit.
///
/// <para>Through v2.2.0 the SDK carried Inf2Cat from the WDK and ran it with
/// the <c>/os:</c> value <see cref="DriverBuilder.CatalogOsFor"/> picks. This
/// class writes the catalog Inf2Cat writes for the same package and value,
/// entry for entry, and through the same calls: Inf2Cat's catalog library is
/// itself a wrapper over CryptCATOpen, CryptCATPutCatAttrInfo,
/// CryptCATPutMemberInfo, CryptCATPutAttrInfo and CryptCATPersistStore, with
/// each member's indirect data from CryptSIPCreateIndirectData. The rules
/// below were read from the catalogs Inf2Cat 10.0.26100 wrote for this
/// package on both architectures, and probe driver_catalog_check compares
/// the two writers' catalogs entry by entry where the WDK is
/// installed.</para>
///
/// <list type="bullet">
/// <item>One catalog per name an INF's CatalogFile entry gives
/// (CatalogFile.NTAMD64 or CatalogFile.NTARM64, then CatalogFile.NT, then
/// CatalogFile), written beside the INF, its name in lowercase.</item>
/// <item>Catalog version 2 (CRYPTCAT_VERSION_2), so the member list
/// algorithm is 1.3.6.1.4.1.311.12.1.3.</item>
/// <item>Its members: the INF, and every file the INF's install sections
/// for the architecture copy. Each file twice: a SHA-1 member and a
/// SHA-256 member, each tagged with its digest in uppercase hex, each with
/// the attributes File (the file's name in lowercase) and OSAttr
/// ("2:10.0"). No page hashes.</item>
/// <item>Its attributes: HWID1, HWID2 and on, the hardware IDs of every
/// models section of every driver INF in the directory, in lowercase, in
/// the order the INFs and their lines come, each once, then OS:
/// "_v100_X64" for x64 and "_v100_ARM64_RS3" for ARM64.</item>
/// <item>Every attribute authenticated, its name and value text
/// (0x10010001).</item>
/// </list>
///
/// <para>The INF reading covers what HIDMaestro's INFs use: string tokens,
/// quoted fields, comments, line continuation, architecture-decorated
/// install sections and SourceDisksFiles/SourceDisksNames. A file the INF
/// copies from a compressed name (ending in an underscore) or a package
/// with INFs below its top folder is refused, since Inf2Cat expands and
/// walks those and this class does not.</para>
/// </summary>
internal static unsafe partial class DriverCatalog
{
    /// <summary>The catalog attribute OS for each architecture's value of
    /// <see cref="DriverBuilder.CatalogOsFor"/>, as Inf2Cat writes it for
    /// <c>/os:10_X64</c> and <c>/os:10_RS3_ARM64</c>.</summary>
    internal static string CatalogOsAttribute(Architecture arch) =>
        arch == Architecture.Arm64 ? "_v100_ARM64_RS3" : "_v100_X64";

    /// <summary>The member attribute OSAttr, the same for both values.</summary>
    internal const string MemberOsAttr = "2:10.0";

    // mscat.h: CRYPTCAT_ATTR_AUTHENTICATED | CRYPTCAT_ATTR_DATAASCII |
    // CRYPTCAT_ATTR_NAMEASCII. Inf2Cat's default attribute type.
    internal const uint AttributeFlags = 0x10010001;

    // mscat.h
    private const uint CRYPTCAT_OPEN_CREATENEW = 0x1, CRYPTCAT_VERSION_2 = 0x200;
    // wincrypt.h: PKCS_7_ASN_ENCODING | X509_ASN_ENCODING
    private const uint Encoding = 0x10001;
    // mssip.h: SPC_EXC_PE_PAGE_HASHES_FLAG, SPC_INC_PE_IMPORT_ADDR_TABLE_FLAG,
    // SPC_INC_PE_RESOURCES_FLAG, MSSIP_FLAGS_PROHIBIT_RESIZE_ON_CREATE: the
    // flags Inf2Cat sets for a member without page hashes.
    private const uint SipFlags = 0x10 | 0x20 | 0x80 | 0x10000;

    internal const string Sha1Oid = "1.3.14.3.2.26", Sha256Oid = "2.16.840.1.101.3.4.2.1";

    /// <summary>Write every catalog the driver INFs in
    /// <paramref name="packageDir"/> name, for <paramref name="arch"/>, and
    /// return their paths. An existing catalog of the same name is
    /// replaced.</summary>
    /// <exception cref="InvalidOperationException">An INF names a file the
    /// package does not hold, uses a form this class does not read, or a
    /// catalog function failed.</exception>
    internal static IReadOnlyList<string> Write(string packageDir, Architecture arch)
    {
        var plan = Plan(packageDir, arch);
        var written = new List<string>(plan.Count);
        foreach (var cat in plan)
        {
            WriteOne(cat);
            written.Add(cat.Path);
        }
        return written;
    }

    /// <summary>One catalog to write: its path, its hardware IDs and OS, and
    /// its members, each a file and the name its File attribute
    /// carries.</summary>
    internal sealed record CatalogPlan(string Path, IReadOnlyList<string> HardwareIds, string Os,
        IReadOnlyList<(string File, string Name)> Members);

    /// <summary>What <see cref="Write"/> writes, read from the INFs, with no
    /// file written.</summary>
    internal static IReadOnlyList<CatalogPlan> Plan(string packageDir, Architecture arch)
    {
        string root = System.IO.Path.GetFullPath(packageDir);
        if (Directory.EnumerateDirectories(root).Any(d => Directory.EnumerateFiles(d, "*.inf", SearchOption.AllDirectories).Any()))
            throw new InvalidOperationException($"{root} holds an INF below its top folder. HIDMaestro catalogs flat packages only.");

        // Inf2Cat walks the folder in the order Windows lists it, which on
        // NTFS is the order of the names in uppercase.
        var infs = Directory.GetFiles(root, "*.inf")
            .OrderBy(f => System.IO.Path.GetFileName(f).ToUpperInvariant(), StringComparer.Ordinal)
            .Select(f => (Path: f, Inf: InfFile.Read(f)))
            .Where(x => x.Inf.IsDriverInf)
            .ToList();

        var hwids = new List<string>();
        foreach (var (_, inf) in infs)
            foreach (string id in inf.HardwareIds())
                if (!hwids.Contains(id, StringComparer.Ordinal)) hwids.Add(id);

        string decoration = arch == Architecture.Arm64 ? "ntarm64" : "ntamd64";
        var catalogs = new List<(string Path, List<(string File, string Name)> Members)>();
        foreach (var (path, inf) in infs)
        {
            string? name = inf.CatalogFileFor(decoration);
            if (string.IsNullOrEmpty(name)) continue;
            string catPath = System.IO.Path.Combine(root, name.TrimStart('\\'));
            var entry = catalogs.FirstOrDefault(c => c.Path.Equals(catPath, StringComparison.OrdinalIgnoreCase));
            if (entry.Members == null)
            {
                entry = (catPath, new List<(string, string)>());
                catalogs.Add(entry);
            }
            void Add(string file, string fileName)
            {
                if (!entry.Members.Any(m => m.File.Equals(file, StringComparison.OrdinalIgnoreCase)))
                    entry.Members.Add((file, fileName));
            }
            Add(path, System.IO.Path.GetFileName(path).ToLowerInvariant());
            foreach (var (file, dest) in inf.CopiedFiles(root, decoration, arch))
                Add(file, dest);
        }

        string os = CatalogOsAttribute(arch);
        // Inf2Cat adds the members in the order the folder lists the files.
        // CryptCATPersistStore sorts them by tag either way.
        return catalogs.Select(c => new CatalogPlan(c.Path, hwids, os,
                c.Members.OrderBy(m => System.IO.Path.GetFileName(m.File).ToUpperInvariant(), StringComparer.Ordinal).ToList()))
            .ToList();
    }

    private static void WriteOne(CatalogPlan cat)
    {
        if (File.Exists(cat.Path)) File.Delete(cat.Path);
        IntPtr store = CryptCATOpen(cat.Path, CRYPTCAT_OPEN_CREATENEW, IntPtr.Zero, CRYPTCAT_VERSION_2, Encoding);
        if (store == IntPtr.Zero || store == new IntPtr(-1))
            throw Failed("CryptCATOpen", cat.Path);
        try
        {
            for (int i = 0; i < cat.HardwareIds.Count; i++)
                PutCatAttr(store, "HWID" + (i + 1).ToString(CultureInfo.InvariantCulture), cat.HardwareIds[i], cat.Path);
            PutCatAttr(store, "OS", cat.Os, cat.Path);

            CRYPTCATSTORE* info = CryptCATStoreFromHandle(store);
            if (info == null) throw Failed("CryptCATStoreFromHandle", cat.Path);
            foreach (var (file, name) in cat.Members)
            {
                Guid subject;
                if (!CryptSIPRetrieveSubjectGuidForCatalogFile(file, IntPtr.Zero, &subject))
                    throw Failed("CryptSIPRetrieveSubjectGuidForCatalogFile", file);
                // Inf2Cat adds the SHA-1 member first, then for a version 2
                // catalog the SHA-256 one.
                AddMember(store, info, file, name, subject, Sha1Oid);
                AddMember(store, info, file, name, subject, Sha256Oid);
            }
            if (!CryptCATPersistStore(store)) throw Failed("CryptCATPersistStore", cat.Path);
        }
        finally
        {
            CryptCATClose(store);
        }
    }

    private static void AddMember(IntPtr store, CRYPTCATSTORE* info, string file, string name, Guid subject, string digestOid)
    {
        byte[] oid = System.Text.Encoding.ASCII.GetBytes(digestOid + "\0");
        fixed (byte* pOid = oid)
        fixed (char* pFile = file)
        {
            var si = new SIP_SUBJECTINFO
            {
                cbSize = (uint)sizeof(SIP_SUBJECTINFO),
                pgSubjectType = &subject,
                hFile = IntPtr.Zero,
                pwsFileName = pFile,
                hProv = info->hProv,
                DigestAlgorithm = new CRYPT_ALGORITHM_IDENTIFIER { pszObjId = pOid },
                dwFlags = SipFlags,
                dwEncodingType = info->dwEncodingType,
            };
            uint cb = 0;
            if (!CryptSIPCreateIndirectData(&si, &cb, null) || cb == 0)
                throw Failed("CryptSIPCreateIndirectData", file);
            byte[] buffer = new byte[cb];
            fixed (byte* pData = buffer)
            {
                if (!CryptSIPCreateIndirectData(&si, &cb, pData))
                    throw Failed("CryptSIPCreateIndirectData", file);
                var indirect = (SIP_INDIRECT_DATA*)pData;
                string tag = Convert.ToHexString(new ReadOnlySpan<byte>(indirect->Digest.pbData, (int)indirect->Digest.cbData));
                IntPtr member = CryptCATPutMemberInfo(store, file, tag, &subject, si.dwIntVersion, cb, pData);
                if (member == IntPtr.Zero) throw Failed("CryptCATPutMemberInfo", file);
                PutAttr(store, member, "File", name, file);
                PutAttr(store, member, "OSAttr", MemberOsAttr, file);
            }
        }
    }

    private static void PutCatAttr(IntPtr store, string tag, string value, string path)
    {
        fixed (char* p = value + "\0")
            if (CryptCATPutCatAttrInfo(store, tag, AttributeFlags, (uint)((value.Length + 1) * 2), (byte*)p) == IntPtr.Zero)
                throw Failed("CryptCATPutCatAttrInfo " + tag, path);
    }

    private static void PutAttr(IntPtr store, IntPtr member, string tag, string value, string path)
    {
        fixed (char* p = value + "\0")
            if (CryptCATPutAttrInfo(store, member, tag, AttributeFlags, (uint)((value.Length + 1) * 2), (byte*)p) == IntPtr.Zero)
                throw Failed("CryptCATPutAttrInfo " + tag, path);
    }

    private static InvalidOperationException Failed(string call, string path) =>
        new($"{call} failed for {path} (error 0x{Marshal.GetLastPInvokeError():X8}).");

    // ── The INF ──────────────────────────────────────────────────────────

    /// <summary>An INF read as Inf2Cat reads it: every name and value in
    /// lowercase, a line's fields after its key (or all of them on a line
    /// with no key), sections in file order.</summary>
    internal sealed class InfFile
    {
        internal sealed record Line(string? Key, List<string> Fields);

        private readonly List<(string Name, List<Line> Lines)> _sections = new();
        private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);

        internal static InfFile Read(string path) => Parse(Decode(File.ReadAllBytes(path)));

        /// <summary>UTF-16 with its byte-order mark, UTF-8 with its mark, or
        /// with no mark the system's ANSI code page, the forms Windows reads
        /// an INF in.</summary>
        internal static string Decode(byte[] bytes)
        {
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return System.Text.Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            return System.Text.Encoding.Latin1.GetString(bytes);
        }

        internal static InfFile Parse(string text)
        {
            var inf = new InfFile();
            List<Line>? current = null;
            var logical = new StringBuilder();
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = StripComment(raw).TrimEnd();
                // A backslash that ends a line joins the next one to it.
                if (line.EndsWith('\\'))
                {
                    logical.Append(line, 0, line.Length - 1);
                    continue;
                }
                logical.Append(line);
                string l = logical.ToString().Trim();
                logical.Clear();
                if (l.Length == 0) continue;
                if (l[0] == '[')
                {
                    int close = l.IndexOf(']');
                    string name = (close > 0 ? l[1..close] : l[1..]).Trim().ToLowerInvariant();
                    current = new List<Line>();
                    inf._sections.Add((name, current));
                    continue;
                }
                if (current == null) continue;
                int eq = IndexOutsideQuotes(l, '=');
                string? key = eq >= 0 ? Unquote(l[..eq].Trim()).ToLowerInvariant() : null;
                string rest = eq >= 0 ? l[(eq + 1)..] : l;
                var fields = SplitOutsideQuotes(rest).Select(f => Unquote(f.Trim()).ToLowerInvariant()).ToList();
                current.Add(new Line(key, fields));
            }
            foreach (var line in inf.Section("strings"))
                if (line.Key != null && line.Fields.Count > 0)
                    inf._strings.TryAdd(line.Key, line.Fields[0]);
            return inf;
        }

        private static string StripComment(string s)
        {
            bool quoted = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"') quoted = !quoted;
                else if (s[i] == ';' && !quoted) return s[..i];
            }
            return s;
        }

        private static int IndexOutsideQuotes(string s, char c)
        {
            bool quoted = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"') quoted = !quoted;
                else if (s[i] == c && !quoted) return i;
            }
            return -1;
        }

        private static List<string> SplitOutsideQuotes(string s)
        {
            var parts = new List<string>();
            bool quoted = false;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"') quoted = !quoted;
                else if (s[i] == ',' && !quoted)
                {
                    parts.Add(s[start..i]);
                    start = i + 1;
                }
            }
            parts.Add(s[start..]);
            return parts;
        }

        // A quoted field loses its quotes, and a doubled quote inside one
        // stands for one quote.
        private static string Unquote(string s)
        {
            if (s.IndexOf('"') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '"') { sb.Append(s[i]); continue; }
                if (i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; }
            }
            return sb.ToString().Trim();
        }

        /// <summary>Every line of every section of that name, in file order.</summary>
        internal IEnumerable<Line> Section(string name) =>
            _sections.Where(s => s.Name == name).SelectMany(s => s.Lines);

        internal bool Has(string name) => _sections.Any(s => s.Name == name);

        /// <summary><paramref name="s"/> with each %key% replaced by its
        /// [Strings] value and %% by a percent sign. A key [Strings] lacks
        /// stays as written.</summary>
        internal string Resolve(string s)
        {
            if (s.IndexOf('%') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                int end;
                if (s[i] == '%' && (end = s.IndexOf('%', i + 1)) > i)
                {
                    string key = s[(i + 1)..end];
                    if (key.Length == 0) sb.Append('%');
                    else if (_strings.TryGetValue(key, out string? v)) sb.Append(v);
                    else sb.Append(s, i, end - i + 1);
                    i = end;
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private string? VersionValue(string key) =>
            Section("version").FirstOrDefault(l => l.Key == key) is { Fields.Count: > 0 } line ? line.Fields[0] : null;

        /// <summary>A driver INF as Inf2Cat counts one: [Version] carries
        /// Signature and Class or ClassGuid, and the INF has [Manufacturer]
        /// or a DefaultInstall section.</summary>
        internal bool IsDriverInf =>
            VersionValue("signature") != null
            && (VersionValue("class") != null || VersionValue("classguid") != null)
            && (Has("manufacturer") || new[] { "", ".nt", ".ntx86", ".ntia64", ".ntamd64", ".ntarm", ".ntarm64" }.Any(d => Has("defaultinstall" + d)));

        /// <summary>The catalog this INF names for an architecture's
        /// decoration.</summary>
        internal string? CatalogFileFor(string decoration)
        {
            string? name = VersionValue("catalogfile." + decoration) ?? VersionValue("catalogfile.nt") ?? VersionValue("catalogfile");
            return name == null ? null : Resolve(name);
        }

        /// <summary>The models sections [Manufacturer] lists: each entry's
        /// section undecorated and with each of its decorations, for every
        /// architecture.</summary>
        private HashSet<string> ModelsSections()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in Section("manufacturer"))
            {
                if (line.Fields.Count == 0) continue;
                string baseName = Resolve(line.Fields[0]);
                set.Add(baseName);
                foreach (string d in line.Fields.Skip(1)) set.Add(baseName + "." + Resolve(d));
            }
            return set;
        }

        /// <summary>The hardware and compatible IDs of every models line, in
        /// file order, with no asterisk, each once.</summary>
        internal IEnumerable<string> HardwareIds()
        {
            var models = ModelsSections();
            var ids = new List<string>();
            foreach (var (name, lines) in _sections)
            {
                if (!models.Contains(name)) continue;
                foreach (var line in lines)
                    for (int i = 1; i < line.Fields.Count; i++)
                    {
                        string id = Resolve(line.Fields[i].Replace("*", ""));
                        if (IsHardwareIdKept(id) && !ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
                    }
            }
            return ids;
        }

        // The legacy PnP IDs of keyboards, mice and serial devices, which
        // Inf2Cat leaves out of the catalog attributes.
        private const string LegacyPnpIds =
            "pnp0300,pnp0301,pnp0302,pnp0303,pnp0304,pnp0305,pnp0306,pnp0307,pnp0308,pnp0309,pnp030a,pnp0320,pnp0321,pnp0322,pnp0323,pnp0324,pnp0325,pnp0326,pnp0327,pnp0340,pnp0341,pnp0342,pnp0343,pnp0343,pnp0344,pnp0f00,pnp0f01,pnp0f02,pnp0f03,pnp0f04,pnp0f05,pnp0f06,pnp0f07,pnp0f08,pnp0f09,pnp0f0a,pnp0f0b,pnp0f0c,pnp0f0d,pnp0f0e,pnp0f0f,pnp0f10,pnp0f11,pnp0f12,pnp0f13,pnp0f14,pnp0f15,pnp0f16,pnp0f17,pnp0f18,pnp0f19,pnp0f1a,pnp0f1b,pnp0f1c,pnp0f1d,pnp0f1e,pnp0f1f,pnp0f20,pnp0f21,pnp0f22,pnp0f23";

        /// <summary>Whether Inf2Cat writes an ID as a HWIDn attribute: every
        /// ID but a bare PnP ID, or one under ACPI or BIOS, outside the
        /// legacy list.</summary>
        internal static bool IsHardwareIdKept(string id)
        {
            if (id.Length == 0) return false;
            if (LegacyPnpIds.Contains(id, StringComparison.OrdinalIgnoreCase)) return true;
            int slash = id.IndexOf('\\');
            if (slash > -1)
            {
                string bus = id[..slash];
                if (bus is "acpi" or "bios")
                {
                    string rest = slash + 1 < id.Length ? id[(slash + 1)..] : "";
                    return !rest.StartsWith("pnp", StringComparison.OrdinalIgnoreCase)
                        || LegacyPnpIds.Contains(rest, StringComparison.OrdinalIgnoreCase);
                }
                return true;
            }
            return id.Length switch
            {
                7 => !id.StartsWith("*pnp", StringComparison.OrdinalIgnoreCase),
                6 => !id.StartsWith("pnp", StringComparison.OrdinalIgnoreCase),
                _ => true,
            };
        }

        /// <summary>The variant of an install section Windows uses on the
        /// architecture: decorated for it, else .nt, else undecorated, the
        /// suffix following each.</summary>
        private string? Variant(string name, string decoration, string suffix)
        {
            foreach (string d in new[] { "." + decoration, ".nt", "" })
                if (Has(name + d + suffix)) return name + d + suffix;
            return null;
        }

        /// <summary>The install sections of the INF for the architecture: the
        /// ClassInstall32 and DefaultInstall sections and every install
        /// section a models line names, in any models section, each with its
        /// .CoInstallers section.</summary>
        private List<string> InstallSections(string decoration)
        {
            var names = new List<string>();
            void AddVariant(string name, string suffix)
            {
                if (Variant(name, decoration, suffix) is string v && !names.Contains(v)) names.Add(v);
            }
            AddVariant("classinstall32", "");
            AddVariant("defaultinstall", "");
            var models = ModelsSections();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, lines) in _sections)
            {
                if (!models.Contains(name)) continue;
                foreach (var line in lines)
                {
                    if (line.Fields.Count == 0 || !seen.Add(line.Fields[0])) continue;
                    string install = Resolve(line.Fields[0]);
                    AddVariant(install, "");
                    AddVariant(install, ".coinstallers");
                }
            }
            return names;
        }

        /// <summary>The files the INF's install sections copy on the
        /// architecture, each with the name it is copied under, found through
        /// SourceDisksFiles and SourceDisksNames in
        /// <paramref name="root"/>.</summary>
        /// <exception cref="InvalidOperationException">A file is missing from
        /// the package or has a compressed name.</exception>
        internal IEnumerable<(string File, string Dest)> CopiedFiles(string root, string decoration, Architecture arch)
        {
            string platform = arch == Architecture.Arm64 ? "arm64" : "amd64";
            foreach (string section in InstallSections(decoration))
                foreach (var directive in Section(section).Where(l => l.Key == "copyfiles"))
                    foreach (string listName in directive.Fields.Select(Resolve))
                    {
                        if (listName.Length == 0) continue;
                        IEnumerable<List<string>> items = listName.StartsWith('@')
                            ? new[] { new List<string> { listName[1..] } }
                            : Section(listName).Select(l => l.Fields);
                        foreach (var item in items)
                        {
                            if (item.Count == 0 || item[0].Length == 0) continue;
                            string dest = Resolve(item[0]);
                            string? src = item.Count > 1 && item[1].Length > 0 ? Resolve(item[1]) : null;
                            yield return (Locate(root, platform, dest, src), dest);
                        }
                    }
        }

        private string Locate(string root, string platform, string dest, string? src)
        {
            var disks = SourceDisks("sourcedisksfiles", platform)
                .Where(l => l.Key == dest || (src != null && l.Key == src)).ToList();
            if (disks.Count == 0)
                throw new InvalidOperationException($"The INF copies {dest}, which SourceDisksFiles does not list.");
            var disk = disks[0];
            string diskId = disk.Fields.Count > 0 ? Resolve(disk.Fields[0]) : "";
            string subdir = disk.Fields.Count > 1 ? Resolve(disk.Fields[1]) : "";
            var names = SourceDisks("sourcedisksnames", platform).Where(l => l.Key == diskId).ToList();
            if (names.Count == 0)
                throw new InvalidOperationException($"SourceDisksNames does not list disk {diskId}, which {dest} comes from.");
            string diskPath = names[0].Fields.Count > 3 ? Resolve(names[0].Fields[3]) : "";
            string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, diskPath.Trim('\\'), subdir.Trim('\\')));
            foreach (string name in new[] { dest, src }.OfType<string>())
            {
                if (name.EndsWith('_'))
                    throw new InvalidOperationException($"The INF copies {dest} from the compressed {name}. HIDMaestro catalogs uncompressed files only.");
                string file = System.IO.Path.Combine(dir, name);
                if (File.Exists(file)) return file;
            }
            throw new InvalidOperationException($"The INF copies {dest}, which is not in {dir}.");
        }

        /// <summary>SourceDisksFiles or SourceDisksNames lines for the
        /// platform: its decorated section and the undecorated one.</summary>
        private IEnumerable<Line> SourceDisks(string name, string platform) =>
            Section(name + "." + platform).Concat(Section(name));
    }

    // ── Interop ──────────────────────────────────────────────────────────

    // mscat.h: CRYPTCATSTORE.
    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPTCATSTORE
    {
        public uint cbStruct;
        public uint dwPublicVersion;
        public IntPtr pwszP7File;
        public IntPtr hProv;
        public uint dwEncodingType;
        public uint fdwStoreFlags;
        public IntPtr hReserved;
        public IntPtr hAttrs;
        public IntPtr hCryptMsg;
        public IntPtr hSorted;
    }

    // wincrypt.h: CRYPT_ALGORITHM_IDENTIFIER and CRYPT_DATA_BLOB.
    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPT_DATA_BLOB
    {
        public uint cbData;
        public byte* pbData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPT_ALGORITHM_IDENTIFIER
    {
        public byte* pszObjId;
        public CRYPT_DATA_BLOB Parameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CRYPT_ATTRIBUTE_TYPE_VALUE
    {
        public byte* pszObjId;
        public CRYPT_DATA_BLOB Value;
    }

    // mssip.h: SIP_SUBJECTINFO and SIP_INDIRECT_DATA.
    [StructLayout(LayoutKind.Sequential)]
    private struct SIP_SUBJECTINFO
    {
        public uint cbSize;
        public Guid* pgSubjectType;
        public IntPtr hFile;
        public char* pwsFileName;
        public char* pwsDisplayName;
        public uint dwReserved1;
        public uint dwIntVersion;
        public IntPtr hProv;
        public CRYPT_ALGORITHM_IDENTIFIER DigestAlgorithm;
        public uint dwFlags;
        public uint dwEncodingType;
        public uint dwReserved2;
        public uint fdwCAPISettings;
        public uint fdwSecuritySettings;
        public uint dwIndex;
        public uint dwUnionChoice;
        public IntPtr psFlat;
        public IntPtr pClientData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIP_INDIRECT_DATA
    {
        public CRYPT_ATTRIBUTE_TYPE_VALUE Data;
        public CRYPT_ALGORITHM_IDENTIFIER DigestAlgorithm;
        public CRYPT_DATA_BLOB Digest;
    }

    [LibraryImport("wintrust.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CryptCATOpen(string pwszFileName, uint fdwOpenFlags, IntPtr hProv, uint dwPublicVersion, uint dwEncodingType);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATClose(IntPtr hCatalog);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial CRYPTCATSTORE* CryptCATStoreFromHandle(IntPtr hCatalog);

    [LibraryImport("wintrust.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CryptCATPutCatAttrInfo(IntPtr hCatalog, string pwszReferenceTag, uint dwAttrTypeAndAction, uint cbData, byte* pbData);

    [LibraryImport("wintrust.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CryptCATPutMemberInfo(IntPtr hCatalog, string pwszFileName, string pwszReferenceTag,
        Guid* pgSubjectType, uint dwCertVersion, uint cbSIPIndirectData, byte* pbSIPIndirectData);

    [LibraryImport("wintrust.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CryptCATPutAttrInfo(IntPtr hCatalog, IntPtr pCatMember, string pwszReferenceTag,
        uint dwAttrTypeAndAction, uint cbData, byte* pbData);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATPersistStore(IntPtr hCatalog);

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptSIPRetrieveSubjectGuidForCatalogFile(string FileName, IntPtr hFileIn, Guid* pgSubject);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptSIPCreateIndirectData(SIP_SUBJECTINFO* pSubjectInfo, uint* pcbIndirectData, byte* pIndirectData);
}
