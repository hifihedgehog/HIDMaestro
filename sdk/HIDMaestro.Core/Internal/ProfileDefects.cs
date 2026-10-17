using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HIDMaestro.Internal;

/// <summary>A defect of a profile that a check found at a known JSON
/// path. The checks written for the device model throw it. Older checks
/// throw <see cref="InvalidOperationException"/> or
/// <see cref="FormatException"/> with the path at the head of their
/// message, which <see cref="ProfileDefects.Locate"/> reads.</summary>
internal sealed class ProfileDefectException : InvalidOperationException
{
    public string JsonPath { get; }
    public string Reason { get; }

    public ProfileDefectException(string jsonPath, string reason)
        : base($"{jsonPath}: {reason}")
    {
        JsonPath = jsonPath;
        Reason = reason;
    }
}

/// <summary>Turns what a profile check threw into a JSON path and a reason,
/// for <see cref="HMProfileException"/> and the load issues.</summary>
internal static partial class ProfileDefects
{
    // "Profile 'id': extendedReport.fields[2] ('uint8').bytes ...", the form
    // every check before v2.0.0 writes.
    [GeneratedRegex(@"^Profile '[^']*':\s+(?<path>[A-Za-z][A-Za-z0-9_]*(?:(?:\.[A-Za-z][A-Za-z0-9_]*)|(?:\[[0-9]*\]))*)")]
    private static partial Regex ProfilePrefixed();

    // "layout/sticks[].x: ...", HMLayoutValidator's form.
    [GeneratedRegex(@"^layout/(?<path>[A-Za-z0-9_.\[\]]+):")]
    private static partial Regex LayoutPrefixed();

    [GeneratedRegex(@"^Profile '[^']*':\s+")]
    private static partial Regex ProfileHead();

    /// <summary>The JSON path and the reason an exception from a profile
    /// check names. <c>$</c> when it names no path.</summary>
    public static (string Path, string Reason) Locate(Exception e)
    {
        switch (e)
        {
            case ProfileDefectException d:
                return (d.JsonPath, d.Reason);
            case JsonException j:
                return (string.IsNullOrEmpty(j.Path) ? "$" : j.Path!, TrimJsonMessage(j.Message));
            case HMLayoutValidationException l:
            {
                var m = LayoutPrefixed().Match(l.Message);
                return (m.Success ? "$.layout." + m.Groups["path"].Value : "$.layout", l.Message);
            }
        }
        var p = ProfilePrefixed().Match(e.Message);
        if (p.Success)
            return ("$." + p.Groups["path"].Value, ProfileHead().Replace(e.Message, ""));
        return ("$", ProfileHead().Replace(e.Message, ""));
    }

    /// <summary>The JSON path and reason of anything a load check threw.
    /// An exception that is no defect type still refuses only its file, at
    /// <c>$</c>, with its type named, since no check placed it.</summary>
    public static (string Path, string Reason) LocateAny(Exception e) =>
        IsDefect(e) ? Locate(e) : ("$", $"a load check failed with {e.GetType().Name}: {e.Message}");

    /// <summary>System.Text.Json ends its messages with the path and
    /// position the exception carries apart.</summary>
    private static string TrimJsonMessage(string message)
    {
        int at = message.IndexOf(" Path: ", StringComparison.Ordinal);
        return at > 0 ? message[..at] : message;
    }

    /// <summary>The exception a create or a build throws for a profile
    /// defect.</summary>
    public static HMProfileException ToProfileException(ControllerProfile p, Exception e)
    {
        if (e is HMProfileException already) return already;
        var (path, reason) = Locate(e);
        return new HMProfileException(p.Id, p.SourceFile, path, reason, e);
    }

    /// <summary>True for what a profile check throws for a defect of the
    /// profile, as opposed to a failure of the machine.</summary>
    public static bool IsDefect(Exception e) =>
        e is InvalidOperationException or FormatException or OverflowException
          or HMLayoutValidationException or JsonException or ArgumentException;
}
