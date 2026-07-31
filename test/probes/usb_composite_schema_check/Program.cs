// Composite USB persona schema check (issue #39).
//
// The schema family and the backend gate are additive data plumbing: no
// device is created, no driver is touched, and the five existing USB
// Sony profiles must be bit-for-bit unaffected. This probe asserts all
// of that, plus the two properties that make the design safe:
//
//   1. A composite profile parses into the full four-interface model
//      with its endpoint parameters and channel roles intact.
//   2. The HID interface is byte-identical to the UMDF2 profile it
//      derives from, so the existing report codec carries over free.
//   3. CreateController REFUSES a usbip-backend profile rather than
//      quietly building the one interface UMDF2 can build.
//
// Requires no elevation: nothing here creates a device.
// Exit 0 PASS / 1 FAIL.

using System;
using System.IO;
using System.Linq;
using System.Text.Json;

using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    static int s_total, s_failures;

    static void Check(string name, bool cond, string detail = "")
    {
        s_total++;
        if (!cond) s_failures++;
        Console.WriteLine($"  [{(cond ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static int Main()
    {
        Console.WriteLine("=== Composite USB persona schema (issue #39) ===");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        var baseProfile = ctx.GetProfile("dualsense");
        Check("base 'dualsense' profile loads from the shipped catalog", baseProfile != null);

        // The composite profile is authored but deliberately NOT embedded
        // until the backend that can instantiate it exists. Shipping it in
        // the catalog now would put an entry in every consumer's picker
        // that CreateController refuses. Assert that, then read the file
        // from the repo, which is where it lives as authored ground truth.
        Check("composite is NOT in the shipped catalog yet",
              ctx.GetProfile("dualsense-composite") == null);

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", ".."));
        string compositePath = Path.Combine(repoRoot, "profiles", "sony", "dualsense-composite.json");
        Check("authored composite profile exists on disk", File.Exists(compositePath), compositePath);
        if (baseProfile == null || !File.Exists(compositePath)) { Summary(); return 1; }

        // The SDK's own serializer options: HMLayout is polymorphic and
        // needs its converter, same options LoadDefaultProfiles uses.
        var inner = JsonSerializer.Deserialize<ControllerProfile>(
            File.ReadAllText(compositePath), HMLayoutJsonOptions.Default);
        Check("composite profile parses", inner != null);
        if (inner == null) { Summary(); return 1; }
        var composite = new HMProfile(inner);

        // ── Backend gating ──────────────────────────────────────────────
        Console.WriteLine("\n-- Backend gate --");
        Check("base profile stays on the UMDF2 path", baseProfile.Backend == "umdf2", baseProfile.Backend);
        Check("base profile needs no opt-in backend", !baseProfile.RequiresUsbipBackend);
        Check("base profile is unchanged by this work (still deployable)", baseProfile.IsDeployable);
        Check("composite declares the usbip backend", composite.Backend == "usbip", composite.Backend);
        Check("composite reports RequiresUsbipBackend", composite.RequiresUsbipBackend);

        // Every other shipping profile must be untouched by this change.
        var strays = ctx.AllProfiles.Where(p => p.RequiresUsbipBackend).ToList();
        Check("no shipped profile requires the backend", strays.Count == 0,
              strays.Count > 0 ? string.Join(", ", strays.Select(p => p.Id)) : "");

        // ── The identity carried over free ──────────────────────────────
        Console.WriteLine("\n-- HID interface reuse --");
        Check("same VID", composite.VendorId == baseProfile.VendorId, $"0x{composite.VendorId:X4}");
        Check("same PID", composite.ProductId == baseProfile.ProductId, $"0x{composite.ProductId:X4}");
        Check("same product string", composite.ProductString == baseProfile.ProductString, composite.ProductString);
        var bd = baseProfile.Inner.GetDescriptorBytes();
        var cd = inner.GetDescriptorBytes();
        Check("HID report descriptor is byte-identical", bd != null && cd != null && bd.SequenceEqual(cd),
              $"{cd?.Length ?? 0} bytes");
        Check("input report size unchanged", inner.InputReportSize == baseProfile.Inner.InputReportSize,
              $"{inner.InputReportSize}");
        Check("vendor-blob input codec carried over", inner.ExtendedReport != null);
        Check("vendor-blob output codec carried over", inner.ExtendedOutputReport != null);

        // ── The USB configuration ───────────────────────────────────────
        Console.WriteLine("\n-- USB configuration --");
        Check("base profile declares no USB configuration", baseProfile.Inner.UsbConfiguration == null);
        var cfg = inner.UsbConfiguration;
        Check("composite declares a USB configuration", cfg != null);
        if (cfg == null) { Summary(); return 1; }

        Check("self-powered, no remote wakeup (bmAttributes 0xC0)", cfg.Attributes == 0xC0, $"0x{cfg.Attributes:X2}");
        Check("500 mA bus current", cfg.MaxPowerMilliamps == 500, $"{cfg.MaxPowerMilliamps} mA");
        Check("four interfaces, as the real pad presents", cfg.Interfaces.Count == 4, $"{cfg.Interfaces.Count}");

        var ac = cfg.Interfaces.FirstOrDefault(i => i.Function == "audioControl");
        var outIf = cfg.Interfaces.FirstOrDefault(i => i.Function == "audioStreamingOut");
        var inIf = cfg.Interfaces.FirstOrDefault(i => i.Function == "audioStreamingIn");
        var hid = cfg.Interfaces.FirstOrDefault(i => i.Function == "hid");
        Check("interface 0 is Audio Control", ac != null && ac.InterfaceNumber == 0);
        Check("interface 1 is the OUT stream", outIf != null && outIf.InterfaceNumber == 1);
        Check("interface 2 is the IN stream", inIf != null && inIf.InterfaceNumber == 2);
        Check("interface 3 is HID", hid != null && hid.InterfaceNumber == 3);
        if (ac == null || outIf == null || inIf == null || hid == null) { Summary(); return 1; }

        Check("Audio Control has class 0x01 subclass 0x01",
              ac.AltSettings[0].InterfaceClass == 0x01 && ac.AltSettings[0].InterfaceSubClass == 0x01);
        Check("Audio Control exposes no endpoint", ac.AltSettings[0].Endpoints.Count == 0);

        // Zero-bandwidth alt 0 plus the streaming alt 1: this pair is what
        // lets the host park the stream when nothing is playing.
        Check("OUT interface offers alt 0 and alt 1", outIf.AltSettings.Count == 2);
        Check("OUT alt 0 is zero-bandwidth", outIf.AltSettings[0].Endpoints.Count == 0);
        var outEp = outIf.AltSettings[1].Endpoints.FirstOrDefault();
        Check("OUT alt 1 endpoint 0x01, isochronous adaptive",
              outEp != null && outEp.Address == 0x01 && outEp.TransferType == "isochronous" && outEp.SyncType == "adaptive");
        Check("OUT wMaxPacketSize 392", outEp != null && outEp.MaxPacketSize == 392, $"{outEp?.MaxPacketSize}");
        Check("OUT bInterval 4 (1 ms service interval)", outEp != null && outEp.Interval == 4, $"{outEp?.Interval}");

        var outStream = outIf.AltSettings[1].AudioStream;
        Check("OUT stream is 4 channels, 16-bit, 48 kHz",
              outStream != null && outStream.Channels == 4 && outStream.BitsPerSample == 16 && outStream.SampleRateHz == 48000);
        Check("OUT wChannelConfig 0x0033 (FL, FR, RL, RR)",
              outStream != null && outStream.ChannelConfig == 0x0033, $"0x{outStream?.ChannelConfig:X4}");
        // The whole point of the feature: channels 3 and 4 are the
        // voice-coil actuators, addressable by role rather than by
        // decoding terminal topology.
        Check("channel roles name the speaker pair and the haptic pair",
              outStream != null && outStream.ChannelRoles.SequenceEqual(
                  new[] { "speakerLeft", "speakerRight", "hapticLeft", "hapticRight" }),
              outStream != null ? string.Join("/", outStream.ChannelRoles) : "");

        var inEp = inIf.AltSettings[1].Endpoints.FirstOrDefault();
        Check("IN alt 1 endpoint 0x82, isochronous asynchronous",
              inEp != null && inEp.Address == 0x82 && inEp.TransferType == "isochronous" && inEp.SyncType == "asynchronous");
        Check("IN wMaxPacketSize 196", inEp != null && inEp.MaxPacketSize == 196, $"{inEp?.MaxPacketSize}");
        Check("IN stream carries the microphone",
              inIf.AltSettings[1].AudioStream?.ChannelRoles.All(r => r == "microphone") == true);

        Check("HID interface class 0x03", hid.AltSettings[0].InterfaceClass == 0x03);
        Check("HID interface has its two interrupt endpoints",
              hid.AltSettings[0].Endpoints.Count == 2 &&
              hid.AltSettings[0].Endpoints.All(e => e.TransferType == "interrupt"));

        // ── The guard ───────────────────────────────────────────────────
        Console.WriteLine("\n-- Create-path guard --");
        bool refused = false;
        string message = "";
        try
        {
            using var c = ctx.CreateController(composite);
            c.Dispose();
        }
        catch (NotSupportedException ex) { refused = true; message = ex.Message; }
        catch (Exception ex) { message = ex.GetType().Name + ": " + ex.Message; }
        Check("CreateController refuses a usbip profile instead of building a partial device", refused);
        Check("the refusal names the backend and says it is opt-in",
              refused && message.Contains("usbip") && message.Contains("opt-in"));

        Summary();
        return s_failures == 0 ? 0 : 1;
    }

    static void Summary()
        => Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
}
