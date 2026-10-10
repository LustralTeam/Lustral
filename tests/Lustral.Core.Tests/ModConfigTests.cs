using System.ComponentModel;
using Lustral.Core.Config;
using Lustral.Core.Logging;

namespace Lustral.Core.Tests;

public sealed class ModConfigTests : IDisposable
{
    public enum Quality
    {
        Low,
        Medium,
        High,
    }

    public sealed class Settings
    {
        [Description("Show the frame rate in the corner.")]
        public bool ShowFps { get; set; }

        public float Volume { get; set; } = 0.8f;

        public int MaxBananas { get; set; } = 3;

        public string Greeting { get; set; } = "hello";

        public Quality Quality { get; set; } = Quality.Medium;

        public List<string> Players { get; set; } = ["ann", "bo"];

        public int[] Levels { get; set; } = [1, 2];

        public string? Nickname { get; set; }

        [Description("Where the HUD goes.")]
        public HudSettings Hud { get; set; } = new();

        public int ReadOnlyIsNotASetting => 7;
    }

    public sealed class HudSettings
    {
        public double Scale { get; set; } = 1;

        public bool Visible { get; set; } = true;
    }

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lustral-tests", Guid.NewGuid().ToString("N"));
    private readonly MemorySink _log = new();

    public ModConfigTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string FilePath => Path.Combine(_directory, "some.mod.toml");

    private TomlModConfig Config()
    {
        var hub = new LogHub { MinimumLevel = LogLevel.Debug };
        hub.AddSink(_log);
        return new TomlModConfig(FilePath, "Some Mod", hub.For("some.mod"));
    }

    [Fact]
    public void Without_a_file_the_defaults_apply_and_get_written_with_comments()
    {
        var settings = Config().Bind<Settings>();

        Assert.Equal(0.8f, settings.Volume);
        Assert.Equal(["ann", "bo"], settings.Players);
        var text = File.ReadAllText(FilePath);
        Assert.Contains("# Settings for Some Mod.", text);
        Assert.Contains("# Show the frame rate in the corner.\nshow_fps = false", Normalize(text));
        Assert.Contains("volume = 0.8\n", Normalize(text));
        Assert.Contains("max_bananas = 3\n", Normalize(text));
        Assert.Contains("# One of: Low, Medium, High\nquality = \"Medium\"", Normalize(text));
        Assert.Contains("players = [\"ann\", \"bo\"]", text);
        Assert.Contains("levels = [1, 2]", text);
        Assert.Contains("# Where the HUD goes.\n[hud]\n\nscale = 1.0\n", Normalize(text));
        Assert.DoesNotContain("nickname", text);
        Assert.DoesNotContain("read_only", text);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Binding_writes_a_schema_for_the_Hub_beside_the_file()
    {
        Config().Bind<Settings>();

        var schemaPath = Path.Combine(_directory, "some.mod.schema.json");
        var schema = Lustral.Core.Updates.Json.Object(Lustral.Core.Updates.Json.Parse(File.ReadAllText(schemaPath)))!;
        Assert.Equal((1.0, "some.mod", "Some Mod"), ((double)schema["format"]!, schema["mod"], schema["name"]));
        var settings = Lustral.Core.Updates.Json.Array(schema["settings"]).Select(Lustral.Core.Updates.Json.Object).ToDictionary(s => (string)s!["key"]!, s => s!);
        Assert.Equal(["show_fps", "volume", "max_bananas", "greeting", "quality", "players", "levels", "nickname", "hud"], settings.Keys);
        Assert.Equal(("bool", false, "Show the frame rate in the corner."), (settings["show_fps"]["type"], settings["show_fps"]["default"], settings["show_fps"]["description"]));
        Assert.Equal(("number", 0.8), (settings["volume"]["type"], Math.Round((double)settings["volume"]["default"]!, 6)));
        Assert.Equal(("integer", 3.0), (settings["max_bananas"]["type"], settings["max_bananas"]["default"]));
        Assert.Equal(["Low", "Medium", "High"], Lustral.Core.Updates.Json.Array(settings["quality"]["options"]));
        Assert.Equal(("enum", "Medium"), (settings["quality"]["type"], settings["quality"]["default"]));
        Assert.Equal(("list", "string"), (settings["players"]["type"], settings["players"]["items"]));
        Assert.Equal(["ann", "bo"], Lustral.Core.Updates.Json.Array(settings["players"]["default"]));
        Assert.Equal((true, null), (settings["nickname"]["nullable"], settings["nickname"]["default"]));
        var hud = Lustral.Core.Updates.Json.Array(settings["hud"]["settings"]).Select(Lustral.Core.Updates.Json.Object).ToList();
        Assert.Equal(("section", "Where the HUD goes."), (settings["hud"]["type"], settings["hud"]["description"]));
        Assert.Equal(["scale", "visible"], hud.Select(h => (string)h!["key"]!));

        var written = File.GetLastWriteTimeUtc(schemaPath);
        File.SetLastWriteTimeUtc(schemaPath, written.AddMinutes(-5));
        Config().Bind<Settings>();
        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(schemaPath)); // unchanged, so not rewritten
    }

