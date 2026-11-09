using System.Text;
namespace HCSL.Core;

public enum ConsoleDelivery { Rejected, Written, Unknown }
public sealed record ServerConsoleCommand(Guid RunId, Guid CommandId, string Text, bool ConfirmMutation = false)
{
    public override string ToString() => $"Console {CommandId} for {RunId}";
}
public sealed record ServerConsoleReceipt(Guid RunId, Guid CommandId, ConsoleDelivery Delivery, string Code, string? DisplayText = null);
public sealed record ParsedConsoleCommand(string Text, bool IsMutation);
public static class ServerConsoleRules
{
    // Transport validation only. CS2, not HCSL, interprets syntax, aliases and command chains.
    public static ParsedConsoleCommand Parse(string text) => new(Validate(text), false);
    public static string Validate(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096 || Encoding.UTF8.GetByteCount(text) > 16384
            || text.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029') || InvalidUnicode(text))
            throw new ServerException("HCSL-CONSOLE-INPUT", "Use one nonempty line, at most 4096 characters / 16384 UTF-8 bytes, without control characters.");
        return text;
    }
    private static bool InvalidUnicode(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !char.IsLowSurrogate(text[++i])) return true;
        }
        return false;
    }
    public static string ValidateConfirmed(ServerConsoleCommand command) => Validate(command.Text);
    public static bool IsSensitive(string text) => new[] { "password", "token", "steamaccount", "gslt", "secret" }
        .Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));
    public static string Display(string text) => IsSensitive(text) ? "[redacted sensitive command]" : text;
}
