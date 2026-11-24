using System.Text;
using System.Text.RegularExpressions;
namespace HCSL.Core;

public enum PresetCfgAction { List, Read, Save, Rename, Delete }
public enum PresetCfgKind { Directory, File }
public sealed record PresetCfgCommand(PresetCfgAction Action, Guid InstanceId, Guid PresetId,
    long InstanceRevision = 0, string? Name = null, string? Content = null, string? ExpectedHash = null, string? NewName = null,
    string? Path = null)
{
    public override string ToString() => $"PresetCfgCommand {{ {Action}, {InstanceId}, {PresetId}, content omitted }}";
}
public sealed record CfgBatchFile(string Path, string Hash);
public sealed record CfgBatchCommand(Guid RunId, Guid InstanceId, Guid PresetId, CfgBatchFile[] Files);

public sealed record PresetCfgFile(string Name, long Bytes, PresetCfgKind Kind = PresetCfgKind.File, string? RelativePath = null)
{
    public override string ToString() => (Kind == PresetCfgKind.Directory ? "[dir] " : "") + (RelativePath ?? Name) + (Kind == PresetCfgKind.Directory ? "" : " · " + Bytes + " B");
}
public sealed record PresetCfgResult(PresetCfgFile[] Files, string? Name = null, string? Content = null, string? Hash = null,
    string? Path = null, string? Parent = null, bool Truncated = false, int Total = 0)
{
    public override string ToString() => "PresetCfgResult { content omitted }";
}
public static class PresetCfgRules
{
    public const int MaxBytes = 8192;
    public const int MaxFiles = 128;
    /// <summary>Read-only tree browsing returns at most this many direct children per directory.</summary>
    public const int MaxListedEntries = 512;
    public const int MaxPathLength = 1024;
    public const int MaxSegmentLength = 64;
    private static readonly Regex SegmentPattern = new(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}\z", RegexOptions.CultureInvariant);
    private static readonly Regex DeviceName = new(@"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Validates a directory or file path relative to the preset cfg root (empty string is the root).</summary>
    public static string Path(string? path, bool requireCfgFile)
    {
        path ??= "";
        if (path.Length > MaxPathLength) throw new InstanceException("HCSL-CFG-PATH", "CFG path exceeds the length limit.");
        if (path.Contains('\\', StringComparison.Ordinal) || path.Any(char.IsControl) || path.StartsWith('/'))
            throw new InstanceException("HCSL-CFG-PATH", "Use a relative CFG path with '/' separators.");
        if (path.Length == 0) return "";
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            if (part.Length is 0 or > MaxSegmentLength || part is "." or ".." || !SegmentPattern.IsMatch(part) || DeviceName.IsMatch(part))
                throw new InstanceException("HCSL-CFG-PATH", "Invalid CFG path segment: " + part);
        }
        if (requireCfgFile && !parts[^1].EndsWith(".cfg", StringComparison.OrdinalIgnoreCase))
            throw new InstanceException("HCSL-CFG-PATH", "Only .cfg files are handled by the CFG browser.");
        return string.Join('/', parts);
    }
    public static string Name(string? name)
    {
        if (name is null || !Regex.IsMatch(name, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,74}\.cfg\z", RegexOptions.IgnoreCase) ||
            name.StartsWith("hcsl-", StringComparison.OrdinalIgnoreCase) || name.Equals("server-password.cfg", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(name.Split('.')[0], @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase))
            throw new InstanceException("HCSL-CFG-NAME", "Use a flat ASCII .cfg filename; managed password files are reserved.");
        return name;
    }
    public static void Content(string? text)
    {
        if (text is null || text.Length > MaxBytes || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InstanceException("HCSL-CFG-CONTENT", "CFG must be UTF-8 text, at most 8 KiB, without unsupported control characters.");
        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(text) > MaxBytes) throw new EncoderFallbackException();
        }
        catch (EncoderFallbackException) { throw new InstanceException("HCSL-CFG-CONTENT", "CFG exceeds 8 KiB or contains invalid Unicode."); }
    }
}
