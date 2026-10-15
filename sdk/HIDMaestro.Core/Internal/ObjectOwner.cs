using System;
using System.Runtime.InteropServices;

namespace HIDMaestro.Internal;

/// <summary>
/// Who created a named kernel object, for names a program that is not
/// elevated can create first. An owner must be a SID the token carries with
/// SE_GROUP_OWNER, one that "can be assigned as the owner of the token or
/// objects" (TOKEN_GROUPS on Learn), and in such a program's token
/// Administrators is deny-only. It cannot name SYSTEM, Administrators or
/// LocalService as an owner: measured, SetSecurityInfo setting the owner of
/// its own mutex to Administrators fails with ERROR_INVALID_OWNER.
///
/// <para>This release sets O:BA on every object it creates, so its own
/// objects are owned by Administrators on any machine. A program built on
/// HIDMaestro 1.12.0 or older set no owner, and its objects take the
/// creating token's default owner. That is Administrators for an elevated
/// token here (measured), but the user on some machines: "User Account
/// Control (UAC) will ensure the user account is being used as owner for all
/// objects created locally" (KB 947721 on Learn). Such an object cannot be
/// told from one a program that is not elevated made. Measured with the
/// user as default owner: an elevated creation and a medium creation both
/// come out owned by the user, and neither carries a mandatory label.
/// Whether that user is a member of Administrators can be read without a
/// token (NetUserGetLocalGroups with LG_INCLUDE_INDIRECT), but a program
/// that is not elevated runs as the same user, so membership does not tell
/// the two apart either. An owner other than SYSTEM or Administrators
/// therefore marks an object this release did not create, and nothing more
/// is known about who did.</para>
/// </summary>
internal static partial class ObjectOwner
{
    /// <summary>Whether the object's owner is SYSTEM or Administrators, and
    /// with <paramref name="allowLocalService"/> LocalService too. The
    /// handle needs READ_CONTROL. False when the owner cannot be read.
    ///
    /// <para>LocalService is a low-rights account every service can run as,
    /// so it is allowed only for the two per-controller events the UMDF2
    /// driver host creates (it runs as LocalService). For every other name
    /// a LocalService owner is some other program's, not HIDMaestro's.</para></summary>
    internal static bool IsOwnedByAdmin(SafeHandle handle, bool allowLocalService)
    {
        uint r = GetSecurityInfo(handle, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION,
            out IntPtr owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out IntPtr sd);
        if (r != 0) return false;
        try
        {
            return IsWellKnownSid(owner, WinLocalSystemSid)
                || IsWellKnownSid(owner, WinBuiltinAdministratorsSid)
                || (allowLocalService && IsWellKnownSid(owner, WinLocalServiceSid));
        }
        finally { LocalFree(sd); }
    }

    /// <summary>The object's owner as a SID string, or null when it cannot
    /// be read. The handle needs READ_CONTROL.</summary>
    internal static string? OwnerSid(SafeHandle handle)
    {
        if (GetSecurityInfo(handle, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION,
                out IntPtr owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out IntPtr sd) != 0)
            return null;
        try
        {
            if (!ConvertSidToStringSidW(owner, out IntPtr text)) return null;
            try { return Marshal.PtrToStringUni(text); }
            finally { LocalFree(text); }
        }
        finally { LocalFree(sd); }
    }

    private const int SE_KERNEL_OBJECT = 6;
    private const uint OWNER_SECURITY_INFORMATION = 0x00000001;
    private const int WinLocalSystemSid = 22;
    private const int WinLocalServiceSid = 23;
    private const int WinBuiltinAdministratorsSid = 26;

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(SafeHandle handle, int objectType, uint securityInfo,
        out IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWellKnownSid(IntPtr sid, int wellKnownSidType);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSidToStringSidW(IntPtr sid, out IntPtr stringSid);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);
}
