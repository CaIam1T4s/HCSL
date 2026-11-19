using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using HCSL.Core;
using HCSL.UI.Shared.Services;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using ArcCopyButton = TKCode.Arc.CopyButton;
using ArcDialog = TKCode.Arc.Dialog;
using ArcSelect = TKCode.Arc.Select;

namespace HCSL.Host.Views;

/// <summary>
/// Share dialog: pick one of the daemon-reported local addresses and copy a CS2 console command.
/// Addresses come from the run snapshot, so the dialog never probes the network or the filesystem.
/// </summary>
public sealed partial class ServerLogWindow
{
    private readonly ArcDialog shareDialog = new()
    {
        Title = "分享到局域网",
        Size = ControlSize.Medium,
        ShowCloseButton = true
    };
    private readonly ArcSelect shareAddress = new() { Placeholder = "选择服务器地址" };
    // Always mounted so the select keeps its width; it only becomes usable once an address is chosen.
    private readonly ArcCopyButton shareCopy = new()
    {
        Label = "复制命令",
        CopiedLabel = "已复制",
        FailedLabel = "复制失败",
        Variant = CopyButtonVariant.Outline,
        IsEnabled = false,
        VerticalAlignment = VerticalAlignment.Center
    };
    // The command preview is its own line: inside the select it grew the field and broke the row alignment.
    private readonly TextBlock sharePreview = new() { TextWrapping = TextWrapping.NoWrap };
    private Guid? shareInstanceId;
    private int sharePort;

    private Control BuildShareDialog()
    {
        shareAddress.SelectionChanged += (_, _) => UpdateShareCommand();
        shareAddress.VerticalAlignment = VerticalAlignment.Center;
        sharePreview.FontFamily = MonospaceFont.Resolve();
        sharePreview.VerticalAlignment = VerticalAlignment.Center;
        ArcTokens.Bind(sharePreview, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeXS);
        ArcTokens.Bind(sharePreview, TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);

        var row = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,Auto") };
        ArcTokens.Bind(row, Grid.ColumnSpacingProperty, ArcDesignTokenKind.Space2);
        row.Children.Add(shareAddress);
        Grid.SetColumn(shareCopy, 1);
        row.Children.Add(shareCopy);

        var body = new StackPanel { Children = { row, sharePreview } };
        body.Classes.Add("share-body");

        shareDialog.Content = body;
        Grid.SetColumnSpan(shareDialog, 2);
        shareDialog.ZIndex = 20;
        return shareDialog;
    }

    private void OpenShareDialog()
    {
        var server = model.Server;
        var snapshot = server.Snapshot;
        var instance = snapshot?.InstanceId is { } id
            ? model.Instances.FirstOrDefault(item => item.Entry.Id == id)
            : null;
        var addresses = snapshot?.ConnectionAddresses?
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        // Loopback addresses stay in the list on purpose: they are the safest way to test locally.
        if (!server.Active || instance?.Entry.Definition is not { } definition || addresses.Length == 0)
        {
            notifications.Show(new ToastOptions
            {
                Title = "未识别到有效地址",
                Description = "服务器运行后才能分享连接命令",
                Kind = ToastKind.Warning
            });
            return;
        }
        shareInstanceId = instance.Entry.Id;
        sharePort = definition.GamePort;
        shareAddress.ItemsSource = addresses;
        shareAddress.SelectedItem = null;
        shareCopy.Value = "";
        SetPreview(null);
        shareDialog.IsOpen = true;
    }

    private void UpdateShareCommand()
    {
        if (shareAddress.SelectedItem is not string address || sharePort is <= 0 or > 65535)
        {
            shareCopy.Value = "";
            shareCopy.IsEnabled = false;
            SetPreview(null);
            return;
        }
        var command = "connect " + Endpoint(address, sharePort);
        shareCopy.Value = command;
        shareCopy.IsEnabled = true;
        SetPreview(command);
    }

    /// <summary>
    /// Nothing is selected, nothing is previewed. The line keeps its slot so the dialog does not resize
    /// when a choice is made; only the text is hidden.
    /// </summary>
    private void SetPreview(string? command)
    {
        sharePreview.Text = command is null ? "" : "预览：" + command;
        sharePreview.Opacity = command is null ? 0 : 1;
    }

    /// <summary>IPv6 literals need brackets in a CS2 connect command; IPv4 and host names do not.</summary>
    private static string Endpoint(string address, int port) =>
        address.Contains(':', StringComparison.Ordinal) ? "[" + address + "]:" + port.ToString() : address + ":" + port.ToString();

    /// <summary>A share link that no longer matches the live run must not stay open.</summary>
    private void SyncShareDialog()
    {
        if (!shareDialog.IsOpen) return;
        var snapshot = model.Server.Snapshot;
        if (model.Server.Active && snapshot?.InstanceId == shareInstanceId) return;
        shareDialog.IsOpen = false;
    }
}
