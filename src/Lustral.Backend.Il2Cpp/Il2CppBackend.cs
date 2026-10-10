using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using AssetRipper.Primitives;
using Il2CppInterop.Common;
using Il2CppInterop.HarmonySupport;
using Il2CppInterop.Runtime.Startup;
using Lustral.Core;
using Lustral.Core.Assets;
using Lustral.Core.Lifecycle;
using Lustral.Core.Loading;
using Lustral.Core.Logging;
using Lustral.Core.Startup;
using Lustral.Interop;
using MonoMod.RuntimeDetour;

namespace Lustral.Backend.Il2Cpp;

/// <summary>
/// Initializes Il2CppInterop V2 in an IL2CPP game, after <c>il2cpp_init</c> and before the first scene.
/// </summary>
public sealed class Il2CppBackend : IBackend
{
    private readonly GamePaths _paths;
    private readonly GameLayout _game;
    private readonly LogHub _logs;
    private readonly ILog _log;
    private readonly object _resolveLock = new();

    public Il2CppBackend(GamePaths paths, LogHub logs)
    {
        _paths = paths;
        _game = GameLayout.FromExecutable(paths.ExecutablePath);
        _logs = logs;
        _log = logs.For("Il2Cpp");
    }

    public static bool IsIl2CppGame(string gameDirectory) => File.Exists(Path.Combine(gameDirectory, "GameAssembly.dll"));

    public string Name => "IL2CPP (Il2CppInterop V2)";

    public ScriptingBackend Kind => ScriptingBackend.Il2Cpp;

    /// <summary>
    /// Each mod has its own load context. The framework, Lustral's assemblies (Api, HarmonyX, MonoMod) and the generated
    /// interop assemblies are loaded from the default context, so that all mods share the same game types. A mod's
    /// injected IL2CPP classes are registered when its assembly loads.
    /// </summary>
    public IModAssemblyLoader CreateAssemblyLoader()
    {
        var shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in (ReadOnlySpan<string>)[RuntimeEnvironment.GetRuntimeDirectory(), _paths.CoreDirectory, _game.InteropDirectory])
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.dll"))
                shared.Add(Path.GetFileNameWithoutExtension(file));
        }
        return new Il2CppModAssemblies(new ContextModAssemblyLoader(name => name.Name is { } simple && shared.Contains(simple), _logs.For("Core")), _logs.For("Il2Cpp"));
    }

    // il2cpp_init has returned: the runtime is ready, and the game's code has not run yet.
    public void WhenReadyForMods(Action loadMods) => loadMods();

    public IAssetBundleBackend CreateAssetBundles() => new Il2CppAssetBundles();

    public void InstallLifecycle(GameLifecycle lifecycle) => new Il2CppUnityHooks(lifecycle, _logs.For("Unity")).Install();

    // Application.version through the interop assembly; builds that never read it do not include the property.
    public string? ReadGameVersion()
    {
        var value = Type.GetType("UnityEngine.Application, UnityEngine.CoreModule")?
            .GetProperty("version", BindingFlags.Public | BindingFlags.Static)?
            .GetValue(null);
        if (value is null or string)
            return (string?)value;
        // An Il2CppSystem.String, which is converted to string.
        var convert = value.GetType().GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == typeof(string) && m.GetParameters() is [{ } parameter] && parameter.ParameterType.IsInstanceOfType(value));
        return convert?.Invoke(null, [value]) as string ?? value.ToString();
    }

    public void Start()
    {
        var stopwatch = Stopwatch.StartNew();
        var manifest = EnsureInteropCache();
        AssemblyLoadContext.Default.Resolving += ResolveInterop;
        Il2CppInteropFixes.Apply(new HarmonyLib.Harmony("lustral.il2cppinterop-fixes"), _logs.For("Il2CppInterop"));

        Il2CppInteropRuntime.Create(new RuntimeConfiguration
            {
                UnityVersion = UnityVersion.Parse(manifest.UnityVersion),
                DetourProvider = new MonoModDetourProvider(),
            })
            .AddLogger(new HubLogger(_logs, "Il2CppInterop"))
            .Start();
        _log.Info($"Il2CppInterop {InteropManifest.CurrentIl2CppInteropVersion} started for Unity {manifest.UnityVersion}");

        // HarmonyX's hooks obtain their detour factory from MonoMod.RuntimeDetour's global context. This factory routes
        // patches on interop methods to native detours on the IL2CPP method, and all others to the default factory. It
        // must not become DetourFactory.Current, which Il2CppInteropDetour uses for its managed part.
        DetourContext.SetGlobalContext(new DetourFactoryContext(new Il2CppInteropDetourFactory()));
        _log.Info($"IL2CPP backend ready in {stopwatch.ElapsedMilliseconds} ms");
    }

    // Generates the interop assemblies first if they are missing or were generated for another build of the game.
    private InteropManifest EnsureInteropCache()
    {
        var staleReason = StaleReason(out var manifest);
        var generated = staleReason is not null;
        if (staleReason is not null)
        {
            InteropGeneration.Run(_game, staleReason, _log, _logs.For("Generator"));
            staleReason = StaleReason(out manifest);
            if (staleReason is not null)
                throw new StartupException($"Interop assemblies are still out of date after generating them: {staleReason}");
        }

        var unstripped = manifest!.Unstripped ? ", unstripped" : manifest.UnstripProblem is { } problem ? $", not unstripped: {problem}" : ", not unstripped";
        var details = $"{unstripped.Substring(2)}{(manifest.ReadyToRun ? ", precompiled" : "")}";
        _log.Info(generated
            ? $"Interop assemblies are ready ({details})"
            : $"Interop cache is up to date (generated {manifest.CreatedUtc:u}, {details})");
        return manifest;
    }

    private string? StaleReason(out InteropManifest? manifest)
    {
        manifest = InteropManifest.TryRead(_game.InteropDirectory);
        return manifest is null ? "none yet" : manifest.StaleReason(_game, InteropManifest.CurrentIl2CppInteropVersion);
    }

    private Assembly? ResolveInterop(AssemblyLoadContext context, AssemblyName name)
    {
        var path = Path.Combine(_game.InteropDirectory, name.Name + ".dll");
        if (!File.Exists(path))
            return null;

        // Two threads can request the same assembly concurrently; the second must receive the first one's copy.
        lock (_resolveLock)
        {
            foreach (var loaded in context.Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                    return loaded;
            }
            return context.LoadFromAssemblyPath(path);
        }
    }
}
