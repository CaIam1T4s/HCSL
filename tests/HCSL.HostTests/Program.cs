using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HCSL.Core;
using HCSL.Contracts;
using HCSL.Frontend;
using HCSL.Host.Services;
using HCSL.HostTests;
using HCSL.Host.ViewModels;
using HCSL.Host.Views;
using HCSL.UI.Shared.Styles.Themes;
using HCSL.UI.Shared.Controls;
using System.Text.Json;
using TKCode.Arc;

// Real Arc templates, fake IPC only: no desktop, Steam, files, daemon or CS2 process.
var only = ParseOnly(args);
var appBuilder = AppBuilder.Configure<HCSL.Host.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions
{
    UseHeadlessDrawing = only != "theme-raster"
});
if (only == "theme-raster") appBuilder.UseSkia();
appBuilder.UseArc(a => a.UseArcControls().UseArcBlocks().UseArcSpecial().WithTheme(t => t.Appearance(ArcAppearance.Dark).Preset(ThemeCatalog.ArcPreset)))
    .SetupWithoutStarting();
if (only is "input")
{
    InputPointerPlacementTests.Run();
    Console.WriteLine("PASS only input");
    return 0;
}
if (only is null) InputPointerPlacementTests.Run();
var daemon = new FakeDaemon();
using var server = new ServerController(daemon, a => Dispatcher.UIThread.Post(a));
using var model = new HostServerController(server, new InstanceClient(daemon), daemon: daemon);
using var steam = new SteamCmdController(daemon, a => Dispatcher.UIThread.Post(a));
using var baseline = new BaselineController(new BaselineClient(daemon));
PumpUntil(server.ConnectAsync());
var owner = new MainWindow
{
    DataContext = new ShellViewModel(new ThemeService(Avalonia.Application.Current!), steam, model, baseline)
};
owner.Show();
if (only == "theme-raster")
{
    ThemeRasterIsolationTests.Run(owner, daemon, model, server);
    return 0;
}
Layout();
if (only is null or "background")
{
    var home = owner.GetVisualDescendants().OfType<HCSL.Host.Views.Pages.HomePage>().Single();
    var scene = owner.GetVisualDescendants().OfType<HCSL.UI.Shared.Controls.BackgroundScene>()
        .Single(s => s.Name == "PART_WindowBackdrop");
    var titlebar = owner.FindControl<TitleBar>("ShellTitleBar")!;
    var chrome = owner.GetVisualDescendants().OfType<HCSL.UI.Shared.Controls.LShapedFrostedChrome>().Single();
    var material = owner.GetVisualDescendants().OfType<HCSL.UI.Shared.Controls.ArcFrostedBackdrop>().Single();
    var homeSurface = home.FindControl<Grid>("HomeSurface")!;
    Check(scene.Bounds.Height >= titlebar.Bounds.Height + owner.FindControl<Grid>("ShellContent")!.Bounds.Height &&
          ReferenceEquals(chrome.Source, scene) && ReferenceEquals(GlassBackdrop.GetSource(material), scene) &&
          material.Bounds.Size == scene.Bounds.Size && !chrome.IsHitTestVisible,
        "One continuous window-sized Arc material must sample the shared scene without intercepting input.");
    var side = chrome.SidebarWidth;
    var top = chrome.TitleBarHeight;
    // Headless geometry hit-testing approximates bounds; inspect the actual polygon vertices instead.
    Check(side == owner.FindControl<Sidebar>("MainSidebar")!.Bounds.Width && top == titlebar.Bounds.Height &&
          material.Clip is Avalonia.Media.PolylineGeometry { IsFilled: true } outline && outline.Points.SequenceEqual(new[]
          {
              default(Point), new Point(scene.Bounds.Width, 0), new Point(scene.Bounds.Width, top),
              new Point(side, top), new Point(side, scene.Bounds.Height), new Point(0, scene.Bounds.Height)
          }),
        "The single clip must be an L-shaped polygon matching the actual title/sidebar dimensions.");
    Check(titlebar.BorderThickness == default && owner.FindControl<Sidebar>("MainSidebar")!.BorderThickness == default &&
          owner.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("sidebar")).BorderThickness == default &&
          owner.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_FooterSeparator").Opacity == 0,
        $"Chrome rims must be removed: title={titlebar.BorderThickness}, sidebar={owner.FindControl<Sidebar>("MainSidebar")!.BorderThickness}, frame={owner.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("sidebar")).BorderThickness}, footerOpacity={owner.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_FooterSeparator").Opacity}.");
    var shadowLayer = chrome.GetVisualDescendants().OfType<Canvas>().Single(c => c.Name == "ChromeShadowLayer");
    Check(shadowLayer.Clip is Avalonia.Media.RectangleGeometry shadowClip &&
          shadowClip.Rect == new Rect(side, top, scene.Bounds.Width - side, scene.Bounds.Height - top) &&
          shadowLayer.Children.OfType<Border>().All(b => b.BoxShadow.Count > 0),
        "Soft shadows must be clipped to page content, never crossing the internal title/sidebar join.");
    var originalWindowSize = new Size(owner.Width, owner.Height);
    owner.Width += 80; owner.Height += 60; Layout();
    var resizedOutline = (Avalonia.Media.PolylineGeometry)material.Clip!;
    Check(chrome.Bounds.Size == scene.Bounds.Size && material.Bounds.Size == scene.Bounds.Size &&
          resizedOutline.Points[1] == new Point(scene.Bounds.Width, 0) &&
          resizedOutline.Points[4] == new Point(side, scene.Bounds.Height),
        "Resizing the window must resize the single material and its L-shaped clip together.");
    owner.Width = originalWindowSize.Width; owner.Height = originalWindowSize.Height; Layout();
    owner.IsTitleBarVisible = false; Layout();
    Check(((Avalonia.Media.PolylineGeometry)material.Clip!).Points[2].Y == 0 &&
          ((Avalonia.Media.RectangleGeometry)shadowLayer.Clip!).Rect.Y == 0,
        "Hiding the title bar must leave only sidebar frost, without a phantom top band or seam shadow.");
    owner.IsTitleBarVisible = true; Layout();
    Check(((Avalonia.Media.PolylineGeometry)material.Clip!).Points[2].Y == titlebar.Bounds.Height,
        "Restoring the title bar must restore the top arm of the same material.");
    Check(!GlassBackdrop.GetIsLive(material) && material.MaxTilt == 0 && material.SheenIntensity == 0 &&
          material.Resources[TKCode.Arc.DesignTokens.GlassPaneTokens.Key(TKCode.Arc.DesignTokens.GlassPaneTokenKind.EdgeLight)]
              is Avalonia.Media.Color { A: 0 },
        "Arc frost must stay static and avoid adding bright card rims.");
    var shellModel = (ShellViewModel)owner.DataContext!;
    var originalBlur = shellModel.SidebarBlurRadius;
    var originalTint = shellModel.SidebarTintPercent;
    shellModel.SidebarBlurRadius = 12; shellModel.SidebarTintPercent = 40; Layout();
    Check(material.BlurRadius == 12 && Math.Abs(material.TintOpacity - 0.4) < 0.001 &&
          titlebar.Effect is null,
        "The shared glass settings must update the title backdrop without blurring title-bar controls.");
    shellModel.SidebarBlurRadius = originalBlur; shellModel.SidebarTintPercent = originalTint; Layout();
    ArcTheme.Apply(t => t.Appearance(ArcAppearance.Light)); Layout();
    var tint = (Avalonia.Media.Color)material.Resources[TKCode.Arc.DesignTokens.GlassPaneTokens.Key(TKCode.Arc.DesignTokens.GlassPaneTokenKind.Tint)]!;
    var neutral = ArcTokens.GetColor(material, TKCode.Arc.DesignTokens.ArcDesignTokenKind.Surface);
    Check(tint.R == neutral.R && tint.G == neutral.G && tint.B == neutral.B && tint.A < 255,
        $"Arc frost must follow the neutral theme surface: tint={tint}, neutral={neutral}, opacity={material.TintOpacity}.");
    ArcTheme.Apply(t => t.Appearance(ArcAppearance.Dark)); Layout();
    var themeSwitcher = owner.FindControl<ThemeSwitcher>("SidebarThemeSwitcher")!;
    themeSwitcher.ApplyTemplate(); // The hidden entry is not materialized by ordinary layout.
    var themeButton = themeSwitcher.GetVisualDescendants()
        .OfType<TKCode.Arc.Button>().Single(b => b.Name == "PART_Button");
    Check(themeButton.Background is Avalonia.Media.ISolidColorBrush { Opacity: > 0, Color.A: > 0 } themeSurface &&
          (themeSurface.Opacity < 1 || themeSurface.Color.A < 255) && themeButton.Opacity == 1,
        $"The theme-switcher background must remain translucent: brush={themeButton.Background}, brushOpacity={themeButton.Background?.Opacity}, controlOpacity={themeButton.Opacity}.");
    Check(scene.ImageSource is null && homeSurface.Background is Avalonia.Media.ISolidColorBrush { Color.A: 0 } &&
          home.FindControl<Grid>("HomeBackdrop") is null && home.FindControl<Avalonia.Controls.Shapes.Ellipse>("AccentOrb") is null,
        "Without wallpaper home must reveal the shared default background, with no private gradient or circle.");
    using var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96));
    using var imageStream = new MemoryStream();
    bitmap.Save(imageStream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); imageStream.Position = 0;
    owner.SetBackground(imageStream); Layout();
    Check(scene.ImageSource is not null && homeSurface.Background is Avalonia.Media.ISolidColorBrush { Color.A: 0 },
        "Wallpaper must remain visible through the transparent home page.");
    var originalStrength = ((ShellViewModel)owner.DataContext!).BackgroundOpacityPercent;
    ((ShellViewModel)owner.DataContext!).BackgroundOpacityPercent = 40; Layout();
    Check(Math.Abs(scene.Strength - 0.4) < 0.001, "Wallpaper strength must reach the shared full-window scene.");
    ((ShellViewModel)owner.DataContext!).BackgroundOpacityPercent = originalStrength;
    ((ShellViewModel)owner.DataContext!).NavigateCommand.Execute("settings"); Layout();
    Check(scene.IsEffectivelyVisible && scene.ImageSource is not null && titlebar.Background is Avalonia.Media.ISolidColorBrush { Color.A: 0 },
        "Other pages and the transparent title bar must keep the same wallpaper.");
    Check(chrome.CoversWorkspace && !shadowLayer.IsVisible, "Wallpaper must use continuous workspace chrome without seam shadows.");
    owner.ResetBackground(); Layout();
    Check(scene.ImageSource is null && chrome.CoversWorkspace && !shadowLayer.IsVisible,
        "Without wallpaper the workspace must still use continuous chrome.");
    ((ShellViewModel)owner.DataContext!).NavigateCommand.Execute("home"); Layout();
    Check(!chrome.CoversWorkspace && shadowLayer.IsVisible, "Home must restore the original L-shaped chrome.");
    Check(scene.ImageSource is null && homeSurface.Background is Avalonia.Media.ISolidColorBrush { Color.A: 0 },
        "Resetting wallpaper must restore the shared default background without a private home overlay.");
    Console.WriteLine("PASS gui.background.full-window-wallpaper-titlebar-sidebar-strength-and-reset");
    if (only is "background") return 0;
}
if (only is null or "sidebar")
{
    var sidebar = owner.FindControl<Sidebar>("MainSidebar")!;
    var shell = (ShellViewModel)owner.DataContext!;
    Check(sidebar.ExpandedWidth == 216 && !sidebar.IsCollapsed && !sidebar.IsCollapseButtonVisible && !sidebar.IsFilterVisible,
        "The trial Arc sidebar must retain the existing expanded layout without extra chrome.");
    Check(sidebar.Sections.Count == 2 && sidebar.Sections[0].Items.Count == 4 && sidebar.Sections[1].Items.Count == 1,
        "The sidebar must preserve server and general navigation groups.");
    Check(sidebar.Header is not null && sidebar.Footer is not null &&
          !owner.FindControl<ThemeSwitcher>("SidebarThemeSwitcher")!.IsVisible &&
          !owner.FindControl<ThemeSwitcher>("SidebarThemeSwitcher")!.IsEffectivelyVisible,
        "The account header/footer must remain, while the snapshot-based theme entry stays hidden.");
    foreach (var page in shell.Pages)
    {
        shell.NavigateCommand.Execute(page.Id); Layout();
        Check(sidebar.SelectedKey == page.Id && sidebar.SelectedItem?.Key == page.Id,
            "Programmatic navigation must update the Arc sidebar selection.");
    }
    var homeLabel = sidebar.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "首页");
    var clickPoint = homeLabel.TranslatePoint(new Point(homeLabel.Bounds.Width / 2, homeLabel.Bounds.Height / 2), owner)!.Value;
    owner.MouseDown(clickPoint, Avalonia.Input.MouseButton.Left);
    owner.MouseUp(clickPoint, Avalonia.Input.MouseButton.Left); Layout();
    Check(shell.IsHome && sidebar.SelectedKey == "home", "Clicking an Arc sidebar row must navigate to its page.");
    owner.KeyPress(Avalonia.Input.Key.B, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.None, ""); Layout();
    Check(!sidebar.IsCollapsed, "The fixed-width trial sidebar must not collapse through Ctrl+B.");
    owner.KeyPress(Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
    owner.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, ""); Layout();
    Check(shell.IsInstance && sidebar.SelectedKey == "instance", "Keyboard navigation must invoke the selected Arc row.");
    shell.NavigateCommand.Execute("home"); Layout();
    Console.WriteLine("PASS gui.sidebar.arc-sections-header-footer-and-route-synchronization");
    if (only is "sidebar") return 0;
}
if (only is null or "instances")
{
    InstancesPageTests.Run(owner, daemon, model, server);
    if (only is "instances") return 0;
}
if (only is null or "shadow")
{
    CenteredShadowTests.Run();
    if (only is "shadow") return 0;
}
if (only is null or "theme")
{
    ThemeSwitchingTests.Run(owner);
    if (only is "theme") return 0;
}
// Development-only diagnostics are explicitly requested, never part of the product switching path.
if (only is "theme-transition")
{
    ThemeTransitionIsolationTests.Run(owner);
    return 0;
}
if (only is "home")
{
    var home = owner.GetVisualDescendants().OfType<HCSL.Host.Views.Pages.HomePage>().Single();
    var surface = home.FindControl<Grid>("HomeSurface")!;
    var content = home.FindControl<Grid>("HomeContent")!;
    Check(surface.ClipToBounds && surface.Children.Count == 1 && ReferenceEquals(surface.Children[0], content) &&
          surface.Background is Avalonia.Media.ISolidColorBrush { Color.A: 0 } && surface.Effect is null && content.Effect is null,
        "Home must reveal the same window background as other pages, without a private backdrop or blur.");
    var launch = home.FindControl<ExpandingButtonGroupItem>("LaunchButton")!;
    var actions = home.FindControl<ExpandingButtonGroup>("LaunchActions")!;
    var configuration = home.FindControl<ExpandingButtonGroupItem>("ConfigurationButton")!;
    var popover = home.FindControl<Popover>("ConfigurationPopover")!;
    var instances = home.FindControl<MorphSelect>("InstancePicker")!;
    var presets = home.FindControl<MorphSelect>("PresetPicker")!;
    var maps = home.FindControl<MorphSelect>("MapPicker")!;
    Check(actions.IsVisible && launch.IsVisible && !launch.IsDisabled && launch.Label == "实例列表" &&
          configuration.IsDisabled && !instances.IsVisible && home.FindControl<TKCode.Arc.Button>("CreateButton") is null,
        "Empty home must reuse the main action for the instance list and disable configuration, without a separate button.");
    configuration.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
    Check(!popover.IsOpen, "Empty home must not allow the startup configuration menu to open.");
    launch.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
    Check(((ShellViewModel)owner.DataContext!).IsInstances, "The empty-state main action must navigate to the instance list.");
    ((ShellViewModel)owner.DataContext!).NavigateCommand.Execute("home"); Layout();
    var p = new PresetDefinition(1, Guid.NewGuid(), 1, "Home preset");
    var id = Guid.NewGuid();
    daemon.IdleForPresetTests = true;
    daemon.Catalog = new([new(id, new(1, id, 1, "Home instance", 27015, p.Id), [p])]);
    PumpUntil(server.RefreshAsync());
    PumpUntil(model.RefreshInstancesAsync());
    WaitUntil(() => maps.ItemsSource is ServerMapTarget[] { Length: 2 } && !model.Busy);
    Check(launch.IsVisible && !configuration.IsDisabled && configuration.IsEnabled &&
          launch.Label == "启动服务器" && Equals(instances.SelectedItem, model.Selected),
        "Adding an instance must restore startup and enable its configuration menu.");
    Check(maps.SelectedItem is ServerMapTarget { Value: "de_dust2" } && !launch.IsDisabled,
        "Startup map must come from the preset pool.");
    Check(actions.IsVisible && !popover.IsOpen && actions.Items.Count == 2,
        "Startup actions must share one group with configuration initially closed.");
    Check(!instances.IsLabelVisible && !presets.IsLabelVisible && !maps.IsLabelVisible,
        "Configuration pickers must use their separate left-hand labels.");
    Check(ReferenceEquals(actions.DefaultExpandedItem, launch) && ReferenceEquals(actions.ExpandedItem, launch),
        "Startup must be the resting action.");
    Check(launch.Height > 36 && launch.Height == configuration.Height,
        "Startup actions must share a height larger than the default compact preset.");
    Check(launch.MinWidth == 0,
        "The launch action must size to padded content rather than a fixed minimum width.");
    var actionRow = launch.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Name == "PART_Row");
    Check(actionRow.Margin.Left > 10 && actionRow.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Center,
        "The launch action must use a larger preset inset and center its content.");
    Check(((StackPanel)popover.Content!).Children.OfType<TextBlock>().Count() == 0,
        "The configuration menu must not have a redundant title.");
    Check(!home.FindControl<TKCode.Arc.Button>("PoolButton")!.IsVisible,
        "A populated map pool must hide the map pool shortcut.");
    Check(popover.Placement == PopoverPlacement.Top && popover.Alignment == PopoverAlignment.End &&
          popover.TriggerMode == PopoverTriggerMode.Manual,
        "Configuration must open above the action area on click, not hover.");
    Check(((StackPanel)popover.Content!).Spacing < 8,
        "Configuration fields must use compact Arc spacing.");
    configuration.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
    Check(popover.IsOpen && instances.IsVisible && presets.IsVisible && maps.IsVisible,
        "Configuration action must reveal all three MorphSelect pickers.");
    Check(instances.IsEffectivelyEnabled && presets.IsEffectivelyEnabled && maps.IsEffectivelyEnabled,
        "Idle configuration pickers must be interactive.");
    foreach (var picker in new[] { instances, presets, maps })
    {
        var row = (Grid)picker.Parent!;
        var label = row.Children.OfType<TextBlock>().Single();
        Check(label.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Left &&
              picker.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right &&
              label.Bounds.Right <= picker.Bounds.Left,
            "Field labels must sit on the left and pickers on the right.");
    }
    Check(ReferenceEquals(home.FindControl<TKCode.Arc.Button>("PoolButton")!.Parent, popover.Content),
        "Map pool management must be inside the configuration menu.");
    Check(actions.Background is Avalonia.Media.ISolidColorBrush { Opacity: > 0, Color.A: > 0 } actionSurface &&
          (actionSurface.Opacity < 1 || actionSurface.Color.A < 255) && actions.Opacity == 1 &&
          configuration.Opacity == 1,
        "Only the launch surface brush may be translucent; action text and icons must remain opaque.");
    var popoverSurface = ((StackPanel)popover.Content!).GetVisualAncestors().OfType<PopoverSurface>().Single();
    Check(popoverSurface.Background is Avalonia.Media.ISolidColorBrush { Opacity: > 0, Color.A: > 0 } menuSurface &&
          (menuSurface.Opacity < 1 || menuSurface.Color.A < 255) && popoverSurface.Opacity == 1,
        "The launch menu surface must be translucent without fading its controls.");
    var anchor = home.FindControl<Grid>("LaunchAnchor")!;
    Check(ReferenceEquals(popover.Anchor, anchor), "The popover must use the fixed action-area anchor.");
    var anchorBounds = anchor.Bounds;
    actions.DefaultExpandedItem = configuration; Layout();
    Check(anchor.Bounds == anchorBounds && ReferenceEquals(popover.Anchor, anchor),
        "Changing the expanded action must not move or resize the popover anchor.");
    actions.DefaultExpandedItem = launch; Layout();
    maps.IsOpen = true; Layout();
    Check(maps.IsOpen && popover.IsOpen, "Opening a MorphSelect must preserve the configuration popover.");
    maps.IsOpen = false; Layout();
    Check(popover.IsOpen, "Closing a MorphSelect must not dismiss the configuration panel.");
    configuration.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
    Check(!popover.IsOpen, "Configuration action must toggle the panel closed.");
    configuration.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
    var badge = owner.FindControl<Badge>("ServerStatusBadge")!;
    var icon = badge.Icon;
    PumpUntil(server.RefreshAsync()); Layout();
    Check(ReferenceEquals(icon, badge.Icon), "Polling must not recreate unchanged status icons.");
    maps.SelectedItem = ((ServerMapTarget[])maps.ItemsSource!)[1]; Layout();
    Click(owner, "取消");
    WaitUntil(() => maps.SelectedItem is ServerMapTarget { Value: "de_dust2" });
    Check(model.Selected!.Entry.Presets[0].Map == "de_dust2", "Cancelling map selection must preserve startup configuration.");
    daemon.IdleForPresetTests = false;
    PumpUntil(server.RefreshAsync()); Layout();
    Check(!model.CanMutate && instances.IsEffectivelyEnabled && presets.IsEffectivelyEnabled && maps.IsEffectivelyEnabled,
        "A runtime write lock must not disable browsing configuration choices.");
    maps.SelectedItem = ((ServerMapTarget[])maps.ItemsSource!)[1]; Layout();
    Check(model.Selected.Entry.Presets[0].Map == "de_dust2" && maps.SelectedItem is ServerMapTarget { Value: "de_dust2" }
          && home.FindControl<Alert>("Feedback")!.IsVisible && launch.IsDisabled,
        "A runtime write lock must explain refused changes, preserve the map and prevent startup.");
    daemon.IdleForPresetTests = true;
    PumpUntil(server.RefreshAsync()); Layout();
    daemon.PoolReadError = true;
    home.FindControl<TKCode.Arc.Button>("RetryButton")!.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    WaitUntil(() => home.FindControl<TKCode.Arc.Button>("RetryButton")!.IsVisible && !model.Busy);
    Check(!home.FindControl<TKCode.Arc.Button>("PoolButton")!.IsVisible && launch.IsDisabled,
        "A pool read failure must hide the empty-pool shortcut and prevent startup.");
    daemon.PoolReadError = false;
    daemon.Pool = new(new(1, 1, []), "empty");
    home.FindControl<TKCode.Arc.Button>("RetryButton")!.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    WaitUntil(() => home.FindControl<TKCode.Arc.Button>("PoolButton")!.IsEnabled);
    configuration.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
    Check(popover.IsOpen, "Configuration must remain available before the final instance is removed.");
    daemon.Catalog = new([]);
    PumpUntil(model.RefreshInstancesAsync()); Layout();
    Check(actions.IsVisible && launch.Label == "实例列表" && !launch.IsDisabled && configuration.IsDisabled && !popover.IsOpen,
        "Removing the last instance must restore the list action and close and disable configuration.");
    Console.WriteLine("PASS gui.home.pool-selection-layout-and-stable-status");
    return 0;
}
if (only is null or "logs")
{
for (var attempt = 0; attempt < 2; attempt++)
{
    var log = new ServerLogWindow(model, owner);
    log.Show(owner);
    PumpUntil(server.RefreshLogsAsync());
    Layout();
    var area = log.GetVisualDescendants().OfType<Textarea>().Single();
    var scroll = area.GetVisualDescendants().OfType<ScrollViewer>().Single();
    Check(area.FrameHeight > 0 && area.FrameHeight < log.ClientSize.Height, "Editor must fit the finite window height.");
    Check(area.FontFamily?.Name == HCSL.UI.Shared.Services.MonospaceFont.Resolve().Name, "Log content must use an installed monospace family.");
    Check(scroll.Extent.Height > scroll.Viewport.Height, "Long logs must have a vertical scroll range.");
    Check(scroll.Extent.Width > scroll.Viewport.Width, "Long lines must have a horizontal scroll range.");
    scroll.Offset = new Vector(40, 100);
    Layout();
    Check(scroll.Offset.Y > 0 && scroll.Offset.X > 0, "Both axes must be scrollable after reopen.");
    log.Height = 420;
    Layout();
    Check(area.FrameHeight < 420, "Resizing must resize the internal Arc frame.");
    var prompt = owner.ChooseAsync("测试确认", "日志不得覆盖此浮层。", "返回");
    Layout();
    Check(!log.IsVisible, "Prompt must hide owned log windows.");
    Click(owner, "返回");
    PumpUntil(prompt);
    Layout();
    Check(log.IsVisible, "Log must return after the prompt.");
    // The console has one command field plus the hidden find field; pick it by its placeholder.
    var queryInput = log.GetVisualDescendants().OfType<TKCode.Arc.Input>()
        .Single(i => (i.PlaceholderText ?? "").StartsWith("输入服务器命令", StringComparison.Ordinal));
    queryInput.Text = "status";
    queryInput.Focus();
    Layout();
    Check(model.Server.CanSendConsole,
        $"Console fixture not writable: state={server.State}, active={server.Active}, connected={server.Connected}, status={server.ConsoleStatus}");
    // Enter is the only submit path: the console has no send button any more.
    log.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
    log.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
    WaitUntil(() => model.Server.ConsoleStatus.Contains("HCSL-CONSOLE-WRITTEN"));
    Check(model.Server.Active && daemon.Queries == attempt + 1, "GUI query must reach IPC without stopping the run.");
    Layout();
    Check(Math.Abs(scroll.Offset.Y - (scroll.Extent.Height - scroll.Viewport.Height)) < 3, "Explicit submission must resume following at bottom.");
    Check(queryInput.IsEnabled && queryInput.IsFocused, "Sending must not disable or defocus the command field.");
    log.Close();
    Layout();
}
Console.WriteLine("PASS gui.logs.reopen-resize-scroll-and-prompt-restore");
var writeLog = new ServerLogWindow(model, owner); writeLog.Show(owner); Layout();
var writeInput = writeLog.GetVisualDescendants().OfType<TKCode.Arc.Input>()
    .Single(i => (i.PlaceholderText ?? "").StartsWith("输入服务器命令", StringComparison.Ordinal));
writeInput.Text = "mp_roundtime 60";
var beforeWrites = daemon.Queries;
writeInput.Focus(); Layout();
writeLog.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
writeLog.KeyRelease(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
Layout();
WaitUntil(() => daemon.Queries == beforeWrites + 1); Layout();
Check(writeLog.IsVisible && daemon.LastConsole is { Text: "mp_roundtime 60", ConfirmMutation: false }, "General console must send without mutation confirmation.");
writeInput.Focus(); writeInput.CaretIndex = 0;
writeLog.KeyPress(Avalonia.Input.Key.Up, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
writeLog.KeyRelease(Avalonia.Input.Key.Up, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
Layout();
Dispatcher.UIThread.RunJobs();
Layout();
Check(writeInput.Text == "mp_roundtime 60" && writeInput.CaretIndex == writeInput.Text.Length, "History recall must put caret at command end.");
// History settings live in the console's options menu; the controller state is what that menu toggles.
Check(server.ConsoleHistoryEnabled, "History must default on.");
server.ConsoleHistoryEnabled = false; Layout();
Check(!server.ConsoleHistoryEnabled && server.ConsoleHistory.Count == 0, "Disable history must clear retained entries.");
server.ConsoleHistoryEnabled = true; Layout();
writeLog.Close();
Console.WriteLine("PASS gui.console.general-send-and-history-toggle");
daemon.RepeatLogText = true;
var evictionLog = new ServerLogWindow(model, owner); evictionLog.Show(owner); Layout();
var evictionArea = evictionLog.GetVisualDescendants().OfType<Textarea>().Single();
var evictionScroll = evictionArea.GetVisualDescendants().OfType<ScrollViewer>().Single();
evictionScroll.Offset = new Vector(0, 100); Layout();
for (var end = 300; end <= 2400; end += 150)
{
    daemon.LastLogSequence = end;
    PumpUntil(server.RefreshLogsAsync()); Layout();
}
Check(evictionLog.GetVisualDescendants().OfType<ToastStack>().Any(t => t.VisibleToasts > 0),
    "Evicted reader must see a non-modal toast notice.");
Check(evictionScroll.Offset.Y < 5, "Evicted reader must be at earliest retained record, not latest.");
evictionScroll.Offset = new Vector(0, 100); Layout();
daemon.LastLogSequence = 2550;
PumpUntil(server.RefreshLogsAsync()); Layout();
Check(evictionScroll.Offset.Y < 5, "Repeated-text eviction must retain earliest position.");
evictionArea.Focus();
evictionLog.KeyPressQwerty(Avalonia.Input.PhysicalKey.End, Avalonia.Input.RawInputModifiers.None);
evictionLog.KeyReleaseQwerty(Avalonia.Input.PhysicalKey.End, Avalonia.Input.RawInputModifiers.None);
Layout();
Check(evictionScroll.Offset.Y > 100, "End inside the real Arc editor must resume following.");
evictionLog.Close();
Console.WriteLine("PASS gui.logs.live-append-anchor-eviction-notice-and-jump-latest");
// Regression: the log stream updates faster than layout, so each projection must re-assert the
// bottom after layout instead of inferring "reader scrolled away" from a growing Extent.
var burstLog = new ServerLogWindow(model, owner); burstLog.Show(owner); Layout();
var burstArea = burstLog.GetVisualDescendants().OfType<Textarea>().Single();
var burstScroll = burstArea.GetVisualDescendants().OfType<ScrollViewer>().Single();
Check(Math.Abs(burstScroll.Offset.Y - (burstScroll.Extent.Height - burstScroll.Viewport.Height)) < 3, "Log must start following at bottom.");
for (var burst = 0; burst < 12; burst++)
{
    daemon.LastLogSequence += 37;
    PumpUntil(server.RefreshLogsAsync()); Layout();
}
Dispatcher.UIThread.RunJobs(); Layout();
Check(Math.Abs(burstScroll.Offset.Y - (burstScroll.Extent.Height - burstScroll.Viewport.Height)) < 3, "Rapid appends must keep following the newest line.");
// A real reader scroll must still pause following and keep the reading position.
burstScroll.Offset = new Vector(0, 10); Layout();
daemon.LastLogSequence += 60;
PumpUntil(server.RefreshLogsAsync()); Layout();
Check(burstScroll.Offset.Y < 40, "Reader scroll must pause following.");
// The console has no jump button any more: End inside the editor is the documented way back to the newest line.
burstArea.Focus();
burstLog.KeyPress(Avalonia.Input.Key.End, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
burstLog.KeyRelease(Avalonia.Input.Key.End, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
Layout();
Check(Math.Abs(burstScroll.Offset.Y - (burstScroll.Extent.Height - burstScroll.Viewport.Height)) < 3, "Jump latest must resume following.");
burstLog.Close();
Console.WriteLine("PASS gui.logs.rapid-appends-keep-following-and-reader-scroll-pauses");
// Find: matches are counted, stepped and highlighted from the rows the log shows, not from a TextBox selection.
daemon.RepeatLogText = true; daemon.LastLogSequence += 150;
var findLog = new ServerLogWindow(model, owner); findLog.Show(owner); Layout();
PumpUntil(server.RefreshLogsAsync()); Layout();
var findScroll = findLog.GetVisualDescendants().OfType<Textarea>().Single()
    .GetVisualDescendants().OfType<ScrollViewer>().Single();
string CounterText() => findLog.GetVisualDescendants().OfType<TextBlock>()
    .Select(t => t.Text).FirstOrDefault(t => t == "无匹配" || (t?.Contains(" / ") ?? false)) ?? "";
var findField = findLog.GetVisualDescendants().OfType<TKCode.Arc.SearchField>().First(f => f.Label == "查找日志");
findField.Text = "REPEAT"; Layout();
var expectedMatches = model.Server.ConsoleRows.Count(r => r.Text.Contains("repeat", StringComparison.Ordinal));
Check(expectedMatches > 100, $"The fixture must hold enough repeated lines, got {expectedMatches}.");
// Typing does not move the reader: the selected match is the first one in the visible range, near the tail here.
var typedCounter = CounterText();
var typedIndex = int.Parse(typedCounter.Split(" / ")[0]);
Check(typedCounter.EndsWith(" / " + expectedMatches, StringComparison.Ordinal) && typedIndex > expectedMatches - 60,
    $"Typing must select the match nearest the viewport: got '{typedCounter}'.");
Check(Math.Abs(findScroll.Offset.Y - (findScroll.Extent.Height - findScroll.Viewport.Height)) < 3, "Typing must not scroll the log.");
var bandLayer = findLog.GetVisualDescendants().OfType<HCSL.Host.Views.LogBandLayer>().First();
Check(bandLayer.BandCount > 0, "Matches must draw bands behind the matched lines.");
var stepNext = findLog.GetVisualDescendants().OfType<TKCode.Arc.Button>()
    .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "下一个匹配");
stepNext.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
Check(CounterText() == (typedIndex + 1) + " / " + expectedMatches, $"Next must advance one match: got '{CounterText()}'.");
Check(findScroll.Offset.Y > 0, "Stepping must keep the current match in view.");
var stepPrevious = findLog.GetVisualDescendants().OfType<TKCode.Arc.Button>()
    .Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "上一个匹配");
stepPrevious.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
Check(CounterText() == typedIndex + " / " + expectedMatches, "Previous must step back to the previous match.");
// The case option is an Arc toggle group: pressing it re-runs the search with Ordinal matching.
var caseOption = findLog.GetVisualDescendants().OfType<TKCode.Arc.ToggleGroupItem>().First();
// Keyboard path: the option is a real toggle, so Space must press it without relying on pointer hit testing.
caseOption.Focus();
findLog.KeyPress(Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
findLog.KeyRelease(Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
Layout();
Check(CounterText() == "无匹配", $"Case-sensitive search must stop matching lower case text: got '{CounterText()}'.");
Check(bandLayer.BandCount == 0, "No match must leave no bands behind.");
findLog.KeyPress(Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
findLog.KeyRelease(Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, "");
Layout();
Check(CounterText().EndsWith(" / " + expectedMatches, StringComparison.Ordinal), "Releasing the case option must restore the matches.");
findField.Text = ""; Layout();
Check(CounterText() == "", "Clearing the query must clear the counter.");
findLog.Close();
Console.WriteLine("PASS gui.logs.find-counts-steps-matches-and-caps-case");
var downloads = new HCSL.Host.Views.Pages.DownloadsPage { DataContext = owner.DataContext };
var downloadWindow = new Window { Width = 900, Height = 700, Content = downloads };
downloadWindow.Show(owner);
Layout();
// The download page now reports measured progress instead of a bounded task log frame; that frame is
// gone, and the progress surfaces live inside the task dialog, which only exists while a task runs.
Check(!downloads.GetVisualDescendants().OfType<Textarea>().Any(),
    "Download page must not keep the removed task log.");
downloadWindow.Close();
Console.WriteLine("PASS gui.downloads.measured-progress-instead-of-task-log");
// Stop lives in the sidebar footer: a hold in the wide layout, a trash icon that opens the same hold in the rail.
var stopWindow = new ServerLogWindow(model, owner); stopWindow.Show(owner); Layout();
var stopHold = stopWindow.FindControl<TKCode.Arc.HoldToConfirm>("StopHold")!;
var stopIcon = stopWindow.FindControl<TKCode.Arc.Button>("StopIcon")!;
Check(stopHold.IsVisible && !stopIcon.IsVisible, "The wide console must show the hold-to-stop control in the sidebar footer.");
stopWindow.Width = 820; Layout();
Check(!stopHold.IsVisible && stopIcon.IsVisible, "The collapsed rail must swap the stop hold for its trash icon.");
stopWindow.Width = 1100; Layout();
Check(stopHold.IsVisible && !stopIcon.IsVisible, "Widening the console must restore the hold-to-stop control.");
stopWindow.Close(); Layout();
Console.WriteLine("PASS gui.logs.stop-action-hold-and-rail-icon");
if (only is "logs") return 0;
}
if (only is null or "roster")
{
RosterEchoTests.Run();
if (only is "roster") return 0;
}
if (only is null or "maps")
{
// The map page reads the app catalog and the preset's pool, then filters and gates its bulk actions.
var mapsPage = new HCSL.Host.Views.MapsPage();
var mapsWindow = new Window { Width = 900, Height = 640, Content = mapsPage };
mapsWindow.Show(owner); Layout();
var mapToasts = new List<TKCode.Arc.ToastOptions>();
// The page needs one instance and one preset; provide a fixture so the section also runs alone.
var mapPreset = new PresetDefinition(1, Guid.NewGuid(), 1, "Map preset");
var mapInstance = Guid.NewGuid();
daemon.IdleForPresetTests = true;
daemon.Catalog = new([new(mapInstance, new(1, mapInstance, 1, "Map instance", 27015, mapPreset.Id), [mapPreset])]);
PumpUntil(server.RefreshAsync());
PumpUntil(model.RefreshInstancesAsync());
var mapEntry = model.Instances.First(i => i.Entry.Definition is not null);
PumpUntil(mapsPage.AttachAsync(new HCSL.Host.Views.MapsPageContext(model, mapEntry.Entry.Id,
    mapEntry.Entry.Presets.First().Id, mapToasts.Add)));
Layout();
var mapsTable = mapsPage.FindControl<TKCode.Arc.TreeTable>("CollectionTree")!;
Check(mapsTable.IsVisible && mapsTable.ItemsSource!.Cast<object>().Count() == 4, "The console must use a tree even without collections.");
var mapsFilter = mapsPage.GetVisualDescendants().OfType<TKCode.Arc.FilterToolbar>().Single();
Check(mapsFilter.IsSearchVisible && mapsFilter.Fields.Count == 2, "The map page must offer search plus source and pool filters.");
var poolOptions = mapsFilter.Fields.Single(f => f.Id == "pool").Options;
Check(poolOptions.Count == 1 && poolOptions[0].Hint?.Contains("张", StringComparison.Ordinal) == true,
    "The pool filter must list the instance pools with their map counts.");
Check(mapsTable.Columns.Count == 3 && !mapsTable.IsSelectable, "The console tree must not offer batch checkboxes.");
var mapsSelection = mapsPage.GetVisualDescendants().OfType<TKCode.Arc.SelectionToolbar>().Single();
Check(mapsSelection.Actions.Select(a => a.Label).SequenceEqual(new[] { "切换到该图" }),
    "With one map selectable, the bar must offer only the switch.");
Check(mapsSelection.Actions.All(a => !a.IsEnabled), "The switch must start disabled without a selection.");
Check(mapsPage.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b =>
    Avalonia.Automation.AutomationProperties.GetName(b) == "地图轮换规则"),
    "The rotation rule must be reachable through a named icon button.");
// A stopped server allows browsing and selection, but cannot switch maps.
var mapRows = mapsTable.ItemsSource!.Cast<HCSL.Host.ViewModels.MapRow>().ToArray();
mapRows[1].IsSelected = true; Layout();
Check(mapRows[1].IsSelected && !mapsSelection.Actions.Single(a => a.Key == "switch").IsEnabled,
    "Selecting a map while stopped must not enable switching it.");

mapRows[3].IsSelected = true; Layout();
Check(!mapRows[1].IsSelected && mapRows[3].IsSelected && mapsSelection.SelectedCount == 1,
    "Selecting another map must move the single selection.");
mapRows[3].IsSelected = false; Layout();
Check(mapsSelection.Actions.All(a => !a.IsEnabled), "Clearing the selection must disable the switch.");
mapsWindow.Close(); Layout();
var editor = new HCSL.Host.Views.MapsPage();
var editorWindow = new Window { Width = 900, Height = 640, Content = editor };
editorWindow.Show(owner); Layout();
var editorContext = new HCSL.Host.Views.MapsPageContext(model, mapEntry.Entry.Id, mapPreset.Id, mapToasts.Add, ManagePools: true);
PumpUntil(editor.AttachAsync(editorContext)); Layout();
var editorTable = editor.FindControl<TKCode.Arc.TreeTable>("CollectionTree")!;
Check(editorTable.IsSelectable && editorTable.Columns.Count == 3 && editorTable.ItemsSource!.Cast<object>().Count() == 4,
    "All maps must include the full catalog with Arc checkboxes and no console selection columns.");
Check(!editor.FindControl<StackPanel>("CatalogActions")!.IsVisible, "Console filter actions must be hidden in the pool editor.");
editorTable.SelectedKeys = editorTable.ItemsSource!.Cast<object>().ToArray(); Layout();
Check(!editorTable.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "PART_Footer" && b.IsVisible),
    "The table's built-in selection footer must stay hidden after Select All.");
Check(editor.GetVisualDescendants().OfType<TKCode.Arc.FilterToolbar>().Single().Fields.Any(f => f.Id == "pool"),
    "The map editor must offer the pool filter.");
var poolSnapshot = model.MapPoolsAsync(new(MapPoolLibraryAction.List, mapInstance));
PumpUntil(poolSnapshot);
var editorPool = poolSnapshot.Result.Library.Pools.Single();
PumpUntil(editor.AttachAsync(editorContext with { PoolId = editorPool.Id })); Layout();
Check(editorTable.ItemsSource!.Cast<object>().Count() == editorPool.Maps.Length && (editorTable.SelectedKeys?.Count ?? 0) == 0,
    "Switching to a pool must show only its members and clear selection.");
daemon.IncludeWorkshopCollection = true;
PumpUntil(editor.AttachAsync(editorContext)); Layout();
var collectionTree = editor.FindControl<TKCode.Arc.TreeTable>("CollectionTree")!;
Check(collectionTree.IsVisible && collectionTree.ItemsSource!.Cast<object>().Count() == 5,
    "Importing a collection must append a branch to the existing map editor tree without crashing.");
PumpUntil(editor.ReloadAsync()); Layout();
daemon.IncludeWorkshopCollection = false;
editorWindow.Close(); Layout();
Console.WriteLine("PASS gui.maps.catalog-filters-selection-and-rotation-hint");
if (only is "maps") return 0;
}
if (only is null or "forms")
{
var saveCalls = 0;
HCSL.Frontend.HostInstanceDraft? savedDraft = null;
var editor = owner.EditInstanceAsync(null, draft =>
{
    saveCalls++; savedDraft = draft; return Task.FromResult<string?>(null);
});
Layout();
var inputs = owner.FindControl<ContentControl>("DialogContent")!.GetVisualDescendants().OfType<TKCode.Arc.Input>().ToArray();
Check(inputs.Length == 2 && string.IsNullOrEmpty(inputs[1].Text), "Creation must show only name and initially empty port fields.");
var dialogSurface = owner.FindControl<Border>("DialogSurface")!;
var dialogOverlay = owner.FindControl<Border>("DialogOverlay")!;
var modalPresenter = owner.GetVisualDescendants().OfType<ContentPresenter>().Single(p => p.Name == "PART_ModalPresenter");
Check(dialogSurface.Bounds.Width == 320 && dialogSurface.Background is Avalonia.Media.ISolidColorBrush { Opacity: 0.8 },
    "Instance editor must use a half-width, 80%-opaque material without fading its foreground.");
Check(WindowModalLayer.GetIsOpen(owner) && modalPresenter.IsVisible &&
      !modalPresenter.GetVisualAncestors().OfType<VisualLayerManager>().Any() &&
      dialogOverlay.TranslatePoint(default, owner) == new Point(0, 0) && dialogOverlay.Bounds.Size == owner.ClientSize,
    "Modal scrim must cover the full client including title chrome, above the transient layer manager.");
var form = (StackPanel)owner.FindControl<ContentControl>("DialogContent")!.Content!;
Check(form.Spacing == 12, "Form spacing must use Space3 (12px), not Space16 (64px).");
var formButtons = form.GetVisualDescendants().OfType<TKCode.Arc.Button>().Where(b => b.Content is "取消" or "保存").ToArray();
Check(formButtons.Length == 2 && formButtons.All(b => b.Bounds.Height == 32 && b.BorderThickness == default) &&
      formButtons.Single(b => Equals(b.Content, "保存")).Variant == ButtonVariant.Primary,
    "Save and cancel must have equal 32px heights, no frame stroke, and a primary save action.");
Check(inputs.All(input => input.GetVisualDescendants().OfType<FieldFrame>().Single(f => f.Name == "PART_Frame").BorderThickness == default),
    "Editor fields must use shadow emphasis instead of an outlined frame.");
var viewport = owner.FindControl<ContentControl>("DialogContent")!.GetVisualAncestors().OfType<ScrollContentPresenter>().First();
foreach (var elevated in form.GetVisualDescendants().OfType<ElevatedSurface>())
{
    var origin = elevated.TranslatePoint(default, viewport)!.Value;
    Check(origin.X >= 31.9 && origin.Y >= 31.9 &&
          origin.X + elevated.Bounds.Width <= viewport.Bounds.Width - 31.9 &&
          origin.Y + elevated.Bounds.Height <= viewport.Bounds.Height - 31.9,
        $"Control shadow must have a real 32px viewport gutter, not ScrollViewer.Padding: {elevated.Bounds}, origin={origin}, viewport={viewport.Bounds}.");
}
inputs[0].Focus(); Layout();
var nameElevation = (ElevatedSurface)inputs[0].GetVisualParent()!;
WaitUntil(() => nameElevation.Children.OfType<Border>().Single(b => b.Classes.Contains("surface-active-shadow")).Opacity == 1);
Check(nameElevation.Children.OfType<Border>().Single(b => b.Classes.Contains("surface-resting-shadow")).Opacity == 0,
    "Focus styles must override resting shadow opacity; no sticky local opacity values.");
inputs[0].Text = "New instance"; inputs[1].Text = "not-a-port";
Click(owner, "保存");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("HCSL-INSTANCE-PORT") == true));
Check(saveCalls == 0 && !editor.IsCompleted, "Invalid input must retain the dialog without saving.");
inputs[1].Text = "27017";
Check(inputs.All(input => !input.IsReadOnly), "Creation must not include the read-only address field.");
Check(!owner.GetVisualDescendants().OfType<TKCode.Arc.Select>().Any(s => s.ItemsSource is string[]), "Instance form must not imply a bind/isolation mode.");
Click(owner, "保存");
PumpUntil(editor);
Check(editor.Result && saveCalls == 1 && savedDraft is { GamePort: 27017, Network: null }, "Saving must not write an automatically discovered address into instance settings.");
var cancelEditor = owner.EditInstanceAsync(null, _ => { saveCalls++; return Task.FromResult<string?>(null); });
Layout();
Click(owner, "取消");
PumpUntil(cancelEditor);
Check(!cancelEditor.Result && saveCalls == 1, "Cancelled editor must not save.");
var closeEditor = owner.EditInstanceAsync(null, _ => { saveCalls++; return Task.FromResult<string?>(null); });
Layout(); Click(owner, "×"); PumpUntil(closeEditor);
Check(!closeEditor.Result && saveCalls == 1 && !WindowModalLayer.GetIsOpen(owner) && !modalPresenter.IsVisible,
    "The top-right close action must cancel without saving and fully release the modal layer.");
Console.WriteLine("PASS gui.instances.editor-two-inputs-empty-port-equal-actions-shadow-gutters-modal-layer-save-and-cancel");
var presetCalls = 0;
PresetDefinition? savedPreset = null;
var presetEditor = owner.EditPresetAsync("新建配置预设", null, false, draft =>
{
    presetCalls++; savedPreset = draft; return Task.FromResult<string?>(null);
});
Layout();
var presetInputs = owner.FindControl<ContentControl>("DialogContent")!.GetVisualDescendants().OfType<TKCode.Arc.Input>().ToArray();
Check(presetInputs.Length == 4, "Preset editor must contain name, map, game_type and game_mode.");
presetInputs[0].Text = "Practice"; presetInputs[1].Text = "workshop/123/map";
Click(owner, "保存");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("HCSL-PRESET-INVALID") == true));
Check(presetCalls == 0 && !presetEditor.IsCompleted, "Invalid map must not be saved.");
presetInputs[1].Text = "de_mirage"; presetInputs[2].Text = "0"; presetInputs[3].Text = "2";
Click(owner, "保存"); PumpUntil(presetEditor);
Check(presetEditor.Result && savedPreset is { Name: "Practice", Map: "de_mirage", GameMode: 2 }, "Preset fields were lost.");
var sourcePreset = savedPreset! with { Id = Guid.NewGuid(), Revision = 4, VacPreferred = false };
var copyEditor = owner.EditPresetAsync("复制配置预设", sourcePreset, true, draft =>
{
    presetCalls++; savedPreset = draft; return Task.FromResult<string?>(null);
});
Layout();
Check(owner.FindControl<ContentControl>("DialogContent")!.GetVisualDescendants().OfType<TKCode.Arc.Input>().Count() == 1, "Copy dialog must edit only the name.");
Click(owner, "保存"); PumpUntil(copyEditor);
Check(copyEditor.Result && savedPreset is { Revision: 4, VacPreferred: false } && savedPreset.Name.EndsWith("副本"), "Copy draft modified source settings.");
var cancelledPreset = owner.EditPresetAsync("编辑配置预设", sourcePreset, false, _ => { presetCalls++; return Task.FromResult<string?>(null); });
Layout(); Click(owner, "取消"); PumpUntil(cancelledPreset);
Check(!cancelledPreset.Result && presetCalls == 2, "Cancelled preset editor saved changes.");
Console.WriteLine("PASS gui.presets.editor-validation-create-copy-preserve-settings-and-cancel");
var workshopEditor = owner.EditPresetAsync("工坊启动地图", sourcePreset, false, draft => { savedPreset = draft; return Task.FromResult<string?>(null); });
Layout();
owner.GetVisualDescendants().OfType<TKCode.Arc.Select>().Single(s => s.IsEffectivelyVisible).SelectedIndex = 1;
owner.FindControl<ContentControl>("DialogContent")!.GetVisualDescendants().OfType<TKCode.Arc.Input>().ToArray()[1].Text = "3070244462";
Click(owner, "保存"); PumpUntil(workshopEditor);
Check(savedPreset is { MapKind: ServerMapKind.Workshop, Map: "3070244462", VacPreferred: false }, "Workshop editor lost source/ID or changed VAC.");
var mapTarget = owner.MapTargetAsync(); Layout();
var mapValue = owner.GetVisualDescendants().OfType<TKCode.Arc.Input>().Single(t => t.IsEffectivelyVisible);
mapValue.Text = "de_dust2;quit"; Click(owner, "下一步");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("HCSL-MAP-INVALID") == true));
Check(!mapTarget.IsCompleted && mapValue.Text == "de_dust2;quit", "Invalid map target was accepted or draft discarded.");
Click(owner, "取消"); PumpUntil(mapTarget); Check(mapTarget.Result is null, "Cancelled map target returned a command.");
Console.WriteLine("PASS gui.maps.workshop-preset-invalid-target-preserved-and-cancel");
if (only is "forms") return 0;
}

