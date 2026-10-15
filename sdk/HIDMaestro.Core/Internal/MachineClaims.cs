using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HIDMaestro.Internal;

/// <summary>
/// Machine-wide ownership for programs that embed HIDMaestro side by side.
///
/// <para>A claim is a named pipe created with
/// <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c> and one instance. The create either
/// makes the first instance or fails, so claiming and checking are one call.
/// The handle is not inheritable, and Windows deletes the pipe when the
/// last handle closes, which it does when the process ends however it ends.
/// Liveness is a <c>WaitNamedPipe</c> probe, which opens nothing, so a probe
/// can never keep a dead owner's claim alive. The design note
/// docs/design/v1.13.0.md has the measurements behind the choice.</para>
///
/// <para>Each process holds one owner claim from its first machine-wide
/// write until it exits, and one claim per transport index and per
/// identity it uses. Beside the owner claim sits a record under
/// <c>HKLM\SOFTWARE\HIDMaestroOwners\&lt;owner id&gt;</c> naming the
/// process, the staging it uses and every controller it has, written with
/// the claim and kept until the process exits, so a program between its
/// InstallDriver and its first create is as visible to other programs as
/// one with controllers. A controller's entry is written before any PnP
/// work. A record whose owner claim is gone describes a dead program, and
/// that is the only thing the sweeps remove without being asked to remove
/// everything.</para>
/// </summary>
internal static partial class MachineClaims
{
    internal const string RecordsRoot = @"SOFTWARE\HIDMaestroOwners";
    private const string ControllersKey = "Controllers";

    internal static string OwnerPipeName(string ownerId) => $"HIDMaestro.Owner.{ownerId}";
    internal static string IndexPipeName(int transportIndex) => $"HIDMaestro.Index.{transportIndex}";
    internal static string IdentityPipeName(string token) => $"HIDMaestro.Identity.{token}";

    /// <summary>The v1.12.0 owner pipe of a transport index. A v1.12.0
    /// program holds it while it has a controller at that index.</summary>
    internal static string LegacyOwnerPipeName(int transportIndex) => $"HIDMaestroOwner{transportIndex}";

    // SYNCHRONIZE | FILE_READ_ATTRIBUTES for SYSTEM, Administrators and
    // LocalService. Nobody can write or read a claim, and a non-elevated
    // process cannot even open one. Probing needs no access at all.
    private const string ClaimSddl = "D:P(A;;0x00100080;;;SY)(A;;0x00100080;;;BA)(A;;0x00100080;;;LS)";

    private static readonly object s_lock = new();
    private static string? s_ownerId;
    private static SafeFileHandle? s_ownerClaim;
    private static bool s_recordWritten;
    // This process's start time for its record, read once: a record goes
    // when its last controller does and comes back with the next one.
    private static long s_startTimeUtc;

    /// <summary>This process's owner id, a GUID drawn once.</summary>
    internal static string OwnerId
    {
        get
        {
            EnsureOwner();
            return s_ownerId!;
        }
    }

    /// <summary>Take this process's owner claim if it has none yet, and
    /// write its record. Every machine-wide write the SDK makes comes after
    /// this. A process that cannot write HKLM, one not elevated, gets the
    /// claim, and its record is written by the first call that can.</summary>
    internal static void EnsureOwner()
    {
        if (s_ownerClaim != null)
        {
            if (!s_recordWritten) TryWriteRecord();
            return;
        }
        lock (s_lock)
        {
            if (s_ownerClaim != null) return;
            string id = Guid.NewGuid().ToString("N");
            SafeFileHandle? h = TryClaim(OwnerPipeName(id));
            // A fresh GUID collides with nothing, so a failure here is the
            // system refusing pipes outright. Nothing machine-wide can be
            // arbitrated without the claim.
            if (h == null)
                throw new InvalidOperationException(
                    $"HIDMaestro could not create its ownership claim (Win32 error {Marshal.GetLastPInvokeError()}).");
            s_ownerId = id;
            s_ownerClaim = h;
            OwnerSweep.RegisterExitCleanup();
            TryWriteRecordLocked();
        }
    }

