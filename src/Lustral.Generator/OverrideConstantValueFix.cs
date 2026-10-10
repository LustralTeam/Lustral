using Cpp2IL.Core.Model.Contexts;
using HarmonyLib;

namespace Lustral.Generator;

/// <summary>
/// Restores Cpp2IL 1.0.8's meaning of <c>FieldAnalysisContext.OverrideConstantValue</c>, which Il2CppInterop V2 beta.8
/// relies on. Lustral uses Cpp2IL 1.0.9 with a generator adapted to it (see Lustral.Generator.csproj).
/// </summary>
/// <remarks>
/// In 1.0.8, a field's constant value is its override if one is set, and its default otherwise. 1.0.9 uses the override
/// only if <c>UseOverrideConstantValue</c> is set, which Il2CppInterop does not set, so unstripped constants lost their
/// values and became ordinary fields, for which unstripping fails. Setting a non-null override now also sets the flag.
/// Remove with the adapted generator.
/// </remarks>
internal static class OverrideConstantValueFix
{
    /// <summary>Patches Cpp2IL. Call once, before generation starts.</summary>
    public static void Install()
    {
        var harmony = new Harmony("lustral.generator.override-constant-value-fix");
        var postfix = new HarmonyMethod(typeof(OverrideConstantValueFix), nameof(SetOverridePostfix));
        foreach (var type in new[] { typeof(FieldAnalysisContext), typeof(ConcreteGenericFieldAnalysisContext) })
            harmony.Patch(AccessTools.PropertySetter(type, nameof(FieldAnalysisContext.OverrideConstantValue)), postfix: postfix);
    }

    private static void SetOverridePostfix(FieldAnalysisContext __instance, object? value) =>
        __instance.UseOverrideConstantValue = value is not null;
}
