using System.Text.Json;
using HCSL.Contracts;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Tui.Views;
using HCSL.Tui.Views.Controls;
using Terminal.Gui;

public static class CfgEditorTests
{
    public static void Run()
    {
        using (var root = new Dialog("Vim", 70, 22))
        {
            var editor = new VimCfgTextView { Width = 60, Height = 15 };
            root.Add(editor);
            root.KeyPress += e => { if (e.KeyEvent.Key == Key.Esc) { editor.Normal(); e.Handled = true; } };
            var state = Application.Begin(root);
            try
            {
                editor.LoadDocument("abc\ndef"); editor.SetFocus();
                Keys(root, "iX"); Press(root, Key.Esc);
                Check(editor.Text.ToString() == "Xabc\ndef" && !editor.Inserting, $"Insert/Esc failed: [{editor.Text}] insert={editor.Inserting} focus={editor.HasFocus}");
                Keys(root, "u"); Check(editor.Text.ToString() == "abc\ndef", "Undo failed.");
                Keys(root, "i"); Press(root, Key.Esc); // Empty insert must not consume redo.
                Press(root, Key.CtrlMask | Key.R); Check(editor.Text.ToString() == "Xabc\ndef", "Redo failed.");
                Keys(root, "ggdd"); Check(editor.Text.ToString() == "def", "gg/dd failed.");
                Keys(root, "uG$"); Check(editor.CurrentRow == 1 && editor.CurrentColumn == 3, "G/$ failed.");
                Keys(root, "oZ"); Press(root, Key.Esc); Check(editor.Text.ToString()!.EndsWith("\nZ"), "o failed.");
                editor.LoadDocument("中文"); Keys(root, "aX"); Press(root, Key.Esc);
                Check(editor.Text.ToString() == "中X文", "Rune cursor insert failed.");
                Keys(root, "0x"); Check(editor.Text.ToString() == "X文", "Rune delete failed.");
                Keys(root, "zv?"); Check(editor.Text.ToString() == "X文", "Normal mode inserted text.");
                editor.LoadDocument("abc"); Keys(root, "OA"); Press(root, Key.Esc);
                Check(editor.Text.ToString() == "A\nabc", "O failed.");
            }
            finally { Application.End(state); }
        }
        var daemon = new Fake();
        using var server = new ServerController(daemon, a => a(), activeInterval: TimeSpan.FromHours(1), idleInterval: TimeSpan.FromHours(1));
        server.ConnectAsync().GetAwaiter().GetResult();
        var client = new PresetCfgClient(new InstanceClient(daemon), server);
        using var dialog = new CfgEditorDialog(client, server, daemon.Instance, daemon.Preset);
        var runState = Application.Begin(dialog);
        try
        {
            dialog.Initialize(); Pump();
            dialog.Editor.SetFocus();
            Keys(dialog, ":new practice.cfg"); Press(dialog, Key.Enter);
            Keys(dialog, "imp_freezetime 3"); Press(dialog, Key.Esc);
            Keys(dialog, ":w"); Press(dialog, Key.Enter); Pump();
            Check(daemon.Files["practice.cfg"] == "mp_freezetime 3" && !dialog.Dirty && daemon.Console.Count == 0, "Save must not exec.");
            Keys(dialog, "A4"); Press(dialog, Key.Esc);
            daemon.FailSave = true; dialog.Execute(":w"); Pump();
            Check(dialog.Dirty && dialog.Editor.Text.ToString() == "mp_freezetime 34", "Failed save lost draft.");
            daemon.FailSave = false;
            AutoChoice("取消"); dialog.Execute(":q"); Pump();
            Check(dialog.Dirty && dialog.Editor.Text.ToString() == "mp_freezetime 34", "Cancel close lost draft.");
            dialog.Execute(":w"); Pump();
            AutoChoice("取消"); dialog.Execute(":delete"); Pump();
            Check(daemon.Files.ContainsKey("practice.cfg"), "Cancelled deletion changed file.");
            AutoChoice("确认"); dialog.Execute(":rename renamed.cfg"); Pump();
            Check(dialog.CurrentName == "renamed.cfg" && daemon.Files.ContainsKey("renamed.cfg"), "Rename lost file identity.");
            daemon.Status = daemon.Status with { State = ServerState.Running, Active = true, RunId = daemon.Run, InstanceId = daemon.Instance.Id, Preset = new(daemon.Preset.Id, 1, "Preset", "de_dust2") };
            server.RefreshAsync().GetAwaiter().GetResult();
            AutoChoice("取消"); dialog.Execute(":exec"); Pump(); Check(daemon.Console.Count == 0, "Exec cancel sent command.");
            AutoChoice("确认"); dialog.Execute(":exec"); Pump();
            Check(daemon.Console.Count == 1 && daemon.Console[0].RunId == daemon.Run && daemon.Console[0].Text == "exec renamed.cfg", "Exec lost captured run/file.");
            Keys(dialog, "iX"); Press(dialog, Key.Esc); dialog.Execute(":w"); Pump();
            Check(dialog.Dirty && daemon.Files["renamed.cfg"] == "mp_freezetime 34", "Active write guard failed or lost draft.");
            Press(dialog, Key.F6); Press(dialog, Key.F2); Press(dialog, Key.Esc);
            Check(dialog.Editor.HasFocus && !dialog.Editor.Inserting, "F2/Esc must return to normal editor.");
            dialog.Execute(":wq"); Pump();
            Check(dialog.Dirty && dialog.CurrentName == "renamed.cfg", "Failed wq closed or lost draft.");
            daemon.Status = daemon.Status with { State = ServerState.Stopped, Active = false };
            server.RefreshAsync().GetAwaiter().GetResult();
            dialog.Execute(":wq"); Pump();
            Check(dialog.CurrentName is null && dialog.Editor.Text.ToString() == "" && daemon.Console.Count == 1, "wq did not save/clear/close without exec.");
        }
        finally { Application.End(runState); }
        Console.WriteLine("PASS tui.cfg.vim-unicode-undo-modal-save-conflict-rename-cancel-run-bound-exec-and-active-write-guard (FakeDriver)");
    }
    private static void Press(Toplevel root, Key key)
    {
        if (root is not CfgEditorDialog && key == Key.Esc && root.MostFocused is VimCfgTextView editor)
            editor.ProcessKey(new(key, new KeyModifiers()));
        else root.ProcessKey(new(key, new KeyModifiers()));
    }
    private static void Keys(Toplevel root, string text) { foreach (var c in text) Press(root, (Key)c); }
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
        public readonly Dictionary<string, string> Files = new();
        public readonly List<ServerConsoleCommand> Console = [];
        public bool FailSave;
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
            if (FailSave && c.Action == PresetCfgAction.Save) throw new IOException("HCSL-CFG-CONFLICT");
            var name = c.Name;
            if (c.Action == PresetCfgAction.Save) Files[name!] = c.Content!;
            if (c.Action == PresetCfgAction.Rename) { Files[c.NewName!] = Files[name!]; Files.Remove(name!); }
            if (c.Action == PresetCfgAction.Delete) Files.Remove(name!);
            var text = name is not null && Files.TryGetValue(name, out var body) ? body : null;
            var result = new PresetCfgResult(Files.Select(f => new PresetCfgFile(f.Key, f.Value.Length)).ToArray(), name, text, "hash");
            return Task.FromResult(Response() with { PresetCfgResultJson = JsonSerializer.Serialize(result) });
        }
    }
}
