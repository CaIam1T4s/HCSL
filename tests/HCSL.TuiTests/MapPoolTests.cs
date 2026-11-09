using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Tui.Views;
using Terminal.Gui;
public static class MapPoolTests
{
    public static void Run()
    {
        var daemon = new Fake();
        using var server = new ServerController(daemon, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1));
        server.ConnectAsync().GetAwaiter().GetResult();
        using var dialog = new MapPoolDialog(new(new InstanceClient(daemon), server), daemon.Instance, daemon.Preset, english: true);
        var state = Application.Begin(dialog);
        try
        {
            dialog.Initialize(); Pump();
            var entry = Descendants(dialog).OfType<TextField>().Single();
            var list = Descendants(dialog).OfType<ListView>().Single();
            void Click(string caption)
            {
                var button = Descendants(dialog).OfType<Button>().Single(b => b.Text.ToString() == caption);
                button.SetFocus(); dialog.ProcessKey(new(Key.Enter, new KeyModifiers())); Pump();
            }
            Check(list.Source.Count == 2, "Pool not loaded.");
            list.SelectedItem = 1; Click("Up"); Check(list.Source.ToList()[0]!.ToString()!.Contains("de_mirage"), "Reorder failed.");
            daemon.FailSave = true; Click("Save");
            Check(daemon.Writes == 0 && list.Source.ToList()[0]!.ToString()!.Contains("de_mirage"), "Conflict lost draft.");
            daemon.FailSave = false; Click("Save"); Check(daemon.Pool.Pool.Maps[0].Value == "de_mirage", "Save changed order.");
            entry.Text = "standard de_nuke"; Click("Add"); Check(list.Source.Count == 3, "Add failed.");
            Click("Add"); Check(list.Source.Count == 3, "Duplicate accepted.");
            Click("Delete"); Check(list.Source.Count == 2, "Delete failed.");
            daemon.Status = daemon.Status with { Active = true, State = ServerState.Running };
            server.RefreshAsync().GetAwaiter().GetResult();
            Click("Delete"); Click("Save"); Check(list.Source.Count == 2 && daemon.Writes == 1, "Live mutation admitted.");
            Check(daemon.ConsoleCalls == 0, "Pool editing sent a console command.");
        }
        finally { Application.End(state); }
        Console.WriteLine("PASS tui.map-pool-add-delete-order-conflict-draft-save-no-console-and-active-guard (FakeDriver)");
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
        public MapPoolResult Pool = new(new(1, 1, [new(ServerMapKind.Standard, "de_dust2"), new(ServerMapKind.Standard, "de_mirage")]), "one");
        public bool FailSave;
        public int Writes, ConsoleCalls;
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
        {
            LocalResponse Response() => new(LocalProtocol.Version, request.Id, true, Defaults: new("/settings", "/data", "/library"));
            if (request.Kind == RequestKind.Hello) return Task.FromResult(Response());
            if (request.Kind == RequestKind.Server) return Task.FromResult(Response() with { ServerSnapshotJson = JsonSerializer.Serialize(Status) });
            if (request.Kind == RequestKind.ServerConsole) { ConsoleCalls++; throw new Exception("Pool must not send console commands."); }
            var command = JsonSerializer.Deserialize<MapPoolCommand>(request.MapPoolJson!)!;
            if (command.Action == MapPoolAction.Save)
            {
                if (FailSave) throw new IOException("HCSL-MAPPOOL-CONFLICT");
                Writes++; Pool = new(new(1, Pool.Pool.Revision + 1, command.Maps!), "next");
            }
            return Task.FromResult(Response() with { MapPoolResultJson = JsonSerializer.Serialize(Pool) });
        }
    }
}
