using System.ComponentModel;
using HCSL.Core;
using TKCode.Arc;

namespace HCSL.Host.ViewModels;

/// <summary>One catalog row: the map, its source, and which pool of the current instance holds it.</summary>
public sealed class MapRow : INotifyPropertyChanged
{
    private readonly List<PresenceStackViewer> pools = [];
    private bool inPool, isSelected, canJoinPool;
    private string? poolName;

    public MapRow(MapCatalogEntry entry, IReadOnlyList<string> poolNames)
    {
        Entry = entry;
        SetPools(poolNames);
    }

    public MapCatalogEntry Entry { get; }
    public string Name => Entry.Name;
    public string? WorkshopId => Entry.WorkshopId;
    public MapCatalogSource Source => Entry.Source;
    public bool Downloaded => !Entry.Incomplete && (Entry.Source == MapCatalogSource.Standard || Entry.SizeBytes > 0 || Entry.WorkshopId is not null && !Entry.Incomplete);
    public bool InPool => inPool;

    /// <summary>The page keeps at most one row selected; the radio column binds here.</summary>
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (isSelected == value) return;
            isSelected = value;
            Raise(nameof(IsSelected));
        }
    }

    /// <summary>Whether the map may be checked in the pool editor: previews and unfinished downloads are ignored.</summary>
    public bool CanJoinPool
    {
        get => canJoinPool;
        set
        {
            if (canJoinPool == value) return;
            canJoinPool = value;
            Raise(nameof(CanJoinPool));
        }
    }
    public string Key => Entry.Key;
    public ServerMapTarget Target => Entry.Source == MapCatalogSource.Workshop
        ? new(ServerMapKind.Workshop, Entry.WorkshopId!)
        : new(ServerMapKind.Standard, Entry.Name);
    public IReadOnlyList<PresenceStackViewer> Pools => pools;
    public string PoolText => poolName ?? "";

    /// <summary>The filter chip this row answers: built-in, official preview, Workshop or not downloaded.</summary>
    public string FilterSource => Entry.Source switch
    {
        MapCatalogSource.Preview => "preview",
        MapCatalogSource.Workshop => Entry.Incomplete ? "missing" : "workshop",
        _ => "standard"
    };

    public string SourceText => Entry.Source switch
    {
        MapCatalogSource.Preview => "预览",
        MapCatalogSource.Workshop => Entry.Incomplete ? "未下载" : "工坊",
        _ => "内置"
    };

    /// <summary>Both spellings a source chip may carry: the option value and its label.</summary>
    public IReadOnlyList<string> SourceFilterValues => [FilterSource, SourceText];

    public BadgeTone SourceTone => Entry.Source switch
    {
        MapCatalogSource.Preview => BadgeTone.Neutral,
        MapCatalogSource.Workshop => Entry.Incomplete ? BadgeTone.Neutral : BadgeTone.Info,
        _ => BadgeTone.Success
    };

    /// <summary>Every pool of the instance that holds this map; the presence stack shows their initials.</summary>
    public void SetPools(IReadOnlyList<string> names)
    {
        pools.Clear();
        foreach (var name in names) pools.Add(new PresenceStackViewer(name));
        poolName = names.Count == 0 ? null : string.Join("、", names);
        inPool = names.Count > 0;
        Raise(nameof(InPool));
        Raise(nameof(PoolText));
        Raise(nameof(Pools));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
