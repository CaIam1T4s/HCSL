using HCSL.Tui.Presentation;
using HCSL.Tui.Views.Controls;
using Terminal.Gui;
using HCSL.Frontend;
using HCSL.Core;

namespace HCSL.Tui.Views;

public sealed partial class ShellWindow : Toplevel
{
    private readonly SteamCmdController _steamCmd;
    private readonly SteamCmdPanel _steamPanel;
    private readonly Window _window;
    private readonly NavigationBar _navigation;
    private readonly FrameView _submenuFrame;
    private readonly PeerListView _submenu;
    private readonly FrameView _instancesFrame;
    private readonly InstanceListView _instances;
    private readonly Label _emptyInstances;
    private readonly List<Button> _instanceActions = [];
    private readonly List<InstanceActionButton> _presetActions = [];
    private readonly List<InstanceActionButton> _serverActions = [];
    private readonly FrameView _page;
    private readonly TextView _content;
    private readonly Label _summary;
    private readonly Label _status;
    private readonly Label _smallTerminal;
    private readonly Label _languageLabel;
    private readonly RadioGroup _language;
    private readonly Button _instanceButton;
    private readonly Button _logsButton;
    private readonly Button _manageButton;
    private TerminalOptions _options;
    private IReadOnlyList<SectionDefinition> _sections;
    private IReadOnlyDictionary<string, PageDefinition> _pages;
    private readonly Dictionary<string, string> _lastPages = [];
    private int _sectionIndex;
    private string _pageId = "home";
    private bool _tooSmall;
    private bool _updating;
    private string T(string zh, string en) => _options.Text(zh, en);

