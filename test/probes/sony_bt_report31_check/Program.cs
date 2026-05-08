// v1.3.5 — Sony BT Report 0x31 emit + CRC32 regression probe (#20).
//
// Verifies the data-driven vendor-blob input encoder produces a correct
// 78-byte Report 0x31 with valid layout and CRC32 footer. Pure encoder
// unit test: no driver install, no virtual device — instantiates the
// VendorBlobCodec directly against the dualsense-bt-full profile's
// extendedReport spec.
//
// Coverage:
//   - Report ID byte == 0x31 (NOT 0x01 — the bug fix for #20)
//   - Stick X/Y/RX/RY at bytes 2..5 with center 128
//   - Triggers at bytes 6..7
//   - Hat octant in low nibble of byte 9 (NorthEast = 1)
//   - Buttons mask in byte 9 high nibble + bytes 10..11
//   - CRC32 footer at bytes 74..77 with prefix [0xA1, 0x31] over bytes 1..73
//   - Rolling counter (reportCounter at byte 8) advances monotonically
//
// Exit code 0 on all PASS, 1 on any FAIL.

using System;
using HIDMaestro;
using HIDMaestro.Internal;

internal sealed class Program
{
    public static int Main(string[] args)
    {
        Console.WriteLine("=== v1.3.5 Sony BT Report 0x31 encoder probe (#20) ===\n");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        var profile = ctx.GetProfile("dualsense-bt-full")
            ?? throw new Exception("missing dualsense-bt-full profile");

        if (!profile.HasExtendedInput)
        {
            Console.WriteLine("  FAIL: profile.HasExtendedInput is false — extendedReport not loaded from JSON");
            return 1;
        }

        var spec = profile.ExtendedReport!;
        Console.WriteLine($"  Profile: {profile.Id}");
        Console.WriteLine($"  ExtendedReport.reportId = 0x{spec.ReportIdByte:X2} (expected 0x31)");
        Console.WriteLine($"  ExtendedReport.size     = {spec.Size} (expected 78)");
        Console.WriteLine($"  Field count             = {spec.Fields.Count}\n");

        int failures = 0;
        int total = 0;

        // Test 1: report ID byte
        total++;
        if (spec.ReportIdByte != 0x31)
        {
            Console.WriteLine($"  [FAIL] ReportIdByte = 0x{spec.ReportIdByte:X2}, expected 0x31");
            failures++;
        }
        else
        {
            Console.WriteLine("  [PASS] ReportIdByte = 0x31");
        }

        // Test 2: encoder produces correct stick / trigger / button bytes.
        // v1.3.9 — sticks/triggers passed explicitly to EncodeInput in
        // uniform [0..1] convention.
        var state = new HMGamepadState
        {
            Buttons = HMButton.A | HMButton.LeftBumper | HMButton.Guide,
            Hat = HMHat.NorthEast,  // hat-octant value = 1
        };

        var buffer = new byte[spec.Size];
        var encState = new VendorBlobCodec.EncoderState();
        VendorBlobCodec.EncodeInput(spec, in state,
            leftStickX: 1.0f, leftStickY: 0.0f,        // 1.0 = full right; 0.0 = old -1
            rightStickX: 0.5f, rightStickY: 0.75f,     // 0.5 = center; 0.75 = old +0.5
            leftTrigger: 1.0f, rightTrigger: 0.5f,
            buffer, encState);

        total++;
        bool reportIdPass = buffer[0] == 0x31;
        Console.WriteLine($"  [{(reportIdPass ? "PASS" : "FAIL")}] buffer[0] = 0x{buffer[0]:X2} (expected 0x31)");
        if (!reportIdPass) failures++;

        var stickAssertions = new (int idx, int expected, string name)[]
        {
            (2, 255, "LeftStickX = 1.0"),
            (3,   0, "LeftStickY = 0.0"),
            (4, 128, "RightStickX = 0.5 (center)"),
            // RightStickY = 0.75 → round(0.75 * 255) = 191
            (5, 191, "RightStickY = 0.75"),
            (6, 255, "LeftTrigger = 1.0"),
            // RightTrigger = 0.5 → round(0.5*255) = 128
            (7, 128, "RightTrigger = 0.5"),
        };
        foreach (var (idx, expected, name) in stickAssertions)
        {
            total++;
            bool pass = buffer[idx] == expected;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name}: buffer[{idx}] = {buffer[idx]} (expected {expected})");
            if (!pass) failures++;
        }

