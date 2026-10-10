using System.Reflection;
using System.Runtime.CompilerServices;
using Lustral.Backend.Mono;
using Lustral.Core.Startup;

namespace Lustral.Host.Mono
{
    /// <summary>
    /// Called by Lustral's stage 0 immediately after Unity's Mono domain starts, before the game's assemblies load.
    /// </summary>
    public static class NativeEntry
    {
        public static void Start() =>
            MonoStartup.Run(Environment.GetEnvironmentVariable("LUSTRAL_CORE_DIRECTORY"));
    }

    internal static class MonoStartup
    {
        private static string s_coreDirectory = ".";

        /// <param name="coreDirectory">The Lustral/core folder, or null to use the folder this assembly was loaded from.</param>
        public static void Run(string? coreDirectory)
        {
            try
            {
                s_coreDirectory = string.IsNullOrEmpty(coreDirectory)
                    ? Path.GetDirectoryName(typeof(MonoStartup).Assembly.Location) ?? "."
                    : coreDirectory!;
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromCore;
                Start();
            }
            catch (Exception e)
            {
                // Bootstrap logs its own failures; this runs only if the log could not be opened.
                try
                {
                    File.WriteAllText(Path.Combine(s_coreDirectory, "..", "lustral-fatal.txt"), e.ToString());
                }
                catch
                {
                    // No remaining way to report the failure; the game must not be brought down.
                }
            }
        }

        // Kept separate from Run so that Mono does not load Lustral.Core before ResolveFromCore is registered.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Start() =>
            Bootstrap.Run(GamePaths.Detect(s_coreDirectory), (paths, logs) => new MonoBackend(paths, logs));

        private static Assembly? ResolveFromCore(object? sender, ResolveEventArgs args)
        {
            var path = Path.Combine(s_coreDirectory, new AssemblyName(args.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
    }
}

namespace Doorstop
{
    /// <summary>
    /// Called by UnityDoorstop 4.5 immediately after Unity's Mono domain starts. Doorstop loads this assembly from memory,
    /// so its Location is unreliable; Doorstop's DOORSTOP_INVOKE_DLL_PATH is used instead. Retained as a fallback for
    /// Lustral's stage 0, which calls <see cref="Lustral.Host.Mono.NativeEntry"/>.
    /// </summary>
    public static class Entrypoint
    {
        public static void Start()
        {
            var self = Environment.GetEnvironmentVariable("DOORSTOP_INVOKE_DLL_PATH");
            Lustral.Host.Mono.MonoStartup.Run(string.IsNullOrEmpty(self) ? null : Path.GetDirectoryName(Path.GetFullPath(self)));
        }
    }
}
