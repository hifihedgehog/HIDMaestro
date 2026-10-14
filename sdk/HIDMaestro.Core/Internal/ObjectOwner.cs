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
/// its own mutex to Administrators fails with ERROR_INVALID_OWNER. An
/// elevated process's objects are owned by Administrators, a service's by
/// its account, so any other owner marks an object someone else made.
/// </summary>
internal static partial class ObjectOwner
{
    /// <summary>Whether the object's owner is SYSTEM, Administrators or
    /// LocalService. The handle needs READ_CONTROL. False when the owner
    /// cannot be read.</summary>
    internal static bool IsAdministrative(SafeHandle handle)
    {
        uint r = GetSecurityInfo(handle, SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION,
            out IntPtr owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out IntPtr sd);
        if (r != 0) return false;
        try
        {
            return IsWellKnownSid(owner, WinLocalSystemSid)
                || IsWellKnownSid(owner, WinBuiltinAdministratorsSid)
                || IsWellKnownSid(owner, WinLocalServiceSid);
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

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);
}
