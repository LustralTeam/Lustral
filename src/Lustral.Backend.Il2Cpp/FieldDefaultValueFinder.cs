extern alias iced;

using iced::Iced.Intel;

namespace Lustral.Backend.Il2Cpp;

/// <summary>
/// Finds IL2CPP's <c>Class::GetFieldDefaultValue(FieldInfo*, const Il2CppType**)</c> (or the
/// <c>MetadataCache::GetFieldDefaultValueForField</c> that it inlines) by analyzing the code's behavior rather than its
/// position.
/// </summary>
/// <remarks>
/// Il2CppInterop V2 locates it as "the last call in Field::StaticGetValue". On Unity 6000.3, the compiler inlines
/// StaticGetValueInternal and GetDefaultFieldValue into Field::StaticGetValue, so that call is the blob reader, and V2
/// hooks an address in the middle of another function's epilogue, which crashes the game once class injection is used.
/// This search follows libil2cpp's logic instead: in the code reached from <c>il2cpp_field_static_get_value</c>, it
/// finds the <c>FIELD_ATTRIBUTE_LITERAL</c> test on the field's type and, on the literal path, takes the first call that
/// receives a stack address in RDX (the <c>&amp;type</c> out parameter). This works whether or not the functions are
/// inlined.
/// </remarks>
internal static unsafe class FieldDefaultValueFinder
{
    private const int FieldAttributeLiteral = 0x40; // Il2CppType.attrs, at offset 8 on x64.
    private const int MaxInstructionsPerFunction = 400;

    /// <param name="staticGetValueExport">Address of the <c>il2cpp_field_static_get_value</c> export.</param>
    /// <returns>The function's address, or 0 if the code does not match the expected pattern.</returns>
    public static nint Find(nint staticGetValueExport)
    {
        var start = FollowThunks(staticGetValueExport);
        var address = SearchFunction(start, depth: 3);
        return address % 16 == 0 ? address : 0; // MSVC aligns functions to 16 bytes.
    }

    private static nint SearchFunction(nint function, int depth)
    {
        var instructions = Decode(function);
        for (var i = 0; i < instructions.Count - 1; i++)
        {
            if (!IsLiteralTest(instructions[i]) || !instructions[i + 1].FlowControl.Equals(FlowControl.ConditionalBranch))
                continue;

            // je/jz skips the literal path; jne/jnz jumps to it.
            var branch = instructions[i + 1];
            var literalPath = branch.ConditionCode == ConditionCode.e ? branch.NextIP : branch.NearBranchTarget;
            return CallWithStackOutParameter(literalPath, depth);
        }

        // No literal test here, because StaticGetValueInternal was not inlined; searches the functions called from here.
        if (depth == 0)
            return 0;
        foreach (var call in instructions.Where(x => x.FlowControl == FlowControl.Call && x.Op0Kind == OpKind.NearBranch64))
        {
            var found = SearchFunction(FollowThunks((nint)call.NearBranch64), depth - 1);
            if (found != 0)
                return found;
        }
        return 0;
    }

    // The first call after `start` whose RDX was set by `lea rdx, [rsp+...]`. If the first call passes another value, it is
    // a non-inlined GetDefaultFieldValue(field, value), and the target call is inside it.
    private static nint CallWithStackOutParameter(ulong start, int depth)
    {
        var rdxIsStackAddress = false;
        foreach (var instruction in Decode((nint)start, maxInstructions: 40))
        {
            if (instruction.Mnemonic == Mnemonic.Lea && instruction.Op0Register == Register.RDX)
                rdxIsStackAddress = instruction.MemoryBase == Register.RSP;
            else if (instruction.Op0Kind == OpKind.Register && instruction.Op0Register is Register.RDX or Register.EDX or Register.DX or Register.DL)
                rdxIsStackAddress = false;

            if (instruction.FlowControl != FlowControl.Call || instruction.Op0Kind != OpKind.NearBranch64)
                continue;
            if (rdxIsStackAddress)
                return (nint)instruction.NearBranch64;
            return depth > 0 ? CallWithStackOutParameter((ulong)FollowThunks((nint)instruction.NearBranch64), depth - 1) : 0;
        }
        return 0;
    }

    private static bool IsLiteralTest(in Instruction instruction) =>
        instruction.Mnemonic == Mnemonic.Test &&
        instruction.Op0Kind == OpKind.Memory && instruction.MemoryDisplacement64 == 8 && instruction.MemoryIndex == Register.None &&
        instruction.Op1Kind is OpKind.Immediate8 or OpKind.Immediate16 or OpKind.Immediate32 &&
        instruction.GetImmediate(1) == FieldAttributeLiteral;

    // Exports are often single-instruction thunks (`jmp target`, sometimes after setting a default argument).
    private static nint FollowThunks(nint address)
    {
        for (var hops = 0; hops < 4; hops++)
        {
            var instructions = Decode(address, maxInstructions: 3);
            var jump = instructions.FirstOrDefault(x => x.FlowControl == FlowControl.UnconditionalBranch && x.Op0Kind == OpKind.NearBranch64);
            var index = instructions.IndexOf(jump);
            if (index < 0 || instructions.Take(index).Any(x => x.FlowControl != FlowControl.Next || x.Mnemonic is Mnemonic.Push or Mnemonic.Sub))
                return address;
            address = (nint)jump.NearBranch64;
        }
        return address;
    }

    // Decodes linearly until the function's first ret, or the limit. Sufficient for the short functions involved.
    private static List<Instruction> Decode(nint address, int maxInstructions = MaxInstructionsPerFunction)
    {
        var decoder = iced::Iced.Intel.Decoder.Create(64, new UnmanagedCodeReader(address), (ulong)address);
        var instructions = new List<Instruction>();
        while (instructions.Count < maxInstructions)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid)
                break;
            instructions.Add(instruction);
            if (instruction.FlowControl == FlowControl.Return)
                break;
        }
        return instructions;
    }

    private sealed class UnmanagedCodeReader(nint address) : CodeReader
    {
        private byte* _next = (byte*)address;

        public override int ReadByte() => *_next++;
    }
}
