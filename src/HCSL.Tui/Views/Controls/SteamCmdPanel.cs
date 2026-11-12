using System.ComponentModel;
using HCSL.Frontend;
using Terminal.Gui;

namespace HCSL.Tui.Views.Controls;
using Application = global::Terminal.Gui.Application;

public sealed class SteamCmdPanel : View
{
    private readonly SteamCmdController model;
    private readonly BaselineController? baseline;
    private readonly Label connection, stage, detail;
    private readonly Label[] labels;
    private readonly TextField settings, data, library, tool, account, timeout, installTimeout;
    private readonly CheckBox bootstrap;
    private readonly Button connect, start, recover, cancel, input, install, validate, baselineButton;
    private readonly TextView events;
    private bool updating, disposed;
    public event Action? StateChanged;

    public SteamCmdPanel(SteamCmdController model, BaselineController? baseline = null)
    {
        this.model = model;
        this.baseline = baseline;
        Width = Dim.Fill(1); Height = Dim.Fill();
        connection = new Label("") { X = 0, Y = 0, Width = Dim.Fill(), Height = 1 };
        labels = Enumerable.Range(1, 7).Select(y => new Label("") { X = 0, Y = y, Width = 12 }).ToArray();
        settings = Field(1, value => model.SettingsRoot = value);
        data = Field(2, value => model.DataRoot = value);
        library = Field(3, value => model.LibraryRoot = value);
        tool = Field(4, value => model.ExecutablePath = value);
        account = Field(5, value => model.AccountName = value);
        timeout = Field(6, value => model.TimeoutSeconds = value);
        installTimeout = Field(7, value => model.InstallTimeoutSeconds = value);
        installTimeout.Width = 7;
        timeout.Width = 7;
        bootstrap = new CheckBox("") { X = 22, Y = 6, Width = Dim.Fill() };
        bootstrap.Toggled += _ => { if (!updating) model.InstallIfMissing = bootstrap.Checked; };
        connect = new Button("") { X = 0, Y = 8 };
        start = new Button("") { X = 14, Y = 8 };
        recover = new Button("") { X = 28, Y = 8 };
        cancel = new Button("") { X = 43, Y = 8 };
        install = new Button("") { X = 0, Y = 9 };
        validate = new Button("") { X = 24, Y = 9 };
        install.Clicked += () => { _ = model.InstallAsync(); };
        validate.Clicked += () => { _ = model.ValidateAsync(); };
        baselineButton = new Button("") { X = 46, Y = 9 };
        baselineButton.Clicked += () => { _ = ShowBaselineAsync(); };
        connect.Clicked += () => { _ = ConnectAsync(); };
        start.Clicked += () => { _ = StartAsync(); };
        recover.Clicked += () => { _ = model.RecoverAsync(); };
        cancel.Clicked += () => { _ = model.CancelAsync(); };
        stage = new Label("") { X = 0, Y = 10, Width = Dim.Fill(), Height = 1 };
        detail = new Label("") { X = 0, Y = 11, Width = Dim.Fill(), Height = 2 };
        events = new TextView { X = 0, Y = 13, Width = Dim.Fill(), Height = Dim.Fill(2),
            ReadOnly = true, WordWrap = true, CanFocus = false, TabStop = false };
        input = new Button("") { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), AutoSize = false };
        input.Clicked += EnterSecret;
        Add(connection);
        foreach (var label in labels) Add(label);
        Add(settings, data, library, tool, account, timeout, installTimeout, bootstrap, install, validate, baselineButton, connect, start, recover, cancel, stage, detail, events, input);
        model.PropertyChanged += Changed;
        Refresh();
    }
    private async Task ConnectAsync()
    {
        await model.ConnectAsync();
        if (!disposed) Application.MainLoop.Invoke(() => { if (!disposed && model.CanStart) start.SetFocus(); });
    }
    private async Task StartAsync()
    {
        await model.StartAsync();
        if (!disposed) Application.MainLoop.Invoke(() => { if (!disposed && model.CanCancel) cancel.SetFocus(); });
    }
    private TextField Field(int row, Action<string> changed)
    {
        var field = new TextField("") { X = 12, Y = row, Width = Dim.Fill() };
        field.TextChanged += _ => { if (!updating) changed(field.Text.ToString() ?? ""); };
        return field;
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) { Refresh(); StateChanged?.Invoke(); }
    public void FocusConnect() => connect.SetFocus();
    private void Refresh()
    {
        if (disposed) return;
        updating = true;
        string T(string zh, string en) => model.English ? en : zh;
        var names = model.English ? new[] { "Settings", "Data", "Library", "Tool (opt.)", "Account", "Login (s)", "Install (s)" }
            : new[] { "设置目录", "数据目录", "内容库", "工具(可空)", "账号(空匿名)", "登录超时(秒)", "安装超时(秒)" };
        for (int i = 0; i < names.Length; i++) labels[i].Text = names[i];
        var fields = new[] { settings, data, library, tool, account, timeout, installTimeout };
        var values = new[] { model.SettingsRoot, model.DataRoot, model.LibraryRoot, model.ExecutablePath, model.AccountName, model.TimeoutSeconds, model.InstallTimeoutSeconds };
        for (int i = 0; i < fields.Length; i++)
        {
            if (fields[i].Text.ToString() != values[i]) fields[i].Text = values[i];
            fields[i].Enabled = model.CanEdit;
        }
        bootstrap.Text = T("缺少时安装工具", "Install tool if missing");
        bootstrap.Checked = model.InstallIfMissing;
        bootstrap.Enabled = model.CanEdit;
        connection.Text = model.ConnectionText;
        connect.Text = T("连接", "Connect"); start.Text = T("准备/登录", "Probe");
        recover.Text = T("恢复检查", "Recover"); cancel.Text = T("取消任务", "Cancel");
        connect.Enabled = model.CanConnect;
        start.Enabled = recover.Enabled = model.CanStart;
        install.Text = T("安装/更新 CS2", "Install/Update CS2");
        validate.Text = T("校验 CS2", "Validate CS2");
        install.Enabled = validate.Enabled = model.CanStart;
        baselineButton.Text = T("基线", "Baseline");
        baselineButton.Enabled = baseline is { CanRefresh: true };
        cancel.Enabled = model.CanCancel;
        stage.Text = model.StageText;
        detail.Text = model.Detail;
        if (events.Text.ToString() != model.LogText) { events.Text = model.LogText; events.MoveEnd(); }
        input.Text = model.NeedsInput ? model.ChallengeText : T("安装目标：", "Install to: ") + model.ServerDirectory;
        input.Enabled = model.CanSubmit;
        updating = false;
        SetNeedsDisplay();
    }
    /// <summary>Shows which baseline deployment would use, and offers one explicit re-verification.</summary>
    private async Task ShowBaselineAsync()
    {
        if (baseline is null) return;
        await baseline.RefreshStatusAsync();
        string T(string zh, string en) => model.English ? en : zh;
        var refresh = new Button(T("校验并刷新", "Verify & refresh")) { Enabled = baseline.CanRefresh };
        var close = new Button(T("关闭", "Close"));
        using var dialog = new Dialog(T("启动基线", "Startup baseline"), 72, 12, refresh, close);
        var text = new Label("")
        {
            X = 1, Y = 0, Width = Dim.Fill(1), Height = 7,
            Text = baseline.StatusText + "\n\n" + T(
                "CS2 更新后部署会拒绝旧哈希。刷新会重新校验入口/原生文件/必要配置并写入本地基线；不启动游戏。",
                "After a CS2 update deployment rejects old hashes. Refresh re-verifies the entrypoint, native files and configs, then writes the local baseline. No game is started.")
        };
        var confirmed = false;
        refresh.Clicked += () => { confirmed = true; Application.RequestStop(); };
        close.Clicked += () => Application.RequestStop();
        dialog.Add(text);
        dialog.Ready += () => refresh.SetFocus();
        Application.Run(dialog);
        if (!confirmed) return;
        if (MessageBox.Query(72, 9, T("刷新启动基线", "Refresh baseline"),
                T("重新校验当前安装并写入本地基线？不会启动服务器。", "Re-verify the installation and write the local baseline? No server is started."),
                T("取消", "Cancel"), T("刷新", "Refresh")) != 1) return;
        await baseline.RefreshBaselineAsync();
        var message = baseline.Status.Length > 0 ? baseline.Status : baseline.StatusText;
        Application.MainLoop.Invoke(() => MessageBox.Query(72, 9, T("启动基线", "Startup baseline"), message, T("返回", "Back")));
    }
    private void EnterSecret()
    {
        var challenge = model.ChallengeId;
        var submit = new Button(model.English ? "Submit" : "提交");
        var back = new Button(model.English ? "Back" : "返回");
        using var dialog = new Dialog(model.English ? "Steam authentication" : "Steam 认证", 60, 8, submit, back);
        var prompt = new Label(model.ChallengeText) { X = 1, Y = 0, Width = Dim.Fill(1), Height = 2 };
        var secret = new TextField("") { X = 1, Y = 2, Width = Dim.Fill(1), Secret = true };
        bool accepted = false;
        submit.Clicked += () => { accepted = true; Application.RequestStop(); };
        back.Clicked += () => Application.RequestStop();
        dialog.Add(prompt, secret);
        secret.SetFocus();
        Application.Run(dialog);
        var value = secret.Text.ToString() ?? "";
        secret.Text = "";
        if (accepted && challenge == model.ChallengeId) { _ = model.SubmitAsync(value); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { disposed = true; model.PropertyChanged -= Changed; }
        base.Dispose(disposing);
    }
}
