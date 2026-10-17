using System.Collections.Generic;
using System.Linq;

namespace HIDMaestro.Internal.Programs;

/// <summary>Every program program_equivalence_check runs besides the
/// catalog's, in one list the build tool emits kernels for (mode probe) and
/// the check reads at run time from the same sources.</summary>
internal static class ProbeCorpus
{
    public static IEnumerable<ProgramSpec> ValidPrograms(int seed) =>
        Corpus.Draw(seed)
            .Concat(ReviewCases.All.Take(3))
            .Concat(Round5Cases.ValidBesideTemplate)
            .Concat(Round5Cases.Edges)
            .Append(Round5Cases.U)
            .Concat(Round5Cases.PathMutations.Select(m => m.program))
            .Concat(SemanticsCases.Samples().Select(x => x.program))
            .Append(SemanticsCases.HatDiagonal)
            .Append(SemanticsCases.HatEdges);

    public static IEnumerable<(string Cls, ProgramSpec Program, PathMutation Gen, InterpMutation Interp)> PathMutations =>
        Round5Cases.PathMutations.Select(m => (m.cls, m.program, m.gen, m.interp));

    /// <summary>The program the inlining check plants a NoInlining kernel
    /// for. Its bytes do not change, so it is not a path mutation.</summary>
    public static ProgramSpec NotInlined => ReviewCases.All[0];
}
