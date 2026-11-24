using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace HCSL.Host.Views.Pages;

internal static class RowMenuConfirmation
{
    public static void Replace(GlassMenu menu, Control source, Canvas layer, Border anchor,
        Popover popover, Func<bool> prepare)
    {
        var origin = menu.IsAttachedToVisualTree()
            ? layer.PointToClient(menu.PointToScreen(default))
            : source.TranslatePoint(default, layer);
        menu.Close();
        Dispatcher.UIThread.Post(() =>
        {
            if (origin is not { } point || !source.IsAttachedToVisualTree() || !prepare()) return;
            Canvas.SetLeft(anchor, point.X);
            Canvas.SetTop(anchor, point.Y - anchor.Height);
            layer.UpdateLayout();
            popover.ShowAt(anchor);
        });
    }
}
