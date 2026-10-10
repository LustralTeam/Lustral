using System.Diagnostics;
using System.Text.RegularExpressions;
using Lustral.Core.Logging;

namespace Lustral.Core.Tests;

public class ConsoleLogSinkTests
{
    [Fact]
    public void Writes_every_line_in_order()
    {
        var console = new PausableWriter();
        var sink = new ConsoleLogSink(console, colors: false);
        for (var i = 0; i < 500; i++)
            sink.Write(Entry($"line {i}"));
        sink.Dispose();

        var lines = console.Lines();
        Assert.Equal(500, lines.Length);
        Assert.Equal(Enumerable.Range(0, 500).Select(i => $"line {i}"), lines.Select(l => l.Substring(l.LastIndexOf("] ", StringComparison.Ordinal) + 2)));
    }

    [Fact]
    public void A_paused_console_doesnt_stop_the_game()
    {
        // A console with selected text blocks all writers until the selection ends.
        var console = new PausableWriter();
        console.Running.Reset();
        var sink = new ConsoleLogSink(console, colors: false);

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
            sink.Write(Entry($"line {i}"));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));

        console.Running.Set();
        sink.Dispose();
        Assert.Equal(1000, console.Lines().Length);
    }

    [Fact]
    public void Lines_that_dont_fit_while_paused_are_counted()
    {
        var console = new PausableWriter();
        console.Running.Reset();
        var sink = new ConsoleLogSink(console, colors: false);
        const int total = ConsoleLogSink.MaxPending + 100;
        for (var i = 0; i < total; i++)
            sink.Write(Entry($"line {i}"));

        console.Running.Set();
        sink.Dispose();

        var lines = console.Lines();
        var notice = Regex.Match(lines[^1], @"^\((\d+) lines were omitted while the console was paused");
        Assert.True(notice.Success, lines[^1]);
        Assert.Equal(total, lines.Length - 1 + int.Parse(notice.Groups[1].Value));
    }

    [Fact]
    public void Closing_doesnt_wait_for_a_paused_console()
    {
        var console = new PausableWriter();
        console.Running.Reset();
        var sink = new ConsoleLogSink(console, colors: false);
        sink.Write(Entry("waiting"));

        var watch = Stopwatch.StartNew();
        sink.Dispose();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        console.Running.Set();
    }

    private static LogEntry Entry(string message) => new(DateTime.Now, LogLevel.Info, "Test", message, null);

    private sealed class PausableWriter : StringWriter
    {
        public ManualResetEventSlim Running { get; } = new(true);

        public override void WriteLine(string? value)
        {
            Running.Wait();
            base.WriteLine(value);
        }

        public string[] Lines() => ToString().Split([Environment.NewLine], StringSplitOptions.RemoveEmptyEntries);
    }
}
