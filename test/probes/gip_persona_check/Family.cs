// gip_persona_check, the family part of "model": every profile that reaches
// XInput through xinputhid presents the GIP function on the USB/IP backend,
// the device of the catalog's GIP model (profiles/models/gip-gamepad.json)
// with the profile's identity (sdk/HIDMaestro.Core/Internal/Usbip/
// GipFunction.cs). Offline, no elevation.
//
// The checks, each of its own layer:
//   - the metadata layout reproduces a real 1697's metadata byte for byte
//     from its fields, and Santroller's 218 bytes from theirs;
//   - the fragmenter equals the fragments xbox-one-s-full carries, which
//     xboxgip took live and handed back byte for byte;
//   - the model with xbox-one-s-full's identity bytes rebuilds that
//     profile's device, reports and protocol exactly, so the slots are the
//     only place the identities differ from the persona proven live;
//   - each catalog profile's form: its identity in the device descriptor and
//     the Hello, its metadata through the engine, reassembled, with the
//     security opt-out and the input report's length, the input report's
//     size, and a Bluetooth identity's data equal to its USB sibling's;
//   - no two forms, xbox-one-s-full among them, share a VID, PID and
//     firmware version with different metadata, since Windows caches
//     metadata by those;
//   - a profile with no identity, an unknown one or one that does not reach
//     XInput through xinputhid is refused.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static partial class Program
{
    // PlasticBand a7b5ca30, Docs/Descriptor Dumps/Xbox One/Microsoft 1st
    // Revision Gamepad.txt: a real 1697's (045E:02DD) metadata, 214 bytes.
    static readonly byte[] Real1697 = Bytes(
        "1000010000000000000000000000D600970016003700380041004900660000000000000000000801000000010001000100020001000300020000000200010002" +
        "0002000200030000080102030406070C1E07010405060A0C1E011A0057696E646F77732E58626F782E496E7075742E47616D657061640356FF7697FD9B8145AD" +
        "45B645BBA526D62C402E08DF07E145A5ABA3127AF197B5E71FF3B88673E940A9F82F21263ACFB7021700200E0001001000000000000000000000000000000017" +
        "00093C00010008000000000000000000000000000000");

    static readonly Guid GController = new("9776ff56-9bfd-4581-ad45-b645bba526d6");
    static readonly Guid GGamepad = new("082e402c-07df-45e1-a5ab-a3127af197b5");
    static readonly Guid GNavigation = new("b8f31fe7-7386-40e9-a9f8-2f21263acfb7");
    static readonly Guid GFunctionMap = new("ecddd2fe-d387-4294-bd96-1a712e3dc77d");

    static byte[] Message(byte type, ushort length, uint flags)
    {
        var e = new byte[23];
        e[0] = 23; e[2] = type; BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(3), length);
        e[5] = 1; BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(7), flags);
        return e;
    }

    /// <summary>The catalog's twelve xinputhid profiles and the identity
    /// each names.</summary>
    static readonly (string Id, string Identity, ushort Pid, string? Sibling)[] Family =
    {
        ("xbox-one-original", "xbox-one-1537", 0x02D1, null),
        ("xbox-one-rev1", "xbox-one-1697", 0x02DD, null),
        ("xbox-one-s", "xbox-one-s-1708", 0x02EA, null),
        ("xbox-one-s-bt", "xbox-one-s-1708", 0x02FD, "xbox-one-s"),
        ("xbox-one-s-bt-full", "xbox-one-s-1708", 0x02FD, "xbox-one-s"),
        ("xbox-series-xs", "xbox-series-1914", 0x0B12, null),
        ("xbox-series-xs-bt", "xbox-series-1914", 0x0B13, "xbox-series-xs"),
        ("xbox-elite-v1", "xbox-elite-1698", 0x02E3, null),
        ("xbox-elite-v2", "xbox-elite-series-2-1797", 0x0B00, null),
        ("xbox-elite-v2-bt", "xbox-elite-series-2-1797", 0x0B05, "xbox-elite-v2"),
        ("xbox-elite-v2-bt-v2", "xbox-elite-series-2-1797", 0x0B22, "xbox-elite-v2"),
        ("xbox-adaptive", "xbox-adaptive-1835", 0x0B0A, null),
    };

    /// <summary>The metadata the form's engine serves: the fragments a
    /// request and the host's two acknowledgements draw, reassembled.</summary>
    static byte[] ServedMetadata(ControllerProfile form, out string trace)
    {
        const string request = "04 20 01 00";
        var first = Packets(form, request);
        int total = first.Count == 1 && first[0].Length >= 6 ? Vlen(first[0], 4) : -1;
        string firstAck = "01 20 01 09 00 04 20 3A 00 00 00 00 00";
        var rest = Packets(form, request, firstAck);
        var t = BitConverter.GetBytes(total);
        var done = Packets(form, request, firstAck, $"01 20 01 09 00 04 20 {t[0]:X2} {t[1]:X2} 00 00 00 00");
        trace = $"{first.Count}+{rest.Count}+{done.Count} packets, total {total}";
        if (first.Count != 1 || rest.Count < 2 || done.Count != 1 || done[0].Length != 6 || done[0][3] != 0) return Array.Empty<byte>();
        return first.Concat(rest).SelectMany(x => x.Skip(6)).ToArray();
    }

    /// <summary>The two-byte length form of the GIP header (7 bits, the
    /// extend bit, then 7 more).</summary>
    static int Vlen(byte[] b, int at) => (b[at] & 0x7F) | (b[at] >= 0x80 ? b[at + 1] << 7 : 0);

    static string Canonical(JsonNode? n) => n switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => JsonSerializer.Serialize(kv.Key) + ":" + Canonical(kv.Value))) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        _ => n.ToJsonString(),
    };

    static void FamilyModel()
    {
        Console.WriteLine("\n=== model, family: every xinputhid profile as the GIP function ===");

        Console.WriteLine("\n-- the metadata layout against real metadata --");
        var real = GipFunction.Compile(
            new (ushort, ushort)[] { (1, 0), (1, 1), (1, 2), (1, 3), (2, 0), (2, 1), (2, 2), (2, 3) },
            Bytes("01 02 03 04 06 07 0C 1E"), Bytes("01 04 05 06 0A 0C 1E"),
            new[] { "Windows.Xbox.Input.Gamepad" }, new[] { GController, GGamepad, GNavigation },
            new[] { Message(0x20, 14, 0x10), Message(0x09, 60, 0x08) });
        Check("the layout from a real 1697's fields gives its 214 bytes (PlasticBand's dump)",
              real.AsSpan().SequenceEqual(Real1697), $"{real.Length} bytes, first difference at {FirstDifference(real, Real1697)}");
        var santroller = GipFunction.Compile(
            new (ushort, ushort)[] { (1, 0), (1, 1) },
            Bytes("01 02 03 04 06 07"), Bytes("01 04 05 06 0A"),
            new[] { "Windows.Xbox.Input.Gamepad" },
            new[] { GController, GGamepad, GNavigation, GFunctionMap, GipFunction.SecurityOptOut },
            new[] { Message(0x20, 32, 0x10), Message(0x09, 60, 0x08) });
        Check("the layout from Santroller's fields gives its 218 bytes",
              santroller.AsSpan().SequenceEqual(Santroller), $"first difference at {FirstDifference(santroller, Santroller)}");

        var persona = Catalog();
        if (persona == null) { Check("the catalog holds xbox-one-s-full", false); return; }
        var personaFrags = Packets(persona, MetadataRequest).Concat(Packets(persona, MetadataRequest, FirstAck))
            .Concat(Packets(persona, MetadataRequest, FirstAck, LastAck)).Select(x => { var c = (byte[])x.Clone(); c[2] = 0; return c; }).ToList();
        var ours = GipFunction.Fragments(ExpectedMetadata());
        Check("the fragmenter cuts the persona's 214 bytes into the packets xbox-one-s-full serves",
              ours.Count == personaFrags.Count && ours.Zip(personaFrags).All(z => z.First.AsSpan().SequenceEqual(z.Second)),
              string.Join(" ", ours.Select(x => Hex(x.AsSpan(0, 6)))));

        Console.WriteLine("\n-- the model rebuilds the persona proven live --");
        var model = (JsonObject)GipFunction.Model.DeepClone();
        model["firmware"]!["minor"] = "0x0230";
        model["identities"]!["persona-1708"] = new JsonObject
        {
            ["bcdDevice"] = "0x0100",
            ["firmware"] = new JsonObject { ["build"] = "0x0C82", ["revision"] = 0 },
            ["hardware"] = "0405010001000100",
            ["interfaces"] = new JsonArray(new JsonNode[] { GController.ToString(), GGamepad.ToString(), GNavigation.ToString(), GFunctionMap.ToString(), GipFunction.SecurityOptOut.ToString() }),
            ["inputSize"] = 36,
            ["inputProgram"] = JsonNode.Parse("[{\"bits\": [[112, 144]]}]"),
        };
        var basis = ProfileDatabase.LoadEmbedded().All.First(x => x.Id == "xbox-one-s").Clone();
        basis.Gip = new GipSpec { Identity = "persona-1708" };
        JsonObject? rebuilt = null;
        try { rebuilt = GipFunction.BuildJson(basis, model, "test model"); }
        catch (Exception e) { Check("the model builds with xbox-one-s-full's identity bytes", false, e.Message); }
        if (rebuilt != null)
        {
            var proven = JsonNode.Parse(Source(persona))!.AsObject();
            foreach (string key in new[] { "usbConfiguration", "stateBlock", "inputReports", "protocol", "layout", "vid", "pid", "productString", "manufacturerString", "backend", "deviceModel" })
            {
                string a = Canonical(rebuilt[key]), b = Canonical(proven[key]);
                Check($"{key} is xbox-one-s-full's", a == b, a == b ? "" : $"first difference at {FirstDifference(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b))}");
            }
        }

        Console.WriteLine("\n-- each catalog profile's form --");
        var catalog = ProfileDatabase.LoadEmbedded().All;
        var xinputhid = catalog.Where(x => x.UsesXinputhid).Select(x => x.Id).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Check("the catalog's xinputhid profiles are the twelve this part knows",
              xinputhid.SequenceEqual(Family.Select(f => f.Id).OrderBy(x => x, StringComparer.Ordinal)), string.Join(" ", xinputhid));
        var pins = Pins();
        var served = new Dictionary<string, (byte[] Metadata, byte[] Hello, ControllerProfile Form)>();
        var cacheKeys = new Dictionary<string, (string Id, byte[] Metadata)>();
        foreach (var (id, identity, pid, sibling) in Family)
        {
            var p = catalog.FirstOrDefault(x => x.Id == id);
            if (p == null) { Check($"{id}: in the catalog", false); continue; }
            ControllerProfile form;
            try { form = GipFunction.FormOf(p); }
            catch (Exception e) { Check($"{id}: its GIP form builds", false, e.Message); continue; }
            Check($"{id}: names {identity}, and its form builds and meets every load check on USB/IP",
                  p.Gip?.Identity == identity && form.RequiresUsbipBackend && form.ReportsOnGipInterface && !form.HasDescriptor && !form.UsesXinputhid);
            Check($"{id}: the USB/IP presentation is the form's, authored", UsbPresentation.Resolve(p) is { Derived: false } pr && pr.Spec.DeviceDescriptorHex == form.UsbConfiguration!.DeviceDescriptorHex);
            var set = new UsbDescriptorSet(form);
            Check($"{id}: device descriptor 045E:{pid:X4}, class FF/47/D0", set.DeviceDescriptor.Length == 18
                  && BinaryPrimitives.ReadUInt16LittleEndian(set.DeviceDescriptor.AsSpan(8)) == 0x045E
                  && BinaryPrimitives.ReadUInt16LittleEndian(set.DeviceDescriptor.AsSpan(10)) == pid
                  && Hex(set.DeviceDescriptor.AsSpan(4, 3)) == "FF47D0", Hex(set.DeviceDescriptor));

            var compiled = CompiledProtocol.Compile(form, set)!;
            var engine = new ProtocolEngine(compiled, "HM0123456789AB", 0, ProgramBinder.BindLayouts(compiled.Model));
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            engine.RunTimers(t0);
            engine.RunTimers(t0 + System.Diagnostics.Stopwatch.Frequency / 2);
            var hello = new byte[64];
            int hn = engine.TakeReply(64, hello);
            byte[] h = hn > 0 ? hello[..hn] : Array.Empty<byte>();
            Check($"{id}: the Hello announces 045E:{pid:X4} and firmware 0x4D48.0x0300",
                  hn == 32 && h[0] == 0x02 && h[3] == 0x1C && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(12)) == 0x045E
                  && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(14)) == pid
                  && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(16)) == 0x4D48 && BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(18)) == 0x0300,
                  Hex(h));

            var meta = ServedMetadata(form, out string trace);
            int total = meta.Length >= 16 ? BinaryPrimitives.ReadUInt16LittleEndian(meta.AsSpan(14)) : -1;
            var input = form.InputReports!.First(r => r.Name == "input");
            Check($"{id}: the metadata comes back whole through the request and two acknowledgements", meta.Length > 16 && total == meta.Length, $"{trace}, {meta.Length} bytes");
            if (meta.Length > 16)
            {
                int dev = 16, ifs = dev + BinaryPrimitives.ReadUInt16LittleEndian(meta.AsSpan(dev + 12));
                int nIfs = meta[ifs];
                var guids = Enumerable.Range(0, nIfs).Select(i => new Guid(meta.AsSpan(ifs + 1 + 16 * i, 16))).ToList();
                int fw = dev + BinaryPrimitives.ReadUInt16LittleEndian(meta.AsSpan(dev + 2));
                Check($"{id}: its one firmware version is the Hello's, and it lists the security opt-out",
                      meta[fw] == 1 && meta.AsSpan(fw + 1, 4).SequenceEqual(h.AsSpan(16, 4)) && guids.Contains(GipFunction.SecurityOptOut),
                      string.Join(" ", guids));
                int msgs = 16 + BinaryPrimitives.ReadUInt16LittleEndian(meta.AsSpan(16));
                var lengths = Enumerable.Range(0, meta[msgs]).ToDictionary(i => meta[msgs + 1 + 23 * i + 2], i => (int)BinaryPrimitives.ReadUInt16LittleEndian(meta.AsSpan(msgs + 1 + 23 * i + 3)));
                Check($"{id}: message 0x20 is the input report's payload, {input.Size - 4} bytes, and 0x09 is 60",
                      lengths.TryGetValue(0x20, out int l20) && l20 == input.Size - 4 && lengths.TryGetValue(0x09, out int l09) && l09 == 60,
                      string.Join(" ", lengths.Select(kv => $"{kv.Key:X2}:{kv.Value}")));
                string key = $"045E:{pid:X4} {Hex(h.AsSpan(16, 4))}";
                if (cacheKeys.TryGetValue(key, out var other))
                    Check($"{id}: shares Windows' metadata cache key {key} with {other.Id}, and the same metadata", other.Metadata.AsSpan().SequenceEqual(meta));
                else cacheKeys[key] = (id, meta);
            }
            Check($"{id}: its metadata matches the pin for its cache key in metadata-pins.txt", PinMismatch(form, pins) == null, PinMismatch(form, pins) ?? "");
            served[id] = (meta, h, form);
            if (sibling != null && served.TryGetValue(sibling, out var usb))
                Check($"{id}: takes {sibling}'s metadata and input report with its own PID",
                      usb.Metadata.AsSpan().SequenceEqual(meta)
                      && usb.Form.InputReports!.First(r => r.Name == "input").Size == input.Size
                      && usb.Hello.AsSpan(16).SequenceEqual(h.AsSpan(16)));
        }
        var personaKey = $"045E:02EA {Hex(Bytes("484D3002"))}";
        Check("xbox-one-s-full's cache key is no form's", !cacheKeys.ContainsKey(personaKey), personaKey);

        Console.WriteLine("\n-- refusals --");
        var one = catalog.First(x => x.Id == "xbox-series-xs");
        var none = one.Clone(); none.Gip = null;
        Check("an xinputhid profile with no gip.identity has no USB/IP presentation, and says why",
              UsbPresentation.NotPresentableReason(none) is { } r1 && r1.Contains("gip.identity", StringComparison.Ordinal), UsbPresentation.NotPresentableReason(none) ?? "null");
        var unknown = one.Clone(); unknown.Gip = new GipSpec { Identity = "no-such-pad" };
        Check("an unknown identity is refused by name",
              UsbPresentation.NotPresentableReason(unknown) is { } r2 && r2.Contains("no-such-pad", StringComparison.Ordinal), UsbPresentation.NotPresentableReason(unknown) ?? "null");
        string notXinputhid = "";
        try
        {
            var o = JsonNode.Parse(Source(catalog.First(x => x.Id == "xbox-360-wired")))!.AsObject();
            o["id"] = "user-gip"; o["gip"] = new JsonObject { ["identity"] = "xbox-series-1914" };
            var db = ProfileDatabase.LoadTexts(new[] { ("user.json", o.ToJsonString()) }, ModelRegistry.Catalog);
            notXinputhid = db.All.Count == 0 && db.Issues.Count == 1 ? $"{db.Issues[0].JsonPath}: {db.Issues[0].Reason}" : $"loaded {db.All.Count}";
        }
        catch (Exception e) { notXinputhid = e.Message; }
        Check("a gip block on a profile that does not reach XInput through xinputhid is refused at $.gip",
              notXinputhid.StartsWith("$.gip:", StringComparison.Ordinal), notXinputhid);
    }
}
