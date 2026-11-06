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
            new("network", T("联机组网", "Network"), ["network", "network-settings", "invites"]),
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
            new("network-settings", T("连接设置", "Connection"), T("网络配置待接入。", "Network configuration is not connected.")),
            new("invites", T("邀请", "Invitations"), T("尚未生成邀请。", "No invitations generated.")),
            new("paths", T("路径", "Paths"), T("路径设置待接入。", "Path settings are not connected.")),
            new("diagnostics", T("诊断", "Diagnostics"), T("诊断导出待接入。", "Diagnostic export is not connected.")),
            new("about", T("关于", "About"), T("HCSL TUI 开发预览，业务宿主尚未接入。", "HCSL TUI development preview. Host is not connected.")),
            new("home", T("首页", "Home"), T("暂无服务器实例。先到实例页创建或导入实例。\n\n独立宿主尚未接入，当前无法检测服务器状态。", "No server instances. Create or import an instance on the Instances page.\n\nThe independent host is not connected; server state is unavailable.")),
            new("instances", T("实例", "Instances"), T("实例列表：空\n创建 / 导入 / 选择：待接入\n\n实例只保存配置、插件和数据，共享最新 CS2 服务端。", "Instance list: empty\nCreate / import / select: not connected\n\nInstances store configuration, plugins and data and share the latest CS2 server.")),
            new("downloads", T("下载", "Downloads"), T("全局服务端：未检测\n任务队列：空\n\nSteamCMD 安装、更新、校验与插件下载待接入。", "Shared installation: not checked\nTask queue: empty\n\nSteamCMD installation, updates, validation and plugin downloads are not connected.")),
            new("plugins", T("插件", "Plugins"), T("未选择实例与配置预设。\n\n插件安装、配置、依赖和 VAC 兼容要求待接入。", "No instance or preset selected.\n\nPlugin installation, configuration, dependencies and VAC requirements are not connected.")),
            new("presets", T("配置预设", "Presets"), T("未选择实例与配置预设。\n\n预设属于实例，隔离插件、CFG、地图、\n授权、启动设置和受管数据。\n同一实例一次运行一套预设，切换前需停服。\n\n管理功能尚未接入，下方入口暂不可用。", "No instance or configuration preset selected.\n\nPresets belong to an instance and isolate plugins, cfg,\nmaps, access, launch settings and managed data.\nOne preset runs per instance; stop before switching.\n\nManagement is not connected. Actions are unavailable.")),
            new("maps", T("地图", "Maps"), T("未选择地图。\n内置 / 本地 / 创意工坊地图与地图轮换待接入。", "No map selected.\nBuilt-in / local / Workshop maps and rotation are not connected.")),
            new("admins", T("管理员", "Admins"), T("暂无授权信息。\n\n入服密码、参赛授权与管理员权限独立。\n未授权只能旁观；按 SteamID 授予管理权限。", "No access information.\n\nJoin password, play access and administrator rights are independent.\nUnapproved players are spectators; grant admin rights by SteamID.")),
            new("launch", T("启动设置", "Launch"), T("未选择实例。\n\n启动参数、自定义 cfg、启动地图与 VAC 偏好待接入。", "No instance selected.\n\nLaunch arguments, custom cfg, starting map and VAC preference are not connected.")),
            new("network", T("组网", "Network"), T("EasyTier：未接入\n虚拟地址 / 邀请：无", "EasyTier: not connected\nVirtual address / invitation: none")),
            new("settings", T("语言", "Language"), T("语言仅在当前会话生效。\n固定使用深色配色和 Unicode 字符，语言只替换文字。\n\nSteam 配对登录尚未接入，当前不生成网址或代码。", "Language applies to this session only.\nFixed dark palette and Unicode characters; language changes text only.\n\nSteam pairing is not connected; no URL or code is generated.")),
            new("logs", T("日志", "Logs"), T("暂无日志。\n筛选、搜索、自动滚动与脱敏导出待接入。", "No logs.\nFiltering, search, follow and redacted export are not connected.")),
            new("manage", T("管理", "Manage"), T("宿主：未连接\n服务器状态：未知\n\n玩家、换图、命令、停止与重启待接入。", "Host: disconnected\nServer state: unknown\n\nPlayers, map changes, commands, stop and restart are not connected."))
        ];
    }
}
