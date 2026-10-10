using System.Reflection;
using Lustral.Core.Mods;

namespace Lustral.Core.Loading;

/// <summary>
/// Loads mod assemblies into the runtime: one load context per mod on CoreCLR, or a single shared domain on Mono.
/// </summary>
public interface IModAssemblyLoader
{
    /// <summary>Prepares assembly resolution for a mod. Called in load order, so the mod's dependencies are already registered.</summary>
    void Register(DiscoveredMod mod);

    /// <summary>Loads an assembly from a registered mod's folder.</summary>
    Assembly Load(DiscoveredMod mod, string path);
}
