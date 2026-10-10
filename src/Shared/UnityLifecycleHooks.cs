using System.Reflection;
using Lustral.Core.Lifecycle;

namespace Lustral.Unity;

/// <summary>
/// Connects Unity's scene, quit and per-frame callbacks to <see cref="GameLifecycle"/>. Compiled into both backends; each
/// backend determines how to hook Unity (Mono: Unity's public events; IL2CPP: Harmony patches on Unity's internal
/// callbacks, which require no IL2CPP delegates). On both, per-frame work is added to Unity's player loop. All members are
/// located by reflection, so neither backend requires Unity at compile time. Each hook is installed independently; a
/// failed hook is logged and the others still work.
/// </summary>
internal abstract class UnityLifecycleHooks
{
    /// <summary>
    /// Lustral's systems in Unity's player loop: the phase (its type's name in the PlayerLoop namespace), the system for the
    /// game's MonoBehaviour calls after which Lustral's system is inserted (null for the end of the phase), and the callback. FixedUpdate follows the game's
    /// FixedUpdate calls, so that mods run before the physics step as a MonoBehaviour would; Update follows the game's
    /// Update calls and coroutines.
    /// </summary>
    private static readonly (string Name, string Phase, string? After, Action Callback)[] LoopSystems =
    [
        ("FixedUpdate", "FixedUpdate", "ScriptRunBehaviourFixedUpdate", FixedFrame),
        ("Update", "Update", null, Frame),
        ("LateUpdate", "PreLateUpdate", "ScriptRunBehaviourLateUpdate", LateFrame),
    ];

    // Unity calls static methods; there is one game and one set of hooks per process.
    private static UnityLifecycleHooks? s_current;

    // Unity holds the delegates; this also keeps the wrappers (and, on IL2CPP, their managed targets) alive.
    private static readonly List<object> s_loopDelegates = [];

    private readonly GameLifecycle _lifecycle;
    private bool _sawFirstScene;
    private bool _guiRequested;

    protected UnityLifecycleHooks(GameLifecycle lifecycle, ILog log)
    {
        _lifecycle = lifecycle;
        Log = log;
    }

    protected ILog Log { get; }

    public void Install()
    {
        s_current = this;
        InstallHooks();
    }

    /// <summary>Installs the hooks that can be installed before the engine starts, which call the static callbacks below.</summary>
    protected abstract void InstallHooks();

