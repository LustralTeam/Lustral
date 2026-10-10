namespace Lustral.Core.Assets;

/// <summary>
/// Unity's asset bundle functions for one backend, used by mods' <see cref="ModAssets"/>. Bundles are the backend's
/// references to loaded bundles; types are the Unity object types requested by mods. Lustral calls these methods on the
/// main thread, after the game is ready, with validated arguments.
/// </summary>
public interface IAssetBundleBackend
{
    /// <summary>Returns why this game cannot load asset bundles, or null if it can.</summary>
    string? Unsupported { get; }

    /// <summary>Returns null if the type is a Unity object type; otherwise, the reason it is not.</summary>
    string? CheckAssetType(Type type);

    /// <summary>The loaded bundle, or null if Unity cannot load the file.</summary>
    object? LoadFromFile(string path);

    /// <summary>The loaded bundle, or null if Unity cannot load the data.</summary>
    object? LoadFromMemory(byte[] data);

    /// <summary>Whether the bundle is still loaded, that is, it has not been unloaded by Lustral or the game.</summary>
    bool IsLoaded(object bundle);

    bool IsStreamedSceneBundle(object bundle);

    string[] GetAllAssetNames(object bundle);

    bool Contains(object bundle, string name);

    /// <summary>The asset, or null if the bundle contains no asset with that name and type.</summary>
    object? LoadAsset(object bundle, string name, Type type);

    /// <summary>The asset and its sub-assets of that type; all assets of that type if the name is empty.</summary>
    object[] LoadAssetWithSubAssets(object bundle, string name, Type type);

    void Unload(object bundle, bool unloadAllLoadedObjects);

    /// <summary>Releases a bundle after it has been unloaded.</summary>
    void Release(object bundle);
}

/// <summary>
/// Mods' asset bundles (<see cref="ModAssets"/>) on the game's backend. Rejects calls made before the game is ready, when
/// Unity's engine may not be initialized (on IL2CPP, mods load during <c>il2cpp_init</c>), and calls made outside the main
/// thread, where Unity's functions fail or, when called natively on IL2CPP, crash the game.
/// </summary>
internal sealed class AssetBundles(IAssetBundleBackend backend) : AssetBundleRuntime
{
    private readonly int _mainThread = Environment.CurrentManagedThreadId;

    /// <summary>Set when the first scene loads, before mods' <see cref="Mod.OnGameReady"/>.</summary>
    public bool GameReady { get; set; }

    public override void CheckUsable()
    {
        if (!GameReady)
            throw new InvalidOperationException("Asset bundles can be loaded only after the game is ready: load them in OnGameReady or later, not in OnLoad");
        if (Environment.CurrentManagedThreadId != _mainThread)
            throw new InvalidOperationException("Asset bundles can be loaded only on Unity's main thread: load them from a mod's events, coroutines or OnGameReady, not from another thread");
        // Queried only at this point, because a backend may look up Unity's functions on first use.
        if (backend.Unsupported is { } reason)
            throw new NotSupportedException(reason);
    }

    public override void CheckAssetType(Type type)
    {
        if (backend.CheckAssetType(type) is { } problem)
            throw new ArgumentException(problem, "T");
    }

    public override object? LoadFromFile(string path) => backend.LoadFromFile(path);

    public override object? LoadFromMemory(byte[] data) => backend.LoadFromMemory(data);

    public override bool IsLoaded(object bundle) => backend.IsLoaded(bundle);

    public override bool IsStreamedSceneBundle(object bundle) => backend.IsStreamedSceneBundle(bundle);

    public override string[] GetAllAssetNames(object bundle) => backend.GetAllAssetNames(bundle);

    public override bool Contains(object bundle, string name) => backend.Contains(bundle, name);

    public override object? LoadAsset(object bundle, string name, Type type) => backend.LoadAsset(bundle, name, type);

    public override object[] LoadAssetWithSubAssets(object bundle, string name, Type type) => backend.LoadAssetWithSubAssets(bundle, name, type);

    public override void Unload(object bundle, bool unloadAllLoadedObjects) => backend.Unload(bundle, unloadAllLoadedObjects);

    public override void Release(object bundle) => backend.Release(bundle);
}
