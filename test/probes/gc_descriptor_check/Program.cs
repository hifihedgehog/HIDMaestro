// S79: the Wii U GameCube adapter persona presents a real WUP-028's bytes.
//
// The catalog part reads the three 057E:0337 profiles: gamecube-adapter is
// the USB/IP persona with the real report descriptor, gamecube-adapter-hid
// carries the same 214 bytes, joypad-gc-adapter carries joypad-os's 70, and
// exactly one of the three is named Full. Every profile in the catalog must
// also compile as a device model: its ports, its USB/IP protocol and its
// WinUSB package.
//
// The device part reads the same bytes three ways and compares each with
// the real adapter's (GcCommon.cs, docs/design/v1.13.0.md):
//  - what the persona serves, from the USB/IP device's own descriptor set.
//  - what Windows enumerates, through the hub the device sits on, before
//    any driver loads. usbip-win2 presents every device at high speed and,
//    for a full-speed one, rewrites each interrupt bInterval to the same
//    period in microframes on every full configuration read (v0.9.8.1
//    drivers/ude/wsk_receive.cpp patch_config and to_high_speed_interval):
//    8 ms becomes bInterval 7. That is the one difference allowed.
//  - what libusb reads, through WinUSB once BindWinUsb has bound it: the
//    strings, the report descriptor and the stalls. usbhub3 forwards no
//    descriptor request to a device that failed to start, and a real
//    adapter fails to start on input.inf, so these need the binding.
// The device must also land where a real adapter does: the serial-named
// instance when no other adapter is present, on input.inf's HID driver,
// failing with Code 10.
//
// Members newer than b4d29d9 are reached by reflection, so the probe builds
// against b4d29d9 and fails there at run time.
//
// Requires elevation. Exit 0 PASS, 1 FAIL, 2 when another program that embeds
// HIDMaestro is running.
using System;
using System.Linq;
using System.Reflection;
using System.Text;
using HIDMaestro;

