// HMProfile's ButtonMap, AxisMap, ExtendedReport, ExtendedOutputReport and
// Layout hand out copies, as XInput does. Every controller of a profile
// encodes with the profile's own objects, so a program that writes into
// what one of these reads returned must change no later read, no report a
// controller of the profile builds and no program it compiles. Each check
// that writes takes a profile of a load of its own, so a write that reaches
// the profile spoils no other check. The Clone check fills a spec of its
// own with every key, so a key a copy drops or shares shows. The last
// writes nothing: it compares every catalog profile's copies with the
// profile's own objects.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Copies
{
    public static int Run()
    {
        var c = new Checks("copies");

        c.Run("ButtonMap: each read is an array of its own, and a write into one changes no later read and no report", () =>
        {
            var inner = Fresh("dualsense-bt");
            var p = new HMProfile(inner);
            byte[] before = Report(inner);
            int[] first = p.ButtonMap!, second = p.ButtonMap!;
            int[] was = (int[])second.Clone();
            first[0] = 13;
            first[1] = 13;
            byte[] after = Report(inner);
            bool own = !ReferenceEquals(first, second);
            bool reads = p.ButtonMap!.SequenceEqual(was) && inner.ButtonMap!.SequenceEqual(was);
            bool report = after.SequenceEqual(before);
            return (own && reads && report, $"own array {own}, later reads unchanged {reads}, report unchanged {report}");
        });

        c.Run("AxisMap: each read is a dictionary of its own with the profile's comparer, and a write into one changes no later read and no report", () =>
        {
            var inner = Fresh("dualsense-bt");
            var p = new HMProfile(inner);
            var control = Fresh("dualsense-bt");
            var first = p.AxisMap!;
            var second = p.AxisMap!;
            var was = second.ToDictionary(kv => kv.Key, kv => kv.Value);
            first.Clear();
            first["0x30"] = "leftTrigger";
            // The builder reads the map when it is first built, after the write.
            byte[] built = Report(inner), expected = Report(control);
            bool own = !ReferenceEquals(first, second) && Equals(second.Comparer, inner.AxisMap!.Comparer);
            bool reads = Same(p.AxisMap!, was) && Same(inner.AxisMap!, was);
            bool report = built.SequenceEqual(expected);
            return (own && reads && report, $"own dictionary {own}, later reads unchanged {reads}, report unchanged {report}");
        });

        foreach (bool output in new[] { false, true })
        {
            string name = output ? "ExtendedOutputReport" : "ExtendedReport";
            c.Run($"{name}: each read is a spec of its own, equal to the profile's as JSON, and a write into one changes no later read", () =>
            {
                var inner = Fresh("dualsense-bt");
                var p = new HMProfile(inner);
                ExtendedReportSpec Read() => (output ? p.ExtendedOutputReport : p.ExtendedReport)!;
                ExtendedReportSpec Own() => (output ? inner.ExtendedOutputReport : inner.ExtendedReport)!;
                string json = Json(Own());
                var first = Read();
                var second = Read();
                bool faithful = Json(second) == json;
                first.Size = 3;
                first.Fields.Clear();
                first.ArmOn?.Clear();
                first.IdleFrameIntervalMs = 77;
                bool own = !ReferenceEquals(first, second);
                bool reads = Json(Read()) == json && Json(Own()) == json && Json(second) == json;
                return (own && faithful && reads, $"own spec {own}, equal to the profile's {faithful}, later reads unchanged {reads}");
            });
        }

        c.Run("ExtendedReport: a write into a read changes no program a controller of the profile compiles", () =>
        {
            var inner = Fresh("steam-controller-2");
            var p = new HMProfile(inner);
            var spec = p.ExtendedReport!;
            spec.IdleFrameIntervalMs = 0;
            spec.Fields.Clear();
            var prog = VendorBlobProgram.Get(inner.ExtendedReport!);
            return (prog.NominalTicks != 0 && prog.Fields.Length > 0, $"NominalTicks {prog.NominalTicks}, {prog.Fields.Length} fields");
        });

        c.Run("Layout: each read is a layout of its own, equal to the profile's as JSON, and a write into one of its lists changes no later read", () =>
        {
            var inner = Fresh("dualsense-bt");
            var p = new HMProfile(inner);
            string json = Json(inner.Layout!);
            var first = (HMGamepadLayout)p.Layout!;
            var second = (HMGamepadLayout)p.Layout!;
            bool faithful = Json(second) == json;
            bool wrote = false;
            if (first.Sticks is IList<HMStick> sticks && !sticks.IsReadOnly && sticks.Count > 0)
            {
                sticks[0] = sticks[0] with { XAxis = HMAxis.Dial };
                wrote = true;
            }
            bool own = !ReferenceEquals(first, second) && !ReferenceEquals(first.Sticks, second.Sticks);
            bool reads = Json(p.Layout!) == json && Json(inner.Layout!) == json && Json(second) == json;
            return (own && faithful && reads, $"own layout {own}, equal to the profile's {faithful}, a list written {wrote}, later reads unchanged {reads}");
        });

        c.Run("ExtendedReportSpec.Clone: a spec with every key of every type under it filled copies each value and shares no list or object", () =>
        {
            int n = 0, keys = 0;
            var spec = (ExtendedReportSpec)Fill(typeof(ExtendedReportSpec), ref n, 0);
            var wrong = new List<string>();
            Walk(spec.Clone(), spec, "spec", wrong, ref keys);
            return (wrong.Count == 0 && keys > 100,
                $"{keys} values compared, {wrong.Count} wrong{(wrong.Count == 0 ? "" : ": " + string.Join(", ", wrong.Take(5)))}");
        });

        c.Run("every catalog profile: each copy's public members equal the profile's own, down to every value", () =>
        {
            int copies = 0;
            var diffs = new List<string>();
            foreach (var inner in ProfileDatabase.LoadEmbedded().All)
            {
                var p = new HMProfile(inner);
                void One(object? copy, object? own, string name)
                {
                    if (own == null) return;
                    copies++;
                    if (ReferenceEquals(copy, own)) diffs.Add($"{inner.Id} {name}: the profile's own object");
                    else Compare(copy, own, $"{inner.Id} {name}", diffs, 0);
                }
                One(p.ButtonMap, inner.ButtonMap, "buttonMap");
                One(p.AxisMap, inner.AxisMap, "axisMap");
                One(p.ExtendedReport, inner.ExtendedReport, "extendedReport");
                One(p.ExtendedOutputReport, inner.ExtendedOutputReport, "extendedOutputReport");
                One(p.Layout, inner.Layout, "layout");
            }
            return (diffs.Count == 0 && copies > 0,
                $"{copies} copies, {diffs.Count} differ{(diffs.Count == 0 ? "" : ": " + string.Join(", ", diffs.Take(5)))}");
        });

        return c.Failures;
    }

    /// <summary>The JSON keys of <paramref name="t"/>: its contract's
    /// properties, or, for a type its own converter reads (ScaleSpec), its
    /// public settable properties.</summary>
    static IEnumerable<(string Name, Type Type, Func<object, object?> Get, Action<object, object?> Set)> Keys(Type t)
    {
        var info = HMJsonContext.Default.GetTypeInfo(t);
        if (info?.Kind == System.Text.Json.Serialization.Metadata.JsonTypeInfoKind.Object)
            return info.Properties.Where(p => p.Get != null && p.Set != null).Select(p => (p.Name, p.PropertyType, p.Get!, p.Set!));
        return t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
            .Select(p => (p.Name, p.PropertyType, (Func<object, object?>)p.GetValue, (Action<object, object?>)p.SetValue));
    }

    /// <summary>An instance of <paramref name="t"/> with every key set to a
    /// value no other key holds and no default equals, each list holding one
    /// element filled the same way.</summary>
    static object Fill(Type t, ref int n, int depth)
    {
        n++;
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string)) return $"s{n}";
        if (t == typeof(int)) return n;
        if (t == typeof(byte)) return (byte)n;
        if (t == typeof(double)) return n + 0.5;
        if (t == typeof(bool)) return true;
        if (depth > 8) throw new InvalidOperationException($"{t.Name} nests deeper than 8");
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
        {
            var list = (System.Collections.IList)Activator.CreateInstance(t)!;
            list.Add(Fill(t.GetGenericArguments()[0], ref n, depth + 1));
            return list;
        }
        var o = Activator.CreateInstance(t)!;
        foreach (var (_, type, _, set) in Keys(t)) set(o, Fill(type, ref n, depth + 1));
        return o;
    }

    /// <summary>A copy beside its original, key by key: every value equal,
    /// and no list or object of the original reused.</summary>
    static void Walk(object? copy, object? own, string path, List<string> wrong, ref int keys)
    {
        keys++;
        if (copy == null || own == null)
        {
            if (copy != null || own != null) wrong.Add($"{path}: one side null");
            return;
        }
        var t = own.GetType();
        if (t.IsValueType || t == typeof(string))
        {
            if (!own.Equals(copy)) wrong.Add($"{path}: {copy} for {own}");
            return;
        }
        if (ReferenceEquals(copy, own)) { wrong.Add($"{path}: shared"); return; }
        if (own is System.Collections.IList ol)
        {
            var cl = (System.Collections.IList)copy;
            if (cl.Count != ol.Count) { wrong.Add($"{path}: {cl.Count} elements for {ol.Count}"); return; }
            for (int i = 0; i < ol.Count; i++) Walk(cl[i], ol[i], $"{path}[{i}]", wrong, ref keys);
            return;
        }
        foreach (var (name, _, get, _) in Keys(t)) Walk(get(copy), get(own), $"{path}.{name}", wrong, ref keys);
    }

    /// <summary>Two objects' public properties and fields, compared down to
    /// values: lists element by element, dictionaries key by key. Adds the
    /// path of each difference to <paramref name="diffs"/>.</summary>
    static void Compare(object? a, object? b, string path, List<string> diffs, int depth)
    {
        if (a == null || b == null)
        {
            if (a != null || b != null) diffs.Add(path);
            return;
        }
        var t = a.GetType();
        if (t != b.GetType()) { diffs.Add($"{path}: {t.Name} against {b.GetType().Name}"); return; }
        if (depth > 16) { diffs.Add($"{path}: deeper than 16"); return; }
        if (t.IsPrimitive || t.IsEnum || a is string or decimal)
        {
            if (!a.Equals(b)) diffs.Add(path);
            return;
        }
        if (a is System.Collections.IDictionary da)
        {
            var db = (System.Collections.IDictionary)b;
            if (da.Count != db.Count) { diffs.Add($"{path}.Count"); return; }
            foreach (System.Collections.DictionaryEntry e in da)
            {
                if (!db.Contains(e.Key)) diffs.Add($"{path}[{e.Key}]");
                else Compare(e.Value, db[e.Key], $"{path}[{e.Key}]", diffs, depth + 1);
            }
            return;
        }
        if (a is System.Collections.IEnumerable ea)
        {
            var la = ea.Cast<object?>().ToList();
            var lb = ((System.Collections.IEnumerable)b).Cast<object?>().ToList();
            if (la.Count != lb.Count) { diffs.Add($"{path}.Count"); return; }
            for (int i = 0; i < la.Count; i++) Compare(la[i], lb[i], $"{path}[{i}]", diffs, depth + 1);
            return;
        }
        const BindingFlags Public = BindingFlags.Public | BindingFlags.Instance;
        foreach (var prop in t.GetProperties(Public))
        {
            if (prop.GetIndexParameters().Length != 0) continue;
            object? va, vb;
            try { va = prop.GetValue(a); vb = prop.GetValue(b); }
            catch (TargetInvocationException e) { diffs.Add($"{path}.{prop.Name}: {e.InnerException?.GetType().Name}"); continue; }
            Compare(va, vb, $"{path}.{prop.Name}", diffs, depth + 1);
        }
        foreach (var field in t.GetFields(Public))
            Compare(field.GetValue(a), field.GetValue(b), $"{path}.{field.Name}", diffs, depth + 1);
    }

    /// <summary>A profile of a load of its own, under a user id, as Edits
    /// takes one.</summary>
    static ControllerProfile Fresh(string id)
    {
        var src = ProfileDatabase.LoadEmbedded().All.First(p => p.Id == id);
        var text = JsonSerializer.Serialize(src, HMJsonContext.Default.ControllerProfile);
        var db = ProfileDatabase.LoadTexts(new[] { ($"user-{id}.json", text.Replace($"\"id\":\"{id}\"", $"\"id\":\"user-{id}\"")) },
            ModelRegistry.Catalog);
        return db.All.Single();
    }

    /// <summary>The descriptor report of one state that presses A and B and
    /// moves the right stick and both triggers, as the profile's own report
    /// builder encodes it.</summary>
    static byte[] Report(ControllerProfile p)
    {
        var axes = new Dictionary<HMAxis, float>
        {
            [HMAxis.X] = 0.2f, [HMAxis.Y] = 0.3f, [HMAxis.Z] = 0.7f, [HMAxis.Rz] = 0.9f, [HMAxis.Rx] = 0.4f, [HMAxis.Ry] = 0.6f,
        };
        return p.GetOrBuildReportBuilder().BuildReport(axes, 0, (uint)(HMButton.A | HMButton.B));
    }

    static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    static string Json(ExtendedReportSpec s) => JsonSerializer.Serialize(s, HMJsonContext.Default.ExtendedReportSpec);

    static string Json(HMLayout l) => JsonSerializer.Serialize(l, HMJsonContext.Default.HMLayout);
}
