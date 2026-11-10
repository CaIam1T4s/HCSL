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
var only = ParseOnly(args);
AppBuilder.Configure<HCSL.Host.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions())
    .UseArc(a => a.UseArcControls().WithTheme(t => t.Appearance(ArcAppearance.Dark).Preset(ThemeCatalog.ArcPreset)))
    .SetupWithoutStarting();
if (only is "input")
{
    InputPointerPlacementTests.Run();
    Console.WriteLine("PASS only input");
    return 0;
}
if (only is null) InputPointerPlacementTests.Run();
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
if (only is null or "logs")
{
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
    var queryInput = log.GetVisualDescendants().OfType<TKCode.Arc.Input>().Single();
    queryInput.Text = "status";
    queryInput.Focus();
    var disabledDuringSend = false;
    queryInput.PropertyChanged += (_, e) => { if (e.Property.Name == "IsEnabled" && !queryInput.IsEnabled) disabledDuringSend = true; };
    log.GetVisualDescendants().OfType<TKCode.Arc.Button>().Single(b => Equals(b.Content, "发送命令"))
        .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    WaitUntil(() => model.Server.ConsoleStatus.Contains("HCSL-CONSOLE-WRITTEN"));
    Check(model.Server.Active && daemon.Queries == attempt + 1, "GUI query must reach IPC without stopping the run.");
    Layout();
    Check(Math.Abs(scroll.Offset.Y - (scroll.Extent.Height - scroll.Viewport.Height)) < 3, "Explicit submission must resume following at bottom.");
    Check(!disabledDuringSend && queryInput.IsFocused, "Sending must not disable or defocus the command field.");
    Click(log, "跳到最新"); Layout();
    Check(Math.Abs(scroll.Offset.Y - (scroll.Extent.Height - scroll.Viewport.Height)) < 3, "Jump latest must resume at bottom.");
    log.Close();
    Layout();
}
Console.WriteLine("PASS gui.logs.reopen-resize-scroll-and-prompt-restore");
var writeLog = new ServerLogWindow(model, owner); writeLog.Show(owner); Layout();
var writeInput = writeLog.GetVisualDescendants().OfType<TKCode.Arc.Input>().Single();
var writeButton = writeLog.GetVisualDescendants().OfType<TKCode.Arc.Button>().Single(b => Equals(b.Content, "发送命令"));
writeInput.Text = "mp_roundtime 60";
var beforeWrites = daemon.Queries;
writeButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
WaitUntil(() => daemon.Queries == beforeWrites + 1); Layout();
Check(writeLog.IsVisible && daemon.LastConsole is { Text: "mp_roundtime 60", ConfirmMutation: false }, "General console must send without mutation confirmation.");
writeInput.Focus(); writeInput.CaretIndex = 0;
writeLog.KeyPress(Avalonia.Input.Key.Up, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
writeLog.KeyRelease(Avalonia.Input.Key.Up, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
Layout();
Dispatcher.UIThread.RunJobs();
Layout();
Check(writeInput.Text == "mp_roundtime 60" && writeInput.CaretIndex == writeInput.Text.Length, "History recall must put caret at command end.");
var historyToggle = writeLog.GetVisualDescendants().OfType<TKCode.Arc.Checkbox>().Single();
Check(historyToggle.IsChecked == true, "History must default on.");
historyToggle.IsChecked = false; Layout();
Check(!server.ConsoleHistoryEnabled && server.ConsoleHistory.Count == 0, "Disable history must clear retained entries.");
writeLog.Close();
Console.WriteLine("PASS gui.console.general-send-and-history-toggle");
daemon.RepeatLogText = true;
var evictionLog = new ServerLogWindow(model, owner); evictionLog.Show(owner); Layout();
var evictionArea = evictionLog.GetVisualDescendants().OfType<Textarea>().Single();
var evictionScroll = evictionArea.GetVisualDescendants().OfType<ScrollViewer>().Single();
evictionScroll.Offset = new Vector(0, 100); Layout();
for (var end = 300; end <= 2400; end += 150)
{
    daemon.LastLogSequence = end;
    PumpUntil(server.RefreshLogsAsync()); Layout();
}
Check(evictionLog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("较早日志已移出缓冲区") == true), "Evicted reader must see a non-modal notice.");
Check(evictionScroll.Offset.Y < 5, "Evicted reader must be at earliest retained record, not latest.");
Click(evictionLog, "跳到最新"); Layout();
Check(evictionScroll.Offset.Y > 100, "Jump latest must leave the oldest retained record.");
evictionScroll.Offset = new Vector(0, 100); Layout();
daemon.LastLogSequence = 2550;
PumpUntil(server.RefreshLogsAsync()); Layout();
Check(evictionLog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("较早日志已移出缓冲区") == true), "Identical text with new sequence IDs must still evict the old reader anchor.");
Check(evictionScroll.Offset.Y < 5, "Repeated-text eviction must retain earliest position.");
evictionArea.Focus();
evictionLog.KeyPressQwerty(Avalonia.Input.PhysicalKey.End, Avalonia.Input.RawInputModifiers.None);
evictionLog.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.End, Avalonia.Input.RawInputModifiers.None);
Layout();
Check(evictionScroll.Offset.Y > 100, "End inside the real Arc editor must resume following.");
evictionLog.Close();
Console.WriteLine("PASS gui.logs.live-append-anchor-eviction-notice-and-jump-latest");
// Regression: the log stream updates faster than layout, so each projection must re-assert the
// bottom after layout instead of inferring "reader scrolled away" from a growing Extent.
var burstLog = new ServerLogWindow(model, owner); burstLog.Show(owner); Layout();
var burstArea = burstLog.GetVisualDescendants().OfType<Textarea>().Single();
var burstScroll = burstArea.GetVisualDescendants().OfType<ScrollViewer>().Single();
Check(Math.Abs(burstScroll.Offset.Y - (burstScroll.Extent.Height - burstScroll.Viewport.Height)) < 3, "Log must start following at bottom.");
for (var burst = 0; burst < 12; burst++)
{
    daemon.LastLogSequence += 37;
    PumpUntil(server.RefreshLogsAsync()); Layout();
}
Dispatcher.UIThread.RunJobs(); Layout();
Check(Math.Abs(burstScroll.Offset.Y - (burstScroll.Extent.Height - burstScroll.Viewport.Height)) < 3, "Rapid appends must keep following the newest line.");
// A real reader scroll must still pause following and keep the reading position.
burstScroll.Offset = new Vector(0, 10); Layout();
daemon.LastLogSequence += 60;
PumpUntil(server.RefreshLogsAsync()); Layout();
Check(burstScroll.Offset.Y < 40, "Reader scroll must pause following.");
Click(burstLog, "跳到最新"); Layout();
Check(Math.Abs(burstScroll.Offset.Y - (burstScroll.Extent.Height - burstScroll.Viewport.Height)) < 3, "Jump latest must resume following.");
burstLog.Close();
Console.WriteLine("PASS gui.logs.rapid-appends-keep-following-and-reader-scroll-pauses");
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
if (only is "logs") return 0;
}
if (only is null or "forms")
{
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
var presetCalls = 0;
PresetDefinition? savedPreset = null;
var presetEditor = owner.EditPresetAsync("新建配置预设", null, false, draft =>
{
    presetCalls++; savedPreset = draft; return Task.FromResult<string?>(null);
});
Layout();
var presetInputs = owner.GetVisualDescendants().OfType<TKCode.Arc.Input>().ToArray();
Check(presetInputs.Length == 4, "Preset editor must contain name, map, game_type and game_mode.");
presetInputs[0].Text = "Practice"; presetInputs[1].Text = "workshop/123/map";
Click(owner, "保存");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("HCSL-PRESET-INVALID") == true));
Check(presetCalls == 0 && !presetEditor.IsCompleted, "Invalid map must not be saved.");
presetInputs[1].Text = "de_mirage"; presetInputs[2].Text = "0"; presetInputs[3].Text = "2";
Click(owner, "保存"); PumpUntil(presetEditor);
Check(presetEditor.Result && savedPreset is { Name: "Practice", Map: "de_mirage", GameMode: 2 }, "Preset fields were lost.");
var sourcePreset = savedPreset! with { Id = Guid.NewGuid(), Revision = 4, VacPreferred = false };
var copyEditor = owner.EditPresetAsync("复制配置预设", sourcePreset, true, draft =>
{
    presetCalls++; savedPreset = draft; return Task.FromResult<string?>(null);
});
Layout();
Check(owner.GetVisualDescendants().OfType<TKCode.Arc.Input>().Count() == 1, "Copy dialog must edit only the name.");
Click(owner, "保存"); PumpUntil(copyEditor);
Check(copyEditor.Result && savedPreset is { Revision: 4, VacPreferred: false } && savedPreset.Name.EndsWith("副本"), "Copy draft modified source settings.");
var cancelledPreset = owner.EditPresetAsync("编辑配置预设", sourcePreset, false, _ => { presetCalls++; return Task.FromResult<string?>(null); });
Layout(); Click(owner, "取消"); PumpUntil(cancelledPreset);
Check(!cancelledPreset.Result && presetCalls == 2, "Cancelled preset editor saved changes.");
Console.WriteLine("PASS gui.presets.editor-validation-create-copy-preserve-settings-and-cancel");
var workshopEditor = owner.EditPresetAsync("工坊启动地图", sourcePreset, false, draft => { savedPreset = draft; return Task.FromResult<string?>(null); });
Layout();
owner.GetVisualDescendants().OfType<TKCode.Arc.Select>().Single(s => s.IsEffectivelyVisible).SelectedIndex = 1;
owner.GetVisualDescendants().OfType<TKCode.Arc.Input>().ToArray()[1].Text = "3070244462";
Click(owner, "保存"); PumpUntil(workshopEditor);
Check(savedPreset is { MapKind: ServerMapKind.Workshop, Map: "3070244462", VacPreferred: false }, "Workshop editor lost source/ID or changed VAC.");
var mapTarget = owner.MapTargetAsync(); Layout();
var mapValue = owner.GetVisualDescendants().OfType<TKCode.Arc.Input>().Single(t => t.IsEffectivelyVisible);
mapValue.Text = "de_dust2;quit"; Click(owner, "下一步");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("HCSL-MAP-INVALID") == true));
Check(!mapTarget.IsCompleted && mapValue.Text == "de_dust2;quit", "Invalid map target was accepted or draft discarded.");
Click(owner, "取消"); PumpUntil(mapTarget); Check(mapTarget.Result is null, "Cancelled map target returned a command.");
Console.WriteLine("PASS gui.maps.workshop-preset-invalid-target-preserved-and-cancel");
if (only is "forms") return 0;
}

