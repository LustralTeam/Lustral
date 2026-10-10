using System.Diagnostics;
using System.Text;
using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Il2CppInterop.Generator;
using Lustral.Generator;
using Lustral.Interop;
using Cpp2IlLogger = Cpp2IL.Core.Logging.Logger;

const string Usage = """
    Usage: Lustral.Generator <game folder> [options]

    Generates Il2CppInterop V2 assemblies for an IL2CPP Unity game into <game folder>/Lustral/interop.

    Options:
      --output <dir>    Write the assemblies to <dir> instead.
      --no-unstrip      Do not restore members stripped from the game's build. By default, the Generator restores the
                        Unity API excluded from the build, using Unity's libraries for the game's exact version from
                        LustralTeam/Lustral.UnityLibraries, downloaded once per Unity version to
                        %LOCALAPPDATA%\Lustral\unity\<version>. If they are unavailable (offline, or the version has not
                        been published yet), it generates without unstripping and tries again one day later.
      --unstrip <dir>   Restore stripped members from the Unity libraries in <dir> instead.
      --unity-libraries <url or folder>
                        The source of Lustral's Unity libraries: a release download address, or a folder with the same
                        layout (<version>/manifest.json). Default: https://github.com/LustralTeam/Lustral.UnityLibraries/releases/download
      --no-r2r          Skip precompilation (ReadyToRun). The game then JIT-compiles the interop code at startup.
      --jobs <n>        Number of parallel crossgen2 processes for precompilation. Default: half the logical processors.
      --force           Regenerate even if the cache is up to date.
      --verbose         Show Cpp2IL's verbose log and the duration of each phase.
      --ui              Show a progress window with a "Play without mods" button. The game passes this option when it
                        starts the Generator because its interop assemblies are missing or outdated.

    Exit codes: 0 generated or already up to date, 1 failed, 2 invalid arguments, 3 the user chose to play without mods,
    4 kept the existing interop assemblies (up to date except for unstripping, which is still not possible).
    """;

const int Failed = 1, BadArguments = 2, Cancelled = 3, Kept = 4;

// The game reads the output through pipes; UTF-8 preserves non-ASCII paths.
if (Console.IsOutputRedirected)
    Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
if (Console.IsErrorRedirected)
    Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });

string? gameDirectory = null, output = null, unstripDirectory = null;
var librariesSource = UnityLibraries.DefaultSource;
bool force = false, verbose = false, readyToRun = true, ui = false, unstrip = true;
var jobs = Math.Max(1, Environment.ProcessorCount / 2);
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--output" when i + 1 < args.Length: output = args[++i]; break;
        case "--unstrip" when i + 1 < args.Length: unstripDirectory = args[++i]; break;
        case "--no-unstrip": unstrip = false; break;
        case "--unity-libraries" when i + 1 < args.Length: librariesSource = args[++i]; break;
        case "--no-r2r": readyToRun = false; break;
        case "--jobs" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n > 0: jobs = n; i++; break;
        case "--force": force = true; break;
        case "--verbose": verbose = true; break;
        case "--ui": ui = true; break;
        case "-h" or "--help": Console.WriteLine(Usage); return 0;
        case var arg when !arg.StartsWith('-') && gameDirectory is null: gameDirectory = arg; break;
        default: Console.Error.WriteLine($"Unknown argument: {args[i]}\n\n{Usage}"); return BadArguments;
    }
}

if (gameDirectory is null)
{
    Console.Error.WriteLine(Usage);
    return BadArguments;
}

