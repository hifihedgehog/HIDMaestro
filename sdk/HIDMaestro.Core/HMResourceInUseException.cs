using System;

namespace HIDMaestro;

/// <summary>
/// A create asked for something another live controller holds: its
/// identity key, or a transport index when none is left. Derived from
/// <see cref="InvalidOperationException"/>, so code that catches that keeps
/// working.
///
/// <para>Two programs that embed HIDMaestro can run on one machine side by
/// side. Each identity is held by one live controller on the whole machine,
/// in whichever program created it. The message names the holder: its
/// process id, image path and SDK version, when the holder is a program
/// built on HIDMaestro 1.13.0 or later.</para>
/// </summary>
public sealed class HMResourceInUseException : InvalidOperationException
{
    /// <summary>What was asked for, such as <c>identity key 'slot-1'</c>.</summary>
    public string Resource { get; }

    /// <summary>The holder's process id, or null when the holder is a
    /// program built on HIDMaestro 1.12.0 or older, which names none.</summary>
    public int? OwnerProcessId { get; }

    /// <summary>The holder's executable path, when known.</summary>
    public string? OwnerImagePath { get; }

    /// <summary>The holder's HIDMaestro version, when known.</summary>
    public string? OwnerSdkVersion { get; }

    internal HMResourceInUseException(string message, string resource, Internal.MachineClaims.OwnerRecord? holder)
        : base(message)
    {
        Resource = resource;
        if (holder != null)
        {
            OwnerProcessId = holder.Pid;
            OwnerImagePath = holder.Image;
            OwnerSdkVersion = holder.SdkVersion;
        }
    }
}
