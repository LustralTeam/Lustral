using Lustral.Core.Config;
using Lustral.Core.Logging;
using Lustral.Core.Mods;

namespace Lustral.Core.Tests;

public class LoaderConfigTests
{
    [Fact]
    public void The_default_file_parses_to_the_defaults()
    {
        var problems = new List<string>();
        var config = LoaderConfig.Parse(LoaderConfig.DefaultText, problems);

        Assert.Empty(problems);
        Assert.Equal(LogLevel.Info, config.LogLevel);
        Assert.Empty(config.DisabledMods);
        Assert.Empty(config.ForcedMods);
        Assert.False(config.Console);
    }

    [Fact]
    public void Reads_the_log_level_and_disabled_mods()
    {
        var problems = new List<string>();
        var config = LoaderConfig.Parse("""
            [log]
            level = "Debug"
            console = true

            [mods]
            disabled = ["sxxr0.betterhud", "someone.uilib"]
            force = ["old.mod"]
            """, problems);

        Assert.Empty(problems);
        Assert.Equal(LogLevel.Debug, config.LogLevel);
        Assert.True(config.Console);
        Assert.Equal(["someone.uilib", "sxxr0.betterhud"], config.DisabledMods.Order());
        Assert.Equal(["old.mod"], config.ForcedMods);
    }

    [Theory]
    [InlineData("[log]\nlevel = \"loud\"", "must be \"debug\", \"info\", \"warn\" or \"error\"")]
    [InlineData("[log]\nlevel = \"7\"", "must be \"debug\"")]
    [InlineData("[mods]\ndisabled = \"a.mod\"", "must be a list of mod IDs")]
    [InlineData("[mods]\nforce = [1]", "no mods are forced")]
    [InlineData("[log]\nconsole = \"yes\"", "must be true or false")]
    [InlineData("log = 3", "must be a [log] section")]
    [InlineData("[log\nlevel = ", "is not valid TOML")]
    public void A_bad_setting_falls_back_to_its_default_with_a_reason(string toml, string expected)
    {
        var problems = new List<string>();
        var config = LoaderConfig.Parse(toml, problems);

        Assert.Contains(problems, p => p.Contains(expected));
        Assert.Equal(LogLevel.Info, config.LogLevel);
        Assert.Empty(config.DisabledMods);
    }

    [Fact]
    public void Load_creates_the_file_with_comments_when_it_is_missing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lustral-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var config = LoaderConfig.Load(directory, out var problems);

            Assert.Empty(problems);
            Assert.Equal(LogLevel.Info, config.LogLevel);
            Assert.Contains("[mods]", File.ReadAllText(Path.Combine(directory, LoaderConfig.FileName)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Disabling_a_library_skips_its_dependents()
    {
        var plan = ModResolver.Resolve([Mod("ui.lib"), Mod("a.mod", "ui.lib"), Mod("b.mod")], disabled: ["ui.lib"]);

        Assert.Equal(["b.mod"], plan.Ordered.Select(m => m.Id));
        Assert.Contains(plan.Disabled, p => p.Mod == "ui.lib" && p.Reason == "disabled in loader.toml");
        Assert.Contains(plan.Disabled, p => p.Mod == "a.mod" && p.Reason == "requires ui.lib, which is disabled");
    }

    private static DiscoveredMod Mod(string id, params string[] dependencies) => new(
        new ModManifest
        {
            Id = id,
            Name = id,
            Version = SemanticVersion.Parse("1.0.0"),
            Assembly = id + ".dll",
            Dependencies = dependencies.ToDictionary(d => d, _ => VersionRange.Any),
        },
        Path.Combine("Mods", id));
}
