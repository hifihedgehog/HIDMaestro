// program-file <profile file> [runs] [reports]: one launch of the programs
// of every profile a user's file loads, on the interpreter, measured by the
// profile check tool's own code (tools\HIDMaestroProfileCheck\InterpreterCost.cs),
// so the battery and a JSON author measure a file one way. The file loads
// as the SDK loads its directory. Each line is "program-file,<program>,<ns
// per report>", the launch's median of its runs, with the compile of each
// program and of each whole model in microseconds beside it, for a script
// to take p50 and p95 over 30 launches. Offline: no device, no elevation.
#if HM_PROGRAM_FILE
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using HIDMaestro.Internal;

internal static partial class Program
{
    static int ProgramFile(string file, int runs, int reports)
    {
        if (!File.Exists(file))
        {
            Console.WriteLine($"program-file: {file} does not exist");
            return 1;
        }
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High;
        var db = ProfileDatabase.Load(Path.GetDirectoryName(file)!);
        var mine = db.All.Where(p => string.Equals(p.SourceFile, file, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var issue in db.Issues.Where(i => string.Equals(i.File, file, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"# {issue}");
        if (mine.Count == 0)
        {
            Console.WriteLine($"program-file: no profile loaded from {file}");
            return 1;
        }
        InterpreterCost.Run(mine, runs, reports, (kind, name, value) => Console.WriteLine(kind switch
        {
            "cost" => FormattableString.Invariant($"program-file,{name},{value:F3}"),
            "compile" => FormattableString.Invariant($"program-file-compile,{name},{value:F2}"),
            "model" => FormattableString.Invariant($"program-file-model,{name},{value:F2}"),
            _ => "# " + name,
        }));
        return 0;
    }
}
#endif
