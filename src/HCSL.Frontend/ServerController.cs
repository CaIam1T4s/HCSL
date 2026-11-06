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
    public bool EffectiveVac => snapshot?.EffectiveVac == true;
    public bool MapLoaded => snapshot?.MapLoaded == true;
    /// <summary>Map the daemon observed for this instance, falling back to a scan of the returned log page.
    /// A daemon that restarted reports the recorded map of the last run instead.</summary>
    public string? Map => snapshot?.Map ?? ServerStatusText.MapFromLog(snapshot?.Lines) ?? ObservedMap;
    public bool CleanupConfirmed => snapshot?.CleanupConfirmed == true;
    public bool HistoryTruncated => snapshot?.HistoryTruncated == true;
    public int LogLineCount => snapshot?.Lines.Length ?? 0;
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

    public string SummaryLine => ServerStatusText.SummaryLine(english, State, RunId,
        Map, ConfiguredMap, MapLoaded, HistoryTruncated, LogLineCount, Pending, MapIsHistorical);

    private bool MapIsHistorical => snapshot?.MapHistorical == true
        || (snapshot?.Map is null && ServerStatusText.MapFromLog(snapshot?.Lines) is null && ObservedMap is not null);
    public string DetailText => ServerStatusText.DetailLine(Code.Length == 0 ? null : Code,
        string.IsNullOrWhiteSpace(snapshot?.Detail) ? null : snapshot!.Detail, error.Length == 0 ? null : error, english);
    public string LogText => snapshot is null ? "" : string.Join(Environment.NewLine, snapshot.Lines.Select(line => line.Text));

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

    public Task<bool> StartAsync(int timeoutSeconds = 120, bool autoDeploy = false) =>
        CanStart
            ? SendAsync(new(ServerAction.Start, attached, null, 0, timeoutSeconds, autoDeploy), true, "start", 4000)
            : Task.FromResult(false);

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
        Post(() => { pending = pendingName; error = ""; Notify(); }, current);
        try
        {
            var result = await server.SendAsync(command, includeWorkspace ? workspace : null, life.Token);
            Post(() => { Apply(result, ticket); error = ""; Notify(); }, current);
        }
        catch (OperationCanceledException) when (life.IsCancellationRequested) { }
        catch (Exception e)
        {
            // The daemon refused or the transport failed: keep the last reported state, surface the code.
            Post(() => { error = e.Message; Notify(); }, current);
        }
        finally
        {
            Interlocked.Exchange(ref requestInFlight, 0);
            Post(() => { pending = ""; Notify(); }, current);
        }
        return true;
    }

    private async Task PollAsync(int current, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && current == generation && !disposed)
            {
                await Task.Delay(snapshot?.Active == true ? activePoll : idlePoll, ct);
                if (Interlocked.CompareExchange(ref requestInFlight, 1, 0) != 0) continue;
                try
                {
                    var ticket = Interlocked.Increment(ref issued);
                    var result = await server.SendAsync(new(ServerAction.Status), null, ct);
                    Post(() => { Apply(result, ticket); Notify(); }, current);
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
        snapshot = value;
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
        Interlocked.Increment(ref generation);
        life.Cancel();
        life.Dispose();
        // No stop command: the daemon owns the server after this UI closes.
    }
}
