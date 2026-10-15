using System;
using System.Collections.Generic;
using System.Globalization;

namespace HIDMaestro.Internal;

/// <summary>A <see cref="PortsSpec"/> checked and compiled once: where each
/// port's block sits, how its status byte is built, and the codec program
/// for its fields. The SDK composes reports from it and the USB/IP device
/// reads the same reports back through it, so both sides agree by
/// construction.
///
/// <para>A spec that does not fit its report, or whose parts overlap, is
/// refused when the controller is created rather than producing a device
/// that is half right.</para></summary>
internal sealed class PortLayout
{
    public int Count { get; }
    public int Offset { get; }
    public int Stride { get; }

    /// <summary>Offset of the status byte in a block, or -1 when the blocks
    /// have none.</summary>
    public int StatusByte { get; }

    /// <summary>Connection names in declaration order. The first is the
    /// empty port.</summary>
    public IReadOnlyList<string> Connections => _connectionNames;
    private readonly string[] _connectionNames;
    private readonly byte[] _connectionValues;

    public IReadOnlyList<string> Flags => _flagNames;
    private readonly string[] _flagNames;
    private readonly byte[] _flagValues;
    private readonly bool[] _flagDefaults;
    private readonly byte _flagMask;

    /// <summary>The data range of a block, inclusive, or -1 when the blocks
    /// declare none.</summary>
    public int DataLo { get; }
    public int DataHi { get; }
    public int DataLength => DataLo < 0 ? 0 : DataHi - DataLo + 1;

    public IReadOnlyList<string> Snapshots => _snapshots;
    private readonly string[] _snapshots;

    /// <summary>The block's fields as a spec of their own, so the codec
    /// compiles and caches them as it does a report's.</summary>
    public ExtendedReportSpec FieldSpec { get; }

