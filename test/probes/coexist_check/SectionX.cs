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

        var parts = new (char Part, Action Body)[]
        {
            ('1', SectionX1), ('2', SectionX2), ('3', SectionX3), ('4', SectionX4),
            ('5', SectionX5), ('6', SectionX6), ('7', SectionX7),
        };
        bool first = true;
        foreach (var (part, body) in parts)
        {
            if (!XPart(part)) continue;
            if (!first) Clean();
            first = false;
            body();
        }
    }

    /// <summary>Whether "--x-parts=" names this part, or is absent.</summary>
    static bool XPart(char part)
    {
        string? arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--x-parts=", StringComparison.Ordinal));
        return arg == null || arg.AsSpan("--x-parts=".Length).Contains(part);
    }

    /// <summary>The USB/IP loopback port ranges taken first. With the first
    /// range held, composite personas' servers take ports of the second
    /// range, which sits below the dynamic port range like the first. With
    /// both held they still create, on OS-assigned ports their owner
    /// records name. In each case two such programs are killed, and the
    /// vhci driver starts a re-attach attempt against each one's location
    /// (usbip-win2 device.cpp detach, start_attach_attempts). The probe
    /// cancels the second's attempt itself, which shows the driver started
    /// one, then another program sweeps, and the first's attempt must be
    /// gone. Section V part 8 covers the cleanup that removes everything in
    /// the OS-assigned state.</summary>
    static void SectionX7()
    {
        const int basePort = 18509, secondPort = 28509, range = 16;
        var held = new List<TcpListener>();
        try
        {
            held.AddRange(HoldPorts(basePort, range));
            Check($"X7: the probe holds the first USB/IP port range {basePort}..{basePort + range - 1}", held.Count == range, $"{held.Count} of {range} bound");
            if (held.Count != range) return;
            X7KillTwoAndSweep("second range", "coexist-x7s", port => port >= secondPort && port < secondPort + range);
            Clean();

            var second = HoldPorts(secondPort, range);
            held.AddRange(second);
            Check($"X7: the probe holds the second range {secondPort}..{secondPort + range - 1} too", second.Count == range, $"{second.Count} of {range} bound");
            if (second.Count != range) return;
            X7KillTwoAndSweep("OS-assigned", "coexist-x7",
                port => (port < basePort || port >= basePort + range) && (port < secondPort || port >= secondPort + range));
        }
        finally { foreach (var l in held) try { l.Stop(); } catch { } }
    }

    /// <summary>Two programs with a composite each on ports
    /// <paramref name="expected"/> accepts, both killed. The second one's
    /// re-attach attempt, canceled by the probe, is the positive control.
    /// After a sweep by a third program, the first one's attempt must be
    /// gone.</summary>
    static void X7KillTwoAndSweep(string label, string key, Func<int, bool> expected)
    {
        var locations = new List<(int Port, string BusId)>();
        var peers = new List<PeerProcess>();
        try
        {
            for (int k = 0; k < 2; k++)
            {
                var p = PeerProcess.Start($"X7-{label}-{k}");
                peers.Add(p);
                p.Send("install");
                string c = p.Send($"create composite - {key}{k}");
                string? recorded = Ok(c) ? Native.RecordValue(p.Pid, "UsbipPort") : null;
                bool onExpected = int.TryParse(recorded, out int port) && expected(port);
                Check($"X7 ({label}): program {k + 1} creates a composite on a port its owner record names",
                      Ok(c) && onExpected, $"{c}, recorded port {recorded ?? "none"}");
                if (!Ok(c) || !onExpected) return;
                locations.Add((port, "1-" + (int.Parse(PeerProcess.Field(c, "transport")!) + 1)));
            }
            foreach (var p in peers) p.Kill();
            foreach (var (port, _) in locations)
                WaitFor(() => !Vhci.Imported().Any(r => r.Port == port), 10_000);

            var (controlPort, controlBus) = locations[1];
            int control = Vhci.Stop("127.0.0.1", controlPort, controlBus);
            Check($"X7 ({label}): the driver started a re-attach attempt for a killed program ({controlPort}/{controlBus})",
                  control >= 1, $"{control} canceled by the probe");

            using (var s = PeerProcess.Start($"X7-{label}-sweeper"))
                Check($"X7 ({label}): another program runs a sweep", Ok(s.Send("sweep")));
            var (port0, bus0) = locations[0];
            int left = Vhci.Stop("127.0.0.1", port0, bus0);
            Check($"X7 ({label}): the sweep canceled the other killed program's attempt ({port0}/{bus0})",
                  left == 0, $"{left} canceled by the probe");
        }
        finally
        {
            foreach (var p in peers) p.Dispose();
            // Whatever the result, no attempt is left for later runs.
            foreach (var (port, busId) in locations) Vhci.Stop("127.0.0.1", port, busId);
        }
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
        Clean();

        // Every index squatted: the create is refused, and the refusal names
        // the cause the allocator found, not running programs or devices.
        const int all = 4096;
        var every = new List<string>(all);
        for (int i = 0; i < all; i++) every.Add($@"Global\HIDMaestroInputEvent{i}");
        sq = new Squatter(s => { foreach (var n in every) s.Event(n); });
        try
        {
            int made = every.Count(sq.Holds);
            Check($"X6: a program that is not elevated squats the input event of all {all} indices", made == all, $"{made} of {all}");
            using var p = PeerProcess.Start("X6-all");
            p.Send("install");
            string c = p.Send("create pad F0E9 -");
            Check("X6: with every index squatted the create is refused, and the refusal names the squatted objects",
                  !Ok(c) && c.Contains($@"{all} carry a Global\ object this release did not create"), c);
            // What was read of such an object is its owner. The refusal
            // names it, and names makers only as examples.
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
            Check("X6: the refusal names the owner it read, and makers only as examples",
                  c.Contains($"is owned by {user}") && c.Contains("such as one that is not elevated or one built on HIDMaestro 1.12.0 or older"), c);
        }
        finally { sq.Dispose(); }
        Check("X6: nothing the squatter made is left",
              every.All(n => Native.OpenEventW(Native.SYNCHRONIZE, false, n) == IntPtr.Zero));
        Clean();

        // Every index's input section name taken by a mutex: an object of
        // another kind, whose owner the allocator never reads. The refusal
        // says what it found and claims no owner.
        var sections = new List<string>(all);
        for (int i = 0; i < all; i++) sections.Add($@"Global\HIDMaestroInput{i}");
        sq = new Squatter(s => { foreach (var n in sections) s.Mutex(n, hold: false); });
        try
        {
            int made = sections.Count(sq.Holds);
            Check($"X6: a program that is not elevated takes the input section name of all {all} indices with a mutex", made == all, $"{made} of {all}");
            using var p = PeerProcess.Start("X6-kind");
            p.Send("install");
            string c = p.Send("create pad F0EA -");
            Check("X6: the refusal says the object is another kind and claims no owner it did not read",
                  !Ok(c) && c.Contains("is another kind of object") && !c.Contains("owned by"), c);
            // A v1.12.0 program makes no object of another kind, so the
            // refusal does not offer it as an example maker.
            Check("X6: with no owner read, the refusal does not name HIDMaestro 1.12.0 as a possible maker",
                  !Ok(c) && c.Contains("Another program made each.") && !c.Contains("1.12.0"), c);
        }
        finally { sq.Dispose(); }
        Check("X6: nothing the squatter made is left",
              sections.All(n => Native.OpenMutexW(Native.SYNCHRONIZE, false, n) == IntPtr.Zero));
    }

    /// <summary>The owner of the objects HIDMaestro creates, under the
    /// effect of the "default owner = Object creator" policy. The SDK sets
    /// O:BA in its security descriptors so the owner is Administrators
    /// whatever the policy, which the transport allocator and the ledger
    /// lock then read as HIDMaestro's own and not a squatter's.</summary>
    static void SectionX5()
    {
        // The descriptors the SDK under test creates its objects with, read
        // from its own constants, so a descriptor that lost its O:BA fails.
        var descriptors = new (string Type, string Field)[]
        {
            ("SharedMemoryIO", "Sddl"), ("SharedMemoryIO", "OwnerPipeSddl"), ("MachineClaims", "ClaimSddl"),
            ("LedgerLock", "LegacySddl"), ("AdminNamespace", "Sddl"),
            ("VrSharedMemory", "SectionSddl"), ("VrSharedMemory", "EventSddl"),
        };
        var sddls = new List<(string Name, string? Sddl)>();
        using (var p = PeerProcess.Start("X5"))
            foreach (var (type, field) in descriptors)
                sddls.Add(($"{type}.{field}", PeerProcess.Field(p.Send($"sddl {type} {field}"), "value")));

        // A DACL with no owner shows the policy's effect is reproduced.
        const string dacl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)";
        string user = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        string control = "?";
        var owners = new List<(string Name, string Owner)>();
        IntPtr token = Native.UserOwnerToken();
        var t = new Thread(() =>
        {
            if (!Native.SetThreadToken(IntPtr.Zero, token)) { control = $"SetThreadToken err {Marshal.GetLastWin32Error()}"; return; }
            try
            {
                control = Native.CreateAndReadOwner(dacl, @"Global\HIDMaestroX5Control");
                int k = 0;
                foreach (var (name, sddl) in sddls)
                    owners.Add((name, sddl == null ? "no descriptor read" : Native.CreateAndReadOwner(sddl, $@"Global\HIDMaestroX5Owner{k++}")));
            }
            finally { Native.RevertToSelf(); }
        });
        t.Start(); t.Join();
        Native.CloseHandle(token);
        Check("X5: the policy's effect is reproduced: without an owner in the descriptor the object is the user's",
              control == user, $"owner {control}, user {user}");
        var wrong = owners.Where(o => o.Owner != "S-1-5-32-544").ToList();
        Check($"X5: under that policy, an object made with each of the SDK's {sddls.Count} descriptors is owned by Administrators",
              owners.Count == sddls.Count && wrong.Count == 0,
              wrong.Count == 0 ? $"{owners.Count} checked" : string.Join(", ", wrong.Select(o => $"{o.Name}: {o.Owner}")));
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

    static long s_x2BaselineExitMs = -1;

    /// <summary>The eight pads' JoystickOem ledger entries that exist.</summary>
    static int ExitPadEntries()
    {
        using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroRegistryLedger\JoystickOem");
        if (k == null) return 0;
        var have = new HashSet<string>(k.GetSubKeyNames(), StringComparer.OrdinalIgnoreCase);
        return Enumerable.Range(0, 8).Count(i => have.Contains($"VID_BEEF&PID_{0xF0C0 + i:X4}"));
    }

    /// <summary>Start a program, have it sweep once (with the legacy name
    /// squatted, that runs the five-second wait out and marks the name),
    /// create eight pads, and exit without disposing them. How long the exit
    /// took, or -1 when a step failed, the eight ledger entries were not
    /// there before the exit, or the exit left any of them.</summary>
    static long ExitWithControllers(string label)
    {
        using var q = PeerProcess.Start(label);
        if (!Ok(q.Send("sweep", 60_000))) return -1;
        for (int i = 0; i < 8; i++)
            if (!Ok(q.Send($"create pad {0xF0C0 + i:X4} -"))) return -1;
        int before = ExitPadEntries();
        var clock = Stopwatch.StartNew();
        q.Post("quit");
        bool exited = WaitFor(() => q.Exited, 120_000);
        long ms = clock.ElapsedMilliseconds;
        int after = ExitPadEntries();
        Console.WriteLine($"    {label}: {before} ledger entries before the exit, {after} after, exit {ms} ms");
        return exited && before == 8 && after == 0 ? ms : -1;
    }

    /// <summary>The registry ledger's lock, which v1.12.0 programs take as
    /// Global\HIDMaestro-OEM-Recovery.</summary>
    static void SectionX2()
    {
        const string name = @"Global\HIDMaestro-OEM-Recovery";
        s_x2BaselineExitMs = ExitWithControllers("X2-exit");
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
                // The process paid the five-second wait once. Its next sweep
                // takes the ledger lock again and waits only the short bound
                // that covers a v1.12.0 write.
                clock.Restart();
                string again = p.Send("sweep", 60_000);
                long second = clock.ElapsedMilliseconds;
                Check("X2: the same program's next sweep waits the short bound, not five seconds again",
                      Ok(again) && second < 2000, $"first {first} ms, second {second} ms: {again}");

                // A program that exits with eight controllers releases each
                // one's ledger entries inside one ledger lock, so the exit
                // pays the short bound once, not once per controller.
                // Compared with the same exit before the squatter came,
                // which waited for nothing: once per controller adds about
                // 4 s (8 x 500 ms), once about 0.5 s.
                long squatted = ExitWithControllers("X2-exit-squatted");
                Check("X2: an exit with eight controllers waits the short bound once, not once per controller",
                      squatted >= 0 && s_x2BaselineExitMs >= 0 && squatted - s_x2BaselineExitMs < 2000,
                      $"exit {squatted} ms with the name squatted, {s_x2BaselineExitMs} ms before");

                // The squatter leaves, and a v1.12.0 program, which opened the
                // same object, writes under it. The object is still the
                // squatter's, owned by the user. Measured, v1.12.0 holds the
                // mutex 34 ms at most (p95 29 ms over 23 holds). The hold here
                // is 150 ms so the wait stands out from the sweep's own time.
                IntPtr legacy = Native.OpenMutexW(Native.SYNCHRONIZE | 0x0001 /* MUTEX_MODIFY_STATE */, false, name);
                sq.Dispose();
                bool holding = legacy != IntPtr.Zero && Native.WaitForSingleObject(legacy, 1000) is 0 or 0x80;
                Check("X2: after the squatter leaves, a writer holds its mutex as a v1.12.0 program would", holding,
                      holding ? "" : legacy == IntPtr.Zero ? "the mutex did not open" : "not taken");
                if (holding)
                {
                    // A sweep that answers while the writer still holds the
                    // mutex wrote beside it.
                    p.Post("sweep");
                    string during = p.Receive("sweep, while the writer holds the mutex", 150);
                    Native.ReleaseMutex(legacy);
                    bool waited = during.StartsWith("ERR timeout", StringComparison.Ordinal);
                    string third = waited ? p.Receive("sweep, once the writer let go", 60_000) : during;
                    Check("X2: the next sweep waits for that brief hold instead of writing beside it",
                          waited && Ok(third), waited ? third : $"answered while held: {during}");
                }
                if (legacy != IntPtr.Zero) Native.CloseHandle(legacy);
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
            // An elevated create under a prefix that does not exist fails
            // with error 5, as a claim would on a build without the
            // Administrators prefix. The refusal must state what was read,
            // error 5 with Administrators enabled, give a missing prefix as
            // one possible cause, and not ask for elevation the token has.
            // The type stays UnauthorizedAccessException, the one the
            // sweeps' TryEnsureOwner turns into a skipped sweep.
            string r = p.Send("claimrefused");
            Check("X3: an elevated create under a missing prefix is refused with error 5, stated as read",
                  Ok(r) && r.Contains("error=5 ") && r.Contains("type=UnauthorizedAccessException")
                  && r.Contains("Win32 error 5, though the token the call ran under has Administrators enabled")
                  && r.Contains("A Windows build without that pipe prefix would refuse a claim this way")
                  && !r.Contains("Run the program elevated") && !r.Contains("does not provide"), r);
            // The same error read on a thread that impersonates a program
            // that is not elevated must still ask for elevation.
            var refused = typeof(HIDMaestro.HMContext).Assembly.GetType("HIDMaestro.Internal.MachineClaims")
                ?.GetMethod("RefusedClaim", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            string medium = refused == null ? "no MachineClaims.RefusedClaim" : "?";
            if (refused != null)
            {
                var t = new Thread(() =>
                {
                    IntPtr token = Native.MediumToken();
                    try
                    {
                        if (!Native.SetThreadToken(IntPtr.Zero, token)) { medium = $"SetThreadToken error {Marshal.GetLastWin32Error()}"; return; }
                        try
                        {
                            var ex = (Exception)refused.Invoke(null, new object[] { 5 })!;
                            medium = $"{ex.GetType().Name}: {ex.Message}";
                        }
                        finally { Native.RevertToSelf(); }
                    }
                    finally { Native.CloseHandle(token); }
                });
                t.Start(); t.Join();
            }
            Check("X3: the same error under a token without Administrators enabled says so and asks for elevation",
                  medium.StartsWith(nameof(UnauthorizedAccessException) + ":", StringComparison.Ordinal)
                  && medium.Contains("does not have Administrators enabled") && medium.Contains("Run the program elevated"), medium);
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
