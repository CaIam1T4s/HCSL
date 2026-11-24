using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace HCSL.Host.Views;

internal static class TreeTableHeaderAlignment
{
    public static void Attach(TreeTable table)
    {
        table.LayoutUpdated += (_, _) =>
        {
            // Arc 1.1 hides the expand-all control when there are no branches, but
            // root rows keep their chevron slot. Reserve the same slot in the header.
            var header = table.GetVisualDescendants().OfType<Panel>()
                .FirstOrDefault(p => p.GetType().Name == "TreeTableHeader");
            if (header is null) return;
            foreach (var column in header.Children.OfType<StackPanel>())
            {
                if (column.Children is not [Control glyph, Control label] ||
                    glyph.GetType().Name != "TreeTableGlyphButton" || label.GetType().Name != "TreeTableHeaderCell") continue;
                var margin = new Thickness(glyph.IsVisible ? 0 : glyph.Width + column.Spacing, 0, 0, 0);
                if (label.Margin != margin) label.Margin = margin;
                break;
            }
        };
    }
}
