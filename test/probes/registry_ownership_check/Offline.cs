// The part that needs no device and no elevation: which descriptors the
// GameInput table applies to, and that no catalog profile is one of them.

using System;
using System.Linq;
using HIDMaestro;
using HIDMaestro.Internal;

internal static partial class Program
{
    static HidDescriptorBuilder Canonical(int stickBits, int triggerBits, int buttons)
    {
        var d = new HidDescriptorBuilder().Gamepad();
        d.AddStick("Left", stickBits); d.AddStick("Right", stickBits);
        d.AddTrigger("Left", triggerBits); d.AddTrigger("Right", triggerBits);
        d.AddHat(); d.AddButtons(buttons);
        return d;
    }

    static void Offline()
    {
        Console.WriteLine("\n== Offline: the shape the GameInput table was measured on ==");
        Check("Gamepad: 16-bit sticks, 16-bit triggers, hat, 11 buttons qualifies",
              GameInputMapping.HasMeasuredShape(Canonical(16, 16, 11).Build()));
        Check("the same with 8-bit triggers qualifies",
              GameInputMapping.HasMeasuredShape(Canonical(16, 8, 11).Build()));
        Check("the same with 8-bit sticks and 16 buttons qualifies",
              GameInputMapping.HasMeasuredShape(Canonical(8, 8, 16).Build()));

        var joy = new HidDescriptorBuilder().Joystick();
        joy.AddStick("Left", 16); joy.AddStick("Right", 16); joy.AddTrigger("Left", 16); joy.AddTrigger("Right", 16);
        joy.AddHat(); joy.AddButtons(11);
        Check("a Joystick collection does not", !GameInputMapping.HasMeasuredShape(joy.Build()));

        var trigFirst = new HidDescriptorBuilder().Gamepad();
        trigFirst.AddTrigger("Left"); trigFirst.AddTrigger("Right");
        trigFirst.AddStick("Left"); trigFirst.AddStick("Right"); trigFirst.AddHat(); trigFirst.AddButtons(11);
        Check("triggers before sticks do not", !GameInputMapping.HasMeasuredShape(trigFirst.Build()));

        var noHat = new HidDescriptorBuilder().Gamepad();
        noHat.AddStick("Left"); noHat.AddStick("Right"); noHat.AddTrigger("Left"); noHat.AddTrigger("Right"); noHat.AddButtons(11);
        Check("no hat does not", !GameInputMapping.HasMeasuredShape(noHat.Build()));

        var fewButtons = Canonical(16, 8, 8);
        Check("eight buttons do not", !GameInputMapping.HasMeasuredShape(fewButtons.Build()));

        var extraAxis = new HidDescriptorBuilder().Gamepad();
        extraAxis.AddStick("Left"); extraAxis.AddStick("Right"); extraAxis.AddTrigger("Left"); extraAxis.AddTrigger("Right");
        extraAxis.AddHat(); extraAxis.AddButtons(11); extraAxis.AddAxis(HMAxis.Slider);
        Check("an extra slider does not", !GameInputMapping.HasMeasuredShape(extraAxis.Build()));

        var built = new HMProfileBuilder().Id("shape-check").Vid(0xBEEF).Pid(0xF0A6)
            .FromDescriptorBuilder(Canonical(16, 8, 11)).Build();
        Check("a profile built in code with the shape gets the table", GameInputMapping.Applies(built.Inner));

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();
        var catalog = ctx.AllProfiles.ToList();
        var applies = catalog.Where(p => GameInputMapping.Applies(p.Inner)).Select(p => p.Id).ToList();
        Check($"no catalog profile gets the table ({catalog.Count} checked)", applies.Count == 0, string.Join(", ", applies));
        var shaped = catalog.Where(p => p.Inner.GetDescriptorBytes() is { } b && GameInputMapping.HasMeasuredShape(b)).Select(p => p.Id).ToList();
        Console.WriteLine($"  catalog descriptors with the measured shape: {(shaped.Count == 0 ? "none" : string.Join(", ", shaped))}");
        var clone = new HMProfileBuilder().FromProfile(ctx.GetProfile("dualsense")!).Pid(0xF0A8).Build();
        Check("a code-built clone of a catalog DualSense does not", !GameInputMapping.Applies(clone.Inner));

        LegacyGiveUp();
    }

    /// <summary>When the ledger lock gives up on v1.12.0's mutex: the five
    /// cases of LedgerLock.GivesUp, the decision after its wait. A bound of
    /// INFINITE is an owner read as SYSTEM or Administrators, 5000 ms an
    /// owner read as neither.</summary>
    static void LegacyGiveUp()
    {
        Console.WriteLine("\n== Offline: when the ledger lock gives up on v1.12.0's mutex ==");
        const uint Infinite = 0xFFFFFFFF;
        Check("an owner read as SYSTEM or Administrators, the caller's bound cut the wait: it gives up",
              LedgerLock.GivesUp(held: false, timeout: 29_990, full: Infinite));
        Check("an owner read as neither, the caller's bound cut the wait: it gives up, never writing without the mutex",
              LedgerLock.GivesUp(held: false, timeout: 10, full: 5000));
        Check("an owner read as neither, the lock's own 5 s ran out: it goes on without the mutex, as with a squatter",
              !LedgerLock.GivesUp(held: false, timeout: 5000, full: 5000));
        Check("the mutex taken within a cut wait: it goes on, holding it",
              !LedgerLock.GivesUp(held: true, timeout: 10, full: 5000));
        Check("an unbounded wait on an owner read as SYSTEM or Administrators that failed: unchanged, it goes on",
              !LedgerLock.GivesUp(held: false, timeout: Infinite, full: Infinite));
    }
}
