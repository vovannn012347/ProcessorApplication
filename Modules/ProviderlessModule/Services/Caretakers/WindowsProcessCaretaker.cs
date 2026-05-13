using System.Diagnostics;
using System.Runtime.InteropServices;

using ProviderlessModule.Infrastructure.Interfaces;

namespace ProviderlessModule.Services.Caretakers;

public class WindowsProcessCaretaker : IProcessCaretaker
{
    private static readonly IntPtr _jobHandle;

    static WindowsProcessCaretaker()
    {
        // Create the Job Object
        _jobHandle = CreateJobObject(IntPtr.Zero, null);

        // Configure: Kill all assigned processes when the Job handle is closed
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = 0x2000 // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        IntPtr ptr = Marshal.AllocHGlobal(length);
        Marshal.StructureToPtr(info, ptr, false);

        if (!SetInformationJobObject(_jobHandle, 9, ptr, (uint)length))
        {
            throw new Exception("Failed to set Windows Job Object information.");
        }
    }

    public void EnforceParentalControl(Process process)
    {
        AssignProcessToJobObject(_jobHandle, process.Handle);
    }

    #region Win32 Imports
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);
    [DllImport("kernel32.dll")]
    static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);
    [DllImport("kernel32.dll")]
    static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION { public long PerProcessUserTimeLimit; public long PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize; public UIntPtr MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass; public uint SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong ReadOperationCount; public ulong WriteOperationCount; public ulong OtherOperationCount; public ulong ReadTransferCount; public ulong WriteTransferCount; public ulong OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION { public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation; public IO_COUNTERS IoCounters; public UIntPtr ProcessMemoryLimit; public UIntPtr JobMemoryLimit; public UIntPtr PeakProcessMemoryUsage; public UIntPtr PeakJobMemoryUsage; }
    #endregion
}