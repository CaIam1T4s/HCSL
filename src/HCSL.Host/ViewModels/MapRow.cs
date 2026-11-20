using System.ComponentModel;
using HCSL.Core;
using TKCode.Arc;

namespace HCSL.Host.ViewModels;

/// <summary>One catalog row: the map, its source, and which pool of the current instance holds it.</summary>
public sealed class MapRow : INotifyPropertyChanged
{
    private readonly List<PresenceStackViewer> pools = [];
    private bool inPool;
    private string? poolName;

    public MapRow(MapCatalogEntry entry, bool inPool, string? poolName)
    {
        Entry = entry;
        this.inPool = inPool;
        this.poolName = poolName;
        RebuildPools();
    }

    public MapCatalogEntry Entry { get; }
    public string Name => Entry.Name;
    public string? WorkshopId => Entry.WorkshopId;
    public MapCatalogSource Source => Entry.Source;
    public bool Downloaded => !Entry.Incomplete && (Entry.Source == MapCatalogSource.Standard || Entry.SizeBytes > 0 || Entry.WorkshopId is not null && !Entry.Incomplete);
    public bool InPool => inPool;
    public string Key => Entry.Key;
    public ServerMapTarget Target => Entry.Source == MapCatalogSource.Workshop
        ? new(ServerMapKind.Workshop, Entry.WorkshopId!)
        : new(ServerMapKind.Standard, Entry.Name);
    public IReadOnlyList<PresenceStackViewer> Pools => pools;
    public string PoolText => poolName ?? "";

    /// <summary>The filter chip value for this row: built-in, Workshop or not downloaded.</summary>
    public string FilterSource => Entry.Source == MapCatalogSource.Standard ? "standard"
        : Entry.Incomplete ? "missing" : "workshop";

    public string SourceText => Entry.Source == MapCatalogSource.Standard ? "内置"
        : Entry.Incomplete ? "未下载" : "工坊";

    public BadgeTone SourceTone => Entry.Source == MapCatalogSource.Standard ? BadgeTone.Success
        : Entry.Incomplete ? BadgeTone.Neutral : BadgeTone.Info;

    public void SetPool(string? name)
    {
        if (poolName == name) return;
        poolName = name;
        inPool = name is not null;
        RebuildPools();
        Raise(nameof(InPool));
        Raise(nameof(PoolText));
        Raise(nameof(Pools));
    }

    private void RebuildPools()
    {
        pools.Clear();
        if (poolName is not null) pools.Add(new PresenceStackViewer(poolName));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
