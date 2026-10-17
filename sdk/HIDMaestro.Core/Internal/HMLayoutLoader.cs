using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace HIDMaestro.Internal;

/// <summary>Polymorphic JSON converter for <see cref="HMLayout"/>. Reads
/// the discriminator <c>kind</c> property and materializes the matching
/// concrete record. snake_case strings in JSON are converted to enum
/// values (matching <see cref="JsonNamingPolicy.SnakeCaseLower"/>).</summary>
internal sealed class HMLayoutJsonConverter : JsonConverter<HMLayout>
{
    public override HMLayout? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("kind", out var kindElem))
            throw new JsonException("HMLayout: missing required 'kind' property");
        var kindStr = kindElem.ValueKind == JsonValueKind.String ? kindElem.GetString() : null;
        if (string.IsNullOrEmpty(kindStr))
            throw Inside(".kind", new JsonException("HMLayout: 'kind' is empty or not a string"));

        var kind = SnakeCaseToEnum<HMLayoutKind>(kindStr)
                   ?? throw Inside(".kind", new JsonException($"HMLayout: unknown kind '{kindStr}'"));

        // Each concrete layout is read with the metadata the options carry:
        // the source-generated HMJsonContext for the SDK's own options, so
        // no reflection runs, and whatever resolver a caller's own options
        // carry otherwise. The kind is matched leniently above, as it has
        // been since layouts shipped, which is why this stays a converter
        // and not a [JsonPolymorphic] discriminator, which matches exactly.
        Type concrete = kind switch
        {
            HMLayoutKind.Unspecified         => typeof(HMUnspecifiedLayout),
            HMLayoutKind.Gamepad             => typeof(HMGamepadLayout),
            HMLayoutKind.Joystick            => typeof(HMJoystickLayout),
            HMLayoutKind.FlightStick         => typeof(HMFlightStickLayout),
            HMLayoutKind.Hotas               => typeof(HMHotasLayout),
            HMLayoutKind.Wheel               => typeof(HMWheelLayout),
            HMLayoutKind.Pedals              => typeof(HMPedalsLayout),
            HMLayoutKind.Shifter             => typeof(HMShifterLayout),
            HMLayoutKind.Handbrake           => typeof(HMHandbrakeLayout),
            HMLayoutKind.SingleAxisAccessory => typeof(HMSingleAxisAccessoryLayout),
            HMLayoutKind.ArcadeStick         => typeof(HMArcadeStickLayout),
            HMLayoutKind.DancePad            => typeof(HMDancePadLayout),
            HMLayoutKind.Guitar              => typeof(HMGuitarLayout),
            HMLayoutKind.MotionWand          => typeof(HMMotionWandLayout),
            HMLayoutKind.Remote              => typeof(HMRemoteLayout),
            HMLayoutKind.ControllerAdapter   => typeof(HMControllerAdapterLayout),
            _                                => throw new JsonException($"HMLayout: unhandled kind {kind}")
        };

        // The source generator builds a record whose properties are init-only
        // in one object initializer that assigns every one of them, and a key
        // the JSON leaves out arrives as default(T): null for the empty lists
        // and new() objects the records initialize, 0 for a trigger's Stages
        // and HMAxisRole.Unknown for a rotary encoder's Role. Reflection built
        // the record first and kept those initializers. The JSON is written
        // out again with each such missing key filled from a freshly
        // constructed record, which keeps them here too.
        var info = options.GetTypeInfo(concrete);
        var plan = PlanFor(concrete, options);
        try
        {
            if (plan == null) return (HMLayout?)root.Deserialize(info);
            var buffer = new System.Buffers.ArrayBufferWriter<byte>(4096);
            using (var w = new Utf8JsonWriter(buffer)) WriteFilled(w, root, plan);
            return (HMLayout?)JsonSerializer.Deserialize(buffer.WrittenSpan, info);
        }
        catch (JsonException e) when (e.Path is { Length: > 1 } inner && inner[0] == '$')
        {
            // The nested reader's path starts at the layout. The outer reader
            // sets the path of the layout itself on an exception that carries
            // none, and the path inside goes with it.
            throw Inside(inner[1..], new JsonException(StripPath(e.Message), e));
        }
    }

    /// <summary>An exception for a value inside the layout, at
    /// <paramref name="inner"/> below it (ProfileDefects.Locate joins the
    /// two).</summary>
    private static JsonException Inside(string inner, JsonException e)
    {
        e.Data[ProfileDefects.InnerPathKey] = inner;
        return e;
    }

    /// <summary>System.Text.Json's message without the path and position it
    /// appends, which name the nested reader's buffer.</summary>
    private static string StripPath(string message)
    {
        int at = message.IndexOf(" Path: ", StringComparison.Ordinal);
        return at > 0 ? message[..at] : message;
    }

    /// <summary>For one record type under one options instance: each
    /// settable property whose missing-key value differs from the record's
    /// own initializer, with the initializer's value as JSON, and the plans
    /// of the nested records that need the same. Built once.</summary>
    private sealed class FillPlan
    {
        public readonly List<(string Name, byte[] Json)> Defaults = new();
        public readonly Dictionary<string, (FillPlan Plan, JsonTypeInfoKind Container)> Nested = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsonSerializerOptions, System.Collections.Concurrent.ConcurrentDictionary<Type, FillPlan?>> s_plans = new();

    /// <summary>The fill plan of <paramref name="type"/>, or null when
    /// nothing in it or under it needs filling, which is every type when the
    /// options read with reflection: that path keeps initializers itself.</summary>
    private static FillPlan? PlanFor(Type type, JsonSerializerOptions options)
    {
        var cache = s_plans.GetValue(options, static _ => new());
        if (cache.TryGetValue(type, out var plan)) return plan;
        lock (cache)
        {
            if (cache.TryGetValue(type, out plan)) return plan;
            return Build(type, options, cache, new HashSet<Type>());
        }
    }

    private static FillPlan? Build(Type type, JsonSerializerOptions options,
        System.Collections.Concurrent.ConcurrentDictionary<Type, FillPlan?> cache, HashSet<Type> path)
    {
        if (cache.TryGetValue(type, out var done)) return done;
        var info = options.GetTypeInfo(type);
        if (info.Kind != JsonTypeInfoKind.Object || !path.Add(type)) return null;

        // What a missing key turns into is what the same metadata builds from
        // an empty object. Where that differs from a freshly constructed
        // record, the record's value has to be supplied.
        object? fresh = NewInstance(type), bare = null;
        if (fresh != null)
        {
            try { bare = JsonSerializer.Deserialize("{}"u8, info); }
            catch (JsonException) { }
            catch (NotSupportedException) { }
        }
        var plan = new FillPlan();
        foreach (var p in info.Properties)
        {
            if (p.Set == null && p.AssociatedParameter == null) continue; // read-only: never bound on read
            if (fresh != null && p.Get != null)
            {
                var valueInfo = options.GetTypeInfo(p.PropertyType);
                byte[] want = JsonSerializer.SerializeToUtf8Bytes(p.Get(fresh), valueInfo);
                if (bare == null || !want.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(p.Get(bare), valueInfo)))
                    plan.Defaults.Add((p.Name, want));
            }
            Type t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var ti = options.GetTypeInfo(t);
            Type? inner = ti.Kind switch
            {
                JsonTypeInfoKind.Object => t,
                JsonTypeInfoKind.Enumerable or JsonTypeInfoKind.Dictionary => ti.ElementType,
                _ => null,
            };
            if (inner != null && Build(Nullable.GetUnderlyingType(inner) ?? inner, options, cache, path) is { } child)
                plan.Nested[p.Name] = (child, ti.Kind);
        }
        path.Remove(type);
        var result = plan.Defaults.Count > 0 || plan.Nested.Count > 0 ? plan : null;
        cache[type] = result;
        return result;
    }

    /// <summary>Write <paramref name="obj"/> as it is, with each missing key
    /// <paramref name="plan"/> names added and the nested records it names
    /// written the same way.</summary>
    private static void WriteFilled(Utf8JsonWriter w, JsonElement obj, FillPlan plan)
    {
        w.WriteStartObject();
        Span<bool> present = stackalloc bool[plan.Defaults.Count];
        foreach (var prop in obj.EnumerateObject())
        {
            for (int i = 0; i < plan.Defaults.Count; i++)
                if (string.Equals(plan.Defaults[i].Name, prop.Name, StringComparison.OrdinalIgnoreCase)) present[i] = true;
            w.WritePropertyName(prop.Name);
            if (plan.Nested.TryGetValue(prop.Name, out var n)) WriteNested(w, prop.Value, n.Plan, n.Container);
            else prop.Value.WriteTo(w);
        }
        for (int i = 0; i < plan.Defaults.Count; i++)
        {
            if (present[i]) continue;
            w.WritePropertyName(plan.Defaults[i].Name);
            w.WriteRawValue(plan.Defaults[i].Json, skipInputValidation: true);
        }
        w.WriteEndObject();
    }

    private static void WriteNested(Utf8JsonWriter w, JsonElement value, FillPlan plan, JsonTypeInfoKind container)
    {
        switch (container)
        {
            case JsonTypeInfoKind.Object when value.ValueKind == JsonValueKind.Object:
                WriteFilled(w, value, plan);
                return;
            case JsonTypeInfoKind.Enumerable when value.ValueKind == JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var e in value.EnumerateArray())
                {
                    if (e.ValueKind == JsonValueKind.Object) WriteFilled(w, e, plan);
                    else e.WriteTo(w);
                }
                w.WriteEndArray();
                return;
            case JsonTypeInfoKind.Dictionary when value.ValueKind == JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var kv in value.EnumerateObject())
                {
                    w.WritePropertyName(kv.Name);
                    if (kv.Value.ValueKind == JsonValueKind.Object) WriteFilled(w, kv.Value, plan);
                    else kv.Value.WriteTo(w);
                }
                w.WriteEndObject();
                return;
            default:
                value.WriteTo(w);
                return;
        }
    }

    /// <summary>A newly constructed instance of each record in the layout
    /// graph, whose initializers are the values a missing key stands for.
    /// Every layout kind is here, and every nested record. A record added to
    /// the layout graph belongs here too: json_parity_check reads the whole
    /// catalog both ways and fails on any profile whose contents differ.</summary>
    private static object? NewInstance(Type t) =>
        t == typeof(HMUnspecifiedLayout) ? new HMUnspecifiedLayout() :
        t == typeof(HMGamepadLayout) ? new HMGamepadLayout() :
        t == typeof(HMJoystickLayout) ? new HMJoystickLayout() :
        t == typeof(HMFlightStickLayout) ? new HMFlightStickLayout() :
        t == typeof(HMHotasLayout) ? new HMHotasLayout() :
        t == typeof(HMWheelLayout) ? new HMWheelLayout() :
        t == typeof(HMPedalsLayout) ? new HMPedalsLayout() :
        t == typeof(HMShifterLayout) ? new HMShifterLayout() :
        t == typeof(HMHandbrakeLayout) ? new HMHandbrakeLayout() :
        t == typeof(HMSingleAxisAccessoryLayout) ? new HMSingleAxisAccessoryLayout() :
        t == typeof(HMArcadeStickLayout) ? new HMArcadeStickLayout() :
        t == typeof(HMDancePadLayout) ? new HMDancePadLayout() :
        t == typeof(HMGuitarLayout) ? new HMGuitarLayout() :
        t == typeof(HMMotionWandLayout) ? new HMMotionWandLayout() :
        t == typeof(HMRemoteLayout) ? new HMRemoteLayout() :
        t == typeof(HMControllerAdapterLayout) ? new HMControllerAdapterLayout() :
        t == typeof(HMStick) ? new HMStick() :
        t == typeof(HMTrigger) ? new HMTrigger() :
        t == typeof(HMDpad) ? new HMDpad() :
        t == typeof(HMButtonBinding) ? new HMButtonBinding() :
        t == typeof(HMHatBinding) ? new HMHatBinding() :
        t == typeof(HMHaptics) ? new HMHaptics() :
        t == typeof(HMImu) ? new HMImu() :
        t == typeof(HMRudder) ? new HMRudder() :
        t == typeof(HMWheelSpec) ? new HMWheelSpec() :
        t == typeof(HMPedal) ? new HMPedal() :
        t == typeof(HMShifter) ? new HMShifter() :
        t == typeof(HMRotaryEncoder) ? new HMRotaryEncoder() :
        t == typeof(HMRevIndicator) ? new HMRevIndicator() :
        t == typeof(HMTriggerButton) ? new HMTriggerButton() :
        null;

    public override void Write(Utf8JsonWriter writer, HMLayout value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options.GetTypeInfo(value.GetType()));

    /// <summary>Convert a snake_case JSON string ("left_stick_x") to its
    /// matching PascalCase enum value (HMAxisRole.LeftStickX). Returns null
    /// when no match. Used by the converter and by the per-enum string
    /// readers below.</summary>
    internal static T? SnakeCaseToEnum<T>(string s) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(s)) return null;
        var pascal = string.Concat(
            s.Split('_').Select(p =>
                p.Length == 0 ? "" : char.ToUpperInvariant(p[0]) + p.Substring(1).ToLowerInvariant()));
        return Enum.TryParse<T>(pascal, ignoreCase: true, out var v) ? v : null;
    }
}

