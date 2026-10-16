// Interop declarations and the method names in the SDK's text.
//
// static (offline, no device):
//   1. No DllImport or extern declaration is left in the SDK sources. Every
//      P/Invoke is a LibraryImport stub generated at build time.
//   2. SetupDiGetClassDevsW's Enumerator is PCWSTR in setupapi.h, so both
//      declarations take a pointer-sized value, not a uint.
//   3. Every "Type.Member" and "Type.Method(args)" an SDK string, comment or
//      XML doc names resolves in the built assembly, and a call with
//      arguments matches an overload that takes that many, under those names.
//      A message that names a call which does not compile sends a program to
//      write code that cannot build.
//
// live (needs elevation, run through the bench): creates an Xbox 360 wired
// controller with HIDMAESTRO_DIAG=1 and reads the SDK's own XInput slot wait
// line, which is the only caller of the XInputGetState declaration.
// Then it disposes the controller and requires the devnode gone within 5 s
// (SetupDiGetClassDevsW through DifRemoveDevice), and runs the phantom sweep
// (the other SetupDiGetClassDevsW caller), which must remove the one record
// the dispose leaves.
//
// Exit 0 PASS, 1 FAIL, 2 when another program that embeds HIDMaestro runs or,
// for static, when the SDK source tree is not beside the exe.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "static";
        if (mode == "live")
        {
            // Read once, when DeviceOrchestrator initializes, so set it first.
            Environment.SetEnvironmentVariable("HIDMAESTRO_DIAG", "1");
            return Live();
        }
        return Static();
    }

    // Null when the exe runs from a staged tree that holds probe binaries only.
    static string? SdkDir()
    {
        for (string? d = AppContext.BaseDirectory; d != null; d = Path.GetDirectoryName(d))
        {
            string c = Path.Combine(d, "sdk", "HIDMaestro.Core");
            if (File.Exists(Path.Combine(c, "HIDMaestro.Core.csproj"))) return c;
        }
        return null;
    }

    // A LibraryImport attribute at the start of a line: short or namespace
    // qualified, with or without the Attribute suffix, alone in its bracket
    // or after other attributes in it.
    static readonly Regex s_libraryImportRx = new(
        @"^[ \t]*\[(?:[^\]\r\n]*,\s*)?(?:global::)?(?:System\.Runtime\.InteropServices\.)?LibraryImport(?:Attribute)?\(",
        RegexOptions.Multiline);

    static IEnumerable<string> SdkSources(string sdk) =>
        Directory.EnumerateFiles(sdk, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    static int Static()
    {
        Console.WriteLine("=== Interop declarations and method names in SDK text ===");
        string? sdkDir = SdkDir();
        if (sdkDir == null)
        {
            Console.WriteLine("  [SKIP] the SDK source tree (sdk\\HIDMaestro.Core) is not beside this exe, so there is no text to check.");
            return 2;
        }
        string sdk = sdkDir;
        var sources = SdkSources(sdk).ToList();

        // 1. No DllImport or extern method left.
        var legacy = new List<string>();
        // Any DllImport (also inside a multi-attribute bracket) and any extern method, static or not.
        var legacyRx = new Regex(@"\bDllImport\b|\bextern\s+(?!alias\b)\w");
        foreach (string f in sources)
        {
            string[] lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith('/')) continue;
                if (legacyRx.IsMatch(lines[i])) legacy.Add($"{Path.GetRelativePath(sdk, f)}:{i + 1}");
            }
        }
        Check("no DllImport or extern declaration in the SDK", legacy.Count == 0, string.Join(", ", legacy));

        int libraryImports = 0;
        foreach (string f in sources) libraryImports += s_libraryImportRx.Matches(File.ReadAllText(f)).Count;
        Console.WriteLine($"  info: {libraryImports} LibraryImport declarations");
        // Each spelling of the attribute the compiler accepts at the start
        // of a line counts, and one in a comment does not.
        const string spellings =
            "    [LibraryImport(\"a.dll\")]\n" +
            "    [System.Runtime.InteropServices.LibraryImport(\"a.dll\")]\n" +
            "    [global::System.Runtime.InteropServices.LibraryImportAttribute(\"a.dll\")]\n" +
            "    [SuppressGCTransition, LibraryImport(\"a.dll\")]\n" +
            "    /" + "/ [LibraryImport(\"a.dll\")]\n"; // split so a comment scan skips this code line
        int counted = s_libraryImportRx.Matches(spellings).Count;
        Check("the LibraryImport count reads the short, qualified and shared-bracket forms", counted == 4, $"{counted} of 4");

        // 2. The Enumerator parameter is a pointer, not a uint.
        var asm = typeof(HMContext).Assembly;
        foreach (string typeName in new[] { "HIDMaestro.Internal.DeviceManager", "HIDMaestro.Internal.HidDeviceEnumerator" })
        {
            var t = asm.GetType(typeName, throwOnError: true)!;
            var m = t.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                .Single(x => x.Name.StartsWith("SetupDiGetClassDevs"));
            var p = m.GetParameters().Single(x => x.Name!.Equals("enumerator", StringComparison.OrdinalIgnoreCase));
            Check($"{t.Name}.{m.Name} Enumerator is pointer-sized", p.ParameterType == typeof(IntPtr),
                  $"declared {p.ParameterType.Name}");
        }

        // 3. Every named member exists, with an overload of the named shape.
        var types = new Dictionary<string, Type>();
        foreach (var t in asm.GetTypes())
            if (!t.IsNested && !types.ContainsKey(t.Name)) types[t.Name] = t;

        var bad = new List<string>();
        int named = 0;
        foreach (string f in sources)
            CheckText(File.ReadAllText(f), Path.GetRelativePath(sdk, f), types, bad, ref named);
        Console.WriteLine($"  info: {named} Type.Member names checked");
        foreach (string b in bad) Console.WriteLine("    " + b);
        Check("every method or property an SDK message names exists as written", bad.Count == 0, $"{bad.Count} unresolved");

        FixtureSelfTest(types);

        Console.WriteLine(s_failures == 0 ? $"=== ALL PASS ({s_total}) ===" : $"=== FAIL: {s_failures}/{s_total} ===");
        return s_failures == 0 ? 0 : 1;
    }

    const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                                  | BindingFlags.Instance | BindingFlags.FlattenHierarchy;

    static readonly Regex s_nameRx = new(@"(?<![\w.])(?<type>HM[A-Za-z0-9]+|DeviceOrchestrator|DeviceManager|OwnerSweep)\.(?<member>[A-Za-z_]\w*)(?<call>\()?");

    // Resolves every Type.Member and Type.Method(args) the text names, in code, strings, comments and XML docs.
    static void CheckText(string text, string file, Dictionary<string, Type> types, List<string> bad, ref int named)
    {
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') lineStarts.Add(i + 1);
        foreach (Match m in s_nameRx.Matches(text))
        {
            if (!types.TryGetValue(m.Groups["type"].Value, out var type)) continue;
            string member = m.Groups["member"].Value;
            // "DeviceOrchestrator.cs:889" names a file. "Pressure*" names a family.
            if (member == "cs") continue;
            int after = m.Index + m.Length;
            if (!m.Groups["call"].Success && after < text.Length && text[after] == '*') continue;
            var found = type.GetMember(member, AllMembers);
            named++;
            int line = lineStarts.BinarySearch(m.Index);
            if (line < 0) line = ~line;
            string where = $"{file}:{line}";
            if (found.Length == 0) { bad.Add($"{where}  {m.Value}  (no such member)"); continue; }
            if (!m.Groups["call"].Success) continue;
            var methods = found.OfType<MethodBase>().ToList();
            if (methods.Count == 0) continue;
            var argList = CallArgs(text, after - 1, out int close);
            // A call that does not close on its line is prose, not a call.
            if (argList == null) continue;
            var argNames = argList.Select(a => Regex.Match(a, @"^([A-Za-z_]\w*)\s*:(?!:)"))
                                  .Where(x => x.Success).Select(x => x.Groups[1].Value).ToList();
            bool ok = methods.Any(mb =>
            {
                var ps = mb.GetParameters();
                int required = ps.Count(p => !p.IsOptional);
                if (argList.Count < required || argList.Count > ps.Length) return false;
                if (!argNames.All(n => ps.Any(p => p.Name == n))) return false;
                for (int a = 0; a < argList.Count && a < ps.Length; a++)
                {
                    var want = InferType(argList[a], types);
                    if (want != null && !(Nullable.GetUnderlyingType(ps[a].ParameterType) ?? ps[a].ParameterType).IsAssignableFrom(want))
                        return false;
                }
                return true;
            });
            if (!ok) bad.Add($"{where}  {text[m.Index..(close + 1)]}  (no overload takes {argList.Count} argument(s)"
                             + (argNames.Count > 0 ? $" named {string.Join(",", argNames)}" : "") + ")");
        }
    }

    // Each fixture is SDK-style text that names DeviceOrchestrator.LogDiag(string). Commas and
    // parentheses inside any C# string form, char literal or comment are never argument
    // separators, so the right ones resolve and the wrong-count ones are still reported.
    // These run with every static run and count toward the exit code, not the check tally.
    static void FixtureSelfTest(Dictionary<string, Type> types)
    {
        const string L = "DeviceOrchestrator.LogDiag(";
        var fixtures = new (string Name, string Text, int Unresolved)[]
        {
            ("regular string", L + "\"a, b (c), d\");", 0),
            ("regular string with escapes", L + "\"q\\\", w, \\\\\");", 0),
            ("verbatim string", L + "@\"C:\\x, y (z) \"\"q, r\"\"\");", 0),
            ("interpolated string", L + "$\"    usbip: ports {BasePort}..{BasePort + PortRange - 1} all taken, bound {port}\");", 0),
            ("interpolated hole with a string, an alignment and a format", L + "$\"{Pick(\"a,b)\", 2)} x {v,5:N0, )} {{ , }}\");", 0),
            ("interpolated hole with a nested interpolated string", L + "$\"{Pick($\"{n}, {m})\", 1)}, tail\");", 0),
            ("interpolated verbatim $@", L + "$@\"a, b {{ , }} \"\"x, y\"\" {v,4}\");", 0),
            ("interpolated verbatim @$", L + "@$\"a, b (c) {v:0,0} \"\"x, y\"\"\");", 0),
            ("raw string", L + "\"\"\"a, \"b\", (c\"\"\");", 0),
            ("raw string with four quotes", L + "\"\"\"\"a, \"\"\"b\"\"\", (c\"\"\"\");", 0),
            ("interpolated raw string", L + "$\"\"\"a, {v}, \"q\" (x\"\"\");", 0),
            ("interpolated raw string with $$", L + "$$\"\"\"{ , } {{v}} , \"q\" ( \"\"\");", 0),
            ("multi-line raw string", L + "\"\"\"\n  a, b\n  (c\n  \"\"\");", 0),
            ("char literals", L + "string.Concat(',', ')', '(', '\'', '\"', '\u002C'));", 0),
            ("block comment with a comma", L + "\"a\" /* x, (y */);", 0),
            ("block comment before the argument", L + "/* x, y */ \"a\");", 0),
            ("named argument and a string with a colon and comma", L + "message: \"a: b, c\");", 0),
            ("call named in a comment", "// " + L + "\"a, b\") writes the line", 0),
            ("call named in a string with escaped quotes, skipped as prose", "var s = \"see " + L + "\\\"a, b\\\") for the line\";", 0),
            ("wrong count, two plain arguments", L + "\"a\", \"b\");", 1),
            ("wrong count, comma-bearing string then a second argument", L + "\"a, b\", \"c\");", 1),
            ("wrong count, interpolated string then a second argument", L + "$\"a, {v}, b\", $\"c\");", 1),
            ("wrong count, raw string then a second argument", L + "\"\"\"a, b\"\"\", 2);", 1),
            ("wrong count, no arguments", L + ");", 1),
            ("wrong count after a char comma", L + "',', ')');", 1),
            ("wrong count, regular string with parentheses", L + "\"a (b, c)\", \"d\");", 1),
            ("wrong count, verbatim string with parentheses", L + "@\"a (b, \"\"c)\"\"\", \"d\");", 1),
            ("wrong count, interpolated string with parentheses", L + "$\"a ({v}, b)\", \"d\");", 1),
            ("wrong count, interpolated hole with a call", L + "$\"{Pick(\"a,b)\", 2)}\", \"d\");", 1),
            ("wrong count, raw string with parentheses", L + "\"\"\"a (b, c)\"\"\", \"d\");", 1),
            ("wrong count, char literal parentheses", L + "string.Concat('(', ','), \"d\");", 1),
            ("wrong count, comment with a parenthesis", L + "\"a\" /* ( */, \"d\");", 1),
            ("one argument, nested call with two arguments", L + "string.Concat(\"a, b\", \"c\"));", 0),
        };
        int bad = 0;
        foreach (var fx in fixtures)
        {
            var unresolved = new List<string>();
            int named = 0;
            string detail = "";
            try { CheckText(fx.Text, "fixture", types, unresolved, ref named); }
            catch (Exception e) { detail = e.GetType().Name + ": " + e.Message; unresolved.Add("threw"); }
            bool ok = unresolved.Count == fx.Unresolved && detail.Length == 0;
            if (!ok) { bad++; s_failures++; }
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] fixture: {fx.Name}"
                              + (ok ? "" : $"  expected {fx.Unresolved} unresolved, got {unresolved.Count} {detail} {string.Join(" | ", unresolved)}"));
        }
        Console.WriteLine($"  info: {fixtures.Length - bad}/{fixtures.Length} string-form fixtures pass");
    }

    // The arguments of the call whose '(' is at open, split at top-level commas.
    // Null when the call does not close on its own line. close is the index of its ')'.
    // Commas and brackets inside string literals (regular, verbatim, interpolated, raw), char
    // literals and comments never split or close anything.
    static List<string>? CallArgs(string text, int open, out int close)
    {
        close = -1;
        var res = new List<string>();
        int depth = 0, angle = 0, start = open + 1;
        for (int i = open + 1; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r' || c == '\n') return null;
            int skip = c switch
            {
                '"' or '$' or '@' => StringEnd(text, i),
                '\'' => CharEnd(text, i),
                _ => -2,
            };
            if (skip == -1) return null;
            if (skip >= 0) { i = skip - 1; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int e = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (e < 0) return null;
                i = e + 1;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/') return null;
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    if (c != ')') return null;
                    close = i;
                    string last = text[start..i].Trim();
                    if (res.Count > 0 || last.Length > 0) res.Add(last);
                    return res;
                }
                depth--;
            }
            // A generic list opens right after a name. "a < b", "=>", "->", ">=" and ">>" are operators.
            else if (c == '<' && i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_')
                     && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '_')) angle++;
            else if (c == '>' && angle > 0 && text[i - 1] != '=' && text[i - 1] != '-') angle--;
            else if (c == ',' && depth == 0 && angle == 0) { res.Add(text[start..i].Trim()); start = i + 1; }
        }
        return null;
    }

    // The index after the string literal that starts at i, -1 when it starts there and does not
    // end, -2 when no string literal starts there. Forms: "..." with \ escapes, @"..." with "" escapes,
    // $"..." and $@"..." / @$"..." with {hole}, {{ and }} escapes, and """...""" raw strings with
    // any number of quotes, with $-count braces for the holes of $"""...""" and $$"""...""".
    static int StringEnd(string t, int i)
    {
        int j = i, dollars = 0;
        bool verbatim = false;
        for (; j < t.Length && (t[j] == '$' || t[j] == '@'); j++)
        {
            if (t[j] == '$') dollars++; else verbatim = true;
        }
        if (j >= t.Length || t[j] != '"') return -2;
        int quotes = 0;
        while (j + quotes < t.Length && t[j + quotes] == '"') quotes++;
        if (quotes >= 3 && !verbatim)
        {
            // Raw: ends at the first run of the opening length. Holes open at the last `dollars` braces of a run.
            j += quotes;
            while (j < t.Length)
            {
                if (t[j] == '"')
                {
                    int run = 0;
                    while (j + run < t.Length && t[j + run] == '"') run++;
                    if (run >= quotes) return j + quotes;
                    j += run;
                }
                else if (dollars > 0 && t[j] == '{')
                {
                    int run = 0;
                    while (j + run < t.Length && t[j + run] == '{') run++;
                    j += run;
                    if (run < dollars) continue;
                    j = HoleEnd(t, j, dollars);
                    if (j < 0) return -1;
                }
                else j++;
            }
            return -1;
        }
        // "" with nothing after it is the empty string, in every non-raw form.
        j++;
        while (j < t.Length)
        {
            char c = t[j];
            if (c == '"')
            {
                if (verbatim && j + 1 < t.Length && t[j + 1] == '"') { j += 2; continue; }
                return j + 1;
            }
            if (!verbatim && c == '\\') { j += 2; continue; }
            if (!verbatim && (c == '\r' || c == '\n')) return -1;
            if (dollars > 0 && c == '{')
            {
                if (j + 1 < t.Length && t[j + 1] == '{') { j += 2; continue; }
                j = HoleEnd(t, j + 1, 1);
                if (j < 0) return -1;
                continue;
            }
            if (dollars > 0 && c == '}' && j + 1 < t.Length && t[j + 1] == '}') { j += 2; continue; }
            j++;
        }
        return -1;
    }

    // The index after the '}' run that closes an interpolation hole whose code starts at j, -1 when
    // it never closes. The code may hold strings, chars, brackets, and a ":format" or ",alignment"
    // tail after the expression, which runs to the closing braces. closing is the brace count that
    // ends the hole (the $ count of a raw string).
    static int HoleEnd(string t, int j, int closing)
    {
        int paren = 0, brace = 0;
        while (j < t.Length)
        {
            char c = t[j];
            int skip = c switch
            {
                '"' or '$' or '@' => StringEnd(t, j),
                '\'' => CharEnd(t, j),
                _ => -2,
            };
            if (skip == -1) return -1;
            if (skip >= 0) { j = skip; continue; }
            if (c is '(' or '[') paren++;
            else if (c is ')' or ']') paren--;
            else if (c == '{') brace++;
            else if (c == '}')
            {
                if (brace > 0) brace--;
                else
                {
                    int run = 0;
                    while (j + run < t.Length && t[j + run] == '}') run++;
                    if (run >= closing) return j + closing;
                    j += run;
                    continue;
                }
            }
            else if (c == ':' && paren == 0 && brace == 0 && !(j + 1 < t.Length && t[j + 1] == ':'))
            {
                // Format specifier: plain text to the closing braces.
                j++;
                while (j < t.Length && t[j] != '}') j++;
                continue;
            }
            j++;
        }
        return -1;
    }

    // The index after the char literal that starts at i, -2 when the quote is an apostrophe in prose.
    static int CharEnd(string t, int i)
    {
        if (i + 2 >= t.Length) return -2;
        if (t[i + 1] == '\\')
        {
            // '\'' and '\\' escape the next char. '\u002C' and '\x2C' run to the closing quote.
            int j = i + 3;
            while (j < t.Length && j - i <= 12 && t[j] != '\'') j++;
            return j < t.Length && t[j] == '\'' ? j + 1 : -2;
        }
        return t[i + 2] == '\'' && t[i + 1] != '\n' && t[i + 1] != '\r' ? i + 3 : -2;
    }

    // The type an argument names: a literal, an enum member, or (in a cref) a type name.
    // Null when the text says nothing certain, which skips the type check.
    static Type? InferType(string arg, Dictionary<string, Type> types)
    {
        arg = Regex.Replace(arg, @"^[A-Za-z_]\w*\s*:(?!:)\s*", "");
        if (arg is "true" or "false" or "bool") return typeof(bool);
        if (arg == "byte") return typeof(byte);
        if (arg == "int") return typeof(int);
        if (arg == "ushort") return typeof(ushort);
        if (arg == "uint") return typeof(uint);
        if (arg == "string") return typeof(string);
        if (types.TryGetValue(arg, out var t)) return t;
        var m = Regex.Match(arg, @"^(HM\w+)\.\w+$");
        if (m.Success && types.TryGetValue(m.Groups[1].Value, out var e) && e.IsEnum) return e;
        return null;
    }

    static int Live()
    {
        Console.WriteLine("=== XInputGetState and SetupDiGetClassDevsW callers, live ===");
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                foreach (ProcessModule m in p.Modules)
                    if (string.Equals(m.ModuleName, "HIDMaestro.Core.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"  [SKIP] {p.ProcessName} is running and shares the SDK's devices.");
                        return 2;
                    }
            }
            catch { }
            finally { p.Dispose(); }
        }

        DeviceManager.RemoveAccumulatedHmPhantoms();
        string diag = Path.Combine(Path.GetTempPath(), "HIDMaestro", "teardown_diag.log");
        long start = File.Exists(diag) ? new FileInfo(diag).Length : 0;

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var profile = ctx.GetProfile("xbox-360-wired")!;
        var ctrl = ctx.CreateController(profile);
        string wait = "";
        try
        {
            using var fs = new FileStream(diag, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) != null)
                if (line.Contains("XInput slot wait:")) wait = line;
        }
        catch (Exception e) { wait = "unreadable: " + e.Message; }
        Console.WriteLine("  " + wait);
        var m2 = Regex.Match(wait, @"before=(\d+) after=(\d+)");
        Check("the SDK's XInputGetState count sees the new slot",
              m2.Success && int.Parse(m2.Groups[2].Value) > int.Parse(m2.Groups[1].Value), wait);

        string instanceId = ctrl.InstanceId ?? "";
        ctrl.Dispose();
        // DifRemoveDevice (SetupDiGetClassDevsW) removes the parent devnode.
        Check("the devnode is gone within 5 s of Dispose",
              instanceId.Length > 0 && DeviceManager.WaitForDeviceRemoval(instanceId, 5000), instanceId);
        // Exactly the one record this run's dispose leaves behind, after a sweep that clears older ones.
        int removed = DeviceManager.RemoveAccumulatedHmPhantoms();
        Check("the phantom sweep (SetupDiGetClassDevsW enumeration) removes the one record this run left",
              removed == 1, $"removed {removed}");

        Console.WriteLine(s_failures == 0 ? $"=== ALL PASS ({s_total}) ===" : $"=== FAIL: {s_failures}/{s_total} ===");
        return s_failures == 0 ? 0 : 1;
    }
}
