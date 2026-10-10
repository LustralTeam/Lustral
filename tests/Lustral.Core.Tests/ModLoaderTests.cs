using Lustral.Core.Loading;
using Lustral.Core.Logging;

namespace Lustral.Core.Tests;

/// <summary>
/// Loads mods compiled at test time through ModLoader, with the CoreCLR loader (one context per mod) and the Mono-style
/// loader (one shared domain).
/// </summary>
public class ModLoaderTests
{
    private static IReadOnlyList<LoadedMod> LoadWithContexts(ModFolder folder)
    {
        var logs = folder.CreateLogHub();
        var assemblies = new ContextModAssemblyLoader(name => name.Name == "Lustral.Api", logs.For("Core"));
        return new ModLoader(logs, assemblies, Path.Combine(folder.Root, "config")).Load(folder.Plan());
    }

    private static IReadOnlyList<LoadedMod> LoadInDomain(ModFolder folder)
    {
        var logs = folder.CreateLogHub();
        return new ModLoader(logs, new DomainModAssemblyLoader(logs.For("Core")), Path.Combine(folder.Root, "config")).Load(folder.Plan());
    }

    /// <summary>A mod whose OnLoad logs <paramref name="expression"/>.</summary>
    private static void LoggingMod(ModFolder folder, string id, string expression, string? dependencies = null, params string[] references)
    {
        var assembly = "Entry" + id.Replace(".", "");
        folder.Compile(id, assembly, $$"""
            public sealed class EntryMod : Lustral.Mod
            {
                public override void OnLoad() => Log.Info({{expression}});
            }
            """, references);
        folder.Manifest(id, $"""
            id = "{id}"
            version = "1.0.0"
            assembly = "{folder.Name(assembly)}.dll"
            {(dependencies is null ? "" : $"\n[dependencies]\n{dependencies}")}
            """);
    }

    private static string Library(ModFolder folder, string id, string assembly, string source, string? dependencies = null, params string[] references)
    {
        var path = folder.Compile(id, assembly, source, references);
        folder.Manifest(id, $"""
            id = "{id}"
            version = "2.1.0"
            kind = "library"
            exports = ["{folder.Name(assembly)}.dll"]
            {(dependencies is null ? "" : $"\n[dependencies]\n{dependencies}")}
            """);
        return path;
    }

    /// <summary>A class whose Context property returns the name of its assembly's load context.</summary>
    private static string ContextNameClass(string name) =>
        $"public static class {name} {{ public static string Context => System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof({name}).Assembly)!.Name!; }}";

    [Fact]
    public void Each_mod_gets_its_own_copy_of_a_private_library()
    {
        using var folder = new ModFolder();
        foreach (var (id, value) in new[] { ("a.mod", "first"), ("b.mod", "second") })
        {
            // The same assembly name with different contents, one in each mod's folder.
            var helper = folder.Compile(id, "Helper", $"public static class Helper {{ public static string Value => \"{value}\"; }}");
            LoggingMod(folder, id, "Helper.Value", references: helper);
        }

        var loaded = LoadWithContexts(folder);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(["first"], folder.Log.From("a.mod"));
        Assert.Equal(["second"], folder.Log.From("b.mod"));
    }

    [Fact]
    public void Dependents_share_a_library_s_export_from_the_library_s_context()
    {
        using var folder = new ModFolder();
        var widgets = Library(folder, "ui.lib", "Widgets", ContextNameClass("Widgets"));
        LoggingMod(folder, "a.mod", "Widgets.Context", "\"ui.lib\" = \"^2.0.0\"", widgets);
        LoggingMod(folder, "b.mod", "Widgets.Context", "\"ui.lib\" = \"^2.0.0\"", widgets);

        LoadWithContexts(folder);

        Assert.Equal(["Lustral mod ui.lib"], folder.Log.From("a.mod"));
        Assert.Equal(["Lustral mod ui.lib"], folder.Log.From("b.mod"));
    }

