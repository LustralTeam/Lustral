using System.Globalization;

namespace Lustral.Core.Diagnostics;

/// <summary>The locations where Windows and Unity record crashes. Configurable so that tests can use a temporary folder.</summary>
/// <param name="LocalLowDirectory"><c>%USERPROFILE%\AppData\LocalLow</c>, which contains Unity's <c>Player.log</c>.</param>
/// <param name="CrashDumpsDirectory"><c>%LOCALAPPDATA%\CrashDumps</c>, where Windows Error Reporting writes dumps if local dumps are enabled.</param>
/// <param name="TempDirectory"><c>%TEMP%</c>, where Unity's crash handler writes <c>&lt;company&gt;\&lt;product&gt;\Crashes</c>.</param>
public sealed record CrashFileLocations(string LocalLowDirectory, string CrashDumpsDirectory, string TempDirectory)
{
    public static CrashFileLocations ForCurrentUser()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new CrashFileLocations(
            Path.Combine(Path.GetDirectoryName(local)!, "LocalLow"),
            Path.Combine(local, "CrashDumps"),
            Path.GetTempPath());
    }
}

/// <summary>
/// The evidence left by a session that ended abruptly. The session crashed if any of the following exist: Unity crash
/// output in the session's <c>Player.log</c> (now <c>Player-prev.log</c>), a Windows crash dump of its process, a Unity
/// crash folder, a Windows or .NET event log record, or a record from the game's own crash reporter. Without any of these,
/// the game was most likely terminated externally (for example by Task Manager or Steam) and there is nothing to report.
/// </summary>
public sealed class CrashEvidence
{
    // Lines written by the Unity player when it crashes: first by its Windows crash handler, then with the native stack
    // trace. Mono's crash reporter (on Mono games) writes the last two.
    private static readonly string[] CrashMarkers = ["Crash!!!", "========== OUTPUTTING STACK TRACE", "Native Crash Reporting", "Got a SIGSEGV"];
    private const string StackTraceEnd = "========== END OF STACKTRACE";
    private const int MaxCrashLines = 150;
    private const int MaxErrorLogLines = 80;

    public string? PlayerLogPath { get; private set; }

    /// <summary>Unity's crash output from the session's Player.log, starting at the first crash line.</summary>
    public string? UnityCrashOutput { get; private set; }

    public string? DumpPath { get; private set; }

    public string? UnityCrashFolder { get; private set; }

    /// <summary>The beginning of <c>error.log</c> in Unity's crash folder, containing the faulting module and the stack.</summary>
    public string? UnityErrorLog { get; private set; }

    /// <summary>The Windows "Application Error" record: the faulting module, exception code and offset.</summary>
    public string? WindowsFault { get; private set; }

    public string? FaultingModulePath { get; private set; }

    /// <summary>The .NET runtime's record, if it terminated the process: the description and the managed stack.</summary>
    public string? ManagedCrash { get; private set; }

    /// <summary>The time at which the game's own crash reporter (Sentry's native SDK) recorded the crash.</summary>
    public DateTime? GameReporterCrashUtc { get; private set; }

    /// <summary>The minidump written by the game's crash reporter, which exists until the reporter uploads and deletes it.</summary>
    public string? GameReporterDumpPath { get; private set; }

    public bool IsCrash => UnityCrashOutput is not null || DumpPath is not null || UnityCrashFolder is not null || WindowsFault is not null ||
                           ManagedCrash is not null || GameReporterCrashUtc is not null;

    /// <param name="currentProcessStartUtc">The start time of the current process; files written by this process are not evidence.</param>
    /// <param name="eventLog">Reads the Windows event log records of the crash; <see cref="WindowsCrashEvents.Find"/> outside tests.</param>
    public static CrashEvidence Find(SessionMarker session, string dataDirectory, CrashFileLocations locations, DateTime currentProcessStartUtc,
        Func<int, string, DateTime, WindowsCrashEvents.Records>? eventLog = null)
    {
        var evidence = new CrashEvidence();
        var executable = Path.GetFileName(session.Game);
        try
        {
            var records = (eventLog ?? WindowsCrashEvents.Find)(session.ProcessId, executable, session.StartedUtc);
            evidence.WindowsFault = records.Fault;
            evidence.FaultingModulePath = records.FaultingModulePath;
            evidence.ManagedCrash = records.ManagedCrash;
        }
        catch (Exception)
        {
            // One of several sources; the others are still checked.
        }
        var dump = Path.Combine(locations.CrashDumpsDirectory, $"{executable}.{session.ProcessId}.dmp");
        if (File.Exists(dump) && File.GetLastWriteTimeUtc(dump) >= session.StartedUtc)
            evidence.DumpPath = dump;

        if (UnityAppInfo.TryRead(dataDirectory) is not { } app)
            return evidence;

        // Unity renames the previous session's Player.log to Player-prev.log when the player starts, before Lustral starts.
        // Players started with -batchmode or -logFile do not, so the previous session's log may still be Player.log. Either
        // file is used if it was last written during that session, before this process started, and contains a crash;
        // timestamps alone cannot distinguish the crashed session's log from a later session's.
        var logDirectory = Path.Combine(locations.LocalLowDirectory, app.Company, app.Product);
        foreach (var path in new[] { "Player-prev.log", "Player.log" }.Select(name => Path.Combine(logDirectory, name)))
        {
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < session.StartedUtc || File.GetLastWriteTimeUtc(path) >= currentProcessStartUtc)
                continue;
            if (ReadCrashOutput(path) is { } output)
            {
                evidence.PlayerLogPath = path;
                evidence.UnityCrashOutput = output;
                break;
            }
        }

