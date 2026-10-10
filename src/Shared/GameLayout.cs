namespace Lustral.Interop;

/// <summary>
/// The locations of the files in a Unity IL2CPP game on which the interop cache depends. Compiled into both the
/// Generator and the IL2CPP backend, so that both use the same locations.
/// </summary>
internal sealed record GameLayout(string GameDirectory, string ExecutablePath, string DataDirectory)
{
    public string GameAssemblyPath => Path.Combine(GameDirectory, "GameAssembly.dll");

    public string MetadataPath => Path.Combine(DataDirectory, "il2cpp_data", "Metadata", "global-metadata.dat");

    public string LustralDirectory => Path.Combine(GameDirectory, "Lustral");

    public string InteropDirectory => Path.Combine(LustralDirectory, "interop");

    /// <summary>The Generator that Lustral installs in the game folder; it runs on the runtime in <c>Lustral/dotnet</c>.</summary>
    public string GeneratorPath => Path.Combine(LustralDirectory, "generator", "Lustral.Generator.exe");

    /// <summary>The game's display name: the product name that Unity writes to <c>app.info</c>, or else the executable's name.</summary>
    public string DisplayName
    {
        get
        {
            try
            {
                // app.info contains the company name, followed by the product name.
                var lines = File.ReadAllLines(Path.Combine(DataDirectory, "app.info"));
                if (lines.Length > 1 && !string.IsNullOrWhiteSpace(lines[1]))
                    return lines[1].Trim();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return Path.GetFileNameWithoutExtension(ExecutablePath);
        }
    }

    public static GameLayout FromExecutable(string executablePath)
    {
        var fullPath = Path.GetFullPath(executablePath);
        var gameDirectory = Path.GetDirectoryName(fullPath)!;
        var dataDirectory = Path.Combine(gameDirectory, Path.GetFileNameWithoutExtension(fullPath) + "_Data");
        return new GameLayout(gameDirectory, fullPath, dataDirectory);
    }

    /// <summary>Finds the single <c>&lt;name&gt;.exe</c> that has a matching <c>&lt;name&gt;_Data</c> folder.</summary>
    public static GameLayout FromDirectory(string gameDirectory)
    {
        var candidates = Directory.GetDirectories(gameDirectory, "*_Data")
            .Select(data => Path.Combine(gameDirectory, Path.GetFileName(data)[..^"_Data".Length] + ".exe"))
            .Where(File.Exists)
            .ToList();

        return candidates.Count switch
        {
            1 => FromExecutable(candidates[0]),
            0 => throw new FileNotFoundException($"No Unity game found in {gameDirectory}: expected <name>.exe next to <name>_Data"),
            _ => throw new InvalidOperationException($"More than one Unity game found in {gameDirectory}: {string.Join(", ", candidates.Select(Path.GetFileName))}"),
        };
    }
}
