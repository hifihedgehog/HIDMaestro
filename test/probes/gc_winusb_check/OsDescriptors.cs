// S81 on a machine that refuses HIDMaestro's package (2.3.0): the device
// asks for Windows' own WinUSB driver with Microsoft OS 1.0 descriptors, and
// no package is installed. "--os-descriptors" takes the same path on a
// machine that installs the package, so x64 runs it too.
//
// Checked here:
//  - the bind: Bound, the devnode on WINUSB and started, its compatible IDs
//    led by USB\MS_COMP_WINUSB, the profile's interface GUID registered, no
//    HIDMaestro package added to the driver store, AlreadyBound on a second
//    call, and the ledger's hold.
//  - usbflags: Windows' answer for 057E:0337 rev 1.00 while the controller
//    lives, the key as it was once it is disposed.
//  - the next life before its own bind comes up as the first did before its
//    bind: on input.inf, not on WinUSB.
using System;
using System.Collections.Generic;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;
using Microsoft.Win32;

internal static partial class Program
{
    const string AdapterFlags = @"SYSTEM\CurrentControlSet\Control\usbflags\057E03370100";

    static string Flags()
    {
        using var k = Registry.LocalMachine.OpenSubKey(AdapterFlags);
        if (k == null) return "absent";
        return string.Join(" ", k.GetValueNames().OrderBy(n => n).Select(n => k.GetValue(n) switch
        {
            byte[] b => $"{n}={Convert.ToHexString(b)}",
            var v => $"{n}={v}",
        }));
    }

    static List<string> OurPackages() =>
        PnputilHelper.TryEnumerateDrivers(n => n.StartsWith("hmwinusb_", StringComparison.OrdinalIgnoreCase))?
            .Select(r => r.PublishedName).OrderBy(n => n).ToList() ?? new List<string> { "(the driver store could not be listed)" };

    static string? InterfaceGuids(string id)
    {
        using var k = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{id}\Device Parameters");
        return k?.GetValue("DeviceInterfaceGUIDs") switch
        {
            string[] a => string.Join(",", a),
            string s => s,
            _ => k?.GetValue("DeviceInterfaceGUID") as string,
        };
    }

    static void OsDescriptorBinding(HMContext ctx, HMProfile gc)
    {
        Console.WriteLine("-- the binding by Microsoft OS descriptors");
        string flagsBefore = Flags();
        var packagesBefore = OurPackages();
        Console.WriteLine($"  usbflags 057E03370100 before: {flagsBefore}");
        string? first, bound = null;
        var steps = new List<string>();
        WinUsbBinding.StepForProbe = s => { lock (steps) steps.Add(s); };
        using (var a = ctx.CreateController(gc))
        {
            first = Arrived(a);
            Probe.Check("the adapter arrives", first != null, first ?? "");
            if (first == null) return;
            Probe.WaitFor(() => Devnodes.Status(first) is { Problem: 10 } || Devnodes.Service(first) != null, Probe.HangMs(InstallBoundMs));
            Console.WriteLine($"  before the bind: {Devnodes.Service(first)}, compatible IDs {string.Join(" ", Devnodes.CompatibleIds(first))}");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = a.BindWinUsb();
            Console.WriteLine($"  BindWinUsb: {result} in {sw.ElapsedMilliseconds} ms");
            Probe.Check("BindWinUsb binds WinUSB with no package", result == HMWinUsbBinding.Bound, $"{result}");
            bound = Arrived(a);
            Probe.Check("the device is back on a devnode", bound != null, bound ?? "");
            if (bound == null) return;
            Probe.Check("the devnode runs WinUSB and is started",
                Probe.WaitFor(() => Devnodes.Service(bound) == "WINUSB" && Devnodes.Status(bound) is { Problem: 0 }, Probe.HangMs()),
                $"{Devnodes.Service(bound)}");
            var compat = Devnodes.CompatibleIds(bound);
            Probe.Check("its compatible IDs start with USB\\MS_COMP_WINUSB, built from the device's descriptors",
                compat.Count > 0 && compat[0].Equals(@"USB\MS_COMP_WINUSB", StringComparison.OrdinalIgnoreCase), string.Join(" ", compat));
            Probe.Check("Windows installed its own winusb.inf",
                string.Equals(System.IO.Path.GetFileName(Devnodes.InfPath(bound) ?? ""), "winusb.inf", StringComparison.OrdinalIgnoreCase),
                Devnodes.InfPath(bound) ?? "no INF");
            string? guids = InterfaceGuids(bound);
            Probe.Check("the profile's interface GUID is registered", guids != null && guids.Contains(GcReal.WinUsbInterface.ToString(), StringComparison.OrdinalIgnoreCase),
                guids ?? "none");
            Probe.Check("no HIDMaestro WinUSB package was added to the driver store", OurPackages().SequenceEqual(packagesBefore),
                string.Join(", ", OurPackages()));
            Probe.Check("Windows recorded the device's Microsoft OS string: usbflags osvc is 01 and the vendor code",
                Flags().Contains("osvc=0101"), Flags());
            Probe.Check("the ledger holds the devnode", RegistryLedger.HeldWinUsbDevnodes().Contains(bound, StringComparer.OrdinalIgnoreCase));
            Probe.Check("a second BindWinUsb finds it already bound", a.BindWinUsb() == HMWinUsbBinding.AlreadyBound);
            List<string> order;
            lock (steps) order = steps.Where(x => x is "unplugged" or "usbflags cleared").ToList();
            Probe.Check("the usbflags key is cleared while the device is unplugged, not before",
                order.Count == 2 && order[0] == "unplugged" && order[1] == "usbflags cleared", string.Join(", ", order));
        }
        WinUsbBinding.StepForProbe = null;
        Console.WriteLine("-- dispose");
        Probe.Check("its devnode leaves", Probe.WaitFor(() => !Devnodes.IsPresent(bound!), Probe.HangMs()));
        Probe.Check("no WinUSB hold is left", Probe.WaitFor(() => RegistryLedger.HeldWinUsbDevnodes().Count == 0, Probe.HangMs()),
            string.Join(", ", RegistryLedger.HeldWinUsbDevnodes()));
        Probe.Check("usbflags 057E03370100 is as it was before the create", Flags() == flagsBefore, Flags());

        using (var b = ctx.CreateController(gc))
        {
            string? next = Arrived(b);
            Probe.Check("the next adapter arrives", next != null, next ?? "");
            if (next != null)
            {
                Probe.WaitFor(() => Devnodes.Status(next) is { Problem: 10 } || Devnodes.Service(next) == "WINUSB", Probe.HangMs(InstallBoundMs));
                Probe.Check("before its own bind it is on input.inf, as the first was, not on WinUSB",
                    Devnodes.Service(next) == "HidUsb", $"{Devnodes.Service(next)} on {next}");
            }
        }
        Probe.Check("usbflags 057E03370100 is as it was once that one is gone too", Flags() == flagsBefore, Flags());
    }
}