    // Unity's scaled game time for coroutines (Wait.Seconds): Time.timeAsDouble from Unity 2020.2, otherwise Time.time. On
    // IL2CPP, the getter returns an interop primitive, which converts implicitly. Builds without it use real time.
    private void InstallGameClock()
    {
        try
        {
            const BindingFlags statics = BindingFlags.Public | BindingFlags.Static;
            var time = FindUnityType("UnityEngine.Time");
            var getter = (time?.GetProperty("timeAsDouble", statics) ?? time?.GetProperty("time", statics))?.GetGetMethod();
            if (getter is null)
            {
                Log.Debug("This build has no Time.time; Wait.Seconds in coroutines uses real time");
                return;
            }
            Func<double> clock;
            if (getter.ReturnType == typeof(double))
                clock = (Func<double>)Delegate.CreateDelegate(typeof(Func<double>), getter);
            else if (getter.ReturnType == typeof(float))
            {
                var seconds = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), getter);
                clock = () => seconds();
            }
            else
            {
                var convert = getter.ReturnType.GetMethods(statics).FirstOrDefault(m =>
                    m.Name == "op_Implicit" && (m.ReturnType == typeof(double) || m.ReturnType == typeof(float)) && m.GetParameters().Length == 1)
                    ?? throw new InvalidCastException($"{getter.ReturnType} cannot be converted to a number");
                clock = () => Convert.ToDouble(convert.Invoke(null, [getter.Invoke(null, null)]), System.Globalization.CultureInfo.InvariantCulture);
            }
            clock();
            _lifecycle.UseGameClock(clock);
        }
        catch (Exception e)
        {
            Log.Debug($"Could not read Unity's game time ({e.Message}); Wait.Seconds in coroutines uses real time");
        }
    }

    /// <summary>Runs once, before the first scene's events. The engine and its player loop are available from this point on.</summary>
    protected virtual void OnFirstScene()
    {
    }

    /// <summary>
    /// Starts calling <see cref="Gui"/> from Unity's IMGUI on the main thread, from the first frame in which a mod has
    /// OnGUI handlers.
    /// </summary>
    protected abstract void InstallGui();

    /// <summary>Returns a UnityEngine type by full name: Unity's type on Mono, or the interop type on IL2CPP.</summary>
    protected static Type? FindUnityType(string fullName, string assembly = "UnityEngine.CoreModule") =>
        Type.GetType($"{fullName}, {assembly}");

    /// <param name="required">False if a fallback exists, in which case a failure is logged only at debug level.</param>
    /// <returns>Whether the hook was installed.</returns>
    protected bool TryInstall(string what, Action install, bool required = true)
    {
        try
        {
            install();
            Log.Debug($"Hooked {what}");
            return true;
        }
        catch (Exception e)
        {
            var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            if (required)
                Log.Warn($"Could not hook {what}; it will not be available to mods in this game: {cause.Message}");
            else
                Log.Debug($"Could not hook {what}: {cause.Message}");
            return false;
        }
    }

    /// <summary>
    /// Adds Lustral's FixedUpdate, Update and LateUpdate systems to Unity's player loop (<see cref="LoopSystems"/>). The
    /// delegate parameters make this work on both runtimes: Unity's delegate and array types are ordinary .NET types on
    /// Mono and interop wrappers on IL2CPP. If the loop lacks a phase, only the corresponding system is omitted.
    /// </summary>
    /// <param name="marker">Returns the value of a system's <c>type</c> field by system name, which names it in Unity's profiler.</param>
    /// <param name="createDelegate">Creates a Unity <c>UpdateFunction</c> of the given type that calls the given action.</param>
    /// <param name="toArray">Converts Unity's <c>subSystemList</c> to a plain array.</param>
    /// <param name="fromArray">Converts a plain array to Unity's <c>subSystemList</c> type.</param>
    /// <returns>The names of the installed systems.</returns>
    protected static List<string> InstallPlayerLoop(Func<string, object?> marker, Func<Type, Action, object> createDelegate, Func<object, Array> toArray, Func<Array, Type, object> fromArray)
    {
        // Before Unity 2019.3, the API was in UnityEngine.Experimental.LowLevel and the phases in
        // UnityEngine.Experimental.PlayerLoop. Unity 2018.1 introduced it.
        var experimental = FindUnityType("UnityEngine.LowLevel.PlayerLoop") is null;
        var lowLevel = experimental ? "UnityEngine.Experimental.LowLevel." : "UnityEngine.LowLevel.";
        var phasePrefix = experimental ? "UnityEngine.Experimental.PlayerLoop." : "UnityEngine.PlayerLoop.";
        var playerLoop = FindUnityType(lowLevel + "PlayerLoop") ?? throw new TypeLoadException("UnityEngine.LowLevel.PlayerLoop was not found");
        var systemType = FindUnityType(lowLevel + "PlayerLoopSystem") ?? throw new TypeLoadException($"{lowLevel}PlayerLoopSystem was not found");
        FieldInfo Field(string name) => systemType.GetField(name) ?? throw new MissingFieldException(systemType.FullName, name);
        var typeField = Field("type");
        var listField = Field("subSystemList");
        var delegateField = Field("updateDelegate");

        string? TypeName(object system) => typeField.GetValue(system)?.ToString();

        // Before Unity 2019.3, only the default loop can be read. Setting it would remove the systems that the game added
        // (as Unity's Entities package does), so the backends' fallbacks are used instead.
        var getCurrent = playerLoop.GetMethod("GetCurrentPlayerLoop", Type.EmptyTypes)
            ?? throw new NotSupportedException("this Unity version cannot return the game's current player loop (Unity 2019.3 and later can)");
        var root = getCurrent.Invoke(null, null)!; // A boxed copy.
        var phases = toArray(listField.GetValue(root)!);
        var installed = new List<string>();
        foreach (var (name, phaseName, after, callback) in LoopSystems)
        {
            var index = IndexOf(phases, system => TypeName(system) == phasePrefix + phaseName);
            if (index < 0)
                continue;
            var phase = phases.GetValue(index)!;

            var lustral = Activator.CreateInstance(systemType)!;
            typeField.SetValue(lustral, marker(name));
            var function = createDelegate(delegateField.FieldType, callback);
            s_loopDelegates.Add(function);
            delegateField.SetValue(lustral, function);

            // Nested system types have names such as "UnityEngine.PlayerLoop.FixedUpdate+ScriptRunBehaviourFixedUpdate".
            var systems = toArray(listField.GetValue(phase)!);
            var follow = after is null ? -1 : IndexOf(systems, system => TypeName(system)?.EndsWith("+" + after, StringComparison.Ordinal) == true);
            var at = follow < 0 ? systems.Length : follow + 1;
            var extended = Array.CreateInstance(systemType, systems.Length + 1);
            Array.Copy(systems, 0, extended, 0, at);
            extended.SetValue(lustral, at);
            Array.Copy(systems, at, extended, at + 1, systems.Length - at);

            // The structs are boxed copies, so each level is written back.
            listField.SetValue(phase, fromArray(extended, listField.FieldType));
            phases.SetValue(phase, index);
            installed.Add(name);
        }
        if (!installed.Contains("Update"))
            throw new InvalidOperationException($"Unity's player loop has no {phasePrefix}Update phase");

        listField.SetValue(root, fromArray(phases, listField.FieldType));
        playerLoop.GetMethod("SetPlayerLoop", [systemType])!.Invoke(null, [root]);
        return installed;
    }

    private static int IndexOf(Array items, Func<object, bool> match)
    {
        for (var i = 0; i < items.Length; i++)
        {
            if (match(items.GetValue(i)!))
                return i;
        }
        return -1;
    }

    // The callbacks run within Unity's calls and must never throw exceptions into Unity.

    protected static void SceneLoaded(object? scene)
    {
        var hooks = s_current;
        if (hooks is null || scene is null)
            return;
        try
        {
            if (!hooks._sawFirstScene)
            {
                hooks._sawFirstScene = true;
                hooks.InstallGameClock();
                hooks.OnFirstScene();
            }
            hooks._lifecycle.SceneLoaded(hooks.DescribeScene(scene));
        }
        catch (Exception e)
        {
            hooks.Log.Error("Scene load handling failed", e);
        }
    }

    protected static void SceneUnloaded(object? scene)
    {
        var hooks = s_current;
        if (hooks is null || scene is null)
            return;
        try
        {
            hooks._lifecycle.SceneUnloaded(hooks.DescribeScene(scene));
        }
        catch (Exception e)
        {
            hooks.Log.Error("Scene unload handling failed", e);
        }
    }

    protected static void Quitting()
    {
        try
        {
            s_current?._lifecycle.Shutdown();
        }
        catch (Exception e)
        {
            s_current?.Log.Error("Shutdown handling failed", e);
        }
    }

    // The highest-priority per-frame source that has fired so far; see Frame(int).
    private static int s_frameSource = int.MaxValue;

    protected static void Frame() => Frame(0, "the player loop");

    /// <summary>
    /// Called for each frame by a source of the given priority (0 is highest). Backends that cannot use the player loop
    /// install several fallbacks, which may or may not fire in a given game, or may fire together; frames are taken from
    /// the highest-priority source that fires.
    /// </summary>
    protected static void Frame(int priority, string source)
    {
        var hooks = s_current;
        if (hooks is null || priority > s_frameSource)
            return;
        if (priority < s_frameSource)
            hooks.Log.Debug($"Frames come from {source}");
        s_frameSource = priority;
        try
        {
            hooks._lifecycle.Update();
            if (!hooks._guiRequested && hooks._lifecycle.WantsGui)
            {
                hooks._guiRequested = true;
                hooks.TryInstall("OnGUI (Unity's IMGUI)", hooks.InstallGui);
            }
        }
        catch (Exception e)
        {
            hooks.Log.Error("Update handling failed", e);
        }
    }

    protected static void FixedFrame()
    {
        try
        {
            s_current?._lifecycle.FixedUpdate();
        }
        catch (Exception e)
        {
            s_current?.Log.Error("FixedUpdate handling failed", e);
        }
    }

    protected static void LateFrame()
    {
        try
        {
            s_current?._lifecycle.LateUpdate();
        }
        catch (Exception e)
        {
            s_current?.Log.Error("LateUpdate handling failed", e);
        }
    }

    /// <summary>Called for each IMGUI event (a MonoBehaviour's OnGUI call) by the backend's GUI component.</summary>
    protected static void Gui()
    {
        try
        {
            s_current?._lifecycle.Gui();
        }
        catch (Exception e)
        {
            s_current?.Log.Error("OnGUI handling failed", e);
        }
    }

    /// <summary>
    /// Scene is a Unity struct. Its name may be returned as an Il2CppSystem.String and its build index as an IL2CPP
    /// integer; either may be missing (stripped) or throw, which must not prevent mods from receiving the event.
    /// </summary>
    protected virtual SceneInfo DescribeScene(object scene)
    {
        var (name, buildIndex) = ReadScene(scene);
        return new SceneInfo(name ?? "", buildIndex ?? -1);
    }

    /// <returns>Scene.name and Scene.buildIndex; null if the property is missing or reading it threw.</returns>
    protected (string? Name, int? BuildIndex) ReadScene(object scene)
    {
        var name = ReadProperty(scene, "name")?.ToString();
        var index = ReadProperty(scene, "buildIndex");
        int? buildIndex = index is int value ? value : int.TryParse(index?.ToString(), out var parsed) ? parsed : null;
        return (name, buildIndex);
    }

    private object? ReadProperty(object target, string name)
    {
        try
        {
            return target.GetType().GetProperty(name)?.GetValue(target);
        }
        catch (Exception e)
        {
            var cause = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
            Log.Debug($"Could not read {target.GetType().Name}.{name}: {cause.GetType().Name}");
            return null;
        }
    }
}
