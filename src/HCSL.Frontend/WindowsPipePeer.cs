using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace HCSL.Frontend;

[SupportedOSPlatform("windows")]
internal static class WindowsPipePeer
{
    internal static void VerifyServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) Denied();
        using var process = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) Denied();
        if (!OpenProcessToken(process, 8, out var token)) Denied();
        using (token)
        using (var server = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
        {
            var serverSid = server.User;
            var currentSid = current.User;
            if (serverSid is null || currentSid is null || !serverSid.Equals(currentSid) || Elevated(token) != Elevated(current.AccessToken)) Denied();
        }
    }
    private static uint Elevated(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformation(token, 20, out uint elevated, 4, out _)) Denied();
        return elevated;
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Denied() => throw new UnauthorizedAccessException("The local daemon user or elevation level could not be verified.");
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, out uint value, int length, out int returned);
}
