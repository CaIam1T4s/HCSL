using System.Text.RegularExpressions;
using HCSL.Core;

namespace HCSL.Infrastructure.Server;

/// <summary>Translates framework-specific log evidence into the generic map tracker.</summary>
internal interface IServerReadinessObserver
{
    void Observe(string line, ServerMapTracker tracker);
}

internal sealed class StandardReadinessObserver : IServerReadinessObserver
{
    public void Observe(string line, ServerMapTracker tracker) { }
}

/// <summary>ModSharp replaces the normal SV: map/player lines with its activation log.</summary>
internal sealed class ModSharpReadinessObserver : IServerReadinessObserver
{
    private static readonly Regex Activation = new(
        @"\|\s*(?<kind>Virtual_GameActivate|Detour_ActiveServer)\s*\|\s*(?<map>[A-Za-z0-9_]+)\s*$",
        RegexOptions.CultureInvariant);

    public void Observe(string line, ServerMapTracker tracker)
    {
        var match = Activation.Match(line);
        if (!match.Success || !ServerLastRunStore.IsMapName(match.Groups["map"].Value)) return;
        tracker.SetMap(match.Groups["map"].Value);
        tracker.SetActive();
        if (match.Groups["kind"].Value == "Detour_ActiveServer") tracker.SetPlayers();
    }
}

internal static class ServerReadinessObservers
{
    public static IServerReadinessObserver For(FrameworkReadinessProfile profile) => profile switch
    {
        FrameworkReadinessProfile.ModSharp => new ModSharpReadinessObserver(),
        _ => new StandardReadinessObserver()
    };
}
