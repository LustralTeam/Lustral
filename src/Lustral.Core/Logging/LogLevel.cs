namespace Lustral.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Source, string Message, Exception? Exception);

public interface ILogSink : IDisposable
{
    void Write(in LogEntry entry);
}
