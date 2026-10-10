using System.Globalization;

namespace Lustral.Core.Logging;

/// <summary>
/// Owns the log sinks and provides a log for each source. Thread-safe.
/// </summary>
/// <remarks>
/// A line repeated in quick succession, such as a mod logging the same error every frame, is written once and then
/// counted. When the repetitions stop, a single line reports the count ("(repeated 6091 more times)"); while they
/// continue, a count is written every <see cref="RepeatReport"/>.
/// </remarks>
public sealed class LogHub : IDisposable
{
    /// <summary>A line written again within this interval of its previous occurrence is a repetition.</summary>
    internal static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(2);

    /// <summary>The interval at which a running count is written for continuing repetitions.</summary>
    internal static readonly TimeSpan RepeatReport = TimeSpan.FromSeconds(10);

    private readonly object _lock = new();
    private readonly List<ILogSink> _sinks = [];

    private LogEntry? _last;
    private LogEntry _held;
    private int _heldCount;
    private DateTime _lastSeen;
    private DateTime _lastWritten;
    private Timer? _idle;

    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;

    /// <summary>The clock used to timestamp entries; tests replace it.</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public void AddSink(ILogSink sink)
    {
        lock (_lock)
            _sinks.Add(sink);
    }

    public ILog For(string source) => new SourceLog(this, source);

    public bool IsEnabled(LogLevel level) => level >= MinimumLevel;

    public void Write(LogLevel level, string source, string message, Exception? exception = null)
    {
        if (!IsEnabled(level))
            return;

        lock (_lock)
        {
            var entry = new LogEntry(Clock(), level, source, message, exception);
            if (_last is { } last && SameLine(last, entry) && entry.Time - _lastSeen < RepeatWindow)
            {
                _held = entry;
                _heldCount++;
                _lastSeen = entry.Time;
                if (entry.Time - _lastWritten >= RepeatReport)
                    WriteHeld();
                else
                    ArmIdleTimer();
                return;
            }

            WriteHeld();
            Send(entry);
            _last = entry;
            _lastSeen = _lastWritten = entry.Time;
        }
    }

    /// <summary>Writes the count of pending repetitions when the game shuts down.</summary>
    public void Flush()
    {
        lock (_lock)
            WriteHeld();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            WriteHeld();
            _idle?.Dispose();
            foreach (var sink in _sinks)
                sink.Dispose();
            _sinks.Clear();
        }
    }

    /// <summary>Formats the line as written to log files, with rich text tags removed (<see cref="RichText"/>).</summary>
    public static string Format(in LogEntry entry)
    {
        var line = $"[{FormatTime(entry.Time)}] [{LevelName(entry.Level)}] [{entry.Source}] {RichText.Strip(entry.Message)}";
        return entry.Exception is null ? line : line + Environment.NewLine + entry.Exception;
    }

    internal static string FormatTime(DateTime time) => time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    internal static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "Debug",
        LogLevel.Info => "Info ",
        LogLevel.Warn => "Warn ",
        LogLevel.Error => "Error",
        _ => level.ToString(),
    };

    /// <summary>Writes pending repetitions after none have occurred for <see cref="RepeatWindow"/>; called by the idle timer.</summary>
    internal void FlushIdleRepeats()
    {
        lock (_lock)
        {
            if (_heldCount > 0 && Clock() - _lastSeen >= RepeatWindow)
                WriteHeld();
        }
    }

    private static bool SameLine(in LogEntry a, in LogEntry b) =>
        a.Level == b.Level && a.Source == b.Source && a.Message == b.Message &&
        a.Exception?.GetType() == b.Exception?.GetType() && a.Exception?.Message == b.Exception?.Message &&
        a.Exception?.StackTrace == b.Exception?.StackTrace;

    // A single pending repetition is written as is; several are written as a count, with the time of the last one.
    private void WriteHeld()
    {
        if (_heldCount == 0)
            return;
        Send(_heldCount == 1 ? _held : _held with { Message = $"(repeated {_heldCount} more times)", Exception = null });
        _lastWritten = _held.Time;
        _heldCount = 0;
    }

    private void ArmIdleTimer()
    {
        _idle ??= new Timer(_ => FlushIdleRepeats());
        _idle.Change(RepeatWindow + TimeSpan.FromMilliseconds(100), Timeout.InfiniteTimeSpan);
    }

    private void Send(in LogEntry entry)
    {
        foreach (var sink in _sinks)
        {
            try
            {
                sink.Write(entry);
            }
            catch
            {
                // A failing sink must never bring down the game.
            }
        }
    }

    private sealed class SourceLog(LogHub hub, string source) : ILog
    {
        public void Debug(string message) => hub.Write(LogLevel.Debug, source, message);
        public void Info(string message) => hub.Write(LogLevel.Info, source, message);
        public void Warn(string message) => hub.Write(LogLevel.Warn, source, message);
        public void Error(string message, Exception? exception = null) => hub.Write(LogLevel.Error, source, message, exception);
    }
}
