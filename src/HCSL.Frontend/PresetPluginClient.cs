using HCSL.Core;
namespace HCSL.Frontend;

/// <summary>Plugin selection is edited locally and saved with the manifest hash returned by the host.</summary>
public sealed class PresetPluginClient(InstanceClient instances, ServerController server)
{
    public bool CanWrite => PresetEditGate.CanWrite(server);
    public Task<PluginResult> ExecuteAsync(PluginCommand command)
    {
        if (!server.Connected || server.Busy || (command.Action != PluginAction.List &&
            (server.Active || server.State == ServerState.RecoveryRequired)))
            throw new IOException("HCSL-PLUGIN-BUSY: Stop the server and finish recovery before editing plugins.");
        return instances.PluginAsync(command);
    }
}

/// <summary>Ordered plugin draft. Package contents are never loaded into the UI.</summary>
public sealed class PluginDraft
{
    private readonly List<PluginSelection> plugins = [];
    public IReadOnlyList<PluginSelection> Plugins => plugins;
    public PluginResult? Loaded { get; private set; }
    public bool Dirty => Loaded is null || !plugins.SequenceEqual(Loaded.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)));
    public void Load(PluginResult result) { PluginRules.Validate(result.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled)).ToArray()); Loaded = result; plugins.Clear(); plugins.AddRange(result.Selected.Select(s => new PluginSelection(s.Id, s.Version, s.Enabled))); }
    public void Add(string id, string version)
    {
        var next = plugins.Append(new PluginSelection(id, version)).ToArray(); PluginRules.Validate(next); plugins.Add(new(id, version));
    }
    public void Remove(int index) { if (index >= 0 && index < plugins.Count) plugins.RemoveAt(index); }
    public void Toggle(int index) { if (index >= 0 && index < plugins.Count) plugins[index] = plugins[index] with { Enabled = !plugins[index].Enabled }; }
    /// <summary>Swap one step within the manifest; array order is deployment/load order.</summary>
    public int Move(int index, int delta)
    {
        if (plugins.Count < 2 || index < 0 || index >= plugins.Count) return Math.Max(0, index);
        var target = Math.Clamp(index + delta, 0, plugins.Count - 1);
        if (target == index) return index;
        (plugins[index], plugins[target]) = (plugins[target], plugins[index]);
        return target;
    }
    public PluginCommand SaveCommand(Guid instanceId, Guid presetId, long instanceRevision)
    {
        if (Loaded is null) throw new IOException("HCSL-PLUGIN-NOT-LOADED: Reload before saving.");
        return new(PluginAction.Save, instanceId, presetId, instanceRevision, Loaded.Hash, [.. plugins]);
    }
}
