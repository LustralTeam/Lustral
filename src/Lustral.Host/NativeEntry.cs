using System.Runtime.InteropServices;
using Lustral.Core.Startup;

namespace Lustral.Host;

/// <summary>
/// The data passed by Lustral's stage 0. Mirrors <c>lustral::NativeApi</c> in stage0/src/stage0.h. Fields are only ever
/// appended, so that newer versions of stage 0 remain compatible with this host.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeApi
{
    public uint Size;
    public uint Version;
    public char* GameExecutable;
    public char* CoreDirectory;
    public delegate* unmanaged<int, char*, void> Log;
}

/// <summary>
/// Called by Lustral's stage 0 on Unity's main thread, immediately after <c>il2cpp_init</c> returns.
/// </summary>
public static unsafe class NativeEntry
{
    /// <returns>0 after startup has run (failures are logged), or 1 if the arguments are invalid.</returns>
    [UnmanagedCallersOnly]
    public static int Start(nint api)
    {
        var native = (NativeApi*)api;
        if (native is null || native->Size < sizeof(NativeApi) || native->Version < 1)
            return 1;

        var paths = new GamePaths(new string(native->GameExecutable), new string(native->CoreDirectory));
        try
        {
            HostStartup.Run(paths);
            return 0;
        }
        catch (Exception e)
        {
            // Bootstrap logs its own failures; this runs only if the log could not be opened.
            fixed (char* message = $"Lustral.Host failed before its log opened: {e}")
                native->Log(3, message);
            return 0;
        }
    }
}
