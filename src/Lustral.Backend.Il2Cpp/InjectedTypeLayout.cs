using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using HarmonyLib;
using Il2CppInterop.Common;
using Il2CppInterop.Common.Attributes;
using Il2CppInterop.Runtime.Structs;
using Il2CppInterop.Runtime.Structs.VersionSpecific.Class;
using Il2CppInterop.Runtime.Structs.VersionSpecific.MethodInfo;

namespace Lustral.Backend.Il2Cpp;

/// <summary>
/// Lays out the virtual method table and the interface tables of the classes and structs that Il2CppInterop V2
/// (2.0.0-beta.8) injects, in place of TypeInjector's SetVTableAndInterfaces, which IL2CPP cannot use:
/// <list type="bullet">
/// <item>It takes an IL2CPP interface's vtable count as the number of its slots, but IL2CPP interfaces have no vtable
/// (their methods have slots 0 to n-1), so it gives an injected class no interface slots. Unity found a component that
/// implements <c>IPointerClickHandler</c>, but calling it crashed the game.</item>
/// <item>It does not set the class's vtable count.</item>
/// <item>It copies the base class's table only up to its first interface, then moves the base class's interface blocks
/// behind the class's own virtual methods, so base class methods that follow an interface block lose their slots.</item>
/// <item>From Unity 6.6, IL2CPP keeps all interfaces of a class in a single array, which Il2CppInterop does not
/// support: it throws for every injected class.</item>
/// </list>
/// Lustral keeps the base class's table in place, with the class's overrides, appends the virtual methods that the class
/// declares and a block for each IL2CPP interface that the base class does not implement, and writes the interface tables
/// in the layout of the running Unity version. Il2CppInterop's own code still handles injected interfaces.
/// </summary>
internal static unsafe class InjectedTypeLayout
{
    private const ushort NoSlot = ushort.MaxValue;
    private const int NotDispatchable = -1; // libil2cpp's kInvalidInterfaceOffset

    private static Type s_valueType = null!; // Il2CppSystem.ValueType, the base class of injected structs
    private static Action<Type, INativeClassStruct, int> s_setVTableAndInterfaces = null!;
    private static Action<Type, INativeClassStruct> s_setClassData = null!;
    private static Func<Type, nint> s_classPointer = null!;
    private static HashSet<Type> s_needsInitialized = null!;
    private static Dictionary<Type, int> s_needsVTableSet = null!;
    private static Func<Type, bool> s_isIl2CppInterface = null!;
    private static Func<Type, IEnumerable<MethodInfo>> s_il2CppMethods = null!;
    // TypeInjector's maps between managed methods, IL2CPP methods and the hashes that match them.
    private static Func<Type, IDictionary> s_methodToHash = null!;
    private static Func<INativeClassStruct, IDictionary> s_hashToNative = null!;
    private static Func<Type, IDictionary> s_hashToMethod = null!;
    private static Func<INativeClassStruct, IDictionary> s_nativeToHash = null!;

