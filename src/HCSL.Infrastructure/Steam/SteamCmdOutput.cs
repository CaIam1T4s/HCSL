using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using HCSL.Application.Abstractions;
using HCSL.Infrastructure.Transfer;

namespace HCSL.Infrastructure.Steam;

/// <summary>Parses bounded chunks, including prompts without newline. Raw output never leaves this object.</summary>
internal sealed class SteamCmdOutput(Guid operation, Action<SteamCmdStage, string> report,
    ChannelWriter<SteamCmdChallenge> challenges, Action<SteamCmdStage, string, double?, TransferProgress?>? progress = null)
{
    private readonly object sync = new();
    private readonly OutputStream stdout = new(), stderr = new();
    private readonly HashSet<SteamCmdStage> stages = [];
    private readonly TransferMeter meter = new();
    private (SteamCmdStage Stage, double? Percent, TransferProgress? Transfer)? lastProgress;
    internal bool AppInstalled { get; private set; }
    internal bool Authenticated { get; private set; }
    internal bool Failed { get; private set; }
    internal bool MissingDependency { get; private set; }
    internal bool ChallengeOverflow { get; private set; }

    internal void Accept(ReadOnlySpan<char> chunk, bool error)
    {
        lock (sync)
        {
            var stream = error ? stderr : stdout;
            var buffer = stream.Buffer;
            foreach (char ch in chunk)
            {
                if (!stream.AcceptCharacter(ch)) continue;
                if (ch is '\n' or '\r') { Parse(buffer.ToString()); buffer.Clear(); }
                else
                {
                    buffer.Append(ch);
                    if (buffer.Length > 4096) buffer.Remove(0, buffer.Length - 4096);
                }
            }
            var pending = buffer.ToString();
            // Prompts are often not terminated by newline. Consume once so split chunks don't duplicate them.
            if (ParsePrompt(pending)) buffer.Clear();
            else Observe(pending);
        }
    }

    internal void Finish(bool error)
    {
        lock (sync) Parse((error ? stderr : stdout).Buffer.ToString());
    }

    private void Parse(string text)
    {
        if (!ParsePrompt(text)) Observe(text, complete: true);
    }
    private void Once(SteamCmdStage stage, string message)
    {
        if (stages.Add(stage)) report(stage, message);
    }
    private bool ParsePrompt(string text)
    {
        SteamCmdChallengeKind? kind = null;
        if (Regex.IsMatch(text, @"(?:password)\s*:\s*$", RegexOptions.IgnoreCase)) kind = SteamCmdChallengeKind.Password;
        else if (Regex.IsMatch(text, @"(?:steam guard code|two.factor code|authenticator code|code from[^\r\n]*authenticator[^\r\n]*|code[^\r\n]*email[^\r\n]*)\s*:\s*$", RegexOptions.IgnoreCase))
            kind = SteamCmdChallengeKind.GuardCode;
        if (kind is null) return false;
        report(kind == SteamCmdChallengeKind.Password ? SteamCmdStage.WaitingForPassword : SteamCmdStage.WaitingForGuardCode,
            kind == SteamCmdChallengeKind.Password ? "SteamCMD requests a password." : "SteamCMD requests a Steam Guard code.");
        if (!challenges.TryWrite(new(operation, Guid.NewGuid(), kind.Value))) ChallengeOverflow = true;
        return true;
    }
    private void Observe(string text, bool complete = false)
    {
        var lower = text.ToLowerInvariant();
        if (complete && Regex.IsMatch(lower, @"^\s*success!\s+app\s+'730'\s+(?:fully installed|already up to date)\.\s*$")) AppInstalled = true;
        var update = Regex.Match(lower, @"^\s*update state [\[(]0x[0-9a-f]+[\])]\s+([^,\r\n]+)");
        if (update.Success)
        {
            var state = update.Groups[1].Value;
            // Real CS2 lines read "verifying install"/"verifying update", never "validating",
            // so the verification check must come first and cover both spellings.
            var verifying = state.Contains("verif") || state.Contains("validat");
            var idle = state.Contains("unknown") || state.Contains("reconfigur");
            var stage = verifying ? SteamCmdStage.ValidatingContent
                : state.Contains("download") ? SteamCmdStage.DownloadingContent : SteamCmdStage.InstallingContent;
            var number = Regex.Match(lower, @"progress:\s*([0-9]+(?:\.[0-9]+)?)\s*(\(|$)");
            double? percent = number.Success && double.TryParse(number.Groups[1].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 100 ? Math.Floor(parsed * 10) / 10 : null;
            // SteamCMD prints "progress: 12.50 (done / total)"; the pair is the only byte-level truth
            // available on both Linux and Windows, so derive the rate from it instead of guessing.
            var bytes = Regex.Match(lower, @"\bprogress:[^(]*\(\s*([0-9]+)\s*/\s*([0-9]+)\s*\)");
            long? done = null, totalBytes = null;
            if (bytes.Success)
            {
                done = long.TryParse(bytes.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var d) ? d : null;
                totalBytes = long.TryParse(bytes.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : null;
            }
            var phase = idle ? TransferPhase.Idle
                : stage switch
                {
                    SteamCmdStage.DownloadingContent => TransferPhase.Downloading,
                    SteamCmdStage.ValidatingContent => TransferPhase.Verifying,
                    _ => TransferPhase.Installing
                };
            if (done is null && lastProgress is { Transfer: { } previous } && previous.Phase != phase) meter.Reset();
            var rate = done is { } current ? meter.Sample(current, totalBytes) : null;
            var transfer = done is null && totalBytes is null
                ? new TransferProgress(phase, Percent: percent)
                : TransferProgress.Measure(phase, done, totalBytes, rate);
            // Ignore partial numeric tokens until the line/progress delimiter arrives.
            if (complete || number.Success && number.Groups[2].Value == "(")
                if (lastProgress != (stage, percent, transfer))
                {
                    lastProgress = (stage, percent, transfer);
                    progress?.Invoke(stage, stage switch
                    {
                        SteamCmdStage.DownloadingContent => "Downloading CS2 server content.",
                        SteamCmdStage.ValidatingContent => "Validating CS2 server content.",
                        _ => "Preparing or applying CS2 server content."
                    }, percent, transfer);
                }
        }
        if (lower.Contains("checking for available updates") || lower.Contains("downloading update") || lower.Contains("installing update"))
            Once(SteamCmdStage.SelfUpdating, "SteamCMD is checking or applying its own updates.");
        if (lower.Contains("steam console client")) Once(SteamCmdStage.Ready, "SteamCMD console is ready.");
        if (lower.Contains("connecting anonymously") || lower.Contains("logging in user"))
            Once(SteamCmdStage.Authenticating, "SteamCMD is authenticating.");
        if (lower.Contains("waiting for user info") && Regex.IsMatch(lower, @"\bok\b"))
        {
            Authenticated = true;
            Once(SteamCmdStage.Authenticated, "SteamCMD reported that user information is ready.");
        }
        if ((lower.Contains("approval") || lower.Contains("confirm")) && (lower.Contains("phone") || lower.Contains("steam mobile")))
            Once(SteamCmdStage.WaitingForMobileApproval, "Confirm the login in your Steam mobile application.");
        // Command/config echoes (notably @ShutdownOnFailedCommand) and nonfatal
        // "failed to ..." warnings are not terminal login failures. A bare FAILED
        // is only final at newline/EOF, never at an arbitrary read boundary.
        if (Regex.IsMatch(lower, @"(?:^|\s)error!|\blog(?:in|on)\s+(?:failure|failed)\b|\bfailed\s*(?:\([^\r\n)]*\)|:)") ||
            (complete && Regex.IsMatch(lower, @"\bfailed\s*$"))) Failed = true;
        if (lower.Contains("error while loading shared libraries") || lower.Contains("cannot execute") || lower.Contains("not found"))
            MissingDependency = true;
    }

    // Keep escape decoding state per stream and across reads. Never buffer terminal
    // control payloads: even unterminated OSC/DCS strings use constant memory.
    private sealed class OutputStream
    {
        internal StringBuilder Buffer { get; } = new();
        private EscapeState state;
        private bool bellTerminates;
        internal bool AcceptCharacter(char value)
        {
            switch (state)
            {
                case EscapeState.Text:
                    if (value == '\x1b') { state = EscapeState.Escape; return false; }
                    return true;
                case EscapeState.Escape:
                    state = value switch
                    {
                        '[' => EscapeState.Csi,
                        ']' or 'P' or '^' or '_' => EscapeState.ControlString,
                        '(' or ')' or '*' or '+' or '#' => EscapeState.Charset,
                        '\x1b' => EscapeState.Escape,
                        _ => EscapeState.Text
                    };
                    bellTerminates = value == ']';
                    return value is '\r' or '\n';
                case EscapeState.Csi:
                    if (value is '\r' or '\n') { state = EscapeState.Text; return true; }
                    if (value == '\x1b') state = EscapeState.Escape;
                    else if (value is >= '@' and <= '~') state = EscapeState.Text;
                    return false;
                case EscapeState.ControlString:
                    if (bellTerminates && value == '\a') state = EscapeState.Text;
                    else if (value == '\x1b') state = EscapeState.ControlStringEscape;
                    return false;
                case EscapeState.ControlStringEscape:
                    if (value == '\\' || (bellTerminates && value == '\a')) state = EscapeState.Text;
                    else if (value != '\x1b') state = EscapeState.ControlString;
                    return false;
                default: // Charset designation consumes one character.
                    state = EscapeState.Text;
                    return value is '\r' or '\n';
            }
        }
        private enum EscapeState { Text, Escape, Csi, ControlString, ControlStringEscape, Charset }
    }

}
