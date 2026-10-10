namespace Lustral;

/// <summary>A mod's identity, as declared in its manifest (<c>mod.toml</c>).</summary>
public sealed class ModInfo
{
    private readonly string? _dataDirectory;

    internal ModInfo(string id, string name, string version, string directory, IReadOnlyList<string>? authors = null, string? description = null, string? dataDirectory = null)
    {
        Id = id;
        Name = name;
        Version = version;
        Directory = directory;
        Authors = authors ?? [];
        Description = description;
        _dataDirectory = dataDirectory;
    }

    /// <summary>The mod's ID, for example <c>sxxr0.betterhud</c>.</summary>
    public string Id { get; }

    /// <summary>The mod's display name.</summary>
    public string Name { get; }

    /// <summary>The mod's version, for example <c>1.2.0</c>.</summary>
    public string Version { get; }

    /// <summary>
    /// The absolute path of the mod's folder, <c>Mods/&lt;id&gt;/</c>, which contains its assemblies and the files it ships.
    /// The folder is replaced when the mod is updated; files written at runtime belong in <see cref="DataDirectory"/>.
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// The absolute path of a folder for files the mod writes at runtime, such as caches and saved data:
    /// <c>Lustral/data/&lt;id&gt;/</c>. The folder is preserved when the mod is updated and is created on first access.
    /// Settings belong in <see cref="Mod.Config"/>.
    /// </summary>
    public string DataDirectory
    {
        get
        {
            var path = _dataDirectory ?? throw new InvalidOperationException($"{Id} has no data folder when not running in a game");
            System.IO.Directory.CreateDirectory(path);
            return path;
        }
    }

    /// <summary>The mod's authors, as listed in its manifest; empty if none are listed.</summary>
    public IReadOnlyList<string> Authors { get; }

    /// <summary>The mod's description, or null if its manifest has none.</summary>
    public string? Description { get; }

    /// <summary>Returns the name, version and ID as written in Lustral's log: <c>Better HUD 1.2.0 (sxxr0.betterhud)</c>.</summary>
    public override string ToString() => $"{Name} {Version} ({Id})";
}
