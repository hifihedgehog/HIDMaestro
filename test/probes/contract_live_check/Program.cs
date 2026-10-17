// contract_live_check: the version contract and FeatureAnswers on the live
// UMDF2 driver (design note, decision (c) and mechanism 4). Needs
// elevation. Creates its controllers one at a time and disposes each.
//
//   identity     The driver reads its instance ID through
//                WdfFdoInitQueryPropertyEx before WdfDeviceCreate, takes
//                its token, and reads the index the token's identity entry
//                names before the device node's first start. A generic pad
//                at index 0 first. Then a fresh-token dualsense at index 1
//                on the ROOT path and a fresh-token xbox-one-s at index 2
//                on the SWD xinputhid path. For each, the driver's half of
//                the chosen index's version section reads protocols 4 to 4,
//                which it writes only into the section of the index it
//                chose. The dualsense answers report 0x09 with its MAC's
//                last byte 1, from the FeatureAnswers of index 1, and the
//                xbox-one-s's input reaches XInput. If any of these fails,
//                the later lanes stop until the design names another
//                source for the index.
//   unnumbered   A profile without report IDs that carries the Deck's
//                message entries: HidD_SetFeature with HID class's leading
//                0 and the message 0x83, then HidD_GetFeature, which must
//                answer 0x83's attributes. This tests the leading 0 the
//                driver drops (R3-N9).
//   extract      Each UMDF2 catalog profile with a model is created, read
//                back with HMDeviceExtractor.Extract, and the extracted
//                profile must name the model its identity gives it, with
//                the answers the device gave.
//   check        the three, the default.
// Exit 0 PASS, 1 FAIL, 3 not elevated (nothing created).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using HIDMaestro;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Usbip;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    static int s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetState(uint user, out XInputState state);

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LX, LY, RX, RY;
    }

    /// <summary>The HID device the driver serves for an index: its serial
    /// is HM-CTL- and the index (driver.c InitInstancePaths).</summary>
    static HMHidDeviceInfo? Find(int index)
    {
        string serial = $"HM-CTL-{index:D4}";
        for (int i = 0; i < 50; i++)
        {
            var d = HMDeviceExtractor.ListDevices().FirstOrDefault(x => x.SerialNumberString == serial);
            if (d != null) return d;
            Thread.Sleep(100);
        }
        return null;
    }

    static SafeFileHandle? Open(HMHidDeviceInfo? d)
    {
        if (d == null) return null;
        var h = CreateFileW(d.DevicePath, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        return h.IsInvalid ? null : h;
    }

    static byte[]? Get(SafeFileHandle h, byte id, int length)
    {
        var buf = new byte[length];
        buf[0] = id;
        return HidD_GetFeature(h, buf, buf.Length) ? buf : null;
    }

    static string Key() => "contract-live-" + Guid.NewGuid().ToString("N");

    static int Main(string[] args)
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.WriteLine("contract_live_check: needs elevation, nothing created");
            return 3;
        }
        string mode = args.Length > 0 ? args[0] : "check";
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        if (mode is "identity" or "check") Identity(ctx);
        if (mode is "unnumbered" or "check") Unnumbered(ctx);
        if (mode is "extract" or "check") Extract(ctx);
        Console.WriteLine($"contract_live_check {mode}: {(s_failures == 0 ? "PASS" : $"FAIL, {s_failures}")}");
        return s_failures == 0 ? 0 : 1;
    }

    static void Identity(HMContext ctx)
    {
        var pad = new HMProfileBuilder().Id("contract-live-pad").Vid(0xBEEF).Pid(0xF0C0)
            .DescriptorHex(ctx.GetProfile("heusinkveld-ultimate-pedals")!.DescriptorHex!).Build();
        using var c0 = ctx.CreateControllerAt(0, pad, Key());
        Check("a generic pad at index 0 starts on a driver of protocols 4 to 4",
              SharedMemoryIO.DriverHalf(0) is { Protocol: 4, ProtocolMin: 4 }, $"{SharedMemoryIO.DriverHalf(0)}");

        using (var c1 = ctx.CreateControllerAt(1, ctx.GetProfile("dualsense")!, Key()))
        {
            var half = SharedMemoryIO.DriverHalf(1);
            Check("a fresh-token dualsense at index 1 on the ROOT path: its driver wrote protocols 4 to 4 into index 1's section at its first add",
                  half is { Protocol: 4, ProtocolMin: 4, ConfigError: 0 }, $"{half}");
            using var h = Open(Find(1));
            var r = h == null ? null : Get(h, 0x09, 20);
            Check("its report 0x09 carries index 1 in its MAC, from index 1's FeatureAnswers",
                  r != null && r[6] == 1, r == null ? "no answer" : Convert.ToHexString(r));
        }

        using (var c2 = ctx.CreateControllerAt(2, ctx.GetProfile("xbox-one-s")!, Key()))
        {
            var half = SharedMemoryIO.DriverHalf(2);
            Check("a fresh-token xbox-one-s at index 2 on the SWD xinputhid path: its driver wrote protocols 4 to 4 into index 2's section at its first add",
                  half is { Protocol: 4, ProtocolMin: 4, ConfigError: 0 }, $"{half}");
            var state = new HMGamepadState { Buttons = HMButton.A | HMButton.Y };
            var sw = Stopwatch.StartNew();
            bool seen = false;
            while (!seen && sw.ElapsedMilliseconds < 3000)
            {
                c2.SubmitState(state);
                for (uint s = 0; s < 4 && !seen; s++)
                    seen = XInputGetState(s, out var st) == 0 && (st.Buttons & 0x9000) == 0x9000;
                Thread.Sleep(20);
            }
            Check("its input reaches XInput, which reads index 2's section", seen, $"{sw.ElapsedMilliseconds} ms");
        }
    }

    static void Unnumbered(HMContext ctx)
    {
        var deck = ctx.GetProfile("steam-deck-composite")!;
        var p = new HMProfileBuilder().Id("contract-live-unnumbered").Vid(0x28DE).Pid(0x1205)
            .DescriptorHex(deck.DescriptorHex!).FeatureAnswers(deck.Inner.FeatureStubs).Build();
        using var c = ctx.CreateControllerAt(3, p, Key());
        using var h = Open(Find(3));
        if (h == null) { Check("the unnumbered profile's HID device opens", false); return; }
        var set = new byte[65];
        set[1] = 0x83;
        bool sent = HidD_SetFeature(h, set, set.Length);
        var get = new byte[65];
        bool got = HidD_GetFeature(h, get, get.Length);
        Check("HidD_SetFeature of message 0x83 with HID class's leading 0, then HidD_GetFeature, answers 0x83's attributes",
              sent && got && get[1] == 0x83 && get[2] == 0x2D,
              $"set {sent} ({Marshal.GetLastPInvokeError()}), get {got}: {Convert.ToHexString(get, 0, 8)}");
    }

    static void Extract(HMContext ctx)
    {
        foreach (var p in ctx.AllProfiles.Where(x => !x.Inner.RequiresUsbipBackend && x.HasDeviceModel && x.Inner.Resolved.Model.FeatureStubs != null)
                     .OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            using var c = ctx.CreateControllerAt(0, p, Key());
            var d = Find(0);
            if (d == null) { Check($"{p.Id}: its HID device is listed", false); continue; }
            var x = HMDeviceExtractor.Extract(d);
            string? named = x.Inner.DeviceModelFrom;
            var model = x.Inner.Resolved.Model.FeatureStubs;
            Check($"{p.Id}: the extracted profile names the model its identity gives it", named != null && x.Inner.Resolved.IdentityModel == named,
                  $"{named ?? "none"} (identity gives {x.Inner.Resolved.IdentityModel ?? "none"})");
            using var h = Open(d);
            var table = FeatureStubTable.From(x.Inner, 0, d.SerialNumberString);
            var differ = new List<string>();
            foreach (var r in p.Inner.GetOrBuildReportMap().Reports.Where(r => r.Kind == HidReportKind.Feature).Select(r => r.ReportId).Distinct())
            {
                var live = h == null ? null : Get(h, r, 65);
                int n = table?.Lookup(r, 65, out _) ?? -1;
                byte[]? want = n < 0 ? null : table!.Answer(n).ToArray();
                if (live == null && want == null) continue;
                if (live == null || want == null || !live.AsSpan(0, want.Length).SequenceEqual(want))
                    differ.Add($"0x{r:X2}");
            }
            Check($"{p.Id}: the extracted profile's answers are the ones the device gave", differ.Count == 0, string.Join(" ", differ));
        }
    }
}
