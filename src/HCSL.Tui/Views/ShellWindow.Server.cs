using HCSL.Core;
using HCSL.Frontend;
using HCSL.Tui.Presentation;
using Terminal.Gui;

namespace HCSL.Tui.Views;
using Application = global::Terminal.Gui.Application;

public sealed partial class ShellWindow
{
    private readonly ServerController _server = new(new DaemonClient(), action => Application.MainLoop.Invoke(action), new DaemonBootstrapper());

    private long _catalogAttempt;
    // Log page: follow the newest line until the operator pauses it.
    private bool _logFollow = true;
    // A Start we issued whose preflight outcome has not been reported yet.
    private bool _startInFlight;
    private Guid? _baselineRun;
    private ServerState _baselineState;
    private string _baselineCode = "";
    // Last failed state we already talked about, so polling the same failure never re-opens a dialog.
    private string _reportedFailure = "";
    private static string Short(Guid? id) => id is { } value ? value.ToString("N")[..8] : "—";

    /// <summary>Text and enablement for the manage-page buttons; the daemon's own gates decide.</summary>
    private void RefreshServerButtons()
    {
        if (_serverActions.Count < 4) return;
        var labels = _options.English ? new[] { "Start", "Stop", "Recover", "Logs" } : ["启动", "停服", "恢复检查", "日志"];
        for (var i = 0; i < labels.Length; i++) _serverActions[i].Text = labels[i];
        _serverActions[0].Enabled = _server.CanStart && CurrentInstance is not null;
        _serverActions[1].Enabled = _server.CanStop;
        _serverActions[2].Enabled = _server.CanRecover;
        _serverActions[3].Enabled = _server.Connected;
    }

    private void ServerAction(int index)
    {
        switch (index)
        {
            case 0: _ = StartSelectedAsync(); break;
            case 1: _ = StopServerAsync(); break;
            case 2: _ = RecoverServerAsync(); break;
            case 3: Navigate("logs"); break;
        }
    }

    private void MoveServerAction(int index, int direction)
    {
        var next = index + direction;
        while (next >= 0 && next < _serverActions.Count)
        {
            if (_serverActions[next].Enabled) { _serverActions[next].SetFocus(); return; }
            next += direction;
        }
        FocusContent();
    }

    /// <summary>Shared by F5 and the manage page; never optimistically claims a running server.</summary>
    private (bool Confirmed, bool AllowLowMemory) ConfirmServerStart(string body)
    {
        var dialog = new Dialog(T("启动服务器", "Start server"), 80, 23);
        dialog.Add(new Label(body) { X = 1, Y = 0, Width = Dim.Fill(2), Height = Dim.Fill(6) });
        var consent = new CheckBox(T("本次调试跳过低内存确认（3–4 GiB）", "Skip low-memory confirmation for this start (3–4 GiB)"), false)
            { X = 1, Y = Pos.AnchorEnd(5) };
        dialog.Add(consent);
        dialog.Add(new Label(T("低于 3 GiB 仍拒绝；控制组限额与 OOM 风险不变。", "Below 3 GiB remains blocked; cgroup limits and OOM risks are unchanged."))
            { X = 1, Y = Pos.AnchorEnd(4), Width = Dim.Fill(2) });
        var confirmed = false;
        var cancel = new Button(T("取消", "Cancel")); cancel.Clicked += () => Application.RequestStop();
        var start = new Button(T("启动", "Start")); start.Clicked += () => { confirmed = true; Application.RequestStop(); };
        dialog.AddButton(cancel); dialog.AddButton(start);
        Application.Run(dialog);
        var result = (confirmed, confirmed && consent.Checked);
        dialog.Dispose();
        return result;
    }

