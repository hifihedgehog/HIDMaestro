using System;
using System.Collections.Generic;
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
    // every check before v2.0.0 writes. A map's key is a step as JSONPath
    // writes a name (FormException.Member): a dotted step for a name the
    // shorthand takes, and a bracketed, quoted one with its escapes for any
    // other.
    [GeneratedRegex(@"^Profile '[^']*':\s+(?<path>[A-Za-z][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*|\[[0-9]*\]|\['(?:[^'\\]|\\.)*'\])*)")]
    private static partial Regex ProfilePrefixed();

    // "layout/sticks[1].x: ...", HMLayoutValidator's form, its map keys
    // written as FormException.Member writes them.
    [GeneratedRegex(@"^layout/(?<path>[^.\[\s']+(?:\.[A-Za-z_][A-Za-z0-9_]*|\[[0-9]+\]|\['(?:[^'\\]|\\.)*'\])*):(?=\s|$)")]
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
                return (FromJsonPath(path), TrimJsonMessage(j.Message));
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
            var steps = Steps(path, stj: false);
            if (steps == null) return path;
            for (int k = 0; k < steps.Count; k++)
            {
                var (name, index) = steps[k];
                if (index < 0) steps[k] = (Step(name), -1);
                else at = at is { ValueKind: JsonValueKind.Array } a && index < a.GetArrayLength() ? a[index] : null;
            }
            return Write(steps);
        }
    }

    /// <summary>The steps of a path: a name, with <c>Index</c> -1, or an
    /// array index. <paramref name="stj"/> reads System.Text.Json's form,
    /// whose brackets end at the first quote and bracket and carry no
    /// escapes (ReadStack.AppendPropertyName), and otherwise the form
    /// <see cref="Programs.FormException.Member"/> writes. Null when the path
    /// does not start at the root or a step does not read.</summary>
    internal static List<(string Name, int Index)>? Steps(string path, bool stj)
    {
        if (!path.StartsWith('$')) return null;
        var steps = new List<(string, int)>();
        int i = 1;
        while (i < path.Length)
        {
            if (path[i] == '.')
            {
                int j = i + 1;
                while (j < path.Length && path[j] != '.' && path[j] != '[') j++;
                steps.Add((path[(i + 1)..j], -1));
                i = j;
            }
            else if (path.AsSpan(i).StartsWith("['"))
            {
                int j = i + 2;
                if (stj)
                {
                    j = path.IndexOf("']", j, StringComparison.Ordinal);
                    if (j < 0) return null;
                    steps.Add((path[(i + 2)..j], -1));
                }
                else
                {
                    while (j < path.Length && path[j] != '\'') j += path[j] == '\\' ? 2 : 1;
                    if (j + 1 >= path.Length || path[j + 1] != ']') return null;
                    steps.Add((Programs.FormException.Unescape(path[(i + 2)..j]), -1));
                }
                i = j + 2;
            }
            else if (path[i] == '[')
            {
                int j = path.IndexOf(']', i);
                if (j < 0 || !int.TryParse(path.AsSpan(i + 1, j - i - 1), out int n)) return null;
                steps.Add((string.Empty, n));
                i = j + 1;
            }
            else return null;
        }
        return steps;
    }

    /// <summary>A path written from its steps, each name as
    /// <see cref="Programs.FormException.Member"/> writes it.</summary>
    internal static string Write(List<(string Name, int Index)> steps)
    {
        string path = "$";
        foreach (var (name, index) in steps)
            path = index >= 0 ? $"{path}[{index}]" : Programs.FormException.Member(path, name);
        return path;
    }

    /// <summary>A System.Text.Json path in the form the SDK's own refusals
    /// take, so every load issue names a key one way. A path that does not
    /// read stays as it is.</summary>
    internal static string FromJsonPath(string path) =>
        Steps(path, stj: true) is { } steps ? Write(steps) : path;

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
