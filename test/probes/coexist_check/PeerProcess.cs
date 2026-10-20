// A program that embeds HIDMaestro, run as its own process and driven one
// command at a time. The current SDK's peer is this probe started with
// --peer. An older release's peer is the same Peer.cs compiled against
// that release's HIDMaestro.Core.dll (legacy\<version>\CoexistPeer.exe).
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal sealed class PeerProcess : IDisposable
{
    readonly Process _p;
    /// <summary>Each line with the Stopwatch timestamp of its arrival on
    /// the reader, so a check can order a reply against a step of its own.</summary>
    readonly BlockingCollection<(string Line, long At)> _lines = new();
    public string Name { get; }
    public int Pid => _p.Id;
    /// <summary>The SDK assembly version the peer reported when it started.</summary>
    public string SdkVersion { get; private set; } = "";
    public bool Exited => _p.HasExited;
    /// <summary>When the line the last Send or Receive returned arrived, as
    /// a Stopwatch timestamp, or 0 when it returned no line.</summary>
    public long LastArrival { get; private set; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    static extern uint SetErrorMode(uint mode);
    const uint SEM_FAILCRITICALERRORS = 0x0001, SEM_NOGPFAULTERRORBOX = 0x0002, SEM_NOOPENFILEERRORBOX = 0x8000;

    /// <summary>A peer, and every process it starts, inherits this error
    /// mode, so a crash or a loader failure ends in an exit code and never
    /// in a dialog on the desktop. The bits already set are kept.</summary>
    static PeerProcess() =>
        SetErrorMode(SetErrorMode(0) | SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);

    PeerProcess(string name, Process p)
    {
        Name = name;
        _p = p;
        _p.OutputDataReceived += (_, e) => { if (e.Data != null) _lines.Add((e.Data, Stopwatch.GetTimestamp())); else _lines.CompleteAdding(); };
        _p.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"    ({name} stderr) {e.Data}"); };
        _p.BeginOutputReadLine();
        _p.BeginErrorReadLine();
    }

    /// <summary>Start a peer and wait for its ready line. A
    /// <paramref name="timeoutScale"/> is the peer's
    /// HIDMAESTRO_TIMEOUT_SCALE, which otherwise it inherits. A
    /// <paramref name="tempDir"/> is its TMP and TEMP, so a release that
    /// stages under %TEMP% stages there.</summary>
    public static PeerProcess Start(string name, string? legacyVersion = null, string? timeoutScale = null, string? tempDir = null) =>
        Begin(name, legacyVersion, timeoutScale, tempDir).AwaitReady();

    /// <summary>Start a peer and return before its ready line, so the
    /// process starts while the caller does other work. Nothing is sent to
    /// it before <see cref="AwaitReady"/>.</summary>
    public static PeerProcess Begin(string name, string? legacyVersion = null, string? timeoutScale = null, string? tempDir = null)
    {
        string self = Environment.ProcessPath!;
        string exe = legacyVersion == null
            ? self
            : Path.Combine(AppContext.BaseDirectory, "legacy", legacyVersion, "CoexistPeer.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException($"peer binary not built: {exe}");
        var psi = new ProcessStartInfo(exe, legacyVersion == null ? "--peer" : "")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        if (timeoutScale != null) psi.Environment["HIDMAESTRO_TIMEOUT_SCALE"] = timeoutScale;
        if (tempDir != null) psi.Environment["TMP"] = psi.Environment["TEMP"] = tempDir;
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        return new PeerProcess(name, p);
    }

    /// <summary>Wait for the ready line of a peer <see cref="Begin"/>
    /// started, and print it.</summary>
    public PeerProcess AwaitReady()
    {
        string ready = Read(30_000);
        if (!ready.StartsWith("OK ready")) throw new InvalidOperationException($"{Name} did not start: {ready}");
        SdkVersion = Field(ready, "sdk") ?? "";
        Console.WriteLine($"    {Name}: {ready}");
        return this;
    }

    string Read(int timeoutMs)
    {
        LastArrival = 0;
        try
        {
            if (!_lines.TryTake(out var line, timeoutMs)) return $"ERR timeout after {timeoutMs} ms";
            LastArrival = line.At;
            return line.Line;
        }
        catch (InvalidOperationException) { return "ERR peer exited"; }
    }

    /// <summary>How long a reply is waited for by default: past the longest
    /// bound the SDK itself waits in a create or a sweep, the transport and
    /// deploy locks' 10 minutes, at the machine's HIDMAESTRO_TIMEOUT_SCALE.
    /// A slow machine whose device installs queue behind Windows' online
    /// driver searches finishes the command, and a peer that hangs still
    /// fails, later.</summary>
    internal static readonly int ReplyMs = (int)(660_000 * Scale());

    static double Scale()
    {
        string? raw = Environment.GetEnvironmentVariable("HIDMAESTRO_TIMEOUT_SCALE");
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)
               && v >= 0.1 && v <= 100.0 ? v : 1.0;
    }

    /// <summary>Send one command and wait for its reply line.</summary>
    public string Send(string command, int timeoutMs = -1)
    {
        if (_p.HasExited) return "ERR peer exited";
        _p.StandardInput.WriteLine(command);
        _p.StandardInput.Flush();
        string reply = Read(timeoutMs < 0 ? ReplyMs : timeoutMs);
        Console.WriteLine($"    {Name} <- {command}: {reply}");
        return reply;
    }

    /// <summary>Send without waiting, for commands two peers must run at
    /// the same moment. Collect the reply with <see cref="Receive"/>.</summary>
    public void Post(string command)
    {
        _p.StandardInput.WriteLine(command);
        _p.StandardInput.Flush();
    }

    public string Receive(string label, int timeoutMs = -1)
    {
        string reply = Read(timeoutMs < 0 ? ReplyMs : timeoutMs);
        Console.WriteLine($"    {Name} <- {label}: {reply}");
        return reply;
    }

    /// <summary>Wait for the peer to end on its own, after a command that
    /// exits it.</summary>
    public bool WaitExit(int timeoutMs) => _p.WaitForExit(timeoutMs);

    public int ExitCode => _p.ExitCode;

    /// <summary>TerminateProcess: no Dispose, no exit hook, no finally.</summary>
    public void Kill()
    {
        try { _p.Kill(entireProcessTree: false); } catch { }
        _p.WaitForExit(10_000);
    }

    bool _disposed;

    /// <summary>Ask the peer to dispose and exit, and kill it if it does not.</summary>
    public void Dispose()
    {
        // A section can end a peer early and leave the using to call this
        // again.
        if (_disposed) return;
        _disposed = true;
        if (!_p.HasExited)
        {
            try
            {
                _p.StandardInput.WriteLine("exit");
                _p.StandardInput.Flush();
            }
            catch { }
            if (!_p.WaitForExit(60_000)) Kill();
        }
        _p.Dispose();
    }

    /// <summary>A field from a reply line such as "OK create index=0 transport=0".</summary>
    public static string? Field(string reply, string name)
    {
        foreach (var part in reply.Split(' '))
            if (part.StartsWith(name + "=", StringComparison.Ordinal)) return part[(name.Length + 1)..];
        return null;
    }
}
