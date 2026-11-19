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
//                      <backend> (umdf2, usbip, or auto for what Auto
//                      resolves to here), and dualsense-composite as
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
        ("joycon-l", "joycon-l-usb"),
        ("joycon-r", "joycon-r-usb"),
        ("snes-nso", "snes-nso-usb"),
        ("n64-nso", "n64-nso-usb"),
        ("genesis-nso", "genesis-nso-usb"),
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
                    Console.WriteLine("usage: BtUsbFormCheck offline | live <umdf2|usbip|auto> | sdl <umdf2|usbip|auto> <SDL3 dir> [profile ids]");
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
        // What Auto resolves to here, HIDMAESTRO_BACKEND included: the
        // backend the regression battery runs.
        "auto" => HMContext.MachineBackend,
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
        Check("the catalog names a USB form on exactly the four Sony Bluetooth pads, switch-pro and the five Nintendo Bluetooth-only pads",
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
            // A form that names its identity in usbFormOf is the device's own
            // USB presentation and keeps its strings (the charging grip).
            var strings = form.FormOfIds?.Contains(bt.Id) == true ? form : bt;
            Check($"the presented profile keeps the Bluetooth id and name, and {(ReferenceEquals(strings, form) ? "the form's" : "the Bluetooth")} strings",
                p.Id == bt.Id && p.Name == bt.Name && p.ProductString == strings.ProductString
                && p.ManufacturerString == strings.ManufacturerString && p.DeviceDescription == strings.DeviceDescription,
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
            // A form with a configuration of its own (the charging grip)
            // keeps it, and any other form is the derived single HID function.
            bool authored = form.UsbConfiguration?.IsAuthored == true;
            Check(authored ? "the USB device is the form's own configuration" : "the USB device is derived",
                  pres.Derived == !authored);
            Check("the USB device is the form's: device descriptor", pres.Spec.DeviceDescriptorHex == formPres.Spec.DeviceDescriptorHex,
                pres.Spec.DeviceDescriptorHex ?? "");
            Check("the USB device is the form's: configuration", pres.Spec.ConfigurationDescriptorHex == formPres.Spec.ConfigurationDescriptorHex,
                pres.Spec.ConfigurationDescriptorHex ?? "");
            _ = new HIDMaestro.Internal.Usbip.UsbDescriptorSet(p);

            // The raw frames only the Bluetooth form takes.
            var table = (bool[]?)typeof(HMController).GetMethod("OtherFormRawLengths",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { bt, p });
            var lengths = table == null ? null : Enumerable.Range(0, table.Length).Where(i => table[i]).ToArray();
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
        Refused("a form with another VID", Variant(dsbt, "x-bt", "switch-pro-usb"), "VID and PID");
        Refused("a form with another PID that names no usbFormOf", Variant(dsbt, "x-bt", "dualshock-4-v2"), "VID and PID");
        Refused("a form whose usbFormOf names another identity", Variant(dsbt, "x-bt", "genesis-nso-usb"), "and not this profile");
        string gen = System.IO.File.ReadAllText(CatalogFile("nintendo", "genesis-nso.json"));
        string jcl = System.IO.File.ReadAllText(CatalogFile("nintendo", "joycon-l.json"));
        Refused("a Nintendo pad naming another pad's form",
            jcl.Replace("\"id\":\"joycon-l\"", "\"id\":\"x-jcl\"").Replace("\"usbForm\":\"joycon-l-usb\"", "\"usbForm\":\"genesis-nso-usb\""),
            "and not this profile or a copy of one");
        string genUsb = System.IO.File.ReadAllText(CatalogFile("nintendo", "genesis-nso-usb.json"));
        Refused("a usbFormOf that names a profile with no Bluetooth connection",
            genUsb.Replace("\"id\":\"genesis-nso-usb\"", "\"id\":\"x-gen-usb\"").Replace("\"usbFormOf\":[\"genesis-nso\"]", "\"usbFormOf\":[\"snes-nso-usb\"]"),
            "is not a Bluetooth identity", "$.usbFormOf[0]");
        // A copy of a named identity, its VID, PID and descriptor under
        // another id, takes the form.
        var copy = ProfileDatabase.LoadTexts(new[] { ("x.json", gen.Replace("\"id\":\"genesis-nso\"", "\"id\":\"x-gen-copy\"")) }, ModelRegistry.Catalog);
        Check("a copy of genesis-nso under another id takes its USB form",
            copy.All.Any(q => q.Id == "x-gen-copy") && !copy.Issues.Any(i => i.Kind == HMProfileLoadIssueKind.Refused),
            string.Join("; ", copy.Issues.Select(i => $"{i.JsonPath} {i.Reason}")));
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
        // On USB/IP the form's model, answers, driver PID and maps replace
        // the copy's own, so a copy that changes one is no longer the form's
        // device.
        CopyDrops(dsbtHm, "a button map", b => b.ButtonMap(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }));
        CopyDrops(dsbtHm, "an axis map", b => b.AxisMap(new Dictionary<string, string> { ["0x30"] = "leftStickX", ["0x31"] = "leftStickY" }));
        CopyDrops(dsbtHm, "a driver PID", b => b.DriverPid("0x0B13"));
        CopyDrops(dsbtHm, "feature answers", b => b.FeatureAnswers("{}"));
        CopyDrops(dsbtHm, "a device model by name", b => b.DeviceModelFrom("dualshock-4-v2-bt"));
        // A copy of a directory's Bluetooth profile keeps the form the
        // directory resolved, which the catalog does not hold.
        string copied;
        try
        {
            var xbtCopy = new HMProfileBuilder().FromProfile(new HMProfile(xbt!)).Id("x-bt-copy").Build();
            copied = xbtCopy.FormOn(HMBackend.Usbip).Id;
        }
        catch (HMProfileException e) { copied = e.Message; }
        Check("a copy of a directory's Bluetooth profile presents the directory's USB form", copied == "x-usb", copied);

        Console.WriteLine("\n-- SubmitState reads the sticks and triggers by the consumer's profile on both backends --");
        foreach (var (btId, _) in Forms)
        {
            var hm = new HMProfile(byId[btId]);
            var (umdf2, _) = SlotsOf(hm, hm, 7910);
            var (usbip, c) = SlotsOf(hm, hm.FormOn(HMBackend.Usbip), 7911);
            Check($"{btId}: the slots on USB/IP are the slots on UMDF2", umdf2 == usbip, $"UMDF2 [{umdf2}] USB/IP [{usbip}]");
            // The descriptor path reads the state by axis key, so a form
            // that puts a stick elsewhere gets the keys moved to its own.
            var formOn = hm.FormOn(HMBackend.Usbip);
            var given = HMGamepadStateHelpers.StandardAxes(hm, 0.5f, 0.5f, 1.0f, 0.25f, 0f, 0f);
            const System.Reflection.BindingFlags Own = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            bool moves = typeof(HMController).GetField("_formAxisMoves", Own)!.GetValue(c) != null;
            var read = moves ? (Dictionary<HMAxis, float>)typeof(HMController).GetMethod("FormAxes", Own)!.Invoke(c, new object?[] { given })! : given;
            if (hm.Sticks.Count > 1 && formOn.Sticks.Count > 1)
            {
                var (fx, fy) = (formOn.Sticks[1].XAxis, formOn.Sticks[1].YAxis);
                bool same = hm.Sticks[1].XAxis == fx && hm.Sticks[1].YAxis == fy;
                Check($"{btId}: the descriptor path reads the right stick at {formOn.Id}'s {fx}/{fy}" + (same ? ", the axes it was given" : ""),
                    read.TryGetValue(fx, out float x) && x == 1.0f && read.TryGetValue(fy, out float y) && y == 0.25f,
                    string.Join(" ", read.OrderBy(k => k.Key.ToString()).Select(k => $"{k.Key}={k.Value}")));
            }
        }

        Console.WriteLine("\n-- The Bluetooth profiles that name no USB form --");
        foreach (var p in db.All.Where(p => string.Equals(p.Connection, "bluetooth", StringComparison.OrdinalIgnoreCase) && p.UsbForm == null)
                     .OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            var hm = new HMProfile(p);
            bool xinput = p.UsesXinputhid;
            string why = BackendSelection.NotSupportedReason(p, HMBackend.Usbip) ?? "";
            if (xinput)
                Console.WriteLine($"  [INFO] {p.Id}: reaches XInput through xinputhid, which the GIP function presents on USB/IP. USB/IP here: {(why.Length > 0 ? why : "presentable")}");
            else
                Check($"{p.Id}: presents its own Bluetooth-format descriptor on USB/IP", hm.SupportsBackend(HMBackend.Usbip)
                    && ReferenceEquals(hm.FormOn(HMBackend.Usbip), hm), why);
        }
    }

    static void CopyDrops(HMProfile source, string what, Func<HMProfileBuilder, HMProfileBuilder> change)
    {
        string got;
        try
        {
            var copy = change(new HMProfileBuilder().FromProfile(source).Id("my-copy")).Build();
            got = ReferenceEquals(copy.FormOn(HMBackend.Usbip), copy) ? "own" : copy.FormOn(HMBackend.Usbip).Id;
        }
        catch (HMProfileException e) { got = e.Message; }
        Check($"a copy with {what} of its own drops usbForm", got == "own", got);
    }

    /// <summary>The axes a controller of <paramref name="profile"/> that
    /// presents <paramref name="presented"/> reads the state's sticks and
    /// triggers from, built offline on a buffer of this probe's as
    /// device_oracle_check's case 1 builds one: no section, event or driver
    /// is created.</summary>
    static (string Slots, HMController Controller) SlotsOf(HMProfile profile, HMProfile presented, int index)
    {
        const System.Reflection.BindingFlags Instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var view = Marshal.AllocHGlobal(SharedMemoryIO.SHARED_INPUT_SIZE);
        Marshal.Copy(new byte[SharedMemoryIO.SHARED_INPUT_SIZE], 0, view, SharedMemoryIO.SHARED_INPUT_SIZE);
        Dictionary<int, IntPtr> Cache(string field) => (Dictionary<int, IntPtr>)typeof(SharedMemoryIO)
            .GetField(field, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        var inputs = Cache("s_inputViews");
        lock (inputs) inputs[index] = view;
        var outputs = Cache("s_outputViews");
        lock (outputs) outputs[index] = IntPtr.Zero;
        var ctor = typeof(HMController).GetConstructor(Instance, new[]
        {
            typeof(HMContext), typeof(int), typeof(HMProfile), typeof(string),
            typeof(HIDMaestro.Internal.Usbip.UsbipBackendHandle), typeof(string), typeof(HMProfile),
        }) ?? throw new MissingMethodException("HMController's private constructor changed.");
        var context = (HMContext)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(HMContext));
        var c = (HMController)ctor.Invoke(new object?[] { context, index, profile, null, null, null, presented });
        string Field(string name) => typeof(HMController).GetField(name, Instance)!.GetValue(c)!.ToString()!;
        return (string.Join(" ", new[] { "_slotLx", "_slotLy", "_slotRx", "_slotRy", "_slotLtField", "_slotRtField", "_canonicalLt", "_canonicalRt" }
            .Select(n => $"{n.TrimStart('_')}={Field(n)}")), c);
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

    static void Refused(string what, string text, string reason, string path = "$.usbForm")
    {
        var db = ProfileDatabase.LoadTexts(new[] { ("x.json", text) }, ModelRegistry.Catalog);
        var issue = db.Issues.FirstOrDefault(i => i.Kind == HMProfileLoadIssueKind.Refused);
        Check($"{what} is refused at {path}", issue != null && issue.JsonPath == path && issue.Reason.Contains(reason),
            issue == null ? $"loaded {db.All.Count}" : $"{issue.JsonPath}: {issue.Reason}");
    }

    static string FieldJson(FieldSpec f) =>
        System.Text.Json.JsonSerializer.Serialize(f, HMJsonContext.Default.FieldSpec);

    static string JsonOf(ExtendedReportSpec? s) =>
        s == null ? "" : System.Text.Json.JsonSerializer.Serialize(s, HMJsonContext.Default.ExtendedReportSpec);

    /// <summary>The raw-frame lengths a profile's form takes, read from
    /// its descriptor's input reports and its own keys rather than from the
    /// SDK's count: each input report with and without its ID, the extended
    /// report with and without its ID, and the state block.</summary>
    static int[] RawLengths(ControllerProfile p)
    {
        var set = new SortedSet<int>();
        foreach (var r in p.GetOrBuildReportMap().Reports.Where(r => r.Kind == HidReportKind.Input))
        {
            set.Add(r.FrameBytes);
            set.Add(r.FrameBytes - (r.ReportId != 0 ? 1 : 0));
        }
        if (p.InputReportSize is int size and > 0) { set.Add(size); set.Add(size - 1); }
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
