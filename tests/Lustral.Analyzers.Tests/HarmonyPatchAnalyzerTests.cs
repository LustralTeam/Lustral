using Microsoft.CodeAnalysis;

namespace Lustral.Analyzers.Tests;

public class HarmonyPatchAnalyzerTests
{
    // Substitutes for a game's classes.
    private const string Game = """
        using HarmonyLib;

        public class Game
        {
            private int lives;
            public Game() { }
            public Game(int seed) { }
            public void Jump(int height) { }
            public void Fire() { }
            public void Fire(int count) { }
            public static void Save(string slot) { }
            public int Score { get; set; }
            public virtual void Tick(float delta) { }
        }

        public class Boss : Game
        {
            public override void Tick(float delta) { }
        }

        """;

    private static Task<IReadOnlyList<Diagnostic>> Run(string patches) =>
        AnalyzerRunner.Run(new HarmonyPatchAnalyzer(), Game + patches, modKind: null);

    [Fact]
    public async Task A_correct_patch_has_no_diagnostics()
    {
        Assert.Empty(await Run("""
            [HarmonyPatch(typeof(Game), nameof(Game.Jump))]
            static class JumpPatch
            {
                static bool Prefix(Game __instance, int height, int __0, ref int ___lives, object[] __args, ref bool __runOriginal) => true;
                static void Postfix(int height) { }
            }
            """));
    }

    [Fact]
    public async Task A_misspelled_target_is_reported()
    {
        var diagnostics = await Run("""
            [HarmonyPatch(typeof(Game), "Jmp")]
            static class JumpPatch
            {
                static void Prefix() { }
            }
            """);

        Assert.Equal(["LUS0101"], diagnostics.Ids());
        Assert.Equal("Game.Jmp does not exist", diagnostics[0].GetMessage());
    }

    [Fact]
    public async Task Overloads_need_parameter_types()
    {
        var ambiguous = await Run("""
            [HarmonyPatch(typeof(Game), nameof(Game.Fire))]
            static class FirePatch { static void Prefix() { } }
            """);
        var picked = await Run("""
            [HarmonyPatch(typeof(Game), nameof(Game.Fire), typeof(int))]
            static class FirePatch { static void Prefix(int count) { } }
            """);
        var wrong = await Run("""
            [HarmonyPatch(typeof(Game), nameof(Game.Fire), typeof(string))]
            static class FirePatch { static void Prefix() { } }
            """);

        Assert.Equal(["LUS0102"], ambiguous.Ids());
        Assert.StartsWith("Game.Fire has 2 overloads", ambiguous[0].GetMessage());
        Assert.Empty(picked);
        Assert.Equal(["LUS0101"], wrong.Ids());
        Assert.Equal("Game.Fire has no overload taking (string)", wrong[0].GetMessage());
    }

    [Fact]
    public async Task Overrides_arent_overloads_and_base_methods_are_found()
    {
        Assert.Empty(await Run("""
            [HarmonyPatch(typeof(Boss), nameof(Boss.Tick))]
            static class TickPatch { static void Postfix(float delta) { } }

            [HarmonyPatch(typeof(Boss), nameof(Game.Jump))]
            static class BossJumpPatch { static void Postfix(int height) { } }
            """));
    }

    [Fact]
    public async Task Patch_parameters_must_exist_on_the_target()
    {
        var diagnostics = await Run("""
            [HarmonyPatch(typeof(Game), nameof(Game.Jump))]
            static class JumpPatch
            {
                static void Prefix(int hight, int __1, int ___health, int __result) { }
            }

            [HarmonyPatch(typeof(Game), nameof(Game.Save))]
            static class SavePatch
            {
                static void Prefix(Game __instance, string slot) { }
            }

            [HarmonyPatch(typeof(Game), nameof(Game.Fire), new System.Type[0])]
            static class FirePatch
            {
                static void Prefix(int count) { }
            }
            """);

        Assert.Equal(["LUS0103", "LUS0103", "LUS0103", "LUS0103", "LUS0103", "LUS0103"], diagnostics.Ids());
        Assert.Equal("hight: Game.Jump has parameters height; use one of these names, __0 for the first parameter, or an injection such as __instance or __result", diagnostics[0].GetMessage());
        Assert.Equal("__1: Game.Jump has 1 parameter(s), counted from __0", diagnostics[1].GetMessage());
        Assert.Equal("___health: Game has no field health", diagnostics[2].GetMessage());
        Assert.Equal("__result: Game.Jump returns nothing", diagnostics[3].GetMessage());
        Assert.Equal("__instance: Game.Save is static and has no instance", diagnostics[4].GetMessage());
        Assert.Equal("count: Game.Fire takes no parameters, so a patch can only use injections such as __instance or __result", diagnostics[5].GetMessage());
    }

