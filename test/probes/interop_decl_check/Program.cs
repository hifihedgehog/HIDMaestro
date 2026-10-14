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
        var liRx = new Regex(@"^\s*\[LibraryImport\(", RegexOptions.Multiline);
        foreach (string f in sources) libraryImports += liRx.Matches(File.ReadAllText(f)).Count;
        Console.WriteLine($"  info: {libraryImports} LibraryImport declarations");

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

        var rx = new Regex(@"(?<![\w.])(?<type>HM[A-Za-z0-9]+|DeviceOrchestrator|DeviceManager|OwnerSweep)\.(?<member>[A-Za-z_]\w*)(?<call>\((?<args>[^()\r\n]*)\))?");
        var bad = new List<string>();
        int named = 0;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static
                               | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
        foreach (string f in sources)
        {
            string[] lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match m in rx.Matches(lines[i]))
                {
                    if (!types.TryGetValue(m.Groups["type"].Value, out var type)) continue;
                    string member = m.Groups["member"].Value;
                    // "DeviceOrchestrator.cs:889" names a file. "Pressure*" names a family.
                    if (member == "cs") continue;
                    int after = m.Index + m.Length;
                    if (!m.Groups["call"].Success && after < lines[i].Length && lines[i][after] == '*') continue;
                    var found = type.GetMember(member, all);
                    named++;
                    string where = $"{Path.GetRelativePath(sdk, f)}:{i + 1}";
                    if (found.Length == 0) { bad.Add($"{where}  {m.Value}  (no such member)"); continue; }
                    if (!m.Groups["call"].Success) continue;
                    var methods = found.OfType<MethodBase>().ToList();
                    if (methods.Count == 0) continue;
                    var argList = SplitArgs(m.Groups["args"].Value.Trim());
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
                    if (!ok) bad.Add($"{where}  {m.Value}  (no overload takes {argList.Count} argument(s)"
                                     + (argNames.Count > 0 ? $" named {string.Join(",", argNames)}" : "") + ")");
                }
            }
        }
        Console.WriteLine($"  info: {named} Type.Member names checked");
        foreach (string b in bad) Console.WriteLine("    " + b);
        Check("every method or property an SDK message names exists as written", bad.Count == 0, $"{bad.Count} unresolved");

        Console.WriteLine(s_failures == 0 ? $"=== ALL PASS ({s_total}) ===" : $"=== FAIL: {s_failures}/{s_total} ===");
        return s_failures == 0 ? 0 : 1;
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

    // Top-level comma split that ignores commas inside <>, () and [].
    static List<string> SplitArgs(string text)
    {
        var res = new List<string>();
        if (text.Length == 0) return res;
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (c == ',' && depth == 0) { res.Add(text[start..i].Trim()); start = i + 1; }
        }
        res.Add(text[start..].Trim());
        return res;
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
