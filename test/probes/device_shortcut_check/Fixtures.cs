// Snippets with known hits, run before the tree. Each rule, the allowed
// names and INF lines tables and rule D's marker and removed-region checks
// have a case here, and a snippet must give exactly its expected hits.
using System;
using System.IO;
using System.Linq;

internal static class Fixtures
{
    // Read as sdk\HIDMaestro.Core\HMLayout.cs, so the table's HMLayout.cs
    // entries apply. Line 27 is a frozen-compat marker in a C# comment.
    // Lines 28 to 34 are the review's F13 forms, line 35 a split comparison
    // with no ID beside it, which does not count.
    const string CsSource = """"
        enum HMButtonRole { Xbox, ModeSwitchLow, SwitchMode, Ps }
        class Fixture
        {
            // class SwitchHidden { }  0x057E "switch-pro"
            string a = "class SonyInString 0x057E";
            string b = $"VID_{vid}&PID_{pid}";
            string c = @"VID_057E";
            string d = """switch-pro""";
            string e = $"{0x057E:X4}";
            char q = '"';
            int deckle;
            void Run() { SwitchToFiber(); if (reportId == 0x05) { } if (reportId == 0x2009) { } }
            bool IsPro(ushort productId) => productId == 0x0BA0;
            int M(ushort pid) { switch (pid) { case 0x2009: return 1; } return 0; }
            int N(ushort pid) => pid switch { 0x2069 => 1, _ => 0 };
            bool O(ushort vid, ushort x) => vid == 0x057E && x == 0x2009;
            const ushort Magic = 0x1234;
            bool P(ushort hwPid) => hwPid == Magic;
            void Q() { int pid = 0; if (pid != 0) { } ushort VendorId = 1118; }
            void R(bool ds4, int steam) { var nintendoLayout = 1; foreach (var sonyPad in pads) { } }
            bool S(string n) => n.Contains("xbox") || n == "DualSense";
            string ProductName = "Steam Deck";
            void T() { Log("Switch Pro ready"); }
            byte[] U = new byte[] { 0x7E, 0x05, 0x69, 0x20 };
            string W = "057E";
            int SixAxisRate;
            // frozen-compat begin sony-feature-table
            static bool M1(ControllerProfile p) { int v1 = 1406; return p.VendorId == v1; }
            static bool M2(ControllerProfile p) { int v2 = 0x57E; return p.VendorId == v2; }
            static bool M3(ushort vendorId, ushort w, byte hi, byte lo) => vendorId == w && hi == 0x20 && lo == 0x09;
            static readonly byte[] Id = { 0x09, 0x20 };
            static bool M4(ControllerProfile p) => p.ProductId == ((0x20 << 8) | 0x09);
            const string Brand = "Nintendo";
            static bool M5(string manufacturer) => manufacturer.StartsWith(Brand);
            static bool M6(byte hi, byte lo) => hi == 0x20 && lo == 0x09;
        }
        """";

    static readonly string[] s_csExpected =
    {
        "1 declared names", "7 vendor IDs", "8 profile ids", "9 vendor IDs", "13 product IDs", "14 product IDs",
        "15 product IDs", "16 vendor IDs", "16 product IDs", "17 product IDs", "18 product IDs", "19 vendor IDs",
        "20 declared names", "20 declared names", "20 declared names", "20 declared names",
        "21 device names in strings", "21 device names in strings", "21 profile ids", "22 device names in strings",
        "24 vendor IDs", "25 vendor IDs", "27 frozen-compat markers",
        "28 vendor IDs", "28 vendor IDs", "29 vendor IDs", "29 vendor IDs", "30 product IDs", "31 product IDs",
        "32 product IDs", "33 device names in strings", "34 device names in strings",
    };

