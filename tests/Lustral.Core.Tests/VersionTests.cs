using Lustral.Core.Mods;

namespace Lustral.Core.Tests;

public class VersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("1.2", 1, 2, 0, "")]
    [InlineData("4", 4, 0, 0, "")]
    [InlineData("1.0.0-beta.2+build.7", 1, 0, 0, "beta.2")]
    public void Parses_semantic_versions(string text, int major, int minor, int patch, string prerelease)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version));
        Assert.Equal(SemanticVersion.Create(major, minor, patch, prerelease), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("-1.0.0")]
    public void Rejects_what_is_not_a_semantic_version(string text) =>
        Assert.False(SemanticVersion.TryParse(text, out _));

    [Fact]
    public void Orders_prereleases_before_their_release()
    {
        string[] ordered = ["1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0"];
        var parsed = ordered.Select(SemanticVersion.Parse).ToList();
        Assert.Equal(ordered, parsed.Order().Select(v => v.ToString()));
    }

    [Theory]
    [InlineData("^2.1.0", "2.1.0", true)]
    [InlineData("^2.1.0", "2.9.4", true)]
    [InlineData("^2.1.0", "3.0.0", false)]
    [InlineData("^2.1.0", "2.0.9", false)]
    [InlineData("^0.3.1", "0.3.9", true)]
    [InlineData("^0.3.1", "0.4.0", false)]
    [InlineData("^0.0.3", "0.0.4", false)]
    [InlineData("2.1.0", "2.5.0", true)]
    [InlineData("2.1.0", "3.0.0", false)]
    [InlineData("~1.4.2", "1.4.9", true)]
    [InlineData("~1.4.2", "1.5.0", false)]
    [InlineData(">=1.2.0 <2.0.0", "1.9.9", true)]
    [InlineData(">=1.2.0 <2.0.0", "2.0.0", false)]
    [InlineData("=1.2.3", "1.2.3", true)]
    [InlineData("=1.2.3", "1.2.4", false)]
    [InlineData("*", "0.0.1", true)]
    [InlineData(">1.0.0", "1.0.0", false)]
    [InlineData("<=1.0.0", "1.0.0", true)]
    public void Ranges_match_like_npm_and_cargo(string range, string version, bool expected) =>
        Assert.Equal(expected, VersionRange.Parse(range).IsSatisfiedBy(SemanticVersion.Parse(version)));

    [Fact]
    public void A_bad_range_says_which_part_is_wrong()
    {
        Assert.False(VersionRange.TryParse(">=1.0.0 <two", out _, out var error));
        Assert.Contains("\"<two\"", error);
    }

    [Fact]
    public void An_installed_version_outside_the_range_disables_the_dependent()
    {
        DiscoveredMod Mod(string id, string version, string? dependency = null) => new(
            ModManifest.Parse($"id = \"{id}\"\nversion = \"{version}\"\nassembly = \"{id}.dll\"" + (dependency is null ? "" : $"\n[dependencies]\n{dependency}")),
            Path.Combine("Mods", id));

        var plan = ModResolver.Resolve([Mod("someone.uilib", "1.9.0"), Mod("sxxr0.betterhud", "1.2.0", "\"someone.uilib\" = \"^2.1.0\"")]);

        Assert.Equal(["someone.uilib"], plan.Ordered.Select(m => m.Id));
        Assert.Equal("sxxr0.betterhud: requires someone.uilib ^2.1.0, but version 1.9.0 is installed", Assert.Single(plan.Disabled).ToString());
    }

    [Theory]
    [InlineData("id = \"a.b\"\nversion = \"one\"\nassembly = \"A.dll\"", "is not a semantic version")]
    [InlineData("id = \"a.b\"\nversion = \"1.0.0\"\nassembly = \"A.dll\"\n[dependencies]\n\"c.d\" = \"latest\"", "invalid version range")]
    public void Manifests_reject_bad_versions(string toml, string expected) =>
        Assert.Contains(expected, Assert.Throws<ManifestException>(() => ModManifest.Parse(toml)).Message);
}
