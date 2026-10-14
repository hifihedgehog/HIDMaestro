// Interop declarations and the method names in the SDK's text.
//
// static (offline, no device):
//   1. No DllImport or extern declaration is left in the SDK sources. Every
//      P/Invoke is a LibraryImport stub generated at build time.
//   2. SetupDiGetClassDevsW's Enumerator is PCWSTR in setupapi.h, so both
//      declarations take a pointer-sized value, not a uint.
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

        Console.WriteLine(s_failures == 0 ? $"=== ALL PASS ({s_total}) ===" : $"=== FAIL: {s_failures}/{s_total} ===");
        return s_failures == 0 ? 0 : 1;
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
