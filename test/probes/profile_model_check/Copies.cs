// HMProfile's ButtonMap, AxisMap, ExtendedReport, ExtendedOutputReport and
// Layout hand out a snapshot, built on the first read and shared by every
// later read of that profile instance. Every controller of a profile
// encodes with the profile's own objects, so a program that writes into
// the snapshot must change no report a controller of the profile builds,
// no program it compiles and no object the SDK holds, and a second read
// allocates nothing. Each check
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

        c.Run("ButtonMap: reads share one detached snapshot, and a write into it changes no encoding and allocates nothing on a second read", () =>
        {
            var inner = Fresh("dualsense-bt");
            var p = new HMProfile(inner);
            byte[] before = Report(inner);
            int[] first = p.ButtonMap!, second = p.ButtonMap!;
            int[] was = (int[])inner.ButtonMap!.Clone();
            bool shared = ReferenceEquals(first, second), detached = !ReferenceEquals(first, inner.ButtonMap);
            first[0] = 13;
            first[1] = 13;
            byte[] after = Report(inner);
            bool reads = inner.ButtonMap!.SequenceEqual(was);
            bool report = after.SequenceEqual(before);
            bool alloc = AllocatesNothing(() => p.ButtonMap);
            return (shared && detached && reads && report && alloc, $"one shared snapshot {shared}, detached {detached}, SDK's map unchanged {reads}, report unchanged {report}, second read allocates nothing {alloc}");
        });

        c.Run("AxisMap: reads share one detached dictionary with the profile's comparer, and a write into it changes no encoding and allocates nothing on a second read", () =>
        {
            var inner = Fresh("dualsense-bt");
            var p = new HMProfile(inner);
            var control = Fresh("dualsense-bt");
            var first = p.AxisMap!;
            var second = p.AxisMap!;
            var was = inner.AxisMap!.ToDictionary(kv => kv.Key, kv => kv.Value);
            first.Clear();
            first["0x30"] = "leftTrigger";
            // The builder reads the map when it is first built, after the write.
            byte[] built = Report(inner), expected = Report(control);
            bool own = ReferenceEquals(first, second) && !ReferenceEquals(first, inner.AxisMap) && Equals(second.Comparer, inner.AxisMap!.Comparer);
            bool reads = Same(inner.AxisMap!, was);
            bool report = built.SequenceEqual(expected);
            bool alloc = AllocatesNothing(() => p.AxisMap);
            return (own && reads && report && alloc, $"shared detached dictionary {own}, SDK's map unchanged {reads}, report unchanged {report}, second read allocates nothing {alloc}");
        });

        foreach (bool output in new[] { false, true })
        {
            string name = output ? "ExtendedOutputReport" : "ExtendedReport";
            c.Run($"{name}: reads share one detached spec, equal to the profile's as JSON, and a write into it leaves the SDK's spec unchanged", () =>
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
                bool own = ReferenceEquals(first, second) && !ReferenceEquals(first, Own());
                bool reads = Json(Own()) == json;
                bool alloc = AllocatesNothing(() => Read());
                return (own && faithful && reads && alloc, $"shared detached spec {own}, equal to the profile's {faithful}, SDK's spec unchanged {reads}, second read allocates nothing {alloc}");
            });
        }

        c.Run("ExtendedReport: a write into the snapshot changes no program a controller of the profile compiles", () =>
        {
            var inner = Fresh("steam-controller-2");
            var p = new HMProfile(inner);
            var spec = p.ExtendedReport!;
            spec.IdleFrameIntervalMs = 0;
            spec.Fields.Clear();
            var prog = VendorBlobProgram.Get(inner.ExtendedReport!);
            return (prog.NominalTicks != 0 && prog.Fields.Length > 0, $"NominalTicks {prog.NominalTicks}, {prog.Fields.Length} fields");
        });

        c.Run("Layout: reads share one detached layout, equal to the profile's as JSON, and a write into one of its lists leaves the SDK's layout unchanged", () =>
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
            bool own = ReferenceEquals(first, second) && !ReferenceEquals(first, inner.Layout) && !ReferenceEquals(first.Sticks, ((HMGamepadLayout)inner.Layout!).Sticks);
            bool reads = Json(inner.Layout!) == json;
            bool alloc = AllocatesNothing(() => p.Layout);
            return (own && faithful && reads && alloc, $"shared detached layout {own}, equal to the profile's {faithful}, a list written {wrote}, SDK's layout unchanged {reads}, second read allocates nothing {alloc}");
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

    /// <summary>True when 1000 calls of <paramref name="read"/>, after one
    /// warm-up call, allocate no byte on this thread.</summary>
    static bool AllocatesNothing(Func<object?> read)
    {
        read();
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) read();
        return GC.GetAllocatedBytesForCurrentThread() == b0;
    }

    static bool Same(Dictionary<string, string> a, Dictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    static string Json(ExtendedReportSpec s) => JsonSerializer.Serialize(s, HMJsonContext.Default.ExtendedReportSpec);

    static string Json(HMLayout l) => JsonSerializer.Serialize(l, HMJsonContext.Default.HMLayout);
}
