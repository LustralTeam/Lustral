using Microsoft.CodeAnalysis;

namespace Lustral.Analyzers;

/// <summary>All diagnostics reported by Lustral's analyzers. The meaning of an ID never changes, and retired IDs are not reused.</summary>
internal static class Diagnostics
{
    private const string ModCategory = "Lustral.Mod";
    private const string HarmonyCategory = "Lustral.Harmony";
    private const string ConfigCategory = "Lustral.Config";
    private const string Il2CppCategory = "Lustral.Il2Cpp";

    public static readonly DiagnosticDescriptor NoModClass = new(
        "LUS0001",
        "A mod requires a class deriving from Lustral.Mod",
        "This mod has no class deriving from Lustral.Mod, so Lustral cannot start it. Add one, or set <ModKind>library</ModKind> for a library.",
        ModCategory, DiagnosticSeverity.Error, isEnabledByDefault: true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor SeveralModClasses = new(
        "LUS0002",
        "A mod must have exactly one Lustral.Mod class",
        "{0} is one of {1} classes deriving from Lustral.Mod ({2}); a mod must have exactly one",
        ModCategory, DiagnosticSeverity.Error, isEnabledByDefault: true, customTags: WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor ModWithoutConstructor = new(
        "LUS0003",
        "Lustral.Mod classes require a public parameterless constructor",
        "{0} has no public parameterless constructor, so Lustral cannot create it",
        ModCategory, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PatchTargetNotFound = new(
        "LUS0101",
        "Harmony patch target not found",
        "{0}",
        HarmonyCategory, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PatchTargetAmbiguous = new(
        "LUS0102",
        "Harmony patch target is ambiguous",
        "{0} has {1} overloads, so Harmony cannot determine which one to patch; specify the parameter types, for example [HarmonyPatch(typeof({2}), nameof({2}.{3}), typeof(int))]",
        HarmonyCategory, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor PatchParameterUnknown = new(
        "LUS0103",
        "Harmony patch parameter does not match the target",
        "{0}",
        HarmonyCategory, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedSetting = new(
        "LUS0201",
        "Setting type cannot be stored in a settings file",
        "{0} is {1}, which cannot be stored in a settings file; use bool, string, a numeric type, an enumeration, a list or array of these types, or a nested settings class",
        ConfigCategory, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnityMessageNotExposed = new(
        "LUS0301",
        "Unity does not call message methods without [Il2CppMethod]",
        "Unity will not call {0}.{1}: only methods marked [Il2CppMethod] are exposed to IL2CPP in an injected class",
        Il2CppCategory, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Il2CppFieldNotIl2CppType = new(
        "LUS0302",
        "An [Il2CppField] requires an IL2CPP type",
        "{0}.{1} is {2}, which an [Il2CppField] cannot store: {3}",
        Il2CppCategory, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericIl2CppMethod = new(
        "LUS0303",
        "Generic methods cannot be exposed to IL2CPP",
        "{0}.{1} is generic, so [Il2CppMethod] has no effect: Il2CppInterop does not expose generic methods to IL2CPP; call it from a non-generic [Il2CppMethod] method instead",
        Il2CppCategory, DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedInjectedType = new(
        "LUS0304",
        "Il2CppInterop cannot inject this type",
        "{0} cannot be injected into IL2CPP: {1}",
        Il2CppCategory, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor Il2CppInterfaceMethodNotExposed = new(
        "LUS0305",
        "IL2CPP interface implementations require [Il2CppMethod]",
        "{0}.{1} implements {2} for IL2CPP, so it requires [Il2CppMethod]: without it, Il2CppInterop cannot register {0}, and the mod fails to load",
        Il2CppCategory, DiagnosticSeverity.Error, isEnabledByDefault: true);
}
