// S85: an exception a program's event handler throws stops nothing else.
//
// Each event the output thread raises gets a handler that throws, attached
// first, and a counting handler after it. The counting handler must still
// get every event, and the SDK must still apply every packet: a keyboard's
// LED state, OutputDecoded on the profile codec's path and on Switch Pro
// rumble, and a Bluetooth DualSense's switch to report 0x31 on the feature
// read that arms it. Before v1.13.0 a throwing handler stopped the handlers
// after it, and a throwing OutputReceived handler skipped the rest of the
// packet.
//
// The other events raised on SDK threads are tested where their stimulus
// lives: the adapter ports' OutputChanged in S80, the USB audio events in
// usbip_server_check, HapticReceived in S50, and the Switch 2 Pro's decode
// in its composite check.
//
// Requires elevation. Exit 0 PASS, 1 FAIL, 2 when another program that
// embeds HIDMaestro is running.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32.SafeHandles;

internal static class Program
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    const int Reports = 20;

    static readonly Action<HMController, HMOutputPacket> ThrowOnOutput =
        (_, _) => throw new InvalidOperationException("S85: a throwing OutputReceived handler");
    static readonly EventHandler<HMOutputDecodedEventArgs> ThrowOnDecoded =
        (_, _) => throw new InvalidOperationException("S85: a throwing OutputDecoded handler");

    static int Main()
    {
        Console.WriteLine("=== S85: a throwing event handler stops nothing else ===");
        if (Probe.OtherSdkRunning()) return 2;

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        OutputAndDecoded(ctx);
        KeyboardLeds(ctx);
        ArmOnFeatureRead(ctx);
        SwitchProRumble(ctx);
        return Probe.Finish("S85");
    }

    // ── OutputReceived and the codec's OutputDecoded ────────────────────

    static void OutputAndDecoded(HMContext ctx)
    {
        Console.WriteLine("-- dualshock-4-v2: OutputReceived and OutputDecoded");
        var profile = ctx.GetProfile("dualshock-4-v2")!;
        var before = HidPaths(profile.VendorId, profile.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var c = ctx.CreateController(profile);
        int received = 0, decoded = 0;
        c.OutputReceived += ThrowOnOutput;
        c.OutputReceived += (_, p) => { if (p.ReportId == 0x05) Interlocked.Increment(ref received); };
        c.OutputDecoded += ThrowOnDecoded;
        c.OutputDecoded += (_, e) => { if (e.ReportId == 0x05) Interlocked.Increment(ref decoded); };

        using var h = OpenNew(profile, before);
        if (h == null) return;
        // Report 0x05, the USB DualShock 4's 32-byte output report.
        var report = new byte[32];
        report[0] = 0x05;
        int sent = 0;
        for (int i = 0; i < Reports; i++)
        {
            report[4] = (byte)(i + 1);
            if (HidD_SetOutputReport(h, report, report.Length)) sent++;
            Thread.Sleep(2);
        }
        Probe.WaitFor(() => Volatile.Read(ref received) >= sent && Volatile.Read(ref decoded) >= sent, 3000);
        Probe.Check($"the handler after a throwing OutputReceived handler gets all {Reports} output reports",
            sent == Reports && Volatile.Read(ref received) == sent, $"{received} of {sent} sent");
        Probe.Check($"OutputDecoded still fires for all {Reports}, and the handler after a throwing one gets them",
            sent == Reports && Volatile.Read(ref decoded) == sent, $"{decoded} of {sent} sent");
    }

    // ── KeyboardLedsChanged ─────────────────────────────────────────────

    static void KeyboardLeds(HMContext ctx)
    {
        Console.WriteLine("-- microsoft-wired-keyboard-600: the LED state");
        var profile = ctx.GetProfile("microsoft-wired-keyboard-600")!;
        using var k = ctx.CreateController(profile);
        // Windows sends a keyboard its LED state when it arrives, before the
        // handlers below are attached.
        bool arrived = Probe.WaitFor(() => k.KeyboardLeds != null, 15_000);
        Probe.Check("Windows sends the new keyboard its LED state", arrived);
        if (!arrived) return;

        var seen = new ConcurrentQueue<HMKeyboardLeds>();
        k.OutputReceived += ThrowOnOutput;
        k.KeyboardLedsChanged += (_, _) => throw new InvalidOperationException("S85: a throwing KeyboardLedsChanged handler");
        k.KeyboardLedsChanged += (_, l) => seen.Enqueue(l);

        bool initial = ScrollLockOn();
        PressScrollLock(k);
        bool toggled = Probe.WaitFor(() => seen.Any(l => l.HasFlag(HMKeyboardLeds.ScrollLock) != initial), 3000);
        bool stateToggled = k.KeyboardLeds?.HasFlag(HMKeyboardLeds.ScrollLock) == !initial;
        PressScrollLock(k);
        bool restored = Probe.WaitFor(() => seen.Count >= 2 && seen.Last().HasFlag(HMKeyboardLeds.ScrollLock) == initial, 3000);
        // Leave Scroll Lock as it was found, whatever the handlers saw.
        if (!Probe.WaitFor(() => ScrollLockOn() == initial, 1000)) PressScrollLock(k);
        Probe.WaitFor(() => ScrollLockOn() == initial, 1000);

        Probe.Check("behind a throwing OutputReceived handler and a throwing KeyboardLedsChanged handler, " +
                    "the handler after them sees Scroll Lock go on and off, and KeyboardLeds follows",
            toggled && stateToggled && restored && k.KeyboardLeds?.HasFlag(HMKeyboardLeds.ScrollLock) == initial,
            $"seen [{string.Join(", ", seen)}], KeyboardLeds {k.KeyboardLeds?.ToString() ?? "null"}");
        Probe.Check("Scroll Lock is left as it was found", ScrollLockOn() == initial);
    }

    static void PressScrollLock(HMController k)
    {
        k.SubmitKeyboard(HMKeyModifiers.None, new[] { HMKey.ScrollLock });
        k.SubmitKeyboard(HMKeyModifiers.None, ReadOnlySpan<HMKey>.Empty);
    }

    // ── The packet applied after a throwing OutputReceived handler ──────

    static void ArmOnFeatureRead(HMContext ctx)
    {
        Console.WriteLine("-- dualsense-bt: the feature read that switches it to report 0x31");
        var profile = ctx.GetProfile("dualsense-bt")!;
        var before = HidPaths(profile.VendorId, profile.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var c = ctx.CreateController(profile);
        var armedField = typeof(HMController).GetField("_extendedModeArmed", Any)!;
        bool Armed() => (bool)armedField.GetValue(c)!;
        int reads = 0;
        c.OutputReceived += ThrowOnOutput;
        c.OutputReceived += (_, p) => { if (p.Source == HMOutputSource.HidFeatureRead && p.ReportId == 0x05) Interlocked.Increment(ref reads); };

        using var h = OpenNew(profile, before);
        if (h == null) return;
        Probe.Check("the DualSense starts on report 0x01", !Armed());
        int length = Caps(h).FeatureReportByteLength;
        var buffer = new byte[Math.Max(length, 2)];
        buffer[0] = 0x05;
        bool read = HidD_GetFeature(h, buffer, buffer.Length);
        bool armed = Probe.WaitFor(Armed, 2000);
        Probe.Check("behind a throwing OutputReceived handler, a host's read of feature report 0x05 " +
                    "still switches it to report 0x31, and the handler after it sees the read",
            armed && Volatile.Read(ref reads) == 1,
            $"HidD_GetFeature {(read ? "succeeded" : "failed")}, {reads} read seen, armed {armed}");
    }

    // ── Switch Pro rumble's OutputDecoded ───────────────────────────────

    static void SwitchProRumble(HMContext ctx)
    {
        Console.WriteLine("-- switch-pro: OutputDecoded on rumble report 0x10");
        var profile = ctx.GetProfile("switch-pro")!;
        var before = HidPaths(profile.VendorId, profile.ProductId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        using var c = ctx.CreateController(profile);
        int decoded = 0;
        c.OutputReceived += ThrowOnOutput;
        c.OutputDecoded += ThrowOnDecoded;
        c.OutputDecoded += (_, e) => { if (e.ReportId == 0x10) Interlocked.Increment(ref decoded); };

        using var h = OpenNew(profile, before);
        if (h == null) return;
        int length = Caps(h).OutputReportByteLength;
        var report = new byte[length];
        report[0] = 0x10;
        // One HD rumble block per side after the packet counter: a 160 Hz
        // tone at a low amplitude, the bytes SDL sends for a light rumble.
        byte[] block = { 0x00, 0x01, 0x40, 0x40 };
        block.CopyTo(report, 2);
        block.CopyTo(report, 6);
        int sent = 0;
        for (int i = 0; i < Reports && length >= 10; i++)
        {
            report[1] = (byte)(i & 0x0F);
            if (HidD_SetOutputReport(h, report, report.Length)) sent++;
            Thread.Sleep(2);
        }
        Probe.WaitFor(() => Volatile.Read(ref decoded) >= sent, 3000);
        Probe.Check($"Switch Pro rumble: behind throwing handlers, OutputDecoded's next handler gets all {Reports}",
            sent == Reports && Volatile.Read(ref decoded) == sent, $"{decoded} of {sent} sent, report length {length}");
    }

    // ── HID helpers ─────────────────────────────────────────────────────

    /// <summary>The new HID device's handle, opened for reading and writing,
    /// or null after a failed check.</summary>
    static SafeFileHandle? OpenNew(HMProfile profile, HashSet<string> before)
    {
        string? path = null;
        Probe.WaitFor(() => (path = HidPaths(profile.VendorId, profile.ProductId).FirstOrDefault(p => !before.Contains(p))) != null, 10_000);
        Probe.Check($"{profile.Id}'s HID device appears", path != null);
        if (path == null) return null;
        var h = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        Probe.Check($"{profile.Id}'s HID device opens", !h.IsInvalid, h.IsInvalid ? $"error {Marshal.GetLastWin32Error()}" : "");
        if (!h.IsInvalid) return h;
        h.Dispose();
        return null;
    }

    static HIDP_CAPS Caps(SafeFileHandle h)
    {
        if (!HidD_GetPreparsedData(h, out IntPtr pp)) return default;
        try { HidP_GetCaps(pp, out var caps); return caps; }
        finally { HidD_FreePreparsedData(pp); }
    }

    /// <summary>The HID device paths of one VID and PID, through the SDK's
    /// own enumerator.</summary>
    static IEnumerable<string> HidPaths(ushort vid, ushort pid)
    {
        var t = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.HidDeviceEnumerator")!;
        var list = (System.Collections.IEnumerable)t.GetMethod("Enumerate", Any)!.Invoke(null, null)!;
        foreach (var d in list)
        {
            var dt = d.GetType();
            if (Convert.ToUInt16(dt.GetField("VendorId")!.GetValue(d)) != vid) continue;
            if (Convert.ToUInt16(dt.GetField("ProductId")!.GetValue(d)) != pid) continue;
            if (dt.GetField("DevicePath")!.GetValue(d) is string path) yield return path;
        }
    }

    const int VK_SCROLL = 0x91;
    static bool ScrollLockOn() => (GetKeyState(VK_SCROLL) & 1) != 0;

    [DllImport("user32.dll")] static extern short GetKeyState(int vk);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    // hidsdi.h: the HidD_ functions return BOOLEAN, one byte.
    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr preparsed);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidD_FreePreparsedData(IntPtr preparsed);

    [DllImport("hid.dll")]
    static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }
}
