using Lustral.Core.Logging;
using Tomlyn;
using Tomlyn.Model;

namespace Lustral.Core.Config;

/// <summary>The action taken when a new version of a mod with an update source is available.</summary>
public enum UpdateMode
{
    /// <summary>Download the new version in the background and install it the next time the game starts.</summary>
    Auto,

    /// <summary>Report the new version in the log only.</summary>
    Notify,

    /// <summary>Do not check for updates.</summary>
    Off,
}

/// <summary>
/// <c>Lustral/loader.toml</c>: the user's settings for this game, created with defaults on first run.
/// Invalid entries never prevent startup: they are reported and the defaults are used instead.
/// </summary>
public sealed class LoaderConfig
{
    public const string FileName = "loader.toml";

    public const string DefaultText = """
        # Lustral configuration for this game. Changes take effect the next time the game starts.

        [log]
        # Minimum level written to Lustral/Logs/latest.log: "debug", "info", "warn" or "error".
        level = "info"
        # Show the log in a console window while the game is running.
        console = false

        [mods]
        # IDs of mods that should not be loaded, for example ["sxxr0.betterhud"]. Mods that depend on them are also skipped.
        disabled = []
        # IDs of mods to load even if their requirements are not met (a different game, game version, platform or
        # Lustral version) or they are incompatible with another installed mod. Forced mods may not work correctly.
        force = []

        [updates]
        # Update behavior for mods that declare an update source:
        #   "auto"    download new versions in the background and install them on the next launch;
        #             a version that fails to load is rolled back
        #   "notify"  report new versions in the log only
        #   "off"     do not check for updates
        # Mods and their updates are third-party software. Lustral does not verify their behavior and is not
        # responsible for any damage they cause. Only install mods from sources you trust.
        mode = "auto"
        # Also install pre-release versions.
        prereleases = false
        # Per-mod update modes, for example { "sxxr0.betterhud" = "notify" }.
        mods = {}
        """;

    public LogLevel LogLevel { get; init; } = LogLevel.Info;

    public IReadOnlyCollection<string> DisabledMods { get; init; } = [];

    /// <summary>IDs of mods to load regardless of their requirements and incompatibilities.</summary>
    public IReadOnlyCollection<string> ForcedMods { get; init; } = [];

    /// <summary>The update mode for all mods, unless overridden for a mod in <see cref="ModUpdates"/>.</summary>
    public UpdateMode Updates { get; init; } = UpdateMode.Auto;

    public IReadOnlyDictionary<string, UpdateMode> ModUpdates { get; init; } = new Dictionary<string, UpdateMode>();

    public bool PrereleaseUpdates { get; init; }

    public UpdateMode UpdatesFor(string modId) => ModUpdates.TryGetValue(modId, out var mode) ? mode : Updates;

    public bool Console { get; init; }

