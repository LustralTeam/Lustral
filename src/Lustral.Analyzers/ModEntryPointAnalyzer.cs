using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Lustral.Analyzers;

/// <summary>
/// Checks that a mod's assembly has exactly one non-abstract class deriving from <c>Lustral.Mod</c>, with a public
/// parameterless constructor; otherwise, Lustral does not load it. A missing class is an error only for mods
/// (ModKind=mod, passed by Lustral.Sdk), because libraries do not require one.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModEntryPointAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [Diagnostics.NoModClass, Diagnostics.SeveralModClasses, Diagnostics.ModWithoutConstructor];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var modType = start.Compilation.GetTypeByMetadataName("Lustral.Mod");
            if (modType is null)
                return;

            var entryPoints = new ConcurrentBag<INamedTypeSymbol>();
            start.RegisterSymbolAction(symbol =>
            {
                var type = (INamedTypeSymbol)symbol.Symbol;
                if (type is not { TypeKind: TypeKind.Class, IsAbstract: false } || !DerivesFrom(type, modType))
                    return;
                entryPoints.Add(type);
                if (!type.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public))
                    symbol.ReportDiagnostic(Diagnostic.Create(Diagnostics.ModWithoutConstructor, type.Locations.FirstOrDefault(), type.Name));
            }, SymbolKind.NamedType);

            start.RegisterCompilationEndAction(end =>
            {
                var found = entryPoints.OrderBy(t => t.ToDisplayString()).ToList();
                if (found.Count > 1)
                {
                    var names = string.Join(", ", found.Select(t => t.Name));
                    foreach (var type in found)
                        end.ReportDiagnostic(Diagnostic.Create(Diagnostics.SeveralModClasses, type.Locations.FirstOrDefault(), type.Name, found.Count, names));
                }
                else if (found.Count == 0 &&
                         end.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.ModKind", out var kind) &&
                         kind == "mod")
                {
                    end.ReportDiagnostic(Diagnostic.Create(Diagnostics.NoModClass, Location.None));
                }
            });
        });
    }

    private static bool DerivesFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
                return true;
        }
        return false;
    }
}
