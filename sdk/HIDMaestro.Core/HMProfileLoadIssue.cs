namespace HIDMaestro;

/// <summary>What a profile load found wrong with one file
/// (<see cref="HMContext.ProfileLoadIssues"/>).</summary>
public enum HMProfileLoadIssueKind
{
    /// <summary>The file was not loaded: it does not parse, carries a key
    /// the schema lacks or a key written twice, holds a value validation
    /// refuses, uses a removed spelling, or repeats an id already loaded.</summary>
    Refused = 0,

    /// <summary>The file has no <c>id</c>, so it is not a profile.</summary>
    NotAProfile = 1,

    /// <summary>The profile loaded, but a device model it names was not
    /// found or does not fit its descriptor. It runs with the model its
    /// identity gives it, and <see cref="HMProfile.DeviceModelProblem"/>
    /// says why.</summary>
    ModelProblem = 2,
}

/// <summary>One file a profile load refused or loaded with a model problem.
/// A program whose own loader swallows exceptions can show these to say why
/// a profile is missing or behaves as it does.</summary>
public sealed class HMProfileLoadIssue
{
    /// <summary>The file, as the load was given it. The embedded catalog's
    /// files read <c>catalog:&lt;path&gt;</c>.</summary>
    public string File { get; }

    /// <summary>The profile's id, or null when the file has none or does
    /// not parse.</summary>
    public string? ProfileId { get; }

    /// <summary>Where in the file the problem is, such as
    /// <c>$.protocol.size</c>, or <c>$</c> for the file as a whole.</summary>
    public string JsonPath { get; }

    /// <summary>What is wrong.</summary>
    public string Reason { get; }

    /// <summary>Whether the file was refused or loaded.</summary>
    public HMProfileLoadIssueKind Kind { get; }

    internal HMProfileLoadIssue(string file, string? profileId, string jsonPath, string reason, HMProfileLoadIssueKind kind)
    {
        File = file;
        ProfileId = profileId;
        JsonPath = jsonPath;
        Reason = reason;
        Kind = kind;
    }

    /// <summary>"file at path: reason".</summary>
    public override string ToString() => $"{File} at {JsonPath}: {Reason}";
}
