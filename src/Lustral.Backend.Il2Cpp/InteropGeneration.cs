using System.Diagnostics;
using System.Text;
using Lustral.Core;
using Lustral.Interop;

namespace Lustral.Backend.Il2Cpp;

/// <summary>
/// Runs Lustral.Generator when the game's interop assemblies are missing or outdated: on the first start and after each
/// game update. The game waits for it during <c>il2cpp_init</c>. The Generator shows a progress window, in which the user
/// can also choose to play without mods for this session. Its output is written to Lustral's log.
/// </summary>
internal static class InteropGeneration
{
    // Lustral.Generator's exit codes.
    private const int Cancelled = 3, Kept = 4;

    // The Generator and its crossgen2 processes end with the game, so closing the game during generation leaves no processes
    // behind.
    private static readonly KillOnCloseJob? s_job = KillOnCloseJob.TryCreate();

    public static void Run(GameLayout game, string reason, ILog log, ILog generatorLog)
    {
        var generator = game.GeneratorPath;
        if (!File.Exists(generator))
            throw new StartupException($"Interop assemblies must be generated ({reason}), but the Generator is not installed at {generator}. Reinstall Lustral.");

        log.Info($"Generating interop assemblies ({reason}); the game will continue once they are ready");
        var stopwatch = Stopwatch.StartNew();
        var start = new ProcessStartInfo(generator)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = game.GameDirectory,
        };
        start.ArgumentList.Add(game.GameDirectory);
        start.ArgumentList.Add("--ui");

        string? firstError = null;
        using var process = Process.Start(start) ?? throw new StartupException($"{generator} failed to start");
        s_job?.Add(process);
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                generatorLog.Debug(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data))
                return;
            generatorLog.Warn(e.Data);
            firstError ??= e.Data;
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();

        switch (process.ExitCode)
        {
            case 0:
                log.Info($"Generated interop assemblies in {stopwatch.Elapsed.TotalSeconds:F0} s");
                break;
            case Kept:
                log.Info("Kept the existing interop assemblies; unstripping is still not possible");
                break;
            case Cancelled:
                throw new StartupException("The user chose to play without mods while interop assemblies were being generated. Lustral will try again on the next start.");
            default:
                throw new StartupException($"Generating interop assemblies failed (exit code {process.ExitCode}): {firstError ?? "no error output"}");
        }
    }
}
