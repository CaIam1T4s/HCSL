using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using HCSL.Frontend;

namespace HCSL.Host.Views;

/// <summary>View of the existing bounded, sanitized IPC log ring; no runtime ownership.</summary>
public sealed class ServerLogWindow : Window
{
    private readonly HostServerController model;
    private readonly MainWindow owner;
    private bool hiddenForPrompt;
    private readonly TextBlock summary = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock detail = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock history = new();
    private readonly TKCode.Arc.Textarea text = new()
    {
        IsReadOnly = true, TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
        IsResizable = false, FrameMinHeight = 0, MinHeight = 0, ClipToBounds = true
    };
    private readonly TKCode.Arc.Button follow = new() { Content = "暂停跟随", Variant = TKCode.Arc.ButtonVariant.Secondary };
    private bool following = true, closed;
    private Guid? displayedRun;
    public ServerLogWindow(HostServerController model, MainWindow owner)
    {
        this.model = model;
        this.owner = owner;
        owner.PromptVisibilityChanged += PromptChanged;
        owner.Closed += OwnerClosed;
        Title = "HCSL · 启动进度 / 服务器日志"; Width = 960; Height = 620; MinWidth = 600; MinHeight = 400;
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = RowDefinitions.Parse("Auto,Auto,Auto,*") };
        grid.Children.Add(summary);
        Grid.SetRow(detail, 1); grid.Children.Add(detail);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 8) };
        follow.Click += (_, _) => { following = !following; follow.Content = following ? "暂停跟随" : "跟随最新"; Update(); };
        tools.Children.Add(follow); tools.Children.Add(history);
        Grid.SetRow(tools, 2); grid.Children.Add(tools);
        // Arc's frame has its own height; stretching the outer control alone does
        // not constrain its internal editor. Size it from a finite, clipped viewport.
        var viewport = new Border { ClipToBounds = true, Child = text };
        viewport.SizeChanged += (_, e) => text.FrameHeight = Math.Max(0, e.NewSize.Height);
        text.Classes.Add("server-log");
        Grid.SetRow(viewport, 3); grid.Children.Add(viewport);
        Content = grid;
        model.PropertyChanged += Changed;
        model.OpenLogs();
        Closed += (_, _) =>
        {
            closed = true; owner.PromptVisibilityChanged -= PromptChanged;
            owner.Closed -= OwnerClosed;
            model.PropertyChanged -= Changed;
            if (!hiddenForPrompt) model.CloseLogs();
            text.Text = "";
        };
        Update();
    }
    private void OwnerClosed(object? sender, EventArgs e) => Close();
    private void PromptChanged(bool visible)
    {
        if (closed) return;
        if (visible && IsVisible)
        {
            hiddenForPrompt = true;
            model.CloseLogs();
            Hide();
        }
        else if (!visible && hiddenForPrompt)
        {
            hiddenForPrompt = false;
            model.OpenLogs();
            Show(); // Restore the existing owner relationship after Hide().
        }
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (!closed) Update(); }
    private void Update()
    {
        summary.Text = model.Header;
        detail.Text = model.Server.DetailText;
        history.Text = model.Server.LogsTruncated ? "日志已截断：仅显示最近记录" : "最近记录；非完整磁盘日志";
        if (displayedRun != model.Server.RunId) { displayedRun = model.Server.RunId; text.Text = ""; }
        // Pause freezes the view, not collection. Resuming projects the latest bounded ring.
        if (!following) return;
        var next = model.Server.LogText;
        if (text.Text != next) { text.Text = next; text.CaretIndex = next.Length; }
    }
}
