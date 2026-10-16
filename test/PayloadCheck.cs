using System;
using System.IO;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace HIDMaestroTest;

partial class Program
{
    // ── verify-payload ──
    //
    // verify-payload <HIDMaestro.Core.dll> <build-dir>: whether an SDK build
    // carries the driver the build directory holds. PackResources
    // (HIDMaestro.Core.csproj) copies build\HIDMaestro.dll, HMXInput.dll,
    // hidmaestro.inf, hidmaestro_xusb.inf and hmswd.exe into the assembly
    // unchanged, as the manifest resources HIDMaestro.Native.x64.<name>
    // DriverBuilder extracts. The assembly is loaded into a collectible
    // context of its own and none of its code runs. Prints each file's
    // SHA-256 as embedded and as built. Read-only. Exit 0 when all five
    // match byte for byte, 1 otherwise.

    static readonly string[] s_payloadNames =
        { "HIDMaestro.dll", "HMXInput.dll", "hidmaestro.inf", "hidmaestro_xusb.inf", "hmswd.exe" };

    static int VerifyPayload(string[] args)
    {
        if (args.Length != 2) return Error("Usage: verify-payload <HIDMaestro.Core.dll> <build-dir>");
        string dll = Path.GetFullPath(args[0]);
        string build = Path.GetFullPath(args[1]);
        if (!File.Exists(dll)) return Error($"{dll} does not exist");
        Console.WriteLine($"=== payload of {dll} against {build} ===");

        var context = new AssemblyLoadContext("verify-payload", isCollectible: true);
        int bad = 0;
        try
        {
            var asm = context.LoadFromAssemblyPath(dll);
            foreach (string name in s_payloadNames)
            {
                string resource = "HIDMaestro.Native.x64." + name;
                byte[]? embedded = null;
                using (var s = asm.GetManifestResourceStream(resource))
                {
                    if (s != null)
                    {
                        using var ms = new MemoryStream();
                        s.CopyTo(ms);
                        embedded = ms.ToArray();
                    }
                }
                string onDisk = Path.Combine(build, name);
                byte[]? built = File.Exists(onDisk) ? File.ReadAllBytes(onDisk) : null;
                bool same = embedded != null && built != null && embedded.AsSpan().SequenceEqual(built);
                if (!same) bad++;
                Console.WriteLine($"  [{(same ? "SAME" : "DIFF")}] {name}  embedded {Sha(embedded)}  built {Sha(built)}");
            }
        }
        finally
        {
            context.Unload();
        }
        Console.WriteLine(bad == 0 ? "verify-payload: the SDK carries the build directory's driver" : $"verify-payload: {bad} file(s) differ");
        return bad == 0 ? 0 : 1;
    }

    static string Sha(byte[]? b) => b == null ? "(absent)" : Convert.ToHexString(SHA256.HashData(b));
}
