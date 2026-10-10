using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Lustral.Core.GameData;

namespace Lustral.Core.Mods;

/// <summary>
/// The environment that a mod's <c>[requires]</c> section is checked against: the Lustral version, the game and the
/// system. Requirements that cannot be checked because a value is unknown, such as a game version that cannot be read
/// yet, do not prevent the mod from loading; the check passes and the unchecked requirements are reported.
/// </summary>
/// <param name="Backend">"mono" or "il2cpp".</param>
/// <param name="ProcessName">The game's executable name without ".exe".</param>
public sealed record ModEnvironment(
    SemanticVersion? LustralVersion,
    string Backend,
    PlatformTarget Platform,
    string ProcessName,
    string? GameName,
    string? GameDeveloper,
    string? GameVersion,
    string? UnityVersion)
{
    /// <summary>The platform of the current process.</summary>
    public static PlatformTarget CurrentPlatform => new(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos" : "linux",
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        });

    /// <summary>
    /// Returns why the mod cannot run in this environment, phrased to complete "Not loading &lt;mod&gt;: ...", or null if
    /// it can.
    /// </summary>
    /// <param name="unchecked">Receives the requirements that could not be checked because a value is unknown, phrased to complete "Loading &lt;mod&gt; without checking ...".</param>
    public string? Check(ModManifest manifest, ICollection<string> @unchecked)
    {
        var requires = manifest.Requires;

        if (requires.Lustral is { } lustral)
        {
            if (LustralVersion is not { } version)
                @unchecked.Add($"the required Lustral version ({lustral}): the installed Lustral version is unknown");
            else if (!lustral.IsSatisfiedBy(version))
                return $"requires Lustral {lustral}; the installed version is {version}";
        }

        if (requires.Backends.Count > 0 && !requires.Backends.Contains(Backend))
            return $"is built for {Join(requires.Backends.Select(BackendName))} games; this game uses {BackendName(Backend)}";

        if (requires.Platforms.Count > 0 && !requires.Platforms.Any(p => p.OperatingSystem == Platform.OperatingSystem && (p.Architecture is null || p.Architecture == Platform.Architecture)))
            return $"requires {Join(requires.Platforms.Select(p => p.ToString()))}; this system is {Platform}";

        if (requires.Processes.Count > 0 && !requires.Processes.Any(p => string.Equals(p, ProcessName, StringComparison.OrdinalIgnoreCase)))
            return $"is for the executable {Join(requires.Processes)}; the running executable is {ProcessName}";

        if (requires.Unity is { } unity)
        {
            if (!UnityVersionNumber.TryParse(UnityVersion, out var unityVersion))
                @unchecked.Add($"the required Unity version ({unity}): the game's Unity version is unknown");
            else if (!unity.IsSatisfiedBy(unityVersion.ToSemanticVersion()))
                return $"requires Unity {unity}; the game uses Unity {UnityVersion}";
        }

        if (requires.Games.Count > 0)
            return CheckGame(requires.Games, @unchecked);
        return null;
    }

    private string? CheckGame(IReadOnlyList<GameTarget> games, ICollection<string> @unchecked)
    {
        if (GameName is null)
        {
            @unchecked.Add($"the supported games ({Join(games.Select(Describe))}): the game's name is unknown");
            return null;
        }

        var matches = games.Where(g => Same(g.Name, GameName) && (g.Developer is null || GameDeveloper is null || Same(g.Developer, GameDeveloper))).ToList();
        if (matches.Count == 0)
            return $"is for {Join(games.Select(Describe))}; the running game is {GameName}";

        var versions = matches.SelectMany(g => g.Versions).ToList();
        if (matches.Any(g => g.Versions.Count == 0) || versions.Count == 0)
            return null;
        if (GameVersion is null)
        {
            @unchecked.Add($"the supported game versions ({Join(versions)}): the version of {GameName} is not yet known");
            return null;
        }
        return versions.Any(v => VersionPattern(v).IsMatch(GameVersion))
            ? null
            : $"is for {GameName} {Join(versions)}; the installed version is {GameVersion}";
    }

    private static string Describe(GameTarget game) =>
        game.Developer is null ? game.Name : $"{game.Name} by {game.Developer}";

    /// <summary>
    /// Returns a suggestion to append to the reason from <see cref="Check"/> when the mod is not loaded, or an empty
    /// string. Mods for both backends are distributed as one package per backend, and the wrong one is easily chosen.
    /// </summary>
    public string Remedy(ModManifest manifest) =>
        manifest.Requires.Backends.Count > 0 && !manifest.Requires.Backends.Contains(Backend)
            ? $" (install the mod's {BackendName(Backend)} package instead, if it has one)"
            : "";

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // A game version pattern: matched literally, except that * matches any sequence of characters.
    private static Regex VersionPattern(string pattern) =>
        new("^" + string.Join(".*", pattern.Trim().Split('*').Select(Regex.Escape)) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string BackendName(string backend) => backend == "il2cpp" ? "IL2CPP" : "Mono";

    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count <= 1 ? string.Concat(list) : string.Join(", ", list.Take(list.Count - 1)) + " or " + list[list.Count - 1];
    }
}
