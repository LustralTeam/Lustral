namespace Lustral;

/// <summary>
/// Loads Unity asset bundles shipped with a mod, on both Mono and IL2CPP:
/// <code>
/// var bundle = Assets.LoadBundle("Assets/ui.bundle");
/// var panel = bundle.Load&lt;GameObject&gt;("Panel");
/// </code>
/// This also works in IL2CPP games whose builds do not include Unity's <c>AssetBundle</c> class, because Lustral calls the
/// engine directly. Bundles must be loaded on the main thread, from <see cref="Mod.OnGameReady"/> onward.
/// </summary>
/// <remarks>
/// Build bundles with the game's Unity version or a close version; Unity cannot load bundles built with a newer version.
/// Ship bundles in the mod's folder (<c>&lt;None Include="Assets\**" CopyToOutputDirectory="PreserveNewest" /&gt;</c> in
/// the project file), or embed them in the mod's assembly and load them from bytes.
/// </remarks>
public sealed class ModAssets
{
    private readonly AssetBundleRuntime? _runtime;
    private readonly string _modDirectory;
    private readonly string? _unityVersion;
    private readonly Dictionary<string, ModAssetBundle> _files =
        new(Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal ModAssets(AssetBundleRuntime? runtime, string modDirectory, string? unityVersion)
    {
        _runtime = runtime;
        _modDirectory = modDirectory;
        _unityVersion = unityVersion;
    }

    /// <summary>
    /// Loads an asset bundle file. Relative paths are resolved against the mod's folder (<see cref="ModInfo.Directory"/>).
    /// Loading a file that is already loaded returns the same bundle, because Unity loads each bundle only once.
    /// </summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidOperationException">
    /// Unity could not load the bundle (the game's log, <c>Player.log</c>, contains the reason), or the method was called
    /// before the game was ready or outside the main thread.
    /// </exception>
    public ModAssetBundle LoadBundle(string path)
    {
        if (path is null)
            throw new ArgumentNullException(nameof(path));
        var runtime = Runtime();
        var full = Path.GetFullPath(Path.Combine(_modDirectory, path));
        if (_files.TryGetValue(full, out var loaded))
        {
            if (loaded.IsLoaded)
                return loaded;
            loaded.Unload(); // Unloaded by the game; release the stale handle.
        }
        if (!File.Exists(full))
            throw new FileNotFoundException($"Asset bundle not found: {full}", full);

        var handle = runtime.LoadFromFile(full) ?? throw CouldNotLoad(Path.GetFileName(full));
        var bundle = new ModAssetBundle(runtime, handle, Path.GetFileName(full), () => _files.Remove(full));
        _files[full] = bundle;
        return bundle;
    }

    /// <summary>
    /// Loads an asset bundle from memory, such as a bundle embedded in the mod's assembly:
    /// <c>Assets.LoadBundle(ReadResource("MyMod.ui.bundle"))</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Unity could not load the bundle (the game's log, <c>Player.log</c>, contains the reason), or the method was called
    /// before the game was ready or outside the main thread.
    /// </exception>
    public ModAssetBundle LoadBundle(byte[] data)
    {
        if (data is null)
            throw new ArgumentNullException(nameof(data));
        var runtime = Runtime();
        var handle = runtime.LoadFromMemory(data) ?? throw CouldNotLoad("from memory");
        return new ModAssetBundle(runtime, handle, ModAssetBundle.FromMemory, null);
    }

    private AssetBundleRuntime Runtime()
    {
        var runtime = _runtime ?? throw new InvalidOperationException("Asset bundles are available only when running in a Unity game");
        runtime.CheckUsable();
        return runtime;
    }

    private InvalidOperationException CouldNotLoad(string name) => new(
        $"Unity could not load the asset bundle {name}. See the game's log (Player.log) for details. Common causes are a bundle built with a " +
        $"newer Unity version than the game's ({_unityVersion ?? "unknown"}), a damaged file, or the same bundle already loaded by another mod.");
}

/// <summary>
/// A loaded asset bundle (<see cref="ModAssets.LoadBundle(string)"/>). Asset names are full lowercase paths as reported by
/// Unity, such as <c>assets/ui/panel.prefab</c>; <see cref="Load{T}"/> also accepts the file name alone, such as <c>Panel</c>.
/// </summary>
public sealed class ModAssetBundle : IDisposable
{
    internal const string FromMemory = "(from memory)";

    private readonly AssetBundleRuntime _runtime;
    private readonly Action? _onUnload;
    private object? _handle;
    private string[]? _assetNames;

    internal ModAssetBundle(AssetBundleRuntime runtime, object handle, string name, Action? onUnload)
    {
        _runtime = runtime;
        _handle = handle;
        Name = name;
        _onUnload = onUnload;
    }

    /// <summary>The bundle's file name, or <c>(from memory)</c> for a bundle loaded from bytes.</summary>
    public string Name { get; }

    /// <summary>Whether the bundle is still loaded, that is, it has not been unloaded by <see cref="Unload"/> or by the game.</summary>
    public bool IsLoaded => _handle is { } handle && _runtime.IsLoaded(handle);

    /// <summary>The full paths of all assets in the bundle, such as <c>assets/ui/panel.prefab</c>.</summary>
    public IReadOnlyList<string> AssetNames => _assetNames ??= _runtime.GetAllAssetNames(Handle());

