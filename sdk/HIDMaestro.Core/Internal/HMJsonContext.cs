using System;
using System.Collections.Generic;
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
/// here, and there is no reflection resolver behind it. A type missing from
/// this list does not fail the build: the layout converter looks each
/// concrete layout up at run time, so the analyzers never see the
/// dependency, and both loaders refuse a profile that fails to parse. An enum
/// without a converter in the list below also builds, and reads and writes
/// as a number. json_parity_check (S76) fails when a type the profile model
/// can deserialize has no metadata here (check G) or an enum the contract
/// reaches has no converter (check F).
///
/// <para>The options match the reflection-based options used through
/// v1.12.0: camelCase property names read case-insensitively, comments and
/// trailing commas allowed, <see cref="HMAxis"/> by its own converter, every
/// other enum it reaches as a snake_case string (each is listed below), and
/// <see cref="HMLayout"/> by the <c>kind</c> converter.</para>
///
/// <para>v2.0.0 reads strictly: a key no type maps and a key written twice
/// in one object are refused, with the JSON path in the exception. Through
/// v2.0.0 the first was dropped and the second replaced the first, so a
/// misspelled key left a profile without what it meant to say.</para></summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false,
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
[JsonSerializable(typeof(XInputSpec))]
[JsonSerializable(typeof(List<FieldSpec>))]
[JsonSerializable(typeof(DeviceModelFile))]
[JsonSerializable(typeof(IdentityDefaultsFile))]
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