    [Fact]
    public void Exports_of_a_transitive_dependency_resolve_too()
    {
        using var folder = new ModFolder();
        var core = Library(folder, "core.lib", "CoreLib", ContextNameClass("CoreLib"));
        Library(folder, "ui.lib", "Widgets", "public static class Widgets { }", "\"core.lib\" = \"*\"", core);
        LoggingMod(folder, "a.mod", "CoreLib.Context", "\"ui.lib\" = \"*\"", core);

        LoadWithContexts(folder);

        Assert.Equal(["Lustral mod core.lib"], folder.Log.From("a.mod"));
    }

    [Fact]
    public void A_mod_that_ships_its_own_Lustral_Api_still_gets_the_shared_one()
    {
        using var folder = new ModFolder();
        LoggingMod(folder, "a.mod", "\"loaded\"");
        File.Copy(typeof(Mod).Assembly.Location, Path.Combine(folder.Root, "a.mod", "Lustral.Api.dll"));

        var loaded = LoadWithContexts(folder);

        Assert.IsAssignableFrom<Mod>(Assert.Single(loaded).Instance);
        Assert.Equal(["loaded"], folder.Log.From("a.mod"));
    }

    [Fact]
    public void A_failing_library_disables_its_dependents_only()
    {
        using var folder = new ModFolder();
        var boom = Library(folder, "boom.lib", "Boom", """
            public sealed class BoomMod : Lustral.Mod
            {
                public override void OnLoad() => throw new System.InvalidOperationException("boom");
            }
            """);
        // A library with an entry point also specifies its assembly.
        File.AppendAllText(Path.Combine(folder.Root, "boom.lib", "mod.toml"), $"\nassembly = \"{folder.Name("Boom")}.dll\"\n");
        LoggingMod(folder, "a.mod", "\"needs boom\"", "\"boom.lib\" = \"*\"", boom);
        LoggingMod(folder, "b.mod", "\"independent\"");

        var loaded = LoadWithContexts(folder);

        Assert.Equal(["b.mod"], loaded.Select(m => m.Mod.Id));
        Assert.Contains(folder.Log.Entries, e => e.Source == "Core" && e.Message == "boom.lib failed to load" && e.Exception?.Message == "boom");
        Assert.Contains("Not loading a.mod: requires boom.lib, which failed to load", folder.Log.From("Core"));
    }

    [Fact]
    public void A_missing_assembly_names_the_mod_that_needed_it()
    {
        using var folder = new ModFolder();
        // Built outside any mod folder, so nothing can resolve it at runtime.
        var missing = folder.Compile(".build", "Missing", "public static class Missing { public static string Value => \"x\"; }");
        LoggingMod(folder, "a.mod", "Missing.Value", references: missing);

        var loaded = LoadWithContexts(folder);

        Assert.Empty(loaded);
        Assert.Contains(folder.Log.From("Core"), m => m.StartsWith($"a.mod cannot find assembly {folder.Name("Missing")}", StringComparison.Ordinal));
        Assert.Contains(folder.Log.Entries, e => e.Message == "a.mod failed to load" && e.Exception is FileNotFoundException);
    }

    [Fact]
    public void In_a_shared_domain_exports_and_private_libraries_still_resolve()
    {
        using var folder = new ModFolder();
        var widgets = Library(folder, "ui.lib", "Widgets", "public static class Widgets { public static string Name => \"widgets\"; }");
        var helper = folder.Compile("a.mod", "Helper", "public static class Helper { public static string Name => \"helper\"; }");
        LoggingMod(folder, "a.mod", "Widgets.Name + \"+\" + Helper.Name", "\"ui.lib\" = \"*\"", widgets, helper);

        var loaded = LoadInDomain(folder);

        Assert.Equal(["ui.lib", "a.mod"], loaded.Select(m => m.Mod.Id));
        Assert.Equal(["widgets+helper"], folder.Log.From("a.mod"));
    }

