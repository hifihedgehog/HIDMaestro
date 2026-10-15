using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace HIDMaestro.Internal;

/// <summary>
/// The machine-wide lock over the registry keys HIDMaestro shares with real
/// devices: <see cref="RegistryLedger"/> and <see cref="OemNameOverrideStore"/>.
/// Held by the calling thread until it calls <see cref="ReleaseMutex"/>.
/// The ledger's own calls inside it take it again on the same thread, which
/// both mutexes allow.
///
/// <para>The lock is the mutex <see cref="MutexObject"/> in
/// <see cref="AdminNamespace"/>, which a program that is not elevated can
/// neither take first nor hold. A v1.12.0 program writes the same OEM keys
/// under <c>Global\HIDMaestro-OEM-Recovery</c>, so the lock takes that
/// mutex too, after its own. Any program can create that name first and
/// hold it. When it is owned by SYSTEM or Administrators, the lock waits
/// for it as v1.12.0 does. When its owner is anyone else, or could not be
/// read because the name refuses this process READ_CONTROL, the lock waits
/// for it a bounded five seconds and goes on without it after that: a
/// v1.12.0 program holds it only while it writes registry values, far less
/// than the bound. A process pays that wait once: when it ran out, later
/// acquisitions wait only a short bound that still covers a v1.12.0 write,
/// until one takes the mutex or finds it owned by SYSTEM or Administrators
/// again. An acquisition nested inside one on the same thread that went on
/// without the mutex does not wait at all: its outer scope already writes
/// without it.</para>
///
/// <para>Three ways remain for this process to write while a v1.12.0
/// program holds the mutex:</para>
/// <list type="number">
/// <item>A scope that went on without the mutex writes for its whole
/// length, and if the squatter lets the mutex go in that time, a v1.12.0
/// program can take it and write beside the scope. The exit handler
/// releases every controller in one scope, so at exit the window spans
/// all of their registry restores.</item>
/// <item>A v1.12.0 hold that outlasts the bound, five seconds on a first
/// wait and the short bound once a wait has run out (both scaled by
/// <see cref="TimeoutScale"/>), is not waited out.</item>
/// <item>A DACL that grants SYNCHRONIZE and MUTEX_MODIFY_STATE to a
/// v1.12.0 program's token and refuses them to this process's, such as one
/// that admits only SYSTEM while the v1.12.0 program runs as a service,
/// keeps this process out of the mutex on every acquisition.</item>
/// </list>
/// <para>Only an unbounded wait closes the first two, and an unbounded wait
/// lets any program stop every HIDMaestro program's ledger work by holding
/// the name. The third needs a token this process does not have.</para>
///
/// <para>The exit handler's one scope also costs time, not data: it holds
/// the namespace mutex, and the legacy mutex when taken, while it drops
/// every controller's holds and restores the GameInput and joy.cpl entries
/// nothing holds any more, a dead program's included. Its WinUSB entries
/// are put back after the scope ends (<see cref="RegistryLedger"/>, the
/// sweep), so no devnode wait runs under it.</para>
/// </summary>
internal sealed partial class LedgerLock : IDisposable
{
    internal const string MutexObject = "Ledger";
    internal const string LegacyMutexName = @"Global\HIDMaestro-OEM-Recovery";

    private readonly Mutex _lock;
    private SafeWaitHandle? _legacy;
    private bool _legacyHeld;

    // Set when a bounded wait for a legacy mutex owned by neither SYSTEM nor
    // Administrators ran out, which v1.12.0, holding it for a few registry
    // writes, never causes. While set the wait is the short bound, which
    // still covers a v1.12.0 write: a v1.12.0 program can hold the same
    // object after the squatter leaves, or own its mutex as the user on a
    // host whose default owner is the user, and must stay serialized. One
    // squatter then costs this process one long wait and a short one per
    // acquisition, not five seconds each.
    private static volatile bool s_legacyHeldByOther;

    // How many acquisitions this thread holds, and whether the outermost one
    // went on without the legacy mutex. A nested acquisition inside such a
    // scope skips the legacy mutex: waiting for it would serialize nothing,
    // since the scope around it already writes without it. An outer scope
    // that holds the legacy mutex takes it again at once, mutexes being
    // recursive per thread.
    [ThreadStatic] private static int t_depth;
    [ThreadStatic] private static bool t_withoutLegacy;
    private bool _counted;

