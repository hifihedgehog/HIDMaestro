using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace HIDMaestro.Internal;

/// <summary>
/// Stable hash of the embedded driver-install payload, used as a fast-path
/// short-circuit in <see cref="DriverBuilder.FullDeploy"/>. If the SHA-256
/// of the embedded INFs and binaries matches the value previously stored
/// at <c>HKLM\Software\HIDMaestro\InstalledManifestSha256</c> AND the
/// driver is actually present in the DriverStore per
/// <see cref="PnputilHelper.IsHidMaestroDriverInstalled"/>, the entire
/// extract + sign + catalog + install pipeline is skipped.
///
/// <para>Cost: SHA-256 over ~6 MB at first access, ~30 ms on a fast box,
/// ~150 ms on Atom. Cached as a static field, so subsequent calls are
/// free. Hashed-into set is the canonical install payload (not the SDK
/// resource manifest as a whole) so changes to e.g. signtool.exe — which
/// affects HOW we install but not WHAT we install — don't invalidate
/// the cache.</para>
/// </summary>
internal static class EmbeddedManifest
{
    /// <summary>Resources whose bytes determine "is the installed driver
    /// equivalent to what's embedded in this assembly?" Order is fixed
    /// so the hash is stable across builds with identical inputs.</summary>
    private static readonly string[] HashedResources = new[]
    {
        "HIDMaestro.Resources.HIDMaestro.dll",
        "HIDMaestro.Resources.HMXInput.dll",
        "HIDMaestro.Resources.hidmaestro.inf",
        "HIDMaestro.Resources.hidmaestro_xusb.inf",
        "HIDMaestro.Resources.hmswd.exe",
    };

    private static string? s_cachedHash;
    private static readonly object s_lock = new();

    /// <summary>SHA-256 hash (lowercase hex, 64 chars) of the embedded
    /// driver-install payload. Computed once per process; safe to call
    /// from any thread.</summary>
    public static string Sha256Hex
    {
        get
        {
            if (s_cachedHash != null) return s_cachedHash;
            lock (s_lock)
            {
                if (s_cachedHash != null) return s_cachedHash;
                s_cachedHash = ComputeHash();
                return s_cachedHash;
            }
        }
    }

    private static string ComputeHash()
    {
        var asm = typeof(EmbeddedManifest).Assembly;
        using var sha = SHA256.Create();

        // Hash the resource names as well as bytes so a rename (without
        // content change) still busts the cache.
        foreach (var name in HashedResources)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name + "\n");
            sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);

            using var s = asm.GetManifestResourceStream(name);
            if (s == null)
            {
                // Resource missing — produce a distinct hash so the
                // mismatch forces FullDeploy. Hash a marker rather than
                // throwing; missing-resource diagnostics happen later
                // in the pipeline at extraction time.
                byte[] missing = Encoding.UTF8.GetBytes("(missing)\n");
                sha.TransformBlock(missing, 0, missing.Length, null, 0);
                continue;
            }

            byte[] buf = new byte[64 * 1024];
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
                sha.TransformBlock(buf, 0, n, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var hex = new StringBuilder(sha.Hash!.Length * 2);
        foreach (byte b in sha.Hash!) hex.Append(b.ToString("x2"));
        return hex.ToString();
    }
}
