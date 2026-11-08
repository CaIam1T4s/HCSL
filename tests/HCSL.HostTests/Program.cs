using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HCSL.Core;
using HCSL.Contracts;
using HCSL.Frontend;
using HCSL.Host.Services;
using HCSL.Host.ViewModels;
using HCSL.Host.Views;
using HCSL.UI.Shared.Styles.Themes;
using System.Text.Json;
using TKCode.Arc;

// Real Arc templates, fake IPC only: no desktop, Steam, files, daemon or CS2 process.
AppBuilder.Configure<HCSL.Host.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions())
    .UseArc(a => a.UseArcControls().WithTheme(t => t.Appearance(ArcAppearance.Dark).Preset(ThemeCatalog.ArcPreset)))
    .SetupWithoutStarting();
var daemon = new FakeDaemon();
using var server = new ServerController(daemon, a => Dispatcher.UIThread.Post(a));
using var model = new HostServerController(server, new InstanceClient(daemon));
using var steam = new SteamCmdController(daemon, a => Dispatcher.UIThread.Post(a));
PumpUntil(server.ConnectAsync());
var owner = new MainWindow
{
    DataContext = new ShellViewModel(new ThemeService(Avalonia.Application.Current!), steam, model)
};
owner.Show();
Layout();
for (var attempt = 0; attempt < 2; attempt++)
{
    var log = new ServerLogWindow(model, owner);
    log.Show(owner);
    PumpUntil(server.RefreshLogsAsync());
    Layout();
    var area = log.GetVisualDescendants().OfType<Textarea>().Single();
    var scroll = area.GetVisualDescendants().OfType<ScrollViewer>().Single();
    Check(area.FrameHeight > 0 && area.FrameHeight < log.ClientSize.Height, "Editor must fit the finite window height.");
    Check(scroll.Extent.Height > scroll.Viewport.Height, "Long logs must have a vertical scroll range.");
    Check(scroll.Extent.Width > scroll.Viewport.Width, "Long lines must have a horizontal scroll range.");
    scroll.Offset = new Vector(40, 100);
    Layout();
    Check(scroll.Offset.Y > 0 && scroll.Offset.X > 0, "Both axes must be scrollable after reopen.");
    log.Height = 420;
    Layout();
    Check(area.FrameHeight < 420, "Resizing must resize the internal Arc frame.");
    var prompt = owner.ChooseAsync("测试确认", "日志不得覆盖此浮层。", "返回");
    Layout();
    Check(!log.IsVisible, "Prompt must hide owned log windows.");
    Click(owner, "返回");
    PumpUntil(prompt);
    Layout();
    Check(log.IsVisible, "Log must return after the prompt.");
    log.Close();
    Layout();
}
Console.WriteLine("PASS gui.logs.reopen-resize-scroll-and-prompt-restore");
var downloads = new HCSL.Host.Views.Pages.DownloadsPage { DataContext = owner.DataContext };
var downloadWindow = new Window { Width = 900, Height = 700, Content = downloads };
downloadWindow.Show(owner);
Layout();
var taskLog = downloads.GetVisualDescendants().OfType<Textarea>().Single();
taskLog.Text = string.Join('\n', Enumerable.Range(1, 150).Select(i => $"Preparing content {i}"));
Layout();
var taskScroll = taskLog.GetVisualDescendants().OfType<ScrollViewer>().Single();
Check(taskLog.Bounds.Height == 220 && taskLog.FrameHeight == 220 && taskLog.ClipToBounds,
    "Download log must constrain both its outer bounds and internal frame.");
Check(taskScroll.Viewport.Height > 0 && taskScroll.Viewport.Height < 220 && taskScroll.Extent.Height > taskScroll.Viewport.Height,
    "Download logs must scroll inside their bounded frame.");
