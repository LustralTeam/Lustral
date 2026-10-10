using System.Reflection;
using Lustral.Core.Mods;

namespace Lustral.Core.Loading;

/// <summary>
/// Mono implementation. Unity's Mono has no load contexts, so all mods share the game's domain. A reference that the
/// runtime cannot resolve is resolved to an already loaded assembly with that name, then to a library's export, then to
/// an assembly in the requesting mod's folder (or in any mod's folder, in load order, if Mono does not identify the
/// requester). Mods cannot use private versions of a library: the first copy loaded is used by all mods.
/// </summary>
public sealed class DomainModAssemblyLoader : IModAssemblyLoader
{
    private readonly ILog _log;
    private readonly List<DiscoveredMod> _mods = [];
    private readonly Dictionary<string, string> _exports = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Assembly, DiscoveredMod> _owners = new();
    private readonly object _lock = new();

    public DomainModAssemblyLoader(ILog log)
    {
        _log = log;
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
    }

    public void Register(DiscoveredMod mod)
    {
        lock (_lock)
        {
            _mods.Add(mod);
            foreach (var export in mod.Manifest.Exports)
            {
                if (_exports.TryGetValue(export, out var existing))
                    _log.Warn($"{mod.Id} exports {export}, which {existing} already exports; dependents use the first export");
                else
                    _exports.Add(export, Path.Combine(mod.Directory, export + ".dll"));
            }
        }
    }

    public Assembly Load(DiscoveredMod mod, string path)
    {
        var assembly = Assembly.LoadFrom(path);
        lock (_lock)
            _owners[assembly] = mod;
        return assembly;
    }

    // Raised for every failed lookup in the game's domain, not only Lustral's, so it must be fast and silent.
    private Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        if (name is null || name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                return loaded;
        }

        lock (_lock)
        {
            var requester = args.RequestingAssembly is { } requesting && _owners.TryGetValue(requesting, out var owner) ? owner : null;

            if (_exports.TryGetValue(name, out var exportPath) && File.Exists(exportPath))
                return LoadOwned(exportPath);

            foreach (var mod in requester is null ? _mods : [requester])
            {
                var candidate = Path.Combine(mod.Directory, name + ".dll");
                if (File.Exists(candidate))
                    return LoadOwned(candidate);
            }

            if (requester is not null)
                _log.Error($"{requester.Id} cannot find assembly {name}: it is not loaded, not exported by a dependency, and not in {requester.Directory}");
            return null;
        }
    }

    private Assembly LoadOwned(string path)
    {
        var assembly = Assembly.LoadFrom(path);
        var directory = Path.GetDirectoryName(path);
        var owner = _mods.FirstOrDefault(m => string.Equals(m.Directory, directory, StringComparison.OrdinalIgnoreCase));
        if (owner is not null)
            _owners[assembly] = owner;
        return assembly;
    }
}
