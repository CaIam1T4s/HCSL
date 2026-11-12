using System.Text;

namespace HCSL.Infrastructure.Storage;

/// <summary>
/// Minimal JSON/JSONC span editor: parse once, then change only the byte range that must change, so comments,
/// indentation, member order, BOM and CRLF stay byte-identical. Callers re-parse and compare before writing.
/// </summary>
internal static class JsonSpanEditor
{
    internal sealed class Node
    {
        internal int Start;
        internal int End;
        internal List<Member>? Members;
    }

    internal sealed record Member(string Key, int KeyStart, int ValueStart, int ValueEnd, Node Value);

    internal static Node ParseObject(string text)
    {
        var index = Skip(text, 0);
        var node = ParseValue(text, ref index);
        index = Skip(text, index);
        if (index != text.Length) throw new FormatException("Trailing characters after the JSON value.");
        return node.Members is null ? throw new FormatException("The document root is not an object.") : node;
    }

    internal static Member? Find(Node root, params string[] path)
    {
        var node = root;
        Member? member = null;
        foreach (var segment in path)
        {
            member = node.Members?.FirstOrDefault(m => m.Key == segment);
            if (member is null) return null;
            node = member.Value;
        }
        return member;
    }

    /// <summary>Create or replace one member without touching any other byte.</summary>
    internal static string SetMember(string text, Node root, IReadOnlyList<string> path, string key, string valueJson)
    {
        var parent = ResolveParent(root, path);
        var existing = parent.Members?.FirstOrDefault(m => m.Key == key);
        if (existing is not null) return text[..existing.ValueStart] + valueJson + text[existing.ValueEnd..];
        return Insert(text, parent, key, valueJson);
    }

    internal static string RemoveMember(string text, Node root, IReadOnlyList<string> path, string key)
    {
        var parent = ResolveParent(root, path);
        var members = parent.Members ?? [];
        var index = members.FindIndex(m => m.Key == key);
        if (index < 0) return text;
        var member = members[index];
        int start, end;
        if (index > 0)
        {
            start = member.KeyStart;
            var previousEnd = members[index - 1].ValueEnd;
            var comma = LastNonSpace(text, previousEnd, member.KeyStart);
            if (comma >= 0 && text[comma] == ',') start = comma;
            while (start > 0 && text[start - 1] is ' ' or '\t') start--;
            end = member.ValueEnd;
        }
        else if (members.Count > 1)
        {
            start = StripPrecedingNewline(text, LineStart(text, member.KeyStart));
            var comma = FirstNonSpace(text, member.ValueEnd, members[1].KeyStart);
            end = comma >= 0 && text[comma] == ',' ? comma + 1 : member.ValueEnd;
        }
        else
        {
            // Only member: drop the line, keep surrounding comments and the braces.
            start = StripPrecedingNewline(text, LineStart(text, member.KeyStart));
            var comma = FirstNonSpace(text, member.ValueEnd, parent.End - 1);
            end = comma >= 0 && text[comma] == ',' ? comma + 1 : member.ValueEnd;
        }
        return text[..start] + text[end..];
    }

    private static Node ResolveParent(Node root, IReadOnlyList<string> path)
    {
        var node = root;
        foreach (var segment in path)
        {
            var member = node.Members?.FirstOrDefault(m => m.Key == segment)
                ?? throw new FormatException("Path segment not found: " + segment);
            node = member.Value;
        }
        return node;
    }

    private static Node ParseValue(string text, ref int index)
    {
        index = Skip(text, index);
        if (index >= text.Length) throw new FormatException("Unexpected end of JSON.");
        var start = index;
        switch (text[index])
        {
            case '{':
            {
                index++;
                var node = new Node { Start = start, Members = [] };
                index = Skip(text, index);
                if (index < text.Length && text[index] == '}') { index++; node.End = index; return node; }
                while (true)
                {
                    index = Skip(text, index);
                    var keyStart = index;
                    var key = ParseString(text, ref index);
                    index = Skip(text, index);
                    if (index >= text.Length || text[index] != ':') throw new FormatException("Expected ':' in an object.");
                    index = Skip(text, index + 1);
                    var valueStart = index;
                    var value = ParseValue(text, ref index);
                    node.Members!.Add(new(key, keyStart, valueStart, value.End, value));
                    index = Skip(text, index);
                    if (index < text.Length && text[index] == ',') { index++; continue; }
                    if (index < text.Length && text[index] == '}') { index++; node.End = index; return node; }
                    throw new FormatException("Expected ',' or '}' in an object.");
                }
            }
            case '[':
            {
                index++;
                var node = new Node { Start = start };
                index = Skip(text, index);
                if (index < text.Length && text[index] == ']') { index++; node.End = index; return node; }
                while (true)
                {
                    ParseValue(text, ref index);
                    index = Skip(text, index);
                    if (index < text.Length && text[index] == ',') { index++; continue; }
                    if (index < text.Length && text[index] == ']') { index++; node.End = index; return node; }
                    throw new FormatException("Expected ',' or ']' in an array.");
                }
            }
            case '"':
            {
                ParseString(text, ref index);
                return new Node { Start = start, End = index };
            }
            default:
            {
                while (index < text.Length && (char.IsAsciiDigit(text[index]) || text[index] is '-' or '+' or '.' or 'e' or 'E')) index++;
                if (index > start) return new Node { Start = start, End = index };
                foreach (var keyword in new[] { "true", "false", "null" })
                    if (start + keyword.Length <= text.Length && string.CompareOrdinal(text, start, keyword, 0, keyword.Length) == 0)
                        return new Node { Start = start, End = start + keyword.Length };
                throw new FormatException("Unexpected character in a value.");
            }
        }
    }

