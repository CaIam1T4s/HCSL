using System.Text.RegularExpressions;

namespace HCSL.Core;

public enum SteamAuthenticationState { Unknown, WaitingForSteam, RequestingCertificate, Ready, Failed }
public enum SdrConfigurationState { Unknown, Retrying, Ready }

/// <summary>Observed protocol readiness, not a reachability or player-admission guarantee.</summary>
public sealed record ServerNetworkReadiness(
    SteamAuthenticationState Authentication = SteamAuthenticationState.Unknown,
    SdrConfigurationState Configuration = SdrConfigurationState.Unknown,
    bool ClientCertificateRejected = false,
    bool Delayed = false)
{
    public const int WarningAfterSeconds = 120;

    /// <summary>Parse original engine lines before secret redaction; retain no raw text or identities.</summary>
    public ServerNetworkReadiness Observe(string line)
    {
        line = line.Trim();
        var auth = Regex.Match(line, @"^AuthStatus \([^\r\n]*\):\s+(Waiting|Attempting|OK|Failed)\b");
        if (auth.Success)
        {
            var state = auth.Groups[1].Value switch
            {
                "Waiting" => SteamAuthenticationState.WaitingForSteam,
                "Attempting" => SteamAuthenticationState.RequestingCertificate,
                "OK" => SteamAuthenticationState.Ready,
                _ => SteamAuthenticationState.Failed
            };
            return this with { Authentication = state, ClientCertificateRejected = state == SteamAuthenticationState.Ready ? false : ClientCertificateRejected };
        }
        if (line.StartsWith("SDR network config fetch attempt #", StringComparison.Ordinal) && line.Contains("failed.", StringComparison.Ordinal))
            return this with { Configuration = SdrConfigurationState.Retrying };
        if (line.StartsWith("Got SDR network config.", StringComparison.Ordinal) && line.EndsWith(" OK", StringComparison.Ordinal))
            return this with { Configuration = SdrConfigurationState.Ready };
        if (line.StartsWith("Failed to accept connection from ", StringComparison.Ordinal) && line.Contains("Bad cert:", StringComparison.Ordinal))
            return this with { ClientCertificateRejected = true };
        if (line.StartsWith("Accepting Steam Net connection ", StringComparison.Ordinal))
            return this with { ClientCertificateRejected = false };
        return this;
    }

    public ServerNetworkReadiness WithElapsed(TimeSpan sinceMapReady) => this with
    {
        Delayed = sinceMapReady >= TimeSpan.FromSeconds(WarningAfterSeconds)
            && (Authentication != SteamAuthenticationState.Ready || Configuration == SdrConfigurationState.Retrying)
    };
}