    private static void TryWriteRecord()
    {
        lock (s_lock) TryWriteRecordLocked();
    }

    /// <summary>Write the record's header if it is not written yet. Caller
    /// holds s_lock. Non-elevated, the write fails and is tried again later.</summary>
    private static void TryWriteRecordLocked()
    {
        if (s_recordWritten || s_ownerId == null) return;
        try
        {
            using var rec = Registry.LocalMachine.CreateSubKey($@"{RecordsRoot}\{s_ownerId}");
            WriteHeader(rec);
        }
        catch (UnauthorizedAccessException) { }
        catch (System.Security.SecurityException) { }
        catch (System.IO.IOException) { }
    }

    /// <summary>The process's own values in its record. Caller holds s_lock.</summary>
    private static void WriteHeader(RegistryKey rec)
    {
        if (s_startTimeUtc == 0)
        {
            using var self = Process.GetCurrentProcess();
            s_startTimeUtc = self.StartTime.ToFileTimeUtc();
        }
        rec.SetValue("Pid", Environment.ProcessId, RegistryValueKind.DWord);
        rec.SetValue("Image", Environment.ProcessPath ?? "", RegistryValueKind.String);
        rec.SetValue("StartTime", s_startTimeUtc, RegistryValueKind.QWord);
        rec.SetValue("SdkVersion", SdkVersion, RegistryValueKind.String);
        if (s_stagingTag != null) rec.SetValue("StagingTag", s_stagingTag, RegistryValueKind.String);
        if (s_usbipPort > 0) rec.SetValue("UsbipPort", s_usbipPort, RegistryValueKind.DWord);
        s_recordWritten = true;
    }

    private static string? s_stagingTag;
    private static int s_usbipPort;

    /// <summary>Name the staging directory this process uses, the build's
    /// <c>&lt;version&gt;-&lt;hash&gt;</c>, in its record, so a deploy by
    /// another build keeps the directory while this process runs.</summary>
    internal static void NoteStaging(string tag)
    {
        EnsureOwner();
        lock (s_lock)
        {
            if (string.Equals(s_stagingTag, tag, StringComparison.Ordinal) && s_recordWritten) return;
            s_stagingTag = tag;
            if (!s_recordWritten) { TryWriteRecordLocked(); return; }
            try
            {
                using var rec = Registry.LocalMachine.CreateSubKey($@"{RecordsRoot}\{s_ownerId}");
                rec.SetValue("StagingTag", tag, RegistryValueKind.String);
            }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
            catch (System.IO.IOException) { }
        }
    }

    // ── Claims ───────────────────────────────────────────────────────────

