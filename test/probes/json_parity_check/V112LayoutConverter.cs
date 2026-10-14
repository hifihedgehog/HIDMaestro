// The HMLayout converter exactly as v1.12.0 shipped it (b4d29d9,
// sdk/HIDMaestro.Core/Internal/HMLayoutLoader.cs), so the parity check's
// baseline is what v1.12.0 actually did and not the converter under test.
// It reads each concrete layout with reflection-based System.Text.Json.
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using HIDMaestro;
using HIDMaestro.Internal;

internal sealed class V112LayoutConverter : JsonConverter<HMLayout>
{
    public override HMLayout? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("kind", out var kindElem))
            throw new JsonException("HMLayout: missing required 'kind' property");
        var kindStr = kindElem.GetString();
        if (string.IsNullOrEmpty(kindStr))
            throw new JsonException("HMLayout: 'kind' is empty");

        var kind = HMLayoutJsonConverter.SnakeCaseToEnum<HMLayoutKind>(kindStr)
                   ?? throw new JsonException($"HMLayout: unknown kind '{kindStr}'");

        var json = root.GetRawText();
        var sub = options;
        return kind switch
        {
            HMLayoutKind.Unspecified         => JsonSerializer.Deserialize<HMUnspecifiedLayout>(json, sub),
            HMLayoutKind.Gamepad             => JsonSerializer.Deserialize<HMGamepadLayout>(json, sub),
            HMLayoutKind.Joystick            => JsonSerializer.Deserialize<HMJoystickLayout>(json, sub),
            HMLayoutKind.FlightStick         => JsonSerializer.Deserialize<HMFlightStickLayout>(json, sub),
            HMLayoutKind.Hotas               => JsonSerializer.Deserialize<HMHotasLayout>(json, sub),
            HMLayoutKind.Wheel               => JsonSerializer.Deserialize<HMWheelLayout>(json, sub),
            HMLayoutKind.Pedals              => JsonSerializer.Deserialize<HMPedalsLayout>(json, sub),
            HMLayoutKind.Shifter             => JsonSerializer.Deserialize<HMShifterLayout>(json, sub),
            HMLayoutKind.Handbrake           => JsonSerializer.Deserialize<HMHandbrakeLayout>(json, sub),
            HMLayoutKind.SingleAxisAccessory => JsonSerializer.Deserialize<HMSingleAxisAccessoryLayout>(json, sub),
            HMLayoutKind.ArcadeStick         => JsonSerializer.Deserialize<HMArcadeStickLayout>(json, sub),
            HMLayoutKind.DancePad            => JsonSerializer.Deserialize<HMDancePadLayout>(json, sub),
            HMLayoutKind.Guitar              => JsonSerializer.Deserialize<HMGuitarLayout>(json, sub),
            HMLayoutKind.MotionWand          => JsonSerializer.Deserialize<HMMotionWandLayout>(json, sub),
            HMLayoutKind.Remote              => JsonSerializer.Deserialize<HMRemoteLayout>(json, sub),
            HMLayoutKind.ControllerAdapter   => JsonSerializer.Deserialize<HMControllerAdapterLayout>(json, sub),
            _                                => throw new JsonException($"HMLayout: unhandled kind {kind}")
        };
    }

    public override void Write(Utf8JsonWriter writer, HMLayout value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), options);
}
