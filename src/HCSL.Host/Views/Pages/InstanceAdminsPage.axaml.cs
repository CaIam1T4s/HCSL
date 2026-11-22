using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;

namespace HCSL.Host.Views.Pages;

/// <summary>管理员: authorization only. The online roster stays in the console window.</summary>
public partial class InstanceAdminsPage : UserControl
{
    /// <summary>One admin row; Arc binds by property name.</summary>
    public sealed record AdminRow(string DisplayName, string SteamId, string Groups);

    private HostServerController? subscribed;
    private bool operating, loaded;
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;

    public InstanceAdminsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible && !loaded) _ = LoadAsync(); };
    }

    private void Subscribe()
    {
        Unsubscribe();
        subscribed = Model;
        if (subscribed is not null) subscribed.PropertyChanged += Changed;
        if (IsVisible) _ = LoadAsync();
    }

    private void Unsubscribe()
    {
        if (subscribed is not null) subscribed.PropertyChanged -= Changed;
        subscribed = null;
        loaded = false;
    }

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (IsVisible) Dispatcher.UIThread.Post(() => { loaded = false; _ = LoadAsync(); });
    }

    private async Task LoadAsync()
    {
        if (Model is not { } m || m.Selected?.Entry.Definition is not { } definition || !m.Server.Connected || operating) return;
        if (m.Selected.Entry.Presets.FirstOrDefault(p => p.Id == definition.SelectedPresetId) is not { } preset) return;
        loaded = true;
        try
        {
            var result = await m.AdminsAsync();
            Admins.ItemsSource = result.Snapshot.Entries.Select(admin => new AdminRow(
                admin.DisplayName ?? "—", admin.SteamId64,
                admin.Groups.Length == 0 ? "—" : string.Join("、", admin.Groups))).ToArray();
            SourceText.Text = result.Snapshot.SourceName + (result.Snapshot.Writable ? "" : " · 只读")
                + " · " + preset.Name;
        }
        catch (IOException error) { Error("读取管理员失败", error.Message); loaded = false; }
        finally { Update(); }
    }

    private void Update()
    {
        if (Model is not { } m) return;
        var ready = !operating && m.CanEditAdmins;
        EditButton.IsEnabled = ready;
        ReloadButton.IsEnabled = ready;
        RefreshButton.IsEnabled = !operating && m.Server.Connected;
    }

    private void Error(string title, string? detail = null) => Owner?.ShowInstanceNotification(title, detail);

    private async void EditAdmins(object? sender, RoutedEventArgs e)
    {
        if (operating || Model is not { } m || Owner is not { } owner ||
            m.Selected?.Entry.Definition is not { } instance ||
            m.Selected.Entry.Presets.FirstOrDefault(p => p.Id == instance.SelectedPresetId) is not { } preset) return;
        operating = true;
        Update();
        try { await owner.EditAdminsAsync(m, instance, preset); }
        finally { operating = false; loaded = false; Update(); await LoadAsync(); }
    }

    private async void Reload(object? sender, RoutedEventArgs e)
    {
        if (operating || Model is not { } m) return;
        operating = true;
        Update();
        try
        {
            var result = await m.ReloadAdminsAsync();
            Owner?.ShowInstanceNotification("已重载权限", result.Reload ?? result.Code,
                result.Reload is null ? ToastKind.Info : ToastKind.Success);
        }
        catch (IOException error) { Error("重载未完成", error.Message); }
        finally { operating = false; loaded = false; Update(); await LoadAsync(); }
    }

    private async void Refresh(object? sender, RoutedEventArgs e)
    {
        if (operating) return;
        loaded = false;
        await LoadAsync();
    }
}