    private LedgerLock(Mutex m) => _lock = m;

    /// <summary>Take the lock. An abandoned mutex is acquired: the ledger
    /// writes its records before its targets, so a replay is safe.</summary>
    /// <exception cref="UnauthorizedAccessException">The process is not
    /// elevated.</exception>
    internal static LedgerLock Acquire() => Acquire(Timeout.Infinite);

    /// <summary>Take the lock within <paramref name="timeoutMs"/>, both
    /// mutexes included, or throw <see cref="TimeoutException"/> holding
    /// neither. When what is left of <paramref name="timeoutMs"/> cuts the
    /// wait for the v1.12.0 mutex short of the lock's own bound, it throws
    /// whoever owns the mutex (<see cref="GivesUp"/>). Only a wait that ran
    /// the lock's own bound, for an owner other than SYSTEM or
    /// Administrators, goes on without the mutex, as
    /// <see cref="Acquire()"/> does. <see cref="Timeout.Infinite"/> waits as
    /// <see cref="Acquire()"/> does.</summary>
    internal static LedgerLock Acquire(int timeoutMs)
    {
        long until = timeoutMs == Timeout.Infinite ? long.MaxValue : Environment.TickCount64 + timeoutMs;
        var m = new Mutex(false, AdminNamespace.Name(MutexObject));
        try
        {
            bool taken;
            try { taken = m.WaitOne(timeoutMs); }
            catch (AbandonedMutexException) { taken = true; }
            if (!taken) throw new TimeoutException($"The registry ledger's lock was not free within {timeoutMs} ms.");
        }
        catch
        {
            m.Dispose();
            throw;
        }
        var l = new LedgerLock(m) { _counted = true };
        bool outermost = ++t_depth == 1;
        try
        {
            if (outermost || !t_withoutLegacy) l.TakeLegacy(until);
            else DeviceOrchestrator.LogDiag($"    ledger: nested in a scope that went on without {LegacyMutexName}, not waited for");
            if (outermost) t_withoutLegacy = !l._legacyHeld;
        }
        catch
        {
            l.ReleaseMutex();
            l.Dispose();
            throw;
        }
        return l;
    }

