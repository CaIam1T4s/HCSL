using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using TKCode.Arc;

namespace HCSL.Host.Views.Pages;

/// <summary>
/// A preset only chooses which instance pool it uses: pools are created, renamed and deleted in the instance scope, so a
/// preset scope cannot change what other presets rely on.
/// </summary>
public partial class PresetMapPoolPage : UserControl
{
    private HostServerController? subscribed;
    private MapPoolLibrary? library;
    private string? libraryHash;
    private MapPoolRow[] rows = [];
    private bool operating, loaded, projecting;
    private HostServerController? Model => (DataContext as ShellViewModel)?.Servers;
    private MainWindow? Owner => TopLevel.GetTopLevel(this) as MainWindow;
    private Guid? PresetId => (DataContext as ShellViewModel)?.ManagedPresetId;

    public PresetMapPoolPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Subscribe();
        AttachedToVisualTree += (_, _) => Subscribe();
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && IsVisible) { loaded = false; _ = LoadAsync(); } };
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
        if (IsVisible) Dispatcher.UIThread.Post(() =>
        {
            UpdateChoices();
            if (!loaded && Model?.Server.Connected == true) { loaded = true; _ = LoadAsync(); }
        });
    }

    private async Task LoadAsync()
    {
        if (operating || Model is not { } m || m.Selected?.Entry.Definition is not { } instance || !m.Server.Connected) return;
        try
        {
            var result = await m.MapPoolsAsync(new MapPoolLibraryCommand(MapPoolLibraryAction.List, instance.Id));
            library = result.Library;
            libraryHash = result.Hash;
            var assigned = PresetId is { } presetId ? library.PoolOf(presetId)?.Id : null;
            rows = library.Pools.Select(pool => new MapPoolRow(pool,
                pool.Maps.Length == 0 ? "空" : $"{pool.Maps.Length} 张",
                pool.Id == assigned ? "本预设使用" : "",
                pool.Id == assigned ? BadgeTone.Success : BadgeTone.Neutral)).ToArray();
            projecting = true;
            try
            {
                PoolChoices.ItemsSource = rows;
                PoolChoices.SelectedItem = rows.FirstOrDefault(row => row.Pool.Id == assigned);
            }
            finally { projecting = false; }
            UpdateChoices();
            EmptyHint.IsVisible = rows.Length == 0;
            ShowMaps(assigned);
        }
        catch (IOException error) { Error("读取图池失败", error.Message); loaded = false; }
    }

    private void ShowMaps(Guid? poolId)
    {
        var maps = poolId is { } id ? library?.Pools.FirstOrDefault(pool => pool.Id == id)?.Maps : null;
        MapsText.Text = maps is { Length: > 0 } stored ? string.Join(" · ", stored.Select(ServerMapRules.Label)) : "";
    }

    private async void PoolSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (projecting || PoolChoices.SelectedItem is not MapPoolRow row) return;
        if (Model?.CanEditMapPool != true)
        {
            projecting = true;
            try { PoolChoices.SelectedItem = rows.FirstOrDefault(r => r.Pool.Id == (PresetId is { } id ? library?.PoolOf(id)?.Id : null)); }
            finally { projecting = false; }
            UpdateChoices();
            return;
        }
        ShowMaps(row.Pool.Id);
        if (operating || Model is not { } m || m.Selected?.Entry.Definition is not { } instance || PresetId is not { } presetId) return;
        if (library is not { } snapshot || snapshot.PoolOf(presetId)?.Id == row.Pool.Id) return;
        operating = true;
        UpdateChoices();
        try
        {
            await m.MapPoolsAsync(new MapPoolLibraryCommand(MapPoolLibraryAction.AssignPreset,
                instance.Id, instance.Revision, row.Pool.Id, presetId,
                ExpectedRevision: snapshot.Revision, ExpectedHash: libraryHash));
        }
        catch (IOException error) { Error("指定图池失败", error.Message); }
        finally { operating = false; UpdateChoices(); }
        await LoadAsync();
    }

    private void UpdateChoices() => PoolChoices.IsEnabled = !operating && Model?.CanEditMapPool == true;

    private void Error(string title, string? detail = null) => Owner?.ShowInstanceNotification(title, detail);
}
