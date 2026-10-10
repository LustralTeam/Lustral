using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Lustral.Core.Assets;
using Lustral.Core.Config;
using Lustral.Core.Diagnostics;
using Lustral.Core.GameData;
using Lustral.Core.Lifecycle;
using Lustral.Core.Loading;
using Lustral.Core.Logging;
using Lustral.Core.Mods;
using Lustral.Core.Updates;

namespace Lustral.Core.Startup;

/// <summary>
/// Lustral's startup sequence, identical on all backends: logging, then the backend, then mods.
/// </summary>
public static class Bootstrap
{
    private static LogHub? s_logs;
    private static string s_gameName = "";
    private static bool s_console;

    // Mods remain referenced for the entire session; nothing is unloaded.
    private static IReadOnlyList<LoadedMod> s_mods = [];
    private static GameLifecycle? s_lifecycle;
    private static SessionMarker? s_session;
    private static IReadOnlyList<SessionMarker> s_crashedSessions = [];
    private static ModUpdater? s_updater;

    public static IReadOnlyList<LoadedMod> Mods => s_mods;

    /// <summary>
    /// Runs startup once. Failures are logged and the game continues without mods; only a failure to open the log throws.
    /// </summary>
    public static void Run(GamePaths paths, Func<GamePaths, LogHub, IBackend> createBackend)
    {
        if (s_logs is not null)
            throw new InvalidOperationException("Lustral has already started");

        var stopwatch = Stopwatch.StartNew();
        var config = LoaderConfig.Load(paths.LustralDirectory, out var configProblems);
        var logs = s_logs = new LogHub { MinimumLevel = LogLevelOverride() ?? config.LogLevel };
        var logFile = new FileLogSink(paths.LogsDirectory);
        logs.AddSink(logFile);
        s_gameName = Path.GetFileNameWithoutExtension(paths.ExecutablePath);
        if (config.Console)
            ShowConsole();
        var log = logs.For("Lustral");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => logs.Dispose();

        var version = InformationalVersion(typeof(Bootstrap).Assembly);
        var runtime = RuntimeInformation.FrameworkDescription.Trim();
        log.Info($"Lustral {version} on {runtime}, {RuntimeInformation.ProcessArchitecture}");
        var metadata = GameMetadata.Read(paths.DataDirectory, paths.GameDirectory, paths.LastRunFile, log.Debug);
        var gameName = metadata.Name ?? Path.GetFileNameWithoutExtension(paths.ExecutablePath);
        log.Info($"Game: {DescribeGame(gameName, metadata)}, Unity {metadata.UnityVersion ?? "unknown"} ({paths.ExecutablePath})");
        foreach (var problem in configProblems)
            log.Warn(problem);

        var session = s_session = StartSession(paths, logs, logFile, version, runtime, DescribeGame(gameName, metadata), metadata.UnityVersion);
        try
        {
            var backend = createBackend(paths, logs);
            log.Info($"Backend: {backend.Name}");
            if (session is not null)
            {
                session.Backend = backend.Name;
                session.Save();
            }
            var game = new GameInfo(gameName, metadata.Developer, metadata.Version, metadata.UnityVersion, backend.Kind, paths.GameDirectory);
            var environment = new ModEnvironment(
                SemanticVersion.TryParse(version, out var lustralVersion) ? lustralVersion : null,
                backend.Kind == ScriptingBackend.Il2Cpp ? "il2cpp" : "mono",
                ModEnvironment.CurrentPlatform,
                Path.GetFileNameWithoutExtension(paths.ExecutablePath),
                metadata.Name,
                metadata.Developer,
                metadata.Version,
                metadata.UnityVersion);
            backend.Start();
            backend.WhenReadyForMods(() => LoadMods(paths, config, logs, backend, session, stopwatch, metadata, game, environment, version));
        }
        catch (StartupException e)
        {
            log.Error($"Lustral failed to start: {e.Message}");
        }
        catch (Exception e)
        {
            log.Error("Lustral failed to start", e);
        }
    }

