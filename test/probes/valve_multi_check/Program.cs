// Several Valve personas at once (issue #56).
//
// Two questions the single-device scenarios cannot answer:
//
//   A) Three DIFFERENT personas live together. The usbip backend carries
//      all three, and a consumer sees three distinct models rather than
//      one device three times.
//
//   B) Two of the SAME persona live together. This is the case that was
//      broken: the Deck's serial is captured from a real unit, and Steam
//      keys its per-controller configuration off the string it reads
//      (configset_<serial>.vdf), so two Decks reporting one serial share
//      one configuration. UsbDescriptorSet.InstanceSerial now varies the
//      trailing digits per controller index, leaving instance 0 identical
//      to the captured unit.
//
// Checked through stock upstream SDL3 rather than our own decode, for the
// same reason S53 is: the fork beside this repo skips HIDMaestro devices.
//
// Exit 0 PASS, 1 FAIL, 2 SKIP (no stock SDL3 build, or Steam is running).

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using HIDMaestro;

static class ValveMultiCheck
{
    const string SDL = "SDL3";
    [DllImport(SDL)] static extern bool SDL_SetHint(string name, string value);
    [DllImport(SDL)] static extern bool SDL_Init(uint flags);
    [DllImport(SDL)] static extern void SDL_Quit();
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepads(out int count);
    [DllImport(SDL)] static extern IntPtr SDL_OpenGamepad(uint id);
    [DllImport(SDL)] static extern void SDL_CloseGamepad(IntPtr gp);
    [DllImport(SDL)] static extern void SDL_UpdateGamepads();
    [DllImport(SDL)] static extern ushort SDL_GetGamepadVendor(IntPtr gp);
    [DllImport(SDL)] static extern ushort SDL_GetGamepadProduct(IntPtr gp);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadSerial(IntPtr gp);
    [DllImport(SDL)] static extern IntPtr SDL_GetGamepadName(IntPtr gp);
    [DllImport(SDL)] static extern short SDL_GetGamepadAxis(IntPtr gp, int axis);
    [DllImport(SDL)] static extern int SDL_GetNumGamepadTouchpads(IntPtr gp);
    [DllImport(SDL)] static extern void SDL_free(IntPtr mem);

