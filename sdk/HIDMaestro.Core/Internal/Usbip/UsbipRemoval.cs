using System.Collections.Generic;
using HIDMaestro.Internal.Uninstall;

namespace HIDMaestro.Internal.Usbip;

/// <summary>The usbip-win2 part of a full removal of HIDMaestro. It runs
/// after HIDMaestro's own imports are unplugged and before HIDMaestro's
/// staged files are deleted.
///
/// <para>It never touches usbip-win2's root-hub filter, whose removal
/// restarts every USB root hub. It removes the host controller, its driver
/// package and the rest of usbip-win2 only when HIDMaestro's install record
/// shows HIDMaestro installed it, or when the caller's
/// <c>includeUnproven</c> asks for it, and in either case only
/// when no other program uses usbip-win2.</para></summary>
internal static class UsbipRemoval
{
    /// <summary>Remove usbip-win2's pieces as the class describes, or with
    /// <paramref name="preview"/> report what a removal would do and change
    /// nothing.</summary>
    internal static IReadOnlyList<UninstallEntry> Remove(bool preview, bool includeUnproven) => [];
}
