using System.Globalization;
using HCSL.Core;

namespace HCSL.Frontend.Dev;

/// <summary>Human-readable projections shared by the dev CLI and its tests. No I/O.</summary>
public static class DevFormat
{
    public static string Bytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double size = value; var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return size.ToString(unit == 0 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    public static string Status(ServerSnapshot snapshot, InstanceEntry? instance, PresetDefinition? preset)
    {
        var run = snapshot.RunId?.ToString("D") ?? "—";
        var state = snapshot.State + (snapshot.Active ? " (active)" : "");
        var map = snapshot.Map ?? "—";
        var addresses = snapshot.ConnectionAddresses is { Length: > 0 } list
            ? string.Join(", ", list.Select(a => a + ":" + (instance?.Definition?.GamePort.ToString(CultureInfo.InvariantCulture) ?? "?")))
            : "—";
        var lines = new List<string>
        {
            "instance   " + (instance?.Definition?.Name is { } name ? name + " (" + instance.Id.ToString("D") + ")" : instance?.Id.ToString("D") ?? "—"),
            "preset     " + (preset is { } p ? p.Name + " (" + p.Id.ToString("D") + ")" : "—"),
            "state      " + state + "  " + snapshot.Code,
            "run        " + run,
            "map        " + map,
            "addresses  " + addresses
        };
        if (snapshot.Preset is { } deployed)
            lines.Add("deployed   " + deployed.Name + " rev " + deployed.Revision.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(snapshot.Detail)) lines.Add("detail     " + snapshot.Detail);
        return string.Join(Environment.NewLine, lines);
    }

    public static string Plan(DeploymentPlan plan, InstanceEntry? instance, PresetDefinition? preset)
    {
        var lines = new List<string>
        {
            "instance   " + (instance?.Definition?.Name is { } name ? name + " (" + plan.InstanceId.ToString("D") + ")" : plan.InstanceId.ToString("D")),
            "preset     " + (preset is { } p ? p.Name + " (" + plan.PresetId.ToString("D") + ")" : plan.PresetId.ToString("D")) + " rev " + plan.PresetRevision.ToString(CultureInfo.InvariantCulture),
            "build      " + plan.BuildId,
            "runtime    " + plan.RuntimePath,
            "copy       " + plan.CopyMode + " · " + plan.FileCount.ToString("N0", CultureInfo.InvariantCulture) + " files · " + Bytes(plan.LogicalBytes),
            "free need  library " + Bytes(plan.MinimumLibraryFreeBytes) + ", data " + Bytes(plan.MinimumDataFreeBytes)
        };
        if (plan.RotationGroup is not null) lines.Add("rotation   map group " + plan.RotationGroup);
        else if (plan.RotationCollection is not null) lines.Add("rotation   workshop collection " + plan.RotationCollection);
        lines.Add("plugins    " + (plan.PluginSetHash is null ? "none enabled" : "set hash " + plan.PluginSetHash[..12] + "…"));
        if (plan.Notes.Length > 0) lines.AddRange(plan.Notes.Select(note => "note       " + note));
        return string.Join(Environment.NewLine, lines);
    }

    public static string Plugins(PluginResult result)
    {
        var total = result.AvailableTotal > result.Available.Length ? result.AvailableTotal : result.Available.Length;
        var lines = new List<string> { "available  " + result.Available.Length + (total > result.Available.Length ? " of " + total : "") + " package version(s)" };
        lines.AddRange(result.Available.Select(a =>
            "  " + a.Id + " " + a.Version + "  " + a.Name + "  " + a.Files + " files " + Bytes(a.Bytes)));
        lines.Add("selected   " + result.Selected.Length);
        lines.AddRange(result.Selected.Select(s =>
            "  " + (s.Enabled ? "[on ] " : "[off] ") + s.Id + " " + s.Version + (s.Available ? "" : "  missing: " + (s.Error ?? "HCSL-PLUGIN-PACKAGE"))));
        if (result.Rejected is { Length: > 0 } rejected)
        {
            lines.Add("rejected   " + rejected.Length);
            lines.AddRange(rejected.Select(r => "  " + r.Id + " " + (r.Version.Length == 0 ? "(no version)" : r.Version) + "  " + r.Code));
        }
        lines.Add("revision   " + result.Revision.ToString(CultureInfo.InvariantCulture));
        return string.Join(Environment.NewLine, lines);
    }

    public static string ExplainPlugins(PluginResult result) =>
        "plugins    " + result.Selected.Count(s => s.Enabled) + " enabled, " + result.Available.Length + " available"
        + (result.Rejected is { Length: > 0 } r ? ", " + r.Length + " rejected (see hcsl plugins)" : "");
}