HCSL.Host.Views.Pages.InstancePresetsPage presetPage = null!;
PresetDefinition firstPreset = null!;
if (only is null or "presets" or "overview" or "admins" or "pools" or "plugins" or "frameworks" or "cfg" or "maps" or "start")
{
firstPreset = new PresetDefinition(1, Guid.NewGuid(), 1, "Selected preset");
var secondPreset = firstPreset with { Id = Guid.NewGuid(), Name = "Browsed preset", Map = "de_nuke" };
var presetInstanceId = Guid.NewGuid();
daemon.Catalog = new([new(presetInstanceId, new(1, presetInstanceId, 1, "Preset GUI fixture", 27015, firstPreset.Id), [firstPreset, secondPreset], HasPassword: true)]);
daemon.StoredPassword = "hunter2";
daemon.IdleForPresetTests = true;
PumpUntil(server.RefreshAsync()); PumpUntil(model.RefreshInstancesAsync());
((ShellViewModel)owner.DataContext!).NavigateCommand.Execute("instance");
((ShellViewModel)owner.DataContext!).SelectInstanceSection("presets");
Layout();
presetPage = owner.GetVisualDescendants().OfType<HCSL.Host.Views.Pages.InstancePresetsPage>().Single();
var presetChoices = presetPage.FindControl<TKCode.Arc.Table>("PresetChoices")!;
var presetItems = ((System.Collections.IEnumerable)presetChoices.ItemsSource!).Cast<HostPresetItem>().ToArray();
Check(presetItems.Length == 2, "GUI preset list missing catalog entries.");
var rowAction = presetChoices.GetVisualDescendants().OfType<TKCode.Arc.Button>().Single(b =>
    b.Classes.Contains("preset-row-action") && b.DataContext is HostPresetItem item && item.Definition.Id == secondPreset.Id);
rowAction.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
Check(model.Selected!.Entry.Definition!.SelectedPresetId == firstPreset.Id, "Browsing changed persisted selection.");
var presetMenu = (TKCode.Arc.GlassMenu)typeof(HCSL.Host.Views.Pages.InstancePresetsPage)
    .GetField("rowMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(presetPage)!;
presetMenu.Items.OfType<TKCode.Arc.GlassMenuItem>().Single(item => Equals(item.Content, "设为启动预设"))
    .RaiseEvent(new RoutedEventArgs(TKCode.Arc.GlassMenuItem.ClickEvent));
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "选择配置预设"));
Click(owner, "取消");
WaitUntil(() => presetPage?.FindControl<TKCode.Arc.Button>("CreateButton") is { IsEnabled: true });
Check(model.Selected.Entry.Definition.SelectedPresetId == firstPreset.Id, "Cancelled selection changed preset.");
if (only is "presets") { Console.WriteLine("PASS only presets"); return 0; }
}

