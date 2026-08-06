// Sony extra-buttons wire check (issue #48).
//
// Gates the two mechanisms that carry the DualSense mic mute and the
// Edge's four extras, plus the sentinel that stopped the old aliasing:
//
//   * Descriptor path (BuildReportInto + buttonMap): mute is descriptor
//     button 14 on the USB DualSense family; the Edge extras are NOT
//     declared buttons on real hardware and ride the 13x1-bit vendor
//     field that continues the button array, which buttonMap values
//     >= Buttons.Count now address (HidReportBuilder.VendorButtonBits).
//   * Vendor-blob path (VendorBlobCodec.EncodeInput): the third buttons
//     byte's mask lists now carry Misc1 and, on Edge specs, the paddle
//     and Fn names.
//
// Every expected byte below is transcribed from the wire ground truth
// FOUR independent implementations agree on, not read back from our own
// encoder: SDL SDL_hidapi_ps5.c (SDL_GAMEPAD_BUTTON_PS5_*), DS4Windows
// DualSenseDevice.cs (inputReport[10]: Mute 0x04, FnL 0x10, FnR 0x20,
// BLP 0x40, BRP 0x80), ds5-edge-relay ds5_report.hpp (BTN_MUTE/LFN/RFN/
// LB/RB), and dualsense-tester. The old aliasing this probe pins down:
// before #48 the Sony maps stopped at bit 11, so HMButton.Share fell
// through identity to descriptor button 12 (PS) and the v1.5.0 paddles
// to 13/14 (Touchpad / Mute).
//
// Exit 0 PASS / 1 FAIL. No elevation and no device required.

