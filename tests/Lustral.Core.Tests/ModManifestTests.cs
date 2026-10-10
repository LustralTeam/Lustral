using Lustral.Core.Mods;

namespace Lustral.Core.Tests;

public class ModManifestTests
{
    [Fact]
    public void Parses_a_full_mod_manifest()
    {
        var manifest = ModManifest.Parse("""
            id = "sxxr0.betterhud"
            name = "Better HUD"
            version = "1.2.0"
            assembly = "BetterHud.dll"

            [dependencies]
            "someone.uilib" = "^2.1.0"
            """);

        Assert.Equal("sxxr0.betterhud", manifest.Id);
        Assert.Equal("Better HUD", manifest.Name);
        Assert.Equal(SemanticVersion.Create(1, 2, 0), manifest.Version);
        Assert.Equal(ModKind.Mod, manifest.Kind);
        Assert.Equal("BetterHud.dll", manifest.Assembly);
        Assert.Equal("^2.1.0", manifest.Dependencies["someone.uilib"].Text);
        Assert.Empty(manifest.Exports);
    }

    [Fact]
    public void Library_exports_are_stored_without_the_dll_extension()
    {
        var manifest = ModManifest.Parse("""
            id = "someone.uilib"
            version = "2.1.0"
            kind = "library"
            exports = ["UILib.dll", "UILib.Contracts"]
            """);

        Assert.Equal(ModKind.Library, manifest.Kind);
        Assert.Null(manifest.Assembly);
        Assert.Equal(["UILib", "UILib.Contracts"], manifest.Exports);
        Assert.Equal("someone.uilib", manifest.Name);
    }

    [Fact]
    public void Parses_everything_the_SDK_writes()
    {
        var manifest = ModManifest.Parse("""
            id = "sxxr0.betterhud"
            name = "Better HUD"
            version = "1.2.0"
            assembly = "BetterHud.dll"
            description = "A cleaner HUD."
            authors = ["sxxr0", "someone"]
            credits = "Icons by someone else"
            license = "MIT"
            load-order = -5
            color = "#58c4dc"
            some-future-key = "ignored"

            [links]
            website = "https://example.com/betterhud"
            source = "https://github.com/sxxr0/betterhud"

            [requires]
            lustral = ">=0.1.0, <1.0.0"
            unity = ">=2022.3"
            backends = ["mono"]
            platforms = ["windows-x64", "linux"]
            processes = ["Lethal Company.exe"]
            games = [{ name = "Lethal Company", developer = "Zeekerss", versions = ["v72", "v73*"] }, { name = "Lethal Company VR" }]

            [dependencies]
            "someone.uilib" = "^2.1.0"

            [optional-dependencies]
            "someone.extras" = "*"

            [incompatibilities]
            "other.hud" = "<2.0"
            """);

        Assert.Equal("A cleaner HUD.", manifest.Description);
        Assert.Equal(["sxxr0", "someone"], manifest.Authors);
        Assert.Equal("Icons by someone else", manifest.Credits);
        Assert.Equal("MIT", manifest.License);
        Assert.Equal(-5, manifest.LoadOrder);
        Assert.Equal("#58c4dc", manifest.Color);
        Assert.Equal("https://github.com/sxxr0/betterhud", manifest.Links["source"]);
        Assert.Equal(2, manifest.Links.Count);

        var requires = manifest.Requires;
        Assert.True(requires.Lustral!.IsSatisfiedBy(SemanticVersion.Create(0, 4, 0)));
        Assert.False(requires.Lustral.IsSatisfiedBy(SemanticVersion.Create(1, 0, 0)));
        Assert.True(requires.Unity!.IsSatisfiedBy(SemanticVersion.Create(2022, 3, 62)));
        Assert.Equal(["mono"], requires.Backends);
        Assert.Equal([new PlatformTarget("windows", "x64"), new PlatformTarget("linux", null)], requires.Platforms);
        Assert.Equal(["Lethal Company"], requires.Processes);
        Assert.Equal(2, requires.Games.Count);
        Assert.Equal(("Lethal Company", "Zeekerss"), (requires.Games[0].Name, requires.Games[0].Developer));
        Assert.Equal(["v72", "v73*"], requires.Games[0].Versions);
        Assert.Null(requires.Games[1].Developer);

        Assert.Equal("^2.1.0", manifest.Dependencies["someone.uilib"].Text);
        Assert.Equal("*", manifest.OptionalDependencies["someone.extras"].Text);
        Assert.Equal("<2.0", manifest.Incompatibilities["other.hud"].Text);
    }

