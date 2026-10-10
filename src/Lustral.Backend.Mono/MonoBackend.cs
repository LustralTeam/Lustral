using System.Reflection;
using Lustral.Core.Assets;
using Lustral.Core.Lifecycle;
using Lustral.Core.Loading;
using Lustral.Core.Logging;
using Lustral.Core.Startup;

namespace Lustral.Backend.Mono;

/// <summary>
/// On Unity's Mono, mods run in the game's domain and use the actual game types, so they reference the game's assemblies
/// directly and HarmonyX patches them without additional support. There are no load contexts, so mods share a single
/// copy of each library.
/// </summary>
public sealed class MonoBackend(GamePaths paths, LogHub logs) : IBackend
{
    // Unity loads the game's scripts after initializing the engine and registering its native functions.
    private const string GameScripts = "Assembly-CSharp";

    private readonly ILog _log = logs.For("Mono");
    private string? _managed;

    public string Name => "Mono";

    public ScriptingBackend Kind => ScriptingBackend.Mono;

    public void Start()
    {
        var runtime = Type.GetType("Mono.Runtime")?
            .GetMethod("GetDisplayName", BindingFlags.NonPublic | BindingFlags.Static)?
            .Invoke(null, null) as string;
        _managed = Directory.GetDirectories(paths.GameDirectory, "*_Data")
            .Select(data => Path.Combine(data, "Managed"))
            .FirstOrDefault(Directory.Exists);
        _log.Info($"Mono {runtime ?? "(version unknown)"}; game assemblies in {_managed ?? "(not found)"}");
        // Stage 0 enabled the debugger agent before Mono started (LUSTRAL_MONO_DEBUGGER, set by a mod's debug launch profile).
        if (Environment.GetEnvironmentVariable("LUSTRAL_MONO_DEBUGGER") is { Length: > 0 } debugger)
            _log.Info($"Mono's debugger agent is enabled ({debugger}); attach Visual Studio Tools for Unity (Debug > Attach Unity Debugger), Rider or dnSpy");
        _log.Debug($"Lustral assemblies in {paths.CoreDirectory}");
    }

    /// <summary>
    /// Lustral starts during <c>mono_jit_init</c>, before the engine is initialized. Mods are loaded when Unity loads the
    /// game's scripts: patching earlier causes Mono to resolve the engine functions called by a patched method before
    /// Unity has registered them, and the failure persists (on Unity 6000.5, a <c>MissingMethodException</c> from
    /// <c>Application.set_targetFrameRate</c>). This is still before the first scene and before any game code runs.
    /// </summary>
    public void WhenReadyForMods(Action loadMods)
    {
        if (_managed is null || !File.Exists(Path.Combine(_managed, GameScripts + ".dll")) ||
            AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == GameScripts))
        {
            loadMods();
            return;
        }

        _log.Debug($"Mods will load when Unity loads {GameScripts}");
        AssemblyLoadEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (e.LoadedAssembly.GetName().Name != GameScripts)
                return;
            AppDomain.CurrentDomain.AssemblyLoad -= handler;
            loadMods();
        };
        AppDomain.CurrentDomain.AssemblyLoad += handler;
    }

    public IModAssemblyLoader CreateAssemblyLoader() => new DomainModAssemblyLoader(logs.For("Core"));

    public IAssetBundleBackend CreateAssetBundles() => new MonoAssetBundles();

    public void InstallLifecycle(GameLifecycle lifecycle) => new MonoUnityHooks(lifecycle, logs.For("Unity")).Install();

    // Called at the first scene, by which time Lustral's quit hook has also used Application.
    public string? ReadGameVersion() => UnityEngine.Application.version;
}
