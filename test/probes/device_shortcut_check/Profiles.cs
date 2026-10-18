// A profile field one catalog profile's behavior reads: every property of
// the profile object model (ControllerProfile and the HIDMaestro types its
// properties reach, through lists, arrays and dictionaries) that the JSON
// reader fills, counted over the catalog. A profile counts for a property
// when some object of the property's type in its graph holds a value other
// than a new object of that type holds. A property exactly one profile sets
// is listed with that profile.
//
// Two finer counts sit beside each property, since one profile can reach
// code of its own through a key many profiles set:
//   Type.Prop=value  a value of an enumerated key that SDK code names, so
//                    it can pick a branch: an enum member the code writes
//                    as Enum.Member (HMAxisRole.Brake), or a string the code
//                    compares the property with as that literal (a switch
//                    arm, a case label, == or an is pattern).
//                    "FieldSpec.Type=stick12-pair". A value no code names is
//                    data the code reads as data.
//   Type.Prop@kind   a property of an object whose discriminator
//                    (Rules.Discriminators) reads kind, counted per kind.
//                    "FieldSpec.Center@int16-le" is a center on an int16-le
//                    field, which the codec handles apart from a center on
//                    uint8-axis.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using HIDMaestro.Internal;

internal sealed record PropertyUse(string Property, string JsonName, int Count, string Profile);

internal static class Profiles
{
    /// <summary>What decides the finer counts: whether code names a value
    /// of a key (Type.Prop, the value, its type), and the discriminators, as
    /// Type.Prop.</summary>
    internal sealed record Finer(Func<string, string, Type, bool> NamedInCode, IReadOnlyCollection<string> Discriminators);

    /// <summary>Every Name.Member pair C# code writes, enum members among
    /// them.</summary>
    public static HashSet<(string, string)> MemberReferences(IEnumerable<List<Token>> sources)
    {
        var set = new HashSet<(string, string)>();
        foreach (var t in sources)
            for (int i = 2; i < t.Count; i++)
                if (t[i].Kind == Tk.Ident && t[i - 1].Text == "." && t[i - 2].Kind == Tk.Ident)
                    set.Add((t[i - 2].Text, t[i].Text));
        return set;
    }