    /// <summary>
    /// A mod that applies a postfix to its own Target.Value() (changing 1 to 2) through its Harmony instance, in OnLoad or in
    /// its constructor, and then optionally fails.
    /// </summary>
    private static string PatchingMod(ModFolder folder, string id, bool failAfterPatching, bool inConstructor = false)
    {
        var patch = $"""
            Harmony.Patch(typeof(Target).GetMethod("Value"), postfix: new HarmonyLib.HarmonyMethod(typeof(ValuePatch).GetMethod("Postfix")));
            {(failAfterPatching ? "throw new System.InvalidOperationException(\"failed after patching\");" : "")}
            """;
        var assembly = "Patching" + id.Replace(".", "");
        folder.Compile(id, assembly, $$"""
            public static class Target
            {
                public static int Value() => 1;
            }

            public static class ValuePatch
            {
                public static void Postfix(ref int __result) => __result = 2;
            }

            public sealed class EntryMod : Lustral.Mod
            {
                public EntryMod()
                {
                    {{(inConstructor ? patch : "")}}
                }

                public override void OnLoad()
                {
                    {{(inConstructor ? "" : patch)}}
                }
            }
            """, typeof(HarmonyLib.Harmony).Assembly.Location);
        folder.Manifest(id, $"""
            id = "{id}"
            version = "1.0.0"
            assembly = "{folder.Name(assembly)}.dll"
            """);
        return folder.Name(assembly);
    }

