namespace Lustral;

/// <summary>
/// The services Lustral provides to a mod, exposed through <see cref="Mod"/>. Lustral.Core implements this class and sets
/// <see cref="Creating"/> while constructing the mod, so that the services are available in the mod's constructor.
/// </summary>
internal abstract class ModContext
{
    [ThreadStatic]
    internal static ModContext? Creating;

    public abstract ModInfo Info { get; }

    public abstract ILog Log { get; }

    public abstract GameEvents Events { get; }

    public abstract ModConfig Config { get; }

    public abstract HarmonyLib.Harmony Harmony { get; }

    public abstract GameInfo Game { get; }

    public abstract LoadedMods Mods { get; }

    public abstract Coroutines Coroutines { get; }

    public abstract ModAssets Assets { get; }
}