using System;

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

    static byte[] Build(HidReportBuilder b, HMButton buttons)
    {
        var report = new byte[b.InputReportByteSize];
        b.BuildReportInto(report, axes: null, buttonMask: (uint)buttons);
        return report;
    }

    static int Main()
    {
        Console.WriteLine("=== Sony extra buttons: mic mute + Edge paddles/Fn (issue #48) ===");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        HMProfile P(string id) => ctx.GetProfile(id) ?? throw new Exception($"missing profile {id}");
        HidReportBuilder B(string id) => P(id).Inner.GetOrBuildReportBuilder();

        // ── Descriptor path: plain USB DualSense ─────────────────────
        Console.WriteLine("\n-- dualsense (USB) descriptor path --");
        var ds5 = B("dualsense");
        Check("declares 15 buttons (mute at index 14, like real hardware)",
              ds5.Buttons.Count == 15, $"got {ds5.Buttons.Count}");
        Check("vendor-bit run is the 13x1-bit field continuing the buttons",
              ds5.VendorButtonBits.Count == 13, $"got {ds5.VendorButtonBits.Count}");

        // Report layout with RID at [0]: byte 8 = hat + face nibble,
        // byte 9 = L1..R3, byte 10 = PS/Touchpad/Mute + vendor bits.
        var r = Build(ds5, HMButton.Misc1);
        Check("Misc1 (mic mute) sets 0x04 in the third buttons byte",
              r[10] == 0x04 && r[9] == 0x00, $"byte10=0x{r[10]:X2}");
        r = Build(ds5, HMButton.Guide);
        Check("Guide still sets PS 0x01", r[10] == 0x01, $"byte10=0x{r[10]:X2}");
        r = Build(ds5, HMButton.Touchpad);
        Check("Touchpad still sets 0x02", r[10] == 0x02, $"byte10=0x{r[10]:X2}");
        r = Build(ds5, HMButton.A);
        Check("A still lands on Cross (face nibble 0x20)",
              r[8] == 0x28 || (r[8] & 0xF0) == 0x20, $"byte8=0x{r[8]:X2}");

        // The sentinel kills the pre-#48 identity aliasing.
        r = Build(ds5, HMButton.Share);
        Check("Share no longer aliases onto PS (was identity bit 12)",
              r[10] == 0x00, $"byte10=0x{r[10]:X2}");
        r = Build(ds5, HMButton.RightPaddle | HMButton.LeftPaddle);
        Check("paddles do nothing on a non-Edge DualSense (were Touchpad/Mute)",
              r[9] == 0x00 && r[10] == 0x00, $"byte9=0x{r[9]:X2} byte10=0x{r[10]:X2}");
        r = Build(ds5, HMButton.RightPaddle2 | HMButton.LeftPaddle2);
        Check("Fn pair does nothing on a non-Edge DualSense",
              r[10] == 0x00, $"byte10=0x{r[10]:X2}");

        // ── Descriptor path: DualSense Edge ──────────────────────────
        Console.WriteLine("\n-- dualsense-edge (USB) descriptor path --");
        var edge = B("dualsense-edge");
        Check("Edge still declares 15 buttons (extras are vendor bits, like real hardware)",
              edge.Buttons.Count == 15, $"got {edge.Buttons.Count}");

        r = Build(edge, HMButton.RightPaddle);
        Check("RightPaddle (back RB) sets 0x80", r[10] == 0x80, $"byte10=0x{r[10]:X2}");
        r = Build(edge, HMButton.LeftPaddle);
        Check("LeftPaddle (back LB) sets 0x40", r[10] == 0x40, $"byte10=0x{r[10]:X2}");
        r = Build(edge, HMButton.RightPaddle2);
        Check("RightPaddle2 (right Fn) sets 0x20", r[10] == 0x20, $"byte10=0x{r[10]:X2}");
        r = Build(edge, HMButton.LeftPaddle2);
        Check("LeftPaddle2 (left Fn) sets 0x10", r[10] == 0x10, $"byte10=0x{r[10]:X2}");
        r = Build(edge, HMButton.Misc1);
        Check("Misc1 (mic mute) sets 0x04 on the Edge too", r[10] == 0x04, $"byte10=0x{r[10]:X2}");
        r = Build(edge, HMButton.Misc1 | HMButton.RightPaddle | HMButton.LeftPaddle
                       | HMButton.RightPaddle2 | HMButton.LeftPaddle2);
        Check("all five extras together read 0xF4, bit 0x08 stays clear",
              r[10] == 0xF4, $"byte10=0x{r[10]:X2}");
        r = Build(edge, HMButton.Share);
        Check("Share stays dead on the Edge as well", r[10] == 0x00, $"byte10=0x{r[10]:X2}");

        // ── Descriptor path: DualShock 4 (no mute, no extras) ────────
        Console.WriteLine("\n-- dualshock-4-v2 (USB) descriptor path --");
        var ds4 = B("dualshock-4-v2");
        Check("DS4 has no vendor-bit run (its 6-bit counter must not qualify)",
              ds4.VendorButtonBits.Count == 0, $"got {ds4.VendorButtonBits.Count}");
        // DS4 report with RID: byte 5 = hat+face, byte 6 = L1..R3,
        // byte 7 = PS 0x01 / Touchpad 0x02 + 6-bit counter.
        r = Build(ds4, HMButton.Share | HMButton.RightPaddle | HMButton.LeftPaddle | HMButton.Misc1);
        Check("Share/paddles/Misc1 all dead on DS4 (were PS / Touchpad by identity)",
              r[7] == 0x00 && r[6] == 0x00, $"byte7=0x{r[7]:X2} byte6=0x{r[6]:X2}");
        r = Build(ds4, HMButton.Touchpad);
        Check("DS4 Touchpad still sets 0x02", r[7] == 0x02, $"byte7=0x{r[7]:X2}");

        // ── Vendor-blob path: armed BT report 0x31 ───────────────────
        // BT 0x31 layout: byte 0 = RID, byte 1 = seq, buttons at bytes
        // 9/10/11; byte 11 = PS/Touchpad/Mute + Edge bits, same bit
        // semantics as USB byte 10 (SDL parses both with one struct).
        Console.WriteLine("\n-- vendor-blob path (BT report 0x31) --");
        byte Enc(string id, HMButton buttons)
        {
            var spec = P(id).ExtendedReport!;
            var buf = new byte[spec.Size];
            var st = new HMGamepadState { Buttons = buttons };
            VendorBlobCodec.EncodeInput(spec, in st, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f,
                                        buf, new VendorBlobCodec.EncoderState());
            return buf[11];
        }

        Check("dualsense-bt: Misc1 sets 0x04 in byte 11",
              Enc("dualsense-bt", HMButton.Misc1) == 0x04,
              $"got 0x{Enc("dualsense-bt", HMButton.Misc1):X2}");
        Check("dualsense-bt: paddles stay dead (non-Edge spec lists no paddle names)",
              Enc("dualsense-bt", HMButton.RightPaddle | HMButton.LeftPaddle) == 0x00);
        Check("dualsense-edge-bt: RightPaddle sets 0x80",
              Enc("dualsense-edge-bt", HMButton.RightPaddle) == 0x80);
        Check("dualsense-edge-bt: LeftPaddle sets 0x40",
              Enc("dualsense-edge-bt", HMButton.LeftPaddle) == 0x40);
        Check("dualsense-edge-bt: RightPaddle2 sets 0x20",
              Enc("dualsense-edge-bt", HMButton.RightPaddle2) == 0x20);
        Check("dualsense-edge-bt: LeftPaddle2 sets 0x10",
              Enc("dualsense-edge-bt", HMButton.LeftPaddle2) == 0x10);
        Check("dualsense-edge-bt: all five extras read 0xF4",
              Enc("dualsense-edge-bt", HMButton.Misc1 | HMButton.RightPaddle | HMButton.LeftPaddle
                                     | HMButton.RightPaddle2 | HMButton.LeftPaddle2) == 0xF4);
        Check("dualsense-edge-bt: Guide/Touchpad bits unchanged",
              Enc("dualsense-edge-bt", HMButton.Guide | HMButton.Touchpad) == 0x03);

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }
}
