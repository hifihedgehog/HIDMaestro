// v1.3.5 — Sony data-driven coverage probe.
//
// Validates the v1.3.5 vendor-blob extendedReport / extendedOutputReport
// blocks across the full Sony profile set:
//
//   - DS5 USB (Report 0x01) input round-trip — sticks/triggers/buttons/hat
//     plus LT_DIGITAL/RT_DIGITAL auto-engagement when triggers > 0
//   - DS4 USB (Report 0x01) input round-trip — sticks/buttons/hat in byte 5
//     (different layout from DS5)
//   - DS4 BT (Report 0x11) input + output round-trip with CRC32 prefix
//     [0xA1, 0x11] / [0xA2, 0x11]
//   - DS5 USB (Report 0x02) output round-trip (no CRC; USB is reliable)
//   - DS4 USB (Report 0x05) output round-trip (no CRC)
//
// Pure encoder unit-test: no driver install, no virtual device.
// Exit code 0 on all PASS, 1 on any FAIL.

using System;
using System.Collections.Generic;
using HIDMaestro;
using HIDMaestro.Internal;

internal sealed class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("=== v1.3.5 Sony data-driven coverage (USB + DS4 BT) ===\n");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        int failures = 0;
        int total = 0;

        // ── DS5 USB input encoder (Report 0x01) ────────────────────────
        Console.WriteLine("--- DS5 USB input (Report 0x01) ---");
        var ds5Usb = ctx.GetProfile("dualsense")!;
        if (!ds5Usb.HasExtendedInput)
        {
            Console.WriteLine("  [FAIL] dualsense (USB) has no ExtendedReport");
            return 1;
        }
        var ds5UsbInSpec = ds5Usb.ExtendedReport!;
        Console.WriteLine($"  reportId=0x{ds5UsbInSpec.ReportIdByte:X2} (expected 0x01)  size={ds5UsbInSpec.Size} fields={ds5UsbInSpec.Fields.Count}");

        // v1.3.9 — uniform [0..1] convention. State only carries non-axis
        // fields (buttons, hat); the 6 simple-slot values are passed
        // explicitly to VendorBlobCodec.EncodeInput.
        var ds5UsbState = new HMGamepadState
        {
            Buttons = HMButton.A | HMButton.LeftBumper | HMButton.Guide,
            Hat = HMHat.NorthEast,
        };
        var ds5UsbInBuf = new byte[ds5UsbInSpec.Size];
        var ds5UsbInState = new VendorBlobCodec.EncoderState();
        VendorBlobCodec.EncodeInput(ds5UsbInSpec, in ds5UsbState,
            leftStickX: 1.0f, leftStickY: 0.0f,        // full right / full up (was +1/-1 in old [-1..+1])
            rightStickX: 0.5f, rightStickY: 0.75f,     // centered / half right
            leftTrigger: 0.7f, rightTrigger: 0.0f,
            ds5UsbInBuf, ds5UsbInState);

        var ds5UsbInAssertions = new (int idx, int expected, string name)[]
        {
            (0,  0x01, "Report ID"),
            (1,  255,  "LeftStickX = 1.0"),
            (2,  0,    "LeftStickY = 0.0"),
            (3,  128,  "RightStickX = 0.5 (center)"),
            (4,  191,  "RightStickY = 0.75"),
            (5,  178,  "LeftTrigger = 0.7"),
            (6,  0,    "RightTrigger = 0"),
        };
        foreach (var (idx, expected, name) in ds5UsbInAssertions)
        {
            total++;
            bool pass = ds5UsbInBuf[idx] == expected;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-22} buf[{idx,2}]={ds5UsbInBuf[idx]} (expected {expected})");
            if (!pass) failures++;
        }
        // Hat NorthEast = 1 in byte 8 low nibble
        total++;
        bool ds5UsbHatPass = (ds5UsbInBuf[8] & 0x0F) == 1;
        Console.WriteLine($"  [{(ds5UsbHatPass ? "PASS" : "FAIL")}] Hat=NorthEast: byte 8 low nibble = {ds5UsbInBuf[8] & 0x0F} (expected 1)");
        if (!ds5UsbHatPass) failures++;

        // Cross (HMButton.A) → byte 8 bit 5
        total++;
        bool ds5UsbCrossPass = (ds5UsbInBuf[8] & 0x20) != 0;
        Console.WriteLine($"  [{(ds5UsbCrossPass ? "PASS" : "FAIL")}] Cross/A: byte 8 = 0x{ds5UsbInBuf[8]:X2} (bit 5 expected set)");
        if (!ds5UsbCrossPass) failures++;

        // L1 (HMButton.LeftBumper) → byte 9 bit 0
        total++;
        bool ds5UsbL1Pass = (ds5UsbInBuf[9] & 0x01) != 0;
        Console.WriteLine($"  [{(ds5UsbL1Pass ? "PASS" : "FAIL")}] L1/LeftBumper: byte 9 = 0x{ds5UsbInBuf[9]:X2} (bit 0 expected set)");
        if (!ds5UsbL1Pass) failures++;

        // LT_DIGITAL (LeftTrigger > 0) → byte 9 bit 2
        total++;
        bool ds5UsbLTDigitalPass = (ds5UsbInBuf[9] & 0x04) != 0;
        Console.WriteLine($"  [{(ds5UsbLTDigitalPass ? "PASS" : "FAIL")}] LT_DIGITAL (LeftTrigger=0.7 > 0): byte 9 bit 2 expected set");
        if (!ds5UsbLTDigitalPass) failures++;

        // RT_DIGITAL (RightTrigger == 0) → byte 9 bit 3 NOT set
        total++;
        bool ds5UsbRTDigitalPass = (ds5UsbInBuf[9] & 0x08) == 0;
        Console.WriteLine($"  [{(ds5UsbRTDigitalPass ? "PASS" : "FAIL")}] RT_DIGITAL (RightTrigger=0): byte 9 bit 3 expected clear");
        if (!ds5UsbRTDigitalPass) failures++;

        // Guide (HMButton.Guide) → byte 10 bit 0
        total++;
        bool ds5UsbGuidePass = (ds5UsbInBuf[10] & 0x01) != 0;
        Console.WriteLine($"  [{(ds5UsbGuidePass ? "PASS" : "FAIL")}] Guide/PS: byte 10 = 0x{ds5UsbInBuf[10]:X2} (bit 0 expected set)");
        if (!ds5UsbGuidePass) failures++;

        Console.WriteLine();

        // ── DS4 USB input encoder (Report 0x01) ────────────────────────
        Console.WriteLine("--- DS4 USB input (Report 0x01) ---");
        var ds4Usb = ctx.GetProfile("dualshock-4-v2")!;
        if (!ds4Usb.HasExtendedInput)
        {
            Console.WriteLine("  [FAIL] dualshock-4-v2 (USB) has no ExtendedReport");
            return 1;
        }
        var ds4UsbInSpec = ds4Usb.ExtendedReport!;

        var ds4UsbState = new HMGamepadState
        {
            Buttons = HMButton.B | HMButton.Start,
            Hat = HMHat.West,
        };
        var ds4UsbInBuf = new byte[ds4UsbInSpec.Size];
        var ds4UsbInEnc = new VendorBlobCodec.EncoderState();
        VendorBlobCodec.EncodeInput(ds4UsbInSpec, in ds4UsbState,
            leftStickX: 0.75f, leftStickY: 0.5f,    // [0..1]: 0.75=+0.5 of old [-1..+1], 0.5=center
            rightStickX: 0.25f, rightStickY: 0.0f,  // 0.25=-0.5, 0.0=full
            leftTrigger: 0.0f, rightTrigger: 1.0f,
            ds4UsbInBuf, ds4UsbInEnc);

        var ds4UsbInAssertions = new (int idx, int expected, string name)[]
        {
            (0, 0x01, "Report ID"),
            (1, 191,  "LeftStickX = 0.75"),
            (2, 128,  "LeftStickY = 0.5 (center)"),
            (3, 64,   "RightStickX = 0.25"),
            (4, 0,    "RightStickY = 0.0"),
            (8, 0,    "LeftTrigger = 0"),
            (9, 255,  "RightTrigger = 1.0"),
        };
        foreach (var (idx, expected, name) in ds4UsbInAssertions)
        {
            total++;
            bool pass = ds4UsbInBuf[idx] == expected;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-22} buf[{idx,2}]={ds4UsbInBuf[idx]} (expected {expected})");
            if (!pass) failures++;
        }
        // Hat West encodes to nibble 6 (HMHat.West=7, encoder writes octant-1=6)
        total++;
        int ds4UsbHat = ds4UsbInBuf[5] & 0x0F;
        bool ds4UsbHatPass = ds4UsbHat == 6;
        Console.WriteLine($"  [{(ds4UsbHatPass ? "PASS" : "FAIL")}] Hat=West: byte 5 low nibble = {ds4UsbHat} (expected 6)");
        if (!ds4UsbHatPass) failures++;

        // Circle (B) → byte 5 bit 6 (high nibble bit 2)
        total++;
        bool ds4UsbCirclePass = (ds4UsbInBuf[5] & 0x40) != 0;
        Console.WriteLine($"  [{(ds4UsbCirclePass ? "PASS" : "FAIL")}] Circle/B: byte 5 = 0x{ds4UsbInBuf[5]:X2} (bit 6 expected set)");
        if (!ds4UsbCirclePass) failures++;

        // Start (Options) → byte 6 bit 5
        total++;
        bool ds4UsbStartPass = (ds4UsbInBuf[6] & 0x20) != 0;
        Console.WriteLine($"  [{(ds4UsbStartPass ? "PASS" : "FAIL")}] Start/Options: byte 6 = 0x{ds4UsbInBuf[6]:X2} (bit 5 expected set)");
        if (!ds4UsbStartPass) failures++;

        // RT_DIGITAL (RightTrigger > 0) → byte 6 bit 3 set
        total++;
        bool ds4UsbRTDigPass = (ds4UsbInBuf[6] & 0x08) != 0;
        Console.WriteLine($"  [{(ds4UsbRTDigPass ? "PASS" : "FAIL")}] RT_DIGITAL (RightTrigger=1.0): byte 6 bit 3 expected set");
        if (!ds4UsbRTDigPass) failures++;

        // LT_DIGITAL (LeftTrigger == 0) → byte 6 bit 2 clear
        total++;
        bool ds4UsbLTDigPass = (ds4UsbInBuf[6] & 0x04) == 0;
        Console.WriteLine($"  [{(ds4UsbLTDigPass ? "PASS" : "FAIL")}] LT_DIGITAL (LeftTrigger=0): byte 6 bit 2 expected clear");
        if (!ds4UsbLTDigPass) failures++;

        Console.WriteLine();

        // ── DS4 BT input encoder (Report 0x11) ────────────────────────
        Console.WriteLine("--- DS4 BT input (Report 0x11) ---");
        var ds4Bt = ctx.GetProfile("dualshock-4-v2-bt")!;
        if (!ds4Bt.HasExtendedInput)
        {
            Console.WriteLine("  [FAIL] dualshock-4-v2-bt has no ExtendedReport");
            return 1;
        }
        var ds4BtSpec = ds4Bt.ExtendedReport!;
        Console.WriteLine($"  reportId=0x{ds4BtSpec.ReportIdByte:X2} (expected 0x11)");
        Console.WriteLine($"  size={ds4BtSpec.Size} fields={ds4BtSpec.Fields.Count}\n");

        var state = new HMGamepadState
        {
            Buttons = HMButton.A | HMButton.LeftBumper | HMButton.Guide,
            Hat = HMHat.NorthEast,
        };
        var buf = new byte[ds4BtSpec.Size];
        var encState = new VendorBlobCodec.EncoderState();
        VendorBlobCodec.EncodeInput(ds4BtSpec, in state,
            leftStickX: 1.0f, leftStickY: 0.0f,
            rightStickX: 0.5f, rightStickY: 0.75f,
            leftTrigger: 1.0f, rightTrigger: 0.5f,
            buf, encState);

        var ds4BtAssertions = new (int idx, int expected, string name)[]
        {
            (0,  0x11, "Report ID"),
            (3,  255,  "LeftStickX = 1.0"),
            (4,  0,    "LeftStickY = 0.0"),
            (5,  128,  "RightStickX = 0.5 (center)"),
            (6,  191,  "RightStickY = 0.75"),
            (10, 255,  "LeftTrigger = 1.0"),
            (11, 128,  "RightTrigger = 0.5"),
        };
        foreach (var (idx, expected, name) in ds4BtAssertions)
        {
            total++;
            bool pass = buf[idx] == expected;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-22} buf[{idx,2}]={buf[idx]} (expected {expected})");
            if (!pass) failures++;
        }
        // Hat NorthEast = 1 in low nibble of byte 7
        total++;
        bool hatPass = (buf[7] & 0x0F) == 1;
        Console.WriteLine($"  [{(hatPass ? "PASS" : "FAIL")}] Hat=NorthEast: byte 7 low nibble = {buf[7] & 0x0F} (expected 1)");
        if (!hatPass) failures++;

        // CRC32 with prefix [0xA1, 0x11]
        total++;
        uint observed = (uint)buf[74] | ((uint)buf[75] << 8) | ((uint)buf[76] << 16) | ((uint)buf[77] << 24);
        uint expected_crc = ComputeCrc32(new byte[] { 0xA1, 0x11 }, buf, 1, 73);
        bool crcPass = observed == expected_crc;
        Console.WriteLine($"  [{(crcPass ? "PASS" : "FAIL")}] CRC32 footer (prefix [0xA1, 0x11]): observed=0x{observed:X8} expected=0x{expected_crc:X8}");
        if (!crcPass) failures++;

        // Decode round-trip
        var (decoded, crcValid) = VendorBlobCodec.Decode(ds4BtSpec, buf);
        total++;
        Console.WriteLine($"  [{(crcValid ? "PASS" : "FAIL")}] CRC verifies on decode");
        if (!crcValid) failures++;

        Console.WriteLine();

        // ── DS4 BT output round-trip (Report 0x11) ─────────────────────
        Console.WriteLine("--- DS4 BT output (Report 0x11) ---");
        var ds4BtOutSpec = ds4Bt.ExtendedOutputReport!;
        var ds4BtOutFields = new Dictionary<string, object>
        {
            { "btTag",      (byte)0xC0 },
            { "btReserved", (byte)0xA0 },
            { "validFlag0", (byte)0xFF },
            { "rightMotor", (byte)200  },
            { "leftMotor",  (byte)64   },
            { "lightbar",   new byte[] { 0xFF, 0x00, 0x80 } },
            { "flashOn",    (byte)10   },
            { "flashOff",   (byte)5    },
        };
        var ds4BtOutBytes = HMOutputEncoder.Encode(ds4Bt, ds4BtOutFields);
        var ds4BtOutAssertions = new (int idx, byte expected, string name)[]
        {
            (0,  0x11, "Report ID"),
            (1,  0xC0, "btTag"),
            (2,  0xA0, "btReserved"),
            (3,  0xFF, "validFlag0"),
            (6,  200,  "rightMotor"),
            (7,  64,   "leftMotor"),
            (8,  0xFF, "lightbar.R"),
            (9,  0x00, "lightbar.G"),
            (10, 0x80, "lightbar.B"),
            (11, 10,   "flashOn"),
            (12, 5,    "flashOff"),
        };
        foreach (var (idx, expected, name) in ds4BtOutAssertions)
        {
            total++;
            bool pass = ds4BtOutBytes[idx] == expected;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-18} buf[{idx,2}]=0x{ds4BtOutBytes[idx]:X2} (expected 0x{expected:X2})");
            if (!pass) failures++;
        }
        // CRC32 [0xA2, 0x11]
        total++;
        uint observedOut = (uint)ds4BtOutBytes[74] | ((uint)ds4BtOutBytes[75] << 8) | ((uint)ds4BtOutBytes[76] << 16) | ((uint)ds4BtOutBytes[77] << 24);
        uint expectedOut = ComputeCrc32(new byte[] { 0xA2, 0x11 }, ds4BtOutBytes, 1, 73);
        bool crcOutPass = observedOut == expectedOut;
        Console.WriteLine($"  [{(crcOutPass ? "PASS" : "FAIL")}] CRC32 footer (prefix [0xA2, 0x11]): observed=0x{observedOut:X8} expected=0x{expectedOut:X8}");
        if (!crcOutPass) failures++;

        // Decode round-trip
        var (ds4BtDecoded, ds4BtCrcValid) = VendorBlobCodec.Decode(ds4BtOutSpec, ds4BtOutBytes);
        total++;
        bool roundTripPass = ds4BtCrcValid
            && ds4BtDecoded.TryGetValue("rightMotor", out var rmObj) && (byte)rmObj == 200
            && ds4BtDecoded.TryGetValue("leftMotor", out var lmObj) && (byte)lmObj == 64
            && ds4BtDecoded.TryGetValue("lightbar", out var rgbObj) && rgbObj is byte[] rgb && rgb[0] == 0xFF;
        Console.WriteLine($"  [{(roundTripPass ? "PASS" : "FAIL")}] decode(encode(x)) round-trips byte-for-byte");
        if (!roundTripPass) failures++;

        Console.WriteLine();

        // ── DS5 USB output round-trip (Report 0x02, no CRC) ────────────
        Console.WriteLine("--- DS5 USB output (Report 0x02) ---");
        if (!ds5Usb.HasExtendedOutput)
        {
            Console.WriteLine("  [FAIL] dualsense (USB) has no ExtendedOutputReport");
            return 1;
        }
        var ds5UsbOutSpec = ds5Usb.ExtendedOutputReport!;
        var ds5UsbFields = new Dictionary<string, object>
        {
            { "validFlag0",      (byte)0xFF },
            { "validFlag1",      (byte)0xF7 },
            { "rightMotor",      (byte)180  },
            { "leftMotor",       (byte)90   },
            { "muteLed",         (byte)0x01 },
            { "validFlag2",      (byte)0x10 },
            { "lightbarSetup",   (byte)0x02 },
            { "ledBrightness",   (byte)0xC0 },
            { "playerIndicator", (byte)0x05 },
            { "lightbar",        new byte[] { 0x00, 0xFF, 0x80 } },
        };
        var ds5UsbBytes = HMOutputEncoder.Encode(ds5Usb, ds5UsbFields);
        var ds5UsbAssertions = new (int idx, byte expected, string name)[]
        {
            (0,  0x02, "Report ID"),
            (1,  0xFF, "validFlag0"),
            (2,  0xF7, "validFlag1"),
            (3,  180,  "rightMotor"),
            (4,  90,   "leftMotor"),
            (9,  0x01, "muteLed"),
            (39, 0x10, "validFlag2"),
            (42, 0x02, "lightbarSetup"),
            (43, 0xC0, "ledBrightness"),
            (44, 0x05, "playerIndicator"),
            (45, 0x00, "lightbar.R"),
            (46, 0xFF, "lightbar.G"),
            (47, 0x80, "lightbar.B"),
        };
        foreach (var (idx, expected, name) in ds5UsbAssertions)
        {
            total++;
            bool pass = ds5UsbBytes[idx] == expected;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-18} buf[{idx,2}]=0x{ds5UsbBytes[idx]:X2} (expected 0x{expected:X2})");
            if (!pass) failures++;
        }
        // Verify length 48
        total++;
        bool lenPass = ds5UsbBytes.Length == 48;
        Console.WriteLine($"  [{(lenPass ? "PASS" : "FAIL")}] Total length = {ds5UsbBytes.Length} (expected 48)");
        if (!lenPass) failures++;

        Console.WriteLine();

        // ── DS4 USB output round-trip (Report 0x05, no CRC) ────────────
        Console.WriteLine("--- DS4 USB output (Report 0x05) ---");
        if (!ds4Usb.HasExtendedOutput)
        {
            Console.WriteLine("  [FAIL] dualshock-4-v2 has no ExtendedOutputReport");
            return 1;
        }
        var ds4UsbFields = new Dictionary<string, object>
        {
            { "validFlag0",  (byte)0xFF },
            { "validFlag1",  (byte)0x04 },
            { "rightMotor",  (byte)150  },
            { "leftMotor",   (byte)50   },
            { "lightbar",    new byte[] { 0xAA, 0x55, 0xCC } },
            { "flashOn",     (byte)20   },
            { "flashOff",    (byte)10   },
        };
        var ds4UsbBytes = HMOutputEncoder.Encode(ds4Usb, ds4UsbFields);
        var ds4UsbAssertions = new (int idx, byte expected, string name)[]
        {
            (0,  0x05, "Report ID"),
            (1,  0xFF, "validFlag0"),
            (2,  0x04, "validFlag1"),
            (4,  150,  "rightMotor"),
            (5,  50,   "leftMotor"),
            (6,  0xAA, "lightbar.R"),
            (7,  0x55, "lightbar.G"),
            (8,  0xCC, "lightbar.B"),
            (9,  20,   "flashOn"),
            (10, 10,   "flashOff"),
        };
        foreach (var (idx, expected, name) in ds4UsbAssertions)
        {
            total++;
            bool pass = ds4UsbBytes[idx] == expected;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-18} buf[{idx,2}]=0x{ds4UsbBytes[idx]:X2} (expected 0x{expected:X2})");
            if (!pass) failures++;
        }
        total++;
        bool ds4LenPass = ds4UsbBytes.Length == 32;
        Console.WriteLine($"  [{(ds4LenPass ? "PASS" : "FAIL")}] Total length = {ds4UsbBytes.Length} (expected 32)");
        if (!ds4LenPass) failures++;

        // Decode round-trip
        var (ds4UsbDecoded, _) = VendorBlobCodec.Decode(ds4Usb.ExtendedOutputReport!, ds4UsbBytes);
        total++;
        bool ds4UsbRoundTripPass = ds4UsbDecoded.TryGetValue("rightMotor", out var ds4Rm) && (byte)ds4Rm == 150
            && ds4UsbDecoded.TryGetValue("lightbar", out var ds4Rgb) && ds4Rgb is byte[] ds4RgbArr
            && ds4RgbArr[0] == 0xAA && ds4RgbArr[1] == 0x55 && ds4RgbArr[2] == 0xCC;
        Console.WriteLine($"  [{(ds4UsbRoundTripPass ? "PASS" : "FAIL")}] decode(encode(x)) preserves rightMotor=150 + lightbar=AA55CC");
        if (!ds4UsbRoundTripPass) failures++;

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine($"=== ALL PASS ({total} cases across DS4 BT input + DS4 BT output + DS5 USB output + DS4 USB output) ===");
            return 0;
        }
        Console.WriteLine($"=== FAIL: {failures}/{total} cases did not match ===");
        return 1;
    }

    private static readonly uint[] s_crc32Table = BuildCrc32Table();
    private static uint[] BuildCrc32Table()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            t[i] = c;
        }
        return t;
    }
    private static uint ComputeCrc32(byte[] prefix, byte[] data, int offset, int count)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in prefix)
            crc = s_crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        for (int i = 0; i < count; i++)
            crc = s_crc32Table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