    private PortLayout(PortsSpec spec, int reportSize, byte reportId, string where, bool output)
    {
        Count = spec.Count;
        Offset = spec.Offset;
        Stride = spec.Stride;
        // Long arithmetic, so a count or stride large enough to wrap an int
        // cannot pass for one that fits.
        if (Count < 1 || Stride < 1 || Offset < 0 || Offset + (long)Count * Stride > reportSize)
            throw new InvalidOperationException(
                $"{where}.ports: {Count} ports of {Stride} bytes from offset {Offset} do not fit a {reportSize}-byte report.");
        // The SDK writes an input report's ID at byte 0 and the device
        // reads it there, so an input block on byte 0 would write over it.
        // An output report reaches the SDK with byte 0 split off as the
        // ring's report ID on every path, ID or none, so an output block
        // on byte 0 is never read.
        if (Offset == 0 && output)
            throw new InvalidOperationException(
                $"{where}.ports.offset 0 puts port 1's block on byte 0, which every output path hands the SDK " +
                "as the report ID. The blocks start at byte 1 or later.");
        if (Offset == 0 && reportId != 0)
            throw new InvalidOperationException(
                $"{where}.ports.offset 0 puts port 1's block on byte 0, which holds report ID 0x{reportId:X2}. " +
                "The blocks start at byte 1 or later.");

        if (spec.Status != null)
        {
            StatusByte = spec.Status.Byte;
            if (StatusByte < 0 || StatusByte >= Stride)
                throw new InvalidOperationException($"{where}.ports.status.byte {StatusByte} is outside the {Stride}-byte block.");
            if (spec.Status.Connections == null || spec.Status.Connections.Count == 0)
                throw new InvalidOperationException($"{where}.ports.status declares no connections. The first is the empty port.");
            if (spec.Status.Connections.Count == 1)
                throw new InvalidOperationException(
                    $"{where}.ports.status declares 1 connection. The first is the empty port, and a port needs a second to hold a controller.");
            _connectionNames = new string[spec.Status.Connections.Count];
            _connectionValues = new byte[spec.Status.Connections.Count];
            for (int i = 0; i < _connectionNames.Length; i++)
            {
                var c = Need(spec.Status.Connections[i], $"{where}.ports.status.connections[{i}]");
                _connectionNames[i] = RequireName(c.Name, $"{where}.ports.status.connections[{i}]");
                _connectionValues[i] = ParseByte(c.Value, $"{where}.ports.status.connections[{i}].value");
            }
            RequireUnique(_connectionNames, $"{where}.ports.status.connections");

            var flags = spec.Status.Flags ?? new List<PortFlagSpec>();
            _flagNames = new string[flags.Count];
            _flagValues = new byte[flags.Count];
            _flagDefaults = new bool[flags.Count];
            for (int i = 0; i < flags.Count; i++)
            {
                Need(flags[i], $"{where}.ports.status.flags[{i}]");
                _flagNames[i] = RequireName(flags[i].Name, $"{where}.ports.status.flags[{i}]");
                _flagValues[i] = ParseByte(flags[i].Value, $"{where}.ports.status.flags[{i}].value");
                _flagDefaults[i] = flags[i].Default;
                _flagMask |= _flagValues[i];
            }
            RequireUnique(_flagNames, $"{where}.ports.status.flags");
            foreach (byte v in _connectionValues)
                if ((v & _flagMask) != 0)
                    throw new InvalidOperationException($"{where}.ports.status: a connection shares bits with a flag.");
            // A status byte tells its connection by value alone, the flags
            // aside, and no connection carries a flag's bits. So each value
            // differs from every other, the empty port's included, or a
            // port's connection could not be read back from its report.
            for (int i = 1; i < _connectionValues.Length; i++)
            {
                for (int k = 0; k < i; k++)
                {
                    if (_connectionValues[i] != _connectionValues[k]) continue;
                    string at = $"{where}.ports.status.connections[{i}].value 0x{_connectionValues[i]:X2}";
                    throw new InvalidOperationException(k == 0
                        ? $"{at} is the empty port's, connections[0]'s, so a port holding it would read as empty."
                        : $"{at} repeats connections[{k}]'s. Each connection needs its own status bits.");
                }
            }
        }
        else
        {
            StatusByte = -1;
            _connectionNames = Array.Empty<string>();
            _connectionValues = Array.Empty<byte>();
            _flagNames = Array.Empty<string>();
            _flagValues = Array.Empty<byte>();
            _flagDefaults = Array.Empty<bool>();
        }

        if (spec.Data != null)
        {
            if (!TryParseRange(spec.Data, out int lo, out int hi) || lo < 0 || hi >= Stride || lo > hi
                || (StatusByte >= lo && StatusByte <= hi))
                throw new InvalidOperationException(
                    $"{where}.ports.data \"{spec.Data}\" must be a range inside the {Stride}-byte block that leaves out the status byte.");
            DataLo = lo;
            DataHi = hi;
        }
        else
        {
            DataLo = -1;
            DataHi = -1;
        }

        var snaps = spec.Snapshots ?? new List<PortSnapshotSpec>();
        _snapshots = new string[snaps.Count];
        for (int i = 0; i < snaps.Count; i++)
        {
            Need(snaps[i], $"{where}.ports.snapshots[{i}]");
            _snapshots[i] = RequireName(snaps[i].Name, $"{where}.ports.snapshots[{i}]");
            if (!string.Equals(snaps[i].On, "connect", StringComparison.Ordinal))
                throw new InvalidOperationException($"{where}.ports.snapshots[{i}].on \"{snaps[i].On}\": only \"connect\" is defined.");
            if (StatusByte < 0)
                throw new InvalidOperationException($"{where}.ports.snapshots need a status byte to see a connection.");
        }
        RequireUnique(_snapshots, $"{where}.ports.snapshots");

        FieldSpec = new ExtendedReportSpec { ReportId = "", Size = Stride, Fields = spec.Fields! };
        // Compile and check now, so an unknown type, a button name the SDK
        // cannot express, or a field wider than the bytes left for it fails
        // here, at create, with the rest.
        VendorBlobProgram.Check(FieldSpec, Stride, $"{where}.ports", output, portBlock: true);
        var prog = VendorBlobProgram.Get(FieldSpec);
        for (int i = 0; i < prog.Fields.Length; i++)
        {
            ref readonly var f = ref prog.Fields[i];
            // Every byte the field writes, by its type's width, against the
            // block's data bytes and its status byte.
            var (lo, hi) = VendorBlobProgram.Extent(in f, Stride);
            if ((DataLo >= 0 && (lo < DataLo || hi > DataHi)) || (StatusByte >= lo && StatusByte <= hi))
                throw new InvalidOperationException(
                    $"{where}.ports.fields[{i}] ('{f.Src.Type}') at bytes {lo}-{hi} is outside the block's " +
                    (DataLo >= 0 ? $"data bytes {DataLo}-{DataHi}." : $"bytes other than the status byte {StatusByte}."));
        }
    }

    /// <summary>Names of the block's output fields, from their semantics,
    /// once <see cref="PrepareOutput"/> has checked them.</summary>
    public IReadOnlyList<string> OutputNames => _outputNames ?? (IReadOnlyList<string>)Array.Empty<string>();
    private string[]? _outputNames;
    // "port{n}.{name}" for OutputDecoded, per port then per field.
    private string[][]? _outputKeys;

    /// <summary>Check that every field of an output report's port block can
    /// be read as a whole number with a name, the form
    /// <see cref="ReadOutput"/> hands to the per-port events.</summary>
    public void PrepareOutput(string where)
    {
        if (_outputNames != null) return;
        var prog = VendorBlobProgram.Get(FieldSpec);
        var names = new string[prog.Fields.Length];
        for (int i = 0; i < names.Length; i++)
        {
            ref readonly var f = ref prog.Fields[i];
            if (string.IsNullOrEmpty(f.Src.Semantic))
                throw new InvalidOperationException($"{where}.ports.fields[{i}] needs a semantic, the name its value is reported under.");
            if (OutputWidth(f.Op) == 0 || f.B < 0)
                throw new InvalidOperationException(
                    $"{where}.ports.fields[{i}] type '{f.Src.Type}' at a single byte offset is needed. A port's output " +
                    "fields are whole numbers: uint8, uint8-axis, uint8-trigger, uint16-le, int16-le.");
            names[i] = f.Src.Semantic!;
        }
        RequireUnique(names, $"{where}.ports.fields semantics");
        var keys = new string[Count][];
        for (int p = 0; p < Count; p++)
        {
            keys[p] = new string[names.Length];
            for (int i = 0; i < names.Length; i++) keys[p][i] = $"port{p + 1}.{names[i]}";
        }
        _outputKeys = keys;
        _outputNames = names;
    }