    [Fact]
    public void An_update_source_is_a_GitHub_repository_or_a_feed()
    {
        var github = ModManifest.Parse("id = \"a.b\"\nversion = \"1.0.0\"\nassembly = \"A.dll\"\n[update]\nsource = \"github:sxxr0/better-hud\"\nasset = \"BetterHud-*.zip\"");
        var feed = ModManifest.Parse("id = \"a.b\"\nversion = \"1.0.0\"\nassembly = \"A.dll\"\n[update]\nsource = \"https://mods.example/feed.json\"");

        Assert.Equal(("sxxr0/better-hud", "BetterHud-*.zip"), (github.Update!.GitHubRepository, github.Update.Asset));
        Assert.Equal(new Uri("https://mods.example/feed.json"), feed.Update!.Feed);
        Assert.Null(ModManifest.Parse("id = \"a.b\"\nversion = \"1.0.0\"\nassembly = \"A.dll\"").Update);
        Assert.Contains("github:owner/repository", Assert.Throws<ManifestException>(() =>
            ModManifest.Parse("id = \"a.b\"\nversion = \"1.0.0\"\nassembly = \"A.dll\"\n[update]\nsource = \"http://mods.example/feed.json\"")).Message);
    }

    [Fact]
    public void Games_can_be_tables_and_one_author_a_string()
    {
        var manifest = ModManifest.Parse("""
            id = "a.b"
            version = "1.0.0"
            assembly = "A.dll"
            authors = "sxxr0"

            [[requires.games]]
            name = "PEAK"

            [[requires.games]]
            name = "Lethal Company"
            versions = ["0.1"]
            """);

        Assert.Equal(["sxxr0"], manifest.Authors);
        Assert.Equal(["PEAK", "Lethal Company"], manifest.Requires.Games.Select(g => g.Name));
        Assert.Equal(["0.1"], manifest.Requires.Games[1].Versions);
    }

    [Fact]
    public void A_manifest_without_the_new_keys_requires_nothing()
    {
        var manifest = ModManifest.Parse("id = \"a.b\"\nversion = \"1.0.0\"\nassembly = \"A.dll\"");

        Assert.Same(ModRequirements.None, manifest.Requires);
        Assert.Empty(manifest.Authors);
        Assert.Empty(manifest.OptionalDependencies);
        Assert.Empty(manifest.Incompatibilities);
        Assert.Equal(0, manifest.LoadOrder);
        Assert.Null(manifest.Color);
    }

    [Theory]
    [InlineData("[requires]\nbackends = [\"coreclr\"]", "\"mono\" or \"il2cpp\"")]
    [InlineData("[requires]\nplatforms = [\"win\"]", "windows, linux or macos")]
    [InlineData("[requires]\nplatforms = [\"linux-ppc\"]", "-x64, -x86 or -arm64")]
    [InlineData("[requires]\nlustral = \"latest\"", "requires.lustral has an invalid version range")]
    [InlineData("[requires]\ngames = [{ developer = \"Zeekerss\" }]", "requires a name")]
    [InlineData("[requires]\ngames = \"Lethal Company\"", "array of tables")]
    [InlineData("color = \"sparkly\"", "Unity color name")]
    [InlineData("load-order = \"first\"", "must be an integer")]
    [InlineData("[links]\nsource = \"file:///C:/evil\"", "HTTP or HTTPS")]
    [InlineData("[dependencies]\n\"x.y\" = \"*\"\n[optional-dependencies]\n\"x.y\" = \"*\"", "both")]
    [InlineData("[dependencies]\n\"x.y\" = \"*\"\n[incompatibilities]\n\"x.y\" = \"*\"", "both a dependency and an incompatibility")]
    [InlineData("[incompatibilities]\n\"a.b\" = \"*\"", "itself")]
    public void Rejects_bad_new_keys_with_a_readable_reason(string extra, string expected)
    {
        var error = Assert.Throws<ManifestException>(() => ModManifest.Parse("id = \"a.b\"\nversion = \"1.0.0\"\nassembly = \"A.dll\"\n" + extra));
        Assert.Contains(expected, error.Message);
    }

    [Theory]
    [InlineData("version = \"1.0.0\"\nassembly = \"A.dll\"", "\"id\" is required")]
    [InlineData("id = \"Bad Id\"\nversion = \"1.0.0\"\nassembly = \"A.dll\"", "lowercase")]
    [InlineData("id = \"a.b\"\nversion = \"1.0.0\"", "entry assembly")]
    [InlineData("id = \"a.b\"\nversion = \"1.0.0\"\nassembly = \"sub/A.dll\"", ".dll file name")]
    [InlineData("id = \"a.b\"\nversion = \"1.0.0\"\nkind = \"plugin\"\nassembly = \"A.dll\"", "kind")]
    [InlineData("id = \"a.b\"\nversion = \"1.0.0\"\nkind = \"library\"", "exports")]
    [InlineData("id = \"a.b\"\nversion = \"1.0.0\"\nassembly = \"A.dll\"\nexports = [\"A\"]", "only libraries")]
    [InlineData("id = \"a.b\"\nversion = \"1.0.0\"\nassembly = \"A.dll\"\n[dependencies]\n\"a.b\" = \"*\"", "itself")]
    [InlineData("id = \"a.b\"\nversion = 1\nassembly = \"A.dll\"", "non-empty string")]
    [InlineData("id = = ", "not valid TOML")]
    public void Rejects_bad_manifests_with_a_readable_reason(string toml, string expected)
    {
        var error = Assert.Throws<ManifestException>(() => ModManifest.Parse(toml));
        Assert.Contains(expected, error.Message);
    }
}
