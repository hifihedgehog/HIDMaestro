// What Windows.Gaming.Input reads from a virtual pad at rest and under each
// stimulus, and how GameInput numbers the pad's axes and buttons
// (foundation-sunshine #1056).
//
// With no arguments it is the battery's check: the XUSB Xbox 360 pad as the
// same-window positive control, a DualSense that must stay a
// RawGameController with no GameInput key, a DualShock 4 v1 that must read
// correctly through the mapping Windows ships for it, a game pad built in
// code that holds the table while it lives and reads correctly through it,
// and a joystick built in code that gets neither. Exit 0 PASS / 1 FAIL / 2
// when another SDK consumer is running.
//
// `--explore [--absent] <profile> [<profile>...]` creates each profile in
// turn, leaves it at rest, and prints the RawGameController reading (the
// index space a GameInput registry mapping addresses) beside the Gamepad
// reading. It then moves one control at a time and prints which raw index
// and which Gamepad field moved. `--absent` deletes the profile's GameInput
// key first, so the same pad can be read both ways. `--sweep` judges every
// deployable non-Xbox profile. `--legacy` writes the table HIDMaestro wrote
// through v1.11.0 first, which is how the scope of #1056 was measured.
//
// Requires elevation.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HIDMaestro;
using Microsoft.Win32;
using Windows.Gaming.Input;

internal static class Program
{
    const string OverrideRoot = @"SYSTEM\CurrentControlSet\Control\GameInput\Devices";

