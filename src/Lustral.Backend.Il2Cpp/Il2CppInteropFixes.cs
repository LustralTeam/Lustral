using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Startup;
using Il2CppInterop.Runtime.Structs;

namespace Lustral.Backend.Il2Cpp;

/// <summary>
/// Corrections to Il2CppInterop V2 (2.0.0-beta.8) that Lustral applies until they are fixed upstream. The defects affect
/// class injection (required by all managed-to-IL2CPP delegate conversions and injected MonoBehaviours), delegate
/// conversion, interop types, or the use of unstripped Unity libraries. Each fix verifies that the code it corrects is
/// still present, so fixes are skipped once Il2CppInterop is corrected.
/// </summary>
internal static class Il2CppInteropFixes
{
    // TrampolineBuilder's helper methods, which CreateTrampoline looks up on the wrong class.
    private static readonly string[] TrampolineHelpers = ["LogError", "UpdateBoxedValue", "ConvertNativeToManaged", "ConvertManagedToNative"];

    private static ILog? s_log;
    private static Type? s_trampolineBuilder;
    private static Type? s_typeInjector;
    private static Array? s_noParameters;
    private static TryGetInjectedClass? s_tryGetInjectedClass;
    private static Func<string, object>? s_toIl2CppString;

    private delegate bool TryGetInjectedClass(long token, out nint classPointer);

    public static void Apply(Harmony harmony, ILog log)
    {
        s_log = log;
        var runtime = typeof(Il2CppInteropRuntime).Assembly;
        FixFieldDefaultValueHook(harmony, runtime);
        FixTrampolineHelperLookups(harmony, runtime);
        FixTrampolineLifetime(harmony);
        FixInvokerCallingConvention(harmony);
        FixParameterlessDelegateConversion(harmony);
        FixNestedTypesOfGenericInstances(harmony);
        FixDuplicateMethodHashes(harmony);
        FixInjectedTypeLayout(harmony);
        FixInjectedTypeNames(harmony);
    }

    /// <summary>
    /// Il2CppInterop matches IL2CPP methods to managed methods by a hash of their signatures (NamedSignatureHash), for which
    /// IL2CPP names each parameter type. From Unity 6000.5, IL2CPP names a type from its metadata
    /// (GlobalMetadata::GetTypeNameInfoFromType), but an injected class has none, and IL2CPP reads past the end of its type
    /// table. Every delegate conversion names Il2CppInterop's own injected delegate holder, so converting any delegate
    /// crashed the game: Lustral's per-frame events in games that keep Unity's player loop, and mods' delegates. The
    /// transpiler names injected classes from their class data, and other types as before.
    /// </summary>
    private static void FixInjectedTypeNames(Harmony harmony)
    {
        var runtime = s_typeInjector?.Assembly;
        var getFullName = runtime?.GetType("Il2CppInterop.Runtime.Injection.NamedSignatureHash")?
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "GetFullName" && m.GetParameters() is [{ ParameterType.IsPointer: true }]);
        var tryGetClassPointer = runtime?.GetType("Il2CppInterop.Runtime.Injection.TokenAllocator")?
            .GetMethod("TryGetClassPointer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        var toIl2CppString = getFullName?.ReturnType.GetMethod("op_Implicit", BindingFlags.Static | BindingFlags.Public, [typeof(string)]);
        if (getFullName is null || tryGetClassPointer is null || toIl2CppString is null)
        {
            s_log?.Debug("Il2CppInterop's NamedSignatureHash or TokenAllocator has changed; injected types are named by IL2CPP");
            return;
        }
        s_tryGetInjectedClass = tryGetClassPointer.CreateDelegate<TryGetInjectedClass>();
        s_toIl2CppString = toIl2CppString.CreateDelegate<Func<string, object>>();
        harmony.Patch(getFullName, transpiler: new HarmonyMethod(typeof(Il2CppInteropFixes), nameof(NameInjectedTypesFirst)));
    }

