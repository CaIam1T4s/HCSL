using System.Globalization;
namespace HCSL.Core;

public enum ServerMapKind { Standard, Workshop, WorkshopCollection }
public sealed record ServerMapTarget(ServerMapKind Kind, string Value);
public static class ServerMapRules
{
    /// <summary>
    /// CS2 cannot start on a Workshop collection alone: with no initial map it stays in Idle,
    /// never initialises the game server or Steam Workshop, and the launcher sees a startup timeout.
    /// A built-in map boots the game server; the collection command then downloads and takes over.
    /// </summary>
    public const string CollectionSeedMap = "de_dust2";

    public static void Validate(ServerMapTarget target)
    {
        var value = target.Value;
        var valid = target.Kind switch
        {
            ServerMapKind.Standard => !string.IsNullOrEmpty(value) && value.Length <= 100 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !value.All(char.IsAsciiDigit),
            ServerMapKind.Workshop => WorkshopId(value),
            ServerMapKind.WorkshopCollection => WorkshopId(value),
            _ => false
        };
        if (!valid) throw new InstanceException("HCSL-MAP-INVALID", "普通地图需填写名称（字母、数字、下划线）；工坊单图/合集需填写正整数 ID，不接受网址或命令。ID 是否可用由 Steam/CS2 验证。");
    }
    private static bool WorkshopId(string value) => !string.IsNullOrEmpty(value) && value.Length <= 20 && value[0] != '0' && value.All(char.IsAsciiDigit) && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
    public static string Label(ServerMapTarget target) => target.Kind switch
    {
        ServerMapKind.Workshop => "Workshop #" + target.Value,
        ServerMapKind.WorkshopCollection => "Collection #" + target.Value,
        _ => target.Value
    };
    public static string ChangeCommand(ServerMapTarget target)
    {
        Validate(target);
        return target.Kind switch
        {
            ServerMapKind.Workshop => "host_workshop_map " + target.Value,
            ServerMapKind.WorkshopCollection => "host_workshop_collection " + target.Value,
            _ => "changelevel " + target.Value
        };
    }
    public static string[] StartupArguments(ServerMapTarget target, string? mapGroup = null)
    {
        Validate(target);
        if (target.Kind == ServerMapKind.WorkshopCollection)
        {
            if (mapGroup is not null) throw new InstanceException("HCSL-MAPGROUP-NAME", "A workshop collection cannot be combined with a native map group.");
            return ["+map", CollectionSeedMap, "+host_workshop_collection", target.Value];
        }
        string[] direct = [target.Kind == ServerMapKind.Workshop ? "+host_workshop_map" : "+map", target.Value];
        if (mapGroup is null) return direct;
        if (target.Kind != ServerMapKind.Standard || !IsMapGroupName(mapGroup))
            throw new InstanceException("HCSL-MAPGROUP-NAME", "A native map group can only start a standard map and must use a launcher-generated name.");
        return ["+mapgroup", mapGroup, .. direct];
    }
    internal static bool IsMapGroupName(string name) => name.Length is > 6 and <= 38 && name.StartsWith("hcsl_", StringComparison.Ordinal) && name[5..].All(char.IsAsciiHexDigitLower);
}
/// <summary>
/// Builds the one explicit recovery command for a CS2 end-of-match transition that
/// requested an empty map. Only a bare standard map name or a workshop ID is accepted;
/// anything else is left to the operator instead of being interpreted as console input.
/// </summary>
public static class ServerMapRecovery
{
    public static string? Command(string? target, ServerMapKind presetKind)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        target = target.Trim();
        var kind = target.All(char.IsAsciiDigit) ? ServerMapKind.Workshop : ServerMapKind.Standard;
        if (presetKind == ServerMapKind.WorkshopCollection)
        {
            if (kind != ServerMapKind.Standard) return null;
            try { ServerMapRules.Validate(new(ServerMapKind.Standard, target)); return "ds_workshop_changelevel " + target; }
            catch (InstanceException) { return null; }
        }
        if (kind == ServerMapKind.Workshop && presetKind != ServerMapKind.Workshop) return null;
        try { return ServerMapRules.ChangeCommand(new(kind, target)); }
        catch (InstanceException) { return null; }
    }
}
public enum ServerMapPhase { None, DownloadObserved, DownloadFailed, Loading, Ready, Unconfirmed }
public sealed record ServerMapProgress(ServerMapPhase Phase);
