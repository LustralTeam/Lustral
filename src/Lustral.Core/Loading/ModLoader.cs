using System.Reflection;
using Lustral.Core.Assets;
using Lustral.Core.Config;
using Lustral.Core.Logging;
using Lustral.Core.Mods;

namespace Lustral.Core.Loading;

/// <param name="Events">The mod's events; null for a library without an entry point.</param>
/// <param name="Harmony">The mod's Harmony instance, if the mod used <see cref="Mod.Harmony"/>.</param>
public sealed record LoadedMod(DiscoveredMod Mod, Assembly? Assembly, Mod? Instance, GameEvents? Events = null, HarmonyLib.Harmony? Harmony = null);

/// <summary>
/// Loads mods in the planned order through the backend's <see cref="IModAssemblyLoader"/> and calls
/// <see cref="Mod.OnLoad"/>. A failure disables the affected mod and its dependents; all other mods are still loaded.
/// </summary>
/// <param name="configDirectory">The folder for mods' settings files (<c>Lustral/config</c>).</param>
/// <param name="game">The game information provided to mods (<see cref="Mod.Game"/>).</param>
/// <param name="dataDirectory">The parent folder of each mod's <see cref="ModInfo.DataDirectory"/> (<c>Lustral/data</c>); null for none.</param>
public sealed class ModLoader(LogHub logs, IModAssemblyLoader assemblies, string configDirectory, GameInfo? game = null, string? dataDirectory = null)
{
    private readonly ILog _log = logs.For("Core");
    private readonly GameInfo _game = game ?? new GameInfo("Unknown game", null, null, null, ScriptingBackend.Mono, configDirectory);

    /// <summary>The mods loaded so far, exposed to mods as <see cref="Mod.Mods"/>.</summary>
    public LoadedMods Loaded { get; } = new();

    /// <summary>The scheduler for mods' coroutines (<see cref="Mod.Coroutines"/>), driven by GameLifecycle.</summary>
    internal CoroutineScheduler Coroutines { get; init; } = new();

    /// <summary>Unity's asset bundle functions, for <see cref="Mod.Assets"/>; null when not running in a game.</summary>
    internal AssetBundles? AssetBundles { get; init; }

    public IReadOnlyList<LoadedMod> Load(LoadPlan plan)
    {
        var failed = new HashSet<string>(StringComparer.Ordinal);
        var loaded = new List<LoadedMod>();

        foreach (var problem in plan.Disabled)
            _log.Warn($"Not loading {problem}");
        foreach (var warning in plan.Warnings)
            _log.Warn(warning.ToString());

        foreach (var mod in plan.Ordered)
        {
            var failedDependency = mod.Manifest.Dependencies.Keys.FirstOrDefault(failed.Contains);
            if (failedDependency is not null)
            {
                _log.Error($"Not loading {mod.Id}: requires {failedDependency}, which failed to load");
                failed.Add(mod.Id);
                continue;
            }

            try
            {
                if (mod.Manifest.Color is { } color && RichText.TryParseColor(color, out var rgb))
                    ConsoleLogSink.SetSourceColor(mod.Id, rgb);
                assemblies.Register(mod);
                loaded.Add(LoadOne(mod));
                Loaded.Add(Describe(mod));
            }
            catch (LoadFailure e)
            {
                failed.Add(mod.Id);
                _log.Error($"{mod.Id} failed to load: {e.Message}", e.InnerException);
            }
            catch (Exception e)
            {
                failed.Add(mod.Id);
                _log.Error($"{mod.Id} failed to load", Unwrap(e));
            }
        }

        _log.Info($"Loaded {loaded.Count} of {plan.Ordered.Count + plan.Disabled.Count} mods");
        return loaded;
    }

    private LoadedMod LoadOne(DiscoveredMod mod)
    {
        var manifest = mod.Manifest;
        if (manifest.Assembly is null)
        {
            _log.Info($"Loaded library {Title(manifest)}");
            return new LoadedMod(mod, null, null);
        }

        var path = Path.Combine(mod.Directory, manifest.Assembly);
        if (!File.Exists(path))
            throw new FileNotFoundException($"{manifest.Assembly} was not found in {mod.Directory}", path);

        var assembly = assemblies.Load(mod, path);
        var entryTypes = LoadableTypes(assembly)
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(Mod).IsAssignableFrom(t))
            .ToList();

        if (entryTypes.Count > 1)
            throw new InvalidOperationException($"{manifest.Assembly} has {entryTypes.Count} Mod classes ({string.Join(", ", entryTypes.Select(t => t.FullName))}); a mod must have exactly one");
        if (entryTypes.Count == 0)
        {
            if (manifest.Kind == ModKind.Mod)
                throw new InvalidOperationException($"{manifest.Assembly} has no class deriving from Lustral.Mod");
            _log.Info($"Loaded library {Title(manifest)}");
            return new LoadedMod(mod, assembly, null);
        }