    private static int CallTargetValue(string assemblyName) =>
        (int)AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == assemblyName)
            .GetType("Target")!.GetMethod("Value")!.Invoke(null, null)!;

    [Fact]
    public void A_mod_gets_its_own_Tomlyn_in_a_shared_domain()
    {
        // Lustral ships Tomlyn renamed (Lustral.Tomlyn), so a mod's Tomlyn, of any version, remains the mod's own.
        using var folder = new ModFolder();
        var tomlyn = folder.CompileExactly("toml.mod", "Tomlyn", "namespace Tomlyn { public static class Marker { public static string Who => \"the mod's own Tomlyn\"; } }");
        LoggingMod(folder, "toml.mod", "Tomlyn.Marker.Who", references: tomlyn);

        var loaded = LoadInDomain(folder);

        Assert.Single(loaded);
        Assert.Equal(["the mod's own Tomlyn"], folder.Log.From("toml.mod"));
        Assert.DoesNotContain(typeof(ModLoader).Assembly.GetReferencedAssemblies(), a => a.Name == "Tomlyn");
    }

    [Fact]
    public void A_mods_Harmony_patches_are_its_own()
    {
        using var folder = new ModFolder();
        var assembly = PatchingMod(folder, "patching.mod", failAfterPatching: false);

        var loaded = LoadInDomain(folder);

        var mod = Assert.Single(loaded);
        Assert.Equal("patching.mod", mod.Harmony!.Id);
        Assert.Equal(["Target.Value"], mod.Harmony.GetPatchedMethods().Select(m => $"{m.DeclaringType!.Name}.{m.Name}"));
        Assert.Equal(2, CallTargetValue(assembly));
    }

    [Fact]
    public void A_mod_that_fails_after_patching_leaves_the_game_unpatched()
    {
        using var folder = new ModFolder();
        var assembly = PatchingMod(folder, "broken.mod", failAfterPatching: true);

        var loaded = LoadInDomain(folder);

        Assert.Empty(loaded);
        Assert.Contains("Removed broken.mod's Harmony patches", folder.Log.From("Core"));
        Assert.Equal(1, CallTargetValue(assembly));
    }

    [Fact]
    public void A_missing_patch_target_is_named()
    {
        using var folder = new ModFolder();
        folder.Compile("typo.mod", "TypoMod", """
            public class Target
            {
                private void Start() { }
            }

            [HarmonyLib.HarmonyPatch(typeof(Target), "Strat")]
            public static class StartPatch
            {
                public static void Postfix() { }
            }

            public sealed class EntryMod : Lustral.Mod
            {
                public override void OnLoad() => Harmony.PatchAll(typeof(EntryMod).Assembly);
            }
            """, typeof(HarmonyLib.Harmony).Assembly.Location);
        folder.Manifest("typo.mod", $"""
            id = "typo.mod"
            version = "1.0.0"
            assembly = "{folder.Name("TypoMod")}.dll"
            """);

        var loaded = LoadInDomain(folder);

        Assert.Empty(loaded);
        Assert.Contains("typo.mod failed to load: StartPatch patches Target.Strat, which does not exist in this version of the game",
            folder.Log.From("Core"));
    }

    [Fact]
    public void A_mod_whose_constructor_fails_after_patching_leaves_the_game_unpatched()
    {
        using var folder = new ModFolder();
        var assembly = PatchingMod(folder, "broken.ctor", failAfterPatching: true, inConstructor: true);

        var loaded = LoadInDomain(folder);

        Assert.Empty(loaded);
        Assert.Contains("Removed broken.ctor's Harmony patches", folder.Log.From("Core"));
        Assert.Equal(1, CallTargetValue(assembly));
    }

    [Fact]
    public void A_mod_has_what_Lustral_gives_it_from_its_constructor_on()
    {
        using var folder = new ModFolder();
        folder.Compile("early.mod", "Early", """
            public sealed class EntryMod : Lustral.Mod
            {
                public EntryMod() => Log.Info($"constructed as {Info.Id} {Info.Version} in {System.IO.Path.GetFileName(Info.Directory)}");

                public override void OnLoad() => Log.Info($"loaded as {Info}");
            }
            """);
        folder.Manifest("early.mod", $"""
            id = "early.mod"
            name = "Early Mod"
            version = "1.4.2"
            assembly = "{folder.Name("Early")}.dll"
            """);

        var loaded = LoadInDomain(folder);

        Assert.Single(loaded);
        Assert.Equal(["constructed as early.mod 1.4.2 in early.mod", "loaded as Early Mod 1.4.2 (early.mod)"], folder.Log.From("early.mod"));
    }

    [Fact]
    public void A_mod_sees_the_game_and_the_mods_loaded_before_it()
    {
        using var folder = new ModFolder();
        Library(folder, "extra.lib", "Extra", "public static class Extra { }");
        folder.Compile("curious.mod", "Curious", """
            public sealed class EntryMod : Lustral.Mod
            {
                public EntryMod() => Log.Info($"{Game.Name} {Game.Version} by {Game.Developer} on {Game.Backend}");

                public override void OnLoad() => Log.Info($"extra: {Mods.IsLoaded("extra.lib")}, missing: {Mods.IsLoaded("missing.mod")}, self: {Mods.IsLoaded(Info.Id)}, by {string.Join("+", Info.Authors)}");
            }
            """);
        folder.Manifest("curious.mod", $"""
            id = "curious.mod"
            version = "1.0.0"
            assembly = "{folder.Name("Curious")}.dll"
            authors = ["sxxr0", "someone"]

            [optional-dependencies]
            "extra.lib" = "*"
            "missing.mod" = "*"
            """);
        var logs = folder.CreateLogHub();
        var game = new GameInfo("Lethal Company", "Zeekerss", "0.1", "2022.3.62f2", ScriptingBackend.Mono, folder.Root);
        var loader = new ModLoader(logs, new DomainModAssemblyLoader(logs.For("Core")), Path.Combine(folder.Root, "config"), game);

        var loaded = loader.Load(folder.Plan());

        Assert.Equal(["extra.lib", "curious.mod"], loaded.Select(m => m.Mod.Id));
        Assert.Equal(["extra.lib", "curious.mod"], loader.Loaded.Select(m => m.Id));
        Assert.Equal(["Lethal Company 0.1 by Zeekerss on Mono", "extra: True, missing: False, self: False, by sxxr0+someone"], folder.Log.From("curious.mod"));
    }

    [Fact]
    public void A_mod_Lustral_didnt_create_says_so()
    {
        var mod = new OrphanMod();

        var e = Assert.Throws<InvalidOperationException>(() => mod.Log);
        Assert.Contains("was not created by Lustral", e.Message);
    }

    private sealed class OrphanMod : Mod;
}