internal static class Program
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    static int Main()
    {
        Console.WriteLine("=== S79: the GameCube adapter persona's descriptors ===");
        if (Probe.OtherSdkRunning()) return 2;

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        Console.WriteLine("-- catalog");
        var adapters = ctx.AllProfiles.Where(p => p.VendorId == 0x057E && p.ProductId == 0x0337).ToList();
        Probe.Check("three 057E:0337 profiles", adapters.Count == 3,
            string.Join(", ", adapters.Select(p => p.Id)));
        var full = adapters.Where(p => p.Name.Contains("Full", StringComparison.Ordinal)).Select(p => p.Id).ToList();
        Probe.Check("exactly one of them is named Full, gamecube-adapter",
            full.Count == 1 && full[0] == "gamecube-adapter", string.Join(", ", full));

        var gc = ctx.GetProfile("gamecube-adapter");
        Probe.Check("gamecube-adapter is a USB/IP persona", gc != null && gc.RequiresUsbipBackend && gc.IsDeployable);
        Probe.Check("gamecube-adapter carries the real report descriptor",
            gc?.DescriptorHex != null && Convert.FromHexString(gc.DescriptorHex).AsSpan().SequenceEqual(GcReal.ReportDescriptor));
        Probe.Check("gamecube-adapter's strings are Nintendo and WUP-028",
            gc != null && gc.ManufacturerString == GcReal.Manufacturer && gc.ProductString == GcReal.Product,
            gc == null ? "" : $"\"{gc.ManufacturerString}\" \"{gc.ProductString}\"");
        Probe.Check("gamecube-adapter's layout is a controller adapter with four ports",
            gc?.AsControllerAdapter()?.Ports == 4, $"{gc?.Layout?.Kind}");

        var hid = ctx.GetProfile("gamecube-adapter-hid");
        Probe.Check("gamecube-adapter-hid carries the real report descriptor",
            hid?.DescriptorHex != null && Convert.FromHexString(hid.DescriptorHex).AsSpan().SequenceEqual(GcReal.ReportDescriptor));
        Probe.Check("gamecube-adapter-hid's manufacturer string is Nintendo",
            hid?.ManufacturerString == GcReal.Manufacturer, $"\"{hid?.ManufacturerString}\"");

        var joypad = ctx.GetProfile("joypad-gc-adapter");
        byte[] joypadRd = joypad?.DescriptorHex != null ? Convert.FromHexString(joypad.DescriptorHex) : Array.Empty<byte>();
        Probe.Check("joypad-gc-adapter carries joypad-os's 70-byte descriptor",
            joypadRd.AsSpan().SequenceEqual(GcReal.JoypadOsReportDescriptor), $"{joypadRd.Length} bytes");

        CheckCatalogCompiles(ctx);

        Console.WriteLine("-- device");
        if (gc == null || !gc.IsDeployable)
        {
            Probe.Check("create the persona", false, "gamecube-adapter is not deployable");
            return Probe.Finish("S79");
        }

        var before = Devnodes.PresentAdapters();
        HMController controller;
        try { controller = ctx.CreateController(gc); }
        catch (Exception ex)
        {
            Probe.Check("create the persona", false, $"{ex.GetType().Name}: {ex.Message}");
            return Probe.Finish("S79");
        }

        using (controller)
        {
            CheckServed(controller);

            string? id = null;
            Probe.WaitFor(() => (id = Devnodes.PresentAdapters().Except(before, StringComparer.OrdinalIgnoreCase).FirstOrDefault()) != null, 10_000);
            Probe.Check("the adapter arrives as a USB device", id != null, id ?? "");
            if (id == null) return Probe.Finish("S79");

            if (before.Count == 0)
                Probe.Check("it is the serial-named instance a real adapter gets",
                    string.Equals(id, GcReal.SerialInstance, StringComparison.OrdinalIgnoreCase), id);
            else
                Probe.Check("beside another adapter it gets a port-named instance, as a second real one does",
                    !string.Equals(id, GcReal.SerialInstance, StringComparison.OrdinalIgnoreCase), id);

            // Windows binds input.inf's HID driver, which cannot parse the
            // report descriptor: Code 10, as on a real adapter.
            Probe.WaitFor(() => Devnodes.Status(id) is { Problem: 10 }, 10_000);
            string? inf = Devnodes.InfPath(id);
            var status = Devnodes.Status(id);
            Probe.Check("Windows gives it input.inf's HID driver",
                inf != null && inf.EndsWith("input.inf", StringComparison.OrdinalIgnoreCase)
                && string.Equals(Devnodes.Service(id), "HidUsb", StringComparison.OrdinalIgnoreCase),
                $"{inf}, {Devnodes.Service(id)}");
            Probe.Check("which fails with Code 10, as on a real adapter", status is { Problem: 10 },
                status == null ? "absent" : $"problem {status.Value.Problem}");

            CheckEnumerated(id);
            CheckThroughWinUsb(controller, id);
        }

        Probe.Check("the device leaves with the controller",
            Probe.WaitFor(() => Devnodes.PresentAdapters().Except(before, StringComparer.OrdinalIgnoreCase).Count() == 0, 10_000));
        return Probe.Finish("S79");
    }

    /// <summary>Every catalog profile compiles as a device model, the check a
    /// create runs before it takes anything machine-wide.</summary>
    static void CheckCatalogCompiles(HMContext ctx)
    {
        var inner = typeof(HMProfile).GetProperty("Inner", Any);
        var validate = inner?.PropertyType.GetMethod("ValidateDeviceModel", Any);
        if (inner == null || validate == null)
        {
            Probe.Check("every catalog profile compiles as a device model", false, "ValidateDeviceModel is absent");
            return;
        }
        int count = 0;
        var failures = new StringBuilder();
        foreach (var p in ctx.AllProfiles)
        {
            count++;
            try { validate.Invoke(inner.GetValue(p), null); }
            catch (TargetInvocationException ex) { failures.Append($" {p.Id}: {ex.InnerException?.Message}"); }
        }
        Probe.Check($"every catalog profile compiles as a device model ({count})", failures.Length == 0, failures.ToString());
    }

    /// <summary>The bytes the persona serves, from the USB/IP device's own
    /// descriptor set: the real adapter's exactly, full speed and 8 ms
    /// included.</summary>
    static void CheckServed(HMController controller)
    {
        Console.WriteLine("-- what the persona serves");
        object? set = Get(Get(Get(controller, "UsbipHandle"), "Device"), "Descriptors");
        if (set == null)
        {
            Probe.Check("the USB/IP device's descriptor set", false, "absent");
            return;
        }
        byte[]? Desc(byte type, byte index, ushort lang) =>
            (byte[]?)set.GetType().GetMethod("GetDescriptor", Any, new[] { typeof(byte), typeof(byte), typeof(ushort) })
                ?.Invoke(set, new object[] { type, index, lang });
        byte[]? Hid(byte type) =>
            (byte[]?)set.GetType().GetMethod("GetHidDescriptor", Any, new[] { typeof(byte), typeof(byte) })
                ?.Invoke(set, new object[] { type, (byte)0 });

        var dev = Desc(0x01, 0, 0);
        Probe.Check("device descriptor", dev != null && dev.AsSpan().SequenceEqual(GcReal.DeviceDescriptor), Show(dev));
        var cfg = Desc(0x02, 0, 0);
        Probe.Check("configuration descriptor, endpoints at 8 ms", cfg != null && cfg.AsSpan().SequenceEqual(GcReal.ConfigurationDescriptor), Show(cfg));
        Probe.Check("full speed in the import reply", Get(set, "Speed") is uint speed && speed == 2, $"{Get(set, "Speed")}");
        var langs = Desc(0x03, 0, 0);
        Probe.Check("string 0 lists English (US) alone", langs != null && Probe.Hex(langs) == "04030904", Show(langs));
        CheckString("served", Desc(0x03, 1, 0x0409), GcReal.Manufacturer, 1);
        CheckString("served", Desc(0x03, 2, 0x0409), GcReal.Product, 2);
        CheckString("served", Desc(0x03, 3, 0x0409), GcReal.Serial, 3);
        Probe.Check("string 0xEE absent: no Microsoft OS descriptors", Desc(0x03, 0xEE, 0) == null);
        Probe.Check("no device qualifier, as on a full-speed device", Desc(0x06, 0, 0) == null);
        Probe.Check("no other-speed configuration", Desc(0x07, 0, 0) == null);
        var rd = Hid(0x22);
        Probe.Check("report descriptor, all 214 bytes", rd != null && rd.AsSpan().SequenceEqual(GcReal.ReportDescriptor), Show(rd));
        var hidClass = Hid(0x21);
        Probe.Check("HID class descriptor", hidClass != null && Probe.Hex(hidClass) == "09211001000122D600", Show(hidClass));
    }

    /// <summary>What Windows enumerates, through the hub, before any driver:
    /// the device descriptor exactly, and the configuration descriptor as
    /// usbip-win2 hands a full-speed device on, at high speed with bInterval
    /// 7.</summary>
    static void CheckEnumerated(string id)
    {
        Console.WriteLine("-- what Windows enumerates");
        using var hub = HubPort.ForDevice(id);
        Probe.Check("its hub port opens", hub != null);
        if (hub == null) return;
        Probe.Check("usbip-win2 presents it at high speed, as every device it carries", hub.Speed() == 2, $"speed {hub.Speed()}");
        var dev = hub.GetDescriptor(0x01, 0, 0, 255);
        Probe.Check("device descriptor", dev != null && dev.AsSpan().SequenceEqual(GcReal.DeviceDescriptor), Show(dev));
        var cfg = hub.GetDescriptor(0x02, 0, 0, 255);
        Probe.Check("configuration descriptor, the real one with usbip-win2's bInterval 7",
            cfg != null && cfg.AsSpan().SequenceEqual(GcReal.ConfigurationDescriptorAtHighSpeed), Show(cfg));
    }

    /// <summary>What libusb reads once WinUSB is bound: every descriptor and
    /// stall, through WinUSB's control pipe.</summary>
    static void CheckThroughWinUsb(HMController controller, string id)
    {
        Console.WriteLine("-- what libusb reads through WinUSB");
        var bind = typeof(HMController).GetMethod("BindWinUsb", Any, Type.EmptyTypes);
        if (bind == null)
        {
            Probe.Check("BindWinUsb binds WinUSB", false, "BindWinUsb is absent");
            return;
        }
        object? result;
        try { result = bind.Invoke(controller, null); }
        catch (TargetInvocationException ex)
        {
            Probe.Check("BindWinUsb binds WinUSB", false, $"{ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
            return;
        }
        Probe.Check("BindWinUsb binds WinUSB", result?.ToString() == "Bound", $"{result}");
        WinUsbDevice? usb = null;
        Probe.WaitFor(() => (usb = WinUsbDevice.Open(id)) != null, 10_000);
        Probe.Check("WinUSB opens the device by the profile's interface GUID", usb != null);
        if (usb == null) return;
        using (usb)
        {
            var dev = usb.Control(0x80, 0x06, 0x0100, 0, 18, null, out int e1);
            Probe.Check("device descriptor", dev != null && dev.AsSpan().SequenceEqual(GcReal.DeviceDescriptor), Show(dev, e1));
            var cfg = usb.Control(0x80, 0x06, 0x0200, 0, 255, null, out int e2);
            Probe.Check("configuration descriptor, as usbip-win2 hands it on",
                cfg != null && cfg.AsSpan().SequenceEqual(GcReal.ConfigurationDescriptorAtHighSpeed), Show(cfg, e2));
            var langs = usb.Control(0x80, 0x06, 0x0300, 0, 255, null, out int e3);
            Probe.Check("string 0 lists English (US) alone", langs != null && Probe.Hex(langs) == "04030904", Show(langs, e3));
            CheckString("WinUSB", usb.Control(0x80, 0x06, 0x0301, 0x0409, 255, null, out _), GcReal.Manufacturer, 1);
            CheckString("WinUSB", usb.Control(0x80, 0x06, 0x0302, 0x0409, 255, null, out _), GcReal.Product, 2);
            CheckString("WinUSB", usb.Control(0x80, 0x06, 0x0303, 0x0409, 255, null, out _), GcReal.Serial, 3);
            Probe.Check("string 0xEE stalls: no Microsoft OS descriptors",
                usb.Control(0x80, 0x06, 0x03EE, 0, 255, null, out _) == null);
            Probe.Check("device qualifier stalls, as on a full-speed device",
                usb.Control(0x80, 0x06, 0x0600, 0, 10, null, out _) == null);
            Probe.Check("other-speed configuration stalls",
                usb.Control(0x80, 0x06, 0x0700, 0, 255, null, out _) == null);
            // GET_DESCRIPTOR(Report) to interface 0, the request a HID host
            // sends.
            var rd = usb.Control(0x81, 0x06, 0x2200, 0, 255, null, out int e4);
            Probe.Check("report descriptor, all 214 bytes", rd != null && rd.AsSpan().SequenceEqual(GcReal.ReportDescriptor), Show(rd, e4));
            var hidClass = usb.Control(0x81, 0x06, 0x2100, 0, 9, null, out int e5);
            Probe.Check("HID class descriptor", hidClass != null && Probe.Hex(hidClass) == "09211001000122D600", Show(hidClass, e5));
        }
    }

    static void CheckString(string view, byte[]? s, string expected, int index)
    {
        string? text = s != null && s.Length >= 2 && s[1] == 0x03 ? Encoding.Unicode.GetString(s, 2, s[0] - 2) : null;
        Probe.Check($"string {index} is \"{expected}\" ({view})", text == expected && s![0] == s.Length,
            s == null ? "stalled" : $"\"{text}\" ({Probe.Hex(s)})");
    }

    static object? Get(object? o, string name) => o?.GetType().GetProperty(name, Any)?.GetValue(o);

    static string Show(byte[]? b, int error = 0)
        => b == null ? (error != 0 ? $"failed, error {error}" : "absent") : $"{b.Length} bytes {Probe.Hex(b.AsSpan(0, Math.Min(48, b.Length)))}";
}
