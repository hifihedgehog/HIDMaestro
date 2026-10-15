// X. Machine-wide names a program that is not elevated creates first,
// and names HIDMaestro creates whose owner tells them from a squatter.
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
using System.Net;
using System.Net.Sockets;
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
        Clean();
        SectionX4();
        Clean();
        SectionX5();
        Clean();
        SectionX6();
        Clean();
        SectionX7();
    }

    /// <summary>The USB/IP loopback port ranges taken first. With the first
    /// range held, a composite persona's server takes a port of the second
    /// range, which sits below the dynamic port range like the first. With
    /// both held it still creates, on an OS-assigned port its owner record
    /// names. That program is then killed, and the vhci driver starts a
    /// re-attach attempt against the port (usbip-win2 device.cpp detach,
    /// start_attach_attempts). A sweep by another program must cancel it:
    /// the record is the only thing that names the port, and the sweep
    /// deletes it. Section V part 8 covers the cleanup that removes
    /// everything in the same state.</summary>
    static void SectionX7()
    {
        const int basePort = 18509, secondPort = 28509, range = 16;
        var held = new List<TcpListener>();
        try
        {
            held.AddRange(HoldPorts(basePort, range));
            Check($"X7: the probe holds the first USB/IP port range {basePort}..{basePort + range - 1}", held.Count == range, $"{held.Count} of {range} bound");
            if (held.Count != range) return;
            using (var p = PeerProcess.Start("X7-second"))
            {
                p.Send("install");
                string c = p.Send("create composite - coexist-x7s");
                string? recorded = Ok(c) ? Native.RecordValue(p.Pid, "UsbipPort") : null;
                bool inSecond = int.TryParse(recorded, out int sp) && sp >= secondPort && sp < secondPort + range;
                Check($"X7: with the first range held, a composite's server takes a port of {secondPort}..{secondPort + range - 1}", inSecond,
                      $"{c}, recorded port {recorded ?? "none"}");
            }
            Clean();

            var second = HoldPorts(secondPort, range);
            held.AddRange(second);
            Check($"X7: the probe holds the second range {secondPort}..{secondPort + range - 1} too", second.Count == range, $"{second.Count} of {range} bound");
            if (second.Count != range) return;
            int port = 0;
            string busId = "";
            using (var a = PeerProcess.Start("X7-killed"))
            {
                a.Send("install");
                string c = a.Send("create composite - coexist-x7");
                Check("X7: a composite persona still creates with both ranges taken", Ok(c), c);
                if (!Ok(c)) return;
                busId = "1-" + (int.Parse(PeerProcess.Field(c, "transport")!) + 1);
                string? recorded = Native.RecordValue(a.Pid, "UsbipPort");
                bool outside = int.TryParse(recorded, out port)
                    && (port < basePort || port >= basePort + range) && (port < secondPort || port >= secondPort + range);
                Check("X7: its server bound a port outside both ranges, recorded in its owner record", outside, $"recorded port {recorded ?? "none"}");
                if (!outside) return;
                a.Kill();
            }
            WaitFor(() => !Vhci.Imported().Any(r => r.Port == port), 10_000);
            using (var b = PeerProcess.Start("X7-sweeper"))
                Check("X7: another program runs a sweep", Ok(b.Send("sweep")));
            int left = Vhci.Stop("127.0.0.1", port, busId);
            Check($"X7: the sweep canceled the killed program's attempt for {port}/{busId}", left == 0, $"{left} canceled by the probe");
        }
        finally { foreach (var l in held) try { l.Stop(); } catch { } }
    }

    static List<TcpListener> HoldPorts(int first, int count)
    {
        var list = new List<TcpListener>();
        for (int i = 0; i < count; i++)
        {
            try { var l = new TcpListener(IPAddress.Loopback, first + i); l.Start(); list.Add(l); }
            catch (SocketException) { }
        }
        return list;
    }

    /// <summary>Many indices squatted at once. The create walks past them
    /// onto a free one, and when every index is taken the refusal names the
    /// squatter as the cause.</summary>
    static void SectionX6()
    {
        var names = new List<string>();
        for (int i = 0; i < 4; i++) names.Add($@"Global\HIDMaestroInputEvent{i}");
        var sq = new Squatter(s => { foreach (var n in names) s.Event(n); });
        try
        {
            Check("X6: a program that is not elevated squats the input event of indices 0 to 3",
                  names.All(sq.Holds), sq.Describe());
            using var p = PeerProcess.Start("X6");
            p.Send("install");
            string c = p.Send("create pad F0E8 -");
            int transport = int.TryParse(PeerProcess.Field(c, "transport"), out int tv) ? tv : -1;
            Check("X6: a keyless create walks past the squatted indices onto a free one",
                  Ok(c) && transport >= 4, c);
        }
        finally { sq.Dispose(); }
        Check("X6: nothing the squatter made is left",
              names.All(n => Native.OpenEventW(Native.SYNCHRONIZE, false, n) == IntPtr.Zero));
    }

    /// <summary>The owner of the objects HIDMaestro creates, under the
    /// effect of the "default owner = Object creator" policy. The SDK sets
    /// O:BA in its security descriptors so the owner is Administrators
    /// whatever the policy, which the transport allocator and the ledger
    /// lock then read as HIDMaestro's own and not a squatter's.</summary>
    static void SectionX5()
    {
        // The DACL the SDK's sections and events carry, with and without the
        // owner it now also sets.
        const string dacl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;LS)(A;;GR;;;WD)";
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        string noOwner = "?", withOwner = "?";
        IntPtr token = Native.UserOwnerToken();
        var t = new Thread(() =>
        {
            if (!Native.SetThreadToken(IntPtr.Zero, token)) { noOwner = $"SetThreadToken err {Marshal.GetLastWin32Error()}"; return; }
            try
            {
                noOwner = Native.CreateAndReadOwner(dacl, @"Global\HIDMaestroX5NoOwner");
                withOwner = Native.CreateAndReadOwner("O:BA" + dacl, @"Global\HIDMaestroX5Owner");
            }
            finally { Native.RevertToSelf(); }
        });
        t.Start(); t.Join();
        Native.CloseHandle(token);
        Check("X5: the policy's effect is reproduced: without an owner in the descriptor the object is the user's",
              noOwner == user, $"owner {noOwner}, user {user}");
        Check("X5: with O:BA, as the SDK sets, the object is owned by Administrators whatever the policy",
              withOwner == "S-1-5-32-544", $"owner {withOwner}");
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
            var clock = Stopwatch.StartNew();
            p.Post("sweep");
            string r = p.Receive("sweep", 60_000);
            long first = clock.ElapsedMilliseconds;
            Check($"X2: a sweep, which takes the ledger lock, finishes while it holds {name}", Ok(r), $"{first} ms: {r}");
            if (!Ok(r))
            {
                sq.Dispose();
                p.Receive("sweep, once the name is let go", 120_000);
            }
            else
            {
                // The process paid the bounded wait once. Its next sweep takes
                // the ledger lock again, and must not wait on the squatter.
                clock.Restart();
                string again = p.Send("sweep", 60_000);
                long second = clock.ElapsedMilliseconds;
                Check("X2: the same program's next sweep does not wait on the squatter again",
                      Ok(again) && second < 2000, $"first {first} ms, second {second} ms: {again}");
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

    /// <summary>A transport index's events and sections.</summary>
    static void SectionX4()
    {
        // An event the driver would wait on, created first with a DACL that
        // lets its creator wait on it too.
        const string inputEvent = @"Global\HIDMaestroInputEvent0";
        var sq = new Squatter(s => s.Event(inputEvent));
        try
        {
            Check($"X4: a program that is not elevated creates {inputEvent}", sq.Holds(inputEvent), sq.Describe());
            using var p = PeerProcess.Start("X4a");
            p.Send("install");
            string c = p.Send("create pad F0E5 -");
            Check("X4: a keyless create succeeds and leaves transport index 0", Ok(c) && PeerProcess.Field(c, "transport") != "0", c);
            if (Ok(c))
            {
                p.Send("stream 0 0.5 125 0");
                int taken = 0;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 1000)
                    if (Native.WaitForSingleObject(sq.Handle(inputEvent), 50) == 0) taken++;
                p.Send("stopstream");
                Check("X4: the squatter's event carries none of the controller's input signals", taken == 0, $"it took {taken} in 1 s");
            }
        }
        finally { sq.Dispose(); }
        Check("X4: nothing the squatter made is left", Native.OpenEventW(Native.SYNCHRONIZE, false, inputEvent) == IntPtr.Zero);
        Clean();

        // A section's name taken by another kind of object, which a program
        // that cannot create a Global\ section can still do.
        const string inputSection = @"Global\HIDMaestroInput0";
        sq = new Squatter(s => s.Mutex(inputSection, hold: false));
        try
        {
            Check($"X4: a program that is not elevated creates a mutex named {inputSection}", sq.Holds(inputSection), sq.Describe());
            using var p = PeerProcess.Start("X4b");
            p.Send("install");
            string c = p.Send("create pad F0E6 -");
            Check("X4: a keyless create succeeds on another transport index", Ok(c) && PeerProcess.Field(c, "transport") != "0", c);
        }
        finally { sq.Dispose(); }
        Check("X4: nothing the squatter made is left", Native.OpenMutexW(Native.SYNCHRONIZE, false, inputSection) == IntPtr.Zero);
        Clean();

        // A live controller's own objects, made by the SDK and the driver,
        // all have an owner the transport allocator accepts, and stay
        // acceptable once the controller is gone.
        using (var p = PeerProcess.Start("X4c"))
        {
            p.Send("install");
            string c = p.Send("create pad F0E7 -");
            string? t = PeerProcess.Field(c, "transport");
            Check("X4: a keyless create on a clean machine takes transport index 0", Ok(c) && t == "0", c);
            if (!Ok(c) || t == null) return;
            p.Send("stream 0 0.5 125 0");
            Thread.Sleep(500);
            var owners = new List<string>();
            bool all = true;
            foreach (var (n, section) in IndexObjects(int.Parse(t)))
            {
                string o = Native.OwnerOf(n, section);
                owners.Add($"{n.Substring(7)} {o}");
                if (o != "absent" && !Native.AdministrativeSid(o)) all = false;
            }
            Check("X4: every object of a live controller is owned by SYSTEM, Administrators or LocalService", all, string.Join(", ", owners));
            p.Send("stopstream");
            p.Send("dispose");
            string again = p.Send("create pad F0E7 -");
            Check("X4: the next keyless create gets transport index 0 back", Ok(again) && PeerProcess.Field(again, "transport") == t, again);
        }
    }

    static IEnumerable<(string Name, bool Section)> IndexObjects(int index)
    {
        foreach (string s in new[] { "Input", "InputAck", "Output", "PidState", "Version" })
            yield return ($@"Global\HIDMaestro{s}{index}", true);
        foreach (string e in new[] { "InputEvent", "CompanionInputEvent", "InputAckEvent", "OutputEvent", "StopEvent" })
            yield return ($@"Global\HIDMaestro{e}{index}", false);
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

        public void Event(string name)
        {
            IntPtr sa = Native.Sa(OpenToAll);
            IntPtr h = Native.CreateEventW(sa, false, false, name);
            _errors[name] = h == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
            Native.FreeSa(sa);
            if (h != IntPtr.Zero) _handles[name] = h;
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

        public IntPtr Handle(string name) => _handles.TryGetValue(name, out IntPtr h) ? h : IntPtr.Zero;

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
