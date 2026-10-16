using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using HIDMaestro;

namespace HIDMaestroTest;

partial class Program
{
    // ── sdl-list ──
    //
    // sdl-list <out-dir> <SDL3.dll> [--control <id>] <profile-id>...: what a
    // game built on SDL 3.4.18 lists for each profile, with SDL's default
    // hints, in
    // device_oracle_check's golden format (case live-sdl-joysticks). The
    // SDL3.dll must be the official 3.4.18 release for Windows x64, the file
    // gc_sdl_check pins (native\build.ps1, $officialSha). Each profile is
    // created in turn and SDL is pumped as a game's frame loop pumps it
    // (SDL_PumpEvents, then the events dropped, as gc_sdl_check's
    // sdl_harness.cpp Pump does) until the joysticks that appeared since the
    // create hold still for 1.5 s, or 15 s pass with none. A joystick
    // present before the create, a real pad among them, is never recorded.
    // Streams sdl/<profile>/index<N>/joysticks: count (u32), then one
    // joystick per new device in SDL's id order, "<vid>:<pid>:type=<n>:
    // gamepad=<0|1>:<name>", the type an SDL_JoystickType
    // (SDL_joystick.h:124-137 at 3.4.18).
    //
    // The control profile (--control, xbox-360-wired by default) must be
    // among the profiles. It runs first, and SDL must list it: a run in
    // which SDL lists nothing at all would otherwise record "not listed"
    // for every profile and prove nothing (review finding 11). When SDL
    // does not list the control, the step fails and writes nothing. Once it
    // has, a profile SDL does not list is recorded with a count of 0.
    //
    // Creates controllers, so it refuses without elevation (exit 3, nothing
    // created). Exit 0 recorded, 1 a step failed or the control is missing
    // from the list, nothing written.

    const string SdlOfficialSha = "2B4B128D8D510CC2D442693CF848C050D700D43DC667AFE9E5FD82B0570D667E";
    const uint SDL_INIT_GAMEPAD = 0x00002000u;
    const string SdlLib = "SDL3";

