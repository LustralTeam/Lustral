using AsmResolver.DotNet;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using HarmonyLib;
using LibCpp2IL.Metadata;

namespace Lustral.Generator;

/// <summary>
/// Works around Cpp2IL on IL2CPP metadata without the "PackingSize is default" and "ClassSize is default" type
/// definition bits (Unity 2018; IL2CPP's headers define them in Unity 2021.3).
/// </summary>
/// <remarks>
/// Cpp2IL declares a struct's size unless its type definition marks the class size as default, and uses the native
/// (marshaling) size for it. Older metadata has no such bit, so every struct receives a declared size, which for
/// <c>bool</c> is 4 bytes and for <c>char</c> is 1, and Il2CppInterop lays out its interop structs accordingly: reading a
/// bool, or a struct that contains one, reads the wrong bytes (unboxing a bool throws). When no type definition in the
/// metadata has either bit set, this fix declares each struct's actual size in IL2CPP's memory instead: its instance
/// size less the object header. That size is also correct for structs that do declare a size, such as fixed buffers.
/// </remarks>
internal static class TypeSizeFix
{
    private const uint DefaultBits = 1u << 10 | 1u << 11;

    private static bool s_bitsAbsent;
    private static uint s_objectHeaderSize;

    /// <summary>Patches Cpp2IL. Call once, before generation starts.</summary>
    public static void Install() =>
        new Harmony("lustral.generator.type-size-fix").Patch(
            AccessTools.Method(typeof(AsmResolverDllOutputFormat), "ConfigureTypeSize"),
            prefix: new HarmonyMethod(typeof(TypeSizeFix), nameof(ConfigureTypeSizePrefix)));

    /// <summary>Must run before the output is written: determines whether the game's metadata has the bits.</summary>
    public sealed class CaptureLayer : Cpp2IlProcessingLayer
    {
        public override string Name => "Lustral type size fix";
        public override string Id => "lustral_type_size_fix";

        public override void Process(ApplicationAnalysisContext appContext, Action<int, int>? progressCallback = null)
        {
            s_bitsAbsent = appContext.Metadata.typeDefs.All(t => (t.Bitfield & DefaultBits) == 0);
            s_objectHeaderSize = appContext.Binary.is32Bit ? 8u : 16u;
        }
    }

    // Replaces Cpp2IL's ConfigureTypeSize where the metadata lacks the bits; otherwise Cpp2IL's runs.
    private static bool ConfigureTypeSizePrefix(Il2CppTypeDefinition il2CppDefinition, TypeDefinition asmResolverDefinition)
    {
        if (!s_bitsAbsent)
            return true;

        if (!il2CppDefinition.IsValueType || il2CppDefinition.IsEnumType)
            return false;
        var instanceSize = il2CppDefinition.RawSizes.instance_size;
        var packing = (ushort)il2CppDefinition.PackingSize;
        var size = instanceSize > s_objectHeaderSize ? instanceSize - s_objectHeaderSize : 0u;
        if (packing != 0 || size != 0)
            asmResolverDefinition.ClassLayout = new ClassLayout(packing, size);
        return false;
    }
}
