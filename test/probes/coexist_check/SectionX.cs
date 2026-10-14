// X. Machine-wide names a program that is not elevated creates first.
//
// Any local program can create a Global\ mutex or event, or a pipe, under
// any name no one holds yet, and hold it. The probe plays that program with
// one of its own threads, which impersonates this user's token as a program
// that is not elevated runs (Native.MediumToken): Administrators deny-only,
// medium integrity. No process is started for it, and everything it made is
// closed before the section ends.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

internal static partial class Program
{
    static void SectionX()
    {
        // The SDK's namespace exists, as it does once any program on this
        // SDK has taken a lock, so the squatter's open below meets it.
        using (Native.AdminMutex("Deploy")) { }

        SectionX1();
        Clean();
        SectionX2();
        Clean();
        SectionX3();
    }

    /// <summary>The deploy lock.</summary>
    static void SectionX1()
    {
        const string name = @"Global\HIDMaestro.Deploy";
        using var p = PeerProcess.Start("X1");
        Check("X1: the program installs", Ok(p.Send("install")));
        (int Create, int Open) ns = (-1, -1);
        var sq = new Squatter(s =>
        {
            s.Mutex(name, hold: true);
            ns = Native.TryAdminNamespace();
        });
        try
        {
            Check($"X1: a program that is not elevated creates and holds {name}", sq.Holds(name), sq.Describe());
            Check("X1: it can neither create nor open the namespace the deploy lock lives in",
                  ns == (5, 5), $"create error {ns.Create}, open error {ns.Open}");
            p.Post("create pad F0E1 -");
            string c = p.Receive("create", 20_000);
            Check($"X1: a create, which takes the deploy lock, finishes while it holds {name}", Ok(c), c);
            if (!Ok(c))
            {
                sq.Dispose();
                p.Receive("create, once the name is let go", 120_000);
            }
        }
        finally { sq.Dispose(); }
        Check("X1: nothing the squatter made is left", Native.OpenMutexW(Native.SYNCHRONIZE, false, name) == IntPtr.Zero);
    }

