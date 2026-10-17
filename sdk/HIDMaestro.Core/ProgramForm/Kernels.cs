namespace HIDMaestro.Internal.Programs;

/// <summary>A generated report program as a value type, so code generic over
/// it is compiled once per program and the program inlined into it.</summary>
internal interface IReportKernel
{
    unsafe void Run(byte* s, byte* d, uint* r, uint* c);
}

/// <summary>A device's layouts on one clock as a value type: <see cref="Run"/>
/// runs the program of the layout the engine selected. The generated sets
/// switch over their kernels, and the interpreted set walks a stage array.</summary>
internal interface IReportSet
{
    int Count { get; }
    unsafe void Run(int index, byte* s, byte* d, uint* r, uint* c);
}

/// <summary>Builds the generic code for one set type. The generated switch
/// over the catalog's set keys calls it with the set's own type, so the JIT
/// and NativeAOT compile that code once per set.</summary>
internal interface IReportSetBinder
{
    object Bind<TSet>() where TSet : struct, IReportSet;
}
