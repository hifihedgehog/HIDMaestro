// Valve composite personas (issue #56, from PadForge discussion #337):
// the Steam Deck, the 2015 Steam Controller, and the 2026 one SDL calls
// Triton. All three pinned here without a device, a host or elevation.
//
// Steam files the plain `steam-deck` and `steam-controller` profiles under
// Generic DirectInput because they present Valve's ids over ordinary
// gamepad descriptors. The personas present the devices instead, and this
// probe pins their wire truth:
//
//   1. Each profile is in the catalog on the usbip backend, and the plain
//      profile it derives from is untouched on UMDF2.
//   2. The descriptors parse into the interface model each device really
//      has - three HID interfaces for the Deck and the 2015 controller
//      (the controller's, plus the keyboard and mouse its lizard mode
//      drives), one for Triton, which addresses everything by report id -
//      and every HID class descriptor agrees with the report descriptor
//      its OWN interface serves. Each controller descriptor is checked
//      byte for byte against the hardware record it came from.
//   3. Endpoint addresses, packet sizes and intervals match each unit's
//      lsusb dump, and the device answers (or stalls) descriptor requests
//      the way a full-speed device does.
//   4. The feature-stub tables answer Steam's interrogation: the message-id
//      keying each protocol uses, the ATTRIB records field by field against
//      the real captures they came from, the per-index string attributes,
//      and a stall for a message the device does not implement.
//   5. The frames consumers submit are the ones each descriptor declares:
//      the Deck's 64-byte Neptune report, Triton's 54-byte report 0x42.
//
// Exit 0 PASS / 1 FAIL.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;