if (only is null or "admins")
{
// 管理员 belongs to a preset: the preset scope owns it, and the row hides when the preset has no admin framework.
var shellForAdmins = (ShellViewModel)owner.DataContext!;
shellForAdmins.OpenPresetScope(firstPreset.Id); Layout();
daemon.AdminUnavailable = true;
((MainWindow)owner).RefreshAdminAvailability();
for (var frame = 0; frame < 8; frame++) { Layout(); Thread.Sleep(10); }
var presetSidebar = owner.FindControl<TKCode.Arc.Sidebar>("PresetSidebar")!;
var adminItem = presetSidebar.Sections.SelectMany(s => s.Items).Single(i => i.Key == "admins");
Check(!adminItem.IsVisible, "The 管理员 section must be hidden when the preset has no admin-capable framework.");
daemon.AdminUnavailable = false;
((MainWindow)owner).RefreshAdminAvailability();
for (var frame = 0; frame < 8; frame++) { Layout(); Thread.Sleep(10); }
Check(adminItem.IsVisible, "The 管理员 section must return once an admin-capable framework is enabled.");
var readsBeforeAdminPage = daemon.AdminReads;
shellForAdmins.SelectPresetSection("admins"); Layout();
for (var frame = 0; frame < 8; frame++) { Layout(); Thread.Sleep(10); }
var adminReads = daemon.AdminReads;
for (var frame = 0; frame < 12; frame++) { Layout(); Thread.Sleep(10); }
var adminsPage = owner.GetVisualDescendants().OfType<HCSL.Host.Views.Pages.InstanceAdminsPage>().Single();
var adminTable = adminsPage.FindControl<TKCode.Arc.Table>("Admins")!;
Check(daemon.AdminReads == adminReads && adminReads - readsBeforeAdminPage <= 4,
    $"The admin page must settle after at most the initial read, not reload on its own Busy notifications ({adminReads} -> {daemon.AdminReads}).");
Check(adminTable.ItemsSource is System.Collections.IEnumerable adminRows && adminRows.Cast<object>().Count() == 1,
    "Admin table missing entries (source: " + (adminTable.ItemsSource?.GetType().Name ?? "null") + ").");
shellForAdmins.ExitPresetScope(); Layout();
Check(!shellForAdmins.IsPresetScope && shellForAdmins.IsInstancePresets, "Leaving the preset scope must return to the preset list of the instance.");
Console.WriteLine("PASS gui.admins.single-read-no-reload-loop");
if (only is "admins") return 0;
}