taskScroll.Offset = new Vector(0, 100);
Layout();
Check(taskScroll.Offset.Y > 0, "Download log must remain scrollable.");
downloadWindow.Close();
Console.WriteLine("PASS gui.downloads.task-log-is-bounded-and-scrollable");
var saveCalls = 0;
HCSL.Frontend.HostInstanceDraft? savedDraft = null;
var editor = owner.EditInstanceAsync(null, draft =>
{
    saveCalls++; savedDraft = draft; return Task.FromResult<string?>(null);
});
Layout();
var inputs = owner.GetVisualDescendants().OfType<TKCode.Arc.Input>().ToArray();
Check(inputs.Length == 3, "Instance form must contain name, port and address fields.");
inputs[0].Text = "New instance"; inputs[1].Text = "not-a-port";
Click(owner, "保存");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("HCSL-INSTANCE-PORT") == true));
Check(saveCalls == 0 && !editor.IsCompleted, "Invalid input must retain the dialog without saving.");
inputs[1].Text = "27017";
Check(inputs[2].IsReadOnly, "Automatically detected address must be read-only.");
Check(!owner.GetVisualDescendants().OfType<TKCode.Arc.Select>().Any(s => s.ItemsSource is string[]), "Instance form must not imply a bind/isolation mode.");
Click(owner, "保存");
PumpUntil(editor);
Check(editor.Result && saveCalls == 1 && savedDraft is { GamePort: 27017, Network: null }, "Saving must not write an automatically discovered address into instance settings.");
var cancelEditor = owner.EditInstanceAsync(null, _ => { saveCalls++; return Task.FromResult<string?>(null); });
Layout();
Click(owner, "取消");
PumpUntil(cancelEditor);
Check(!cancelEditor.Result && saveCalls == 1, "Cancelled editor must not save.");
Console.WriteLine("PASS gui.instances.editor-validation-readonly-address-save-and-cancel");
var finalLog = new ServerLogWindow(model, owner);
finalLog.Show(owner);
owner.Close();
Layout();
Check(!finalLog.IsVisible, "Exit confirmation must hide log.");
Click(owner, "取消");
WaitUntil(() => finalLog.IsVisible);
Check(owner.IsVisible && daemon.Stops == 0, "Cancelling exit must retain both windows without stopping.");
owner.Close();
Layout();
Click(owner, "停服后退出");
WaitUntil(() => !owner.IsVisible);
Check(daemon.Stops == 1, "Confirmed exit must stop the captured server run exactly once.");
Check(!finalLog.IsVisible && !owner.OwnedWindows.Any(), "Confirmed exit must close auxiliary windows.");
Console.WriteLine("PASS gui.exit.stop-confirmed-closes-owned-logs");
return 0;

static void Layout()
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
}
static void WaitUntil(Func<bool> condition)
{
    var timeout = System.Diagnostics.Stopwatch.StartNew();
    while (!condition())
    {
        if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("GUI operation timed out.");
        Layout(); Thread.Sleep(5);
    }
    Layout();
}
static void PumpUntil(Task task) { WaitUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Click(MainWindow owner, string label) => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>()
    .Single(b => Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

sealed class FakeDaemon : IDaemonClient
{
    private readonly Guid run = Guid.Parse("68fa2944-7d6b-4373-be01-368879164976");
    private bool stopped;
    public int Stops { get; private set; }
    public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
    {
        if (request.ServerCommandJson is { } json && JsonSerializer.Deserialize<ServerCommand>(json) is { Action: ServerAction.Stop } command)
        {
            if (command.RunId != run) throw new Exception("Stop must carry the confirmed RunId.");
            stopped = true; Stops++;
        }
        var lines = Enumerable.Range(1, 150).Select(i => new ServerLogLine(i, $"{i} " + new string('x', 300))).ToArray();
        var snapshot = new ServerSnapshot(run, null, stopped ? ServerState.Stopped : ServerState.Running,
            !stopped, stopped ? "HCSL-SERVER-STOPPED" : "HCSL-SERVER-MAP-READY", true, stopped, null, 151, false, lines);
        return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true,
            Defaults: new("/settings", "/data", "/library"), ServerSnapshotJson: JsonSerializer.Serialize(snapshot)));
    }
}