    /// <summary>The registry ledger's lock, which v1.12.0 programs take as
    /// Global\HIDMaestro-OEM-Recovery.</summary>
    static void SectionX2()
    {
        const string name = @"Global\HIDMaestro-OEM-Recovery";
        using var p = PeerProcess.Start("X2");
        var sq = new Squatter(s => s.Mutex(name, hold: true));
        try
        {
            Check($"X2: a program that is not elevated creates and holds {name}", sq.Holds(name), sq.Describe());
            p.Post("sweep");
            string r = p.Receive("sweep", 20_000);
            Check($"X2: a sweep, which takes the ledger lock, finishes while it holds {name}", Ok(r), r);
            if (!Ok(r))
            {
                sq.Dispose();
                p.Receive("sweep, once the name is let go", 120_000);
            }
        }
        finally { sq.Dispose(); }
        Check("X2: nothing the squatter made is left", Native.OpenMutexW(Native.SYNCHRONIZE, false, name) == IntPtr.Zero);

        // An elevated program holding the name, as a v1.12.0 program does
        // while it writes OEM keys, is still waited for.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            IntPtr sa = Native.Sa("D:P(A;;GA;;;SY)(A;;GA;;;BA)");
            IntPtr h = Native.CreateMutexW(sa, false, name);
            Native.FreeSa(sa);
            if (h == IntPtr.Zero) { held.Set(); return; }
            Native.WaitForSingleObject(h, 0xFFFFFFFF);
            held.Set();
            release.Wait(15_000);
            Native.ReleaseMutex(h);
            Native.CloseHandle(h);
        }) { IsBackground = true };
        holder.Start();
        held.Wait(10_000);
        var sw = Stopwatch.StartNew();
        p.Post("sweep");
        Thread.Sleep(3000);
        release.Set();
        string r2 = p.Receive("sweep", 30_000);
        long ms = sw.ElapsedMilliseconds;
        holder.Join(5000);
        Check($"X2: a sweep still waits while an elevated program holds {name}", Ok(r2) && ms >= 2500, $"{ms} ms: {r2}");
    }

    /// <summary>The claim pipes.</summary>
    static void SectionX3()
    {
        const string key = "coexist-x3";
        string token = IdentityToken(key);
        string index0 = "HIDMaestro.Index.0";
        string identity = $"HIDMaestro.Identity.{token}";
        var sq = new Squatter(s =>
        {
            s.Pipe(index0);
            s.Pipe(identity);
            s.Pipe(Native.ClaimPrefix + index0);
            s.Pipe(Native.ClaimPrefix + identity);
        });
        try
        {
            Check("X3: a program that is not elevated creates the claim names without the prefix",
                  sq.Holds(index0) && sq.Holds(identity), sq.Describe());
            Check("X3: it cannot create them under the claim prefix",
                  sq.Error(Native.ClaimPrefix + index0) == 5 && sq.Error(Native.ClaimPrefix + identity) == 5, sq.Describe());
            using var p = PeerProcess.Start("X3");
            p.Send("install");
            string c1 = p.Send("create pad F0E3 -");
            Check("X3: a keyless create takes transport index 0", Ok(c1) && PeerProcess.Field(c1, "transport") == "0", c1);
            string c2 = p.Send($"create pad F0E4 {key}");
            Check($"X3: a create of the key whose identity name it holds succeeds", Ok(c2), c2);
        }
        finally { sq.Dispose(); }
        Check("X3: nothing the squatter made is left", !Native.PipeExists(index0) && !Native.PipeExists(identity));
    }

    /// <summary>DeviceIdentity's token for a key: HM_ and the first eight
    /// bytes of the key's tagged SHA-256, big-endian.</summary>
    static string IdentityToken(string key)
    {
        byte[] d = SHA256.HashData(Encoding.UTF8.GetBytes("HIDMaestro.DeviceIdentity.v1\n" + key));
        return "HM_" + Convert.ToHexString(d, 0, 8);
    }

    /// <summary>A program that is not elevated, played by one thread of
    /// this probe. The setup runs under <see cref="Native.MediumToken"/>.
    /// The thread then reverts to the probe's own token and keeps what it
    /// made, mutexes it took included, until <see cref="Dispose"/>, which
    /// releases and closes everything and ends the thread.</summary>
    sealed class Squatter : IDisposable
    {
        readonly Thread _thread;
        readonly ManualResetEventSlim _ready = new();
        readonly ManualResetEventSlim _release = new();
        readonly Dictionary<string, IntPtr> _handles = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, SafeFileHandle> _pipes = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> _errors = new(StringComparer.OrdinalIgnoreCase);
        readonly List<IntPtr> _owned = new();
        readonly HashSet<string> _toHold = new(StringComparer.OrdinalIgnoreCase);
        Exception? _failure;
        bool _disposed;

        public Squatter(Action<Squatter> setup)
        {
            _thread = new Thread(() =>
            {
                IntPtr token = IntPtr.Zero;
                try
                {
                    token = Native.MediumToken();
                    if (!Native.SetThreadToken(IntPtr.Zero, token)) throw new System.ComponentModel.Win32Exception();
                    try { setup(this); }
                    finally { Native.RevertToSelf(); }
                }
                catch (Exception ex) { _failure = ex; }
                finally
                {
                    if (token != IntPtr.Zero) Native.CloseHandle(token);
                    _ready.Set();
                }
                _release.Wait();
                foreach (IntPtr h in _owned) Native.ReleaseMutex(h);
                foreach (IntPtr h in _handles.Values) Native.CloseHandle(h);
                foreach (var p in _pipes.Values) p.Dispose();
            }) { IsBackground = true, Name = "squatter" };
            _thread.Start();
            _ready.Wait();
        }

        const string OpenToAll = "D:(A;;GA;;;WD)";

        public void Mutex(string name, bool hold)
        {
            IntPtr sa = Native.Sa(OpenToAll);
            IntPtr h = Native.CreateMutexW(sa, false, name);
            _errors[name] = h == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            Native.FreeSa(sa);
            if (h == IntPtr.Zero) return;
            _handles[name] = h;
            if (!hold) return;
            _toHold.Add(name);
            if (Native.WaitForSingleObject(h, 0) == 0) _owned.Add(h);
        }

        public void Pipe(string name)
        {
            var h = Native.CreatePipe(name, out int error);
            _errors[name] = error;
            if (h != null) _pipes[name] = h;
        }

        /// <summary>Whether the squatter created the name, and for a mutex
        /// it was asked to hold, holds it.</summary>
        public bool Holds(string name) =>
            _failure == null && (_pipes.ContainsKey(name)
                || (_handles.TryGetValue(name, out IntPtr h) && (!_toHold.Contains(name) || _owned.Contains(h))));

        public int Error(string name) => _errors.TryGetValue(name, out int e) ? e : -1;

        public string Describe() =>
            _failure != null ? $"{_failure.GetType().Name}: {_failure.Message}"
            : string.Join(", ", _errors.Select(e => $"{e.Key} {(e.Value == 0 ? "created" : $"error {e.Value}")}")) +
              (_owned.Count > 0 ? $", {_owned.Count} held" : "");

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _release.Set();
            _thread.Join(10_000);
            _ready.Dispose();
            _release.Dispose();
        }
    }
}
