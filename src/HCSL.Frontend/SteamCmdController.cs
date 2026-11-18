using System.ComponentModel;
using System.Runtime.CompilerServices;
using HCSL.Contracts;

namespace HCSL.Frontend;

/// <summary>Shared presentation state only: never launches a process or touches workspace files.</summary>
public sealed class SteamCmdController(IDaemonClient client, Action<Action> dispatch, IDaemonBootstrapper? bootstrapper = null) : INotifyPropertyChanged, IDisposable
{
    private CancellationTokenSource connection = new();
    private OperationSnapshot? snapshot;
    private HCSL.Core.Cs2ContentStatus? contentStatus;
    public bool? ContentInstalled => contentStatus?.Installed;
    public string? ContentBuildId => contentStatus?.BuildId;
    public bool ContentUpdateAvailable => contentStatus?.UpdateAvailable == true;
    private long revision = -1;
    private Guid daemonInstance;
    private int generation;
    private int requestInFlight;
    private bool disposed, connected, connectionUnknown, english, connecting;
    private string settings = "", data = "", library = "", executable = "", account = "", timeout = "600", installTimeout = "21600", error = "";
    private bool bootstrap = true;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string SettingsRoot { get => settings; set => Set(ref settings, value); }
    public string DataRoot { get => data; set => Set(ref data, value); }
    public string LibraryRoot { get => library; set { if (library == value) return; contentStatus = null; Set(ref library, value); Notify(); } }
    public string ExecutablePath { get => executable; set => Set(ref executable, value); }
    public string AccountName { get => account; set => Set(ref account, value); }
    public string TimeoutSeconds { get => timeout; set => Set(ref timeout, value); }
    public string InstallTimeoutSeconds { get => installTimeout; set => Set(ref installTimeout, value); }
    public string ServerDirectory => string.IsNullOrWhiteSpace(library) ? "" : Path.Combine(library, "server");
    public double ProgressPercent => snapshot?.Percent ?? 0;
    public bool ProgressIndeterminate => snapshot?.Percent is null;
    public bool InstallIfMissing { get => bootstrap; set => Set(ref bootstrap, value); }
    public bool English { get => english; set { english = value; Notify(); } }
    public bool Connected => connected;
    public bool Running => snapshot?.Running == true;
    public bool CanConnect => requestInFlight == 0;
    public bool CanEdit => requestInFlight == 0 && !Running;
    public bool CanStart => connected && CanEdit;
    public bool CanCancel => connected && Running && requestInFlight == 0;
    public bool CanSubmit => connected && Running && snapshot?.Challenge is not null && requestInFlight == 0;
    public bool NeedsInput => connected && snapshot?.Challenge is not null;
    public bool ShowProgress => connected && Running;
    public bool ShowDeterminateProgress => ShowProgress && !ProgressIndeterminate;
    public bool ShowIndeterminateProgress => ShowProgress && ProgressIndeterminate;
    public Guid? ChallengeId => snapshot?.Challenge?.Id;
    public OperationSnapshot? LastSnapshot => snapshot;
    /// <summary>Measured transfer state reported by the daemon; null until it reports one.</summary>
    public TransferSnapshot? Transfer => snapshot?.Transfer;
    /// <summary>Rate only while bytes are actually being downloaded; verification and applying report none.</summary>
    public string TransferSpeedText
    {
        get
        {
            if (Transfer is not { Phase: TransferPhase.Downloading, BytesPerSecond: { } rate } || rate <= 0) return "-";
            return FormatBytes((long)rate) + "/s";
        }
    }
    public string TransferBytesText => Transfer switch
    {
        { DoneBytes: { } done, TotalBytes: { } total } => FormatBytes(done) + " / " + FormatBytes(total),
        { DoneBytes: { } done } => FormatBytes(done),
        _ => "-"
    };
    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " KB",
        _ => bytes + " B"
    };
    private string T(string zh, string en) => english ? en : zh;
    public string ConnectionText => connecting ? T("正在连接或启动本机宿主", "Connecting or starting local daemon") : connected ? T("已连接本机宿主", "Local daemon connected")
        : connectionUnknown ? T("连接已断开；任务状态未知", "Disconnected; task state unknown")
        : T("未连接本机宿主", "Local daemon not connected");
    public string StageText => !connected && connectionUnknown ? T("状态未知", "Unknown")
        : StageName(snapshot?.Stage ?? ProbeStage.Idle) + (snapshot?.Percent is { } percent ? $" {percent:0.0}%" : "");
    public string Detail => error.Length != 0 ? error : snapshot?.Message ?? T("连接本机宿主后可准备并登录 SteamCMD。", "Connect to the local daemon to prepare and log into SteamCMD.");
    public string ChallengeText => snapshot?.Challenge?.Kind == ChallengeKind.Password
        ? T("请输入 Steam 密码（不保存、不回显）", "Enter Steam password (not stored or echoed)")
        : T("请输入 Steam Guard 验证码", "Enter Steam Guard code");
    public string LogText => snapshot is null ? "" : string.Join(Environment.NewLine,
        snapshot.Events.Select(e => StageName(e.Stage) + (e.Percent is { } percent ? $" {percent:0.0}%" : "") + " · " + e.Message));
    public string OperationText => snapshot?.Id?.ToString() ?? T("暂无任务", "No operation");

    public async Task ConnectAsync()
    {
        if (disposed || Interlocked.CompareExchange(ref requestInFlight, 1, 0) != 0) return;
        var current = Interlocked.Increment(ref generation);
        connection.Cancel();
        connection.Dispose();
        connection = new CancellationTokenSource();
        var ct = connection.Token;
        Post(() => { connected = false; connecting = true; error = ""; revision = -1; Notify(); }, current);
        try
        {
            if (bootstrapper is not null) await bootstrapper.EnsureRunningAsync(ct);
            var response = await client.SendAsync(Request(RequestKind.Hello), ct);
            if (!response.Success) throw new IOException(response.Code);
            Post(() =>
            {
                daemonInstance = response.InstanceId;
                connected = true;
                connectionUnknown = false;
                if (response.Defaults is { } defaults)
                {
                    if (settings.Length == 0) settings = defaults.SettingsRoot;
                    if (data.Length == 0) data = defaults.DataRoot;
                    if (library.Length == 0) library = defaults.LibraryRoot;
                }
                Apply(response.Snapshot);
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
    public async Task RefreshContentStatusAsync()
    {
        if (!connected || disposed) return;
        var current = generation;
        try
        {
            var response = await client.SendAsync(Request(RequestKind.ContentStatus, new(new(settings, data, library))), connection.Token);
            if (!response.Success || response.InstanceId != daemonInstance) throw new IOException(response.Code);
            var value = System.Text.Json.JsonSerializer.Deserialize<HCSL.Core.Cs2ContentStatus>(response.ContentStatusJson ?? "null");
            Post(() => { contentStatus = value; Notify(); }, current);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or System.Text.Json.JsonException)
        { Post(() => { contentStatus = null; Notify(); }, current); }
    }

    public Task StartAsync() => OptionsRequestAsync(RequestKind.StartProbe);
    public Task InstallAsync() => OptionsRequestAsync(RequestKind.InstallOrUpdate);
    public Task ValidateAsync() => OptionsRequestAsync(RequestKind.ValidateContent);
    public Task RecoverAsync() => OptionsRequestAsync(RequestKind.Recover);
    private Task OptionsRequestAsync(RequestKind kind)
    {
        if (!CanStart) return Task.CompletedTask;
        if (!int.TryParse(timeout, out var seconds) || seconds is < 1 or > 3600)
        {
            Post(() => { error = T("超时须为 1..3600 秒。", "Timeout must be 1..3600 seconds."); Notify(); }, generation);
            return Task.CompletedTask;
        }
        if (!int.TryParse(installTimeout, out var installSeconds) || installSeconds is < 1 or > 86400)
        {
            Post(() => { error = T("安装超时须为 1..86400 秒。", "Installation timeout must be 1..86400 seconds."); Notify(); }, generation);
            return Task.CompletedTask;
        }
        var options = new ProbeOptions(new(settings, data, library), Empty(executable), bootstrap, Empty(account), seconds, installSeconds);
        return SendActionAsync(Request(kind, options));
    }
    public Task CancelAsync() => CanCancel ? SendActionAsync(Request(RequestKind.Cancel, operation: snapshot!.Id)) : Task.CompletedTask;
    public Task SubmitAsync(string secret)
    {
        if (!CanSubmit) return Task.CompletedTask;
        return SendActionAsync(Request(RequestKind.SubmitInput, operation: snapshot!.Id, challenge: snapshot.Challenge!.Id, secret: secret));
    }
    private async Task SendActionAsync(LocalRequest request)
    {
        if (disposed || Interlocked.CompareExchange(ref requestInFlight, 1, 0) != 0) return;
        var current = generation;
        Post(Notify, current);
        try
        {
            var response = await client.SendAsync(request, connection.Token);
            if (response.InstanceId != daemonInstance) throw new IOException("Daemon restarted.");
            Post(() =>
            {
                if (!response.Success) error = response.Code + ": " + response.Message;
                else { error = ""; Apply(response.Snapshot); }
                Notify();
            }, current);
        }
        catch (Exception) { Disconnected(current); }
        finally { Interlocked.Exchange(ref requestInFlight, 0); Post(Notify, current); }
    }
    private async Task PollAsync(int current, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && current == generation && !disposed)
            {
                await Task.Delay(500, ct);
                var response = await client.SendAsync(Request(RequestKind.Status), ct);
                if (!response.Success || response.InstanceId != daemonInstance) throw new IOException("Status unavailable or daemon restarted.");
                Post(() => { Apply(response.Snapshot); Notify(); }, current);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { Disconnected(current); }
    }
    private void Disconnected(int current) => Post(() =>
    {
        connected = false;
        connectionUnknown = true;
        error = T("无法连接本机宿主。请重试连接，或检查随应用提供的宿主文件；界面断开不会取消已提交任务。", "Cannot reach HCSL.Daemon. Retry connection or check the bundled daemon; disconnecting does not cancel submitted tasks.");
        Notify();
    }, current);
    private void Apply(OperationSnapshot? value)
    {
        if (value is null || value.Revision < revision) return;
        revision = value.Revision;
        snapshot = value;
    }
    private void Post(Action action, int current)
    {
        if (disposed) return;
        dispatch(() => { if (!disposed && current == generation) action(); });
    }
    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static LocalRequest Request(RequestKind kind, ProbeOptions? options = null, Guid? operation = null,
        Guid? challenge = null, string? secret = null) => new(LocalProtocol.Version, Guid.NewGuid(), kind, options, operation, challenge, secret);
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
    private void Notify() => PropertyChanged?.Invoke(this, new(null));
    private string StageName(ProbeStage stage) => english ? stage.ToString() : stage switch
    {
        ProbeStage.Idle => "暂无任务", ProbeStage.Preparing => "准备中", ProbeStage.DownloadingTool => "下载 SteamCMD 工具",
        ProbeStage.Starting => "启动 SteamCMD", ProbeStage.SelfUpdating => "SteamCMD 自更新", ProbeStage.Ready => "控制台就绪",
        ProbeStage.Authenticating => "认证中", ProbeStage.WaitingForPassword => "等待密码", ProbeStage.WaitingForGuardCode => "等待验证码",
        ProbeStage.WaitingForMobileApproval => "等待手机确认", ProbeStage.Authenticated => "已认证", ProbeStage.Cancelling => "正在取消并清理",
        ProbeStage.DownloadingContent => "下载 CS2 内容", ProbeStage.ValidatingContent => "校验 CS2 内容", ProbeStage.InstallingContent => "应用 CS2 内容",
        ProbeStage.CheckingBaseline => "检查部署校验记录",
        ProbeStage.Succeeded => "已完成", ProbeStage.Failed => "失败", ProbeStage.Cancelled => "已取消", ProbeStage.TimedOut => "超时",
        ProbeStage.RecoveryRequired => "需要恢复检查", _ => stage.ToString()
    };
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Interlocked.Increment(ref generation);
        connection.Cancel();
        connection.Dispose();
        // Deliberately no Cancel request: the daemon owns the operation after this UI closes.
    }
}
