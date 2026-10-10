using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Startup;
using Lustral.Core.Lifecycle;
using Lustral.Unity;

namespace Lustral.Backend.Il2Cpp;

/// <summary>
/// On IL2CPP, Lustral patches the internal methods that Unity's engine calls for scene loads and quitting
/// (<c>SceneManager.Internal_SceneLoaded/Unloaded</c>, <c>Application.Internal_ApplicationQuit</c>) through the same
/// Harmony mechanism that mods use, so that loading mods does not depend on class injection. Per-frame work is added to
/// Unity's player loop, which requires an IL2CPP delegate and therefore class injection.
/// </summary>
/// <remarks>
/// IL2CPP builds exclude managed code that the game does not use, so the player loop API or <c>Scene.name</c> may be
/// missing. Engine callbacks marked RequiredByNativeCode are always included, so per-frame work then follows the
/// reflection probe update or <c>Canvas.SendWillRenderCanvases</c>, and scene names are read through the engine's native
/// functions.
/// </remarks>
internal sealed class Il2CppUnityHooks(GameLifecycle lifecycle, ILog log) : UnityLifecycleHooks(lifecycle, log)
{
    private readonly Harmony _harmony = new("lustral.lifecycle");

    protected override void InstallHooks()
    {
        TryInstall("scene events", () =>
        {
            Postfix("UnityEngine.SceneManagement.SceneManager", "Internal_SceneLoaded", nameof(SceneLoadedPostfix));
            Postfix("UnityEngine.SceneManagement.SceneManager", "Internal_SceneUnloaded", nameof(SceneUnloadedPostfix));
        });
        TryInstall("the quit event", () => Postfix("UnityEngine.Application", "Internal_ApplicationQuit", nameof(QuitPostfix)));
    }

    protected override void OnFirstScene()
    {
        if (TryInstall("per-frame Update, FixedUpdate and LateUpdate (player loop)", InstallIl2CppPlayerLoop, required: false))
            return;

        // Both are used if available: frames are driven by whichever fires, preferring the reflection probe update, which
        // does not depend on the game drawing UI. Neither fires without rendering (-batchmode).
        var probes = TryInstall("per-frame Update (reflection probe tick)", () => Postfix(
            "UnityEngine.Experimental.Rendering.ScriptableRuntimeReflectionSystemWrapper",
            "Internal_ScriptableRuntimeReflectionSystemWrapper_TickRealtimeProbes", nameof(ProbeTickPostfix)), required: false);
        var canvas = TryInstall("per-frame Update (Canvas.SendWillRenderCanvases)",
            () => Postfix("UnityEngine.Canvas", "SendWillRenderCanvases", nameof(FramePostfix), "UnityEngine.UIModule"), required: false);
        // FixedUpdate and LateUpdate are received through a Lustral component, as a MonoBehaviour receives them. Its Update
        // drives frames only where nothing is rendered (-batchmode), because it depends on class injection.
        var component = TryInstall("FixedUpdate, LateUpdate and Update (a component)", () => Il2CppComponents.Create("LustralFrames",
            [
                ("Update", typeof(Il2CppComponentCallbacks).GetMethod(nameof(Il2CppComponentCallbacks.Update))!),
                ("FixedUpdate", typeof(Il2CppComponentCallbacks).GetMethod(nameof(Il2CppComponentCallbacks.FixedUpdate))!),
                ("LateUpdate", typeof(Il2CppComponentCallbacks).GetMethod(nameof(Il2CppComponentCallbacks.LateUpdate))!),
            ], name => FindUnityType(name)));
        if (probes || canvas || component)
            Log.Info("Unity's player loop is not available to Lustral in this game, so per-frame Update follows rendering");
        else
            Log.Warn("Could not hook per-frame Update in this game; mods' Update handlers will not run");
    }

    protected override void InstallGui()
    {
        if (FindUnityType("UnityEngine.GUI", "UnityEngine.IMGUIModule") is null)
            throw new NotSupportedException("this game's build does not include Unity's IMGUI module");
        Il2CppComponents.Create("LustralGui",
            [("OnGUI", typeof(Il2CppComponentCallbacks).GetMethod(nameof(Il2CppComponentCallbacks.OnGUI))!)],
            name => FindUnityType(name));
    }

    internal static void ComponentUpdate() => Frame(3, "a Lustral component");