HCSL.Host.Views.Pages.InstancePage presetPage = null!;
PresetDefinition firstPreset = null!;
if (only is null or "presets" or "pools" or "plugins" or "cfg" or "maps" or "start")
{
firstPreset = new PresetDefinition(1, Guid.NewGuid(), 1, "Selected preset");
var secondPreset = firstPreset with { Id = Guid.NewGuid(), Name = "Browsed preset", Map = "de_nuke" };
var presetInstanceId = Guid.NewGuid();
daemon.Catalog = new([new(presetInstanceId, new(1, presetInstanceId, 1, "Preset GUI fixture", 27015, firstPreset.Id), [firstPreset, secondPreset])]);
daemon.IdleForPresetTests = true;
PumpUntil(server.RefreshAsync()); PumpUntil(model.RefreshInstancesAsync());
((ShellViewModel)owner.DataContext!).NavigateCommand.Execute("instance"); Layout();
presetPage = owner.GetVisualDescendants().OfType<HCSL.Host.Views.Pages.InstancePage>().Single();
var presetSelect = presetPage.FindControl<TKCode.Arc.Select>("PresetSelect")!;
Check(presetSelect.ItemsSource!.Cast<HostPresetItem>().Count() == 2, "GUI preset list missing catalog entries.");
presetSelect.SelectedIndex = 1; Layout();
Check(model.Selected!.Entry.Definition!.SelectedPresetId == firstPreset.Id, "Browsing changed persisted selection.");
Click(owner, "设为当前所选");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "选择配置预设"));
Click(owner, "取消");
WaitUntil(() => presetPage.FindControl<TKCode.Arc.Button>("CreatePresetButton")!.IsEnabled);
Check(model.Selected.Entry.Definition.SelectedPresetId == firstPreset.Id, "Cancelled selection changed preset.");
if (only is "presets") { Console.WriteLine("PASS only presets"); return 0; }
}

