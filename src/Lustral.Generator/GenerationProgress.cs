using System.Diagnostics;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Model.Contexts;

namespace Lustral.Generator;

/// <summary>
/// Generation progress for the progress dialog: a readable step description and the completed fraction of the run. The
/// generating thread reports progress, and the dialog polls it. Each phase is assigned the share of the run it took on
/// BOMBANANA! (measured with <c>--verbose</c>, which prints the timings), so that the bar moves at a roughly steady pace.
/// </summary>
internal sealed class GenerationProgress
{
    public enum Phase
    {
        Reading,
        Building,
        Writing,
        Precompiling,
    }

    // Shares of the entire run (BOMBANANA!: 3, 8, 17 and 10 s with 16 crossgen2 jobs). Without ReadyToRun, the other
    // phases fill the bar.
    private static readonly double[] Weights = [0.08, 0.21, 0.44, 0.27];

    private readonly Lock _lock = new();
    private readonly bool _readyToRun;
    private readonly bool _verbose;
    private readonly Stopwatch _phaseClock = new();
    private Phase? _phase;
    private string _step = "Starting";
    private double _fraction;

    public GenerationProgress(bool readyToRun, bool verbose)
    {
        _readyToRun = readyToRun;
        _verbose = verbose;
    }

    public (string Step, double Fraction) Current
    {
        get
        {
            lock (_lock)
                return (_step, _fraction);
        }
    }

    /// <summary>Reports that <paramref name="done"/> of <paramref name="total"/> units of <paramref name="phase"/> are complete.</summary>
    public void Report(Phase phase, int done = 0, int total = 1, string? detail = null) =>
        Report(phase, total <= 0 ? 0 : (double)done / total, detail);

    /// <summary>Reports that the fraction <paramref name="within"/> of <paramref name="phase"/> is complete.</summary>
    public void Report(Phase phase, double within, string? detail = null)
    {
        string? finished = null;
        lock (_lock)
        {
            if (_phase != phase)
            {
                if (_phase is { } previous)
                    finished = $"{Describe(previous)} took {_phaseClock.Elapsed.TotalSeconds:F1} s";
                _phase = phase;
                _phaseClock.Restart();
            }
            _fraction = Math.Max(_fraction, (Start(phase) + Math.Clamp(within, 0, 1) * Weight(phase)) / Total);
            _step = detail is null ? $"{Describe(phase)}…" : $"{Describe(phase)} ({detail})";
        }
        if (_verbose && finished is not null)
            Console.WriteLine($"[Lustral] {finished}");
    }

    /// <summary>Completes the bar; with <c>--verbose</c>, prints the duration of the last phase.</summary>
    public void Complete()
    {
        string? finished;
        lock (_lock)
        {
            finished = _phase is { } last ? $"{Describe(last)} took {_phaseClock.Elapsed.TotalSeconds:F1} s" : null;
            _fraction = 1;
            _step = "Done";
        }
        if (_verbose && finished is not null)
            Console.WriteLine($"[Lustral] {finished}");
    }

    public static string Describe(Phase phase) => phase switch
    {
        Phase.Reading => "Reading the game's code",
        Phase.Building => "Building mod support",
        Phase.Writing => "Writing mod support files",
        Phase.Precompiling => "Optimizing for faster startup",
        _ => phase.ToString(),
    };

    private double Weight(Phase phase) => phase == Phase.Precompiling && !_readyToRun ? 0 : Weights[(int)phase];

    private double Start(Phase phase) => Enumerable.Range(0, (int)phase).Sum(i => Weight((Phase)i));

    private double Total => Start(Phase.Precompiling) + Weight(Phase.Precompiling);

    /// <summary>
    /// Inserted between Cpp2IL's processing layers to report which layer runs next. Layers locate each other by type in the
    /// shared list, so the actual layers cannot be wrapped; markers placed between them leave them unchanged.
    /// </summary>
    public static List<Cpp2IlProcessingLayer> WithMarkers(List<Cpp2IlProcessingLayer> layers, GenerationProgress progress)
    {
        var marked = new List<Cpp2IlProcessingLayer>(layers.Count * 2 + 1);
        for (var i = 0; i < layers.Count; i++)
        {
            marked.Add(new Marker(progress, i, layers.Count));
            marked.Add(layers[i]);
        }
        marked.Add(new Marker(progress, layers.Count, layers.Count));
        return marked;
    }

    private sealed class Marker(GenerationProgress progress, int index, int count) : Cpp2IlProcessingLayer
    {
        public override string Name => "Lustral progress";
        public override string Id => $"lustral_progress_{index}";

        public override void Process(ApplicationAnalysisContext appContext, Action<int, int>? progressCallback = null) =>
            progress.Report(Phase.Building, index, count, index < count ? $"step {index + 1} of {count}" : null);
    }
}
