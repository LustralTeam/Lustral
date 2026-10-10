namespace Lustral;

/// <summary>
/// The base class of a mod's entry point. Lustral creates one instance of each mod and calls it on the game's main thread.
/// </summary>
/// <remarks>
/// <see cref="Info"/>, <see cref="Log"/>, <see cref="Harmony"/>, <see cref="Config"/>, <see cref="Events"/>,
/// <see cref="Coroutines"/>, <see cref="Assets"/>, <see cref="Game"/> and <see cref="Mods"/> are available from the
/// constructor onward. Code outside the class, such as Harmony patches, can access them through a static reference to the
/// instance, as in the project template.
/// </remarks>
public abstract class Mod
{
    private readonly ModContext? _context = ModContext.Creating;

    /// <summary>The mod's ID, name, version and folder, as declared in its manifest.</summary>
    public ModInfo Info => Context.Info;

    /// <summary>The mod's log. Messages are tagged with the mod's ID and may contain Unity rich text tags (see <see cref="ILog"/>).</summary>
    public ILog Log => Context.Log;

    /// <summary>
    /// A Harmony instance identified by the mod's ID: <c>Harmony.PatchAll(typeof(MyMod).Assembly)</c>. If the mod fails to
    /// load, Lustral removes the patches applied through this instance. Crash reports use it to identify the mod that
    /// patched each method.
    /// </summary>
    public HarmonyLib.Harmony Harmony => Context.Harmony;

    /// <summary>The mod's settings file, <c>Lustral/config/&lt;id&gt;.toml</c>: <c>Config.Bind&lt;Settings&gt;()</c>.</summary>
    public ModConfig Config => Context.Config;

    /// <summary>Scene and frame events.</summary>
    public GameEvents Events => Context.Events;

    /// <summary>
    /// The mod's coroutines, which behave identically on Mono and IL2CPP: <c>Coroutines.Start(Fade())</c>. A coroutine is
    /// an iterator method that yields <c>null</c>, <see cref="Wait"/> values, other iterators or Unity yield instructions.
    /// </summary>
    public Coroutines Coroutines => Context.Coroutines;

    /// <summary>
    /// Loads Unity asset bundles shipped with the mod, on both Mono and IL2CPP: <c>Assets.LoadBundle("Assets/ui.bundle")</c>.
    /// Paths are relative to the mod's folder. Bundles can be loaded from <see cref="OnGameReady"/> onward.
    /// </summary>
    public ModAssets Assets => Context.Assets;

    /// <summary>The game's name, developer, version, Unity version, scripting backend and folder.</summary>
    public GameInfo Game => Context.Game;

    /// <summary>
    /// The mods loaded so far, in load order: <c>Mods.IsLoaded("other.mod")</c>. Dependencies and optional dependencies are
    /// loaded before the mods that depend on them, so an installed optional dependency is always in this list.
    /// </summary>
    public LoadedMods Mods => Context.Mods;

    private ModContext Context => _context ?? throw new InvalidOperationException(
        $"{GetType().FullName} was not created by Lustral. Info, Log, Harmony, Config, Events, Coroutines, Assets, Game and Mods are available only on mod instances that Lustral creates.");

    /// <summary>
    /// Called once, before the game loads its first scene. Apply Harmony patches here. Unity APIs cannot be used yet.
    /// </summary>
    public virtual void OnLoad()
    {
    }

    /// <summary>
    /// Called once, after the game's first scene has loaded. Unity APIs can be used from this point on.
    /// </summary>
    public virtual void OnGameReady()
    {
    }

    /// <summary>
    /// Called once when the game quits. Limit the work to short tasks such as saving state and closing files.
    /// </summary>
    public virtual void OnShutdown()
    {
    }
}
