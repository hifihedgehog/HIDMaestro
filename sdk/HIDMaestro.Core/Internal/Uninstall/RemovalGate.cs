using System;
using System.Diagnostics;
using System.Threading;

namespace HIDMaestro.Internal.Uninstall;

/// <summary>
/// Holds a program back while another process removes HIDMaestro. A full
/// removal holds its machine-wide mutex (<see cref="Uninstaller"/>, in the
/// admin-only namespace) from its first look for running programs to its
/// last delete. A program's first call that touches the machine waits for
/// that mutex, takes it and lets it go at once, so a program that starts
/// during a removal claims nothing, creates nothing and deploys nothing
/// until the removal has ended, and then installs everything again. The
/// removal's censuses of running programs stay as the backstop for what no
/// gate covers: a v1.12.0 program, and a program already past its gate when
/// the removal began, which the first census finds.
///
/// <para>The gate is passed once per process: its first pass costs one
/// acquire and one release of the mutex, and every later call one read of a
/// field. It sits only where the SDK holds no lock of its own: the first
/// owner claim of a sweep (MachineClaims.TryEnsureOwner, which
/// InstallDriver and the first create run before they claim, deploy or take
/// <see cref="OwnerSweep.CreateGate"/>), the cleanup overloads, and the
/// public calls that write machine-wide state without a claim (OEM name
/// overrides, the OpenVR registration, the transport install). So a program
/// waiting here holds nothing the removal waits for: not CreateGate, not the
/// deploy, PnP, staging, transport or ledger lock. A process running a
/// removal itself (<see cref="RemovingHere"/>) never waits on its own
/// mutex: a thread of it already waiting stops within a quarter
/// second.</para>
/// </summary>
internal static class RemovalGate
{
    private static int s_passed;

    /// <summary>Set while this process runs a removal and holds its
    /// mutex.</summary>
    internal static volatile bool RemovingHere;

    /// <summary>How long a program waits for a removal before it goes on,
    /// logging that it did: a removal takes seconds, and one stuck past this
    /// is not waited for.</summary>
    private const int WaitBoundMs = 600_000;

    /// <summary>Wait, once per process, for a removal another process runs
    /// to end, then run <paramref name="underGate"/> while still holding the
    /// mutex: the first owner claim goes there, so a removal that starts
    /// right after it finds the program in its first census. Every later
    /// call runs <paramref name="underGate"/> at once.</summary>
    internal static void Pass(Action? underGate = null)
    {
        if (Volatile.Read(ref s_passed) != 0 || RemovingHere) { underGate?.Invoke(); return; }
        // A process without elevation can write nothing a removal takes,
        // and cannot open the admin-only namespace.
        if (!Environment.IsPrivilegedProcess) { Volatile.Write(ref s_passed, 1); underGate?.Invoke(); return; }
        Mutex m;
        try { m = new Mutex(false, AdminNamespace.Name(Uninstaller.UninstallMutexObject)); }
        catch (Exception e) when (e is UnauthorizedAccessException or InvalidOperationException or System.IO.IOException)
        {
            Volatile.Write(ref s_passed, 1);
            underGate?.Invoke();
            return;
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
            Volatile.Write(ref s_passed, 1);
            try { underGate?.Invoke(); }
            finally { if (got) m.ReleaseMutex(); }
        }
    }
}
