using System.Text;

namespace HCSL.Tui.Presentation;

public static class TerminalLayout
{
    public static int Width(string text) => text.EnumerateRunes().Sum(rune => Math.Max(0, System.Rune.ColumnWidth((System.Rune)rune.Value)));

    public static string Fit(string text, int columns)
    {
        var result = new StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var width = Math.Max(0, System.Rune.ColumnWidth((System.Rune)rune.Value));
            if (used + width > columns) break;
            result.Append(rune.ToString());
            used += width;
        }
        return result.ToString();
    }

    public static string Center(string text, int columns)
    {
        text = Fit(text, columns);
        var spare = Math.Max(0, columns - Width(text));
        return new string(' ', spare / 2) + text + new string(' ', spare - spare / 2);
    }

    public static string Hotkeys(bool english, int columns, bool logs = false, bool following = true)
    {
        string[] labels = english
            ? ["F1 Help", "F5 Start", "F6 VAC", "F7 Preset", "F8 Manage", "F10 Quit"]
            : ["F1 帮助", "F5 启动", "F6 VAC", "F7 配置预设", "F8 管理", "F10 退出"];
        if (logs)
            labels = [.. labels,
                english ? (following ? "Space Pause" : "Space Follow") : (following ? "空格 暂停" : "空格 跟随"),
                english ? "End Newest" : "End 最新"];
        var gap = columns >= 100 ? "   │   " : "   ";
        return " " + string.Join(gap, labels);
    }
}