        var context = new LoadedModContext(mod, Describe(mod), logs.For(mod.Id), configDirectory, _game, Loaded, Coroutines, AssetBundles);
        Mod instance;
        try
        {
            // Makes Lustral's services available from the mod's constructor onward (Mod reads ModContext.Creating).
            ModContext.Creating = context;
            try
            {
                instance = (Mod)Activator.CreateInstance(entryTypes[0])!;
            }
            finally
            {
                ModContext.Creating = null;
            }
            instance.OnLoad();
        }
        catch (Exception e)
        {
            // A mod that failed partway may already have applied patches; remove them.
            if (context.HarmonyIfCreated is { } harmony && harmony.GetPatchedMethods().Any())
            {
                harmony.UnpatchSelf();
                _log.Info($"Removed {mod.Id}'s Harmony patches");
            }
            if (IsHarmonyFailure(e) && MissingPatchTargets(assembly) is { Count: > 0 } missing)
                throw new LoadFailure(string.Join("; ", missing), Unwrap(e));
            throw;
        }
        _log.Info($"Loaded {Title(manifest)}");
        return new LoadedMod(mod, assembly, instance, context.Events, context.HarmonyIfCreated);
    }

    // "Better HUD 1.2.0 (sxxr0.betterhud) by sxxr0"
    private static string Title(ModManifest manifest) =>
        $"{manifest.Name} {manifest.Version} ({manifest.Id}){(manifest.Authors.Count > 0 ? " by " + string.Join(", ", manifest.Authors) : "")}";

    private ModInfo Describe(DiscoveredMod mod) =>
        new(mod.Id, mod.Manifest.Name, mod.Manifest.Version.ToString(), mod.Directory, mod.Manifest.Authors, mod.Manifest.Description,
            dataDirectory is null ? null : Path.Combine(dataDirectory, mod.Id));

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            return e.Types.OfType<Type>();
        }
    }

    private static Exception Unwrap(Exception e) =>
        e is TargetInvocationException { InnerException: { } inner } ? inner : e;

    private static bool IsHarmonyFailure(Exception? e)
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is HarmonyLib.HarmonyException)
                return true;
        }
        return false;
    }

    // Harmony reports a missing target without naming it ("Undefined target method for patch method ...Postfix()"), and
    // targets most often disappear when a game update renames or removes a method. This names them, for patch classes
    // whose [HarmonyPatch] attributes specify the target method.
    private static List<string> MissingPatchTargets(Assembly assembly)
    {
        var missing = new List<string>();
        foreach (var type in LoadableTypes(assembly))
        {
            try
            {
                var attributes = HarmonyLib.HarmonyMethodExtensions.GetFromType(type);
                if (attributes.Count == 0 || type.GetMethod("TargetMethod", HarmonyLib.AccessTools.all) is not null ||
                    type.GetMethod("TargetMethods", HarmonyLib.AccessTools.all) is not null)
                    continue;
                var target = HarmonyLib.HarmonyMethod.Merge(attributes);
                if (target.declaringType is null || target.methodName is null || target.methodType is not (null or HarmonyLib.MethodType.Normal))
                    continue;
                if (HarmonyLib.AccessTools.Method(target.declaringType, target.methodName, target.argumentTypes) is null)
                    missing.Add($"{type.Name} patches {target.declaringType.Name}.{target.methodName}, which does not exist in this version of the game");
            }
            catch (Exception)
            {
                // An ambiguous or unloadable target; Harmony's exception describes it.
            }
        }
        return missing;
    }

    /// <summary>A mod's failure, explained in <see cref="Exception.Message"/>; the original exception is the inner one.</summary>
    private sealed class LoadFailure(string message, Exception inner) : Exception(message, inner);

    private sealed class LoadedModContext(
        DiscoveredMod mod, ModInfo info, ILog log, string configDirectory, GameInfo game, LoadedMods mods, CoroutineScheduler coroutines, AssetBundles? assetBundles)
        : ModContext
    {
        private ModConfig? _config;
        private HarmonyLib.Harmony? _harmony;

        public override ModInfo Info { get; } = info;
        public override GameInfo Game => game;
        public override LoadedMods Mods => mods;
        public override Coroutines Coroutines { get; } = new(coroutines, (name, e) => log.Error($"Coroutine {name} threw an exception and was stopped", e));
        public override ModAssets Assets { get; } = new(assetBundles, mod.Directory, game.UnityVersion);
        public override ILog Log => log;
        public override GameEvents Events { get; } = new();
        public override ModConfig Config => _config ??= new TomlModConfig(Path.Combine(configDirectory, $"{mod.Id}.toml"), mod.Manifest.Name, log);
        public override HarmonyLib.Harmony Harmony => _harmony ??= new HarmonyLib.Harmony(mod.Id);
        public HarmonyLib.Harmony? HarmonyIfCreated => _harmony;
    }
}