    private static void LoadMods(
        GamePaths paths, LoaderConfig config, LogHub logs, IBackend backend, SessionMarker? session, Stopwatch stopwatch,
        GameMetadata metadata, GameInfo game, ModEnvironment environment, string version)
    {
        var log = logs.For("Lustral");
        try
        {
            var updater = s_updater = new ModUpdater(paths.LustralDirectory, config, environment, logs.For("Updates"), $"Lustral/{version.Split('+')[0]}");
            RollBackAfterCrashes(updater);
            CreateModsDirectory(paths, log);
            var (discovered, problems) = ModDiscovery.Scan(paths.ModsDirectory);
            var applied = updater.ApplyStaged(discovered);
            if (applied.Count > 0)
            {
                (discovered, problems) = ModDiscovery.Scan(paths.ModsDirectory);
                if (session is not null)
                {
                    session.Updated = applied.Where(u => !u.Rollback).Select(u => $"{u.Id}@{u.To}").ToList();
                    SaveSession(session, log);
                }
            }
            // Logged with the loader's messages about each mod.
            var modsLog = logs.For("Core");
            foreach (var problem in problems)
                modsLog.Warn($"Not loading {problem}");

            foreach (var id in config.DisabledMods.Where(id => discovered.All(m => m.Id != id)).OrderBy(id => id, StringComparer.Ordinal))
                modsLog.Warn($"{LoaderConfig.FileName} disables {id}, but no installed mod has that ID");
            foreach (var id in config.ForcedMods.Where(id => discovered.All(m => m.Id != id)).OrderBy(id => id, StringComparer.Ordinal))
                modsLog.Warn($"{LoaderConfig.FileName} forces {id}, but no installed mod has that ID");

            var plan = ModResolver.Resolve(discovered, config.DisabledMods, environment, config.ForcedMods);
            var coroutines = new CoroutineScheduler();
            var assetBundles = new AssetBundles(backend.CreateAssetBundles());
            s_mods = new ModLoader(logs, backend.CreateAssemblyLoader(), paths.ConfigDirectory, game, paths.ModDataDirectory)
            {
                Coroutines = coroutines,
                AssetBundles = assetBundles,
            }.Load(plan);
            SaveSessionMods();
            s_lifecycle = new GameLifecycle(logs, s_mods) { Coroutines = coroutines };
            s_lifecycle.Ready += () => assetBundles.GameReady = true;
            s_lifecycle.Ready += () => CheckGameVersion(paths, metadata, game, backend, log);
            if (session is not null)
            {
                s_lifecycle.Ready += () =>
                {
                    session.Ready = true; // An update that crashes the game after this point is not rolled back.
                    SaveSession(session, log);
                };
            }
            // Writes the count of pending repetitions before the game exits, as ProcessExit may not be raised.
            s_lifecycle.ShutdownCompleted += logs.Flush;
            if (session is not null)
                s_lifecycle.ShutdownCompleted += session.Delete;
            backend.InstallLifecycle(s_lifecycle);
            log.Info($"Startup finished in {stopwatch.ElapsedMilliseconds} ms");

            updater.RollBackFailed(applied, id => s_mods.Any(m => m.Mod.Id == id));
            if (discovered.Any(m => m.Manifest.Update is not null))
                Task.Run(() => updater.CheckAsync(discovered));
        }
        catch (Exception e)
        {
            log.Error("Lustral could not load mods", e);
        }
    }

    // An update installed at the start of a session that crashed before the game was ready is rolled back now, before the
    // mods load. Lustral cannot determine which mod caused the crash, and a new version is the most likely cause.
    private static void RollBackAfterCrashes(ModUpdater updater)
    {
        foreach (var crashed in s_crashedSessions.Where(s => !s.Ready))
        {
            foreach (var entry in crashed.Updated)
            {
                var at = entry.LastIndexOf('@');
                if (at > 0 && SemanticVersion.TryParse(entry.Substring(at + 1), out var version))
                    updater.RollBack(entry.Substring(0, at), entry.Substring(0, at), version, "was newly installed when the game crashed during startup", "now");
            }
        }
    }