    // Read as driver\fixture.c. Lines 6 and 8 are region markers and line
    // 14 the review's F12 bypass, a gate line declaring a vendor ID and a
    // function comparing it: each marker fails, and the code on and between
    // them is checked as any other (line 7). Line 15 is the F13 C form.
    // Line 16 keeps code of the fixture region fx-kept with its whitespace
    // changed, and line 17 a line fx-kept shared with code outside it at
    // the old commit, which no print holds alone. fx-gone is gone.
    const string CSource = """
        #define SONY_VID 0x054C
        #define DS4_PRODUCT_ID 0x05C4
        typedef struct _CTX { BOOLEAN SwitchMode; UCHAR Mac[6]; } CTX, *PCTX;
        static const UCHAR g_SonyTable[2] = { 1, 2 };
        static VOID Ds3Helper(int a);
        /* frozen-compat begin fx-good */
        static int a(USHORT ProductID) { return ProductID == 0x09CC; }
        /* frozen-compat end fx-good */
        static int y(USHORT ProductID) { return ProductID == 0x09CC; }
        static const WCHAR *s = L"HID\\VID_054C&PID_09CC";
        static int deckle;
        static void f(void) { static const UCHAR ds4Table[2] = { 1, 2 }; int ds4 = 0; UCHAR ds3Raw[4]; }
        static const WCHAR defaultStr[] = L"Controller (XBOX 360 For Windows)";
        static const USHORT g_NintendoVendor = 0x057E; static BOOLEAN IsSwitch(USHORT v, USHORT p) { return v == 0x057E && p == 0x2009; } /* frozen-compat gate fx-bypass */
        static BOOLEAN MutC(PDEVICE_CONTEXT ctx) { USHORT v = 0x54C; return ctx->HidDeviceAttributes.VendorID == v; }
        static	int   kept_from_region =  7;  /* a comment */
        static int shared = 0;
        """;

    static readonly string[] s_cExpected =
    {
        "1 declared names", "1 vendor IDs", "2 declared names", "2 product IDs", "3 declared names", "4 declared names",
        "5 declared names", "6 frozen-compat markers", "7 product IDs", "8 frozen-compat markers", "9 product IDs",
        "10 vendor IDs", "10 product IDs", "12 declared names", "12 declared names", "12 declared names",
        "13 device names in strings", "14 declared names", "14 declared names", "14 vendor IDs", "14 vendor IDs",
        "14 product IDs", "14 frozen-compat markers", "15 vendor IDs", "15 vendor IDs", "16 removed regions",
    };

    // The fixture's old commit. fx-kept is lines 2 to 4, whose line 3 is
    // also at line 1 outside it, fx-gone is line 6, fx-table lines 7 to 9
    // (a data table) and fx-gate lines 10 to 17, whose line 16 is dispatch
    // the plan keeps (s_kept) and whose line 13 declares a local every
    // function may declare.
    const string OldSource = """
        static int shared = 0;
        static int kept_from_region = 7;
        static int shared = 0;
        static int dropped = 9;
        static int other = 1;
        static int gone_entirely = 3;
        static const unsigned char g_Table[8] = {
            0x40, 0x1F, 0xC0, 0xE0,
            0xF4, 0x01, 0x00, 0x20 };
        typedef struct _GCTX { BOOLEAN GateField; ULONG SeqNo; } GCTX, *PGCTX;
        static void Gate(PGCTX ctx, ULONG cmd, ULONG seqNo)
        {
            ULONG i;
            if (ctx->GateField) { ctx->SeqNo = seqNo; return; }
            for (i = 0; i < 4; i++) { KeptCall(ctx); }
            switch (cmd) { case 0x02: KeptCall(ctx); break; }
        }
        """;

    static readonly RemovedRegion[] s_regions =
    {
        new("fx-kept", @"driver\fixture.c", 2, 4, "fixture", "fixture"),
        new("fx-gone", @"driver\fixture.c", 6, 6, "fixture", "fixture"),
        new("fx-table", @"driver\fixture.c", 7, 9, "fixture", "fixture"),
        new("fx-gate", @"driver\fixture.c", 10, 17, "fixture", "fixture"),
    };

    static readonly KeptRange[] s_kept = { new(@"driver\fixture.c", 16, 16, "fixture") };

