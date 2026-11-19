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
/// report through ProtocolEngine.BuildNext over the device's set type, as
/// the protocol host of a UMDF2 device does, so
/// that method, compiled for each catalog set, must hold no call to a
/// kernel or to the set's Run. Under the JIT a child process runs BuildNext
/// for each catalog device and for a set whose kernel is marked NoInlining,
/// with DOTNET_JitDisasm naming BuildNext, and the parent reads the listings.
/// The planted set must show its call, or the check proves nothing. The JIT
/// run then reads the NativeAOT build beside it the same way, through
/// dumpbin /disasm with the build's symbols, where a Visual Studio C++
/// toolset is installed. Each listing is read with the call instructions of
/// its architecture: call on x64, bl on ARM64. The ARM64 JIT names no target
/// of a call through a register, so there the JIT run also lists every
/// kernel and Run the child compiles on its own, which it does only for one
/// that is called: the planted kernel must be among them and no catalog
/// one may be.</summary>
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
    /// reports, then for the planted set on the engine of the last of those
    /// devices whose clock tick builds a report from it. A tick that sends
    /// nothing returns 0 without running the set, and on ARM64 the JIT lists
    /// the planted kernel only once it runs, so an engine that sends nothing
    /// on its clock would leave the check blind. The planted set's Run calls
    /// its kernel for every layout index.</summary>
    public static int Child()
    {
        var engines = new List<(string Id, ProtocolEngine Engine)>();
        foreach (var p in ProfileDatabase.LoadEmbedded().All.Where(p => p.BuildsItsOwnReports))
        {
            var model = p.GetOrCompileModel();
            var runner = ProgramBinder.BindLayouts(model)!;
            // A UMDF2 device has no USB descriptors: its HID driver carries the
            // reports, which the SDK's protocol host builds through the same
            // BuildNext.
            var compiled = CompiledProtocol.Compile(p, p.RequiresUsbipBackend ? new UsbDescriptorSet(p, 0) : null)!;
            var engine = new ProtocolEngine(compiled, "HM-INLINE-0001", 0, runner);
            int bytes = 0;
            for (int layout = 0; layout < model.Layouts.Length; layout++)
                bytes += runner.Visit(new Build { Engine = engine });
            Console.WriteLine($"built {p.Id} through {(runner.Generated ? "its generated set" : "the interpreter")}, {bytes} bytes on its clock");
            engines.Add((p.Id, engine));
        }
        if (engines.Count == 0) { Console.WriteLine("no catalog device builds its reports"); return 1; }
        var v = ProgramForm.Validate(ProbeCorpus.NotInlined);
        if (!ProbeKernels.TryBindSet($"{v.Hash}|{PathMutation.NotInlined}", new Binder(), out var bound) || bound is not LayoutRunner planted)
        {
            Console.WriteLine("the probe's build has no NotInlined set");
            return 1;
        }
        for (int i = engines.Count - 1; i >= 0; i--)
        {
            int bytes = planted.Visit(new Build { Engine = engines[i].Engine });
            if (bytes == 0) continue;
            Console.WriteLine($"built the planted set on {engines[i].Id}'s engine, {bytes} bytes");
            return 0;
        }
        Console.WriteLine("no catalog device's engine builds a report from the planted set on its clock, so its kernel never ran");
        return 1;
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
        // The JIT appends to this file, and a run that ended before its
        // cleanup leaves one behind for a later process with the same id.
        File.Delete(listing);
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add("inline-child");
        // On ARM64 the JIT calls a method it has not compiled yet through a
        // register, blr, loaded from an address the listing gives as bare
        // movz and movk immediates, so the listing names no target. A kernel
        // or Run that is called rather than inlined is then compiled on its
        // own when the call first runs, and that listing names it: the
        // child also lists every method whose name starts with K and every
        // Run.
        bool arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        start.Environment["DOTNET_JitDisasm"] = arm64 ? "BuildNext K* Run" : "BuildNext";
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
        var all = text.Split("; Assembly listing for method ", StringSplitOptions.RemoveEmptyEntries).ToList();
        var methods = all.Where(m => m.Contains("BuildNext[", StringComparison.Ordinal)).ToList();
        int catalog = 0, planted = 0;
        foreach (var m in methods)
        {
            string header = m[..m.IndexOf('\n')].Trim();
            bool isPlanted = header.Contains("_NotInlined", StringComparison.Ordinal);
            bool isCatalog = header.Contains("Programs.Set", StringComparison.Ordinal) && !isPlanted;
            if (!isPlanted && !isCatalog) continue;
            var calls = JitCalls(m.Split('\n'), arm64)
                .Where(l => l.Contains("Kernels:K", StringComparison.Ordinal) || l.Contains(":Run(", StringComparison.Ordinal))
                .ToList();
            string set = header.Contains('[') ? header[(header.IndexOf('[') + 1)..header.IndexOf(']')] : header;
            if (isPlanted)
            {
                planted++;
                if (arm64) Console.WriteLine($"{set}: BuildNext over the planted set is listed");
                else if (calls.Count > 0) Console.WriteLine($"{set}: the planted NoInlining kernel is called ({calls[0]}), so the check sees a call");
                else Checks.Fail($"{set}: the planted NoInlining kernel shows no call, so the check cannot see one");
            }
            else
            {
                catalog++;
                if (calls.Count == 0) Console.WriteLine($"{set}: BuildNext holds no{(arm64 ? " named" : "")} call to a kernel or the set's Run");
                else Checks.Fail($"{set}: BuildNext calls {string.Join("; ", calls)}");
            }
        }
        if (catalog == 0) Checks.Fail("inline: no listing of BuildNext over a catalog set");
        if (planted == 0) Checks.Fail("inline: no listing of BuildNext over the planted set");
        if (arm64)
        {
            // The kernels and Runs compiled on their own: each one was called.
            var compiled = all.Select(m => m[..Math.Max(0, m.IndexOf('\n'))].Trim())
                .Where(h => h.Contains("Kernels:K", StringComparison.Ordinal)
                    || (h.Contains("Programs.Set", StringComparison.Ordinal) && h.Contains(":Run(", StringComparison.Ordinal)))
                .ToList();
            var plantedKernel = compiled.Where(h => h.Contains("_NotInlined", StringComparison.Ordinal)).ToList();
            var catalogCalled = compiled.Where(h => !h.Contains("_NotInlined", StringComparison.Ordinal)).ToList();
            if (plantedKernel.Count > 0) Console.WriteLine($"the planted NoInlining kernel is compiled on its own ({plantedKernel[0]}), so the check sees a call");
            else Checks.Fail("the planted NoInlining kernel is not compiled on its own, so the check cannot see a call");
            if (catalogCalled.Count == 0) Console.WriteLine("no catalog kernel or set's Run is compiled on its own: each is inlined where it runs");
            else Checks.Fail($"called rather than inlined: {string.Join("; ", catalogCalled)}");
        }
        Aot();
        return Checks.Failures - before;
    }

    /// <summary>The calls in one method of the JIT's listing that name their
    /// target. On x64 a call is one instruction, call, with its target on the
    /// line. On ARM64 a direct call is bl with its target on the line, and a
    /// call through a register, blr, names none.</summary>
    internal static IEnumerable<string> JitCalls(IEnumerable<string> lines, bool arm64)
    {
        foreach (var raw in lines)
        {
            string l = raw.Trim();
            if (arm64 ? l.StartsWith("bl ", StringComparison.Ordinal)
                      : l.StartsWith("call ", StringComparison.Ordinal) || l.Contains(" call ", StringComparison.Ordinal))
                yield return l;
        }
    }

    /// <summary>The target of a call in one line of dumpbin's disassembly, or
    /// null when the line holds no call. x64 calls with call, ARM64 with bl,
    /// or blr through a register, which names no target.</summary>
    internal static string? AotCall(string line, bool arm64)
    {
        string op = arm64 ? " bl " : " call ";
        int at = line.IndexOf(op, StringComparison.Ordinal);
        return at < 0 ? null : line[(at + op.Length)..].Trim();
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
        // HM_PEC_AOT_ARCH names the other architecture's build, so an x64
        // machine's dumpbin can read the ARM64 build, which the probe's ARM64
        // output carries, through the ARM64 reading.
        string arch = Environment.GetEnvironmentVariable("HM_PEC_AOT_ARCH") is ("arm64" or "x64") and var named
            ? named
            : RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        bool arm64 = arch == "arm64";
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
                else if (name != null && AotCall(line, arm64) is { } target)
                {
                    calls.Add(target);
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
