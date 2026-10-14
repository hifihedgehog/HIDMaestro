using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HIDMaestro.Internal;

/// <summary>A <see cref="JsonStringEnumConverter{TEnum}"/> with the
/// snake_case names the profile catalog writes ("left_stick_x"). The source
/// generator needs a converter it can construct without arguments, and the
/// non-generic <see cref="JsonStringEnumConverter"/> builds its converters
/// with runtime code generation, which NativeAOT does not have.</summary>
internal sealed class SnakeCaseEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    public SnakeCaseEnumConverter() : base(JsonNamingPolicy.SnakeCaseLower) { }
}

/// <summary>Source-generated metadata for every type the SDK reads from or
/// writes to JSON: a profile, and each layout the <c>kind</c> converter can
/// produce. Every serializer call in the SDK passes a
/// <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/> from
/// here, and there is no reflection resolver behind it, so a type missing
/// from this list fails the build's AOT analysis instead of failing at run
/// time in a NativeAOT or trimmed app. An enum is the exception: one without
/// a converter in the list below still builds, and reads and writes as a
/// number. json_parity_check (S76, check F) fails when an enum the contract
/// reaches has none.
///
/// <para>The options match the reflection-based options used through
/// v1.12.0: camelCase property names read case-insensitively, comments and
/// trailing commas allowed, <see cref="HMAxis"/> by its own converter, every
/// other enum it reaches as a snake_case string (each is listed below), and
/// <see cref="HMLayout"/> by the <c>kind</c> converter.</para></summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    Converters = new[]
    {
        typeof(HMAxisJsonConverter),
        typeof(HMLayoutJsonConverter),
        typeof(SnakeCaseEnumConverter<HMLayoutKind>),
        typeof(SnakeCaseEnumConverter<HMAxisRole>),
        typeof(SnakeCaseEnumConverter<HMButtonRole>),
        typeof(SnakeCaseEnumConverter<HMPedalType>),
        typeof(SnakeCaseEnumConverter<HMRudderKind>),
        typeof(SnakeCaseEnumConverter<HMShifterKind>),
        typeof(SnakeCaseEnumConverter<HMShifterActuation>),
        typeof(SnakeCaseEnumConverter<HMHatLocation>),
        typeof(SnakeCaseEnumConverter<HMTriggerKind>),
        typeof(SnakeCaseEnumConverter<HMDpadEncoding>),
        typeof(SnakeCaseEnumConverter<HMStickSide>),
        typeof(SnakeCaseEnumConverter<HMTriggerSide>),
        typeof(SnakeCaseEnumConverter<HMRumbleKind>),
    })]
[JsonSerializable(typeof(ControllerProfile))]
[JsonSerializable(typeof(HMLayout))]
[JsonSerializable(typeof(HMUnspecifiedLayout))]
[JsonSerializable(typeof(HMGamepadLayout))]
[JsonSerializable(typeof(HMJoystickLayout))]
[JsonSerializable(typeof(HMFlightStickLayout))]
[JsonSerializable(typeof(HMHotasLayout))]
[JsonSerializable(typeof(HMWheelLayout))]
[JsonSerializable(typeof(HMPedalsLayout))]
[JsonSerializable(typeof(HMShifterLayout))]
[JsonSerializable(typeof(HMHandbrakeLayout))]
[JsonSerializable(typeof(HMSingleAxisAccessoryLayout))]
[JsonSerializable(typeof(HMArcadeStickLayout))]
[JsonSerializable(typeof(HMDancePadLayout))]
[JsonSerializable(typeof(HMGuitarLayout))]
[JsonSerializable(typeof(HMMotionWandLayout))]
[JsonSerializable(typeof(HMRemoteLayout))]
[JsonSerializable(typeof(HMControllerAdapterLayout))]
internal sealed partial class HMJsonContext : JsonSerializerContext
{
}