    // Rule D's cases, each read as driver\fixture.c (or the path given)
    // with the regions found and the lines they are found from.
    static readonly (string Name, string Rel, string Source, string Expected)[] s_ruleD =
    {
        ("a region line split over five lines", @"driver\fixture.c", """
            static void N(PGCTX ctx, ULONG seqNo)
            {
                if (ctx->GateField)
                {
                    ctx->SeqNo
                        = seqNo; return;
                }
            }
            """, "fx-gate 3-5"),
        ("a table renamed and wrapped at another width", @"driver\fixture.c", """
            static const unsigned char other_name[8] = { 0x40, 0x1F,
                0xC0, 0xE0, 0xF4,
                0x01, 0x00, 0x20 };
            """, "fx-table 1-3"),
        ("the table's values in decimal, in a .inl", @"driver\sub\table.inl", """
            static const unsigned char t2[] = { 64, 31, 192, 224, 244, 1, 0, 32 };
            """, "fx-table 1"),
        ("a field pasted by a macro", @"driver\fixture.c", """
            #define GF(c) (c)->Gate##Field
            static void N(PGCTX ctx, ULONG seqNo) { if (GF(ctx)) { ctx->SeqNo = seqNo; return; } }
            """, "fx-gate 2"),
        ("the code in a #define body", @"driver\fixture.c", """
            #define BODY(ctx) if (ctx->GateField) { ctx->SeqNo = seqNo; \
                return; }
            """, "fx-gate 1"),
        ("under third_party", @"driver\openvr\third_party\x\y.h", """
            static int kept_from_region = 7;
            """, "fx-kept 1"),
        ("the kept dispatch, a local every function declares, a loop kept code has, the region gone", @"driver\fixture.c", """
            static void K(PCTX ctx, ULONG cmd)
            {
                ULONG i;
                int bit;
                switch (cmd) { case 0x02: KeptCall(ctx); break; }
                for (i = 0; i < len; i++) { other = 0x00, 0x00; }
            }
            static int shared = 0;
            """, ""),
    };

    // Rules A to C on the forms the second review found missed. Read as
    // sdk\HIDMaestro.Core\Internal\Fixture2.cs.
    static readonly string Cs2Source = Cs2Text.Replace("{U+2010}", "\u2010", StringComparison.Ordinal);

    const string Cs2Text = """
        class Fixture2
        {
            const string A = "Dual"; const string B = "Sense";
            bool F1(string product) => product.Contains(A + B);
            bool F2(string m) => m.StartsWith("Nin" + "tendo");
            bool F3(string m) => m == $"{"Dual"}Sense";
            bool F4(string m) => m.Equals(string.Concat("Dual", "Sense"));
            int F5(string m) => m.IndexOf("xbo" + "x");
            bool F6(string n) => Regex.IsMatch(n, "dual.?sense");
            bool F7(string n) => Regex.IsMatch(n, "Nintendo");
            bool F8(ControllerProfile profile) => profile.Id == "8bitdo-" + "pro-2";
            bool F9(ControllerProfile profile) { ushort maker = profile.VendorId; return maker == 1406; }
            bool F10(ControllerProfile p) { byte hi = (byte)(p.ProductId >> 8), lo = (byte)p.ProductId; return hi == 0x20 && lo == 0x09; }
            static readonly byte[] Sig = { 0x09, 0x20 };
            bool F11(ControllerProfile p) { byte lo = (byte)p.ProductId, hi = (byte)(p.ProductId >> 8); return lo == Sig[0] && hi == Sig[1]; }
            bool F12(ControllerProfile p) => p.VendorId == unchecked((ushort)0x1057E);
            bool F13(ControllerProfile p) => p.VendorId == int.Parse("57E", NumberStyles.HexNumber);
            static bool IsVendor(ControllerProfile p, ushort vid) => p.VendorId == vid;
            bool F14(ControllerProfile p) => IsVendor(p, 0x54C) || IsVendor(p, 1356);
            string M1 = "frozen-" + "compat";
            // frozen_compat
            // frozen{U+2010}compat
            class StadiaRumble { } class LunaStickCenter { } class LogitechWheelRange { } class HoripadMap { } class EightBitDoFix { }
            class Ps5Thing { } class Ps4Report { } class NsProPacker { } class Xboxone { } class Steamdeck { } class Wiimote { }
            bool F15(string n) => new Regex("(?i)xbox").IsMatch(n);
            const string F = "frozen-"; string M2 = F + "compat";
            bool IsHorizontal(string n) => n.Contains("Arcade" + " Stick") || n == "a" + "b";
            static readonly byte[] Sig2 = { 0x09, 0x20 }; static bool F17(byte[] report) => report[1] == Sig2[0] && report[2] == Sig2[1];
            static bool F18(string manufacturer) { string s = "Nin" + "tendo"; return manufacturer.StartsWith(s); }
            static bool IsVendor2(ControllerProfile p, int x) => p.VendorId == x; bool F19(ControllerProfile p) => IsVendor2(p, 1356);
            // Frozen-Compat
            // frozen&#45;compat
            /* frozen-
               compat */
        }
        """;