    // Creates the Mods folder on the first start, so that users find it in place.
    private static void CreateModsDirectory(GamePaths paths, ILog log)
    {
        try
        {
            Directory.CreateDirectory(paths.ModsDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Debug($"Could not create {paths.ModsDirectory}: {e.Message}");
        }
    }

    private static void SaveSession(SessionMarker session, ILog log)
    {
        try
        {
            session.Save();
        }
        catch (Exception e)
        {
            log.Debug($"Could not update the session marker: {e.Message}");
        }
    }

    // Once the game is running, it reports its own version. Lustral compares it with the version read from the files, or
    // records it for the next start if the version could not be read (for Unity versions newer than the known
    // PlayerSettings layouts).
    private static void CheckGameVersion(GamePaths paths, GameMetadata metadata, GameInfo game, IBackend backend, ILog log)
    {
        try
        {
            var running = backend.ReadGameVersion()?.Trim();
            if (string.IsNullOrEmpty(running))
            {
                log.Debug("The running game does not report its version (Application.version)");
                return;
            }
            if (metadata.VersionSource == GameVersionSource.Files)
            {
                if (running != metadata.Version)
                    log.Warn($"The game reports version {running}, but Lustral read {metadata.Version ?? "none"} from its files and checked mods against it. Please report this issue on Lustral's issue tracker.");
                return;
            }
            if (running != metadata.Version)
            {
                game.Version = running;
                log.Info($"Game version: {running}. The version could not be read from the game's files; from the next start on, it will be known before mods load.");
            }
            if (metadata.DataIdentity is { } identity && (running != metadata.Version || metadata.VersionSource != GameVersionSource.LastRun))
                GameMetadata.RecordVersion(paths.LastRunFile, identity, running!);
        }
        catch (Exception e)
        {
            log.Debug($"Could not check the game's version while it is running: {e.Message}");
        }
    }

    // "Lethal Company 0.1 by ZeekerssRBLX"
    private static string DescribeGame(string name, GameMetadata metadata)
    {
        var version = metadata.VersionSource switch
        {
            GameVersionSource.Files when metadata.Version is not null => " " + metadata.Version,
            GameVersionSource.LastRun => $" {metadata.Version} (as it reported on its last run)",
            _ => " (version unknown)",
        };
        return $"{name}{version}{(metadata.Developer is null ? "" : " by " + metadata.Developer)}";
    }

    /// <summary>Opens a console window that shows the log as it is written (the [log] console setting in loader.toml), once.</summary>
    private static void ShowConsole()
    {
        if (s_console || s_logs is null)
            return;
        s_console = true;
        if (ConsoleLogSink.TryOpen($"Lustral: {s_gameName}") is { } console)
            s_logs.AddSink(console);
        else
            s_logs.For("Lustral").Warn("Could not open a console window; the log is available in Lustral/Logs/latest.log");
    }

    private static void SaveSessionMods()
    {
        if (s_session is null)
            return;
        try
        {
            s_session.Mods = s_mods.Select(CrashReporter.Describe).ToList();
            s_session.Patches = s_mods.SelectMany(CrashReporter.PatchesOf).ToList();
            s_session.Save();
        }
        catch (Exception e)
        {
            s_logs?.For("Crash").Debug($"Could not update the session marker: {e.Message}");
        }
    }

    /// <summary>
    /// Reports on previous sessions that crashed, marks this session as running until the game exits normally, and reports
    /// an unhandled managed exception that ends the process when it occurs.
    /// </summary>
    private static SessionMarker? StartSession(GamePaths paths, LogHub logs, FileLogSink logFile, string version, string runtime, string title, string? unityVersion)
    {
        var log = logs.For("Lustral");
        var reporter = new CrashReporter(paths.ReportsDirectory, paths.DataDirectory, CrashFileLocations.ForCurrentUser(), logs.For("Crash"));
        SessionMarker? session = null;
        try
        {
            s_crashedSessions = reporter.ReportAbandonedSessions(SessionMarker.FindAbandoned(paths.LogsDirectory), logFile.PreviousLogPath);
            session = SessionMarker.Begin(paths.LogsDirectory, version, runtime, paths.ExecutablePath, unityVersion, title);
        }
        catch (Exception e)
        {
            log.Warn($"Crash reports are disabled for this session: {e.Message}");
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var exception = e.ExceptionObject as Exception;
            log.Error("Unhandled exception", exception);
            if (!e.IsTerminating || session is null || exception is null)
                return;
            if (reporter.ReportFatalException(session, exception, s_mods, logFile.Path) is { } report)
            {
                log.Error($"The game is closing because of this exception. Crash report: {report}");
                session.Delete();
            }
        };
        return session;
    }

    // For developers: overrides the log level in loader.toml without editing the file.
    private static LogLevel? LogLevelOverride() =>
        Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("LUSTRAL_LOG_LEVEL"), ignoreCase: true, out var level)
            ? level
            : null;

    private static string InformationalVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