if (only is null or "pools")
{
var poolQueries = daemon.Queries;
var poolDialog = owner.EditMapPoolAsync(model, model.Selected!.Entry.Definition!, firstPreset); Layout();
var poolSelect = owner.GetVisualDescendants().OfType<TKCode.Arc.Select>().Single(s => s.IsEffectivelyVisible && s.ItemsSource is string[]);
poolSelect.SelectedIndex = 1; Click(owner, "上移");
WaitUntil(() => poolSelect.ItemsSource!.Cast<string>().First().Contains("de_mirage"));
daemon.RejectPoolWrites = true; Click(owner, "保存地图池");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("HCSL-MAPPOOL-CONFLICT") == true));
Check(poolSelect.ItemsSource!.Cast<string>().First().Contains("de_mirage") && daemon.PoolWrites == 0, "Conflict lost map pool draft.");
daemon.RejectPoolWrites = false; Click(owner, "保存地图池");
WaitUntil(() => daemon.PoolWrites == 1);
Check(daemon.Pool.Pool.Maps[0].Value == "de_mirage" && daemon.Queries == poolQueries, "Pool save reordered incorrectly or sent a game command.");
Click(owner, "删除"); WaitUntil(() => poolSelect.ItemsSource!.Cast<string>().Count() == 1);
Click(owner, "关闭"); WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "未保存的地图池"));
Click(owner, "继续编辑"); WaitUntil(() => poolSelect.IsEffectivelyVisible);
Check(poolSelect.ItemsSource!.Cast<string>().Count() == 1, "Cancelled close lost draft.");
Click(owner, "关闭"); WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "未保存的地图池"));
Click(owner, "丢弃草稿"); PumpUntil(poolDialog);
Console.WriteLine("PASS gui.map-pool-order-save-conflict-preserves-draft-no-console-and-dirty-close");
if (only is "pools") return 0;
}

