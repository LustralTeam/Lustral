using System.Reflection;
using Lustral.Core.Mods;

namespace Lustral.Core.Loading;

/// <summary>
/// CoreCLR implementation: each mod has its own <see cref="ModLoadContext"/>, so that mods can ship different versions of
/// a private library.
/// </summary>
/// <param name="isShared">Identifies assemblies that all mods must load from the default context (Lustral's and the game's).</param>
public sealed class ContextModAssemblyLoader(Func<AssemblyName, bool> isShared, ILog log) : IModAssemblyLoader
{
    private readonly Dictionary<string, ModLoadContext> _contexts = new(StringComparer.Ordinal);

    public void Register(DiscoveredMod mod) =>
        _contexts.Add(mod.Id, new ModLoadContext(mod, TransitiveDependencies(mod), isShared, log));

    public Assembly Load(DiscoveredMod mod, string path) => _contexts[mod.Id].LoadFromAssemblyPath(path);

    // Direct and transitive dependencies, including optional dependencies that are being loaded (registered before their
    // dependents).
    private List<ModLoadContext> TransitiveDependencies(DiscoveredMod mod)
    {
        var result = new List<ModLoadContext>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(DependencyIds(mod));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id))
                continue;
            var context = _contexts[id];
            result.Add(context);
            foreach (var next in DependencyIds(context.Mod))
                queue.Enqueue(next);
        }
        return result;
    }

    private IEnumerable<string> DependencyIds(DiscoveredMod mod) =>
        mod.Manifest.Dependencies.Keys
            .Concat(mod.Manifest.OptionalDependencies.Keys.Where(_contexts.ContainsKey))
            .OrderBy(k => k, StringComparer.Ordinal);
}
