using System.Text;
using System.Text.RegularExpressions;
namespace HCSL.Core;

public enum PresetCfgAction { List, Read, Save, Rename, Delete }
public sealed record PresetCfgCommand(PresetCfgAction Action, Guid InstanceId, Guid PresetId,
    long InstanceRevision = 0, string? Name = null, string? Content = null, string? ExpectedHash = null, string? NewName = null)
{
    public override string ToString() => $"PresetCfgCommand {{ {Action}, {InstanceId}, {PresetId}, content omitted }}";
}
public sealed record PresetCfgFile(string Name, long Bytes)
{
    public override string ToString() => $"{Name} · {Bytes} B";
}
public sealed record PresetCfgResult(PresetCfgFile[] Files, string? Name = null, string? Content = null, string? Hash = null)
{
    public override string ToString() => "PresetCfgResult { content omitted }";
}
public static class PresetCfgRules
{
    public const int MaxBytes = 8192;
    public const int MaxFiles = 128;
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
