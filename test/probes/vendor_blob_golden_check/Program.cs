// VendorBlobCodec golden-report check (issue #34).
//
// The codec is being compiled from per-frame string switches to numeric
// opcodes. This probe locks the WIRE BYTES for every shipped profile and
// every direction BEFORE the refactor, so the opcode implementation must
// reproduce the exact same reports byte for byte:
//
//   - EncodeInput: 3 successive frames per extendedReport profile with a
//     fully-populated deterministic HMGamepadState (every input field
//     type: uint8-rolling stride, uint8-axis, uint8-trigger, hat-octant,
//     button-mask, int16-le IMU, uint32-le and uint16-le timestamps,
//     touchpad-finger x2, uint8-battery, bitfield, crc32-le, constants,
//     and the DualShock 3 uint8-pressure, uint10-be and uint8-level-map).
//   - EncodeOutput: 3 successive frames per extendedOutputReport profile
//     with every semantic populated deterministically (uint8, rgb24,
//     bytes-passthrough, uint8-rolling auto-advance, crc32-le).
//   - Decode: the last encoded output frame, canonicalized field dump +
//     crcValid (locks decode behavior too).
//
// Each artifact is SHA-256 hashed. `--dump` prints the golden table to
// paste below; default mode compares against the embedded goldens and
// exits 1 on any mismatch. Runs UNELEVATED (pure byte manipulation, no
// driver, no devices).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using HIDMaestro;
using HIDMaestro.Internal;

internal static class Program
{
    static readonly string[] ProfileIds =
    {
        "dualsense", "dualsense-edge", "dualsense-bt", "dualsense-bt-full",
        "dualsense-edge-bt", "dualshock-4-v1", "dualshock-4-v1-full",
        "dualshock-4-v2", "dualshock-4-v2-bt",
        // v1.10.0 (PadForge discussion 476).
        "dualshock-3-full",
        // v1.10.1 (issue #65): the native-descriptor DualShock 3 decodes the
        // same output report with the same spec, so its output and decode
        // goldens equal dualshock-3-full's.
        "dualshock-3",
    };