if (only is null or "overview")
{
((ShellViewModel)owner.DataContext!).SelectInstanceSection("overview"); Layout();
var overview = owner.GetVisualDescendants().OfType<HCSL.Host.Views.Pages.InstanceOverviewPage>().Single();
// The scope names itself in the title bar: back arrow + "实例管理 · <instance>" beside the status badge.
var scopeBack = owner.FindControl<TKCode.Arc.Button>("ScopeBackButton")!;
var scopeTitle = owner.FindControl<TextBlock>("ScopeTitleText")!;
var scopeModel = (ShellViewModel)owner.DataContext!;
Check(scopeBack.IsEffectivelyVisible && scopeTitle.IsEffectivelyVisible
    && scopeTitle.Text == "实例管理 · " + scopeModel.Servers.Selected!.Entry.Definition!.Name,
    "The instance scope must put the back arrow and the instance name in the title bar.");
var portField = overview.FindControl<TKCode.Arc.Input>("PortInput")!;
var writes = daemon.InstanceWrites.Count;
daemon.AllowInstanceWrites = true;
Check(overview.FindControl<TKCode.Arc.Input>("NameInput") is null,
    "The overview must not edit the instance name; that belongs to the instance list.");
portField.Focus();
portField.Text = "27077";
owner.KeyPress(Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, ""); Layout();
WaitUntil(() => daemon.InstanceWrites.Count == writes + 1);
Check(daemon.InstanceWrites[^1].Action == InstanceAction.Update && daemon.InstanceWrites[^1].GamePort == 27077
    && daemon.InstanceWrites[^1].Name == model.Selected!.Entry.Definition!.Name,
    "Inline port edit must save through the instance record without touching the name.");
// Escape puts the stored value back instead of saving the half-typed one.
portField.Focus();
portField.Text = "27099";
owner.KeyPress(Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, ""); Layout();
Check(portField.Text == model.Selected!.Entry.Definition!.GamePort.ToString(), "Escape must restore the stored port.");
// The password is loaded once and then survives polling: a status tick must not wipe the field.
var passwordField = overview.FindControl<TKCode.Arc.PasswordField>("PasswordInput")!;
WaitUntil(() => passwordField.Text == "hunter2");
PumpUntil(server.RefreshAsync()); Layout();
Check(passwordField.Text == "hunter2", "Polling must not clear the loaded password.");
passwordField.Focus();
passwordField.Text = "";
owner.KeyPress(Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.None, ""); Layout();
WaitUntil(() => daemon.StoredPassword.Length == 0);
Check(passwordField.Text == "", "An empty password field must clear the stored password on Enter.");
var copyId = overview.FindControl<TKCode.Arc.CopyButton>("CopyIdButton")!;
Check(copyId.Value == model.Selected.Entry.Id.ToString("D"), "The instance ID row needs a copy button holding the full id.");
Check(overview.GetVisualDescendants().OfType<TKCode.Arc.SettingsRow>().All(r => r.Header != "连接地址"),
    "The connection-address row must be gone from the overview.");
// The scope's runtime entries are action rows in the sidebar's 运行 section, not controls inside the pages.
var scopeSidebar = owner.FindControl<TKCode.Arc.Sidebar>("InstanceSidebar")!;
var scopeItems = scopeSidebar.Sections.SelectMany(s => s.Items).ToArray();
var startItem = scopeItems.Single(i => i.Key == "start");
var consoleItem = scopeItems.Single(i => i.Key == "console");
Check(!startItem.IsAction && !consoleItem.IsAction && consoleItem.IsEnabled && consoleItem.IsVisible,
    "The 运行 rows must render like the navigation rows they sit beside.");
Check(!ReferenceEquals(scopeSidebar.SelectedItem, startItem) && !ReferenceEquals(scopeSidebar.SelectedItem, consoleItem),
    "Invoking an action row must leave the section selection alone.");
Check(!overview.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => Equals(b.Content, "启动服务器")),
    "Runtime actions must not be duplicated inside the overview page.");
