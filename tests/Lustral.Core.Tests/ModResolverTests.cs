using Lustral.Core.Mods;

namespace Lustral.Core.Tests;

public class ModResolverTests
{
    [Fact]
    public void Dependencies_load_before_dependents_and_ties_sort_by_id()
    {
        var plan = ModResolver.Resolve([Mod("c.mod", "a.lib"), Mod("b.mod"), Mod("a.lib"), Mod("d.mod", "c.mod", "b.mod")]);

        Assert.Empty(plan.Disabled);
        Assert.Equal(["a.lib", "b.mod", "c.mod", "d.mod"], plan.Ordered.Select(m => m.Id));
    }

    [Fact]
    public void A_missing_dependency_disables_the_mod_and_everything_downstream()
    {
        var plan = ModResolver.Resolve([Mod("a.mod", "gone.lib"), Mod("b.mod", "a.mod"), Mod("c.mod")]);

        Assert.Equal(["c.mod"], plan.Ordered.Select(m => m.Id));
        Assert.Contains(plan.Disabled, p => p.Mod == "a.mod" && p.Reason.Contains("gone.lib") && p.Reason.Contains("not installed"));
        Assert.Contains(plan.Disabled, p => p.Mod == "b.mod" && p.Reason.Contains("a.mod, which will not be loaded"));
    }

    [Fact]
    public void A_cycle_disables_only_the_mods_in_it()
    {
        var plan = ModResolver.Resolve([Mod("a.mod", "b.mod"), Mod("b.mod", "a.mod"), Mod("c.mod")]);

        Assert.Equal(["c.mod"], plan.Ordered.Select(m => m.Id));
        Assert.Equal(["a.mod", "b.mod"], plan.Disabled.Select(p => p.Mod));
        Assert.All(plan.Disabled, p => Assert.Contains("cycle", p.Reason));
    }

    [Fact]
    public void A_duplicate_id_keeps_the_first_folder()
    {
        var first = Mod("a.mod") with { Directory = Path.Combine("Mods", "a1") };
        var second = Mod("a.mod") with { Directory = Path.Combine("Mods", "a2") };

        var plan = ModResolver.Resolve([second, first]);

        Assert.Same(first, Assert.Single(plan.Ordered));
        Assert.Contains("a1 already uses it", Assert.Single(plan.Disabled).Reason);
    }

    [Fact]
    public void A_mod_for_another_game_doesnt_load_and_neither_do_its_dependents()
    {
        var plan = ModResolver.Resolve(
            [With(Mod("a.lib"), requires: new ModRequirements { Games = [new GameTarget("PEAK", null, [])] }), Mod("b.mod", "a.lib"), Mod("c.mod")],
            environment: Environment());

        Assert.Equal(["c.mod"], plan.Ordered.Select(m => m.Id));
        Assert.Contains(plan.Disabled, p => p.Mod == "a.lib" && p.Reason == "is for PEAK; the running game is Lethal Company");
        Assert.Contains(plan.Disabled, p => p.Mod == "b.mod" && p.Reason == "requires a.lib, which will not be loaded");
    }

    [Fact]
    public void A_mod_for_the_other_backend_is_not_loaded_and_the_other_package_is_suggested()
    {
        var plan = ModResolver.Resolve(
            [With(Mod("a.mod"), requires: new ModRequirements { Backends = ["il2cpp"] })],
            environment: Environment());

        Assert.Empty(plan.Ordered);
        Assert.Equal("a.mod: is built for IL2CPP games; this game uses Mono (install the mod's Mono package instead, if it has one)",
            Assert.Single(plan.Disabled).ToString());
    }

    [Fact]
    public void A_forced_mod_loads_despite_its_requirements_with_a_warning()
    {
        var plan = ModResolver.Resolve(
            [With(Mod("a.mod"), requires: new ModRequirements { Backends = ["il2cpp"] })],
            environment: Environment(),
            force: ["a.mod"]);

        Assert.Equal(["a.mod"], plan.Ordered.Select(m => m.Id));
        Assert.Empty(plan.Disabled);
        Assert.Equal("a.mod: loading although it is built for IL2CPP games; this game uses Mono (forced in loader.toml)", Assert.Single(plan.Warnings).ToString());
    }

    [Fact]
    public void An_unknown_game_version_loads_the_mod_with_a_warning()
    {
        var plan = ModResolver.Resolve(
            [With(Mod("a.mod"), requires: new ModRequirements { Games = [new GameTarget("Lethal Company", null, ["v72"])] })],
            environment: Environment() with { GameVersion = null });

        Assert.Equal(["a.mod"], plan.Ordered.Select(m => m.Id));
        Assert.Contains("the version of Lethal Company is not yet known", Assert.Single(plan.Warnings).Reason);
    }

    [Fact]
    public void Optional_dependencies_load_first_when_present_and_are_skipped_when_not()
    {
        var plan = ModResolver.Resolve([With(Mod("a.mod"), optional: ["z.extra", "missing.mod"]), Mod("z.extra")]);

        Assert.Empty(plan.Disabled);
        Assert.Equal(["z.extra", "a.mod"], plan.Ordered.Select(m => m.Id));
    }

