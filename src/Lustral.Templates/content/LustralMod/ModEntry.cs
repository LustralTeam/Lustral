using HarmonyLib;
using Lustral;

namespace LustralMod;

/// <summary>The mod's entry point. Lustral creates a single instance when the game starts.</summary>
public sealed class ModEntry : Mod
{
    /// <summary>The mod instance, for use outside this class, for example in Harmony patches.</summary>
    internal static ModEntry Instance { get; private set; } = null!;

    // Log, Harmony, Config, Events, Coroutines, Assets, Info, Game and Mods are already available in the constructor.
    public ModEntry() => Instance = this;

    public override void OnLoad()
    {
        // Applies all [HarmonyPatch] classes in this assembly.
        Harmony.PatchAll(typeof(ModEntry).Assembly);

        Events.SceneLoaded += scene => Log.Info($"Scene loaded: {scene}");
        Log.Info($"{Info.Name} {Info.Version} loaded in {Game.Name} {Game.Version}");
    }

    public override void OnGameReady()
    {
        // The first scene has loaded. Unity APIs can be used from this point on.
    }
}

// Examples
//
// Per-frame events (subscribe in OnLoad): Events.Update, Events.LateUpdate, Events.FixedUpdate, and Events.OnGUI for
// Unity's immediate mode GUI.
//
// Events.OnGUI += () => UnityEngine.GUI.Label(new UnityEngine.Rect(10, 10, 300, 20), "Hello from my mod");

// Coroutines behave identically on Mono and IL2CPP. Start one in OnLoad with Coroutines.Start(Greet()).
// Wait.Seconds uses game time; Wait.RealSeconds, Wait.Frames, Wait.Until and null (the next frame) are also available.
//
// private System.Collections.IEnumerator Greet()
// {
//     yield return Wait.Seconds(5);
//     Log.Info("Five seconds have passed");
// }

// Asset bundles shipped with the mod (see the project file) can be loaded from OnGameReady onward. Build them with the
// game's Unity version.
//
// var bundle = Assets.LoadBundle("Assets/ui.bundle");                      // relative to the mod's folder
// var panel = bundle.Load<UnityEngine.GameObject>("Panel");                // by name or by path: "assets/ui/panel.prefab"

// Custom components are added with someGameObject.AddComponent<Spinner>(). On Mono games, they are ordinary
// MonoBehaviours. On IL2CPP games, they must be injected into the IL2CPP runtime: declare the class partial with
// [InjectedType], mark each method called by Unity with [Il2CppMethod], and store state in [Il2CppField] or
// [ManagedField] partial properties (a [ManagedField] must be assigned before it is read). Lustral registers injected
// types when the mod loads. A project that targets both backends declares each variant with conditional compilation:
//
//-:cnd:noEmit
// #if LUSTRAL_IL2CPP
// [Il2CppInterop.Common.Attributes.InjectedType]
// public partial class Spinner : UnityEngine.MonoBehaviour
// {
//     [Il2CppInterop.Common.Attributes.Il2CppMethod]
//     private void Update() => transform.Rotate(0, 90 * UnityEngine.Time.deltaTime, 0);
// }
// #else
// public class Spinner : UnityEngine.MonoBehaviour
// {
//     private void Update() => transform.Rotate(0, 90 * UnityEngine.Time.deltaTime, 0);
// }
// #endif
//+:cnd:noEmit

// Settings are stored in Lustral/config/<mod id>.toml in the game folder, where users can edit them:
//
// public sealed class Settings
// {
//     [System.ComponentModel.Description("Show the frame rate in the corner.")]
//     public bool ShowFps { get; set; }
// }
//
// var settings = Config.Bind<Settings>();                                  // in OnLoad

// Harmony patches run code before or after a game method:
//
// [HarmonyPatch(typeof(SomeGameClass), nameof(SomeGameClass.SomeMethod))]
// internal static class SomeMethodPatch
// {
//     private static void Postfix() => ModEntry.Instance.Log.Info("SomeMethod ran");
// }
