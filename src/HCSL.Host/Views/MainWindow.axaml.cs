using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using TKCode.Arc;

namespace HCSL.Host.Views;

public partial class MainWindow : Window
{
    private Bitmap? _background;
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => ResetBackground();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
                MaximizeGlyph.Data = WindowState == WindowState.Maximized
                    ? ArcIcons.Minimize2 : ArcIcons.Square;
        };
    }

    private void DragTitleBar(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2) ToggleMaximizeWindow(sender, new RoutedEventArgs());
        else BeginMoveDrag(e);
        e.Handled = true;
    }

    private void MinimizeWindow(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void ToggleMaximizeWindow(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseWindow(object? sender, RoutedEventArgs e) => Close();
    public void SetBackground(Stream stream)
    {
        var image = new Bitmap(stream);
        BackgroundLayer.ImageSource = image;
        _background?.Dispose();
        _background = image;
    }
    public void ResetBackground()
    {
        BackgroundLayer.ImageSource = null;
        _background?.Dispose();
        _background = null;
    }
}