GameLayout? game = null;
try
{
    game = GameLayout.FromDirectory(Path.GetFullPath(gameDirectory));
    if (!File.Exists(game.GameAssemblyPath) || !File.Exists(game.MetadataPath))
        throw new FileNotFoundException($"{Path.GetFileName(game.ExecutablePath)} is not an IL2CPP game: GameAssembly.dll or global-metadata.dat is missing.");
    if (unstripDirectory is not null && !Directory.Exists(unstripDirectory))
        throw new DirectoryNotFoundException($"Unstrip directory not found: {unstripDirectory}");

    var outputDirectory = Path.GetFullPath(output ?? game.InteropDirectory);
    var existing = InteropManifest.TryRead(outputDirectory);
    var staleReason = existing is null ? "no interop assemblies yet" : existing.StaleReason(game, InteropManifest.CurrentIl2CppInteropVersion);
    if (staleReason is null && !force)
    {
        Console.WriteLine($"Interop assemblies in {outputDirectory} are up to date. Use --force to regenerate.");
        return 0;
    }

    // Up to date except for unstripping, which was not possible last time; regenerates only once Unity's libraries are
    // available.
    if (staleReason == InteropManifest.UnstripRetryReason && !force && unstrip && unstripDirectory is null)
    {
        var outcome = UnityLibraries.Get(Cpp2IlApi.DetermineUnityVersion(null, game.DataDirectory).ToString(), librariesSource);
        if (outcome.Libraries is null)
        {
            existing!.UnstripProblem = outcome.Problem;
            existing.UnstripRetryUtc = outcome.TryAgainLater ? DateTime.UtcNow.AddDays(1) : null;
            existing.Write(outputDirectory);
            Console.WriteLine($"Kept the interop assemblies in {outputDirectory}; unstripping is still not possible: {outcome.Problem}");
            return Kept;
        }
        staleReason = "Unity's libraries for unstripping are now available";
    }

    Console.WriteLine($"Generating interop for {Path.GetFileName(game.ExecutablePath)}: {staleReason ?? "forced"}");
    Cpp2IlLogger.InfoLog += (message, source) => Console.WriteLine($"[{source}] {message.TrimEnd()}");
    Cpp2IlLogger.WarningLog += (message, source) => Console.WriteLine($"[{source}] warning: {message.TrimEnd()}");
    Cpp2IlLogger.ErrorLog += (message, source) => Console.Error.WriteLine($"[{source}] error: {message.TrimEnd()}");
    if (verbose)
        Cpp2IlLogger.VerboseLog += (message, source) => Console.WriteLine($"[{source}] {message.TrimEnd()}");

    var progress = new GenerationProgress(readyToRun, verbose);
    var layout = game;
    void Generate()
    {
        var stopwatch = Stopwatch.StartNew();
        progress.Report(GenerationProgress.Phase.Reading);
        var unityVersion = Cpp2IlApi.DetermineUnityVersion(null, layout.DataDirectory);
        var gameAssemblyHash = InteropManifest.HashFile(layout.GameAssemblyPath);
        var metadataHash = InteropManifest.HashFile(layout.MetadataPath);

        // Generates into a sibling folder and swaps it in at the end, so that a failed run never leaves a partial cache.
        var staging = outputDirectory + ".tmp";
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        EnumBaseTypeFix.Install();
        OverrideConstantValueFix.Install();
        TypeSizeFix.Install();
        var layers = Il2CppGame.GetDefaultProcessingLayers();
        layers.Insert(0, new EnumBaseTypeFix.CaptureLayer());
        layers.Insert(0, new TypeSizeFix.CaptureLayer());
        layers.Insert(0, new ConstraintIndicesFix(layout.MetadataPath));

        // Unstripping restores the Unity members excluded from the build, using the libraries for this exact Unity version.
        UnityLibraries? libraries = null;
        string? unstripProblem = null;
        DateTime? unstripRetry = null;
        if (unstripDirectory is not null)
        {
            libraries = UnityLibraries.FromDirectory(Path.GetFullPath(unstripDirectory));
        }
        else if (unstrip)
        {
            progress.Report(GenerationProgress.Phase.Reading, 0, "getting Unity's libraries");
            var outcome = UnityLibraries.Get(unityVersion.ToString(), librariesSource);
            (libraries, unstripProblem) = (outcome.Libraries, outcome.Problem);
            if (outcome.TryAgainLater)
                unstripRetry = DateTime.UtcNow.AddDays(1);
            progress.Report(GenerationProgress.Phase.Reading);
        }
        if (libraries is not null)
        {
            Console.WriteLine($"Unstripping with Unity's libraries from {libraries.Source}");
            layers.Insert(0, new UnityLibraries.Layer(libraries.Load()));
        }
        else
        {
            if (unstripProblem is not null)
                Console.WriteLine($"Not unstripping: {unstripProblem}{(unstripRetry is null ? "" : "; retrying in one day")}");
            layers.RemoveAll(layer => layer is UnstripProcessingLayer); // Without libraries, the layer would only log warnings.
        }
        Il2CppGame.Process(layout.ExecutablePath, GenerationProgress.WithMarkers(layers, progress), []);
        progress.Report(GenerationProgress.Phase.Writing);
        using (var output = new ProgressOutputFormat(progress))
            output.DoOutput(Cpp2IlApi.CurrentAppContext!, staging);
        Console.WriteLine($"Generated interop assemblies in {stopwatch.Elapsed.TotalSeconds:F1} s");

        var precompiled = 0;
        if (readyToRun)
        {
            var precompileStopwatch = Stopwatch.StartNew();
            progress.Report(GenerationProgress.Phase.Precompiling);
            precompiled = ReadyToRun.Compile(staging, jobs,
                (done, total) => progress.Report(GenerationProgress.Phase.Precompiling, done, total, $"{done} of {total}"));
            Console.WriteLine($"Precompiled {precompiled} assemblies (ReadyToRun, {jobs} jobs) in {precompileStopwatch.Elapsed.TotalSeconds:F1} s");
        }

        new InteropManifest
        {
            UnityVersion = unityVersion.ToString(),
            Il2CppInteropVersion = InteropManifest.CurrentIl2CppInteropVersion,
            GameAssemblyHash = gameAssemblyHash,
            MetadataHash = metadataHash,
            Unstripped = libraries is not null,
            UnityLibraries = libraries?.Source,
            UnstripProblem = unstripProblem,
            UnstripRetryUtc = unstripRetry,
            ReadyToRun = precompiled > 0,
            CreatedUtc = DateTime.UtcNow,
        }.Write(staging);

        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, recursive: true);
        Directory.Move(staging, outputDirectory);
        progress.Complete();

        var count = Directory.GetFiles(outputDirectory, "*.dll").Length;
        Console.WriteLine($"Wrote {count} assemblies for Unity {unityVersion} to {outputDirectory} in {stopwatch.Elapsed.TotalSeconds:F1} s");
    }

    if (!ui)
    {
        Generate();
        return 0;
    }
    if (ProgressDialog.Show(game.DisplayName, progress, Generate) == ProgressDialog.Outcome.Cancelled)
    {
        // Exiting also ends crossgen2, which runs in a kill-on-close job; the next run starts with a clean staging folder.
        Console.WriteLine("Stopped: the user chose to play without mods");
        return Cancelled;
    }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Generation failed: {e}");
    if (ui)
    {
        var name = game?.DisplayName ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(gameDirectory));
        var log = game is null ? "the console output" : Path.Combine(game.LustralDirectory, "Logs", "latest.log");
        ProgressDialog.ShowError(name, e.Message, log);
    }
    return Failed;
}
