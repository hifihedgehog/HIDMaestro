using System;

namespace HIDMaestro;

/// <summary>
/// A profile cannot make a controller: its descriptor is missing, its layout
/// names an axis or button the descriptor lacks, or its device model does
/// not compile. Derived from <see cref="InvalidOperationException"/>, as the
/// SDK's other exceptions are. <see cref="HMContext.CreateController(HMProfile)"/>
/// throws it before the create takes anything machine-wide, and
/// <see cref="HMProfileBuilder.Build"/> throws it for a profile it cannot
/// build. A profile file that fails the same checks when it loads is not
/// loaded and is listed in <see cref="HMContext.ProfileLoadIssues"/>.
/// </summary>
public sealed class HMProfileException : InvalidOperationException
{
    /// <summary>The profile's id, empty when it has none.</summary>
    public string ProfileId { get; }

    /// <summary>The file the profile was read from, or null for a profile
    /// built in code. The embedded catalog's files read
    /// <c>catalog:&lt;path&gt;</c>.</summary>
    public string? File { get; }

    /// <summary>Where in the profile JSON the defect is, such as
    /// <c>$.extendedReport.fields[2]</c>, or <c>$</c> for the profile as a
    /// whole.</summary>
    public string JsonPath { get; }

    /// <summary>What is wrong, without the profile id and path.</summary>
    public string Reason { get; }

    internal HMProfileException(string profileId, string? file, string jsonPath, string reason, Exception? inner = null)
        : base($"Profile '{profileId}'{(file != null ? $" ({file})" : "")} at {jsonPath}: {reason}", inner)
    {
        ProfileId = profileId;
        File = file;
        JsonPath = jsonPath;
        Reason = reason;
    }
}
