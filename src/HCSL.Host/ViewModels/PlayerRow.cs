using System.ComponentModel;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace HCSL.Host.ViewModels;

/// <summary>
/// One row of the console roster table. Values that change every poll raise PropertyChanged so the table
/// updates in place: rebuilding the row would reset cells (and their badge icons) on every refresh.
/// </summary>
public sealed class PlayerRow : INotifyPropertyChanged
{
    private string state, connected, identity;
    private string serverConnected;
    private int ping;
    private string? adminGroup;

    public PlayerRow(int userId, string name, bool isBot, string? steamId, string address, string state,
        int ping, string connected)
    {
        UserId = userId; Name = name; IsBot = isBot; SteamId = steamId; Address = address;
        this.state = state; this.ping = ping; this.connected = connected;
        serverConnected = connected;
        identity = isBot ? "BOT" : "游客";
    }

    public int UserId { get; }
    public string Name { get; }
    public bool IsBot { get; }
    public string? SteamId { get; }
    public string Address { get; }
    public string SteamIdText => SteamId ?? "-";
    /// <summary>Bots have no network address, so the column keeps the same placeholder as a missing SteamID.</summary>
    public string AddressText => IsBot || Address.Length == 0 ? "-" : Address;
    public string PingText => Ping > 0 ? Ping + " ms" : "-";
    public bool IsAdmin => adminGroup is { Length: > 0 };
    public string? AdminGroup => adminGroup;

    /// <summary>Badge text stays short; the concrete group is shown in the tooltip.</summary>
    public string Identity => IsBot ? "BOT" : IsAdmin ? "管理员" : "游客";

    public string IdentityTooltip => IsAdmin ? "管理组：" + adminGroup : IsBot ? "服务器 BOT" : "非管理员玩家";

    /// <summary>Icon and tone are part of the row so the table cell template stays declarative.</summary>
    public LucideIconKind IdentityIcon => IsBot ? LucideIconKind.Bot
        : IsAdmin ? LucideIconKind.UserRoundStar : LucideIconKind.User;

    public BadgeTone IdentityTone => IsAdmin ? BadgeTone.Info : BadgeTone.Neutral;

    /// <summary>Sort order of the identity column: admins first, then guests, then bots.</summary>
    public int IdentityRank => IsBot ? 2 : IsAdmin ? 0 : 1;

    public BadgeTone StateTone =>
        string.Equals(state, "active", StringComparison.OrdinalIgnoreCase) ? BadgeTone.Success : BadgeTone.Info;

    public string State { get => state; set => Set(ref state, value, nameof(State), nameof(StateTone)); }

    public string Connected { get => connected; set => Set(ref connected, value, nameof(Connected)); }

    public int Ping { get => ping; set => Set(ref ping, value, nameof(Ping), nameof(PingText)); }

    public void SetAdminGroup(string? group)
    {
        if (adminGroup == group) return;
        adminGroup = group;
        identity = Identity;
        Raise(nameof(AdminGroup));
        Raise(nameof(IsAdmin));
        Raise(nameof(Identity));
        Raise(nameof(IdentityTooltip));
        Raise(nameof(IdentityIcon));
        Raise(nameof(IdentityTone));
        Raise(nameof(IdentityRank));
    }

    public void Update(string state, int ping, string connected)
    {
        State = state;
        Ping = ping;
        // The local one-second tick must survive polls that carry the same server value, otherwise the
        // clock would jump forward and back every five seconds.
        if (connected != serverConnected)
        {
            serverConnected = connected;
            Connected = connected;
        }
    }

    /// <summary>Local tick between roster polls so the session time is not frozen for five seconds.</summary>
    public void AdvanceSecond()
    {
        var parts = Connected.Split(':');
        if (parts.Length is < 2 or > 3) return;
        var seconds = 0L;
        foreach (var part in parts)
        {
            if (!long.TryParse(part, out var value)) return;
            seconds = seconds * 60 + value;
        }
        seconds++;
        Connected = parts.Length == 3
            ? (seconds / 3600).ToString("0") + ":" + (seconds / 60 % 60).ToString("00") + ":" + (seconds % 60).ToString("00")
            : (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00");
    }

    private void Set<T>(ref T field, T value, params string[] names)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        foreach (var name in names) Raise(name);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}
