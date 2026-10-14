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
    readonly BlockingCollection<string> _lines = new();
    public string Name { get; }
    public int Pid => _p.Id;
    /// <summary>The SDK assembly version the peer reported when it started.</summary>
    public string SdkVersion { get; private set; } = "";
    public bool Exited => _p.HasExited;

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
        _p.OutputDataReceived += (_, e) => { if (e.Data != null) _lines.Add(e.Data); else _lines.CompleteAdding(); };
        _p.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine($"    ({name} stderr) {e.Data}"); };
        _p.BeginOutputReadLine();
        _p.BeginErrorReadLine();
    }

    /// <summary>Start a peer and wait for its ready line.</summary>
    public static PeerProcess Start(string name, string? legacyVersion = null)
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
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"could not start {exe}");
        var peer = new PeerProcess(name, p);
        string ready = peer.Read(30_000);
        if (!ready.StartsWith("OK ready")) throw new InvalidOperationException($"{name} did not start: {ready}");
        peer.SdkVersion = Field(ready, "sdk") ?? "";
        Console.WriteLine($"    {name}: {ready}");
        return peer;
    }

    string Read(int timeoutMs)
    {
        try
        {
            return _lines.TryTake(out string? line, timeoutMs) ? line : $"ERR timeout after {timeoutMs} ms";
        }
        catch (InvalidOperationException) { return "ERR peer exited"; }
    }

    /// <summary>Send one command and wait for its reply line.</summary>
    public string Send(string command, int timeoutMs = 120_000)
    {
        if (_p.HasExited) return "ERR peer exited";
        _p.StandardInput.WriteLine(command);
        _p.StandardInput.Flush();
        string reply = Read(timeoutMs);
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

    public string Receive(string label, int timeoutMs = 120_000)
    {
        string reply = Read(timeoutMs);
        Console.WriteLine($"    {Name} <- {label}: {reply}");
        return reply;
    }

    /// <summary>TerminateProcess: no Dispose, no exit hook, no finally.</summary>
    public void Kill()
    {
        try { _p.Kill(entireProcessTree: false); } catch { }
        _p.WaitForExit(10_000);
    }

    /// <summary>Ask the peer to dispose and exit, and kill it if it does not.</summary>
    public void Dispose()
    {
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
