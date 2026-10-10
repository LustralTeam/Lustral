using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Lustral.Interop;

namespace Lustral.Generator;

/// <summary>
/// Precompiles interop assemblies to native code with crossgen2 (ReadyToRun), so that the game does not JIT-compile the
/// interop code at startup.
/// </summary>
/// <remarks>
/// crossgen2 matches the runtime that Lustral ships (<c>LustralDotNetRuntimeVersion</c>). An assembly that crossgen2 cannot
/// compile keeps its IL and is JIT-compiled as before, so this step can only improve startup time, never break it.
/// </remarks>
internal static class ReadyToRun
{
    // Its Initialize() is a single method with about 22,000 calls, which crossgen2 does not complete; Lustral reads only
    // its IL.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "Il2CppInterop.Initialization.dll" };

    private static readonly TimeSpan PerAssemblyTimeout = TimeSpan.FromMinutes(5);

    // crossgen2 processes end with the Generator, including when the user cancels from the progress window.
    private static readonly KillOnCloseJob? s_job = KillOnCloseJob.TryCreate();

    public static string CrossgenPath => Path.Combine(AppContext.BaseDirectory, "crossgen2", "crossgen2.exe");

    /// <param name="progress">Called with the number of completed assemblies and the total, as each assembly completes.</param>
    /// <returns>The number of assemblies compiled.</returns>
    public static int Compile(string directory, int jobs, Action<int, int>? progress = null)
    {
        if (!File.Exists(CrossgenPath))
            throw new FileNotFoundException("crossgen2 was not found next to the Generator; rebuild Lustral.Generator", CrossgenPath);

        // Inputs also serve as references for each other, so outputs are written to a separate folder and replace the IL
        // only at the end.
        var output = Path.Combine(directory, ".r2r");
        Directory.CreateDirectory(output);

        string[] references =
        [
            Path.Combine(directory, "*.dll"),
            Path.Combine(AppContext.BaseDirectory, "Il2CppInterop.*.dll"),
            Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"),
        ];

        // Largest first, so that a large assembly does not start last while the other workers are idle.
        var inputs = Directory.GetFiles(directory, "*.dll")
            .Where(path => !Skipped.Contains(Path.GetFileName(path)))
            .OrderByDescending(path => new FileInfo(path).Length)
            .ToList();

        var compiled = new ConcurrentBag<string>();
        var finished = 0;
        progress?.Invoke(0, inputs.Count);
        Parallel.ForEach(inputs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, jobs) }, input =>
        {
            var target = Path.Combine(output, Path.GetFileName(input));
            var error = Run(input, target, references);
            if (error is null)
                compiled.Add(input);
            else
                Console.WriteLine($"[ReadyToRun] {Path.GetFileName(input)} remains IL: {error}");
            progress?.Invoke(Interlocked.Increment(ref finished), inputs.Count);
        });

        foreach (var input in compiled)
            File.Move(Path.Combine(output, Path.GetFileName(input)), input, overwrite: true);
        Directory.Delete(output, recursive: true);
        return compiled.Count;
    }

    /// <returns>Null on success; otherwise, the reason for the failure.</returns>
    private static string? Run(string input, string target, string[] references)
    {
        var start = new ProcessStartInfo(CrossgenPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var reference in references)
        {
            start.ArgumentList.Add("-r");
            start.ArgumentList.Add(reference);
        }
        foreach (var argument in (ReadOnlySpan<string>)["--targetos:windows", "--targetarch:x64", "-O", input, "-o", target])
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("crossgen2 failed to start");
        s_job?.Add(process);
        try
        {
            // Generation may run while the game is being played, so it runs at a lower priority.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(PerAssemblyTimeout))
        {
            process.Kill(entireProcessTree: true);
            return $"timed out after {PerAssemblyTimeout.TotalMinutes:F0} minutes";
        }

        if (process.ExitCode == 0 && File.Exists(target))
            return null;

        var message = (stderr.Result + stdout.Result).Trim().Split('\n').LastOrDefault()?.Trim();
        return $"crossgen2 exited with {process.ExitCode}: {message}";
    }
}
