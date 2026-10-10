using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Lustral.Analyzers;

/// <summary>
/// Checks a mod's injected IL2CPP classes (Il2CppInterop's <c>[InjectedType]</c>). On the IL2CPP side, a MonoBehaviour
/// has only the methods marked <c>[Il2CppMethod]</c>, and Unity looks up its messages there by name, so an
/// <c>Update</c> method without the attribute compiles but is never called (LUS0301). An <c>[Il2CppField]</c> is stored as
/// an IL2CPP value, so its type must be an interop type (<c>Il2CppSystem.Int32</c>, not <c>int</c>); otherwise, the
/// source generator's code fails with an error that refers to generated code (LUS0302). Il2CppInterop skips generic
/// methods when it injects a class, so <c>[Il2CppMethod]</c> on one has no effect (LUS0303), and it cannot inject a
/// generic or nested type, one whose base type is generic (it throws when the mod loads), or one that implements a
/// generic IL2CPP interface (LUS0304). IL2CPP calls an interface method through the class's virtual method table, which
/// Il2CppInterop fills with the [Il2CppMethod] methods that implement it; it throws for an implementation without the
/// attribute (LUS0305).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InjectedComponentAnalyzer : DiagnosticAnalyzer
{
    private const string InjectedType = "Il2CppInterop.Common.Attributes.InjectedTypeAttribute";
    private const string Il2CppMethod = "Il2CppInterop.Common.Attributes.Il2CppMethodAttribute";
    private const string Il2CppField = "Il2CppInterop.Common.Attributes.Il2CppFieldAttribute";
    private const string Il2CppType = "Il2CppInterop.Common.IIl2CppType`1";
    private const string Il2CppObject = "Il2CppSystem.IObject";

    // Interfaces that every interop object, value type or enumeration implements, which Il2CppInterop does not treat as
    // IL2CPP interfaces of an injected type.
    private static readonly ImmutableHashSet<string> InteropMarkerInterfaces = ImmutableHashSet.Create(
        "Il2CppSystem.IValueType", "Il2CppSystem.IEnum");

    // The interop types corresponding to .NET types, as named by Il2CppInterop V2.
    private static readonly ImmutableDictionary<SpecialType, string> Il2CppSystemTypes = new Dictionary<SpecialType, string>
    {
        [SpecialType.System_Boolean] = "Il2CppSystem.Boolean",
        [SpecialType.System_Char] = "Il2CppSystem.Char",
        [SpecialType.System_SByte] = "Il2CppSystem.SByte",
        [SpecialType.System_Byte] = "Il2CppSystem.Byte",
        [SpecialType.System_Int16] = "Il2CppSystem.Int16",
        [SpecialType.System_UInt16] = "Il2CppSystem.UInt16",
        [SpecialType.System_Int32] = "Il2CppSystem.Int32",
        [SpecialType.System_UInt32] = "Il2CppSystem.UInt32",
        [SpecialType.System_Int64] = "Il2CppSystem.Int64",
        [SpecialType.System_UInt64] = "Il2CppSystem.UInt64",
        [SpecialType.System_Single] = "Il2CppSystem.Single",
        [SpecialType.System_Double] = "Il2CppSystem.Double",
        [SpecialType.System_String] = "Il2CppSystem.String",
    }.ToImmutableDictionary();

    // MonoBehaviour messages that Unity sends in a player, excluding editor-only messages such as OnValidate or Reset.
    private static readonly ImmutableHashSet<string> Messages = ImmutableHashSet.Create(
        "Awake", "Start", "Update", "FixedUpdate", "LateUpdate", "OnEnable", "OnDisable", "OnDestroy", "OnGUI",
        "OnApplicationFocus", "OnApplicationPause", "OnApplicationQuit", "OnBecameVisible", "OnBecameInvisible",
        "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit", "OnCollisionEnter2D", "OnCollisionStay2D", "OnCollisionExit2D",
        "OnTriggerEnter", "OnTriggerStay", "OnTriggerExit", "OnTriggerEnter2D", "OnTriggerStay2D", "OnTriggerExit2D",
        "OnControllerColliderHit", "OnMouseDown", "OnMouseUp", "OnMouseUpAsButton", "OnMouseEnter", "OnMouseExit", "OnMouseOver",
        "OnMouseDrag", "OnPreCull", "OnPreRender", "OnPostRender", "OnRenderObject", "OnRenderImage", "OnWillRenderObject",
        "OnAnimatorMove", "OnAnimatorIK", "OnParticleCollision", "OnParticleTrigger", "OnJointBreak", "OnJointBreak2D",
        "OnTransformChildrenChanged", "OnTransformParentChanged", "OnRectTransformDimensionsChange", "OnCanvasGroupChanged",
        "OnAudioFilterRead");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [Diagnostics.UnityMessageNotExposed, Diagnostics.Il2CppFieldNotIl2CppType, Diagnostics.GenericIl2CppMethod, Diagnostics.UnsupportedInjectedType,
         Diagnostics.Il2CppInterfaceMethodNotExposed];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var injectedType = start.Compilation.GetTypeByMetadataName(InjectedType);
            var il2CppMethod = start.Compilation.GetTypeByMetadataName(Il2CppMethod);
            if (injectedType is null || il2CppMethod is null)
                return; // Not an IL2CPP mod.
            var il2CppField = start.Compilation.GetTypeByMetadataName(Il2CppField);
            var il2CppType = start.Compilation.GetTypeByMetadataName(Il2CppType);
            var il2CppObject = start.Compilation.GetTypeByMetadataName(Il2CppObject);
            start.RegisterSymbolAction(symbol =>
            {
                var type = (INamedTypeSymbol)symbol.Symbol;
                if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct) || !HasAttribute(type, injectedType))
                    return;
                foreach (var reason in UnsupportedShape(type, il2CppObject))
                    symbol.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnsupportedInjectedType, type.Locations.FirstOrDefault(), type.Name, reason));
                if (type.TypeKind != TypeKind.Class)
                    return;
                foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                {
                    if (method.IsGenericMethod && HasAttribute(method, il2CppMethod))
                        symbol.ReportDiagnostic(Diagnostic.Create(Diagnostics.GenericIl2CppMethod, method.Locations.FirstOrDefault(), type.Name, method.Name));
                }
                if (il2CppObject is not null)
                {
                    foreach (var (implementation, member) in UnexposedInterfaceImplementations(type, il2CppObject, il2CppMethod))
                    {
                        symbol.ReportDiagnostic(Diagnostic.Create(Diagnostics.Il2CppInterfaceMethodNotExposed, implementation.Locations.FirstOrDefault(),
                            type.Name, implementation.Name, $"{member.ContainingType.Name}.{member.Name}"));
                    }
                }
                if (IsMonoBehaviour(type))
                {
                    foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
                    {
                        if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || !Messages.Contains(method.Name) || HasAttribute(method, il2CppMethod))
                            continue;
                        symbol.ReportDiagnostic(Diagnostic.Create(Diagnostics.UnityMessageNotExposed, method.Locations.FirstOrDefault(), type.Name, method.Name));
                    }
                }
                if (il2CppField is null || il2CppType is null)
                    return;
                foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
                {
                    if (!HasAttribute(property, il2CppField) || IsIl2CppType(property.Type, il2CppType))
                        continue;
                    var fix = Il2CppSystemTypes.TryGetValue(property.Type.SpecialType, out var replacement)
                        ? $"use {replacement}, which converts to and from {property.Type.ToDisplayString()}"
                        : "use a type from the game's interop assemblies, or make it a [ManagedField] to keep a .NET object";
                    symbol.ReportDiagnostic(Diagnostic.Create(Diagnostics.Il2CppFieldNotIl2CppType, SourceLocation(property),
                        type.Name, property.Name, property.Type.ToDisplayString(), fix));
                }
            }, SymbolKind.NamedType);
        });
    }

    // What Il2CppInterop's TypeInjector rejects when it registers the type, and the generic interfaces it does not support.
    private static IEnumerable<string> UnsupportedShape(INamedTypeSymbol type, INamedTypeSymbol? il2CppObject)
    {
        if (type.TypeParameters.Length > 0)
            yield return "generic types cannot be injected";
        if (type.ContainingType is { } outer)
            yield return $"nested types cannot be injected; move it out of {outer.Name}";
        if (type.BaseType is { IsGenericType: true } baseType)
            yield return $"its base type {baseType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} is generic";
        if (il2CppObject is null)
            yield break;
        foreach (var implemented in type.Interfaces.Where(i => i.IsGenericType && i.AllInterfaces.Contains(il2CppObject, SymbolEqualityComparer.Default)))
            yield return $"it implements the generic IL2CPP interface {implemented.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}";
    }

    // The type's own methods that implement a method of an IL2CPP interface without [Il2CppMethod]. Implementations
    // inherited from interop classes are IL2CPP methods already.
    private static IEnumerable<(IMethodSymbol Implementation, IMethodSymbol Member)> UnexposedInterfaceImplementations(
        INamedTypeSymbol type, INamedTypeSymbol il2CppObject, INamedTypeSymbol il2CppMethod)
    {
        foreach (var implemented in type.AllInterfaces)
        {
            if (!implemented.AllInterfaces.Contains(il2CppObject, SymbolEqualityComparer.Default) ||
                InteropMarkerInterfaces.Contains(implemented.OriginalDefinition.ToDisplayString()))
                continue;
            foreach (var member in implemented.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.IsStatic || !(member.IsAbstract || member.IsVirtual))
                    continue;
                if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol implementation &&
                    SymbolEqualityComparer.Default.Equals(implementation.ContainingType, type) &&
                    !HasAttribute(implementation, il2CppMethod))
                    yield return (implementation, member);
            }
        }
    }

    // Interop types implement IIl2CppType<TSelf>.
    private static bool IsIl2CppType(ITypeSymbol type, INamedTypeSymbol il2CppType) =>
        type.TypeKind == TypeKind.TypeParameter ||
        type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, il2CppType));

    // The mod's declaration, not the source generator's part of the partial property.
    private static Location? SourceLocation(IPropertySymbol property) =>
        property.Locations.FirstOrDefault(l => l.SourceTree?.FilePath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) == false)
        ?? property.Locations.FirstOrDefault();

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attribute) =>
        symbol.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));

    private static bool IsMonoBehaviour(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.Name == "MonoBehaviour" && current.ContainingNamespace?.ToDisplayString() == "UnityEngine")
                return true;
        }
        return false;
    }
}
