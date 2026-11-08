using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;

namespace HCSL.ServerTests;

public static class HostServerControllerTests
{
    public static async Task RunAsync()
    {
        var daemon = new FakeDaemon();
        using var host = new HostServerController(new ServerController(daemon, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1)), new InstanceClient(daemon));
        void Check(bool v, string message) { if (!v) throw new Exception(message); }
        Check(host.Header.Contains("未知"), "unconnected host claims idle");
        await host.InitializeAsync();
        Check(host.Instances.Count == 2 && host.Selected?.Entry.Id == daemon.First, "catalog not loaded");
        Check(host.SelectionText.Contains("de_dust2") && host.SelectionText.Contains("27015"), "missing preset and address");
        await host.SetPasswordAsync("temporary-test");
        Check(host.Selected!.Entry.HasPassword && daemon.PasswordCalls == 1, "instance password action missing");
        Check(!host.SelectionText.Contains("temporary-test"), "password exposed in projection");
        Check(await host.StartAsync(), "start rejected");
        Check(daemon.LastStart?.AutoDeploy == true && daemon.LastStart.InstanceId == daemon.First, "start not delegated to shared controller");
        Check(!host.CanMutate && host.Header.Contains("运行中"), "active state not projected");
        await host.SetPasswordAsync("must-not-send");
        Check(daemon.PasswordCalls == 1, "active mutation was sent");
        host.Selected = host.Instances.Single(x => x.Entry.Id == daemon.Second);
        Check(host.Server.LiveInstanceId == daemon.First && host.SelectionText.Contains("运行实例：First"), "selection disguised active server identity");
        Check(await host.StopAndWaitAsync(TimeSpan.FromSeconds(1)), "confirmed stop did not finish");
        Check(daemon.StopRun == daemon.Run, "stop must use snapshot run identity");
        await host.StartAsync(allowLowMemory: true);
        Check(daemon.LastStart?.AllowLowMemory == true && daemon.LastStart.InstanceId == daemon.Second, "consent or selected instance lost");
        var stopCalls = daemon.StopCalls;
        host.OpenLogs(); host.CloseLogs();
        host.Dispose();
        Check(daemon.StopCalls == stopCalls, "disposing GUI stopped server");
        Console.WriteLine("PASS host.controller.catalog-selection-password-guards-start-stop-and-dispose");

        var recovery = new FakeDaemon { Recover = true };
        using var restored = new HostServerController(new ServerController(recovery, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1)), new InstanceClient(recovery));
        await restored.InitializeAsync();
        Check(restored.Selected?.Entry.Id == recovery.Second, "fresh GUI did not select orphan instance");
        Check(!restored.CanMutate && await restored.RecoverAsync() && recovery.Recovered == recovery.Second, "recovery identity lost");
        Console.WriteLine("PASS host.controller.orphan-selection-and-recovery");
    }
    private sealed class FakeDaemon : IDaemonClient
    {
        public Guid First { get; } = Guid.NewGuid();
        public Guid Second { get; } = Guid.NewGuid();
        public Guid Run { get; } = Guid.NewGuid();
        public bool Recover;
        public Guid? Recovered, StopRun;
        public int PasswordCalls, StopCalls;
        public ServerCommand? LastStart;
        private bool hasPassword, active;
        private Guid current;
        private readonly Guid preset = Guid.NewGuid();
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
        {
            if (request.Kind == RequestKind.Hello)
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, Defaults: new("/settings", "/data", "/library")));
            if (request.Kind == RequestKind.Instances)
            {
                var c = JsonSerializer.Deserialize<InstanceCommand>(request.InstanceCommandJson!)!;
                if (c.Action == InstanceAction.SetPassword) { PasswordCalls++; hasPassword = !string.IsNullOrEmpty(c.Password); }
                InstanceEntry Item(Guid id, string name, int port) => new(id, new(1, id, 1, name, port, preset), [new(1, preset, 1, "Default")], HasPassword: id == First && hasPassword);
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, InstanceCatalogJson: JsonSerializer.Serialize(new InstanceCatalog([Item(First, "First", 27015), Item(Second, "Second", 27016)]))));
            }
            var command = JsonSerializer.Deserialize<ServerCommand>(request.ServerCommandJson!)!;
            if (command.Action == ServerAction.Start) { LastStart = command; current = command.InstanceId; active = true; }
            if (command.Action == ServerAction.Stop) { StopCalls++; StopRun = command.RunId; active = false; }
            if (command.Action == ServerAction.Recover) { Recovered = command.InstanceId; Recover = false; }
            var snapshot = new ServerSnapshot(active || Recover ? Run : null, Recover ? Second : current == Guid.Empty ? null : current,
                Recover ? ServerState.RecoveryRequired : active ? ServerState.Running : ServerState.Stopped,
                active, Recover ? "HCSL-SERVER-RECOVERY" : active ? "HCSL-SERVER-MAP-READY" : "HCSL-SERVER-STOPPED", active, !active && !Recover,
                null, 1, false, [], Map: active ? "de_dust2" : null);
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, ServerSnapshotJson: JsonSerializer.Serialize(snapshot)));
        }
    }
}