    static int s_fail;
    static void Check(string what, bool ok, string detail = "")
    {
        if (!ok) s_fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(detail.Length > 0 ? "  " + detail : "")}");
    }

    static string? FindStockSdl()
    {
        string root = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && root.Length > 3; i++)
        {
            string sib = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                root, "..", "SDL3-build", "build-stock", "Release", "SDL3.dll"));
            if (System.IO.File.Exists(sib)) return sib;
            root = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, ".."));
        }
        return null;
    }

    /// <summary>Every SDL gamepad matching a VID/PID, with its serial.</summary>
    static List<(uint Id, ushort Pid, string Serial, int Pads)> ValvePads()
    {
        var found = new List<(uint, ushort, string, int)>();
        SDL_UpdateGamepads();
        IntPtr arr = SDL_GetGamepads(out int n);
        if (arr == IntPtr.Zero) return found;
        try
        {
            for (int j = 0; j < n; j++)
            {
                uint id = (uint)Marshal.ReadInt32(arr, j * 4);
                IntPtr gp = SDL_OpenGamepad(id);
                if (gp == IntPtr.Zero) continue;
                if (SDL_GetGamepadVendor(gp) == 0x28DE)
                    found.Add((id, SDL_GetGamepadProduct(gp),
                               Marshal.PtrToStringUTF8(SDL_GetGamepadSerial(gp)) ?? "",
                               SDL_GetNumGamepadTouchpads(gp)));
                SDL_CloseGamepad(gp);
            }
        }
        finally { SDL_free(arr); }
        return found;
    }

    /// <summary>Polls SDL's Valve pads every 100 ms until
    /// <paramref name="done"/> holds or <paramref name="limitMs"/> passes,
    /// and returns the last list read. SDL learns of a device from Windows'
    /// interface arrival and removal broadcasts (SDL_hidapi.c
    /// ControllerWndProc, HIDAPI_UpdateDiscovery), which SDL_UpdateGamepads
    /// on the joystick thread dispatches, so the list changes as soon as
    /// Windows announces the device.</summary>
    static List<(uint Id, ushort Pid, string Serial, int Pads)> PollPads(
        Func<List<(uint Id, ushort Pid, string Serial, int Pads)>, bool> done, int limitMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var pads = ValvePads();
            if (done(pads) || sw.ElapsedMilliseconds >= limitMs) return pads;
            Thread.Sleep(100);
        }
    }

    static int Main()
    {
        string? dll = FindStockSdl();
        if (dll == null) { Console.WriteLine("no stock SDL"); return 2; }
        NativeLibrary.SetDllImportResolver(typeof(ValveMultiCheck).Assembly,
            (name, asm, path) => name == SDL ? NativeLibrary.Load(dll) : IntPtr.Zero);

        // The Steam client claims Valve devices exclusively, so beside it a
        // persona SDL cannot see is Steam's doing, not the SDK's. S54 shuts
        // down a Steam it started, so this skips only beside a Steam the
        // machine was already running. Its web helper and service open no
        // controller, so neither one alone skips the scenario.
        if (System.Diagnostics.Process.GetProcessesByName("steam").Length > 0)
        {
            Console.WriteLine("  [SKIP] steam.exe is running. Steam claims Valve devices "
                              + "exclusively, so SDL cannot open a persona Steam holds.");
            return 2;
        }

        SDL_SetHint("SDL_JOYSTICK_HIDAPI", "1");
        SDL_SetHint("SDL_JOYSTICK_HIDAPI_STEAM", "1");
        SDL_SetHint("SDL_JOYSTICK_HIDAPI_STEAMDECK", "1");
        SDL_SetHint("SDL_JOYSTICK_RAWINPUT", "0");
        SDL_SetHint("SDL_JOYSTICK_THREAD", "1");
        if (!SDL_Init(0x00002000u)) { Console.WriteLine("SDL_Init failed"); return 1; }

        using var ctx = new HMContext();
        ctx.LoadDefaultProfiles();

        // ── A: three different personas at once ──────────────────────────
        Console.WriteLine("-- A: Deck + 2015 + Triton, all live together --");
        var live = new List<HMController>();
        try
        {
            foreach (var id in new[] { "steam-deck-composite", "steam-controller-composite", "steam-controller-2" })
            {
                var prof = ctx.GetProfile(id);
                if (prof == null) { Check($"{id} in catalog", false); continue; }
                live.Add(ctx.CreateController(prof));
                Console.WriteLine($"     created {id}");
                // Each persona reaches SDL before the next is made, as the
                // 1.5 s this waited gave it time to.
                int made = live.Count;
                PollPads(p => p.Count >= made, 1500);
            }
            var pads = PollPads(p => p.Count >= 3, 20_000);
            foreach (var p in pads)
                Console.WriteLine($"     SDL: 28DE:{p.Pid:X4} serial='{p.Serial}' touchpads={p.Pads}");
            Check("SDL sees all three at the same time", pads.Count >= 3, $"count={pads.Count}");
            var pidSet = new HashSet<ushort>();
            foreach (var p in pads) pidSet.Add(p.Pid);
            Check("all three distinct models are present", pidSet.Count >= 3,
                  string.Join(",", pidSet));
        }
        catch (Exception ex) { Check("three personas coexist", false, ex.Message); }
        finally
        {
            foreach (var c in live) { try { c.Dispose(); } catch { } }
            live.Clear();
            // B counts Decks, so A's must have left SDL's list first.
            PollPads(p => p.Count == 0, 3000);
        }

        // ── B: two of the SAME persona ───────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("-- B: two Steam Decks at once (serial-collision case) --");
        try
        {
            var prof = ctx.GetProfile("steam-deck-composite");
            if (prof == null) { Check("deck in catalog", false); }
            else
            {
                for (int i = 0; i < 2; i++)
                {
                    live.Add(ctx.CreateController(prof));
                    Console.WriteLine($"     created deck #{i + 1}");
                    int made = live.Count;
                    PollPads(p => p.Count >= made, 2000);
                }
                var pads = PollPads(p => p.Count >= 2, 20_000);
                foreach (var p in pads)
                    Console.WriteLine($"     SDL: 28DE:{p.Pid:X4} serial='{p.Serial}' touchpads={p.Pads}");
                Check("SDL sees two Decks at once", pads.Count >= 2, $"count={pads.Count}");

                var serials = new HashSet<string>();
                foreach (var p in pads) serials.Add(p.Serial);
                Check("the two Decks carry DISTINCT serials", serials.Count >= 2,
                      string.Join(" | ", serials));
            }
        }
        catch (Exception ex) { Check("two Decks coexist", false, ex.Message); }
        finally
        {
            foreach (var c in live) { try { c.Dispose(); } catch { } }
            PollPads(p => p.Count == 0, 2000);
        }

        SDL_Quit();
        Console.WriteLine();
        Console.WriteLine(s_fail == 0 ? "=== MULTI OK ===" : $"=== {s_fail} FAILED ===");
        return s_fail == 0 ? 0 : 1;
    }
}