    public static List<PropertyUse> Census(IReadOnlyList<ControllerProfile> catalog, Finer finer)
    {
        var setBy = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var json = new Dictionary<string, string>(StringComparer.Ordinal);
        var defaults = new Dictionary<Type, object?>();
        foreach (var p in catalog)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            Walk(p, p.Id, setBy, json, defaults, seen, finer);
        }
        // A key on a kind only one profile uses says no more than the kind's
        // own value does ("FieldSpec.Byte@uint10-be" beside
        // "FieldSpec.Type=uint10-be"), so it is left to that value.
        foreach (string key in setBy.Keys.Where(k => k.Contains('@')).ToList())
        {
            string type = key[..key.IndexOf('.')], kind = key[(key.IndexOf('@') + 1)..];
            string? disc = finer.Discriminators.FirstOrDefault(d => d.StartsWith(type + ".", StringComparison.Ordinal));
            if (disc != null && setBy.TryGetValue(disc + "=" + kind, out var byKind) && byKind.Count == 1 && setBy[key].SetEquals(byKind))
                setBy.Remove(key);
        }
        // Properties no profile sets are not listed: the census is over
        // what the catalog uses.
        return setBy.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new PropertyUse(kv.Key, json[kv.Key], kv.Value.Count, kv.Value.Count == 1 ? kv.Value.Min! : ""))
            .ToList();
    }

    /// <summary>The strings C# code compares a property with as literals,
    /// by the property's name: the arms of a switch expression on it
    /// (f.Type switch { "a" =&gt; ... }), the case labels of a switch statement
    /// on it, an == or != with it on the other side, an is pattern on it,
    /// and its Equals call. A key's string value that is one of its
    /// property's literals picks a branch.</summary>
    public static Dictionary<string, HashSet<string>> ComparedLiterals(IEnumerable<List<Token>> sources)
    {
        var byName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Add(string? name, Token s)
        {
            if (name == null || s.Kind != Tk.String || s.Interp != 0) return;
            if (!byName.TryGetValue(name, out var set)) byName[name] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(s.Content);
        }
        foreach (var t in sources)
            for (int i = 0; i < t.Count; i++)
            {
                string x = t[i].Text;
                if (x == "switch" && i > 0 && t[i - 1].Kind == Tk.Ident && i + 1 < t.Count && t[i + 1].Text == "{")
                {
                    // A switch expression: the patterns before each arm's =>.
                    string name = t[i - 1].Text;
                    bool inPattern = true;
                    for (int j = i + 2, depth = 1; j < t.Count && depth > 0; j++)
                    {
                        string y = t[j].Text;
                        if (y is "{" or "(" or "[") depth++;
                        else if (y is "}" or ")" or "]") depth--;
                        else if (depth == 1 && y == "=>") inPattern = false;
                        else if (depth == 1 && y == ",") inPattern = true;
                        else if (depth == 1 && inPattern) Add(name, t[j]);
                    }
                }
                else if (x == "switch" && i + 1 < t.Count && t[i + 1].Text == "(")
                {
                    // A switch statement: its case labels, on the last name of
                    // the governing expression.
                    int j = i + 2, depth = 1;
                    string? name = null;
                    for (; j < t.Count && depth > 0; j++)
                    {
                        if (t[j].Text == "(") depth++;
                        else if (t[j].Text == ")") depth--;
                        else if (t[j].Kind == Tk.Ident) name = t[j].Text;
                    }
                    if (j >= t.Count || t[j].Text != "{") continue;
                    for (int k = j + 1, d = 1; k < t.Count && d > 0; k++)
                    {
                        if (t[k].Text == "{") d++;
                        else if (t[k].Text == "}") d--;
                        else if (d == 1 && k > 0 && t[k - 1].Text is "case" or "or" or "and" or "not") Add(name, t[k]);
                    }
                }
                else if (x is "==" or "!=" && i > 0 && i + 1 < t.Count)
                {
                    if (t[i + 1].Kind == Tk.String && t[i - 1].Kind == Tk.Ident) Add(t[i - 1].Text, t[i + 1]);
                    else if (t[i - 1].Kind == Tk.String) Add(LastName(t, i + 1), t[i - 1]);
                }
                else if (x == "is" && i > 0 && t[i - 1].Kind == Tk.Ident)
                {
                    for (int j = i + 1; j < t.Count && (t[j].Kind == Tk.String || t[j].Text is "or" or "and" or "not" or "(" or ")"); j++)
                        Add(t[i - 1].Text, t[j]);
                }
                else if (x == "Equals" && i > 1 && t[i - 1].Text == "." && t[i - 2].Kind == Tk.Ident && i + 2 < t.Count && t[i + 1].Text == "(")
                    Add(t[i - 2].Text, t[i + 2]);
            }
        return byName;
    }

    // The last name of a dotted chain that starts at i: f.Type gives Type.
    static string? LastName(List<Token> t, int i)
    {
        string? name = null;
        for (; i < t.Count && (t[i].Kind == Tk.Ident || t[i].Text is "." or "?."); i++)
            if (t[i].Kind == Tk.Ident) name = t[i].Text;
        return name;
    }

    static bool ModelType(Type t) => t.Namespace != null && t.Namespace.StartsWith("HIDMaestro", StringComparison.Ordinal)
                                     && t.IsClass && !t.IsAbstract;

    static IEnumerable<PropertyInfo> JsonProperties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
         .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod != null
                     && (p.GetCustomAttribute<JsonIgnoreAttribute>() == null || ReadBySharedParse(t, p))
                     && p.GetCustomAttribute<JsonExtensionDataAttribute>() == null
                     && (p.SetMethod?.IsPublic == true || p.GetCustomAttribute<JsonIncludeAttribute>() != null))
         .OrderBy(p => p.Name, StringComparer.Ordinal);

    /// <summary>v2.0.0: the model groups (protocol, stateBlock, inputReports,
    /// liveFeatureReports) are filled by the shared parse the build tool runs
    /// (ProfilePrograms), not by the profile's own JSON context, which
    /// ignores them. They are profile keys all the same. So is the state
    /// block's field list, which stateBlock holds as JSON and the parse
    /// reads into StateBlockFields in the codec's vocabulary, so a field
    /// there counts as one in an extended report does.</summary>
    static bool ReadBySharedParse(Type t, PropertyInfo p) =>
        t == typeof(ControllerProfile)
        && (p.Name == nameof(ControllerProfile.StateBlockFields)
            || p.GetCustomAttribute<JsonPropertyNameAttribute>() is { } name && Array.IndexOf(ProfilePrograms.GroupKeys, name.Name) >= 0);

    static object? DefaultOf(Type t, Dictionary<Type, object?> cache)
    {
        if (cache.TryGetValue(t, out var d)) return d;
        try { d = t.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(t) : null; }
        catch (TargetInvocationException) { d = null; }
        cache[t] = d;
        return d;
    }

    static void Walk(object obj, string profile, Dictionary<string, SortedSet<string>> setBy,
                     Dictionary<string, string> json, Dictionary<Type, object?> defaults, HashSet<object> seen, Finer finer)
    {
        if (!seen.Add(obj)) return;
        var type = obj.GetType();
        object? def = DefaultOf(type, defaults);
        var props = JsonProperties(type).ToList();
        // The kind this object's discriminator reads, if it has one.
        var discriminator = props.FirstOrDefault(p => finer.Discriminators.Contains(type.Name + "." + p.Name));
        string? kind = discriminator?.GetValue(obj)?.ToString() is { Length: > 0 } k ? k : null;
        foreach (var prop in props)
        {
            object? value = prop.GetValue(obj);
            object? dv = def == null ? null : prop.GetValue(def);
            string key = type.Name + "." + prop.Name;
            string jsonName = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? prop.Name;
            json[key] = jsonName;
            if (NonDefault(value, dv, prop.PropertyType))
            {
                Count(setBy, key, profile);
                Type vt = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                string? member = value is string s ? (finer.NamedInCode(key, s, vt) ? s : null)
                               : value != null && vt.IsEnum && value.ToString() is { } m && finer.NamedInCode(key, m, vt) ? m
                               : null;
                if (member != null)
                {
                    json[key + "=" + member] = $"{jsonName} \"{member}\"";
                    Count(setBy, key + "=" + member, profile);
                }
                if (kind != null && prop != discriminator)
                {
                    json[key + "@" + kind] = $"{jsonName} on {kind}";
                    Count(setBy, key + "@" + kind, profile);
                }
            }
            Descend(value, profile, setBy, json, defaults, seen, finer);
        }
    }

    static void Count(Dictionary<string, SortedSet<string>> setBy, string key, string profile)
    {
        if (!setBy.TryGetValue(key, out var set)) setBy[key] = set = new SortedSet<string>(StringComparer.Ordinal);
        set.Add(profile);
    }

    static void Descend(object? value, string profile, Dictionary<string, SortedSet<string>> setBy,
                        Dictionary<string, string> json, Dictionary<Type, object?> defaults, HashSet<object> seen, Finer finer)
    {
        if (value == null || value is string) return;
        if (ModelType(value.GetType()))
        {
            Walk(value, profile, setBy, json, defaults, seen, finer);
            return;
        }
        if (value is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict) Descend(e.Value, profile, setBy, json, defaults, seen, finer);
            return;
        }
        if (value is IEnumerable items)
            foreach (object? item in items) Descend(item, profile, setBy, json, defaults, seen, finer);
    }

    static bool NonDefault(object? value, object? def, Type declared)
    {
        if (value == null) return false;
        if (Nullable.GetUnderlyingType(declared) != null) return !value.Equals(def);
        if (value is string s) return def is string ds ? s != ds : s.Length > 0 || def == null;
        if (value is IEnumerable e && value is not string)
        {
            int n = e.Cast<object?>().Count();
            int dn = def is IEnumerable de ? de.Cast<object?>().Count() : 0;
            return n != dn;
        }
        if (value.GetType().IsValueType) return !value.Equals(def ?? Activator.CreateInstance(value.GetType()));
        return def == null;
    }
}
