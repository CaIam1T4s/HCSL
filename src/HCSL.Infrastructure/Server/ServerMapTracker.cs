using HCSL.Core;
namespace HCSL.Infrastructure.Server;

public sealed class ServerMapTracker
{
    public string? Map { get; private set; }
    public ServerMapProgress Progress { get; private set; } = new(ServerMapPhase.None);
    public void Unconfirmed() => Progress = new(ServerMapPhase.Unconfirmed);
    public void MarkReady() => Progress = new(ServerMapPhase.Ready);
    public void BeginTransition() { Map = null; active = players = false; Progress = new(ServerMapPhase.Loading); }
    public bool Complete => active && Map is not null && players;
    /// <summary>The map named by the last CHANGE LEVEL line, before CS2 queues the request.</summary>
    public string? PendingChangeLevel { get; private set; }
    private bool active, players;
    private readonly IServerReadinessObserver observer;

    public ServerMapTracker(FrameworkReadinessProfile profile = FrameworkReadinessProfile.Standard)
        => observer = ServerReadinessObservers.For(profile);

    internal void SetMap(string map) => Map = map;
    internal void SetActive() => active = true;
    internal void SetPlayers() => players = true;

    /// <summary>
    /// Recognizes the end-of-match state where CS2 prints the intended map but queues
    /// <c>Changelevel ()</c> with an empty argument. Returns that intended map once.
    /// </summary>
    public string? ObserveEmptyChangeLevel(string line)
    {
        var declared = System.Text.RegularExpressions.Regex.Match(line, @"^CHANGE LEVEL:\s+(?<map>[A-Za-z0-9_]+)\s*$");
        if (declared.Success) { PendingChangeLevel = declared.Groups["map"].Value; return null; }
        var request = System.Text.RegularExpressions.Regex.Match(line, @"Changelevel \((?<map>[^)]*)\)");
        if (!request.Success) return null;
        var requested = request.Groups["map"].Value.Trim();
        if (requested.Length != 0) { PendingChangeLevel = null; return null; }
        var target = PendingChangeLevel;
        PendingChangeLevel = null;
        return target;
    }
    public bool Observe(string line)
    {
        // These are observations, never proof of successful download or a particular requested map.
        var ugc = line.Contains("[UGC]", StringComparison.OrdinalIgnoreCase) || line.Contains("workshop", StringComparison.OrdinalIgnoreCase);
        if (ugc && line.Contains("download", StringComparison.OrdinalIgnoreCase))
            Progress = new(line.Contains("fail", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.OrdinalIgnoreCase)
                ? ServerMapPhase.DownloadFailed : ServerMapPhase.DownloadObserved);
        var loading = line.Contains("-> ss_loading", StringComparison.Ordinal)
            || line.Contains("ss_active -> ss_dead", StringComparison.Ordinal);
        if (loading) { Map = null; active = players = false; Progress = new(ServerMapPhase.Loading); }
        if (line.Contains("ss_loading -> ss_active", StringComparison.Ordinal)) active = true;
        observer.Observe(line, this);
        if (ServerLastRunStore.MapFrom(line) is { } map) Map = map;
        if (System.Text.RegularExpressions.Regex.IsMatch(line, @"SV:\s+\d+ player server started")) players = true;
        if (Complete && Progress.Phase is ServerMapPhase.None or ServerMapPhase.Loading) Progress = new(ServerMapPhase.Ready);
        return loading;
    }
}
