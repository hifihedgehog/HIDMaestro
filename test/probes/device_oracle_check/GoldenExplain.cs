// GoldenCase's --explain (Explain.cs), and Adopt, in the probe only: the
// test app links Golden.cs, and the explainers read the SDK's internals,
// which only this probe sees.
using HIDMaestro;

internal sealed partial class GoldenCase
{
    /// <summary>When set, every case run also holds each output against
    /// the base dump in &lt;dir&gt;\&lt;case&gt;.bin (Explain.cs).</summary>
    public static string? ExplainDir { get; set; }
    ExplainSession? _explain;

    /// <summary>The layout of the current stream's outputs, for
    /// --explain. A case sets it after it starts a stream.</summary>
    public IExplainer? Explainer { get; set; }

    /// <summary>Chooses the explainer of each stream a case starts by
    /// name, for a case whose streams another reader starts (case 5's
    /// native harness output). An explainer a case sets after a stream
    /// starts takes its place.</summary>
    public System.Func<string, IExplainer?>? ExplainerFor { get; set; }

    /// <summary>The input of the outputs that follow, for --explain.</summary>
    public readonly ExplainInput Input = new();

    /// <summary>The state a case submits, and for a packer it calls itself
    /// the slot values it passes. Set before the outputs it makes.</summary>
    public void SetInput(in HMGamepadState state, double[]? slots = null)
    {
        Input.State = state;
        Input.Slots = slots;
        Input.Set = true;
    }

    /// <summary>Marks the next output as an input report the device built
    /// from the controller's frame (case 8's transfers).</summary>
    public void NextIsReport(bool report) => Input.Report = report;

    /// <summary>What --explain found, once the case has run.</summary>
    public ExplainSession? Explained => _explain;

    partial void ExplainStart()
    {
        if (ExplainDir != null) _explain = new ExplainSession(ExplainDir, Name);
    }

    partial void ExplainStream(string name)
    {
        Explainer = ExplainerFor?.Invoke(name);
        Input.Set = false;
        Input.Slots = null;
        Input.Report = false;
        _explain?.Stream(name);
    }

    partial void ExplainOut(string tag, System.ReadOnlySpan<byte> data)
    {
        _explain?.Out(tag, data, Explainer, Input);
        Input.Report = false;
    }

    partial void ExplainNote(string tag, System.ReadOnlySpan<byte> data) => _explain?.Note(tag, data, Explainer);

    partial void ExplainEnd() => _explain?.End();

    /// <summary>Appends another recording's streams, in its order, as
    /// though they were recorded here: case 1 records its subjects on
    /// threads of their own and adopts them in order. In the probe only,
    /// beside --explain, since a recording that explains or dumps takes
    /// each output as it comes, and neither saw the adopted ones.</summary>
    public void Adopt(GoldenCase part)
    {
        if (_explain != null || _dump != null)
            throw new System.InvalidOperationException($"Case {Name} explains or dumps its outputs, so it records them itself.");
        foreach (var s in part.Streams)
        {
            if (!_names.Add(s.Name)) throw new System.ArgumentException($"Case {Name} has stream {s.Name} twice.");
            Streams.Add(s);
        }
        _current = null;
    }
}
