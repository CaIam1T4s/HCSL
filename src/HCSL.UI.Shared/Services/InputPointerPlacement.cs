using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace HCSL.UI.Shared.Services;

/// <summary>Map Arc field-frame padding clicks to the nearest rendered text line.</summary>
public sealed class InputPointerPlacement : AvaloniaObject
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<InputPointerPlacement, TextBox, bool>("Enabled");
    public static bool GetEnabled(TextBox control) => control.GetValue(EnabledProperty);
    public static void SetEnabled(TextBox control, bool value) => control.SetValue(EnabledProperty, value);

    static InputPointerPlacement()
    {
        EnabledProperty.Changed.AddClassHandler<TextBox>((box, change) =>
        {
            if (GetEnabled(box))
                box.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Bubble, handledEventsToo: true);
            else box.RemoveHandler(InputElement.PointerPressedEvent, Pressed);
        });
    }

    private static void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBox box || e.ClickCount != 1 ||
            !e.GetCurrentPoint(box).Properties.IsLeftButtonPressed) return;
        // Do not steal clear/reveal buttons, scrollbars or resize handles in field templates.
        if (e.Source is Visual source && source.GetVisualAncestors().Prepend(source)
            .TakeWhile(v => v != box).Any(v => v is Button or ScrollBar or Thumb)) return;
        var presenter = box.GetVisualDescendants().OfType<TextPresenter>()
            .FirstOrDefault(p => p.Name == "PART_TextPresenter");
        if (presenter is null || presenter.Bounds.Width <= 0 || presenter.Bounds.Height <= 0) return;
        var point = e.GetPosition(presenter);
        if (point.Y >= 0 && point.Y < presenter.Bounds.Height && point.X >= 0 && point.X < presenter.Bounds.Width)
            return; // Native text clicks, double-clicks and dragging retain their normal behavior.
        var anchor = box.SelectionStart;
        presenter.MoveCaretToPoint(new Point(Math.Clamp(point.X, 0, presenter.Bounds.Width),
            Math.Clamp(point.Y, 0, Math.Max(0, presenter.Bounds.Height - 0.01))));
        box.CaretIndex = presenter.CaretIndex;
        box.SelectionStart = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? anchor : box.CaretIndex;
        box.SelectionEnd = box.CaretIndex;
        // Leave the event/capture to TextBox so native drag selection is not replaced.
    }
}
