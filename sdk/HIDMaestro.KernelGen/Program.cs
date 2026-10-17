using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HIDMaestro.Internal;
using HIDMaestro.Internal.Programs;

// The build step. Modes:
//   catalog <out.g.cs> <inputs>  the SDK's kernels, from the profile and model
//                                files the inputs list names
//   probe <out.g.cs> <inputs>    program_equivalence_check's kernels: the
//                                catalog's, the seeded corpus, the review
//                                cases and the planted defects
//   check-file <file>            reads and validates one file and prints the
//                                result, as the run-time loader would
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: HIDMaestro.KernelGen catalog|probe <out.g.cs> <inputs> | check-file <file>");
    return 2;
}

switch (args[0])
{
    case "catalog":
    case "probe":
    {
        if (args.Length < 3) { Console.Error.WriteLine("catalog and probe take <out.g.cs> <inputs>"); return 2; }
        // The inputs file: its first line names the mode, every other line is
        // "path|sha256" for one profile or model file, written by the build
        // so a change of content, an added file or a removed one changes the
        // file this step depends on.
        var programs = new List<ValidProgram>();
        var sets = new List<ProgramSet>();
        int files = 0;
        var lines = File.ReadAllLines(args[2]);
        // The first line is the mode, then for the probe the corpus seed.
        string[] head = lines.Length > 0 ? lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        int seed = Corpus.Seed;
        if (head.Length > 1 && !int.TryParse(head[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seed))
        {
            Console.Error.WriteLine($"{args[2]}(1,1): error KG0002: the corpus seed '{head[1]}' is not a whole number from 0 to {int.MaxValue}");
            return 1;
        }
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) continue;
            int bar = line.LastIndexOf('|');
            string path = bar > 0 ? line[..bar] : line;
            files++;
            try
            {
                var model = ModelCompiler.Compile(ProfilePrograms.Read(File.ReadAllBytes(path)));
                programs.AddRange(model.AllPrograms);
                if (model.LayoutSet is { } set) sets.Add(set);
            }
            catch (FormException e)
            {
                // A catalog program that fails validation fails the build,
                // named by file and JSON path in MSBuild's error format.
                Console.Error.WriteLine($"{path}(1,1): error KG0001: {e.Path}: {e.Reason}");
                return 1;
            }
            catch (Exception e)
            {
                // Validation refuses every value the paths cannot take, so
                // this is a defect of the tool. It still fails the build
                // with the file named, in place of the tool's crash.
                Console.Error.WriteLine($"{path}(1,1): error KG0001: $: the tool failed on this file: {e.GetType().Name}: {e.Message}");
                return 1;
            }
        }
        string text;
        if (args[0] == "catalog")
        {
            text = Emitter.EmitFile(programs, sets, "HIDMaestro.Internal.Programs", "CatalogKernels");
        }
        else
        {
            foreach (var p in ProbeCorpus.ValidPrograms(seed))
                programs.Add(ProgramForm.Validate(p));
            var mutants = ProbeCorpus.PathMutations.Where(m => m.Gen != PathMutation.None)
                .Select(m => (ProgramForm.Validate(m.Program), m.Gen)).ToList();
            mutants.Add((ProgramForm.Validate(ProbeCorpus.NotInlined), PathMutation.NotInlined));
            text = Emitter.EmitFile(programs, sets, "HIDMaestro.Internal.Programs", "ProbeKernels", mutants)
                + "/// <summary>The seed the build drew the random programs from.</summary>\n"
                + "internal static partial class ProbeCorpusSeed\n{\n"
                + $"    public const int Value = {seed.ToString(System.Globalization.CultureInfo.InvariantCulture)};\n}}\n";
        }
        Write(args[1], text);
        Console.WriteLine($"HIDMaestro.KernelGen {args[0]}: {files} files, {programs.Select(p => p.Hash).Distinct().Count()} programs, {sets.Count} layout sets{(args[0] == "probe" ? $", corpus seed {seed}" : "")}");
        return 0;
    }
    case "check-file":
    {
        try
        {
            var model = ModelCompiler.Compile(ProfilePrograms.Read(File.ReadAllBytes(args[1])));
            Console.WriteLine($"{Path.GetFileName(args[1])}: {model.AllPrograms.Count()} programs valid");
        }
        catch (FormException e) { Console.WriteLine($"{Path.GetFileName(args[1])}: refused at {e.Path}: {e.Reason}"); }
        return 0;
    }
}
Console.Error.WriteLine($"unknown mode {args[0]}");
return 2;

static void Write(string path, string text)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, text);
}
