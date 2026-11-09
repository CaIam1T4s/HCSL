using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HCSL.UI.Shared.Services;
using TKCode.Arc;

internal static class InputPointerPlacementTests
{
    public static void Run()
    {
        foreach (var field in new TextBox[] { new Input(), new PasswordField(), new Input { IsReadOnly = true },
                     new Textarea { FrameHeight = 90, IsResizable = false } })
        {
            field.Text = "de_dust2"; field.Width = 480;
            var other = new TKCode.Arc.Button { Content = "Other" };
            var window = new Window { Width = 600, Height = 260,
                Content = new StackPanel { Margin = new Thickness(20), Children = { field, other } } };
            window.Show(); Layout();
            var presenter = field.GetVisualDescendants().OfType<TextPresenter>().Single(p => p.Name == "PART_TextPresenter");
            if (!InputPointerPlacement.GetEnabled(field)) throw new Exception("Shared pointer behavior not applied.");
            void Click(double x, double y)
            {
                var point = presenter.TranslatePoint(new Point(x, y), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            }
            foreach (var y in new[] { -4d, presenter.Bounds.Height + 4 })
            {
                other.Focus(); field.CaretIndex = 0; field.SelectionStart = field.SelectionEnd = 0;
                Click(200, y);
                if (field.CaretIndex != 8 || !field.IsFocused)
                    throw new Exception($"{field.GetType().Name}: padding click did not place caret at text end.");
            }
            Click(0, presenter.Bounds.Height / 2);
            if (field.CaretIndex != 0) throw new Exception("Native start-of-text click was overridden.");
            Click(200, presenter.Bounds.Height / 2);
            if (field.CaretIndex != 8) throw new Exception("Native end-of-text click was overridden.");
            // A click at a measured interior glyph must not be redirected to the end.
            Click(15, presenter.Bounds.Height / 2);
            if (field.CaretIndex <= 0 || field.CaretIndex >= 8) throw new Exception("Interior positioning was overridden.");
            foreach (var startY in new[] { presenter.Bounds.Height / 2, -4d })
            {
                var start = presenter.TranslatePoint(new Point(200, startY), window)!.Value;
                var end = presenter.TranslatePoint(new Point(0, presenter.Bounds.Height / 2), window)!.Value;
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(end, Avalonia.Input.RawInputModifiers.LeftMouseButton);
                window.MouseUp(end, MouseButton.Left); Layout();
                if (field.SelectionStart == field.SelectionEnd) throw new Exception("Drag selection was lost.");
            }
            window.Close(); Layout();
        }
        Console.WriteLine("PASS gui.shared-fields.padding-click-and-native-caret-input-password-readonly-textarea");
    }
    private static void Layout()
    {
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }
}