    [Fact]
    public async Task Getters_setters_and_constructors_resolve()
    {
        var diagnostics = await Run("""
            [HarmonyPatch(typeof(Game), nameof(Game.Score), MethodType.Getter)]
            static class ScorePatch { static void Postfix(ref int __result) { } }

            [HarmonyPatch(typeof(Game), "Lives", MethodType.Getter)]
            static class LivesPatch { static void Postfix() { } }

            [HarmonyPatch(typeof(Game), MethodType.Constructor, typeof(int))]
            static class SeedPatch { static void Postfix(int seed) { } }

            [HarmonyPatch(typeof(Game), MethodType.Constructor)]
            static class AnyConstructorPatch { static void Postfix() { } }
            """);

        Assert.Equal(["LUS0101", "LUS0102"], diagnostics.Ids());
        Assert.Equal("Game has no property Lives", diagnostics[0].GetMessage());
    }

    [Fact]
    public async Task Class_and_method_attributes_combine()
    {
        Assert.Empty(await Run("""
            [HarmonyPatch(typeof(Game))]
            static class GamePatches
            {
                [HarmonyPatch(nameof(Game.Jump))]
                [HarmonyPrefix]
                static void BeforeJump(int height) { }

                [HarmonyPatch(nameof(Game.Save))]
                [HarmonyPostfix]
                static void AfterSave(string slot) { }
            }
            """));
    }

    [Fact]
    public async Task Targets_picked_in_code_or_renamed_parameters_are_left_to_Harmony()
    {
        Assert.Empty(await Run("""
            [HarmonyPatch]
            static class CustomTarget
            {
                static System.Reflection.MethodBase TargetMethod() => AccessTools.Method(typeof(Game), "Anything");
                static void Prefix(int whatever) { }
            }

            [HarmonyPatch(typeof(Game), nameof(Game.Jump))]
            static class Renamed
            {
                static void Prefix([HarmonyArgument("height")] int howHigh) { }
            }
            """));
    }

    // A game's assembly: the compiler does not import its private members, which mods commonly patch.
    private const string GameLibrary = """
        namespace Game
        {
            public class Player
            {
                private int health;
                private void Die() { }
                public void Jump(int height) { }
            }
        }
        """;

    private const string GameLibraryPatches = """
        using HarmonyLib;

        [HarmonyPatch(typeof(Game.Player), "Die")]
        static class DiePatch { static void Prefix(int ___health) { } }

        [HarmonyPatch(typeof(Game.Player), "Typo")]
        static class TypoPatch { static void Prefix() { } }

        [HarmonyPatch(typeof(Game.Player), nameof(Game.Player.Jump))]
        static class JumpPatch { static void Prefix(int hight) { } }
        """;

    [Fact]
    public async Task In_a_games_own_assembly_private_members_are_found_and_everything_is_checked()
    {
        var game = AnalyzerRunner.Library("Assembly-CSharp", GameLibrary);

        var diagnostics = await AnalyzerRunner.Run(new HarmonyPatchAnalyzer(), GameLibraryPatches, modKind: null, game);

        // The private Die and health are found; Typo and hight are not.
        Assert.Equal(["LUS0101", "LUS0103"], diagnostics.Ids());
        Assert.Equal("Player.Typo does not exist", diagnostics[0].GetMessage());
        Assert.StartsWith("hight: Player.Jump has parameters height", diagnostics[1].GetMessage());
    }

    [Fact]
    public async Task Private_overloads_and_fields_in_a_games_assembly_are_checked()
    {
        var game = AnalyzerRunner.Library("Assembly-CSharp", """
            namespace Game
            {
                public class Enemy
                {
                    private int armor;
                    private void Hit() { }
                    private void Hit(float damage) { }
                    public class Spawner { private void Spawn(Enemy enemy) { } }
                }
            }
            """);

        var diagnostics = await AnalyzerRunner.Run(new HarmonyPatchAnalyzer(), """
            using HarmonyLib;

            [HarmonyPatch(typeof(Game.Enemy), "Hit")]
            static class AmbiguousPatch { static void Prefix() { } }

            [HarmonyPatch(typeof(Game.Enemy), "Hit", new[] { typeof(float) })]
            static class HitPatch { static void Prefix(float damage, int ___armour) { } }

            [HarmonyPatch(typeof(Game.Enemy.Spawner), "Spawn")]
            static class SpawnPatch { static void Postfix(Game.Enemy enemy) { } }
            """, modKind: null, game);

        Assert.Equal(["LUS0102", "LUS0103"], diagnostics.Ids());
        Assert.Equal("___armour: Enemy has no field armour", diagnostics[1].GetMessage());
    }

    [Fact]
    public async Task Il2CppInterop_assemblies_show_every_member_so_everything_is_checked()
    {
        // Il2CppInterop makes all members of the assemblies it generates public.
        var interop = AnalyzerRunner.Library("Il2CppInterop.Common", "namespace Il2CppInterop.Common { public class Il2CppObjectBase { } }");
        var game = AnalyzerRunner.Library("Assembly-CSharp", GameLibrary
            .Replace("public class Player", "public class Player : Il2CppInterop.Common.Il2CppObjectBase")
            .Replace("private", "public"), interop);

        var diagnostics = await AnalyzerRunner.Run(new HarmonyPatchAnalyzer(), GameLibraryPatches, modKind: null, game, interop);

        Assert.Equal(["LUS0101", "LUS0103"], diagnostics.Ids());
        Assert.Equal("Player.Typo does not exist", diagnostics[0].GetMessage());
    }
}