owner.InvokeInstanceScopeAction("console"); Layout();
Check(owner.OwnedWindows.Any(w => w is HCSL.Host.Views.ServerLogWindow),
    "The console action row must open the console window.");
foreach (var owned in owner.OwnedWindows.ToArray()) owned.Close();
Layout();
scopeBack.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
Check(!scopeModel.IsInstance && !scopeBack.IsEffectivelyVisible,
    "The title-bar back arrow must leave the scope and hide itself.");
Console.WriteLine("PASS gui.overview.inline-instance-settings-save-on-enter");
if (only is "overview") return 0;
}

if (only is null or "pools")
{
var poolQueries = daemon.Queries;
var poolDialog = owner.EditMapPoolAsync(model, model.Selected!.Entry.Definition!, firstPreset); Layout();
var poolSelect = owner.GetVisualDescendants().OfType<TKCode.Arc.Select>().Single(s => s.IsEffectivelyVisible && s.ItemsSource is string[]);
poolSelect.SelectedIndex = 1; Click(owner, "上移");
WaitUntil(() => poolSelect.ItemsSource!.Cast<string>().First().Contains("de_mirage"));
daemon.RejectPoolWrites = true; Click(owner, "保存地图池");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("HCSL-MAPPOOL-CONFLICT") == true));
Check(poolSelect.ItemsSource!.Cast<string>().First().Contains("de_mirage") && daemon.PoolWrites == 0, "Conflict lost map pool draft.");
daemon.RejectPoolWrites = false; Click(owner, "保存地图池");
WaitUntil(() => daemon.PoolWrites == 1);
Check(daemon.Pool.Pool.Maps[0].Value == "de_mirage" && daemon.Queries == poolQueries, "Pool save reordered incorrectly or sent a game command.");
Click(owner, "删除"); WaitUntil(() => poolSelect.ItemsSource!.Cast<string>().Count() == 1);
Click(owner, "关闭"); WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "未保存的地图池"));
Click(owner, "继续编辑"); WaitUntil(() => poolSelect.IsEffectivelyVisible);
Check(poolSelect.ItemsSource!.Cast<string>().Count() == 1, "Cancelled close lost draft.");
Click(owner, "关闭"); WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "未保存的地图池"));
Click(owner, "丢弃草稿"); PumpUntil(poolDialog);
Console.WriteLine("PASS gui.map-pool-order-save-conflict-preserves-draft-no-console-and-dirty-close");
if (only is "pools") return 0;
}

