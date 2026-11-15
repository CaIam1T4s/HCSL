using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using HCSL.Host.Views;
using HCSL.Host.ViewModels;
using HCSL.UI.Shared.Services;
using TKCode.Arc;

internal static class ThemeTransitionIsolationTests
{
    public static void Run(MainWindow owner)
    {
        Check(ThemeTransitionIsolation.ParseMode(null) == ThemeTransitionMode.Normal &&
              ThemeTransitionIsolation.ParseMode("unknown") == ThemeTransitionMode.Normal &&
              ThemeTransitionIsolation.ParseMode("observe") == ThemeTransitionMode.Observe &&
              ThemeTransitionIsolation.ParseMode("DIRECT") == ThemeTransitionMode.Direct &&
              ThemeTransitionIsolation.ParseMode(" settled ") == ThemeTransitionMode.Settled,
            "Isolation is opt-in; unknown configuration must preserve the original Arc transition.");
        CheckEffectRestoration();
        var sourceShadow = BoxShadows.Parse("2 8 20 -2 #90000000");
        var centered = CenteredShadow.Center(sourceShadow);
        Check(centered.Count == sourceShadow.Count && centered[0].OffsetX == 0 && centered[0].OffsetY == 0 &&
              centered[0].Blur == sourceShadow[0].Blur && centered[0].Spread == sourceShadow[0].Spread &&
              centered[0].Color == sourceShadow[0].Color,
            "Centered elevation must retain token blur/spread/color rather than inventing an appearance.");
        var original = ArcTheme.State.Appearance;
        var requested = Application.Current!.RequestedThemeVariant;
        var trigger = owner.FindControl<ThemeSwitcher>("SidebarThemeSwitcher")!;
        try
        {
            foreach (var mode in new[] { ThemeTransitionMode.Observe, ThemeTransitionMode.Direct, ThemeTransitionMode.Settled, ThemeTransitionMode.Normal })
            {
                List<string> stages = [];
                var applied = 0;
                var next = ArcTheme.State.Appearance == ArcAppearance.Light ? ArcAppearance.Dark : ArcAppearance.Light;
                Pump(ThemeTransitionIsolation.RunAsync(trigger, ThemeSwitcherVariant.Reveal, () =>
                {
                    applied++;
                    ArcTheme.Apply(t => t.Appearance(next));
                    return Task.CompletedTask;
                }, mode, stages.Add));
                Check(applied == 1 && ArcTheme.State.Appearance == next, "Each strategy must apply the theme exactly once.");
                if (mode == ThemeTransitionMode.Normal)
                    Check(stages.Count == 0, "Normal mode must not add diagnostic checkpoints or extra layout steps.");
                else
                {
                    var expected = mode switch
                    {
                        ThemeTransitionMode.Direct => new[] { "begin", "before-apply", "after-apply", "after-layout", "complete" },
                        ThemeTransitionMode.Observe => new[] { "begin", "before-arc", "before-apply", "after-apply", "complete" },
                        _ => new[] { "begin", "before-settle", "before-arc", "before-apply", "after-apply", "after-layout", "complete" }
                    };
                    Check(stages.SequenceEqual(expected), "Diagnostic checkpoints must distinguish snapshot bypass from settled-layout capture.");
                }
            }
            var shell = (ShellViewModel)owner.DataContext!;
            var originalSelection = shell.SelectedTheme;
            var environment = Environment.GetEnvironmentVariable("HCSL_THEME_TRANSITION");
            var stderr = Console.Error;
            using var diagnostic = new StringWriter();
            try
            {
                Environment.SetEnvironmentVariable("HCSL_THEME_TRANSITION", "direct");
                Console.SetError(diagnostic);
                var next = originalSelection == "浅色" ? ThemeSwitcherAppearance.Dark : ThemeSwitcherAppearance.Light;
                var request = new ThemeSwitcherChangeRequestedEventArgs(ThemeSwitcher.ThemeChangeRequestedEvent,
                    next, ThemeSwitcherVariant.Reveal, trigger, default);
                trigger.RaiseEvent(request);
                Layout();
                Check(request.Handled && shell.SelectedTheme == (next == ThemeSwitcherAppearance.Dark ? "深色" : "浅色") &&
                      diagnostic.ToString().Contains("mode=Direct") && diagnostic.ToString().Contains("phase=complete"),
                    "The actual sidebar handler must honor the opt-in mode and produce geometry checkpoints.");
            }
            finally
            {
                Console.SetError(stderr);
                Environment.SetEnvironmentVariable("HCSL_THEME_TRANSITION", environment);
                shell.SelectedTheme = originalSelection;
            }
            Console.WriteLine("PASS gui.theme.isolation-observe-direct-settled-normal-flow (headless; not a rendered Reveal/DPI verification)");
        }
        finally
        {
            ArcTheme.Apply(t => t.Appearance(original));
            Application.Current.RequestedThemeVariant = requested;
            Layout();
        }
    }

    private static void CheckEffectRestoration()
    {
        Check(ThemeEffectIsolation.ParseMode("shadows") == ThemeEffectIsolationMode.Shadows &&
              ThemeEffectIsolation.ParseMode("ALL") == ThemeEffectIsolationMode.All &&
              ThemeEffectIsolation.ParseMode(null) == ThemeEffectIsolationMode.None,
            "Effect suppression must be opt-in.");
        var original = BoxShadows.Parse("0 4 12 #80000000");
        var changed = BoxShadows.Parse("0 8 20 #90000000");
        var blur = new BlurEffect { Radius = 4 };
        var border = new Border { BoxShadow = original, Effect = blur };
        using (var shadows = new ThemeEffectIsolation(border, ThemeEffectIsolationMode.Shadows))
        {
            Check(border.BoxShadow.Count == 0 && border.Effect == blur, "Shadow-only isolation must retain visual effects.");
            border.BoxShadow = changed;
            Check(border.BoxShadow.Count == 0, "Theme changes must remain suppressed during isolation.");
            var child = new Border { BoxShadow = original };
            border.Child = child; shadows.Refresh();
            Check(child.BoxShadow.Count == 0, "Newly instantiated visuals must also be covered.");
        }
        Check(border.BoxShadow.Equals(changed) && ((Border)border.Child!).BoxShadow.Equals(original),
            "Disposal must restore current values without overwriting bindings/theme changes.");
        try
        {
            using var all = new ThemeEffectIsolation(border, ThemeEffectIsolationMode.All);
            Check(border.Effect is null && border.BoxShadow.Count == 0, "All isolation must also suppress Visual.Effect.");
            throw new OperationCanceledException();
        }
        catch (OperationCanceledException) { }
        Check(border.Effect == blur && border.BoxShadow.Equals(changed), "Cancellation must restore all overrides.");
    }

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Theme transition isolation did not settle.");
            Layout(); Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult(); Layout();
    }
    private static void Layout() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
