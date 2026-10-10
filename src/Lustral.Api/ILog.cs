namespace Lustral;

/// <summary>
/// A log whose messages are tagged with their source. Messages are written to <c>Lustral/Logs/latest.log</c> and, when
/// enabled, to the console window.
/// </summary>
/// <remarks>
/// Messages may contain Unity rich text tags, which the console window renders and log files omit:
/// <c>&lt;color=red&gt;</c> or <c>&lt;color=#ff8800&gt;</c> (a Unity color name, or <c>#rgb</c>/<c>#rrggbb</c> with
/// optional alpha), <c>&lt;b&gt;</c>, <c>&lt;i&gt;</c>, <c>&lt;u&gt;</c> and <c>&lt;s&gt;</c>, each with a closing tag:
/// <c>log.Info("Health: &lt;color=red&gt;low&lt;/color&gt;")</c>. As in Unity, a tag without a closing tag is written as
/// plain text.
/// </remarks>
public interface ILog
{
    /// <summary>Writes a debug message. Debug messages are written only when the log level is Debug.</summary>
    void Debug(string message);

    /// <summary>Writes an informational message.</summary>
    void Info(string message);

    /// <summary>Writes a warning.</summary>
    void Warn(string message);

    /// <summary>Writes an error, including the exception and its stack trace if one is given.</summary>
    void Error(string message, Exception? exception = null);
}
