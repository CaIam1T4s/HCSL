using Avalonia.Controls;
using HCSL.Core;
using HCSL.Frontend;
using TKCode.Arc;
namespace HCSL.Host.Views;

public partial class MainWindow
{
    /// <summary>Framework admin list for the selected preset: read, page, search, grant/revoke, reload.</summary>
    public async Task EditAdminsAsync(HostServerController model, InstanceDefinition instance, PresetDefinition preset,
        string? grantSteamId = null, string? grantName = null)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "管理员 · " + instance.Name + " / " + preset.Name, FontSize = 20 });
        content.Children.Add(new TextBlock
        {
            Text = "名单直接读写当前框架自己的文件（CounterStrikeSharp / Source2Mod / SwiftlyS2 按预设启用的框架自动选择）。"
                + "写入保持原有注释与格式，带哈希冲突检测与备份；SwiftlyS2 由原生代码在启动时读取，需要重启服务器才生效。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var source = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var groups = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var list = new Select();
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var query = new Input { PlaceholderText = "按 SteamID / 名字 / 组搜索" };
        content.Children.Add(source);
        content.Children.Add(groups);
        content.Children.Add(query);
        content.Children.Add(list);
        content.Children.Add(status);

        AdminListSnapshot? snapshot = null;
        string? error = null;
        var offset = 0;
        string? activeQuery = null;
        while (true)
        {
            if (snapshot is null && error is null)
            {
                try { snapshot = (await model.AdminsAsync(offset, 0, activeQuery)).Snapshot; }
                catch (Exception e) { error = Describe(e); }
            }
            source.Text = (error is not null) ? error
                : snapshot is null ? "尚未读取"
                : $"来源：{snapshot.SourceName}（{snapshot.SourceId}）· 生效方式：{ReloadText(snapshot.Capabilities)}"
                + $" · 条目 {snapshot.Total}（本页 {snapshot.Entries.Length}，偏移 {snapshot.Offset}）"
                + $"{(snapshot.HasMore ? " · 还有更多" : "")}";
            groups.Text = snapshot is null ? "" : snapshot.Groups.Length == 0
                ? "未定义组：先「初始化组」，再按组授权；也可直接给权限名（如 admin.kick）。"
                : "组：" + string.Join(" · ", snapshot.Groups.Select(g => $"{g.Name}（{g.Flags.Length} 项{(g.Immunity is { } i ? "，免疫 " + i : "")}）"));
            list.ItemsSource = (snapshot?.Entries ?? []).Select(Label).ToArray();
            if (list.SelectedIndex < 0 && list.ItemCount > 0) list.SelectedIndex = 0;
            status.Text = "";
            var choice = await ShowOverlayAsync(content, ["关闭", "刷新", "搜索", "上一页", "下一页", "授予 / 修改", "移除所选", "初始化组", "重载"]);
            if (choice <= 0) return;
            switch (choice)
            {
                case 1: snapshot = null; error = null; continue;
                case 2: activeQuery = string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim(); offset = 0; snapshot = null; error = null; continue;
                case 3: offset = Math.Max(0, offset - (snapshot?.Limit ?? AdminRules.DefaultPageSize)); snapshot = null; continue;
                case 4: offset += snapshot?.Limit ?? AdminRules.DefaultPageSize; snapshot = null; continue;
                case 8:
                    try
                    {
                        var reloaded = await model.ReloadAdminsAsync();
                        status.Text = reloaded.Reload ?? "该来源没有重载命令。";
                        snapshot = reloaded.Snapshot;
                    }
                    catch (Exception e) { status.Text = Describe(e); }
                    continue;
                case 7:
                    try
                    {
                        var result = await model.ApplyAdminAsync(new(AdminAction.Apply, instance.Id, preset.Id,
                            snapshot?.Hash, [], InitGroups: true));
                        status.Text = "已写入初始化内容。" + (result.Reload is { } r ? " " + r : "");
                        snapshot = result.Snapshot;
                    }
                    catch (Exception e) { status.Text = Describe(e); }
                    continue;
            }
            if (snapshot is null) { status.Text = "先刷新名单。"; continue; }
            if (choice == 6)
            {
                var entry = list.SelectedIndex >= 0 && list.SelectedIndex < snapshot.Entries.Length ? snapshot.Entries[list.SelectedIndex] : null;
                if (entry is null) { status.Text = "先在列表中选择一名管理员。"; continue; }
                if (await ChooseAsync("取消管理员", $"移除 {entry.DisplayName ?? entry.SteamId64}（{entry.SteamId64}）的整条记录？\n组定义与其他人不受影响。", "取消", "移除") != 1) continue;
                try
                {
                    var result = await model.ApplyAdminAsync(new(AdminAction.Apply, instance.Id, preset.Id, snapshot.Hash,
                        [new(AdminMutationKind.Remove, entry.SteamId64)]));
                    status.Text = "已移除。" + (result.Reload is { } r ? " " + r : "");
                    snapshot = result.Snapshot;
                }
                catch (Exception e) { status.Text = Describe(e); }
                continue;
            }
            // Grant: the framework key matters. CSSharp keys by display name (SteamID goes into "identity"),
            // the others key by SteamID itself, so the form only offers the fields that source can hold.
            var existingEntry = snapshot.Entries.FirstOrDefault(e => grantSteamId is { Length: > 0 } && e.SteamId64 == grantSteamId);
            var steam = new Input { Text = grantSteamId ?? existingEntry?.SteamId64 ?? "", PlaceholderText = "7656… / STEAM_0:0:… / [U:1:…]" };
            var nameInput = new Input
            {
                Text = existingEntry?.DisplayName ?? grantName ?? "",
                PlaceholderText = "显示名（这是 admins.json 的 key，可随时改）"
            };
            var groupInput = new Input
            {
                Text = existingEntry is { Groups.Length: > 0 } ? string.Join(",", existingEntry.Groups) : snapshot.Groups.FirstOrDefault()?.Name ?? "",
                PlaceholderText = "组，逗号分隔"
            };
            var flagInput = new Input
            {
                Text = existingEntry is { Flags.Length: > 0 } ? string.Join(",", existingEntry.Flags) : "",
                PlaceholderText = "可选：权限列表，逗号分隔"
            };
            var form = new StackPanel { Spacing = 6 };
            var byName = snapshot.Capabilities?.Key != AdminKeyKind.SteamId64;
            form.Children.Add(new TextBlock { Text = "SteamID（身份，写入 identity / 作为 key）" });
            form.Children.Add(steam);
            if (byName)
            {
                form.Children.Add(new TextBlock { Text = "显示名" });
                form.Children.Add(nameInput);
            }
            form.Children.Add(new TextBlock { Text = "组（可用组：" + (snapshot.Groups.Length == 0 ? "无，先「初始化组」" : string.Join(" / ", snapshot.Groups.Select(g => g.Name))) + "）" });
            form.Children.Add(groupInput);
            form.Children.Add(new TextBlock { Text = "额外权限（可选；SwiftlyS2 只记组成员）" });
            form.Children.Add(flagInput);
            form.Children.Add(new TextBlock
            {
                Text = existingEntry is null ? "新条目：确认后会写入名单并发送重载命令。" : "已存在：确认后更新该条目的组/权限（显示名改了就会重命名 key）。",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            if (await ShowOverlayAsync(form, ["取消", "写入"]) != 1) continue;
            var steamId = (steam.Text ?? "").Trim();
            if (steamId.Length == 0) { status.Text = "需要 SteamID。"; continue; }
            var groupList = Split(groupInput.Text);
            var flagList = Split(flagInput.Text);
            if (groupList.Length == 0 && flagList.Length == 0) { status.Text = "至少给一个组或权限。"; continue; }
            if (flagList.Any(f => f is "@css/root" or "@css/rcon" or "*")
                && await ChooseAsync("确认授予最高权限", "该权限等同完整控制台访问。确认继续？", "取消", "确认") != 1) continue;
            var displayName = byName ? (nameInput.Text ?? "").Trim() : null;
            try
            {
                var result = await model.ApplyAdminAsync(new(AdminAction.Apply, instance.Id, preset.Id, snapshot.Hash,
                    [new(AdminMutationKind.Upsert, steamId, string.IsNullOrEmpty(displayName) ? null : displayName, groupList, flagList)]));
                status.Text = "已写入：" + string.Join(", ", result.Applied) + (result.Reload is { } r ? " · " + r : "")
                    + (byName && string.IsNullOrEmpty(displayName) ? "\n未填显示名，key 用了 SteamID；下次写入填上名称即可重命名。" : "");
                snapshot = result.Snapshot;
                grantSteamId = null;
                grantName = null;
            }
            catch (Exception e) { status.Text = Describe(e); }
        }

        static string[] Split(string? text) =>
            (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        static string ReloadText(AdminSourceCapabilities? capabilities) => capabilities?.Reload switch
        {
            AdminReloadKind.Command => "命令 " + capabilities.ReloadCommand,
            AdminReloadKind.HotReload => "框架热重载（需实测）",
            AdminReloadKind.Restart => "重启服务器",
            _ => "未知"
        };
        static string Label(AdminEntry entry) =>
            $"{entry.DisplayName ?? entry.SteamId64} · {entry.SteamId64} · 组：{string.Join(",", entry.Groups)}"
            + (entry.Flags.Length > 0 ? " · 权限：" + string.Join(",", entry.Flags) : "")
            + (entry.Problems.Length > 0 ? " · ⚠ " + string.Join(",", entry.Problems) : "");
        static string Describe(Exception e) => e is HCSL.Core.InstanceException failure ? failure.Code + ": " + failure.Message : e.Message;
    }
}
