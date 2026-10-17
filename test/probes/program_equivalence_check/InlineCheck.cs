using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;
using HIDMaestro.Internal.Usbip;

/// <summary>inline: the generated program inlines into the code that sends
/// its reports. The device clock and a USB/IP device's reads both build a
/// report through ProtocolEngine.BuildNext over the device's set type, so
/// that method, compiled for each catalog set, must hold no call to a
/// kernel or to the set's Run. Under the JIT a child process runs BuildNext
/// for each catalog device and for a set whose kernel is marked NoInlining,
/// with DOTNET_JitDisasm naming BuildNext, and the parent reads the listings.
/// The planted set must show its call, or the check proves nothing. The JIT
/// run then reads the NativeAOT build beside it the same way, through
/// dumpbin /disasm with the build's symbols, where a Visual Studio C++
/// toolset is installed.</summary>
internal static class InlineCheck
{
    sealed class Binder : IReportSetBinder
    {
        public object Bind<TSet>() where TSet : struct, IReportSet => new LayoutRunner<TSet>(default);
    }

    sealed class Build : ILayoutVisitor<int>
    {
        public ProtocolEngine Engine = null!;
        public readonly byte[] Buffer = new byte[64];
        public int Visit<TSet>(ref TSet set) where TSet : struct, IReportSet
        {
            int n = 0;
            for (int i = 0; i < 1000; i++) n += Engine.BuildNext(ref set, Buffer);
            return n;
        }
    }

    /// <summary>The child: BuildNext for every catalog device that builds its
    /// reports, then for the planted set.</summary>
    public static int Child()
    {
        ProtocolEngine? last = null;
        foreach (var p in ProfileDatabase.LoadEmbedded().All.Where(p => p.BuildsItsOwnReports))
        {
            var model = p.GetOrCompileModel();
            var runner = ProgramBinder.BindLayouts(model)!;
            var compiled = CompiledProtocol.Compile(p, new UsbDescriptorSet(p, 0))!;
            var engine = new ProtocolEngine(compiled, "HM-INLINE-0001", 0, runner);
            for (int layout = 0; layout < model.Layouts.Length; layout++)
                runner.Visit(new Build { Engine = engine });
            Console.WriteLine($"built {p.Id} through {(runner.Generated ? "its generated set" : "the interpreter")}");
            last = engine;
        }
        if (last == null) { Console.WriteLine("no catalog device builds its reports"); return 1; }
        var v = ProgramForm.Validate(ProbeCorpus.NotInlined);
        if (!ProbeKernels.TryBindSet($"{v.Hash}|{PathMutation.NotInlined}", new Binder(), out var bound) || bound is not LayoutRunner planted)
        {
            Console.WriteLine("the probe's build has no NotInlined set");
            return 1;
        }
        planted.Visit(new Build { Engine = last });
        Console.WriteLine("built the planted set");
        return 0;
    }

