using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace HIDMaestro.Internal;

/// <summary>
/// Machine-wide ownership for programs that embed HIDMaestro side by side.
///
/// <para>A claim is a named pipe created with
/// <c>FILE_FLAG_FIRST_PIPE_INSTANCE</c> and one instance. The create either
/// makes the first instance or fails, so claiming and checking are one call.
/// Claims live under the pipe prefix <c>ProtectedPrefix\Administrators\</c>,
/// which the Session Manager creates at boot and under which only a token
/// with Administrators enabled can create a pipe. Under a plain name a
/// program that is not elevated could create a claim first, and every
/// HIDMaestro program would take it for a live owner's for as long as that
/// program ran.</para>
///
/// <para>No Learn page describes the prefixes. CreateNamedPipeW says a
/// pipe name "can include any character other than a backslash", which the
/// prefix path does not follow, and the one public account (Forshaw, "Named
/// Pipe Secure Prefixes", 2017) names no Windows version. Nine smss.exe
/// files and three npfs.sys files were read, named here by their version
/// resources. Each smss.exe, 10.0.10240.16384, 10586.0, 14393.0,
/// 15254.158, 16299.192, 17763.107, 19041.5125, 19041.7664 and 26100.1,
/// creates <c>\Device\NamedPipe\ProtectedPrefix\Administrators</c> among
/// its prefixes at boot. Windows 10 2004 through 22H2, build 19044 among
/// them, run 19041.x binaries. Each npfs.sys, 10240.16384, 19041.5125 and
/// 26100.1, has in Microsoft's public symbols the same prefix routines
/// (NpCreateNamedPipePrefix, NpOpenNamedPipePrefix, NpFindPrefix,
/// NpFreeProtectedPrefix, NpUnlinkProtectedPrefix). That a token without
/// Administrators is refused there was measured on 26200 only.</para>
///
/// <para>The handle is not inheritable, and Windows deletes the pipe when the
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

    private const string ClaimPrefix = @"ProtectedPrefix\Administrators\";

    internal static string OwnerPipeName(string ownerId) => $"{ClaimPrefix}HIDMaestro.Owner.{ownerId}";
    internal static string IndexPipeName(int transportIndex) => $"{ClaimPrefix}HIDMaestro.Index.{transportIndex}";
    internal static string IdentityPipeName(string token) => $"{ClaimPrefix}HIDMaestro.Identity.{token}";

    /// <summary>The v1.12.0 owner pipe of a transport index. A v1.12.0
    /// program holds it while it has a controller at that index.</summary>
    internal static string LegacyOwnerPipeName(int transportIndex) => $"HIDMaestroOwner{transportIndex}";

    // SYNCHRONIZE | FILE_READ_ATTRIBUTES for SYSTEM, Administrators and
    // LocalService. Nobody can write or read a claim, and a non-elevated
    // process cannot even open one. Probing needs no access at all, so any
    // process can still probe a claim under the prefix. Owner Administrators
    // (O:BA), so the owner does not depend on the default-owner policy.
    private const string ClaimSddl = "O:BAD:P(A;;0x00100080;;;SY)(A;;0x00100080;;;BA)(A;;0x00100080;;;LS)";

    private static readonly object s_lock = new();
    private static string? s_ownerId;
    // Written after s_ownerId with Volatile.Write and read outside s_lock
    // with Volatile.Read, so a thread that sees the claim sees the id too.
    // Ordinary reads of the two may be reordered (Memory-model.md, in the
    // dotnet runtime), and on ARM64 a thread racing the first claim would
    // read the claim and a null id, and take its own record for another
    // program's.
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
    /// this. A claim whose record could not be written gets it from the
    /// first call that can write it.</summary>
    /// <exception cref="UnauthorizedAccessException">The claim was refused
    /// with ERROR_ACCESS_DENIED: the token lacks Administrators, or, with
    /// Administrators enabled, for a cause not read (<see cref="RefusedClaim"/>).</exception>
    internal static void EnsureOwner()
    {
        if (Volatile.Read(ref s_ownerClaim) != null)
        {
            if (!s_recordWritten) TryWriteRecord();
            return;
        }
        lock (s_lock)
        {
            if (s_ownerClaim != null) return;
            string id = Guid.NewGuid().ToString("N");
            SafeFileHandle? h = TryClaim(OwnerPipeName(id));
            // A fresh GUID collides with nothing, so a failure here is a
            // process that is not elevated, refused by the claim prefix, a
            // pipe system without that prefix, or the system refusing pipes
            // outright. Nothing machine-wide can be arbitrated without the
            // claim.
            if (h == null)
                throw RefusedClaim(Marshal.GetLastPInvokeError());
            s_ownerId = id;
            Volatile.Write(ref s_ownerClaim, h);
            OwnerSweep.RegisterExitCleanup();
            TryWriteRecordLocked();
        }
    }

    /// <summary>The exception for a refused owner claim, stating what was
    /// read. Every ERROR_ACCESS_DENIED is an UnauthorizedAccessException,
    /// the type <see cref="TryEnsureOwner"/> turns into a skipped sweep, as
    /// before. Only the message depends on the token. Without
    /// Administrators enabled the refusal is the prefix at work, and the
    /// message asks for elevation. With it enabled, the cause was not read.
    /// A pipe system without the Administrators prefix is one possible
    /// cause: a create under a prefix that does not exist fails with
    /// ERROR_ACCESS_DENIED even with Administrators enabled (measured on
    /// 26200), though no build read lacks the prefix. Software that filters
    /// pipe creation is another. When the token could not be read, the
    /// message says so.</summary>
    internal static Exception RefusedClaim(int error)
    {
        if (error != ERROR_ACCESS_DENIED)
            return new InvalidOperationException($"HIDMaestro could not create its ownership claim (Win32 error {error}).");
        return AdministratorsEnabled() switch
        {
            false => new UnauthorizedAccessException(
                "HIDMaestro could not take its ownership claim: the token the call ran under does not have Administrators enabled, which the claim requires. Run the program elevated."),
            null => new UnauthorizedAccessException(
                $@"HIDMaestro could not create its ownership claim under \\.\pipe\{ClaimPrefix}: the pipe system refused it with Win32 error {error}, and whether the token the call ran under has Administrators enabled could not be read."),
            true => new UnauthorizedAccessException(
                $@"HIDMaestro could not create its ownership claim under \\.\pipe\{ClaimPrefix}: the pipe system refused it with Win32 error {error}, though the token the call ran under has Administrators enabled. A Windows build without that pipe prefix would refuse a claim this way, and so can software that filters pipe creation."),
        };
    }

    /// <summary>Whether the token the create ran under, the thread's when it
    /// impersonates and the process's otherwise, has Administrators enabled,
    /// which is what the claim prefix requires, or null when that could not
    /// be read. CheckTokenMembership with no token reads that token.</summary>
    private static bool? AdministratorsEnabled()
    {
        Span<byte> sid = stackalloc byte[SECURITY_MAX_SID_SIZE];
        uint size = SECURITY_MAX_SID_SIZE;
        if (!CreateWellKnownSid(WinBuiltinAdministratorsSid, IntPtr.Zero, sid, ref size)) return null;
        if (!CheckTokenMembership(IntPtr.Zero, sid, out bool member)) return null;
        return member;
    }

    /// <summary><see cref="EnsureOwner"/> for the sweeps, which have
    /// nothing to do in a process that is not elevated: false there.</summary>
    internal static bool TryEnsureOwner()
    {
        try { EnsureOwner(); return true; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void TryWriteRecord()
    {
        lock (s_lock) TryWriteRecordLocked();
    }

    /// <summary>Write the record's header if it is not written yet. Caller
    /// holds s_lock. The owner claim needs Administrators enabled, so a write
    /// that fails here was refused for another cause, such as the key's ACL
    /// or an I/O error. The next EnsureOwner or NoteStaging tries it again.</summary>
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
    /// made the first and only instance, null when anyone already holds it,
    /// this process included: a second instance of a one-instance pipe is
    /// refused to the process that holds the first as well.</summary>
    internal static SafeFileHandle? TryClaim(string name)
    {
        SafeFileHandle? claim = CreateClaimHook is { } hook ? hook(name) : CreateClaim(name);
        if (claim != null)
            lock (s_madeHere)
            {
                // Entries of claims let go since are dropped here, so the
                // table holds about as many names as the process holds.
                foreach (string gone in s_madeHere.Where(kv => !Open(kv.Value)).Select(kv => kv.Key).ToList())
                    s_madeHere.Remove(gone);
                s_madeHere[name] = new WeakReference<SafeFileHandle>(claim);
            }
        return claim;
    }

    private static SafeFileHandle? CreateClaim(string name)
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

    /// <summary>Whether a claim this process made through
    /// <see cref="TryClaim"/> is still open: a lease's, a sweep's, the
    /// phantom cleanup's. A claim is let go by disposing its handle, or
    /// with the handle when the process ends.</summary>
    internal static bool HeldHere(string name)
    {
        lock (s_madeHere)
            return s_madeHere.TryGetValue(name, out var made) && Open(made);
    }

    private static bool Open(WeakReference<SafeFileHandle> made) =>
        made.TryGetTarget(out var h) && !h.IsClosed && !h.IsInvalid;

    /// <summary>The claims this process made, by name, for
    /// <see cref="HeldHere"/>. Guarded by itself.</summary>
    private static readonly Dictionary<string, WeakReference<SafeFileHandle>> s_madeHere =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stands in for the pipe create of <see cref="TryClaim"/>:
    /// the claim's name to a handle, or null when it is held. Set by
    /// pnp_notify_check's offline part, which cannot create a claim without
    /// elevation, to hold claims for this process or for another one. Null
    /// outside that test.</summary>
    internal static Func<string, SafeFileHandle?>? CreateClaimHook;

    /// <summary>Whether a live process holds the named claim. True and
    /// ERROR_SEM_TIMEOUT both mean a holder, ERROR_FILE_NOT_FOUND means
    /// none, and any other error counts as a holder. A timeout of zero
    /// would mean the server's default wait, so the probe passes 1 ms.</summary>
    internal static bool IsHeld(string name)
    {
        if (WaitNamedPipe(@"\\.\pipe\" + name, 1)) return true;
        return Marshal.GetLastPInvokeError() != ERROR_FILE_NOT_FOUND;
    }

    // Published with Volatile.Write once the structure it points to is
    // written, and read outside s_lock with Volatile.Read, for the reason
    // s_ownerClaim is: a pointer is no object reference, so its plain
    // store orders nothing before it.
    private static IntPtr s_claimSa;

    /// <summary>The claim pipes' SECURITY_ATTRIBUTES, built once and kept
    /// for the life of the process.</summary>
    private static IntPtr ClaimSecurityAttributes()
    {
        IntPtr published = Volatile.Read(ref s_claimSa);
        if (published != IntPtr.Zero) return published;
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
            Volatile.Write(ref s_claimSa, sa);
            return sa;
        }
    }

    // ── Records ──────────────────────────────────────────────────────────

    /// <summary>One controller as its owner's record lists it.</summary>
    internal sealed record ControllerEntry(string Token, int TransportIndex, string Backend,
        string? ProfileId, string? InstanceId);

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
        RegistryKey? root = null;
        string[] ids;
        try
        {
            root = Registry.LocalMachine.OpenSubKey(RecordsRoot);
            if (root == null) return list;
            ids = root.GetSubKeyNames();
        }
        catch (Exception e) when (IsRegistryFailure(e))
        {
            root?.Dispose();
            return list;
        }
        // The id is this process's once its claim exists.
        string? selfId = Volatile.Read(ref s_ownerClaim) != null ? s_ownerId : null;
        using (root)
        {
            foreach (string id in ids)
            {
                // Another program's sweep deletes a dead record at any
                // moment, and the next query on a key deleted under an open
                // handle throws. That costs the record being deleted and
                // no other: a record this walk skipped could be a running
                // program's, which RemoveOrphaned would then not see.
                try
                {
                    if (ReadRecord(root, id, selfId) is { } r) list.Add(r);
                }
                catch (Exception e) when (IsRegistryFailure(e)) { }
            }
        }
        return list;
    }

    /// <summary>Called in <see cref="ReadAll"/> once a record's Controllers
    /// key is open, before its entries are listed. Coexistence probe section
    /// W sets it through reflection to delete the record there, as another
    /// program's sweep can. Null outside the probe.</summary>
    internal static Action<string>? AfterRecordOpened;

    private static OwnerRecord? ReadRecord(RegistryKey root, string id, string? selfId)
    {
        using var rec = root.OpenSubKey(id);
        if (rec == null) return null;
        var controllers = new List<ControllerEntry>();
        using (var cs = rec.OpenSubKey(ControllersKey))
        {
            AfterRecordOpened?.Invoke(id);
            if (cs != null)
                foreach (string token in cs.GetSubKeyNames())
                {
                    using var c = cs.OpenSubKey(token);
                    if (c == null) continue;
                    controllers.Add(new ControllerEntry(token,
                        c.GetValue("TransportIndex") is int ti ? ti : -1,
                        c.GetValue("Backend") as string ?? "umdf",
                        c.GetValue("Profile") as string,
                        c.GetValue("InstanceId") as string));
                }
        }
        bool self = string.Equals(id, selfId, StringComparison.OrdinalIgnoreCase);
        return new OwnerRecord(id,
            rec.GetValue("Pid") is int pid ? pid : 0,
            rec.GetValue("Image") as string,
            rec.GetValue("StartTime") is long st ? st : 0,
            rec.GetValue("SdkVersion") as string,
            rec.GetValue("UsbipPort") is int up ? up : 0,
            self || IsHeld(OwnerPipeName(id)),
            self,
            controllers,
            rec.GetValue("StagingTag") as string);
    }

    /// <summary>What RegistryKey throws when a key cannot be read:
    /// SecurityException from OpenSubKey on access denied, and from the
    /// queries IOException on any other error, a key deleted under an open
    /// handle included, and UnauthorizedAccessException on access denied
    /// (RegistryKey.cs, Win32Error).</summary>
    private static bool IsRegistryFailure(Exception e) =>
        e is System.IO.IOException or System.Security.SecurityException or UnauthorizedAccessException;

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
    private const int ERROR_ACCESS_DENIED = 5;

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

    private const int SECURITY_MAX_SID_SIZE = 68;
    private const int WinBuiltinAdministratorsSid = 26;

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateWellKnownSid(int type, IntPtr domainSid, Span<byte> sid, ref uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CheckTokenMembership(IntPtr token, ReadOnlySpan<byte> sid, [MarshalAs(UnmanagedType.Bool)] out bool isMember);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision,
        out IntPtr securityDescriptor, IntPtr securityDescriptorSize);
}
