using Lustral.Core.Lifecycle;
using Lustral.Core.Loading;
using Lustral.Core.Logging;
using Lustral.Core.Mods;

namespace Lustral.Core.Tests;

public class GameLifecycleTests
{
    [Fact]
    public void Ready_fires_once_before_mods_are_told_the_game_is_ready()
    {
        var lifecycle = Lifecycle(Loaded("a.mod", new RecordingMod(_calls, "a"), new GameEvents()));
        lifecycle.Ready += () => _calls.Add("ready");

        lifecycle.SceneLoaded(new SceneInfo("Boot", 0));
        lifecycle.SceneLoaded(new SceneInfo("Menu", 1));

        Assert.Equal(["ready", "a.ready"], _calls);
    }

    [Fact]
    public void A_failing_Ready_handler_doesnt_keep_mods_from_their_OnGameReady()
    {
        var lifecycle = Lifecycle(Loaded("a.mod", new RecordingMod(_calls, "a"), new GameEvents()));
        lifecycle.Ready += () => throw new InvalidOperationException("broken");

        lifecycle.SceneLoaded(new SceneInfo("Boot", 0));

        Assert.Equal(["a.ready"], _calls);
    }

    private readonly List<string> _calls = [];
    private readonly MemorySink _log = new();

    private sealed class RecordingMod(List<string> calls, string name) : Mod
    {
        public override void OnGameReady() => calls.Add($"{name}.ready");
        public override void OnShutdown() => calls.Add($"{name}.shutdown");
    }

    private sealed class ThrowingMod : Mod
    {
        public override void OnGameReady() => throw new InvalidOperationException("not ready");
    }

    private static LoadedMod Loaded(string id, Mod mod, GameEvents events) => new(
        new DiscoveredMod(new ModManifest { Id = id, Name = id, Version = SemanticVersion.Parse("1.0.0"), Assembly = id + ".dll" }, id),
        null,
        mod,
        events);

    private GameLifecycle Lifecycle(params LoadedMod[] mods)
    {
        var hub = new LogHub { MinimumLevel = LogLevel.Debug };
        hub.AddSink(_log);
        return new GameLifecycle(hub, mods);
    }

    [Fact]
    public void The_first_scene_runs_OnGameReady_once_before_any_SceneLoaded_handler()
    {
        var events = new GameEvents();
        events.SceneLoaded += scene => _calls.Add($"loaded {scene.Name}");
        var lifecycle = Lifecycle(Loaded("a.mod", new RecordingMod(_calls, "a"), events));

        lifecycle.SceneLoaded(new SceneInfo("Boot", 0));
        lifecycle.SceneLoaded(new SceneInfo("Menu", 1));

        Assert.Equal(["a.ready", "loaded Boot", "loaded Menu"], _calls);
        Assert.True(lifecycle.IsGameReady);
    }

    [Fact]
    public void Scene_unloads_before_the_game_is_ready_are_not_forwarded()
    {
        var events = new GameEvents();
        events.SceneUnloaded += scene => _calls.Add($"unloaded {scene.Name}");
        var lifecycle = Lifecycle(Loaded("a.mod", new RecordingMod(_calls, "a"), events));

        // What Unity 2022.3 does at startup: unload a placeholder scene, then load the first real one.
        lifecycle.SceneUnloaded(new SceneInfo("", -1));
        lifecycle.SceneLoaded(new SceneInfo("Boot", 0));
        lifecycle.SceneUnloaded(new SceneInfo("Boot", 0));

        Assert.Equal(["a.ready", "unloaded Boot"], _calls);
    }

    [Fact]
    public void A_mod_that_throws_doesn_t_stop_the_others()
    {
        var lifecycle = Lifecycle(
            Loaded("bad.mod", new ThrowingMod(), new GameEvents()),
            Loaded("good.mod", new RecordingMod(_calls, "good"), new GameEvents()));

        lifecycle.SceneLoaded(new SceneInfo("Boot", 0));

        Assert.Equal(["good.ready"], _calls);
        Assert.Contains(_log.Entries, e => e.Message == "bad.mod threw in OnGameReady" && e.Exception?.Message == "not ready");
    }

    [Fact]
    public void FixedUpdate_LateUpdate_and_OnGUI_reach_their_subscribers()
    {
        var events = new GameEvents();
        var calls = new List<string>();
        var lifecycle = Lifecycle(Loaded("a.mod", new RecordingMod(_calls, "a"), events));
        Assert.False(lifecycle.WantsGui);

        events.FixedUpdate += () => calls.Add("fixed");
        events.LateUpdate += () => calls.Add("late");
        events.OnGUI += () => calls.Add("gui");
        lifecycle.FixedUpdate();
        lifecycle.Update();
        lifecycle.LateUpdate();
        lifecycle.Gui();

        Assert.True(lifecycle.WantsGui);
        Assert.Equal(["fixed", "late", "gui"], calls);
    }

    [Fact]
    public void Update_reaches_only_subscribers_and_repeated_errors_are_capped()
    {
        var quiet = new GameEvents();
        var noisy = new GameEvents();
        var counting = new GameEvents();
        var frames = 0;
        noisy.Update += () => throw new InvalidOperationException("every frame");
        counting.Update += () => frames++;
        var lifecycle = Lifecycle(
            Loaded("quiet.mod", new RecordingMod(_calls, "quiet"), quiet),
            Loaded("noisy.mod", new RecordingMod(_calls, "noisy"), noisy),
            Loaded("counting.mod", new RecordingMod(_calls, "counting"), counting));

        for (var i = 0; i < 10; i++)
            lifecycle.Update();

        Assert.Equal(10, frames);
        // Three errors are logged, with identical ones written as a count, followed by a line stating that further errors
        // are not logged.
        Assert.Equal(
            ["noisy.mod threw in Update", "(repeated 2 more times)", "noisy.mod repeatedly throws in Update; further errors from it in Update are not logged"],
            _log.Entries.Where(e => e.Level >= LogLevel.Warn).Select(e => e.Message));
    }

    [Fact]
    public void Shutdown_runs_once_in_reverse_load_order()
    {
        var lifecycle = Lifecycle(
            Loaded("lib.mod", new RecordingMod(_calls, "lib"), new GameEvents()),
            Loaded("app.mod", new RecordingMod(_calls, "app"), new GameEvents()));

        lifecycle.Shutdown();
        lifecycle.Shutdown();

        Assert.Equal(["app.shutdown", "lib.shutdown"], _calls);
    }
}
