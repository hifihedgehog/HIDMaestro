// Nintendo Switch 2 Pro Controller profile check.
//
// The Switch 2 Pro profile was reconstructed from source rather than from a
// hardware capture, so this probe's job is to prove the reconstruction is
// faithful to the two implementations it was derived from, both of which
// agree on every field independently:
//
//   * VIIPER device/ns2pro  (github.com/Alia5/VIIPER)
//       - descriptor.go   : the report descriptor item list
//       - inputstate.go   : buildProReport, proButtonMap, packStick12
//       - const.go        : VID/PID, report IDs, stick range
//   * SDL3 SDL_hidapi_switch2.c
//       - the driver SDL routes 057E:2069 to, and the authority on how a
//         real pad's bytes are read back
//
// Every expectation below is a literal transcribed from one of those two,
// not a value read back out of our own encoder, so a drift in either
// direction fails rather than silently agreeing with itself.
//
// Exit 0 PASS / 1 FAIL. No elevation and no device required: this drives
// VendorBlobCodec directly.

using System;
using System.Linq;

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
        Console.WriteLine("=== Nintendo Switch 2 Pro Controller profile ===");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var profile = ctx.GetProfile("switch2-pro-controller");
        if (profile == null)
        {
            // The catalog loader deliberately swallows per-file parse
            // exceptions so one bad profile cannot take down every
            // consumer. That is the right behaviour in production and a
            // terrible one to debug against, because a rejected profile is
            // indistinguishable from an absent one. Re-parse the file here
            // with the exception left in, so the reason is on screen.
            Console.WriteLine("  [FAIL] profile switch2-pro-controller not found in the catalog");
            string onDisk = System.IO.Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..",
                "profiles", "nintendo", "switch2-pro.json");
            onDisk = System.IO.Path.GetFullPath(onDisk);
            if (System.IO.File.Exists(onDisk))
            {
                try
                {
                    // The very same options object the catalog loader uses,
                    // so this reproduces its parse exactly rather than an
                    // approximation of it.
                    System.Text.Json.JsonSerializer.Deserialize<ControllerProfile>(
                        System.IO.File.ReadAllText(onDisk), HMLayoutJsonOptions.Default);
                    Console.WriteLine("  [note] the on-disk file parses standalone; the catalog "
                                    + "options must differ from the ones used here");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [note] on-disk parse threw: {ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine($"  [note] could not locate {onDisk} to re-parse");
            }
            return 1;
        }

        // ── Identity ────────────────────────────────────────────────────
        // VIIPER const.go: DefaultVID 0x057E, DefaultPID 0x2069.
        // SDL usb_ids.h: USB_PRODUCT_NINTENDO_SWITCH2_PRO 0x2069, and
        // controller_list.h maps it to k_eControllerType_Switch2ProController.
        Console.WriteLine("\n-- Identity --");
        Check("VID is Nintendo 0x057E", profile.VendorId == 0x057E, $"0x{profile.VendorId:X4}");
        Check("PID is Switch 2 Pro 0x2069", profile.ProductId == 0x2069, $"0x{profile.ProductId:X4}");
        Check("does NOT use the usbip backend (plain UMDF2 HID)",
              !string.Equals(profile.Backend, "usbip", StringComparison.OrdinalIgnoreCase),
              profile.Backend ?? "umdf2");

        // ── Report descriptor ───────────────────────────────────────────
        // Encoded from VIIPER descriptor.go's item list using the HID short
        // item rule header = (tag << 4) | (type << 2) | size. Spelled out
        // here so a change to either side has to be deliberate.
        Console.WriteLine("\n-- Report descriptor --");
        byte[] expectDesc = {
            0x05,0x01, 0x09,0x05, 0xa1,0x01,
            0x85,0x05, 0x05,0xff, 0x09,0x01, 0x15,0x00, 0x26,0xff,0x00, 0x95,0x3f, 0x75,0x08, 0x81,0x02,
            0x85,0x09, 0x09,0x01, 0x95,0x02, 0x81,0x02,
            0x05,0x09, 0x19,0x01, 0x29,0x15, 0x25,0x01, 0x95,0x15, 0x75,0x01, 0x81,0x02,
            0x95,0x01, 0x75,0x03, 0x81,0x03,
            0x05,0x01, 0x09,0x01, 0xa1,0x00,
            0x09,0x30, 0x09,0x31, 0x09,0x33, 0x09,0x35,
            0x26,0xff,0x0f, 0x95,0x04, 0x75,0x0c, 0x81,0x02, 0xc0,
            0x05,0xff, 0x09,0x02, 0x26,0xff,0x00, 0x95,0x34, 0x75,0x08, 0x81,0x02,
            0x85,0x02, 0x09,0x01, 0x95,0x3f, 0x91,0x02, 0xc0,
        };
        var desc = profile.GetDescriptorBytes();
        Check("descriptor is 97 bytes", desc != null && desc.Length == 97,
              desc == null ? "null" : $"{desc.Length}");
        Check("descriptor matches VIIPER's item list byte for byte",
              desc != null && desc.SequenceEqual(expectDesc),
              desc == null ? "null"
                  : $"{desc.Zip(expectDesc, (a, b) => a == b).Count(x => x)}/{expectDesc.Length} equal");

        // Structural facts worth asserting by name, because each encodes a
        // decision that would otherwise be silent.
        if (desc != null)
        {
            // 21 buttons, not 22: Headset lives in report 0x05 only and has
            // no bit in the Pro report, so the array stops at 21 and three
            // padding bits follow.
            int usageMaxIdx = Array.IndexOf(desc, (byte)0x29);
            Check("declares exactly 21 buttons (Headset is 0x05-only)",
                  usageMaxIdx >= 0 && desc[usageMaxIdx + 1] == 0x15,
                  usageMaxIdx >= 0 ? $"usage max {desc[usageMaxIdx + 1]}" : "not found");
            Check("sticks are 12-bit (report size 0x0C, logical max 0x0FFF)",
                  IndexOfSeq(desc, new byte[] { 0x26, 0xff, 0x0f, 0x95, 0x04, 0x75, 0x0c }) >= 0);
            Check("declares report IDs 0x05, 0x09 and 0x02",
                  IndexOfSeq(desc, new byte[] { 0x85, 0x05 }) >= 0 &&
                  IndexOfSeq(desc, new byte[] { 0x85, 0x09 }) >= 0 &&
                  IndexOfSeq(desc, new byte[] { 0x85, 0x02 }) >= 0);
        }

        // ── Emitted report ──────────────────────────────────────────────
        // VIIPER device.go constructs with activeReportID = ReportIDPro, so
        // a pad that has not been switched by a subcommand emits 0x09.
        Console.WriteLine("\n-- Emitted report (0x09) --");
        var spec = profile.ExtendedReport;
        if (spec == null)
        {
            Check("profile declares an extendedReport", false);
            return Done();
        }
        Check("emits report 0x09, the power-on default", spec.ReportIdByte == 0x09, $"0x{spec.ReportIdByte:X2}");
        Check("report size is 64", spec.Size == 64, $"{spec.Size}");

        // Buttons: every bit position transcribed from VIIPER proButtonMap.
        // byte 3: B 01, A 02, Y 04, X 08, R 10, ZR 20, Plus 40, RStick 80
        // byte 4: Down 01, Right 02, Left 04, Up 08, L 10, ZL 20, Minus 40, LStick 80
        // byte 5: Home 01, Capture 02, GR 04, GL 08, C 10
        var buf = new byte[spec.Size];
        var enc = new VendorBlobCodec.EncoderState();

        void Encode(HMGamepadState st, float lx = 0.5f, float ly = 0.5f,
                    float rx = 0.5f, float ry = 0.5f, float lt = 0f, float rt = 0f)
            => VendorBlobCodec.EncodeInput(spec, in st, lx, ly, rx, ry, lt, rt, buf, enc);

        (string name, HMButton btn, int idx, byte mask)[] simple = {
            ("B",           HMButton.B,           3, 0x01),
            ("A",           HMButton.A,           3, 0x02),
            ("Y",           HMButton.Y,           3, 0x04),
            ("X",           HMButton.X,           3, 0x08),
            ("R",           HMButton.RightBumper, 3, 0x10),
            ("Plus",        HMButton.Start,       3, 0x40),
            ("RightStick",  HMButton.RightStick,  3, 0x80),
            ("L",           HMButton.LeftBumper,  4, 0x10),
            ("Minus",       HMButton.Back,        4, 0x40),
            ("LeftStick",   HMButton.LeftStick,   4, 0x80),
            ("Home",        HMButton.Guide,       5, 0x01),
            ("Capture",     HMButton.Share,       5, 0x02),
            ("GR (right paddle)", HMButton.RightPaddle, 5, 0x04),
            ("GL (left paddle)",  HMButton.LeftPaddle,  5, 0x08),
            ("C",           HMButton.Misc1,       5, 0x10),
        };
        foreach (var (name, btn, idx, mask) in simple)
        {
            Encode(new HMGamepadState { Buttons = btn });
            Check($"{name} sets byte {idx} bit 0x{mask:X2}", (buf[idx] & mask) == mask,
                  $"byte{idx}=0x{buf[idx]:X2}");
        }

        // Triggers are digital on this pad: ZL/ZR are button bits driven by
        // the analog trigger value, exactly as DS4/DS5 do with LT/RT_DIGITAL.
        Encode(new HMGamepadState(), rt: 1.0f);
        Check("ZR is driven by the right trigger (byte 3 bit 0x20)", (buf[3] & 0x20) == 0x20, $"byte3=0x{buf[3]:X2}");
        Encode(new HMGamepadState(), lt: 1.0f);
        Check("ZL is driven by the left trigger (byte 4 bit 0x20)", (buf[4] & 0x20) == 0x20, $"byte4=0x{buf[4]:X2}");

        // D-pad: four discrete bits, not a hat. Diagonals set both parts,
        // which is what SDL reassembles into SDL_HAT_* on the other side.
        (HMHat hat, byte b4, string label)[] dpad = {
            (HMHat.South,     0x01, "Down"),
            (HMHat.East,      0x02, "Right"),
            (HMHat.West,      0x04, "Left"),
            (HMHat.North,     0x08, "Up"),
            (HMHat.NorthEast, 0x0A, "Up+Right"),
            (HMHat.SouthWest, 0x05, "Down+Left"),
        };
        foreach (var (hat, expected, label) in dpad)
        {
            Encode(new HMGamepadState { Hat = hat });
            Check($"d-pad {label} sets byte 4 low nibble 0x{expected:X2}",
                  (buf[4] & 0x0F) == expected, $"0x{buf[4] & 0x0F:X2}");
        }
        Encode(new HMGamepadState { Hat = HMHat.None });
        Check("d-pad neutral clears all four direction bits", (buf[4] & 0x0F) == 0x00, $"0x{buf[4] & 0x0F:X2}");

        // Sticks: two 12-bit axes in three shared bytes.
        //   out[0] = x low 8
        //   out[1] = x high 4 (low nibble) | y low 4 (high nibble)
        //   out[2] = y high 8
        // Centre is 0x800 per VIIPER StickCenter, full scale 0x0FFF.
        Console.WriteLine("\n-- 12-bit stick packing --");
        Encode(new HMGamepadState(), lx: 0.5f, ly: 0.5f);
        int lxc = buf[6] | ((buf[7] & 0x0F) << 8);
        int lyc = (buf[7] >> 4) | (buf[8] << 4);
        Check("centred left stick reads 0x800 on both axes (VIIPER StickCenter)",
              lxc == 0x800 && lyc == 0x800, $"x=0x{lxc:X3} y=0x{lyc:X3}");

        Encode(new HMGamepadState(), lx: 1.0f, ly: 0.0f);
        lxc = buf[6] | ((buf[7] & 0x0F) << 8);
        lyc = (buf[7] >> 4) | (buf[8] << 4);
        Check("full-scale left stick reads 0x000/0xFFF at the rails",
              lxc == 0x0FFF && lyc == 0x000, $"x=0x{lxc:X3} y=0x{lyc:X3}");

        Encode(new HMGamepadState(), rx: 0.0f, ry: 1.0f);
        int rxc = buf[9] | ((buf[10] & 0x0F) << 8);
        int ryc = (buf[10] >> 4) | (buf[11] << 4);
        Check("right stick occupies bytes 9..11 independently",
              rxc == 0x000 && ryc == 0x0FFF, $"x=0x{rxc:X3} y=0x{ryc:X3}");

        // The shared middle byte is the whole reason this is one field.
        // Setting one axis must not disturb the other.
        Encode(new HMGamepadState(), lx: 1.0f, ly: 0.5f);
        lxc = buf[6] | ((buf[7] & 0x0F) << 8);
        lyc = (buf[7] >> 4) | (buf[8] << 4);
        Check("X at full scale does not corrupt Y through the shared byte",
              lxc == 0x0FFF && lyc == 0x800, $"x=0x{lxc:X3} y=0x{lyc:X3}");

        // ── Header bytes ────────────────────────────────────────────────
        Console.WriteLine("\n-- Header --");
        Encode(new HMGamepadState());
        Check("byte 0 is the report ID 0x09", buf[0] == 0x09, $"0x{buf[0]:X2}");
        Check("byte 2 powerInfo is non-zero (level<<2 | external power)",
              buf[2] != 0, $"0x{buf[2]:X2}");
        Check("byte 12 advertises rumble (VIIPER writes 0x38 when enabled)",
              buf[12] == 0x38, $"0x{buf[12]:X2}");

        byte first = buf[1];
        Encode(new HMGamepadState());
        Check("byte 1 sequence counter advances between reports",
              buf[1] != first, $"{first} -> {buf[1]}");

        return Done();
    }

    static int Done()
    {
        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static int IndexOfSeq(byte[] hay, byte[] needle)
    {
        for (int i = 0; i + needle.Length <= hay.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < needle.Length; j++)
                if (hay[i + j] != needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }
}