        FindGameReporterCrash(evidence, Path.Combine(logDirectory, ".sentry-native"), session.StartedUtc, currentProcessStartUtc);

        var crashes = new DirectoryInfo(Path.Combine(locations.TempDirectory, app.Company, app.Product, "Crashes"));
        if (crashes.Exists)
        {
            var folder = crashes.GetDirectories("Crash_*")
                .Where(d => d.CreationTimeUtc >= session.StartedUtc)
                .OrderByDescending(d => d.CreationTimeUtc)
                .FirstOrDefault();
            if (folder is not null)
            {
                evidence.UnityCrashFolder = folder.FullName;
                var errorLog = Path.Combine(folder.FullName, "error.log");
                if (File.Exists(errorLog))
                    evidence.UnityErrorLog = string.Join(Environment.NewLine, ReadLinesShared(errorLog).Take(MaxErrorLogLines));

                // The crash folder holds a copy of the session's log (output_log.txt before Unity 2019.3, Player.log later),
                // which contains the stack trace where the Player.log in LocalLow does not, as in Unity 2018.
                if (evidence.UnityCrashOutput is null)
                {
                    foreach (var path in new[] { "Player.log", "output_log.txt" }.Select(name => Path.Combine(folder.FullName, name)))
                    {
                        if (File.Exists(path) && ReadCrashOutput(path) is { } output)
                        {
                            evidence.PlayerLogPath = path;
                            evidence.UnityCrashOutput = output;
                            break;
                        }
                    }
                }
            }
        }
        return evidence;
    }

    // Games with Sentry's Unity SDK handle native crashes themselves (crashpad), so neither Windows nor Unity's crash
    // handler records them. Sentry's database is in the game's persistent data folder: last_crash contains the time of
    // the last crash, and reports/ contains the minidump until it is uploaded.
    private static void FindGameReporterCrash(CrashEvidence evidence, string database, DateTime sessionStartUtc, DateTime currentProcessStartUtc)
    {
        try
        {
            var lastCrash = Path.Combine(database, "last_crash");
            if (!File.Exists(lastCrash) ||
                !DateTime.TryParse(File.ReadAllText(lastCrash).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var crashedUtc) ||
                crashedUtc < sessionStartUtc || crashedUtc >= currentProcessStartUtc)
                return;
            evidence.GameReporterCrashUtc = crashedUtc;
            var reports = new DirectoryInfo(Path.Combine(database, "reports"));
            evidence.GameReporterDumpPath = reports.Exists
                ? reports.GetFiles("*.dmp")
                    .Where(f => f.LastWriteTimeUtc >= sessionStartUtc && f.LastWriteTimeUtc < currentProcessStartUtc)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault()?.FullName
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // One of several sources.
        }
    }

    // Extracts the text from the first crash line to the end of the stack trace, excluding the surrounding output that
    // Unity prints (the list of loaded modules, printed twice, symbol search paths and failed symbol lookups), which would
    // otherwise push the stack frames beyond the excerpt.
    internal static string? ReadCrashOutput(string playerLog)
    {
        var lines = ReadLinesShared(playerLog).ToList();
        var start = lines.FindIndex(line => CrashMarkers.Any(marker => line.StartsWith(marker, StringComparison.Ordinal)));
        if (start < 0)
            return null;
        var end = lines.FindIndex(start, line => line.StartsWith(StackTraceEnd, StringComparison.Ordinal));
        var kept = lines.Skip(start).Take((end < 0 ? lines.Count : end + 1) - start)
            .Where(line => !IsNoise(line))
            .Take(MaxCrashLines);
        return string.Join(Environment.NewLine, kept);
    }

    private static bool IsNoise(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 ||
               trimmed.StartsWith("SymInit:", StringComparison.Ordinal) ||
               trimmed.StartsWith("OS-Version:", StringComparison.Ordinal) ||
               trimmed.StartsWith("ERROR: SymGetSymFromAddr64", StringComparison.Ordinal) ||
               trimmed.StartsWith("ERROR: SymGetModuleInfo64", StringComparison.Ordinal) ||
               // A module: "C:\...\x.dll:x.dll (00007FF...), size: ..., SymType: '-deferred-', PDB: ..."
               trimmed.IndexOf(", SymType: '", StringComparison.Ordinal) >= 0;
    }

    internal static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            yield return line;
    }
}

/// <summary>The company and product names from <c>&lt;game&gt;_Data/app.info</c>, which Unity uses to name the game's data folders.</summary>
public sealed record UnityAppInfo(string Company, string Product)
{
    public static UnityAppInfo? TryRead(string dataDirectory)
    {
        try
        {
            var lines = File.ReadAllLines(Path.Combine(dataDirectory, "app.info"));
            return lines.Length >= 2 && lines[0].Trim().Length > 0 && lines[1].Trim().Length > 0
                ? new UnityAppInfo(lines[0].Trim(), lines[1].Trim())
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
