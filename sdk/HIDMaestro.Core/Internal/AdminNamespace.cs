using System;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HIDMaestro.Internal;

/// <summary>
/// The private object namespace the SDK's machine-wide locks live in.
///
/// <para>A <c>Global\</c> mutex is open to every local program: one that is
/// not elevated can create the name first and hold it, or give it a DACL
/// that refuses everyone else, and every HIDMaestro program then waits on
/// it or fails. This namespace's boundary descriptor holds the
/// Administrators SID. "The caller must be within the specified boundary for
/// the create operation to succeed" (Object Namespaces on Learn), so only a
/// process whose token has Administrators enabled can create it, and its
/// DACL admits SYSTEM and Administrators alone, so only such a process can
/// open it. An object in it is named <c>&lt;alias&gt;\&lt;name&gt;</c>, the
/// alias being the one this process opened it under (CreatePrivateNamespace
/// on Learn).</para>
///
/// <para>Lifetime. Learn says "The handle is also closed when the process
/// that created the namespace terminates. After the namespace handle is
/// closed, subsequent calls to OpenPrivateNamespace fail, but all operations
/// on objects in the namespace succeed." Read strictly, the namespace stops
/// opening once its creator exits, even while others hold handles. Measured
/// on this build the opposite: with the creator's handle closed the
/// namespace still opened while another process held a handle, and only
/// after the last handle closed did it stop opening, though a mutex made in
/// it was still held. The code is correct whichever way it falls. Every
/// process opens the namespace once and keeps the handle until it exits, so
/// a held lock always has a live namespace. A process that finds the open
/// gone creates the namespace again, and the create fails with
/// ERROR_ALREADY_EXISTS while one still exists, so at most one namespace of
/// this boundary and alias exists at a time and the lock inside it is one
/// object. If the strict reading held and an open failed, the create retries
/// below cover it, and a deploy that cannot take the lock simply does not
/// run rather than running beside another.</para>
///
/// <para>The boundary holds the Administrators SID and nothing else. The NS1
/// example on Learn adds "the current session number" to a boundary on
/// purpose to scope a namespace to one session, so a boundary without a
/// session SID, like this one, is not per-session. A session-0 service and a
/// desktop program therefore share the one namespace. This was not run
/// across two sessions here: no session-0 process is available to the test.</para>
/// </summary>
internal static partial class AdminNamespace
{
    private const string BoundaryName = "HIDMaestro";
    private const string Alias = "HIDMaestro.Admin";
    // Owner Administrators (O:BA), so the owner is fixed whatever the
    // default-owner policy is.
    private const string Sddl = "O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)";

    private static readonly object s_lock = new();
    private static volatile bool s_open;
    // Kept for the life of the process, never closed: see the class comment.
    private static IntPtr s_handle;

    /// <summary>The name of an object in the namespace, opening the
    /// namespace on first use.</summary>
    /// <exception cref="UnauthorizedAccessException">The process is not
    /// elevated.</exception>
    internal static string Name(string objectName)
    {
        if (!s_open) Open();
        return Alias + @"\" + objectName;
    }

    private static void Open()
    {
        lock (s_lock)
        {
            if (s_open) return;
            IntPtr boundary = CreateBoundaryDescriptorW(BoundaryName, 0);
            if (boundary == IntPtr.Zero)
                throw new InvalidOperationException($"HIDMaestro could not create its namespace boundary (Win32 error {Marshal.GetLastPInvokeError()}).");
            IntPtr sd = IntPtr.Zero, sa = IntPtr.Zero;
            try
            {
                var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                byte[] sid = new byte[admins.BinaryLength];
                admins.GetBinaryForm(sid, 0);
                if (!AddSIDToBoundaryDescriptor(ref boundary, sid))
                    throw new InvalidOperationException($"HIDMaestro could not build its namespace boundary (Win32 error {Marshal.GetLastPInvokeError()}).");
                if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl, 1, out sd, IntPtr.Zero))
                    throw new InvalidOperationException($"HIDMaestro could not build its namespace security descriptor (Win32 error {Marshal.GetLastPInvokeError()}).");
                sa = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
                Marshal.StructureToPtr(new SECURITY_ATTRIBUTES
                {
                    nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                    lpSecurityDescriptor = sd,
                    bInheritHandle = 0,
                }, sa, false);

                // Create, or open the one another process created. The open
                // fails with a missing path when the last other holder closed
                // it between the two calls, and the create is tried again.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    IntPtr h = CreatePrivateNamespaceW(sa, boundary, Alias);
                    int err = Marshal.GetLastPInvokeError();
                    if (h == IntPtr.Zero && err == ERROR_ALREADY_EXISTS)
                    {
                        h = OpenPrivateNamespaceW(boundary, Alias);
                        err = Marshal.GetLastPInvokeError();
                    }
                    if (h != IntPtr.Zero)
                    {
                        s_handle = h;
                        s_open = true;
                        return;
                    }
                    switch (err)
                    {
                        case ERROR_DUP_NAME:
                            // Another copy of this assembly in this process
                            // opened the same namespace under this alias, and
                            // its handle lives as long as the process.
                            s_open = true;
                            return;
                        case ERROR_ACCESS_DENIED:
                            throw new UnauthorizedAccessException(
                                "HIDMaestro's machine-wide locks need an elevated process: only a token with Administrators enabled can create or open them.");
                        case ERROR_FILE_NOT_FOUND:
                        case ERROR_PATH_NOT_FOUND:
                            continue;
                        default:
                            throw new InvalidOperationException($"HIDMaestro could not open its private namespace (Win32 error {err}).");
                    }
                }
                throw new InvalidOperationException("HIDMaestro could not open its private namespace: it ended eight times between a create and an open.");
            }
            finally
            {
                if (sa != IntPtr.Zero) Marshal.FreeHGlobal(sa);
                if (sd != IntPtr.Zero) LocalFree(sd);
                DeleteBoundaryDescriptor(boundary);
            }
        }
    }

    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_DUP_NAME = 52;
    private const int ERROR_ALREADY_EXISTS = 183;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreateBoundaryDescriptorW(string name, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AddSIDToBoundaryDescriptor(ref IntPtr boundaryDescriptor, byte[] requiredSid);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteBoundaryDescriptor(IntPtr boundaryDescriptor);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr CreatePrivateNamespaceW(IntPtr attributes, IntPtr boundaryDescriptor, string aliasPrefix);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenPrivateNamespaceW(IntPtr boundaryDescriptor, string aliasPrefix);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);
}
