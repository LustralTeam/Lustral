using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Lustral.Analyzers;

/// <summary>
/// Checks that settings classes passed to <c>ModConfig.Bind&lt;T&gt;()</c> and <c>Save&lt;T&gt;()</c> use only types that
/// can be stored in a settings file, following the same rules as Lustral.Core's TomlModConfig; other types would never
/// be loaded.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ModConfigAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Diagnostics.UnsupportedSetting];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var modConfig = start.Compilation.GetTypeByMetadataName("Lustral.ModConfig");
            if (modConfig is null)
                return;
            start.RegisterOperationAction(operation =>
            {
                var invocation = (IInvocationOperation)operation.Operation;
                var method = invocation.TargetMethod;
                if (method.Name is not ("Bind" or "Save") || method.TypeArguments.Length != 1 ||
                    !SymbolEqualityComparer.Default.Equals(method.ContainingType, modConfig))
                    return;
                if (method.TypeArguments[0] is INamedTypeSymbol settings)
                    Check(operation, invocation.Syntax.GetLocation(), settings, settings.Name, []);
            }, OperationKind.Invocation);
        });
    }

    private static void Check(OperationAnalysisContext context, Location location, INamedTypeSymbol type, string path, ImmutableHashSet<INamedTypeSymbol> visiting)
    {
        if (visiting.Contains(type))
            return;
        visiting = visiting.Add(type);
        foreach (var property in Settings(type))
        {
            var name = $"{path}.{property.Name}";
            var propertyType = property.Type;
            if (IsValue(propertyType) || IsList(propertyType, out _))
                continue;
            if (IsSection(propertyType, out var section))
            {
                Check(context, location, section!, name, visiting);
                continue;
            }
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnsupportedSetting, location, name,
                propertyType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    // Public instance properties with a public getter and setter, as bound by TomlModConfig.
    private static IEnumerable<IPropertySymbol> Settings(INamedTypeSymbol type) =>
        type.GetMembers().OfType<IPropertySymbol>().Where(p =>
            !p.IsStatic && !p.IsIndexer && p.DeclaredAccessibility == Accessibility.Public &&
            p.GetMethod?.DeclaredAccessibility == Accessibility.Public && p.SetMethod?.DeclaredAccessibility == Accessibility.Public);

    private static bool IsValue(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            type = nullable.TypeArguments[0];
        return type.TypeKind == TypeKind.Enum || type.SpecialType is
            SpecialType.System_Boolean or SpecialType.System_String or
            SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16 or
            SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 or
            SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal;
    }

    // T[], or a List<T> interface or class, whose elements are values.
    private static bool IsList(ITypeSymbol type, out ITypeSymbol? element)
    {
        element = type switch
        {
            IArrayTypeSymbol { Rank: 1 } array => array.ElementType,
            INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } generic when generic.OriginalDefinition.ToDisplayString() is
                "System.Collections.Generic.List<T>" or "System.Collections.Generic.IList<T>" or "System.Collections.Generic.ICollection<T>" or
                "System.Collections.Generic.IEnumerable<T>" or "System.Collections.Generic.IReadOnlyList<T>" or
                "System.Collections.Generic.IReadOnlyCollection<T>" => generic.TypeArguments[0],
            _ => null,
        };
        return element is not null && IsValue(element);
    }

    // A nested settings class, created with its public parameterless constructor.
    private static bool IsSection(ITypeSymbol type, out INamedTypeSymbol? section)
    {
        section = type as INamedTypeSymbol;
        return section is { TypeKind: TypeKind.Class, IsAbstract: false, SpecialType: SpecialType.None } &&
               !section.AllInterfaces.Any(i => i.ToDisplayString() == "System.Collections.IEnumerable") &&
               section.InstanceConstructors.Any(c => c.Parameters.Length == 0 && c.DeclaredAccessibility == Accessibility.Public);
    }
}