    [Fact]
    public void An_optional_dependency_at_a_version_the_mod_doesnt_accept_keeps_the_mod_out()
    {
        var plan = ModResolver.Resolve([With(Mod("a.mod"), optional: ["z.extra@^2.0"]), Mod("z.extra")]);

        Assert.Equal(["z.extra"], plan.Ordered.Select(m => m.Id));
        Assert.Equal("supports z.extra ^2.0 as an optional dependency, but version 1.0.0 is installed", Assert.Single(plan.Disabled).Reason);
    }

    [Fact]
    public void A_disabled_optional_dependency_counts_as_absent()
    {
        var plan = ModResolver.Resolve([With(Mod("a.mod"), optional: ["z.extra@^2.0"]), Mod("z.extra")], disabled: ["z.extra"]);

        Assert.Equal(["a.mod"], plan.Ordered.Select(m => m.Id));
    }

    [Fact]
    public void Optional_dependencies_on_each_other_still_load_both()
    {
        var plan = ModResolver.Resolve([With(Mod("b.mod"), optional: ["a.mod"]), With(Mod("a.mod"), optional: ["b.mod"])]);

        Assert.Empty(plan.Disabled);
        Assert.Equal(["a.mod", "b.mod"], plan.Ordered.Select(m => m.Id));
    }

    [Fact]
    public void An_incompatible_mod_takes_only_itself_out()
    {
        var plan = ModResolver.Resolve([With(Mod("a.mod"), incompatible: ["b.mod"]), Mod("b.mod"), Mod("c.mod", "a.mod")]);

        Assert.Equal(["b.mod"], plan.Ordered.Select(m => m.Id));
        Assert.Contains(plan.Disabled, p => p.Mod == "a.mod" && p.Reason == "is incompatible with b.mod 1.0.0 (b.mod); remove one of them");
        Assert.Contains(plan.Disabled, p => p.Mod == "c.mod" && p.Reason == "requires a.mod, which will not be loaded");
    }

    [Fact]
    public void Mods_incompatible_with_each_other_both_stay_out()
    {
        var plan = ModResolver.Resolve([With(Mod("a.mod"), incompatible: ["b.mod"]), With(Mod("b.mod"), incompatible: ["a.mod"])]);

        Assert.Empty(plan.Ordered);
        Assert.Equal(["a.mod", "b.mod"], plan.Disabled.Select(p => p.Mod));
    }

    [Fact]
    public void An_incompatibility_applies_only_to_the_versions_it_names()
    {
        var plan = ModResolver.Resolve([With(Mod("a.mod"), incompatible: ["b.mod@<1.0"]), Mod("b.mod")]);

        Assert.Equal(["a.mod", "b.mod"], plan.Ordered.Select(m => m.Id));
    }

    [Fact]
    public void An_incompatibility_with_a_mod_that_doesnt_load_doesnt_count()
    {
        var plan = ModResolver.Resolve([With(Mod("a.mod"), incompatible: ["b.mod"]), Mod("b.mod")], disabled: ["b.mod"]);

        Assert.Equal(["a.mod"], plan.Ordered.Select(m => m.Id));
    }

    [Fact]
    public void A_forced_mod_loads_beside_a_mod_it_says_its_incompatible_with()
    {
        var plan = ModResolver.Resolve([With(Mod("a.mod"), incompatible: ["b.mod"]), Mod("b.mod")], force: ["a.mod"]);

        Assert.Equal(["a.mod", "b.mod"], plan.Ordered.Select(m => m.Id));
        Assert.Contains("incompatible with b.mod", Assert.Single(plan.Warnings).Reason);
    }

    [Fact]
    public void Load_order_sorts_independent_mods_but_never_beats_a_dependency()
    {
        var plan = ModResolver.Resolve([
            With(Mod("a.mod"), loadOrder: 10),
            With(Mod("b.mod"), loadOrder: -10),
            Mod("c.mod"),
            With(Mod("d.mod", "a.mod"), loadOrder: -100),
        ]);

        Assert.Equal(["b.mod", "c.mod", "a.mod", "d.mod"], plan.Ordered.Select(m => m.Id));
    }

    private static ModEnvironment Environment() => new(
        SemanticVersion.Create(0, 1, 0), "mono", new PlatformTarget("windows", "x64"), "Lethal Company",
        "Lethal Company", "ZeekerssRBLX", "0.1", "2022.3.62f2");

    // optional and incompatible accept "id" or "id@range".
    private static DiscoveredMod With(DiscoveredMod mod, ModRequirements? requires = null, string[]? optional = null, string[]? incompatible = null, int loadOrder = 0)
    {
        static Dictionary<string, VersionRange> Ranges(string[]? items) =>
            (items ?? []).Select(i => i.Split('@')).ToDictionary(p => p[0], p => p.Length > 1 ? VersionRange.Parse(p[1]) : VersionRange.Any);
        var m = mod.Manifest;
        return mod with
        {
            Manifest = new ModManifest
            {
                Id = m.Id,
                Name = m.Name,
                Version = m.Version,
                Assembly = m.Assembly,
                Dependencies = m.Dependencies,
                Requires = requires ?? ModRequirements.None,
                OptionalDependencies = Ranges(optional),
                Incompatibilities = Ranges(incompatible),
                LoadOrder = loadOrder,
            },
        };
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
