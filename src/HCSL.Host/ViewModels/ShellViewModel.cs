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

public sealed class ShellViewModel : ObservableObject
{
    public SteamCmdController SteamCmd { get; }
    public IReadOnlyList<NavigationItem> Pages { get; } =
    [
        new("home", "首页", "选择实例，准备启动服务器。"),
        new("instance", "实例管理", "管理当前实例的配置、插件、地图与玩家授权。"),
        new("instances", "实例列表", "实例只保存配置与插件，共用全局最新版 CS2 服务端。"),
        new("downloads", "下载中心", "统一管理 CS2 服务端更新与插件下载。"),
        new("network", "联机组网", "使用 EasyTier 创建网络并分享组网邀请。"),
        new("settings", "设置", "调整外观与应用偏好。")
    ];

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
    public ICommand NavigateCommand { get; }
    public ICommand SetThemeCommand { get; }

    public ShellViewModel(IThemeService themeService, SteamCmdController steamCmd)
    {
        SteamCmd = steamCmd;
        _selectedPage = Pages[0];
        _selectedPage.IsSelected = true;
        SetThemeCommand = new RelayCommand(value => { if (value is string theme) SelectedTheme = theme; });
        NavigateCommand = new RelayCommand(value =>
        {
            if (value is string id && Pages.FirstOrDefault(page => page.Id == id) is { } page)
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
            Notify(); Notify(nameof(IsHome)); Notify(nameof(IsSettings)); Notify(nameof(IsInstance)); Notify(nameof(IsInstances)); Notify(nameof(IsDownloads)); Notify(nameof(IsNetwork));
        }
    }
    public bool IsHome => SelectedPage.Id == "home";
    public bool IsSettings => SelectedPage.Id == "settings";
    public bool IsInstance => SelectedPage.Id == "instance";
    public bool IsInstances => SelectedPage.Id == "instances";
    public bool IsDownloads => SelectedPage.Id == "downloads";
    public bool IsNetwork => SelectedPage.Id == "network";
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
