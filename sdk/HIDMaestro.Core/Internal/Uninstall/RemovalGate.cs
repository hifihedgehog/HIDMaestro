using System;
using System.Diagnostics;
using System.Threading;

namespace HIDMaestro.Internal.Uninstall;

/// <summary>
/// Holds a program's first owner claim back while another process removes
/// HIDMaestro. A full removal holds its machine-wide mutex
/// (<see cref="Uninstaller"/>, in the admin-only namespace) from its first
/// look for running programs to its last delete. A process without its
/// owner claim waits for that mutex and takes the claim while holding it, so
/// a program that starts during a removal claims nothing, creates nothing and
/// deploys nothing until the removal has ended, and a removal that starts
/// after the claim finds the program in its first census. The removal's
/// censuses stay as the backstop for a program on v1.12.0 or 2.0.0, which has
/// no gate.
///
/// <para>Only <see cref="MachineClaims.TryEnsureOwner"/> comes here. Every
/// public call that writes machine-wide state calls it first, holding no
/// lock of the SDK's: InstallDriver and every create (through the session
/// sweep, the threads that lose its race included), the cleanup overloads,
/// the prewarm before it stages, OEM name overrides, the OpenVR registration
/// and the transport install. So a program waiting here holds nothing the
/// removal waits for: not CreateGate, not the deploy, PnP, staging, transport
/// or ledger lock.</para>
///
/// <para>Once the process holds its claim the gate costs one field read. The
/// first claim costs one acquire and one release of the mutex. One thread of
/// the process at a time touches the mutex, since a mutex is owned by a
/// thread: another thread of the same process would otherwise find it taken
/// and wait as if behind a removal. A process running a removal itself
/// (<see cref="RemovingHere"/>) never waits on its own mutex and takes no
/// claim while it runs: a thread of it already waiting stops within a
/// quarter second.</para>
/// </summary>
internal static class RemovalGate
{
    private static readonly SemaphoreSlim s_oneThread = new(1, 1);

    /// <summary>Set while this process runs a removal and holds its
    /// mutex.</summary>
    internal static volatile bool RemovingHere;

    /// <summary>How long a program waits for a removal before it goes on,
    /// logging that it did: a removal takes seconds, and one stuck past this
    /// is not waited for.</summary>
    private const int WaitBoundMs = 600_000;

    /// <summary>Run <paramref name="claim"/>, the owner claim, once no other
    /// process is removing HIDMaestro, holding the removal's mutex while it
    /// runs. False, with no claim taken, while this process runs a removal
    /// itself. A process that holds its claim runs it at once, and so does
    /// one without elevation, whose claim is refused.</summary>
    internal static bool Claim(Action claim)
    {
        if (MachineClaims.HoldsOwnerClaim || !Environment.IsPrivilegedProcess) { claim(); return true; }
        if (!s_oneThread.Wait(0))
        {
            DeviceOrchestrator.LogDiag("removal gate: another thread of this program is at the gate, and this one waits behind it");
            s_oneThread.Wait();
        }
        try
        {
            if (MachineClaims.HoldsOwnerClaim) { claim(); return true; }
            if (RemovingHere) return false;
            Mutex m;
            try { m = new Mutex(false, AdminNamespace.Name(Uninstaller.UninstallMutexObject)); }
            catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or System.IO.IOException)
            {
                claim();
                return true;
            }
            using (m)
            {
                var clock = Stopwatch.StartNew();
                bool got = false, waited = false;
                try
                {
                    if (!(got = m.WaitOne(0)))
                    {
                        waited = true;
                        DeviceOrchestrator.LogDiag("removal gate: another process is removing HIDMaestro, and this program waits for it to end");
                        while (!(got = m.WaitOne(250)))
                        {
                            if (RemovingHere) break;
                            if (clock.ElapsedMilliseconds >= TimeoutScale.Apply(WaitBoundMs))
                            {
                                DeviceOrchestrator.LogDiag("removal gate: another process's removal held its lock for ten minutes, so this program goes on");
                                break;
                            }
                        }
                    }
                }
                catch (AbandonedMutexException) { got = true; }
                if (waited)
                    DeviceOrchestrator.LogDiag($"removal gate: waited {clock.ElapsedMilliseconds} ms for another process's removal of HIDMaestro");
                try
                {
                    if (!got && RemovingHere) return false;
                    claim();
                    return true;
                }
                finally { if (got) m.ReleaseMutex(); }
            }
        }
        finally { s_oneThread.Release(); }
    }
}
