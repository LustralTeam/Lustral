using Lustral.Backend.Il2Cpp;
using Lustral.Core;
using Lustral.Core.Startup;

namespace Lustral.Host;

internal static class HostStartup
{
    public static void Run(GamePaths paths) =>
        Bootstrap.Run(paths, (paths, logs) =>
            Il2CppBackend.IsIl2CppGame(paths.GameDirectory)
                ? new Il2CppBackend(paths, logs)
                : throw new StartupException("This is a Mono game, but the IL2CPP version of Lustral is installed. Install the Mono version instead."));
}
