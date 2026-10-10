using System.Globalization;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Lustral.Core.Updates;

/// <summary>
/// The update state of each mod (<c>Lustral/updates/state.toml</c>): the time of the last check, the newest version
/// found, and versions that will not be installed again because they were incompatible or rolled back. Also read by
/// Lustral Hub. If the file is lost, the sources are simply checked again.
/// </summary>
internal sealed class UpdateState
{
    public Dictionary<string, ModUpdateState> Mods { get; } = new(StringComparer.Ordinal);

    public ModUpdateState For(string modId)
    {
        if (!Mods.TryGetValue(modId, out var state))
            Mods[modId] = state = new ModUpdateState();
        return state;
    }

    public static UpdateState Load(string path)
    {
        var state = new UpdateState();
        try
        {
            if (!File.Exists(path))
                return state;
            var document = Toml.Parse(File.ReadAllText(path), path);
            if (document.HasErrors || document.ToModel() is not { } root || !root.TryGetValue("mods", out var modsValue) || modsValue is not TomlTable mods)
                return state;
            foreach (var pair in mods)
            {
                if (pair.Value is not TomlTable table)
                    continue;
                var mod = state.For(pair.Key);
                if (table.TryGetValue("checked", out var checkedValue) && checkedValue is string checkedText &&
                    DateTime.TryParse(checkedText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var checkedAt))
                    mod.Checked = checkedAt;
                mod.Latest = table.TryGetValue("latest", out var latest) ? latest as string : null;
                mod.Notified = table.TryGetValue("notified", out var notified) ? notified as string : null;
                if (table.TryGetValue("skipped", out var skipped) && skipped is TomlArray array)
                    mod.Skipped.UnionWith(array.OfType<string>());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable; starts with an empty state.
        }
        return state;
    }

    public void Save(string path)
    {
        var text = new StringBuilder()
            .Append("# What Lustral knows about mods' updates; Lustral Hub reads it too. Deleting it only means checking again.\n");
        foreach (var pair in Mods.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var mod = pair.Value;
            text.Append("\n[mods.").Append(Quote(pair.Key)).Append("]\n");
            if (mod.Checked is { } checkedAt)
                text.Append("checked = ").Append(Quote(checkedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))).Append('\n');
            if (mod.Latest is not null)
                text.Append("latest = ").Append(Quote(mod.Latest)).Append('\n');
            if (mod.Notified is not null)
                text.Append("notified = ").Append(Quote(mod.Notified)).Append('\n');
            if (mod.Skipped.Count > 0)
                text.Append("skipped = [").Append(string.Join(", ", mod.Skipped.OrderBy(v => v, StringComparer.Ordinal).Select(Quote))).Append("]\n");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text.ToString());
        if (File.Exists(path))
            File.Delete(path);
        File.Move(temporary, path);
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}

internal sealed class ModUpdateState
{
    public DateTime? Checked { get; set; }

    /// <summary>The newest version offered by the source at the last check.</summary>
    public string? Latest { get; set; }

    /// <summary>The newest version reported to the user in notify mode, so that each version is reported only once.</summary>
    public string? Notified { get; set; }

    /// <summary>Versions that are not installed, because they were incompatible or were rolled back.</summary>
    public HashSet<string> Skipped { get; } = new(StringComparer.Ordinal);
}
