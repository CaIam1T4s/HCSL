using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Deployment;
using HCSL.Infrastructure.Storage;
namespace HCSL.Infrastructure.Server;

internal sealed class ServerLaunch(IWorkspaceLease workspace, string runtime, string map, string executable, string[] arguments, List<Socket> ports, IServerAccessProvider access, InstancePasswordConfig.Binding? password = null) : IAsyncDisposable
{
    internal IWorkspaceLease Workspace { get; } = workspace;
    internal string Runtime { get; } = runtime;
    internal string Map { get; } = map;
    /// <summary>Game port, shared by the SDR socket and the RCON TCP listener.</summary>
    internal int GamePort { get; init; }
    internal ServerPresetIdentity? Preset { get; init; }
    internal FrameworkReadinessProfile ReadinessProfile { get; init; } = FrameworkReadinessProfile.Standard;
    internal string Executable { get; } = executable;
    internal string[] Arguments { get; } = arguments;
    internal string Redact(string text) => password?.Redact(text) ?? text;
    internal void ReleasePorts() { foreach (var port in ports) port.Dispose(); ports.Clear(); }
    /// <summary>The provider owns anything it created for this run (nothing for direct access).</summary>
    public async ValueTask DisposeAsync()
    {
        try { ReleasePorts(); await access.DisposeAsync(); }
        finally { try { password?.Dispose(); } finally { Workspace.Dispose(); } }
    }
}
internal static class ServerPreparation
{
    internal static async Task<ServerLaunch> PrepareAsync(WorkspaceRequest request, ServerStartOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64) throw new ServerException("HCSL-SERVER-PLATFORM", "Linux x64 server baseline is required.");
        CheckMemory(options.AllowLowMemory); var lease = new LocalWorkspaceManager().Acquire(request);
        var ports = new List<Socket>();
        IServerAccessProvider? access = null;
        InstancePasswordConfig.Binding? password = null;
        try
        {
            var deployment = new RuntimeDeploymentService();
            var deploymentOptions = new DeploymentOptions(options.InstanceId);
            var plan = deployment.Prepare(lease, deploymentOptions, ct);
            // AutoDeploy is the operator's explicit "redeploy and start" choice, made in the UI after a
            // DEPLOYMENT/STALE rejection; it is never implied. The deploy reuses this session's lease,
            // because a second lease in the same process is refused with HCSL-LOCK.
            var receipt = RuntimeDeploymentService.ValidateExisting(lease, options.InstanceId, plan.RuntimePath, verifyRotation: false, allowLegacy: true);
            var legacyLayout = receipt is not null && receipt.FormatVersion != 2;
            var rotationMissing = receipt is not null && !RuntimeDeploymentService.RotationFileMatches(receipt, plan.RuntimePath);
            if (options.AutoDeploy && (receipt is null || legacyLayout || IsStale(receipt, plan) || rotationMissing))
                receipt = deployment.Deploy(lease, deploymentOptions, cancellationToken: ct);
            if (receipt is null) throw new ServerException("HCSL-SERVER-DEPLOYMENT", "Deploy this instance before starting it.");
            if (legacyLayout && !options.AutoDeploy)
                throw new ServerException("HCSL-DEPLOY-LAYOUT", "This runtime uses the old persistent layout; redeploy before starting it.");
            if (IsStale(receipt, plan) || !RuntimeDeploymentService.RotationFileMatches(receipt, plan.RuntimePath))
                throw new ServerException("HCSL-SERVER-STALE", "Deployment is stale. Redeploy after changing instance or preset settings.");
            deployment.VerifyBaseline(plan.RuntimePath, deployment.ActiveBaseline(lease.Layout, plan.BuildId), ct);
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
            var mapArgs = plan.RotationCollection is not null
                ? ServerMapRules.StartupArguments(new(ServerMapKind.WorkshopCollection, plan.RotationCollection))
                : ServerMapRules.StartupArguments(new(preset.MapKind, preset.Map), plan.RotationGroup);
            var startupMap = plan.RotationCollection ?? preset.Map;
            var startupKind = plan.RotationCollection is null ? preset.MapKind : ServerMapKind.WorkshopCollection;
            CheckMemory(options.AllowLowMemory);
            password = InstancePasswordConfig.Bind(Path.Combine(lease.Layout.InstancesRoot, options.InstanceId.ToString("D")), plan.PresetId.ToString("D"));
            var launch = new ServerLaunch(lease, plan.RuntimePath, startupMap, "/bin/bash",
                [Path.Combine(plan.RuntimePath, "game/cs2.sh"), "-dedicated", "-console", "-insecure", "-port", entry.Definition.GamePort.ToString(),
                 "+sv_lan", "1", "+game_type", preset.GameType.ToString(), "+game_mode", preset.GameMode.ToString(), "+servercfgfile", InstancePasswordConfig.ExecName, .. mapArgs], ports, access, password) { Preset = new(preset.Id, preset.Revision, preset.Name, startupMap, startupKind), ReadinessProfile = plan.FrameworkReadiness, GamePort = entry.Definition.GamePort };
            password = null;
            access = null;
            return launch;
        }
        catch (SocketException) { password?.Dispose(); await Dispose(ports, access); lease.Dispose(); throw new ServerException("HCSL-SERVER-PORT", "Configured game port is occupied (TCP or UDP) on the bind address."); }
        catch { password?.Dispose(); await Dispose(ports, access); lease.Dispose(); throw; }
    }
    private static async Task Dispose(List<Socket> ports, IServerAccessProvider? access)
    {
        foreach (var port in ports) port.Dispose();
        if (access is not null) await access.DisposeAsync();
    }
    internal static bool IsStale(DeploymentReceipt receipt, DeploymentPlan plan) =>
        receipt.BuildId != plan.BuildId || receipt.PresetId != plan.PresetId
        || receipt.InstanceRevision != plan.InstanceRevision || receipt.PresetRevision != plan.PresetRevision
        || !string.Equals(receipt.RotationGroup, plan.RotationGroup, StringComparison.Ordinal)
        || !string.Equals(receipt.RotationHash, plan.RotationHash, StringComparison.Ordinal)
        || !string.Equals(receipt.RotationCollection, plan.RotationCollection, StringComparison.Ordinal)
        || !string.Equals(receipt.PluginSetHash, plan.PluginSetHash, StringComparison.Ordinal);
    internal static void CheckMemory(bool allowLowMemory = false)
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
        EnforceMemory(available, allowLowMemory);
    }
    internal static void EnforceMemory(long available, bool allowLowMemory = false)
    {
        var mib = Math.Max(0, available / ServerMemoryPolicy.MiB);
        switch (ServerMemoryPolicy.Evaluate(available, allowLowMemory))
        {
            case ServerMemoryDecision.Insufficient:
                throw new ServerException("HCSL-SERVER-MEMORY", $"Available host/cgroup memory is {mib} MiB; at least 3072 MiB is required even with confirmation. No server was launched.");
            case ServerMemoryDecision.ConfirmationRequired:
                throw new ServerException("HCSL-SERVER-MEMORY-WARNING", $"Available host/cgroup memory is {mib} MiB (recommended: 4096 MiB). Explicit low-memory confirmation is required for this start. The 2560 MiB service limit and swap prohibition remain unchanged. No server was launched.");
        }
    }
}
