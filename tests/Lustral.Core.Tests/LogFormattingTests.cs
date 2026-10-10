using System.Text;
using System.Text.RegularExpressions;
using Lustral.Core.Logging;

namespace Lustral.Core.Tests;

public class LogFormattingTests
{
    private const string Esc = "\u001b";

    private static readonly LogEntry Entry = new(new DateTime(2026, 10, 6, 1, 2, 3, 456), LogLevel.Warn, "MoreCompany",
        "Lobby size <color=#ff8800>32</color> is <b>big</b>", null);

    [Theory]
    [InlineData("<color=red>RED TEXT</color>", "RED TEXT")]
    [InlineData("<color=#FFFFFF>WHITE TEXT</color> and <b>bold</b>", "WHITE TEXT and bold")]
    [InlineData("<i>a</i><u>b</u><s>c</s><size=20>d</size>", "abcd")]
    [InlineData("<color=red>a<color=#0f0>b</color>c</color>", "abc")]
    [InlineData("<COLOR=\"Orange\">loud</COLOR>", "loud")]
    [InlineData("<color='#12345678'>alpha</color>", "alpha")]
    public void Files_get_the_text_without_its_tags(string message, string expected) =>
        Assert.Equal(expected, RichText.Strip(message));

    [Theory]
    [InlineData("Foo/<Bar>d__3::<i>5__2")]
    [InlineData("Dictionary<string, List<int>>")]
    [InlineData("a < b and b > c")]
    [InlineData("<color=notacolor>x</color>")]
    [InlineData("<color=#12345>x</color>")]
    [InlineData("<b=1>x</b>")]
    [InlineData("<size>x</size>")]
    [InlineData("closing </b> with nothing open")]
    [InlineData("<blink>x</blink>")]
    public void Text_that_only_looks_like_tags_is_left_alone(string message) =>
        Assert.Equal(message, RichText.Strip(message));

    [Fact]
    public void A_tag_left_open_inside_a_pair_is_text()
    {
        Assert.Equal("<i>x", RichText.Strip("<b><i>x</b>"));
    }

    [Theory]
    [InlineData("red", 0xFF, 0x00, 0x00)]
    [InlineData("Grey", 0x80, 0x80, 0x80)]
    [InlineData("gray", 0x80, 0x80, 0x80)]
    [InlineData("#f80", 0xFF, 0x88, 0x00)]
    [InlineData("#f80c", 0xFF, 0x88, 0x00)]
    [InlineData("#a1B2c3", 0xA1, 0xB2, 0xC3)]
    [InlineData("#a1b2c3ff", 0xA1, 0xB2, 0xC3)]
    public void Reads_Unitys_color_names_and_hex_colors(string value, int r, int g, int b)
    {
        Assert.True(RichText.TryParseColor(value, out var color));
        Assert.Equal(new Rgb((byte)r, (byte)g, (byte)b), color);
    }

    [Fact]
    public void Nested_colors_come_back_as_their_tags_close()
    {
        var builder = new StringBuilder();
        RichText.AppendAnsi(builder, "<color=red>a<color=#00ff00>b</color>c</color>d", "<base>");

        var red = $"{Esc}[38;2;255;0;0m";
        var green = $"{Esc}[38;2;0;255;0m";
        var reset = $"{Esc}[0m";
        Assert.Equal($"<base>{reset}<base>{red}a{reset}<base>{green}b{reset}<base>{red}c{reset}<base>d", builder.ToString());
    }

    [Fact]
    public void Bold_italic_underline_and_strikethrough_combine()
    {
        var builder = new StringBuilder();
        RichText.AppendAnsi(builder, "<b><i><u><s>x</s></u></i></b>", "");

        Assert.Contains($"{Esc}[1m{Esc}[3m{Esc}[4m{Esc}[9mx", builder.ToString());
    }

    [Fact]
    public void The_file_line_has_no_tags()
    {
        Assert.Equal("[01:02:03.456] [Warn ] [MoreCompany] Lobby size 32 is big", LogHub.Format(Entry));
    }

    [Fact]
    public void The_console_line_is_the_file_line_in_color()
    {
        var colored = ConsoleLogSink.FormatColored(Entry);

        Assert.Contains(ConsoleLogSink.SourceColor("MoreCompany") + "MoreCompany", colored);
        Assert.Contains($"{Esc}[38;2;255;136;0m32", colored);
        Assert.Equal(LogHub.Format(Entry), Regex.Replace(colored, $@"{Esc}\[[0-9;]*m", ""));
    }

    [Fact]
    public void Exceptions_follow_the_console_line()
    {
        var entry = new LogEntry(DateTime.Now, LogLevel.Error, "Core", "<b>Broke</b>", new InvalidOperationException("boom"));

        Assert.Equal(LogHub.Format(entry), Regex.Replace(ConsoleLogSink.FormatColored(entry), $@"{Esc}\[[0-9;]*m", ""));
    }

    [Fact]
    public void A_source_keeps_its_color()
    {
        Assert.Equal(ConsoleLogSink.SourceColor("LethalConfig"), ConsoleLogSink.SourceColor("LethalConfig"));
        var colors = new[] { "Lustral", "Core", "BepInEx", "MoreCompany", "LethalLib", "More Suits", "HarmonyX", "Lifecycle" }
            .Select(ConsoleLogSink.SourceColor).Distinct();
        Assert.True(colors.Count() > 1);
    }
}
