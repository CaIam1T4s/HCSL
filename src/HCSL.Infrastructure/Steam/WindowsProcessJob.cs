using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HCSL.Infrastructure.Steam;

// The SteamCMD process is assigned while the launcher gate is blocked, before any tool code runs.
internal sealed class WindowsProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;
    internal string Name { get; } = "Local\\HCSL.SteamCmd." + Guid.NewGuid().ToString("N");
    internal WindowsProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, Name);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var info = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            handle.Dispose();
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }
    internal void Assign(IntPtr process)
    {
        if (!AssignProcessToJobObject(handle, process)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    internal void Terminate()
    {
        if (!TerminateJobObject(handle, 1)) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }
    internal bool HasActiveProcesses => Active(handle);
    internal static bool IsActive(string name)
    {
        if (!name.StartsWith("Local\\HCSL.SteamCmd.", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid SteamCMD job name.");
        using var job = OpenJobObject(4, false, name); // JOB_OBJECT_QUERY
        if (job.IsInvalid)
        {
            if (Marshal.GetLastPInvokeError() == 2) return false;
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        return Active(job);
    }
    private static bool Active(SafeFileHandle job)
    {
        if (!QueryInformationJobObject(job, 1, out var info, (uint)Marshal.SizeOf<Accounting>(), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return info.ActiveProcesses != 0;
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)]
    private struct Accounting
    {
        public long TotalUser, TotalKernel, PeriodUser, PeriodKernel;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong A, B, C, D, E, F; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out Accounting info, uint length, IntPtr returned);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
}
