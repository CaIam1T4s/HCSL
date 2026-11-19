namespace HCSL.Frontend;

public sealed record ConsoleLogRow(string Id, string Text, DateTimeOffset? ReceivedAt = null, bool Echo = false)
{
    public string DisplayText => ReceivedAt is { } time
        ? "[" + time.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "] " + Text
        : Text;
}
/// <summary>Stable row anchors survive bounded-ring eviction without guessing by text content.</summary>
public sealed class ConsoleLogViewport
{
    private string? anchor;
    public bool ReadingPositionLost { get; private set; }
    public void Capture(IReadOnlyList<ConsoleLogRow> rows, int row)
    {
        anchor = rows.Count == 0 ? null : rows[Math.Clamp(row, 0, rows.Count - 1)].Id;
    }
    public int Restore(IReadOnlyList<ConsoleLogRow> rows)
    {
        if (anchor is null) return 0;
        for (var i = 0; i < rows.Count; i++) if (rows[i].Id == anchor) return i;
        ReadingPositionLost = true;
        anchor = rows.FirstOrDefault()?.Id;
        return 0;
    }
    public void Reset() { anchor = null; ReadingPositionLost = false; }
}
