using HarmonyLib;
using Il2Cpp;

namespace Lustral.Samples.Bombanana;

public sealed class HelloMod : Mod
{
    private int _frames;

    /// <summary>The mod instance, for use outside this class, for example in patches.</summary>
    internal static HelloMod Instance { get; private set; } = null!;

    public HelloMod() => Instance = this;

    public override void OnLoad()
    {
        Harmony.PatchAll(typeof(HelloMod).Assembly);
        var settings = Config.Bind<Settings>();
        if (settings.LogScenes)
            Events.SceneLoaded += scene => Log.Info($"Scene loaded: {scene}");
        Events.Update += CountFrames;
        Log.Info("Patched BuildVersionDisplay.Awake, which runs when the title screen shows the build version");

        // Converts a managed delegate to an IL2CPP delegate, which requires Il2CppInterop's class injection.
        UnityEngine.SceneManagement.SceneManager.add_sceneLoaded(
            (Action<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>)OnUnitySceneLoaded);
        Log.Info("Subscribed to SceneManager.sceneLoaded with a managed delegate");
    }

    private static void OnUnitySceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        string name = scene.name;
        Instance.Log.Info($"SceneManager.sceneLoaded delegate called for \"{name}\": class injection works");
    }

    public override void OnGameReady() => Log.Info("Game ready");

    public override void OnShutdown() => Log.Info("Shutting down");

    // Per-frame work is optional: after this handler unsubscribes, the mod has no per-frame cost.
    private void CountFrames()
    {
        if (++_frames < 300)
            return;
        Log.Info("Update ran for 300 frames");
        Events.Update -= CountFrames;
    }
}

// BOMBANANA! is an online multiplayer game, so this patch only reads and logs and never changes game state.
[HarmonyPatch(typeof(BuildVersionDisplay), nameof(BuildVersionDisplay.Awake))]
internal static class BuildVersionDisplayPatch
{
    private static void Postfix(BuildVersionDisplay __instance)
    {
        HelloMod.Instance.Log.Info("BuildVersionDisplay.Awake postfix ran: Harmony patches on IL2CPP methods work");

        // A separate check: calling into IL2CPP from the patch.
        try
        {
            string name = __instance.gameObject.name; // V2 returns Il2CppSystem.String, which converts implicitly.
            HelloMod.Instance.Log.Info($"The component is on GameObject \"{name}\"");
        }
        catch (Exception e)
        {
            HelloMod.Instance.Log.Error("Reading the GameObject's name failed", e);
        }
    }
}