    private async Task StartSelectedAsync()
    {
        if (!_server.Connected)
        {
            MessageBox.ErrorQuery(76, 10, T("无法启动", "Cannot start"),
                T("本机宿主不可达，服务器状态未知。请先确认 --status 后再试。", "The local daemon is unreachable and the state is unknown. Check --status first."), T("返回", "Back"));
            return;
        }
        var entry = CurrentInstance;
        if (entry?.Definition is not { } definition)
        {
            MessageBox.Query(76, 10, T("未选择实例", "No instance selected"),
                T("请先在实例列表中创建并选中一个实例。", "Create and select an instance first."), T("返回", "Back"));
            return;
        }
        if (_server.Active)
        {
            MessageBox.Query(78, 10, T("服务器已在运行", "Server already running"),
                T($"宿主已有运行中的任务（RunId {Short(_server.RunId)}）。需要重启请先在管理页停服。", $"The daemon already owns run {Short(_server.RunId)}. Stop it before restarting."), T("返回", "Back"));
            return;
        }
        if (_server.State == ServerState.RecoveryRequired)
        {
            MessageBox.Query(80, 12, T("需要先恢复", "Recovery required"),
                T("上次会话未确认清理。请在管理页执行“恢复检查”，确认旧进程与控制组已清空后再启动。", "The previous session was not confirmed clean. Run recovery from the manage page first."), T("返回", "Back"));
            return;
        }
        if (!_server.CanStart)
        {
            MessageBox.Query(78, 10, T("暂时不能启动", "Cannot start yet"),
                _server.DetailText.Length != 0 ? _server.DetailText : T("宿主仍在处理上一个操作。", "The daemon is still finishing a previous operation."), T("返回", "Back"));
            return;
        }
        var preset = entry.Presets.FirstOrDefault(p => p.Id == definition.SelectedPresetId);
        var presetName = preset?.Name ?? T("无", "none");
        var presetMap = preset is null ? T("无", "none") : ServerMapRules.Label(new(preset.MapKind, preset.Map));
        var zh = new List<string>
        {
            $"实例：{definition.Name}",
            ServerStatusText.ConnectionAddressesLine(false, _connectionAddresses, definition.GamePort),
            "地址仅供连接参考；访问范围由防火墙控制。",
        };
        var en = new List<string>
        {
            $"Instance: {definition.Name}",
            ServerStatusText.ConnectionAddressesLine(true, _connectionAddresses, definition.GamePort),
            "Addresses are connection hints, not firewall isolation.",
        };
        zh.AddRange([$"预设：{presetName}   地图：{presetMap}", "VAC：关 · sv_lan 1 · 接入：Direct", "启动超时：120 秒", "",
            "部署缺失或已过期时，宿主会先用 reflink 重新部署再启动（旧部署改名保留，不改基础安装）。",
            "内存与进程数由宿主按 systemd 控制组限制；失败原因原样显示宿主代码。"]);
        en.AddRange([$"Preset: {presetName}   Map: {presetMap}", "VAC: off · sv_lan 1 · access: Direct", "Start timeout: 120 s", "",
            "A missing or stale deployment is reflinked and redeployed by the daemon before the start (the old deployment is kept as .previous-<time>; the base install is untouched).",
            "Memory and process limits come from the daemon's systemd control group; failures keep the daemon's own code."]);
        var body = T(string.Join("\n", zh), string.Join("\n", en));
        var confirmation = ConfirmServerStart(body);
        if (!confirmation.Confirmed) return;
        _server.Attach(definition.Id);
        _startInFlight = true;
        _reportedFailure = "";
        // Remember what the daemon already reported. A refresh can deliver that same snapshot after the press
        // (the status poll is faster than a start), and a repeated refusal keeps the old run: only a failure
        // whose run or state has moved on belongs to this attempt.
        _baselineRun = _server.RunId;
        _baselineState = _server.State;
        _baselineCode = _server.Code;
        _status.Text = T("已提交启动请求，等待地图就绪…", "Start requested; waiting for map readiness…");
        // One press is enough: the daemon deploys first when the deployment is missing or stale, so the
        // operator never has to discover a refusal and press again to accept the same fix.
        if (!await _server.StartAsync(autoDeploy: true, allowLowMemory: confirmation.AllowLowMemory))
        {
            _startInFlight = false;
            ShowServerFailure();
            return;
        }
        await HandleStartOutcomeAsync();
    }

    /// <summary>
    /// Start answers as soon as the daemon admits the request; deployment, memory and port checks run
    /// afterwards in the daemon's background task. A rejection therefore arrives as a later snapshot
    /// update, so the outcome is reported from the state, not from the Start reply.
    /// </summary>
    private async Task HandleStartOutcomeAsync()
    {
        if (!_startInFlight || _server.Busy) return;
        if (_server.State is ServerState.Preparing or ServerState.Starting or ServerState.LoadingMap) return;
        if (_server.State is ServerState.Running) { _startInFlight = false; return; }
        if (_server.State is not (ServerState.Failed or ServerState.RecoveryRequired)) { _startInFlight = false; return; }
        if (_server.RunId == _baselineRun && _server.State == _baselineState && _server.Code == _baselineCode) return;
        var signature = _server.State + "|" + _server.Code + "|" + Short(_server.RunId);
        if (_reportedFailure == signature) return;
        _reportedFailure = signature;
        _startInFlight = false;
        if (_server.Code == "HCSL-SERVER-MEMORY-WARNING")
        {
            var instance = _server.Snapshot?.InstanceId;
            if (MessageBox.Query(84, 16, T("低内存启动确认", "Confirm low-memory start"),
                _server.DetailText + "\n\n" + T("可用内存不足 4 GiB，继续可能导致卡顿或 OOM。低于 3 GiB 仍禁止启动。服务器硬上限 2560 MiB，禁止 swap。仅本次有效。", "Less than 4 GiB is available. Proceeding may cause stalls or OOM. Below 3 GiB remains blocked. Service limit: 2560 MiB; no swap. This start only."),
                T("取消", "Cancel"), T("仍然启动", "Start anyway")) != 1) return;
            if (instance is not { } id) return;
            _server.Attach(id);
            _baselineRun = _server.RunId; _baselineState = _server.State; _baselineCode = _server.Code;
            _startInFlight = true;
            if (!await _server.StartAsync(autoDeploy: true, allowLowMemory: true))
            { _startInFlight = false; ShowServerFailure(); }
            return;
        }
        ShowServerFailure();
    }

