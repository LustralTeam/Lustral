using Lustral.Core.Assets;

namespace Lustral.Core.Tests;

public sealed class ModAssetsTests : IDisposable
{
    private class Asset(string name)
    {
        public string Name { get; } = name;
    }

    private sealed class Texture(string name) : Asset(name);

    private sealed class FakeBundle(string source, Dictionary<string, Asset> assets)
    {
        public string Source { get; } = source;
        public Dictionary<string, Asset> Assets { get; } = assets;
        public bool Loaded { get; set; } = true;
        public bool Released { get; set; }
        public bool Scenes { get; set; }
    }

    // Unity's side, as provided by the backends: bundles by file and assets by name.
    private sealed class FakeBackend : IAssetBundleBackend
    {
        public string? Unsupported { get; set; }
        public Dictionary<string, Asset> Contents { get; } = new() { ["assets/ui/panel.prefab"] = new Asset("Panel"), ["assets/ui/icon.png"] = new Texture("icon") };
        public List<FakeBundle> Loads { get; } = [];
        public bool Fails { get; set; }
        public bool Scenes { get; set; }

        public string? CheckAssetType(Type type) => typeof(Asset).IsAssignableFrom(type) ? null : $"{type.Name} isn't an asset";

        public object? LoadFromFile(string path) => Load(path);

        public object? LoadFromMemory(byte[] data) => Load($"{data.Length} bytes");

        private FakeBundle? Load(string source)
        {
            if (Fails)
                return null;
            var bundle = new FakeBundle(source, Contents) { Scenes = Scenes };
            Loads.Add(bundle);
            return bundle;
        }

        public bool IsLoaded(object bundle) => ((FakeBundle)bundle).Loaded;

        public bool IsStreamedSceneBundle(object bundle) => ((FakeBundle)bundle).Scenes;

        public string[] GetAllAssetNames(object bundle) => [.. ((FakeBundle)bundle).Assets.Keys];

        public bool Contains(object bundle, string name) => Find(bundle, name) is not null;

        public object? LoadAsset(object bundle, string name, Type type) => Find(bundle, name) is { } asset && type.IsInstanceOfType(asset) ? asset : null;

        public object[] LoadAssetWithSubAssets(object bundle, string name, Type type) =>
            [.. ((FakeBundle)bundle).Assets.Where(a => (name.Length == 0 || Matches(a.Key, name)) && type.IsInstanceOfType(a.Value)).Select(a => a.Value)];

        public void Unload(object bundle, bool unloadAllLoadedObjects) => ((FakeBundle)bundle).Loaded = false;

        public void Release(object bundle) => ((FakeBundle)bundle).Released = true;

        private static Asset? Find(object bundle, string name) => ((FakeBundle)bundle).Assets.FirstOrDefault(a => Matches(a.Key, name)).Value;

        // As in Unity: the full path, or the file name without its extension, case-insensitively.
        private static bool Matches(string path, string name) =>
            string.Equals(path, name, StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileNameWithoutExtension(path), name, StringComparison.OrdinalIgnoreCase);
    }