    /// <summary>Replaces SetVTableAndInterfaces if TypeInjector still has it as expected; returns whether it did.</summary>
    public static bool Install(Harmony harmony, Type typeInjector)
    {
        const BindingFlags anyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var methods = typeInjector.GetMethods(anyStatic);
        MethodInfo? Local(string name) => methods.FirstOrDefault(m => m.Name.Contains($">g__{name}|", StringComparison.Ordinal));
        MethodInfo? Private(string name) => typeInjector.GetMethod(name, anyStatic);

        var setVTable = Local("SetVTableAndInterfaces");
        var setClassData = Local("SetClassData");
        var classPointer = Local("GetClassPointerNotNull");
        var upperBound = Private("CalculateVTableUpperBoundUsingReflection");
        var helpers = new[] { Private("IsIl2CppInterface"), Private("GetIl2CppMethods"), Private("CreateMethodInfoToHashDictionary"),
            Private("CreateHashToNativeMethodInfoDictionary"), Private("CreateHashToMethodInfoDictionary"), Private("CreateNativeMethodInfoToHashDictionary") };
        var needsInitialized = typeInjector.GetField("NeedsInitialized", anyStatic)?.GetValue(null) as HashSet<Type>;
        var needsVTableSet = typeInjector.GetField("NeedsVTableSet", anyStatic)?.GetValue(null) as Dictionary<Type, int>;
        // The copy of Il2CppSystem.ValueType that TypeInjector uses, from the interop corlib that it references.
        var corlib = typeInjector.Assembly.GetReferencedAssemblies().FirstOrDefault(a => a.Name == "Il2Cppmscorlib");
        var valueType = corlib is null ? null : AssemblyLoadContext.GetLoadContext(typeInjector.Assembly)?.LoadFromAssemblyName(corlib).GetType("Il2CppSystem.ValueType");
        if (valueType is null || setVTable is null || setClassData is null || classPointer is null || upperBound is null || helpers.Any(h => h is null) ||
            needsInitialized is null || needsVTableSet is null ||
            !PatchProcessor.GetOriginalInstructions(setVTable).Any(i => i.operand is MethodInfo { Name: "get_ImplementedInterfaces" }))
            return false;

        s_valueType = valueType;
        s_setVTableAndInterfaces = setVTable.CreateDelegate<Action<Type, INativeClassStruct, int>>();
        s_setClassData = setClassData.CreateDelegate<Action<Type, INativeClassStruct>>();
        s_classPointer = classPointer.CreateDelegate<Func<Type, nint>>();
        s_needsInitialized = needsInitialized;
        s_needsVTableSet = needsVTableSet;
        s_isIl2CppInterface = helpers[0]!.CreateDelegate<Func<Type, bool>>();
        s_il2CppMethods = helpers[1]!.CreateDelegate<Func<Type, IEnumerable<MethodInfo>>>();
        s_methodToHash = helpers[2]!.CreateDelegate<Func<Type, IDictionary>>();
        s_hashToNative = helpers[3]!.CreateDelegate<Func<INativeClassStruct, IDictionary>>();
        s_hashToMethod = helpers[4]!.CreateDelegate<Func<Type, IDictionary>>();
        s_nativeToHash = helpers[5]!.CreateDelegate<Func<INativeClassStruct, IDictionary>>();

        harmony.Patch(setVTable, prefix: new HarmonyMethod(typeof(InjectedTypeLayout), nameof(SetVTableAndInterfaces)));
        harmony.Patch(upperBound, postfix: new HarmonyMethod(typeof(InjectedTypeLayout), nameof(EnoughVTableSlots)));
        return true;
    }

    private static bool SetVTableAndInterfaces(Type __0, INativeClassStruct __1, int __2)
    {
        if (__0.IsInterface || (__0.BaseType is null && !__0.IsValueType))
            return true;
        LayOut(__0, __1, __2);
        return false;
    }

