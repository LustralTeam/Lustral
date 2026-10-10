using System.Reflection;
using System.Runtime.Loader;
using Lustral.Core.Mods;

namespace Lustral.Core.Loading;

/// <summary>
/// A mod's load context, which exists for the entire session. Assemblies are resolved in this order: shared assemblies
/// from the default context, exports of the mod's direct and transitive dependencies, and the mod's own folder.
/// </summary>
internal sealed class ModLoadContext : AssemblyLoadContext
{
    private readonly DiscoveredMod _mod;
    private readonly IReadOnlyList<ModLoadContext> _dependencies;
    private readonly Func<AssemblyName, bool> _isShared;
    private readonly HashSet<string> _exports;
    private readonly ILog _log;

    public ModLoadContext(DiscoveredMod mod, IReadOnlyList<ModLoadContext> dependencies, Func<AssemblyName, bool> isShared, ILog log)
        : base($"Lustral mod {mod.Id}", isCollectible: false)
    {
        _mod = mod;
        _dependencies = dependencies;
        _isShared = isShared;
        _exports = new HashSet<string>(mod.Manifest.Exports, StringComparer.OrdinalIgnoreCase);
        _log = log;
        Resolving += OnResolving;
    }

    public DiscoveredMod Mod => _mod;

    public IReadOnlyList<ModLoadContext> Dependencies => _dependencies;

    public bool Exports(AssemblyName name) => name.Name is { } simple && _exports.Contains(simple);

    protected override Assembly? Load(AssemblyName name)
    {
        if (_isShared(name))
            return Default.LoadFromAssemblyName(name);

        foreach (var dependency in _dependencies)
        {
            if (dependency.Exports(name))
                return dependency.LoadFromAssemblyName(name);
        }

        var path = Path.Combine(_mod.Directory, name.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var candidate in (ReadOnlySpan<string>)[unmanagedDllName, unmanagedDllName + ".dll"])
        {
            var path = Path.Combine(_mod.Directory, candidate);
            if (File.Exists(path))
                return LoadUnmanagedDllFromPath(path);
        }
        return 0;
    }

    private Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
    {
        _log.Error($"{_mod.Id} cannot find assembly {name.Name} {name.Version}: it is not shared, not exported by a dependency, and not in {_mod.Directory}");
        return null;
    }
}
