// Backend parity check: a profile on the USB/IP backend against the same
// profile on the UMDF2 driver, live. Elevated, under the bench lock, on a
// test machine, never on the development laptop.
//
//   parity [ids]   Every deployable profile both backends present (or the ids
//                  given): created on each backend the machine runs, with the
//                  outcome Windows' own HID parser (hidparse.sys, run in this
//                  process as report_length_check does) predicts from the
//                  profile's descriptor: a descriptor it refuses fails the
//                  start with Code 10, as a real device with it does, and one
//                  it accepts starts with no problem, every HID collection
//                  under it read through the HID class: HidD_GetAttributes,
//                  HidP_GetCaps, the button and value caps of every report
//                  type, and the three strings. Where both backends run, the
//                  parsed fields and the product string must match. Whether
//                  the HID interface opened the moment the create returned is
//                  recorded for each.
//   long           Reports over 64 bytes (a profile with dualsense-bt's
//                  descriptor, joycon-l, apple-magic-keyboard-a1644) on
//                  USB/IP at the derived speed
//                  and pinned to full speed: one ReadFile returns one whole
//                  input report, and one output report written arrives whole.
//   ports          Creates past the host controller's USB 2.0 ports: the one
//                  past the last is refused by name and the rest stay up.
//   firstlife [n]  n creates on new identity keys and n on one key: the time
//                  from the call to an open of the HID interface, p50 and p95.
//   start [n]      n creates, each followed at once by an open of the HID
//                  interface, with no retry.
//   wgi            A derived gamepad and a gamepad built in code reach
//                  Windows.Gaming.Input as raw game controllers, and
//                  GameInputSvc runs.
//   oem            The joy.cpl name of the identity while a USB/IP controller
//                  lives, and the value before it once it goes.
//   identity first|second
//                  One identity key across a dispose and a process restart:
//                  the HID interface path comes back the same.
//   twoprograms    This process and a child each create USB/IP controllers on
//                  one host controller: their bus locations differ, and one's
//                  dispose leaves the other's device in place.
//   liveness       A stale sweep's answer to whether a server listens on a
//                  loopback port: another process's listener reads live, 16
//                  closed ports read gone, all within 3 s. Creates no device
//                  and needs no elevation.
//   ackgen         A delivered sequence number a torn-down USB/IP device
//                  stores after its index is made again does not land in
//                  the new controller's section. Offline, no elevation.
//   ignoreserial   One create with usbflags IgnoreHWSerNum set for its VID
//                  and PID: the start wait finds the device on its port.
//   portrule       The host controller's USB 2.0 port count and the imports
//                  that count against it. Offline, no elevation.
//   firstcreate [n]
//                  n fresh processes, each timing its first USB/IP create,
//                  p50 and p95.
//
// --backend umdf2|usbip|both limits parity to backends (default: every backend
// the machine runs). --out <file> writes one JSON line per profile.
// Exit 0 PASS, 1 FAIL, 2 SKIP.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;

internal static partial class Program
{
    static int s_total, s_failures;
    static readonly List<string> s_usbIds = new();

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static void Note(string text) => Console.WriteLine($"  [NOTE] {text}");

