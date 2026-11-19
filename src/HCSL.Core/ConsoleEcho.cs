namespace HCSL.Core;

/// <summary>
/// Marker for a console log line the frontend wrote to the server itself. The server never echoes a command
/// it reads from standard input, so the daemon records the write; the console highlights those lines.
/// </summary>
public static class ConsoleEcho
{
    /// <summary>Leading glyph of a written command; one character keeps the log's column rhythm.</summary>
    public const string Prefix = "› ";

    public static bool IsEcho(string? text) =>
        text is not null && text.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The recorded line for a command, without line breaks and bounded like server output.</summary>
    public static string Line(string command)
    {
        var single = command.ReplaceLineEndings(" ").Trim();
        return Prefix + (single.Length > 512 ? single[..512] : single);
    }
}
