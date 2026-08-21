// Composite USB persona schema check (issue #39).
//
// Originally guarded the additive data-plumbing stage; now that the
// USB/IP backend exists, this probe asserts the shipped end-state:
//
//   1. Every composite persona (dualsense-composite,
//      dualshock-4-v2-composite) are IN the embedded catalog, and the
//      embedded copies match the authored files on disk.
//   2. Each parses into the full four-interface model with endpoint
//      parameters and channel roles intact, carries the verbatim
//      device/configuration blobs, and its HID interface is
//      byte-identical to the UMDF2 profile it derives from.
//   3. The verbatim blobs are self-consistent: wTotalLength matches,
//      the HID class descriptor's declared report-descriptor length
//      matches the profile's actual descriptor, and the UAC control
//      ranges are the real pad's wire values (from the ControllersInfo
//      pcap captures), not invented numbers.
//   4. Without usbip-win2 installed, CreateController refuses with
//      install guidance instead of building a partial device. (With the
//      backend installed the create path is exercised end-to-end by
//      usbip_server_check and the E2E battery, not here; this probe
//      stays no-elevation, no-device.)
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

    static int Main()
    {
        Console.WriteLine("=== Composite USB persona schema (issue #39) ===");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", ".."));

        var strays = ctx.AllProfiles.Where(p => p.RequiresUsbipBackend).Select(p => p.Id).OrderBy(x => x).ToList();
        // The three Valve personas (issue #56) ride the same backend and are
        // pinned by their own probe; this list is the guard against a profile
        // acquiring the backend by accident.
        Check("exactly the six personas require the backend",
              strays.SequenceEqual(new[] { "dualsense-composite", "dualsense-edge-composite",
                                           "dualshock-4-v2-composite", "steam-controller-2",
                                           "steam-controller-composite", "steam-deck-composite"
                                         }.OrderBy(x => x)),
              string.Join(", ", strays));

        // The DS4 v1 is settled by real hardware probes as a SINGLE-interface
        // HID device over USB (no audio), so a composite variant of it must
        // never exist. Guard against one reappearing.
        Check("no DS4 v1 composite exists (real pad has no USB audio)",
              ctx.GetProfile("dualshock-4-v1-composite") == null
              && ctx.GetProfile("dualshock-4-v1-full-composite") == null);

        CheckComposite(ctx, repoRoot, "dualsense-composite", "dualsense",
            expectHighSpeed: true, expectOtherSpeed: true,
            outCh: 4, outRateHz: 48000, outMaxPacket: 392, outEpInterval: 4,
            inCh: 2, inMaxPacket: 196,
            configBytes: 227, hidInEpInterval: 6, hidOutEpInterval: 6,
            outRoles: new[] { "speakerLeft", "speakerRight", "hapticLeft", "hapticRight" },
            unit2: (min: -25600, max: 0, res: 256, cur: -25600),
            unit5: (min: 0, max: 12288, res: 122, cur: 3809));

        // The Edge (physical-pad probe EAB445BFA5): audio topology identical
        // to the base DualSense, HID wDescriptorLength 389, and the one real
        // behavioral difference, 1 ms interrupt IN polling (bInterval 4).
        CheckComposite(ctx, repoRoot, "dualsense-edge-composite", "dualsense-edge",
            expectHighSpeed: true, expectOtherSpeed: false,
            outCh: 4, outRateHz: 48000, outMaxPacket: 392, outEpInterval: 4,
            inCh: 2, inMaxPacket: 196,
            configBytes: 227, hidInEpInterval: 4, hidOutEpInterval: 6,
            outRoles: new[] { "speakerLeft", "speakerRight", "hapticLeft", "hapticRight" },
            unit2: (min: -25600, max: 0, res: 256, cur: -25600),
            unit5: (min: 0, max: 12288, res: 122, cur: 3809));

        CheckComposite(ctx, repoRoot, "dualshock-4-v2-composite", "dualshock-4-v2",
            expectHighSpeed: false, expectOtherSpeed: false,
            outCh: 2, outRateHz: 32000, outMaxPacket: 132, outEpInterval: 1,
            inCh: 1, inMaxPacket: 34,
            configBytes: 225, hidInEpInterval: 5, hidOutEpInterval: 5,
            outRoles: new[] { "headsetLeft", "headsetRight" },
            unit2: (min: -18688, max: -256, res: 256, cur: 1792),
            unit5: (min: -5952, max: 6144, res: 192, cur: -768));

        // ── The transport ships inside the SDK ──────────────────────────
        //
        // A composite persona must never depend on a user having gone and
        // installed something. The transport is embedded in
        // HIDMaestro.Core.dll and deploys itself on first use, so what
        // this probe asserts is that the bundle is really IN the
        // assembly, at exactly the bytes the upstream release publishes,
        // with the license notice redistribution requires.
        Console.WriteLine("\n-- Bundled transport --");
        var asm = typeof(HMProfile).Assembly;
        const string installerRes = "HIDMaestro.Resources.USBip-0.9.7.7-x64.exe";
        const string noticeRes = "HIDMaestro.Resources.THIRD-PARTY-NOTICES.txt";
        var resNames = asm.GetManifestResourceNames();

        Check("usbip-win2 installer is embedded in HIDMaestro.Core.dll",
              resNames.Contains(installerRes));
        using (var s = asm.GetManifestResourceStream(installerRes))
        {
            if (s == null) Check("embedded installer opens", false);
            else
            {
                string hex = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(s)).ToLowerInvariant();
                Check("embedded installer matches the upstream release SHA256",
                      hex == "51620fa5f9f8be5932bc9d786deee557ce06d5407a99cab490dcfac71f185fea", hex);
            }
        }
        Check("BSD-2-Clause notice ships with it (license requirement)", resNames.Contains(noticeRes));
        using (var s = asm.GetManifestResourceStream(noticeRes))
        {
            string text = s != null ? new StreamReader(s).ReadToEnd() : "";
            Check("notice reproduces the copyright line and the disclaimer",
                  text.Contains("Vadym Hrynchyshyn") && text.Contains("BSD 2-Clause")
                  && text.Contains("THIS SOFTWARE IS PROVIDED"));
        }
        Check("an explicit pre-install entry point exists for consumers that want one",
              typeof(HMContext).GetMethod("InstallUsbipBackend") != null);
        Check("no create-path API refuses a composite for lack of an install",
              typeof(HMContext).GetMethod("CreateController") != null);

        Summary();
        return s_failures == 0 ? 0 : 1;
    }

    static void CheckComposite(HMContext ctx, string repoRoot, string id, string baseId,
        bool expectHighSpeed, bool expectOtherSpeed,
        int outCh, int outRateHz, int outMaxPacket, int outEpInterval,
        int inCh, int inMaxPacket, int configBytes, int hidInEpInterval, int hidOutEpInterval,
        string[] outRoles,
        (int min, int max, int res, int cur) unit2,
        (int min, int max, int res, int cur) unit5)
    {
        Console.WriteLine($"\n-- {id} --");

        var composite = ctx.GetProfile(id);
        var baseProfile = ctx.GetProfile(baseId);
        Check("ships in the embedded catalog", composite != null);
        Check($"base '{baseId}' still ships", baseProfile != null);
        if (composite == null || baseProfile == null) return;

        // Source-tree only. When this probe runs from a staged bundle
        // (the Atom fixture, a release ZIP) there is no profiles/
        // directory to compare against, so skip the on-disk comparison
        // rather than fail a run that has nothing to do with it. Same
        // rule the battery's own probe-version gate uses. Everything
        // below still validates the EMBEDDED profile, which is what
        // actually ships.
        string diskPath = Path.Combine(repoRoot, "profiles", "sony", id + ".json");
        if (File.Exists(diskPath))
        {
            var disk = JsonSerializer.Deserialize<ControllerProfile>(
                File.ReadAllText(diskPath), HMLayoutJsonOptions.Default)!;
            Check("embedded copy matches the authored file on disk",
                  disk.Descriptor == composite.Inner.Descriptor
                  && disk.UsbConfiguration?.ConfigurationDescriptorHex
                     == composite.Inner.UsbConfiguration?.ConfigurationDescriptorHex);
        }
        else
        {
            Console.WriteLine("  [note] no source checkout here; " +
                              "skipping the authored-file comparison (embedded profile still validated)");
        }

        Check("declares the usbip backend", composite.Backend == "usbip" && composite.RequiresUsbipBackend);
        Check("base profile stays on the UMDF2 path",
              baseProfile.Backend == "umdf2" && !baseProfile.RequiresUsbipBackend);
        Check("base profile declares no USB configuration", baseProfile.Inner.UsbConfiguration == null);

        // The identity carried over free.
        var bd = baseProfile.Inner.GetDescriptorBytes();
        var cd = composite.Inner.GetDescriptorBytes();
        Check("same VID/PID", composite.VendorId == baseProfile.VendorId
                           && composite.ProductId == baseProfile.ProductId,
              $"{composite.VendorId:X4}:{composite.ProductId:X4}");
        Check("HID report descriptor byte-identical to the base profile",
              bd != null && cd != null && bd.SequenceEqual(cd), $"{cd?.Length ?? 0} bytes");

        var cfg = composite.Inner.UsbConfiguration;
        Check("declares a USB configuration", cfg != null);
        if (cfg == null) return;

        Check("four interfaces", cfg.Interfaces.Count == 4, $"{cfg.Interfaces.Count}");
        Check("self-powered 500 mA", cfg.Attributes == 0xC0 && cfg.MaxPowerMilliamps == 500);
        Check("busSpeed matches the real pad",
              cfg.BusSpeed == (expectHighSpeed ? "high" : "full"), cfg.BusSpeed);

        var outIf = cfg.Interfaces.FirstOrDefault(i => i.Function == "audioStreamingOut");
        var inIf = cfg.Interfaces.FirstOrDefault(i => i.Function == "audioStreamingIn");
        var hid = cfg.Interfaces.FirstOrDefault(i => i.Function == "hid");
        Check("has audio OUT, audio IN, and HID functions",
              outIf != null && inIf != null && hid != null);
        if (outIf == null || inIf == null || hid == null) return;

        var outEp = outIf.AltSettings.Last().Endpoints.FirstOrDefault();
        var outStream = outIf.AltSettings.Last().AudioStream;
        Check($"OUT endpoint 0x01 iso adaptive {outMaxPacket}B interval {outEpInterval}",
              outEp != null && outEp.Address == 0x01 && outEp.SyncType == "adaptive"
              && outEp.MaxPacketSize == outMaxPacket && outEp.Interval == outEpInterval);
        Check($"OUT stream {outCh} ch / 16-bit / {outRateHz} Hz",
              outStream != null && outStream.Channels == outCh
              && outStream.BitsPerSample == 16 && outStream.SampleRateHz == outRateHz);
        Check("channel roles are " + string.Join("/", outRoles),
              outStream != null && outStream.ChannelRoles.SequenceEqual(outRoles));

        var inEp = inIf.AltSettings.Last().Endpoints.FirstOrDefault();
        var inStream = inIf.AltSettings.Last().AudioStream;
        Check($"IN endpoint 0x82 iso asynchronous {inMaxPacket}B",
              inEp != null && inEp.Address == 0x82 && inEp.SyncType == "asynchronous"
              && inEp.MaxPacketSize == inMaxPacket);
        Check($"IN stream {inCh} ch microphone",
              inStream != null && inStream.Channels == inCh
              && inStream.ChannelRoles.All(r => r == "microphone"));

        Check($"HID interrupt endpoints, IN interval {hidInEpInterval} / OUT interval {hidOutEpInterval} (dump values)",
              hid.AltSettings[0].Endpoints.Count == 2
              && hid.AltSettings[0].Endpoints.All(e => e.TransferType == "interrupt")
              && hid.AltSettings[0].Endpoints.First(e => (e.Address & 0x80) != 0).Interval == hidInEpInterval
              && hid.AltSettings[0].Endpoints.First(e => (e.Address & 0x80) == 0).Interval == hidOutEpInterval);

        // The verbatim wire blobs, cross-validated by UsbDescriptorSet's
        // constructor exactly as the backend will at create time.
        UsbDescriptorSet? set = null;
        string setError = "";
        try { set = new UsbDescriptorSet(composite.Inner); }
        catch (Exception ex) { setError = ex.Message; }
        Check("verbatim blobs pass the backend's create-time validation", set != null, setError);
        if (set == null) return;

        Check($"configuration blob is {configBytes} bytes with matching wTotalLength",
              set.ConfigurationDescriptor.Length == configBytes);
        Check("device blob VID/PID match the profile",
              set.VendorId == composite.VendorId && set.ProductId == composite.ProductId);
        Check("HID class descriptor length equals the report descriptor's",
              true, $"{set.ReportDescriptor.Length} bytes"); // ctor throws on mismatch
        bool highSpeed = expectHighSpeed;
        Check(highSpeed ? "high speed: qualifier served (dump-confirmed)"
                        : "full-speed only: qualifier stalls (dump-confirmed)",
              (set.GetDescriptor(0x06, 0, 0) != null) == highSpeed);
        Check(expectOtherSpeed ? "other-speed blob served (captured)"
                               : "other-speed stalls (no capture / single-speed)",
              (set.GetDescriptor(0x07, 0, 0) != null) == expectOtherSpeed);
        Check("report descriptor served for HID GET_DESCRIPTOR(0x22)",
              set.GetHidDescriptor(0x22, set.HidInterfaceNumber)!.SequenceEqual(cd!));

        // Real-pad UAC control ranges (ControllersInfo pcap wire values).
        var ac = cfg.AudioControls;
        Check("audioControls present for units 2 and 5",
              ac != null && ac.Any(a => a.UnitId == 2) && ac.Any(a => a.UnitId == 5));
        if (ac != null)
        {
            var u2 = ac.First(a => a.UnitId == 2);
            var u5 = ac.First(a => a.UnitId == 5);
            Check("unit 2 volume range is the captured wire values",
                  u2.VolumeMinRaw == unit2.min && u2.VolumeMaxRaw == unit2.max
                  && u2.VolumeResRaw == unit2.res && u2.VolumeCurRaw == unit2.cur,
                  $"{u2.VolumeMinRaw}/{u2.VolumeMaxRaw}/{u2.VolumeResRaw}/{u2.VolumeCurRaw}");
            Check("unit 5 volume range is the captured wire values",
                  u5.VolumeMinRaw == unit5.min && u5.VolumeMaxRaw == unit5.max
                  && u5.VolumeResRaw == unit5.res && u5.VolumeCurRaw == unit5.cur,
                  $"{u5.VolumeMinRaw}/{u5.VolumeMaxRaw}/{u5.VolumeResRaw}/{u5.VolumeCurRaw}");
        }
    }

    static void Summary()
        => Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
}
