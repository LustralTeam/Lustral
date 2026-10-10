using Lustral.Core.Mods;

namespace Lustral.Core.Tests;

public class ModEnvironmentTests
{
    private static readonly ModEnvironment Here = new(
        SemanticVersion.Create(0, 3, 0), "mono", new PlatformTarget("windows", "x64"), "Lethal Company",
        "Lethal Company", "ZeekerssRBLX", "0.1", "2022.3.62f2");

    [Fact]
    public void A_mod_without_requirements_runs_anywhere()
    {
        Assert.Null(Check(ModRequirements.None, out var @unchecked));
        Assert.Empty(@unchecked);
    }

    [Theory]
    [InlineData("^0.3", null)]
    [InlineData(">=0.4.0", "requires Lustral >=0.4.0; the installed version is 0.3.0")]
    public void Checks_the_Lustral_version(string range, string? expected) =>
        Assert.Equal(expected, Check(new ModRequirements { Lustral = VersionRange.Parse(range) }, out _));

    [Theory]
    [InlineData(">=2022.3", null)]
    [InlineData("=2022.3.62", null)]
    [InlineData(">=6000.0", "requires Unity >=6000.0; the game uses Unity 2022.3.62f2")]
    public void Checks_the_Unity_version_without_its_release_suffix(string range, string? expected) =>
        Assert.Equal(expected, Check(new ModRequirements { Unity = VersionRange.Parse(range) }, out _));

    [Fact]
    public void Checks_the_backend_platform_and_process()
    {
        Assert.Equal("is built for IL2CPP games; this game uses Mono", Check(new ModRequirements { Backends = ["il2cpp"] }, out _));
        Assert.Null(Check(new ModRequirements { Backends = ["il2cpp", "mono"] }, out _));
        Assert.Equal("requires linux or macos-arm64; this system is windows-x64",
            Check(new ModRequirements { Platforms = [new PlatformTarget("linux", null), new PlatformTarget("macos", "arm64")] }, out _));
        Assert.Null(Check(new ModRequirements { Platforms = [new PlatformTarget("windows", null)] }, out _));
        Assert.Null(Check(new ModRequirements { Platforms = [new PlatformTarget("windows", "x64")] }, out _));
        Assert.Equal("is for the executable Lethal Company VR; the running executable is Lethal Company", Check(new ModRequirements { Processes = ["Lethal Company VR"] }, out _));
        Assert.Null(Check(new ModRequirements { Processes = ["lethal company"] }, out _));
    }

    [Theory]
    [InlineData("Lethal Company", null, null)]
    [InlineData("lethal company", "zeekerssrblx", null)]
    [InlineData("Lethal Company", "Someone Else", "is for Lethal Company by Someone Else; the running game is Lethal Company")]
    [InlineData("PEAK", null, "is for PEAK; the running game is Lethal Company")]
    public void Matches_games_by_name_and_developer_ignoring_case(string name, string? developer, string? expected) =>
        Assert.Equal(expected, Check(new ModRequirements { Games = [new GameTarget(name, developer, [])] }, out _));

    [Theory]
    [InlineData("0.1", null)]
    [InlineData("0.*", null)]
    [InlineData("*", null)]
    [InlineData("0.2", "is for Lethal Company 0.2; the installed version is 0.1")]
    [InlineData("0.1.*", "is for Lethal Company 0.1.*; the installed version is 0.1")]
    public void Matches_game_versions_exactly_or_with_wildcards(string version, string? expected) =>
        Assert.Equal(expected, Check(new ModRequirements { Games = [new GameTarget("Lethal Company", null, [version])] }, out _));

    [Fact]
    public void Any_listed_game_will_do()
    {
        var requires = new ModRequirements { Games = [new GameTarget("PEAK", null, ["1.0"]), new GameTarget("Lethal Company", null, ["0.1"])] };
        Assert.Null(Check(requires, out _));
    }

    [Fact]
    public void Facts_Lustral_couldnt_read_dont_stop_a_mod_but_are_reported()
    {
        var unknown = Here with { GameName = null, GameVersion = null, UnityVersion = null, LustralVersion = null };
        var requires = new ModRequirements
        {
            Lustral = VersionRange.Parse("^9.0"),
            Unity = VersionRange.Parse(">=6000.0"),
            Games = [new GameTarget("PEAK", null, ["1.0"])],
        };
        var @unchecked = new List<string>();

        Assert.Null(unknown.Check(Manifest(requires), @unchecked));
        Assert.Equal(3, @unchecked.Count);

        var versionOnly = new List<string>();
        Assert.Null((Here with { GameVersion = null }).Check(Manifest(new ModRequirements { Games = [new GameTarget("Lethal Company", null, ["0.1"])] }), versionOnly));
        Assert.Equal("the supported game versions (0.1): the version of Lethal Company is not yet known", Assert.Single(versionOnly));
    }

    private static string? Check(ModRequirements requires, out List<string> @unchecked)
    {
        @unchecked = [];
        return Here.Check(Manifest(requires), @unchecked);
    }

    private static ModManifest Manifest(ModRequirements requires) => new()
    {
        Id = "a.mod",
        Name = "A",
        Version = SemanticVersion.Create(1, 0, 0),
        Assembly = "A.dll",
        Requires = requires,
    };
}