    [DllImport(SdlLib)][return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_Init(uint flags);
    [DllImport(SdlLib)] static extern void SDL_Quit();
    [DllImport(SdlLib)] static extern int SDL_GetVersion();
    [DllImport(SdlLib)] static extern IntPtr SDL_GetRevision();
    [DllImport(SdlLib)] static extern IntPtr SDL_GetError();
    [DllImport(SdlLib)] static extern void SDL_PumpEvents();
    [DllImport(SdlLib)] static extern void SDL_FlushEvents(uint minType, uint maxType);
    [DllImport(SdlLib)] static extern IntPtr SDL_GetJoysticks(out int count);
    [DllImport(SdlLib)] static extern IntPtr SDL_GetJoystickNameForID(uint id);
    [DllImport(SdlLib)] static extern int SDL_GetJoystickTypeForID(uint id);
    [DllImport(SdlLib)] static extern ushort SDL_GetJoystickVendorForID(uint id);
    [DllImport(SdlLib)] static extern ushort SDL_GetJoystickProductForID(uint id);
    [DllImport(SdlLib)][return: MarshalAs(UnmanagedType.U1)] static extern bool SDL_IsGamepad(uint id);
    [DllImport(SdlLib)] static extern void SDL_free(IntPtr mem);

    const string SdlControlProfile = "xbox-360-wired";

    static readonly string[] s_sdlJoystickTypes =
        { "unknown", "gamepad", "wheel", "arcade-stick", "flight-stick", "dance-pad", "guitar", "drum-kit", "arcade-pad", "throttle" };

    static int SdlList(string[] args)
    {
        const string Usage = "Usage: sdl-list <out-dir> <SDL3.dll> [--control <id>] <profile-id>...";
        if (args.Length < 3) return Error(Usage);
        string outDir = args[0];
        string sdl = Path.GetFullPath(args[1]);
        string control = SdlControlProfile;
        var listed = new List<string>();
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] != "--control") { listed.Add(args[i]); continue; }
            if (++i >= args.Length) return Error(Usage);
            control = args[i];
        }
        if (!listed.Contains(control))
            return Error($"the control profile {control} is not among the profiles, so a run SDL lists nothing in would pass. Nothing was created");
        // The control first, so a run that cannot see it stops before the rest.
        string[] ids = new[] { control }.Concat(listed.Where(id => id != control)).ToArray();
        Console.WriteLine($"=== SDL 3.4.18's joystick list for {string.Join(", ", ids)}, recorded for device_oracle_check ===");
        if (!IsElevated())
        {
            Console.WriteLine("  [REFUSED] sdl-list creates controllers and needs elevation. Nothing was created");
            return 3;
        }
        if (!File.Exists(sdl)) return Error($"{sdl} does not exist");
        string sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(sdl)));
        if (sha != SdlOfficialSha) return Error($"{sdl} is SHA-256 {sha}, not the official SDL3 3.4.18 release's {SdlOfficialSha}");
        NativeLibrary.SetDllImportResolver(typeof(Program).Assembly,
            (name, asm, path) => name == SdlLib ? NativeLibrary.Load(sdl) : IntPtr.Zero);

        if (!SDL_Init(SDL_INIT_GAMEPAD)) return Error("SDL_Init: " + Marshal.PtrToStringUTF8(SDL_GetError()));
        using var rec = new LiveRecorder(outDir, "live-sdl-joysticks");
        try
        {
            int v = SDL_GetVersion();
            rec.Stream("sdl/version");
            rec.Out("version", System.Text.Encoding.ASCII.GetBytes($"{v / 1000000}.{v / 1000 % 1000}.{v % 1000}"));
            Console.WriteLine($"  SDL {v / 1000000}.{v / 1000 % 1000}.{v % 1000} ({Marshal.PtrToStringUTF8(SDL_GetRevision())})");

            using var ctx = new HMContext();
            ctx.LoadDefaultProfiles();
            ctx.InstallDriver();
            foreach (string id in ids)
            {
                var profile = ctx.GetProfile(id) ?? throw new InvalidOperationException($"no profile {id}");
                var before = new HashSet<uint>(SdlJoysticks());
                List<uint> fresh;
                using (var c = ctx.CreateController(profile))
                {
                    fresh = SdlWaitNew(before);
                    rec.Stream($"sdl/{id}/index{c.TransportIndex}/joysticks");
                    rec.OutU32("count", (uint)fresh.Count);
                    foreach (uint j in fresh)
                    {
                        int type = SDL_GetJoystickTypeForID(j);
                        string line = $"{SDL_GetJoystickVendorForID(j):x4}:{SDL_GetJoystickProductForID(j):x4}:type={type}:" +
                                      $"gamepad={(SDL_IsGamepad(j) ? 1 : 0)}:{Marshal.PtrToStringUTF8(SDL_GetJoystickNameForID(j))}";
                        rec.Out("joystick", System.Text.Encoding.UTF8.GetBytes(line));
                        Console.WriteLine($"  {id}: {line} ({(type >= 0 && type < s_sdlJoystickTypes.Length ? s_sdlJoystickTypes[type] : "?")})");
                    }
                    if (fresh.Count == 0) Console.WriteLine($"  {id}: SDL lists no new joystick in 15 s");
                    if (fresh.Count == 0 && id == control)
                        throw new InvalidOperationException($"SDL lists no joystick for the control profile {id}, so no profile's absence can be recorded");
                }
                // The next profile's list must not see this one leaving.
                var sw = Stopwatch.StartNew();
                while (SdlJoysticks().Any(fresh.Contains))
                {
                    if (sw.ElapsedMilliseconds > 15000)
                        throw new InvalidOperationException($"{id}: SDL still lists it 15 s after the dispose");
                    Thread.Sleep(50);
                }
            }
            rec.Save();
            Console.WriteLine($"  wrote {outDir}");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  [FAIL] {e.GetType().Name}: {e.Message}");
            return 1;
        }
        finally
        {
            SDL_Quit();
        }
    }

    /// <summary>One frame of a game's loop, then SDL's joystick ids.</summary>
    static List<uint> SdlJoysticks()
    {
        SDL_PumpEvents();
        SDL_FlushEvents(0, 0xFFFF);
        var list = new List<uint>();
        IntPtr p = SDL_GetJoysticks(out int n);
        if (p == IntPtr.Zero) return list;
        try
        {
            for (int i = 0; i < n; i++) list.Add((uint)Marshal.ReadInt32(p, i * 4));
        }
        finally { SDL_free(p); }
        return list;
    }

    /// <summary>The joysticks SDL lists that <paramref name="before"/> did
    /// not, once they hold still for 1.5 s, or none after 15 s.</summary>
    static List<uint> SdlWaitNew(HashSet<uint> before)
    {
        var sw = Stopwatch.StartNew();
        var last = new List<uint>();
        long stableSince = 0;
        while (sw.ElapsedMilliseconds < 15000)
        {
            var now = SdlJoysticks().Where(j => !before.Contains(j)).ToList();
            if (!now.SequenceEqual(last)) { last = now; stableSince = sw.ElapsedMilliseconds; }
            else if (last.Count > 0 && sw.ElapsedMilliseconds - stableSince >= 1500) return last;
            Thread.Sleep(16);
        }
        return last;
    }
}
