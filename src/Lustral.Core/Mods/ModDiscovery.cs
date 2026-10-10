namespace Lustral.Core.Mods;

public sealed record DiscoveredMod(ModManifest Manifest, string Directory)
{
    public string Id => Manifest.Id;
}

/// <summary>A mod that cannot be loaded, with a one-line reason shown to the user.</summary>
public sealed record ModProblem(string Mod, string Reason)
{
    public override string ToString() => $"{Mod}: {Reason}";
}

public static class ModDiscovery
{
    /// <summary>
    /// Reads <c>&lt;modsDirectory&gt;/*/mod.toml</c>. Invalid manifests are reported as problems, so that one invalid mod
    /// does not prevent others from loading. Common installation mistakes are reported as well: a folder extracted one
    /// level too deep, an archive that was not extracted, or an assembly outside a mod folder. Other folders without a
    /// manifest are skipped.
    /// </summary>
    public static (List<DiscoveredMod> Mods, List<ModProblem> Problems) Scan(string modsDirectory)
    {
        var mods = new List<DiscoveredMod>();
        var problems = new List<ModProblem>();
        if (!Directory.Exists(modsDirectory))
            return (mods, problems);

        foreach (var file in Directory.GetFiles(modsDirectory).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                problems.Add(new ModProblem(name, "is a zip archive; extract it so that the mod's folder is directly in Mods"));
            else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                problems.Add(new ModProblem(name, "is not in a mod folder; a Lustral mod is a folder in Mods that contains a mod.toml file (MelonLoader and BepInEx mods are not compatible with Lustral)"));
        }

        foreach (var directory in Directory.GetDirectories(modsDirectory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(directory, ModManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                if (Misplaced(directory) is { } reason)
                    problems.Add(new ModProblem(Path.GetFileName(directory), reason));
                continue;
            }

            try
            {
                var manifest = ModManifest.Parse(File.ReadAllText(manifestPath));
                mods.Add(new DiscoveredMod(manifest, Path.GetFullPath(directory)));
            }
            catch (Exception e) when (e is ManifestException or IOException or UnauthorizedAccessException)
            {
                problems.Add(new ModProblem(Path.GetFileName(directory), $"{ModManifest.FileName} {e.Message}"));
            }
        }

        return (mods, problems);
    }

    // Returns why a folder in Mods without a manifest appears to be an incorrectly installed mod, or null.
    private static string? Misplaced(string directory)
    {
        var name = Path.GetFileName(directory);
        if (name.StartsWith(".", StringComparison.Ordinal))
            return null;
        try
        {
            var inner = Directory.GetDirectories(directory)
                .Where(d => File.Exists(Path.Combine(d, ModManifest.FileName)))
                .Select(Path.GetFileName)
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToList();
            if (inner.Count > 0)
                return $"is nested one folder too deep; move {string.Join(" and ", inner.Select(d => $"{name}/{d}"))} into Mods";
            if (Directory.EnumerateFiles(directory, "*.dll").Any())
                return "has no mod.toml file and is not a Lustral mod (MelonLoader and BepInEx mods are not compatible with Lustral)";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}
