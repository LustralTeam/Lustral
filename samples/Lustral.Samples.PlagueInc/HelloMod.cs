using HarmonyLib;

namespace Lustral.Samples.PlagueInc;

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
        Log.Info("Patched CMainMenuScreen.SetActive, which runs when the main menu opens");
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

// Plague Inc is a single-player game, but as with all samples, this patch only reads and logs.
[HarmonyPatch(typeof(CMainMenuScreen), nameof(CMainMenuScreen.SetActive))]
internal static class MainMenuPatch
{
    private static void Postfix(CMainMenuScreen __instance, bool __0)
    {
        if (__0)
            HelloMod.Instance.Log.Info($"Main menu opened on GameObject \"{__instance.gameObject.name}\": Harmony patches on Mono work");
    }
}