    private static void LayOut(Type type, INativeClassStruct klass, int allocated)
    {
        if (s_needsInitialized.Contains(type))
            s_setClassData(type, klass);

        var baseType = type.IsValueType ? s_valueType : type.BaseType!;
        var baseClass = UnityVersionHandler.Wrap((Il2CppClass*)s_classPointer(baseType));
        if (s_needsVTableSet.TryGetValue(baseType, out var baseAllocated))
            s_setVTableAndInterfaces(baseType, baseClass, baseAllocated);

        var interfaces = type.GetInterfaces().Where(s_isIl2CppInterface).ToArray();
        var newInterfaces = interfaces.Where(i => !i.IsAssignableFrom(baseType)).ToArray();
        foreach (var @interface in newInterfaces)
        {
            if (s_needsVTableSet.TryGetValue(@interface, out var interfaceAllocated))
                s_setVTableAndInterfaces(@interface, Class(@interface), interfaceAllocated);
        }

        var layout = new Layout(type, klass, allocated);

        // The base class's table, in place, with this class's overrides.
        var declared = s_il2CppMethods(type).ToArray();
        var overrides = new Dictionary<MethodInfo, MethodInfo>();
        foreach (var method in declared)
        {
            if (method.GetBaseDefinition() is var overridden && overridden != method)
                overrides.TryAdd(overridden, method);
        }
        var baseHashToMethod = s_hashToMethod(baseType);
        var baseNativeToHash = s_nativeToHash(baseClass);
        for (var slot = 0; slot < baseClass.VTableCount; slot++)
        {
            var entry = baseClass.VTable[slot];
            layout.Set(slot, entry);
            if (overrides.Count > 0 && Lookup(baseHashToMethod, Lookup(baseNativeToHash, (nint)entry.method)) is MethodInfo inherited &&
                overrides.TryGetValue(inherited.GetBaseDefinition(), out var @override))
                layout.Set(slot, layout.Native(@override));
        }
        var index = (int)baseClass.VTableCount;

        // Virtual methods that this class declares, for classes derived from it.
        foreach (var method in declared)
        {
            if ((method.IsVirtual || method.IsAbstract) && !method.IsFinal && method.GetBaseDefinition() == method)
                layout.Set(index++, layout.Native(method));
        }

        // The base class's interfaces, which this class may implement again.
        var byPointer = interfaces.ToDictionary(i => s_classPointer(i));
        var dispatch = DispatchEntries(baseClass);
        foreach (var (pointer, offset) in dispatch)
        {
            if (byPointer.TryGetValue(pointer, out var @interface))
                layout.Implement(@interface, UnityVersionHandler.Wrap((Il2CppClass*)pointer), offset, ownMethodsOnly: true);
        }

        // A block for each interface that the base class does not implement. Interfaces without methods have no block.
        foreach (var @interface in newInterfaces)
        {
            var itf = Class(@interface);
            var slots = SlotCount(itf);
            if (slots == 0)
                continue;
            dispatch.Add(((nint)itf.ClassPointer, index));
            layout.Implement(@interface, itf, index, ownMethodsOnly: false);
            index += slots;
        }

        klass.VTableCount = (ushort)index;
        if (klass.HasCombinedInterfaceData)
            WriteCombinedInterfaces(klass, baseClass, newInterfaces, dispatch);
        else
            WriteInterfaces(klass, baseClass, newInterfaces, dispatch);
        klass.IsVtableInitialized = true;
        s_needsVTableSet.Remove(type);
    }

    // Before Unity 6.6: the interfaces that the class implements (Il2CppInterop also lists the base class's), and the
    // vtable offset of each interface. IL2CPP also checks the base classes' tables.
    private static void WriteInterfaces(INativeClassStruct klass, INativeClassStruct baseClass, Type[] newInterfaces, List<(nint Interface, int Offset)> dispatch)
    {
        var implemented = new List<nint>();
        for (var i = 0; baseClass.ImplementedInterfaces != null && i < baseClass.InterfaceCount; i++)
            implemented.Add((nint)baseClass.ImplementedInterfaces[i]);
        implemented.AddRange(newInterfaces.Select(i => s_classPointer(i)));
        klass.InterfaceCount = (ushort)implemented.Count;
        klass.ImplementedInterfaces = (Il2CppClass**)Marshal.AllocHGlobal(Math.Max(implemented.Count, 1) * sizeof(nint));
        for (var i = 0; i < implemented.Count; i++)
            klass.ImplementedInterfaces[i] = (Il2CppClass*)implemented[i];

        klass.InterfaceOffsetsCount = (ushort)dispatch.Count;
        klass.InterfaceOffsets = (Il2CppRuntimeInterfaceOffsetPair*)Marshal.AllocHGlobal(Math.Max(dispatch.Count, 1) * sizeof(Il2CppRuntimeInterfaceOffsetPair));
        for (var i = 0; i < dispatch.Count; i++)
            klass.InterfaceOffsets[i] = new Il2CppRuntimeInterfaceOffsetPair { interfaceType = (Il2CppClass*)dispatch[i].Interface, offset = dispatch[i].Offset };
    }

