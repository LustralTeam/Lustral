namespace Lustral.Tests.ChainLibrary;

/// <summary>
/// The mods that have loaded, in order. A library is loaded once and shared, so all mods that depend on it see the same
/// list; the second mod finding the first in the list verifies both the load order and the sharing.
/// </summary>
public static class ChainRegistry
{
    private static readonly List<string> s_loaded = [];

    public static void Loaded(string modId)
    {
        lock (s_loaded)
            s_loaded.Add(modId);
    }

    public static IReadOnlyList<string> LoadedMods
    {
        get
        {
            lock (s_loaded)
                return s_loaded.ToArray();
        }
    }
}
