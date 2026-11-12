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

        // Online roster and admin list are delegated through the controller, never straight from the view.
        var live = new FakeDaemon();
        using var players = new HostServerController(new ServerController(live, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1)), new InstanceClient(live), daemon: live);
        await players.InitializeAsync();
        Check(!players.CanQueryPlayers, "roster offered while the server is stopped");
        try { await players.PlayersAsync(); throw new Exception("roster read while stopped"); }
        catch (IOException e) when (e.Message.Contains("HCSL-PLAYERS-STATE", StringComparison.Ordinal)) { }
        Check(await players.StartAsync(), "start for roster failed");
        Check(players.CanQueryPlayers, "roster not offered while running");
        var roster = await players.PlayersAsync();
        Check(roster.Humans == 1 && roster.Bots == 1 && roster.Players[0].SteamId64 == "76561198000000000", "roster not projected");
        var runId = players.Server.Snapshot?.RunId ?? throw new Exception("run id missing");
        var kicked = await players.KickAsync(live.First, runId, 2, "76561198000000000", "afk");
        Check(kicked.Written && live.LastKick is { UserId: 2, SteamId64: "76561198000000000", Reason: "afk", RunId: var sent } && sent == runId,
            "kick not delegated with run/user identity");

        var admins = await players.AdminsAsync(200, 50, "Player");
        Check(live.LastAdmin is { Action: AdminAction.List, Offset: 200, Limit: 50, Query: "Player" }, "admin paging/search not passed through");
        Check(admins.Snapshot.Capabilities?.Key == AdminKeyKind.DisplayName, "admin capabilities missing");
        var applied = await players.ApplyAdminAsync(new(AdminAction.Apply, live.First, live.PresetId, admins.Snapshot.Hash,
            [new(AdminMutationKind.Upsert, "76561198000000000", "CaIam1Tas", ["#hcsl/admin"])]));
        Check(live.LastAdmin is { Action: AdminAction.Apply, ExpectedHash: not null } && applied.Code == "HCSL-ADMIN-WRITTEN", "admin write not delegated");
        try
        {
            await players.ApplyAdminAsync(new(AdminAction.Apply, live.Second, live.PresetId, admins.Snapshot.Hash, [new(AdminMutationKind.Remove, "76561198000000000")]));
            throw new Exception("admin write accepted for a different instance");
        }
        catch (IOException e) when (e.Message.Contains("HCSL-ADMIN-REQUEST", StringComparison.Ordinal)) { }
        var reloaded = await players.ReloadAdminsAsync();
        Check(live.LastAdmin?.Action == AdminAction.Reload && reloaded.Reload == "css_admins_reload sent", "admin reload not delegated");
        Console.WriteLine("PASS host.controller.roster-kick-and-admin-delegation");

        var recovery = new FakeDaemon { Recover = true };
        using var restored = new HostServerController(new ServerController(recovery, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1)), new InstanceClient(recovery));
        await restored.InitializeAsync();
        Check(restored.Selected?.Entry.Id == recovery.Second, "fresh GUI did not select orphan instance");
        Check(!restored.CanMutate && await restored.RecoverAsync() && recovery.Recovered == recovery.Second, "recovery identity lost");
        Console.WriteLine("PASS host.controller.orphan-selection-and-recovery");

        // Deploy-only action and the directory entry points that the GUI buttons call.
        var root = Path.Combine(Path.GetTempPath(), "hcsl-dirs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var deployer = new FakeDaemon { DataRoot = Path.Combine(root, "data"), LibraryRoot = Path.Combine(root, "library") };
            Directory.CreateDirectory(Path.Combine(deployer.LibraryRoot, "runtime", deployer.First.ToString("D")));
            var opener = new StubOpener();
            using var host2 = new HostServerController(new ServerController(deployer, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1)),
                new InstanceClient(deployer), opener);
            await host2.InitializeAsync();
            Check(host2.CanDeploy, "deploy must be admitted before the first start.");
            Check(await host2.DeployAsync() && deployer.DeployCalls == 1 && host2.Server.Code == "HCSL-SERVER-DEPLOYED",
                "deploy-only action not delegated.");
            Check(!host2.Server.Active, "deploy-only must not mark the server active.");
            var preset = host2.Selected!.Entry.Presets[0];
            var probed = await host2.ProbePluginImportAsync("/tmp/source2mod-1.0.46.zip");
            Check(probed.Id == "source2mod" && probed.Version == "1.0.46" && deployer.ProbeCalls == 1, "plugin import probe not delegated.");
            var imported = await host2.ImportPluginAsync(new(deployer.First, preset.Id, "/tmp/src", "metamod", "Metamod", "2.0.0"));
            Check(imported.Id == "metamod" && deployer.ImportCalls == 1, "plugin import not delegated.");
            var preview = await host2.DeployPreviewAsync(deployer.First, preset.Id);
            Check(preview is not null && deployer.PlanDeployments == 1 && preview.CopyMode == DeploymentCopyMode.Reflink,
                "deployment preview not delegated.");
            Check(host2.CanRecoverDeployment && await host2.RecoverDeploymentAsync() && deployer.RecoverDeployments == 1
                && host2.Server.Code == "HCSL-SERVER-DEPLOY-RECOVERED", "recover-deployment not delegated.");
            Check(await host2.OpenPresetDirectoryAsync(deployer.First, preset.Id, "addons"), "preset addons open failed.");
            Check(opener.Last == Path.Combine(deployer.DataRoot, "instances", deployer.First.ToString("D"), "presets", preset.Id.ToString("D"), "addons"),
                "preset addons path wrong: " + opener.Last);
            Check(await host2.OpenRuntimeDirectoryAsync(deployer.First), "runtime open failed.");
            Check(opener.Last == Path.Combine(deployer.LibraryRoot, "runtime", deployer.First.ToString("D")), "runtime path wrong: " + opener.Last);
            Check(!await host2.OpenPresetDirectoryAsync(deployer.First, preset.Id, "../escape"), "unknown preset directory accepted.");
            Check(host2.Error.Contains("HCSL-PRESET-DIR"), "unknown directory error code missing.");
            Console.WriteLine("PASS host.controller.deploy-only-and-directory-entries");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class StubOpener : HCSL.Application.Abstractions.ILocalDirectoryOpener
    {
        public string? Last;
        public Task OpenAsync(string directory, CancellationToken cancellationToken = default) { Last = directory; return Task.CompletedTask; }
    }
    private sealed class FakeDaemon : IDaemonClient
    {
        public string DataRoot = "/data", LibraryRoot = "/library";
        public Guid First { get; } = Guid.NewGuid();
        public Guid Second { get; } = Guid.NewGuid();
        public Guid Run { get; } = Guid.NewGuid();
        public bool Recover;
        public Guid? Recovered, StopRun;
        public int PasswordCalls, StopCalls, DeployCalls, RecoverDeployments, PlanDeployments, ImportCalls, ProbeCalls;
        public AdminCommand? LastAdmin;
        public ServerKickCommand? LastKick;
        public Guid PresetId => preset;
        public ServerCommand? LastStart;
        private bool hasPassword, active;
        private Guid current;
        private readonly Guid preset = Guid.NewGuid();
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
        {
            if (request.Kind == RequestKind.Hello)
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, Defaults: new("/settings", DataRoot, LibraryRoot)));
            if (request.Kind == RequestKind.PluginImportProbe)
            {
                var c = JsonSerializer.Deserialize<PluginImportProbeCommand>(request.PluginImportProbeJson!)!;
                ProbeCalls++;
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, PluginImportPreviewJson: JsonSerializer.Serialize(
                    new PluginImportPreview(c.SourcePath, c.SourcePath.EndsWith(".zip") ? "archive" : "directory", "source2mod", "Source2Mod", "1.0.46", null, 3, 10, []))));
            }
            if (request.Kind == RequestKind.PluginImport)
            {
                var c = JsonSerializer.Deserialize<PluginImportCommand>(request.PluginImportJson!)!;
                ImportCalls++;
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, PluginImportResultJson: JsonSerializer.Serialize(
                    new PluginImportResult("/library/plugin-packages/" + c.Id + "/" + c.Version, c.Id, c.Version, 3, 10, true, [], c.Enable))));
            }
            if (request.Kind == RequestKind.Instances)
            {
                var c = JsonSerializer.Deserialize<InstanceCommand>(request.InstanceCommandJson!)!;
                if (c.Action == InstanceAction.SetPassword) { PasswordCalls++; hasPassword = !string.IsNullOrEmpty(c.Password); }
                InstanceEntry Item(Guid id, string name, int port) => new(id, new(1, id, 1, name, port, preset), [new(1, preset, 1, "Default")], HasPassword: id == First && hasPassword);
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, InstanceCatalogJson: JsonSerializer.Serialize(new InstanceCatalog([Item(First, "First", 27015), Item(Second, "Second", 27016)]))));
            }
            if (request.Kind == RequestKind.Admin)
            {
                var admin = JsonSerializer.Deserialize<AdminCommand>(request.AdminJson!)!;
                LastAdmin = admin;
                var adminSnapshot = new AdminListSnapshot("cssharp", "CounterStrikeSharp", true, ["admins.json"],
                    [new("#hcsl/admin", ["@css/kick"], 50)],
                    [new("76561198000000000", "CaIam1Tas", ["#hcsl/admin"], [], 100, [])],
                    admin.ExpectedHash ?? "", [], AdminRules.CounterStrikeSharpCapabilities, 1, admin.Offset, admin.Limit <= 0 ? AdminRules.DefaultPageSize : admin.Limit);
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true,
                    AdminResultJson: JsonSerializer.Serialize(new AdminResult(adminSnapshot, "HCSL-ADMIN-WRITTEN", ["Upsert"],
                        admin.Action == AdminAction.Reload ? "css_admins_reload sent" : null))));
            }
            if (request.Kind == RequestKind.Server && JsonSerializer.Deserialize<ServerCommand>(request.ServerCommandJson!)!.Action == ServerAction.Players)
            {
                var roster = new PlayerRoster(DateTimeOffset.UtcNow,
                    [new(2, "CaIam1Tas", false, "192.168.1.10:61466", "active", "00:12", 1, "76561198000000000"),
                     new(0, "Bank", true, "", "active", "BOT", 0)]);
                var snap = new ServerSnapshot(Run, First, ServerState.Running, true, "HCSL-SERVER-MAP-READY", true, false, null, 1, false, [], PlayerRoster: roster);
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, ServerSnapshotJson: JsonSerializer.Serialize(snap)));
            }
            if (request.Kind == RequestKind.Server && JsonSerializer.Deserialize<ServerCommand>(request.ServerCommandJson!)!.Action == ServerAction.Kick)
            {
                var kick = JsonSerializer.Deserialize<ServerKickCommand>(request.ServerKickJson!)!;
                LastKick = kick;
                var result = new ServerKickResult(true, "HCSL-KICK-WRITTEN", kick.UserId, kick.SteamId64,
                    "kickid " + kick.UserId, new(kick.UserId, "CaIam1Tas", false, "192.168.1.10:61466", "active", "00:12", 1, kick.SteamId64));
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, ServerKickResultJson: JsonSerializer.Serialize(result)));
            }
            var command = JsonSerializer.Deserialize<ServerCommand>(request.ServerCommandJson!)!;
            var code = "HCSL-SERVER-STOPPED";
            if (command.Action == ServerAction.Start) { LastStart = command; current = command.InstanceId; active = true; code = "HCSL-SERVER-MAP-READY"; }
            if (command.Action == ServerAction.Stop) { StopCalls++; StopRun = command.RunId; active = false; }
            if (command.Action == ServerAction.Recover) { Recovered = command.InstanceId; Recover = false; }
            if (command.Action == ServerAction.Deploy) { DeployCalls++; current = command.InstanceId; active = false; code = "HCSL-SERVER-DEPLOYED"; }
            if (command.Action == ServerAction.RecoverDeployment) { RecoverDeployments++; current = command.InstanceId; active = false; code = "HCSL-SERVER-DEPLOY-RECOVERED"; }
            if (command.Action == ServerAction.PlanDeployment)
            {
                PlanDeployments++;
                var plan = new DeploymentPlan(command.InstanceId, 1, preset, 1, "123", "/library/runtime/preview", DeploymentCopyMode.Reflink,
                    3140, 1024, 1024, 1024, [], [], null, null, null, null);
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, DeploymentPlanJson: JsonSerializer.Serialize(plan)));
            }
            var snapshot = new ServerSnapshot(active || Recover ? Run : null, Recover ? Second : current == Guid.Empty ? null : current,
                Recover ? ServerState.RecoveryRequired : active ? ServerState.Running : ServerState.Stopped,
                active, Recover ? "HCSL-SERVER-RECOVERY" : code, active, !active && !Recover,
                null, 1, false, [], Map: active ? "de_dust2" : null);
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, ServerSnapshotJson: JsonSerializer.Serialize(snapshot)));
        }
    }
}
