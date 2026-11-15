using System.ComponentModel;
using HCSL.Core;
namespace HCSL.Frontend;

/// <summary>Startup baseline state for the GUI/TUI. Disk access happens only in the daemon.</summary>
public sealed class BaselineController(BaselineClient client) : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private readonly CancellationTokenSource lifetime = new();
    private bool busy;
    private string status = "";
    private BaselineResult? result;

    public bool Busy { get => busy; private set { if (busy == value) return; busy = value; Notify(); Notify(nameof(CanRefresh)); } }
    /// <summary>Refresh is allowed unless another baseline request is running; the daemon enforces the server gate.</summary>
    public bool CanRefresh => !busy;
    public string Status { get => status; private set { if (status == value) return; status = value; Notify(); } }
    public string StatusText => result is null
        ? Status
        : "安装 BuildId " + result.BuildId + " · 基线：" + (result.Source switch
        {
            "Local" => "本地" + (result.LocalBuildId is { Length: > 0 } local ? "（" + local + "）" : ""),
            "Embedded" => "内置",
            _ => "缺失，需刷新"
        });
    public string? InstalledBuildId => result?.BuildId;
    public bool NeedsRefresh => result is { Source: "None" };

    private void Notify([System.Runtime.CompilerServices.CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new(name));

    public async Task RefreshStatusAsync(CancellationToken ct = default)
    {
        if (busy) return;
        Busy = true;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            result = await client.StatusAsync(linked.Token);
            Status = "";
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        { Status = e.Message; result = null; }
        finally
        {
            Busy = false; Notify(nameof(StatusText)); Notify(nameof(NeedsRefresh)); Notify(nameof(InstalledBuildId));
        }
    }

    /// <summary>Writes the library-local baseline after re-verifying installed files; failures keep their code.</summary>
    public async Task<bool> RefreshBaselineAsync(CancellationToken ct = default)
    {
        if (busy) return false;
        Busy = true;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
            result = await client.RefreshAsync(linked.Token);
            Status = "基线已刷新：" + result.Source;
            return true;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        { Status = e.Message; return false; }
        finally
        {
            Busy = false; Notify(nameof(StatusText)); Notify(nameof(NeedsRefresh)); Notify(nameof(InstalledBuildId));
        }
    }

    public void Dispose() => lifetime.Cancel();
}
