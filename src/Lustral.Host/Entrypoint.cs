using Lustral.Core.Startup;

namespace Doorstop;

/// <summary>
/// Called by UnityDoorstop 4.5 on Unity's main thread, immediately after <c>il2cpp_init</c> returns. Retained as a
/// fallback for Lustral's stage 0, which calls <see cref="Lustral.Host.NativeEntry"/>.
/// </summary>
public static class Entrypoint
{
    public static void Start()
    {
        var coreDirectory = Path.GetDirectoryName(typeof(Entrypoint).Assembly.Location) ?? ".";
        try
        {
            Lustral.Host.HostStartup.Run(GamePaths.Detect(coreDirectory));
        }
        catch (Exception e)
        {
            // Bootstrap logs its own failures; this runs only if the log could not be opened.
            try
            {
                File.WriteAllText(Path.Combine(coreDirectory, "..", "lustral-fatal.txt"), e.ToString());
            }
            catch
            {
                // No remaining way to report the failure; the game must not be brought down.
            }
        }
    }
}
