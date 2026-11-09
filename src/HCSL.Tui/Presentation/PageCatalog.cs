namespace HCSL.Tui.Presentation;

public sealed record PageDefinition(string Id, string Title, string Content);

public sealed record SectionDefinition(string Id, string Title, IReadOnlyList<string> Pages);

public static class PageCatalog
{
    public static IReadOnlyList<SectionDefinition> Sections(TerminalOptions options)
    {
        string T(string zh, string en) => options.Text(zh, en);
        return
        [
            new("home", T("首页", "Home"), ["home", "manage", "logs"]),
            new("instance", T("实例管理", "Instance"), ["basic", "launch", "plugins", "presets", "maps", "access", "admins"]),
            new("instances", T("实例列表", "Instances"), ["instances"]),
            new("downloads", T("下载中心", "Downloads"), ["downloads", "plugin-downloads", "tasks"]),
            new("settings", T("设置", "Settings"), ["settings", "paths", "diagnostics", "about"])
        ];
    }

    public static IReadOnlyList<PageDefinition> Create(TerminalOptions options)
    {
        string T(string zh, string en) => options.Text(zh, en);
        return
        [
            new("basic", T("基本信息", "General"), T("未选择实例。基本信息待接入。", "No instance selected. Details are not connected.")),
            new("access", T("参赛授权", "Play access"), T("授权服务未接入；未授权玩家只能旁观。", "Access service is not connected; unapproved players are spectators.")),
            new("plugin-downloads", T("插件下载", "Plugins"), T("插件目录尚未接入。", "Plugin catalog is not connected.")),
            new("tasks", T("任务队列", "Tasks"), T("暂无下载任务。", "No download tasks.")),
            new("paths", T("路径", "Paths"), T("在表单中设置下一次 SteamCMD 工具任务的工作区路径。", "Use the form to set workspace paths for the next SteamCMD task.")),
            new("diagnostics", T("诊断", "Diagnostics"), T("诊断导出待接入。", "Diagnostic export is not connected.")),
            new("about", T("关于", "About"), T("HCSL TUI 开发预览；下载中心可连接本机宿主进行 SteamCMD 探测。", "HCSL TUI preview. Downloads can connect to the local daemon for SteamCMD probes.")),
            new("home", T("首页", "Home"), T("暂无服务器实例。先到实例列表创建或选择实例。\n\n服务器状态与具体地图见窗口顶栏；F5 启动，F8 管理页提供启动/停服/恢复与日志入口。", "No server instances. Create or select an instance on the Instances page.\n\nLive state and map are in the header; F5 starts, and the F8 manage page stops, recovers and opens the log.")),
            new("instances", T("实例", "Instances"), T("实例列表：空\n可创建、编辑名称与端口、删除实例。本机连接 IP 自动识别、只读；访问范围由防火墙控制。\n导入与他人分享的实例包尚未接入。\n\n实例只保存配置、插件和数据，共享最新 CS2 服务端。", "Instance list: empty\nCreate, edit name/port and delete instances. Local IPs are detected and read-only; use a firewall for access control.\nImporting a shared instance package is not connected.\n\nInstances store configuration, plugins and data and share the latest CS2 server.")),
            new("downloads", T("下载", "Downloads"), T("全局服务端：未检测\n任务队列：空\n\nSteamCMD 工具准备与登录已接入；CS2 安装、更新、校验与插件下载待接入。", "Shared installation: not checked\nTask queue: empty\n\nSteamCMD tool/login is connected; CS2 installation, updates, validation and plugins are not connected.")),
            new("plugins", T("插件", "Plugins"), T("未选择实例与配置预设。\n\n选择实例后，使用下方按钮管理预设插件清单；包来自 library/plugin-packages，保存后需重新部署。\n依赖解析、远程下载、授权与 VAC 兼容检查尚未接入。", "No instance or preset selected.\n\nSelect an instance, then use the actions below to manage the preset plugin manifest. Packages come from library/plugin-packages; redeploy after saving.\nDependency resolution, remote download, authorization and VAC checks are not connected.")),
            new("presets", T("配置预设", "Presets"), T("未选择实例与配置预设。\n\n预设属于实例，隔离插件、CFG、地图、\n授权、启动设置和受管数据。\n同一实例一次运行一套预设，切换前需停服。\n\n选择实例后，可使用下方操作管理配置预设。", "No instance or configuration preset selected.\n\nPresets belong to an instance and isolate plugins, cfg,\nmaps, access, launch settings and managed data.\nOne preset runs per instance; stop before switching.\n\nSelect an instance to manage presets using the actions below.")),
            new("maps", T("地图", "Maps"), T("可保存当前预设的普通地图/工坊单图 ID，或确认向实际运行实例换图。全普通池生成地图组；单工坊合集池由合集原生轮换；混排未验证。", "Save a standard map/Workshop item ID in the current preset, or confirm a change on the live run. All-standard pools generate a map group; a single Workshop collection rotates natively; mixed pools are unverified.")),
            new("admins", T("管理员", "Admins"), T("暂无授权信息。\n\n入服密码、参赛授权与管理员权限独立。\n未授权只能旁观；按 SteamID 授予管理权限。", "No access information.\n\nJoin password, play access and administrator rights are independent.\nUnapproved players are spectators; grant admin rights by SteamID.")),
            new("launch", T("启动设置", "Launch"), T("未选择实例。\n\n可在配置预设页编辑地图、模式与 VAC 偏好。\nCFG 管理在预设页；自定义启动参数尚未接入。", "No instance selected.\n\nEdit map, mode and VAC preference on the Presets page.\nCFG management is on the Presets page; custom launch arguments are not connected.")),
            new("settings", T("语言", "Language"), T("语言仅在当前会话生效。\n固定使用深色配色和 Unicode 字符，语言只替换文字。\n\nSteam 配对登录尚未接入，当前不生成网址或代码。", "Language applies to this session only.\nFixed dark palette and Unicode characters; language changes text only.\n\nSteam pairing is not connected; no URL or code is generated.")),
            new("logs", T("日志", "Logs"), T("服务器启动后，这里显示宿主返回的最近日志（宿主已脱敏）。", "Once a server starts, the daemon's recent log lines appear here (already redacted by the daemon).")),
            new("manage", T("管理", "Manage"), T("服务器状态：见窗口顶栏（来自本机宿主）\n\n下方按钮对应宿主已实现的启动、停服与恢复检查。\n启动使用选中实例的当前预设；实例或预设改动后需重新部署。\n日志页 F2 输入通用 CS2 单行命令，支持 exec、alias、命令链与换图；回执不是执行成功。历史默认开启，仅保留在前端内存，可关闭/清空。", "Server state: see the header (reported by the local daemon)\n\nThe buttons below drive start, stop and recovery as implemented by the daemon.\nStart uses the selected instance's preset; redeploy after changing either.\nF2 on the log page accepts general single-line CS2 commands, exec, aliases, chains and map changes. Receipts are not execution success. History defaults on, is memory-only and can be disabled/cleared."))
        ];
    }
}
