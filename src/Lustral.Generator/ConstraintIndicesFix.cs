using System.Buffers.Binary;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.Model.Contexts;
using LibCpp2IL.BinaryStructures;
using LibCpp2IL.Metadata;

namespace Lustral.Generator;

/// <summary>
/// Works around LibCpp2IL on IL2CPP metadata v38 and newer (Unity 6000.x; BOMBANANA! is v39).
/// </summary>
/// <remarks>
/// From v38, type indices in the metadata are 1, 2 or 4 bytes wide, depending on the number of types. LibCpp2IL reads
/// the generic parameter constraint table with the correct width but sizes it as <c>Size / sizeof(int)</c>, so with
/// 2-byte indices it reads only the first half, and all constraints stored beyond that point are lost. Interface methods
/// such as Unity Netcode's <c>INetworkSerializable.NetworkSerialize&lt;T&gt;</c> then lose <c>where T : IReaderWriter</c>
/// while their implementations keep it, and CoreCLR rejects the implementing types ("weaker type parameter constraints").
/// <para>
/// This layer rereads the table from <c>global-metadata.dat</c> with the correct count before any constraints are read.
/// It has no effect if LibCpp2IL's table has the expected length (not yet fixed on Cpp2IL's development branch as of
/// 2026-10-03).
/// </para>
/// </remarks>
internal sealed class ConstraintIndicesFix(string metadataPath) : Cpp2IlProcessingLayer
{
    private const float FirstAffectedMetadataVersion = 38f;

    public override string Name => "Lustral generic constraint table fix";
    public override string Id => "lustral_constraint_indices_fix";

    public override void Process(ApplicationAnalysisContext appContext, Action<int, int>? progressCallback = null)
    {
        if (appContext.MetadataVersion < FirstAffectedMetadataVersion)
            return;

        var metadata = appContext.Metadata;
        var header = metadata.metadataHeader;
        var section = header.genericParameterConstraints;

        // Derived as in LibCpp2IL: an interface offset entry consists of a type index and a 4-byte offset.
        var typeIndexWidth = header.interfaceOffsets.Size / header.interfaceOffsets.Count - sizeof(int);
        if (typeIndexWidth is not (1 or 2 or 4))
            throw new InvalidDataException($"Unexpected type index width {typeIndexWidth} in {metadataPath}");

        var count = section.Size / typeIndexWidth;
        if (metadata.constraintIndices.Length == count)
            return;

        var bytes = new byte[section.Size];
        using (var file = File.OpenRead(metadataPath))
        {
            file.Position = section.Offset;
            file.ReadExactly(bytes);
        }

        var indices = new Il2CppVariableWidthIndex<Il2CppType>[count];
        for (var i = 0; i < count; i++)
        {
            var span = bytes.AsSpan(i * typeIndexWidth, typeIndexWidth);
            indices[i] = typeIndexWidth switch
            {
                1 => Index(span[0], nullValue: byte.MaxValue),
                2 => Index(BinaryPrimitives.ReadUInt16LittleEndian(span), nullValue: ushort.MaxValue),
                _ => Index(BinaryPrimitives.ReadInt32LittleEndian(span), nullValue: -1),
            };
        }

        Console.WriteLine($"[{nameof(ConstraintIndicesFix)}] LibCpp2IL read {metadata.constraintIndices.Length} of {count} generic constraints; restored the rest");
        metadata.constraintIndices = indices;
    }

    // Narrow indices use the all-ones value for "none", as in LibCpp2IL's reader.
    private static Il2CppVariableWidthIndex<Il2CppType> Index(int value, int nullValue) =>
        value == nullValue ? Il2CppVariableWidthIndex<Il2CppType>.Null : Il2CppVariableWidthIndex<Il2CppType>.MakeTemporaryForFixedWidthUsage(value);
}