    [Fact]
    public void Edited_values_are_read_back()
    {
        Config().Bind<Settings>();
        File.WriteAllText(FilePath, """
            show_fps = true
            volume = 1
            max_bananas = 12
            greeting = "hi \"there\""
            quality = "high"
            players = ["cy"]
            levels = [5, 6, 7]
            nickname = "Banana King"

            [hud]
            scale = 1.5
            visible = false
            """);

        var settings = Config().Bind<Settings>();

        Assert.True(settings.ShowFps);
        Assert.Equal(1f, settings.Volume);
        Assert.Equal(12, settings.MaxBananas);
        Assert.Equal("hi \"there\"", settings.Greeting);
        Assert.Equal(Quality.High, settings.Quality);
        Assert.Equal(["cy"], settings.Players);
        Assert.Equal([5, 6, 7], settings.Levels);
        Assert.Equal("Banana King", settings.Nickname);
        Assert.Equal(1.5, settings.Hud.Scale);
        Assert.False(settings.Hud.Visible);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void A_missing_setting_is_added_and_the_players_values_stay()
    {
        File.WriteAllText(FilePath, "show_fps = true\nmax_bananas = 9\n");

        var settings = Config().Bind<Settings>();

        Assert.True(settings.ShowFps);
        Assert.Equal(9, settings.MaxBananas);
        var text = File.ReadAllText(FilePath);
        Assert.Contains("show_fps = true", text);
        Assert.Contains("max_bananas = 9", text);
        Assert.Contains("volume = 0.8", text);
        Assert.Contains("[hud]", text);
    }

    [Fact]
    public void A_complete_file_isnt_rewritten()
    {
        Config().Bind<Settings>();
        var written = File.GetLastWriteTimeUtc(FilePath);
        File.SetLastWriteTimeUtc(FilePath, written.AddMinutes(-5));

        Config().Bind<Settings>();

        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(FilePath));
    }

    [Fact]
    public void Wrong_values_and_unknown_settings_are_logged_and_ignored()
    {
        Config().Bind<Settings>();
        var text = File.ReadAllText(FilePath)
            .Replace("max_bananas = 3", "max_bananas = \"lots\"")
            .Replace("quality = \"Medium\"", "quality = \"Ultra\"")
            .Replace("show_fps = false", "show_fps = false\nshow_fsp = true")
            .Replace("[hud]", "[hud]\nopacity = 0.5");
        File.WriteAllText(FilePath, text);

        var settings = Config().Bind<Settings>();

        Assert.Equal(3, settings.MaxBananas);
        Assert.Equal(Quality.Medium, settings.Quality);
        var warnings = _log.From("some.mod").ToList();
        Assert.Contains("some.mod.toml: max_bananas = \"lots\" is not an integer within the range of Int32; using 3", warnings);
        Assert.Contains("some.mod.toml: quality = \"Ultra\" is not one of Low, Medium, High; using \"Medium\"", warnings);
        Assert.Contains("some.mod.toml: unknown setting show_fsp; check the spelling", warnings);
        Assert.Contains("some.mod.toml: unknown setting hud.opacity; check the spelling", warnings);
    }

    [Fact]
    public void An_integer_too_big_for_its_setting_is_ignored()
    {
        File.WriteAllText(FilePath, "max_bananas = 99999999999\n");

        Assert.Equal(3, Config().Bind<Settings>().MaxBananas);
        Assert.Contains(_log.From("some.mod"), m => m.Contains("max_bananas = 99999999999 is not an integer within the range of Int32"));
    }

    [Fact]
    public void A_broken_file_keeps_every_default_and_is_left_alone()
    {
        const string broken = "show_fps = true\nvolume = = 2\n";
        File.WriteAllText(FilePath, broken);

        var settings = Config().Bind<Settings>();

        Assert.False(settings.ShowFps);
        Assert.Equal(broken, File.ReadAllText(FilePath));
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("is not valid TOML"));
    }

    [Fact]
    public void Save_writes_the_current_values()
    {
        var config = Config();
        var settings = config.Bind<Settings>();
        settings.Volume = 0.25f;
        settings.Greeting = "line one\nline \\ two";
        settings.Hud.Visible = false;

        config.Save(settings);
        var reread = Config().Bind<Settings>();

        Assert.Equal(0.25f, reread.Volume);
        Assert.Equal("line one\nline \\ two", reread.Greeting);
        Assert.False(reread.Hud.Visible);
    }

    [Theory]
    [InlineData("ShowFps", "show_fps")]
    [InlineData("HUDScale", "hud_scale")]
    [InlineData("Level2Name", "level2_name")]
    [InlineData("volume", "volume")]
    [InlineData("IO", "io")]
    public void Names_are_snake_case(string name, string expected) => Assert.Equal(expected, TomlModConfig.SnakeCase(name));

    private static string Normalize(string text) => text.Replace("\r\n", "\n");
}
