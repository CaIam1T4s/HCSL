using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Tui.Views;
using Terminal.Gui;

public static class CfgBrowserTests
{
    public static void Run()
    {
        var daemon = new Fake();
        using var server = new ServerController(daemon, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1));
        server.ConnectAsync().GetAwaiter().GetResult();
        using var dialog = new CfgBrowserDialog(new PresetCfgClient(new InstanceClient(daemon), server), server, daemon.Instance, daemon.Preset);
        var state = Application.Begin(dialog);
        try
        {
            dialog.Initialize(); Pump();
            Check(dialog.CurrentPath == "", "Browser did not start at the root.");
            Check(dialog.Entries.Any(e => e.Kind == PresetCfgKind.Directory && e.Name == "pt"), "pt directory missing.");
            Check(dialog.Entries.Any(e => e.Kind == PresetCfgKind.File && e.Name == "server.cfg"), "root cfg missing.");
            Check(!dialog.Entries.Any(e => e.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)), "Non-cfg file listed.");
            Select(dialog, "pt"); Press(dialog, Key.Enter); Pump();
            Check(dialog.CurrentPath == "pt", "Enter did not open pt.");
            Check(dialog.Entries.Any(e => e.Kind == PresetCfgKind.Directory && e.Name == "map"), "nested map directory missing.");
            Select(dialog, "map"); Press(dialog, Key.Enter); Pump();
            Check(dialog.CurrentPath == "pt/map", "Nested directory did not open.");
            Select(dialog, "dust2.cfg");
            daemon.Status = daemon.Status with { State = ServerState.Running, Active = true, RunId = daemon.Run, InstanceId = daemon.Instance.Id, Preset = new(daemon.Preset.Id, 1, "Preset", "de_dust2") };
            server.RefreshAsync().GetAwaiter().GetResult();
            AutoChoice("执行"); Press(dialog, Key.E);
            for (var i = 0; i < 200 && daemon.Console.Count == 0; i++) Pump();
            Check(daemon.Console.Count == 1 && daemon.Console[0].Text == "exec pt/map/dust2.cfg", "Exec did not capture the nested cfg path.");
            Check(daemon.Writes == 0, "Read-only browser wrote a file.");
            Check(dialog.CurrentPath == "pt/map", "Exec changed the browsed directory.");
            Press(dialog, Key.Esc);
        }
        finally { Application.End(state); }
        Console.WriteLine("PASS tui.cfg-tree-readonly-nested-exec (FakeDriver)");
    }
    private static void Select(CfgBrowserDialog dialog, string name)
    {
        var index = Array.FindIndex(dialog.Entries, e => e.Name == name);
        Check(index >= 0, "Entry not found: " + name);
        dialog.List.SelectedItem = index; dialog.List.SetFocus();
    }
    private static void Press(Toplevel root, Key key) { root.ProcessKey(new(key, new KeyModifiers())); }
    private static void Pump() { for (var i = 0; i < 20; i++) { Application.MainLoop.MainIteration(); Thread.Sleep(2); } }
    private static IEnumerable<View> Descendants(View view) => view.Subviews.SelectMany(v => new[] { v }.Concat(Descendants(v)));
    private static void AutoChoice(string caption) => Application.MainLoop.AddIdle(() =>
    {
        var current = Application.Current;
        var button = Descendants(current).OfType<Button>().First(b => (b.Text.ToString() ?? "").Contains(caption));
        button.SetFocus(); Press(current, Key.Enter); return false;
    });
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private sealed class Fake : IDaemonClient
    {
        public readonly Guid Run = Guid.NewGuid();
        public readonly PresetDefinition Preset = new(1, Guid.NewGuid(), 1, "Preset");
        public InstanceDefinition Instance => new(1, id, 1, "Instance", 27015, Preset.Id);
        private readonly Guid id = Guid.NewGuid();
        public ServerSnapshot Status = new(null, null, ServerState.Idle, false, "", false, true, null, 1, false, []);
        public readonly List<ServerConsoleCommand> Console = [];
        public int Writes;
        public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
        {
            LocalResponse Response() => new(LocalProtocol.Version, request.Id, true, Defaults: new("/settings", "/data", "/library"));
            if (request.Kind == RequestKind.Hello) return Task.FromResult(Response());
            if (request.Kind == RequestKind.Server) return Task.FromResult(Response() with { ServerSnapshotJson = JsonSerializer.Serialize(Status) });
            if (request.Kind == RequestKind.ServerConsole)
            {
                var command = JsonSerializer.Deserialize<ServerConsoleCommand>(request.ServerConsoleJson!)!; Console.Add(command);
                return Task.FromResult(Response() with { ConsoleReceiptJson = JsonSerializer.Serialize(new ServerConsoleReceipt(command.RunId, command.CommandId, ConsoleDelivery.Written, "written", DisplayText: command.Text)) });
            }
            var c = JsonSerializer.Deserialize<PresetCfgCommand>(request.PresetCfgJson!)!;
            if (c.Action is not (PresetCfgAction.List or PresetCfgAction.Read)) Writes++;
            var path = c.Path ?? "";
            if (c.Action == PresetCfgAction.List)
            {
                var entries = path switch
                {
                    "" => new[] { new PresetCfgFile("pt", 0, PresetCfgKind.Directory, "pt"), new PresetCfgFile("server.cfg", 10, PresetCfgKind.File, "server.cfg") },
                    "pt" => new[] { new PresetCfgFile("map", 0, PresetCfgKind.Directory, "pt/map"), new PresetCfgFile("pt.cfg", 5, PresetCfgKind.File, "pt/pt.cfg") },
                    "pt/map" => new[] { new PresetCfgFile("dust2.cfg", 7, PresetCfgKind.File, "pt/map/dust2.cfg") },
                    _ => []
                };
                return Task.FromResult(Response() with { PresetCfgResultJson = JsonSerializer.Serialize(new PresetCfgResult(entries, Path: path, Parent: path.Length == 0 ? null : "")) });
            }
            if (c.Action == PresetCfgAction.Read)
                return Task.FromResult(Response() with { PresetCfgResultJson = JsonSerializer.Serialize(new PresetCfgResult([], c.Name, "echo test", "hash")) });
            throw new IOException("Unexpected cfg write.");
        }
    }
}