    internal static void ComponentFixedUpdate() => FixedFrame();

    internal static void ComponentLateUpdate() => LateFrame();

    internal static void ComponentGui() => Gui();

    // Unity's native functions are used only if the build stripped the managed property. A scene may legitimately have no
    // name or build index (the empty scene unloaded during startup), and native calls are limited to where they are
    // required.
    protected override SceneInfo DescribeScene(object scene)
    {
        var (name, buildIndex) = ReadScene(scene);
        if (name is null || buildIndex is null)
        {
            try
            {
                if (NativeSceneInfo.TryDescribe(scene, out var nativeName, out var nativeIndex))
                {
                    name ??= nativeName;
                    buildIndex ??= nativeIndex;
                }
            }
            catch (Exception e)
            {
                Log.Debug($"Could not read the scene from native code: {e.Message}");
            }
        }
        return new SceneInfo(name ?? "", buildIndex ?? -1);
    }

    private static void InstallIl2CppPlayerLoop()
    {
        var convertDelegate = typeof(DelegateSupport).GetMethod(nameof(DelegateSupport.ConvertDelegate), BindingFlags.Public | BindingFlags.Static)!;
        var marker = Il2CppTypeOf("UnityEngine.MonoBehaviour");
        InstallPlayerLoop(
            marker: _ => marker,
            createDelegate: (type, callback) => convertDelegate.MakeGenericMethod(type).Invoke(null, [callback])
                ?? throw new InvalidOperationException($"Il2CppInterop returned no {type.FullName}"),
            toArray: list => (Array)ExplicitConversion(list.GetType(), list.GetType(), to: null).Invoke(null, [list])!,
            fromArray: (systems, listType) => ExplicitConversion(listType, systems.GetType(), to: listType).Invoke(null, [systems])!);
    }

    // The Il2CppSystem.Type of a UnityEngine type, for the player loop system's profiler name.
    private static object Il2CppTypeOf(string unityType)
    {
        var type = FindUnityType(unityType) ?? throw new TypeLoadException($"{unityType} was not found");
        var classPointer = Il2CppInterop.Common.Il2CppType.GetClassPointer(type);
        var fromClassPointer = typeof(Il2CppInteropRuntime).Assembly
            .GetType("Il2CppInterop.Runtime.Extensions.Il2CppSystemTypeExtensions")?
            .GetMethod("FromClassPointer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("Il2CppSystemTypeExtensions", "FromClassPointer");
        return fromClassPointer.Invoke(null, [classPointer])!;
    }

    /// <summary>Returns the <c>op_Explicit</c> on <paramref name="owner"/> from <paramref name="from"/> to an array (if <paramref name="to"/> is null) or to <paramref name="to"/>.</summary>
    private static MethodInfo ExplicitConversion(Type owner, Type from, Type? to) =>
        owner.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m =>
            m.Name == "op_Explicit" &&
            m.GetParameters() is [var parameter] && parameter.ParameterType == from &&
            (to is null ? m.ReturnType.IsArray : m.ReturnType == to))
        ?? throw new MissingMethodException(owner.FullName, $"op_Explicit({from.Name})");

    private void Postfix(string typeName, string methodName, string postfixName, string assembly = "UnityEngine.CoreModule")
    {
        var type = FindUnityType(typeName, assembly) ?? throw new TypeLoadException($"{typeName} was not found in {assembly}");
        // Il2CppInterop adds convenience overloads with .NET ref parameters alongside the actual (ByReference<T>) method.
        var target = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.Name == methodName)
            .OrderBy(m => m.GetParameters().Count(p => p.ParameterType.IsByRef))
            .FirstOrDefault()
            ?? throw new MissingMethodException(typeName, methodName);
        var postfix = typeof(Il2CppUnityHooks).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static)!;
        _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
    }

    // Harmony postfixes. __args avoids compile-time references to Unity types.
    private static void SceneLoadedPostfix(object[] __args) => SceneLoaded(__args[0]);

    private static void SceneUnloadedPostfix(object[] __args) => SceneUnloaded(__args[0]);

    private static void QuitPostfix() => Quitting();

    private static void ProbeTickPostfix() => Frame(1, "the reflection probe tick");

    private static void FramePostfix() => Frame(2, "Canvas.SendWillRenderCanvases");
}
