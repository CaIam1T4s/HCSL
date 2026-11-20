namespace HCSL.Frontend;

/// <summary>One occurrence of the query: the row it belongs to and where inside that row's text it starts.</summary>
public readonly record struct LogMatch(int Row, int Start, int Length);

/// <summary>
/// Text search over the console rows. It only reads the strings the view already holds, so it can run on every
/// keystroke without touching the daemon, the ring buffer or the Textarea.
/// </summary>
public static class LogSearch
{
    /// <summary>Every match in row order, then in text order; matches may overlap, as in a plain editor find.</summary>
    public static IReadOnlyList<LogMatch> Find(IReadOnlyList<string> lines, string? query, bool matchCase = false)
    {
        var matches = new List<LogMatch>();
        if (query is not { Length: > 0 }) return matches;
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var row = 0; row < lines.Count; row++)
        {
            var line = lines[row] ?? "";
            for (var start = line.IndexOf(query, comparison); start >= 0; start = line.IndexOf(query, start + 1, comparison))
            {
                matches.Add(new LogMatch(row, start, query.Length));
                if (start + 1 >= line.Length) break;
            }
        }
        return matches;
    }

    /// <summary>
    /// The match to show from <paramref name="current"/>: <paramref name="step"/> forward or back, wrapping at both
    /// ends. A fresh query starts at the first match.
    /// </summary>
    public static int Step(int count, int current, int step)
    {
        if (count <= 0) return -1;
        if (current < 0 || current >= count) return step < 0 ? count - 1 : 0;
        var next = (current + step) % count;
        return next < 0 ? next + count : next;
    }

    /// <summary>One-based position and total for the counter, or an empty label when nothing matches.</summary>
    public static string Counter(int count, int current) =>
        count == 0 ? "无匹配" : (current < 0 ? 1 : current + 1) + " / " + count;
}