    // Golden table captured from the pre-opcode string-switch codec at
    // 84b8361 (v1.3.18). Regenerate with --dump ONLY when the wire format
    // is intentionally changed, never to make a refactor pass. In v1.10.0
    // (issue #64) the DualShock 4 input rows were regenerated when those
    // maps moved to the layout SDL and Linux parse, and the Bluetooth
    // DualSense input rows when their maps gained the L2 and R2 digital
    // bits. The DualShock 3 rows were added in v1.10.0. In v2.0.0 every
    // input row was regenerated for the one arithmetic (WireArith): with
    // --bytes before and after, each changed report differs only in a
    // uint8-trigger byte, by one count, and in its CRC. Every output and
    // decode row still matches the v1.3.18 capture.
    static readonly string[] Goldens =
    {
        "golden dualsense in 0 728BEC8461DD20621FBC713B28165DAD10DBB0722B6362B707CB35E3DDA1949D",
        "golden dualsense in 1 A038747B8B2306168B7DB408986C1A334CC2C76400A91E3A1C4077A58B59077C",
        "golden dualsense in 2 4A642312163B2345BFBAA76EFC18A12EDA3BAF005253B3FE7A66F279029D6C65",
        "golden dualsense out 0 83807013E95889568F83AFD0DDD4BA218A9AEA757D8E57FE694D9BEB3A29F17C",
        "golden dualsense out 1 D12BD0586374C7816F9474A0FCA296BE2B6E10A98C85894B58E2C4F60F694F5F",
        "golden dualsense out 2 A94544952CCCB8A14C0FB46D6424AED8146D3C46DD7A0C79BC26732785552505",
        "golden dualsense dec 0 52756DE5DB40B971507510186D306B288AD5C352841FE8D253EC995A6143DD40",
        "golden dualsense-edge in 0 CE87BFF757AF39B8657CF6A7DB721E6127D64BE82B6C5FDFFA4A5A8420D8F055",
        "golden dualsense-edge in 1 B0C253148F8C9D162B2F0606CBB0501DCB3002A921BBE0F07924B2E7FAF81EFE",
        "golden dualsense-edge in 2 4A642312163B2345BFBAA76EFC18A12EDA3BAF005253B3FE7A66F279029D6C65",
        "golden dualsense-edge out 0 953E8E28585A6BD2D2C45EFE53853CEAD310F32636A61E5C3EF25178571564EC",
        "golden dualsense-edge out 1 2ED5B6C68433FEECF72737E28F62310F1AF20A9CB5FACACB13967C0B25CA6D09",
        "golden dualsense-edge out 2 EF86AAE9971624DE0319A35D9E8A168B27A96803F9A672FB3106152B2C172774",
        "golden dualsense-edge dec 0 52756DE5DB40B971507510186D306B288AD5C352841FE8D253EC995A6143DD40",
        "golden dualsense-bt in 0 0DC9952F58A9F5914C5A63402EDA6C23031A16C4138725BA531846E7374616F2",
        "golden dualsense-bt in 1 08CA1838B5A3671BB6DBA2F00F79EB3B0F2FB677856EBD37047212BCEC95AEED",
        "golden dualsense-bt in 2 486940D0ED66C62E48D1150137C7226FE0FAED1DA7C653137C735A7CECEEF7DF",
        "golden dualsense-bt out 0 6816CBEC199754791F3D942FA95456760291063D4A92DA99014B32687B24D333",
        "golden dualsense-bt out 1 F6CA9E6EE5D8C3771812E3D931D5314471EFD0AB438A351E1807F72C641DC9BF",
        "golden dualsense-bt out 2 1896ACEA1C3C8709891E9B9AB50F97AD7616709625E887353E4572F4600349F4",
        "golden dualsense-bt dec 0 69D6D7461B7B3D7307FFB3E361681753507849B1D02BC78AF7B323A82C9A5BAB",
        "golden dualsense-bt-full in 0 0DC9952F58A9F5914C5A63402EDA6C23031A16C4138725BA531846E7374616F2",
        "golden dualsense-bt-full in 1 08CA1838B5A3671BB6DBA2F00F79EB3B0F2FB677856EBD37047212BCEC95AEED",
        "golden dualsense-bt-full in 2 486940D0ED66C62E48D1150137C7226FE0FAED1DA7C653137C735A7CECEEF7DF",
        "golden dualsense-bt-full out 0 6816CBEC199754791F3D942FA95456760291063D4A92DA99014B32687B24D333",
        "golden dualsense-bt-full out 1 F6CA9E6EE5D8C3771812E3D931D5314471EFD0AB438A351E1807F72C641DC9BF",
        "golden dualsense-bt-full out 2 1896ACEA1C3C8709891E9B9AB50F97AD7616709625E887353E4572F4600349F4",
        "golden dualsense-bt-full dec 0 69D6D7461B7B3D7307FFB3E361681753507849B1D02BC78AF7B323A82C9A5BAB",
        "golden dualsense-edge-bt in 0 16D5E981226733213D5A4B1B4173B0F8FCD1CA762A4E9A678AF60CC5993C4BD4",
        "golden dualsense-edge-bt in 1 A3B729D5E32818CB57F5527F42C0CA42F7F2E9713A5EEDFEA3A009C27AB46EA2",
        "golden dualsense-edge-bt in 2 04EE60BAB8451D4E11A6DDED4F1988C1A0B839E5769E317B93A58FD2207F641C",
        "golden dualsense-edge-bt out 0 6816CBEC199754791F3D942FA95456760291063D4A92DA99014B32687B24D333",
        "golden dualsense-edge-bt out 1 F6CA9E6EE5D8C3771812E3D931D5314471EFD0AB438A351E1807F72C641DC9BF",
        "golden dualsense-edge-bt out 2 1896ACEA1C3C8709891E9B9AB50F97AD7616709625E887353E4572F4600349F4",
        "golden dualsense-edge-bt dec 0 69D6D7461B7B3D7307FFB3E361681753507849B1D02BC78AF7B323A82C9A5BAB",
        "golden dualshock-4-v1 in 0 02E5D9BA9BC34AE154A6F6E0FF6EEB443CB438F8B78AB36A327C765579B9E3D9",
        "golden dualshock-4-v1 in 1 52E64C54A76192BE211EBC5BE61953025699C7A9A435284EF63B99B2EAA13A60",
        "golden dualshock-4-v1 in 2 87347CE349493A7FE679676C794472D7D369E9E0371C3F623B380338A3D17F5F",
        "golden dualshock-4-v1 out 0 4B7A785C65BAA747572BE6787D8CDA352CF3147A6DF1674918B52DFE099F2299",
        "golden dualshock-4-v1 out 1 392D0B472E16392FB865637AFE56F0D97D5C8630B64DB2E1671D367A77C489C6",
        "golden dualshock-4-v1 out 2 79AB7B18E68124DDD5C19445AE30DCF2D65E6BE5FB70D110A614BAAEF2462C42",
        "golden dualshock-4-v1 dec 0 2F0580BB72322624CE3EF9C03BEC29ED21B61C1F6EA2A55CC4D845AC9F43DEE7",
        "golden dualshock-4-v1-full in 0 02E5D9BA9BC34AE154A6F6E0FF6EEB443CB438F8B78AB36A327C765579B9E3D9",
        "golden dualshock-4-v1-full in 1 52E64C54A76192BE211EBC5BE61953025699C7A9A435284EF63B99B2EAA13A60",
        "golden dualshock-4-v1-full in 2 87347CE349493A7FE679676C794472D7D369E9E0371C3F623B380338A3D17F5F",
        "golden dualshock-4-v1-full out 0 4B7A785C65BAA747572BE6787D8CDA352CF3147A6DF1674918B52DFE099F2299",
        "golden dualshock-4-v1-full out 1 392D0B472E16392FB865637AFE56F0D97D5C8630B64DB2E1671D367A77C489C6",
        "golden dualshock-4-v1-full out 2 79AB7B18E68124DDD5C19445AE30DCF2D65E6BE5FB70D110A614BAAEF2462C42",
        "golden dualshock-4-v1-full dec 0 2F0580BB72322624CE3EF9C03BEC29ED21B61C1F6EA2A55CC4D845AC9F43DEE7",
        "golden dualshock-4-v2 in 0 02E5D9BA9BC34AE154A6F6E0FF6EEB443CB438F8B78AB36A327C765579B9E3D9",
        "golden dualshock-4-v2 in 1 52E64C54A76192BE211EBC5BE61953025699C7A9A435284EF63B99B2EAA13A60",
        "golden dualshock-4-v2 in 2 87347CE349493A7FE679676C794472D7D369E9E0371C3F623B380338A3D17F5F",
        "golden dualshock-4-v2 out 0 4B7A785C65BAA747572BE6787D8CDA352CF3147A6DF1674918B52DFE099F2299",
        "golden dualshock-4-v2 out 1 392D0B472E16392FB865637AFE56F0D97D5C8630B64DB2E1671D367A77C489C6",
        "golden dualshock-4-v2 out 2 79AB7B18E68124DDD5C19445AE30DCF2D65E6BE5FB70D110A614BAAEF2462C42",
        "golden dualshock-4-v2 dec 0 2F0580BB72322624CE3EF9C03BEC29ED21B61C1F6EA2A55CC4D845AC9F43DEE7",
        "golden dualshock-4-v2-bt in 0 90C9C82A8DF453C28F905FDA14C076568AD9234B0C8FDDB1E6298093C61D0D01",
        "golden dualshock-4-v2-bt in 1 C30654128FAD93BB36B8462AB68A4646C0516CA63BB213541159F30F31A560A1",
        "golden dualshock-4-v2-bt in 2 6D3E8E87E3CE443B2D9FA91B8A9EE3B2AFDDC44883733CD9DBD11F70547E0468",
        "golden dualshock-4-v2-bt out 0 753BCBF657E30B407A7F4ADC26077B671B58C767F53513967A8FD40E0E90D722",
        "golden dualshock-4-v2-bt out 1 F43E0A404EEE79E47B825691DF71BBBF0A4E52980B5C5BB68CBD22E00E7E9B34",
        "golden dualshock-4-v2-bt out 2 04FA1DE1110169E9A669C3D7B1951F6E3491342F3BDB434F83ABF732FDBE63AF",
        "golden dualshock-4-v2-bt dec 0 BA2EC300DF7B4D788866A9CFCD8FCBBC1597DF71EE115BCD27FE0786CF4FFBFB",
        "golden dualshock-3-full in 0 4B603F154EB5F63CF20156E9D652FC8A170A9C8580892337A8DE15CB4D0F8ADB",
        "golden dualshock-3-full in 1 66ECB0E68E87A9291D9FAF56DEE9F82A6543D042682B825AB130DE9323547375",
        "golden dualshock-3-full in 2 C6F4908783E37875FD85B6E1A69ED625916535676B94F9F5547EBBD77C5458A8",
        "golden dualshock-3-full out 0 07FCEF7A57C97780DC992A36E93E835A7436DE9186DF30881AD49BD300CED7A5",
        "golden dualshock-3-full out 1 E39DCB23E21D2275F87D4B91219F8B4B9FA006CA0AC9F619F499BF6DF73DBB51",
        "golden dualshock-3-full out 2 3554DA9900A9966F77128BF999984D3012AF0A7C653AC6409522E69D63576FAC",
        "golden dualshock-3-full dec 0 C317D8EC38CC58B36E3AFA5C53AF37BF02C90C18CE30EF18E257E45874E08F09",
        "golden dualshock-3 out 0 07FCEF7A57C97780DC992A36E93E835A7436DE9186DF30881AD49BD300CED7A5",
        "golden dualshock-3 out 1 E39DCB23E21D2275F87D4B91219F8B4B9FA006CA0AC9F619F499BF6DF73DBB51",
        "golden dualshock-3 out 2 3554DA9900A9966F77128BF999984D3012AF0A7C653AC6409522E69D63576FAC",
        "golden dualshock-3 dec 0 C317D8EC38CC58B36E3AFA5C53AF37BF02C90C18CE30EF18E257E45874E08F09",
    };

