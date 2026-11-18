// bt_usb_form_check: a Bluetooth identity's USB form on the USB/IP backend.
//
// No inbox route gives a USB/IP device a Bluetooth devnode, so a Bluetooth
// profile created there sits on the USB bus. hidapi reads the bus from the
// HID devnode's parent (SDL 3.4.18 src/hidapi/windows/hid.c:759-793) and SDL
// sets is_bluetooth from it (SDL_hidapijoystick.c:937). SDL's PS4, PS5 and
// Switch drivers then speak the USB protocol: output report 0x05 or 0x02
// (SDL_hidapi_ps4.c:967-988, SDL_hidapi_ps5.c:1099-1111) and the 0x80 USB
// handshake (SDL_hidapi_switch.c:689-711, 1617-1622). A profile names the
// USB profile it presents there with usbForm, and the device takes that
// profile's descriptor, reports, feature answers and protocol under the
// Bluetooth profile's id and strings.
//
//   offline            the load rule, the presented profile, its USB
//                      device, HMProfile.FormOn, the raw-frame rule, the
//                      decoded output names and the builder rule. No device.
//   live <backend>     each Bluetooth identity with a USB form, created on
//                      <backend> (umdf2 or usbip), and dualsense-composite as
//                      the positive control: the HID interface's report
//                      lengths, version and strings, a feature read, the
//                      raw-frame rule. Elevated.
//   sdl <backend> <dir> each Bluetooth identity of the Sony and Nintendo
//                      families and the USB forms, through the SDL3.dll in
//                      <dir>: open, the driver and bus SDL reads, input,
//                      rumble and LEDs. Elevated.
//
// Exit 0 PASS, 1 FAIL, 2 bad arguments, 3 not elevated.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using HIDMaestro;
using HIDMaestro.Internal;