    // Unity 6.6 and later: one array of every interface of the class and its base classes, since IL2CPP no longer checks
    // the base classes, with the interfaces that have a vtable block first. As libil2cpp's SetupInterfacesLocked does, an
    // interface's depth is 0 if the class declares it and one more than its depth in the interface or class it comes from.
    private static void WriteCombinedInterfaces(INativeClassStruct klass, INativeClassStruct baseClass, Type[] newInterfaces, List<(nint Interface, int Offset)> dispatch)
    {
        var entries = dispatch.Select(d => new Il2CppRuntimeInterfaceData { interfaceType = (Il2CppClass*)d.Interface, offset = d.Offset, depth = int.MaxValue }).ToList();
        void Append(nint pointer, int depth)
        {
            var existing = entries.FindIndex(e => (nint)e.interfaceType == pointer);
            if (existing < 0)
                entries.Add(new Il2CppRuntimeInterfaceData { interfaceType = (Il2CppClass*)pointer, offset = NotDispatchable, depth = depth });
            else if (depth < entries[existing].depth)
                entries[existing] = entries[existing] with { depth = depth };
        }

        // Managed metadata does not say which interfaces a class names itself, so those that no other new interface brings are.
        foreach (var @interface in newInterfaces.Where(i => !newInterfaces.Any(other => other != i && i.IsAssignableFrom(other))))
        {
            var itf = Class(@interface);
            Append((nint)itf.ClassPointer, 0);
            for (var i = 0; itf.Interfaces != null && i < itf.InterfaceCount; i++)
                Append((nint)itf.Interfaces[i].interfaceType, 1 + itf.Interfaces[i].depth);
        }
        for (var i = 0; baseClass.Interfaces != null && i < baseClass.InterfaceCount; i++)
            Append((nint)baseClass.Interfaces[i].interfaceType, 1 + baseClass.Interfaces[i].depth);
        foreach (var @interface in newInterfaces)
            Append(s_classPointer(@interface), 1);

        klass.InterfaceOffsetsCount = (ushort)dispatch.Count;
        klass.InterfaceCount = (ushort)entries.Count;
        klass.Interfaces = (Il2CppRuntimeInterfaceData*)Marshal.AllocHGlobal(Math.Max(entries.Count, 1) * sizeof(Il2CppRuntimeInterfaceData));
        for (var i = 0; i < entries.Count; i++)
            klass.Interfaces[i] = entries[i];
    }

    // The interfaces that have a vtable block in the class, with their offsets.
    private static List<(nint Interface, int Offset)> DispatchEntries(INativeClassStruct klass)
    {
        var entries = new List<(nint, int)>();
        var present = klass.HasCombinedInterfaceData ? klass.Interfaces != null : klass.InterfaceOffsets != null;
        for (var i = 0; present && i < klass.InterfaceOffsetsCount; i++)
        {
            entries.Add(klass.HasCombinedInterfaceData
                ? ((nint)klass.Interfaces[i].interfaceType, klass.Interfaces[i].offset)
                : ((nint)klass.InterfaceOffsets[i].interfaceType, klass.InterfaceOffsets[i].offset));
        }
        return entries;
    }

    // An interface's methods have slots 0 to n-1 within its block; static methods have none.
    private static int SlotCount(INativeClassStruct itf)
    {
        var count = 0;
        for (var i = 0; i < itf.MethodCount; i++)
        {
            var slot = UnityVersionHandler.Wrap(itf.Methods[i]).Slot;
            if (slot != NoSlot)
                count = Math.Max(count, slot + 1);
        }
        return count;
    }

    private static INativeClassStruct Class(Type type) => UnityVersionHandler.Wrap((Il2CppClass*)s_classPointer(type));

    private static object? Lookup(IDictionary map, object? key) => key is not null && map.Contains(key) ? map[key] : null;

