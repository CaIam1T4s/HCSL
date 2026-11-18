namespace HCSL.Application.Abstractions;

/// <summary>What the bytes of a transfer currently represent. Speed is only meaningful while downloading.</summary>
public enum TransferPhase { Idle, Downloading, Verifying, Installing }

/// <summary>
/// Measured transfer state for one operation. Unknown values stay null so a caller never shows
/// "0 B/s" or "0 %" when nothing was actually measured.
/// </summary>
public sealed record TransferProgress(
    TransferPhase Phase,
    long? DoneBytes = null,
    long? TotalBytes = null,
    double? BytesPerSecond = null,
    double? Percent = null)
{
    public static readonly TransferProgress Idle = new(TransferPhase.Idle);

    /// <summary>Progress derived from bytes; the upstream percentage is only a fallback.</summary>
    public static TransferProgress Measure(TransferPhase phase, long? done, long? total, double? bytesPerSecond) =>
        new(phase, done, total, bytesPerSecond,
            done is { } have && total is > 0 ? Math.Round(Math.Clamp(have * 100.0 / total.Value, 0, 100), 1) : null);
}
