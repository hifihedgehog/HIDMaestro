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

    // "layout/sticks[1].x: ...", HMLayoutValidator's form. A map's key is
    // written as System.Text.Json writes it (HMLayoutValidator.Member): a
    // dash or a colon stays in a dotted segment, and a key with a dot or a
    // space is a bracketed one.
    [GeneratedRegex(@"^layout/(?<path>[^.\[\s']+(?:\.[^.\[\s']+|\[[0-9]+\]|\['.*?'\])*):(?=\s|$)")]
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
            {
                // A converter that reads a value through a nested reader, as
                // the layout's does, carries the path inside that value
                // apart: the outer reader sets Path to the value itself.
                string path = string.IsNullOrEmpty(j.Path) ? "$" : j.Path!;
                if (j.Data[InnerPathKey] is string inner) path += inner;
                return (path, TrimJsonMessage(j.Message));
            }
            case HMLayoutValidationException l:
            {
                var m = LayoutPrefixed().Match(l.Message);
                return m.Success
                    ? ("$.layout." + m.Groups["path"].Value, l.Message[m.Length..].TrimStart())
                    : ("$.layout", l.Message);
            }
        }
        var p = ProfilePrefixed().Match(e.Message);
        if (p.Success)
            return ("$." + p.Groups["path"].Value, ProfileHead().Replace(e.Message, ""));
        return ("$", ProfileHead().Replace(e.Message, ""));
    }

    /// <summary>The key of <see cref="Exception.Data"/> under which a
    /// converter that reads a value through a nested reader leaves the path
    /// inside that value, such as <c>.pedals[0].axs</c>.</summary>
    internal const string InnerPathKey = "HIDMaestro.InnerJsonPath";

    /// <summary>A path of the form <c>$.a.b[2].c</c> as the file spells it.
    /// The reader matches keys without regard to case, so a check's path,
    /// written with the schema's spelling, is respelled here from the file's
    /// own keys. A segment the file does not hold, and every one after it,
    /// keeps the check's spelling.</summary>
    public static string Spell(string text, string path)
    {
        if (!path.StartsWith('$') || path.Length == 1) return path;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException) { return path; }
        using (doc)
        {
            var sb = new System.Text.StringBuilder("$");
            JsonElement? at = doc.RootElement;
            // The file's spelling of a key of the object at hand, and the
            // value it holds, null when the file has no such key.
            string Step(string key)
            {
                string spelled = key;
                if (at is { ValueKind: JsonValueKind.Object } o)
                {
                    JsonElement? next = null;
                    foreach (var p in o.EnumerateObject())
                        if (p.Name == key) { spelled = p.Name; next = p.Value; break; }
                    if (next == null)
                        foreach (var p in o.EnumerateObject())
                            if (string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) { spelled = p.Name; next = p.Value; break; }
                    at = next;
                }
                else at = null;
                return spelled;
            }
            int i = 1;
            while (i < path.Length)
            {
                if (path[i] == '.')
                {
                    int j = i + 1;
                    while (j < path.Length && path[j] != '.' && path[j] != '[') j++;
                    sb.Append('.').Append(Step(path[(i + 1)..j]));
                    i = j;
                }
                else if (path.AsSpan(i).StartsWith("['"))
                {
                    // A key System.Text.Json brackets for its special
                    // characters (ReadStack.AppendPropertyName).
                    int j = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                    if (j < 0) { sb.Append(path[i..]); break; }
                    sb.Append("['").Append(Step(path[(i + 2)..j])).Append("']");
                    i = j + 2;
                }
                else if (path[i] == '[')
                {
                    int j = path.IndexOf(']', i);
                    if (j < 0) { sb.Append(path[i..]); break; }
                    string index = path[(i + 1)..j];
                    at = at is { ValueKind: JsonValueKind.Array } a && int.TryParse(index, out int n) && n >= 0 && n < a.GetArrayLength()
                        ? a[n] : null;
                    sb.Append('[').Append(index).Append(']');
                    i = j + 1;
                }
                else { sb.Append(path[i..]); break; }
            }
            return sb.ToString();
        }
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
