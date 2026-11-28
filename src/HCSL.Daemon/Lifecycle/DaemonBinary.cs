namespace HCSL.Daemon.Lifecycle;

internal static class DaemonBinary
{
    public static string Path { get; } =
#if HCSL_DAEMON_NATIVE_AOT
        Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the native daemon executable.");
#else
        typeof(DaemonBinary).Assembly.Location;
#endif
}
