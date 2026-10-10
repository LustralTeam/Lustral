using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Lustral.Analyzers.Tests;

/// <summary>Compiles a mod's source against the framework, HarmonyX and Lustral.Api, then runs one analyzer on it.</summary>
internal static class AnalyzerRunner
{
    private static readonly MetadataReference[] References = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Where(path => !Path.GetFileName(path).StartsWith("Lustral.", StringComparison.Ordinal))
        .Append(typeof(HarmonyLib.Harmony).Assembly.Location)
        .Append(typeof(Mod).Assembly.Location)
        .Distinct()
        .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
        .ToArray();

    /// <param name="modKind">The value Lustral.Sdk passes as build_property.ModKind; null for a project without the SDK.</param>
    /// <param name="references">Additional assemblies to compile against, such as a game's (see <see cref="Library"/>).</param>
    public static async Task<IReadOnlyList<Diagnostic>> Run(DiagnosticAnalyzer analyzer, string source, string? modKind = "mod", params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(
            "TestMod",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            References.Concat(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "The test source doesn't compile:\n" + string.Join("\n", errors));

        var options = new AnalyzerOptions([], new GlobalOptionsProvider(modKind is null ? [] : new() { ["build_property.ModKind"] = modKind }));
        var diagnostics = await compilation.WithAnalyzers([analyzer], options).GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);
        return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ThenBy(d => d.Id).ToList();
    }

    /// <summary>Compiles <paramref name="source"/> into an assembly to reference, as a game's assemblies are referenced.</summary>
    public static MetadataReference Library(string name, string source, params MetadataReference[] references)
    {
        var compilation = CSharpCompilation.Create(
            name,
            [CSharpSyntaxTree.ParseText(source)],
            References.Concat(references),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var result = compilation.Emit(image);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    public static IReadOnlyList<string> Ids(this IEnumerable<Diagnostic> diagnostics) => diagnostics.Select(d => d.Id).ToList();

    private sealed class GlobalOptionsProvider(Dictionary<string, string> values) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(values);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;
    }

    private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions
    {
        public static readonly Options Empty = new([]);

        public override bool TryGetValue(string key, out string value) => values.TryGetValue(key, out value!);
    }
}
