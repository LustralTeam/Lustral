using System.Reflection;
using System.Reflection.Emit;
using Il2CppInterop.Common;
using Il2CppInterop.Common.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Startup;

namespace Lustral.Backend.Il2Cpp;

/// <summary>
/// Lustral's hidden components on IL2CPP. Unity sends OnGUI (and, where Lustral cannot use the player loop, Update,
/// FixedUpdate and LateUpdate) only to components, and an IL2CPP component must be an IL2CPP class. Lustral cannot compile against the
/// game's interop assemblies, because they are generated on the user's computer, so it emits a MonoBehaviour subclass
/// against them at runtime and registers it through Il2CppInterop's class injection.
/// </summary>
/// <remarks>
/// Modeled on Il2CppInterop V2's own injected class (<c>Il2CppToMonoDelegateReference</c>): an <c>ObjectPointer</c>
/// constructor; <c>IIl2CppType&lt;T&gt;.UnboxNative</c>, which calls it (the object pool's initializer for the class,
/// through which a component created by Unity obtains its managed object); <c>ObjectClass</c>, which returns the injected
/// class; and <c>[Il2CppMethod]</c> on the methods IL2CPP may call. The public
/// <c>TypeInjector.RegisterTypeInIl2Cpp&lt;T&gt;</c> requires <c>T : IIl2CppType&lt;T&gt;</c>, which an emitted type cannot
/// satisfy at compile time; it only forwards to the internal <c>RegisterTypeInIl2Cpp(Type)</c>, which is called here
/// instead. Unity's calls into the component go through Il2CppInterop's invoker and trampoline, as for mods' components
/// (with <see cref="Il2CppInteropFixes"/>).
/// </remarks>
internal static class Il2CppComponents
{
    private static ModuleBuilder? s_module;

    /// <summary>
    /// Creates a hidden GameObject that persists across scene loads, with a component whose Unity messages (by name) call
    /// the callbacks. Callbacks must be public static methods of a public type, because emitted code calls them.
    /// </summary>
    public static void Create(string name, IReadOnlyList<(string Message, MethodInfo Callback)> messages, Func<string, Type?> findUnityType)
    {
        Type Find(string typeName) => findUnityType(typeName) ?? throw new TypeLoadException($"{typeName} was not found");
        var type = Emit(name, Find("UnityEngine.MonoBehaviour"), messages);
        var register = typeof(TypeInjector).GetMethod("RegisterTypeInIl2Cpp", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static, [typeof(Type)])
            ?? throw new MissingMethodException(nameof(TypeInjector), "RegisterTypeInIl2Cpp(Type)");
        register.Invoke(null, [type]);

        var gameObject = Find("UnityEngine.GameObject");
        var nameConstructor = gameObject.GetConstructors().Single(c => c.GetParameters() is [{ ParameterType.FullName: "Il2CppSystem.String" }]);
        var il2CppString = nameConstructor.GetParameters()[0].ParameterType;
        var host = nameConstructor.Invoke([il2CppString.GetMethod("op_Implicit", [typeof(string)])!.Invoke(null, [name])]);

        var unityObject = Find("UnityEngine.Object");
        var hideFlags = unityObject.GetProperty("hideFlags")!;
        hideFlags.SetValue(host, HideAndDontSave(hideFlags.PropertyType));
        unityObject.GetMethod("DontDestroyOnLoad", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [host]);

        // AddComponent(Il2CppSystem.Type): the generic AddComponent<T> reads a per-type store that runtime registration may
        // not populate.
        var addComponent = gameObject.GetMethods().Single(m => m.Name == "AddComponent" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
        addComponent.Invoke(host, [Il2CppSystemType(Il2CppType.GetClassPointer(type))]);
    }

    // HideFlags.HideAndDontSave (61). Il2CppInterop V2 wraps IL2CPP enums in structs, whose values are static members or
    // conversions from the underlying integer.
    private static object HideAndDontSave(Type flags)
    {
        const BindingFlags statics = BindingFlags.Public | BindingFlags.Static;
        if (flags.IsEnum)
            return Enum.Parse(flags, "HideAndDontSave");
        if (flags.GetField("HideAndDontSave", statics) is { } field)
            return field.GetValue(null)!;
        if (flags.GetProperty("HideAndDontSave", statics) is { } property)
            return property.GetValue(null)!;
        var conversion = flags.GetMethods(statics).FirstOrDefault(m =>
            m.Name is "op_Explicit" or "op_Implicit" && m.ReturnType == flags && m.GetParameters() is [{ ParameterType: var from }] && from == typeof(int))
            ?? throw new MissingMemberException(flags.FullName, "HideAndDontSave");
        return conversion.Invoke(null, [61])!;
    }

    // An Il2CppSystem.Type for an IL2CPP class.
    private static object Il2CppSystemType(IntPtr classPointer)
    {
        var fromClassPointer = typeof(Il2CppInteropRuntime).Assembly
            .GetType("Il2CppInterop.Runtime.Extensions.Il2CppSystemTypeExtensions")?
            .GetMethod("FromClassPointer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("Il2CppSystemTypeExtensions", "FromClassPointer");
        return fromClassPointer.Invoke(null, [classPointer])!;
    }

    // public sealed class Lustral.<name> : UnityEngine.MonoBehaviour, IIl2CppType, IIl2CppType<<name>>
    // {
    //     public <name>(ObjectPointer pointer) : base(pointer) { }
    //     static <name> IIl2CppType<<name>>.UnboxNative(ObjectPointer pointer) => new(pointer);
    //     IntPtr IIl2CppType.ObjectClass => Il2CppType.GetClassPointer(typeof(<name>));
    //     [Il2CppMethod(Name = "<message>")] public void <message>() => <callback>();
    // }
    private static Type Emit(string name, Type behaviour, IReadOnlyList<(string Message, MethodInfo Callback)> messages)
    {
        s_module ??= AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Lustral.Components"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("Lustral.Components");
        var builder = s_module.DefineType("Lustral." + name, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class, behaviour);

        var baseConstructor = behaviour.GetConstructors().Single(c => c.GetParameters() is [{ ParameterType.Name: nameof(ObjectPointer) }]);
        var pointerType = baseConstructor.GetParameters()[0].ParameterType;
        var constructor = builder.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig, CallingConventions.HasThis, [pointerType]);
        var il = constructor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, baseConstructor);
        il.Emit(OpCodes.Ret);

        var selfInterface = typeof(IIl2CppType<>).MakeGenericType(builder);
        builder.AddInterfaceImplementation(typeof(IIl2CppType));
        builder.AddInterfaceImplementation(selfInterface);

        var unboxNative = builder.DefineMethod("UnboxNative", MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, builder, [pointerType]);
        il = unboxNative.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, constructor);
        il.Emit(OpCodes.Ret);
        builder.DefineMethodOverride(unboxNative, TypeBuilder.GetMethod(selfInterface, typeof(IIl2CppType<>).GetMethod("UnboxNative")!));

