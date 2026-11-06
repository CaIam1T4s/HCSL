using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HCSL.Daemon.Lifecycle;

internal static class BackgroundConsole
{
    private static SafeFileHandle? nullDevice;
    internal static void Detach()
    {
        if (OperatingSystem.IsLinux())
        {
            var fd = Open("/dev/null", 2);
            if (fd < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            try
            {
                for (int target = 0; target < 3; target++)
                    if (Dup2(fd, target) < 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            finally { if (fd > 2) Close(fd); }
        }
        else if (OperatingSystem.IsWindows())
        {
            nullDevice = CreateFile("NUL", 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (nullDevice.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
            var old = new HashSet<IntPtr>();
            foreach (var target in new[] { -10, -11, -12 })
            {
                old.Add(GetStdHandle(target));
                if (!SetStdHandle(target, nullDevice.DangerousGetHandle())) throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
            foreach (var handle in old)
                if (handle != IntPtr.Zero && handle != new IntPtr(-1) && handle != nullDevice.DangerousGetHandle()) CloseHandle(handle);
        }
        Console.SetIn(TextReader.Null);
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
    }
    [DllImport("libc", EntryPoint = "open", SetLastError = true)] private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)] private static extern int Dup2(int oldFd, int newFd);
    [DllImport("libc", EntryPoint = "close")] private static extern int Close(int fd);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int target);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetStdHandle(int target, IntPtr handle);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
