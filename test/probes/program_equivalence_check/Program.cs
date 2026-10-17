using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;

// program_equivalence_check: every report program three ways (the generated
// kernel, the interpreter and the naive evaluator), the validation rules and
// their JSON paths, and the binding of each catalog program to its kernel.
//   check (default)     every check below, the exit code 1 on any failure
//   user [dir]          the profiles of a directory, on the interpreter
//   parse-file <file>   one file through the run-time loader, printed as the
//                       build tool's check-file prints it
//   state-cost [runs] [calls]
//                       the budget table's codec reports as generated state
//                       programs, timed on perf_budget_check's encode states
//   inline              the generated programs inline into BuildNext, read
//                       from the JIT's listing of a child process
string mode = args.Length > 0 ? args[0] : "check";
if (mode == "inline-child") return InlineCheck.Child();
Console.WriteLine($"# {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}, " +
                  $"{(RuntimeFeature.IsDynamicCodeSupported ? "JIT" : "NativeAOT")}, corpus seed {ProbeCorpusSeed.Value}");
int failures = 0;
switch (mode)
{
    case "check":
        failures += Checks.Run();
        failures += CatalogChecks.Run();
        failures += StateChecks.Run();
        failures += RuleChecks.Run();
        failures += UserChecks.Run(UserChecks.DefaultDir());
        failures += InlineCheck.Run();
        break;
    case "state-cost":
        return StateCost.Run(args.Length > 1 && int.TryParse(args[1], out int costRuns) ? costRuns : 9,
                             args.Length > 2 && int.TryParse(args[2], out int costCalls) ? costCalls : 100_000);
    case "inline":
        failures += InlineCheck.Run();
        break;
    case "user":
        failures += UserChecks.Run(args.Length > 1 ? Path.GetFullPath(args[1]) : UserChecks.DefaultDir());
        break;
    case "parse-file":
        if (args.Length < 2) { Console.Error.WriteLine("parse-file takes <file>"); return 2; }
        Console.WriteLine(RuleChecks.ParseFile(args[1]));
        return 0;
    default:
        Console.Error.WriteLine($"unknown mode {mode}: check, user [dir] or parse-file <file>");
        return 2;
}
Console.WriteLine(failures == 0 ? "RESULT: PASS" : $"RESULT: {failures} FAILED");
return failures == 0 ? 0 : 1;
