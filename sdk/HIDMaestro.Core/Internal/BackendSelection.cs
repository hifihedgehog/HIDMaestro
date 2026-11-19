using System;
using System.Runtime.InteropServices;

namespace HIDMaestro.Internal;

/// <summary>
/// Which backend a create runs on. Every rule runs before the create takes
/// anything machine-wide, so a refusal changes nothing.
///
/// <list type="number">
/// <item>The create's backend when it is not <see cref="HMBackend.Auto"/>.</item>
/// <item>Else the context's backend when it is not Auto.</item>
/// <item>Else Auto: <see cref="HMBackend.Usbip"/> for a profile only the
/// USB/IP backend presents, and otherwise <see cref="Machine"/>.</item>
/// <item>The result checked against the profile's data
/// (<see cref="NotSupportedReason"/>).</item>
/// <item>The result checked against the machine: an explicit UMDF2 request
/// where Windows refuses HIDMaestro's package throws
/// <see cref="HMDriverSigningException"/>, and a backend whose payload the
/// build lacks throws <see cref="HMPayloadMissingException"/>.</item>
/// </list>
/// </summary>
internal static class BackendSelection
{
    /// <summary>The environment variable that sets what Auto resolves to
    /// for the whole process, <c>umdf2</c> or <c>usbip</c>, read once. It
    /// takes the place of <see cref="Machine"/>'s own reading and never
    /// changes an explicit request. A diagnostic: it lets the regression
    /// battery run every profile on USB/IP on an x64 machine.</summary>
    internal const string OverrideVariable = "HIDMAESTRO_BACKEND";

    private static readonly Lazy<HMBackend?> s_override = new(() => ParseOverride(Environment.GetEnvironmentVariable(OverrideVariable)));

    /// <summary>The override's value, or null for none. Any value other
    /// than the two names is no override.</summary>
    internal static HMBackend? ParseOverride(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "umdf2" => HMBackend.Umdf2,
            "usbip" => HMBackend.Usbip,
            _ => null,
        };

    /// <summary>True when this process runs elevated, which an install of
    /// the USB/IP host controller needs.</summary>
    internal static bool ProcessElevated
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>The override this process read, or null.</summary>
    internal static HMBackend? Override => s_override.Value;

    /// <summary>What Auto resolves to on this machine for a profile both
    /// backends present: UMDF2 where the build carries the driver for this
    /// architecture and Auto can run it without an import Windows refuses
    /// (<see cref="DriverDeploy.AutoAvoidsDriver"/>). Else USB/IP whenever
    /// the build carries either payload: a build without the transport then
    /// names the transport it lacks (<see cref="HMPayloadMissingException"/>)
    /// rather than a driver Windows refuses. A build that carries neither
    /// resolves to UMDF2 and names the driver.
    /// <see cref="OverrideVariable"/> replaces the answer.</summary>
    internal static HMBackend Machine => Override ?? MachineRule(
        HMBuildInfo.Carries(HMPayload.Driver, RuntimeInformation.OSArchitecture),
        HMBuildInfo.Carries(HMPayload.UsbipTransport, RuntimeInformation.OSArchitecture),
        () => DriverDeploy.AutoAvoidsDriver());

    /// <summary>The machine rule over its inputs. <paramref name="avoidsDriver"/>
    /// is asked only when the build carries the driver.</summary>
    internal static HMBackend MachineRule(bool carriesDriver, bool carriesTransport, Func<bool> avoidsDriver)
    {
        if (carriesDriver && !avoidsDriver()) return HMBackend.Umdf2;
        return carriesDriver || carriesTransport ? HMBackend.Usbip : HMBackend.Umdf2;
    }

    /// <summary>The backend a request names: the create's when it is not
    /// Auto, else the context's, else Auto.</summary>
    internal static HMBackend Requested(HMBackend create, HMBackend context) =>
        create != HMBackend.Auto ? create : context;

    /// <summary>Steps 1 to 4: the backend a create of
    /// <paramref name="profile"/> runs on, never Auto. Throws
    /// <see cref="HMBackendNotSupportedException"/> when the profile's data
    /// has no presentation on it.</summary>
    internal static HMBackend ForProfile(ControllerProfile profile, HMBackend requested, Func<HMBackend> machine)
    {
        HMBackend resolved = requested != HMBackend.Auto ? requested
            : profile.RequiresUsbipBackend ? HMBackend.Usbip
            : machine();
        if (NotSupportedReason(profile, resolved) is { } reason)
            throw new HMBackendNotSupportedException(profile.Id, resolved, reason);
        return resolved;
    }

    /// <summary>Step 5: the machine's half. <paramref name="explicitRequest"/>
    /// is true when the backend came from the create or the context rather
    /// than from Auto.</summary>
    internal static void ThrowIfMachineRefuses(ControllerProfile profile, HMBackend resolved, bool explicitRequest)
    {
        if (resolved == HMBackend.Usbip)
        {
            HMBuildInfo.ThrowIfMissing(HMPayload.UsbipTransport,
                $"Profile '{profile.Id}' runs on the USB/IP backend.");
            return;
        }
        HMBuildInfo.ThrowIfMissing(HMPayload.Driver, $"Profile '{profile.Id}' runs on the UMDF2 driver.");
        // Auto resolves to UMDF2 only where Windows installs the package,
        // unless the process override says umdf2: that diagnostic gets the
        // refusal an explicit request gets.
        if (explicitRequest || Override == HMBackend.Umdf2)
            DriverDeploy.ThrowIfSigningStopsDeploy();
    }

    /// <summary>Why <paramref name="profile"/>'s data has no presentation
    /// on <paramref name="backend"/>, or null when it has one. For
    /// <see cref="HMBackend.Auto"/> the answer is for the backend Auto
    /// resolves to on this machine (<see cref="Machine"/>), so it is the
    /// answer an Auto create gets. For Umdf2 and Usbip the machine plays no
    /// part.</summary>
    internal static string? NotSupportedReason(ControllerProfile profile, HMBackend backend) =>
        NotSupportedReason(profile, backend, () => Machine);

    /// <summary><see cref="NotSupportedReason(ControllerProfile, HMBackend)"/>
    /// with Auto resolved through <paramref name="machine"/>.</summary>
    internal static string? NotSupportedReason(ControllerProfile profile, HMBackend backend, Func<HMBackend> machine)
    {
        switch (backend)
        {
            case HMBackend.Auto:
                return NotSupportedReason(profile, profile.RequiresUsbipBackend ? HMBackend.Usbip : machine(), machine);
            case HMBackend.Umdf2:
                if (profile.RequiresUsbipBackend)
                    return "its backend is \"usbip\": it has interfaces only the USB/IP backend presents.";
                if (!profile.HasDescriptor) return "it has no HID descriptor.";
                // The identity check the XUSB function makes on USB/IP holds
                // for the companion too: xinputhid would bind its HID child.
                return Usbip.XusbFunction.RefusedIdentityReason(profile);
            case HMBackend.Usbip:
                return Usbip.UsbPresentation.NotPresentableReason(profile);
            default:
                return $"{backend} is not a backend.";
        }
    }
}
