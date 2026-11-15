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
    private bool themeTransitionInProgress;
    private (string Text, BadgeTone Tone, LucideIconKind Icon)? badgeState;

    private void InitializeHomeShell()
    {
        Title = $"Hello Counter Strike! Launcher v{ProductVersion.Current}";
        ShellTitleBar.Title = Title;
        DataContextChanged += (_, _) =>
        {
            if (shellServers is not null) shellServers.PropertyChanged -= ShellServerChanged;
            shellServers = (DataContext as ShellViewModel)?.Servers;
            if (shellServers is not null) shellServers.PropertyChanged += ShellServerChanged;
            UpdateShellStatus();
        };
        ActualThemeVariantChanged += (_, _) => UpdateThemeText();
        Closed += (_, _) =>
        {
            if (shellServers is not null) shellServers.PropertyChanged -= ShellServerChanged;
            shellServers = null;
        };
        UpdateThemeText();
        UpdateShellStatus();
    }

    public Task StartHomeServerAsync() => InstanceManagementPage.StartSelectedAsync();
    public void OpenSelectedInstanceLogs() => InstanceManagementPage.ShowSelectedLogs();
    public void ShowInstanceNotification(string title, string? detail = null, ToastKind kind = ToastKind.Error) =>
        InstanceNotifications.Show(new ToastOptions { Title = title, Description = detail, Kind = kind });

    private void ShellServerChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(UpdateShellStatus);
    private void UpdateShellStatus()
    {
        var server = shellServers?.Server;
        var next = ServerBadgePresentation.For(server?.State ?? ServerState.Idle, server?.Connected == true);
        if (badgeState == next) return;
        badgeState = next;
        ServerStatusBadge.Content = next.Item1;
        ServerStatusBadge.Tone = next.Item2;
        ServerStatusBadge.Icon = new LucideIcon { Kind = next.Item3 };
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
