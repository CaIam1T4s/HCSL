using Avalonia.Controls;
using HCSL.Core;
using HCSL.Frontend;
using TKCode.Arc;
namespace HCSL.Host.Views;

public partial class MainWindow
{
    /// <summary>Online roster: RCON-backed, read-only until the operator acts on one player.</summary>
    public async Task EditPlayersAsync(HostServerController model, InstanceDefinition instance, PresetDefinition preset)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "在线玩家 · " + instance.Name, FontSize = 20 });
        content.Children.Add(new TextBlock
        {
            Text = "名单由宿主通过 RCON 向运行中的服务器查询，不写入服务器日志。SteamID 来自宿主从日志行解析的身份表；刚连上的玩家可能短暂显示未知。BOT 不允许从这里踢出。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var hint = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var list = new Select();
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        content.Children.Add(hint);
        content.Children.Add(list);
        content.Children.Add(status);

        PlayerRoster? roster = null;
        string? error = null;
        while (true)
        {
            if (roster is null && error is null)
            {
                try { roster = await model.PlayersAsync(); }
                catch (Exception e) { error = Describe(e); }
            }
            hint.Text = (error is not null) ? error
                : roster is null ? "尚未查询"
                : $"{roster.Humans} 名玩家 · {roster.Bots} 个机器人 · 查询于 {roster.QueriedAt.ToLocalTime():HH:mm:ss}";
            list.ItemsSource = (roster?.Players ?? []).Select(Label).ToArray();
            if (list.SelectedIndex < 0 && list.ItemCount > 0) list.SelectedIndex = 0;
            status.Text = "";
            var choice = await ShowOverlayAsync(content, ["关闭", "刷新", "踢出", "授予管理员"]);
            if (choice <= 0) return;
            if (choice == 1) { roster = null; error = null; continue; }
            var player = roster is null || list.SelectedIndex < 0 || list.SelectedIndex >= roster.Players.Length
                ? null : roster.Players[list.SelectedIndex];
            if (player is null) { status.Text = "先选择一名玩家。"; continue; }
            if (choice == 2)
            {
                if (player.IsBot) { status.Text = "BOT 没有 SteamID，不从这里踢出。"; continue; }
                var runId = model.Server.Snapshot?.RunId;
                if (runId is not { } run) { status.Text = "运行 ID 未知：先刷新状态。"; continue; }
                if (await ChooseAsync("踢出玩家", $"将断开 {player.Name}（{player.SteamId64 ?? "SteamID 未知"}）。\n这是即时操作，不影响预设或管理员名单。", "取消", "踢出") != 1) continue;
                try
                {
                    var result = await model.KickAsync(instance.Id, run, player.UserId, player.SteamId64, "");
                    status.Text = result.Written
                        ? "已发送踢出命令：" + result.Command
                        : result.Code + "：命令未被接受，玩家仍在。";
                    roster = null;
                }
                catch (Exception e) { status.Text = Describe(e); }
                continue;
            }
            await EditAdminsAsync(model, instance, preset, player.SteamId64);
            roster = null;
        }

        static string Describe(Exception e) => e is HCSL.Core.InstanceException failure ? failure.Code + ": " + failure.Message : e.Message;
        static string Label(RosterPlayer player) =>
            $"{player.Name} · {player.SteamId64 ?? "SteamID 未知"} · {player.Address} · {(player.IsBot ? "BOT" : player.State)}";
    }
}
