using System.Reflection;
using Lustral.Core.Lifecycle;
using Lustral.Unity;

namespace Lustral.Backend.Mono;

/// <summary>
/// On Mono, Unity's public API is used directly: scene and quit events are standard .NET events, and the player loop
/// accepts ordinary delegates and arrays. Where the player loop cannot be extended (before Unity 2019.3), a Lustral
/// component receives the per-frame calls, as a MonoBehaviour of the game would.
/// </summary>
internal sealed class MonoUnityHooks(GameLifecycle lifecycle, ILog log) : UnityLifecycleHooks(lifecycle, log)
{
    protected override void InstallHooks()
    {
        TryInstall("scene events", () =>
        {
            Subscribe("UnityEngine.SceneManagement.SceneManager", "sceneLoaded", nameof(OnSceneLoaded));
            Subscribe("UnityEngine.SceneManagement.SceneManager", "sceneUnloaded", nameof(OnSceneUnloaded));
        });
    }

    // Everything else waits for the engine. Accessing UnityEngine.Application during startup runs its static constructor
    // before the engine is initialized; on Unity 6000.5, this throws, and the failed type initializer breaks Application
    // for the game as well, for the entire session. The player loop is also available only from this point on.
    protected override void OnFirstScene()
    {
        TryInstall("the quit event", () => Subscribe("UnityEngine.Application", "quitting", nameof(OnQuitting)));
        var playerLoop = TryInstall("per-frame Update, FixedUpdate and LateUpdate (player loop)", () => InstallPlayerLoop(
            marker: name => name switch
            {
                "FixedUpdate" => typeof(LustralFixedUpdate),
                "LateUpdate" => typeof(LustralLateUpdate),
                _ => typeof(LustralUpdate),
            },
            createDelegate: (type, callback) => Delegate.CreateDelegate(type, callback.Method),
            toArray: list => (Array)list,
            fromArray: (systems, _) => systems), required: false);
        if (!playerLoop && TryInstall("per-frame Update, FixedUpdate and LateUpdate (a component)",
                () => LustralFrameBehaviour.Create(() => Frame(1, "a Lustral component"), FixedFrame, LateFrame)))
            Log.Info("Unity's player loop is not available to Lustral in this game, so per-frame events come from a Lustral component");
    }

    protected override void InstallGui()
    {
        if (FindUnityType("UnityEngine.GUI", "UnityEngine.IMGUIModule") is null)
            throw new NotSupportedException("this game's build does not include Unity's IMGUI module");
        LustralGuiBehaviour.Create(Gui);
    }

    private static void Subscribe(string typeName, string eventName, string handlerName)
    {
        var type = FindUnityType(typeName) ?? throw new TypeLoadException($"{typeName} was not found");
        var unityEvent = type.GetEvent(eventName, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMemberException(typeName, eventName);
        var delegateType = unityEvent.EventHandlerType!;
        var parameters = delegateType.GetMethod("Invoke")!.GetParameters().Select(p => p.ParameterType).ToArray();
        var handler = typeof(MonoUnityHooks).GetMethod(handlerName, BindingFlags.NonPublic | BindingFlags.Static)!;
        if (handler.IsGenericMethodDefinition)
            handler = handler.MakeGenericMethod(parameters);
        unityEvent.AddEventHandler(null, Delegate.CreateDelegate(delegateType, handler));
    }

    private static void OnSceneLoaded<TScene, TMode>(TScene scene, TMode mode) => SceneLoaded(scene);

    private static void OnSceneUnloaded<TScene>(TScene scene) => SceneUnloaded(scene);

    private static void OnQuitting() => Quitting();
}

/// <summary>Names Lustral's entries in Unity's player loop and therefore their profiler markers.</summary>
internal sealed class LustralUpdate;

/// <inheritdoc cref="LustralUpdate"/>
internal sealed class LustralFixedUpdate;

/// <inheritdoc cref="LustralUpdate"/>
internal sealed class LustralLateUpdate;
