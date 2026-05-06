// v1.3.5 — Sony BT extended-output round-trip regression probe.
//
// Validates the bidirectional vendor-blob output codec on a Sony BT
// profile. Pure unit-test: no driver install, no virtual device.
//
// Coverage:
//   - HMOutputEncoder.Encode produces 78-byte Report 0x31 with declared
//     fields landing at the correct offsets per the JSON layout
//   - CRC32 footer with prefix [0xA2, 0x31] matches independent CRC computation
//   - VendorBlobCodec.Decode round-trips: decode(encode(x)) preserves
//     every declared field's value byte-for-byte
//   - Unset fields encode to zero; partial-field dictionaries don't crash
//
// Exit code 0 on all PASS, 1 on any FAIL.

using System;
using System.Collections.Generic;
using HIDMaestro;
using HIDMaestro.Internal;

internal sealed class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("=== v1.3.5 Sony BT extended-output round-trip probe ===\n");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        var profile = ctx.GetProfile("dualsense-bt-full")
            ?? throw new Exception("missing dualsense-bt-full profile");

        if (!profile.HasExtendedOutput)
        {
            Console.WriteLine("  FAIL: profile.HasExtendedOutput is false — extendedOutputReport not loaded from JSON");
            return 1;
        }

        var spec = profile.ExtendedOutputReport!;
        Console.WriteLine($"  Profile: {profile.Id}");
        Console.WriteLine($"  ExtendedOutputReport.reportId = 0x{spec.ReportIdByte:X2} (expected 0x31)");
        Console.WriteLine($"  ExtendedOutputReport.size     = {spec.Size} (expected 78)\n");

        int failures = 0;
        int total = 0;

        // ── Test 1: encode known values, verify byte placement ─────────
        var fields = new Dictionary<string, object>
        {
            { "btTag",           (byte)0x02 },
            { "validFlag0",      (byte)0xFF },
            { "validFlag1",      (byte)0xF7 },
            { "rightMotor",      (byte)200  },
            { "leftMotor",       (byte)64   },
            { "muteLed",         (byte)0x01 },
            { "validFlag2",      (byte)0x10 },
            { "lightbarSetup",   (byte)0x02 },
            { "ledBrightness",   (byte)0x80 },
            { "playerIndicator", (byte)0x05 },
            { "lightbar",        new byte[] { 0xFF, 0x00, 0x80 } },
        };

        var encoded = HMOutputEncoder.Encode(profile, fields);

        total++;
        bool sizePass = encoded.Length == 78;
        Console.WriteLine($"  [{(sizePass ? "PASS" : "FAIL")}] Encoded length = {encoded.Length} (expected 78)");
        if (!sizePass) failures++;

        var byteAssertions = new (int idx, byte expected, string name)[]
        {
            (0,  0x31, "Report ID"),
            (1,  0x02, "btTag"),
            (2,  0x10, "btFlag (constant 0x10 — Sony BT framing)"),
            (3,  0xFF, "validFlag0"),
            (4,  0xF7, "validFlag1"),
            (5,  200,  "rightMotor"),
            (6,  64,   "leftMotor"),
            (11, 0x01, "muteLed"),
            (41, 0x10, "validFlag2"),
            (44, 0x02, "lightbarSetup"),
            (45, 0x80, "ledBrightness"),
            (46, 0x05, "playerIndicator"),
            (47, 0xFF, "lightbar.R"),
            (48, 0x00, "lightbar.G"),
            (49, 0x80, "lightbar.B"),
        };
        foreach (var (idx, expected, name) in byteAssertions)
        {
            total++;
            bool pass = encoded[idx] == expected;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name,-18} encoded[{idx,2}] = 0x{encoded[idx]:X2} (expected 0x{expected:X2})");
            if (!pass) failures++;
        }

        // ── Test 2: CRC32 footer matches independent computation ──────
        total++;
        uint observedCrc = (uint)encoded[74]
                         | ((uint)encoded[75] << 8)
                         | ((uint)encoded[76] << 16)
                         | ((uint)encoded[77] << 24);
        uint expectedCrc = ComputeCrc32(new byte[] { 0xA2, 0x31 }, encoded, 1, 73);
        bool crcPass = observedCrc == expectedCrc;
        Console.WriteLine($"  [{(crcPass ? "PASS" : "FAIL")}] CRC32 footer: observed=0x{observedCrc:X8} expected=0x{expectedCrc:X8}");
        if (!crcPass) failures++;

        // ── Test 3: decode round-trips byte-for-byte ──────────────────
        var (decoded, crcValid) = VendorBlobCodec.Decode(spec, encoded);
        total++;
        Console.WriteLine($"  [{(crcValid ? "PASS" : "FAIL")}] Decode CRC verification: crcValid={crcValid}");
        if (!crcValid) failures++;

        // Round-trip every declared field
        var roundTripFields = new (string semantic, object expected)[]
        {
            ("btTag",           (byte)0x02),
            ("validFlag0",      (byte)0xFF),
            ("validFlag1",      (byte)0xF7),
            ("rightMotor",      (byte)200),
            ("leftMotor",       (byte)64),
            ("muteLed",         (byte)0x01),
            ("validFlag2",      (byte)0x10),
            ("lightbarSetup",   (byte)0x02),
            ("ledBrightness",   (byte)0x80),
            ("playerIndicator", (byte)0x05),
        };
        foreach (var (semantic, expected) in roundTripFields)
        {
            total++;
            bool present = decoded.TryGetValue(semantic, out var observed);
            bool match = present && observed!.Equals(expected);
            Console.WriteLine($"  [{(match ? "PASS" : "FAIL")}] decode[{semantic}] = {observed ?? "(missing)"} (expected {expected})");
            if (!match) failures++;
        }

        // RGB array equality
        total++;
        bool rgbPresent = decoded.TryGetValue("lightbar", out var rgbObj);
        bool rgbMatch = rgbPresent && rgbObj is byte[] rgb
                     && rgb.Length == 3
                     && rgb[0] == 0xFF && rgb[1] == 0x00 && rgb[2] == 0x80;
        Console.WriteLine($"  [{(rgbMatch ? "PASS" : "FAIL")}] decode[lightbar] = " +
            (rgbObj is byte[] r ? $"[{r[0]:X2},{r[1]:X2},{r[2]:X2}]" : "(missing)") +
            " (expected [FF,00,80])");
        if (!rgbMatch) failures++;

        // ── Test 4: unset fields encode to zero ────────────────────────
        var minimal = new Dictionary<string, object>
        {
            { "rightMotor", (byte)128 },
        };
        var encMin = HMOutputEncoder.Encode(profile, minimal);
        total++;
        bool zeroPass = encMin[3] == 0 && encMin[4] == 0 && encMin[6] == 0 && encMin[44] == 0 && encMin[47] == 0;
        Console.WriteLine($"  [{(zeroPass ? "PASS" : "FAIL")}] Unset fields encode to zero (validFlag0/1=0, leftMotor=0, lightbar=0)");
        if (!zeroPass) failures++;

        total++;
        bool setPass = encMin[5] == 128;
        Console.WriteLine($"  [{(setPass ? "PASS" : "FAIL")}] Set field encodes correctly: rightMotor[5] = {encMin[5]} (expected 128)");
        if (!setPass) failures++;

        // Critical: byte 2 (btFlag) must be 0x10 even when consumer doesn't
        // pass it. Real Sony BT firmware silently drops the effect packet
        // if byte 2 is anything other than 0x10. Per dualsense-tester.
        total++;
        bool btFlagPass = encMin[2] == 0x10;
        Console.WriteLine($"  [{(btFlagPass ? "PASS" : "FAIL")}] btFlag default: encMin[2] = 0x{encMin[2]:X2} (expected 0x10 from JSON 'initial': 16)");
        if (!btFlagPass) failures++;

        // ── Test 5: bytes-passthrough for trigger effects ──────────────
        var withTriggers = new Dictionary<string, object>
        {
            { "rightTriggerEffect", new byte[] { 0x01, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xA0 } },
            { "leftTriggerEffect",  new byte[] { 0x21, 0x05, 0x0A, 0x0F, 0x14, 0x19, 0x1E, 0x23, 0x28, 0x2D, 0x32 } },
        };
        var encT = HMOutputEncoder.Encode(profile, withTriggers);

        total++;
        bool rtePass = encT[13] == 0x01 && encT[14] == 0x10 && encT[23] == 0xA0;
        Console.WriteLine($"  [{(rtePass ? "PASS" : "FAIL")}] rightTriggerEffect at bytes 13-23: encT[13..23] starts {encT[13]:X2} {encT[14]:X2} ... ends {encT[23]:X2}");
        if (!rtePass) failures++;

        total++;
        bool ltePass = encT[24] == 0x21 && encT[25] == 0x05 && encT[34] == 0x32;
        Console.WriteLine($"  [{(ltePass ? "PASS" : "FAIL")}] leftTriggerEffect at bytes 24-34: encT[24..34] starts {encT[24]:X2} {encT[25]:X2} ... ends {encT[34]:X2}");
        if (!ltePass) failures++;

        // Decode: bytes-passthrough should round-trip the byte arrays
        var (decT, _) = VendorBlobCodec.Decode(spec, encT);
        total++;
        bool decRtePass = decT.TryGetValue("rightTriggerEffect", out var rteObj)
            && rteObj is byte[] rte && rte.Length == 11 && rte[0] == 0x01 && rte[10] == 0xA0;
        Console.WriteLine($"  [{(decRtePass ? "PASS" : "FAIL")}] decode round-trip: rightTriggerEffect bytes preserved");
        if (!decRtePass) failures++;

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine($"=== ALL PASS ({total} cases) ===");
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