    private readonly string _modDirectory = Path.Combine(Path.GetTempPath(), "lustral-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeBackend _backend = new();
    private readonly AssetBundles _runtime;
    private readonly ModAssets _assets;

    public ModAssetsTests()
    {
        Directory.CreateDirectory(Path.Combine(_modDirectory, "Assets"));
        File.WriteAllBytes(Path.Combine(_modDirectory, "Assets", "ui.bundle"), [1, 2, 3]);
        _runtime = new AssetBundles(_backend) { GameReady = true };
        _assets = new ModAssets(_runtime, _modDirectory, "6000.0.58f2");
    }

    public void Dispose() => Directory.Delete(_modDirectory, recursive: true);

    [Fact]
    public void A_bundle_loads_from_the_mods_folder_and_its_assets_by_name_or_path()
    {
        var bundle = _assets.LoadBundle("Assets/ui.bundle");

        Assert.Equal(Path.Combine(_modDirectory, "Assets", "ui.bundle"), Assert.Single(_backend.Loads).Source);
        Assert.Equal("ui.bundle", bundle.Name);
        Assert.Equal(["assets/ui/panel.prefab", "assets/ui/icon.png"], bundle.AssetNames);
        Assert.Equal("Panel", bundle.Load<Asset>("Panel").Name);
        Assert.Equal("icon", bundle.Load<Texture>("assets/ui/icon.png").Name);
        Assert.True(bundle.Contains("panel"));
        Assert.Equal(["icon"], bundle.LoadAll<Texture>().Select(t => t.Name));
        Assert.Equal(2, bundle.LoadAll<Asset>().Length);
        Assert.Single(bundle.LoadWithSubAssets<Asset>("icon"));
    }

    [Fact]
    public void Loading_a_file_again_gives_the_same_bundle()
    {
        var first = _assets.LoadBundle("Assets/ui.bundle");

        Assert.Same(first, _assets.LoadBundle(Path.Combine(_modDirectory, "Assets", "UI.bundle")));
        Assert.Single(_backend.Loads);
    }

    [Fact]
    public void A_missing_asset_names_what_the_bundle_has()
    {
        var bundle = _assets.LoadBundle("Assets/ui.bundle");

        var missing = Assert.Throws<KeyNotFoundException>(() => bundle.Load<Asset>("Panal"));
        Assert.Equal("ui.bundle contains no Asset named Panal. Assets in the bundle: assets/ui/panel.prefab, assets/ui/icon.png", missing.Message);
        Assert.Throws<KeyNotFoundException>(() => bundle.Load<Texture>("Panel")); // there, but not a texture
        Assert.Throws<ArgumentException>(() => bundle.Load<Asset>(""));
        Assert.Throws<ArgumentException>(() => bundle.Load<string>("Panel"));
    }

    [Fact]
    public void An_unloaded_bundle_refuses_loads_and_lets_go_of_its_handle()
    {
        var bundle = _assets.LoadBundle("Assets/ui.bundle");

        bundle.Unload();
        bundle.Unload();

        Assert.False(bundle.IsLoaded);
        Assert.True(_backend.Loads[0].Released);
        Assert.Throws<ObjectDisposedException>(() => bundle.Load<Asset>("Panel"));
        Assert.NotSame(bundle, _assets.LoadBundle("Assets/ui.bundle"));
        Assert.Equal(2, _backend.Loads.Count);
    }

    [Fact]
    public void A_bundle_the_game_unloaded_is_loaded_again()
    {
        var bundle = _assets.LoadBundle("Assets/ui.bundle");
        _backend.Loads[0].Loaded = false;

        var again = _assets.LoadBundle("Assets/ui.bundle");

        Assert.NotSame(bundle, again);
        Assert.True(_backend.Loads[0].Released);
        Assert.True(again.IsLoaded);
    }

    [Fact]
    public void A_bundle_loads_from_bytes()
    {
        var bundle = _assets.LoadBundle([1, 2, 3, 4]);

        Assert.Equal("(from memory)", bundle.Name);
        Assert.Equal("4 bytes", _backend.Loads[0].Source);
        Assert.Equal("Panel", bundle.Load<Asset>("Panel").Name);
    }

    [Fact]
    public void Failures_say_what_to_check()
    {
        Assert.Throws<FileNotFoundException>(() => _assets.LoadBundle("Assets/missing.bundle"));

        _backend.Fails = true;
        var failed = Assert.Throws<InvalidOperationException>(() => _assets.LoadBundle("Assets/ui.bundle"));
        Assert.Contains("Unity could not load the asset bundle ui.bundle", failed.Message);
        Assert.Contains("(6000.0.58f2)", failed.Message);
    }

    [Fact]
    public void A_scenes_bundle_has_no_assets_to_load()
    {
        _backend.Scenes = true;
        var bundle = _assets.LoadBundle("Assets/ui.bundle");

        Assert.Contains("is a scene bundle", Assert.Throws<InvalidOperationException>(() => bundle.Load<Asset>("Panel")).Message);
    }

    [Fact]
    public void Bundles_wait_for_the_game_and_the_main_thread()
    {
        _runtime.GameReady = false;
        Assert.Contains("OnGameReady", Assert.Throws<InvalidOperationException>(() => _assets.LoadBundle("Assets/ui.bundle")).Message);

        _runtime.GameReady = true;
        Exception? offThread = null;
        var thread = new Thread(() => offThread = Record.Exception(() => _assets.LoadBundle("Assets/ui.bundle")));
        thread.Start();
        thread.Join();
        Assert.Contains("main thread", Assert.IsType<InvalidOperationException>(offThread).Message);

        _backend.Unsupported = "no bundles here";
        Assert.Equal("no bundles here", Assert.Throws<NotSupportedException>(() => _assets.LoadBundle("Assets/ui.bundle")).Message);
        Assert.Empty(_backend.Loads);
    }

    [Fact]
    public void Outside_a_game_there_are_no_bundles() =>
        Assert.Throws<InvalidOperationException>(() => new ModAssets(null, _modDirectory, null).LoadBundle("Assets/ui.bundle"));
}
