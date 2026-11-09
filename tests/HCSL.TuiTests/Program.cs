using Terminal.Gui;
using HCSL.Tui.Presentation;

Application.Init(new FakeDriver());
try
{
    var log = new TextView { X = 0, Y = 0, Width = 60, Height = 10, ReadOnly = true, WordWrap = false,
        Text = string.Join("\n", Enumerable.Range(1, 100).Select(i => "line " + i)) };
    var input = new TextField("draft") { X = 0, Y = 11, Width = 60 };
    Application.Top.Add(log, input);
    var state = Application.Begin(Application.Top);
    try
    {
        input.KeyPress += e =>
        {
            var direction = TerminalLogPaging.Direction(e.KeyEvent.Key);
            if (direction == 0) return;
            TerminalLogPaging.Page(log, direction);
            e.Handled = true;
        };
        input.SetFocus();
        foreach (var key in new[] { Key.PageUp, Key.CtrlMask | Key.PageUp, Key.F3 })
        {
            log.TopRow = 90;
            Application.Top.ProcessKey(new KeyEvent(key, new KeyModifiers()));
            Check(log.TopRow < 90 && input.HasFocus, "Decoded page-up key did not reach focused input: " + key);
        }
        foreach (var key in new[] { Key.PageDown, Key.CtrlMask | Key.PageDown, Key.F4 })
        {
            log.TopRow = 0;
            Application.Top.ProcessKey(new KeyEvent(key, new KeyModifiers()));
            Check(log.TopRow > 0 && input.HasFocus, "Decoded page-down key did not reach focused input: " + key);
        }
        log.TopRow = 90;
        var up = TerminalLogPaging.Page(log, -1);
        Check(up < 90 && up >= 0, "Page up did not move the log.");
        Check(input.HasFocus && input.Text.ToString() == "draft", "Paging stole focus or modified input.");
        var down = TerminalLogPaging.Page(log, 1);
        Check(down > up && down <= 90, "Page down did not move the log.");
        for (var i = 0; i < 30; i++) TerminalLogPaging.Page(log, -1);
        Check(log.TopRow == 0, "Paging above first line escaped bounds.");
        for (var i = 0; i < 30; i++) TerminalLogPaging.Page(log, 1);
        Check(log.TopRow == 90 && input.HasFocus, "Paging below last viewport escaped bounds or stole focus.");
        Console.WriteLine("PASS tui.log-paging-up-down-bounds-and-input-focus (FakeDriver; not a real terminal key decoder)");
    }
    finally { Application.End(state); }
    CfgEditorTests.Run();
    MapPoolTests.Run();
    PluginTests.Run();
}
finally { Application.Shutdown(); }

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