    private async Task StopServerAsync()
    {
        if (!_server.CanStop)
        {
            MessageBox.Query(78, 10, T("没有可停服的服务器", "Nothing to stop"),
                T("宿主当前没有活动任务；若状态为“需要恢复检查”，请改用恢复按钮。", "The daemon has no active run; use recovery if the state requires it."), T("返回", "Back"));
            return;
        }
        var run = Short(_server.RunId);
        if (MessageBox.Query(80, 14, T("停服确认", "Confirm stop"),
            T(string.Join("\n",
                    $"将向 RunId {run} 发送 quit；5 秒内未退出则结束整个控制组。",
                    "已提交的 SteamCMD 任务不受影响，界面保持打开。"),
                string.Join("\n",
                    $"Sends quit to run {run}; after 5 seconds the whole control group is terminated.",
                    "SteamCMD tasks are unaffected and the UI stays open.")),
            T("取消", "Cancel"), T("停服", "Stop")) != 1) return;
        await _server.StopAsync();
        Application.MainLoop.Invoke(() =>
        {
            _status.Text = T("已请求停服，等待清理…", "Stop requested; waiting for cleanup…");
            RefreshServerButtons();
            ShowServerFailure();
        });
    }

    private async Task RecoverServerAsync()
    {
        if (!_server.CanRecover)
        {
            MessageBox.Query(78, 10, T("无需恢复", "Recovery not needed"),
                T("当前状态不需要恢复检查。仅在清理未确认或宿主重启后发现遗留会话时使用。", "Recovery is only for unconfirmed cleanup or a session left behind by a restarted daemon."), T("返回", "Back"));
            return;
        }
        var instance = _server.Snapshot?.InstanceId is { } recorded ? recorded
            : _server.AttachedInstance != Guid.Empty ? _server.AttachedInstance
            : CurrentInstance?.Id ?? Guid.Empty;
        if (instance == Guid.Empty)
        {
            MessageBox.Query(80, 10, T("无法确定实例", "Instance unknown"),
                T("宿主报告需要恢复，但没有记录实例身份。请用命令行 --recover --instance <UUID> 指定。", "The daemon needs recovery but recorded no instance. Use --recover --instance <UUID> on the command line."), T("返回", "Back"));
            return;
        }
        if (MessageBox.Query(82, 14, T("恢复检查", "Recovery check"),
            T(string.Join("\n",
                    "恢复不会杀死无法确认归属的进程；只清理能确认属于上一次会话的单元与标记，失败时保留标记。",
                    $"记录中的 RunId 为 {Short(_server.RunId)}；界面不另传 RunId，宿主按库内会话记录核对归属。"),
                string.Join("\n",
                    "Recovery never kills processes it cannot attribute; it only clears a unit and markers proven to belong to the previous session, and keeps them when unsure.",
                    $"The recorded run is {Short(_server.RunId)}; the UI sends no separate RunId and the daemon verifies ownership from the library record.")),
            T("取消", "Cancel"), T("恢复检查", "Recover")) != 1) return;
        _server.Attach(instance);
        await _server.RecoverAsync();
        Application.MainLoop.Invoke(() =>
        {
            RefreshServerButtons();
            ShowServerFailure();
        });
    }

    /// <summary>Surfaces the daemon's own code plus its hint; never invent a friendlier status.</summary>
    private void ShowServerFailure()
    {
        // A failed Start/Stop/Recover answers with State=Failed plus a code in the snapshot, not with
        // ErrorText, so reporting only ErrorText silently swallowed preflight rejections.
        var failed = _server.ErrorText.Length != 0 || _server.State is ServerState.Failed or ServerState.RecoveryRequired;
        if (!failed) return;
        var text = _server.DetailText.Length != 0 ? _server.DetailText : _server.ErrorText;
        if (text.Length == 0) return;
        MessageBox.ErrorQuery(84, 14, T("服务器操作失败", "Server operation failed"), text, T("返回", "Back"));
    }