    // Il2CppInterop allocates a class's vtable by counting [Il2CppMethod] methods; an interop interface's methods may not be
    // among them, so the count is raised to what the layout needs: the base class's slots, one for each method of the class
    // and for each method of its IL2CPP interfaces. Counting must not register an injected base class or interface, whose
    // registration may need this class.
    private static void EnoughVTableSlots(Type __0, ref int __result)
    {
        __result = Math.Min(Math.Max(__result, RequiredSlots(__0)), ushort.MaxValue);
    }

    private static int RequiredSlots(Type type)
    {
        var baseType = type.IsValueType ? s_valueType : type.BaseType;
        var slots = baseType is null ? 0
            : IsInjected(baseType) ? RequiredSlots(baseType)
            : UnityVersionHandler.Wrap((Il2CppClass*)Il2CppType.GetClassPointer(baseType)).VTableCount;
        slots += s_il2CppMethods(type).Count();
        foreach (var @interface in type.GetInterfaces().Where(s_isIl2CppInterface))
        {
            slots += IsInjected(@interface)
                ? s_il2CppMethods(@interface).Count()
                : UnityVersionHandler.Wrap((Il2CppClass*)Il2CppType.GetClassPointer(@interface)).MethodCount;
        }
        return slots;
    }

    private static bool IsInjected(Type type) => type.IsDefined(typeof(InjectedTypeAttribute), inherit: false);

    // Writes one class's vtable.
    private sealed class Layout(Type type, INativeClassStruct klass, int allocated)
    {
        private readonly IDictionary _methodToHash = s_methodToHash(type);
        private readonly IDictionary _hashToNative = s_hashToNative(klass);

        public void Set(int slot, VirtualInvokeData entry)
        {
            if (slot >= allocated)
                throw new InvalidOperationException($"{type.FullName} needs more than the {allocated} virtual method slots allocated for it");
            klass.VTable[slot] = entry;
        }

        public void Set(int slot, INativeMethodInfoStruct method) =>
            Set(slot, new VirtualInvokeData { methodPtr = method.MethodPointer, method = method.MethodInfoPointer });

        // The IL2CPP method of a managed method of the class or its base classes.
        public INativeMethodInfoStruct Native(MethodInfo method) =>
            Lookup(_hashToNative, Lookup(_methodToHash, method)) as INativeMethodInfoStruct
            ?? throw new InvalidOperationException(
                $"{type.FullName}: {method.DeclaringType?.Name}.{method.Name} must be marked [Il2CppMethod] for IL2CPP to call it");

        // Fills an interface's block with the methods that implement it. For an interface of the base class, only the
        // methods that this class declares replace the base class's.
        public void Implement(Type @interface, INativeClassStruct itf, int offset, bool ownMethodsOnly)
        {
            var map = type.GetInterfaceMap(@interface);
            var hashToMethod = s_hashToMethod(@interface);
            var nativeToHash = s_nativeToHash(itf);
            for (var i = 0; i < itf.MethodCount; i++)
            {
                var method = UnityVersionHandler.Wrap(itf.Methods[i]);
                if (method.Slot == NoSlot)
                    continue;
                var managed = Lookup(hashToMethod, Lookup(nativeToHash, (nint)method.MethodInfoPointer)) as MethodInfo
                    ?? ByName(@interface, method);
                var index = managed is null ? -1 : Array.IndexOf(map.InterfaceMethods, managed);
                if (index < 0)
                {
                    if (ownMethodsOnly)
                        continue;
                    throw new InvalidOperationException(
                        $"{type.FullName}: no implementation found for {@interface.FullName}.{Marshal.PtrToStringUTF8(method.Name)}");
                }
                var target = map.TargetMethods[index];
                if (ownMethodsOnly && target.DeclaringType != type)
                    continue;
                Set(offset + method.Slot, Native(target));
            }
        }

        // The interface's managed method with the IL2CPP method's name and parameter count, if there is exactly one.
        private static MethodInfo? ByName(Type @interface, INativeMethodInfoStruct method)
        {
            var name = Marshal.PtrToStringUTF8(method.Name);
            var candidates = @interface.GetMethods().Where(m => m.Name == name && m.GetParameters().Length == method.ParametersCount).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
    }
}