    public ShellWindow(TerminalOptions? options = null)
    {
        _options = options ?? new(false);
        _steamCmd = new SteamCmdController(new DaemonClient(), action => Application.MainLoop.Invoke(action), new DaemonBootstrapper());
        _steamCmd.English = _options.English;
        _sections = PageCatalog.Sections(_options);
        _pages = PageCatalog.Create(_options).ToDictionary(page => page.Id);
        _window = new Window("") { Width = Dim.Fill(), Height = Dim.Fill(1) };
        _navigation = new NavigationBar { Width = NavigationBar.ItemWidth * 6 };
        _navigation.SelectionChanged += SelectSection;
        _navigation.Activated += OpenSection;
        _summary = new Label("") { X = 1, Y = 2, Width = Dim.Fill(1), Height = 2 };
        _submenuFrame = new FrameView("") { X = 0, Y = 5, Width = 19, Height = Dim.Fill() };
        _submenu = new PeerListView { Width = Dim.Fill(), Height = Dim.Fill() };
        _submenu.SelectedItemChanged += args =>
        {
            if (!_updating) OpenPage(_sections[_sectionIndex].Pages[args.Item]);
        };
        _submenu.OpenSelectedItem += args =>
        {
            if (_updating) return;
            OpenPage(_sections[_sectionIndex].Pages[args.Item]);
            FocusContent();
        };
        _submenuFrame.Add(_submenu);
        _page = new FrameView("") { X = 0, Y = 5, Width = Dim.Fill(), Height = Dim.Fill() };
        _content = new TextView { X = 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(5), ReadOnly = true, WordWrap = true, CanFocus = false, TabStop = false };
        _languageLabel = new Label("") { X = 1, Y = Pos.AnchorEnd(4), Width = 12 };
        _language = new RadioGroup(["中文", "English"]) { X = 14, Y = Pos.AnchorEnd(4), SelectedItem = _options.English ? 1 : 0 };
        _language.SelectedItemChanged += _ =>
        {
            if (_updating) return;
            _options = _options with { English = _language.SelectedItem == 1 };
            RefreshText();
        };
        _instanceButton = new Button("") { X = 1, Y = Pos.AnchorEnd(4) };
        _logsButton = new Button("") { X = 1, Y = Pos.AnchorEnd(3) };
        _manageButton = new Button("") { X = 1, Y = Pos.AnchorEnd(2) };
        _instanceButton.Clicked += () => Navigate("instances");
        _logsButton.Clicked += () => Navigate("logs");
        _manageButton.Clicked += () => Navigate("manage");
        _page.Add(_content, _languageLabel, _language, _instanceButton, _logsButton, _manageButton);
        _instancesFrame = new FrameView("") { X = 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(4), Visible = false };
        // No synthetic rows: the host has not supplied any instances yet.
        _instances = new InstanceListView() { Width = Dim.Fill(), Height = Dim.Fill() };
        _emptyInstances = new Label("") { X = 1, Y = 1, Width = Dim.Fill(1), Height = 2, CanFocus = false };
        _instancesFrame.Add(_instances, _emptyInstances);
        _instances.SetSource(new List<string>());
        _instances.ActionsRequested += forward =>
        {
            if (forward) FocusFirstInstanceAction();
            else _instanceActions.Last(button => button.Enabled).SetFocus();
        };
        _instances.SelectedItemChanged += _ =>
        {
            if (_updating) return;
            _selectedInstanceId = SelectedRow?.Id;
            UpdateInstanceSummary();
        };
        for (var i = 0; i < 5; i++)
        {
            var index = i;
            var button = new InstanceActionButton() { X = 1 + i * 14, Y = Pos.AnchorEnd(2), Width = 13, AutoSize = false, Visible = false };
            button.Clicked += () => ShowInstanceAction(index);
            button.PeerRequested += direction => MoveInstanceAction(index, direction);
            _instanceActions.Add(button);
            _page.Add(button);
        }
        for (var i = 0; i < 5; i++)
        {
            var button = new InstanceActionButton
            {
                X = 1 + i % 3 * 15, Y = Pos.AnchorEnd(i < 3 ? 4 : 2),
                Width = 14, AutoSize = false, Enabled = false, Visible = false
            };
            var actionIndex = i;
            button.Clicked += () => PresetAction(actionIndex);
            _presetActions.Add(button);
            _page.Add(button);
        }
        for (var i = 0; i < 4; i++)
        {
            var index = i;
            var button = new InstanceActionButton { X = 1 + i * 16, Y = Pos.AnchorEnd(2), Width = 15, AutoSize = false, Visible = false };
            button.Clicked += () => ServerAction(index);
            button.PeerRequested += direction => MoveServerAction(index, direction);
            _serverActions.Add(button);
            _page.Add(button);
        }
        _page.Add(_instancesFrame);
        _steamPanel = new SteamCmdPanel(_steamCmd) { X = 1, Y = 0, Visible = false };
        _steamPanel.StateChanged += RefreshHostTitle;
        _page.Add(_steamPanel);
        _window.Add(_navigation, _summary, _submenuFrame, _page);
        _status = new Label("") { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
        _smallTerminal = new Label("") { Width = Dim.Fill(), Height = Dim.Fill(), Visible = false };
        Add(_window, _status, _smallTerminal);
        KeyPress += args =>
        {
            switch (args.KeyEvent.Key)
            {
                case Key.Esc: FocusParent(); break;
                case Key.F1: ShowHelp(); break;
                case Key.F5: _ = StartSelectedAsync(); break;
                case Key.F6: Navigate("launch"); ShowUnavailable(); break;
                case Key.F7: Navigate("presets"); break;
                case Key.F8: Navigate("manage"); break;
                case Key.F10:
                case Key.CtrlMask | Key.Q: ConfirmExit(); break;
                default: return;
            }
            args.Handled = true;
        };
        LayoutComplete += _ => CheckSize();
        RefreshText();
        ApplyTheme();
        InitializeServerControl();
        _navigation.SetFocus();
        Application.MainLoop.AddIdle(() =>
        {
            _ = _steamCmd.ConnectAsync();
            _ = _server.ConnectAsync();
            return false;
        });
    }

    private void SelectSection(int index)
    {
        _sectionIndex = index;
        var section = _sections[index];
        OpenPage(_lastPages.GetValueOrDefault(section.Id, section.Pages[0]));
        _navigation.SetFocus();
    }

    private void OpenSection(int index)
    {
        SelectSection(index);
        if (_submenuFrame.Visible) _submenu.SetFocus();
        else FocusContent();
    }

    private void UpdateInstanceActions()
    {
        if (_instanceActions.Count < 5) return;
        _emptyInstances.Visible = (_instances.Source?.Count ?? 0) == 0;
        var selected = _instances.SelectedItem >= 0 && _instances.SelectedItem < (_instances.Source?.Count ?? 0);
        _instanceActions[0].Enabled = _instanceActions[3].Enabled = _instanceActions[4].Enabled = selected && SelectedRow?.Definition != null;
        _instanceActions[2].Enabled = false;
        foreach (var button in _presetActions) button.Enabled = CurrentInstance != null && !_instanceBusy;
    }

    private void FocusFirstInstanceAction() => _instanceActions.First(button => button.Enabled).SetFocus();

    private void MoveInstanceAction(int index, int direction)
    {
        var next = index + direction;
        while (next >= 0 && next < _instanceActions.Count)
        {
            if (_instanceActions[next].Enabled) { _instanceActions[next].SetFocus(); return; }
            next += direction;
        }
        _instances.SetFocus();
    }

    private void ShowInstanceAction(int index)
    {
        InstanceActionClicked(index);
    }

    private void Navigate(string id)
    {
        _sectionIndex = _sections.ToList().FindIndex(section => section.Pages.Contains(id));
        OpenPage(id);
        FocusContent();
    }

    private void OpenPage(string id)
    {
        _pageId = id;
        _lastPages[_sections[_sectionIndex].Id] = id;
        _updating = true;
        var section = _sections[_sectionIndex];
        _navigation.ActiveIndex = _sectionIndex;
        _navigation.CursorIndex = _sectionIndex;
        _submenuFrame.Title = section.Title;
        var hasSubmenu = section.Id is not ("home" or "instances");
        _submenuFrame.Visible = hasSubmenu;
        _submenu.SetSource(section.Pages.Select(route => (route == id ? "● " : "  ") + _pages[route].Title).ToList());
        _submenu.SelectedItem = section.Pages.ToList().IndexOf(id);
        _page.X = hasSubmenu ? 19 : 0;
        _page.Width = Dim.Fill();
        var instanceList = section.Id == "instances";
        _instancesFrame.Visible = instanceList;
        var steamPage = id is "downloads" or "tasks" or "paths";
        _steamPanel.Visible = steamPage;
        _content.Visible = !instanceList && !steamPage;
        foreach (var button in _instanceActions) button.Visible = instanceList;
        // Select/copy/delete require an actual selection; do not target an empty row.
        UpdateInstanceActions();
        _page.Title = instanceList ? section.Title : _pages[id].Title;
        _content.Text = _pages[id].Content;
        if (id == "presets") UpdateInstanceSummary();
        _content.MoveHome();
        var language = id == "settings";
        _languageLabel.Visible = _language.Visible = language;
        _instanceButton.Visible = id == "home";
        _logsButton.Visible = _manageButton.Visible = section.Id == "home";
        _content.CanFocus = _content.TabStop = id == "logs";
        foreach (var button in _presetActions) button.Visible = id == "presets";
        foreach (var button in _serverActions) button.Visible = id == "manage";
        _updating = false;
        if (id is "instances" or "presets") _ = RefreshInstancesAsync();
        SetNeedsDisplay();
    }

    private void FocusContent()
    {
        if (_sections[_sectionIndex].Id == "instances") _instances.SetFocus();
        else if (_steamPanel.Visible) _steamPanel.FocusConnect();
        else if (_pageId == "settings") _language.SetFocus();
        else if (_pageId == "logs") _content.SetFocus();
        else if (_pageId == "home") _instanceButton.SetFocus();
        else if (_pageId == "manage")
        {
            if (_serverActions.FirstOrDefault(button => button.Enabled) is { } first) first.SetFocus();
            else _navigation.SetFocus();
        }
        else if (_submenuFrame.Visible) _submenu.SetFocus();
    }

    private void FocusParent()
    {
        if (_sections[_sectionIndex].Id == "instances")
        {
            if (_instances.HasFocus) _navigation.SetFocus();
            else _instances.SetFocus();
        }
        else if (_submenu.HasFocus || _navigation.HasFocus) _navigation.SetFocus();
        else if (_submenuFrame.Visible) _submenu.SetFocus();
        else if (_pageId != "home") { OpenPage("home"); _navigation.SetFocus(); }
        else _navigation.SetFocus();
    }

    private void RefreshText()
    {
        _updating = true;
        _sections = PageCatalog.Sections(_options);
        _pages = PageCatalog.Create(_options).ToDictionary(page => page.Id);
        _steamCmd.English = _options.English;
        _server.English = _options.English;
        RefreshHostTitle();
        _navigation.Titles = _options.English
            ? ["Home", "Manage", "Servers", "Download", "Network", "Settings"]
            : ["首页", "实例管理", "实例列表", "下载", "组网", "设置"];
        _instancesFrame.Title = T("实例", "Instances");
        _emptyInstances.Text = T("暂无实例；请创建，或重新进入本页刷新。", "No instances. Create one, or reopen this page to refresh.");
        var actions = _options.English ? new[] { "Select", "Create", "Import", "Edit", "Delete" } : ["选择", "创建", "导入", "编辑", "删除"];
        for (var i = 0; i < actions.Length; i++) _instanceActions[i].Text = actions[i];
        var presetActions = _options.English
            ? new[] { "New", "Copy", "Edit", "Delete", "Switch" }
            : ["新建", "复制", "编辑", "删除", "切换"];
        for (var i = 0; i < presetActions.Length; i++) _presetActions[i].Text = presetActions[i];
        _languageLabel.Text = T("语言", "Language");
        _language.RadioLabels = _options.English ? ["Chinese", "English"] : ["中文", "英文"];
        _language.SelectedItem = _options.English ? 1 : 0;
        _instanceButton.Text = T("没有服务器实例 - 前往实例列表", "No instances - Open instances");
        _logsButton.Text = T("日志", "Logs");
        _manageButton.Text = T("运行管理", "Manage server");
        RefreshServerButtons();
        _smallTerminal.Text = T("请扩大终端至至少 80x24。F10 退出", "Resize terminal to at least 80x24. F10 Quit");
        _status.Text = TerminalLayout.Hotkeys(_options.English, Bounds.Width);
        OpenPage(_pageId);
        UpdateInstanceSummary();
    }

    private void ApplyTheme()
    {
        var scheme = ThemeCatalog.Create();
        ApplyScheme(this, scheme);
        _content.ColorScheme = ThemeCatalog.Create(readOnlyContent: true);
        Colors.Dialog = Colors.Menu = Colors.Error = scheme;
        SetNeedsDisplay();
    }

    private static void ApplyScheme(View view, ColorScheme scheme)
    {
        view.ColorScheme = scheme;
        foreach (var child in view.Subviews) ApplyScheme(child, scheme);
    }

    private void CheckSize()
    {
        _status.Text = TerminalLayout.Hotkeys(_options.English, Bounds.Width);
        var small = Bounds.Width < 80 || Bounds.Height < 24;
        if (small == _tooSmall) return;
        _tooSmall = small;
        _window.Visible = _status.Visible = !small;
        _smallTerminal.Visible = small;
        if (!small) _navigation.SetFocus();
        SetNeedsDisplay();
    }

    private void ShowUnavailable() => MessageBox.Query(60, 8, T("操作不可用", "Unavailable"),
        T("F6 VAC 修改尚未接入：当前启动固定 -insecure 并报告 VAC 关闭。", "F6 VAC edits are not wired yet: launches stay -insecure and report VAC off."), T("返回", "Back"));

    private void ShowHelp() => MessageBox.Query(68, 17, T("帮助", "Help"),
        T("Tab / Shift+Tab 切换同级菜单并立即显示页面\n方向键同样切换，Enter 进入下一级\n实例列表：上下选实例，右方向键 / Enter 到操作按钮\n按钮 Tab 循环，Enter 执行，Esc 返回上级\n日志支持方向键 / PageUp / PageDown\nF5 启动，F6 VAC，F7 配置预设，F8 管理\n设置页切换语言；固定深色与 Unicode\nF10 / Ctrl+Q 退出\n\n顶栏显示本机宿主报告的服务器状态与地图；\nF5 启动，F8 管理页可停服/恢复检查；日志页尚未接入。", "Tab / Shift+Tab: peer menu, immediate preview\nArrows: peer menu; Enter: descend\nInstances: Up/Down select, Right/Enter to actions\nActions: Tab cycle, Enter run; Esc parent\nLogs: arrows / PageUp / PageDown\nF5 Start, F6 VAC, F7 Presets, F8 Manage\nSettings: language; fixed dark palette and Unicode\nF10 / Ctrl+Q: quit\n\nThe header shows live server state and map from the local daemon;\nF5 starts; the F8 manage page stops and recovers; the log page is not wired yet."), T("返回", "Back"));

    private void ConfirmExit()
    {
        // Disconnect only this frontend; the daemon owns running servers and submitted tasks.
        if (!_server.Active)
        {
            if (MessageBox.Query(66, 10, T("退出", "Quit"),
                T("服务器当前未运行。退出仅关闭当前界面；本机宿主继续保留任务。", "The server is not running. Exit closes this interface; the local daemon keeps its tasks."),
                T("取消", "Cancel"), T("退出界面", "Exit UI")) == 1)
                Application.RequestStop();
            return;
        }
        var choice = MessageBox.Query(70, 12, T("服务器仍在运行", "Server is still running"),
            T("服务器由本机宿主持有，退出界面不会停止它。\n请选择停服后退出，或保持运行继续退出。",
              "The local daemon owns the server; closing this interface does not stop it.\nStop it first, or keep it running and exit."),
            T("保持运行并退出", "Keep running, exit"), T("停服并退出", "Stop, then exit"), T("取消", "Cancel"));
        if (choice == 0) Application.RequestStop();
        else if (choice == 1) _ = StopAndExitAsync();
    }
    private void RefreshHostTitle()
    {
        var name = CurrentInstance?.Definition?.Name ?? T("未选择", "none");
        _window.Title = "HCSL | " + name + " | " + (_steamCmd.Connected ? T("本机宿主已连接", "Local daemon connected") : T("宿主未连接", "Host disconnected"));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _server.Dispose(); _steamCmd.Dispose(); }
        base.Dispose(disposing);
    }

}