    public static int Run()
    {
        int before = Checks.Failures;
        Console.WriteLine("== Inlining: no call to a program in the code that sends its reports");
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            Console.WriteLine("inline: read under the JIT. A NativeAOT build is read with dumpbin (scripts in the lane's report)");
            return 0;
        }
        string listing = Path.Combine(Path.GetTempPath(), $"HIDMaestro_inline_{Environment.ProcessId}.txt");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("inline-child");
        start.Environment["DOTNET_JitDisasm"] = "BuildNext";
        start.Environment["DOTNET_JitStdOutFile"] = listing;
        start.Environment.Remove("HIDMAESTRO_DIAG");
        string output;
        int exit;
        using (var child = Process.Start(start)!)
        {
            var err = child.StandardError.ReadToEndAsync();
            output = child.StandardOutput.ReadToEnd();
            child.WaitForExit();
            exit = child.ExitCode;
            output += err.Result;
        }
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("  child: " + line.TrimEnd());
        if (exit != 0 || !File.Exists(listing)) { Checks.Fail($"inline: the child exited {exit}, listing {(File.Exists(listing) ? "written" : "missing")}"); return Checks.Failures - before; }
        string text = File.ReadAllText(listing);
        File.Delete(listing);
        var methods = text.Split("; Assembly listing for method ", StringSplitOptions.RemoveEmptyEntries)
            .Where(m => m.Contains("BuildNext[", StringComparison.Ordinal)).ToList();
        int catalog = 0, planted = 0;
        foreach (var m in methods)
        {
            string header = m[..m.IndexOf('\n')].Trim();
            bool isPlanted = header.Contains("_NotInlined", StringComparison.Ordinal);
            bool isCatalog = header.Contains("Programs.Set", StringComparison.Ordinal) && !isPlanted;
            if (!isPlanted && !isCatalog) continue;
            var calls = m.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("call ", StringComparison.Ordinal) || l.Contains(" call ", StringComparison.Ordinal))
                .Where(l => l.Contains("Kernels:K", StringComparison.Ordinal) || l.Contains(":Run(", StringComparison.Ordinal))
                .ToList();
            string set = header.Contains('[') ? header[(header.IndexOf('[') + 1)..header.IndexOf(']')] : header;
            if (isPlanted)
            {
                planted++;
                if (calls.Count > 0) Console.WriteLine($"{set}: the planted NoInlining kernel is called ({calls[0]}), so the check sees a call");
                else Checks.Fail($"{set}: the planted NoInlining kernel shows no call, so the check cannot see one");
            }
            else
            {
                catalog++;
                if (calls.Count == 0) Console.WriteLine($"{set}: BuildNext holds no call to a kernel or the set's Run");
                else Checks.Fail($"{set}: BuildNext calls {string.Join("; ", calls)}");
            }
        }
        if (catalog == 0) Checks.Fail("inline: no listing of BuildNext over a catalog set");
        if (planted == 0) Checks.Fail("inline: no listing of BuildNext over the planted set");
        Aot();
        return Checks.Failures - before;
    }

    /// <summary>dumpbin from the newest Visual Studio C++ toolset for this
    /// machine, or null. HM_PEC_DUMPBIN names another, for the check that a
    /// missing dumpbin fails the probe.</summary>
    static string? Dumpbin()
    {
        if (Environment.GetEnvironmentVariable("HM_PEC_DUMPBIN") is { Length: > 0 } named)
            return File.Exists(named) ? named : null;
        string host = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? Path.Combine("HostARM64", "arm64") : Path.Combine("Hostx64", "x64");
        var found = new List<string>();
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            string vs = Path.Combine(root, "Microsoft Visual Studio");
            if (!Directory.Exists(vs)) continue;
            foreach (var msvc in Directory.EnumerateDirectories(vs, "MSVC", SearchOption.AllDirectories))
                foreach (var version in Directory.EnumerateDirectories(msvc))
                {
                    string exe = Path.Combine(version, "bin", host, "dumpbin.exe");
                    if (File.Exists(exe)) found.Add(exe);
                }
        }
        return found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).LastOrDefault();
    }

    /// <summary>The NativeAOT build's BuildNext over each set, read from
    /// dumpbin's disassembly, one function per symbol line.</summary>
    static void Aot()
    {
        string arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string aot = Path.Combine(AppContext.BaseDirectory, "aot-" + arch, "ProgramEquivalenceCheck.exe");
        string? dumpbin = Dumpbin();
        // The check reads the NativeAOT build or fails: a machine without the
        // build or without dumpbin cannot tell an inlined kernel from a call.
        // HM_PEC_NO_AOT=1 leaves it out by name, for a build made with
        // -p:PecSkipAot=true.
        if (Environment.GetEnvironmentVariable("HM_PEC_NO_AOT") == "1")
        {
            Console.WriteLine("inline, NativeAOT: left out, HM_PEC_NO_AOT=1");
            return;
        }
        if (!File.Exists(aot)) { Checks.Fail($"inline, NativeAOT: no NativeAOT build at {aot}"); return; }
        if (dumpbin == null) { Checks.Fail("inline, NativeAOT: no dumpbin: install a Visual Studio C++ toolset, or name one in HM_PEC_DUMPBIN"); return; }
        var start = new ProcessStartInfo(dumpbin) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/disasm:nobytes");
        start.ArgumentList.Add(aot);
        int catalog = 0, planted = 0;
        string? name = null;
        var calls = new List<string>();
        void End()
        {
            if (name == null || !name.Contains("ProtocolEngine__BuildNext<", StringComparison.Ordinal) || !name.Contains("_Programs_Set", StringComparison.Ordinal)) return;
            var kernel = calls.Where(c => c.Contains("Kernels__K", StringComparison.Ordinal) || c.Contains("__Run", StringComparison.Ordinal)).ToList();
            string set = name[(name.IndexOf('<') + 1)..name.LastIndexOf('>')];
            if (name.Contains("_NotInlined", StringComparison.Ordinal))
            {
                planted++;
                if (kernel.Count > 0) Console.WriteLine($"NativeAOT {set}: the planted NoInlining kernel is called ({kernel[0]}), so the check sees a call");
                else Checks.Fail($"NativeAOT {set}: the planted NoInlining kernel shows no call");
            }
            else if (!name.Contains("_Programs_SetK", StringComparison.Ordinal))
            {
                catalog++;
                if (kernel.Count == 0) Console.WriteLine($"NativeAOT {set}: BuildNext holds no call to a kernel or the set's Run");
                else Checks.Fail($"NativeAOT {set}: BuildNext calls {string.Join("; ", kernel)}");
            }
        }
        using (var p = Process.Start(start)!)
        {
            var err = p.StandardError.ReadToEndAsync();
            for (string? line; (line = p.StandardOutput.ReadLine()) != null;)
            {
                if (line.Length > 0 && line[0] != ' ' && line.EndsWith(':'))
                {
                    End();
                    name = line[..^1];
                    calls.Clear();
                }
                else if (name != null && line.Contains(" call ", StringComparison.Ordinal))
                {
                    calls.Add(line[(line.IndexOf(" call ", StringComparison.Ordinal) + 6)..].Trim());
                }
            }
            End();
            p.WaitForExit();
            _ = err.Result;
        }
        if (catalog == 0) Checks.Fail("inline, NativeAOT: no BuildNext over a catalog set in the build");
        if (planted == 0) Checks.Fail("inline, NativeAOT: no BuildNext over the planted set in the build");
    }
}