    static string F(double v) => v.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);

    static RawGameController? FindRaw(ushort vid, ushort pid, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var r in RawGameController.RawGameControllers)
                if (r.HardwareVendorId == vid && r.HardwareProductId == pid) return r;
            Thread.Sleep(100);
        }
        return null;
    }

    sealed class Snapshot
    {
        public double[] Axes = Array.Empty<double>();
        public bool[] Buttons = Array.Empty<bool>();
        public GameControllerSwitchPosition[] Switches = Array.Empty<GameControllerSwitchPosition>();
        public GamepadReading? Pad;
    }

    static Snapshot Read(RawGameController raw, Gamepad? pad)
    {
        var s = new Snapshot
        {
            Axes = new double[raw.AxisCount],
            Buttons = new bool[raw.ButtonCount],
            Switches = new GameControllerSwitchPosition[raw.SwitchCount],
        };
        raw.GetCurrentReading(s.Buttons, s.Switches, s.Axes);
        if (pad != null) s.Pad = pad.GetCurrentReading();
        return s;
    }

    static string PadText(GamepadReading? p) => p is { } r
        ? $"LX {F(r.LeftThumbstickX)} LY {F(r.LeftThumbstickY)} RX {F(r.RightThumbstickX)} RY {F(r.RightThumbstickY)} LT {F(r.LeftTrigger)} RT {F(r.RightTrigger)} buttons [{r.Buttons}]"
        : "(not a Gamepad)";

    static string RawText(Snapshot s) =>
        "axes [" + string.Join(" ", s.Axes.Select(F)) + "] buttons [" +
        string.Join(",", s.Buttons.Select((b, i) => b ? i.ToString() : null).Where(x => x != null)) + "] switches [" +
        string.Join(",", s.Switches) + "]";

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct XInputState
    {
        public uint Packet;
        public ushort Buttons;
        public byte LT, RT;
        public short LX, LY, RX, RY;
    }

    [System.Runtime.InteropServices.DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    static extern uint XInputGetState(uint index, out XInputState state);

    static bool s_showXInput;
    static int s_settleMs = 600;

    /// <summary>Every connected XInput slot, as the same-window control for
    /// what WGI reads: the feed reached the pad when XInput moved.</summary>
    static string XInputText()
    {
        var parts = new List<string>();
        for (uint i = 0; i < 4; i++)
            if (XInputGetState(i, out var s) == 0)
                parts.Add($"slot{i} LX {s.LX} LY {s.LY} RX {s.RX} RY {s.RY} LT {s.LT} RT {s.RT} btn {s.Buttons:X4}");
        return parts.Count == 0 ? "no slot connected" : string.Join(" | ", parts);
    }

    static void MarkOverrideWritten(ushort vid, ushort pid)
    {
        // The SDK writes the override once per VID:PID per process. Putting
        // the pair in its cache first makes this process skip the write.
        var t = typeof(HMContext).Assembly.GetType("HIDMaestro.Internal.DeviceOrchestrator")!;
        var f = t.GetField("s_gameInputRegistryWritten", BindingFlags.NonPublic | BindingFlags.Static);
        if (f?.GetValue(null) is HashSet<uint> set)
            lock (set) set.Add(((uint)vid << 16) | pid);
        // Builds from v1.12.0 on write no override for a catalog profile, so
        // there is nothing to suppress.
    }

    /// <summary>The custom profile PadForge builds: two sticks, two
    /// triggers, a hat and eleven buttons, under a Joystick or a Game Pad
    /// top-level usage.</summary>
    static HMProfile BuildCustom(bool gamepadUsage)
    {
        var d = gamepadUsage ? new HidDescriptorBuilder().Gamepad() : new HidDescriptorBuilder().Joystick();
        d.AddStick("Left", 16); d.AddStick("Right", 16);
        d.AddTrigger("Left", 16); d.AddTrigger("Right", 16);
        d.AddHat(); d.AddButtons(11);
        return new HMProfileBuilder()
            .Id(gamepadUsage ? "wgi-custom-gamepad" : "wgi-custom-joystick")
            .Name("WGI mapping check custom").Vendor("Custom")
            .Vid(0xBEEF).Pid((ushort)(gamepadUsage ? 0xF0A5 : 0xF0A4))
            .ProductString(gamepadUsage ? "WgiCustomGamepad" : "WgiCustomJoystick").ManufacturerString("HIDMaestro")
            .Type("gamepad").Connection("usb").FromDescriptorBuilder(d).Build();
    }

    /// <summary>The GameInput override exactly as HIDMaestro wrote it through
    /// v1.11.0, for a profile with separate triggers.</summary>
    static void WriteLegacyOverride(ushort vid, ushort pid, bool combinedTriggers)
    {
        string path = $@"{OverrideRoot}\{vid:X4}{pid:X4}00010005";
        Registry.LocalMachine.DeleteSubKeyTree(path, false);
        using var gp = Registry.LocalMachine.CreateSubKey($@"{path}\Gamepad");
        void Axis(string n, int i, bool inv = false)
        {
            using var k = gp.CreateSubKey(n);
            k.SetValue("AxisIndex", i, RegistryValueKind.DWord);
            if (inv) k.SetValue("Invert", 1, RegistryValueKind.DWord);
        }
        void Button(string n, int i)
        {
            using var k = gp.CreateSubKey(n);
            k.SetValue("ButtonIndex", i, RegistryValueKind.DWord);
        }
        void DPad(string n, string pos)
        {
            using var k = gp.CreateSubKey(n);
            k.SetValue("SwitchIndex", 0, RegistryValueKind.DWord);
            k.SetValue("SwitchPosition", pos, RegistryValueKind.String);
            k.SetValue("IncludeAdjacent", 1, RegistryValueKind.DWord);
        }
        Axis("LeftThumbstickX", 0); Axis("LeftThumbstickY", 1, true);
        Axis("RightThumbstickX", 2); Axis("RightThumbstickY", 3, true);
        Axis("LeftTrigger", combinedTriggers ? 5 : 4); Axis("RightTrigger", combinedTriggers ? 6 : 5);
        Button("A", 0); Button("B", 1); Button("X", 2); Button("Y", 3);
        Button("LeftShoulder", 4); Button("RightShoulder", 5);
        Button("View", 6); Button("Menu", 7);
        Button("LeftThumbstickButton", 8); Button("RightThumbstickButton", 9);
        DPad("DPadUp", "Up"); DPad("DPadDown", "Down"); DPad("DPadLeft", "Left"); DPad("DPadRight", "Right");
    }

    static bool WaitGone(ushort vid, ushort pid, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (!RawGameController.RawGameControllers.Any(r => r.HardwareVendorId == vid && r.HardwareProductId == pid))
                return true;
            Thread.Sleep(100);
        }
        return false;
    }

    sealed class Verdict
    {
        public bool Listed;
        public bool IsGamepad;
        public int Judged;
        public bool KeyWhileLive;
        public List<string> Problems = new();
        public string Text = "";
        public override string ToString() => Text;
    }

    /// <summary>One profile through WGI: is it a Gamepad, and when it is,
    /// does every control the raw reading sees land on the right Gamepad
    /// field.</summary>
    static Verdict SweepOne(HMContext ctx, HMProfile profile, string mode)
    {
        var v = new Verdict();
        ushort vid = profile.VendorId, pid = profile.ProductId;
        string keyPath = $@"{OverrideRoot}\{vid:X4}{pid:X4}00010005";
        if (mode == "absent")
        {
            Registry.LocalMachine.DeleteSubKeyTree(keyPath, false);
            MarkOverrideWritten(vid, pid);
        }
        else if (mode == "legacy")
        {
            WriteLegacyOverride(vid, pid, string.Equals(profile.TriggerMode, "combined", StringComparison.OrdinalIgnoreCase));
            MarkOverrideWritten(vid, pid);
        }
        if (!WaitGone(vid, pid, 6000))
        {
            v.Text = "SKIP: a device with this VID:PID is still listed by WGI";
            return v;
        }

        HMController pad;
        try { pad = ctx.CreateController(profile); }
        catch (Exception ex) { v.Text = $"CREATE FAILED: {ex.GetType().Name}: {ex.Message}"; return v; }
        bool stop = false;
        var neutral = new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(profile) };
        var state = neutral;
        object gate = new();
        var feeder = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                HMGamepadState s;
                lock (gate) s = state;
                try { pad.SubmitState(in s); } catch { }
                Thread.Sleep(8);
            }
        }) { IsBackground = true };
        feeder.Start();
        try
        {
            var raw = FindRaw(vid, pid, 10000);
            if (raw == null) { v.Text = "WGI lists no RawGameController within 10 s"; return v; }
            v.Listed = true;
            using (var k = Registry.LocalMachine.OpenSubKey(keyPath)) v.KeyWhileLive = k != null;
            Thread.Sleep(s_settleMs);
            var gp = Gamepad.FromGameController(raw);
            string head = $"'{raw.DisplayName}' {raw.AxisCount}a/{raw.ButtonCount}b/{raw.SwitchCount}s";
            if (gp == null) { v.Text = $"not a Gamepad  {head}"; return v; }
            v.IsGamepad = true;

            var problems = v.Problems;
            var rest = Read(raw, gp);
            if (rest.Pad is { } r0)
            {
                void RestCheck(string n, double v, double lim) { if (Math.Abs(v) > lim) problems.Add($"rest {n}={F(v)}"); }
                RestCheck("LX", r0.LeftThumbstickX, 0.1); RestCheck("LY", r0.LeftThumbstickY, 0.1);
                RestCheck("RX", r0.RightThumbstickX, 0.1); RestCheck("RY", r0.RightThumbstickY, 0.1);
                RestCheck("LT", r0.LeftTrigger, 0.1); RestCheck("RT", r0.RightTrigger, 0.1);
                if (r0.Buttons != GamepadButtons.None) problems.Add($"rest buttons [{r0.Buttons}]");
            }

            int judged = 0;
            void Stim(string label, HMGamepadState s, Func<GamepadReading, GamepadReading, bool> expected, string expectText)
            {
                lock (gate) state = s;
                Thread.Sleep(220);
                var now = Read(raw, gp);
                lock (gate) state = neutral;
                Thread.Sleep(120);
                bool rawMoved = false;
                for (int i = 0; i < now.Axes.Length; i++) if (Math.Abs(now.Axes[i] - rest.Axes[i]) > 0.2) rawMoved = true;
                for (int i = 0; i < now.Buttons.Length; i++) if (now.Buttons[i] != rest.Buttons[i]) rawMoved = true;
                for (int i = 0; i < now.Switches.Length; i++) if (now.Switches[i] != rest.Switches[i]) rawMoved = true;
                if (!rawMoved || now.Pad is not { } a || rest.Pad is not { } b) return;   // the profile lacks this control
                judged++;
                if (!expected(a, b))
                {
                    var moved = new List<string>();
                    void D(string n, double x, double y) { if (Math.Abs(x - y) > 0.2) moved.Add($"{n} {F(y)}->{F(x)}"); }
                    D("LX", a.LeftThumbstickX, b.LeftThumbstickX); D("LY", a.LeftThumbstickY, b.LeftThumbstickY);
                    D("RX", a.RightThumbstickX, b.RightThumbstickX); D("RY", a.RightThumbstickY, b.RightThumbstickY);
                    D("LT", a.LeftTrigger, b.LeftTrigger); D("RT", a.RightTrigger, b.RightTrigger);
                    if (a.Buttons != b.Buttons) moved.Add($"[{a.Buttons}]");
                    problems.Add($"{label}: expected {expectText}, got {(moved.Count == 0 ? "nothing" : string.Join(" ", moved))}");
                }
            }
            HMGamepadState Ax(float lx = .5f, float ly = .5f, float rx = .5f, float ry = .5f, float lt = 0, float rt = 0)
                => new() { Axes = HMGamepadStateHelpers.StandardAxes(profile, lx, ly, rx, ry, lt, rt) };
            HMGamepadState Btn(HMButton bt) => new() { Axes = HMGamepadStateHelpers.StandardAxes(profile), Buttons = bt };
            static bool Only(GamepadReading a, GamepadReading b, Func<GamepadReading, double> f, double target, GamepadButtons buttons = GamepadButtons.None)
            {
                double[] all = { a.LeftThumbstickX - b.LeftThumbstickX, a.LeftThumbstickY - b.LeftThumbstickY,
                                 a.RightThumbstickX - b.RightThumbstickX, a.RightThumbstickY - b.RightThumbstickY,
                                 a.LeftTrigger - b.LeftTrigger, a.RightTrigger - b.RightTrigger };
                int movedCount = all.Count(d => Math.Abs(d) > 0.2);
                return Math.Abs(f(a) - target) < 0.15 && movedCount == 1 && a.Buttons == buttons;
            }

            Stim("LX right", Ax(lx: 1f), (a, b) => Only(a, b, g => g.LeftThumbstickX, 1), "LX +1");
            Stim("LY down", Ax(ly: 1f), (a, b) => Only(a, b, g => g.LeftThumbstickY, -1), "LY -1");
            Stim("RX right", Ax(rx: 1f), (a, b) => Only(a, b, g => g.RightThumbstickX, 1), "RX +1");
            Stim("RY down", Ax(ry: 1f), (a, b) => Only(a, b, g => g.RightThumbstickY, -1), "RY -1");
            Stim("LT full", Ax(lt: 1f), (a, b) => Only(a, b, g => g.LeftTrigger, 1), "LT 1");
            Stim("RT full", Ax(rt: 1f), (a, b) => Only(a, b, g => g.RightTrigger, 1), "RT 1");
            foreach (var (hb, gb) in new[] {
                (HMButton.A, GamepadButtons.A), (HMButton.B, GamepadButtons.B), (HMButton.X, GamepadButtons.X), (HMButton.Y, GamepadButtons.Y),
                (HMButton.LeftBumper, GamepadButtons.LeftShoulder), (HMButton.RightBumper, GamepadButtons.RightShoulder),
                (HMButton.Back, GamepadButtons.View), (HMButton.Start, GamepadButtons.Menu),
                (HMButton.LeftStick, GamepadButtons.LeftThumbstick), (HMButton.RightStick, GamepadButtons.RightThumbstick) })
                Stim(hb.ToString(), Btn(hb), (a, b) => a.Buttons == gb, gb.ToString());
            Stim("Hat North", new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(profile), Hat = HMHat.North },
                 (a, b) => a.Buttons == GamepadButtons.DPadUp, "DPadUp");
            Stim("Hat East", new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(profile), Hat = HMHat.East },
                 (a, b) => a.Buttons == GamepadButtons.DPadRight, "DPadRight");

            v.Judged = judged;
            v.Text = problems.Count == 0
                ? $"Gamepad, correct ({judged} controls judged)  {head}"
                : $"Gamepad, WRONG ({problems.Count} of {judged} controls plus rest)  {head}\n      " + string.Join("\n      ", problems);
            return v;
        }
        finally
        {
            Volatile.Write(ref stop, true);
            feeder.Join(500);
            try { pad.Dispose(); } catch { }
            if (mode == "legacy") Registry.LocalMachine.DeleteSubKeyTree(keyPath, false);
        }
    }

    static int s_total, s_failures;

    static void Check(string name, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static string? FindLiveSdkConsumer()
    {
        int self = Environment.ProcessId;
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            if (p.Id == self) { p.Dispose(); continue; }
            try
            {
                foreach (System.Diagnostics.ProcessModule m in p.Modules)
                    if (string.Equals(m.ModuleName, "HIDMaestro.Core.dll", StringComparison.OrdinalIgnoreCase))
                        return p.ProcessName;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return null;
    }

    /// <summary>The battery's check (foundation-sunshine #1056): what WGI
    /// reads from a pad at rest and under each control, with the GameInput
    /// key the SDK now writes or leaves alone. The XUSB pad is the
    /// same-window positive control: WGI reads our feed through a path the
    /// GameInput key never touched.</summary>
    static int Assert()
    {
        Console.WriteLine("=== What WGI reads from a virtual pad (foundation-sunshine #1056) ===");
        string? consumer = FindLiveSdkConsumer();
        if (consumer != null)
        {
            Console.WriteLine($"  [SKIP] {consumer} is running and shares the SDK's devices.");
            return 2;
        }
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();

        Console.WriteLine("\n-- xbox-360-wired, the positive control --");
        var x = SweepOne(ctx, ctx.GetProfile("xbox-360-wired")!, "sdk");
        Console.WriteLine($"  {x}");
        Check("WGI reads the XUSB pad as a Gamepad, correct at rest and on every control",
              x.IsGamepad && x.Problems.Count == 0 && x.Judged >= 16, $"{x.Judged} judged");

        Console.WriteLine("\n-- dualsense: no mapping, as on a real pad --");
        var ds = SweepOne(ctx, ctx.GetProfile("dualsense")!, "sdk");
        Console.WriteLine($"  {ds}");
        Check("WGI lists the DualSense", ds.Listed);
        Check("as a RawGameController, not a Gamepad read through a wrong table", ds.Listed && !ds.IsGamepad);
        Check("no GameInput key under 054C:0CE6 while it lives", !ds.KeyWhileLive);

        Console.WriteLine("\n-- dualshock-4-v1: Windows' own mapping --");
        if (GameInputMappingShipped(0x054C, 0x05C4))
        {
            var ds4 = SweepOne(ctx, ctx.GetProfile("dualshock-4-v1")!, "sdk");
            Console.WriteLine($"  {ds4}");
            Check("Windows' mapping makes it a Gamepad, correct at rest and on every control",
                  ds4.IsGamepad && ds4.Problems.Count == 0 && ds4.Judged >= 16, $"{ds4.Judged} judged");
            Check("no GameInput key from HIDMaestro shadows it", !ds4.KeyWhileLive);
        }
        else
            Console.WriteLine("  [SKIP] this Windows build ships no mapping for 054C:05C4");

        Console.WriteLine("\n-- a game pad built in code with the measured shape --");
        var custom = BuildCustom(true);
        var c = SweepOne(ctx, custom, "sdk");
        Console.WriteLine($"  {c}");
        Check("it holds the table while it lives", c.KeyWhileLive);
        Check("and WGI reads it correctly at rest and on every control",
              c.IsGamepad && c.Problems.Count == 0 && c.Judged >= 16, $"{c.Judged} judged");
        using (var k = Registry.LocalMachine.OpenSubKey($@"{OverrideRoot}\BEEFF0A500010005"))
            Check("the table is gone after dispose", k == null);

        Console.WriteLine("\n-- a joystick built in code --");
        var j = SweepOne(ctx, BuildCustom(false), "sdk");
        Console.WriteLine($"  {j}");
        Check("no table and no Gamepad", j.Listed && !j.IsGamepad && !j.KeyWhileLive);

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} checks passed ===");
        return s_failures == 0 ? 0 : 1;
    }

    static bool GameInputMappingShipped(ushort vid, ushort pid)
    {
        using var k = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\GameInput\Devices\{vid:X4}{pid:X4}00010005");
        return k != null;
    }

    static int Sweep(string mode, string? only)
    {
        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        var profiles = ctx.AllProfiles
            .Where(p => p.IsDeployable && !p.RequiresUsbipBackend)
            .Where(p => !string.Equals(p.DriverMode, "xinputhid", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(p.TriggerMode, "combined", StringComparison.OrdinalIgnoreCase))
            .Where(p => only == null || only.Split(',').Contains(p.Id))
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
        Console.WriteLine($"sweep mode {mode}: {profiles.Count} deployable non-Xbox UMDF2 profiles");
        int n = 0;
        foreach (var p in profiles)
        {
            n++;
            string verdict;
            try { verdict = SweepOne(ctx, p, mode).Text; }
            catch (Exception ex) { verdict = $"ERROR {ex.GetType().Name}: {ex.Message}"; }
            Console.WriteLine($"[{n,3}/{profiles.Count}] {p.Id,-36} {p.VendorId:X4}:{p.ProductId:X4} {verdict}");
        }
        return 0;
    }

    static void Explore(HMContext ctx, string id, bool absent)
    {
        var profile = id == "custom-joystick" ? BuildCustom(false)
                    : id == "custom-gamepad" ? BuildCustom(true)
                    : ctx.GetProfile(id);
        if (profile == null) { Console.WriteLine($"\n== {id}: no such profile"); return; }
        ushort vid = profile.VendorId, pid = profile.ProductId;
        string keyName = $"{vid:X4}{pid:X4}00010005";
        Console.WriteLine($"\n== {id} ({vid:X4}:{pid:X4}), override key {(absent ? "ABSENT" : "as the SDK writes it")} ==");

        if (absent)
        {
            try { Registry.LocalMachine.DeleteSubKeyTree($@"{OverrideRoot}\{keyName}", false); } catch { }
            MarkOverrideWritten(vid, pid);
        }

        using var pad = ctx.CreateController(profile);
        bool stop = false;
        var state = new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(profile) };
        object gate = new();
        var feeder = new Thread(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                HMGamepadState s;
                lock (gate) s = state;
                try { pad.SubmitState(in s); } catch { }
                Thread.Sleep(8);
            }
        }) { IsBackground = true };
        feeder.Start();

        using (var k = Registry.LocalMachine.OpenSubKey($@"{OverrideRoot}\{keyName}"))
            Console.WriteLine($"  override key after create: {(k == null ? "absent" : "present")}");

        var raw = FindRaw(vid, pid, 10000);
        if (raw == null)
        {
            Console.WriteLine("  WGI lists no RawGameController for it within 10 s");
            Volatile.Write(ref stop, true); feeder.Join(500);
            return;
        }
        Thread.Sleep(s_settleMs);
        var gp = Gamepad.FromGameController(raw);
        var sameIdentity = RawGameController.RawGameControllers
            .Where(r => r.HardwareVendorId == vid && r.HardwareProductId == pid).ToList();
        Console.WriteLine($"  WGI lists {sameIdentity.Count} RawGameController(s) for {vid:X4}:{pid:X4} and {Gamepad.Gamepads.Count} Gamepad(s) in all");
        foreach (var r in sameIdentity)
            Console.WriteLine($"    '{r.DisplayName}' axes {r.AxisCount} buttons {r.ButtonCount} switches {r.SwitchCount} gamepad {(Gamepad.FromGameController(r) != null ? "yes" : "no")}");
        Console.WriteLine($"  RawGameController '{raw.DisplayName}': {raw.AxisCount} axes, {raw.ButtonCount} buttons, {raw.SwitchCount} switches. Gamepad: {(gp != null ? "yes" : "no")}");
        Console.WriteLine($"  id {raw.NonRoamableId}");

        var rest = Read(raw, gp);
        Console.WriteLine($"  REST raw: {RawText(rest)}");
        Console.WriteLine($"  REST pad: {PadText(rest.Pad)}");
        Console.WriteLine($"  REST xinput: {XInputText()}");

        void Stimulus(string label, HMGamepadState s)
        {
            lock (gate) state = s;
            Thread.Sleep(220);
            var now = Read(raw, gp);
            var moved = new List<string>();
            for (int i = 0; i < now.Axes.Length; i++)
                if (Math.Abs(now.Axes[i] - rest.Axes[i]) > 0.2) moved.Add($"axis{i} {F(rest.Axes[i])}->{F(now.Axes[i])}");
            for (int i = 0; i < now.Buttons.Length; i++)
                if (now.Buttons[i] != rest.Buttons[i]) moved.Add($"button{i}");
            for (int i = 0; i < now.Switches.Length; i++)
                if (now.Switches[i] != rest.Switches[i]) moved.Add($"switch{i}={now.Switches[i]}");
            var padMoved = new List<string>();
            if (now.Pad is { } a && rest.Pad is { } b)
            {
                void D(string n, double x, double y) { if (Math.Abs(x - y) > 0.2) padMoved.Add($"{n} {F(y)}->{F(x)}"); }
                D("LX", a.LeftThumbstickX, b.LeftThumbstickX); D("LY", a.LeftThumbstickY, b.LeftThumbstickY);
                D("RX", a.RightThumbstickX, b.RightThumbstickX); D("RY", a.RightThumbstickY, b.RightThumbstickY);
                D("LT", a.LeftTrigger, b.LeftTrigger); D("RT", a.RightTrigger, b.RightTrigger);
                if (a.Buttons != b.Buttons) padMoved.Add($"buttons [{b.Buttons}]->[{a.Buttons}]");
            }
            Console.WriteLine($"  {label,-14} raw: {(moved.Count == 0 ? "-" : string.Join(", ", moved))}" +
                              (gp != null ? $"   pad: {(padMoved.Count == 0 ? "-" : string.Join(", ", padMoved))}" : "") +
                              (s_showXInput ? $"   xinput: {XInputText()}" : ""));
            lock (gate) state = new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(profile) };
            Thread.Sleep(120);
        }

        HMGamepadState Ax(float lx = .5f, float ly = .5f, float rx = .5f, float ry = .5f, float lt = 0, float rt = 0)
            => new() { Axes = HMGamepadStateHelpers.StandardAxes(profile, lx, ly, rx, ry, lt, rt) };
        HMGamepadState Btn(HMButton b) => new() { Axes = HMGamepadStateHelpers.StandardAxes(profile), Buttons = b };

        Stimulus("LX right", Ax(lx: 1f));
        Stimulus("LY down", Ax(ly: 1f));
        Stimulus("RX right", Ax(rx: 1f));
        Stimulus("RY down", Ax(ry: 1f));
        Stimulus("LT full", Ax(lt: 1f));
        Stimulus("RT full", Ax(rt: 1f));
        foreach (var b in new[] { HMButton.A, HMButton.B, HMButton.X, HMButton.Y, HMButton.LeftBumper, HMButton.RightBumper,
                                  HMButton.Back, HMButton.Start, HMButton.LeftStick, HMButton.RightStick, HMButton.Guide })
            Stimulus(b.ToString(), Btn(b));
        Stimulus("Hat North", new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(profile), Hat = HMHat.North });
        Stimulus("Hat East", new HMGamepadState { Axes = HMGamepadStateHelpers.StandardAxes(profile), Hat = HMHat.East });

        Volatile.Write(ref stop, true);
        feeder.Join(500);
    }

    static int Main(string[] args)
    {
        Gamepad.GamepadAdded += (_, _) => { };
        RawGameController.RawGameControllerAdded += (_, _) => { };

        if (args.Contains("--list"))
        {
            // Read-only: no SDK call, nothing created or removed.
            Thread.Sleep(1500);
            var all = RawGameController.RawGameControllers;
            Console.WriteLine($"WGI lists {all.Count} RawGameController(s), {Gamepad.Gamepads.Count} Gamepad(s)");
            foreach (var r in all)
            {
                var g = Gamepad.FromGameController(r);
                var snap = Read(r, g);
                Console.WriteLine($"- {r.HardwareVendorId:X4}:{r.HardwareProductId:X4} '{r.DisplayName}' axes {r.AxisCount} buttons {r.ButtonCount} switches {r.SwitchCount} gamepad {(g != null ? "yes" : "no")}");
                Console.WriteLine($"    id {r.NonRoamableId}");
                Console.WriteLine($"    raw: {RawText(snap)}");
                Console.WriteLine($"    pad: {PadText(snap.Pad)}");
            }
            return 0;
        }

        bool absent = args.Contains("--absent");
        s_showXInput = args.Contains("--xinput");
        foreach (var a in args)
            if (a.StartsWith("--settle=", StringComparison.Ordinal) && int.TryParse(a.AsSpan(9), out int ms))
                s_settleMs = ms;

        if (args.Contains("--sweep"))
        {
            string mode = args.Contains("--legacy") ? "legacy" : absent ? "absent" : "sdk";
            string? only = args.FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))?.Substring(7);
            return Sweep(mode, only);
        }
        var ids = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (args.Length == 0) return Assert();
        if (!args.Contains("--explore") || ids.Count == 0)
        {
            Console.WriteLine("usage: WgiMappingCheck                      the battery's check");
            Console.WriteLine("       WgiMappingCheck --list               read-only WGI listing");
            Console.WriteLine("       WgiMappingCheck --sweep [--legacy|--absent] [--only=a,b]");
            Console.WriteLine("       WgiMappingCheck --explore [--absent] [--xinput] [--settle=ms] <profile>...");
            return 2;
        }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        ctx.InstallDriver();
        Console.WriteLine($"physical or leftover WGI devices before any create: {RawGameController.RawGameControllers.Count}");
        foreach (var id in ids) Explore(ctx, id, absent);
        return 0;
    }
}
