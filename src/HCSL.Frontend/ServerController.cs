using System.ComponentModel;
using HCSL.Contracts;
using HCSL.Core;

namespace HCSL.Frontend;

/// <summary>
/// Presentation state for the daemon-owned server lifecycle. Sends commands over IPC only;
/// it never launches processes and never invents a state the daemon did not report.
/// </summary>
public sealed class ServerController : INotifyPropertyChanged, IDisposable
{
    private readonly IDaemonClient client;
    private readonly ServerClient server;
    private readonly Action<Action> dispatch;
    private readonly IDaemonBootstrapper? bootstrapper;
    private readonly TimeSpan activePoll, idlePoll;
    private CancellationTokenSource life = new();
    private ServerSnapshot? snapshot;
    private readonly List<ServerLogLine> logLines = new();
    private long logCursor;
    private Guid? logRun;
    private bool logsVisible, logsTruncated;
    private const int LogCapacity = 2000;
    private static readonly TimeSpan LogPoll = TimeSpan.FromMilliseconds(250);
    private WorkspacePaths? workspace;
    private Guid attached;
    private string error = "", pending = "";
    private bool connected, connectionUnknown, english, disposed, connecting;
    private int generation, requestInFlight;
    private long issued, applied;

    public ServerController(IDaemonClient client, Action<Action> dispatch, IDaemonBootstrapper? bootstrapper = null,
        TimeSpan? activeInterval = null, TimeSpan? idleInterval = null)
    {
        this.client = client;
        this.dispatch = dispatch;
        this.bootstrapper = bootstrapper;
        activePoll = activeInterval ?? TimeSpan.FromMilliseconds(700);
        idlePoll = idleInterval ?? TimeSpan.FromSeconds(2);
        server = new ServerClient(client);
    }