if (only is null or "plugins")
{
daemon.IdleForPresetTests = true;
PumpUntil(server.RefreshAsync());
var pluginQueries = daemon.Queries;
daemon.Plugins = new([new("sample", "Sample plugin", "1.0.0", "", 1, 2)], [], 1, "plugin-1");
var pluginShell = (ShellViewModel)owner.DataContext!;
pluginShell.NavigateCommand.Execute("instance");
pluginShell.OpenPresetScope(model.Selected!.Entry.Definition!.SelectedPresetId);
pluginShell.SelectPresetSection("plugins"); Layout();
var pluginPage = owner.GetVisualDescendants().OfType<HCSL.Host.Views.Pages.PresetPluginsPage>().Single();
Check(pluginPage.IsEffectivelyVisible, "Plugin test must open the preset plugin page.");
pluginPage.FindControl<TKCode.Arc.ExpandingButtonGroupItem>("RefreshButton")!
    .RaiseEvent(new RoutedEventArgs(TKCode.Arc.ExpandingButtonGroupItem.ClickEvent));
TKCode.Arc.Checkbox? pluginBox = null;
WaitUntil(() => (pluginBox = owner.GetVisualDescendants().OfType<TKCode.Arc.Checkbox>().FirstOrDefault(b =>
    b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == "启用 Sample plugin")) is not null);
var writesBefore = daemon.PluginWrites;
Check(pluginBox!.IsEffectivelyEnabled, "Stopped test server must allow changing plugins.");
pluginBox.IsChecked = true;
WaitUntil(() => daemon.PluginWrites == writesBefore + 1);
Check(daemon.Plugins.Selected.Single() is { Id: "sample", Enabled: true } && daemon.Queries == pluginQueries,
    "Plugin save changed live game state or lost toggle.");
pluginShell.ExitPresetScope(); Layout();
Console.WriteLine("PASS gui.plugins.toggle-save-no-console");
if (only is "plugins") return 0;
}

if (only is null or "frameworks")
{
daemon.Plugins = new(
    [new("metamod", "Metamod", "1.0.0", "hash", 2, 2, "metamod", true),
     new("counterstrikesharp", "CounterStrikeSharp", "1.0.0", "hash", 1, 1, "counterstrikesharp", true),
     new("source2mod", "Source2Mod", "1.0.0", "hash", 1, 1, "source2mod", true)],
    [new("metamod", "Metamod", "1.0.0", true, true)], 1, "plugin-fw");
var frameworkInstalls = daemon.FrameworkInstalls;
var frameworkDialog = owner.EditFrameworksAsync(model, model.Selected!.Entry.Definition!, firstPreset); Layout();
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("脚本框架：未启用") == true));
WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "更新到 1.0.46")));
Check(!owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "已安装") && b.IsEffectivelyEnabled),
    "An up-to-date framework still offered a download button.");
Check(owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible &&
    t.Text?.Contains("基础框架：Metamod:Source 1.0.0（已启用）", StringComparison.Ordinal) == true),
    "Metamod must be reported separately from the scripting framework.");
var download = owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().First(b => b.IsEffectivelyVisible && Equals(b.Content, "更新到 1.0.46"));
Check(download.IsEffectivelyEnabled, "Available framework download was disabled.");
Click(owner, "更新到 1.0.46");
WaitUntil(() => daemon.FrameworkInstalls == frameworkInstalls + 1);
Click(owner, "关闭"); PumpUntil(frameworkDialog);
daemon.Plugins = new(
    [new("metamod", "Metamod", "1.0.0", "hash", 2, 2, "metamod", true),
     new("counterstrikesharp", "CounterStrikeSharp", "1.0.0", "hash", 1, 1, "counterstrikesharp", true),
     new("source2mod", "Source2Mod", "1.0.0", "hash", 1, 1, "source2mod", true)],
    [new("metamod", "Metamod", "1.0.0", true, true), new("counterstrikesharp", "CounterStrikeSharp", "1.0.0", true, true)], 1, "plugin-fw2");
var compatDialog = owner.EditFrameworksAsync(model, model.Selected!.Entry.Definition!, firstPreset); Layout();
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("脚本框架：CounterStrikeSharp") == true));
WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "切换到此框架")));
Click(owner, "关闭"); PumpUntil(compatDialog);
Console.WriteLine("PASS gui.frameworks.download-and-incompatible-card");
if (only is "frameworks") return 0;
}

if (only is null or "baseline")
{
    // The baseline lives on the settings page now; the downloads page only links to it after a failed refresh.
    ((ShellViewModel)owner.DataContext!).NavigateCommand.Execute("settings"); Layout();
    baseline.RefreshStatusAsync().GetAwaiter().GetResult();
    Layout();
    var refreshButton = owner.GetVisualDescendants().OfType<TKCode.Arc.Button>()
        .First(b => Equals(b.Content, "校验并重建"));
    Check(refreshButton.IsEffectivelyVisible && refreshButton.IsEffectivelyEnabled, "Missing baseline must offer a rebuild button.");
    var statusText = owner.GetVisualDescendants().OfType<TextBlock>()
        .First(t => t.Text?.Contains("安装 BuildId 26598758", StringComparison.Ordinal) == true).Text!;
    Check(statusText.Contains("缺失", StringComparison.Ordinal), "Baseline NONE state not shown: " + statusText);
    refreshButton.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    // The rebuild asks for confirmation in the shared overlay; its button carries the same label as the page button.
    var dialogContent = owner.FindControl<ContentControl>("DialogContent")!;
    WaitUntil(() => dialogContent.GetVisualDescendants().OfType<TKCode.Arc.Button>()
        .Any(b => Equals(b.Content, "校验并重建")));
    dialogContent.GetVisualDescendants().OfType<TKCode.Arc.Button>()
        .Single(b => Equals(b.Content, "校验并重建")).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    WaitUntil(() => daemon.BaselineRefreshes == 1);
    WaitUntil(() => baseline.NeedsRefresh == false);
    Check(baseline.StatusText.Contains("本地", StringComparison.Ordinal), "Baseline status did not become local: " + baseline.StatusText);
    // The page reports the result through the shared overlay; close it before continuing.
    WaitUntil(() => dialogContent.GetVisualDescendants().OfType<TKCode.Arc.Button>()
        .Any(b => Equals(b.Content, "返回")));
    dialogContent.GetVisualDescendants().OfType<TKCode.Arc.Button>()
        .Single(b => Equals(b.Content, "返回")).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
    Layout();

    // Busy host: the failure code must surface unchanged, not be rewritten.
    daemon.BaselineBusy = true;
    daemon.Baseline = new("26598758", "None");
    Check(!baseline.RefreshBaselineAsync().GetAwaiter().GetResult(), "Busy refresh claimed success.");
    Check(baseline.Status.Contains("HCSL-BASELINE-BUSY", StringComparison.Ordinal), "Busy code not preserved: " + baseline.Status);
    daemon.BaselineBusy = false;
    Console.WriteLine("PASS gui.baseline.status-refresh-confirm-and-host-code");
if (only is "baseline") return 0;
}

