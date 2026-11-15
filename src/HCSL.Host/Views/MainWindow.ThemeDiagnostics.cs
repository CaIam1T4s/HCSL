using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using HCSL.Host.Views.Pages;
using HCSL.UI.Shared.Services;
using TKCode.Arc;

namespace HCSL.Host.Views;

public partial class MainWindow
{
    private string? lastThemeGeometry;

    private void TryCaptureThemeSnapshot(string directory, string phase)
    {
        try
        {
            ThemeSnapshotProbe.Capture(this, directory, phase);
            Console.Error.WriteLine($"[HCSL.theme] independent snapshots saved: {directory} (contains visible window content)");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[HCSL.theme] snapshot export FAILED: {error.GetType().Name}: {error.Message}");
        }
    }

    private void LogThemeGeometry(ThemeTransitionMode mode, string phase)
    {
        // Explicitly enabled diagnostics only. No screenshots/files, instance names or identifiers are recorded.
        try
        {
            var geometry = new StringBuilder($"scale={RenderScaling} theme={ActualThemeVariant} window={Bounds.Size}");
            var layer = this.GetVisualDescendants().OfType<OverlayLayer>().FirstOrDefault();
            if (layer is not null)
            {
                var offset = layer.TranslatePoint(default, this) ?? default;
                geometry.Append($" overlayBounds={layer.Bounds} overlayAt={offset} " +
                    $"expectedDest={new Rect(-offset.X, -offset.Y, Bounds.Width, Bounds.Height)} " +
                    $"overlayChildren={string.Join(';', layer.Children.Select(c => $"{c.GetType().Name}:{c.Bounds}"))}");
            }
            var page = this.GetVisualDescendants().OfType<InstancesPage>().FirstOrDefault(p => p.IsEffectivelyVisible);
            var index = 0;
            foreach (var row in page?.GetVisualDescendants().OfType<RadioGroupItem>() ?? [])
            {
                var children = row.GetVisualDescendants().ToArray();
                var surface = children.OfType<Border>().FirstOrDefault(b => b.Name == "PART_Row");
                var label = children.OfType<ContentPresenter>().FirstOrDefault(p => p.Name == "PART_Label");
                var text = children.OfType<TextBlock>().FirstOrDefault();
                var labelAt = surface is null ? null : label?.TranslatePoint(default, surface);
                var textAt = surface is null ? null : text?.TranslatePoint(default, surface);
                var blurLayers = children.OfType<ContentPresenter>().Count(p => p.Effect is not null);
                geometry.AppendLine().Append($"row={index++} bounds={row.Bounds} surface={surface?.Bounds} " +
                    $"label={label?.Bounds} labelAt={labelAt} textAt={textAt} textInWindow={text?.TranslatePoint(default, this)} transform={row.RenderTransform?.Value} blurLayers={blurLayers}");
            }
            var current = geometry.ToString();
            if (phase == "layout" && current == lastThemeGeometry) return;
            lastThemeGeometry = current;
            Console.Error.WriteLine($"[HCSL.theme] mode={mode} phase={phase} {current}");
        }
        catch (IOException)
        {
            // A closed diagnostic console must not prevent the appearance change.
        }
    }
}
