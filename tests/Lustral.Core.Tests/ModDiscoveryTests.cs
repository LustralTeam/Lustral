using Lustral.Core.Mods;

namespace Lustral.Core.Tests;

public sealed class ModDiscoveryTests : IDisposable
{
    private readonly string _mods = Path.Combine(Path.GetTempPath(), "lustral-tests", Guid.NewGuid().ToString("N"), "Mods");

    public ModDiscoveryTests() => Directory.CreateDirectory(_mods);

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_mods)!, recursive: true);

    private void Mod(string folder, string id = "someone.betterhud")
    {
        Directory.CreateDirectory(Path.Combine(_mods, folder));
        File.WriteAllText(Path.Combine(_mods, folder, ModManifest.FileName), $"id = \"{id}\"\nversion = \"1.0.0\"\nassembly = \"BetterHud.dll\"");
    }

    private void File_(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_mods, path))!);
        File.WriteAllText(Path.Combine(_mods, path), "x");
    }

    [Fact]
    public void Finds_mods_in_their_folders()
    {
        Mod("someone.betterhud");

        var (mods, problems) = ModDiscovery.Scan(_mods);

        Assert.Equal("someone.betterhud", Assert.Single(mods).Id);
        Assert.Empty(problems);
    }

    [Fact]
    public void A_mod_extracted_one_folder_too_deep_is_pointed_out()
    {
        Mod(Path.Combine("someone.betterhud-1.2.0", "someone.betterhud"));

        var (mods, problems) = ModDiscovery.Scan(_mods);

        Assert.Empty(mods);
        Assert.Equal("someone.betterhud-1.2.0: is nested one folder too deep; move someone.betterhud-1.2.0/someone.betterhud into Mods",
            Assert.Single(problems).ToString());
    }

    [Fact]
    public void Zips_and_loose_dlls_are_pointed_out()
    {
        File_("someone.betterhud-1.2.0.zip");
        File_("SomeMelonMod.dll");
        File_("readme.txt");

        var (_, problems) = ModDiscovery.Scan(_mods);

        Assert.Collection(problems.OrderBy(p => p.Mod, StringComparer.Ordinal),
            p => Assert.StartsWith("SomeMelonMod.dll: is not in a mod folder", p.ToString()),
            p => Assert.StartsWith("someone.betterhud-1.2.0.zip: is a zip archive", p.ToString()));
    }

    [Fact]
    public void A_folder_with_dlls_but_no_manifest_is_pointed_out()
    {
        File_(Path.Combine("SomePlugin", "SomePlugin.dll"));

        var (_, problems) = ModDiscovery.Scan(_mods);

        Assert.StartsWith("SomePlugin: has no mod.toml", Assert.Single(problems).ToString());
    }

    [Fact]
    public void Other_folders_without_a_manifest_are_left_alone()
    {
        File_(Path.Combine("notes", "readme.txt"));
        File_(Path.Combine(".git", "config.dll"));
        Directory.CreateDirectory(Path.Combine(_mods, "empty"));

        var (mods, problems) = ModDiscovery.Scan(_mods);

        Assert.Empty(mods);
        Assert.Empty(problems);
    }
}
