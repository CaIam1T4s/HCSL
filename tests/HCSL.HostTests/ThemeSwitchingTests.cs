using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using HCSL.Host.ViewModels;
using HCSL.Host.Views;
using HCSL.UI.Shared.Services;
using TKCode.Arc;

internal static class ThemeSwitchingTests
{
    public static void Run(MainWindow owner)
    {
        foreach (var assembly in new[] { typeof(MainWindow).Assembly, typeof(CenteredShadow).Assembly })
            Check(assembly.GetReferencedAssemblies().All(a => a.Name != "HCSL.UI.Diagnostics"),
                "Product assemblies must not reference the development diagnostics assembly.");

        foreach (var type in new[] { "ThemeSnapshotProbe", "ThemeEffectIsolation", "ThemeTransitionIsolation" })
            Check(typeof(CenteredShadow).Assembly.GetType($"HCSL.UI.Shared.Services.{type}") is null,
                "Diagnostics implementations must no longer be compiled into UI.Shared.");

        var shell = (ShellViewModel)owner.DataContext!;
        var selected = shell.SelectedTheme;
        var variables = new[] { "HCSL_THEME_TRANSITION", "HCSL_THEME_EFFECTS", "HCSL_THEME_CAPTURE_DIR" };
        var original = variables.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        var directory = Path.Combine(Path.GetTempPath(), $"hcsl-product-no-theme-capture-{Guid.NewGuid():N}");
        var stderr = Console.Error;
        using var output = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(variables[0], "direct");
            Environment.SetEnvironmentVariable(variables[1], "all");
            Environment.SetEnvironmentVariable(variables[2], directory);
            Console.SetError(output);
            var trigger = owner.FindControl<ThemeSwitcher>("SidebarThemeSwitcher")!;
            var next = selected == "浅色" ? ThemeSwitcherAppearance.Dark : ThemeSwitcherAppearance.Light;
            var request = new ThemeSwitcherChangeRequestedEventArgs(ThemeSwitcher.ThemeChangeRequestedEvent,
                next, ThemeSwitcherVariant.Reveal, trigger, default);
            trigger.RaiseEvent(request);
            var expected = next == ThemeSwitcherAppearance.Dark ? "深色" : "浅色";
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (shell.SelectedTheme != expected)
            {
                if (DateTime.UtcNow > deadline) throw new Exception("Normal theme switching did not settle.");
                Layout(); Thread.Sleep(5);
            }
            Layout();
            Check(request.Handled && !Directory.Exists(directory) && !output.ToString().Contains("[HCSL.theme]"),
                "Product theme switching must ignore old diagnostic variables, export no snapshots and log no geometry.");
        }
        finally
        {
            Console.SetError(stderr);
            foreach (var variable in original) Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            shell.SelectedTheme = selected;
            Layout();
        }
        Console.WriteLine("PASS gui.theme.product-switching-ignores-diagnostics-and-no-product-reference");
    }

    private static void Layout() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