if (only is null or "plugins")
{
var pluginQueries = daemon.Queries;
var pluginDialog = owner.EditPluginsAsync(model, model.Selected!.Entry.Definition!, firstPreset); Layout();
WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Select>().Any(s => s.IsEffectivelyVisible && s.ItemsSource is string[]));
var pluginSelect = owner.GetVisualDescendants().OfType<TKCode.Arc.Select>().Single(s => s.IsEffectivelyVisible && s.ItemsSource is string[]);
Check(pluginSelect.ItemsSource!.Cast<string>().Single().Contains("Metamod"), "Plugin list missing selected package.");
Click(owner, "启用/禁用");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("已切换启用状态草稿") == true));
Click(owner, "保存清单");
WaitUntil(() => daemon.PluginWrites == 1);
Check(daemon.Plugins.Selected.Single().Enabled == false && daemon.Queries == pluginQueries, "Plugin save changed live game state or lost toggle.");
Click(owner, "关闭"); PumpUntil(pluginDialog);
Console.WriteLine("PASS gui.plugins.toggle-save-no-console");
if (only is "plugins") return 0;
}

if (only is null or "errors")
{
    if (daemon.Catalog.Instances.Length == 0)
    {
        var errorPreset = new PresetDefinition(1, Guid.NewGuid(), 1, "Default");
        var errorInstance = Guid.NewGuid();
        daemon.Catalog = new([new(errorInstance, new(1, errorInstance, 1, "cs_test_dust_", 27015, errorPreset.Id), [errorPreset])]);
    }
    PumpUntil(model.RefreshInstancesAsync());
    ((ShellViewModel)owner.DataContext!).NavigateCommand.Execute("instance"); Layout();
    daemon.IdleForPresetTests = true; daemon.CfgRunIdentity = false;
    PumpUntil(server.RefreshAsync()); Layout();
    // A failed start must show the daemon's code plus the documented next step.
    daemon.StartFailureCode = "HCSL-DEPLOY-PLUGINS";
    daemon.StartFailureDetail = "Plugin packages may not write preset CFG, logs or recordings.";
    Click(owner, "启动");
    WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "取消")));
    Click(owner, "启动");
    WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("HCSL-DEPLOY-PLUGINS") == true));
    Check(owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("hcsl doctor") == true),
        "Deployment failure dialog must include the actionable hint.");
    Click(owner, "返回"); Layout();
    daemon.StartFailureCode = null;
    // Deploy-only is a first-class action: it must not start CS2 and must report completion.
    PumpUntil(server.RefreshAsync()); Layout();
    Click(owner, "部署（不启动）");
    WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "部署")));
    Click(owner, "部署");
    WaitUntil(() => daemon.Deploys == 1);
    WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "返回")));
    Click(owner, "返回"); Layout();
    Check(!server.Active && daemon.Stops == 0, "Deploy-only action must not start or stop the server.");
    // A leftover journal is recoverable from the UI, not only from the CLI.
    Click(owner, "恢复部署");
    WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "恢复")));
    Click(owner, "恢复");
    WaitUntil(() => daemon.RecoverDeployments == 1);
    WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "返回")));
    Click(owner, "返回"); Layout();
    Check(!server.Active, "Deployment recovery must not start the server.");
    Console.WriteLine("PASS gui.errors.failed-start-hint-and-deploy-only");
    if (only is "errors") return 0;
}


