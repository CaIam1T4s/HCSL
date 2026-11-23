using System.Windows.Input;
using HCSL.Frontend;
using HCSL.Host.Services;

namespace HCSL.Host.ViewModels;

public sealed class NavigationItem(string id, string label, string description) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Description { get; } = description;
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; Notify(); } }
}

/// <summary>One page of the instance scope. Entering 实例管理 swaps the whole sidebar for these sections.</summary>
public sealed class InstanceSection(string id, string label) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; Notify(); } }
}

public sealed class ShellViewModel : ObservableObject
{
    public SteamCmdController SteamCmd { get; }
    public HostServerController Servers { get; }
    public BaselineController Baseline { get; }
    public IReadOnlyList<NavigationItem> Pages { get; } =
    [
        new("home", "首页", "选择实例，准备启动服务器。"),
        new("instance", "实例管理", "管理当前实例的配置、插件、地图与玩家授权。"),
        new("instances", "实例列表", "实例只保存配置与插件，共用全局最新版 CS2 服务端。"),
        new("downloads", "下载中心", "统一管理 CS2 服务端更新与插件下载。"),
        new("settings", "设置", "调整外观与应用偏好。")
    ];    public IReadOnlyList<InstanceSection> InstanceSections { get; } =
    [
        new("overview", "概览"),
        new("presets", "预设"),
        new("pools", "地图池")
    ];

    /// <summary>Sections of the preset scope: everything that belongs to one preset rather than to the instance.</summary>
    public IReadOnlyList<InstanceSection> PresetSections { get; } =
    [
        new("cfg", "CFG"),
        new("content", "框架与插件"),
        new("pools", "地图池"),
        new("admins", "管理员")
    ];

    private InstanceSection? _selectedInstanceSection;
    private InstanceSection? _selectedPresetSection;
    private string? returnPageId;
    private Guid? managedPresetId;

    /// <summary>The section shown while 实例管理 is open; the sidebar selects it.</summary>
    public InstanceSection SelectedInstanceSection
    {
        get => _selectedInstanceSection ??= InstanceSections[0];
        private set
        {
            if (ReferenceEquals(SelectedInstanceSection, value)) return;
            SelectedInstanceSection.IsSelected = false;
            _selectedInstanceSection = value;
            value.IsSelected = true;
            Notify(nameof(SelectedInstanceSection));
            Notify(nameof(IsInstanceOverview));
            Notify(nameof(IsInstancePresets));
            Notify(nameof(IsInstancePools));
            Notify(nameof(IsInstanceScopeVisible));
        }
    }

    /// <summary>The preset being managed inside the preset scope; null means the instance scope is showing.</summary>
    public Guid? ManagedPresetId => managedPresetId;
    public bool IsPresetScope => IsInstance && managedPresetId is { } id &&
        Servers.Selected?.Entry.Presets.Any(p => p.Id == id) == true;
    public bool IsInstanceScopeVisible => IsInstance && !IsPresetScope;
    public string ManagedPresetName => managedPresetId is { } id
        ? Servers.Selected?.Entry.Presets.FirstOrDefault(p => p.Id == id)?.Name ?? ""
        : "";

    public InstanceSection SelectedPresetSection
    {
        get => _selectedPresetSection ??= PresetSections[0];
        private set
        {
            if (ReferenceEquals(SelectedPresetSection, value)) return;
            SelectedPresetSection.IsSelected = false;
            _selectedPresetSection = value;
            value.IsSelected = true;
            Notify(nameof(SelectedPresetSection));
            Notify(nameof(IsPresetCfg));
            Notify(nameof(IsPresetContent));
            Notify(nameof(IsPresetPools));
            Notify(nameof(IsPresetAdmins));
        }
    }

    public bool IsPresetCfg => IsPresetScope && SelectedPresetSection.Id == "cfg";
    public bool IsPresetContent => IsPresetScope && SelectedPresetSection.Id == "content";
    public bool IsPresetPools => IsPresetScope && SelectedPresetSection.Id == "pools";
    public bool IsPresetAdmins => IsPresetScope && SelectedPresetSection.Id == "admins";

    public void SelectPresetSection(string id)
    {
        if (PresetSections.FirstOrDefault(section => section.Id == id) is { } section)
            SelectedPresetSection = section;
    }