    static readonly string[] s_cs2Expected =
    {
        "4 device names in strings", "4 profile ids", "5 device names in strings", "6 device names in strings", "6 profile ids",
        "7 device names in strings", "7 profile ids",
        "8 device names in strings", "9 device names in strings", "10 device names in strings", "11 device names in strings",
        "11 profile ids", "12 vendor IDs", "13 product IDs", "14 product IDs", "16 vendor IDs", "17 vendor IDs",
        "19 vendor IDs", "19 vendor IDs", "20 frozen-compat markers", "21 frozen-compat markers", "22 frozen-compat markers",
        "23 declared names", "23 declared names", "23 declared names", "23 declared names", "23 declared names",
        "24 declared names", "24 declared names", "24 declared names", "24 declared names", "24 declared names", "24 declared names",
        "25 device names in strings", "26 frozen-compat markers", "28 product IDs", "29 device names in strings",
        "29 device names in strings", "30 vendor IDs", "31 frozen-compat markers", "32 frozen-compat markers", "33 frozen-compat markers",
    };

    // The same forms in C. Read as driver\fixture2.c.
    const string C2Source = """
        static BOOLEAN C1(PDEVICE_CONTEXT ctx) { USHORT maker = ctx->HidDeviceAttributes.VendorID; return maker == 1406; }
        static const WCHAR *C2(const WCHAR *buf) { return wcsstr(buf, L"DUAL" L"SENSE"); }
        #define FIRST_PART L"DUAL"
        static const WCHAR *C3(const WCHAR *buf) { return wcsstr(buf, FIRST_PART L"SENSE"); }
        static ULONG C4(void) { return strtoul("57E", NULL, 16); }
        """;

    static readonly string[] s_c2Expected =
    {
        "1 vendor IDs", "2 device names in strings", "2 profile ids", "4 device names in strings", "4 profile ids", "5 vendor IDs",
    };

    // Read as driver\hidmaestro_xusb.inf. Line 4 reaches an ID through a
    // [Strings] token. Line 5 is a model line the table allows.
    const string InfSource = """
        [Manufacturer]
        %Mfg% = Models,NTamd64
        [Models.NTamd64]
        %Desc% = Install, %PadHwId%
        %DeviceDesc% = XUSB_Install, root\VID_045E&PID_028E&XI_00
        [Strings]
        PadHwId = "HID\VID_054C&PID_0CE6"
        """;

    static readonly string[] s_infExpected = { "4 vendor IDs", "4 product IDs" };

