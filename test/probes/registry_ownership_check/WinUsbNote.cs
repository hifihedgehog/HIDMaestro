// The cleanup's WinUSB package note, live and elevated, through the SDK's
// own bindings to HKLM, the deploy lock and pnputil: a package add takes
// its name out of the note, the uninstall cleanup notes a package that a
// gone devnode keeps, and once nothing uses it, a later program's sweep
// removes that package and drops the note, and leaves a package the note
// does not name.
//
// It binds a GameCube adapter persona, removes its gone devnode and runs
// the uninstall cleanup, so it runs only with "--winusb-note", in a sandbox
// with usbip-win2 installed and no other program on HIDMaestro, never in
// the battery's S66.

using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32;

internal static partial class Program
{
    const string GcInf = "hmwinusb_gamecube-adapter.inf";
    const string ExtraInf = "hmwinusb_regown-extra.inf";

    [DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint devInst, string id, uint flags);
    const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;

    static string[] Noted() => RegistryLedger.NotedPackages(Registry.LocalMachine);

    static string? PublishedOf(string infName) =>
        PnputilHelper.TryEnumerateDrivers()?.FirstOrDefault(r => r.OriginalName.Equals(infName, StringComparison.OrdinalIgnoreCase))?.PublishedName;

    static bool Waited(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(100); }
        return cond();
    }

    static void WinUsbNoteLive()
    {
        Console.WriteLine("\n== Live, sandbox: the cleanup's WinUSB package note ==");
        if (!HMContext.IsUsbipBackendAvailable) { Check("the usbip-win2 host controller answers", false); return; }
        if (PublishedOf(ExtraInf) != null) { Check($"{ExtraInf} is not in the driver store before the run", false); return; }
        string[] before = Noted();
        string? extra = null;
        try
        {
            // A program that adds the package takes it out of the note.
            RegistryLedger.NoteWinUsbPackagesLeft(RegistryLedger.NoteChange.Replace(new[] { GcInf }));
            Check("the note in HKLM names the GameCube adapter's package before the add", Noted().Contains(GcInf), string.Join(",", Noted()));
            var (bound, id) = ChildBindGc();
            Check("another program binds a GameCube adapter persona", bound == "Bound" && id != null, $"{bound} {id}");
            if (id == null) return;
            Check("its package add takes the package out of the note", !Noted().Contains(GcInf, StringComparer.OrdinalIgnoreCase), string.Join(",", Noted()));

            // Its devnode is gone and keeps the package.
            bool gone = Waited(() => CM_Locate_DevNodeW(out _, id, 0) != 0, 30_000);
            string? published = PublishedOf(GcInf);
            string? on = WinUsbBinding.DriverOf(id, phantom: true).Inf;
            Check("the adapter's devnode is gone and still on the package",
                  gone && published != null && on != null && on.EndsWith(published, StringComparison.OrdinalIgnoreCase),
                  $"gone {gone}, package {published ?? "none"}, devnode on {on ?? "none"}");
            if (!gone || published == null) return;

            // The uninstall cleanup keeps it for the devnode, and notes it.
            HMContext.RemoveAllVirtualControllers(HMCleanupScope.Everything, preserveInstall: false);
            Check("the uninstall cleanup keeps the package a gone devnode uses", PublishedOf(GcInf) != null);
            Check("and the note in HKLM names it, alone", Noted().SequenceEqual(new[] { GcInf }, StringComparer.OrdinalIgnoreCase), string.Join(",", Noted()));

            // A package no note names and no devnode uses, which a deferred
            // run must leave and a cleanup would delete.
            extra = AddExtraPackage();
            Check("a package the note does not name is in the driver store", extra != null && !Noted().Contains(ExtraInf, StringComparer.OrdinalIgnoreCase));

            // Nothing uses the noted package any more.
            DeviceManager.RemoveDevice(id, timeoutMs: 30_000, forceFallbacks: false);
            Check("the gone devnode is removed", CM_Locate_DevNodeW(out _, id, 1) != 0);

            // The first sweep of a program with no WinUSB entry runs the
            // deferred removal, on a pool thread.
            typeof(RegistryLedger).GetField("s_leftPackagesTried", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, 0);
            RegistryLedger.PruneDead();
            bool removed = Waited(() => PublishedOf(GcInf) == null, 60_000);
            Check("a later sweep's deferred run deletes the noted package", removed);
            bool dropped = Waited(() => Registry.LocalMachine.OpenSubKey(@"SOFTWARE\HIDMaestroPackagesLeft") == null, 10_000);
            Check("and the note's key goes with it", dropped, string.Join(",", Noted()));
            Check("and the package the note does not name stays", PublishedOf(ExtraInf) != null);
        }
        finally
        {
            string? left = PublishedOf(ExtraInf);
            if (left != null) PnputilHelper.Run($"/delete-driver {left}", timeoutMs: 30_000);
            RegistryLedger.WriteNote(Registry.LocalMachine, before.Length > 0 ? before : null);
            try
            {
                using var ctx = new HMContext();
                ctx.InstallDriver();
            }
            catch (Exception ex) { Console.WriteLine($"  the driver was not put back: {ex.Message}"); }
        }
    }

    /// <summary>The package of a WinUSB profile no device matches, added
    /// the way a bind adds one, under the deploy lock. Its published name,
    /// or null.</summary>
    static string? AddExtraPackage()
    {
        var p = new WinUsbBinding.Package(ExtraInf, "hmwinusb_regown-extra.cat", 0xBEEF, 0xF0AF,
            @"USB\COMPAT_VID_BEEF&Class_FF&SubClass_00&Prot_00", Guid.NewGuid(), "RegOwn extra", "regown-extra");
        var add = typeof(WinUsbBinding).GetMethod("AddPackage", BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(WinUsbBinding.Package) })!;
        using var deploy = DriverDeploy.Acquire();
        try { return (string?)add.Invoke(null, new object[] { p }); }
        catch (Exception ex) { Console.WriteLine($"  the extra package was not added: {ex.InnerException?.Message ?? ex.Message}"); return null; }
        finally { deploy.ReleaseMutex(); }
    }

    /// <summary>Another program binds a GameCube adapter persona, disposes
    /// it and exits. Its answer and the devnode it bound.</summary>
    static (string Bound, string? Instance) ChildBindGc()
    {
        // A loader failure in the child returns an exit code, never a dialog.
        SetErrorMode(SetErrorMode(0) | SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
        using var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--child-bindgc")
        {
            RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        string output = "";
        var reader = new Thread(() => { try { output = child.StandardOutput.ReadToEnd(); } catch { } }) { IsBackground = true };
        reader.Start();
        if (!child.WaitForExit(180_000)) { try { child.Kill(); } catch { } return ("timeout", null); }
        reader.Join(5000);
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("BOUND ", StringComparison.Ordinal));
        if (line == null) return ($"no answer: {output.Trim()}", null);
        var parts = line.Split(' ', 3);
        return (parts[1], parts.Length > 2 ? parts[2] : null);
    }

    static int ChildBindGc(string _)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var p = ctx.GetProfile("gamecube-adapter")!;
        using (var c = ctx.CreateController(p))
        {
            var r = c.BindWinUsb();
            string? id = RegistryLedger.HeldWinUsbDevnodes().FirstOrDefault(i => i.StartsWith(@"USB\VID_057E&PID_0337\", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"BOUND {r} {id}");
        }
        Console.WriteLine("DONE");
        return 0;
    }
}