    static int Main(string[] args)
    {
        var list = args.ToList();
        string Take(string flag, string fallback)
        {
            int at = list.IndexOf(flag);
            if (at < 0 || at + 1 >= list.Count) return fallback;
            string v = list[at + 1];
            list.RemoveRange(at, 2);
            return v;
        }
        string backend = Take("--backend", "machine");
        string? output = Take("--out", "") is { Length: > 0 } o ? o : null;
        string part = list.Count > 0 ? list[0] : "parity";
        var rest = list.Skip(1).ToList();

        if (part == "listener") return Listener(int.Parse(rest[0]));
        if (part == "attachhang") return AttachHang(int.Parse(rest[0]));
        if (part is "liveness" or "ackgen" or "portrule" or "longlist")
        {
            if (part == "liveness") Liveness();
            else if (part == "ackgen") AckGeneration();
            else if (part == "longlist") LongList();
            else PortRule();
            Console.WriteLine();
            Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
            return s_failures == 0 ? 0 : 1;
        }

        using (var id = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
            {
                Console.WriteLine("  [SKIP] creating a controller needs elevation.");
                return 2;
            }
        if (part is not ("child" or "firstone") && SdkConsumers.Find() is { } other)
        {
            Console.WriteLine($"  [SKIP] {other} holds HIDMaestro state on this machine, and two programs that embed HIDMaestro never test at once.");
            return 2;
        }
        Note($"{System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}, Windows {Environment.OSVersion.Version}, " +
             $"MachineBackend {HMContext.MachineBackend}, SDK {typeof(HMContext).Assembly.GetName().Version}");

        try
        {
            switch (part)
            {
                case "parity": Parity(rest, backend, output); break;
                case "long": Long(); break;
                case "ports": Ports(); break;
                case "firstlife": FirstLife(rest.Count > 0 ? int.Parse(rest[0]) : 30); break;
                case "start": Start(rest.Count > 0 ? int.Parse(rest[0]) : 30); break;
                case "wgi": Wgi(); break;
                case "oem": Oem(); break;
                case "identity": Identity(rest.Count > 0 ? rest[0] : "first"); break;
                case "twoprograms": TwoPrograms(); break;
                case "killedattach": KilledAttach(); break;
                case "child": return Child(rest);
                case "ignoreserial": IgnoreSerial(); break;
                case "firstcreate": FirstCreate(rest.Count > 0 ? int.Parse(rest[0]) : 30); break;
                case "firstone": return FirstOne();
                default: Console.WriteLine($"unknown part {part}"); return 1;
            }
        }
        catch (Exception e) { Check($"{part}: the part ran to its end", false, $"{e.GetType().Name}: {e.Message}"); }

        if (s_usbIds.Count > 0)
            Note("USB instance ids this run created: " + string.Join(" ", s_usbIds.Distinct()));
        Console.WriteLine();
        Console.WriteLine($"=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    // ── What a controller presents ───────────────────────────────────────

    /// <summary>The devnode the controller's HID collections sit under: the
    /// UMDF2 parent, or the USB device on the host controller.</summary>
    static string? ParentOf(HMController c) =>
        c.Backend == HMBackend.Usbip ? c.UsbipHandle?.Device.Descriptors.UsbInstanceId : c.InstanceId;

    sealed record Snapshot(HMBackend Backend, string? Parent, bool OpenAtReturn, long CreateMs, long ReadyMs,
                           List<Collection> Collections, List<string> Errors, (bool Present, bool Started, uint Problem) State, string? Failure,
                           string Output = "", (ushort Vid, ushort Pid, ushort Version) Attributes = default, string? Product = null,
                           int HidInterfaces = 0, string FirstLook = "");

    /// <summary>The HID interfaces a USB/IP controller's device declares,
    /// the primary one and each secondary one. 0 on UMDF2.</summary>
    static int HidInterfacesOf(HMController c) =>
        c.UsbipHandle?.Device.Descriptors is { } d ? (d.HasHidInterface ? 1 : 0) + d.SecondaryHidInterfaces.Count : 0;

    /// <summary>The VID, PID and version Windows gives the controller's HID
    /// collections, as it gives them a real device of that kind. On USB/IP a
    /// device whose presented form is a GIP device has its HID collection on
    /// the child Windows' GIP driver makes, which carries that driver's own
    /// VID, PID and version (the GIP model's hidChild) whatever the device
    /// announces, and a Bluetooth pad that reaches XInput through xinputhid
    /// presents its Bluetooth HID form's version (bluetoothHid). Every other
    /// device, on either backend, presents the profile's own VID, PID and
    /// version.</summary>
    static (ushort Vid, ushort Pid, ushort Version) AttributesOf(HMController c, HMProfile p)
    {
        if (c.Backend != HMBackend.Usbip) return (p.VendorId, p.ProductId, p.Inner.VersionNumber ?? 0x0100);
        if (c.Presented.Inner.ReportsOnGipInterface) return HIDMaestro.Internal.Usbip.GipFunction.HidChild;
        var form = p.FormOn(HMBackend.Usbip);
        return (form.VendorId, form.ProductId, form.Inner.VersionNumber ?? 0x0100);
    }

    /// <summary>The product string Windows gives the HID collection of a
    /// device whose presented form is a GIP device on USB/IP: the GIP
    /// driver's own (the GIP model's hidChild), whatever the device serves.
    /// Null for every other device, whose product string is the one the
    /// two backends compare.</summary>
    static string? ProductOf(HMController c) =>
        c.Backend == HMBackend.Usbip && c.Presented.Inner.ReportsOnGipInterface ? HIDMaestro.Internal.Usbip.GipFunction.HidChildProduct : null;

    static Snapshot Take(HMContext ctx, HMProfile p, HMBackend backend, string key)
    {
        var sw = Stopwatch.StartNew();
        HMController c;
        try { c = ctx.CreateController(p, key, backend); }
        catch (Exception e) { return new(backend, null, false, sw.ElapsedMilliseconds, -1, new(), new(), default, $"{e.GetType().Name}: {e.Message}"); }
        long createMs = sw.ElapsedMilliseconds;
        try
        {
            string? parent = ParentOf(c);
            if (parent == null) return new(c.Backend, null, false, createMs, -1, new(), new(), default, "no parent instance id");
            if (c.Backend == HMBackend.Usbip) s_usbIds.Add(parent);
            // The moment the create returned: one look, no retry.
            var first = Hid.Read(parent, out var firstErrors);
            bool openAtReturn = first.Count > 0 && firstErrors.Count == 0;
            // What the look saw when the interface was not open: each
            // devnode under the device, its state and HID interfaces, and
            // the opens that failed.
            string firstLook = openAtReturn ? "" :
                string.Join("; ", Hid.Descendants(parent).Select(d =>
                    $"{d} {(Hid.Status(d) is var st && st.Started ? "started" : st.Present ? $"present problem {st.Problem}" : "absent")} {Hid.Interfaces(d).Count} interface(s)"))
                + (firstErrors.Count > 0 ? " | " + string.Join("; ", firstErrors) : "") + $" | {first.Count} collection(s) read";
            var collections = first;
            var errors = firstErrors;
            long readyMs = openAtReturn ? createMs : -1;
            while (!openAtReturn && sw.ElapsedMilliseconds < createMs + 10000)
            {
                Thread.Sleep(50);
                collections = Hid.Read(parent, out errors);
                if (collections.Count > 0 && errors.Count == 0) { readyMs = sw.ElapsedMilliseconds; break; }
            }
            return new(c.Backend, parent, openAtReturn, createMs, readyMs, collections, errors, Hid.Status(parent), null,
                       OutputOf(c, p, collections), AttributesOf(c, p), ProductOf(c), HidInterfacesOf(c), firstLook);
        }
        finally { c.Dispose(); }
    }

    /// <summary>An output report written to the first collection that has
    /// one and is not a keyboard's, a keypad's or a mouse's, and the packet
    /// OutputReceived raises for it: source, report ID and data.
    /// "none" when nothing arrives within a second, "not opened" for a
    /// collection that does not open for writing, and "held by Windows"
    /// when only such a collection has an output report.</summary>
    static string OutputOf(HMController c, HMProfile p, List<Collection> collections)
    {
        // Windows opens a keyboard's, a keypad's and a mouse's collection for
        // itself, and whether a program's open for writing reaches one in the
        // moments after the device starts depends on when Windows' input
        // stack took it: "not opened", or on the laptop on 2026-11-19 an open
        // whose write failed with Incorrect function. The output path is
        // read through the first other collection with an output report.
        static bool HeldByWindows(Collection x) => x.UsagePage == 0x01 && x.Usage is 0x02 or 0x06 or 0x07;
        var target = collections.FirstOrDefault(x => x.OutputLength > 0 && !HeldByWindows(x));
        if (target == null) return collections.Any(x => x.OutputLength > 0) ? "held by Windows" : "";
        // The report the device's own descriptor declares: on USB/IP that is
        // the presented form's (HMProfile.FormOn).
        var map = p.FormOn(c.Backend).Inner.GetOrBuildReportMap();
        var report = map.Reports.Where(r => r.Kind == HidReportKind.Output).OrderBy(r => r.ReportId).FirstOrDefault();
        if (report == null) return "";
        var buf = new byte[target.OutputLength];
        buf[0] = map.UsesReportIds ? report.ReportId : (byte)0;
        for (int i = 1; i < buf.Length; i++) buf[i] = (byte)(i * 3 + 1);
        // Another program on the machine can write to the same device. On
        // the laptop on 2026-11-19 one sent the virtual Switch Pro a
        // subcommand of its own, and the first packet raised was that
        // program's. So a raw output report counts as this write's only
        // when it carries this write's bytes, four of the pattern in a row
        // from its third byte, which the packet's data holds with or without
        // report IDs. A packet of another kind is the device's own reading
        // of the write and is taken at once, and a raw report without the
        // bytes is taken only when nothing else comes within the second.
        byte[] mark = buf.Length > 2 ? buf.AsSpan(2, Math.Min(4, buf.Length - 2)).ToArray() : Array.Empty<byte>();
        string? seen = null, other = null;
        var got = new ManualResetEventSlim();
        var sync = new object();
        void Handler(HMController _, HMOutputPacket pkt)
        {
            string text = $"{pkt.Source} 0x{pkt.ReportId:X2} {Convert.ToHexString(pkt.Data.Span)}";
            lock (sync)
            {
                if (seen != null) return;
                bool raw = pkt.Source == HMOutputSource.HidOutput;
                if (!raw || mark.Length == 0 || pkt.Data.Span.IndexOf(mark) >= 0) { seen = text; got.Set(); }
                else other ??= text;
            }
        }
        c.OutputReceived += Handler;
        try
        {
            using var h = Hid.Open(target.Path, 0x40000000, overlapped: false);
            if (h.IsInvalid) return "not opened";
            using var fs = new FileStream(h, FileAccess.Write, 1, isAsync: false);
            try { fs.Write(buf, 0, buf.Length); }
            catch (IOException e) { return $"write failed: {e.Message}"; }
            got.Wait(1000);
            lock (sync) return seen ?? other ?? "none";
        }
        finally { c.OutputReceived -= Handler; }
    }

    // ── parity ───────────────────────────────────────────────────────────

    static void Parity(List<string> ids, string backend, string? output)
    {
        Console.WriteLine();
        Console.WriteLine("=== The same profile on both backends ===");
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        bool umdf2Here = HMContext.MachineBackend == HMBackend.Umdf2 && backend is "machine" or "both" or "umdf2";
        bool usbipHere = backend is "machine" or "both" or "usbip";
        var profiles = (ids.Count > 0 ? ids.Select(i => ctx.GetProfile(i) ?? throw new ArgumentException($"no profile {i}")) : ctx.AllProfiles)
            .Where(p => p.IsDeployable && p.SupportsBackend(HMBackend.Usbip) && p.SupportsBackend(HMBackend.Umdf2))
            .OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        // A profile whose USB/IP presentation is an XUSB function reaches
        // Windows through xusb22, which makes the HID device itself from the
        // subtype, so its HID collection is xusb22's and not the profile's
        // descriptor. xusb_persona_check's family part compares those
        // profiles across backends where they meet: XInput's capabilities,
        // state and packets.
        var xusb = profiles.Where(p => HIDMaestro.Internal.Usbip.UsbPresentation.Resolve(p.Inner).Kind == HIDMaestro.Internal.Usbip.UsbPresentationKind.XusbFunction).ToList();
        if (xusb.Count > 0)
        {
            Note($"{xusb.Count} present an XUSB function on USB/IP and are compared by xusb_persona_check family: {string.Join(", ", xusb.Select(p => p.Id))}");
            profiles = profiles.Except(xusb).ToList();
        }
        Note($"{profiles.Count} profiles, UMDF2 {(umdf2Here ? "on" : "off")}, USB/IP {(usbipHere ? "on" : "off")}");
        // HID class hands the descriptor to hidparse.sys when the device
        // starts, and a status other than success fails the start with Code
        // 10 (WindowsParser.cs). The same parser, run here on each profile's
        // descriptor, says which outcome each backend owes.
        bool parser = WindowsParser.TryLoad(out string why);
        Check($"Windows' HID parser loads in this process from {WindowsParser.ImagePath}", parser, why);
        if (!parser) return;
        using var log = output != null ? new StreamWriter(output, append: false) : null;
        int matched = 0, umdf2Up = 0, usbipUp = 0, openAtReturn = 0, accepted = 0;
        var bad = new List<string>();
        var attributesOff = new HashSet<string>();
        // USB/IP devices a driver package on this machine binds in place of
        // HID class, as it binds the real device of that VID and PID
        // (PadForge's ds3_winusb.inf binds WinUSB to 054C:0268). They carry
        // no HID collection, so the USB/IP checks leave them out and name
        // them.
        var foreign = new List<string>();
        var ownForms = new List<string>();
        int foreignAccepted = 0;
        foreach (var p in profiles)
        {
            string key = "parity:" + p.Id;
            var verdict = WindowsParser.Parse(p.Inner.GetDescriptorBytes()!);
            bool accepts = verdict.Ok;
            if (accepts) accepted++;
            Snapshot? u = umdf2Here ? Take(ctx, p, HMBackend.Umdf2, key) : null;
            Snapshot? s = usbipHere ? Take(ctx, p, HMBackend.Usbip, key) : null;
            var problems = new List<string>();
            // A descriptor Windows refuses owes a start that fails with Code
            // 10 and nothing else.
            static bool Code10(Snapshot x) =>
                x.Failure != null && System.Text.RegularExpressions.Regex.IsMatch(x.Failure, @"problem code 10\b");
            void Up(Snapshot? x, ref int count)
            {
                if (x == null) return;
                if (!accepts)
                {
                    if (Code10(x)) count++;
                    else problems.Add($"{x.Backend}: Windows' parser refuses the descriptor ({verdict}), so the start owes Code 10, and it gave {x.Failure ?? $"devnode {x.State}"}");
                    return;
                }
                if (x.Failure != null) problems.Add($"{x.Backend} create: {x.Failure}");
                else if (!x.State.Present || !x.State.Started || x.State.Problem != 0) problems.Add($"{x.Backend} devnode {x.State}");
                else if (x.Collections.Count == 0) problems.Add($"{x.Backend} no HID collection ({string.Join("; ", x.Errors)})");
                else if (x.Errors.Count > 0) problems.Add($"{x.Backend} {string.Join("; ", x.Errors)}");
                else count++;
            }
            if (s is { Failure: null, Parent: { } usbDevice } && s.Collections.Count == 0
                && Hid.Binding(usbDevice) is var bound && bound.Service.Length > 0
                && !bound.Service.Equals("HidUsb", StringComparison.OrdinalIgnoreCase)
                && !bound.Service.Equals("usbccgp", StringComparison.OrdinalIgnoreCase))
            {
                foreign.Add($"{p.Id} ({bound.Service} from {bound.Inf}, {bound.Provider})");
                if (accepts) foreignAccepted++;
                Up(u, ref umdf2Up);
                Console.WriteLine($"  {p.Id,-44} USB/IP device bound by {bound.Service} from {bound.Inf} ({bound.Provider}), as that package binds the real device: no HID collection, left out of the USB/IP checks" +
                                  (u != null ? $"  umdf2 {u.CreateMs} ms" : "") + (problems.Count > 0 ? " | " + string.Join(" | ", problems) : ""));
                if (problems.Count > 0) bad.Add(p.Id);
                continue;
            }
            Up(u, ref umdf2Up);
            Up(s, ref usbipUp);
            if (accepts && s is { OpenAtReturn: true }) openAtReturn++;
            foreach (var x in new[] { u, s })
                if (x != null && x.Failure == null)
                    foreach (var c in x.Collections)
                        if (c.Vid != x.Attributes.Vid || c.Pid != x.Attributes.Pid || c.Version != x.Attributes.Version)
                        {
                            problems.Add($"{x.Backend} attributes {c.Vid:X4}:{c.Pid:X4}:{c.Version:X4}, Windows gives this kind of device {x.Attributes.Vid:X4}:{x.Attributes.Pid:X4}:{x.Attributes.Version:X4}");
                            attributesOff.Add(p.Id);
                        }
                        else if (x.Product != null && c.Product != x.Product)
                        {
                            problems.Add($"{x.Backend} product string {c.Product}, Windows gives this kind of device {x.Product}");
                            attributesOff.Add(p.Id);
                        }
            // A GIP form and a Bluetooth HID form are compared as any other
            // profile is: xinputhid gives programs the same HID device on
            // both backends. A GIP form's product string is checked above.
            // One difference is the profile's own. A Bluetooth pad's
            // descriptor can declare top-level collections its UMDF2 form's
            // does not, as the Elite Series 2's declares a keyboard. On
            // USB/IP the device presents the pad's own descriptor, and
            // Windows starts those collections from build 26100. So USB/IP
            // may show up to that many more, beside the ones both forms
            // share, which must read the same. identity_fidelity_check
            // (S115) pins each such collection by its usage.
            if (!accepts && u != null && s != null && Code10(u) && Code10(s)) matched++;
            else if (u != null && s != null && u.Failure == null && s.Failure == null)
            {
                var uk = u.Collections.Select(c => c.FieldsKey).ToList();
                var sk = s.Collections.Select(c => c.FieldsKey).ToList();
                int own = p.Inner.PresentsBluetoothHid
                    ? HIDMaestro.Internal.Usbip.UsbDescriptorSet.TopLevelCollections(p.Inner.BluetoothHidForm().GetDescriptorBytes()!)
                      - HIDMaestro.Internal.Usbip.UsbDescriptorSet.TopLevelCollections(p.Inner.GetDescriptorBytes()!)
                    : 0;
                var beyond = new List<Collection>(s.Collections);
                bool shared = u.Collections.All(c => beyond.Remove(beyond.FirstOrDefault(d => d.FieldsKey == c.FieldsKey && (s.Product != null || d.Product == c.Product))!));
                bool ownMore = own > 0 && shared && beyond.Count > 0 && beyond.Count <= own;
                if (!uk.SequenceEqual(sk) && !ownMore)
                    problems.Add($"parsed fields differ: UMDF2 {uk.Count} collection(s), USB/IP {sk.Count}" +
                                 (uk.Count > 0 && sk.Count > 0 ? $", UMDF2 {uk[0]} against USB/IP {sk[0]}" : ""));
                // A GIP form's product string is the GIP driver's, checked
                // above against the model, so only the others compare.
                else if (!ownMore && s.Product == null && !u.Collections.Select(c => c.Product).SequenceEqual(s.Collections.Select(c => c.Product)))
                    problems.Add($"product string {u.Collections[0].Product} against {s.Collections[0].Product}");
                else if (u.Output != s.Output)
                    problems.Add($"output packet UMDF2 '{u.Output}' against USB/IP '{s.Output}'");
                else
                {
                    matched++;
                    if (ownMore) ownForms.Add($"{p.Id} ({string.Join(", ", beyond.Select(c => $"{c.UsagePage:X4}:{c.Usage:X4}"))})");
                }
            }
            string line = problems.Count == 0 ? (accepts ? "ok" : $"ok, Code 10 as Windows' parser predicts ({verdict})") : string.Join(" | ", problems);
            Console.WriteLine($"  {p.Id,-44} {line}" +
                (s != null ? $"  usbip {s.CreateMs} ms{(s.OpenAtReturn ? "" : $", ready {s.ReadyMs} ms")}" : "") +
                (u != null ? $"  umdf2 {u.CreateMs} ms" : "") +
                (s != null && accepts && !s.OpenAtReturn ? $"\n      first look on USB/IP: {s.FirstLook}" : ""));
            if (problems.Count > 0) bad.Add(p.Id);
            log?.WriteLine(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["id"] = p.Id, ["problems"] = problems, ["windowsParser"] = verdict.ToString(),
                ["umdf2"] = Summary(u), ["usbip"] = Summary(s),
            }));
            log?.Flush();
        }
        Note($"Windows' parser accepts {accepted} of {profiles.Count} descriptors");
        if (foreign.Count > 0)
            Note($"{foreign.Count} USB/IP device(s) bound by a driver package on this machine in place of HID class, left out of the USB/IP checks: {string.Join(", ", foreign)}");
        if (ownForms.Count > 0)
            Note($"{ownForms.Count} Bluetooth pad(s) show on USB/IP the collections their own descriptor declares beyond the UMDF2 form's: {string.Join(", ", ownForms)}");
        Check("the parser made no call this process does not bind, so its verdicts are Windows' own", WindowsParser.UnexpectedCalls == 0, $"{WindowsParser.UnexpectedCalls}");
        if (umdf2Here) Check("every profile reaches the outcome Windows' parser predicts on UMDF2: it starts and enumerates its HID collections, or fails with Code 10 where the parser refuses its descriptor",
                             umdf2Up == profiles.Count, $"{umdf2Up} of {profiles.Count}");
        int onUsbip = profiles.Count - foreign.Count;
        if (usbipHere) Check("every profile reaches the outcome Windows' parser predicts on USB/IP: it starts and enumerates its HID collections, or fails with Code 10 where the parser refuses its descriptor",
                             usbipUp == onUsbip, $"{usbipUp} of {onUsbip}");
        if (umdf2Here && usbipHere)
            Check("both backends reach the same outcome: Code 10 on both, or the same attributes, caps and product string, and an output report that reaches the program as the same packet",
                  matched == onUsbip, $"{matched} of {onUsbip}");
        if (usbipHere)
            Check("on USB/IP the HID interface opens the moment the create returns, for every descriptor Windows' parser accepts", openAtReturn == accepted - foreignAccepted, $"{openAtReturn} of {accepted - foreignAccepted}");
        Check("every HID collection carries the attributes Windows gives that kind of device, and a GIP form's the product string Windows' GIP driver gives, on each backend here",
              attributesOff.Count == 0, string.Join(", ", attributesOff));
        if (bad.Count > 0) Note("differ or failed: " + string.Join(", ", bad));
    }

    static object? Summary(Snapshot? x) => x == null ? null : new Dictionary<string, object?>
    {
        ["backend"] = x.Backend.ToString(), ["parent"] = x.Parent, ["failure"] = x.Failure,
        ["createMs"] = x.CreateMs, ["readyMs"] = x.ReadyMs, ["openAtReturn"] = x.OpenAtReturn,
        ["state"] = $"{x.State.Present}/{x.State.Started}/{x.State.Problem}",
        ["output"] = x.Output,
        ["collections"] = x.Collections.Select(c => new Dictionary<string, object?>
        {
            ["usage"] = $"{c.UsagePage:X4}:{c.Usage:X4}", ["in"] = c.InputLength, ["out"] = c.OutputLength, ["feature"] = c.FeatureLength,
            ["manufacturer"] = c.Manufacturer, ["product"] = c.Product, ["serial"] = c.Serial, ["path"] = c.Path,
            ["parsed"] = c.ParsedKey,
        }).ToList(),
        ["errors"] = x.Errors,
    };
}
