using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Lustral.Core.Loading;

namespace Lustral.Core.Diagnostics;

/// <summary>
/// Writes crash reports to <c>Lustral/Reports</c>: one file per crash, describing the session and the records from
/// Unity, Windows and Lustral's log, suitable for attaching to a bug report. Two cases are handled: an unhandled managed
/// exception that ends the process (reported immediately), and a session that ended abruptly (reported on the next start,
/// from its session marker).
/// </summary>
public sealed class CrashReporter(string reportsDirectory, string dataDirectory, CrashFileLocations locations, ILog log,
    Func<int, string, DateTime, WindowsCrashEvents.Records>? eventLog = null)
{
    private const int KeepReports = 20;
    private const int LogTailLines = 60;

    /// <summary>Reports on all previous sessions that ended without exiting normally, then deletes their markers.</summary>
    /// <param name="previousLog">The previous session's log, if available.</param>
    /// <returns>The sessions that crashed; the others were terminated externally.</returns>
    public IReadOnlyList<SessionMarker> ReportAbandonedSessions(IReadOnlyList<SessionMarker> sessions, string? previousLog)
    {
        var crashed = new List<SessionMarker>();
        foreach (var session in sessions)
        {
            try
            {
                var evidence = CrashEvidence.Find(session, dataDirectory, locations, CurrentProcessStartUtc(), eventLog);
                if (!evidence.IsCrash)
                {
                    log.Info($"The last session (started {Local(session.StartedUtc)}) did not exit normally, but no crash was recorded; it was probably terminated externally");
                    continue;
                }
                crashed.Add(session);

                var logFile = previousLog is not null && File.Exists(previousLog) && File.GetLastWriteTimeUtc(previousLog) >= session.StartedUtc ? previousLog : null;
                var endedUtc = logFile is not null ? File.GetLastWriteTimeUtc(logFile) : session.StartedUtc;
                var crashText = string.Join(Environment.NewLine, new[] { evidence.UnityCrashOutput, evidence.UnityErrorLog, evidence.ManagedCrash, evidence.FaultingModulePath }.Where(t => t is not null));
                var suspects = Mentioned(session.Mods, session.Patches, crashText);

                var report = new StringBuilder();
                Header(report, session, endedUtc, "ended with a crash");
                Suspects(report, suspects, "appear in the crash records, or patched a method that does");
                if (evidence.WindowsFault is not null)
                    Section(report, "Windows' record of the crash", evidence.WindowsFault + (evidence.FaultingModulePath is null ? "" : Environment.NewLine + evidence.FaultingModulePath));
                if (evidence.ManagedCrash is not null)
                    Section(report, ".NET's record of the crash", evidence.ManagedCrash);
                if (evidence.GameReporterCrashUtc is { } reportedUtc)
                    Section(report, "The game's crash reporter", $"The game's own crash reporter (Sentry) recorded the crash at {Local(reportedUtc)}" +
                        " and may have sent it to the game's developers. Crashes that occur with mods installed should be reported to the mod authors instead.");
                Patches(report, session.Patches);
                if (evidence.UnityCrashOutput is not null)
                    Section(report, $"Unity's crash output ({evidence.PlayerLogPath})", evidence.UnityCrashOutput);
                if (evidence.UnityErrorLog is not null)
                    Section(report, $"Unity's error.log ({evidence.UnityCrashFolder})", evidence.UnityErrorLog);
                if (logFile is not null)
                    Section(report, $"Lustral's log, last {LogTailLines} lines ({logFile})", Tail(logFile));
                Files(report, ("Unity's log", evidence.PlayerLogPath), ("Unity's crash folder", evidence.UnityCrashFolder), ("Crash dump", evidence.DumpPath),
                    ("The game's crash reporter's dump", evidence.GameReporterDumpPath), ("Lustral's log", logFile));

                var path = Write(report, endedUtc);
                log.Warn($"The last session crashed{SuspectsSuffix(suspects)}. Crash report: {path}");
            }
            catch (Exception e)
            {
                log.Warn($"Could not write a crash report for the session that started {Local(session.StartedUtc)}: {e.Message}");
            }
            finally
            {
                session.Delete();
            }
        }
        return crashed;
    }

    /// <summary>Reports an unhandled managed exception that is about to end the process.</summary>
    /// <returns>The report's path, or null if it could not be written.</returns>
    public string? ReportFatalException(SessionMarker session, Exception exception, IReadOnlyList<LoadedMod> loaded, string? currentLog)
    {
        try
        {
            var suspects = ModsOnStack(exception, loaded);
            var report = new StringBuilder();
            Header(report, session, DateTime.UtcNow, "is ending because of an unhandled managed exception");
            Suspects(report, suspects, "are on the exception's stack");
            Section(report, "Exception", exception.ToString());
            if (currentLog is not null && File.Exists(currentLog))
                Section(report, $"Lustral's log, last {LogTailLines} lines ({currentLog})", Tail(currentLog));
            return Write(report, DateTime.UtcNow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Header(StringBuilder report, SessionMarker session, DateTime endedUtc, string what)
    {
        report.AppendLine("Lustral crash report");
        report.AppendLine("====================");
        report.AppendLine($"The game session that started {Local(session.StartedUtc)} {what}.");
        report.AppendLine();
        report.AppendLine(session.Title is null ? $"Game:      {session.Game}" : $"Game:      {session.Title} ({session.Game})");
        report.AppendLine($"Unity:     {session.UnityVersion ?? "unknown"}");
        report.AppendLine($"Lustral:   {session.LustralVersion} ({session.Backend ?? "backend did not start"})");
        report.AppendLine($"Runtime:   {session.Runtime}");
        report.AppendLine($"Process:   {session.ProcessId}");
        report.AppendLine($"Started:   {Local(session.StartedUtc)}");
        report.AppendLine($"Ended:     {Local(endedUtc)} (or soon after)");
        report.AppendLine();
        report.AppendLine(session.Mods.Count == 0 ? "Mods: none loaded" : $"Mods ({session.Mods.Count}):");
        foreach (var mod in session.Mods)
            report.AppendLine($"  {mod.Id} {mod.Version} ({mod.Name})");
    }

    private static void Suspects(StringBuilder report, IReadOnlyList<SessionMod> suspects, string where)
    {
        if (suspects.Count == 0)
            return;
        report.AppendLine();
        report.AppendLine($"These mods {where}: {string.Join(", ", suspects.Select(m => m.Id))}");
    }

    private static void Patches(StringBuilder report, IReadOnlyList<SessionPatch> patches)
    {
        if (patches.Count == 0)
            return;
        report.AppendLine();
        report.AppendLine("Harmony patches (mod: method)");
        foreach (var patch in patches.OrderBy(p => p.ModId, StringComparer.Ordinal).ThenBy(p => p.Method, StringComparer.Ordinal))
            report.AppendLine($"  {patch.ModId}: {patch.Method}");
    }

    private static void Section(StringBuilder report, string title, string body)
    {
        report.AppendLine();
        report.AppendLine(title);
        report.AppendLine(new string('-', Math.Min(title.Length, 100)));
        report.AppendLine(body.TrimEnd());
    }

    private static void Files(StringBuilder report, params (string Name, string? Path)[] files)
    {
        var present = files.Where(f => f.Path is not null).ToList();
        if (present.Count == 0)
            return;
        report.AppendLine();
        report.AppendLine("Files to attach to a bug report");
        report.AppendLine("-------------------------------");
        foreach (var (name, path) in present)
            report.AppendLine($"{name}: {path}");
    }

    private string Write(StringBuilder report, DateTime whenUtc)
    {
        Directory.CreateDirectory(reportsDirectory);
        var stamp = whenUtc.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        var path = Path.Combine(reportsDirectory, $"crash-{stamp}.txt");
        File.WriteAllText(path, report.ToString());

        foreach (var old in new DirectoryInfo(reportsDirectory).GetFiles("crash-*.txt").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepReports))
        {
            try
            {
                old.Delete();
            }
            catch (IOException)
            {
            }
        }
        return path;
    }

    // Native stack traces identify managed frames (Mono) by namespace and class, and sometimes by assembly, so mods are
    // matched on both, and on the methods they patched: Type.Method or Type:Method (Mono) and Type_Method (IL2CPP symbol
    // names).
    internal static IReadOnlyList<SessionMod> Mentioned(IReadOnlyList<SessionMod> mods, IReadOnlyList<SessionPatch> patches, string text)
    {
        if (text.Length == 0)
            return [];
        bool Contains(string? name) => !string.IsNullOrEmpty(name) && text.IndexOf(name, StringComparison.Ordinal) >= 0;
        var patchedInCrash = new HashSet<string>(
            patches.Where(p => PatchNames(p.Method).Any(Contains)).Select(p => p.ModId), StringComparer.Ordinal);
        // A crash in a mod's native library names a module in the mod's folder.
        bool InFolder(SessionMod mod) => text.IndexOf($"\\Mods\\{mod.Id}\\", StringComparison.OrdinalIgnoreCase) >= 0;
        return mods.Where(mod => Contains(mod.Assembly) || Contains(mod.Namespace) || patchedInCrash.Contains(mod.Id) || InFolder(mod)).ToList();
    }

    private static IEnumerable<string> PatchNames(string method)
    {
        var lastDot = method.LastIndexOf('.');
        if (lastDot <= 0)
            yield break;
        var type = method.Substring(0, lastDot);
        var shortType = type.Substring(type.LastIndexOf('.') + 1);
        var name = method.Substring(lastDot + 1);
        yield return $"{shortType}.{name}";
        yield return $"{shortType}:{name}";
        yield return $"{shortType}::{name}";
        yield return $"{shortType}_{name}";
    }

    /// <summary>The methods patched by a mod through <see cref="Mod.Harmony"/>.</summary>
    internal static IEnumerable<SessionPatch> PatchesOf(LoadedMod mod) =>
        mod.Harmony is null
            ? []
            : mod.Harmony.GetPatchedMethods().Select(m => new SessionPatch(mod.Mod.Id, $"{m.DeclaringType?.FullName ?? "?"}.{m.Name}"));

    private static IReadOnlyList<SessionMod> ModsOnStack(Exception exception, IReadOnlyList<LoadedMod> loaded)
    {
        var assemblies = new HashSet<Assembly>();
        for (var e = exception; e is not null; e = e.InnerException)
        {
            foreach (var frame in new StackTrace(e, false).GetFrames() ?? [])
            {
                if (frame.GetMethod()?.Module.Assembly is { } assembly)
                    assemblies.Add(assembly);
            }
        }
        return loaded.Where(m => m.Assembly is not null && assemblies.Contains(m.Assembly)).Select(Describe).ToList();
    }

    internal static SessionMod Describe(LoadedMod mod) => new(
        mod.Mod.Id,
        mod.Mod.Manifest.Version.ToString(),
        mod.Mod.Manifest.Name,
        mod.Assembly?.GetName().Name,
        mod.Instance?.GetType().Namespace);

    private static string Tail(string path)
    {
        var tail = new Queue<string>(LogTailLines);
        foreach (var line in CrashEvidence.ReadLinesShared(path))
        {
            if (tail.Count == LogTailLines)
                tail.Dequeue();
            tail.Enqueue(line);
        }
        return string.Join(Environment.NewLine, tail);
    }

    private static string SuspectsSuffix(IReadOnlyList<SessionMod> suspects) =>
        suspects.Count == 0 ? "" : $" (the crash records mention {string.Join(", ", suspects.Select(m => m.Id))})";

    private static DateTime CurrentProcessStartUtc()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return DateTime.UtcNow;
        }
    }

    private static string Local(DateTime utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
