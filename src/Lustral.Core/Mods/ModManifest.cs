using System.Text.RegularExpressions;
using Lustral.Core.Logging;
using Lustral.Core.Updates;
using Tomlyn;
using Tomlyn.Model;

namespace Lustral.Core.Mods;

public enum ModKind
{
    Mod,
    Library,
}

/// <summary>
/// A parsed <c>mod.toml</c>. The SDK generates it from the project file, next to the mod's assembly; users do not edit
/// it. Unknown keys are ignored, so that manifests written by newer SDK versions can still be read; their
/// <c>lustral</c> requirement determines whether the installed Lustral version is supported.
/// </summary>
public sealed class ModManifest
{
    public const string FileName = "mod.toml";

    private static readonly Regex IdPattern = new("^[a-z0-9]+([._-][a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public required string Id { get; init; }
    public required string Name { get; init; }
    public required SemanticVersion Version { get; init; }
    public ModKind Kind { get; init; } = ModKind.Mod;

    /// <summary>The assembly containing the mod's entry point, relative to its folder. Optional for libraries.</summary>
    public string? Assembly { get; init; }

    /// <summary>The simple names of the assemblies that a library exposes to its dependents.</summary>
    public IReadOnlyList<string> Exports { get; init; } = [];

    public string? Description { get; init; }
    public IReadOnlyList<string> Authors { get; init; } = [];

    /// <summary>Acknowledgements, shown with the mod's details.</summary>
    public string? Credits { get; init; }

    /// <summary>An SPDX license expression, for example <c>MIT</c>.</summary>
    public string? License { get; init; }

    /// <summary>Named HTTP(S) links, such as <c>website</c>, <c>source</c> and <c>issues</c>.</summary>
    public IReadOnlyDictionary<string, string> Links { get; init; } = new Dictionary<string, string>();

    /// <summary>Among mods that do not depend on each other, lower values load first. Defaults to 0.</summary>
    public int LoadOrder { get; init; }

    /// <summary>The color of the mod's name in the console (a Unity color name or <c>#rrggbb</c>). Defaults to a color derived from the ID.</summary>
    public string? Color { get; init; }

    /// <summary>The mod's requirements: Lustral and Unity versions, backends, platforms, processes and games.</summary>
    public ModRequirements Requires { get; init; } = ModRequirements.None;

    /// <summary>Required mods, mapped to the accepted version ranges.</summary>
    public IReadOnlyDictionary<string, VersionRange> Dependencies { get; init; } = new Dictionary<string, VersionRange>();

    /// <summary>Optional mods, mapped to the accepted version ranges. If installed, they are loaded first.</summary>
    public IReadOnlyDictionary<string, VersionRange> OptionalDependencies { get; init; } = new Dictionary<string, VersionRange>();

    /// <summary>Incompatible mods and versions. If one of them is loaded, this mod is not.</summary>
    public IReadOnlyDictionary<string, VersionRange> Incompatibilities { get; init; } = new Dictionary<string, VersionRange>();

    /// <summary>The source of automatic updates, or null if none is specified.</summary>
    public UpdateSource? Update { get; init; }

    /// <exception cref="ManifestException">The text is not valid TOML or breaks a manifest rule.</exception>
    public static ModManifest Parse(string toml)
    {
        var document = Toml.Parse(toml, ModManifest.FileName);
        if (document.HasErrors)
            throw new ManifestException($"not valid TOML: {document.Diagnostics.First(d => d.Kind == Tomlyn.Syntax.DiagnosticMessageKind.Error)}");
        var table = document.ToModel();

        var id = RequiredString(table, "id");
        if (!IdPattern.IsMatch(id))
            throw new ManifestException($"id \"{id}\" must consist of lowercase letters and digits, separated by '.', '-' or '_'");

        var kind = OptionalString(table, "kind") switch
        {
            null or "mod" => ModKind.Mod,
            "library" => ModKind.Library,
            var other => throw new ManifestException($"kind \"{other}\" must be \"mod\" or \"library\""),
        };

        var assembly = OptionalString(table, "assembly");
        if (assembly is not null && (assembly.IndexOfAny(['/', '\\']) >= 0 || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            throw new ManifestException($"assembly \"{assembly}\" must be a .dll file name inside the mod's folder");
        if (assembly is null && kind == ModKind.Mod)
            throw new ManifestException("a mod must specify its entry assembly in \"assembly\"");

        var exports = StringArray(table, "exports").Select(StripDll).ToList();
        if (kind == ModKind.Library && exports.Count == 0)
            throw new ManifestException("a library must list the assemblies it exposes to dependents in \"exports\"");
        if (kind == ModKind.Mod && exports.Count > 0)
            throw new ManifestException("only libraries can have \"exports\"; set kind = \"library\"");

        var color = OptionalString(table, "color");
        if (color is not null && !RichText.TryParseColor(color, out _))
            throw new ManifestException($"color \"{color}\" must be a Unity color name (for example \"orange\") or \"#rrggbb\"");

        var dependencies = VersionRanges(table, "dependencies", id);
        var optional = VersionRanges(table, "optional-dependencies", id);
        var incompatibilities = VersionRanges(table, "incompatibilities", id);
        if (dependencies.Keys.Intersect(optional.Keys).FirstOrDefault() is { } both)
            throw new ManifestException($"{both} is in both \"dependencies\" and \"optional-dependencies\"");
        if (dependencies.Keys.Concat(optional.Keys).Intersect(incompatibilities.Keys).FirstOrDefault() is { } contradiction)
            throw new ManifestException($"{contradiction} is both a dependency and an incompatibility");

        return new ModManifest
        {
            Id = id,
            Name = OptionalString(table, "name") ?? id,
            Version = ParseVersion(RequiredString(table, "version")),
            Kind = kind,
            Assembly = assembly,
            Exports = exports,
            Description = OptionalString(table, "description"),
            Authors = StringOrStringArray(table, "authors"),
            Credits = OptionalString(table, "credits"),
            License = OptionalString(table, "license"),
            Links = ParseLinks(table),
            LoadOrder = OptionalInteger(table, "load-order") ?? 0,
            Color = color,
            Requires = ParseRequirements(table),
            Dependencies = dependencies,
            OptionalDependencies = optional,
            Incompatibilities = incompatibilities,
            Update = ParseUpdate(table),
        };
    }

    private static ModRequirements ParseRequirements(TomlTable root)
    {
        if (!root.TryGetValue("requires", out var value))
            return ModRequirements.None;
        if (value is not TomlTable table)
            throw new ManifestException("\"requires\" must be a [requires] table");

        var backends = StringArray(table, "backends", "requires.backends").ToList();
        if (backends.FirstOrDefault(b => b is not ("mono" or "il2cpp")) is { } backend)
            throw new ManifestException($"requires.backends: \"{backend}\" must be \"mono\" or \"il2cpp\"");

        var platforms = new List<PlatformTarget>();
        foreach (var text in StringArray(table, "platforms", "requires.platforms"))
        {
            platforms.Add(PlatformTarget.TryParse(text, out var platform)
                ? platform
                : throw new ManifestException($"requires.platforms: \"{text}\" must be windows, linux or macos, optionally with -x64, -x86 or -arm64"));
        }

        return new ModRequirements
        {
            Lustral = OptionalRange(table, "lustral"),
            Unity = OptionalRange(table, "unity"),
            Backends = backends,
            Platforms = platforms,
            Processes = StringArray(table, "processes", "requires.processes").Select(StripExe).ToList(),
            Games = ParseGames(table),
        };
    }

    private static List<GameTarget> ParseGames(TomlTable requires)
    {
        if (!requires.TryGetValue("games", out var value))
            return [];
        // Either [[requires.games]] tables or an array of inline tables.
        IEnumerable<object> entries = value switch
        {
            TomlTableArray tables => tables,
            TomlArray array => array.Cast<object>(),
            _ => throw new ManifestException("requires.games must be an array of tables with a name"),
        };
        var games = new List<GameTarget>();
        foreach (var entry in entries)
        {
            if (entry is not TomlTable game)
                throw new ManifestException("requires.games must be an array of tables with a name");
            var name = OptionalString(game, "name", "requires.games.name")
                ?? throw new ManifestException("each entry in requires.games requires a name: the game's product name (for example \"Lethal Company\")");
            games.Add(new GameTarget(name, OptionalString(game, "developer", "requires.games.developer"), StringArray(game, "versions", "requires.games.versions").ToList()));
        }
        return games;
    }

    private static UpdateSource? ParseUpdate(TomlTable root)
    {
        if (!root.TryGetValue("update", out var value))
            return null;
        if (value is not TomlTable table)
            throw new ManifestException("\"update\" must be an [update] table with a source");
        var text = OptionalString(table, "source", "update.source") ?? throw new ManifestException("[update] requires a source: github:owner/repository or the HTTPS address of a JSON feed");
        return UpdateSource.TryParse(text, OptionalString(table, "asset", "update.asset"), out var source, out var error)
            ? source
            : throw new ManifestException($"update.source: {error}");
    }

    private static Dictionary<string, string> ParseLinks(TomlTable table)
    {
        var links = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!table.TryGetValue("links", out var value))
            return links;
        if (value is not TomlTable section)
            throw new ManifestException("\"links\" must be a [links] table of name = \"https://...\"");
        foreach (var pair in section)
        {
            if (pair.Value is not string url || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ManifestException($"links.{pair.Key} must be an HTTP or HTTPS address");
            links[pair.Key] = url;
        }
        return links;
    }

    private static Dictionary<string, VersionRange> VersionRanges(TomlTable table, string key, string id)
    {
        var ranges = new Dictionary<string, VersionRange>(StringComparer.Ordinal);
        if (!table.TryGetValue(key, out var value))
            return ranges;
        if (value is not TomlTable section)
            throw new ManifestException($"\"{key}\" must be a table of id = \"version range\"");
        foreach (var pair in section)
        {
            if (pair.Value is not string text)
                throw new ManifestException($"{key}: \"{pair.Key}\" requires a version range string, for example \"^1.0.0\" or \"*\"");
            if (pair.Key == id)
                throw new ManifestException($"a mod cannot list itself in \"{key}\"");
            if (!VersionRange.TryParse(text, out var range, out var error))
                throw new ManifestException($"{key}: \"{pair.Key}\" has an invalid version range: {error}");
            ranges[pair.Key] = range;
        }
        return ranges;
    }

    private static VersionRange? OptionalRange(TomlTable table, string key)
    {
        var text = OptionalString(table, key, "requires." + key);
        if (text is null)
            return null;
        return VersionRange.TryParse(text, out var range, out var error)
            ? range
            : throw new ManifestException($"requires.{key} has an invalid version range: {error}");
    }

    private static SemanticVersion ParseVersion(string text) =>
        SemanticVersion.TryParse(text, out var version)
            ? version
            : throw new ManifestException($"version \"{text}\" is not a semantic version (for example 1.2.0)");

    private static string RequiredString(TomlTable table, string key) =>
        OptionalString(table, key) ?? throw new ManifestException($"\"{key}\" is required");

    private static string? OptionalString(TomlTable table, string key, string? path = null)
    {
        if (!table.TryGetValue(key, out var value))
            return null;
        if (value is not string text || text.Trim().Length == 0)
            throw new ManifestException($"\"{path ?? key}\" must be a non-empty string");
        return text;
    }

    private static int? OptionalInteger(TomlTable table, string key)
    {
        if (!table.TryGetValue(key, out var value))
            return null;
        return value is long number && number is >= int.MinValue and <= int.MaxValue
            ? (int)number
            : throw new ManifestException($"\"{key}\" must be an integer");
    }

    private static IEnumerable<string> StringArray(TomlTable table, string key, string? path = null)
    {
        if (!table.TryGetValue(key, out var value))
            return [];
        if (value is not TomlArray array || array.Any(x => x is not string s || s.Trim().Length == 0))
            throw new ManifestException($"\"{path ?? key}\" must be an array of strings");
        return array.Cast<string>();
    }

    // Hand-written manifests may specify a single author as a string.
    private static List<string> StringOrStringArray(TomlTable table, string key) =>
        table.TryGetValue(key, out var value) && value is string single ? [OptionalString(table, key)!] : StringArray(table, key).ToList();

    private static string StripDll(string name) =>
        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;

    private static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 4) : name;
}

/// <summary>A mod's requirements. An empty list or range imposes no requirement.</summary>
public sealed class ModRequirements
{
    public static ModRequirements None { get; } = new();

    /// <summary>The supported Lustral versions. The SDK writes the version the mod was built against and later compatible versions.</summary>
    public VersionRange? Lustral { get; init; }

    /// <summary>The supported Unity versions, compared as major.minor.patch (2022.3.62f2 is compared as 2022.3.62).</summary>
    public VersionRange? Unity { get; init; }

    /// <summary>The supported backends: "mono" and/or "il2cpp".</summary>
    public IReadOnlyList<string> Backends { get; init; } = [];

    public IReadOnlyList<PlatformTarget> Platforms { get; init; } = [];

    /// <summary>Executable names without ".exe", for games that ship more than one executable.</summary>
    public IReadOnlyList<string> Processes { get; init; } = [];

    /// <summary>The supported games; any game if empty.</summary>
    public IReadOnlyList<GameTarget> Games { get; init; } = [];
}

/// <summary>A supported game, identified by Unity's productName and companyName, as stored in <c>&lt;game&gt;_Data/app.info</c>.</summary>
/// <param name="Versions">The supported game versions (<c>Application.version</c>); <c>*</c> matches any sequence of characters. Any version if empty.</param>
public sealed record GameTarget(string Name, string? Developer, IReadOnlyList<string> Versions);

/// <summary>An operating system and optional processor architecture, for example <c>windows</c> or <c>linux-x64</c>.</summary>
public readonly record struct PlatformTarget(string OperatingSystem, string? Architecture)
{
    public static bool TryParse(string text, out PlatformTarget platform)
    {
        platform = default;
        var parts = text.Trim().ToLowerInvariant().Split('-');
        if (parts.Length > 2 || parts[0] is not ("windows" or "linux" or "macos"))
            return false;
        if (parts.Length == 2 && parts[1] is not ("x64" or "x86" or "arm64"))
            return false;
        platform = new PlatformTarget(parts[0], parts.Length == 2 ? parts[1] : null);
        return true;
    }

    public override string ToString() => Architecture is null ? OperatingSystem : $"{OperatingSystem}-{Architecture}";
}

public sealed class ManifestException(string message) : Exception(message);
