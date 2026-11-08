namespace HCSL.Core;

public enum ServerMemoryDecision { Normal, ConfirmationRequired, Insufficient }

/// <summary>Available memory, not installed RAM. Consent never bypasses the hard floor.</summary>
public static class ServerMemoryPolicy
{
    public const long MiB = 1024 * 1024;
    public const long MinimumBytes = 3072 * MiB;
    public const long RecommendedBytes = 4096 * MiB;
    public static ServerMemoryDecision Evaluate(long availableBytes, bool allowLowMemory = false) =>
        availableBytes < MinimumBytes ? ServerMemoryDecision.Insufficient :
        availableBytes < RecommendedBytes && !allowLowMemory ? ServerMemoryDecision.ConfirmationRequired :
        ServerMemoryDecision.Normal;
}
