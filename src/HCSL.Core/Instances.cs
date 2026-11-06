namespace HCSL.Core;

public sealed record InstanceDefinition(int FormatVersion, Guid Id, long Revision, string Name, int GamePort, Guid SelectedPresetId);
public sealed record PresetDefinition(int FormatVersion, Guid Id, long Revision, string Name, string Map = "de_dust2", int GameType = 0, int GameMode = 1, bool VacPreferred = true);
public sealed record InstanceEntry(Guid Id, InstanceDefinition? Definition, PresetDefinition[] Presets, string? Error = null);
public sealed record InstanceCatalog(InstanceEntry[] Instances);
public enum InstanceAction { List, Create, Update, Delete, CreatePreset, UpdatePreset, CopyPreset, DeletePreset, SelectPreset }
public sealed record InstanceCommand(InstanceAction Action, Guid InstanceId = default, Guid PresetId = default, long Revision = 0,
    string? Name = null, int GamePort = 27015, PresetDefinition? Preset = null);
public sealed class InstanceException(string code, string message) : Exception(message) { public string Code { get; } = code; }
public static class InstanceRules
{
    public static void Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new InstanceException("HCSL-INSTANCE-NAME", "Name must contain 1–80 visible characters.");
    }
    public static void Preset(PresetDefinition p)
    {
        Name(p.Name);
        if (string.IsNullOrWhiteSpace(p.Map) || p.Map.Length > 100 || p.Map.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || p.GameType is < 0 or > 10 || p.GameMode is < 0 or > 100)
            throw new InstanceException("HCSL-PRESET-INVALID", "Invalid map or game mode.");
    }
}
