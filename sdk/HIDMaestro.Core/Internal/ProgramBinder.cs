using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HIDMaestro.Internal.Programs;

namespace HIDMaestro.Internal;

/// <summary>A device's layouts, bound once per controller: the generated set
/// for a catalog device, whose kernels the build step emitted from the same
/// JSON, or the interpreted set for any other. <see cref="Run"/> serves the
/// reads that are not on the clock, such as a GET_REPORT. The clock reaches
/// the set through <see cref="Visit"/>, which hands the set's own type to code
/// generic over it, so the JIT and NativeAOT compile that code once per set
/// and inline its programs there.</summary>
internal abstract unsafe class LayoutRunner
{
    public abstract bool Generated { get; }
    public abstract void Run(int layout, byte* s, byte* d, uint* r, uint* c);
    public abstract TResult Visit<TResult>(ILayoutVisitor<TResult> visitor);
}

/// <summary>Code generic over a set's type, built for it once.</summary>
internal interface ILayoutVisitor<TResult>
{
    TResult Visit<TSet>(ref TSet set) where TSet : struct, IReportSet;
}

internal sealed unsafe class LayoutRunner<TSet> : LayoutRunner where TSet : struct, IReportSet
{
    private TSet _set;

    public LayoutRunner(TSet set) { _set = set; }

    public override bool Generated => typeof(TSet) != typeof(InterpretedSet);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public override void Run(int layout, byte* s, byte* d, uint* r, uint* c) => _set.Run(layout, s, d, r, c);

    public override TResult Visit<TResult>(ILayoutVisitor<TResult> visitor) => visitor.Visit(ref _set);
}

/// <summary>The interpreter's set: one compiled program per layout.</summary>
internal readonly unsafe struct InterpretedSet : IReportSet
{
    private readonly ReportProgram[] _programs;

    public InterpretedSet(ReportProgram[] programs) { _programs = programs; }

    public int Count => _programs.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Run(int index, byte* s, byte* d, uint* r, uint* c) => _programs[index].Run(s, d, r, c);
}

/// <summary>One decoder, bound: its generated kernel through a function
/// pointer, or its interpreted program.</summary>
internal sealed unsafe class DecoderRunner
{
    private readonly delegate*<byte*, byte*, uint*, uint*, void> _kernel;
    private readonly ReportProgram? _interpreted;
    public DecoderForm Form { get; }
    public bool Generated => _kernel != null;

    public DecoderRunner(DecoderForm form, delegate*<byte*, byte*, uint*, uint*, void> kernel, ReportProgram? interpreted)
    {
        Form = form;
        _kernel = kernel;
        _interpreted = interpreted;
    }

    /// <summary>Decodes <paramref name="s"/>, the report with the slack every
    /// read's 32-bit load needs past its end, into <paramref name="d"/>, one
    /// byte a field.</summary>
    public void Run(byte* s, byte* d)
    {
        if (_kernel != null) _kernel(s, d, null, null);
        else _interpreted!.Run(s, d, null, null);
    }
}

/// <summary>One program, bound: its generated kernel through a function
/// pointer, or its interpreted program, for a report built off the clock,
/// such as a command's emitted report or a live feature report.</summary>
internal sealed unsafe class BoundProgram
{
    private readonly delegate*<byte*, byte*, uint*, uint*, void> _kernel;
    private readonly ReportProgram? _interpreted;
    public ValidProgram Program { get; }
    public bool Generated => _kernel != null;

    public BoundProgram(ValidProgram program, delegate*<byte*, byte*, uint*, uint*, void> kernel)
    {
        Program = program;
        _kernel = kernel;
        _interpreted = kernel == null ? new ReportProgram(program) : null;
    }

    public void Run(byte* s, byte* d, uint* r, uint* c)
    {
        if (_kernel != null) _kernel(s, d, r, c);
        else _interpreted!.Run(s, d, r, c);
    }
}

/// <summary>Looks a compiled program's hash up in the kernels the build step
/// generated from the catalog, and builds the code that runs it, once per
/// controller. A program no generated kernel matches, from a profile outside
/// the catalog or an edited copy of a catalog one, falls back to the
/// interpreter with the same bytes. No dispatch runs per report: a clock runs
/// code built over the set's type, and a decoder is one function pointer.</summary>
internal static unsafe class ProgramBinder
{
    private sealed class Binder : IReportSetBinder
    {
        public object Bind<TSet>() where TSet : struct, IReportSet => new LayoutRunner<TSet>(default);
    }

    private static readonly Binder s_binder = new();

    /// <summary>The runner for a model's layouts, or null for a model with
    /// none.</summary>
    public static LayoutRunner? BindLayouts(CompiledModel model, bool interpretOnly = false)
    {
        if (model.LayoutSet is not { } set) return null;
        if (!interpretOnly && CatalogKernels.TryBindSet(set.Key, s_binder, out var bound) && bound is LayoutRunner generated)
            return generated;
        return new LayoutRunner<InterpretedSet>(new InterpretedSet(set.Layouts.Select(l => new ReportProgram(l)).ToArray()));
    }

    /// <summary>The generated kernel whose build-time hash is
    /// <paramref name="hash"/>, or null.</summary>
    public static delegate*<byte*, byte*, uint*, uint*, void> Kernel(string hash) =>
        CatalogKernels.ByHash.TryGetValue(hash, out nint fn) ? (delegate*<byte*, byte*, uint*, uint*, void>)fn : null;

    /// <summary>One program, bound to its generated kernel or to the
    /// interpreter.</summary>
    public static BoundProgram Bind(ValidProgram program, bool interpretOnly = false) =>
        new(program, interpretOnly ? null : Kernel(program.Hash));

    public static DecoderRunner[] BindDecoders(CompiledModel model, bool interpretOnly = false)
    {
        var runners = new DecoderRunner[model.Decoders.Length];
        for (int i = 0; i < runners.Length; i++)
        {
            var form = model.Decoders[i];
            var kernel = interpretOnly ? null : Kernel(form.Program.Hash);
            runners[i] = new DecoderRunner(form, kernel, kernel == null ? new ReportProgram(form.Program) : null);
        }
        return runners;
    }
}
