using Lustral.Core.Logging;
using Microsoft.Extensions.Logging;
using LogLevel = Lustral.Core.Logging.LogLevel;
using MsLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Lustral.Backend.Il2Cpp;

/// <summary>Routes Il2CppInterop's <see cref="ILogger"/> output into Lustral's log.</summary>
internal sealed class HubLogger(LogHub hub, string source) : ILogger
{
    // Il2CppInterop warnings about expected behavior, which would only concern users reading the log. Class::Init: its byte
    // signatures match few builds, so Il2CppInterop calls an export that runs Class::Init internally (seen on Unity 2022.3).
    private static readonly string[] RoutineWarnings = ["Class::Init signatures have been exhausted"];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(MsLogLevel logLevel) => logLevel != MsLogLevel.None && hub.IsEnabled(Map(logLevel));

    public void Log<TState>(MsLogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;
        var level = Map(logLevel);
        var message = formatter(state, exception);
        if (level == LogLevel.Warn && RoutineWarnings.Any(message.StartsWith))
            level = LogLevel.Debug;
        if (hub.IsEnabled(level))
            hub.Write(level, source, message, exception);
    }

    private static LogLevel Map(MsLogLevel level) => level switch
    {
        MsLogLevel.Trace or MsLogLevel.Debug => LogLevel.Debug,
        MsLogLevel.Information => LogLevel.Info,
        MsLogLevel.Warning => LogLevel.Warn,
        _ => LogLevel.Error,
    };
}