        // Hat-octant low nibble: NorthEast = HMHat value 2 → idx (2-1) = 1
        total++;
        int hatNibble = buffer[9] & 0x0F;
        bool hatPass = hatNibble == 1;
        Console.WriteLine($"  [{(hatPass ? "PASS" : "FAIL")}] Hat=NorthEast: buffer[9] low nibble = {hatNibble} (expected 1)");
        if (!hatPass) failures++;

        // Button-mask high nibble of byte 9: bit 1 = A (Cross)
        total++;
        int buttonsHi = (buffer[9] >> 4) & 0x0F;
        bool aPass = (buttonsHi & 0x02) != 0;  // A is index 1 in ["X","A","B","Y"]
        Console.WriteLine($"  [{(aPass ? "PASS" : "FAIL")}] Button A (Cross): byte 9 high nibble = 0x{buttonsHi:X1} (bit 1 expected set)");
        if (!aPass) failures++;

        // Button-mask byte 10: LeftBumper at bit 0
        total++;
        bool lbPass = (buffer[10] & 0x01) != 0;
        Console.WriteLine($"  [{(lbPass ? "PASS" : "FAIL")}] LeftBumper: buffer[10] = 0x{buffer[10]:X2} (bit 0 expected set)");
        if (!lbPass) failures++;

        // Byte 11: Guide at bit 0
        total++;
        bool guidePass = (buffer[11] & 0x01) != 0;
        Console.WriteLine($"  [{(guidePass ? "PASS" : "FAIL")}] Guide: buffer[11] = 0x{buffer[11]:X2} (bit 0 expected set)");
        if (!guidePass) failures++;

        // CRC32 footer at bytes 74..77
        total++;
        uint observedCrc = (uint)buffer[74]
                         | ((uint)buffer[75] << 8)
                         | ((uint)buffer[76] << 16)
                         | ((uint)buffer[77] << 24);
        // Reproduce the CRC computation: prefix [0xA1, 0x31] over buffer[1..73]
        uint expectedCrc = ComputeCrc32(new byte[] { 0xA1, 0x31 }, buffer, 1, 73);
        bool crcPass = observedCrc == expectedCrc;
        Console.WriteLine($"  [{(crcPass ? "PASS" : "FAIL")}] CRC32: observed=0x{observedCrc:X8} expected=0x{expectedCrc:X8}");
        if (!crcPass) failures++;

        // Rolling counter monotonic advance
        total++;
        // First call already happened; next encode should advance the counter at byte 8
        byte counterBefore = buffer[8];
        VendorBlobCodec.EncodeInput(spec, in state,
            leftStickX: 1.0f, leftStickY: 0.0f,
            rightStickX: 0.5f, rightStickY: 0.75f,
            leftTrigger: 1.0f, rightTrigger: 0.5f,
            buffer, encState);
        byte counterAfter = buffer[8];
        bool counterPass = counterAfter == (byte)(counterBefore + 1);
        Console.WriteLine($"  [{(counterPass ? "PASS" : "FAIL")}] reportCounter advanced: {counterBefore} → {counterAfter}");
        if (!counterPass) failures++;

        // Test cycling all 8 hat directions
        Console.WriteLine();
        var hatCases = new (HMHat dir, int expectedNibble, string name)[]
        {
            (HMHat.None,      8, "None"),    // neutralValue
            (HMHat.North,     0, "North"),
            (HMHat.NorthEast, 1, "NorthEast"),
            (HMHat.East,      2, "East"),
            (HMHat.SouthEast, 3, "SouthEast"),
            (HMHat.South,     4, "South"),
            (HMHat.SouthWest, 5, "SouthWest"),
            (HMHat.West,      6, "West"),
            (HMHat.NorthWest, 7, "NorthWest"),
        };
        foreach (var (dir, expectedNibble, name) in hatCases)
        {
            total++;
            var hatState = new HMGamepadState { Hat = dir };
            VendorBlobCodec.EncodeInput(spec, in hatState,
                leftStickX: 0.5f, leftStickY: 0.5f,
                rightStickX: 0.5f, rightStickY: 0.5f,
                leftTrigger: 0.0f, rightTrigger: 0.0f,
                buffer, encState);
            int observed = buffer[9] & 0x0F;
            bool pass = observed == expectedNibble;
            Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] Hat={name,-10} → byte 9 low nibble = {observed} (expected {expectedNibble})");
            if (!pass) failures++;
        }

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine($"=== ALL PASS ({total} cases) ===");
            return 0;
        }
        Console.WriteLine($"=== FAIL: {failures}/{total} cases did not match ===");
        return 1;
    }

    // Stand-alone CRC32 reference. Same algorithm as VendorBlobCodec uses
    // internally — duplicated here so the probe is independent of the SDK's
    // implementation choice (avoids tautological "encoder validates itself"
    // testing).
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
