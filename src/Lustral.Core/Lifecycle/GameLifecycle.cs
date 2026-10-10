using Lustral.Core.Loading;
using Lustral.Core.Logging;

namespace Lustral.Core.Lifecycle;

/// <summary>
/// Delivers game lifecycle events to mods. Backends call it from Unity hooks on the main thread; it has no dependency on
/// Unity itself. Each mod's failures are logged under its ID and do not affect other mods or the game.
/// </summary>
public sealed class GameLifecycle(LogHub logs, IReadOnlyList<LoadedMod> mods)
{
    /// <summary>The number of errors logged per mod and event before further errors are only counted.</summary>
    private const int LoggedErrorsPerEvent = 3;

    private readonly ILog _log = logs.For("Lifecycle");
    private readonly Dictionary<(string Mod, string Event), int> _errorCounts = [];
    private readonly LoadedMod[] _withEvents = mods.Where(m => m.Events is not null).ToArray();
    private bool _gameReady;
    private bool _shutDown;

    public bool IsGameReady => _gameReady;

    /// <summary>Raised after mods have shut down, when the game is exiting normally.</summary>
    public event Action? ShutdownCompleted;

    /// <summary>Raised once, when the first scene has loaded and before mods' <see cref="Mod.OnGameReady"/>. Unity APIs can be used from this point on.</summary>
    public event Action? Ready;

    /// <summary>Called when a scene has loaded. For the first scene, all mods' <see cref="Mod.OnGameReady"/> methods are called first.</summary>
    public void SceneLoaded(SceneInfo scene)
    {
        if (!_gameReady)
        {
            _gameReady = true;
            _log.Info($"Game ready: first scene {scene}");
            try
            {
                Ready?.Invoke();
            }
            catch (Exception e)
            {
                _log.Error("Lustral's game-ready processing failed", e);
            }
            foreach (var mod in mods)
            {
                if (mod.Instance is { } instance)
                    Run(mod, nameof(Mod.OnGameReady), instance.OnGameReady);
            }
        }

        _log.Debug($"Scene loaded: {scene}");
        foreach (var mod in _withEvents)
            mod.Events!.RaiseSceneLoaded(scene, e => Report(mod, nameof(GameEvents.SceneLoaded), e));
    }

    /// <summary>Called when a scene has been unloaded. Ignored before the game is ready, because Unity unloads a placeholder scene during startup.</summary>
    public void SceneUnloaded(SceneInfo scene)
    {
        if (!_gameReady)
        {
            _log.Debug($"Scene unloaded before the game was ready, not forwarded: {scene}");
            return;
        }

        _log.Debug($"Scene unloaded: {scene}");
        foreach (var mod in _withEvents)
            mod.Events!.RaiseSceneUnloaded(scene, e => Report(mod, nameof(GameEvents.SceneUnloaded), e));
    }

    /// <summary>Unity's scaled game time in seconds, for coroutines' <see cref="Wait.Seconds"/>. Set by the backend once the engine is running.</summary>
    public void UseGameClock(Func<double> clock)
    {
        if (Coroutines is not null)
            Coroutines.GameTime = clock;
    }

    /// <summary>The mods' coroutines, run after each frame phase's handlers.</summary>
    internal CoroutineScheduler? Coroutines { get; init; }

    /// <summary>Whether any mod has OnGUI handlers; Unity's GUI is called only in that case.</summary>
    public bool WantsGui => _withEvents.Any(m => m.Events!.HasGuiHandlers);

    /// <summary>Called once per frame. Only mods with Update handlers are invoked.</summary>
    public void Update()
    {
        foreach (var mod in _withEvents)
        {
            if (mod.Events!.HasUpdateHandlers)
                mod.Events.RaiseUpdate(e => Report(mod, nameof(GameEvents.Update), e));
        }
        TickCoroutines(CoroutineScheduler.Phase.Update);
    }

    /// <summary>Called once per physics step.</summary>
    public void FixedUpdate()
    {
        foreach (var mod in _withEvents)
        {
            if (mod.Events!.HasFixedUpdateHandlers)
                mod.Events.RaiseFixedUpdate(e => Report(mod, nameof(GameEvents.FixedUpdate), e));
        }
        TickCoroutines(CoroutineScheduler.Phase.FixedUpdate);
    }

    /// <summary>Called once per frame, after all Update methods.</summary>
    public void LateUpdate()
    {
        foreach (var mod in _withEvents)
        {
            if (mod.Events!.HasLateUpdateHandlers)
                mod.Events.RaiseLateUpdate(e => Report(mod, nameof(GameEvents.LateUpdate), e));
        }
        TickCoroutines(CoroutineScheduler.Phase.LateUpdate);
    }

    private void TickCoroutines(CoroutineScheduler.Phase phase)
    {
        try
        {
            Coroutines?.Tick(phase);
        }
        catch (Exception e)
        {
            _log.Error("Running coroutines failed", e); // Coroutine failures are attributed to their mods; this one is Lustral's.
        }
    }

    /// <summary>Called once per GUI event.</summary>
    public void Gui()
    {
        foreach (var mod in _withEvents)
        {
            if (mod.Events!.HasGuiHandlers)
                mod.Events.RaiseGui(e => Report(mod, nameof(GameEvents.OnGUI), e));
        }
    }

    /// <summary>Called when the game is exiting. Calls each mod's <see cref="Mod.OnShutdown"/> once, in reverse load order.</summary>
    public void Shutdown()
    {
        if (_shutDown)
            return;
        _shutDown = true;
        _log.Info("Game is exiting");
        for (var i = mods.Count - 1; i >= 0; i--)
        {
            if (mods[i].Instance is { } instance)
                Run(mods[i], nameof(Mod.OnShutdown), instance.OnShutdown);
        }
        ShutdownCompleted?.Invoke();
    }

    private void Run(LoadedMod mod, string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Report(mod, name, e);
        }
    }

    private void Report(LoadedMod mod, string name, Exception e)
    {
        var key = (mod.Mod.Id, name);
        _errorCounts.TryGetValue(key, out var count);
        _errorCounts[key] = ++count;
        if (count <= LoggedErrorsPerEvent)
            _log.Error($"{mod.Mod.Id} threw in {name}", e);
        if (count == LoggedErrorsPerEvent)
            _log.Warn($"{mod.Mod.Id} repeatedly throws in {name}; further errors from it in {name} are not logged");
    }
}
