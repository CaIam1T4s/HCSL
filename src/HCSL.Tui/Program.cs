using HCSL.Tui.Views;
using HCSL.Tui.Presentation;
using Terminal.Gui;

namespace HCSL.Tui;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine("HCSL TUI (development preview)\nUsage: HCSL.Tui [--lang en|zh-CN]\nTERM=linux defaults to English. Fixed dark palette and Unicode. Minimum size: 80x24.\nF1 Help | F10 / Ctrl+Q Quit\nNavigation only; server operations are not connected.");
            return 0;
        }
        TerminalOptions options;
        try { options = TerminalOptions.Parse(args); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine(options.Text("TUI 需要交互终端，请使用 ssh -t 分配终端。", "An interactive terminal is required. Use ssh -t to allocate a terminal."));
            return 2;
        }

        var initialized = false;
        Exception? failure = null;
        try
        {
            Application.Init();
            initialized = true;
            using var shell = new ShellWindow(options);
            Application.Run(shell);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (initialized) Application.Shutdown();
        }
        if (failure is null) return 0;
        Console.Error.WriteLine($"CSL-TUI-001: Terminal UI failed.\n{failure.GetType().Name}: {failure.Message}\nCheck terminal type and dependencies. Run --help for requirements.");
        return 1;
    }
}