    private static int OutputWidth(VendorBlobProgram.FieldOp op) => op switch
    {
        VendorBlobProgram.FieldOp.U8Const or VendorBlobProgram.FieldOp.U8Axis
            or VendorBlobProgram.FieldOp.U8Trigger => 1,
        VendorBlobProgram.FieldOp.U16 or VendorBlobProgram.FieldOp.I16 => 2,
        _ => 0,
    };

    /// <summary>Output field <paramref name="field"/> of one port's block,
    /// the block starting at byte 0 of <paramref name="block"/>. A
    /// single-byte field declaring bits reads those bits alone.</summary>
    public int ReadOutput(int field, ReadOnlySpan<byte> block)
    {
        ref readonly var f = ref VendorBlobProgram.Get(FieldSpec).Fields[field];
        switch (f.Op)
        {
            case VendorBlobProgram.FieldOp.U16:
                return block[f.B] | (block[f.B + 1] << 8);
            case VendorBlobProgram.FieldOp.I16:
                return (short)(block[f.B] | (block[f.B + 1] << 8));
            default:
            {
                int v = block[f.B];
                if (f.HasBits) v = (v >> f.BitLo) & ((1 << (f.BitHi - f.BitLo + 1)) - 1);
                return v;
            }
        }
    }

    /// <summary>The OutputDecoded key of a port's output field.</summary>
    public string OutputKey(int port, int field) => _outputKeys![port][field];

    /// <summary>The compiled layout of <paramref name="spec"/>'s ports, or
    /// null when it has none. Built once per spec.</summary>
    public static PortLayout? Of(ExtendedReportSpec? spec, string where, bool output)
    {
        if (spec?.Ports == null) return null;
        if (spec.PortLayoutCache is PortLayout cached) return cached;
        var built = new PortLayout(spec.Ports, spec.Size, spec.ReportIdByte, where, output);
        spec.PortLayoutCache = built;
        return built;
    }

    /// <summary>Offset of a port's block in the report, 0-based port.</summary>
    public int BlockAt(int port) => Offset + port * Stride;

    public int ConnectionIndex(string name)
    {
        int i = Array.IndexOf(_connectionNames, name);
        if (i < 0)
            throw new ArgumentException(
                $"'{name}' is not a connection this device declares: {string.Join(", ", _connectionNames)}.", nameof(name));
        return i;
    }

    public int FlagIndex(string name)
    {
        int i = Array.IndexOf(_flagNames, name);
        if (i < 0)
            throw new ArgumentException(
                $"'{name}' is not a flag this device declares: {(_flagNames.Length == 0 ? "none" : string.Join(", ", _flagNames))}.", nameof(name));
        return i;
    }

    public bool FlagDefault(int i) => _flagDefaults[i];

    /// <summary>The status byte for a connection and the flags that are
    /// on.</summary>
    public byte Status(int connection, ReadOnlySpan<bool> flagsOn)
    {
        byte s = _connectionValues[connection];
        for (int i = 0; i < _flagValues.Length; i++)
            if (flagsOn[i]) s |= _flagValues[i];
        return s;
    }

    /// <summary>True when a status byte shows something other than the
    /// empty connection, whatever the flags.</summary>
    public bool HoldsController(byte status)
        => _connectionValues.Length > 0 && (byte)(status & ~_flagMask) != _connectionValues[0];

    internal static byte ParseByte(string? hex, string where)
    {
        if (hex == null)
            throw new InvalidOperationException($"{where} is missing. It takes a hex byte such as \"0x10\".");
        string s = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        if (!byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
            throw new InvalidOperationException($"{where} \"{hex}\" is not a hex byte.");
        return b;
    }

    internal static bool TryParseRange(string range, out int lo, out int hi)
    {
        lo = hi = -1;
        var parts = range.Split('-');
        if (parts.Length == 1 && int.TryParse(parts[0], out lo)) { hi = lo; return true; }
        return parts.Length == 2 && int.TryParse(parts[0], out lo) && int.TryParse(parts[1], out hi);
    }

    private static T Need<T>(T? value, string where) where T : class
        => value ?? throw new InvalidOperationException($"{where} is null.");

    private static string RequireName(string name, string where)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException($"{where} has no name.");
        return name;
    }

    private static void RequireUnique(string[] names, string where)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in names)
            if (!seen.Add(n)) throw new InvalidOperationException($"{where} names '{n}' twice.");
    }
}
