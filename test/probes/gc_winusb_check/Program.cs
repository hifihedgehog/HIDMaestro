// S81: WinUSB goes on one virtual adapter and no real one, and the binding
// goes when the program that made it does.
//
// BindWinUsb installs a per-profile package whose INF matches the adapter
// only through its vendor-scoped compatible ID behind fifteen IDs nothing
// carries, so Windows ranks input.inf above it and never picks it on its
// own, and binds it with DiInstallDevice on the one devnode. The registry
// ledger records the binding and puts the devnode back when the controller
// goes or, after a crash, at the next program's reclaim: a present devnode
// is installed again by rank, and a gone one gets the reinstall flag, so its
// next arrival installs by rank. The bound devnode carries no flag, since
// libusb skips a device whose install state the flag makes nonzero.
//
// Checked here:
//  - the binding, the package's rank against input.inf's, no flag on the
//    bound devnode, and AlreadyBound on a second call.
//  - a second adapter beside a bound one stays on input.inf until it is
//    bound itself.
//  - dispose leaves no ledger hold, and the gone devnode gets the flag.
//  - five create-bind-dispose cycles, each binding on the first call.
//  - a program killed while bound: an adapter that arrives on the same
//    instance before any reclaim finds WinUSB, as after Zadig, and the
//    reclaim puts it back on input.inf. When the reclaim comes first, the
//    arrival gets input.inf.
//
// Requires elevation. Exit 0 PASS, 1 FAIL, 2 when another program that embeds
// HIDMaestro is running.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    const uint CONFIGFLAG_REINSTALL = 0x20;

    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "child") return Child();

        Console.WriteLine("=== S81: the WinUSB binding of the GameCube adapter persona ===");
        if (Probe.OtherSdkRunning()) return 2;

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var gc = ctx.GetProfile("gamecube-adapter")!;

        BindAndRank(ctx, gc);
        Cycles(ctx, gc);
        Crash(ctx, gc, reclaimFirst: false);
        Crash(ctx, gc, reclaimFirst: true);

        Console.WriteLine("-- afterward");
        Probe.Check("no WinUSB hold left in the ledger", RegistryLedger.HeldWinUsbDevnodes().Count == 0,
            string.Join(", ", RegistryLedger.HeldWinUsbDevnodes()));
        Probe.Check("no adapter left present", Probe.WaitFor(() => Devnodes.PresentAdapters().Count == 0, 10_000),
            string.Join(", ", Devnodes.PresentAdapters()));
        return Probe.Finish("S81");
    }

    // ── One binding, its rank and a second adapter ──────────────────────

    static void BindAndRank(HMContext ctx, HMProfile gc)
    {
        Console.WriteLine("-- the binding");
        string? first = null, second = null;
        using (var a = ctx.CreateController(gc))
        {
            first = Arrived(a);
            Probe.Check("the adapter arrives", first != null, first ?? "");
            if (first == null) return;
            var bound = a.BindWinUsb();
            Probe.Check("BindWinUsb binds WinUSB", bound == HMWinUsbBinding.Bound, $"{bound}");
            Probe.Check("the devnode runs WinUSB", Probe.WaitFor(() => Devnodes.Service(first) == "WINUSB" && Devnodes.Status(first) is { Problem: 0 }, 5000),
                $"{Devnodes.Service(first)}");
            Probe.Check("it carries no reinstall flag, which libusb would read as an install problem",
                ((Devnodes.ConfigFlags(first) ?? 0) & CONFIGFLAG_REINSTALL) == 0,
                $"ConfigFlags 0x{Devnodes.ConfigFlags(first) ?? 0:X}");
            Probe.Check("the ledger holds it", RegistryLedger.HeldWinUsbDevnodes().Contains(first, StringComparer.OrdinalIgnoreCase));

            var ranks = DriverRanks(first);
            Probe.Check("input.inf matches it and ranks 0x00FF3205, best",
                ranks.TryGetValue("input.inf", out var input) && input.Rank == 0x00FF3205 && input.Status.Contains("Best Ranked"),
                Describe(ranks));
            var ours = ranks.Values.FirstOrDefault(r => r.Original.StartsWith("hmwinusb_", StringComparison.OrdinalIgnoreCase));
            Probe.Check("HIDMaestro's package matches through the vendor-scoped compatible ID at rank 0x00FF3F00, outranked",
                ours.Original == "hmwinusb_gamecube-adapter.inf" && ours.Rank == 0x00FF3F00 && ours.Status.Contains("Outranked"),
                Describe(ranks));

            Probe.Check("a second BindWinUsb finds it already bound", a.BindWinUsb() == HMWinUsbBinding.AlreadyBound);

            using (var b = ctx.CreateController(gc))
            {
                second = Arrived(b, except: first);
                Probe.Check("a second adapter arrives on a port-named instance", second != null && !second.Equals(first, StringComparison.OrdinalIgnoreCase),
                    second ?? "");
                if (second != null)
                {
                    Probe.WaitFor(() => Devnodes.Status(second) is { Problem: 10 }, 10_000);
                    Probe.Check("it gets input.inf, as a real adapter would, while the first stays on WinUSB",
                        Devnodes.Service(second) == "HidUsb" && Devnodes.Service(first) == "WINUSB",
                        $"{Devnodes.Service(second)} / {Devnodes.Service(first)}");
                    var bound2 = b.BindWinUsb();
                    Probe.Check("binding the second takes its devnode alone", bound2 == HMWinUsbBinding.Bound
                        && Devnodes.Service(second) == "WINUSB" && Devnodes.Service(first) == "WINUSB", $"{bound2}");
                }
            }
            if (second != null)
                Probe.Check("the second's hold goes with it", Probe.WaitFor(
                    () => !RegistryLedger.HeldWinUsbDevnodes().Contains(second, StringComparer.OrdinalIgnoreCase), 5000));
        }
        Console.WriteLine("-- dispose");
        Probe.Check("the first's hold goes with it",
            !RegistryLedger.HeldWinUsbDevnodes().Contains(first!, StringComparer.OrdinalIgnoreCase));
        Probe.Check("its devnode leaves", Probe.WaitFor(() => !Devnodes.IsPresent(first!), 10_000));
        Probe.Check("and the restore gives the gone devnode the reinstall flag for its next arrival",
            ((Devnodes.ConfigFlags(first!) ?? 0) & CONFIGFLAG_REINSTALL) != 0,
            $"ConfigFlags 0x{Devnodes.ConfigFlags(first!) ?? 0:X}");
    }

    // ── Repeated cycles ─────────────────────────────────────────────────

    static void Cycles(HMContext ctx, HMProfile gc)
    {
        Console.WriteLine("-- five create, bind, dispose cycles");
        var results = new List<string>();
        for (int i = 0; i < 5; i++)
        {
            using var c = ctx.CreateController(gc);
            string? id = Arrived(c);
            if (id == null) { results.Add("absent"); continue; }
            try { results.Add(c.BindWinUsb().ToString()); }
            catch (Exception ex) { results.Add($"{ex.GetType().Name}: {ex.Message}"); }
            Probe.WaitFor(() => Devnodes.Service(id) == "WINUSB", 5000);
        }
        Probe.Check("every cycle binds on the first call", results.All(r => r == "Bound"), string.Join(", ", results));
        Probe.Check("no hold left after them", RegistryLedger.HeldWinUsbDevnodes().Count == 0);
    }

    // ── A killed owner ──────────────────────────────────────────────────

    static void Crash(HMContext ctx, HMProfile gc, bool reclaimFirst)
    {
        Console.WriteLine(reclaimFirst
            ? "-- a program killed while bound, then a reclaim before the next arrival"
            : "-- a program killed while bound, and an arrival before any reclaim");
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "child")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        string? line = null;
        var reader = new Thread(() => { try { line = child.StandardOutput.ReadLine(); } catch { } }) { IsBackground = true };
        reader.Start();
        reader.Join(60_000);
        var m = line == null ? null : Regex.Match(line, "^bound (\\w+) (.+)$");
        Probe.Check("the child binds WinUSB", m is { Success: true } && m.Groups[1].Value == "Bound", line ?? "no answer");
        if (m is not { Success: true })
        {
            try { child.Kill(); } catch { }
            return;
        }
        string id = m.Groups[2].Value;
        child.Kill();
        child.WaitForExit(10_000);
        Probe.Check("the killed child's adapter leaves", Probe.WaitFor(() => !Devnodes.IsPresent(id), 15_000));
        Probe.Check("the gone devnode still names WinUSB", Devnodes.Service(id, phantom: true) == "WINUSB",
            $"{Devnodes.Service(id, phantom: true)}");
        Probe.Check("the ledger still holds it for the next reclaim",
            RegistryLedger.HeldWinUsbDevnodes().Contains(id, StringComparer.OrdinalIgnoreCase));

        if (reclaimFirst)
        {
            // The next program's reclaim: the child's hold is dead, so the
            // gone devnode gets the reinstall flag.
            RegistryLedger.PruneDead();
            Probe.Check("the reclaim drops the dead hold", !RegistryLedger.HeldWinUsbDevnodes().Contains(id, StringComparer.OrdinalIgnoreCase));
            Probe.Check("and flags the gone devnode for reinstall", ((Devnodes.ConfigFlags(id) ?? 0) & CONFIGFLAG_REINSTALL) != 0,
                $"ConfigFlags 0x{Devnodes.ConfigFlags(id) ?? 0:X}");
        }

        // A stand-in for a real adapter: same serial, so the same instance,
        // and an identity of its own, so its create reclaims nothing. This
        // process swept once already, at its first create.
        using (var standIn = ctx.CreateController(gc, reclaimFirst ? "s81-after-reclaim" : "s81-before-reclaim"))
        {
            string? arrived = Arrived(standIn);
            Probe.Check("the stand-in arrives on the same instance", string.Equals(arrived, id, StringComparison.OrdinalIgnoreCase), arrived ?? "");
            if (reclaimFirst)
            {
                Probe.Check("it gets input.inf: the flag reinstalled it by rank",
                    Probe.WaitFor(() => Devnodes.Service(id) == "HidUsb" && Devnodes.Status(id) is { Problem: 10 }, 10_000),
                    $"{Devnodes.Service(id)}");
                Probe.Check("and the flag is spent", ((Devnodes.ConfigFlags(id) ?? 0) & CONFIGFLAG_REINSTALL) == 0);
            }
            else
            {
                Probe.Check("before any reclaim it finds WinUSB, as an instance Zadig bound would",
                    Probe.WaitFor(() => Devnodes.Service(id) == "WINUSB", 10_000), $"{Devnodes.Service(id)}");
                RegistryLedger.PruneDead();
                Probe.Check("the reclaim puts it back on input.inf",
                    Probe.WaitFor(() => Devnodes.Service(id) == "HidUsb", 15_000), $"{Devnodes.Service(id)}");
            }
        }
        Probe.Check("no hold left", RegistryLedger.HeldWinUsbDevnodes().Count == 0, string.Join(", ", RegistryLedger.HeldWinUsbDevnodes()));
        // The child's controller record and its import: the orphan-safe
        // cleanup takes what a dead program left.
        HMContext.RemoveAllVirtualControllers(preserveInstall: true);
    }

    static int Child()
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var c = ctx.CreateController(ctx.GetProfile("gamecube-adapter")!);
        string? id = Arrived(c);
        var bound = c.BindWinUsb();
        Console.WriteLine($"bound {bound} {id}");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>The adapter devnode of a USB/IP controller, by its vhci
    /// port, once it is present.</summary>
    static string? Arrived(HMController c, string? except = null)
    {
        string? id = null;
        int port = c.UsbipHandle!.VhciPort;
        Probe.WaitFor(() => (id = Devnodes.AdapterOnVhciPort(port)) != null
                            && !string.Equals(id, except, StringComparison.OrdinalIgnoreCase), 10_000);
        return id;
    }

    readonly record struct DriverRank(string Original, uint Rank, string Status);

    /// <summary>The devnode's matching drivers as pnputil lists them.</summary>
    static Dictionary<string, DriverRank> DriverRanks(string id)
    {
        var psi = new ProcessStartInfo("pnputil.exe", $"/enum-devices /instanceid \"{id}\" /drivers")
        { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        var result = new Dictionary<string, DriverRank>(StringComparer.OrdinalIgnoreCase);
        int start = text.IndexOf("Matching Drivers:", StringComparison.Ordinal);
        if (start < 0) return result;
        foreach (var block in Regex.Split(text[start..], @"(?=^\s*Driver Name:)", RegexOptions.Multiline))
        {
            string Field(string name) => Regex.Match(block, $@"^\s*{name}:\s*(.+?)\s*$", RegexOptions.Multiline).Groups[1].Value;
            string name = Field("Driver Name");
            if (name.Length == 0) continue;
            string original = Field("Original Name");
            uint.TryParse(Field("Driver Rank"), System.Globalization.NumberStyles.HexNumber, null, out uint rank);
            result[name] = new DriverRank(original.Length > 0 ? original : name, rank, Field("Driver Status"));
        }
        return result;
    }

    static string Describe(Dictionary<string, DriverRank> ranks)
        => string.Join("; ", ranks.Select(kv => $"{kv.Key} ({kv.Value.Original}) 0x{kv.Value.Rank:X8} {kv.Value.Status}"));
}
