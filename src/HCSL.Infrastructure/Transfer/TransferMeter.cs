using System.Diagnostics;

namespace HCSL.Infrastructure.Transfer;

/// <summary>
/// Turns byte counters into a smoothed transfer rate. One instance per operation; the caller feeds
/// every sample it observes and displays whatever comes back. A rate is only reported once the window
/// holds enough evidence, so a single line never produces a bogus speed.
/// </summary>
public sealed class TransferMeter(TimeSpan? window = null, TimeSpan? minimumSpan = null)
{
    private readonly TimeSpan window = window ?? TimeSpan.FromSeconds(5);
    private readonly TimeSpan minimumSpan = minimumSpan ?? TimeSpan.FromSeconds(1);
    private readonly Queue<(double At, long Done)> samples = new();
    private long? total;

    /// <summary>Total bytes of the current transfer, or null when the source did not report one.</summary>
    public long? TotalBytes => total;

    /// <summary>Forgets every sample; call when a transfer restarts so the rate cannot go negative.</summary>
    public void Reset()
    {
        samples.Clear();
        total = null;
    }

    /// <summary>
    /// Records one observation. <paramref name="done"/> must be the cumulative byte count for the
    /// current transfer. Returns the smoothed bytes per second, or null while there is not enough
    /// evidence yet.
    /// </summary>
    public double? Sample(long done, long? totalBytes = null)
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (done < 0) return null;
        if (totalBytes is > 0) total = totalBytes;
        // A counter that moved backwards means a new transfer started under the same operation.
        if (samples.Count > 0 && done < samples.Last().Done) samples.Clear();
        samples.Enqueue((now, done));
        // Two bounds: a fixed time window, and a hard cap so a fast source cannot grow the queue.
        while (samples.Count > 2 && now - samples.Peek().At > window.TotalSeconds) samples.Dequeue();
        while (samples.Count > 256) samples.Dequeue();
        if (samples.Count < 2) return null;
        var first = samples.Peek();
        var span = now - first.At;
        if (span < minimumSpan.TotalSeconds) return null;
        var delta = done - first.Done;
        return delta > 0 ? delta / span : 0;
    }
}