    /// <summary>Create the named claim. Returns the handle when this call
    /// made the first and only instance, null when anyone already holds it.</summary>
    internal static SafeFileHandle? TryClaim(string name)
    {
        IntPtr sa = ClaimSecurityAttributes();
        SafeFileHandle h = CreateNamedPipe(@"\\.\pipe\" + name,
            PIPE_ACCESS_INBOUND | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_REJECT_REMOTE_CLIENTS, 1, 0, 0, 0, sa);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            Marshal.SetLastPInvokeError(err);
            return null;
        }
        return h;
    }

    /// <summary>Whether a live process holds the named claim. True and
    /// ERROR_SEM_TIMEOUT both mean a holder, ERROR_FILE_NOT_FOUND means
    /// none, and any other error counts as a holder. A timeout of zero
    /// would mean the server's default wait, so the probe passes 1 ms.</summary>
    internal static bool IsHeld(string name)
    {
        if (WaitNamedPipe(@"\\.\pipe\" + name, 1)) return true;
        return Marshal.GetLastPInvokeError() != ERROR_FILE_NOT_FOUND;
    }

    private static IntPtr s_claimSa;

    /// <summary>The claim pipes' SECURITY_ATTRIBUTES, built once and kept
    /// for the life of the process.</summary>
    private static IntPtr ClaimSecurityAttributes()
    {
        if (s_claimSa != IntPtr.Zero) return s_claimSa;
        lock (s_lock)
        {
            if (s_claimSa != IntPtr.Zero) return s_claimSa;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor(ClaimSddl, 1, out IntPtr sd, IntPtr.Zero))
                throw new InvalidOperationException($"HIDMaestro could not build the claim security descriptor (Win32 error {Marshal.GetLastPInvokeError()}).");
            IntPtr sa = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
            Marshal.StructureToPtr(new SECURITY_ATTRIBUTES
            {
                nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = 0,
            }, sa, false);
            s_claimSa = sa;
            return sa;
        }
    }

    // ── Records ──────────────────────────────────────────────────────────

    /// <summary>One controller as its owner's record lists it.</summary>
    internal sealed record ControllerEntry(string Token, int TransportIndex, string Backend,
        string? ProfileId, string? InstanceId, int VhciPort);

    /// <summary>One program's record, and whether its owner claim is held.</summary>
    internal sealed record OwnerRecord(string OwnerId, int Pid, string? Image, long StartTime,
        string? SdkVersion, int UsbipPort, bool Live, bool Self, IReadOnlyList<ControllerEntry> Controllers,
        string? StagingTag = null);

    /// <summary>Record a controller before any PnP work for it. Creates
    /// this process's record on first use.</summary>
    internal static void AddController(string token, int transportIndex, string backend, string? profileId)
    {
        EnsureOwner();
        lock (s_lock)
        {
            using var rec = Registry.LocalMachine.CreateSubKey($@"{RecordsRoot}\{s_ownerId}");
            if (!s_recordWritten) WriteHeader(rec);
            using var c = rec.CreateSubKey($@"{ControllersKey}\{token}");
            c.SetValue("TransportIndex", transportIndex, RegistryValueKind.DWord);
            c.SetValue("Backend", backend, RegistryValueKind.String);
            c.SetValue("Profile", profileId ?? "", RegistryValueKind.String);
        }
    }

    /// <summary>Add a value to a controller's entry once it is known: the
    /// devnode instance id after the create, the vhci port after the
    /// attach.</summary>
    internal static void SetControllerValue(string token, string name, object value)
    {
        lock (s_lock)
        {
            if (s_ownerId == null) return;
            using var c = Registry.LocalMachine.OpenSubKey($@"{RecordsRoot}\{s_ownerId}\{ControllersKey}\{token}", writable: true);
            c?.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
    }

    /// <summary>The loopback port this process's USB/IP server listens on.</summary>
    internal static void SetUsbipPort(int port)
    {
        EnsureOwner();
        lock (s_lock)
        {
            s_usbipPort = port;
            using var rec = Registry.LocalMachine.CreateSubKey($@"{RecordsRoot}\{s_ownerId}");
            if (!s_recordWritten) WriteHeader(rec);
            rec.SetValue("UsbipPort", port, RegistryValueKind.DWord);
        }
    }

    /// <summary>Drop a controller's entry after its teardown, or after a
    /// create that failed writing it. The record stays until the process
    /// exits. An entry has values and no subkeys, so RegDeleteKeyEx removes
    /// it with DELETE on the entry alone: the write access a failed write
    /// was refused is never needed here, where opening the Controllers key
    /// for writing would need it.</summary>
    internal static void RemoveController(string token)
    {
        lock (s_lock)
        {
            if (s_ownerId == null) return;
            // ERROR_FILE_NOT_FOUND when there is no entry. Any other error
            // leaves an entry the sweeps read as this live process's.
            RegDeleteKeyExW(HKEY_LOCAL_MACHINE, $@"{RecordsRoot}\{s_ownerId}\{ControllersKey}\{token}", 0, 0);
        }
    }

    /// <summary>Delete this process's record, at its exit.</summary>
    internal static void DeleteOwnRecord()
    {
        lock (s_lock)
        {
            if (s_ownerId == null) return;
            DeleteRecord(s_ownerId);
            s_recordWritten = false;
        }
    }

    /// <summary>Delete a record. The sweep calls this for a dead owner
    /// once everything the record listed is gone.</summary>
    internal static void DeleteRecord(string ownerId)
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree($@"{RecordsRoot}\{ownerId}", throwOnMissingSubKey: false);
            using var root = Registry.LocalMachine.OpenSubKey(RecordsRoot, writable: true);
            bool empty = root != null && root.SubKeyCount == 0 && root.ValueCount == 0;
            root?.Close();
            if (empty) Registry.LocalMachine.DeleteSubKey(RecordsRoot, throwOnMissingSubKey: false);
        }
        catch { }
    }

    /// <summary>Every record on the machine, each with its owner's liveness.</summary>
    internal static List<OwnerRecord> ReadAll()
    {
        var list = new List<OwnerRecord>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(RecordsRoot);
            if (root == null) return list;
            foreach (string id in root.GetSubKeyNames())
            {
                using var rec = root.OpenSubKey(id);
                if (rec == null) continue;
                var controllers = new List<ControllerEntry>();
                using (var cs = rec.OpenSubKey(ControllersKey))
                {
                    if (cs != null)
                        foreach (string token in cs.GetSubKeyNames())
                        {
                            using var c = cs.OpenSubKey(token);
                            if (c == null) continue;
                            controllers.Add(new ControllerEntry(token,
                                c.GetValue("TransportIndex") is int ti ? ti : -1,
                                c.GetValue("Backend") as string ?? "umdf",
                                c.GetValue("Profile") as string,
                                c.GetValue("InstanceId") as string,
                                c.GetValue("VhciPort") is int vp ? vp : -1));
                        }
                }
                bool self = string.Equals(id, s_ownerId, StringComparison.OrdinalIgnoreCase);
                list.Add(new OwnerRecord(id,
                    rec.GetValue("Pid") is int pid ? pid : 0,
                    rec.GetValue("Image") as string,
                    rec.GetValue("StartTime") is long st ? st : 0,
                    rec.GetValue("SdkVersion") as string,
                    rec.GetValue("UsbipPort") is int up ? up : 0,
                    self || IsHeld(OwnerPipeName(id)),
                    self,
                    controllers,
                    rec.GetValue("StagingTag") as string));
            }
        }
        catch { }
        return list;
    }

    /// <summary>The live record holding an identity token, for naming the
    /// owner in a refusal. Null when no live record lists it.</summary>
    internal static OwnerRecord? LiveHolderOf(string token)
    {
        foreach (var r in ReadAll())
            if (r.Live && r.Controllers.Any(c => string.Equals(c.Token, token, StringComparison.OrdinalIgnoreCase)))
                return r;
        return null;
    }

    /// <summary>"process 1234 (C:\Apps\PadForge.exe, HIDMaestro 1.13.0)".</summary>
    internal static string Describe(OwnerRecord r) =>
        $"process {r.Pid} ({(string.IsNullOrEmpty(r.Image) ? "unknown image" : r.Image)}, HIDMaestro {r.SdkVersion ?? "unknown"})";

    internal static string SdkVersion { get; } =
        typeof(MachineClaims).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    // ── Win32 ────────────────────────────────────────────────────────────

    private const uint PIPE_ACCESS_INBOUND = 0x00000001;
    private const uint FILE_FLAG_FIRST_PIPE_INSTANCE = 0x00080000;
    private const uint PIPE_TYPE_BYTE = 0x00000000;
    private const uint PIPE_REJECT_REMOTE_CLIENTS = 0x00000008;
    private const int ERROR_FILE_NOT_FOUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    private static readonly IntPtr HKEY_LOCAL_MACHINE = unchecked((IntPtr)(int)0x80000002);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegDeleteKeyExW(IntPtr key, string subKey, int samDesired, int reserved);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint maxInstances,
        uint outBufferSize, uint inBufferSize, uint defaultTimeout, IntPtr securityAttributes);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipe(string name, uint timeout);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision,
        out IntPtr securityDescriptor, IntPtr securityDescriptorSize);
}