    // var name = InjectedTypeName(type); if (name != null) return name; followed by the original body.
    private static IEnumerable<CodeInstruction> NameInjectedTypesFirst(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        var originalBody = generator.DefineLabel();
        CodeInstruction[] check =
        [
            new(OpCodes.Ldarg_0),
            new(OpCodes.Call, typeof(Il2CppInteropFixes).GetMethod(nameof(InjectedTypeName), BindingFlags.Static | BindingFlags.NonPublic)),
            new(OpCodes.Castclass, ((MethodInfo)original).ReturnType),
            new(OpCodes.Dup),
            new(OpCodes.Brfalse, originalBody),
            new(OpCodes.Ret),
            new(OpCodes.Pop) { labels = [originalBody] },
        ];
        return check.Concat(instructions);
    }

    // The full name of an injected class or struct as an Il2CppSystem.String, or null for any other type. Il2CppInterop
    // gives injected types negative tokens in place of a metadata handle.
    private static unsafe object? InjectedTypeName(Il2CppTypeStruct* type)
    {
        var wrapped = UnityVersionHandler.Wrap(type);
        if (wrapped.Type is not (Il2CppTypeEnum.IL2CPP_TYPE_CLASS or Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE) || wrapped.Data >= 0 ||
            !s_tryGetInjectedClass!(wrapped.Data, out var classPointer))
            return null;
        var klass = UnityVersionHandler.Wrap((Il2CppClass*)classPointer);
        var name = Marshal.PtrToStringUTF8(klass.Name);
        var @namespace = Marshal.PtrToStringUTF8(klass.Namespace);
        var fullName = string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
        return s_toIl2CppString!(wrapped.ByRef ? fullName + "&" : fullName!);
    }

    /// <summary>See <see cref="InjectedTypeLayout"/>: IL2CPP could not call injected classes through interfaces, and from
    /// Unity 6.6, no class could be injected.</summary>
    private static void FixInjectedTypeLayout(Harmony harmony)
    {
        if (s_typeInjector is null || !InjectedTypeLayout.Install(harmony, s_typeInjector))
            s_log?.Debug("Il2CppInterop's TypeInjector.SetVTableAndInterfaces has changed; its vtable and interface layout is not replaced");
    }

    /// <summary>
    /// When injecting a class, TypeInjector maps each inherited method by a hash of its name and the IL2CPP names of its
    /// types using <c>ToDictionary</c>, which throws if two methods have the same hash. With unstripped Unity libraries,
    /// <c>Object</c> has both <c>Instantiate(Object, Vector3, Quaternion)</c> and <c>Instantiate&lt;T&gt;(T, Vector3,
    /// Quaternion)</c>, which have the same hash, so no MonoBehaviour could be injected. The map is used only to match
    /// IL2CPP methods to managed methods, and a generic method never corresponds to a non-generic one, so the first method
    /// with each hash is kept, with non-generic methods first.
    /// </summary>
    private static void FixDuplicateMethodHashes(Harmony harmony)
    {
        var create = s_typeInjector?.GetMethod("CreateHashToMethodInfoDictionary", BindingFlags.Static | BindingFlags.NonPublic);
        if (create is null || !PatchProcessor.GetOriginalInstructions(create).Any(IsToDictionary))
        {
            s_log?.Debug("Il2CppInterop's TypeInjector.CreateHashToMethodInfoDictionary has changed; duplicate hashes are not handled");
            return;
        }
        harmony.Patch(create, transpiler: new HarmonyMethod(typeof(Il2CppInteropFixes), nameof(KeepFirstOfEachHash)));
    }

    private static bool IsToDictionary(CodeInstruction instruction) =>
        instruction.operand is MethodInfo { Name: nameof(Enumerable.ToDictionary), IsGenericMethod: true } method &&
        method.DeclaringType == typeof(Enumerable) && method.GetParameters().Length == 2;

    // Enumerable.ToDictionary<MethodInfo, NamedSignatureHash>(methods, hash) -> FirstOfEachKey<NamedSignatureHash>(methods, hash).
    private static IEnumerable<CodeInstruction> KeepFirstOfEachHash(IEnumerable<CodeInstruction> instructions)
    {
        var firstOfEachKey = typeof(Il2CppInteropFixes).GetMethod(nameof(FirstOfEachKey), BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var instruction in instructions)
        {
            if (IsToDictionary(instruction) && ((MethodInfo)instruction.operand).GetGenericArguments() is [var source, var key] && source == typeof(MethodInfo))
                instruction.operand = firstOfEachKey.MakeGenericMethod(key);
            yield return instruction;
        }
    }

