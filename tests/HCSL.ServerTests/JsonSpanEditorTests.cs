using HCSL.Infrastructure.Storage;
namespace HCSL.ServerTests;

/// <summary>
/// The span editor is the only thing standing between "HCSL edits a file the user hand-maintains" and data loss,
/// so every case here asserts byte-level preservation of comments and formatting.
/// </summary>
public static class JsonSpanEditorTests
{
    public static void Run()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Refuse(string text, string message)
        {
            try { JsonSpanEditor.ParseObject(text); throw new Exception("Expected refusal: " + message); }
            catch (FormatException) { }
        }

        // --- insert into an empty object that carries a comment (the real Source2Mod shape) ---
        var empty = "{\n  \"Admins\": {\n    // \"76561197960265728\": { \"Groups\": [ \"root\" ] }\n  }\n}\n";
        var root = JsonSpanEditor.ParseObject(empty);
        var inserted = JsonSpanEditor.SetMember(empty, root, ["Admins"], "76561198000000000", "{ \"Groups\": [ \"admin\" ] }");
        Check(inserted.Contains("// \"76561197960265728\"", StringComparison.Ordinal), "Comment inside the empty object was lost.");
        Check(inserted.Contains("\"76561198000000000\": { \"Groups\": [ \"admin\" ] }", StringComparison.Ordinal), "Entry not inserted.");
        Check(Count(inserted, "Admin") == Count(empty, "Admin"), "Unrelated text changed.");
        var reparsed = JsonSpanEditor.ParseObject(inserted);
        var entry = JsonSpanEditor.Find(reparsed, "Admins", "76561198000000000");
        Check(entry is not null && inserted[entry.ValueStart..entry.ValueEnd].Contains("admin", StringComparison.Ordinal), "Result is not valid JSON or lost the value.");

        // --- insert into an object that already has members, preserving order and indentation ---
        var filled = "{\n  \"A\": { \"x\": 1 },\n  \"B\": {\n    \"76561198000000001\": { \"Groups\": [ \"vip\" ] }\n  }\n}\n";
        root = JsonSpanEditor.ParseObject(filled);
        var added = JsonSpanEditor.SetMember(filled, root, ["B"], "76561198000000000", "{ \"Groups\": [ \"admin\" ] }");
        Check(added.StartsWith("{\n  \"A\": { \"x\": 1 },\n  \"B\": {\n", StringComparison.Ordinal), "Sibling object changed.");
        Check(added.Contains("\"76561198000000001\": { \"Groups\": [ \"vip\" ] },\n    \"76561198000000000\"", StringComparison.Ordinal),
            "No comma-separated append with the original indentation.\n" + added);

        // --- replace an existing value keeps everything else byte-identical ---
        var replaced = JsonSpanEditor.SetMember(filled, JsonSpanEditor.ParseObject(filled), ["B"], "76561198000000001", "{ \"Groups\": [ \"root\" ] }");
        Check(replaced.Replace("{ \"Groups\": [ \"root\" ] }", "{ \"Groups\": [ \"vip\" ] }", StringComparison.Ordinal) == filled, "Replace touched other bytes.");

        // --- remove: middle member, first member, last member, only member ---
        var three = "{\n  \"Admins\": {\n    \"a\": {},\n    \"b\": {},\n    \"c\": {}\n  }\n}\n";
        var noMiddle = JsonSpanEditor.RemoveMember(three, JsonSpanEditor.ParseObject(three), ["Admins"], "b");
        Check(noMiddle == "{\n  \"Admins\": {\n    \"a\": {},\n    \"c\": {}\n  }\n}\n", "Removing a middle member left a broken separator:\n" + noMiddle);
        var noFirst = JsonSpanEditor.RemoveMember(three, JsonSpanEditor.ParseObject(three), ["Admins"], "a");
        Check(noFirst == "{\n  \"Admins\": {\n    \"b\": {},\n    \"c\": {}\n  }\n}\n", "Removing the first member:\n" + noFirst);
        var noLast = JsonSpanEditor.RemoveMember(three, JsonSpanEditor.ParseObject(three), ["Admins"], "c");
        Check(noLast == "{\n  \"Admins\": {\n    \"a\": {},\n    \"b\": {}\n  }\n}\n", "Removing the last member:\n" + noLast);
        var only = "{\n  \"Admins\": {\n    \"a\": {}\n  }\n}\n";
        var none = JsonSpanEditor.RemoveMember(only, JsonSpanEditor.ParseObject(only), ["Admins"], "a");
        Check(none == "{\n  \"Admins\": {\n  }\n}\n", "Removing the only member:\n" + none);
        Check(JsonSpanEditor.RemoveMember(three, JsonSpanEditor.ParseObject(three), ["Admins"], "zzz") == three, "Missing key changed the file.");

        // --- comments around the edit survive verbatim ---
        var commented = "{\n  // keep me\n  \"Admins\": {\n    \"a\": {} // trailing note\n  },\n  // tail comment\n  \"X\": 1\n}\n";
        var withEntry = JsonSpanEditor.SetMember(commented, JsonSpanEditor.ParseObject(commented), ["Admins"], "b", "{}");
        Check(withEntry.Contains("// keep me", StringComparison.Ordinal) && withEntry.Contains("// trailing note", StringComparison.Ordinal)
            && withEntry.Contains("// tail comment", StringComparison.Ordinal), "A comment was dropped.");
        var removed = JsonSpanEditor.RemoveMember(commented, JsonSpanEditor.ParseObject(commented), ["Admins"], "a");
        Check(removed.Contains("// keep me", StringComparison.Ordinal) && removed.Contains("// tail comment", StringComparison.Ordinal), "Removal dropped a comment.");

        // --- CRLF and BOM must survive an edit ---
        var crlf = "\uFEFF{\r\n  \"Admins\": {\r\n    \"a\": {}\r\n  }\r\n}\r\n";
        var crlfEdited = JsonSpanEditor.SetMember(crlf, JsonSpanEditor.ParseObject(crlf), ["Admins"], "b", "{}");
        Check(crlfEdited.StartsWith('\uFEFF') && crlfEdited.Contains("\r\n", StringComparison.Ordinal) && !crlfEdited.Contains("\n\n", StringComparison.Ordinal),
            "BOM or CRLF lost.");
        Check(crlfEdited.Contains("\"a\": {},\r\n    \"b\": {}", StringComparison.Ordinal), "CRLF insert formatting:\n" + crlfEdited.Replace("\r", "\\r"));

        // --- names that need escaping ---
        var quotedText = "{\n  \"Admins\": {\n    \"a\": {}\n  }\n}\n";
        var quoted = JsonSpanEditor.SetMember(quotedText, JsonSpanEditor.ParseObject(quotedText), ["Admins"], "we\"ird\\name", "{}");
        Check(JsonSpanEditor.Find(JsonSpanEditor.ParseObject(quoted), "Admins", "we\"ird\\name") is not null, "Escaped key not written correctly.");

        // --- refusals: broken input must not be "repaired" silently ---
        Refuse("{\"Admins\": {", "unterminated object");
        Refuse("[1,2]", "array root");
        Refuse("{} trailing", "trailing text");
        Refuse("{\"A\": unknown}", "unquoted word");
    }

    private static int Count(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
