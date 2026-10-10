using Lustral.Core.Assets;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lustral.Backend.Mono;

/// <summary>Mods' asset bundles on Mono, using Unity's <see cref="AssetBundle"/> class from the game's UnityEngine.AssetBundleModule.</summary>
internal sealed class MonoAssetBundles : IAssetBundleBackend
{
    public string? Unsupported { get; } = Type.GetType("UnityEngine.AssetBundle, UnityEngine.AssetBundleModule") is null
        ? "This game's build does not include Unity's AssetBundle module (UnityEngine.AssetBundleModule.dll), so it cannot load asset bundles"
        : null;

    public string? CheckAssetType(Type type) =>
        typeof(Object).IsAssignableFrom(type) ? null : $"{type.FullName} is not a Unity object type; load assets as UnityEngine.Object or a derived type";

    public object? LoadFromFile(string path) => NullIfDestroyed(AssetBundle.LoadFromFile(path));

    public object? LoadFromMemory(byte[] data) => NullIfDestroyed(AssetBundle.LoadFromMemory(data));

    public bool IsLoaded(object bundle) => (AssetBundle)bundle != null;

    public bool IsStreamedSceneBundle(object bundle) => ((AssetBundle)bundle).isStreamedSceneAssetBundle;

    public string[] GetAllAssetNames(object bundle) => ((AssetBundle)bundle).GetAllAssetNames();

    public bool Contains(object bundle, string name) => ((AssetBundle)bundle).Contains(name);

    public object? LoadAsset(object bundle, string name, Type type) => NullIfDestroyed(((AssetBundle)bundle).LoadAsset(name, type));

    public object[] LoadAssetWithSubAssets(object bundle, string name, Type type) =>
        // LoadAllAssets is equivalent to LoadAssetWithSubAssets with an empty name, which the managed method rejects.
        name.Length == 0 ? ((AssetBundle)bundle).LoadAllAssets(type) : ((AssetBundle)bundle).LoadAssetWithSubAssets(name, type);

    public void Unload(object bundle, bool unloadAllLoadedObjects) => ((AssetBundle)bundle).Unload(unloadAllLoadedObjects);

    public void Release(object bundle)
    {
    }

    // Unity returns a "fake null" object for a failed load in the editor; destroyed objects compare equal to null.
    private static object? NullIfDestroyed(Object? value) => value != null ? value : null;
}