        var objectClass = builder.DefineMethod("get_ObjectClass",
            MethodAttributes.Private | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            typeof(IntPtr), Type.EmptyTypes);
        il = objectClass.GetILGenerator();
        il.Emit(OpCodes.Ldtoken, builder);
        il.Emit(OpCodes.Call, typeof(Type).GetMethod(nameof(Type.GetTypeFromHandle))!);
        il.Emit(OpCodes.Call, typeof(Il2CppType).GetMethod(nameof(Il2CppType.GetClassPointer), [typeof(Type)])!);
        il.Emit(OpCodes.Ret);
        builder.DefineMethodOverride(objectClass, typeof(IIl2CppType).GetProperty(nameof(IIl2CppType.ObjectClass))!.GetMethod!);

        // Only methods marked [Il2CppMethod] are added to the IL2CPP class, where Unity looks up its messages.
        var il2CppMethod = typeof(Il2CppMethodAttribute);
        var nameProperty = il2CppMethod.GetProperty(nameof(Il2CppMethodAttribute.Name))!;
        foreach (var (message, callback) in messages)
        {
            var method = builder.DefineMethod(message, MethodAttributes.Public | MethodAttributes.HideBySig, typeof(void), Type.EmptyTypes);
            method.SetCustomAttribute(new CustomAttributeBuilder(il2CppMethod.GetConstructor(Type.EmptyTypes)!, [], [nameProperty], [message]));
            var body = method.GetILGenerator();
            body.Emit(OpCodes.Call, callback);
            body.Emit(OpCodes.Ret);
        }
        return builder.CreateType();
    }
}

/// <summary>The callbacks invoked by Lustral's emitted IL2CPP components; public because they are called from another assembly.</summary>
public static class Il2CppComponentCallbacks
{
    public static void Update() => Il2CppUnityHooks.ComponentUpdate();

    public static void FixedUpdate() => Il2CppUnityHooks.ComponentFixedUpdate();

    public static void LateUpdate() => Il2CppUnityHooks.ComponentLateUpdate();

    public static void OnGUI() => Il2CppUnityHooks.ComponentGui();
}
