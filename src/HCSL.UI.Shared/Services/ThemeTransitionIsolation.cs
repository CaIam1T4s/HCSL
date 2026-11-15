using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TKCode.Arc;

namespace HCSL.UI.Shared.Services;

/// <summary>Opt-in A/B modes for diagnosing theme snapshots; Normal preserves Arc's original transition.</summary>
public enum ThemeTransitionMode { Normal, Observe, Direct, Settled }

public static class ThemeTransitionIsolation
{
    public static ThemeTransitionMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "observe" => ThemeTransitionMode.Observe,
        "direct" => ThemeTransitionMode.Direct,
        "settled" => ThemeTransitionMode.Settled,
        _ => ThemeTransitionMode.Normal
    };

    public static async Task RunAsync(Visual trigger, ThemeSwitcherVariant variant, Func<Task> applyTheme,
        ThemeTransitionMode mode = ThemeTransitionMode.Normal, Action<string>? checkpoint = null)
    {
        if (mode == ThemeTransitionMode.Normal)
        {
            await ThemeSwitcher.RunTransitionAsync(trigger, variant, applyTheme);
            return;
        }

        var window = TopLevel.GetTopLevel(trigger);
        async Task ApplyAsync()
        {
            checkpoint?.Invoke("before-apply");
            await applyTheme();
            checkpoint?.Invoke("after-apply");
            if (mode == ThemeTransitionMode.Settled) await SettleAsync(window);
            else if (mode == ThemeTransitionMode.Direct) window?.UpdateLayout();
            if (mode != ThemeTransitionMode.Observe) checkpoint?.Invoke("after-layout");
        }

        checkpoint?.Invoke("begin");
        if (mode == ThemeTransitionMode.Direct)
        {
            // Bypass only the window snapshot, not the independent row/skeleton/presence animations.
            await ApplyAsync();
        }
        else
        {
            if (mode == ThemeTransitionMode.Settled)
            {
                checkpoint?.Invoke("before-settle");
                await SettleAsync(window);
            }
            checkpoint?.Invoke("before-arc");
            await ThemeSwitcher.RunTransitionAsync(trigger, variant, ApplyAsync);
        }
        checkpoint?.Invoke("complete");
    }

    private static async Task SettleAsync(TopLevel? window)
    {
        if (window is null) return;
        window.UpdateLayout();
        // Flush queued binding/layout work before Arc captures the old window, and after applying the new theme.
        await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
    }
}
