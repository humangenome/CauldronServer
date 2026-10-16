using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CauldronServer.Services;

/// <summary>
/// Ties the game's lifetime to CauldronServer's on Windows.
///
/// The supervisor puts the Witchspire process it starts into a Windows job object
/// that kills its members when the last handle to it closes. The handle is held for
/// the life of CauldronServer, so when CauldronServer ends for any reason (its
/// console window closed, a crash, Task Manager) Windows ends the game too, instead
/// of leaving a headless host running out of sight and holding the port.
/// </summary>
public static class KillOnCloseJob
{
    private static readonly object Gate = new();
    private static IntPtr _job = IntPtr.Zero;

    /// <summary>Add a process to the job. Returns false when it could not be done.</summary>
    public static bool TryAdd(Process process, Action<string> log)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            lock (Gate)
            {
                if (_job == IntPtr.Zero)
                {
                    var job = CreateJobObject(IntPtr.Zero, null);
                    if (job == IntPtr.Zero)
                    {
                        log($"Could not create the job that stops the game with the server (error {Marshal.GetLastWin32Error()})");
                        return false;
                    }

                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
                            (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                    {
                        log($"Could not set up the job that stops the game with the server (error {Marshal.GetLastWin32Error()})");
                        CloseHandle(job);
                        return false;
                    }
                    _job = job;
                }

                if (!AssignProcessToJobObject(_job, process.Handle))
                {
                    log($"Could not tie the game (pid {process.Id}) to the server; it will keep running if the server is closed (error {Marshal.GetLastWin32Error()})");
                    return false;
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            log($"Could not tie the game to the server: {ex.Message}");
            return false;
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
