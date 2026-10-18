using System;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>A refusal as the load issues read it: the regular expressions
/// and the exception list of ProfileDefects.Locate and IsDefect
/// (sdk\HIDMaestro.Core\Internal\ProfileDefects.cs), copied here so the
/// program refusals can be held to them. Locate takes the path from a
/// message "Profile 'id': path ..." whose path starts with a letter, its
/// steps names as System.Text.Json writes them, [n] indexes, ['key'] and
/// a const's ["n"], and reads any other message as naming the root.</summary>
internal static partial class Dd2View
{
    [GeneratedRegex(@"^Profile '[^']*':\s+(?<path>[A-Za-z][A-Za-z0-9_]*(?:\.(?:[^\s.\[\]'""():,]|:(?!\s|$))+|\[[0-9]*\]|\['[^']*'\]|\[""[^""]*""\])*)")]
    private static partial Regex ProfilePrefixed();

    public static string Locate(Exception e)
    {
        if (e is JsonException j) return string.IsNullOrEmpty(j.Path) ? "$" : j.Path!;
        var p = ProfilePrefixed().Match(e.Message);
        return p.Success ? "$." + p.Groups["path"].Value : "$";
    }

    public static bool IsDefect(Exception e) =>
        e is InvalidOperationException or FormatException or OverflowException or JsonException or ArgumentException;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*(?:\.(?:[^\s.\[\]'""():,]|:(?!\s|$))+|\[[0-9]*\]|\['[^']*'\]|\[""[^""]*""\])*")]
    private static partial Regex Steps();

    /// <summary>The path Locate can give for <paramref name="path"/>: the
    /// steps its expression reads, up to the first it cannot.</summary>
    public static string Reachable(string path)
    {
        if (!path.StartsWith("$.", StringComparison.Ordinal)) return "$";
        var m = Steps().Match(path[2..]);
        return m.Success ? "$." + m.Value : "$";
    }
}
