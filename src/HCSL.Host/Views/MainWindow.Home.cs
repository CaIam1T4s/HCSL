using System.ComponentModel;
using Avalonia.Styling;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.Services;
using HCSL.Host.ViewModels;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.Views;

public partial class MainWindow
{
    private HostServerController? shellServers;
    private ShellViewModel? shellModel;
    private bool themeTransitionInProgress;
    private string BaseTitle = "";
    private (string Text, BadgeTone Tone, LucideIconKind Icon)? badgeState;

    private void InitializeHomeShell()
    {
        Title = $"Hello Counter Strike! Launcher v{ProductVersion.Current}";
        BaseTitle = Title;
        ShellTitleBar.Title = Title;
        DataContextChanged += (_, _) =>
        {
            if (shellServers is not null)
            {
                shellServers.PropertyChanged -= ShellServerChanged;
                shellServers.Server.ConnectionNotice -= ServerConnectionNotice;
            }
            if (shellModel is not null) shellModel.PropertyChanged -= ShellPageChanged;
            shellModel = DataContext as ShellViewModel;
            if (shellModel is not null) shellModel.PropertyChanged += ShellPageChanged;
            shellServers = shellModel?.Servers;
            if (shellServers is not null)
            {
                shellServers.PropertyChanged += ShellServerChanged;
                shellServers.Server.ConnectionNotice += ServerConnectionNotice;
            }
            UpdateShellStatus();
        };
        ActualThemeVariantChanged += (_, _) => UpdateThemeText();
        Closed += (_, _) =>
        {
            if (shellServers is not null)
            {
                shellServers.PropertyChanged -= ShellServerChanged;
                shellServers.Server.ConnectionNotice -= ServerConnectionNotice;
            }
            if (shellModel is not null) shellModel.PropertyChanged -= ShellPageChanged;
            shellModel = null;
            shellServers = null;
        };
        UpdateThemeText();
        UpdateShellStatus();
        // The instance sidebar is a plain section list; its icons are geometry, so they are resolved once here.
        foreach (var item in InstanceSidebar.Sections.SelectMany(section => section.Items))
            item.Icon = Lucide.Geometry(item.Key switch
            {
                "overview" => LucideIconKind.Gauge,
                "presets" => LucideIconKind.FileStack,
                "content" => LucideIconKind.Puzzle,
                "pools" => LucideIconKind.Map,
                "start" => LucideIconKind.Play,
                "console" => LucideIconKind.Terminal,
                _ => LucideIconKind.ShieldCheck
            });
    }

    private void InstanceSidebarItemInvoked(object? sender, SidebarItemInvokedEventArgs e)
    {
        if (e.Item.Key is not { } key) return;
        InvokeInstanceScopeAction(key);
        // The 运行 rows are actions rather than pages: keep the selection block on the current section.
        if (key is "start" or "console" && DataContext is ShellViewModel shell)
            InstanceSidebar.SelectedKey = shell.SelectedInstanceSection.Id;
    }

    private void LeaveInstanceScope(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        (DataContext as ShellViewModel)?.ExitInstanceScope();

    public void ShowInstanceNotification(string title, string? detail = null, ToastKind kind = ToastKind.Error) =>
        InstanceNotifications.Show(new ToastOptions { Title = title, Description = detail, Kind = kind });

    private void ShellServerChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateShellStatus();
            _ = CheckStartOutcomeAsync();
        });
    }

    /// <summary>Entering or leaving the instance scope changes the window title, not the server state.</summary>
    private void ShellPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsInstance)) Dispatcher.UIThread.Post(UpdateShellStatus);
    }

    /// <summary>Link state changes are transient results, so they use the 3-second toast stack.</summary>
    private void ServerConnectionNotice(string message) => Dispatcher.UIThread.Post(() =>
        InstanceNotifications.Show(new ToastOptions
        {
            Title = message,
            Kind = message.Contains("重新连接", StringComparison.Ordinal) ? ToastKind.Success : ToastKind.Warning
        }));
    private void UpdateShellStatus()
    {
        var server = shellServers?.Server;
        var next = ServerBadgePresentation.For(server?.State ?? ServerState.Idle, server?.Connected == true, server?.Reconnecting == true);
        UpdateScopeFooter();
        // In the instance scope the title bar carries the back arrow and the instance name, like the reference launcher;
        // the scope name replaces the icon and title text so both stay in one place.
        var scopedName = (DataContext as ShellViewModel)?.IsInstance == true ? shellServers?.Selected?.Entry.Definition?.Name : null;
        var inScope = scopedName is { Length: > 0 };
        ScopeBackButton.IsVisible = inScope;
        ScopeTitleText.IsVisible = inScope;
        ScopeTitleText.Text = inScope ? $"实例管理 · {scopedName}" : "";
        if (ShellTitleBar.Title != (inScope ? "" : BaseTitle)) ShellTitleBar.Title = inScope ? "" : BaseTitle;
        if (inScope != (ShellTitleBar.Icon is null))
            ShellTitleBar.Icon = inScope ? null : new ArcGlyph { Data = ArcIcons.Server, Size = 16 };
        if (badgeState == next) return;
        badgeState = next;
        ServerStatusBadge.Content = next.Item1;
        ServerStatusBadge.Tone = next.Item2;
        ServerStatusBadge.Icon = server?.Reconnecting == true
            ? new MorphLoader { Size = 12, Variant = MorphLoaderVariant.Bars, Status = MorphLoaderStatus.Loading, IsDecorative = true }
            : new LucideIcon { Kind = next.Item3 };
    }

    private void UpdateThemeText() => SidebarThemeSwitcher.Text = ActualThemeVariant == ThemeVariant.Dark
        ? "切换到浅色模式" : "切换到深色模式";

    private async void SidebarThemeRequested(object? sender, ThemeSwitcherChangeRequestedEventArgs e)
    {
        e.Handled = true;
        if (themeTransitionInProgress || DataContext is not ShellViewModel model) return;
        themeTransitionInProgress = true;
        try
        {
            await ThemeSwitcher.RunTransitionAsync(e.Trigger, e.Variant, () =>
            {
                model.SelectedTheme = e.Next == ThemeSwitcherAppearance.Dark ? "深色" : "浅色";
                UpdateThemeText();
                return Task.CompletedTask;
            });
        }
        finally
        {
            themeTransitionInProgress = false;
        }
    }
}