if (only is null or "cfg")
{
var cfgQueries = daemon.Queries;
var cfgDialog = owner.EditCfgAsync(model, model.Selected!.Entry.Definition!, firstPreset);
Layout();
Click(owner, "读取");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "已读取；编辑不会自动保存，保存不会自动 exec。"));
var cfgText = owner.GetVisualDescendants().OfType<TKCode.Arc.Textarea>().Single(t => t.IsEffectivelyVisible);
var cfgName = owner.GetVisualDescendants().OfType<TKCode.Arc.Input>().Single(t => t.IsEffectivelyVisible && t.IsEffectivelyEnabled);
cfgText.Text = "mp_freezetime 3\n";
cfgName.Text = "../bad.cfg";
Click(owner, "保存");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("HCSL-CFG-NAME") == true));
Check(daemon.CfgWrites == 0 && cfgText.Text == "mp_freezetime 3\n", "Invalid CFG lost draft or reached storage.");
cfgName.Text = "practice.cfg"; Click(owner, "保存");
WaitUntil(() => daemon.CfgWrites == 1 && owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "已保存到预设；没有向服务器执行 exec。"));
Check(daemon.Queries == cfgQueries, "CFG save implicitly executed a command.");
Click(owner, "删除"); WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "删除 CFG"));
Click(owner, "取消");
WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "刷新列表")));
Check(daemon.CfgWrites == 1, "Cancelled deletion reached storage.");
cfgText.Text = "unsaved"; Click(owner, "关闭");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "未保存的 CFG 草稿"));
Click(owner, "继续编辑");
WaitUntil(() => cfgText.IsEffectivelyVisible);
Check(cfgText.Text == "unsaved", "Cancelled discard lost draft.");
Click(owner, "关闭"); WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "未保存的 CFG 草稿"));
Click(owner, "丢弃草稿"); PumpUntil(cfgDialog);
Check(cfgText.Text == "", "Closed CFG editor retained contents.");
Console.WriteLine("PASS gui.cfg.load-invalid-preserves-draft-save-without-exec-delete-cancel-and-dirty-close");
daemon.IdleForPresetTests = false; daemon.CfgRunIdentity = true; PumpUntil(server.RefreshAsync()); Layout();
var execDialog = owner.EditCfgAsync(model, model.Selected!.Entry.Definition!, firstPreset); Layout();
Click(owner, "读取");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "已读取；编辑不会自动保存，保存不会自动 exec。"));
Click(owner, "执行 exec");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "向运行服务器执行 CFG"));
Check(owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(server.RunId!.Value.ToString("D")) == true), "CFG exec confirmation omitted captured RunId.");
Click(owner, "取消");
WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "刷新列表")));
Check(daemon.Queries == cfgQueries, "Cancelled CFG exec was sent.");
Click(owner, "执行 exec");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "向运行服务器执行 CFG"));
Click(owner, "执行 exec");
WaitUntil(() => daemon.Queries == cfgQueries + 1 && owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "刷新列表")));
Check(daemon.LastConsole?.Text == "exec practice.cfg" && daemon.LastConsole.RunId == server.RunId, "Confirmed CFG exec target changed.");
Click(owner, "关闭"); PumpUntil(execDialog);
Console.WriteLine("PASS gui.cfg.exec-confirmation-cancel-and-explicit-run-bound-send");
if (only is "cfg") return 0;
}

