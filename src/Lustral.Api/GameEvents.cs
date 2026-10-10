namespace Lustral;

/// <summary>
/// Scene and frame events for a mod. Handlers run on the game's main thread. An exception thrown by a handler is logged
/// under the mod's ID and does not affect other handlers or other mods.
/// </summary>
public sealed class GameEvents
{
    internal GameEvents()
    {
    }

    /// <summary>Raised after a scene has loaded. For the first scene, it is raised after <see cref="Mod.OnGameReady"/>.</summary>
    public event Action<SceneInfo>? SceneLoaded;

    /// <summary>Raised after a scene has been unloaded.</summary>
    public event Action<SceneInfo>? SceneUnloaded;

    /// <summary>
    /// Raised once per frame, after the game's <c>Update</c> methods. Mods that do not subscribe incur no per-frame cost.
    /// </summary>
    public event Action? Update;

    /// <summary>
    /// Raised once per physics step, together with the game's <c>FixedUpdate</c> methods and before the physics
    /// simulation. Physics steps occur at a fixed rate (50 per second by default) independent of the frame rate, so the
    /// event can be raised zero or more times per frame. Use it for forces and other physics work.
    /// </summary>
    public event Action? FixedUpdate;

    /// <summary>
    /// Raised once per frame, after the game's <c>LateUpdate</c> methods and therefore after all <c>Update</c> methods.
    /// Use it for cameras and other logic that depends on movement during the frame.
    /// </summary>
    public event Action? LateUpdate;

    /// <summary>
    /// Raised for Unity's immediate mode GUI (<c>GUI</c>, <c>GUILayout</c>), drawn over the game, as a MonoBehaviour's
    /// <c>OnGUI</c> method is: several times per frame, once per GUI event (<c>Event.current</c>: layout, repaint, input).
    /// Lustral enables Unity's GUI only after a mod subscribes, because it has a per-frame cost. Some game builds exclude
    /// Unity's IMGUI module; Lustral then logs a message and the event is not raised. On IL2CPP games, GUI methods that the
    /// game does not use may be stripped from the build, and those that Lustral cannot restore are absent from the
    /// interop assemblies.
    /// </summary>
    public event Action? OnGUI;

    internal bool HasUpdateHandlers => Update is not null;

    internal bool HasFixedUpdateHandlers => FixedUpdate is not null;

    internal bool HasLateUpdateHandlers => LateUpdate is not null;

    internal bool HasGuiHandlers => OnGUI is not null;

    internal void RaiseSceneLoaded(SceneInfo scene, Action<Exception> onError) => Raise(SceneLoaded, scene, onError);

    internal void RaiseSceneUnloaded(SceneInfo scene, Action<Exception> onError) => Raise(SceneUnloaded, scene, onError);

    internal void RaiseUpdate(Action<Exception> onError) => Raise(Update, onError);

    internal void RaiseFixedUpdate(Action<Exception> onError) => Raise(FixedUpdate, onError);

    internal void RaiseLateUpdate(Action<Exception> onError) => Raise(LateUpdate, onError);

    internal void RaiseGui(Action<Exception> onError) => Raise(OnGUI, onError);

    private static void Raise(Action? handlers, Action<Exception> onError)
    {
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception e)
            {
                onError(e);
            }
        }
    }

    private static void Raise(Action<SceneInfo>? handlers, SceneInfo scene, Action<Exception> onError)
    {
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<SceneInfo>)handler)(scene);
            }
            catch (Exception e)
            {
                onError(e);
            }
        }
    }
}

/// <summary>A Unity scene, represented without Unity types so that it is identical on every backend.</summary>
public readonly struct SceneInfo : IEquatable<SceneInfo>
{
    /// <summary>Initializes a scene description.</summary>
    public SceneInfo(string name, int buildIndex)
    {
        Name = name;
        BuildIndex = buildIndex;
    }

    /// <summary>The scene's name, as listed in the game's build settings.</summary>
    public string Name { get; }

    /// <summary>The scene's index in the game's build settings, or -1 for scenes loaded from asset bundles.</summary>
    public int BuildIndex { get; }

    /// <summary>Returns whether both scenes have the same name and build index.</summary>
    public bool Equals(SceneInfo other) => Name == other.Name && BuildIndex == other.BuildIndex;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SceneInfo other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => (Name, BuildIndex).GetHashCode();

    /// <summary>Returns whether both scenes have the same name and build index.</summary>
    public static bool operator ==(SceneInfo left, SceneInfo right) => left.Equals(right);

    /// <summary>Returns whether the scenes differ in name or build index.</summary>
    public static bool operator !=(SceneInfo left, SceneInfo right) => !left.Equals(right);

    /// <summary>Returns <c>Name (#BuildIndex)</c>.</summary>
    public override string ToString() => $"{Name} (#{BuildIndex})";
}