if (only is null or "errors")
{
    if (daemon.Catalog.Instances.Length == 0)
    {
        var errorPreset = new PresetDefinition(1, Guid.NewGuid(), 1, "Default");
        var errorInstance = Guid.NewGuid();
        daemon.Catalog = new([new(errorInstance, new(1, errorInstance, 1, "cs_test_dust_", 27015, errorPreset.Id), [errorPreset])]);
    }
    PumpUntil(model.RefreshInstancesAsync());
    ((ShellViewModel)owner.DataContext!).NavigateCommand.Execute("instance"); Layout();
    daemon.IdleForPresetTests = true; daemon.CfgRunIdentity = false;
    PumpUntil(server.RefreshAsync()); Layout();
    // A failed start must show the daemon's code plus the documented next step.
    daemon.StartFailureCode = "HCSL-DEPLOY-PLUGINS";
    daemon.StartFailureDetail = "Plugin packages may not write preset CFG, logs or recordings.";
    // The scope no longer owns 启动: the launch entry is the sidebar's 启动服务器 row.
    ((MainWindow)owner).InvokeInstanceScopeAction("start");
    WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "取消")));
    Click(owner, "启动");
    WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("HCSL-DEPLOY-PLUGINS") == true));
    Check(owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("hcsl doctor") == true),
        "Deployment failure dialog must include the actionable hint.");
    var notifications = owner.FindControl<ToastStack>("InstanceNotifications")!;
    Check(notifications.VisibleToasts > 0 && !WindowModalLayer.GetIsOpen(owner), "Start failure must use ToastStack, not a modal");
    var shell = (ShellViewModel)owner.DataContext!;
    foreach (var (code, path) in new[]
    {
        ("HCSL-DEPLOY-INSTALL-STATE", "「下载中心」"),
        ("HCSL-DEPLOY-BASELINE", "「设置 → 启动基线」")
    })
    {
        daemon.StartFailureCode = code;
        PumpUntil(server.RefreshAsync()); Layout();
        shell.NavigateCommand.Execute("instance"); Layout();
        ((MainWindow)owner).InvokeInstanceScopeAction("start");
        WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "启动")));
        Click(owner, "启动");
        WaitUntil(() => notifications.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => t.IsEffectivelyVisible && t.Text?.Contains(path, StringComparison.Ordinal) == true));
        Check(!WindowModalLayer.GetIsOpen(owner) && shell.SelectedPage.Id == "instance",
            "Recovery notice must not block the window or navigate");
        Check(!notifications.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
            .Any(b => b.IsEffectivelyVisible && b.Content is string label && label.StartsWith("前往", StringComparison.Ordinal)),
            "Recovery toast must not have navigation actions");
    }
    shell.NavigateCommand.Execute("instance"); Layout();
    daemon.StartFailureCode = null;
    // Deploy-only and journal cleanup stay reachable from the advanced rows; their daemon calls are exercised directly
    // so the fixture's request ordering cannot hide the outcome.
    PumpUntil(server.RefreshAsync()); Layout();
    var overviewPage = owner.GetVisualDescendants().OfType<HCSL.Host.Views.Pages.InstanceOverviewPage>().Single();
    Check(overviewPage.FindControl<TKCode.Arc.Button>("DeployButton") is { } deploy && Equals(deploy.Content, "部署"),
        "The advanced section must offer the manual deploy action.");
    Check(overviewPage.FindControl<TKCode.Arc.Button>("RecoverDeployButton") is { } recover && Equals(recover.Content, "清理"),
        "The advanced section must offer the interrupted-deployment cleanup.");
    PumpUntil(model.DeployAsync()); Layout();
    WaitUntil(() => daemon.Deploys == 1);
    Check(!server.Active && daemon.Stops == 0, "Deploy-only action must not start or stop the server.");
    PumpUntil(model.RecoverDeploymentAsync());
    WaitUntil(() => daemon.RecoverDeployments == 1);
    Check(!server.Active, "Deployment recovery must not start the server.");
    Console.WriteLine("PASS gui.errors.failed-start-hint-and-deploy-only");
    if (only is "errors") return 0;
}


if (only is null or "cfg")
{
var cfgQueries = daemon.Queries;
var cfgDialog = owner.EditCfgAsync(model, model.Selected!.Entry.Definition!, firstPreset);
Layout();
WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "刷新")));
Check(!owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible &&
    b.Content is "读取" or "新建" or "保存" or "重命名" or "删除"), "CFG page still exposes write actions.");
var cfgSelect = owner.GetVisualDescendants().OfType<TKCode.Arc.Select>().First(x => x.IsEffectivelyVisible);
cfgSelect.SelectedIndex = 0;
daemon.IdleForPresetTests = false; daemon.CfgRunIdentity = true; PumpUntil(server.RefreshAsync()); Layout();
Click(owner, "执行 exec");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("向运行服务器执行 CFG") == true));
Check(owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(server.RunId!.Value.ToString("D")) == true), "CFG exec confirmation omitted captured RunId.");
Click(owner, "取消");
WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>().Any(b => b.IsEffectivelyVisible && Equals(b.Content, "刷新")));
Check(daemon.Queries == cfgQueries, "Cancelled CFG exec was sent.");
Click(owner, "执行 exec");
WaitUntil(() => owner.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("向运行服务器执行 CFG") == true));
Click(owner, "执行 exec");
WaitUntil(() => daemon.Queries == cfgQueries + 1);
Check(daemon.LastConsole?.Text == "exec practice.cfg" && daemon.LastConsole.RunId == server.RunId, "Confirmed CFG exec target changed.");
Click(owner, "关闭"); PumpUntil(cfgDialog);
Console.WriteLine("PASS gui.cfg.readonly-tree-exec-confirmation-run-bound");
if (only is "cfg") return 0;
}

if (only is null or "start")
{
daemon.SimulateRunningServer();
daemon.IdleForPresetTests = false;
daemon.CfgRunIdentity = false; PumpUntil(server.RefreshAsync()); Layout();
((ShellViewModel)owner.DataContext!).SelectInstanceSection("presets"); Layout();
WaitUntil(() => server.Active && presetPage.IsVisible && !presetPage.FindControl<TKCode.Arc.Button>("CreateButton")!.IsEnabled);
Check(!presetPage.FindControl<TKCode.Arc.Button>("CreateButton")!.IsEnabled, "Active run must disable preset creation.");
((ShellViewModel)owner.DataContext!).NavigateCommand.Execute("home"); Layout();
Console.WriteLine("PASS gui.presets.catalog-browse-without-switch-cancel-selection-and-active-write-guards");
var normalStart = owner.ConfirmStartAsync("测试启动确认");
Layout();
Check(owner.GetVisualDescendants().OfType<TKCode.Arc.Checkbox>().Single(c => c.IsEffectivelyVisible).IsChecked != true, "Low-memory consent must default off.");
Click(owner, "启动"); PumpUntil(normalStart);
Check(normalStart.Result is { Confirmed: true, AllowLowMemory: false }, "Ordinary start implicitly consented.");
var debugStart = owner.ConfirmStartAsync("测试调试启动");
Layout();
owner.GetVisualDescendants().OfType<TKCode.Arc.Checkbox>().Single(c => c.IsEffectivelyVisible).IsChecked = true;
Click(owner, "启动"); PumpUntil(debugStart);
Check(debugStart.Result is { Confirmed: true, AllowLowMemory: true }, "Explicit debug consent missing.");
var nextStart = owner.ConfirmStartAsync("测试不保存同意");
Layout();
Check(owner.GetVisualDescendants().OfType<TKCode.Arc.Checkbox>().Single(c => c.IsEffectivelyVisible).IsChecked != true, "Consent leaked into the next start.");
owner.GetVisualDescendants().OfType<TKCode.Arc.Checkbox>().Single(c => c.IsEffectivelyVisible).IsChecked = true;
Click(owner, "取消"); PumpUntil(nextStart);
Check(nextStart.Result is { Confirmed: false, AllowLowMemory: false }, "Cancel must not return consent.");
Console.WriteLine("PASS gui.start.low-memory-default-explicit-one-shot-and-cancel");
var finalLog = new ServerLogWindow(model, owner);
finalLog.Show(owner);
owner.Close();
Layout();
Check(!finalLog.IsVisible, "Exit confirmation must hide log.");
Click(owner, "取消");
WaitUntil(() => finalLog.IsVisible);
Check(owner.IsVisible && daemon.Stops == 0, "Cancelling exit must retain both windows without stopping.");
owner.Close();
Layout();
Click(owner, "停服后退出");
WaitUntil(() => !owner.IsVisible);
Check(daemon.Stops == 1, "Confirmed exit must stop the captured server run exactly once.");
Check(!finalLog.IsVisible && !owner.OwnedWindows.Any(), "Confirmed exit must close auxiliary windows.");
Console.WriteLine("PASS gui.exit.stop-confirmed-closes-owned-logs");
if (only is "start") return 0;
}

if (only is not null)
{
    Console.Error.WriteLine($"Unknown test section '{only}'. Use input, background, sidebar, instances, shadow, theme, theme-transition, theme-raster, home, logs, forms, presets, overview, admins, pools, plugins, frameworks, cfg, maps, start, errors or all.");
    return 2;
}
return 0;

static string? ParseOnly(string[] args)
{
    string? only = null;
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--only")
        {
            if (i + 1 >= args.Length) throw new ArgumentException("--only requires a section name.");
            only = args[++i].ToLowerInvariant();
        }
        else if (args[i].StartsWith("--only=", StringComparison.Ordinal))
            only = args[i]["--only=".Length..].ToLowerInvariant();
    }
    return only is "all" ? null : only;
}

