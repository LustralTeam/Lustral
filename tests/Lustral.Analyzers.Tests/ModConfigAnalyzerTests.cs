namespace Lustral.Analyzers.Tests;

public class ModConfigAnalyzerTests
{
    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> Run(string source) =>
        AnalyzerRunner.Run(new ModConfigAnalyzer(), source, modKind: null);

    [Fact]
    public async Task Supported_settings_are_fine()
    {
        Assert.Empty(await Run("""
            using System.Collections.Generic;

            public enum Quality { Low, High }

            public sealed class Hud
            {
                public double Scale { get; set; } = 1;
            }

            public sealed class Settings
            {
                public bool ShowFps { get; set; }
                public int? MaxBananas { get; set; }
                public decimal Price { get; set; }
                public Quality Quality { get; set; }
                public List<string> Players { get; set; } = new();
                public IReadOnlyList<int> Levels { get; set; } = new int[0];
                public Hud Hud { get; set; } = new();
                public System.DateTime NotASetting => default;
            }

            public sealed class MyMod : Lustral.Mod
            {
                public override void OnLoad()
                {
                    var settings = Config.Bind<Settings>();
                    Config.Save(settings);
                }
            }
            """));
    }

    [Fact]
    public async Task Unsupported_settings_are_reported()
    {
        var diagnostics = await Run("""
            using System.Collections.Generic;

            public sealed class Point
            {
                public Point(int x) { }
            }

            public sealed class Hud
            {
                public Dictionary<string, int> Colors { get; set; } = new();
            }

            public sealed class Settings
            {
                public System.DateTime LastPlayed { get; set; }
                public List<Hud> Huds { get; set; } = new();
                public Point Origin { get; set; } = new(0);
                public Hud Hud { get; set; } = new();
            }

            public sealed class MyMod : Lustral.Mod
            {
                public override void OnLoad() => Config.Bind<Settings>();
            }
            """);

        Assert.Equal(["LUS0201", "LUS0201", "LUS0201", "LUS0201"], diagnostics.Ids());
        Assert.Equal(
            ["Settings.LastPlayed is DateTime", "Settings.Huds is List<Hud>", "Settings.Origin is Point", "Settings.Hud.Colors is Dictionary<string, int>"],
            diagnostics.Select(d => d.GetMessage()[..d.GetMessage().IndexOf(", which", StringComparison.Ordinal)]).ToList());
    }
}
