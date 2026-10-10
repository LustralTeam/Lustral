using Lustral.Core.Assets;
using Lustral.Core.Lifecycle;
using Lustral.Core.Loading;

namespace Lustral.Core.Startup;

/// <summary>
/// The runtime-specific part of startup. Lustral.Core handles discovery, ordering and loading; a backend prepares the
/// runtime for mods and determines how their assemblies are loaded.
/// </summary>
public interface IBackend
{
    /// <summary>The name shown in the log, for example "IL2CPP" or "Mono".</summary>
    string Name { get; }

    /// <summary>The game's scripting backend.</summary>
    ScriptingBackend Kind { get; }

    /// <summary>Prepares the runtime for mods. Throws <see cref="StartupException"/> with a message that tells the user how to proceed.</summary>
    void Start();

    /// <summary>
    /// Calls <paramref name="loadMods"/> once mods can be loaded and can patch the game, on the backend's current thread:
    /// immediately on IL2CPP, and on Mono once Unity has loaded the game's code. Before that point, the engine has not
    /// registered its native functions, and a patched method calling one would fail for the rest of the session.
    /// </summary>
    void WhenReadyForMods(Action loadMods);

    /// <summary>The assembly loading strategy for mods on this backend.</summary>
    IModAssemblyLoader CreateAssemblyLoader();

    /// <summary>Unity's asset bundle functions for mods' <see cref="ModAssets"/>. Called once, on the main thread, before mods are loaded.</summary>
    IAssetBundleBackend CreateAssetBundles();

    /// <summary>Connects Unity's scene, quit and frame events to <paramref name="lifecycle"/>. Never throws.</summary>
    void InstallLifecycle(GameLifecycle lifecycle);

    /// <summary>
    /// The game's version as reported by the running game (<c>Application.version</c>), queried once the game is ready;
    /// null if the build does not provide it (IL2CPP builds may strip the property).
    /// </summary>
    string? ReadGameVersion();
}
