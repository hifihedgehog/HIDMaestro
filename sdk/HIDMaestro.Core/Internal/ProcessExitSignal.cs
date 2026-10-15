using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace HIDMaestro.Internal;

/// <summary>
/// Whether the runtime has begun to raise ProcessExit. Nothing public says
/// so: Environment.HasShutdownStarted returns false unconditionally on .NET
/// 10 (Environment.cs, "since .NET Core does not support object finalization
/// during shutdown"). AppContext.OnProcessExit raises every load context's
/// Unloading event first, the default context's included
/// (AssemblyLoadContext.OnProcessExit), and only then ProcessExit
/// (AppDomain.OnProcessExit). NativeAOT raises exit through the same
/// AppContext.OnProcessExit (Environment.NativeAot.cs, ShutdownCore) and
/// compiles the same AssemblyLoadContext source. So a handler on the default
/// context's Unloading, subscribed before exit begins, has run by the time
/// any ProcessExit handler runs.
///
/// <para>The SDK subscribes from its module initializer. Under NativeAOT
/// module initializers run at startup, so the signal always holds. Under
/// .NET the initializer runs when code that refers to the SDK is first
/// compiled, or first run where it was compiled ahead of time: a method
/// that names an SDK type, even in a branch never taken, runs it when the
/// method is compiled. So the signal misses a program in which no such code
/// ran or was compiled before exit began, such as one that uses the SDK
/// only inside a ProcessExit handler of its own: the Unloading event was
/// raised before the subscription, and it is raised once
/// (RaiseUnloadEvent swaps the delegate out with Interlocked.Exchange).
/// Measured with a library that subscribes from its module initializer,
/// under .NET 10 and NativeAOT, returning from Main, calling
/// Environment.Exit, and calling it on another thread: loaded at startup,
/// the flag was set inside every ProcessExit handler on both runtimes.
/// First loaded inside the handler, it was not set under .NET and was set
/// under NativeAOT.</para>
///
/// <para>A handler on the default context holds a delegate to this
/// assembly's code, which would keep a collectible load context that loaded
/// the SDK from ever unloading. So when the SDK lives in a context other
/// than the default, that context's own Unloading removes the handler. It
/// also sets the flag, because process exit raises every context's event in
/// the order of the runtime's list of contexts (AssemblyLoadContext
/// .OnProcessExit), which can put this one before the default. After an
/// Unload, the flag only makes a first call run the cleanup on the calling
/// thread before it returns.</para>
/// </summary>
internal static class ProcessExitSignal
{
    private static volatile bool s_raised;

    /// <summary>True once the runtime has begun to raise ProcessExit.</summary>
    internal static bool Raised => s_raised;

    // CA2255 warns that a module initializer in a library "can interfere
    // with application initialization and also lead to limitations in that
    // application's trimming abilities", and it allows suppression only in
    // a library that is not distributed, which this one is. Accepted here:
    // the initializer adds one or two event handlers and runs nothing else,
    // so an application's initialization order cannot change what it does,
    // and the trimmer keeps it with no warning in the AOT and trimmed smoke
    // builds. The fix the rule gives, an initialization method the program
    // calls, cannot reach a program that first uses the SDK inside its own
    // exit handler, and a static constructor would subscribe only at first
    // use of its type, which under NativeAOT misses that program too.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Subscribe()
    {
        AssemblyLoadContext.Default.Unloading += OnDefaultUnloading;
        AssemblyLoadContext? own = AssemblyLoadContext.GetLoadContext(typeof(ProcessExitSignal).Assembly);
        if (own != null && own != AssemblyLoadContext.Default)
            own.Unloading += OnOwnUnloading;
    }
#pragma warning restore CA2255

    private static void OnDefaultUnloading(AssemblyLoadContext context) => s_raised = true;

    private static void OnOwnUnloading(AssemblyLoadContext context)
    {
        s_raised = true;
        AssemblyLoadContext.Default.Unloading -= OnDefaultUnloading;
    }
}