if (only is null or "maps")
{
if (only is "maps")
{
    daemon.IdleForPresetTests = false;
    daemon.CfgRunIdentity = true;
    PumpUntil(server.RefreshAsync());
    Layout();
}
var beforeMaps = daemon.Queries;
Click(owner, "即时换图"); Layout(); Click(owner, "取消");
WaitUntil(() => presetPage.FindControl<TKCode.Arc.Button>("ChangeMapButton")!.IsEnabled);
Check(daemon.Queries == beforeMaps, "Cancelled map change sent a command.");
Click(owner, "即时换图"); Layout();
owner.GetVisualDescendants().OfType<TKCode.Arc.Select>().Single(s => s.IsEffectivelyVisible && s.ItemsSource is string[]).SelectedIndex = 1;
owner.GetVisualDescendants().OfType<TKCode.Arc.Input>().Single(t => t.IsEffectivelyVisible).Text = "3070244462";
Click(owner, "下一步"); WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "确认即时换图"));
Click(owner, "换图"); WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "换图投递结果"));
Check(daemon.Queries == beforeMaps + 1 && daemon.LastConsole is { Text: "host_workshop_map 3070244462" } && daemon.LastConsole.RunId == server.RunId, "Confirmed map change target changed.");
Click(owner, "返回"); WaitUntil(() => presetPage.FindControl<TKCode.Arc.Button>("ChangeMapButton")!.IsEnabled);
Console.WriteLine("PASS gui.maps.cancel-confirmed-run-bound-workshop-command-no-preset-write");
if (only is "maps") return 0;
}

if (only is null or "start")
{
daemon.IdleForPresetTests = false;
daemon.CfgRunIdentity = false; PumpUntil(server.RefreshAsync()); Layout();
Check(!presetPage.FindControl<TKCode.Arc.Button>("CreatePresetButton")!.IsEnabled && !presetPage.FindControl<TKCode.Arc.Button>("EditPresetButton")!.IsEnabled, "Active run must disable preset writes.");
((ShellViewModel)owner.DataContext!).NavigateCommand.Execute("home"); Layout();
Console.WriteLine("PASS gui.presets.catalog-browse-without-switch-cancel-selection-and-active-write-guards");
var normalStart = owner.ConfirmStartAsync("测试启动确认");
Layout();
Check(owner.GetVisualDescendants().OfType<TKCode.Arc.Checkbox>().Single().IsChecked != true, "Low-memory consent must default off.");
Click(owner, "启动"); PumpUntil(normalStart);
Check(normalStart.Result is { Confirmed: true, AllowLowMemory: false }, "Ordinary start implicitly consented.");
var debugStart = owner.ConfirmStartAsync("测试调试启动");
Layout();
owner.GetVisualDescendants().OfType<TKCode.Arc.Checkbox>().Single().IsChecked = true;
Click(owner, "启动"); PumpUntil(debugStart);
Check(debugStart.Result is { Confirmed: true, AllowLowMemory: true }, "Explicit debug consent missing.");
var nextStart = owner.ConfirmStartAsync("测试不保存同意");
Layout();
Check(owner.GetVisualDescendants().OfType<TKCode.Arc.Checkbox>().Single().IsChecked != true, "Consent leaked into the next start.");
owner.GetVisualDescendants().OfType<TKCode.Arc.Checkbox>().Single().IsChecked = true;
Click(owner, "取消"); PumpUntil(nextStart);
Check(nextStart.Result is { Confirmed: false, AllowLowMemory: false }, "Cancel must not return consent.");
Console.WriteLine("PASS gui.start.low-memory-default-explicit-one-shot-and-cancel");
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
if (only is "start") return 0;
}

if (only is not null)
{
    Console.Error.WriteLine($"Unknown test section '{only}'. Use input, logs, forms, presets, pools, plugins, cfg, maps, start, errors or all.");
    return 2;
}
return 0;

