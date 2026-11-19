// sdl <backend> <dir> [ids]: each profile through the SDL3.dll in <dir>, one
// at a time: whether SDL opens the pad, the driver and bus its joystick GUID
// names (SDL_CreateJoystickGUID: bytes 0-1 the bus, byte 14 the driver,
// 'h' for HIDAPI), the gamepad type, the connection state, a face button,
// rumble and the LEDs, each read back through the controller's
// OutputDecoded or OutputReceived. dualsense-composite runs first as the
// positive control. The Joy-Cons, the SNES, N64 and Genesis pads, their USB
// forms and the two Stadia pads have each of their controls checked on the
// joystick button or axis SDL's driver for the pad sends it on. On USB/IP
// the plain copies, the Bluetooth profiles with no usbForm, print what SDL
// does as the control for the rule.

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
        [DllImport(L)] static extern short SDL_GetGamepadAxis(IntPtr g, int axis);
        [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_GetJoystickButton(IntPtr j, int button);
        [DllImport(L)] static extern short SDL_GetJoystickAxis(IntPtr j, int axis);
        [DllImport(L)] static extern byte SDL_GetJoystickHat(IntPtr j, int hat);
        const int AXIS_LEFTX = 0, AXIS_LEFTY = 1, AXIS_RIGHTX = 2, AXIS_RIGHTY = 3, AXIS_LEFT_TRIGGER = 4, AXIS_RIGHT_TRIGGER = 5;
        [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_RumbleGamepad(IntPtr g, ushort low, ushort high, uint ms);
        [DllImport(L)] [return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_SetGamepadLED(IntPtr g, byte r, byte gr, byte b);

        const uint SDL_INIT_GAMEPAD = 0x00002000;

        static readonly string[] s_default =
        {
            "dualsense-composite",
            "dualsense-bt", "dualsense-bt-full", "dualsense-edge-bt", "dualshock-4-v2-bt",
            "switch-pro", "switch-pro-usb",
            "joycon-l", "joycon-r", "snes-nso", "n64-nso", "genesis-nso",
            "joycon-l-usb", "joycon-r-usb", "snes-nso-usb", "n64-nso-usb", "genesis-nso-usb",
            "stadia-controller-bt", "stadia-controller-usb", "amazon-luna-ble",
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
        static readonly string[] s_plain = { "plain:dualsense-bt", "plain:dualshock-4-v2-bt", "plain:switch-pro", "plain:joycon-l" };

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
                // The presented form's PID: the Genesis pad carries another
                // on USB, and a Joy-Con is the charging grip.
                ownPath = OwnInterface(form, serial)?.DevicePath;
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
            // A plain copy is the control the rule is measured against: what
            // SDL does with it is recorded, not asserted.
            bool plain = profile.Id.EndsWith("-plain", StringComparison.Ordinal);
            bool record = plain;
            void Expect(string name, bool cond, string detail = "")
            {
                if (record) Console.WriteLine($"  [INFO] {name}: {(cond ? "yes" : "no")}{(detail.Length > 0 ? "  " + detail : "")}");
                else Check(name, cond, detail);
            }
            if (jid == 0)
            {
                string err = Marshal.PtrToStringUTF8(SDL_GetError()) ?? "";
                if (plain)
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
                if (plain)
                {
                    Console.WriteLine($"  [INFO] SDL does not open the pad: '{err}'");
                    return;
                }
                Expect("SDL opens the pad", false, $"SDL_GetError '{err}'");
                return;
            }
            if (plain) Console.WriteLine("  [INFO] SDL opens the pad");
            else Expect("SDL opens the pad", true);
            try
            {
                int type = SDL_GetGamepadType(pad);
                Console.WriteLine($"  gamepad '{Marshal.PtrToStringUTF8(SDL_GetGamepadName(pad))}' type {Marshal.PtrToStringUTF8(SDL_GetGamepadStringForType(type))}");
                int conn = SDL_GetJoystickConnectionState(SDL_GetGamepadJoystick(pad));
                Console.WriteLine($"  connection state {conn} (1 wired, 2 wireless)");
                Expect($"SDL reports the connection {(wantBt ? "wireless" : "wired")}", conn == (wantBt ? 2 : 1), conn.ToString());

                var joy = SDL_GetGamepadJoystick(pad);
                if (PadChecks(profile.Id) is { } own)
                {
                    // Each control on the joystick button or axis SDL's own
                    // driver sends it on, read at the joystick, so the
                    // driver's reading is checked rather than a mapping.
                    foreach (var c in own)
                    {
                        setState(c.State(profile));
                        bool ok = WaitPump(() => c.Holds(joy), 5000);
                        Expect(c.Name, ok, JoyNow(joy));
                        setState(new HMGamepadState());
                        WaitPump(() => c.Released(joy), 2000);
                    }
                }
                else
                {
                    // A face button: A and B, so a Nintendo label remap still
                    // shows one of the four.
                    setState(new HMGamepadState { Buttons = HMButton.A | HMButton.B });
                    bool pressed = WaitPump(() => Enumerable.Range(0, 4).Any(b => SDL_GetGamepadButton(pad, b)), 5000);
                    Expect("a face button submitted with SubmitState reads back through SDL", pressed);
                    setState(new HMGamepadState());

                    // The sticks and triggers, filled for the profile the
                    // consumer created, read back through SDL in the form the
                    // device presents: the right stick hard right, the left
                    // stick at rest, the right trigger pulled and the left
                    // released. A form whose stick or trigger axes differ from
                    // its Bluetooth identity's (switch-pro's right stick is
                    // Rx/Ry, switch-pro-usb's Z/Rz) shows here. A pad with
                    // digital triggers pulls ZR, layout button 7.
                    bool digitalTriggers = profile.Triggers.Count == 0;
                    var standard = HMGamepadStateHelpers.StandardAxes(profile, 0.5f, 0.5f, 1.0f, 0.5f, 0f, 1.0f);
                    setState(new HMGamepadState
                    {
                        Axes = standard,
                        Buttons = digitalTriggers ? (HMButton)(1u << 7) : HMButton.None,
                    });
                    bool moved = WaitPump(() => SDL_GetGamepadAxis(pad, AXIS_RIGHTX) > 25000 && SDL_GetGamepadAxis(pad, AXIS_RIGHT_TRIGGER) > 25000, 5000);
                    short rx = SDL_GetGamepadAxis(pad, AXIS_RIGHTX), ry = SDL_GetGamepadAxis(pad, AXIS_RIGHTY);
                    short lx = SDL_GetGamepadAxis(pad, AXIS_LEFTX), ly = SDL_GetGamepadAxis(pad, AXIS_LEFTY);
                    short lt = SDL_GetGamepadAxis(pad, AXIS_LEFT_TRIGGER), rt = SDL_GetGamepadAxis(pad, AXIS_RIGHT_TRIGGER);
                    string axesNow = $"LX {lx} LY {ly} RX {rx} RY {ry} LT {lt} RT {rt}";
                    Expect("the right stick submitted hard right reads back on SDL's right stick X, and nowhere else",
                        rx > 25000 && Math.Abs(ry) < 8000 && Math.Abs(lx) < 8000 && Math.Abs(ly) < 8000, axesNow);
                    Expect("the right trigger submitted pulled reads back on SDL's right trigger, the left released",
                        rt > 25000 && lt < 8000, axesNow);
                    setState(new HMGamepadState());
                }

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

        /// <summary>One control of a pad: the state that presses or moves
        /// it, and the joystick reading that shows it, as SDL 3.4.18's
        /// driver for the pad sends it.</summary>
        sealed record Control(string Name, Func<HMProfile, HMGamepadState> State, Func<IntPtr, bool> Holds, Func<IntPtr, bool> Released);

        // The Switch driver's joystick buttons (the SDL_GAMEPAD_BUTTON values
        // SDL_hidapi_switch.c sends, and its SWITCH_SHARE and paddle enum)
        // and the Stadia driver's (SDL_hidapi_stadia.c, CAPTURE 11).
        const int J_SOUTH = 0, J_EAST = 1, J_WEST = 2, J_NORTH = 3, J_BACK = 4, J_GUIDE = 5, J_START = 6,
                  J_LSTICK = 7, J_LSHOULDER = 9, J_RSHOULDER = 10, J_SHARE = 11, J_RPADDLE1 = 12, J_LPADDLE1 = 13,
                  J_RPADDLE2 = 14, J_LPADDLE2 = 15;

        static Control Btn(int bit, int joy, string name) => new(
            $"{name} (HMButton bit {bit}) reads back as SDL joystick button {joy}",
            _ => new HMGamepadState { Buttons = (HMButton)(1u << bit) },
            j => SDL_GetJoystickButton(j, joy), j => !SDL_GetJoystickButton(j, joy));

        static Control HatBtn(HMHat hat, int joy, string name) => new(
            $"{name} (SubmitState's hat {hat}) reads back as SDL joystick button {joy}",
            _ => new HMGamepadState { Hat = hat },
            j => SDL_GetJoystickButton(j, joy), j => !SDL_GetJoystickButton(j, joy));

        // A digital trigger the Switch driver reads into a trigger axis,
        // -32768 released and 32767 pulled (HandleFullControllerState).
        static Control Trig(int bit, int axis, string name) => new(
            $"{name} (HMButton bit {bit}) reads back as SDL joystick axis {axis} pulled",
            _ => new HMGamepadState { Buttons = (HMButton)(1u << bit) },
            j => SDL_GetJoystickAxis(j, axis) > 25000, j => SDL_GetJoystickAxis(j, axis) < 0);

        static Control Stick(string name, float lx, float ly, float rx, float ry, int axis, bool positive, int other) => new(
            $"{name} reads back on SDL joystick axis {axis} {(positive ? "high" : "low")}, axis {other} at rest",
            p => new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(p, lx, ly, rx, ry, 0f, 0f) },
            j => (positive ? SDL_GetJoystickAxis(j, axis) > 25000 : SDL_GetJoystickAxis(j, axis) < -25000)
                 && Math.Abs((int)SDL_GetJoystickAxis(j, other)) < 8000,
            j => Math.Abs((int)SDL_GetJoystickAxis(j, axis)) < 8000);

        static Control AnalogTrigger(string name, int axis) => new(
            $"{name} pulled reads back on SDL joystick axis {axis}",
            p => new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(p, 0.5f, 0.5f, 0.5f, 0.5f, 0f, 1f) },
            j => SDL_GetJoystickAxis(j, axis) > 25000, j => SDL_GetJoystickAxis(j, axis) < 0);

        /// <summary>The controls checked for a pad SDL drives with a driver
        /// of its own, or null for the generic face button and stick check.
        /// A Joy-Con on its own is a sideways mini pad: its stick reads on
        /// SDL's left stick turned a quarter (HandleMiniControllerStateL and
        /// R), and L, ZL, R and ZR are its paddles. The SNES, N64 and Genesis
        /// pads take the Pro Controller's reading, with the N64's and the
        /// Genesis's face buttons by label (AlwaysUsesLabels, RemapButton).
        /// A USB form is checked as the pad it is the form of.</summary>
        static Control[]? PadChecks(string id) => (id.EndsWith("-nso-usb", StringComparison.Ordinal) || id.StartsWith("joycon-", StringComparison.Ordinal) && id.EndsWith("-usb", StringComparison.Ordinal) ? id[..^4] : id) switch
        {
            "joycon-l" => new[]
            {
                Btn(8, J_START, "Minus"), Btn(10, J_LSTICK, "the stick click"), Btn(13, J_GUIDE, "Capture"),
                Btn(4, J_LSHOULDER, "SL"), Btn(5, J_RSHOULDER, "SR"), Btn(14, J_LPADDLE1, "L"), Btn(15, J_LPADDLE2, "ZL"),
                HatBtn(HMHat.West, J_SOUTH, "the left arrow"),
                Stick("the stick pushed right, upright", 1f, 0.5f, 0.5f, 0.5f, 1, false, 0),
            },
            "joycon-r" => new[]
            {
                Btn(0, J_SOUTH, "A"), Btn(1, J_EAST, "X"), Btn(2, J_WEST, "B"), Btn(3, J_NORTH, "Y"),
                Btn(4, J_LSHOULDER, "SL"), Btn(5, J_RSHOULDER, "SR"), Btn(9, J_START, "Plus"), Btn(11, J_LSTICK, "the stick click"),
                Btn(12, J_GUIDE, "Home"), Btn(14, J_RPADDLE1, "R"), Btn(15, J_RPADDLE2, "ZR"),
                Stick("the stick pushed right, upright", 0.5f, 0.5f, 1f, 0.5f, 1, true, 0),
            },
            "snes-nso" => new[]
            {
                Btn(0, J_SOUTH, "B"), Btn(1, J_EAST, "A"), Btn(2, J_WEST, "Y"), Btn(3, J_NORTH, "X"),
                Btn(4, J_LSHOULDER, "L"), Btn(5, J_RSHOULDER, "R"), Btn(8, J_BACK, "Select"), Btn(9, J_START, "Start"),
                Trig(6, 4, "ZL"), Trig(15, 5, "ZR"),
            },
            "n64-nso" => new[]
            {
                Btn(1, J_SOUTH, "A"), Btn(0, J_EAST, "B"), Btn(2, J_NORTH, "C-Up"), Btn(3, J_WEST, "C-Left"),
                Btn(8, J_BACK, "C-Right"), Btn(4, J_LSHOULDER, "L"), Btn(5, J_RSHOULDER, "R"), Btn(9, J_START, "Start"),
                Btn(10, J_LSTICK, "ZR"), Btn(12, J_GUIDE, "Home"), Btn(13, J_SHARE, "Capture"),
                Trig(6, 4, "Z"), Trig(7, 5, "C-Down"),
                Stick("the stick pushed right", 1f, 0.5f, 0.5f, 0.5f, 0, true, 1),
            },
            "genesis-nso" => new[]
            {
                Btn(1, J_SOUTH, "A"), Btn(0, J_EAST, "B"), Btn(5, J_RSHOULDER, "C"), Btn(9, J_START, "Start"),
                Btn(12, J_GUIDE, "Home"), Btn(13, J_SHARE, "Capture"), Trig(7, 5, "Mode"),
            },
            // HMButton bit n is the button with the n-th lowest usage
            // (HidReportBuilder.OrderButtonsByUsage): A usage 0x01, B 0x02,
            // X 0x04, Y 0x05 and Capture 0x12, which SDL reads at data[3]
            // bits 0x40, 0x20, 0x10, 0x08 and data[2] bit 0x01.
            "stadia-controller-bt" or "stadia-controller-usb" => new[]
            {
                Btn(0, J_SOUTH, "A"), Btn(1, J_EAST, "B"), Btn(2, J_WEST, "X"), Btn(3, J_NORTH, "Y"), Btn(12, 11, "Capture"),
                Stick("the right stick pushed right", 0.5f, 0.5f, 1f, 0.5f, 2, true, 3),
                AnalogTrigger("the right trigger", 5),
            },
            _ => null,
        };

        static string JoyNow(IntPtr j)
        {
            var on = Enumerable.Range(0, 16).Where(b => SDL_GetJoystickButton(j, b)).ToArray();
            return $"buttons [{string.Join(",", on)}] axes {string.Join(" ", Enumerable.Range(0, 6).Select(a => SDL_GetJoystickAxis(j, a)))} hat {SDL_GetJoystickHat(j, 0)}";
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
