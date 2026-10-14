namespace HIDMaestro;

/// <summary>Which virtual controllers
/// <see cref="HMContext.RemoveAllVirtualControllers(HMCleanupScope, bool)"/>
/// removes.</summary>
public enum HMCleanupScope
{
    /// <summary>The controllers of programs that have exited without
    /// removing them. A running program's controllers, the caller's own
    /// included, are never touched. This is what the overloads without a
    /// scope do.</summary>
    Orphaned = 0,

    /// <summary>Every HIDMaestro controller on the machine, whoever
    /// created it and whether or not that program still runs. For an
    /// uninstall, a test harness or a support tool. Running programs lose
    /// their controllers.</summary>
    Everything = 1,
}