static string? ParseOnly(string[] args)
{
    string? only = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--only")
        {
            if (i + 1 >= args.Length) throw new ArgumentException("--only requires a section name.");
            only = args[++i].ToLowerInvariant();
        }
        else if (args[i].StartsWith("--only=", StringComparison.Ordinal))
            only = args[i]["--only=".Length..].ToLowerInvariant();
    }
    return only is "all" ? null : only;
}

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
static void Click(Window owner, string label) => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>()
    .Single(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled && Equals(b.Content, label)).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

sealed class FakeDaemon : IDaemonClient
{
    private readonly Guid run = Guid.Parse("68fa2944-7d6b-4373-be01-368879164976");
    private bool stopped;
    public int Stops { get; private set; }
    public int Queries { get; private set; }
    public ServerConsoleCommand? LastConsole { get; private set; }
    public int LastLogSequence { get; set; } = 150;
    public bool RepeatLogText { get; set; }
    public bool IdleForPresetTests;
    public string? StartFailureCode, StartFailureDetail;
    public Guid? FailedRun;
    public int Deploys;
    public int RecoverDeployments;
    public int PlanDeployments;
    public string DeployCode = "HCSL-SERVER-DEPLOYED";
    public bool CfgRunIdentity;
    public int CfgWrites;
    public int PoolWrites;
    public bool RejectPoolWrites;
    public int PluginWrites;
    public PluginResult Plugins = new([new("metamod", "Metamod", "1.0.0", "hash", 1024, 2)], [new("metamod", "Metamod", "1.0.0", true, true)], 1, "plugin-1");
    public MapPoolResult Pool = new(new(1, 1, [new(ServerMapKind.Standard, "de_dust2"), new(ServerMapKind.Standard, "de_mirage")]), "pool-1");
    private string cfgText = "status\n";
    private string cfgHash = "v1";
    public InstanceCatalog Catalog = new([]);
    public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
    {
        if (request.Kind == RequestKind.Instances)
        {
            if (JsonSerializer.Deserialize<InstanceCommand>(request.InstanceCommandJson!)!.Action != InstanceAction.List)
                throw new Exception("Cancelled/browse-only GUI unexpectedly sent a metadata write.");
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, InstanceCatalogJson: JsonSerializer.Serialize(Catalog)));
        }
        if (request.Kind == RequestKind.MapPool)
        {
            var c = JsonSerializer.Deserialize<MapPoolCommand>(request.MapPoolJson!)!;
            if (c.Action == MapPoolAction.Save)
            {
                if (RejectPoolWrites) return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, false, "HCSL-MAPPOOL-CONFLICT", "External edit; reload."));
                if (!IdleForPresetTests) throw new Exception("Active pool write admitted by GUI.");
                PoolWrites++; Pool = new(new(1, Pool.Pool.Revision + 1, c.Maps!), "pool-" + (Pool.Pool.Revision + 1));
            }
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, MapPoolResultJson: JsonSerializer.Serialize(Pool)));
        }
        if (request.Kind == RequestKind.Plugin)
        {
            var c = JsonSerializer.Deserialize<PluginCommand>(request.PluginJson!)!;
            if (c.Action == PluginAction.Save)
            {
                if (!IdleForPresetTests) throw new Exception("Active plugin write admitted by GUI.");
                PluginWrites++;
                Plugins = new(Plugins.Available, (c.Plugins ?? []).Select(p => new PluginSelectionInfo(p.Id, "Metamod", p.Version, p.Enabled, true)).ToArray(), Plugins.Revision + 1, "plugin-" + (Plugins.Revision + 1));
            }
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, PluginResultJson: JsonSerializer.Serialize(Plugins)));
        }
        if (request.Kind == RequestKind.PresetCfg)
        {
            var c = JsonSerializer.Deserialize<PresetCfgCommand>(request.PresetCfgJson!)!;
            PresetCfgResult result;
            if (c.Action == PresetCfgAction.List) result = new([new("practice.cfg", cfgText.Length)]);
            else if (c.Action == PresetCfgAction.Read) result = new([], "practice.cfg", cfgText, cfgHash);
            else
            {
                if (c.Action != PresetCfgAction.Save) throw new Exception("Cancelled GUI action reached CFG storage.");
                PresetCfgRules.Name(c.Name); PresetCfgRules.Content(c.Content);
                cfgText = c.Content!; cfgHash = "v" + (++CfgWrites + 1);
                result = new([], c.Name, cfgText, cfgHash);
            }
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, PresetCfgResultJson: JsonSerializer.Serialize(result)));
        }
        if (request.Kind == RequestKind.ServerConsole)
        {
            var c = JsonSerializer.Deserialize<ServerConsoleCommand>(request.ServerConsoleJson!)!;
            if (c.RunId != run || (c.Text != "status" && c.Text != "mp_roundtime 60" && c.Text != "exec practice.cfg" && c.Text != "host_workshop_map 3070244462")) throw new Exception("GUI query identity/text/confirmation changed.");
            LastConsole = c;
            Queries++;
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, ConsoleReceiptJson: JsonSerializer.Serialize(new ServerConsoleReceipt(run, c.CommandId, ConsoleDelivery.Written, "HCSL-CONSOLE-WRITTEN", ServerConsoleRules.Display(c.Text)))));
        }
        var serverCommand = request.ServerCommandJson is { } json ? JsonSerializer.Deserialize<ServerCommand>(json) : null;
        if (serverCommand is { Action: ServerAction.Stop } stop)
        {
            if (stop.RunId != run) throw new Exception("Stop must carry the confirmed RunId.");
            stopped = true; Stops++;
        }
        if (serverCommand is { Action: ServerAction.Deploy }) Deploys++;
        if (serverCommand is { Action: ServerAction.RecoverDeployment }) RecoverDeployments++;
        if (serverCommand is { Action: ServerAction.PlanDeployment })
        {
            PlanDeployments++;
            var plan = new DeploymentPlan(Catalog.Instances.FirstOrDefault()?.Id ?? Guid.Empty, 1, Catalog.Instances.FirstOrDefault()?.Presets.FirstOrDefault()?.Id ?? Guid.Empty,
                1, "26445667", "/library/runtime/preview", DeploymentCopyMode.Reflink, 3140, 73434849358, 1024, 1024,
                [], ["Offline deployment only; no server is launched."], null, null, null, null);
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, DeploymentPlanJson: JsonSerializer.Serialize(plan)));
        }
        var failing = serverCommand is { Action: ServerAction.Start } && StartFailureCode is not null;
        if (failing) FailedRun = Guid.NewGuid();
        var lines = Enumerable.Range(LastLogSequence - 149, 150).Select(i => new ServerLogLine(i, (RepeatLogText ? "repeat " : $"{i} ") + new string('x', 300))).ToArray();
        var snapshot = failing
            ? new ServerSnapshot(FailedRun, null, ServerState.Failed, false, StartFailureCode!, false, true, null, LastLogSequence + 1, false, lines, Detail: StartFailureDetail)
            : serverCommand is { Action: ServerAction.RecoverDeployment }
                ? new ServerSnapshot(run, null, ServerState.Stopped, false, "HCSL-SERVER-DEPLOY-RECOVERED", false, true, null, LastLogSequence + 1, false, lines, Detail: "PreviousDeploymentPreserved_StagingRetained")
                : serverCommand is { Action: ServerAction.Deploy }
                ? new ServerSnapshot(run, null, ServerState.Stopped, false, DeployCode, false, true, null, LastLogSequence + 1, false, lines, Detail: "Deployment published (Reflink).")
                : new ServerSnapshot(run, CfgRunIdentity ? Catalog.Instances.Single().Id : null, stopped ? ServerState.Stopped : IdleForPresetTests ? ServerState.Idle : ServerState.Running,
                    !stopped && !IdleForPresetTests, stopped ? "HCSL-SERVER-STOPPED" : "HCSL-SERVER-MAP-READY", true, stopped, null, LastLogSequence + 1, false, lines,
                    Preset: CfgRunIdentity ? new(Catalog.Instances.Single().Presets[0].Id, 1, "Selected preset", "de_dust2") : null);
        return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true,
            Defaults: new("/settings", "/data", "/library"), ServerSnapshotJson: JsonSerializer.Serialize(snapshot)));
    }
}
