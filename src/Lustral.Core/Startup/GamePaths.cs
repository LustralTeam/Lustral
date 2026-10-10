using System.Diagnostics;

namespace Lustral.Core.Startup;

/// <summary>
/// The locations of the game and of Lustral's folders. Lustral writes files only inside the game's folder.
/// </summary>
/// <param name="ExecutablePath">The game's executable.</param>
/// <param name="CoreDirectory">The folder containing Lustral's assemblies (<c>Lustral/core</c>).</param>
public sealed record GamePaths(string ExecutablePath, string CoreDirectory)
{
    public string GameDirectory => Path.GetDirectoryName(ExecutablePath)!;
    public string LustralDirectory => Path.Combine(GameDirectory, "Lustral");
    public string LogsDirectory => Path.Combine(LustralDirectory, "Logs");
    public string ReportsDirectory => Path.Combine(LustralDirectory, "Reports");
    public string ConfigDirectory => Path.Combine(LustralDirectory, "config");

    /// <summary>The folder for files that mods write at runtime, with one folder per mod (<c>Lustral/data/&lt;id&gt;</c>), preserved across mod updates.</summary>
    public string ModDataDirectory => Path.Combine(LustralDirectory, "data");
    public string ModsDirectory => Path.Combine(GameDirectory, "Mods");

    /// <summary>Data that Lustral stores between sessions and can recreate (<c>Lustral/cache</c>).</summary>
    public string CacheDirectory => Path.Combine(LustralDirectory, "cache");

    /// <summary>The game's version as reported on its last run, used when the version cannot be read from the files.</summary>
    public string LastRunFile => Path.Combine(CacheDirectory, "game.txt");

    /// <summary>Unity's <c>&lt;name&gt;_Data</c> folder next to the executable.</summary>
    public string DataDirectory => Path.Combine(GameDirectory, Path.GetFileNameWithoutExtension(ExecutablePath) + "_Data");

    /// <summary>Reads the game's path from the environment set by stage 0 or Doorstop, falling back to the current process.</summary>
    public static GamePaths Detect(string coreDirectory)
    {
        var executable = Environment.GetEnvironmentVariable("LUSTRAL_GAME_EXECUTABLE");
        if (string.IsNullOrEmpty(executable))
            executable = Environment.GetEnvironmentVariable("DOORSTOP_PROCESS_PATH");
        if (string.IsNullOrEmpty(executable))
        {
            using var process = Process.GetCurrentProcess();
            executable = process.MainModule?.FileName;
        }
        if (string.IsNullOrEmpty(executable))
            throw new InvalidOperationException("Cannot determine which game is running: DOORSTOP_PROCESS_PATH is not set");

        return new GamePaths(Path.GetFullPath(executable), Path.GetFullPath(coreDirectory));
    }
}