    private static Dictionary<TKey, MethodInfo> FirstOfEachKey<TKey>(IEnumerable<MethodInfo> methods, Func<MethodInfo, TKey> key)
        where TKey : notnull
    {
        var result = new Dictionary<TKey, MethodInfo>();
        foreach (var method in methods.OrderBy(m => m.IsGenericMethodDefinition))
        {
            if (!result.TryAdd(key(method), method))
                s_log?.Debug($"Injection: {method.DeclaringType?.Name}.{method} has the same hash as {result[key(method)].DeclaringType?.Name}.{result[key(method)]}; keeping the latter");
        }
        return result;
    }

    /// <summary>
    /// Class injection hooks Class::GetFieldDefaultValue. V2 locates it as "the last call in Field::StaticGetValue", which on
    /// Unity 6000.3 (with inlined code) is an address in the middle of another function's epilogue, causing a crash a few
    /// seconds later.
    /// </summary>
    private static void FixFieldDefaultValueHook(Harmony harmony, Assembly runtime)
    {
        var find = runtime.GetType("Il2CppInterop.Runtime.Injection.Hooks.Class_GetFieldDefaultValue_Hook")?
            .GetMethod("FindTargetMethod", BindingFlags.Public | BindingFlags.Instance);
        if (find is null)
        {
            s_log?.Debug("Il2CppInterop's Class_GetFieldDefaultValue_Hook has changed; its search is not replaced");
            return;
        }
        harmony.Patch(find, prefix: new HarmonyMethod(typeof(Il2CppInteropFixes), nameof(FindFieldDefaultValue)));
    }

    private static bool FindFieldDefaultValue(ref nint __result)
    {
        try
        {
            var gameAssembly = NativeLibrary.Load("GameAssembly");
            var address = FieldDefaultValueFinder.Find(NativeLibrary.GetExport(gameAssembly, "il2cpp_field_static_get_value"));
            if (address == 0)
            {
                s_log?.Warn("Could not find Class::GetFieldDefaultValue; falling back to Il2CppInterop's search, which is incorrect on Unity 6000.3");
                return true;
            }

            s_log?.Debug($"Class::GetFieldDefaultValue at GameAssembly+0x{address - gameAssembly:X}");
            __result = address;
            return false;
        }
        catch (Exception e)
        {
            s_log?.Warn($"Could not find Class::GetFieldDefaultValue ({e.Message}); falling back to Il2CppInterop's search");
            return true;
        }
    }

