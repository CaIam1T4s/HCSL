namespace HCSL.Core;

/// <summary>How players reach the server. Loopback is the validated default; Lan binds one local
/// address. Public (VAC/GSLT, 0.0.0.0) is not implemented yet and must not be assumed.</summary>
public enum NetworkAccessMode { Loopback, Lan }

/// <summary>The address the game server binds. Stored on the instance because it is a property of this
/// deployment (like the port), not of the game rules held by a preset.</summary>
public sealed record ServerNetwork(NetworkAccessMode Mode = NetworkAccessMode.Loopback, string BindAddress = "127.0.0.1");

public sealed record InstanceDefinition(int FormatVersion, Guid Id, long Revision, string Name, int GamePort, Guid SelectedPresetId, ServerNetwork? Network = null);
public sealed record PresetDefinition(int FormatVersion, Guid Id, long Revision, string Name, string Map = "de_dust2", int GameType = 0, int GameMode = 1, bool VacPreferred = true, ServerMapKind MapKind = ServerMapKind.Standard);
public sealed record InstanceEntry(Guid Id, InstanceDefinition? Definition, PresetDefinition[] Presets, string? Error = null, bool HasPassword = false);
public sealed record InstanceCatalog(InstanceEntry[] Instances, string[]? ConnectionAddresses = null);
public enum InstanceAction { List, Create, Update, Delete, CreatePreset, UpdatePreset, CopyPreset, DeletePreset, SelectPreset, SetPassword }
public sealed record InstanceCommand(InstanceAction Action, Guid InstanceId = default, Guid PresetId = default, long Revision = 0,
    string? Name = null, int GamePort = 27015, PresetDefinition? Preset = null, ServerNetwork? Network = null, string? Password = null)
{
    public override string ToString() => $"InstanceCommand {Action} {InstanceId}";
}
public sealed class InstanceException(string code, string message) : Exception(message) { public string Code { get; } = code; }
public static class InstanceRules
{
    public static void Password(string? password)
    {
        if (password is null) throw new InstanceException("HCSL-INSTANCE-PASSWORD", "A password value is required; empty clears it.");
        try { PlayerServerRules.Password(password); }
        catch (ArgumentException) { throw new InstanceException("HCSL-INSTANCE-PASSWORD", "Password must be at most 128 characters, without control characters, quotes, backslashes, semicolons or plus signs."); }
    }

    public static void Name(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl))
            throw new InstanceException("HCSL-INSTANCE-NAME", "Name must contain 1–80 visible characters.");
    }
    public static void Preset(PresetDefinition p)
    {
        Name(p.Name);
        if (p.MapKind == ServerMapKind.WorkshopCollection)
            throw new InstanceException("HCSL-PRESET-INVALID", "A workshop collection belongs in the map pool, not in the startup map field.");
        try { ServerMapRules.Validate(new(p.MapKind, p.Map)); }
        catch (InstanceException e) { throw new InstanceException("HCSL-PRESET-INVALID", e.Message); }
        if (p.GameType is < 0 or > 10 || p.GameMode is < 0 or > 100)
            throw new InstanceException("HCSL-PRESET-INVALID", "Invalid map or game mode.");
    }
    /// <summary>Syntax and mode rules only: whether the address exists on this host is decided at
    /// start time, so an instance stays readable after a laptop moves to another network.</summary>
    public static void Network(ServerNetwork? network)
    {
        if (network is null) return;
        if (network.Mode is not (NetworkAccessMode.Loopback or NetworkAccessMode.Lan))
            throw new InstanceException("HCSL-INSTANCE-BIND", "Unsupported network access mode.");
        if (!System.Net.IPAddress.TryParse(network.BindAddress, out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InstanceException("HCSL-INSTANCE-BIND", "Bind address must be an IPv4 literal such as 192.168.1.20.");
        if (network.Mode == NetworkAccessMode.Loopback && !System.Net.IPAddress.IsLoopback(address))
            throw new InstanceException("HCSL-INSTANCE-BIND", "Loopback mode requires a 127.0.0.0/8 address.");
        if (network.Mode == NetworkAccessMode.Lan && (System.Net.IPAddress.IsLoopback(address) || address.Equals(System.Net.IPAddress.Any)))
            throw new InstanceException("HCSL-INSTANCE-BIND", "Lan mode requires one specific address of a local interface; 0.0.0.0 belongs to the public mode.");
    }
    // Hand-edited metadata is not re-validated on load, so this must not throw: keep the text and let
    // Network()/the access provider report HCSL-INSTANCE-BIND instead of a FormatException out of a listing.
    public static ServerNetwork Resolve(ServerNetwork? network)
    {
        if (network is null) return new();
        return System.Net.IPAddress.TryParse(network.BindAddress, out var parsed)
            ? network with { BindAddress = parsed.ToString() }
            : network;
    }
}
