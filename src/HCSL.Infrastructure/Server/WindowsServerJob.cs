using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HCSL.Infrastructure.Server;

/// <summary>Owns the Windows server process tree; the OS terminates it when the daemon loses the job handle.</summary>
internal static class WindowsServerJob
{
    private const uint KillOnClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    internal static SafeFileHandle Create()
    {
        var job = CreateJobObjectW(IntPtr.Zero, IntPtr.Zero);
        if (job.IsInvalid)
        {
            var error = new Win32Exception();
            job.Dispose();
            throw error;
        }
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = KillOnClose } };
        if (!SetInformationJobObject(job, ExtendedLimitInformation, in limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = new Win32Exception();
            job.Dispose();
            throw error;
        }
        return job;
    }

    internal static void Attach(SafeFileHandle job, Process process)
    {
        if (!AssignProcessToJobObject(job, process.SafeHandle)) throw new Win32Exception();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, IntPtr name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, in ExtendedLimits limits, uint size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