    /// <summary>Reads the file, creating it with the default settings if it does not exist.</summary>
    /// <param name="problems">Receives the problems found in the file; each affected setting falls back to its default.</param>
    public static LoaderConfig Load(string lustralDirectory, out List<string> problems)
    {
        problems = [];
        var path = Path.Combine(lustralDirectory, FileName);
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(lustralDirectory);
                File.WriteAllText(path, DefaultText.Replace("\n", Environment.NewLine));
            }
            return Parse(File.ReadAllText(path), problems);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{FileName} could not be read ({e.Message}); using the defaults");
            return new LoaderConfig();
        }
    }

    public static LoaderConfig Parse(string toml, List<string> problems)
    {
        var document = Toml.Parse(toml, FileName);
        if (document.HasErrors)
        {
            problems.Add($"{FileName} is not valid TOML ({document.Diagnostics.First(d => d.Kind == Tomlyn.Syntax.DiagnosticMessageKind.Error)}); using the defaults");
            return new LoaderConfig();
        }

        var table = document.ToModel();
        var level = LogLevel.Info;
        var console = false;
        if (Section(table, "log", problems) is { } log)
        {
            if (log.TryGetValue("level", out var levelValue))
            {
                if (levelValue is string text && Enum.TryParse<LogLevel>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(typeof(LogLevel), parsed))
                    level = parsed;
                else
                    problems.Add($"[log] level = {Describe(levelValue)} must be \"debug\", \"info\", \"warn\" or \"error\"; using \"info\"");
            }
            if (log.TryGetValue("console", out var consoleValue))
            {
                if (consoleValue is bool show)
                    console = show;
                else
                    problems.Add($"[log] console = {Describe(consoleValue)} must be true or false; the console is disabled");
            }
        }

        var disabled = new HashSet<string>(StringComparer.Ordinal);
        var forced = new HashSet<string>(StringComparer.Ordinal);
        if (Section(table, "mods", problems) is { } mods)
        {
            if (mods.TryGetValue("disabled", out var disabledValue))
            {
                if (disabledValue is TomlArray array && array.All(x => x is string))
                    disabled.UnionWith(array.Cast<string>());
                else
                    problems.Add("[mods] disabled must be a list of mod IDs, for example [\"sxxr0.betterhud\"]; no mods are disabled");
            }
            if (mods.TryGetValue("force", out var forceValue))
            {
                if (forceValue is TomlArray array && array.All(x => x is string))
                    forced.UnionWith(array.Cast<string>());
                else
                    problems.Add("[mods] force must be a list of mod IDs, for example [\"sxxr0.betterhud\"]; no mods are forced");
            }
        }

        var updates = UpdateMode.Auto;
        var prereleases = false;
        var modUpdates = new Dictionary<string, UpdateMode>(StringComparer.Ordinal);
        if (Section(table, "updates", problems) is { } updatesSection)
        {
            if (updatesSection.TryGetValue("mode", out var modeValue))
            {
                if (TryParseMode(modeValue, out var mode))
                    updates = mode;
                else
                    problems.Add($"[updates] mode = {Describe(modeValue)} must be \"auto\", \"notify\" or \"off\"; using \"auto\"");
            }
            if (updatesSection.TryGetValue("prereleases", out var prereleaseValue))
            {
                if (prereleaseValue is bool allowed)
                    prereleases = allowed;
                else
                    problems.Add($"[updates] prereleases = {Describe(prereleaseValue)} must be true or false; pre-releases are excluded");
            }
            if (updatesSection.TryGetValue("mods", out var modsValue))
            {
                if (modsValue is TomlTable perMod)
                {
                    foreach (var pair in perMod)
                    {
                        if (TryParseMode(pair.Value, out var mode))
                            modUpdates[pair.Key] = mode;
                        else
                            problems.Add($"[updates] mods: \"{pair.Key}\" = {Describe(pair.Value)} must be \"auto\", \"notify\" or \"off\"; using [updates] mode");
                    }
                }
                else
                    problems.Add("[updates] mods must be a table, for example { \"sxxr0.betterhud\" = \"notify\" }; using [updates] mode for all mods");
            }
        }

        return new LoaderConfig
        {
            LogLevel = level,
            DisabledMods = disabled,
            ForcedMods = forced,
            Console = console,
            Updates = updates,
            ModUpdates = modUpdates,
            PrereleaseUpdates = prereleases,
        };
    }

    private static bool TryParseMode(object? value, out UpdateMode mode)
    {
        mode = UpdateMode.Auto;
        return value is string text && Enum.TryParse(text, ignoreCase: true, out mode) && Enum.IsDefined(typeof(UpdateMode), mode);
    }

    private static TomlTable? Section(TomlTable table, string name, List<string> problems)
    {
        if (!table.TryGetValue(name, out var value))
            return null;
        if (value is TomlTable section)
            return section;
        problems.Add($"\"{name}\" must be a [{name}] section; using its defaults");
        return null;
    }

    private static string Describe(object? value) => value is string text ? $"\"{text}\"" : value?.ToString() ?? "nothing";
}