    /// <summary>Opens one preset of the managed instance; the back arrow returns to the instance scope.</summary>
    public void OpenPresetScope(Guid presetId)
    {
        if (!IsInstance) return;
        managedPresetId = presetId;
        Notify(nameof(ManagedPresetId));
        Notify(nameof(IsPresetScope));
        Notify(nameof(IsInstanceScopeVisible));
        Notify(nameof(ManagedPresetName));
        SelectedPresetSection = PresetSections[0];
        NotifyPresetSections();
    }

    public void ExitPresetScope()
    {
        managedPresetId = null;
        Notify(nameof(ManagedPresetId));
        Notify(nameof(IsPresetScope));
        Notify(nameof(IsInstanceScopeVisible));
        Notify(nameof(ManagedPresetName));
        SelectedInstanceSection = InstanceSections.First(section => section.Id == "presets");
        NotifyPresetSections();
    }
    private void NotifyPresetSections()
    {
        Notify(nameof(IsPresetCfg));
        Notify(nameof(IsPresetContent));
        Notify(nameof(IsPresetPools));
        Notify(nameof(IsPresetAdmins));
    }

    public bool IsInstanceOverview => IsInstance && SelectedInstanceSection.Id == "overview";
    public bool IsInstancePresets => IsInstance && SelectedInstanceSection.Id == "presets";
    public bool IsInstancePools => IsInstance && SelectedInstanceSection.Id == "pools";

    public void SelectInstanceSection(string id)
    {
        if (InstanceSections.FirstOrDefault(section => section.Id == id) is { } section)
            SelectedInstanceSection = section;
    }

    /// <summary>
    /// 实例管理 is a scope, not a peer page: entering it records where the operator came from and manages the startup
    /// instance chosen in 实例列表, so the back button restores exactly that one page.
    /// </summary>
    /// <summary>Opens the scope; without an instance it manages the startup instance chosen in the list.</summary>
    private void EnterInstanceScope(HostInstanceItem? instance = null)
    {
        if (!IsInstance) returnPageId = SelectedPage.Id;
        if (Pages.FirstOrDefault(page => page.Id == "instance") is not { } page) return;
        if (instance is not null) Servers.Selected = instance;
        else if (StartupInstanceId is { } startup && Servers.Instances.FirstOrDefault(i => i.Entry.Id == startup) is { } item)
            Servers.Selected = item;
        SelectedPage = page;
        ClearPresetScope();
        SelectedInstanceSection = InstanceSections[0];
    }

    private void ClearPresetScope()
    {
        if (managedPresetId is null) return;
        managedPresetId = null;
        Notify(nameof(ManagedPresetId));
        Notify(nameof(IsPresetScope));
        Notify(nameof(IsInstanceScopeVisible));
        Notify(nameof(ManagedPresetName));
        NotifyPresetSections();
    }

    /// <summary>
    /// Manages one instance without making it the startup one: the row menu opens the row it was invoked on, while the
    /// list keeps its own startup choice.
    /// </summary>
    public void OpenInstanceScope(HostInstanceItem? instance = null) => EnterInstanceScope(instance);

    public void ExitInstanceScope()
    {
        ClearPresetScope();
        NavigateCommand.Execute(returnPageId ?? "instances");
    }

    /// <summary>Used by entry points that name one section, such as the home page's map-pool button.</summary>
    public void OpenInstanceSection(string section)
    {
        EnterInstanceScope();
        SelectInstanceSection(section);
    }

    private Guid? startupInstanceId;
    public Guid? StartupInstanceId => startupInstanceId is { } id && Servers.Instances.Any(i => i.Entry.Id == id)
        ? id : Servers.Selected?.Entry.Id;
    public void RememberStartupInstance() => startupInstanceId = StartupInstanceId;
    public bool SelectStartupInstance(HostInstanceItem item)
    {
        if (item.Entry.Definition is null) return false;
        Servers.Selected = item;
        if (Servers.Selected?.Entry.Id != item.Entry.Id) return false;
        startupInstanceId = item.Entry.Id;
        Notify(nameof(StartupInstanceId));
        return true;
    }

