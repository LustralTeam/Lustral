using System.Reflection.Emit;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;
using HarmonyLib;

namespace Lustral.Generator;

/// <summary>
/// Works around Cpp2IL on IL2CPP metadata v35 and newer (Unity 6000.x; BOMBANANA! is v39).
/// </summary>
/// <remarks>
/// From v35, IL2CPP stores an enum's underlying type in place of its parent type. LibCpp2IL accounts for this
/// (<c>Il2CppTypeDefinition.EnumUnderlyingType</c>), as does Cpp2IL's DLL writer, but
/// <see cref="TypeAnalysisContext.DefaultBaseType"/> still returns the parent index, so every enum reports <c>Int32</c>
/// (or a similar type) as its base type. Il2CppInterop V2's layers then never see <c>Il2CppSystem.Enum</c> and skip the
/// enum-to-struct conversion, and the writer emits actual enums containing interop methods, which CoreCLR refuses to
/// load (<c>TypeLoadException: Method in an Enum</c>).
/// <para>
/// The fix has two parts: it reports the game's <c>System.Enum</c> as each enum's default base type, so that V2
/// converts them, and prevents the writer from restoring <c>System.Enum</c> as their base type afterwards. Remove both
/// once Cpp2IL's <c>DefaultBaseType</c> handles v35 enums (not yet fixed on its development branch as of 2026-10-03).
/// </para>
/// </remarks>
internal static class EnumBaseTypeFix
{
    private const float FirstAffectedMetadataVersion = 35f;

    // The game's corlib System.Enum. V2 later injects a reference mscorlib and SystemTypes is redirected to it, so this
    // must be captured before any of V2's layers run.
    private static TypeAnalysisContext? s_gameSystemEnum;

    /// <summary>Patches Cpp2IL. Call once, before generation starts, so that no caller is compiled against the unpatched getter.</summary>
    public static void Install()
    {
        var harmony = new Harmony("lustral.generator.enum-base-type-fix");
        harmony.Patch(
            AccessTools.PropertyGetter(typeof(TypeAnalysisContext), nameof(TypeAnalysisContext.DefaultBaseType)),
            postfix: new HarmonyMethod(typeof(EnumBaseTypeFix), nameof(DefaultBaseTypePostfix)));
        harmony.Patch(
            AccessTools.Method(typeof(AsmResolverAssemblyPopulator), nameof(AsmResolverAssemblyPopulator.ConfigureHierarchy)),
            transpiler: new HarmonyMethod(typeof(EnumBaseTypeFix), nameof(ConfigureHierarchyTranspiler)));
    }

    /// <summary>Must run first: records the game's System.Enum while <c>SystemTypes</c> still refers to the game's corlib.</summary>
    public sealed class CaptureLayer : Cpp2IlProcessingLayer
    {
        public override string Name => "Lustral enum base type fix";
        public override string Id => "lustral_enum_base_type_fix";

        public override void Process(ApplicationAnalysisContext appContext, Action<int, int>? progressCallback = null)
        {
            if (appContext.MetadataVersion >= FirstAffectedMetadataVersion)
                s_gameSystemEnum = appContext.SystemTypes.EnumType;
        }
    }

    private static void DefaultBaseTypePostfix(TypeAnalysisContext __instance, ref TypeAnalysisContext? __result)
    {
        if (s_gameSystemEnum is not null && __instance.Definition is { IsEnumType: true })
            __result = s_gameSystemEnum;
    }

    // ConfigureHierarchy sets System.Enum as the base type of every v35+ enum, which would undo V2's struct conversion.
    // With the postfix, the model's base type is already correct, so the version check is made to see 0.
    private static IEnumerable<CodeInstruction> ConfigureHierarchyTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var getMetadataVersion = AccessTools.PropertyGetter(typeof(ApplicationAnalysisContext), nameof(ApplicationAnalysisContext.MetadataVersion));
        var result = new List<CodeInstruction>();
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(getMetadataVersion))
            {
                result.Add(new CodeInstruction(OpCodes.Pop).MoveLabelsFrom(instruction));
                result.Add(new CodeInstruction(OpCodes.Ldc_R4, 0f));
            }
            else
            {
                result.Add(instruction);
            }
        }

        var replaced = result.Count(i => i.opcode == OpCodes.Ldc_R4 && i.operand is 0f);
        if (replaced != 1)
            throw new InvalidOperationException($"Expected one metadata version check in ConfigureHierarchy, found {replaced}. Cpp2IL has changed; EnumBaseTypeFix must be revised.");
        return result;
    }
}