    private void TakeLegacy(long until)
    {
        IntPtr sd = IntPtr.Zero, sa = IntPtr.Zero;
        try
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(LegacySddl, 1, out sd, IntPtr.Zero)) return;
            sa = Marshal.AllocHGlobal(Marshal.SizeOf<SECURITY_ATTRIBUTES>());
            Marshal.StructureToPtr(new SECURITY_ATTRIBUTES
            {
                nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = sd,
                bInheritHandle = 0,
            }, sa, false);
            // A v1.12.0 program opens the name with new Mutex(false, name),
            // which asks CreateMutexEx for MAXIMUM_ALLOWED | SYNCHRONIZE |
            // MUTEX_MODIFY_STATE (dotnet/runtime release/10.0,
            // Mutex.Windows.cs, AccessRights): any DACL that grants those two
            // rights admits it, READ_CONTROL or not. So a refusal of the
            // first open, which also asks READ_CONTROL for the owner check,
            // is retried with those two rights alone, and the owner is then
            // not read: the wait is bounded as for any owner other than
            // SYSTEM or Administrators. A name that opens for neither, or is
            // another kind of object, is one no v1.12.0 program running
            // under this token can take either.
            var h = CreateMutexExW(sa, LegacyMutexName, 0, SYNCHRONIZE | MUTEX_MODIFY_STATE | READ_CONTROL);
            int openError = h.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
            if (openError == ERROR_ACCESS_DENIED)
            {
                h.Dispose();
                h = CreateMutexExW(sa, LegacyMutexName, 0, SYNCHRONIZE | MUTEX_MODIFY_STATE);
                openError = h.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
                if (!h.IsInvalid)
                    DeviceOrchestrator.LogDiag($"    ledger: {LegacyMutexName} refuses READ_CONTROL, opened without it, owner not read");
            }
            if (h.IsInvalid)
            {
                DeviceOrchestrator.LogDiag($"    ledger: {LegacyMutexName} did not open (Win32 error {openError}), not taken");
                h.Dispose();
                return;
            }
            _legacy = h;
            // A mutex this process made is owned by Administrators, from its
            // O:BA, and a v1.12.0 program's mutex by its creator's default
            // owner: SYSTEM for a service, Administrators for an elevated
            // program on most hosts, and the user where UAC makes the user
            // the owner (KB 947721). Another owner says only that this
            // release did not create the mutex, whether a squatter or such a
            // v1.12.0 program did. It is waited for, bounded: longer than
            // v1.12.0 holds the mutex (it holds it only while it writes a few
            // registry values), so a v1.12.0 hold within the bound still
            // serializes, and a squatter costs only the wait.
            bool administrative = ObjectOwner.IsOwnedByAdmin(h, allowLocalService: false);
            bool marked = s_legacyHeldByOther;
            uint full = administrative ? INFINITE
                : (uint)TimeoutScale.Apply(marked ? LegacyMarkedWaitMs : LegacyUnknownWaitMs);
            // A bounded take waits no longer than its caller allows.
            bool bounded = until != long.MaxValue;
            uint timeout = bounded ? (uint)Math.Min(full, Math.Max(0, until - Environment.TickCount64)) : full;
            uint r = WaitForSingleObject(h, timeout);
            _legacyHeld = r is WAIT_OBJECT_0 or WAIT_ABANDONED;
            if (GivesUp(_legacyHeld, timeout, full))
                throw new TimeoutException($"{LegacyMutexName} was held past the ledger lock's timeout.");
            if (administrative || _legacyHeld)
                s_legacyHeldByOther = false;
            else if (!marked && timeout == full)
                // Only a wait that ran its own full bound marks the mutex,
                // not one a caller's shorter bound cut.
                s_legacyHeldByOther = true;
            if (!administrative)
                DeviceOrchestrator.LogDiag($"    ledger: {LegacyMutexName} has no owner read as SYSTEM or Administrators, waited {timeout} ms, {(_legacyHeld ? "taken" : "still held")}");
        }
        finally
        {
            if (sa != IntPtr.Zero) Marshal.FreeHGlobal(sa);
            if (sd != IntPtr.Zero) LocalFree(sd);
        }
    }

    /// <summary>True when an acquisition gives up on the v1.12.0 mutex
    /// and throws: it does not hold it, and the caller's bound cut the wait
    /// short of the lock's own bound (<paramref name="timeout"/> below
    /// <paramref name="full"/>), whoever owns the mutex. A scope never
    /// writes without the mutex because its caller ran short of time. Only
    /// a wait that ran the lock's own bound for an owner other than SYSTEM
    /// or Administrators goes on without it, as a squatter's must.</summary>
    internal static bool GivesUp(bool held, uint timeout, uint full) => !held && timeout < full;

    /// <summary>Release both mutexes, the v1.12.0 one first.</summary>
    public void ReleaseMutex()
    {
        if (_legacyHeld && _legacy != null)
        {
            ReleaseMutexNative(_legacy);
            _legacyHeld = false;
        }
        if (_counted)
        {
            _counted = false;
            if (--t_depth == 0) t_withoutLegacy = false;
        }
        _lock.ReleaseMutex();
    }

    public void Dispose()
    {
        _legacy?.Dispose();
        _legacy = null;
        _lock.Dispose();
    }

    // SYSTEM and Administrators, the security a v1.12.0 program's elevated
    // default gives the same name. Owner Administrators (O:BA), so a mutex
    // this process creates reads as administrative whatever the default-owner
    // policy is.
    private const string LegacySddl = "O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)";
    // How long to wait for a legacy mutex whose owner is neither SYSTEM nor
    // Administrators. v1.12.0 holds it for a few registry writes: measured
    // over 23 holds across creates and disposes, p50 0.7 ms, p95 29 ms,
    // most 34 ms. The marked bound covers that with room to spare.
    private const int LegacyUnknownWaitMs = 5000;
    private const int LegacyMarkedWaitMs = 500;
    private const uint SYNCHRONIZE = 0x00100000;
    private const uint MUTEX_MODIFY_STATE = 0x0001;
    private const uint READ_CONTROL = 0x00020000;
    private const int ERROR_ACCESS_DENIED = 5;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint WAIT_OBJECT_0 = 0x00000000;
    private const uint WAIT_ABANDONED = 0x00000080;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeWaitHandle CreateMutexExW(IntPtr attributes, string name, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "ReleaseMutex", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseMutexNative(SafeWaitHandle handle);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision,
        out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);
}