    private NavigationItem _selectedPage;
    private string _selectedTheme = "跟随系统";
    private double _backgroundOpacityPercent = 100;
    public double BackgroundOpacityPercent
    {
        get => _backgroundOpacityPercent;
        set
        {
            if (!double.IsFinite(value)) return;
            var clamped = Math.Clamp(value, 0, 100);
            if (_backgroundOpacityPercent == clamped) return;
            _backgroundOpacityPercent = clamped;
            Notify(); Notify(nameof(BackgroundOpacity)); Notify(nameof(BackgroundOpacityLabel));
        }
    }
    public double BackgroundOpacity => BackgroundOpacityPercent / 100;
    public string BackgroundOpacityLabel => $"{BackgroundOpacityPercent:0}%";
    private double _sidebarTintPercent = 72;
    public double SidebarTintPercent
    {
        get => _sidebarTintPercent;
        set
        {
            if (!double.IsFinite(value)) return;
            var clamped = Math.Clamp(value, 0, 100);
            if (_sidebarTintPercent == clamped) return;
            _sidebarTintPercent = clamped;
            Notify(); Notify(nameof(SidebarTintOpacity)); Notify(nameof(SidebarTintLabel));
        }
    }
    public double SidebarTintOpacity => SidebarTintPercent / 100;
    public string SidebarTintLabel => $"{SidebarTintPercent:0}%";

    private double _sidebarBlurRadius = 18;
    public double SidebarBlurRadius
    {
        get => _sidebarBlurRadius;
        set
        {
            if (!double.IsFinite(value)) return;
            var clamped = Math.Clamp(value, 0, 32);
            if (_sidebarBlurRadius == clamped) return;
            _sidebarBlurRadius = clamped;
            Notify(); Notify(nameof(SidebarBlurLabel));
        }
    }
    public string SidebarBlurLabel => $"{SidebarBlurRadius:0} px";
    public IReadOnlyList<string> Themes { get; } = ["跟随系统", "浅色", "深色"];
    public string VersionLabel => ProductVersion.Display;
    public bool DownloadsBlocked { get; set; }
    public ICommand NavigateCommand { get; }
    public ICommand SetThemeCommand { get; }

    public ShellViewModel(IThemeService themeService, SteamCmdController steamCmd, HostServerController servers, BaselineController baseline)
    {
        SteamCmd = steamCmd;
        Servers = servers;
        Baseline = baseline;
        _selectedPage = Pages[0];
        _selectedPage.IsSelected = true;
        SetThemeCommand = new RelayCommand(value => { if (value is string theme) SelectedTheme = theme; });
        NavigateCommand = new RelayCommand(value =>
        {
            if (DownloadsBlocked || value is not string id || Pages.FirstOrDefault(page => page.Id == id) is not { } page) return;
            if (id == "instance") { EnterInstanceScope(); return; }
            returnPageId = null;
            if (id == "home" && StartupInstanceId is { } startup &&
                Servers.Instances.FirstOrDefault(i => i.Entry.Id == startup) is { } item)
                Servers.Selected = item;
            SelectedPage = page;
        });
        _themeService = themeService;
    }

    private readonly IThemeService _themeService;
    public NavigationItem SelectedPage
    {
        get => _selectedPage;
        private set
        {
            if (_selectedPage == value) return;
            _selectedPage.IsSelected = false;
            _selectedPage = value;
            _selectedPage.IsSelected = true;
            Notify(); Notify(nameof(IsHome)); Notify(nameof(IsSettings)); Notify(nameof(IsInstance)); Notify(nameof(IsInstances)); Notify(nameof(IsDownloads));
            Notify(nameof(IsInstanceOverview)); Notify(nameof(IsInstancePresets));
            Notify(nameof(IsInstancePools)); Notify(nameof(IsInstanceScopeVisible));
        }
    }
    public bool IsHome => SelectedPage.Id == "home";
    public bool IsSettings => SelectedPage.Id == "settings";
    public bool IsInstance => SelectedPage.Id == "instance";
    public bool IsInstances => SelectedPage.Id == "instances";
    public bool IsDownloads => SelectedPage.Id == "downloads";
    public bool IsSystemTheme => SelectedTheme == "跟随系统";
    public bool IsLightTheme => SelectedTheme == "浅色";
    public bool IsDarkTheme => SelectedTheme == "深色";
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (_selectedTheme == value) return;
            _selectedTheme = value;
            _themeService.Apply(value switch
            {
                "浅色" => ThemePreference.Light,
                "深色" => ThemePreference.Dark,
                _ => ThemePreference.System
            });
            Notify(); Notify(nameof(IsSystemTheme)); Notify(nameof(IsLightTheme)); Notify(nameof(IsDarkTheme));
        }
    }
}
