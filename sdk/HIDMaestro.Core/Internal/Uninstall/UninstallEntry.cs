namespace HIDMaestro.Internal.Uninstall;

/// <summary>What a removal did, or would do, to one artifact.</summary>
internal enum UninstallOutcome
{
    /// <summary>A preview found it, and a removal would take it.</summary>
    WouldRemove,

    /// <summary>Removed.</summary>
    Removed,

    /// <summary>Taken out of use now and gone after the next restart.</summary>
    RemovedAtRestart,

    /// <summary>Left on purpose. The reason says why.</summary>
    Kept,

    /// <summary>The removal tried and failed. The reason says how.</summary>
    Failed,
}

/// <summary>One artifact a removal found: what kind of thing it is (a
/// devnode, a driver package, a registry key, a file, a certificate), where
/// it is, and what happened to it.</summary>
internal sealed record UninstallEntry(string Kind, string Location, UninstallOutcome Outcome, string? Reason = null);
