namespace Lustral.Core.Mods;

/// <param name="Ordered">The mods to load, in order.</param>
/// <param name="Disabled">Mods that will not be loaded, with the reasons.</param>
/// <param name="Warnings">Mods that are loaded with a warning: a requirement that could not be checked, or one that the user overrode.</param>
public sealed record LoadPlan(IReadOnlyList<DiscoveredMod> Ordered, IReadOnlyList<ModProblem> Disabled, IReadOnlyList<ModProblem> Warnings)
{
    public LoadPlan(IReadOnlyList<DiscoveredMod> ordered, IReadOnlyList<ModProblem> disabled)
        : this(ordered, disabled, [])
    {
    }
}

public static class ModResolver
{
    /// <summary>
    /// Determines which mods are loaded and in what order. A mod is not loaded if the user disabled it, its
    /// <c>[requires]</c> section does not match the environment, a dependency is missing or will not be loaded, an
    /// installed optional dependency has an unsupported version, or a mod it is incompatible with is loaded (an
    /// incompatibility only excludes the mod that declares it). Dependencies and optional dependencies are loaded before
    /// their dependents; otherwise, mods are ordered by load order, then by ID, so that the order is deterministic. Each
    /// problem disables only the mods it affects.
    /// </summary>
    /// <param name="disabled">IDs of mods disabled in loader.toml.</param>
    /// <param name="environment">The environment that <c>[requires]</c> is checked against; null skips these checks.</param>
    /// <param name="force">IDs of mods to load regardless of their <c>[requires]</c> section and incompatibilities.</param>
    public static LoadPlan Resolve(
        IEnumerable<DiscoveredMod> mods,
        IReadOnlyCollection<string>? disabled = null,
        ModEnvironment? environment = null,
        IReadOnlyCollection<string>? force = null)
    {
        var problems = new List<ModProblem>();
        var warnings = new List<ModProblem>();
        var byId = new Dictionary<string, DiscoveredMod>(StringComparer.Ordinal);
        var forced = new HashSet<string>(force ?? [], StringComparer.Ordinal);

        foreach (var mod in mods.OrderBy(m => m.Directory, StringComparer.Ordinal))
        {
            if (byId.TryGetValue(mod.Id, out var first))
                problems.Add(new ModProblem(mod.Id, $"duplicate ID; {Path.GetFileName(first.Directory)} already uses it, so {Path.GetFileName(mod.Directory)} is ignored"));
            else
                byId.Add(mod.Id, mod);
        }

        var enabled = new Dictionary<string, DiscoveredMod>(byId, StringComparer.Ordinal);
        var turnedOff = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in (disabled ?? []).Where(enabled.ContainsKey).OrderBy(id => id, StringComparer.Ordinal))
        {
            enabled.Remove(id);
            turnedOff.Add(id);
            problems.Add(new ModProblem(id, $"disabled in {Config.LoaderConfig.FileName}"));
        }

        if (environment is not null)
        {
            foreach (var mod in enabled.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList())
            {
                var @unchecked = new List<string>();
                var reason = environment.Check(mod.Manifest, @unchecked);
                foreach (var what in @unchecked)
                    warnings.Add(new ModProblem(mod.Id, $"loading without checking {what}"));
                if (reason is null)
                    continue;
                if (forced.Contains(mod.Id))
                {
                    warnings.Add(new ModProblem(mod.Id, $"loading although it {reason} (forced in {Config.LoaderConfig.FileName})"));
                    continue;
                }
                enabled.Remove(mod.Id);
                problems.Add(new ModProblem(mod.Id, reason + environment.Remedy(mod.Manifest)));
            }
        }

        DropUnmetDependencies(enabled, byId, turnedOff, problems);
        if (DropIncompatible(enabled, forced, problems, warnings))
            DropUnmetDependencies(enabled, byId, turnedOff, problems);

