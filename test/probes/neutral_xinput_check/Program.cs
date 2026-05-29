// End-to-end neutral-input override check.
//
// Creates a real xbox-360-wired virtual controller, drives an intentionally
// active HMGamepadState, reads the resulting XInput slot, then enables
// HMController.Neutralized while continuing to submit that active state. The
// assertion is intentionally external: XInput must keep reporting the slot as
// connected, but every input field must stay neutral.
//
// Requires admin (driver install + virtual creation). Exit 0 on PASS.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;

internal sealed class Program
{
    private const uint ERROR_SUCCESS = 0;
    private static int s_total;
    private static int s_failures;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll")]
    private static extern uint XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    private static void Check(string label, bool ok, string detail = "")
    {
        s_total++;
        if (!ok) s_failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    private static string FormatState(in XINPUT_STATE s)
    {
        var g = s.Gamepad;
        return $"pkt={s.dwPacketNumber} btn=0x{g.wButtons:X4} LT={g.bLeftTrigger} RT={g.bRightTrigger} " +
               $"LX={g.sThumbLX} LY={g.sThumbLY} RX={g.sThumbRX} RY={g.sThumbRY}";
    }

    private static HashSet<int> ConnectedSlots()
    {
        var result = new HashSet<int>();
        for (uint i = 0; i < 4; i++)
            if (XInputGetState(i, out _) == ERROR_SUCCESS) result.Add((int)i);
        return result;
    }

    private static int WaitForNewSlot(HashSet<int> baseline, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            for (uint i = 0; i < 4; i++)
            {
                if (baseline.Contains((int)i)) continue;
                if (XInputGetState(i, out _) == ERROR_SUCCESS) return (int)i;
            }
            Thread.Sleep(20);
        }
        return -1;
    }

    private static bool IsNeutral(in XINPUT_STATE s)
    {
        var g = s.Gamepad;
        return g.wButtons == 0
            && g.bLeftTrigger == 0
            && g.bRightTrigger == 0
            // The SDK packs neutral stick centers as 32767/32768-ish and the
            // XUSB companion maps that back to signed XInput space. Allow a
            // tiny one/two-count rounding tolerance around zero.
            && Math.Abs((int)g.sThumbLX) <= 2
            && Math.Abs((int)g.sThumbLY) <= 2
            && Math.Abs((int)g.sThumbRX) <= 2
            && Math.Abs((int)g.sThumbRY) <= 2;
    }

    private static bool IsActive(in XINPUT_STATE s)
    {
        var g = s.Gamepad;
        return g.wButtons != 0
            || g.bLeftTrigger > 8
            || g.bRightTrigger > 8
            || Math.Abs((int)g.sThumbLX) > 4000
            || Math.Abs((int)g.sThumbLY) > 4000
            || Math.Abs((int)g.sThumbRX) > 4000
            || Math.Abs((int)g.sThumbRY) > 4000;
    }

    private static bool WaitForActive(HMController ctrl, in HMGamepadState active, int slot, string label, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        XINPUT_STATE last = default;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            ctrl.SubmitState(in active);
            Thread.Sleep(30);
            if (XInputGetState((uint)slot, out last) == ERROR_SUCCESS && IsActive(in last))
            {
                Check(label, true, FormatState(in last));
                return true;
            }
        }
        Check(label, false, $"last={FormatState(in last)}");
        return false;
    }

    private static bool AssertNeutralStable(HMController ctrl, in HMGamepadState sourceStillActive, int slot, string label, int durationMs)
    {
        var sw = Stopwatch.StartNew();
        int samples = 0;
        XINPUT_STATE last = default;
        while (sw.ElapsedMilliseconds < durationMs)
        {
            // Keep submitting active source data while neutralized. This is the
            // behavior the fork needs for real gamepad passthrough: the source
            // may still be held/moving, but the virtual output must stay idle.
            ctrl.SubmitState(in sourceStillActive);
            Thread.Sleep(30);
            samples++;
            if (XInputGetState((uint)slot, out last) != ERROR_SUCCESS || !IsNeutral(in last))
            {
                Check(label, false, $"sample={samples} state={FormatState(in last)}");
                return false;
            }
        }
        Check(label, true, $"{samples} neutral samples; last={FormatState(in last)}");
        return true;
    }

    public static int Main()
    {
        Console.WriteLine("=== HIDMaestro neutral-input XInput regression ===");

        try { HMContext.RemoveAllVirtualControllers(); } catch { }
        Thread.Sleep(500);
        var baseline = ConnectedSlots();
        Console.WriteLine($"  baseline XInput slots: {(baseline.Count == 0 ? "(none)" : string.Join(",", baseline))}");

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        Console.Write("  Installing driver... ");
        ctx.InstallDriver();
        Console.WriteLine("OK");

        var profile = ctx.GetProfile("xbox-360-wired")
                      ?? throw new Exception("missing xbox-360-wired");

        Console.Write("  Creating xbox-360-wired controller... ");
        using var ctrl = ctx.CreateController(profile);
        Console.WriteLine("OK");

        int slot = WaitForNewSlot(baseline, timeoutMs: 5000);
        if (slot < 0)
        {
            Console.WriteLine("  FAIL: virtual did not claim a new XInput slot within 5 s.");
            return 1;
        }
        Console.WriteLine($"  virtual XInput slot: {slot}");

        var active = new HMGamepadState
        {
            Axes = HMGamepadStateHelpers.StandardAxes(profile,
                leftStickX: 1.0f, leftStickY: 0.0f,
                rightStickX: 1.0f, rightStickY: 0.0f,
                leftTrigger: 1.0f, rightTrigger: 1.0f),
            Buttons = HMButton.A | HMButton.B | HMButton.Start,
            Hat = HMHat.East,
        };

        WaitForActive(ctrl, in active, slot, "baseline source input reaches XInput", timeoutMs: 3000);

        ctrl.Neutralized = true;
        Thread.Sleep(80);
        XInputGetState((uint)slot, out var afterSet);
        Check("setting Neutralized immediately releases current input",
            IsNeutral(in afterSet), FormatState(in afterSet));

        AssertNeutralStable(ctrl, in active, slot,
            "Neutralized=true suppresses continued active source input", durationMs: 1500);

        ctrl.Neutralized = false;
        WaitForActive(ctrl, in active, slot,
            "Neutralized=false restores input without reconnect", timeoutMs: 3000);

        ctrl.Neutralized = true;
        AssertNeutralStable(ctrl, in active, slot,
            "second neutralize remains stable on same slot", durationMs: 1000);

        ctrl.Neutralized = false;
        WaitForActive(ctrl, in active, slot,
            "second unneutralize restores input on same slot", timeoutMs: 3000);

        Console.WriteLine($"\n=== {s_total - s_failures}/{s_total} PASS ===");
        return s_failures == 0 ? 0 : 1;
    }
}
