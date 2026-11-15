using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using HCSL.Host.Views;
using HCSL.UI.Diagnostics;
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
            var stderr = Console.Error;
            using var diagnostic = new StringWriter();
            try
            {
                Console.SetError(diagnostic);
                var windowDiagnostics = new ThemeWindowDiagnostics(owner);
                Pump(ThemeTransitionIsolation.RunAsync(trigger, ThemeSwitcherVariant.Reveal, () => Task.CompletedTask,
                    ThemeTransitionMode.Direct, phase => windowDiagnostics.LogGeometry(ThemeTransitionMode.Direct, phase)));
                Check(diagnostic.ToString().Contains("mode=Direct") && diagnostic.ToString().Contains("phase=complete"),
                    "Tool-owned geometry checkpoints must remain available without installing a product handler.");
            }
            finally { Console.SetError(stderr); }
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