static void Layout()
{
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
}
static void WaitUntil(Func<bool> condition)
{
    var timeout = System.Diagnostics.Stopwatch.StartNew();
    while (!condition())
    {
        if (timeout.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("GUI operation timed out.");
        Layout(); Thread.Sleep(5);
    }
    Layout();
}
static void PumpUntil(Task task) { WaitUntil(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
// Overlays are re-opened asynchronously after a nested prompt resolves, so wait for the button
// instead of assuming it is already attached.
static void Click(Window owner, string label)
{
    WaitUntil(() => owner.GetVisualDescendants().OfType<TKCode.Arc.Button>()
        .Any(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled && Equals(b.Content, label)));
    owner.GetVisualDescendants().OfType<TKCode.Arc.Button>()
        .Single(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled && Equals(b.Content, label))
        .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
}

sealed class FakeDaemon : IDaemonClient
{
    private readonly Guid run = Guid.Parse("68fa2944-7d6b-4373-be01-368879164976");
    private bool stopped;
    public void SimulateRunningServer() => stopped = false;
    public int Stops { get; private set; }
    public int Queries { get; private set; }
    public ServerConsoleCommand? LastConsole { get; private set; }
    public int LastLogSequence { get; set; } = 150;
    public bool RepeatLogText { get; set; }
    public bool IdleForPresetTests;
    public string? StartFailureCode, StartFailureDetail;
    public Guid? FailedRun;
    public int Deploys;
    public int RecoverDeployments;
    public bool IncludeWorkshopCollection;
    public int PlanDeployments;
    public string DeployCode = "HCSL-SERVER-DEPLOYED";
    public bool CfgRunIdentity;
    public Guid? ListRunInstanceId;
    public int BaselineRefreshes;
    public bool BaselineBusy;
    public BaselineResult Baseline = new("26598758", "None");
    public int CfgWrites;
    public int PoolWrites, PoolReads;
    public int AdminReads;
    public bool AdminUnavailable;
    public string StoredPassword = "";
    public bool RejectPoolWrites;
    public int PluginWrites;
    public int FrameworkInstalls;
    public FrameworkCheckResult FrameworkCheck = new([
        new("metamod", "Metamod:Source", true, true, "2.0.0-dev+1490", "2.0.0-dev+1490", "mmsource-2.0.0-git1490-linux.tar.gz", false, null, null),
        new("counterstrikesharp", "CounterStrikeSharp", false, true, "1.0.0", "1.0.379", "counterstrikesharp-with-runtime-linux-1.0.379.zip", true, "metamod", null),
        new("source2mod", "Source2Mod", false, true, null, "1.0.46", "source2mod_v1.0.46-linux.tar.gz", false, "metamod", null)
    ]);
    public PluginResult Plugins = new([new("metamod", "Metamod", "1.0.0", "hash", 1024, 2)], [new("metamod", "Metamod", "1.0.0", true, true)], 1, "plugin-1");
    public bool PoolReadError;
    public MapPoolResult Pool = new(new(1, 1, [new(ServerMapKind.Standard, "de_dust2"), new(ServerMapKind.Standard, "de_mirage")]), "pool-1");
    private string cfgText = "status\n";
    private string cfgHash = "v1";
    public InstanceCatalog Catalog = new([]);
    public bool AllowInstanceWrites, RejectInstanceDeletes;
    public readonly List<InstanceCommand> InstanceWrites = [];
    public TaskCompletionSource? InstanceListGate;
    public Task<LocalResponse> SendAsync(LocalRequest request, CancellationToken ct = default)
    {
        if (request.Kind == RequestKind.Instances)
        {
            var command = JsonSerializer.Deserialize<InstanceCommand>(request.InstanceCommandJson!)!;
            LocalResponse CatalogResponse() => new(LocalProtocol.Version, request.Id, true, InstanceCatalogJson: JsonSerializer.Serialize(Catalog));
            if (command.Action == InstanceAction.List)
                return InstanceListGate is { } gate ? gate.Task.ContinueWith(_ => CatalogResponse()) : Task.FromResult(CatalogResponse());
            if (command.Action == InstanceAction.RevealPassword)
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true,
                    InstanceCatalogJson: JsonSerializer.Serialize(Catalog with { RevealedPassword = StoredPassword })));
            if (!AllowInstanceWrites) throw new Exception("Cancelled/browse-only GUI unexpectedly sent a metadata write.");
            InstanceWrites.Add(command);
            if (command.Action == InstanceAction.SetPassword)
            {
                StoredPassword = command.Password ?? "";
                Catalog = new(Catalog.Instances.Select(i => i.Id != command.InstanceId ? i : i with { HasPassword = StoredPassword.Length > 0 }).ToArray());
                return Task.FromResult(CatalogResponse());
            }
            if (command.Action == InstanceAction.Delete && RejectInstanceDeletes)
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, false, "HCSL-INSTANCE-CONFLICT", "Synthetic conflict."));
            var entry = Catalog.Instances.FirstOrDefault(i => i.Id == command.InstanceId);
            if (command.Action != InstanceAction.Create && entry?.Definition?.Revision != command.Revision)
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, false, "HCSL-INSTANCE-CONFLICT", "Revision changed."));
            if (command.Action == InstanceAction.Delete)
                Catalog = new(Catalog.Instances.Where(i => i.Id != command.InstanceId).ToArray());
            else if (command.Action == InstanceAction.Update)
                Catalog = new(Catalog.Instances.Select(i => i.Id != command.InstanceId ? i : i with
                { Definition = i.Definition! with { Name = command.Name!, GamePort = command.GamePort, Network = command.Network, Revision = command.Revision + 1 } }).ToArray());
            else if (command.Action == InstanceAction.Create)
            {
                var id = Guid.NewGuid(); var preset = new PresetDefinition(1, Guid.NewGuid(), 1, "Default");
                Catalog = new([.. Catalog.Instances, new(id, new(1, id, 1, command.Name!, command.GamePort, preset.Id, LayoutVersion: InstanceRules.CurrentLayoutVersion), [preset])]);
            }
            else throw new Exception("Unexpected instance write in GUI fixture.");
            return Task.FromResult(CatalogResponse());
        }
        if (request.Kind == RequestKind.MapPoolLibrary)
        {
            var pool = new MapPoolRecord(Guid.Parse("11111111-1111-1111-1111-111111111111"), "比赛池",
                [new ServerMapTarget(ServerMapKind.Standard, "de_dust2")]);
            MapPoolRecord[] pools = IncludeWorkshopCollection
                ? [pool, new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Maps by Ose",
                    [new(ServerMapKind.WorkshopCollection, "3503819307")], [new("12345", "aim_treeni")])]
                : [pool];
            var library = new MapPoolLibraryResult(new(1, 2, pools, new Dictionary<Guid, Guid>()), "library-hash");
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true,
                MapPoolLibraryResultJson: JsonSerializer.Serialize(library)));
        }
        if (request.Kind == RequestKind.MapCatalog)
        {
            var catalog = new MapCatalogResult(
            [
                new MapCatalogEntry("de_dust2", MapCatalogSource.Standard, null),
                new MapCatalogEntry("de_mirage", MapCatalogSource.Standard, null),
                new MapCatalogEntry("Workshop #3070244462", MapCatalogSource.Workshop, "3070244462", 1024),
                new MapCatalogEntry("Workshop #3120456789", MapCatalogSource.Workshop, "3120456789", 0, null, Incomplete: true)
            ], "fake catalog");
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, MapCatalogResultJson: JsonSerializer.Serialize(catalog)));
        }
        if (request.Kind == RequestKind.Admin)
        {
            var command = JsonSerializer.Deserialize<AdminCommand>(request.AdminJson!)!;
            if (command.Action == AdminAction.List) AdminReads++;
            if (AdminUnavailable)
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, false,
                    "HCSL-ADMIN-SOURCE", "No enabled framework exposes an admin list."));
            var adminSnapshot = new AdminListSnapshot("css", "CounterStrikeSharp", true, [], [],
                [new AdminEntry("76561198000000000", "Admin", ["#hcsl/admin"], [], null, [])], "admin-1", [], null, 1);
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true,
                AdminResultJson: JsonSerializer.Serialize(new AdminResult(adminSnapshot, "HCSL-ADMIN-OK", []))));
        }
        if (request.Kind == RequestKind.MapPool)
        {
            var c = JsonSerializer.Deserialize<MapPoolCommand>(request.MapPoolJson!)!;
            if (c.Action == MapPoolAction.Read) PoolReads++;
            if (c.Action == MapPoolAction.Read && PoolReadError)
                return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, false, "HCSL-MAPPOOL-READ", "Read failed."));
            if (c.Action == MapPoolAction.Save)
            {
                if (RejectPoolWrites) return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, false, "HCSL-MAPPOOL-CONFLICT", "External edit; reload."));
                if (!IdleForPresetTests) throw new Exception("Active pool write admitted by GUI.");
                PoolWrites++; Pool = new(new(1, Pool.Pool.Revision + 1, c.Maps!), "pool-" + (Pool.Pool.Revision + 1));
            }
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, MapPoolResultJson: JsonSerializer.Serialize(Pool)));
        }
        if (request.Kind == RequestKind.Baseline)
        {
            var command = JsonSerializer.Deserialize<BaselineCommand>(request.BaselineJson!)!;
            if (command.Action == BaselineAction.Refresh)
            {
                if (BaselineBusy) return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, false, "HCSL-BASELINE-BUSY", "Stop the server first."));
                BaselineRefreshes++;
                Baseline = new("26598758", "Local", "26598758", "/library/.hcsl/linux-startup-baseline.json");
            }
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, BaselineResultJson: JsonSerializer.Serialize(Baseline)));
        }
        if (request.Kind == RequestKind.FrameworkCheck)
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, FrameworkCheckJson: JsonSerializer.Serialize(FrameworkCheck)));
        if (request.Kind == RequestKind.FrameworkInstall)
        {
            var command = JsonSerializer.Deserialize<FrameworkInstallCommand>(request.FrameworkInstallJson!)!;
            FrameworkInstalls++;
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, FrameworkInstallResultJson: JsonSerializer.Serialize(
                new FrameworkInstallResult(command.Id, command.Version ?? "1.0.46", "/library/plugin-packages/" + command.Id + "/1.0.46", 1, 1, "sha", true, []))));
        }
        if (request.Kind == RequestKind.Plugin)
        {
            var c = JsonSerializer.Deserialize<PluginCommand>(request.PluginJson!)!;
            if (c.Action == PluginAction.Save)
            {
                if (!IdleForPresetTests) throw new Exception("Active plugin write admitted by GUI.");
                PluginWrites++;
                Plugins = new(Plugins.Available, (c.Plugins ?? []).Select(p => new PluginSelectionInfo(p.Id, "Metamod", p.Version, p.Enabled, true)).ToArray(), Plugins.Revision + 1, "plugin-" + (Plugins.Revision + 1));
            }
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, PluginResultJson: JsonSerializer.Serialize(Plugins)));
        }
        if (request.Kind == RequestKind.PresetCfg)
        {
            var c = JsonSerializer.Deserialize<PresetCfgCommand>(request.PresetCfgJson!)!;
            PresetCfgResult result;
            if (c.Action == PresetCfgAction.List) result = new([new("practice.cfg", cfgText.Length)]);
            else if (c.Action == PresetCfgAction.Read) result = new([], "practice.cfg", cfgText, cfgHash);
            else
            {
                if (c.Action != PresetCfgAction.Save) throw new Exception("Cancelled GUI action reached CFG storage.");
                PresetCfgRules.Name(c.Name); PresetCfgRules.Content(c.Content);
                cfgText = c.Content!; cfgHash = "v" + (++CfgWrites + 1);
                result = new([], c.Name, cfgText, cfgHash);
            }
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, PresetCfgResultJson: JsonSerializer.Serialize(result)));
        }
        if (request.Kind == RequestKind.ServerConsole)
        {
            var c = JsonSerializer.Deserialize<ServerConsoleCommand>(request.ServerConsoleJson!)!;
            if (c.RunId != run || (c.Text != "status" && c.Text != "mp_roundtime 60" && c.Text != "exec practice.cfg" && c.Text != "host_workshop_map 3070244462")) throw new Exception("GUI query identity/text/confirmation changed.");
            LastConsole = c;
            Queries++;
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, ConsoleReceiptJson: JsonSerializer.Serialize(new ServerConsoleReceipt(run, c.CommandId, ConsoleDelivery.Written, "HCSL-CONSOLE-WRITTEN", ServerConsoleRules.Display(c.Text)))));
        }
        var serverCommand = request.ServerCommandJson is { } json ? JsonSerializer.Deserialize<ServerCommand>(json) : null;
        if (serverCommand is { Action: ServerAction.Stop } stop)
        {
            if (stop.RunId != run) throw new Exception("Stop must carry the confirmed RunId.");
            stopped = true; Stops++;
        }
        if (serverCommand is { Action: ServerAction.Deploy }) Deploys++;
        if (serverCommand is { Action: ServerAction.RecoverDeployment }) RecoverDeployments++;
        if (serverCommand is { Action: ServerAction.PlanDeployment })
        {
            PlanDeployments++;
            var plan = new DeploymentPlan(Catalog.Instances.FirstOrDefault()?.Id ?? Guid.Empty, 1, Catalog.Instances.FirstOrDefault()?.Presets.FirstOrDefault()?.Id ?? Guid.Empty,
                1, "26445667", "/library/runtime/preview", DeploymentCopyMode.Reflink, 3140, 73434849358, 1024, 1024,
                [], ["Offline deployment only; no server is launched."], null, null, null, null);
            return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true, DeploymentPlanJson: JsonSerializer.Serialize(plan)));
        }
        var failing = serverCommand is { Action: ServerAction.Start } && StartFailureCode is not null;
        if (failing) FailedRun = Guid.NewGuid();
        var lines = Enumerable.Range(LastLogSequence - 149, 150).Select(i => new ServerLogLine(i, (RepeatLogText ? "repeat " : $"{i} ") + new string('x', 300))).ToArray();
        var snapshot = failing
            ? new ServerSnapshot(FailedRun, null, ServerState.Failed, false, StartFailureCode!, false, true, null, LastLogSequence + 1, false, lines, Detail: StartFailureDetail)
            : serverCommand is { Action: ServerAction.RecoverDeployment }
                ? new ServerSnapshot(run, null, ServerState.Stopped, false, "HCSL-SERVER-DEPLOY-RECOVERED", false, true, null, LastLogSequence + 1, false, lines, Detail: "PreviousDeploymentPreserved_StagingRetained")
                : serverCommand is { Action: ServerAction.Deploy }
                ? new ServerSnapshot(run, null, ServerState.Stopped, false, DeployCode, false, true, null, LastLogSequence + 1, false, lines, Detail: "Deployment published (Reflink).")
                : new ServerSnapshot(run, CfgRunIdentity ? Catalog.Instances.Single(i => ListRunInstanceId is null || i.Id == ListRunInstanceId).Id : null, stopped ? ServerState.Stopped : IdleForPresetTests ? ServerState.Idle : ServerState.Running,
                    !stopped && !IdleForPresetTests, stopped ? "HCSL-SERVER-STOPPED" : "HCSL-SERVER-MAP-READY", true, stopped, null, LastLogSequence + 1, false, lines,
                    Preset: CfgRunIdentity ? new(Catalog.Instances.Single(i => ListRunInstanceId is null || i.Id == ListRunInstanceId).Presets[0].Id, 1, "Selected preset", "de_dust2") : null);
        return Task.FromResult(new LocalResponse(LocalProtocol.Version, request.Id, true,
            Defaults: new("/settings", "/data", "/library"), ServerSnapshotJson: JsonSerializer.Serialize(snapshot)));
    }
}