    private int consoleBusy;
    private readonly List<string> consoleHistory = new();
    public IReadOnlyList<string> ConsoleHistory => consoleHistory;
    private bool historyEnabled = true;
    public bool ConsoleHistoryEnabled { get => historyEnabled; set { historyEnabled = value; if (!value) consoleHistory.Clear(); Notify(); } }
    public void ClearConsoleHistory() { consoleHistory.Clear(); Notify(); }
    private readonly List<(Guid Id, long Anchor, string Text)> inputMarkers = new();
    public IReadOnlyList<ConsoleLogRow> ConsoleRows
    {
        get
        {
            var first = logLines.FirstOrDefault()?.Sequence ?? 0;
            inputMarkers.RemoveAll(m => m.Anchor < first - 1);
            return logLines.Select(l => (Order: l.Sequence, Kind: 0, Row: new ConsoleLogRow("log:" + l.Sequence, l.Text)))
                .Concat(inputMarkers.Select(m => (Order: m.Anchor, Kind: 1, Row: new ConsoleLogRow("input:" + m.Id, "> " + m.Text))))
                .OrderBy(x => x.Order).ThenBy(x => x.Kind).Select(x => x.Row).TakeLast(2000).ToArray();
        }
    }
    public string ConsoleStatus { get; private set; } = "";
    /// <summary>The PTY exists once the run is active; commands such as host_workshop_collection may be
    /// needed before a map is published as ready. A map change still requires Running.</summary>
    public bool CanInputConsole => Connected && Active && State is ServerState.Starting or ServerState.LoadingMap or ServerState.Running;
    public bool CanSendConsole => CanInputConsole && consoleBusy == 0;
    public async Task<ServerConsoleReceipt?> SendConsoleAsync(Guid expectedRun, string text, bool confirmMutation = false)
    {
        string query;
        try { query = ServerConsoleRules.ValidateConfirmed(new(expectedRun, Guid.Empty, text, confirmMutation)); }
        catch (ServerException e) { ConsoleStatus = e.Code + ": " + e.Message; Notify(); return null; }
        if (!CanSendConsole || RunId != expectedRun || Interlocked.CompareExchange(ref consoleBusy, 1, 0) != 0)
        { ConsoleStatus = "HCSL-CONSOLE-STATE: 当前运行不可发送 / run changed or unavailable."; Notify(); return null; }
        var current = generation;
        var anchor = logCursor;
        ConsoleStatus = "发送中 / Sending"; Notify();
        var receipt = await server.SendConsoleAsync(new(expectedRun, Guid.NewGuid(), query, confirmMutation), life.Token);
        Interlocked.Exchange(ref consoleBusy, 0);
        Post(() =>
        {
            if (RunId != expectedRun) return;
            ConsoleStatus = receipt.Code + ": " + (receipt.Delivery switch
            {
                ConsoleDelivery.Written => English ? "Written to PTY; inspect logs, execution is not confirmed." : "已写入服务器输入通道；请查看输出，不代表执行成功。",
                ConsoleDelivery.Rejected => English ? "Rejected; not sent." : "已拒绝，未发送。",
                _ => English ? "Delivery unknown. Not retried automatically." : "结果未知；未自动重发。"
            });
            var display = receipt.DisplayText ?? "[命令显示未获确认 / command display unavailable]";
            inputMarkers.Add((receipt.CommandId, anchor, display));
            if (inputMarkers.Count > 200) inputMarkers.RemoveAt(0);
            if (receipt.Delivery != ConsoleDelivery.Rejected && ConsoleHistoryEnabled && display == query && !ServerConsoleRules.IsSensitive(query))
            {
                consoleHistory.Add(query);
                if (consoleHistory.Count > 50) consoleHistory.RemoveAt(0);
            }
            Notify();
        }, current);
        return receipt;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ServerSnapshot? Snapshot => snapshot;
    public ServerState State => snapshot?.State ?? ServerState.Idle;
    public bool Active => snapshot?.Active == true;
    public Guid? RunId => snapshot?.RunId;
    public Guid? InstanceId => snapshot?.InstanceId ?? (attached == Guid.Empty ? null : attached);
    /// <summary>
    /// The instance a snapshot may speak for as "the instance in charge". A failed or clean-stopped run
    /// keeps its identity for receipts and retries, but naming it in the header or re-selecting it in the
    /// list would hide the selection the operator just made and make a failed start look still live.
    /// </summary>
    public Guid? LiveInstanceId => snapshot is { State: not (ServerState.Idle or ServerState.Stopped or ServerState.Failed) } current
        && (current.Active || current.State == ServerState.RecoveryRequired)
        ? current.InstanceId : null;
    public Guid AttachedInstance => attached;
    public string Mode => snapshot?.Mode ?? "LoopbackLanValidation";
    /// <summary>Address the daemon resolved for this run (loopback until it reports otherwise).</summary>
    public string BindAddress => snapshot?.BindAddress ?? "127.0.0.1";
    public NetworkAccessMode Access => snapshot?.Access ?? NetworkAccessMode.Loopback;
    public bool EffectiveVac => snapshot?.EffectiveVac == true;
    public bool MapLoaded => snapshot?.MapLoaded == true;
    /// <summary>Map the daemon observed for this instance, falling back to a scan of the returned log page.
    /// A daemon that restarted reports the recorded map of the last run instead.</summary>
    public string? Map => State == ServerState.LoadingMap ? snapshot?.Map : snapshot?.Map ?? ServerStatusText.MapFromLog(snapshot?.Lines) ?? ObservedMap;
    public bool CleanupConfirmed => snapshot?.CleanupConfirmed == true;
    /// <summary>Ticket of the last snapshot applied to this controller; a monotonically rising view counter.</summary>
    public long Applied => applied;
    public bool HistoryTruncated => snapshot?.HistoryTruncated == true;
    public int LogLineCount => logLines.Count;
    /// <summary>Newest lines the daemon returned, oldest first. Bounded so a chatty server cannot grow the UI.</summary>
    public IReadOnlyList<ServerLogLine> LogLines => logLines;
    /// <summary>True when the daemon dropped history, or when the local ring dropped lines.</summary>
    public bool LogsTruncated => logsTruncated || HistoryTruncated || logLines.Count + inputMarkers.Count > LogCapacity;
    /// <summary>Next sequence to ask for; also the cursor the daemon validates against the run.</summary>
    public long LogCursor => logCursor;
    public ServerSeverity Severity => ServerStatusText.Severity(State);
    public bool Connected => connected;
    public bool ConnectionUnknown => connectionUnknown;
    public bool Connecting => connecting;
    public string Code => snapshot?.Code ?? "";
    public string ErrorText => error;
    public string? Pending => pending.Length == 0 ? null : pending;
    /// <summary>
    /// True only while an operator command is outstanding. Background polling also occupies the
    /// request slot, but that must not make the buttons flicker or talk the operator out of a start.
    /// </summary>
    public bool Busy => pending.Length != 0;
    public bool CanStart => connected && !Busy && attached != Guid.Empty
        && snapshot is null or { Active: false, State: not ServerState.RecoveryRequired };
    public bool CanStop => connected && !Busy && snapshot?.Active == true && snapshot.RunId is not null;
    public bool CanRecover => connected && !Busy && snapshot is { Active: false, State: ServerState.RecoveryRequired };
    public string? ConfiguredMap { get; set; }

    public bool English
    {
        get => english;
        set { if (english == value) return; english = value; Notify(); }
    }

    /// <summary>
    /// Map the daemon reported for this instance while it ran. It outlives the run (and a daemon restart
    /// that no longer knows the run) so a stopped server does not silently fall back to the preset map.
    /// </summary>
    public string? ObservedMap { get; set; }

    public ServerNetworkReadiness? NetworkReadiness => snapshot?.NetworkReadiness;
    private bool ShowNetworkReadiness => Connected && Active && State is ServerState.Starting or ServerState.LoadingMap or ServerState.Running;
    public string NetworkSummaryLine => ShowNetworkReadiness ? ServerStatusText.NetworkLine(NetworkReadiness, english) : "";
    public string LifecycleSummaryLine => ServerStatusText.SummaryLine(english, State, RunId,
        Map, ConfiguredMap, MapLoaded, LogsTruncated, LogLineCount, Pending, MapIsHistorical);
    public string SummaryLine => LifecycleSummaryLine + (NetworkSummaryLine.Length > 0 ? " · " + NetworkSummaryLine : "");

    private bool MapIsHistorical => State != ServerState.LoadingMap && (snapshot?.MapHistorical == true
        || (snapshot?.Map is null && ServerStatusText.MapFromLog(snapshot?.Lines) is null && ObservedMap is not null));
    public string DetailText => ServerStatusText.DetailLine(Code.Length == 0 ? null : Code,
        string.IsNullOrWhiteSpace(snapshot?.Detail) ? null : snapshot!.Detail, error.Length == 0 ? null : error, english)
        + (ShowNetworkReadiness && ServerStatusText.NetworkHint(NetworkReadiness, english) is { } hint ? " — " + hint : "")
        + (ServerMapClient.ProgressText(snapshot?.MapProgress, english) is { Length: > 0 } mapHint ? " — " + mapHint : "");
    public string LogText => string.Join(Environment.NewLine, logLines.Select(line => line.Text));

    public void Attach(Guid instanceId)
    {
        if (attached == instanceId) return;
        attached = instanceId;
        ObservedMap = null; // A remembered map belongs to the instance it was observed for.
        Notify();
        // The daemon answers status for the attached instance, so refresh now instead of waiting for the
        // next poll: reopening the panel then shows that instance's recorded last-run map immediately.
        if (connected) _ = RefreshAsync();
    }

    public async Task ConnectAsync()
    {
        if (disposed || Interlocked.CompareExchange(ref requestInFlight, 1, 0) != 0) return;
        var current = Interlocked.Increment(ref generation);
        life.Cancel();
        life.Dispose();
        life = new CancellationTokenSource();
        var ct = life.Token;
        Post(() => { connected = false; connecting = true; error = ""; Notify(); }, current);
        try
        {
            if (bootstrapper is not null) await bootstrapper.EnsureRunningAsync(ct);
            var hello = await client.SendAsync(new(LocalProtocol.Version, Guid.NewGuid(), RequestKind.Hello), ct);
            if (!hello.Success || hello.Defaults is null) throw new IOException(hello.Code + ": " + hello.Message);
            var ticket = Interlocked.Increment(ref issued);
            var status = await server.SendAsync(new(ServerAction.Status, attached), null, ct);
            Post(() =>
            {
                workspace = hello.Defaults;
                connected = true;
                connectionUnknown = false;
                Apply(status, ticket);
                Notify();
            }, current);
            _ = PollAsync(current, ct);
        }
        catch (DaemonStartupException ex)
        {
            Post(() => { connected = false; connectionUnknown = true; error = ex.Code + ": " + ex.Message; Notify(); }, current);
        }
        catch (Exception) { Disconnected(current); }
        finally
        {
            Interlocked.Exchange(ref requestInFlight, 0);
            Post(() => { connecting = false; Notify(); }, current);
        }
    }

    public Task<bool> RefreshAsync() => SendAsync(new(ServerAction.Status, attached), false, "", 500);

    /// <summary>
    /// Telling the controller whether the log page is visible keeps paging off the wire while the
    /// operator looks at other pages; turning it on fetches the backlog immediately.
    /// </summary>
    public void SetLogsVisible(bool visible)
    {
        if (logsVisible == visible) return;
        logsVisible = visible;
        if (visible && connected) _ = RefreshLogsAsync();
    }

    /// <summary>The daemon binds a log cursor to its run, so a run without a RunId cannot be paged.</summary>
    public Task<bool> RefreshLogsAsync()
    {
        if (!logsVisible || !connected || snapshot?.RunId is not { } run) return Task.FromResult(false);
        return SendAsync(new(ServerAction.Logs, snapshot.InstanceId ?? attached, run, logCursor), false, "", 500);
    }

    public Task<bool> StartAsync(int timeoutSeconds = 120, bool autoDeploy = false, bool allowLowMemory = false) =>
        CanStart
            ? SendAsync(new(ServerAction.Start, attached, null, 0, timeoutSeconds, autoDeploy, allowLowMemory), true, "start", 4000)
            : Task.FromResult(false);

    /// <summary>Deploy the selected preset without starting CS2; same admission as Start.</summary>
    public bool CanDeploy => CanStart;

    public Task<bool> DeployAsync() =>
        CanDeploy ? SendAsync(new(ServerAction.Deploy, attached), true, "deploy", 4000) : Task.FromResult(false);

    public Task<bool> StopAsync() =>
        CanStop && snapshot?.RunId is { } run
            ? SendAsync(new(ServerAction.Stop, snapshot.InstanceId ?? attached, run), false, "stop", 4000)
            : Task.FromResult(false);

    public Task<bool> RecoverAsync(int timeoutSeconds = 120) =>
        CanRecover
            ? SendAsync(new(ServerAction.Recover, attached, null, 0, timeoutSeconds), true, "recover", 4000)
            : Task.FromResult(false);

    /// <summary>
    /// Sends one request, waiting up to <paramref name="waitMs"/> for the single request slot instead of
    /// dropping the command when a background poll happens to hold it. Returns false only when the slot
    /// never freed, so callers can report the refusal instead of quitting silently.
    /// </summary>
    private async Task<bool> SendAsync(ServerCommand command, bool includeWorkspace, string pendingName, int waitMs = 0)
    {
        if (disposed) return false;
        var current = generation;
        var guard = life.Token;
        for (var waited = 0; Interlocked.CompareExchange(ref requestInFlight, 1, 0) != 0; waited += 50)
        {
            if (waited >= waitMs)
            {
                Post(() =>
                {
                    error = english ? "The daemon is still busy; try again in a moment." : "宿主仍在处理上一个请求，请稍后再试。";
                    Notify();
                }, current);
                return false;
            }
            try { await Task.Delay(50, guard).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        var ticket = Interlocked.Increment(ref issued);
        var accepted = true;
        Post(() => { pending = pendingName; error = ""; Notify(); }, current);
        try
        {
            var result = await server.SendAsync(command, includeWorkspace ? workspace : null, life.Token);
            Post(() => { Apply(result, ticket); error = ""; Notify(); }, current);
        }
        catch (OperationCanceledException) when (life.IsCancellationRequested) { accepted = false; }
        catch (Exception e)
        {
            // The daemon refused or the transport failed: keep the last reported state, surface the code,
            // and tell the caller the command never ran. Reporting success here made a refusal look like a
            // submitted start, so the operator only saw the old failure on a later attempt.
            accepted = false;
            Post(() => { error = e.Message; Notify(); }, current);
        }
        finally
        {
            Interlocked.Exchange(ref requestInFlight, 0);
            Post(() => { pending = ""; Notify(); }, current);
        }
        return accepted;
    }

    private async Task PollAsync(int current, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && current == generation && !disposed)
            {
                // Reading the log wants a tighter cadence than the header: a 700 ms poll plus one page
                // arrives in visible chunks. The cursor still keeps every page incremental.
                var wait = snapshot?.Active == true ? activePoll : idlePoll;
                if (logsVisible && wait > LogPoll) wait = LogPoll;
                await Task.Delay(wait, ct);
                if (Interlocked.CompareExchange(ref requestInFlight, 1, 0) != 0) continue;
                try
                {
                    var ticket = Interlocked.Increment(ref issued);
                    var result = await server.SendAsync(new(ServerAction.Status), null, ct);
                    Post(() =>
                    {
                        Apply(result, ticket);
                        if (error.Contains("Log cursor belongs to another run.", StringComparison.Ordinal)) error = "";
                        Notify();
                    }, current);
                    // Page the log only while the operator is looking at it; the cursor keeps it incremental.
                    if (logsVisible && result.RunId is { } run && !ct.IsCancellationRequested)
                    {
                        var pageTicket = Interlocked.Increment(ref issued);
                        var page = await server.SendAsync(new(ServerAction.Logs, result.InstanceId ?? attached, run, run == logRun ? logCursor : 0), null, ct);
                        Post(() => { Apply(page, pageTicket); Notify(); }, current);
                    }
                }
                finally { Interlocked.Exchange(ref requestInFlight, 0); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { Disconnected(current); }
    }

    private void Disconnected(int current) => Post(() =>
    {
        connected = false;
        connectionUnknown = true;
        error = english ? "Cannot reach the local daemon; server state is unknown." : "无法连接本机宿主，服务器状态未知。";
        Notify();
    }, current);

    private void Apply(ServerSnapshot value, long ticket)
    {
        if (ticket < applied) return;
        applied = ticket;
        if (snapshot?.RunId != value.RunId) { consoleHistory.Clear(); inputMarkers.Clear(); ConsoleStatus = ""; }
        snapshot = value;
        AppendLogs(value);
    }

    /// <summary>
    /// Merges the returned page into the local ring. Lines are appended strictly by sequence, a new run
    /// clears the ring, and the cursor only ever moves forward so a late page cannot duplicate or rewind.
    /// </summary>
    private void AppendLogs(ServerSnapshot value)
    {
        if (value.RunId != logRun)
        {
            logRun = value.RunId;
            logLines.Clear();
            logCursor = 0;
            logsTruncated = false;
        }
        if (value.HistoryTruncated) logsTruncated = true;
        foreach (var line in value.Lines)
        {
            if (line.Sequence <= logCursor) continue;
            logLines.Add(line);
            logCursor = line.Sequence;
        }
        if (logLines.Count > LogCapacity)
        {
            logLines.RemoveRange(0, logLines.Count - LogCapacity);
            logsTruncated = true;
        }
    }

    private void Post(Action action, int current)
    {
        if (disposed) return;
        dispatch(() => { if (!disposed && current == generation) action(); });
    }

    private void Notify() => PropertyChanged?.Invoke(this, new(null));

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        consoleHistory.Clear(); inputMarkers.Clear(); ConsoleStatus = "";
        Interlocked.Increment(ref generation);
        life.Cancel();
        life.Dispose();
        // No stop command: the daemon owns the server after this UI closes.
    }
}