    private void InitializeServerControl()
    {
        InitializeConsole();
        _server.English = _options.English;
        _server.PropertyChanged += (_, _) =>
        {
            RefreshServerSummary();
            RefreshServerButtons();
            RefreshConsole();
            _ = HandleStartOutcomeAsync();
        };
        RefreshServerSummary();
        RefreshServerButtons();
        // Load the catalog up front: without it a reopened UI can only show the running instance ID,
        // because the header names presets from the catalog rather than from the daemon.
        _catalogAttempt = System.Diagnostics.Stopwatch.GetTimestamp();
        _ = RefreshInstancesAsync();
    }

    private void AttachSelectedInstance()
    {
        var entry = CurrentInstance;
        if (entry?.Definition is not { } definition) return;
        _server.Attach(definition.Id);
        var configured = entry.Presets.FirstOrDefault(p => p.Id == definition.SelectedPresetId);
        _server.ConfiguredMap = configured is null ? null : ServerMapRules.Label(new(configured.MapKind, configured.Map));
    }

    /// <summary>Single writer of the header: catalog, lifecycle, then network readiness.</summary>
    private void RefreshServerSummary()
    {
        if (_updating || _summary is null) return;
        // The snapshot can name an instance the catalog has not been read for yet (reopened UI, retried
        // read after a busy host); retry, but not on every poll.
        if (_instanceRows.Length == 0 && _server.LiveInstanceId is not null &&
            System.Diagnostics.Stopwatch.GetElapsedTime(_catalogAttempt) >= TimeSpan.FromSeconds(5))
        {
            _catalogAttempt = System.Diagnostics.Stopwatch.GetTimestamp();
            _ = RefreshInstancesAsync();
        }
        // Only a live run (or one awaiting recovery) may override the user's selection; a failed or
        // stopped run keeps its identity in the snapshot but must not hijack the header.
        var selected = CurrentInstance;
        var liveId = _server.LiveInstanceId;
        var running = liveId is { } live ? Array.Find(_instanceRows, e => e.Id == live) : null;
        var displayed = selected ?? running;
        var definition = displayed?.Definition;
        var preset = definition is null ? null : displayed!.Presets.FirstOrDefault(p => p.Id == definition.SelectedPresetId);
        if (definition is not null) _server.ConfiguredMap = preset is null ? null : ServerMapRules.Label(new(preset.MapKind, preset.Map));
        // Keep naming the map of the last run after a stop or a daemon restart, instead of silently
        // reverting to "（预设）" as if that map had never loaded.
        if (_server.Map is { } observed && _server.State is ServerState.Running or ServerState.Stopping)
            _server.ObservedMap = observed;

        var instance = definition?.Name
            ?? (liveId is { } pending ? pending.ToString("N")[..8] : T("未选择实例", "no instance"));
        var presetName = preset?.Name ?? T("未选择", "none");
        var presetMap = preset is null ? T("未选择", "none") : ServerMapRules.Label(new(preset.MapKind, preset.Map));
        var vac = !_server.Connected ? T("未知", "unknown")
            : (_server.EffectiveVac ? T("开", "on") : T("关", "off")) + " · " + _server.Mode;
        var head = $"{T("实例", "Instance")}: {instance}   {T("预设", "Preset")}: {presetName}   " +
                   $"{T("预设地图", "Preset map")}: {presetMap}   VAC: {vac}";
        if (running is not null && running.Id != selected?.Id)
        {
            var label = _server.State == ServerState.RecoveryRequired ? T("待恢复实例", "Recovering") : T("运行实例", "Running");
            head = $"{label}: {running.Definition?.Name ?? running.Id.ToString("N")[..8]}   " + head;
        }
        // A stale snapshot must never be presented as the current state.
        var line = _server.ConnectionUnknown
            ? (_server.English ? "State: unknown — the local daemon is unreachable." : "状态：未知 · 无法连接本机宿主。")
            : _server.LifecycleSummaryLine;
        var detail = _server.DetailText;
        // The address comes from the instance; a live run on this instance wins, because the daemon resolved
        // it before launching and the stored definition may have changed since.
        var addresses = _server.Snapshot?.ConnectionAddresses ?? _connectionAddresses;
        var endpoint = displayed?.Definition is { } addressed
            ? "   · " + ServerStatusText.ConnectionAddressesLine(_options.English, addresses, addressed.GamePort)
            : "";
        var networkLine = _server.NetworkSummaryLine;
        if (networkLine.Length > 0 && ServerStatusText.NetworkHint(_server.NetworkReadiness, _options.English) is { } hint)
            networkLine += " · " + hint;
        _summary.Text = head + "\n" + line + endpoint + (detail.Length == 0 ? "" : "   · " + detail)
            + "\n" + networkLine;
        UpdateLogView();
        RefreshMapPage();
        RefreshPluginPage();
    }

