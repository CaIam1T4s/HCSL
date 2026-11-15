using Avalonia;
using TKCode.Arc.Primitives;

namespace HCSL.UI.Shared.Services;

/// <summary>Keeps modal content alive until Arc completes its exit, including reduced/off motion.</summary>
public static class PresenceLifecycle
{
    public static Task HideAsync(PresenceHost host)
    {
        if (!host.IsPresent) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Cleanup()
        {
            host.Exited -= Exited;
            host.DetachedFromVisualTree -= Detached;
            completion.TrySetResult();
        }
        void Exited(object? sender, EventArgs e) => Cleanup();
        void Detached(object? sender, VisualTreeAttachmentEventArgs e) => Cleanup();
        host.Exited += Exited;
        host.DetachedFromVisualTree += Detached;
        host.IsPresent = false;
        return completion.Task;
    }
}
