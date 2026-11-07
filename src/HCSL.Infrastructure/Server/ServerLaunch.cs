using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Deployment;
using HCSL.Infrastructure.Storage;
namespace HCSL.Infrastructure.Server;

internal sealed class ServerLaunch(IWorkspaceLease workspace, string runtime, string map, string executable, string[] arguments, List<Socket> ports, IServerAccessProvider access) : IAsyncDisposable
{
    internal IWorkspaceLease Workspace { get; } = workspace;
    internal string Runtime { get; } = runtime;
    internal string Map { get; } = map;
    internal string Executable { get; } = executable;
    internal string[] Arguments { get; } = arguments;
    internal void ReleasePorts() { foreach (var port in ports) port.Dispose(); ports.Clear(); }
    /// <summary>The provider owns anything it created for this run (nothing for direct access).</summary>
    public async ValueTask DisposeAsync() { ReleasePorts(); await access.DisposeAsync(); Workspace.Dispose(); }
}
internal static class ServerPreparation
{
    internal static async Task<ServerLaunch> PrepareAsync(WorkspaceRequest request, ServerStartOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) throw new ServerException("HCSL-SERVER-PLATFORM", "Linux x64 server baseline is required.");
        CheckMemory(); var lease = new LocalWorkspaceManager().Acquire(request);
        var ports = new List<Socket>();
        IServerAccessProvider? access = null;
        try
        {
            var deployment = new RuntimeDeploymentService();
            var deploymentOptions = new DeploymentOptions(options.InstanceId);
            var plan = deployment.Prepare(lease, deploymentOptions, ct);
            // AutoDeploy is the operator's explicit "redeploy and start" choice, made in the UI after a
            // DEPLOYMENT/STALE rejection; it is never implied. The deploy reuses this session's lease,
            // because a second lease in the same process is refused with HCSL-LOCK.
            var receipt = RuntimeDeploymentService.ValidateExisting(lease, options.InstanceId, plan.RuntimePath);
            if (options.AutoDeploy && (receipt is null || IsStale(receipt, plan)))
                receipt = deployment.Deploy(lease, deploymentOptions, cancellationToken: ct);
            if (receipt is null) throw new ServerException("HCSL-SERVER-DEPLOYMENT", "Deploy this instance before starting it.");
            if (IsStale(receipt, plan))
                throw new ServerException("HCSL-SERVER-STALE", "Deployment is stale. Redeploy after changing instance or preset settings.");
            deployment.VerifyBaseline(plan.RuntimePath, ct);
            var entry = LocalInstanceStore.Load(Path.Combine(lease.Layout.InstancesRoot, options.InstanceId.ToString("D")), options.InstanceId);
            var network = InstanceRules.Resolve(entry.Definition!.Network);
            access = AccessProviders.For(network);
            var resolved = await access.PrepareAsync(network, entry.Definition.GamePort, ct);
            // Probe the wildcard, not the instance address: CS2's SteamNetworkingSockets server socket is
            // opened on 0.0.0.0 regardless of -ip (verified against build 26445667 — `ss` reports *:27015), so
            // a specific-address probe could pass while the real wildcard bind fails on another interface.
            var bind = IPAddress.Any;
            foreach (var type in new[] { SocketType.Stream, SocketType.Dgram })
            {
                var socket = new Socket(AddressFamily.InterNetwork, type, type == SocketType.Stream ? ProtocolType.Tcp : ProtocolType.Udp);
                ports.Add(socket); socket.ExclusiveAddressUse = true;
                socket.Bind(new IPEndPoint(bind, entry.Definition.GamePort));
            }
            var preset = entry.Presets.Single(p => p.Id == plan.PresetId);
            CheckMemory();
            var launch = new ServerLaunch(lease, plan.RuntimePath, preset.Map, "/bin/bash",
                [Path.Combine(plan.RuntimePath, "game/cs2.sh"), "-dedicated", "-console", "-insecure", "-ip", resolved.BindAddress, "-port", entry.Definition.GamePort.ToString(),
                 "+sv_lan", "1", "+game_type", preset.GameType.ToString(), "+game_mode", preset.GameMode.ToString(), "+map", preset.Map], ports, access);
            access = null;
            return launch;
        }
        catch (SocketException) { await Dispose(ports, access); lease.Dispose(); throw new ServerException("HCSL-SERVER-PORT", "Configured game port is occupied (TCP or UDP) on the bind address."); }
        catch { await Dispose(ports, access); lease.Dispose(); throw; }
    }
    private static async Task Dispose(List<Socket> ports, IServerAccessProvider? access)
    {
        foreach (var port in ports) port.Dispose();
        if (access is not null) await access.DisposeAsync();
    }
    private static bool IsStale(DeploymentReceipt receipt, DeploymentPlan plan) =>
        receipt.BuildId != plan.BuildId || receipt.PresetId != plan.PresetId
        || receipt.InstanceRevision != plan.InstanceRevision || receipt.PresetRevision != plan.PresetRevision;
    internal static void CheckMemory()
    {
        long available = long.Parse(File.ReadLines("/proc/meminfo").First(l => l.StartsWith("MemAvailable:", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) * 1024;
        var group = File.ReadLines("/proc/self/cgroup").FirstOrDefault(l => l.StartsWith("0::", StringComparison.Ordinal));
        if (group != null)
        {
            var relative = group[3..].TrimStart('/');
            if (relative.Split('/').Any(p => p == "..")) throw new ServerException("HCSL-SERVER-MEMORY", "Cannot inspect memory control group.");
            var current = Path.Combine("/sys/fs/cgroup", relative);
            while (current.StartsWith("/sys/fs/cgroup", StringComparison.Ordinal))
            {
                var max = Path.Combine(current, "memory.max"); var used = Path.Combine(current, "memory.current");
                if (File.Exists(max) && File.Exists(used) && long.TryParse(File.ReadAllText(max).Trim(), out var limit))
                    available = Math.Min(available, Math.Max(0, limit - long.Parse(File.ReadAllText(used).Trim())));
                if (current == "/sys/fs/cgroup") break; current = Path.GetDirectoryName(current)!;
            }
        }
        if (available < 4096L * 1024 * 1024) throw new ServerException("HCSL-SERVER-MEMORY", "At least 4096 MiB of host/cgroup available memory is required. No server was launched.");
    }
}