    static int Main(string[] args)
    {
        bool dump = args.Contains("--dump");
        // --bytes prints each artifact whole beside its hash, so a deliberate
        // regeneration can be checked byte by byte against the tree before.
        bool bytes = args.Contains("--bytes");
        var hex = new List<string>();
        var lines = new List<string>();

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        foreach (var id in ProfileIds)
        {
            var profile = ctx.GetProfile(id);
            if (profile == null) { lines.Add($"golden {id} MISSING-PROFILE"); continue; }

            var inSpec = profile.ExtendedReport;
            if (inSpec != null)
            {
                var encState = new VendorBlobCodec.EncoderState();
                var buf = new byte[inSpec.Size];
                for (int frame = 0; frame < 3; frame++)
                {
                    var state = MakeState(frame);
                    VendorBlobCodec.EncodeInput(inSpec, in state,
                        0.25f + frame * 0.1f, 0.75f, 0.40f, 0.60f,
                        0.10f + frame * 0.2f, 0.90f,
                        buf, encState);
                    lines.Add($"golden {id} in {frame} {Sha(buf)}");
                    hex.Add($"bytes {id} in {frame} {Convert.ToHexString(buf)}");
                }
            }

            var outSpec = profile.ExtendedOutputReport;
            if (outSpec != null)
            {
                var encState = new VendorBlobCodec.EncoderState();
                byte[] last = Array.Empty<byte>();
                for (int frame = 0; frame < 3; frame++)
                {
                    var fields = MakeOutputFields(outSpec, frame);
                    last = VendorBlobCodec.EncodeOutput(outSpec, fields, encState);
                    lines.Add($"golden {id} out {frame} {Sha(last)}");
                    hex.Add($"bytes {id} out {frame} {Convert.ToHexString(last)}");
                }
                var (decoded, crcValid) = VendorBlobCodec.Decode(outSpec, last);
                lines.Add($"golden {id} dec 0 {Sha(Encoding.UTF8.GetBytes(Canonical(decoded, crcValid)))}");
                hex.Add($"bytes {id} dec 0 {Canonical(decoded, crcValid)}");
            }
        }

        if (bytes)
        {
            foreach (var l in hex) Console.WriteLine(l);
            return 0;
        }
        if (dump)
        {
            foreach (var l in lines) Console.WriteLine($"        \"{l}\",");
            return 0;
        }

        int failures = 0;
        var expected = new HashSet<string>(Goldens);
        foreach (var l in lines)
        {
            bool ok = expected.Contains(l);
            if (!ok) failures++;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {l}");
        }
        if (Goldens.Length != lines.Count)
        {
            failures++;
            Console.WriteLine($"  [FAIL] artifact count {lines.Count} != golden count {Goldens.Length}");
        }
        Console.WriteLine($"\n=== {lines.Count - failures}/{lines.Count} golden checks passed ===");
        failures += Vocabulary.Run();
        return failures == 0 ? 0 : 1;
    }

