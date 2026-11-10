using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Tui.Views;
using Terminal.Gui;
public static class PluginTests
{
    public static void Run()
    {
        var daemon = new Fake();
        using var server = new ServerController(daemon, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1));
        server.ConnectAsync().GetAwaiter().GetResult();
        var instanceClient = new InstanceClient(daemon);
        using var dialog = new PluginsDialog(new(instanceClient, server), instanceClient, daemon.Instance, daemon.Preset, english: true);
        var state = Application.Begin(dialog);
        try
        {
            dialog.Initialize(); Pump();
            var list = Descendants(dialog).OfType<ListView>().Single();
            void Click(string caption)
            {
                var button = Descendants(dialog).OfType<Button>().Single(b => b.Text.ToString() == caption);
                button.SetFocus(); dialog.ProcessKey(new(Key.Enter, new KeyModifiers())); Pump();
            }
            Check(list.Source.Count == 1 && list.Source.ToList()[0]!.ToString()!.Contains("Metamod"), "Plugins not loaded.");
            Click("Toggle"); Click("Save");
            Check(daemon.Plugins.Selected.Single().Enabled == false && daemon.Writes == 1, "Toggle/save failed.");
            daemon.Status = daemon.Status with { Active = true, State = ServerState.Running };
            server.RefreshAsync().GetAwaiter().GetResult();
            Click("Toggle"); Click("Save");
            Check(daemon.Plugins.Selected.Single().Enabled == false && daemon.Writes == 1, "Live mutation admitted.");
            Check(daemon.ConsoleCalls == 0, "Plugin editing sent a console command.");
        }
        finally { Application.End(state); }
        Console.WriteLine("PASS tui.plugin-toggle-save-and-active-guard (FakeDriver)");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Pump() { for (var i = 0; i < 20; i++) { Application.MainLoop.MainIteration(); Thread.Sleep(2); } }
    private static IEnumerable<View> Descendants(View view) => view.Subviews.SelectMany(v => new[] { v }.Concat(Descendants(v)));
    private sealed class Fake : IDaemonClient
    {
        public readonly PresetDefinition Preset = new(1, Guid.NewGuid(), 1, "Preset");
        public InstanceDefinition Instance => new(1, id, 1, "Instance", 27015, Preset.Id);
        private readonly Guid id = Guid.NewGuid();
        public ServerSnapshot Status = new(null, null, ServerState.Idle, false, "", false, true, null, 1, false, []);
        public PluginResult Plugins = new([new("metamod", "Metamod", "1.0.0", "hash", 1024, 2)], [new("metamod", "Metamod", "1.0.0", true, true)], 1, "one");
        public int Writes, ConsoleCalls;
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
        {
            LocalResponse Response() => new(LocalProtocol.Version, request.Id, true, Defaults: new("/settings", "/data", "/library"));
            if (request.Kind == RequestKind.Hello) return Task.FromResult(Response());
            if (request.Kind == RequestKind.PluginCfg)
            {
                var cfgCommand = JsonSerializer.Deserialize<PluginCfgCommand>(request.PluginCfgJson!)!;
                var json = cfgCommand.Action is PluginCfgAction.Preview or PluginCfgAction.CleanupPreview
                    ? JsonSerializer.Serialize(new PluginCfgPreview([], [], [], [], []))
                    : JsonSerializer.Serialize(new PluginCfgResult(0, 0, 0, []));
                return Task.FromResult(Response() with { PluginCfgResultJson = json });
            }
            if (request.Kind == RequestKind.Server) return Task.FromResult(Response() with { ServerSnapshotJson = JsonSerializer.Serialize(Status) });
            if (request.Kind == RequestKind.ServerConsole) { ConsoleCalls++; throw new Exception("Plugins must not send console commands."); }
            var command = JsonSerializer.Deserialize<PluginCommand>(request.PluginJson!)!;
            if (command.Action == PluginAction.Save)
            {
                Writes++;
                Plugins = new(Plugins.Available, (command.Plugins ?? []).Select(p => new PluginSelectionInfo(p.Id, "Metamod", p.Version, p.Enabled, true)).ToArray(), Plugins.Revision + 1, "next");
            }
            return Task.FromResult(Response() with { PluginResultJson = JsonSerializer.Serialize(Plugins) });
        }
    }
}
