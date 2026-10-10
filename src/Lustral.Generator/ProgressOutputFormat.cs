using AsmResolver.DotNet;
using Cpp2IL.Core.Model.Contexts;
using Il2CppInterop.Generator;
using Cpp2IlLogger = Cpp2IL.Core.Logging.Logger;

namespace Lustral.Generator;

/// <summary>
/// Il2CppInterop's output format, extended to report the progress of writing the assemblies. Cpp2IL's <c>DoOutput</c> is
/// sealed and runs several long steps without callbacks, so this class tracks the verbose log line that starts each step
/// and counts method bodies within the longest step. If Cpp2IL changes the wording of a line, the bar waits for the next
/// recognized line.
/// </summary>
internal sealed class ProgressOutputFormat : AsmResolverDllOutputFormatBinding, IDisposable
{
    // The start of each step within the writing phase, based on the step timings Cpp2IL logs on BOMBANANA! (16.4 s in total).
    private static readonly (string Prefix, double Start)[] Steps =
    [
        ("Building stub assemblies", 0.00),
        ("Configuring inheritance and generics", 0.02),
        ("Adding fields, methods, properties, and events", 0.03),
        ("Adding custom attributes", 0.33),
        ("Filling method bodies", 0.41),
        ("Generating PE images", 0.75),
        ("Building and writing managed PE files", 0.95),
    ];

    private const int MethodBodiesStep = 4;
    private const int ReportEvery = 1024;

    private readonly GenerationProgress _progress;
    private int _methodCount;
    private int _filled;

    public ProgressOutputFormat(GenerationProgress progress)
    {
        _progress = progress;
        Cpp2IlLogger.VerboseLog += OnVerbose;
    }

    public override List<AssemblyDefinition> BuildAssemblies(ApplicationAnalysisContext context)
    {
        _methodCount = context.AllTypes.Sum(type => type.Methods.Count);
        return base.BuildAssemblies(context);
    }

    // Listens for the entire DoOutput call, because its last two steps run after BuildAssemblies.
    public void Dispose() => Cpp2IlLogger.VerboseLog -= OnVerbose;

    protected override void FillMethodBody(MethodDefinition methodDefinition, MethodAnalysisContext methodContext)
    {
        base.FillMethodBody(methodDefinition, methodContext);
        var filled = Interlocked.Increment(ref _filled);
        if (filled % ReportEvery == 0 && _methodCount > 0)
        {
            var start = Steps[MethodBodiesStep].Start;
            var end = Steps[MethodBodiesStep + 1].Start;
            _progress.Report(GenerationProgress.Phase.Writing, start + (end - start) * Math.Min(1, (double)filled / _methodCount));
        }
    }

    private void OnVerbose(string message, string source)
    {
        if (source != "DllOutput")
            return;
        foreach (var (prefix, start) in Steps)
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                _progress.Report(GenerationProgress.Phase.Writing, start);
                return;
            }
        }
    }
}