    // Deterministic, fully-populated input state. Every input-direction
    // field type across the 9 specs draws from one of these fields.
    static HMGamepadState MakeState(int frame) => new()
    {
        Buttons = (HMButton)(0x0000_A5A5u ^ (uint)(frame * 0x1111)),
        Hat = HMHat.NorthEast,
        GyroPitch = (short)(1000 + frame * 17),
        GyroYaw = (short)(-2000 + frame * 13),
        GyroRoll = (short)(300 - frame * 7),
        AccelX = (short)(4096 + frame),
        AccelY = (short)(-8192 + frame * 3),
        AccelZ = (short)(512 + frame * 5),
        SensorTimestamp = 0xDEAD0000u + (uint)(frame * 1333),
        TouchpadFinger0Active = true,
        TouchpadFinger0X = (ushort)(960 + frame * 10),
        TouchpadFinger0Y = (ushort)(540 - frame * 10),
        TouchpadFinger0Id = (byte)(1 + frame),
        TouchpadFinger1Active = frame != 1,
        TouchpadFinger1X = 100,
        TouchpadFinger1Y = 200,
        TouchpadFinger1Id = 7,
        // Only the DualShock 4 maps read this, so adding it in v1.10.0 left
        // the DualSense rows untouched.
        TouchpadPacketCounter = (byte)(0x40 + frame * 3),
        BatteryLevel = 8,
        BatteryCharging = true,
        BatteryFull = false,
        MicMuted = frame == 2,
        HeadphonesConnected = true,
    };

