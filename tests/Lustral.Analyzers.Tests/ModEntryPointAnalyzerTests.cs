namespace Lustral.Analyzers.Tests;

public class ModEntryPointAnalyzerTests
{
    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> Run(string source, string? modKind = "mod") =>
        AnalyzerRunner.Run(new ModEntryPointAnalyzer(), source, modKind);

    [Fact]
    public async Task One_mod_class_is_fine()
    {
        var diagnostics = await Run("""
            public abstract class ModBase : Lustral.Mod { }
            public sealed class MyMod : ModBase { }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task A_mod_without_a_mod_class_is_an_error()
    {
        Assert.Equal(["LUS0001"], (await Run("public class Helper { }")).Ids());
    }

    [Theory]
    [InlineData("library")]
    [InlineData(null)]
    public async Task Libraries_and_projects_without_the_SDK_dont_need_one(string? modKind)
    {
        Assert.Empty(await Run("public class Helper { }", modKind));
    }

    [Fact]
    public async Task Two_mod_classes_are_both_reported()
    {
        var diagnostics = await Run("""
            public sealed class First : Lustral.Mod { }
            public sealed class Second : Lustral.Mod { }
            """);

        Assert.Equal(["LUS0002", "LUS0002"], diagnostics.Ids());
        Assert.Contains("First is one of 2 classes deriving from Lustral.Mod (First, Second)", diagnostics[0].GetMessage());
    }

    [Fact]
    public async Task A_mod_class_needs_a_public_parameterless_constructor()
    {
        var diagnostics = await Run("""
            public sealed class MyMod : Lustral.Mod
            {
                public MyMod(int seed) { }
            }
            """);

        Assert.Equal(["LUS0003"], diagnostics.Ids());
    }
}
