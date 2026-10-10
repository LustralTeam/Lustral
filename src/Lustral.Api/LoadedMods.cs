using System.Collections;

namespace Lustral;

/// <summary>
/// The mods Lustral has loaded in the current session, including libraries, in load order. A mod's dependencies and
/// optional dependencies are always loaded before it, so from the mod's constructor onward,
/// <c>Mods.IsLoaded("other.mod")</c> indicates whether an optional dependency is present.
/// </summary>
public sealed class LoadedMods : IReadOnlyList<ModInfo>
{
    private volatile ModInfo[] _mods = [];

    internal LoadedMods()
    {
    }

    /// <summary>The number of loaded mods.</summary>
    public int Count => _mods.Length;

    /// <summary>The loaded mod at the specified position in the load order.</summary>
    public ModInfo this[int index] => _mods[index];

    /// <summary>Returns whether the mod with the specified ID has been loaded.</summary>
    public bool IsLoaded(string id) => Find(id) is not null;

    /// <summary>Returns the loaded mod with the specified ID, or null.</summary>
    public ModInfo? Find(string id)
    {
        foreach (var mod in _mods)
        {
            if (mod.Id == id)
                return mod;
        }
        return null;
    }

    /// <summary>Returns an enumerator over the loaded mods in load order.</summary>
    public IEnumerator<ModInfo> GetEnumerator() => ((IEnumerable<ModInfo>)_mods).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Mods are added on a single thread. Readers on other threads always see a complete array.
    internal void Add(ModInfo mod)
    {
        var mods = new ModInfo[_mods.Length + 1];
        _mods.CopyTo(mods, 0);
        mods[mods.Length - 1] = mod;
        _mods = mods;
    }
}