    public static bool Run(Catalog cat)
    {
        var prints = RegionPrints.Build(new[] { (Path.Combine("driver", "fixture.c"), OldSource) }, s_regions, s_kept,
                                        name => cat.Words.Match(name).Hit);
        // fx-gone prints its one line. fx-kept prints windows holding its
        // own names and none of line 3 alone, which code outside holds.
        // fx-gate prints nothing of the kept line or of the local i.
        var outside = RegionPrints.Normalize(new Lexer("static int shared = 0;", Lang.C).Run().Tokens, prints.External);
        string shared = "x " + RegionPrints.Hash(string.Join("\u0001", outside.Select(x => x.Key)));
        bool ok = prints.Count("fx-gone") == 1 && prints.Count("fx-kept") > 0 && !prints.ByRegion["fx-kept"].Contains(shared)
                  && prints.Count("fx-table") > 0 && prints.Count("fx-gate") > 0;
        if (!ok)
            Console.WriteLine($"  [FAIL] self-test prints: fx-kept {prints.Count("fx-kept")}, fx-gone {prints.Count("fx-gone")} of 1, "
                              + $"fx-table {prints.Count("fx-table")}, fx-gate {prints.Count("fx-gate")}");
        ok &= One(cat, prints, Path.Combine("sdk", "HIDMaestro.Core", "HMLayout.cs"), "HMLayout.cs", CsSource, SourceKind.CSharp,
                  s_csExpected, allowedNames: 3, allowedInf: 0, regions: "");
        ok &= One(cat, prints, Path.Combine("driver", "fixture.c"), "fixture.c", CSource, SourceKind.C,
                  s_cExpected, allowedNames: 0, allowedInf: 0, regions: "fx-kept 16");
        ok &= One(cat, prints, Path.Combine("driver", "hidmaestro_xusb.inf"), "hidmaestro_xusb.inf", InfSource, SourceKind.Inf,
                  s_infExpected, allowedNames: 0, allowedInf: 1, regions: "");
        ok &= One(cat, prints, Path.Combine("sdk", "HIDMaestro.Core", "Internal", "Fixture2.cs"), Path.Combine("Internal", "Fixture2.cs"), Cs2Source,
                  SourceKind.CSharp, s_cs2Expected, allowedNames: 0, allowedInf: 0, regions: "");
        ok &= One(cat, prints, Path.Combine("driver", "fixture2.c"), "fixture2.c", C2Source, SourceKind.C,
                  s_c2Expected, allowedNames: 0, allowedInf: 0, regions: "");
        foreach (var (name, rel, src, expected) in s_ruleD)
        {
            var scan = new Scan(cat, prints, s_regions, Rules.Names, Rules.InfLines, Rules.Vendored);
            scan.Add(rel, rel["driver\\".Length..], src, SourceKind.C);
            scan.Run();
            string got = Found(scan);
            if (got == expected) continue;
            ok = false;
            Console.WriteLine($"  [FAIL] self-test rule D, {name}: expected \"{expected}\", got \"{got}\"");
        }
        Console.WriteLine(ok ? $"  self-test: the {5 + s_ruleD.Length} fixtures give exactly their expected hits and regions" : "  [FAIL] self-test");
        return ok;
    }

    // The regions a scan found and the lines they are found from: "fx-gate 1-6".
    static string Found(Scan scan) =>
        string.Join("; ", scan.Regions.Select(r => $"{r.Region.Name} {(r.Lines.Min() == r.Lines.Max() ? $"{r.Lines.Min()}" : $"{r.Lines.Min()}-{r.Lines.Max()}")}"));

    static bool One(Catalog cat, RegionPrints prints, string rel, string below, string src, SourceKind kind, string[] expected,
                    int allowedNames, int allowedInf, string regions)
    {
        var scan = new Scan(cat, prints, s_regions, Rules.Names, Rules.InfLines, Rules.Vendored);
        scan.Add(rel, below, src, kind);
        scan.Run();
        var got = scan.Failing.Where(h => h.Group != HitGroups.Removed).Select(h => $"{h.Line} {h.Group}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        var want = expected.Where(e => !e.EndsWith(HitGroups.Removed, StringComparison.Ordinal)).OrderBy(x => x, StringComparer.Ordinal).ToList();
        bool ok = got.SequenceEqual(want) && scan.AllowedNames.Count == allowedNames && scan.AllowedInf.Count == allowedInf
                  && Found(scan) == regions;
        if (!ok)
        {
            Console.WriteLine($"  [FAIL] self-test {below}: expected {string.Join(", ", want)}, {allowedNames} allowed names, "
                              + $"{allowedInf} allowed INF lines, regions \"{regions}\"");
            foreach (var h in scan.Failing) Console.WriteLine($"    got {h.Line} {h.Group}  {h.Kind}  {h.Name}");
            Console.WriteLine($"    allowed names {scan.AllowedNames.Count}, allowed INF lines {scan.AllowedInf.Count}, regions \"{Found(scan)}\"");
        }
        return ok;
    }
}
