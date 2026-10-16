// A profile field one catalog profile's behavior reads: every property of
// the profile object model (ControllerProfile and the HIDMaestro types its
// properties reach, through lists, arrays and dictionaries) that the JSON
// reader fills, counted over the catalog. A profile counts for a property
// when some object of the property's type in its graph holds a value other
// than a new object of that type holds. A property exactly one profile sets
// is listed with that profile.
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
    public static List<PropertyUse> Census(IReadOnlyList<ControllerProfile> catalog)
    {
        var setBy = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var json = new Dictionary<string, string>(StringComparer.Ordinal);
        var defaults = new Dictionary<Type, object?>();
        foreach (var p in catalog)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            Walk(p, p.Id, setBy, json, defaults, seen);
        }
        // Properties no profile sets are not listed: the census is over
        // what the catalog uses.
        return setBy.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new PropertyUse(kv.Key, json[kv.Key], kv.Value.Count, kv.Value.Count == 1 ? kv.Value.Min! : ""))
            .ToList();
    }

    static bool ModelType(Type t) => t.Namespace != null && t.Namespace.StartsWith("HIDMaestro", StringComparison.Ordinal)
                                     && t.IsClass && !t.IsAbstract;

    static IEnumerable<PropertyInfo> JsonProperties(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
         .Where(p => p.GetIndexParameters().Length == 0 && p.GetMethod != null
                     && p.GetCustomAttribute<JsonIgnoreAttribute>() == null
                     && p.GetCustomAttribute<JsonExtensionDataAttribute>() == null
                     && (p.SetMethod?.IsPublic == true || p.GetCustomAttribute<JsonIncludeAttribute>() != null))
         .OrderBy(p => p.Name, StringComparer.Ordinal);

    static object? DefaultOf(Type t, Dictionary<Type, object?> cache)
    {
        if (cache.TryGetValue(t, out var d)) return d;
        try { d = t.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(t) : null; }
        catch (TargetInvocationException) { d = null; }
        cache[t] = d;
        return d;
    }

    static void Walk(object obj, string profile, Dictionary<string, SortedSet<string>> setBy,
                     Dictionary<string, string> json, Dictionary<Type, object?> defaults, HashSet<object> seen)
    {
        if (!seen.Add(obj)) return;
        var type = obj.GetType();
        object? def = DefaultOf(type, defaults);
        foreach (var prop in JsonProperties(type))
        {
            object? value = prop.GetValue(obj);
            object? dv = def == null ? null : prop.GetValue(def);
            string key = type.Name + "." + prop.Name;
            json[key] = prop.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? prop.Name;
            if (NonDefault(value, dv, prop.PropertyType))
            {
                if (!setBy.TryGetValue(key, out var set)) setBy[key] = set = new SortedSet<string>(StringComparer.Ordinal);
                set.Add(profile);
            }
            Descend(value, profile, setBy, json, defaults, seen);
        }
    }

    static void Descend(object? value, string profile, Dictionary<string, SortedSet<string>> setBy,
                        Dictionary<string, string> json, Dictionary<Type, object?> defaults, HashSet<object> seen)
    {
        if (value == null || value is string) return;
        if (ModelType(value.GetType()))
        {
            Walk(value, profile, setBy, json, defaults, seen);
            return;
        }
        if (value is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict) Descend(e.Value, profile, setBy, json, defaults, seen);
            return;
        }
        if (value is IEnumerable items)
            foreach (object? item in items) Descend(item, profile, setBy, json, defaults, seen);
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