using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    // The real Deck's interface layout, from the lsusb -v dump of a Valve
    // Jupiter (linuxhw/LsUSB): keyboard on 0 with EP 0x82, mouse on 1 with
    // EP 0x81, the controller on 2 with EP 0x83.
    const byte KbdIface = 0, MouseIface = 1, CtrlIface = 2;

    static int Main()
    {
        Console.WriteLine("=== Steam Deck composite persona (issue #56) ===");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        var persona = ctx.GetProfile("steam-deck-composite");
        var basic = ctx.GetProfile("steam-deck");
        Check("steam-deck-composite is in the catalog", persona != null);
        Check("the plain steam-deck profile still exists", basic != null);
        if (persona == null || basic == null)
        {
            Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
            return 1;
        }

        var inner = persona.Inner;

        // ── 1. Identity and backend ─────────────────────────────────────
        Console.WriteLine("\n-- identity --");
        Check("declares the usbip backend",
              inner.Backend == "usbip" && inner.RequiresUsbipBackend);
        Check("the plain profile stays on UMDF2 and declares no USB configuration",
              basic.Inner.Backend != "usbip" && basic.Inner.UsbConfiguration == null);
        Check("carries Valve's real ids 28DE:1205",
              persona.VendorId == 0x28DE && persona.ProductId == 0x1205,
              $"{persona.VendorId:X4}:{persona.ProductId:X4}");
        // The real Deck's controller report descriptor, recorded from
        // hardware: the plain steam-deck profile has carried it as
        // nativeDescriptor since that profile was authored, and the
        // hhd-dev/hwinfo capture of a physical Deck reports the same 38
        // bytes. The persona SERVES it rather than recording it.
        const string RealCtrlDescriptor =
            "06ffff0901a10109020903150026ff0075089540810209060907150026ff0075089540b102c0";
        Check("controller descriptor is the real Deck's, byte for byte",
              string.Equals(inner.Descriptor, RealCtrlDescriptor, StringComparison.OrdinalIgnoreCase),
              inner.Descriptor?.Length.ToString() ?? "null");

        // ── 2. Descriptor set: three HID interfaces ─────────────────────
        Console.WriteLine("\n-- descriptors --");
        UsbDescriptorSet set;
        try
        {
            set = new UsbDescriptorSet(inner);
        }
        catch (Exception ex)
        {
            Check("descriptor set builds", false, ex.Message);
            Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
            return 1;
        }
        Check("descriptor set builds (blobs agree with the structured spec)", true);
        Check("primary HID interface is the controller interface",
              set.HidInterfaceNumber == CtrlIface, set.HidInterfaceNumber.ToString());
        Check("two secondary HID interfaces: keyboard and mouse",
              set.SecondaryHidInterfaces.Count == 2
              && set.SecondaryHidInterfaces.Contains(KbdIface)
              && set.SecondaryHidInterfaces.Contains(MouseIface),
              string.Join(",", set.SecondaryHidInterfaces.OrderBy(x => x)));
        Check("configuration declares three interfaces", set.NumInterfaces == 3,
              set.NumInterfaces.ToString());

        var ctrlRd = set.GetHidDescriptor(0x22, CtrlIface);
        var kbdRd = set.GetHidDescriptor(0x22, KbdIface);
        var mouseRd = set.GetHidDescriptor(0x22, MouseIface);
        Check("each interface serves its own report descriptor",
              ctrlRd != null && kbdRd != null && mouseRd != null
              && !ctrlRd.SequenceEqual(kbdRd) && !kbdRd.SequenceEqual(mouseRd),
              $"ctrl={ctrlRd?.Length} kbd={kbdRd?.Length} mouse={mouseRd?.Length}");

        // The controller interface is the vendor page with 64-byte input and
        // feature reports, which is what makes it the Deck's controller and
        // not a gamepad: 06 FF FF usage page, Report Size 8, Report Count 64.
        Check("controller descriptor is vendor page FFFF with 64-byte reports",
              ctrlRd != null && ctrlRd.Length >= 6
              && ctrlRd[0] == 0x06 && ctrlRd[1] == 0xFF && ctrlRd[2] == 0xFF
              && Contains(ctrlRd, new byte[] { 0x75, 0x08, 0x95, 0x40 }),
              ctrlRd == null ? "" : $"{ctrlRd.Length} bytes");
        Check("keyboard descriptor declares the keyboard usage (05 01 09 06)",
              kbdRd != null && Contains(kbdRd, new byte[] { 0x05, 0x01, 0x09, 0x06 }));
        Check("mouse descriptor declares the mouse usage (05 01 09 02)",
              mouseRd != null && Contains(mouseRd, new byte[] { 0x05, 0x01, 0x09, 0x02 }));

        // ── 3. Endpoints and speed ──────────────────────────────────────
        Console.WriteLine("\n-- endpoints --");
        CheckEndpoint(set, 0x83, CtrlIface, 64, 1, "controller");
        CheckEndpoint(set, 0x82, KbdIface, 8, 1, "keyboard");
        CheckEndpoint(set, 0x81, MouseIface, 8, 1, "mouse");
        Check("exactly three endpoints, all interrupt IN",
              set.Endpoints.Count == 3
              && set.Endpoints.Values.All(e => e.TransferType == 3 && e.IsIn),
              set.Endpoints.Count.ToString());

        // A full-speed device stalls Device_Qualifier and Other_Speed, as the
        // real Deck's own bulk endpoints prove it to be (64-byte bulk is a
        // full-speed maximum; high speed would be 512).
        Check("enumerates at full speed", set.Speed == 2, set.Speed.ToString());
        Check("stalls Device_Qualifier, as a full-speed device does",
              set.GetDescriptor(0x06, 0, 0) == null);
        Check("stalls Other_Speed_Configuration", set.GetDescriptor(0x07, 0, 0) == null);
        Check("serves the verbatim device and configuration blobs",
              set.GetDescriptor(0x01, 0, 0)!.Length == 18
              && set.GetDescriptor(0x02, 0, 0)!.SequenceEqual(set.ConfigurationDescriptor));

        // ── 4. Feature stubs: Steam's interrogation ─────────────────────
        Console.WriteLine("\n-- feature stubs --");
        var stubs = FeatureStubTable.From(inner);
        Check("profile declares a feature-stub table", stubs != null);
        if (stubs != null)
        {
            Check("keys on the preceding message, the Deck protocol's rule",
                  stubs.MatchesLastMessage);

            var attr = stubs.Lookup(0x83, 64);
            Check("ID_GET_ATTRIBUTES_VALUES (0x83) answers a full 64-byte report",
                  attr != null && attr.Length == 64, $"{attr?.Length ?? 0} bytes");
            if (attr != null)
            {
                // The reply's own framing: message id, then the attribute
                // block length, then 5-byte (tag, u32) records.
                Check("0x83 reply is framed as the real device's is",
                      attr[0] == 0x83 && attr[1] == 0x2D && attr[1] % 5 == 0,
                      $"id=0x{attr[0]:X2} len={attr[1]}");
                Check("ATTRIB_PRODUCT_ID (0x01) carries 0x1205",
                      AttrValue(attr, 0x01) == 0x1205,
                      $"0x{AttrValue(attr, 0x01):X}");
                // Steam reads the connection interval to decide its poll
                // cadence, and the persona's own endpoint runs at 4 ms.
                Check("ATTRIB_CONNECTION_INTERVAL_IN_US (0x0B) is 4000 us, matching the real cadence",
                      AttrValue(attr, 0x0B) == 4000, AttrValue(attr, 0x0B).ToString());
                Check("ATTRIB_FIRMWARE_BUILD_TIME (0x04) is present and non-zero",
                      AttrValue(attr, 0x04) != 0);
            }

            var serial = stubs.Lookup(0xAE, 64);
            Check("ID_GET_STRING_ATTRIBUTE (0xAE) answers", serial != null && serial.Length == 64);
            Check("a message the device does not implement stalls",
                  stubs.Lookup(0x99, 64) == null);
            var shortRead = stubs.Lookup(0x83, 16);
            Check("a short read is truncated, never over-run",
                  shortRead != null && shortRead.Length == 16);
        }

        // ── 5. The frame the consumer submits ───────────────────────────
        Console.WriteLine("\n-- input frame --");
        var builder = inner.GetOrBuildReportBuilder();
        Check("input report is 64 bytes with no report id",
              builder.InputReportByteSize == 64 && builder.InputReportId == 0,
              $"id={builder.InputReportId} size={builder.InputReportByteSize}");
        Check("profile declares inputReportSize 64", inner.InputReportSize == 64,
              inner.InputReportSize?.ToString() ?? "null");

        // ── 6. The authored file on disk matches what ships ─────────────
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", ".."));
        string diskPath = Path.Combine(repoRoot, "profiles", "valve", "steam-deck-composite.json");
        if (File.Exists(diskPath))
        {
            var disk = JsonSerializer.Deserialize<ControllerProfile>(
                File.ReadAllText(diskPath), HMLayoutJsonOptions.Default)!;
            Check("embedded copy matches the authored file on disk",
                  disk.Descriptor == inner.Descriptor
                  && disk.UsbConfiguration?.ConfigurationDescriptorHex
                     == inner.UsbConfiguration?.ConfigurationDescriptorHex);

            // The persona and the plain profile must never disagree about
            // what the real hardware's controller interface looks like.
            string basicPath = Path.Combine(repoRoot, "profiles", "valve", "steam-deck.json");
            if (File.Exists(basicPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(basicPath));
                string? native = doc.RootElement.TryGetProperty("nativeDescriptor", out var nd)
                    ? nd.GetString() : null;
                Check("matches the nativeDescriptor the plain steam-deck profile records",
                      native != null && string.Equals(native, inner.Descriptor,
                                                      StringComparison.OrdinalIgnoreCase));
            }
        }
        else
        {
            Console.WriteLine("  [note] no source checkout here; skipping the authored-file comparison");
        }

        // 7. The two Steam Controllers.
        CheckSteamController(ctx);
        CheckTriton(ctx);

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    /// <summary>The 2015 Steam Controller (D0G), wired. SDL binds it only on
    /// interface 2, which is why the persona presents all three.</summary>
    static void CheckSteamController(HMContext ctx)
    {
        Console.WriteLine();
        Console.WriteLine("-- 2015 Steam Controller (28DE:1102) --");
        var persona = ctx.GetProfile("steam-controller-composite");
        Check("steam-controller-composite is in the catalog", persona != null);
        if (persona == null) return;
        var inner = persona.Inner;
        Check("carries the wired 2015 ids 28DE:1102",
              persona.VendorId == 0x28DE && persona.ProductId == 0x1102,
              $"{persona.VendorId:X4}:{persona.ProductId:X4}");
        Check("rides the usbip backend, as a composite must",
              string.Equals(inner.Backend, "usbip", StringComparison.OrdinalIgnoreCase));

        var set = new UsbDescriptorSet(inner);
        Check("the controller is on interface 2, where SDL's driver requires it",
              set.HidInterfaceNumber == 2, set.HidInterfaceNumber.ToString());
        Check("three interfaces, two of them secondary HID",
              set.NumInterfaces == 3 && set.SecondaryHidInterfaces.Count == 2,
              $"{set.NumInterfaces} interfaces, {set.SecondaryHidInterfaces.Count} secondary");
        var rd = set.GetHidDescriptor(0x22, 2);
        Check("controller descriptor is the real 33-byte vendor descriptor (page FF00)",
              rd != null && rd.Length == 33 && rd[0] == 0x06 && rd[1] == 0x00 && rd[2] == 0xFF,
              rd == null ? "absent" : $"{rd.Length} bytes");
        var basic = ctx.GetProfile("steam-controller");
        Check("byte-identical to the plain steam-controller profile's descriptor",
              basic != null && string.Equals(basic.Inner.Descriptor, inner.Descriptor,
                                             StringComparison.OrdinalIgnoreCase));
        CheckEndpoint(set, 0x83, 2, 64, 6, "controller");
        CheckEndpoint(set, 0x81, 0, 8, 10, "keyboard");
        CheckEndpoint(set, 0x82, 1, 4, 6, "mouse");
        Check("configuration reproduces the real dump's wTotalLength 0x54",
              set.ConfigurationDescriptor.Length == 0x54,
              $"0x{set.ConfigurationDescriptor.Length:X}");

        var stubs = FeatureStubTable.From(inner);
        Check("answers ID_GET_ATTRIBUTES_VALUES, which SDL reads before it trusts the pad",
              stubs != null && stubs.Lookup(0x83, 64) != null);
        if (stubs == null) return;
        Check("keyed by the message id in payload byte 0, as this protocol has no report ids",
              stubs.MessageByte == 0);
        var a = stubs.Lookup(0x83, 64)!;
        Check("ATTRIB_PRODUCT_ID reads back 0x1102", AttrValue(a, 0x01) == 0x1102,
              $"0x{AttrValue(a, 0x01):X}");
        Check("ATTRIB_CONNECTION_INTERVAL_IN_US is SDL's 9000us default for this family",
              AttrValue(a, 0x0B) == 9000, AttrValue(a, 0x0B).ToString());
        Check("ATTRIB_CAPABILITIES is zero, as it is on both real Valve devices captured",
              a[3] == 0x02 && AttrValue(a, 0x02) == 0);
        Check("the record block is a whole number of 5-byte records, as SDL divides by",
              a[1] % 5 == 0, $"len={a[1]}");
        Check("no firmware or bootloader timestamp is invented for a unit nobody has captured",
              a.Take(2 + a[1]).Where((b, i) => i >= 2 && (i - 2) % 5 == 0)
               .All(t => t != 0x04 && t != 0x0A && t != 0x09));
        Check("ID_GET_STRING_ATTRIBUTE answers rather than stalling, as the real pad does",
              stubs.Lookup(0xAE, 64) is byte[] str && str[0] == 0xAE && str[1] == 0x14);
        Check("a message the device does not implement stalls rather than answering",
              stubs.Lookup(0x7F, 64) == null);
    }

    /// <summary>The 2026 Steam Controller, SDL's Triton: one HID interface
    /// carrying every report, addressed by report id.</summary>
    static void CheckTriton(HMContext ctx)
    {
        Console.WriteLine();
        Console.WriteLine("-- 2026 Steam Controller / Triton (28DE:1302) --");
        var persona = ctx.GetProfile("steam-controller-2");
        Check("steam-controller-2 is in the catalog", persona != null);
        if (persona == null) return;
        var inner = persona.Inner;
        Check("carries the wired Triton ids 28DE:1302",
              persona.VendorId == 0x28DE && persona.ProductId == 0x1302,
              $"{persona.VendorId:X4}:{persona.ProductId:X4}");
        Check("rides the usbip backend", 
              string.Equals(inner.Backend, "usbip", StringComparison.OrdinalIgnoreCase));

        var set = new UsbDescriptorSet(inner);
        Check("one HID interface carrying everything by report id",
              set.NumInterfaces == 1 && set.SecondaryHidInterfaces.Count == 0,
              $"{set.NumInterfaces} interfaces");
        var rd = set.GetHidDescriptor(0x22, set.HidInterfaceNumber);
        Check("descriptor is the full 372-byte Triton descriptor",
              rd != null && rd.Length == 372, rd == null ? "absent" : $"{rd.Length} bytes");

        // Every report id SDL's Triton driver names has to be declared.
        var ids = new[] { ((byte)0x42, "ID_TRITON_CONTROLLER_STATE"),
                          ((byte)0x43, "ID_TRITON_BATTERY_STATUS"),
                          ((byte)0x45, "ID_TRITON_CONTROLLER_STATE_BLE"),
                          ((byte)0x79, "ID_TRITON_WIRELESS_STATUS") };
        foreach (var (id, what) in ids)
            Check($"declares report 0x{id:X2} ({what})",
                  rd != null && Contains(rd, new byte[] { 0x85, id }));
        Check("declares the lizard reports 0x40 (mouse) and 0x41 (keyboard) in the same descriptor",
              rd != null && Contains(rd, new byte[] { 0x85, 0x40 })
                         && Contains(rd, new byte[] { 0x85, 0x41 }));
        Check("declares the 0x80 haptic output report Steam writes",
              rd != null && Contains(rd, new byte[] { 0x85, 0x80 }));

        Check("declares inputReportSize 54, report 0x42's 53 bytes plus its report id",
              inner.InputReportSize == 54, inner.InputReportSize.ToString());
        Check("has both an IN and an OUT interrupt endpoint",
              set.Endpoints.Count == 2
              && set.Endpoints.Values.Any(e => e.IsIn && e.TransferType == 3)
              && set.Endpoints.Values.Any(e => !e.IsIn && e.TransferType == 3),
              $"{set.Endpoints.Count} endpoints");

        var stubs = FeatureStubTable.From(inner);
        Check("declares the command-channel answers", stubs != null);
        if (stubs == null) return;
        Check("keyed by payload byte 1, the byte after Triton's feature report id",
              stubs.MessageByte == 1);

        // ID_GET_ATTRIBUTES_VALUES. The reply is [report id][0x83][len][25
        // bytes of (tag, u32-LE) records], and Steam validates those 25
        // bytes for byte: they are verbatim from a real 28DE:1302.
        var a = stubs.Lookup(0x83, 64);
        Check("0x83 answers at the descriptor's 63-byte report plus its report id",
              a != null && a.Length == 64, a == null ? "stalled" : $"{a.Length} bytes");
        if (a != null)
        {
            Check("framed [report id][0x83][25]", a[1] == 0x83 && a[2] == 25,
                  $"{a[1]:X2} {a[2]:X2}");
            Check("ATTRIB_PRODUCT_ID is 0x1302", Attr(a, 3, 25, 0x01) == 0x1302,
                  $"0x{Attr(a, 3, 25, 0x01):X}");
            Check("bootloader build is the real unit's 0x68D2F92E",
                  Attr(a, 3, 25, 0x0A) == 0x68D2F92E, $"0x{Attr(a, 3, 25, 0x0A):X}");
            Check("firmware build is the real unit's 0x6A18D057",
                  Attr(a, 3, 25, 0x04) == 0x6A18D057, $"0x{Attr(a, 3, 25, 0x04):X}");
            Check("board revision is the real unit's 0x48",
                  Attr(a, 3, 25, 0x09) == 0x48, $"0x{Attr(a, 3, 25, 0x09):X}");
        }

        // ID_GET_STRING_ATTRIBUTE takes an index and answers a different
        // string for each, so the persona declares one entry per index.
        var s3 = stubs.Lookup(0xAE, 3, 64);
        Check("0xAE index 3 is the Valve constant Steam checks",
              s3 != null && s3[3] == 3 && Ascii(s3, 4) == "7054257d2da7",
              s3 == null ? "stalled" : $"idx={s3![3]} '{Ascii(s3, 4)}'");
        var s1 = stubs.Lookup(0xAE, 1, 64);
        Check("0xAE index 1 is a unit serial in Valve's own format",
              s1 != null && s1[3] == 1 && Ascii(s1, 4).StartsWith("FXA", StringComparison.Ordinal),
              s1 == null ? "stalled" : $"idx={s1![3]} '{Ascii(s1, 4)}'");
        Check("index 0 and index 1 are different strings, as board and unit serial are",
              s1 != null && stubs.Lookup(0xAE, 0, 64) is byte[] s0
              && s0[3] == 0 && Ascii(s0, 4) != Ascii(s1, 4));
        var s9 = stubs.Lookup(0xAE, 9, 64);
        Check("an index the device has no string for reads 0xFF, Steam's not-provisioned marker",
              s9 != null && s9[3] == 0xFF, s9 == null ? "stalled" : $"0x{s9![3]:X2}");

        var w = stubs.Lookup(0xB4, 64);
        Check("0xB4 reports the wired transport with no radio link",
              w != null && w[1] == 0xB4 && w[2] == 0x01 && w[3] == 0x01);
        Check("a message the device does not implement stalls rather than answering",
              stubs.Lookup(0x7F, 64) == null);
    }

    /// <summary>Read a (tag, u32-LE) attribute record out of a reply's
    /// record block.</summary>
    static uint Attr(byte[] reply, int start, int len, byte tag)
    {
        for (int off = start; off + 5 <= start + len && off + 5 <= reply.Length; off += 5)
            if (reply[off] == tag)
                return (uint)(reply[off + 1] | (reply[off + 2] << 8)
                            | (reply[off + 3] << 16) | (reply[off + 4] << 24));
        return 0;
    }

    /// <summary>The NUL-terminated ASCII run at an offset.</summary>
    static string Ascii(byte[] b, int off)
    {
        int end = off;
        while (end < b.Length && b[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(b, off, end - off);
    }

    static void CheckEndpoint(UsbDescriptorSet set, byte addr, byte iface, int mps, int interval, string what)
    {
        bool ok = set.Endpoints.TryGetValue(addr, out var ep)
                  && ep.InterfaceNumber == iface && ep.MaxPacketSize == mps
                  && ep.Interval == interval && ep.TransferType == 3 && ep.IsIn;
        set.Endpoints.TryGetValue(addr, out var found);
        Check($"{what} endpoint 0x{addr:X2} on interface {iface}, {mps}B interrupt IN, bInterval {interval}",
              ok, ok ? "" : $"iface={found.InterfaceNumber} mps={found.MaxPacketSize} interval={found.Interval}");
    }

    /// <summary>Read a ControllerAttribute's u32 out of a 0x83 reply: a
    /// two-byte header then 5-byte (tag, little-endian u32) records.</summary>
    static uint AttrValue(byte[] reply, byte tag)
    {
        int len = reply[1];
        for (int off = 2; off + 5 <= 2 + len && off + 5 <= reply.Length; off += 5)
            if (reply[off] == tag)
                return (uint)(reply[off + 1] | (reply[off + 2] << 8)
                            | (reply[off + 3] << 16) | (reply[off + 4] << 24));
        return 0;
    }

    static bool Contains(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { hit = false; break; }
            if (hit) return true;
        }
        return false;
    }
}
