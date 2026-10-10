using UnityEngine;

namespace Lustral.Backend.Mono;

/// <summary>
/// Lustral's hidden component for mods' OnGUI handlers, because Unity calls OnGUI only on components. Created when a mod
/// first subscribes.
/// </summary>
/// <remarks>Created no earlier than the first scene, because Unity's GUI requires the engine to be running.</remarks>
internal sealed class LustralGuiBehaviour : MonoBehaviour
{
    private static Action? s_gui;

    public static void Create(Action gui)
    {
        s_gui = gui;
        var host = new GameObject("Lustral") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(host);
        host.AddComponent<LustralGuiBehaviour>();
    }

    private void OnGUI() => s_gui?.Invoke();
}

/// <summary>
/// Lustral's hidden component for per-frame events where Unity's player loop cannot be extended (before Unity 2019.3).
/// Unity calls it like the game's own components: Update, then LateUpdate, and FixedUpdate before each physics step.
/// </summary>
internal sealed class LustralFrameBehaviour : MonoBehaviour
{
    private static Action? s_update, s_fixedUpdate, s_lateUpdate;

    public static void Create(Action update, Action fixedUpdate, Action lateUpdate)
    {
        (s_update, s_fixedUpdate, s_lateUpdate) = (update, fixedUpdate, lateUpdate);
        var host = new GameObject("Lustral Frames") { hideFlags = HideFlags.HideAndDontSave };
        DontDestroyOnLoad(host);
        host.AddComponent<LustralFrameBehaviour>();
    }

    private void Update() => s_update?.Invoke();

    private void FixedUpdate() => s_fixedUpdate?.Invoke();

    private void LateUpdate() => s_lateUpdate?.Invoke();
}
