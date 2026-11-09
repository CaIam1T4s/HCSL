using HCSL.Core;
using System.Text;
using Terminal.Gui;
namespace HCSL.Tui.Views;

public sealed partial class ShellWindow
{
    private readonly TextField _consoleInput = new("") { X = 1, Y = Pos.AnchorEnd(2), Width = Dim.Fill(14), Visible = false };
    private readonly Button _consoleSend = new("发送命令") { X = Pos.AnchorEnd(12), Y = Pos.AnchorEnd(2), Visible = false };
    private readonly Label _consoleStatus = new("") { X = 1, Y = Pos.AnchorEnd(4), Width = Dim.Fill(1), Height = 2, Visible = false };
    private readonly CheckBox _rememberConsole = new("保存历史", true) { X = 1, Y = Pos.AnchorEnd(5), Visible = false };
    private readonly Button _clearConsoleHistory = new("清空历史") { X = 18, Y = Pos.AnchorEnd(5), Visible = false };
    private Guid? _consoleRun;
    private int _consoleHistoryIndex = -1;
    private void InitializeConsole()
    {
        _page.Add(_consoleInput, _consoleSend, _consoleStatus, _rememberConsole, _clearConsoleHistory);
        _rememberConsole.Toggled += _ => { _server.ConsoleHistoryEnabled = _rememberConsole.Checked; _consoleHistoryIndex = -1; };
        _clearConsoleHistory.Clicked += () => { _server.ClearConsoleHistory(); _consoleHistoryIndex = -1; };
        _consoleSend.Clicked += () => { _ = SendConsoleQueryAsync(); };
        _consoleInput.KeyPress += e =>
        {
            var direction = HCSL.Tui.Presentation.TerminalLogPaging.Direction(e.KeyEvent.Key);
            if (direction != 0)
            {
                e.Handled = true;
                ScrollConsoleLog(direction);
            }
            else if (e.KeyEvent.Key == Key.Enter) { e.Handled = true; _ = SendConsoleQueryAsync(); }
            else if (e.KeyEvent.Key is Key.CursorUp or Key.CursorDown && _server.ConsoleHistory.Count > 0)
            {
                var items = _server.ConsoleHistory;
                if (_consoleHistoryIndex < 0) _consoleHistoryIndex = items.Count;
                _consoleHistoryIndex = Math.Clamp(_consoleHistoryIndex + (e.KeyEvent.Key == Key.CursorUp ? -1 : 1), 0, items.Count);
                _consoleInput.Text = _consoleHistoryIndex == items.Count ? "" : items[_consoleHistoryIndex];
                _consoleInput.CursorPosition = (_consoleInput.Text.ToString() ?? "").EnumerateRunes().Count();
                e.Handled = true;
            }
        };
    }
    private void ScrollConsoleLog(int direction)
    {
        if (_pageId != "logs") return;
        var top = HCSL.Tui.Presentation.TerminalLogPaging.Page(_content, direction);
        _logFollow = top >= Math.Max(0, _content.Lines - Math.Max(1, _content.Bounds.Height));
        if (_logFollow) _logReading.Reset();
        _lastLogTop = top;
        RefreshHotkeys();
    }
    private void RefreshConsole()
    {
        var visible = _pageId == "logs";
        _consoleInput.Visible = _consoleSend.Visible = _consoleStatus.Visible = _rememberConsole.Visible = _clearConsoleHistory.Visible = visible;
        if (_consoleRun != _server.RunId) { _consoleRun = _server.RunId; _consoleInput.Text = ""; _consoleHistoryIndex = -1; }
        _consoleInput.Enabled = _server.Connected && _server.Active && _server.State == ServerState.Running;
        _consoleSend.Enabled = _server.CanSendConsole;
        _consoleSend.Text = T("发送命令", "Send");
        var notice = _logReading.ReadingPositionLost ? T("较早日志已移出缓冲区；已定位最早保留记录。End 跳到最新。\n", "Reading position evicted; earliest retained record shown. End: latest.\n") : "";
        _consoleStatus.Text = notice + (_server.ConsoleStatus.Length > 0 ? _server.ConsoleStatus
            : T("通用单行命令；↑↓历史；PgUp/PgDn 或 F3/F4 翻日志；F2 回输入；历史仅存内存", "CS2 commands; Up/Down history; PgUp/PgDn or F3/F4 scroll logs; F2 input; memory-only history"));
    }
    private async Task SendConsoleQueryAsync()
    {
        if (_consoleRun is not { } run || !_server.CanSendConsole) return;
        var text = _consoleInput.Text.ToString() ?? "";
        ParsedConsoleCommand parsed;
        try { parsed = ServerConsoleRules.Parse(text); }
        catch (ServerException) { await _server.SendConsoleAsync(run, text); return; }
        JumpToNewestLog();
        await _server.SendConsoleAsync(run, parsed.Text);
        if (_consoleRun == run && _consoleInput.Text.ToString() == text) _consoleInput.Text = "";
        _consoleHistoryIndex = -1;
    }
}