internal static partial class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    /// <summary>The Bluetooth identities that name a USB form, and the
    /// form each names.</summary>
    internal static readonly (string Bt, string Form)[] Forms =
    {
        ("dualsense-bt", "dualsense"),
        ("dualsense-bt-full", "dualsense"),
        ("dualsense-edge-bt", "dualsense-edge"),
        ("dualshock-4-v2-bt", "dualshock-4-v2"),
        ("switch-pro", "switch-pro-usb"),
    };

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "offline";
        try
        {
            switch (mode)
            {
                case "offline":
                    Offline();
                    break;
                case "live" when args.Length >= 2 && ParseBackend(args[1]) is { } lb:
                    if (!Elevated()) { Console.WriteLine("  refused: live needs elevation"); return 3; }
                    Live(lb);
                    break;
                case "sdl" when args.Length >= 3 && ParseBackend(args[1]) is { } sb:
                    if (!Elevated()) { Console.WriteLine("  refused: sdl needs elevation"); return 3; }
                    Sdl.Run(sb, args[2], args.Skip(3).ToArray());
                    break;
                default:
                    Console.WriteLine("usage: BtUsbFormCheck offline | live <umdf2|usbip> | sdl <umdf2|usbip> <SDL3 dir> [profile ids]");
                    return 2;
            }
        }
        catch (Exception e)
        {
            Check("the run ends without an exception", false, e.ToString());
        }
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static HMBackend? ParseBackend(string s) => s.ToLowerInvariant() switch
    {
        "umdf2" => HMBackend.Umdf2,
        "usbip" => HMBackend.Usbip,
        _ => null,
    };

    static bool Elevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // ── offline ──────────────────────────────────────────────────────────

    static void Offline()
    {
        var db = ProfileDatabase.LoadEmbedded();
        var byId = db.All.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("\n-- The catalog's USB forms --");
        var named = db.All.Where(p => p.UsbForm != null).Select(p => p.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var want = Forms.Select(f => f.Bt).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Check("the catalog names a USB form on exactly the four Sony Bluetooth pads and switch-pro",
            named.SequenceEqual(want), string.Join(", ", named));
        Check("the catalog load refused no profile", !db.Issues.Any(i => i.Kind == HMProfileLoadIssueKind.Refused),
            string.Join("; ", db.Issues.Select(i => $"{i.ProfileId} {i.JsonPath} {i.Reason}")));

        foreach (var (btId, formId) in Forms)
        {
            Console.WriteLine($"\n-- {btId} -> {formId} --");
            var bt = byId[btId];
            var form = byId[formId];
            Check("the load found the form", ReferenceEquals(bt.UsbFormProfile, form), bt.UsbFormProfile?.Id ?? "none");

            var hm = new HMProfile(bt);
            Check("FormOn(Umdf2) is the profile itself", ReferenceEquals(hm.FormOn(HMBackend.Umdf2), hm));
            var onUsb = hm.FormOn(HMBackend.Usbip);
            Check($"FormOn(Usbip) is {formId}", onUsb.Id == formId, onUsb.Id);
            Check("FormOn(Usbip) answers the same object again", ReferenceEquals(onUsb, hm.FormOn(HMBackend.Usbip)));
            bool autoThrows = false;
            try { hm.FormOn(HMBackend.Auto); } catch (ArgumentException) { autoThrows = true; }
            Check("FormOn(Auto) throws ArgumentException", autoThrows);
            Check("SupportsBackend(Usbip)", hm.SupportsBackend(HMBackend.Usbip));
            Check("SupportsBackend(Umdf2)", hm.SupportsBackend(HMBackend.Umdf2));

            var p = bt.UsbFormOf(HMBackend.Usbip);
            Check("UsbFormOf(Umdf2) is the profile itself", ReferenceEquals(bt.UsbFormOf(HMBackend.Umdf2), bt));
            Check("the presented profile keeps the Bluetooth id, name and strings",
                p.Id == bt.Id && p.Name == bt.Name && p.ProductString == bt.ProductString
                && p.ManufacturerString == bt.ManufacturerString && p.DeviceDescription == bt.DeviceDescription,
                $"'{p.Id}' '{p.ProductString}' '{p.ManufacturerString}'");
            Check("the presented profile has the form's descriptor, connection and version",
                p.Descriptor == form.Descriptor && p.Connection == form.Connection && p.VersionNumber == form.VersionNumber,
                $"connection {p.Connection}, version {p.VersionNumber?.ToString("X4") ?? "default"}");
            Check("the presented profile takes the form's device model, group by group",
                p.Resolved.Sources.OrderBy(k => k.Key).SequenceEqual(form.Resolved.Sources.OrderBy(k => k.Key)),
                string.Join(" ", p.Resolved.Sources.Select(k => $"{k.Key}={k.Value}")));
            Check("the presented profile has the form's protocol, state block and reports",
                ReferenceEquals(p.Protocol, form.Protocol) && ReferenceEquals(p.StateBlock, form.StateBlock)
                && ReferenceEquals(p.InputReports, form.InputReports)
                && JsonOf(p.ExtendedOutputReport) == JsonOf(form.ExtendedOutputReport));
            Check("the presented profile names no USB form", p.UsbForm == null);
            if (bt.ExtendedReport?.ArmOn is { Count: > 0 })
            {
                // The Bluetooth form sends the whole state once armed, so the
                // USB form sends its extended report from the start.
                Check("the USB form's extended report is the form's, sent from the start (alwaysArmed)",
                    p.ExtendedReport is { AlwaysArmed: true } e && e.ReportId == form.ExtendedReport!.ReportId
                    && e.Size == form.ExtendedReport.Size && form.ExtendedReport.Fields.All(f => e.Fields.Any(g => FieldJson(g) == FieldJson(f))),
                    $"report {p.ExtendedReport?.ReportId} size {p.ExtendedReport?.Size} alwaysArmed {p.ExtendedReport?.AlwaysArmed}");
                foreach (var d in form.InputDefaults ?? new List<InputBytePatch>())
                    Check($"the form's fixed byte {d.Byte} = 0x{d.Value:X2} is a constant of that report",
                        p.ExtendedReport!.Fields.Any(f => f.Byte == d.Byte && f.Type == "uint8" && f.Initial == d.Value));
                var block = new byte[p.ExtendedReport!.Size];
                VendorBlobCodec.EncodeInput(p.ExtendedReport, new HMGamepadState { GyroPitch = 1000, AccelY = 8192, BatteryLevel = 7 },
                    0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f, block, new VendorBlobCodec.EncoderState());
                Check("SubmitState's sensors reach the USB form's report", block.Skip(1).Count(b => b != 0) > 8,
                    Convert.ToHexString(block, 0, Math.Min(32, block.Length)));
            }
            else
                Check("the presented profile's extended report is the form's", JsonOf(p.ExtendedReport) == JsonOf(form.ExtendedReport));

            var pres = HIDMaestro.Internal.Usbip.UsbPresentation.Resolve(bt);
            var formPres = HIDMaestro.Internal.Usbip.UsbPresentation.Resolve(form);
            Check("the USB device is derived", pres.Derived);
            Check("the USB device is the form's: device descriptor", pres.Spec.DeviceDescriptorHex == formPres.Spec.DeviceDescriptorHex,
                pres.Spec.DeviceDescriptorHex ?? "");
            Check("the USB device is the form's: configuration", pres.Spec.ConfigurationDescriptorHex == formPres.Spec.ConfigurationDescriptorHex,
                pres.Spec.ConfigurationDescriptorHex ?? "");
            _ = new HIDMaestro.Internal.Usbip.UsbDescriptorSet(p);

            // The raw frames only the Bluetooth form takes.
            var lengths = (int[]?)typeof(HMController).GetMethod("OtherFormRawLengths",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { bt, p });
            var takes = RawLengths(p);
            Console.WriteLine($"  raw lengths refused: {string.Join(",", lengths ?? Array.Empty<int>())}, the form's: {string.Join(",", takes)}");
            if (bt.StateBlock != null)
                Check("a state block the two forms share is no refused length",
                    lengths == null || !lengths.Contains(bt.StateBlock.Size));
            else
                Check("the Bluetooth extended report's length is refused", lengths != null && lengths.Contains(bt.ExtendedReport!.Size));
            Check("no length the form takes is refused", lengths == null || !lengths.Intersect(takes).Any());

            // What the SDK decodes from output, by name.
            var btOut = OutputNames(bt);
            var formOut = OutputNames(form);
            var transport = new HashSet<string> { "btTag", "btFlag", "btReserved", "crc32-le" };
            var missing = btOut.Where(n => !transport.Contains(n) && !formOut.Contains(n)).ToArray();
            Check("every decoded output value of the Bluetooth form but its transport bytes is decoded by the USB form too",
                missing.Length == 0, missing.Length == 0 ? string.Join(",", btOut.Where(n => !transport.Contains(n))) : "missing " + string.Join(",", missing));
        }

        Console.WriteLine("\n-- The load refuses what is no USB form --");
        string dsbt = System.IO.File.ReadAllText(CatalogFile("sony", "dualsense-bt.json"));
        Refused("a form with another PID", Variant(dsbt, "x-bt", "dualshock-4-v2"), "VID and PID");
        var plainBt = ProfileDatabase.LoadTexts(new[]
        {
            ("x-bt.json", Variant(dsbt, "x-bt", "x-bt-plain")),
            ("x-bt-plain.json", dsbt.Replace("\"id\":\"dualsense-bt\"", "\"id\":\"x-bt-plain\"").Replace("\"usbForm\":\"dualsense\",", "")),
        }, ModelRegistry.Catalog);
        Check("a Bluetooth form is refused at $.usbForm",
            plainBt.Issues.Any(i => i.ProfileId == "x-bt" && i.JsonPath == "$.usbForm" && i.Reason.Contains("Bluetooth identity itself"))
            && plainBt.All.Any(q => q.Id == "x-bt-plain"),
            string.Join("; ", plainBt.Issues.Select(i => $"{i.ProfileId} {i.JsonPath} {i.Reason}")));
        Refused("a composite form", Variant(dsbt, "x-bt", "dualsense-composite"), "\"usbip\"");
        Refused("a name nothing holds", Variant(dsbt, "x-bt", "no-such-profile"), "no loaded profile");
        Refused("the profile itself", Variant(dsbt, "x-bt", "x-bt"), "itself");
        var usb = System.IO.File.ReadAllText(CatalogFile("sony", "dualsense.json"));
        Refused("usbForm on a USB profile", usb.Replace("\"id\":\"dualsense\"", "\"id\":\"x-usb\",\"usbForm\":\"dualsense\""), "connection is not");
        Refused("usbForm beside usbConfiguration",
            Variant(dsbt, "x-bt", "dualsense").Replace("\"usbForm\":\"dualsense\",", "\"usbForm\":\"dualsense\",\"usbConfiguration\":{\"busSpeed\":\"high\"},"),
            "usbConfiguration both");
        // A chain cannot load, since a load refuses usbForm on a profile
        // that is not Bluetooth, and a Bluetooth form is refused first. The
        // rule still holds for a profile a program edits after its load.
        var hop = byId["dualsense"].Clone();
        hop.UsbForm = "dualsense-edge";
        string? chainWhy = ControllerProfile.UsbFormRefusal(byId["dualsense-bt"], hop);
        Check("a form that names a form of its own is refused", chainWhy?.Contains("of its own") == true, chainWhy ?? "taken");
        // A directory that holds both forms resolves the pair itself.
        var pair = ProfileDatabase.LoadTexts(new[]
        {
            ("x-bt.json", Variant(dsbt, "x-bt", "x-usb")),
            ("x-usb.json", usb.Replace("\"id\":\"dualsense\"", "\"id\":\"x-usb\"")),
        }, ModelRegistry.Catalog);
        var xbt = pair.All.FirstOrDefault(p => p.Id == "x-bt");
        Check("a USB form in the same load resolves", xbt?.UsbFormProfile?.Id == "x-usb",
            string.Join("; ", pair.Issues.Select(i => $"{i.ProfileId} {i.JsonPath} {i.Reason}")));

        Console.WriteLine("\n-- A copy keeps the form only as the same device --");
        var dsbtHm = new HMProfile(byId["dualsense-bt"]);
        var renamed = new HMProfileBuilder().FromProfile(dsbtHm).Id("my-ds-bt").Name("Mine").Build();
        Check("a renamed copy keeps usbForm", renamed.FormOn(HMBackend.Usbip).Id == "dualsense");
        var redrawn = new HMProfileBuilder().FromProfile(dsbtHm).Id("my-ds-bt2")
            .DescriptorHex(byId["dualsense-bt-full"].Descriptor!).Build();
        Check("a copy with another descriptor drops usbForm", ReferenceEquals(redrawn.FormOn(HMBackend.Usbip), redrawn));
        var moved = new HMProfileBuilder().FromProfile(dsbtHm).Id("my-ds-bt3").Connection("usb").Build();
        Check("a copy with another connection drops usbForm", ReferenceEquals(moved.FormOn(HMBackend.Usbip), moved));

        Console.WriteLine("\n-- The Bluetooth profiles that name no USB form --");
        foreach (var p in db.All.Where(p => string.Equals(p.Connection, "bluetooth", StringComparison.OrdinalIgnoreCase) && p.UsbForm == null)
                     .OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var hm = new HMProfile(p);
            bool xinput = p.UsesXinputhid;
            string why = BackendSelection.NotSupportedReason(p, HMBackend.Usbip) ?? "";
            if (xinput)
                Console.WriteLine($"  [INFO] {p.Id}: an xinputhid profile, the GIP function's (lane GIP). USB/IP here: {(why.Length > 0 ? why : "presentable")}");
            else
                Check($"{p.Id}: presents its own Bluetooth-format descriptor on USB/IP", hm.SupportsBackend(HMBackend.Usbip)
                    && ReferenceEquals(hm.FormOn(HMBackend.Usbip), hm), why);
        }
    }

    static string CatalogFile(string dir, string file)
    {
        for (var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string path = System.IO.Path.Combine(d.FullName, "profiles", dir, file);
            if (System.IO.File.Exists(path)) return path;
        }
        throw new System.IO.FileNotFoundException($"profiles/{dir}/{file} above {AppContext.BaseDirectory}");
    }

    /// <summary>dualsense-bt's file under another id, naming <paramref name="form"/>.</summary>
    static string Variant(string text, string id, string form) =>
        text.Replace("\"id\":\"dualsense-bt\"", $"\"id\":\"{id}\"").Replace("\"usbForm\":\"dualsense\"", $"\"usbForm\":\"{form}\"");

    static void Refused(string what, string text, string reason)
    {
        var db = ProfileDatabase.LoadTexts(new[] { ("x.json", text) }, ModelRegistry.Catalog);
        var issue = db.Issues.FirstOrDefault(i => i.Kind == HMProfileLoadIssueKind.Refused);
        Check($"{what} is refused at $.usbForm", issue != null && issue.JsonPath == "$.usbForm" && issue.Reason.Contains(reason),
            issue == null ? $"loaded {db.All.Count}" : $"{issue.JsonPath}: {issue.Reason}");
    }

    static string FieldJson(FieldSpec f) =>
        System.Text.Json.JsonSerializer.Serialize(f, HMJsonContext.Default.FieldSpec);

    static string JsonOf(ExtendedReportSpec? s) =>
        s == null ? "" : System.Text.Json.JsonSerializer.Serialize(s, HMJsonContext.Default.ExtendedReportSpec);

    /// <summary>The raw-frame lengths a profile's form takes, as the SDK
    /// counts them.</summary>
    static int[] RawLengths(ControllerProfile p)
    {
        var set = new SortedSet<int>();
        int n = DeviceOrchestrator.DriverInputReport(p).Length;
        set.Add(n); set.Add(n - 1);
        if (p.ExtendedReport is { Size: > 0 } e) { set.Add(e.Size); set.Add(e.Size - 1); }
        if (p.StateBlock is { Size: > 0 } b) set.Add(b.Size);
        set.Remove(0);
        return set.ToArray();
    }

    /// <summary>The names the SDK decodes output into: the extended output
    /// report's fields and the model's decoders' fields.</summary>
    static HashSet<string> OutputNames(ControllerProfile p)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in p.ExtendedOutputReport?.Fields ?? new List<FieldSpec>())
            names.Add(f.Semantic ?? f.Type);
        foreach (var d in p.GetOrCompileModel().Decoders)
            foreach (var f in d.Semantics) names.Add(f);
        return names;
    }
}