    /// <summary>
    /// TrampolineBuilder.CreateTrampoline emits calls to its helper methods but looks them up with
    /// <c>typeof(TypeInjector).GetMethod(nameof(...))</c>, which returns null, so injecting any class with methods throws.
    /// The transpiler redirects these lookups to TrampolineBuilder.
    /// </summary>
    private static void FixTrampolineHelperLookups(Harmony harmony, Assembly runtime)
    {
        s_trampolineBuilder = runtime.GetType("Il2CppInterop.Runtime.Injection.TrampolineBuilder");
        s_typeInjector = runtime.GetType("Il2CppInterop.Runtime.Injection.TypeInjector");
        var createTrampoline = s_trampolineBuilder?.GetMethod("CreateTrampoline", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        const BindingFlags anyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var misplaced = createTrampoline is null || s_typeInjector is null
            ? []
            : TrampolineHelpers.Where(name => s_trampolineBuilder!.GetMethod(name, anyStatic) is not null && s_typeInjector.GetMethod(name, anyStatic) is null).ToArray();
        if (misplaced.Length == 0)
        {
            s_log?.Debug("Il2CppInterop's TrampolineBuilder helper lookups are correct; no fix needed");
            return;
        }
        harmony.Patch(createTrampoline, transpiler: new HarmonyMethod(typeof(Il2CppInteropFixes), nameof(RetargetHelperLookups)));
    }

    // Replaces `ldtoken TypeInjector`, followed (through GetTypeFromHandle) by `ldstr "<helper>"`, with `ldtoken TrampolineBuilder`.
    private static IEnumerable<CodeInstruction> RetargetHelperLookups(IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        var fixedCount = 0;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].opcode != OpCodes.Ldtoken || !Equals(list[i].operand, s_typeInjector))
                continue;
            var name = list.Skip(i + 1).Take(3).FirstOrDefault(x => x.opcode == OpCodes.Ldstr)?.operand as string;
            if (name is null || !TrampolineHelpers.Contains(name))
                continue;
            list[i].operand = s_trampolineBuilder;
            fixedCount++;
        }
        s_log?.Debug($"Retargeted {fixedCount} TrampolineBuilder helper lookups in CreateTrampoline");
        return list;
    }

    // All trampoline delegates passed to IL2CPP; injected methods are never unregistered.
    private static readonly List<Delegate> s_trampolines = [];

    /// <summary>
    /// TypeInjector passes each injected method to IL2CPP as
    /// <c>Marshal.GetFunctionPointerForDelegate(CreateTrampoline(...))</c> without keeping a reference to the delegate, as
    /// that function requires; once the GC collects the delegate, IL2CPP would call freed code. The postfix keeps all
    /// trampolines alive, including those used by delegate conversions.
    /// </summary>
    private static void FixTrampolineLifetime(Harmony harmony)
    {
        var createTrampoline = s_trampolineBuilder?.GetMethod("CreateTrampoline", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        if (createTrampoline is null || createTrampoline.ReturnType != typeof(Delegate))
        {
            s_log?.Debug("Il2CppInterop's TrampolineBuilder.CreateTrampoline was not found as expected; trampolines are not kept alive");
            return;
        }
        harmony.Patch(createTrampoline, postfix: new HarmonyMethod(typeof(Il2CppInteropFixes), nameof(KeepTrampoline)));
    }

    private static void KeepTrampoline(Delegate __result)
    {
        lock (s_trampolines)
            s_trampolines.Add(__result);
    }

    /// <summary>
    /// IL2CPP calls an injected method through its invoker (for Unity messages and <c>il2cpp_runtime_invoke</c>), which V2
    /// emits in TypeInjector.CreateInvoker as a dynamic method that performs a <c>calli</c> to the method's native pointer.
    /// That pointer is the result of <c>Marshal.GetFunctionPointerForDelegate</c> for the trampoline, which is native code.
    /// The <c>calli</c> uses the managed calling convention, and CoreCLR fails fast when managed code enters such a
    /// native-callable stub (0x80131506, an internal error in coreclr.dll), so every injected MonoBehaviour crashed on its
    /// first message. The transpiler converts it to an unmanaged <c>calli</c>, which performs a proper transition. Direct
    /// native calls to the pointer (delegates invoked by IL2CPP) are not affected.
    /// </summary>
    private static void FixInvokerCallingConvention(Harmony harmony)
    {
        var createInvoker = s_typeInjector?.GetMethod("CreateInvoker", BindingFlags.Static | BindingFlags.NonPublic);
        if (createInvoker is null || !PatchProcessor.GetOriginalInstructions(createInvoker).Any(IsManagedCalli))
        {
            s_log?.Debug("Il2CppInterop's TypeInjector.CreateInvoker emits no managed calli; no fix needed");
            return;
        }
        harmony.Patch(createInvoker, transpiler: new HarmonyMethod(typeof(Il2CppInteropFixes), nameof(UseUnmanagedCalli)));
    }

    private static readonly MethodInfo ManagedEmitCalli = typeof(ILGenerator).GetMethod(nameof(ILGenerator.EmitCalli),
        [typeof(OpCode), typeof(CallingConventions), typeof(Type), typeof(Type[]), typeof(Type[])])!;

    private static bool IsManagedCalli(CodeInstruction instruction) => instruction.Calls(ManagedEmitCalli);

    // Replaces ilGenerator.EmitCalli(opcode, CallingConventions.Standard, ret, params, null) with
    // EmitUnmanagedCalli(ilGenerator, ...), with the same arguments on the stack.
    private static IEnumerable<CodeInstruction> UseUnmanagedCalli(IEnumerable<CodeInstruction> instructions)
    {
        var helper = typeof(Il2CppInteropFixes).GetMethod(nameof(EmitUnmanagedCalli), BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var instruction in instructions)
        {
            if (IsManagedCalli(instruction))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = helper;
            }
            yield return instruction;
        }
    }

    // The trampoline's pointer comes from GetFunctionPointerForDelegate, which uses the platform's default convention (Winapi).
    private static void EmitUnmanagedCalli(ILGenerator il, OpCode opcode, CallingConventions managed, Type? returnType, Type[]? parameterTypes, Type[]? optional) =>
        il.EmitCalli(opcode, CallingConvention.Winapi, returnType, parameterTypes);

    /// <summary>
    /// DelegateSupport.ConvertDelegate reads the IL2CPP delegate's parameters through the interop
    /// <c>MethodBase.GetParameters()</c>. For a method without parameters, corlib returns a cached array whose IL2CPP class
    /// is <c>RuntimeParameterInfo[]</c>; V2 wraps it as <c>Il2CppArrayRank1&lt;RuntimeParameterInfo&gt;</c>, and the cast to
    /// <c>Il2CppArrayRank1&lt;ParameterInfo&gt;</c> throws. As a result, <c>Action</c>, <c>UnityAction</c> and other
    /// parameterless delegates fail to convert. The transpiler returns an empty parameter list before that call.
    /// </summary>
    private static void FixParameterlessDelegateConversion(Harmony harmony)
    {
        var getParameters = typeof(DelegateSupport).GetMethod("GetParameters", BindingFlags.Static | BindingFlags.NonPublic);
        if (getParameters is not { ReturnType: { IsGenericType: true } listType } || listType.GetGenericTypeDefinition() != typeof(IReadOnlyList<>))
        {
            s_log?.Debug("Il2CppInterop's DelegateSupport.GetParameters has changed; the parameterless case is not added");
            return;
        }
        s_noParameters = Array.CreateInstance(listType.GetGenericArguments()[0], 0);
        harmony.Patch(getParameters, transpiler: new HarmonyMethod(typeof(Il2CppInteropFixes), nameof(ShortCircuitNoParameters)));
    }

    // if (HasNoParameters(method)) return (IReadOnlyList<ParameterInfo>)s_noParameters; followed by the original body.
    private static IEnumerable<CodeInstruction> ShortCircuitNoParameters(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        var originalBody = generator.DefineLabel();
        CodeInstruction[] check =
        [
            new(OpCodes.Ldarg_0),
            new(OpCodes.Call, typeof(Il2CppInteropFixes).GetMethod(nameof(HasNoParameters), BindingFlags.Static | BindingFlags.NonPublic)),
            new(OpCodes.Brfalse, originalBody),
            new(OpCodes.Ldsfld, typeof(Il2CppInteropFixes).GetField(nameof(s_noParameters), BindingFlags.Static | BindingFlags.NonPublic)),
            new(OpCodes.Castclass, ((MethodInfo)original).ReturnType),
            new(OpCodes.Ret),
        ];
        var body = instructions.ToList();
        body[0].labels.Add(originalBody);
        return check.Concat(body);
    }

    // method is an Il2CppSystem.Reflection.MethodBase; Lustral has no compile-time reference to Il2Cppmscorlib.
    private static bool HasNoParameters(object? method)
    {
        if (method is not IIl2CppType il2CppObject)
            return false;
        var native = IL2CPP.il2cpp_method_get_from_reflection((nint)il2CppObject.BoxNative());
        return native != 0 && IL2CPP.il2cpp_method_get_param_count(native) == 0;
    }

    /// <summary>
    /// Nested types of generic instances (<c>List&lt;T&gt;.Enumerator</c>, <c>Dictionary&lt;TKey, TValue&gt;.Entry</c>) are
    /// resolved through IL2CPP's managed reflection, <c>Type.GetNestedType</c>, because IL2CPP lists no nested types for
    /// inflated classes. This runs the game's corlib code, which depends on static constructors that may not have run yet;
    /// on Unity 6000.5, it throws for every such type. As in .NET, the nested types of a generic instance are those of its
    /// generic definition, so the prefix queries IL2CPP's metadata API for them, without running managed code or class
    /// initialization.
    /// </summary>
    private static void FixNestedTypesOfGenericInstances(Harmony harmony)
    {
        var lookup = typeof(GenerationInternals).GetMethod("GetIl2CppNestedType", BindingFlags.Public | BindingFlags.Static, [typeof(nint), typeof(string)]);
        if (lookup is null || !ClassMetadata.Available)
        {
            s_log?.Debug("Il2CppInterop's GenerationInternals.GetIl2CppNestedType has changed or IL2CPP lacks a class export; its reflection fallback is not replaced");
            return;
        }
        harmony.Patch(lookup, prefix: new HarmonyMethod(typeof(Il2CppInteropFixes), nameof(NestedTypeOfGenericInstance)));
    }

    private static bool NestedTypeOfGenericInstance(nint __0, string __1, ref nint __result)
    {
        if (__0 == 0 || !ClassMetadata.IsInflated(__0))
            return true;
        var nested = ClassMetadata.FindNested(ClassMetadata.GenericDefinition(__0), __1);
        if (nested == 0)
        {
            s_log?.Debug($"Nested type {__1} of an inflated class was not found through IL2CPP's metadata; using Il2CppInterop's reflection fallback");
            return true;
        }
        __result = nested;
        return false;
    }

    // IL2CPP class metadata exports. None of them runs managed code or class initialization.
    private static unsafe class ClassMetadata
    {
        private static readonly nint s_gameAssembly = NativeLibrary.Load("GameAssembly");
        private static readonly delegate* unmanaged<nint, byte> s_isInflated = (delegate* unmanaged<nint, byte>)Export("il2cpp_class_is_inflated");
        private static readonly delegate* unmanaged<nint, nint> s_getDeclaringType = (delegate* unmanaged<nint, nint>)Export("il2cpp_class_get_declaring_type");
        private static readonly delegate* unmanaged<nint, nint> s_getImage = (delegate* unmanaged<nint, nint>)Export("il2cpp_class_get_image");
        private static readonly delegate* unmanaged<nint, nint> s_getNamespace = (delegate* unmanaged<nint, nint>)Export("il2cpp_class_get_namespace");
        private static readonly delegate* unmanaged<nint, nint> s_getName = (delegate* unmanaged<nint, nint>)Export("il2cpp_class_get_name");
        private static readonly delegate* unmanaged<nint, nint, nint, nint> s_fromName = (delegate* unmanaged<nint, nint, nint, nint>)Export("il2cpp_class_from_name");
        private static readonly delegate* unmanaged<nint, nint*, nint> s_getNestedTypes = (delegate* unmanaged<nint, nint*, nint>)Export("il2cpp_class_get_nested_types");

        public static bool Available => s_isInflated != null && s_getDeclaringType != null && s_getImage != null && s_getNamespace != null
            && s_getName != null && s_fromName != null && s_getNestedTypes != null;

        public static bool IsInflated(nint klass) => s_isInflated(klass) != 0;

        // The generic definition of an inflated class, by name: from its image, or from its declaring type's nested types.
        public static nint GenericDefinition(nint klass)
        {
            if (!IsInflated(klass))
                return klass;
            var declaring = s_getDeclaringType(klass);
            return declaring != 0
                ? FindNested(GenericDefinition(declaring), Marshal.PtrToStringUTF8(s_getName(klass)))
                : s_fromName(s_getImage(klass), s_getNamespace(klass), s_getName(klass));
        }

        public static nint FindNested(nint klass, string? name)
        {
            if (klass == 0 || name is null)
                return 0;
            nint iterator = 0, nested;
            while ((nested = s_getNestedTypes(klass, &iterator)) != 0)
            {
                if (Marshal.PtrToStringUTF8(s_getName(nested)) == name)
                    return nested;
            }
            return 0;
        }

        private static nint Export(string name) => NativeLibrary.TryGetExport(s_gameAssembly, name, out var address) ? address : 0;
    }
}