    /// <summary>
    /// Renders the daemon's paged log. Following keeps the newest line visible; the operator can stop
    /// following to read history without the next poll yanking the view back to the end.
    /// </summary>
    private IReadOnlyList<ConsoleLogRow> _visibleLogRows = Array.Empty<ConsoleLogRow>();
    private readonly ConsoleLogViewport _logReading = new();
    private int _lastLogTop;
    private Guid? _visibleLogRun;
    private void UpdateLogView()
    {
        if (_content is null || _pageId != "logs") return;
        _content.WordWrap = false;
        _content.Height = Dim.Fill(6);
        if (_visibleLogRun != _server.RunId)
        {
            _visibleLogRun = _server.RunId; _visibleLogRows = Array.Empty<ConsoleLogRow>();
            _logReading.Reset(); _logFollow = true; _lastLogTop = _content.TopRow;
        }
        // A viewport move since our last projection belongs to the reader, not to incoming text.
        if (_content.TopRow != _lastLogTop)
        {
            _logFollow = _content.TopRow + _content.Bounds.Height >= _content.Lines;
            if (_logFollow) _logReading.Reset();
        }
        var rows = _server.ConsoleRows;
        var next = string.Join("\n", rows.Select(r => r.Text));
        if (_content.Text?.ToString() != next || !_visibleLogRows.Select(r => r.Id).SequenceEqual(rows.Select(r => r.Id)))
        {
            var top = _content.TopRow;
            var relative = _content.CurrentRow - top;
            if (!_logFollow) _logReading.Capture(_visibleLogRows, top);
            _visibleLogRows = rows;
            var restored = _logFollow ? 0 : _logReading.Restore(rows);
            _content.Text = next;
            if (_logFollow) _content.MoveEnd();
            else if (_content.Lines > 0)
            {
                _content.CursorPosition = new Point(0, Math.Clamp(restored + relative, 0, _content.Lines - 1));
                _content.TopRow = restored;
            }
        }
        _lastLogTop = _content.TopRow;
        if (_logReading.ReadingPositionLost)
            _consoleStatus.Text = T("较早日志已移出缓冲区；定位最早保留记录。End 跳到最新。", "Reading position evicted; earliest retained record shown. End: latest.");
        RefreshHotkeys();
    }

    /// <summary>Space pauses/resumes following; End jumps back to the newest line and re-follows.</summary>
    private void ToggleLogFollow()
    {
        _logFollow = !_logFollow;
        if (_logFollow) { _logReading.Reset(); _content?.MoveEnd(); _lastLogTop = _content?.TopRow ?? 0; }
        RefreshHotkeys();
    }

    private void JumpToNewestLog()
    {
        _logFollow = true;
        _logReading.Reset();
        _content?.MoveEnd();
        _lastLogTop = _content?.TopRow ?? 0;
        RefreshHotkeys();
    }

    /// <summary>Updates only the hotkey bar; reopening the page would reset the log caret.</summary>
    private void RefreshHotkeys()
    {
        _status.Text = TerminalLayout.Hotkeys(_options.English, Bounds.Width, _pageId == "logs", _logFollow);
        SetNeedsDisplay();
    }

    /// <summary>Keys that only mean something while the log page holds focus.</summary>
    private void OnContentKeyPress(KeyEventEventArgs e)
    {
        if (_pageId != "logs") return;
        switch (e.KeyEvent.Key)
        {
            case Key.Space: ToggleLogFollow(); break;
            case Key.End: JumpToNewestLog(); break;
            default: return;
        }
        e.Handled = true;
    }

    private async Task StopAndExitAsync()
    {
        await _server.StopAsync();
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (_server.Active && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(300);
            await _server.RefreshAsync();
        }
        if (!_server.Active)
        {
            Application.MainLoop.Invoke(() => Application.RequestStop());
            return;
        }
        Application.MainLoop.Invoke(() => MessageBox.ErrorQuery(72, 10, T("停服未确认", "Stop not confirmed"),
            _server.DetailText.Length != 0 ? _server.DetailText
                : T("停服请求已发送，但服务器仍在运行或状态未知。", "The stop request was sent, but the server is still running or its state is unknown."),
            T("返回", "Back")));
    }
}
