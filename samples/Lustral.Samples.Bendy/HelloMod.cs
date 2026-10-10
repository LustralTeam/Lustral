using HarmonyLib;
using Il2Cpp;

namespace Lustral.Samples.Bendy;

public sealed class HelloMod : Mod
{
    private int _frames;
    private bool _drewLabel;

    /// <summary>The mod, for code outside this class such as its patches.</summary>
    internal static HelloMod Instance { get; private set; } = null!;

    public HelloMod() => Instance = this;

    public override void OnLoad()
    {
        Harmony.PatchAll(typeof(HelloMod).Assembly);
        var settings = Config.Bind<Settings>();
        if (settings.LogScenes)
            Events.SceneLoaded += scene => Log.Info($"Scene loaded: {scene}");
        Events.Update += CountFrames;
        Events.OnGUI += DrawLabel;
        Log.Info("Patched TitleScreenController.ShowMainMenu; it runs when the title screen shows the main menu");

        // An IL2CPP delegate made from a managed one: Il2CppInterop injects a class for it.
        UnityEngine.SceneManagement.SceneManager.add_sceneLoaded(
            (Action<UnityEngine.SceneManagement.Scene, UnityEngine.SceneManagement.LoadSceneMode>)OnUnitySceneLoaded);
    }

    private static void OnUnitySceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
    {
        string name = scene.name;
        Instance.Log.Info($"SceneManager.sceneLoaded delegate ran for \"{name}\": class injection works");
    }

    public override void OnGameReady() => Log.Info("Game ready");

    public override void OnShutdown() => Log.Info("Shutting down");

    // Unity's IMGUI over the game. IL2CPP builds keep only the GUI methods a game calls; Bendy's has GUI.Label.
    private void DrawLabel()
    {
        UnityEngine.GUI.Label(new UnityEngine.Rect(10, 10, 400, 20), "Lustral sample: OnGUI on IL2CPP");
        if (_drewLabel)
            return;
        _drewLabel = true;
        Log.Info("OnGUI drew its first label");
    }

    // Per-frame work is opt-in: once this unsubscribes, the mod costs nothing per frame.
    private void CountFrames()
    {
        if (++_frames < 300)
            return;
        Log.Info("Update ran for 300 frames");
        Events.Update -= CountFrames;
    }
}

// Reads and logs only; it never changes game state.
[HarmonyPatch(typeof(TitleScreenController), nameof(TitleScreenController.ShowMainMenu))]
internal static class ShowMainMenuPatch
{
    private static void Postfix(TitleScreenController __instance)
    {
        HelloMod.Instance.Log.Info("TitleScreenController.ShowMainMenu postfix ran: Harmony patches on IL2CPP methods work");
        try
        {
            // An IL2CPP List<MenuItemButton>, enumerated through Il2CppInterop (MoveNext returns Il2CppSystem.Boolean).
            var names = new List<string>();
            var buttons = __instance.m_MainMenuItemButtons.GetEnumerator();
            while (buttons.MoveNext())
                names.Add(buttons.Current.gameObject.name);
            HelloMod.Instance.Log.Info($"The main menu has {names.Count} buttons: {string.Join(", ", names)}");
        }
        catch (Exception e)
        {
            HelloMod.Instance.Log.Error("Reading the main menu's buttons failed", e);
        }
    }
}