internal static partial class Program
{
    /// <summary>"--bind-timing": one create of the adapter and its bind,
    /// each step timed, for a run under CPU load. The process's own CPU time
    /// is printed with each step, to tell work from waiting.</summary>
    static int BindTiming()
    {
        Console.WriteLine("=== S81 timing: one create and its WinUSB bind ===");
        if (Probe.OtherSdkRunning()) return 2;
        var self = System.Diagnostics.Process.GetCurrentProcess();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Step(string what)
        {
            self.Refresh();
            Console.WriteLine($"  {DateTime.Now:HH:mm:ss.fff} +{sw.ElapsedMilliseconds,7} ms  cpu {self.TotalProcessorTime.TotalMilliseconds,7:F0} ms  {what}");
        }
        Step("start");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        Step("context");
        var gc = ctx.GetProfile("gamecube-adapter")!;
        using (var a = ctx.CreateController(gc))
        {
            Step("created");
            string? id = Arrived(a);
            Step($"devnode {id}");
            var r = a.BindWinUsb();
            Step($"BindWinUsb {r}");
        }
        Step("disposed");
        return 0;
    }
}

internal static partial class Program
{
    /// <summary>A re-plug whose attach fails: the device is plugged in again
    /// as it was, on input.inf with no Microsoft OS descriptors, and
    /// BindWinUsb throws. A later bind binds.</summary>
    static void ReplugFails(HMContext ctx, HMProfile gc)
    {
        Console.WriteLine("-- a re-plug whose attach fails");
        using var a = ctx.CreateController(gc);
        string? first = Arrived(a);
        Probe.Check("the adapter arrives", first != null, first ?? "");
        if (first == null) return;
        Probe.WaitFor(() => Devnodes.Status(first) is { Problem: 10 } || Devnodes.Service(first) != null, Probe.HangMs(InstallBoundMs));
        HIDMaestro.Internal.Usbip.UsbipBackendHandle.AttachForProbe = () => throw new System.ComponentModel.Win32Exception(31, "the probe fails the attach");
        Exception? thrown = null;
        try { a.BindWinUsb(); }
        catch (Exception ex) { thrown = ex; }
        finally { HIDMaestro.Internal.Usbip.UsbipBackendHandle.AttachForProbe = null; }
        Probe.Check("BindWinUsb throws and names the failed re-plug", thrown is InvalidOperationException && thrown.Message.Contains("plugged in again as it was"),
            thrown?.Message ?? "no exception");
        string? back = Arrived(a);
        Probe.Check("the device is plugged in again", back != null, back ?? "none");
        if (back == null) return;
        Probe.WaitFor(() => Devnodes.Status(back) is { Problem: 10 } || Devnodes.Service(back) == "WINUSB", Probe.HangMs(InstallBoundMs));
        Probe.Check("as it was: on input.inf, with no WINUSB compatible ID", Devnodes.Service(back) == "HidUsb"
            && !Devnodes.CompatibleIds(back).Exists(c => c.Contains("MS_COMP", StringComparison.OrdinalIgnoreCase)),
            $"{Devnodes.Service(back)} {string.Join(" ", Devnodes.CompatibleIds(back))}");
        Probe.Check("and no WinUSB hold of the failed bind is left", !RegistryLedger.HeldWinUsbDevnodes().Contains(first, StringComparer.OrdinalIgnoreCase));
        var bound = a.BindWinUsb();
        Probe.Check("a bind after it binds", bound == HMWinUsbBinding.Bound, $"{bound}");
    }
}
