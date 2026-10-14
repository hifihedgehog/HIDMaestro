using System;
using System.Runtime.InteropServices;

namespace HIDMaestro;

/// <summary>
/// This build of HIDMaestro.Core lacks a payload the call needs: the UMDF2
/// driver or the USB/IP transport for the machine's architecture, or the
/// OpenVR driver. Derived from <see cref="InvalidOperationException"/>, and
/// thrown before any machine-wide step. Official releases carry every
/// payload. <see cref="HMBuildInfo"/> says what a build carries, so a
/// consumer can leave out what its build cannot create.
/// </summary>
public sealed class HMPayloadMissingException : InvalidOperationException
{
    /// <summary>The payload the call needed.</summary>
    public HMPayload Payload { get; }

    /// <summary>The architecture it was needed for, or null for the OpenVR
    /// driver, which has one package.</summary>
    public Architecture? Architecture { get; }

    internal HMPayloadMissingException(string message, HMPayload payload, Architecture? architecture)
        : base(message)
    {
        Payload = payload;
        Architecture = architecture;
    }
}
