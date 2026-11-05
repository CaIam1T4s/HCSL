using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using CS2Launcher.Host.Services;
using CS2Launcher.Host.ViewModels;
using CS2Launcher.Host.Views;
using CS2Launcher.Host.Styles.Themes;

namespace CS2Launcher.Host;

public partial class App : Application
{
    public override void Initialize()
    {
        Resources = ThemeCatalog.CreateResources();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow
            {
                DataContext = new ShellViewModel(new ThemeService(this))
            };
        base.OnFrameworkInitializationCompleted();
    }
}
