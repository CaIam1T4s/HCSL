using System.ComponentModel;
using Avalonia.Styling;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.Services;
using HCSL.Host.ViewModels;
using HCSL.UI.Shared.Services;
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
        EventHandler? diagnosticLayout = null;
        try
        {
            var mode = ThemeTransitionIsolation.ParseMode(Environment.GetEnvironmentVariable("HCSL_THEME_TRANSITION"));
            var effectMode = ThemeEffectIsolation.ParseMode(Environment.GetEnvironmentVariable("HCSL_THEME_EFFECTS"));
            var captureRoot = Environment.GetEnvironmentVariable("HCSL_THEME_CAPTURE_DIR");
            var captureDirectory = string.IsNullOrWhiteSpace(captureRoot) ? null :
                System.IO.Path.Combine(captureRoot, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}");
            if (mode == ThemeTransitionMode.Normal && (effectMode != ThemeEffectIsolationMode.None || captureDirectory is not null))
                mode = ThemeTransitionMode.Observe;
            using var effects = new ThemeEffectIsolation(this, effectMode);
            if (mode != ThemeTransitionMode.Normal)
            {
                diagnosticLayout = (_, _) => LogThemeGeometry(mode, "layout");
                LayoutUpdated += diagnosticLayout;
            }
            await ThemeTransitionIsolation.RunAsync(e.Trigger, e.Variant, () =>
            {
                model.SelectedTheme = e.Next == ThemeSwitcherAppearance.Dark ? "深色" : "浅色";
                UpdateThemeText();
                effects.Refresh();
                return Task.CompletedTask;
            }, mode, mode == ThemeTransitionMode.Normal ? null : phase =>
            {
                LogThemeGeometry(mode, phase);
                if (phase == "begin") Console.Error.WriteLine($"[HCSL.theme] effectIsolation={effectMode}");
                // Independent captures before Arc adds its overlay; never mistake a nested screenshot for its raw bitmap.
                if (captureDirectory is not null && phase == "before-arc") TryCaptureThemeSnapshot(captureDirectory, "before");
                if (captureDirectory is not null && phase == "complete") TryCaptureThemeSnapshot(captureDirectory, "after");
            });
        }
        finally
        {
            if (diagnosticLayout is not null) LayoutUpdated -= diagnosticLayout;
            lastThemeGeometry = null;
            themeTransitionInProgress = false;
        }
    }
}
