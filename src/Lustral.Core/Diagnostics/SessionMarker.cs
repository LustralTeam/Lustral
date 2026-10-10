using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Lustral.Core.Diagnostics;

/// <summary>A method patched by a mod with Harmony, as <c>Namespace.Type.Method</c>.</summary>
public sealed record SessionPatch(string ModId, string Method);

/// <summary>A mod as recorded in a session marker, for crash reports.</summary>
/// <param name="Assembly">The assembly's simple name, as shown in stack traces.</param>
/// <param name="Namespace">The namespace of the mod's entry type, as shown in managed stack frames.</param>
public sealed record SessionMod(string Id, string Version, string Name, string? Assembly, string? Namespace);

/// <summary>
/// A file for each running game, <c>Lustral/Logs/session-&lt;pid&gt;.txt</c>, deleted when the game exits normally. A
/// marker left by a process that no longer exists indicates that the session ended abruptly: it crashed or was
/// terminated. The marker records the information a crash report needs to describe the session afterwards.
/// </summary>
/// <remarks>
/// The running session keeps its marker open, and the operating system releases it however the process ends, so a marker
/// that no process holds open is abandoned. Process IDs and start times are not compared, because process IDs are reused
/// and Unity's Mono cannot always determine whether a process is still running.
/// </remarks>
public sealed class SessionMarker
{
    private const string Prefix = "session-";

    private FileStream? _held;

    private SessionMarker(string path) => FilePath = path;

    public string FilePath { get; }
    public int ProcessId { get; private set; }
    public DateTime StartedUtc { get; private set; }
    public string LustralVersion { get; private set; } = "";
    public string Runtime { get; private set; } = "";
    /// <summary>The game's executable path.</summary>
    public string Game { get; private set; } = "";

    /// <summary>The game's name, version and developer, for example "Lethal Company 0.1 by ZeekerssRBLX".</summary>
    public string? Title { get; private set; }
    public string? UnityVersion { get; private set; }
    public string? Backend { get; set; }

    /// <summary>Whether the game reached its first scene.</summary>
    public bool Ready { get; set; }

    /// <summary>Mods updated by this session before they were loaded, as <c>id@version</c>.</summary>
    public IReadOnlyList<string> Updated { get; set; } = [];
    public IReadOnlyList<SessionMod> Mods { get; set; } = [];
    public IReadOnlyList<SessionPatch> Patches { get; set; } = [];

    /// <summary>Records the current process's session.</summary>
    public static SessionMarker Begin(string directory, string lustralVersion, string runtime, string game, string? unityVersion, string? title = null)
    {
        using var process = Process.GetCurrentProcess();
        var marker = new SessionMarker(Path.Combine(directory, $"{Prefix}{process.Id}.txt"))
        {
            ProcessId = process.Id,
            StartedUtc = DateTime.UtcNow,
            LustralVersion = lustralVersion,
            Runtime = runtime,
            Game = game,
            Title = title,
            UnityVersion = unityVersion,
        };
        marker.Save();
        return marker;
    }

    /// <summary>Returns the markers that no running game holds open; their sessions ended without exiting normally.</summary>
    public static IReadOnlyList<SessionMarker> FindAbandoned(string directory)
    {
        if (!Directory.Exists(directory))
            return [];
        var abandoned = new List<SessionMarker>();
        foreach (var path in Directory.GetFiles(directory, $"{Prefix}*.txt"))
        {
            if (IsHeld(path))
                continue;
            var marker = TryRead(path);
            if (marker is null)
                TryDelete(path);
            else
                abandoned.Add(marker);
        }
        return abandoned;
    }

    public void Save()
    {
        var text = new StringBuilder()
            .AppendLine($"pid={ProcessId}")
            .AppendLine($"started={Format(StartedUtc)}")
            .AppendLine($"lustral={LustralVersion}")
            .AppendLine($"runtime={Runtime}")
            .AppendLine($"game={Game}")
            .AppendLine($"title={Title}")
            .AppendLine($"unity={UnityVersion}")
            .AppendLine($"backend={Backend}")
            .AppendLine($"ready={(Ready ? "true" : "false")}");
        foreach (var updated in Updated)
            text.AppendLine($"updated={updated}");
        foreach (var mod in Mods)
            text.AppendLine($"mod={mod.Id}|{mod.Version}|{mod.Name}|{mod.Assembly}|{mod.Namespace}");
        foreach (var patch in Patches)
            text.AppendLine($"patch={patch.ModId}|{patch.Method}");
        try
        {
            // Other processes can read the file while it is held, but cannot open it exclusively; this indicates that it is
            // in use.
            _held ??= new FileStream(FilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            var bytes = new UTF8Encoding(false).GetBytes(text.ToString());
            _held.SetLength(0);
            _held.Write(bytes, 0, bytes.Length);
            _held.Flush();
        }
        catch (IOException)
        {
            // Without a marker, this session cannot be reported on; this does not justify failing startup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes the marker when the session ends normally or has been reported on.</summary>
    public void Delete()
    {
        _held?.Dispose();
        _held = null;
        TryDelete(FilePath);
    }

    internal static SessionMarker? TryRead(string path)
    {
        try
        {
            var marker = new SessionMarker(path);
            var mods = new List<SessionMod>();
            var patches = new List<SessionPatch>();
            var updated = new List<string>();
            foreach (var line in CrashEvidence.ReadLinesShared(path))
            {
                var separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;
                var value = line.Substring(separator + 1);
                switch (line.Substring(0, separator))
                {
                    case "pid": marker.ProcessId = int.Parse(value, CultureInfo.InvariantCulture); break;
                    case "started": marker.StartedUtc = Parse(value) ?? DateTime.MinValue; break;
                    case "lustral": marker.LustralVersion = value; break;
                    case "runtime": marker.Runtime = value; break;
                    case "game": marker.Game = value; break;
                    case "title": marker.Title = NullIfEmpty(value); break;
                    case "unity": marker.UnityVersion = NullIfEmpty(value); break;
                    case "backend": marker.Backend = NullIfEmpty(value); break;
                    case "ready": marker.Ready = value == "true"; break;
                    case "updated": updated.Add(value); break;
                    case "mod":
                        var parts = value.Split('|');
                        if (parts.Length == 5)
                            mods.Add(new SessionMod(parts[0], parts[1], parts[2], NullIfEmpty(parts[3]), NullIfEmpty(parts[4])));
                        break;
                    case "patch":
                        var patch = value.Split('|');
                        if (patch.Length == 2)
                            patches.Add(new SessionPatch(patch[0], patch[1]));
                        break;
                }
            }
            marker.Mods = mods;
            marker.Patches = patches;
            marker.Updated = updated;
            return marker.ProcessId > 0 ? marker : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException)
        {
            return null;
        }
    }

    // A running session holds its marker open (FileShare.Read), so opening it exclusively fails.
    private static bool IsHeld(string path)
    {
        try
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Format(DateTime? time) => time?.ToString("o", CultureInfo.InvariantCulture) ?? "";

    private static DateTime? Parse(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time) ? time.ToUniversalTime() : null;

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
}
