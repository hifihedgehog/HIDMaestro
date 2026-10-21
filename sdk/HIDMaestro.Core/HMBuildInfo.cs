using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

namespace HIDMaestro;

/// <summary>A payload a build of HIDMaestro.Core carries or lacks. Official
/// releases carry all three, the first two for x64 and ARM64. See
/// <see cref="HMBuildInfo"/>.</summary>
public enum HMPayload
{
    /// <summary>The UMDF2 driver, its XUSB companion, the SWD helper, both
    /// INFs and the signing and catalog tools that install them, for one
    /// architecture. Every controller except a composite persona runs on
    /// it. A build made with <c>HMSkipArm64</c> carries it for x64
    /// only.</summary>
    Driver = 0,

    /// <summary>usbip-win2's host controller files for one
    /// architecture, the transport composite personas ride
    /// (<see cref="HMProfile.RequiresUsbipBackend"/>). A build made with
    /// <c>HMSkipUsbip</c> carries none, and one made with
    /// <c>HMSkipArm64</c> none for ARM64.</summary>
    UsbipTransport = 1,

    /// <summary>The OpenVR driver package that
    /// <see cref="HMVR.EnsureDriverRegistered"/> registers with SteamVR.
    /// <c>scripts\build_openvr.cmd</c> builds it, once, for x64, and a build
    /// that skipped that script lacks it.</summary>
    OpenVrDriver = 2,
}

/// <summary>
/// What this build of HIDMaestro.Core carries. Each answer is read once
/// from the assembly's own embedded resources, so it describes the bytes
/// this build holds, whatever settings built it. Official releases carry
/// everything (<see cref="IsComplete"/>). A build from source can leave
/// payloads out, and then refuses whatever needs them with
/// <see cref="HMPayloadMissingException"/> before any machine-wide step.
/// </summary>
public static class HMBuildInfo
{
    private sealed record State(
        IReadOnlyList<Architecture> Driver,
        IReadOnlyList<Architecture> Usbip,
        bool OpenVr);

    private static readonly Lazy<State> s_state = new(Read);

    // The architectures HIDMaestro builds for. NativePrefixFor maps every
    // other architecture to the x64 payload, so the check is limited to these.
    private static readonly Architecture[] s_built = { Architecture.X64, Architecture.Arm64 };

    /// <summary>The architectures this build carries the UMDF2 driver for.
    /// Every controller except a composite persona needs the one matching
    /// the machine.</summary>
    public static IReadOnlyList<Architecture> DriverArchitectures => s_state.Value.Driver;

    /// <summary>The architectures this build carries the USB/IP transport
    /// for, which composite personas ride. Empty in a build made with
    /// <c>HMSkipUsbip</c>.</summary>
    public static IReadOnlyList<Architecture> UsbipArchitectures => s_state.Value.Usbip;

    /// <summary>True when this build carries the OpenVR driver.</summary>
    public static bool HasOpenVrDriver => s_state.Value.OpenVr;

    /// <summary>True when this build carries everything an official release
    /// carries: the UMDF2 driver and the USB/IP transport for x64 and ARM64,
    /// and the OpenVR driver.</summary>
    public static bool IsComplete =>
        s_built.All(a => DriverArchitectures.Contains(a)) &&
        s_built.All(a => UsbipArchitectures.Contains(a)) &&
        HasOpenVrDriver;

    /// <summary>Whether this build carries <paramref name="payload"/> for
    /// <paramref name="architecture"/>. The OpenVR driver has one package,
    /// so its answer ignores the architecture.</summary>
    internal static bool Carries(HMPayload payload, Architecture architecture) => payload switch
    {
        HMPayload.Driver => DriverArchitectures.Contains(architecture),
        HMPayload.UsbipTransport => UsbipArchitectures.Contains(architecture),
        HMPayload.OpenVrDriver => HasOpenVrDriver,
        _ => throw new ArgumentOutOfRangeException(nameof(payload)),
    };

    /// <summary>Throw <see cref="HMPayloadMissingException"/> when this build
    /// lacks <paramref name="payload"/> for <paramref name="architecture"/>.
    /// <paramref name="need"/>, when given, opens the message with what
    /// asked for it.</summary>
    internal static void ThrowIfMissing(HMPayload payload, Architecture architecture, string? need = null)
    {
        if (Carries(payload, architecture)) return;
        string lead = need != null ? need + " " : "";
        string message = payload switch
        {
            HMPayload.Driver =>
                $"{lead}This build of HIDMaestro.Core carries no UMDF2 driver for {architecture}{OnlyFor(DriverArchitectures)}. " +
                "Official releases carry it for X64 and Arm64, and a build made with HMSkipArm64 for X64 only.",
            HMPayload.UsbipTransport =>
                $"{lead}This build of HIDMaestro.Core carries no USB/IP transport for {architecture}{OnlyFor(UsbipArchitectures)}. " +
                "Official releases carry it for X64 and Arm64. A build made with HMSkipUsbip carries none, " +
                "and one made with HMSkipArm64 none for Arm64.",
            _ =>
                $"{lead}This build of HIDMaestro.Core carries no OpenVR driver. scripts\\build_all.cmd builds it " +
                "with scripts\\build_openvr.cmd before the SDK, and official releases carry it.",
        };
        throw new HMPayloadMissingException(message, payload,
            payload == HMPayload.OpenVrDriver ? null : architecture);
    }

    /// <summary><see cref="ThrowIfMissing(HMPayload, Architecture, string?)"/>
    /// for this machine's architecture.</summary>
    internal static void ThrowIfMissing(HMPayload payload, string? need = null)
        => ThrowIfMissing(payload, RuntimeInformation.OSArchitecture, need);

    // ", only for X64" when the build carries the payload for some other
    // architecture, and nothing when it carries it for none.
    private static string OnlyFor(IReadOnlyList<Architecture> archs) =>
        archs.Count == 0 ? "" : ", only for " + string.Join(" and ", archs);

    private static State Read()
    {
        var asm = typeof(HMBuildInfo).Assembly;
        var names = new HashSet<string>(asm.GetManifestResourceNames(), StringComparer.Ordinal);

        bool driver(Architecture a) =>
            DriverBuilder.PayloadFor(a).All(p => names.Contains(p.Logical)) &&
            names.Contains(DriverBuilder.NativePrefixFor(a) + SwdDeviceFactory.HelperFile);
        bool usbip(Architecture a) => UsbipDriverInstaller.UdeResourcesFor(a).All(names.Contains);

        return new State(
            Array.AsReadOnly(s_built.Where(driver).ToArray()),
            Array.AsReadOnly(s_built.Where(usbip).ToArray()),
            VrDriverBuilder.RequiredResources.All(names.Contains));
    }
}
