using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc.Motion;
using TKCode.Arc.Primitives;

namespace HCSL.UI.Shared.Controls;

/// <summary>Arc spring translations keyed by stable items, including containers recreated by Avalonia Move.</summary>
public sealed class AnimatedItemReorder
{
    private readonly SelectingItemsControl owner;
    private readonly Dictionary<Control, RowMotion> motions = [];
    private Dictionary<object, (Point Position, double Velocity)>? before;
    private int generation;
    public bool IsAnimating { get; private set; }

    public AnimatedItemReorder(SelectingItemsControl owner)
    {
        this.owner = owner;
        owner.LayoutUpdated += LayoutUpdated;
        owner.DetachedFromVisualTree += (_, _) => Reset();
    }

    public void Capture()
    {
        if (!owner.IsEffectivelyVisible) { before = null; return; }
        before = [];
        foreach (var item in owner.Items)
            if (item is not null && owner.ContainerFromItem(item) is { } row && row.Bounds.Height > 0 &&
                row.TranslatePoint(default, owner) is { } point)
                before[item] = (point, motions.TryGetValue(row, out var motion) ? motion.Velocity : 0);
    }

    public void Reset()
    {
        generation++;
        before = null;
        foreach (var motion in motions.Values) motion.Dispose();
        motions.Clear();
        IsAnimating = false;
    }

    private void LayoutUpdated(object? sender, EventArgs e)
    {
        if (before is not { } previous) return;
        before = null;
        var currentGeneration = ++generation;
        List<Task<bool>> tasks = [];
        foreach (var removed in motions.Keys.Where(row => !row.IsAttachedToVisualTree()).ToArray())
        {
            motions[removed].Dispose();
            motions.Remove(removed);
        }
        foreach (var (item, pose) in previous)
        {
            if (owner.ContainerFromItem(item) is not { } row || !row.IsAttachedToVisualTree() ||
                row.TranslatePoint(default, owner) is not { } next) continue;
            if (!motions.TryGetValue(row, out var motion))
                motions[row] = motion = new RowMotion(row, pose.Position.Y, UpdateIndicator);
            var target = next.Y - motion.Offset;
            if (Math.Abs(target - pose.Position.Y) < 0.01 && Math.Abs(motion.Offset) < 0.01) continue;
            tasks.Add(motion.Move(pose.Position.Y, target, pose.Velocity));
        }
        IsAnimating = tasks.Any(t => !t.IsCompleted);
        _ = FinishAsync(tasks, currentGeneration);
    }

    private async Task FinishAsync(List<Task<bool>> tasks, int currentGeneration)
    {
        await Task.WhenAll(tasks).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != currentGeneration) return;
            IsAnimating = false;
            owner.InvalidateArrange();
        });
    }

    private void UpdateIndicator()
    {
        if (owner.SelectedItem is not { } selected || owner.ContainerFromItem(selected) is not { } row ||
            owner.GetVisualDescendants().OfType<GlidingIndicator>().FirstOrDefault() is not { IsVisible: true } indicator ||
            row.TranslatePoint(default, indicator) is not { } position) return;
        // Keep Arc's selection outline attached to the moving selected row, not on a competing spring.
        indicator.MoveTo(new Rect(position, row.Bounds.Size).Deflate(indicator.StrokeThickness / 2), animate: false);
    }

    private sealed class RowMotion : IDisposable
    {
        private readonly Control row;
        private readonly ITransform? original;
        private readonly TranslateTransform translation = new();
        private readonly MotionValue<double> position;
        private double layoutY;
        public double Offset => translation.Y;
        public double Velocity => position.Velocity;

        public RowMotion(Control row, double initialY, Action changed)
        {
            this.row = row;
            original = row.RenderTransform;
            layoutY = initialY;
            row.RenderTransform = original is null ? translation : new TransformGroup
            {
                Children = { original is Transform transform ? transform : new MatrixTransform(original.Value), translation }
            };
            // A theme cannot recover the previous visual position after a data move. Arc provides the frame
            // scheduler, Morph spring, interruption velocity and Reduced/Off policy; no custom timer is used.
            position = new MotionValue<double>(row, initialY, MotionVectors.Double);
            position.Changed += value => { translation.Y = value - layoutY; changed(); };
        }

        public Task<bool> Move(double shownY, double targetY, double velocity)
        {
            layoutY = targetY;
            position.Jump(shownY);
            return position.AnimateTo(targetY, MotionSpec.Spring(ArcSpring.Morph), velocity);
        }

        public void Dispose()
        {
            position.Dispose();
            row.RenderTransform = original;
        }
    }
}
