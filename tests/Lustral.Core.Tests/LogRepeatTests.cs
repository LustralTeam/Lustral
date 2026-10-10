using Lustral.Core.Logging;

namespace Lustral.Core.Tests;

public class LogRepeatTests
{
    private readonly ListSink _sink = new();
    private readonly LogHub _hub = new();
    private DateTime _now = new(2026, 10, 6, 12, 0, 0);

    public LogRepeatTests()
    {
        _hub.Clock = () => _now;
        _hub.AddSink(_sink);
    }

    [Fact]
    public void A_line_repeated_every_frame_is_written_once_then_counted()
    {
        var log = _hub.For("StaminaStats");
        for (var frame = 0; frame < 300; frame++)
        {
            log.Error("Field not found: bool .CharacterData.dead");
            _now += TimeSpan.FromMilliseconds(16);
        }
        _hub.For("Lifecycle").Info("Game is quitting");

        Assert.Equal(
        [
            "[StaminaStats] Field not found: bool .CharacterData.dead",
            "[StaminaStats] (repeated 299 more times)",
            "[Lifecycle] Game is quitting",
        ], _sink.Lines);
        Assert.Equal(LogLevel.Error, _sink.Entries[1].Level);
    }

    [Fact]
    public void Repeats_that_go_on_get_a_running_count()
    {
        var log = _hub.For("Spam");
        for (var second = 0.0; second < 25; second += 0.5)
        {
            log.Warn("again");
            _now += TimeSpan.FromSeconds(0.5);
        }
        _hub.Flush();

        Assert.Equal(["[Spam] again", "[Spam] (repeated 20 more times)", "[Spam] (repeated 20 more times)", "[Spam] (repeated 9 more times)"], _sink.Lines);
    }

    [Fact]
    public void One_repeat_is_written_as_itself()
    {
        var log = _hub.For("Mod");
        log.Info("Saved");
        log.Info("Saved");
        log.Info("Done");

        Assert.Equal(["[Mod] Saved", "[Mod] Saved", "[Mod] Done"], _sink.Lines);
    }

    [Fact]
    public void The_same_line_after_a_pause_is_not_a_repeat()
    {
        var log = _hub.For("Mod");
        log.Info("Autosaved");
        _now += TimeSpan.FromMinutes(5);
        log.Info("Autosaved");

        Assert.Equal(["[Mod] Autosaved", "[Mod] Autosaved"], _sink.Lines);
    }

    [Fact]
    public void Held_repeats_come_out_once_they_have_been_quiet()
    {
        var log = _hub.For("Mod");
        for (var i = 0; i < 5; i++)
            log.Info("burst");

        _hub.FlushIdleRepeats();
        Assert.Single(_sink.Lines);

        _now += LogHub.RepeatWindow;
        _hub.FlushIdleRepeats();
        Assert.Equal(["[Mod] burst", "[Mod] (repeated 4 more times)"], _sink.Lines);
    }

    [Fact]
    public void The_timer_writes_the_count_when_repeats_stop()
    {
        var sink = new ListSink();
        using var hub = new LogHub();
        hub.AddSink(sink);
        for (var i = 0; i < 3; i++)
            hub.For("Mod").Info("tick");

        var deadline = DateTime.Now + LogHub.RepeatWindow + TimeSpan.FromSeconds(5);
        while (sink.Entries.Count < 2 && DateTime.Now < deadline)
            Thread.Sleep(50);

        lock (sink.Entries)
            Assert.Equal(["[Mod] tick", "[Mod] (repeated 2 more times)"], sink.Lines);
    }

    [Fact]
    public void Lines_differing_in_source_level_or_exception_are_not_repeats()
    {
        _hub.For("A").Info("same");
        _hub.For("B").Info("same");
        _hub.For("B").Warn("same");
        _hub.For("B").Error("same", new InvalidOperationException("one"));
        _hub.For("B").Error("same", new InvalidOperationException("two"));

        Assert.Equal(5, _sink.Lines.Count);
    }

    [Fact]
    public void Disposing_writes_what_is_held()
    {
        var log = _hub.For("Mod");
        for (var i = 0; i < 3; i++)
            log.Info("x");
        _hub.Dispose();

        Assert.Equal(["[Mod] x", "[Mod] (repeated 2 more times)"], _sink.Lines);
    }

    private sealed class ListSink : ILogSink
    {
        public List<LogEntry> Entries { get; } = [];

        public List<string> Lines => Entries.Select(e => $"[{e.Source}] {e.Message}").ToList();

        public void Write(in LogEntry entry) => Entries.Add(entry);

        public void Dispose()
        {
        }
    }
}
