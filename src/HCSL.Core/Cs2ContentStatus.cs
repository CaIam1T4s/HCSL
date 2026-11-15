namespace HCSL.Core;

/// <summary>Observed local content, independent of deployment/startup baseline validity. Null update availability is unknown.</summary>
public sealed record Cs2ContentStatus(bool Installed, string? BuildId, bool? UpdateAvailable = null);