    private static string ParseString(string text, ref int index)
    {
        if (index >= text.Length || text[index] != '"') throw new FormatException("Expected a string.");
        var builder = new StringBuilder();
        index++;
        while (index < text.Length)
        {
            var c = text[index++];
            if (c == '"') return builder.ToString();
            if (c != '\\') { builder.Append(c); continue; }
            if (index >= text.Length) throw new FormatException("Unterminated escape.");
            var escape = text[index++];
            builder.Append(escape switch
            {
                'n' => '\n', 't' => '\t', 'r' => '\r', 'b' => '\b', 'f' => '\f', '/' => '/', '\\' => '\\', '"' => '"',
                'u' => ParseUnicode(text, ref index),
                _ => throw new FormatException("Unsupported escape: \\" + escape)
            });
        }
        throw new FormatException("Unterminated string.");
    }

    private static char ParseUnicode(string text, ref int index)
    {
        if (index + 4 > text.Length) throw new FormatException("Truncated \\u escape.");
        var value = Convert.ToInt32(text.Substring(index, 4), 16);
        index += 4;
        return (char)value;
    }

    /// <summary>Skip whitespace and comments (// and /* */) as JSONC allows.</summary>
    private static int Skip(string text, int index)
    {
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index])) { index++; continue; }
            if (text[index] == '\uFEFF') { index++; continue; } // UTF-8 BOM must survive an edit, so tolerate it here.
            if (text[index] == '/' && index + 1 < text.Length)
            {
                if (text[index + 1] == '/')
                {
                    index += 2;
                    while (index < text.Length && text[index] != '\n') index++;
                    continue;
                }
                if (text[index + 1] == '*')
                {
                    index += 2;
                    while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/')) index++;
                    index = Math.Min(index + 2, text.Length);
                    continue;
                }
            }
            break;
        }
        return index;
    }

    private static int LastNonSpace(string text, int from, int to)
    {
        for (var i = to - 1; i >= from; i--) if (!char.IsWhiteSpace(text[i])) return i;
        return -1;
    }

    private static int FirstNonSpace(string text, int from, int to)
    {
        for (var i = from; i < to; i++) if (!char.IsWhiteSpace(text[i])) return i;
        return -1;
    }

    private static string Insert(string text, Node parent, string key, string valueJson)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var members = parent.Members ?? [];
        var entry = Quote(key) + ": " + valueJson;
        if (members.Count == 0)
        {
            var close = parent.End - 1;
            var parentIndent = LineIndent(text, parent.Start);
            var childIndent = parentIndent + "  ";
            var inner = text[(parent.Start + 1)..close];
            if (inner.Trim().Length == 0)
                return text[..(parent.Start + 1)] + newline + childIndent + entry + newline + parentIndent + text[close..];
            // Comments inside the empty object are preserved: append after them.
            return text[..close].TrimEnd(' ', '\t')
                + newline + childIndent + entry
                + newline + parentIndent + text[close..];
        }
        var first = members[0];
        var indent = text[LineStart(text, first.KeyStart)..first.KeyStart];
        var anchor = members[^1];
        var next = FirstNonSpace(text, anchor.ValueEnd, parent.End - 1);
        var hasTrailingComma = next >= 0 && text[next] == ',';
        var insertAt = hasTrailingComma ? next + 1 : anchor.ValueEnd;
        return text[..insertAt] + (hasTrailingComma ? "" : ",") + newline + indent + entry + text[insertAt..];
    }

    private static int LineStart(string text, int index)
    {
        var start = index;
        while (start > 0 && text[start - 1] is ' ' or '\t') start--;
        return start;
    }

    /// <summary>Include the line terminator so removing the first member does not leave a blank line.</summary>
    private static int StripPrecedingNewline(string text, int start)
    {
        if (start >= 2 && text[start - 1] == '\n' && text[start - 2] == '\r') return start - 2;
        if (start >= 1 && text[start - 1] == '\n') return start - 1;
        return start;
    }

    private static string LineIndent(string text, int index) => text[LineStart(text, index)..index];

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