    /// <summary>Returns whether the bundle contains an asset with the specified name or path.</summary>
    public bool Contains(string name)
    {
        if (name is null)
            throw new ArgumentNullException(nameof(name));
        return _runtime.Contains(Handle(), name);
    }

    /// <summary>
    /// Loads an asset by name (<c>Panel</c>) or path (<c>assets/ui/panel.prefab</c>) as the specified type, for example a
    /// prefab as <c>GameObject</c>, or an image as <c>Texture2D</c> or <c>Sprite</c>.
    /// </summary>
    /// <typeparam name="T">A Unity object type: <c>UnityEngine.Object</c> or a derived type.</typeparam>
    /// <exception cref="KeyNotFoundException">The bundle contains no asset with that name and type.</exception>
    public T Load<T>(string name) where T : class
    {
        CheckName(name);
        var handle = AssetHandle(typeof(T));
        if (_runtime.LoadAsset(handle, name, typeof(T)) is T asset)
            return asset;
        var names = AssetNames;
        throw new KeyNotFoundException(
            $"{Name} contains no {typeof(T).Name} named {name}. {(names.Count == 0 ? "The bundle is empty." : "Assets in the bundle: " + string.Join(", ", names.Take(20)) + (names.Count > 20 ? $" and {names.Count - 20} more" : ""))}");
    }

    /// <summary>Loads all assets of type <typeparamref name="T"/> in the bundle; all assets for <c>UnityEngine.Object</c>.</summary>
    public T[] LoadAll<T>() where T : class => LoadWithSubAssets<T>(AssetHandle(typeof(T)), "");

    /// <summary>
    /// Loads an asset and its sub-assets of type <typeparamref name="T"/>, such as the sprites in a texture or the meshes in
    /// a model. Returns an empty array if the asset does not exist.
    /// </summary>
    public T[] LoadWithSubAssets<T>(string name) where T : class
    {
        CheckName(name);
        return LoadWithSubAssets<T>(AssetHandle(typeof(T)), name);
    }

    // An empty name loads all assets, as Unity's LoadAllAssets does.
    private T[] LoadWithSubAssets<T>(object handle, string name) where T : class =>
        _runtime.LoadAssetWithSubAssets(handle, name, typeof(T)).OfType<T>().ToArray();

    /// <summary>
    /// Unloads the bundle. Assets already loaded from it remain loaded unless <paramref name="unloadAssets"/> is true, in
    /// which case they are destroyed and objects still using them show missing textures or meshes. Has no effect if the
    /// bundle is already unloaded.
    /// </summary>
    public void Unload(bool unloadAssets = false)
    {
        if (_handle is not { } handle)
            return;
        _runtime.CheckUsable();
        _handle = null;
        _onUnload?.Invoke();
        if (_runtime.IsLoaded(handle))
            _runtime.Unload(handle, unloadAssets);
        _runtime.Release(handle);
    }

    /// <summary>Unloads the bundle without unloading the assets loaded from it (see <see cref="Unload"/>).</summary>
    public void Dispose() => Unload();

    /// <inheritdoc />
    public override string ToString() => Name;

    private static void CheckName(string name)
    {
        if (name is null)
            throw new ArgumentNullException(nameof(name));
        if (name.Length == 0)
            throw new ArgumentException("The asset name cannot be empty", nameof(name));
    }

    private object Handle()
    {
        _runtime.CheckUsable();
        if (_handle is not { } handle || !_runtime.IsLoaded(handle))
            throw new ObjectDisposedException(Name, $"The asset bundle {Name} has been unloaded");
        return handle;
    }

    private object AssetHandle(Type type)
    {
        var handle = Handle();
        _runtime.CheckAssetType(type);
        if (_runtime.IsStreamedSceneBundle(handle))
            throw new InvalidOperationException($"{Name} is a scene bundle. Load its scenes by name with Unity's SceneManager.");
        return handle;
    }
}

/// <summary>
/// Unity's asset bundle functions for the game's backend, implemented in Lustral.Core. Handles are the backend's references
/// to loaded bundles.
/// </summary>
internal abstract class AssetBundleRuntime
{
    /// <summary>Throws <see cref="InvalidOperationException"/> before the game is ready or off the main thread.</summary>
    public abstract void CheckUsable();

    /// <summary>Throws <see cref="ArgumentException"/> unless the type is a Unity object type.</summary>
    public abstract void CheckAssetType(Type type);

    /// <summary>The loaded bundle, or null if Unity cannot load the file.</summary>
    public abstract object? LoadFromFile(string path);

    /// <summary>The loaded bundle, or null if Unity cannot load the data.</summary>
    public abstract object? LoadFromMemory(byte[] data);

    /// <summary>Whether the bundle is still loaded.</summary>
    public abstract bool IsLoaded(object bundle);

    public abstract bool IsStreamedSceneBundle(object bundle);

    public abstract string[] GetAllAssetNames(object bundle);

    public abstract bool Contains(object bundle, string name);

    /// <summary>The asset, or null if the bundle contains no asset with that name and type.</summary>
    public abstract object? LoadAsset(object bundle, string name, Type type);

    public abstract object[] LoadAssetWithSubAssets(object bundle, string name, Type type);

    public abstract void Unload(object bundle, bool unloadAllLoadedObjects);

    /// <summary>Releases the handle after the bundle has been unloaded.</summary>
    public abstract void Release(object bundle);
}
