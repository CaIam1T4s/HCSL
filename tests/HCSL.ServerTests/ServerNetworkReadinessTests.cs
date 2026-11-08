using System.Text.Json;
using HCSL.Application.Abstractions;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Daemon.Workers;
using HCSL.Frontend;

namespace HCSL.ServerTests;

public static class ServerNetworkReadinessTests
{
    public static async Task RunAsync()
    {
        var value = new ServerNetworkReadiness();
        Check(value.Observe("unrecognized engine format") == value, "Unknown logs must not imply readiness.");
        value = value.Observe("AuthStatus (invalid):  Waiting  (Waiting for Steam login)");
        Check(value.Authentication == SteamAuthenticationState.WaitingForSteam, "Login wait not observed.");
        value = value.Observe("AuthStatus (steamid:123):  Attempting  (Requesting cert)");
        value = value.Observe("Failed to accept connection from 192.168.1.10:51260.  Bad cert: CA key 123 is not known to us");
        value = value.Observe("SDR network config fetch attempt #1 failed.  No response from server.  Trying again.");
        Check(value.Authentication == SteamAuthenticationState.RequestingCertificate && value.Configuration == SdrConfigurationState.Retrying && value.ClientCertificateRejected, "Retry/certificate evidence missing.");
        Check(!value.WithElapsed(TimeSpan.FromSeconds(119)).Delayed && value.WithElapsed(TimeSpan.FromSeconds(120)).Delayed, "120s diagnostic boundary incorrect.");
        Check(ServerStatusText.NetworkHint(value.WithElapsed(TimeSpan.FromSeconds(120)), false)?.Contains("不要关闭证书") == true, "Safe diagnostic missing.");
        value = value.Observe("Got SDR network config.  Loaded revision 1793736599 OK");
        Check(value.Authentication != SteamAuthenticationState.Ready, "Config loaded must not imply authentication readiness.");
        value = value.Observe("AuthStatus (steamid:123):  OK  (OK)");
        Check(value.Authentication == SteamAuthenticationState.Ready && !value.WithElapsed(TimeSpan.FromMinutes(10)).Delayed && !value.ClientCertificateRejected, "Recovery not observed.");
        value = value.Observe("Failed to accept connection from 192.168.1.10:51260. Bad cert: unknown");
        Check(value.Authentication == SteamAuthenticationState.Ready, "A bad peer must not downgrade server authentication.");
        value = value.Observe("AuthStatus (steamid:123): Failed (Disconnected)");
        Check(value.Authentication == SteamAuthenticationState.Failed, "Authentication regression must be visible.");
        Check(ServerStatusText.NetworkLine(null, false).Contains("未知") && ServerStatusText.NetworkLine(null, true).Contains("unknown"), "Old daemon must display unknown.");
        Check(JsonSerializer.Deserialize<ServerSnapshot>("{\"State\":4,\"Active\":true,\"Lines\":[]}")!.NetworkReadiness is null, "Old JSON must remain compatible.");
        Console.WriteLine("PASS network.readiness.retry-recovery-failure-unknown-delay-and-json-defaults");

        var fake = new FrontendDaemon();
        using (var controller = new ServerController(fake, a => a()))
        {
            await controller.ConnectAsync();
            Check(controller.SummaryLine.Contains("Steam 认证：未知"), "Old daemon must not appear authenticated.");
            fake.Readiness = value with { Authentication = SteamAuthenticationState.Ready, ClientCertificateRejected = false };
            await controller.RefreshAsync();
            Check(controller.NetworkReadiness?.Authentication == SteamAuthenticationState.Ready && controller.NetworkSummaryLine.Contains("就绪"), "Frontend must project independent readiness.");
            controller.English = true;
            Check(controller.NetworkSummaryLine.Contains("Steam auth: ready"), "English readiness text missing.");
        }
        Console.WriteLine("PASS controller.network-readiness.status-projection-and-old-daemon-unknown");

        var runtime = new Runtime();
        await using var operations = new ServerOperations(runtime);
        var instance = Guid.NewGuid();
        var workspace = new WorkspaceRequest("/unused-settings", "/unused-data", "/unused-library");
        operations.Start(workspace, new(instance));
        await Wait(() => runtime.Events is not null);
        var sink = runtime.Events!;
        var pending = new ServerNetworkReadiness().Observe("AuthStatus (invalid): Waiting (Waiting for Steam login)");
        sink.Report(new(ServerState.Starting, "HCSL-SERVER-LOG", "[redacted sensitive configuration]", NetworkReadiness: pending));
        sink.Report(new(ServerState.Running, "HCSL-SERVER-MAP-READY", Map: "de_dust2"));
        var snapshot = operations.Snapshot();
        Check(snapshot.MapLoaded && snapshot.State == ServerState.Running && snapshot.NetworkReadiness?.Authentication == SteamAuthenticationState.WaitingForSteam && snapshot.Lines.Length == 0, "Status must carry independent readiness without log pages.");
        var ready = pending.Observe("Got SDR network config. Loaded revision 1 OK").Observe("AuthStatus (steamid:123): OK (OK)");
        sink.Report(new(ServerState.Starting, "HCSL-SERVER-LOG", "[redacted sensitive configuration]", NetworkReadiness: ready));
        snapshot = operations.Snapshot();
        Check(snapshot.State == ServerState.Running && snapshot.NetworkReadiness?.Authentication == SteamAuthenticationState.Ready, "Network updates must not regress map lifecycle state.");
        Check(ServerStatusText.NetworkLine(snapshot.NetworkReadiness, false).Contains("就绪"), "Frontend status projection missing.");
        operations.Stop(snapshot.RunId);
        await Wait(() => !operations.Snapshot().Active);
        Check(operations.Snapshot().NetworkReadiness?.Authentication == SteamAuthenticationState.Unknown, "Stopped run must not advertise readiness.");
        runtime.Events = null;
        await Wait(() =>
        {
            try { operations.Start(workspace, new(instance)); return true; }
            catch (ServerException e) when (e.Code == "HCSL-SERVER-BUSY") { return false; }
        });
        await Wait(() => runtime.Events is not null);
        // An event from the old run must not publish old authentication into the new run.
        sink.Report(new(ServerState.Starting, "HCSL-SERVER-LOG", "old", NetworkReadiness: ready));
        Check(operations.Snapshot().NetworkReadiness?.Authentication == SteamAuthenticationState.Unknown, "Run change must clear readiness and reject old events.");
        Console.WriteLine("PASS daemon.network-readiness.independent-map-status-redaction-reset-and-stale-events");
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new Exception("Network readiness fixture timed out."); await Task.Delay(5); }
    }
    private sealed class FrontendDaemon : IDaemonClient
    {
        private readonly Guid run = Guid.NewGuid();
        public ServerNetworkReadiness? Readiness;
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
        {
            var snapshot = new ServerSnapshot(run, null, ServerState.Running, true, "HCSL-SERVER-MAP-READY", true, false, null, 0, false, [], NetworkReadiness: Readiness);
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true,
                Defaults: new("/settings", "/data", "/library"), ServerSnapshotJson: JsonSerializer.Serialize(snapshot)));
        }
    }
    private sealed class Runtime : IServerRuntimeService
    {
        public IProgress<ServerRuntimeEvent>? Events;
        public async Task<ServerOutcome> RunAsync(WorkspaceRequest workspace, Guid runId, ServerStartOptions options, IProgress<ServerRuntimeEvent> events, CancellationToken stop)
        {
            Events = events;
            try { await Task.Delay(Timeout.Infinite, stop); } catch (OperationCanceledException) { }
            return new("HCSL-SERVER-STOPPED", 0, true, true);
        }
        public void Recover(WorkspaceRequest workspace, Guid instanceId, Guid? expectedRunId) { }
    }
}