    // Deterministic per-semantic values: scalars from a name hash, rgb24
    // as a byte triple, passthrough blobs as a sized pattern. Rolling
    // counters are left OUT of the dict so the codec's auto-advance path
    // (the stride behavior) is what gets locked.
    static Dictionary<string, object> MakeOutputFields(ExtendedReportSpec spec, int frame)
    {
        var fields = new Dictionary<string, object>();
        foreach (var f in spec.Fields)
        {
            if (f.Semantic == null) continue;
            if (f.Type == "crc32-le" || f.Type == "uint8-rolling") continue;
            if (fields.ContainsKey(f.Semantic)) continue;

            switch (f.Type)
            {
                case "rgb24":
                    fields[f.Semantic] = new byte[]
                    {
                        (byte)(10 + frame), (byte)(20 + frame), (byte)(30 + frame),
                    };
                    break;
                case "bytes-passthrough":
                {
                    int len = RangeLen(f.Bytes, 4);
                    var blob = new byte[len];
                    for (int i = 0; i < len; i++)
                        blob[i] = (byte)(NameByte(f.Semantic) + i + frame);
                    fields[f.Semantic] = blob;
                    break;
                }
                default:
                    fields[f.Semantic] = (byte)(NameByte(f.Semantic) + frame);
                    break;
            }
        }
        return fields;
    }

    static byte NameByte(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) h = (h ^ c) * 16777619;
        return (byte)(h & 0x7F);
    }

    static int RangeLen(string? bytes, int fallback)
    {
        if (bytes == null) return fallback;
        var parts = bytes.Split('-');
        if (parts.Length == 2 && int.TryParse(parts[0], out int lo) && int.TryParse(parts[1], out int hi))
            return hi - lo + 1;
        return fallback;
    }

    static string Canonical(Dictionary<string, object> decoded, bool crcValid)
    {
        var sb = new StringBuilder();
        foreach (var kv in decoded.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(kv.Key).Append('=');
            sb.Append(kv.Value switch
            {
                byte[] arr => "hex:" + Convert.ToHexString(arr),
                _ => Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture),
            });
            sb.Append(';');
        }
        sb.Append("crcValid=").Append(crcValid);
        return sb.ToString();
    }

    static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
