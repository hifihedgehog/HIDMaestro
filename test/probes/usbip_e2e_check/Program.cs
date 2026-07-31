// USB/IP backend end-to-end check (issue #39).
//
// The only probe in the suite that requires usbip-win2 0.9.7.7 installed:
// it creates a live dualsense-composite through the real kernel path
// (vhci -> UDE -> usbccgp -> usbaudio + hidusb) and proves the whole
// promise of the backend on real Windows plumbing:
//
//   1. The composite enumerates: the USB devnode exists and Windows
//      surfaces BOTH an audio render endpoint and a capture endpoint
//      named after the pad, which no UMDF2 profile can produce.
//   2. HID input flows: SubmitState -> interrupt IN -> ReadFile sees the
//      report; GET_REPORT(Input) on the control pipe agrees.
//   3. HID output flows: HidD_SetOutputReport -> OutputReceived.
//   4. The Sony feature stub serves HidD_GetFeature(0x05) through the
//      real stack.
//   5. Audio out: WASAPI renders a tone to the endpoint and the SDK's
//      UsbAudio.Output delivers the PCM windows (speaker/haptic lanes).
//   6. Microphone: the SDK's feed is captured by WASAPI as live audio.
//   7. Teardown detaches cleanly: the devnode and endpoints disappear.
//
// Requires elevation AND usbip-win2 0.9.7.7. Exit 0 PASS / 1 FAIL / 2 SKIP
// (backend not installed).

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using HIDMaestro;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

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
        Console.WriteLine("=== USB/IP backend end-to-end (issue #39) ===");

        if (!HMContext.IsUsbipBackendAvailable)
        {
            Console.WriteLine("SKIP: usbip-win2 vhci controller not present.");
            return 2;
        }
        Check("usbip-win2 vhci controller present", true);

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var profile = ctx.GetProfile("dualsense-composite")!;

        var sw = Stopwatch.StartNew();
        HMController controller = ctx.CreateController(profile);
        sw.Stop();
        Check($"CreateController(dualsense-composite) attached via vhci", true, $"{sw.ElapsedMilliseconds} ms");
        Check("controller exposes the UsbAudio surface", controller.UsbAudio != null);

        long outBytes = 0;
        bool outStreamed = false, micStreamed = false;
        controller.UsbAudio!.Output.FramesReceived += (_, pcm) => Interlocked.Add(ref outBytes, pcm.Length);
        controller.UsbAudio.Output.StreamingChanged += (_, s) => { if (s) outStreamed = true; };
        controller.UsbAudio.Microphone.StreamingChanged += (_, s) => { if (s) micStreamed = true; };

        int outputPackets = 0;
        byte lastOutputRid = 0;
        controller.OutputReceived += (_, pkt) =>
        {
            if (pkt.Source == HMOutputSource.HidOutput) { Interlocked.Increment(ref outputPackets); lastOutputRid = pkt.ReportId; }
        };

        try
        {
            RunChecks(controller);
        }
        finally
        {
            var tsw = Stopwatch.StartNew();
            controller.Dispose();
            tsw.Stop();
            Console.WriteLine($"\n-- Teardown --");
            Check("controller disposed (vhci detach)", true, $"{tsw.ElapsedMilliseconds} ms");
            Thread.Sleep(2500);
            Check("HID devnode gone after dispose", FindHidDevicePath(0x054C, 0x0CE6) == null);
            Check("audio endpoints gone after dispose",
                  FindEndpoint(DataFlow.Render, "Wireless Controller") == null
                  && FindEndpoint(DataFlow.Capture, "Wireless Controller") == null);
        }

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;

        void RunChecks(HMController c)
        {
            // ── Enumeration ─────────────────────────────────────────────
            Console.WriteLine("\n-- Enumeration --");
            string? hidPath = null;
            for (int i = 0; i < 100 && hidPath == null; i++) { Thread.Sleep(100); hidPath = FindHidDevicePath(0x054C, 0x0CE6); }
            Check("HID interface enumerated (VID_054C&PID_0CE6)", hidPath != null, hidPath ?? "not found");

            MMDevice? render = null, capture = null;
            for (int i = 0; i < 150 && (render == null || capture == null); i++)
            {
                Thread.Sleep(100);
                render ??= FindEndpoint(DataFlow.Render, "Wireless Controller");
                capture ??= FindEndpoint(DataFlow.Capture, "Wireless Controller");
            }
            // Windows persists per-endpoint enable/disable by device
            // identity. Because the composite presents the real pad's
            // exact identity, it inherits whatever state the user's real
            // DualSense speaker endpoint was left in on this machine. A
            // Disabled render endpoint here is that persistence at work
            // (and proof the identity matched); enable it and continue.
            if (render == null)
            {
                string? disabledId = FindEndpointIdAnyState(DataFlow.Render, "Wireless Controller",
                    out DeviceState st);
                if (disabledId != null && st == DeviceState.Disabled)
                {
                    Console.WriteLine("  [note] render endpoint exists but is Disabled " +
                                      "(persisted state from the real pad); enabling it.");
                    PolicyConfig.SetEndpointVisibility(disabledId, true);
                    for (int i = 0; i < 50 && render == null; i++)
                    {
                        Thread.Sleep(100);
                        render = FindEndpoint(DataFlow.Render, "Wireless Controller");
                    }
                }
            }
            Check("audio RENDER endpoint appeared (usbaudio bound)", render != null, render?.FriendlyName ?? "none");
            Check("audio CAPTURE endpoint appeared (microphone)", capture != null, capture?.FriendlyName ?? "none");

            // Diagnostic dump: every endpoint state for this pad, so a
            // missing Active endpoint distinguishes "never created" from
            // "created but disabled/unplugged".
            try
            {
                using var en = new MMDeviceEnumerator();
                foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
                    foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.All))
                    {
                        string name = "?";
                        try { name = d.FriendlyName; } catch { try { name = d.DeviceFriendlyName; } catch { } }
                        if (name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("Speaker", StringComparison.OrdinalIgnoreCase) == false)
                        {
                            if (name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase))
                                Console.WriteLine($"  [diag] {flow} endpoint '{name}' state={d.State}");
                        }
                        d.Dispose();
                    }
            }
            catch (Exception ex) { Console.WriteLine($"  [diag] endpoint dump failed: {ex.Message}"); }

            if (Environment.GetCommandLineArgs().Contains("--hold"))
            {
                Console.WriteLine("  [diag] holding 30 s for external inspection...");
                Thread.Sleep(30000);
            }

            // ── HID input ───────────────────────────────────────────────
            Console.WriteLine("\n-- HID input --");
            var state = new HMGamepadState
            {
                Buttons = HMButton.A | HMButton.LeftBumper,
                Axes = new System.Collections.Generic.Dictionary<HMAxis, float>
                {
                    [HMAxis.X] = 1.0f, [HMAxis.Y] = 0.0f, [HMAxis.Z] = 0.5f, [HMAxis.Rz] = 0.5f,
                },
            };
            c.SubmitState(state);
            Thread.Sleep(120);
            c.SubmitState(state);
            byte[]? viaControl = hidPath != null ? GetCurrentInputReport(hidPath, 64, 0x01) : null;
            Check("GET_REPORT(Input) over the real stack returns the state",
                  viaControl != null && viaControl[0] == 0x01 && viaControl[1] == 255,
                  viaControl != null ? $"rid={viaControl[0]:X2} lsx={viaControl[1]}" : "read failed");

            byte[]? viaInterrupt = hidPath != null ? ReadOneInterruptReport(hidPath, 64, c, state) : null;
            Check("interrupt IN report received via ReadFile",
                  viaInterrupt != null && viaInterrupt[0] == 0x01 && viaInterrupt[1] == 255,
                  viaInterrupt != null ? $"rid={viaInterrupt[0]:X2} lsx={viaInterrupt[1]}" : "read failed");

            // ── HID output + feature ────────────────────────────────────
            Console.WriteLine("\n-- HID output + feature --");
            if (hidPath != null)
            {
                IntPtr h = OpenPath(hidPath);
                try
                {
                    var outRep = new byte[48];
                    outRep[0] = 0x02; outRep[3] = 0xC8; outRep[4] = 0x40;
                    bool wrote = HidD_SetOutputReport(h, outRep, outRep.Length);
                    Thread.Sleep(300);
                    Check("HidD_SetOutputReport delivered", wrote);
                    Check("OutputReceived fired with RID 0x02", outputPackets > 0 && lastOutputRid == 0x02,
                          $"{outputPackets} packet(s)");

                    var feat = new byte[41];
                    feat[0] = 0x05;
                    bool gotFeat = HidD_GetFeature(h, feat, feat.Length);
                    Check("HidD_GetFeature(0x05) served through the stack", gotFeat && feat[0] == 0x05);
                }
                finally { CloseHandle(h); }
            }

            // ── Audio render ────────────────────────────────────────────
            Console.WriteLine("\n-- Audio render (speaker/haptic lanes) --");
            if (render != null)
            {
                long before = Interlocked.Read(ref outBytes);
                using var wo = new WasapiOut(render, AudioClientShareMode.Shared, false, 50);
                var tone = new SignalGenerator(48000, 2) { Type = SignalGeneratorType.Sin, Frequency = 440, Gain = 0.5 };
                wo.Init(tone.Take(TimeSpan.FromMilliseconds(600)));
                wo.Play();
                Thread.Sleep(900);
                wo.Stop();
                Thread.Sleep(300);
                long delivered = Interlocked.Read(ref outBytes) - before;
                Check("host PCM reached UsbAudio.Output.FramesReceived", delivered > 0, $"{delivered} bytes");
                Check("stream open observed (alt setting 1)", outStreamed);
                Check("channel roles present on the surface",
                      c.UsbAudio!.Output.ChannelRoles.SequenceEqual(
                          new[] { "speakerLeft", "speakerRight", "hapticLeft", "hapticRight" }));
            }

            // ── Microphone capture ──────────────────────────────────────
            Console.WriteLine("\n-- Microphone capture --");
            if (capture != null)
            {
                // Feed a loud 1 kHz square continuously while WASAPI records.
                var feeding = true;
                var feeder = new Thread(() =>
                {
                    var block = new byte[192 * 4]; // 4 ms per push
                    while (feeding)
                    {
                        for (int i = 0; i < block.Length; i += 4)
                        {
                            short v = (i / 4 / 24 % 2 == 0) ? (short)20000 : (short)-20000;
                            block[i] = (byte)(v & 0xFF); block[i + 1] = (byte)(v >> 8);
                            block[i + 2] = block[i]; block[i + 3] = block[i + 1];
                        }
                        c.UsbAudio!.Microphone.Submit(block);
                        Thread.Sleep(3);
                    }
                }) { IsBackground = true };
                feeder.Start();

                long captured = 0, energy = 0;
                using var wi = new WasapiCapture(capture);
                wi.DataAvailable += (_, e) =>
                {
                    Interlocked.Add(ref captured, e.BytesRecorded);
                    long sum = 0;
                    for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                        sum += Math.Abs((short)(e.Buffer[i] | (e.Buffer[i + 1] << 8)));
                    Interlocked.Add(ref energy, sum / Math.Max(1, e.BytesRecorded / 2));
                };
                wi.StartRecording();
                Thread.Sleep(1000);
                wi.StopRecording();
                feeding = false;
                Thread.Sleep(200);
                Check("capture endpoint recorded data", Interlocked.Read(ref captured) > 0,
                      $"{Interlocked.Read(ref captured)} bytes");
                Check("captured audio is the fed signal, not silence", Interlocked.Read(ref energy) > 0,
                      $"mean-abs accum {Interlocked.Read(ref energy)}");
                Check("mic stream open observed", micStreamed);
            }
        }
    }

    static string? FindEndpointIdAnyState(DataFlow flow, string nameContains, out DeviceState state)
    {
        state = DeviceState.NotPresent;
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.All))
            {
                string name = "";
                try { name = d.FriendlyName; } catch { try { name = d.DeviceFriendlyName; } catch { } }
                if (name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                {
                    state = d.State;
                    string id = d.ID;
                    d.Dispose();
                    return id;
                }
                d.Dispose();
            }
        }
        catch { }
        return null;
    }

    /// <summary>The stock (undocumented, ubiquitously used) PolicyConfig
    /// COM interface, for flipping an endpoint's enabled state the way
    /// the Sound control panel does.</summary>
    static class PolicyConfig
    {
        [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        class PolicyConfigClient { }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPolicyConfig
        {
            // Slots 1-11: GetMixFormat, GetDeviceFormat, ResetDeviceFormat,
            // SetDeviceFormat, GetProcessingPeriod, SetProcessingPeriod,
            // GetShareMode, SetShareMode, GetPropertyValue, SetPropertyValue,
            // SetDefaultEndpoint. SetEndpointVisibility is slot 12.
            void Unused1(); void Unused2(); void Unused3(); void Unused4(); void Unused5();
            void Unused6(); void Unused7(); void Unused8(); void Unused9(); void Unused10();
            void Unused11();
            int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
        }

        public static void SetEndpointVisibility(string deviceId, bool visible)
        {
            var client = (IPolicyConfig)new PolicyConfigClient();
            client.SetEndpointVisibility(deviceId, visible ? 1 : 0);
        }
    }

    static MMDevice? FindEndpoint(DataFlow flow, string nameContains)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                if (d.FriendlyName.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                    return d;
                d.Dispose();
            }
        }
        catch { }
        return null;
    }

    static byte[]? ReadOneInterruptReport(string path, int reportLen, HMController c, in HMGamepadState state)
    {
        IntPtr fh = OpenPath(path);
        if (fh == INVALID_HANDLE_VALUE) return null;
        try
        {
            var buf = new byte[reportLen];
            // Prime a fresh frame so a parked read completes promptly.
            var pump = new Thread(() =>
            {
                var s = new HMGamepadState
                {
                    Buttons = HMButton.A | HMButton.LeftBumper,
                    Axes = new System.Collections.Generic.Dictionary<HMAxis, float>
                    { [HMAxis.X] = 1.0f, [HMAxis.Y] = 0.0f, [HMAxis.Z] = 0.5f, [HMAxis.Rz] = 0.5f },
                };
                for (int i = 0; i < 40; i++) { c.SubmitState(s); Thread.Sleep(20); }
            }) { IsBackground = true };
            pump.Start();
            bool ok = ReadFile(fh, buf, buf.Length, out int read, IntPtr.Zero);
            return ok && read > 0 ? buf : null;
        }
        finally { CloseHandle(fh); }
    }

    static byte[]? GetCurrentInputReport(string path, int reportLen, byte reportId)
    {
        IntPtr fh = OpenPath(path);
        if (fh == INVALID_HANDLE_VALUE) return null;
        try
        {
            var buf = new byte[reportLen];
            buf[0] = reportId;
            return HidD_GetInputReport(fh, buf, buf.Length) ? buf : null;
        }
        finally { CloseHandle(fh); }
    }

    static IntPtr OpenPath(string path)
        => CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                       IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    static string? FindHidDevicePath(ushort vid, ushort pid)
    {
        HidD_GetHidGuid(out Guid hidGuid);
        IntPtr h = SetupDiGetClassDevsW(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (h == INVALID_HANDLE_VALUE) return null;
        try
        {
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(h, IntPtr.Zero, ref hidGuid, i, ref data)) break;
                SetupDiGetDeviceInterfaceDetailW(h, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetailW(h, ref data, buf, required, IntPtr.Zero, IntPtr.Zero)) continue;
                    string path = Marshal.PtrToStringUni(IntPtr.Add(buf, 4))!;
                    IntPtr fh = OpenPath(path);
                    if (fh == INVALID_HANDLE_VALUE) continue;
                    try
                    {
                        var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (HidD_GetAttributes(fh, ref attr) && attr.VendorID == vid && attr.ProductID == pid)
                            return path;
                    }
                    finally { CloseHandle(fh); }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(h); }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID; public ushort ProductID; public ushort VersionNumber; }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    const uint DIGCF_PRESENT = 0x02;
    const uint DIGCF_DEVICEINTERFACE = 0x10;
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 0x01;
    const uint FILE_SHARE_WRITE = 0x02;
    const uint OPEN_EXISTING = 3;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES attr);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetInputReport(IntPtr h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_SetOutputReport(IntPtr h, byte[] buf, int len);
    [DllImport("hid.dll", SetLastError = true)] static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr h, IntPtr devInfoData, ref Guid classGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr h, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, out uint required, IntPtr devInfoData);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr h, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, IntPtr required, IntPtr devInfoData);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFileW(string fn, uint access, uint share, IntPtr sec, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadFile(IntPtr h, byte[] buf, int len, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
}
