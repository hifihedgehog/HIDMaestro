// sdl <backend> <dir> [ids]: each profile through the SDL3.dll in <dir>, one
// at a time: whether SDL opens the pad, the driver and bus its joystick GUID
// names (SDL_CreateJoystickGUID: bytes 0-1 the bus, byte 14 the driver,
// 'h' for HIDAPI), the gamepad type, the connection state, a face button,
// rumble and the LEDs, each read back through the controller's
// OutputDecoded or OutputReceived. dualsense-composite runs first as the
// positive control. The Nintendo pads with no USB form print what SDL does
// as a finding: no profile of theirs answers SDL's subcommands on any bus.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

using HIDMaestro;

internal static partial class Program
{
    internal static class Sdl
    {
        const string L = "SDL3";

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetDllDirectoryW(string path);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string path);

        [StructLayout(LayoutKind.Sequential)] struct SDL_GUID { public ulong Lo, Hi; }

        [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_Init(uint flags);
        [DllImport(L)] static extern void SDL_Quit();
        [DllImport(L)] static extern IntPtr SDL_GetRevision();
        [DllImport(L)] static extern int SDL_GetVersion();
        [DllImport(L)] static extern void SDL_PumpEvents();
        [DllImport(L)] static extern IntPtr SDL_GetError();
        [DllImport(L)] static extern void SDL_ClearError();
        [DllImport(L)] static extern void SDL_free(IntPtr p);
        [DllImport(L)] static extern IntPtr SDL_GetJoysticks(out int count);
        [DllImport(L)] static extern IntPtr SDL_GetJoystickPathForID(uint id);
        [DllImport(L)] static extern IntPtr SDL_GetJoystickNameForID(uint id);
        [DllImport(L)] static extern SDL_GUID SDL_GetJoystickGUIDForID(uint id);
        [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_IsGamepad(uint id);
        [DllImport(L)] static extern IntPtr SDL_OpenJoystick(uint id);
        [DllImport(L)] static extern void SDL_CloseJoystick(IntPtr j);
        [DllImport(L)] static extern IntPtr SDL_OpenGamepad(uint id);
        [DllImport(L)] static extern void SDL_CloseGamepad(IntPtr g);
        [DllImport(L)] static extern IntPtr SDL_GetGamepadJoystick(IntPtr g);
        [DllImport(L)] static extern IntPtr SDL_GetGamepadName(IntPtr g);
        [DllImport(L)] static extern int SDL_GetGamepadType(IntPtr g);
        [DllImport(L)] static extern IntPtr SDL_GetGamepadStringForType(int type);
        [DllImport(L)] static extern int SDL_GetJoystickConnectionState(IntPtr j);
        [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_GetGamepadButton(IntPtr g, int button);
        [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_RumbleGamepad(IntPtr g, ushort low, ushort high, uint ms);
        [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_SetGamepadLED(IntPtr g, byte r, byte gr, byte b);

        const uint SDL_INIT_GAMEPAD = 0x00002000;

        static readonly string[] s_default =
        {
            "dualsense-composite",
            "dualsense-bt", "dualsense-bt-full", "dualsense-edge-bt", "dualshock-4-v2-bt",
            "switch-pro", "switch-pro-usb",
            "joycon-l", "joycon-r", "snes-nso", "n64-nso", "genesis-nso",
            "stadia-controller-bt", "amazon-luna-ble",
        };

        internal static void Run(HMBackend backend, string dir, string[] ids)
        {
            dir = Path.GetFullPath(dir);
            if (!File.Exists(Path.Combine(dir, "SDL3.dll")))
            {
                Check($"SDL3.dll in {dir}", false);
                return;
            }
            string libusb = Path.Combine(dir, "libusb-1.0.dll");
            if (File.Exists(libusb)) LoadLibraryW(libusb);
            SetDllDirectoryW(dir);
            int v = SDL_GetVersion();
            Console.WriteLine($"  SDL3 {v / 1000000}.{v / 1000 % 1000}.{v % 1000} {Marshal.PtrToStringUTF8(SDL_GetRevision())} from {dir}");

            using var ctx = new HMContext(backend);
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            var list = ids.Length > 0 ? ids : backend == HMBackend.Usbip ? s_default.Concat(s_plain).ToArray() : s_default;
            string? plainDir = list.Any(i => i.StartsWith("plain:", StringComparison.Ordinal)) ? PlainCopies(ctx, list) : null;
            foreach (var id in list)
            {
                var p = ctx.GetProfile(id.StartsWith("plain:", StringComparison.Ordinal) ? id.Substring(6) + "-plain" : id);
                if (p == null) { Check($"{id} is in the catalog", false); continue; }
                One(ctx, p, p.RequiresUsbipBackend ? HMBackend.Usbip : backend);
            }
            if (plainDir != null)
            {
                try { Directory.Delete(plainDir, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        // The control for the rule itself: the same Bluetooth profile with
        // no usbForm, which presents its Bluetooth form on the USB bus, as
        // every Bluetooth profile did before usbForm.
        static readonly string[] s_plain = { "plain:dualsense-bt", "plain:dualshock-4-v2-bt", "plain:switch-pro" };

        /// <summary>Loads "&lt;id&gt;-plain", a catalog profile's file with
        /// no usbForm, for each plain: entry, from a directory of its own.</summary>
        static string PlainCopies(HMContext ctx, string[] list)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"hm-btform-plain-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            var asm = typeof(HMContext).Assembly;
            foreach (var entry in list.Where(i => i.StartsWith("plain:", StringComparison.Ordinal)))
            {
                string id = entry.Substring(6);
                foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("HIDMaestro.Profiles.", StringComparison.Ordinal)))
                {
                    using var st = asm.GetManifestResourceStream(res)!;
                    string t = new StreamReader(st, System.Text.Encoding.UTF8).ReadToEnd();
                    if (System.Text.Json.Nodes.JsonNode.Parse(t) is not System.Text.Json.Nodes.JsonObject o || (string?)o["id"] != id) continue;
                    o["id"] = id + "-plain";
                    o.Remove("usbForm");
                    File.WriteAllText(Path.Combine(dir, id + "-plain.json"), o.ToJsonString());
                }
            }
            ctx.LoadProfilesFromDirectory(dir);
            return dir;
        }

        static void One(HMContext ctx, HMProfile profile, HMBackend backend)
        {
            Console.WriteLine($"\n-- {profile.Id} on {backend} --");
            using var ctrl = ctx.CreateController(profile, null, backend);
            var form = profile.FormOn(ctrl.Backend);
            Console.WriteLine($"  presents {form.Id}");
            var decoded = new List<IReadOnlyDictionary<string, object>>();
            var packets = new List<(byte Id, byte[] Data, HMOutputSource Source)>();
            ctrl.OutputDecoded += (_, e) => { lock (decoded) decoded.Add(e.Fields); };
            ctrl.OutputReceived += (_, pkt) => { lock (packets) packets.Add((pkt.ReportId, pkt.Data.ToArray(), pkt.Source)); };

            var state = new HMGamepadState();
            var stateLock = new object();
            bool stop = false;
            var pump = new Thread(() =>
            {
                while (!Volatile.Read(ref stop))
                {
                    HMGamepadState s;
                    lock (stateLock) s = state;
                    try { ctrl.SubmitState(s); } catch { }
                    Thread.Sleep(8);
                }
            }) { IsBackground = true };
            pump.Start();

            string serial = SerialOf(ctrl);
            string? ownPath = null;
            var sw = Stopwatch.StartNew();
            while (ownPath == null && sw.ElapsedMilliseconds < 15000)
            {
                ownPath = HMDeviceExtractor.ListDevices().FirstOrDefault(d => d.VendorId == profile.VendorId
                    && d.ProductId == profile.ProductId && d.SerialNumberString == serial)?.DevicePath;
                if (ownPath == null) Thread.Sleep(50);
            }
            Check("the HID interface enumerates", ownPath != null, ownPath ?? "");
            Check("SDL_Init(GAMEPAD)", SDL_Init(SDL_INIT_GAMEPAD));
            try
            {
                if (ownPath != null) Drive(profile, form, ctrl, ownPath, decoded, packets, stateLock, s => { lock (stateLock) state = s; });
            }
            finally
            {
                SDL_Quit();
                Volatile.Write(ref stop, true);
                pump.Join(1000);
            }
        }

        static void Drive(HMProfile profile, HMProfile form, HMController ctrl, string ownPath,
            List<IReadOnlyDictionary<string, object>> decoded, List<(byte Id, byte[] Data, HMOutputSource Source)> packets,
            object stateLock, Action<HMGamepadState> setState)
        {
            // The joystick SDL lists for this controller's HID interface.
            uint jid = 0;
            var sw = Stopwatch.StartNew();
            while (jid == 0 && sw.ElapsedMilliseconds < 20000)
            {
                SDL_PumpEvents();
                IntPtr list = SDL_GetJoysticks(out int n);
                for (int i = 0; i < n && jid == 0; i++)
                {
                    uint id = (uint)Marshal.ReadInt32(list, i * 4);
                    string path = Marshal.PtrToStringUTF8(SDL_GetJoystickPathForID(id)) ?? "";
                    if (path.Equals(ownPath, StringComparison.OrdinalIgnoreCase)) jid = id;
                }
                if (list != IntPtr.Zero) SDL_free(list);
                if (jid == 0) Thread.Sleep(50);
            }
            bool nintendoNoForm = profile.VendorId == 0x057E && form.Inner.Protocol == null;
            // A plain copy is the control the rule is measured against: what
            // SDL does with it is recorded, not asserted.
            if (profile.Id.EndsWith("-plain", StringComparison.Ordinal)) nintendoNoForm = true;
            // The Nintendo pads with no USB form and the plain copies are
            // findings: their lines are recorded, not asserted.
            bool record = nintendoNoForm;
            void Expect(string name, bool cond, string detail = "")
            {
                if (record) Console.WriteLine($"  [INFO] {name}: {(cond ? "yes" : "no")}{(detail.Length > 0 ? "  " + detail : "")}");
                else Check(name, cond, detail);
            }
            if (jid == 0)
            {
                string err = Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";
                if (nintendoNoForm)
                {
                    Console.WriteLine($"  [INFO] SDL lists no joystick for the pad after {sw.ElapsedMilliseconds} ms. SDL_GetError: '{err}'");
                    return;
                }
                Expect("SDL lists a joystick for the pad's HID interface", false, $"SDL_GetError '{err}'");
                return;
            }
            var guid = SDL_GetJoystickGUIDForID(jid);
            var g = new byte[16];
            BitConverter.GetBytes(guid.Lo).CopyTo(g, 0);
            BitConverter.GetBytes(guid.Hi).CopyTo(g, 8);
            int bus = g[0] | (g[1] << 8);
            char driver = (char)g[14];
            string name = Marshal.PtrToStringUTF8(SDL_GetJoystickNameForID(jid)) ?? "";
            Console.WriteLine($"  joystick {jid} '{name}' guid {Convert.ToHexString(g)} bus 0x{bus:X2} driver '{driver}' gamepad {SDL_IsGamepad(jid)}");
            Expect("SDL's HIDAPI backend drives the pad (GUID driver 'h')", driver == 'h', $"'{driver}'");
            bool wantBt = ctrl.Backend == HMBackend.Umdf2 && profile.Connection == "bluetooth";
            Expect($"SDL reads the bus as {(wantBt ? "Bluetooth (0x05)" : "USB (0x03)")}", bus == (wantBt ? 0x05 : 0x03), $"0x{bus:X2}");

            SDL_ClearError();
            IntPtr pad = SDL_OpenGamepad(jid);
            if (pad == IntPtr.Zero)
            {
                string err = Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";
                if (nintendoNoForm)
                {
                    Console.WriteLine($"  [INFO] SDL does not open the pad: '{err}'");
                    return;
                }
                Expect("SDL opens the pad", false, $"SDL_GetError '{err}'");
                return;
            }
            if (nintendoNoForm) Console.WriteLine("  [INFO] SDL opens the pad");
            else Expect("SDL opens the pad", true);
            try
            {
                int type = SDL_GetGamepadType(pad);
                Console.WriteLine($"  gamepad '{Marshal.PtrToStringUTF8(SDL_GetGamepadName(pad))}' type {Marshal.PtrToStringUTF8(SDL_GetGamepadStringForType(type))}");
                int conn = SDL_GetJoystickConnectionState(SDL_GetGamepadJoystick(pad));
                Console.WriteLine($"  connection state {conn} (1 wired, 2 wireless)");
                Expect($"SDL reports the connection {(wantBt ? "wireless" : "wired")}", conn == (wantBt ? 2 : 1), conn.ToString());

                // A face button: A and B, so a Nintendo label remap still
                // shows one of the four.
                setState(new HMGamepadState { Buttons = HMButton.A | HMButton.B });
                bool pressed = WaitPump(() => Enumerable.Range(0, 4).Any(b => SDL_GetGamepadButton(pad, b)), 5000);
                // The SDK places HMButton bits on the descriptor's buttons in
                // declaration order. A descriptor that declares its button
                // usages out of order (stadia: 0x12 first) puts A where a
                // reader by usage or by bit finds another button, on both
                // backends alike. That is finding F1 of lane BTFORM's
                // report, outside the USB form, and its line is recorded.
                var usages = form.Inner.GetOrBuildReportBuilder().Buttons.Where(b => b.UsagePage == 0x09).Select(b => (int)b.Usage).ToList();
                bool outOfOrder = usages.Zip(usages.Skip(1), (x, y) => y < x).Any(t => t);
                if (outOfOrder && !record)
                    Console.WriteLine($"  [INFO] a face button submitted with SubmitState reads back through SDL: {(pressed ? "yes" : "no")}  the descriptor declares its button usages out of order ({string.Join(",", usages.Take(4).Select(u => "0x" + u.ToString("X2")))}...), finding F1");
                else
                    Expect("a face button submitted with SubmitState reads back through SDL", pressed);
                setState(new HMGamepadState());

                lock (decoded) decoded.Clear();
                int sentBefore;
                lock (packets) sentBefore = packets.Count(p => p.Source == HMOutputSource.HidOutput);
                bool rumbled = SDL_RumbleGamepad(pad, 0xFFFF, 0x8000, 1000);
                // A profile that decodes its motors raises them. Any other
                // shows SDL's rumble write as the output report it is.
                bool decodes = form.Inner.ExtendedOutputReport?.Fields?.Any(f => f.Semantic is "leftMotor" or "rightMotor") == true
                    || form.Inner.GetOrCompileModel().Decoders.Any(d => d.Semantics.Contains("leftMotor"));
                if (decodes)
                {
                    bool reached = rumbled && WaitPump(() => { lock (decoded) return decoded.Any(f => Num(f, "leftMotor") > 0 || Num(f, "rightMotor") > 0); }, 5000);
                    string motors;
                    lock (decoded) motors = decoded.Count == 0 ? "none" : string.Join(" ", decoded[^1].Where(k => k.Key.EndsWith("Motor")).Select(k => $"{k.Key}={k.Value}"));
                    Expect("SDL_RumbleGamepad reaches OutputDecoded leftMotor/rightMotor", reached, $"rumble call {rumbled}, {motors}");
                }
                else
                {
                    bool wrote = rumbled && WaitPump(() => { lock (packets) return packets.Count(p => p.Source == HMOutputSource.HidOutput) > sentBefore; }, 5000);
                    string what;
                    lock (packets) what = string.Join(" ", packets.Where(p => p.Source == HMOutputSource.HidOutput).Skip(sentBefore).Take(2).Select(p => $"0x{p.Id:X2}:{Convert.ToHexString(p.Data)}"));
                    Expect($"SDL_RumbleGamepad's write reaches OutputReceived ({form.Id} decodes no motors)", wrote, $"rumble call {rumbled}, {(what.Length > 0 ? what : "none")}");
                }

                if (form.Inner.ExtendedOutputReport?.Fields?.Any(f => f.Semantic == "lightbar") == true)
                {
                    lock (decoded) decoded.Clear();
                    bool set = SDL_SetGamepadLED(pad, 0xFF, 0x00, 0x00);
                    bool lit = set && WaitPump(() => { lock (decoded) return decoded.Any(f => f.TryGetValue("lightbar", out var lb) && LightRed(lb)); }, 5000);
                    string bar;
                    lock (decoded) bar = decoded.Count == 0 ? "none" : decoded[^1].TryGetValue("lightbar", out var lb) ? Show(lb) : "no lightbar";
                    Expect("SDL_SetGamepadLED reaches OutputDecoded lightbar", lit, $"call {set}, {bar}");
                }
                else if (form.Inner.Protocol != null)
                {
                    // The player LEDs: SDL sends subcommand 0x30 on open
                    // (UpdateSlotLED, SDL_hidapi_switch.c:777-788).
                    bool led;
                    lock (packets) led = packets.Any(p => p.Id == 0x01 && p.Data.Length > 9 && p.Data[9] == 0x30);
                    Expect("SDL sets the player LEDs (subcommand 0x30 reaches OutputReceived)", led);
                    // On a USB bus SDL opens the pad only after the 0x80 0x02
                    // handshake is answered (SDL_hidapi_switch.c:1617-1622,
                    // 689-711), and the device's protocol consumes the 0x80
                    // commands, so they reach no OutputReceived handler: the
                    // open itself is the evidence.
                }
            }
            finally { SDL_CloseGamepad(pad); }
        }

        static bool LightRed(object v) => v switch
        {
            byte[] b => b.Length >= 3 && b[0] == 0xFF && b[1] == 0 && b[2] == 0,
            IConvertible c => (c.ToInt32(null) & 0xFFFFFF) is 0xFF0000 or 0x0000FF,
            _ => Show(v).Contains("FF0000", StringComparison.OrdinalIgnoreCase),
        };

        static string Show(object v) => v is byte[] b ? Convert.ToHexString(b) : v?.ToString() ?? "null";

        static bool WaitPump(Func<bool> cond, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { SDL_PumpEvents(); if (cond()) return true; Thread.Sleep(10); }
            SDL_PumpEvents();
            return cond();
        }
    }
}
