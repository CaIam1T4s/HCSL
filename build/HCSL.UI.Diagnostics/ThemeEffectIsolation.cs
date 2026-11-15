using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace HCSL.UI.Diagnostics;

public enum ThemeEffectIsolationMode { None, Shadows, All }

/// <summary>Temporary diagnostic overrides; disposing reveals the current bindings, not stale saved values.</summary>
public sealed class ThemeEffectIsolation : IDisposable
{
    private readonly Visual root;
    private readonly ThemeEffectIsolationMode mode;
    private readonly HashSet<Visual> seen = [];
    private readonly List<IDisposable> overrides = [];
    private bool disposed;

    public static ThemeEffectIsolationMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "shadows" => ThemeEffectIsolationMode.Shadows,
        "all" => ThemeEffectIsolationMode.All,
        _ => ThemeEffectIsolationMode.None
    };

    public ThemeEffectIsolation(Visual root, ThemeEffectIsolationMode mode)
    {
        this.root = root;
        this.mode = mode;
        try { Refresh(); }
        catch { Dispose(); throw; }
    }

    // Also cover controls newly instantiated while applying the theme.
    public void Refresh()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (mode == ThemeEffectIsolationMode.None) return;
        foreach (var visual in root.GetVisualDescendants().Prepend(root))
        {
            if (!seen.Add(visual)) continue;
            if (visual is Border border) Keep(border.SetValue(Border.BoxShadowProperty, default, BindingPriority.Animation));
            if (visual is ContentPresenter presenter) Keep(presenter.SetValue(ContentPresenter.BoxShadowProperty, default, BindingPriority.Animation));
            if (mode == ThemeEffectIsolationMode.All) Keep(visual.SetValue(Visual.EffectProperty, null, BindingPriority.Animation));
        }
    }

    private void Keep(IDisposable? lease)
    {
        if (lease is not null) overrides.Add(lease);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        for (var i = overrides.Count - 1; i >= 0; i--) overrides[i].Dispose();
        overrides.Clear(); seen.Clear();
    }
}
