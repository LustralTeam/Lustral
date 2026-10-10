using System.Diagnostics;
using System.Globalization;
using Lustral.Core.Diagnostics;
using Lustral.Core.Loading;
using Lustral.Core.Logging;
using Lustral.Core.Mods;

namespace Lustral.Core.Tests;

public sealed class CrashReportTests : IDisposable
{
    // A Unity player's crash output as it ends up in Player.log.
    private const string CrashedPlayerLog = """
        Initialize engine version: 6000.3.10f1
        [Physics::Module] Initialized MultithreadedJobDispatcher with 31 workers.
        Crash!!!
        SymInit: Symbol-SearchPath: '.', symOptions: 534, UserName: 'player'
        OS-Version: 10.0.0
        C:\Games\Some Game\UnityPlayer.dll:UnityPlayer.dll (00007FF939C20000), size: 38248448 (result: 0), SymType: '-deferred-', PDB: '', fileVersion: 6000.5.4.20703
        ========== OUTPUTTING STACK TRACE ==================

          ERROR: SymGetSymFromAddr64, GetLastError: 'Attempt to access invalid address.' (Address: 00007FF93ABC9629)
        0x00007FFB12345678 (GameAssembly) Il2CppSample_Crasher_Explode
        0x00007FFB12345679 (Lustral.Samples.Crashy) Lustral.Samples.Crashy.Boom
        ========== END OF STACKTRACE ===========
        A crash has been intercepted by the crash handler.
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "lustral-tests", Guid.NewGuid().ToString("N"));
    private readonly MemorySink _log = new();
    private readonly CrashFileLocations _locations;
    private readonly string _logs, _reports, _data;

    public CrashReportTests()
    {
        _logs = Directory.CreateDirectory(Path.Combine(_root, "Game", "Lustral", "Logs")).FullName;
        _reports = Path.Combine(_root, "Game", "Lustral", "Reports");
        _data = Directory.CreateDirectory(Path.Combine(_root, "Game", "Game_Data")).FullName;
        File.WriteAllText(Path.Combine(_data, "app.info"), "Some Studio\nSome Game");
        _locations = new CrashFileLocations(Path.Combine(_root, "LocalLow"), Path.Combine(_root, "CrashDumps"), Path.Combine(_root, "Temp"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_marker_round_trips_and_isnt_abandoned_while_its_session_holds_it()
    {
        var marker = SessionMarker.Begin(_logs, "0.0.1", ".NET 10.0.9", Path.Combine(_root, "Game", "Game.exe"), "6000.3.10f1", "Some Game 1.2 by Some Studio");
        marker.Backend = "IL2CPP (Il2CppInterop V2)";
        marker.Mods = [new SessionMod("a.mod", "1.2.3", "A Mod", "A.Mod", "A.Mod.Code")];
        marker.Save();

        Assert.Empty(SessionMarker.FindAbandoned(_logs));
        var read = SessionMarker.TryRead(marker.FilePath)!;
        Assert.Equal(Environment.ProcessId, read.ProcessId);
        Assert.Equal("IL2CPP (Il2CppInterop V2)", read.Backend);
        Assert.Equal((Path.Combine(_root, "Game", "Game.exe"), "Some Game 1.2 by Some Studio"), (read.Game, read.Title));
        Assert.Equal(new SessionMod("a.mod", "1.2.3", "A Mod", "A.Mod", "A.Mod.Code"), Assert.Single(read.Mods));

        // Simulates the effect of a crash on the open handle.
        var held = typeof(SessionMarker).GetField("_held", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((FileStream)held.GetValue(marker)!).Dispose();
        Assert.Equal(Environment.ProcessId, Assert.Single(SessionMarker.FindAbandoned(_logs)).ProcessId);
    }

    [Fact]
    public void Quitting_deletes_the_marker()
    {
        var marker = SessionMarker.Begin(_logs, "0.0.1", ".NET 10.0.9", "Game.exe", null);

        marker.Delete();

        Assert.False(File.Exists(marker.FilePath));
        Assert.Empty(SessionMarker.FindAbandoned(_logs));
    }

    [Fact]
    public void A_session_that_ended_without_a_crash_trace_is_only_logged()
    {
        WriteMarker(999_999);

        Reporter().ReportAbandonedSessions(SessionMarker.FindAbandoned(_logs), previousLog: null);

        Assert.Contains(_log.From("Crash"), m => m.Contains("did not exit normally"));
        Assert.False(Directory.Exists(_reports));
        Assert.Empty(SessionMarker.FindAbandoned(_logs)); // Forgotten once handled.
    }

    [Fact]
    public void A_crash_in_Player_log_gets_a_report_naming_the_mods_on_the_stack()
    {
        var started = DateTime.UtcNow.AddMinutes(-10);
        WriteMarker(999_999, started, [
            new SessionMod("lustral.samples.crashy", "0.1.0", "Crashy", "Lustral.Samples.Crashy", "Lustral.Samples.Crashy"),
            new SessionMod("innocent.mod", "2.0.0", "Innocent", "Innocent", "Innocent.Code"),
        ]);
        var playerLog = Path.Combine(_locations.LocalLowDirectory, "Some Studio", "Some Game", "Player-prev.log");
        Directory.CreateDirectory(Path.GetDirectoryName(playerLog)!);
        File.WriteAllText(playerLog, CrashedPlayerLog);
        File.SetLastWriteTimeUtc(playerLog, started.AddMinutes(1)); // Written by the crashed session, before this process started.
        var previousLog = Path.Combine(_logs, "2026-10-04_12-00-00.log");
        File.WriteAllLines(previousLog, Enumerable.Range(1, 100).Select(i => $"log line {i}"));

        Reporter().ReportAbandonedSessions(SessionMarker.FindAbandoned(_logs), previousLog);

        var report = File.ReadAllText(Assert.Single(Directory.GetFiles(_reports, "crash-*.txt")));
        Assert.Contains("ended with a crash", report);
        Assert.Contains($"Game:      Some Game 1.2 by Some Studio ({Path.Combine(_root, "Game", "Game.exe")})", report);
        Assert.Contains("lustral.samples.crashy 0.1.0 (Crashy)", report);
        Assert.Contains("These mods appear in the crash records, or patched a method that does: lustral.samples.crashy\n", report.Replace("\r\n", "\n"));
        Assert.DoesNotContain("output: lustral.samples.crashy, innocent.mod", report);
        Assert.Contains("========== OUTPUTTING STACK TRACE", report);
        Assert.Contains("========== END OF STACKTRACE", report);
        Assert.DoesNotContain("intercepted by the crash handler", report); // The excerpt ends with the stack.
        Assert.DoesNotContain("Initialize engine version", report);
        Assert.DoesNotContain("SymInit", report);
        Assert.DoesNotContain("SymType", report);
        Assert.DoesNotContain("SymGetSymFromAddr64", report);
        Assert.Contains("0x00007FFB12345679 (Lustral.Samples.Crashy) Lustral.Samples.Crashy.Boom", report);
        Assert.Contains("log line 100", report);
        Assert.DoesNotContain("log line 40\n", report.Replace("\r\n", "\n")); // Only the tail.
        Assert.Contains(_log.From("Crash"), m => m.StartsWith("The last session crashed (the crash records mention lustral.samples.crashy)"));
    }

    [Fact]
    public void A_mod_that_patched_a_method_in_the_crash_is_named()
    {
        var path = WriteMarker(999_999, DateTime.UtcNow.AddMinutes(-10), [
            new SessionMod("lustral.samples.crashy", "0.1.0", "Crashy", "Lustral.Samples.Crashy", "Lustral.Samples.Crashy"),
            new SessionMod("patcher.mod", "1.0.0", "Patcher", "Patcher", "Patcher"),
        ]);
        File.AppendAllLines(path, ["patch=patcher.mod|Il2Cpp.Sample_Crasher.Explode", "patch=lustral.samples.crashy|Il2Cpp.Menu.Open"]);
        var playerLog = Path.Combine(_locations.LocalLowDirectory, "Some Studio", "Some Game", "Player-prev.log");
        Directory.CreateDirectory(Path.GetDirectoryName(playerLog)!);
        File.WriteAllText(playerLog, CrashedPlayerLog.Replace("Il2CppSample_Crasher_Explode", "Sample_Crasher_Explode"));
        File.SetLastWriteTimeUtc(playerLog, DateTime.UtcNow.AddMinutes(-9));

        Reporter().ReportAbandonedSessions(SessionMarker.FindAbandoned(_logs), previousLog: null);

        var report = File.ReadAllText(Assert.Single(Directory.GetFiles(_reports, "crash-*.txt")));
        Assert.Contains("These mods appear in the crash records, or patched a method that does: lustral.samples.crashy, patcher.mod", report);
        Assert.Contains("  patcher.mod: Il2Cpp.Sample_Crasher.Explode", report);
    }

    [Fact]
    public void Without_a_Player_prev_log_the_last_sessions_Player_log_is_used()
    {
        // Starting with -batchmode or -logFile does not rotate Player.log.
        WriteMarker(999_999, DateTime.UtcNow.AddMinutes(-10));
        var playerLog = Path.Combine(_locations.LocalLowDirectory, "Some Studio", "Some Game", "Player.log");
        Directory.CreateDirectory(Path.GetDirectoryName(playerLog)!);
        File.WriteAllText(playerLog, CrashedPlayerLog);
        File.SetLastWriteTimeUtc(playerLog, DateTime.UtcNow.AddMinutes(-5));

        var evidence = CrashEvidence.Find(Assert.Single(SessionMarker.FindAbandoned(_logs)), _data, _locations, currentProcessStartUtc: DateTime.UtcNow.AddMinutes(-1));
        var current = CrashEvidence.Find(Assert.Single(SessionMarker.FindAbandoned(_logs)), _data, _locations, currentProcessStartUtc: DateTime.UtcNow.AddMinutes(-6));

        Assert.Equal(playerLog, evidence.PlayerLogPath);
        Assert.True(evidence.IsCrash);
        Assert.Null(current.PlayerLogPath); // Written after this process started: it's the current session's own log.
    }

    [Fact]
    public void A_crash_the_games_own_crash_reporter_recorded_counts_as_a_crash()
    {
        // Sentry's native SDK (BOMBANANA!) handles the crash itself, so there is no Windows or Unity record, only Sentry's
        // database.
        WriteMarker(999_999, DateTime.UtcNow.AddMinutes(-10));
        var database = Path.Combine(_locations.LocalLowDirectory, "Some Studio", "Some Game", ".sentry-native");
        Directory.CreateDirectory(Path.Combine(database, "reports"));
        var crashedUtc = DateTime.UtcNow.AddMinutes(-9);
        File.WriteAllText(Path.Combine(database, "last_crash"), crashedUtc.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture));
        var dump = Path.Combine(database, "reports", "670ac8ef-cb07-42d3-1f74-819fab2d43eb.dmp");
        File.WriteAllText(dump, "MDMP");
        File.SetLastWriteTimeUtc(dump, crashedUtc);

        Reporter().ReportAbandonedSessions(SessionMarker.FindAbandoned(_logs), previousLog: null);

        var report = File.ReadAllText(Assert.Single(Directory.GetFiles(_reports, "crash-*.txt")));
        Assert.Contains("The game's own crash reporter (Sentry) recorded the crash", report);
        Assert.Contains($"The game's crash reporter's dump: {dump}", report);
    }

    [Fact]
    public void The_game_crash_reporters_record_of_an_earlier_session_isnt_evidence()
    {
        WriteMarker(999_999, DateTime.UtcNow.AddMinutes(-10));
        var database = Path.Combine(_locations.LocalLowDirectory, "Some Studio", "Some Game", ".sentry-native");
        Directory.CreateDirectory(database);
        File.WriteAllText(Path.Combine(database, "last_crash"), DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture));

        Reporter().ReportAbandonedSessions(SessionMarker.FindAbandoned(_logs), previousLog: null);

        Assert.False(Directory.Exists(_reports));
        Assert.Contains(_log.From("Crash"), m => m.Contains("did not exit normally"));
    }

    [Fact]
    public void A_crash_dump_of_the_sessions_process_counts_as_a_crash()
    {
        WriteMarker(4242, DateTime.UtcNow.AddMinutes(-1));
        Directory.CreateDirectory(_locations.CrashDumpsDirectory);
        var dump = Path.Combine(_locations.CrashDumpsDirectory, "Game.exe.4242.dmp");
        File.WriteAllText(dump, "MDMP");

        Reporter().ReportAbandonedSessions(SessionMarker.FindAbandoned(_logs), previousLog: null);

        var report = File.ReadAllText(Assert.Single(Directory.GetFiles(_reports)));
        Assert.Contains($"Crash dump: {dump}", report);
    }

    [Fact]
    public void A_fatal_exception_report_names_the_mods_whose_code_threw()
    {
        var marker = SessionMarker.TryRead(WriteMarker(999_999))!;
        var thrower = new LoadedMod(
            new DiscoveredMod(new ModManifest { Id = "test.thrower", Name = "Thrower", Version = SemanticVersion.Parse("1.0.0"), Assembly = "x.dll" }, _root),
            typeof(CrashReportTests).Assembly,
            null);
        var bystander = new LoadedMod(
            new DiscoveredMod(new ModManifest { Id = "test.bystander", Name = "Bystander", Version = SemanticVersion.Parse("1.0.0"), Assembly = "y.dll" }, _root),
            typeof(object).Assembly,
            null);
        Exception exception;
        try
        {
            throw new InvalidOperationException("mod code failed");
        }
        catch (InvalidOperationException e)
        {
            exception = e;
        }

        var path = Reporter().ReportFatalException(marker, exception, [thrower, bystander], currentLog: null);

        var report = File.ReadAllText(path!);
        Assert.Contains("because of an unhandled managed exception", report);
        Assert.Contains("These mods are on the exception's stack: test.thrower", report);
        Assert.Contains("mod code failed", report);
    }

    private CrashReporter Reporter(WindowsCrashEvents.Records? eventLog = null)
    {
        var hub = new LogHub { MinimumLevel = LogLevel.Debug };
        hub.AddSink(_log);
        // Never the actual event log of this computer.
        return new CrashReporter(_reports, _data, _locations, hub.For("Crash"), (_, _, _) => eventLog ?? new WindowsCrashEvents.Records(null, null, null));
    }

    // In the format of wevtutil's /f:xml output: two logged crashes, of which only one belongs to this session's process.
    private const string EventLogXml = """
        <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='.NET Runtime'/><EventID Qualifiers='0'>1026</EventID><TimeCreated SystemTime='2026-10-05T12:42:12.8327053Z'/></System><EventData><Data>Application: Game.exe&#xD;&#xA;CoreCLR Version: 10.0.926.27113&#xD;&#xA;Description: The process was terminated due to an unhandled exception.&#xD;&#xA;Stack:&#xD;&#xA;   at DynamicClass.DMD&lt;DMD&lt;&gt;?653::RuntimeModified_Il2Cpp.Sample_Crasher::Explode&gt;(Il2Cpp.Sample_Crasher)</Data></EventData></Event>
        <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Application Error' Guid='{a0e9b465-b939-57d7-b27d-95d8e925ff57}'/><EventID>1000</EventID><TimeCreated SystemTime='2026-10-05T12:42:13.0030106Z'/></System><EventData><Data Name='AppName'>Game.exe</Data><Data Name='ModuleName'>coreclr.dll</Data><Data Name='ModuleVersion'>10.0.926.27113</Data><Data Name='ExceptionCode'>c0000005</Data><Data Name='FaultingOffset'>0000000000356fef</Data><Data Name='ProcessId'>0x1092</Data><Data Name='ModulePath'>C:\Games\Game\Lustral\dotnet\coreclr.dll</Data></EventData></Event>
        <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Application Error'/><EventID>1000</EventID><TimeCreated SystemTime='2026-10-05T12:50:00.0000000Z'/></System><EventData><Data Name='AppName'>Game.exe</Data><Data Name='ModuleName'>UnityPlayer.dll</Data><Data Name='ExceptionCode'>c0000005</Data><Data Name='FaultingOffset'>1</Data><Data Name='ProcessId'>0x9999</Data></EventData></Event>
        """;

    [Fact]
    public void The_crash_output_is_read_from_the_log_in_Unitys_crash_folder()
    {
        // Unity 2018 writes the stack trace to the log copy in its crash folder, not to the Player.log in LocalLow.
        var started = DateTime.UtcNow.AddMinutes(-10);
        WriteMarker(999_999, started, [new SessionMod("lustral.samples.crashy", "0.1.0", "Crashy", "Lustral.Samples.Crashy", "Lustral.Samples.Crashy")]);
        var folder = Directory.CreateDirectory(Path.Combine(_locations.TempDirectory, "Some Studio", "Some Game", "Crashes", "Crash_2026-10-10_000751311"));
        File.WriteAllText(Path.Combine(folder.FullName, "error.log"), "Some Game by Some Studio [version: Unity 2018.4.36f1_6cd387d23174]");
        File.WriteAllText(Path.Combine(folder.FullName, "output_log.txt"), CrashedPlayerLog);

        Reporter().ReportAbandonedSessions(SessionMarker.FindAbandoned(_logs), previousLog: null);

        var report = File.ReadAllText(Assert.Single(Directory.GetFiles(_reports, "crash-*.txt")));
        Assert.Contains($"Unity's crash output ({Path.Combine(folder.FullName, "output_log.txt")})", report);
        Assert.Contains("0x00007FFB12345679 (Lustral.Samples.Crashy) Lustral.Samples.Crashy.Boom", report);
        Assert.Contains("These mods appear in the crash records, or patched a method that does: lustral.samples.crashy", report);
    }

    [Fact]
    public void Windows_crash_records_are_matched_by_process()
    {
        var records = WindowsCrashEvents.Parse(EventLogXml, 0x1092, "Game.exe");

        Assert.Equal("coreclr.dll 10.0.926.27113 faulted with exception 0xc0000005 at offset 0x0000000000356fef", records.Fault);
        Assert.Equal(@"C:\Games\Game\Lustral\dotnet\coreclr.dll", records.FaultingModulePath);
        Assert.StartsWith("Application: Game.exe\r\nCoreCLR Version", records.ManagedCrash);
        Assert.Contains("RuntimeModified_Il2Cpp.Sample_Crasher::Explode>(Il2Cpp.Sample_Crasher)", records.ManagedCrash);
        Assert.Null(WindowsCrashEvents.Parse(EventLogXml, 0x1234, "Game.exe").Fault);
    }

    [Fact]
    public void A_crash_dotnet_reported_names_the_mod_whose_patch_was_on_the_stack()
    {
        var path = WriteMarker(4242, DateTime.UtcNow.AddMinutes(-10), [new SessionMod("patcher.mod", "1.0.0", "Patcher", "Patcher", "Patcher")]);
        File.AppendAllLines(path, ["patch=patcher.mod|Il2Cpp.Sample_Crasher.Explode"]);
        var records = WindowsCrashEvents.Parse(EventLogXml, 0x1092, "Game.exe");

        Reporter(records).ReportAbandonedSessions(SessionMarker.FindAbandoned(_logs), previousLog: null);

        var report = File.ReadAllText(Assert.Single(Directory.GetFiles(_reports, "crash-*.txt")));
        Assert.Contains("These mods appear in the crash records, or patched a method that does: patcher.mod", report);
        Assert.Contains("Windows' record of the crash", report);
        Assert.Contains("coreclr.dll 10.0.926.27113 faulted with exception 0xc0000005", report);
        Assert.Contains(".NET's record of the crash", report);
        Assert.Contains("RuntimeModified_Il2Cpp.Sample_Crasher::Explode", report);
    }

    private string WriteMarker(int pid, DateTime? startedUtc = null, SessionMod[]? mods = null)
    {
        var path = Path.Combine(_logs, $"session-{pid}.txt");
        var lines = new List<string>
        {
            $"pid={pid}",
            $"started={(startedUtc ?? DateTime.UtcNow.AddMinutes(-1)).ToString("o")}",
            "lustral=0.0.1",
            "runtime=.NET 10.0.9",
            $"game={Path.Combine(_root, "Game", "Game.exe")}",
            "title=Some Game 1.2 by Some Studio",
            "unity=6000.3.10f1",
            "backend=IL2CPP (Il2CppInterop V2)",
        };
        lines.AddRange((mods ?? []).Select(m => $"mod={m.Id}|{m.Version}|{m.Name}|{m.Assembly}|{m.Namespace}"));
        File.WriteAllLines(path, lines);
        return path;
    }

}