        var ordered = Order(enabled, out var cycle);
        foreach (var id in cycle)
            problems.Add(new ModProblem(id, $"dependency cycle among {string.Join(", ", cycle)}"));
        return new LoadPlan(ordered, problems, warnings);
    }

    // Removes mods with missing dependencies until no further changes occur, so that the reason propagates along the
    // dependency chain.
    private static void DropUnmetDependencies(Dictionary<string, DiscoveredMod> enabled, Dictionary<string, DiscoveredMod> installed, HashSet<string> turnedOff, List<ModProblem> problems)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var mod in enabled.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList())
            {
                var reason = UnmetDependency(mod, installed, enabled, turnedOff);
                if (reason is null)
                    continue;
                enabled.Remove(mod.Id);
                problems.Add(new ModProblem(mod.Id, reason));
                changed = true;
            }
        }
        while (changed);
    }

    private static string? UnmetDependency(DiscoveredMod mod, Dictionary<string, DiscoveredMod> installed, Dictionary<string, DiscoveredMod> enabled, HashSet<string> turnedOff)
    {
        foreach (var dependency in mod.Manifest.Dependencies.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (!installed.TryGetValue(dependency.Key, out var installedMod))
                return $"requires {dependency.Key} {dependency.Value}, which is not installed";
            if (!dependency.Value.IsSatisfiedBy(installedMod.Manifest.Version))
                return $"requires {dependency.Key} {dependency.Value}, but version {installedMod.Manifest.Version} is installed";
            if (!enabled.ContainsKey(dependency.Key))
                return turnedOff.Contains(dependency.Key) ? $"requires {dependency.Key}, which is disabled" : $"requires {dependency.Key}, which will not be loaded";
        }
        foreach (var optional in mod.Manifest.OptionalDependencies.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            if (enabled.TryGetValue(optional.Key, out var present) && !optional.Value.IsSatisfiedBy(present.Manifest.Version))
                return $"supports {optional.Key} {optional.Value} as an optional dependency, but version {present.Manifest.Version} is installed";
        }
        return null;
    }

    // Removes every mod that declares an incompatibility with a mod being loaded. All mods are evaluated before any is
    // removed, so the outcome does not depend on the order of evaluation. Returns whether any mod was removed.
    private static bool DropIncompatible(Dictionary<string, DiscoveredMod> enabled, HashSet<string> forced, List<ModProblem> problems, List<ModProblem> warnings)
    {
        var dropped = new List<(string Id, string Reason)>();
        foreach (var mod in enabled.Values.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            foreach (var incompatibility in mod.Manifest.Incompatibilities.OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                if (!enabled.TryGetValue(incompatibility.Key, out var other) || !incompatibility.Value.IsSatisfiedBy(other.Manifest.Version))
                    continue;
                var reason = $"is incompatible with {other.Manifest.Name} {other.Manifest.Version} ({other.Id})";
                if (forced.Contains(mod.Id))
                    warnings.Add(new ModProblem(mod.Id, $"loading although it {reason} (forced in {Config.LoaderConfig.FileName})"));
                else
                {
                    dropped.Add((mod.Id, reason + "; remove one of them"));
                    break;
                }
            }
        }
        foreach (var (id, reason) in dropped)
        {
            enabled.Remove(id);
            problems.Add(new ModProblem(id, reason));
        }
        return dropped.Count > 0;
    }

    // Kahn's algorithm, with the ready set sorted by load order, then ID. Optional dependencies affect the order only
    // where possible: mods blocked solely by each other's optional dependencies are released in the same order, and a
    // cycle of required dependencies disables the mods in it.
    private static List<DiscoveredMod> Order(Dictionary<string, DiscoveredMod> enabled, out List<string> cycle)
    {
        var required = new Dictionary<string, int>(StringComparer.Ordinal);
        var optional = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<(string Id, bool Optional)>>(StringComparer.Ordinal);
        foreach (var mod in enabled.Values)
        {
            required[mod.Id] = mod.Manifest.Dependencies.Count;
            optional[mod.Id] = 0;
            foreach (var dependency in mod.Manifest.Dependencies.Keys)
                Edge(dependency, mod.Id, isOptional: false);
            foreach (var dependency in mod.Manifest.OptionalDependencies.Keys.Where(enabled.ContainsKey))
            {
                optional[mod.Id]++;
                Edge(dependency, mod.Id, isOptional: true);
            }
        }

        var comparer = Comparer<DiscoveredMod>.Create((a, b) =>
        {
            var byOrder = a.Manifest.LoadOrder.CompareTo(b.Manifest.LoadOrder);
            return byOrder != 0 ? byOrder : StringComparer.Ordinal.Compare(a.Id, b.Id);
        });
        var ready = new SortedSet<DiscoveredMod>(enabled.Values.Where(m => required[m.Id] == 0 && optional[m.Id] == 0), comparer);
        var waiting = new HashSet<string>(enabled.Keys.Where(id => required[id] > 0 || optional[id] > 0), StringComparer.Ordinal);
        var ordered = new List<DiscoveredMod>();
        while (true)
        {
            while (ready.Count > 0)
            {
                var mod = ready.Min!;
                ready.Remove(mod);
                ordered.Add(mod);
                if (!dependents.TryGetValue(mod.Id, out var list))
                    continue;
                foreach (var (dependent, isOptional) in list)
                {
                    if (!waiting.Contains(dependent))
                        continue;
                    if (isOptional)
                        optional[dependent]--;
                    else
                        required[dependent]--;
                    if (required[dependent] == 0 && optional[dependent] == 0)
                    {
                        waiting.Remove(dependent);
                        ready.Add(enabled[dependent]);
                    }
                }
            }

            // Blocked: releases the mods held back only by optional dependencies.
            var released = waiting.Where(id => required[id] == 0).ToList();
            if (released.Count == 0)
                break;
            foreach (var id in released)
            {
                optional[id] = 0;
                waiting.Remove(id);
                ready.Add(enabled[id]);
            }
        }

        cycle = waiting.OrderBy(id => id, StringComparer.Ordinal).ToList();
        return ordered;

        void Edge(string dependency, string dependent, bool isOptional)
        {
            if (!dependents.TryGetValue(dependency, out var list))
                dependents[dependency] = list = [];
            list.Add((dependent, isOptional));
        }
    }
}
