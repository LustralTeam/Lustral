using UnityEngine;

namespace Lustral.Tests.Player;

/// <summary>
/// Loads the asset bundle produced by the test player's build (shipped as Assets/lustral-e2e.bundle in the mod's folder) as
/// a mod would, and logs the results for tools/e2e.ps1 ("e2e: asset bundle: ..."). The IL2CPP test players do not include
/// Unity's AssetBundle class, so on IL2CPP the bundle is loaded through Lustral's native path.
/// </summary>
internal static class AssetBundleCheck
{
    private const string BundlePath = "Assets/lustral-e2e.bundle";

    public static void BeforeReady(Mod mod)
    {
        try
        {
            mod.Assets.LoadBundle(BundlePath);
            mod.Log.Error("e2e: an asset bundle loaded before the game was ready");
        }
        catch (InvalidOperationException)
        {
            mod.Log.Info("e2e: asset bundles wait for the game to be ready");
        }
    }

    public static void Run(Mod mod)
    {
        try
        {
            var steps = new List<string>();
            var bundle = mod.Assets.LoadBundle(BundlePath);
            steps.Add($"{bundle.AssetNames.Count} assets ({string.Join(", ", bundle.AssetNames.OrderBy(n => n, StringComparer.Ordinal))})");
            steps.Add($"contains note {bundle.Contains("note")}");
            steps.Add($"text {Name(bundle.Load<TextAsset>("note"))}");
            steps.Add($"prefab {Name(bundle.Load<GameObject>("Lustral Cube"))}");
            steps.Add($"sprites {string.Join("+", bundle.LoadWithSubAssets<Sprite>("atlas").Select(Name).OrderBy(n => n, StringComparer.Ordinal))}");
            steps.Add($"meshes {bundle.LoadAll<Mesh>().Length}");
            try
            {
                bundle.Load<Texture2D>("missing");
                steps.Add("missing asset loaded");
            }
            catch (KeyNotFoundException)
            {
                steps.Add("missing asset refused");
            }
            steps.Add(ReferenceEquals(bundle, mod.Assets.LoadBundle(BundlePath)) ? "loaded once" : "loaded twice");
            bundle.Unload();
            steps.Add(bundle.IsLoaded ? "still loaded" : "unloaded");
            try
            {
                bundle.Load<TextAsset>("note");
                steps.Add("loaded after unloading");
            }
            catch (ObjectDisposedException)
            {
                steps.Add("refused after unloading");
            }
            var fromBytes = mod.Assets.LoadBundle(File.ReadAllBytes(Path.Combine(mod.Info.Directory, BundlePath)));
            steps.Add($"from bytes {Name(fromBytes.Load<TextAsset>("note"))}");
            fromBytes.Unload(unloadAssets: true);
            mod.Log.Info($"e2e: asset bundle: {string.Join(", ", steps)}");
        }
        catch (Exception e)
        {
            mod.Log.Error("e2e: asset bundle check failed", e);
        }
    }

    // Il2CppInterop returns names as Il2CppSystem.String, which converts to string.
    private static string Name(UnityEngine.Object asset)
    {
        string name = asset.name;
        return name;
    }
}
