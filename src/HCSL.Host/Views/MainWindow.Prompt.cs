using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using HCSL.UI.Shared.Controls;

namespace HCSL.Host.Views;

/// <summary>Small single-field prompt shared by the instance pages (pool names, future renames).</summary>
public partial class MainWindow
{
    internal Task<int> ShowWorkshopPreviewAsync(StackPanel content, string[] choices) => ShowOverlayAsync(content, choices, hideConsole: false);

    public async Task<string?> TextAsync(string title, string label, string? initial = null, int maxLength = 40, bool hideConsole = true, string confirmLabel = "保存")
    {
        var content = new StackPanel { Spacing = 12 };
        content.Classes.Add("instance-editor");
        var heading = new TextBlock { Text = title };
        ArcTokens.Bind(heading, TextBlock.FontSizeProperty, ArcDesignTokenKind.TypeLG);
        content.Children.Add(heading);
        var input = new Input
        {
            Text = initial ?? "",
            Label = label,
            IsLabelVisible = false,
            PlaceholderText = label,
            MaxLength = maxLength,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        content.Children.Add(new ElevatedSurface(input) { HorizontalAlignment = HorizontalAlignment.Stretch });
        while (await ShowOverlayAsync(content, ["取消", confirmLabel], hideConsole) == 1)
            if ((input.Text ?? "").Trim() is { Length: > 0 } text) return text;
        return null;
    }
}
