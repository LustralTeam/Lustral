using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Lustral.Core.Logging;

/// <summary>
/// A console window (Windows) that shows the log as it is written, in color: the time, the level, each source in its own
/// color, warnings and errors highlighted, and messages' rich text tags rendered (<see cref="RichText"/>).
/// </summary>
/// <remarks>
/// The console cannot pause or end the game. Its close button is removed, because closing a console window ends the
/// process it belongs to; the console closes with the game instead. Ctrl+C is ignored. Lines are written by a dedicated
/// thread, because a console blocks all writers while text is selected in it, and the game must not block with it. Lines
/// that arrive while the console is blocked are queued, up to <see cref="MaxPending"/>; the log file contains all lines.
/// </remarks>
public sealed class ConsoleLogSink : ILogSink
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint EnableVirtualTerminalProcessing = 0x4;
    private const uint ScClose = 0xF060;
    private const uint MfByCommand = 0x0;

    private static readonly string Dim = Ansi.Foreground(Rgb.Hex(0x6E7681));
    private static readonly string DebugText = Ansi.Foreground(Rgb.Hex(0x8B949E));
    private static readonly string InfoLevel = Ansi.Foreground(Rgb.Hex(0x3FB950));
    private static readonly string WarnText = Ansi.Foreground(Rgb.Hex(0xE3B341));
    private static readonly string ErrorText = Ansi.Foreground(Rgb.Hex(0xFF6B6B));
    private static readonly string ExceptionText = Ansi.Foreground(Rgb.Hex(0xD98080));

    // Source colors, selected by a hash of the name so that a mod keeps the same color across sessions. The hues are
    // distinct from each other and from the level colors (green, yellow, red and gray).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ChosenColors = new(StringComparer.Ordinal);

    private static readonly string[] SourceColors =
    [
        Ansi.Foreground(Rgb.Hex(0xFF9E64)), // orange
        Ansi.Foreground(Rgb.Hex(0xC3E88D)), // lime
        Ansi.Foreground(Rgb.Hex(0x2BC4A9)), // teal
        Ansi.Foreground(Rgb.Hex(0x7FDBFF)), // sky
        Ansi.Foreground(Rgb.Hex(0x5C9DFF)), // blue
        Ansi.Foreground(Rgb.Hex(0xA492FF)), // lavender
        Ansi.Foreground(Rgb.Hex(0xD076F0)), // purple
        Ansi.Foreground(Rgb.Hex(0xFF79C6)), // pink
    ];

    internal const int MaxPending = 20000;

    private readonly Queue<string> _pending = new();
    private readonly TextWriter _writer;
    private readonly bool _colors;
    private readonly Thread _thread;
    private int _dropped;
    private bool _closing;

    internal ConsoleLogSink(TextWriter writer, bool colors)
    {
        _writer = writer;
        _colors = colors;
        _thread = new Thread(WriteLines) { IsBackground = true, Name = "Lustral console" };
        _thread.Start();
    }

    /// <summary>Opens a console window for the game, or returns null if no Windows console is available.</summary>
    public static ConsoleLogSink? TryOpen(string title)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;
        try
        {
            if (GetConsoleWindow() == IntPtr.Zero && !AllocConsole())
                return null;
            SetConsoleTitle(title);
            SetConsoleOutputCP(65001);
            // Ctrl+C without a selection would otherwise end the game immediately.
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            var window = GetConsoleWindow();
            if (window != IntPtr.Zero)
                DeleteMenu(GetSystemMenu(window, false), ScClose, MfByCommand);

            // Read access is required as well; without it, Get/SetConsoleMode fail and colors are unavailable.
            var handle = CreateFile("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                return null;
            var colors = GetConsoleMode(handle, out var mode) && SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
            var writer = new StreamWriter(new FileStream(handle, FileAccess.Write), new UTF8Encoding(false));
            return new ConsoleLogSink(writer, colors);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException)
        {
            return null;
        }
    }

    public void Write(in LogEntry entry)
    {
        var line = _colors ? FormatColored(entry) : LogHub.Format(entry);
        lock (_pending)
        {
            if (_closing)
                return;
            if (_pending.Count >= MaxPending)
            {
                _dropped++;
                return;
            }
            _pending.Enqueue(line);
            Monitor.Pulse(_pending);
        }
    }

    // Writes queued lines in batches until the sink is closed and the queue is empty.
    private void WriteLines()
    {
        var batch = new List<string>();
        while (true)
        {
            int dropped;
            lock (_pending)
            {
                while (_pending.Count == 0 && !_closing)
                    Monitor.Wait(_pending);
                if (_pending.Count == 0)
                    return;
                batch.AddRange(_pending);
                _pending.Clear();
                dropped = _dropped;
                _dropped = 0;
            }
            try
            {
                foreach (var line in batch)
                    _writer.WriteLine(line);
                if (dropped > 0)
                    _writer.WriteLine($"({dropped} lines were omitted while the console was paused; see Lustral/Logs/latest.log)");
                _writer.Flush();
            }
            catch (IOException)
            {
                return;
            }
            batch.Clear();
        }
    }

    /// <summary>
    /// Formats a line as <see cref="LogHub.Format"/> does, with ANSI colors; removing the escape sequences yields the same
    /// text. Info messages use the console's default color.
    /// </summary>
    internal static string FormatColored(in LogEntry entry)
    {
        var (level, text) = entry.Level switch
        {
            LogLevel.Debug => (DebugText, DebugText),
            LogLevel.Info => (InfoLevel, ""),
            LogLevel.Warn => (WarnText, WarnText),
            _ => (ErrorText + Ansi.Bold, ErrorText),
        };
        var builder = new StringBuilder(entry.Message.Length + 160);
        builder.Append(Dim).Append('[').Append(LogHub.FormatTime(entry.Time)).Append("] ")
            .Append('[').Append(level).Append(LogHub.LevelName(entry.Level)).Append(Ansi.Reset).Append(Dim).Append("] ")
            .Append('[').Append(SourceColor(entry.Source)).Append(entry.Source).Append(Dim).Append("] ")
            .Append(Ansi.Reset);
        RichText.AppendAnsi(builder, entry.Message, text);
        builder.Append(Ansi.Reset);
        if (entry.Exception is not null)
            builder.Append(Environment.NewLine).Append(ExceptionText).Append(entry.Exception).Append(Ansi.Reset);
        return builder.ToString();
    }

    /// <summary>Sets the color of a source (a mod's ID), as requested in the mod's manifest, instead of a color derived from its name.</summary>
    internal static void SetSourceColor(string source, Rgb color) => ChosenColors[source] = Ansi.Foreground(color);

    internal static string SourceColor(string source)
    {
        if (ChosenColors.TryGetValue(source, out var chosen))
            return chosen;
        // FNV-1a, because string.GetHashCode differs between runs on .NET.
        var hash = 2166136261u;
        foreach (var c in source)
            hash = (hash ^ c) * 16777619u;
        return SourceColors[hash % (uint)SourceColors.Length];
    }

    /// <summary>Writes queued lines unless the console is paused; the game waits at most one second.</summary>
    public void Dispose()
    {
        lock (_pending)
        {
            _closing = true;
            Monitor.Pulse(_pending);
        }
        if (_thread.Join(TimeSpan.FromSeconds(1)))
            _writer.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SetConsoleTitle(string title);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleOutputCP(uint codePage);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool GetConsoleMode(SafeFileHandle handle, out uint mode);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleMode(SafeFileHandle handle, uint mode);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr window, bool revert);

    [DllImport("user32.dll")]
    private static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);

    [DllImport("kernel32.dll")]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
