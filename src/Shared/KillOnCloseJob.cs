using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Lustral.Interop;

/// <summary>
/// A Windows job object that terminates its processes when the current process ends, regardless of how it ends. The game
/// uses one for the Generator (so that closing the game during generation also closes the progress window), and the
/// Generator uses one for crossgen2.
/// </summary>
internal sealed partial class KillOnCloseJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    // Never closed explicitly: the handle is closed when the process exits, which terminates the job's processes.
    private readonly nint _handle;

    private KillOnCloseJob(nint handle) => _handle = handle;

    /// <returns>Null if job objects are not available, in which case the processes may outlive the current process.</returns>
    public static KillOnCloseJob? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var handle = CreateJobObjectW(0, null);
        if (handle == 0)
            return null;
        var limits = new ExtendedLimitInformation { Basic = { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            CloseHandle(handle);
            return null;
        }
        return new KillOnCloseJob(handle);
    }

    /// <returns>Whether the process was added. A process that has already exited cannot be added.</returns>
    public bool Add(Process process)
    {
        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