/// <summary>JSON serializer options preconfigured for HMLayout
/// deserialization. Registered once per ControllerProfile load.
///
/// <para>Since v1.13.0 these are the options of the SDK's source-generated
/// context, with no reflection behind them, so they work in NativeAOT and
/// trimmed apps. They read and write profiles and every <see cref="HMLayout"/>
/// type. A type outside the SDK needs options of its own.</para></summary>
public static class HMLayoutJsonOptions
{
    public static readonly JsonSerializerOptions Default = HMJsonContext.Default.Options;
}

/// <summary>Reads <see cref="HMAxis"/> from JSON as the canonical
/// short string ("X", "Y", "Slider", "Throttle", etc. Matches the enum
/// PascalCase names). The HMAxis enum is page-and-usage encoded under the
/// hood, but in JSON we want human-readable names.</summary>
internal sealed class HMAxisJsonConverter : JsonConverter<HMAxis>
{
    public override HMAxis Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return HMAxis.None;
        var s = reader.GetString();
        if (string.IsNullOrEmpty(s)) return HMAxis.None;
        if (Enum.TryParse<HMAxis>(s, ignoreCase: true, out var v))
            return v;
        throw new JsonException($"HMAxis: unknown axis name '{s}'");
    }

    public override void Write(Utf8JsonWriter writer, HMAxis value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

/// <summary>Validates an authored <see cref="HMLayout"/> against the
/// profile's HID descriptor. Every <c>HMAxis</c> reference must resolve
/// to a real declared analog input field; every button index must be
/// within <c>[0, ButtonCount)</c>.
///
/// Throws <see cref="HMLayoutValidationException"/> with a structured path
/// into the layout for any mismatch.</summary>
public static class HMLayoutValidator
{
    public static void Validate(HMLayout layout, HidReportBuilder layoutDescriptor)
    {
        var declaredAxes = new HashSet<HMAxis>(layoutDescriptor.AxisFields.Keys);
        // Hat Switch is its own input shape (not in AxisFields by design)
        // but layouts reference it via HMAxis.Hat. Include it in the
        // validator's declared set when the descriptor declares a hat.
        if (layoutDescriptor.HatSwitch != null)
            declaredAxes.Add(HMAxis.Hat);
        int buttonCount = layoutDescriptor.Buttons.Count;
        var ctx = new ValidationContext(declaredAxes, buttonCount, layout.Kind);
        WalkLayout(layout, ctx);
    }

    private sealed record ValidationContext(HashSet<HMAxis> DeclaredAxes, int ButtonCount, HMLayoutKind Kind);

    private static void WalkLayout(HMLayout layout, ValidationContext ctx)
    {
        switch (layout)
        {
            case HMUnspecifiedLayout: return;

            case HMGamepadLayout g:
                Present(g.Sticks, "sticks"); Present(g.Triggers, "triggers");
                // Every stick's x, and the first stick's y, as through v1.13.0.
                for (int i = 0; i < g.Sticks.Count; i++) CheckAxis(ctx, g.Sticks[i].XAxis, $"sticks[{i}].xAxis");
                if (g.Sticks.Count > 0) CheckAxis(ctx, g.Sticks[0].YAxis, "sticks[0].yAxis");
                for (int i = 0; i < g.Triggers.Count; i++) CheckAxis(ctx, g.Triggers[i].Axis, $"triggers[{i}].axis");
                for (int i = 0; i < g.Sticks.Count; i++)
                    if (g.Sticks[i].ClickButton is int cb) CheckButton(ctx, cb, $"sticks[{i}].clickButton");
                CheckDpad(ctx, g.Dpad, "dpad");
                CheckBindings(ctx, g.FaceButtons,     "faceButtons");
                CheckBindings(ctx, g.ShoulderButtons, "shoulderButtons");
                CheckBindings(ctx, g.SystemButtons,   "systemButtons");
                CheckBindings(ctx, g.ExtraButtons,    "extraButtons");
                break;

            case HMJoystickLayout j:
                Present(j.Stick, "stick"); Present(j.Hats, "hats");
                CheckAxis(ctx, j.Stick.XAxis, "stick.xAxis");
                CheckAxis(ctx, j.Stick.YAxis, "stick.yAxis");
                if (j.Stick.ClickButton is int jcb) CheckButton(ctx, jcb, "stick.clickButton");
                if (j.Rudder is { } jr)   CheckAxis(ctx, jr.Axis, "rudder.axis");
                if (j.Throttle is { } jt) CheckAxis(ctx, jt.Axis, "throttle.axis");
                CheckHats(ctx, j.Hats, "hats");
                CheckTriggerButton(ctx, j.Trigger, "trigger");
                CheckBindings(ctx, j.StickButtons, "stickButtons");
                CheckBindings(ctx, j.BaseButtons,  "baseButtons");
                break;

            case HMFlightStickLayout f:
                Present(f.Stick, "stick"); Present(f.Hats, "hats");
                CheckAxis(ctx, f.Stick.XAxis, "stick.xAxis");
                CheckAxis(ctx, f.Stick.YAxis, "stick.yAxis");
                if (f.Rudder is { } fr)   CheckAxis(ctx, fr.Axis, "rudder.axis");
                if (f.Throttle is { } ft) CheckAxis(ctx, ft.Axis, "throttle.axis");
                CheckHats(ctx, f.Hats, "hats");
                CheckTriggerButton(ctx, f.Trigger, "trigger");
                CheckBindings(ctx, f.StickButtons, "stickButtons");
                CheckBindings(ctx, f.BaseButtons,  "baseButtons");
                break;

            case HMHotasLayout h:
                Present(h.Stick, "stick"); Present(h.StickHats, "stickHats");
                Present(h.ThrottleSecondary, "throttleSecondary"); Present(h.ThrottleHats, "throttleHats");
                CheckAxis(ctx, h.Stick.XAxis, "stick.xAxis");
                CheckAxis(ctx, h.Stick.YAxis, "stick.yAxis");
                if (h.StickRudder is { } sr) CheckAxis(ctx, sr.Axis, "stickRudder.axis");
                CheckHats(ctx, h.StickHats, "stickHats");
                CheckTriggerButton(ctx, h.StickTrigger, "stickTrigger");
                CheckBindings(ctx, h.StickButtons, "stickButtons");
                if (h.ThrottlePrimary is { } tp) CheckAxis(ctx, tp.Axis, "throttlePrimary.axis");
                for (int i = 0; i < h.ThrottleSecondary.Count; i++) CheckAxis(ctx, h.ThrottleSecondary[i].Axis, $"throttleSecondary[{i}].axis");
                CheckHats(ctx, h.ThrottleHats, "throttleHats");
                CheckBindings(ctx, h.ThrottleButtons, "throttleButtons");
                if (h.RudderModule is { } rm) CheckAxis(ctx, rm.Axis, "rudderModule.axis");
                break;

            case HMWheelLayout w:
                Present(w.Wheel, "wheel"); Present(w.Pedals, "pedals");
                Present(w.Shifters, "shifters"); Present(w.RotaryEncoders, "rotaryEncoders");
                CheckAxis(ctx, w.Wheel.Axis, "wheel.axis");
                for (int i = 0; i < w.Pedals.Count; i++) CheckAxis(ctx, w.Pedals[i].Axis, $"pedals[{i}].axis");
                for (int i = 0; i < w.Shifters.Count; i++)
                    if (w.Shifters[i].ButtonIndex is int sb) CheckButton(ctx, sb, $"shifters[{i}].buttonIndex");
                for (int i = 0; i < w.RotaryEncoders.Count; i++) CheckAxis(ctx, w.RotaryEncoders[i].Axis, $"rotaryEncoders[{i}].axis");
                CheckBindings(ctx, w.WheelButtons, "wheelButtons");
                CheckDpad(ctx, w.Dpad, "dpad");
                break;

            case HMPedalsLayout pl:
                Present(pl.Pedals, "pedals");
                for (int i = 0; i < pl.Pedals.Count; i++) CheckAxis(ctx, pl.Pedals[i].Axis, $"pedals[{i}].axis");
                break;

            case HMShifterLayout sh:
                if (sh.SequentialUpButton   is int sub) CheckButton(ctx, sub, "sequentialUpButton");
                if (sh.SequentialDownButton is int sdb) CheckButton(ctx, sdb, "sequentialDownButton");
                if (sh.HPatternGears != null)
                    foreach (var (key, b) in PresentValues(sh.HPatternGears, "hPatternGears"))
                        CheckButton(ctx, b.ButtonIndex, Member("hPatternGears", key) + ".buttonIndex");
                break;

            case HMHandbrakeLayout hb:
                CheckAxis(ctx, hb.Axis, "axis");
                break;

            case HMSingleAxisAccessoryLayout sa:
                CheckAxis(ctx, sa.Axis, "axis");
                break;

            case HMArcadeStickLayout a:
                CheckDpad(ctx, a.Joystick, "joystick");
                CheckBindings(ctx, a.FaceButtons,   "faceButtons");
                CheckBindings(ctx, a.SystemButtons, "systemButtons");
                break;

            case HMDancePadLayout d:
                if (d.Pads != null)
                    foreach (var (key, b) in PresentValues(d.Pads, "pads"))
                        CheckButton(ctx, b.ButtonIndex, Member("pads", key) + ".buttonIndex");
                CheckBindings(ctx, d.SystemButtons, "systemButtons");
                break;

            case HMGuitarLayout gt:
                CheckBindings(ctx, gt.Frets,         "frets");
                CheckBindings(ctx, gt.SoloFrets,     "soloFrets");
                CheckBindings(ctx, gt.SystemButtons, "systemButtons");
                CheckDpad(ctx, gt.Strum, "strum");
                if (gt.WhammyAxis is HMAxis whammy && whammy != HMAxis.None)
                    CheckAxis(ctx, whammy, "whammyAxis");
                if (gt.TiltButton is int tb) CheckButton(ctx, tb, "tiltButton");
                break;

            case HMMotionWandLayout mw:
                if (mw.TriggerAxis is HMAxis ta && ta != HMAxis.None)
                    CheckAxis(ctx, ta, "triggerAxis");
                CheckTriggerButton(ctx, mw.Trigger, "trigger");
                CheckBindings(ctx, mw.Buttons, "buttons");
                break;

            case HMRemoteLayout r:
                CheckBindings(ctx, r.Buttons, "buttons");
                break;

            case HMControllerAdapterLayout:
                // adapter ports may have varying per-port descriptors;
                // free-text per_port_layout is not validated here
                break;
        }
    }

    /// <summary>A list or object the layout reads, or the refusal at its
    /// key when the file wrote null for it or for one of its items.</summary>
    private static void Present<T>([NotNull] IReadOnlyList<T>? list, string path) where T : class
    {
        if (list == null)
            throw new HMLayoutValidationException($"layout/{path}: is null. Write [] for none, or leave the key out.");
        for (int i = 0; i < list.Count; i++)
            if (list[i] == null)
                throw new HMLayoutValidationException($"layout/{path}[{i}]: is null.");
    }

    private static void Present([NotNull] object? value, string path)
    {
        if (value == null)
            throw new HMLayoutValidationException($"layout/{path}: is null. Leave the key out for the default.");
    }

    private static IReadOnlyDictionary<string, HMButtonBinding> PresentValues(IReadOnlyDictionary<string, HMButtonBinding> map, string path)
    {
        foreach (var (key, b) in map)
            if (b == null)
                throw new HMLayoutValidationException($"layout/{Member(path, key)}: is null.");
        return map;
    }

    /// <summary>The path of a map's member as System.Text.Json writes it
    /// (ReadStack.AppendPropertyName): <c>pads.up-left</c>, or
    /// <c>pads['up left']</c> for a key holding one of the characters
    /// JsonReaderHelper.SpecialCharacters lists, so a dot or a space in a
    /// key is not read as the path's own punctuation.</summary>
    internal static string Member(string path, string key) =>
        key.AsSpan().IndexOfAny(PathSpecials) >= 0 ? $"{path}['{key}']" : $"{path}.{key}";

    /// <summary>JsonReaderHelper.SpecialCharacters, System.Text.Json 10.0.</summary>
    private const string PathSpecials = ". '/\"[]()\t\n\r\f\b\\\u0085\u2028\u2029";

    private static void CheckAxis(ValidationContext ctx, HMAxis axis, string path)
    {
        if (axis == HMAxis.None) return;
        if (!ctx.DeclaredAxes.Contains(axis))
            throw new HMLayoutValidationException(
                $"layout/{path}: axis '{axis}' is not declared in the descriptor's input fields. " +
                $"Declared axes: [{string.Join(", ", ctx.DeclaredAxes)}]");
    }

    private static void CheckButton(ValidationContext ctx, int index, string path)
    {
        if (index < 0 || index >= ctx.ButtonCount)
            throw new HMLayoutValidationException(
                $"layout/{path}: button index {index} is out of range [0, {ctx.ButtonCount}).");
    }

    private static void CheckBindings(ValidationContext ctx, IReadOnlyList<HMButtonBinding>? bindings, string path)
    {
        Present(bindings, path);
        int i = 0;
        foreach (var b in bindings)
        {
            CheckButton(ctx, b.ButtonIndex, $"{path}[{i++}].buttonIndex");
        }
    }

    private static void CheckHats(ValidationContext ctx, IReadOnlyList<HMHatBinding>? hats, string path)
    {
        Present(hats, path);
        int i = 0;
        foreach (var h in hats)
        {
            CheckAxis(ctx, h.Axis, $"{path}[{i++}].axis");
        }
    }

    private static void CheckTriggerButton(ValidationContext ctx, HMTriggerButton? tb, string path)
    {
        if (tb is null) return;
        CheckButton(ctx, tb.ButtonIndex, $"{path}.buttonIndex");
    }

    private static void CheckDpad(ValidationContext ctx, HMDpad? dpad, string path)
    {
        if (dpad is null) return;
        if (dpad.Encoding == HMDpadEncoding.Hat)
        {
            if (dpad.HatAxis is HMAxis ax && ax != HMAxis.None)
                CheckAxis(ctx, ax, $"{path}.hatAxis");
        }
        else
        {
            if (dpad.UpButton    is int u) CheckButton(ctx, u, $"{path}.upButton");
            if (dpad.DownButton  is int d) CheckButton(ctx, d, $"{path}.downButton");
            if (dpad.LeftButton  is int l) CheckButton(ctx, l, $"{path}.leftButton");
            if (dpad.RightButton is int rr) CheckButton(ctx, rr, $"{path}.rightButton");
        }
    }
}

/// <summary>Thrown when an authored <see cref="HMLayout"/> references
/// axes or buttons that don't exist in the profile's descriptor.</summary>
public sealed class HMLayoutValidationException : Exception
{
    public HMLayoutValidationException(string message) : base(message) { }
}
