using System.Diagnostics;

namespace Lustral.Core.GameData;

/// <summary>The source of the game's version.</summary>
internal enum GameVersionSource
{
    /// <summary>Unknown: the game's Unity version is newer than Lustral's PlayerSettings layouts, and the game has not run with Lustral since its last update.</summary>
    Unknown,

    /// <summary>Read from the game's files with the exact PlayerSettings layout.</summary>
    Files,

    /// <summary>The value of <c>Application.version</c> the last time the game ran with the same data files.</summary>
    LastRun,
}

/// <summary>
/// The game's metadata, read from its files before Unity starts: the developer and product name
/// (<c>Application.companyName</c> and <c>productName</c>), the game's version (<c>Application.version</c>, the
/// PlayerSettings bundleVersion) and the Unity version. Each value is null if it could not be read.
/// </summary>
/// <param name="DataIdentity">Identifies the data file containing PlayerSettings (name, size and last write time); a version reported by the running game is recorded for this file.</param>
internal sealed record GameMetadata(string? Developer, string? Name, string? Version, string? UnityVersion, GameVersionSource VersionSource, string? DataIdentity)
{
    private const int PlayerSettingsClassId = 129;

    /// <summary>Reads the game's metadata. Never throws: values that cannot be read remain null, and the reasons are passed to <paramref name="debug"/>.</summary>
    /// <param name="dataDirectory">The game's <c>&lt;name&gt;_Data</c> folder.</param>
    /// <param name="gameDirectory">The folder containing the executable and UnityPlayer.dll.</param>
    /// <param name="lastRunFile">The file in which <see cref="RecordVersion"/> stores the version reported by the running game, or null to ignore it.</param>
    public static GameMetadata Read(string dataDirectory, string gameDirectory, string? lastRunFile, Action<string>? debug = null)
    {
        var (developer, name) = ReadAppInfo(dataDirectory, debug);
        string? version = null;
        string? unityVersion = null;
        string? identity = null;
        var source = GameVersionSource.Unknown;
        try
        {
            var path = DataFile(dataDirectory);
            if (path is null)
                debug?.Invoke("The game has no globalgamemanagers, mainData or data.unity3d; its version is unknown");
            else
            {
                identity = Identity(path);
                using var bytes = Path.GetFileName(path) == "data.unity3d" ? (ByteSource)UnityBundle.Open(path, "globalgamemanagers") : new ByteSource.FromFile(path);
                var file = SerializedFile.Read(bytes);
                unityVersion = NullIfEmpty(file.UnityVersion);
                if (file.FindObject(PlayerSettingsClassId) is not { } playerSettings)
                    debug?.Invoke("globalgamemanagers has no PlayerSettings; the game's version is unknown");
                else if (playerSettings.TypeHash is null || PlayerSettingsLayouts.Embedded.ForTypeHash(playerSettings.TypeHash) is not { } layout)
                    debug?.Invoke($"The PlayerSettings layout of Unity {file.UnityVersion} is not yet supported (newer than the known layouts)");
                else if (PlayerSettingsLayouts.TryRead(playerSettings.Data, layout, file.BigEndian) is not { } values)
                    debug?.Invoke($"PlayerSettings could not be read with its layout (Unity {layout.FirstVersion} and later)");
                else
                {
                    developer = NullIfEmpty(values.Company) ?? developer;
                    name = NullIfEmpty(values.Product) ?? name;
                    version = NullIfEmpty(values.Version);
                    source = GameVersionSource.Files;
                }
            }
        }
        catch (Exception e)
        {
            debug?.Invoke($"Could not read the game's data: {e.Message}");
        }

        if (source == GameVersionSource.Unknown && identity is not null && lastRunFile is not null && ReadLastRun(lastRunFile, identity, debug) is { } lastRun)
        {
            version = lastRun;
            source = GameVersionSource.LastRun;
        }

        unityVersion ??= UnityPlayerVersion(gameDirectory);
        return new GameMetadata(developer, name, version, unityVersion, source, identity);
    }

    /// <summary>
    /// Records the version reported by the running game (<c>Application.version</c>) for its current data files, so that
    /// the version is known before Unity starts on the next launch, even if it cannot be read from the files.
    /// </summary>
    public static void RecordVersion(string lastRunFile, string dataIdentity, string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lastRunFile)!);
        var temporary = lastRunFile + ".tmp";
        File.WriteAllText(temporary, string.Join("\n",
            "# The game's version (Application.version), as reported while running with Lustral, for the data file below.",
            "# Used when the version cannot be read from the game's files. This file can be deleted safely.",
            $"data = {dataIdentity}",
            $"version = {version.Replace("\n", " ")}",
            ""));
        if (File.Exists(lastRunFile))
            File.Delete(lastRunFile);
        File.Move(temporary, lastRunFile);
    }

    private static string? ReadLastRun(string lastRunFile, string identity, Action<string>? debug)
    {
        try
        {
            if (!File.Exists(lastRunFile))
                return null;
            var values = File.ReadAllLines(lastRunFile)
                .Where(line => !line.StartsWith("#", StringComparison.Ordinal) && line.Contains(" = "))
                .Select(line => line.Split([" = "], 2, StringSplitOptions.None))
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.Ordinal);
            if (values.TryGetValue("data", out var data) && data == identity && values.TryGetValue("version", out var version))
                return NullIfEmpty(version);
            debug?.Invoke("The game's data has changed since its version was last recorded; the version is unknown until the game is running");
            return null;
        }
        catch (Exception e)
        {
            debug?.Invoke($"Could not read {Path.GetFileName(lastRunFile)}: {e.Message}");
            return null;
        }
    }

    // globalgamemanagers contains PlayerSettings. Before Unity 5, it was named mainData; LZ4-compressed builds store it in
    // data.unity3d.
    private static string? DataFile(string dataDirectory) =>
        ((string[])["globalgamemanagers", "mainData", "data.unity3d"]).Select(n => Path.Combine(dataDirectory, n)).FirstOrDefault(File.Exists);

    private static string Identity(string path)
    {
        var info = new FileInfo(path);
        return FormattableString.Invariant($"{info.Name} {info.Length} {info.LastWriteTimeUtc.Ticks}");
    }

    // app.info contains the company name on the first line and the product name on the second. Unity writes it to every
    // player build.
    private static (string? Developer, string? Name) ReadAppInfo(string dataDirectory, Action<string>? debug)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "app.info");
            if (!File.Exists(path))
                return (null, null);
            var lines = File.ReadAllText(path).Split('\n').Select(l => l.Trim('\r', ' ', '﻿')).ToArray();
            return (lines.Length > 0 ? NullIfEmpty(lines[0]) : null, lines.Length > 1 ? NullIfEmpty(lines[1]) : null);
        }
        catch (Exception e)
        {
            debug?.Invoke($"Could not read app.info: {e.Message}");
            return (null, null);
        }
    }

    private static string? UnityPlayerVersion(string gameDirectory)
    {
        try
        {
            var path = Path.Combine(gameDirectory, "UnityPlayer.dll");
            // For example "2022.3.62f2 (7670c08855a9)"; the version is the first word.
            return File.Exists(path) ? NullIfEmpty(FileVersionInfo.GetVersionInfo(path).ProductVersion?.Split(' ')[0]) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrWhiteSpace(text) ? null : text!.Trim();
}
