using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HCSL.Core;
using HCSL.Frontend;
using HCSL.Host.ViewModels;
using HCSL.Host.Views;
using HCSL.Host.Views.Pages;
using HCSL.UI.Shared.Controls;
using HCSL.UI.Shared.Services;
using Avalonia.Media;
using TKCode.Arc.Motion;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using TKCode.Arc.DesignTokens;
using ArcButton = TKCode.Arc.Button;

internal static class InstancesPageTests
{
    public static void Run(MainWindow owner, FakeDaemon daemon, HostServerController model, ServerController server)
    {
        var originalCatalog = daemon.Catalog;
        var originalIdle = daemon.IdleForPresetTests;
        var originalIdentity = daemon.CfgRunIdentity;
        var originalRunInstance = daemon.ListRunInstanceId;
        var originalWriteCount = daemon.InstanceWrites.Count;
        var shell = (ShellViewModel)owner.DataContext!;
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var preset = new PresetDefinition(1, Guid.NewGuid(), 1, "Practice");
        daemon.Catalog = new([
            new(first, new(1, first, 1, "Alpha", 27015, preset.Id, LayoutVersion: InstanceRules.CurrentLayoutVersion), [preset]),
            new(second, new(1, second, 1, "Beta", 27016, preset.Id, LayoutVersion: InstanceRules.CurrentLayoutVersion), [preset])
        ]);
        daemon.IdleForPresetTests = true;
        daemon.AllowInstanceWrites = true;
        Pump(server.RefreshAsync()); Pump(model.RefreshInstancesAsync());
        shell.NavigateCommand.Execute("instances");
        var page = owner.GetVisualDescendants().OfType<InstancesPage>().Single();
        var choices = page.FindControl<RadioGroup>("InstanceChoices")!;
        var actions = page.FindControl<ExpandingButtonGroup>("ToolbarActions")!;
        var search = page.FindControl<SearchField>("Search")!;
        var loading = page.FindControl<SkeletonMorph>("LoadingRegion")!;
        var popover = page.FindControl<Popover>("DeletePopover")!;
        var properties = page.FindControl<Popover>("PropertiesPopover")!;
        var hold = page.FindControl<HoldToConfirm>("DeleteConfirm")!;
        InstanceListRow[] Rows() => choices.ItemsSource!.Cast<InstanceListRow>().Where(r => r.IsPresent).ToArray();
        RadioGroupItem Container(Guid id) => (RadioGroupItem)choices.ContainerFromItem(Rows().Single(r => r.Id == id))!;
        GlassMenu Menu(string field) => (GlassMenu)typeof(InstancesPage).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        void Invoke(GlassMenu menu, string label)
        {
            var item = menu.Items.OfType<GlassMenuItem>().Single(i => i.Content?.ToString()?.StartsWith(label, StringComparison.Ordinal) == true);
            Check(item.IsEnabled, label + " should be enabled in this fixture.");
            item.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
        }
        GlassMenu OpenMenu(Guid id)
        {
            Container(id).RaiseEvent(new ContextRequestedEventArgs()); Layout();
            var menu = Menu("rowMenu");
            Check(menu.IsOpen, "The row context request must open GlassMenu.");
            return menu;
        }
        void OpenDelete(Guid id, bool pointerMenu = false)
        {
            GlassMenu deleteMenu;
            if (pointerMenu)
            {
                var at = Container(id).TranslatePoint(new Point(24, Container(id).Bounds.Height / 2), owner)!.Value;
                owner.MouseMove(at); owner.MouseDown(at, MouseButton.Right); owner.MouseUp(at, MouseButton.Right); Layout();
                deleteMenu = Menu("rowMenu");
                Check(deleteMenu.IsOpen, "Physical right-click must open deletion's source menu.");
            }
            else deleteMenu = OpenMenu(id);
            var origin = deleteMenu.PointToScreen(default);
            Invoke(deleteMenu, "删除实例"); Layout();
            Check(popover.IsOpen && popover.Anchor == page.FindControl<Border>("DeleteAnchor"),
                "Delete must use the captured menu-position anchor, not the instance row.");
            Wait(() => Math.Abs(hold.PointToScreen(default).X - origin.X) <= 2 && Math.Abs(hold.PointToScreen(default).Y - origin.Y) <= 2);
        }
        void Toolbar(string name) { Pump(actions.ActivateAsync(page.FindControl<ExpandingButtonGroupItem>(name)!)); Layout(); }
        // A closing menu keeps its light-dismiss overlay over the toolbar, and a loaded machine processes the
        // pointer move a tick later; both made the hover checks below read the previous expansion.
        void Hover(Point point)
        {
            owner.MouseMove(point); Layout();
            Wait(() => owner.GetVisualAt(point)?.GetType().Name != "LightDismissOverlayLayer");
            Layout();
        }
        try
        {
            Wait(() => Rows().Length == 2 && choices.GetVisualDescendants().OfType<RadioGroupItem>().Count() == 2 && model.CanCreate);
            Check(Rows()[0].Id == second, "Initial order must be name descending.");
            Check(loading.Content is not MorphBlock && loading.Background is ISolidColorBrush { Color.A: 0 } &&
                  choices.GetVisualDescendants().OfType<MorphBlock>().Count() == 6,
                "Loading must use three independent field blocks per row, never one full-list solid mask.");
            Check(!page.GetVisualDescendants().OfType<Table>().Any() && !page.GetVisualDescendants().OfType<RadioButton>().Any(), "No table/header/standalone radio should remain.");
            Check(choices.GetVisualDescendants().OfType<Panel>().Where(p => p.Name == "PART_Mark").All(p => !p.IsVisible), "The native radio marks must be hidden.");
            Check(choices.GetVisualDescendants().OfType<TKCode.Arc.Primitives.GlidingIndicator>().Single().IsVisible == false,
                "Selection must use elevation, not Arc's moving selection outline.");
            Check(Rows().All(r => r.CreatedAt == "暂无记录" && r.LastRunAt == "暂无记录" && r.StatusText == "已停止"), "Unavailable dates must not be invented.");
            var row = Container(first);
            var badge = row.GetVisualDescendants().OfType<Badge>().Single();
            Check(row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Row").Background is ISolidColorBrush { Opacity: > 0 and < 1 } &&
                  !choices.GetVisualDescendants().OfType<GlassCard>().Any() && row.Opacity == 1 && badge.Opacity == 1,
                "Rows must have a solid-color translucent fill, not a glass material or a faded foreground.");
            Check(badge.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Frame").Background is ISolidColorBrush { Opacity: < 1 } &&
                  actions.Background is ISolidColorBrush { Opacity: < 1 }, "Non-glass badge and toolbar backgrounds must be translucent.");
            Check(!row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Shadow").BoxShadow.Equals(default(BoxShadows)) &&
                  !badge.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Frame").BoxShadow.Equals(default(BoxShadows)),
                "Rows must have separate decorative shadow layers; badges retain their own lighter elevation.");
            Check(choices.Margin.Left > 0 && loading.Margin.Left == -choices.Margin.Left &&
                  row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Row").BoxShadow.Count == 0,
                "The clipping shell must reserve shadow gutters without applying shadow transforms to row content.");
            var badgeOrigin = badge.TranslatePoint(default, row)!.Value;
            Check(badge.HorizontalAlignment == HorizontalAlignment.Right && badge.VerticalAlignment == VerticalAlignment.Center &&
                  Math.Abs(badgeOrigin.Y + badge.Bounds.Height / 2 - row.Bounds.Height / 2) < 2 &&
                  row.Bounds.Width - badgeOrigin.X - badge.Bounds.Width < 30, "The badge must be aligned to the right and vertically centered in the row.");
            Check(search.TranslatePoint(default, page)!.Value.X == choices.TranslatePoint(default, page)!.Value.X,
                "Search must start at the list's left edge, not be centered in its toolbar lane.");
            var toolbar = page.FindControl<Grid>("InstancesToolbar")!;
            Check(toolbar.ColumnDefinitions.Count == 3 && toolbar.ColumnDefinitions[1].ActualWidth > 0 &&
                  toolbar.ColumnDefinitions[0].ActualWidth <= toolbar.ColumnDefinitions[1].ActualWidth * 3 + 1 &&
                  search.Bounds.Width <= toolbar.ColumnDefinitions[0].ActualWidth + 1 &&
                  Grid.GetColumn(page.FindControl<Grid>("ToolbarSlot")!) == 2,
                "Search must use the shorter three-quarter lane and leave spacing before the right-aligned action group.");
            Check(page.FindControl<ExpandingButtonGroupItem>("CreateButton")!.Icon!.ToString() == Lucide.Geometry(LucideIconKind.Plus).ToString(), "Create must use Lucide plus.");

            // Native keyboard selection, not a synthetic SelectionChanged event.
            ArcMotion.SetLevel(page, ArcMotionLevel.Off);
            row.Focus(NavigationMethod.Tab); SendKey(owner, Key.Space);
            Check(shell.StartupInstanceId == first && model.Selected?.Entry.Id == first && shell.IsInstances, "Selecting a row must choose startup without navigating to management.");
            var secondRow = Container(second); secondRow.Focus(NavigationMethod.Tab); SendKey(owner, Key.Space);
            Border Layer(RadioGroupItem item, string name) => item.GetVisualDescendants().OfType<Border>().Single(b => b.Name == name);
            Border Surface(RadioGroupItem item) => Layer(item, "PART_Row");
            Check(Layer(secondRow, "PART_SelectedShadow").BoxShadow.Equals(CenteredShadow.Center(ArcTokens.Get<BoxShadows>(secondRow, ArcDesignTokenKind.ShadowFloating))) &&
                  Layer(Container(first), "PART_Shadow").BoxShadow.Equals(CenteredShadow.Center(ArcTokens.Get<BoxShadows>(Container(first), ArcDesignTokenKind.ShadowRaised))) &&
                  Layer(secondRow, "PART_SelectedShadow").Opacity == 1 && Layer(Container(first), "PART_SelectedShadow").Opacity == 0 &&
                  Layer(secondRow, "PART_Shadow").Opacity == 0 && Layer(Container(first), "PART_Shadow").Opacity == 1 &&
                  Layer(secondRow, "PART_SelectedFill").Opacity > 0 &&
                  secondRow.ZIndex > Container(first).ZIndex &&
                  secondRow.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "PART_FocusRing"),
                "Selection must have a deeper centered shadow above its siblings, without removing the keyboard focus ring.");
            var normalFill = (ISolidColorBrush)Surface(Container(first)).Background!;
            var hoverPoint = Container(first).TranslatePoint(new Point(24, Container(first).Bounds.Height / 2), owner)!.Value;
            owner.MouseMove(hoverPoint); Layout();
            var hoverFill = (ISolidColorBrush)Surface(Container(first)).Background!;
            Check(Container(first).IsPointerOver && Layer(Container(first), "PART_HoverFill").Opacity > 0 &&
                  normalFill.Color == hoverFill.Color && normalFill.Opacity == hoverFill.Opacity &&
                  Container(first).Opacity == 1 && shell.StartupInstanceId == second,
                "Hover must reveal its own stable fill layer without sampling animated parent transparency or changing selection.");
            ArcMotion.SetLevel(choices, ArcMotionLevel.Full);
            owner.MouseDown(hoverPoint, MouseButton.Left); Layout();
            owner.MouseUp(hoverPoint, MouseButton.Left); Layout();
            Wait(() => Layer(Container(first), "PART_SelectedShadow").Opacity is > 0 and < 1);
            Check(Layer(Container(first), "PART_SelectedFill").Opacity is > 0 and < 0.6 && shell.StartupInstanceId == first,
                "A physical click must cross-fade both selected elevation and the subtle selected background.");
            for (var frame = 0; frame < 12; frame++)
            {
                Layout(); Thread.Sleep(8);
                Check(Surface(Container(first)).Background is ISolidColorBrush steady && steady.Color == normalFill.Color &&
                      steady.Opacity == normalFill.Opacity && Container(first).Opacity == 1,
                    "Press/release and selection must never fade the base card to a transparent blank frame.");
            }
            Wait(() => Layer(Container(first), "PART_SelectedShadow").Opacity == 1);
            ArcMotion.SetLevel(choices, ArcMotionLevel.Off);
            Container(second).Focus(NavigationMethod.Tab); SendKey(owner, Key.Space);
            choices.ClearValue(ArcMotion.LevelProperty);
            owner.MouseMove(new Point(1, 1)); Layout();
            Check(Surface(Container(first)).Background is ISolidColorBrush restored && restored.Color == normalFill.Color && restored.Opacity == normalFill.Opacity,
                "Leaving a row must restore its normal translucent surface.");
            ArcMotion.SetLevel(page, ArcMotionLevel.Off);
            var menu = OpenMenu(first);
            Check(shell.StartupInstanceId == second && choices.SelectedItem is InstanceListRow { Id: var selected } && selected == second,
                "Right-click must not change startup selection.");
            Check(menu.Items.OfType<GlassMenuItem>().Select(i => i.Content?.ToString()).SequenceEqual(new[]
                { "启动实例", "查看日志", "实例管理", "重命名实例", "属性", "删除实例" }), "Context actions must replace the operations column and include properties.");
            var menuPosition = menu.PointToScreen(default);
            Invoke(menu, "属性");
            var propertyAnchor = page.FindControl<Border>("PropertiesAnchor")!;
            var propertyPosition = propertyAnchor.PointToScreen(new Point(0, propertyAnchor.Height));
            Check(properties.Anchor == propertyAnchor && Math.Abs(propertyPosition.X - menuPosition.X) <= 2 && Math.Abs(propertyPosition.Y - menuPosition.Y) <= 2,
                "Property panel must use the actual menu origin, including keyboard placement, not the instance row's edge.");
            var card = page.FindControl<GlassCard>("PropertiesCard")!;
            Check(properties.IsOpen && card.DataContext is InstanceListRow { Id: var propertyId } && propertyId == first &&
                  shell.StartupInstanceId == second, "Properties must bind the clicked row, not the startup row.");
            Check(card.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "27015") &&
                  card.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == "暂无记录") == 2 &&
                  card.GetVisualDescendants().OfType<Badge>().Single().Content?.ToString() == "已停止", "Property values and status badge must be present.");
            Check(card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("instance-property-value"))
                .All(t => t.HorizontalAlignment == HorizontalAlignment.Right && t.TextAlignment == Avalonia.Media.TextAlignment.Right),
                "Property values must align right, separate from the left-aligned field labels.");
            var propertyBadge = card.GetVisualDescendants().OfType<Badge>().Single();
            Check(card.Opacity == 1 && propertyBadge.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Frame").Background is ISolidColorBrush propertyBrush &&
                  propertyBadge.Background is ISolidColorBrush originalBrush && propertyBrush.Opacity == originalBrush.Opacity,
                "Glass property-card content must not inherit the list's surface-opacity override.");
            properties.IsOpen = false; page.ClearValue(ArcMotion.LevelProperty); Layout();
            var pointer = Container(first).TranslatePoint(new Point(24, Container(first).Bounds.Height / 2), owner)!.Value;
            owner.MouseMove(pointer);
            owner.MouseDown(pointer, MouseButton.Right); owner.MouseUp(pointer, MouseButton.Right); Layout();
            Check(Menu("rowMenu").IsOpen && shell.StartupInstanceId == second,
                "Native right-click must open the context menu without changing startup selection.");
            menuPosition = Menu("rowMenu").PointToScreen(default);
            Invoke(Menu("rowMenu"), "属性");
            propertyPosition = propertyAnchor.PointToScreen(new Point(0, propertyAnchor.Height));
            Check(Math.Abs(propertyPosition.X - menuPosition.X) <= 2 && Math.Abs(propertyPosition.Y - menuPosition.Y) <= 2,
                "Property panel must also replace the pointer-positioned context menu at its actual origin.");
            properties.IsOpen = false; Layout();
            var poolReads = daemon.PoolReads;
            Invoke(OpenMenu(first), "实例管理"); Layout();
            Check(shell.IsInstance && model.Selected?.Entry.Id == first && shell.StartupInstanceId == second && daemon.PoolReads == poolReads,
                "Only explicit management action may navigate, without changing startup or reading hidden home pools.");
            shell.NavigateCommand.Execute("home"); Layout();
            Check(model.Selected?.Entry.Id == second, "Home must restore startup choice.");
            shell.NavigateCommand.Execute("instances"); Layout();
            ArcMotion.SetLevel(choices, ArcMotionLevel.Full);
            search.Text = "27016"; Layout();
            var leaving = choices.ItemsSource!.Cast<InstanceListRow>().Single(r => r.Id == first);
            var leavingPresence = choices.ContainerFromItem(leaving)!.GetVisualDescendants().OfType<TKCode.Arc.Primitives.PresenceHost>().Single(p => p.Name == "RowPresence");
            Check(!leaving.IsPresent && !leaving.CanSelect && choices.Items.Count == 2 && !leavingPresence.IsPresent && leavingPresence.Progress > 0,
                "A filtered row must remain non-interactive until Arc's exit fade completes.");
            Wait(() => leavingPresence.Opacity is > 0 and < 1);
            Check(ReferenceEquals(choices.ContainerFromItem(leaving)?.GetVisualDescendants().OfType<TKCode.Arc.Primitives.PresenceHost>().Single(p => p.Name == "RowPresence"), leavingPresence),
                "The actual fading host must remain mounted, not just the row's data object.");
            Check(Rows().Length == 1 && Rows()[0].Id == second, "Search must match port.");
            search.Text = "Practice"; Layout();
            Check(Rows().Length == 2 && leaving.IsPresent, "Clearing the filter before exit ends must cancel removal and restore the same row.");
            search.Text = "missing"; Layout();
            Wait(() => page.FindControl<EmptyState>("Empty")!.IsVisible);
            Check(Rows().Length == 0 && page.FindControl<EmptyState>("Empty")!.IsVisible && shell.StartupInstanceId == second, "Filtering must not clear startup identity.");
            search.Text = ""; Layout();
            ArcMotion.SetLevel(choices, ArcMotionLevel.Reduced);
            search.Text = "27016"; Layout();
            var reducedExit = choices.ContainerFromItem(leaving)!.GetVisualDescendants().OfType<TKCode.Arc.Primitives.PresenceHost>().Single(p => p.Name == "RowPresence");
            Check(choices.Items.Count == 2 && !reducedExit.IsPresent, "Reduced motion must retain the outgoing card for its short fade.");
            Wait(() => reducedExit.Opacity is > 0 and < 1);
            Wait(() => choices.Items.Count == 1);
            search.Text = ""; Layout();
            var retainedSource = choices.ItemsSource;
            var reorder = (AnimatedItemReorder)typeof(InstancesPage).GetField("reorder", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
            ArcMotion.SetLevel(choices, ArcMotionLevel.Full);
            Toolbar("SortButton"); Invoke(Menu("sortMenu"), "名称升序");
            Check(ReferenceEquals(retainedSource, choices.ItemsSource) && reorder.IsAnimating &&
                  choices.GetVisualDescendants().OfType<RadioGroupItem>().All(c => c.RenderTransform is TranslateTransform t && Math.Abs(t.Y) > 0.01),
                "Every reordered item must spring from its old position, even when Avalonia recreates its container.");
            Wait(() => !reorder.IsAnimating);
            Check(Rows()[0].Id == first && page.FindControl<ExpandingButtonGroupItem>("SortButton")!.Label == "名称升序" && shell.StartupInstanceId == second,
                "Sort menu must change order and label, not startup selection.");

            ArcMotion.SetLevel(choices, ArcMotionLevel.Reduced);
            Toolbar("SortButton"); Invoke(Menu("sortMenu"), "名称降序");
            Check(!reorder.IsAnimating && Rows()[0].Id == second, "Reduced motion must reorder without sliding.");
            ArcMotion.SetLevel(choices, ArcMotionLevel.Off);
            Toolbar("SortButton"); Invoke(Menu("sortMenu"), "名称升序");
            Check(!reorder.IsAnimating && Rows()[0].Id == first, "Off must immediately reach the sorted state.");
            choices.ClearValue(ArcMotion.LevelProperty);
            daemon.IdleForPresetTests = false; daemon.CfgRunIdentity = true; daemon.ListRunInstanceId = second;
            Pump(server.RefreshAsync()); Layout();
            Toolbar("SortButton"); Invoke(Menu("sortMenu"), "运行状态升序");
            Check(Rows()[0].Id == first && Rows()[1].StatusText == "运行中", "Status ascending must sort by state rather than name or global run status.");
            Toolbar("SortButton"); Invoke(Menu("sortMenu"), "运行状态降序");
            Check(Rows()[0].Id == second && shell.StartupInstanceId == second, "Status descending must reorder without changing selection.");
            daemon.IdleForPresetTests = true; daemon.CfgRunIdentity = originalIdentity; daemon.ListRunInstanceId = originalRunInstance;
            Pump(server.RefreshAsync()); Layout();
            Check(daemon.InstanceWrites.Count == originalWriteCount, "Selection, browsing, properties, filtering and sorting must not write metadata.");
            // Test the longest sort label against the shorter labels using real pointer input.
            // A transparent fixed hit area must not resize with the pill or lose hover as that pill shrinks.
            ArcMotion.SetLevel(actions, ArcMotionLevel.Off);
            page.FindControl<ExpandingButtonGroupItem>("CreateButton")!.Focus(NavigationMethod.Tab);
            search.Focus(); owner.MouseMove(new Point(1, 1)); Layout();
            var pill = actions.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Pill");
            var rightEdge = page.FindControl<Grid>("ToolbarSlot")!.TranslatePoint(default, page)!.Value.X +
                            page.FindControl<Grid>("ToolbarSlot")!.Bounds.Width;
            Check(!actions.ClipToBounds && Enumerable.Range(0, pill.BoxShadow.Count).All(i => pill.BoxShadow[i].OffsetX == 0 && pill.BoxShadow[i].OffsetY == 0),
                "The pill shadow must be centered and allowed outside the toolbar's stable hit bounds.");
            foreach (var label in new[] { "名称升序", "名称降序", "运行状态升序", "运行状态降序" })
            {
                // Actual menu mapping/order was verified above; isolate label geometry from popup dismissal/focus animations here.
                page.FindControl<ExpandingButtonGroupItem>("SortButton")!.Label = label;
                Layout();
                foreach (var level in new[] { ArcMotionLevel.Off, ArcMotionLevel.Reduced, ArcMotionLevel.Full })
                {
                    ArcMotion.SetLevel(actions, level);
                    search.Focus(); Hover(new Point(1, 1));
                    foreach (var name in new[] { "RefreshButton", "CreateButton", "SortButton" })
                    {
                        var item = page.FindControl<ExpandingButtonGroupItem>(name)!;
                        var icon = item.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "PART_IconBox");
                        var point = icon.TranslatePoint(new Point(icon.Bounds.Width / 2, icon.Bounds.Height / 2), owner)!.Value;
                        Hover(point);
                        Pump(server.RefreshAsync());
                        // The hover must land before geometry is sampled; the ticks below then prove it holds.
                        Wait(() => actions.ExpandedItem == item);
                        for (var tick = 0; tick < (level == ArcMotionLevel.Full ? 40 : 4); tick++)
                        {
                            Layout(); Thread.Sleep(8);
                            Check(actions.ExpandedItem == item && Math.Abs(pill.TranslatePoint(default, page)!.Value.X + pill.Bounds.Width - rightEdge) < 1 &&
                                  pill.Bounds.Width <= actions.Bounds.Width + 1,
                                $"Toolbar drift: sort={label}, motion={level}, target={name}, expanded={actions.ExpandedItem?.Name}, tick={tick}, " +
                                $"right={pill.TranslatePoint(default, page)!.Value.X + pill.Bounds.Width}/{rightEdge}, width={pill.Bounds.Width}/{actions.Bounds.Width}, over={actions.IsPointerOver}, " +
                                $"pointer={point}, groupAt={actions.TranslatePoint(default, owner)}, groupSize={actions.Bounds.Size}, item={item.Bounds}, hit={owner.GetVisualAt(point)?.GetType().Name}.");
                        }
                    }
                }
            }
            owner.MouseMove(new Point(1, 1)); Layout();
            Check(actions.ExpandedItem == page.FindControl<ExpandingButtonGroupItem>("SortButton"), "Leaving the fixed hit area must restore the current sort label.");
            actions.ClearValue(ArcMotion.LevelProperty);
            ArcMotion.SetLevel(loading, ArcMotionLevel.Full);
            daemon.InstanceListGate = new TaskCompletionSource();
            var refresh = page.FindControl<ExpandingButtonGroupItem>("RefreshButton")!;
            var createVisible = page.FindControl<ExpandingButtonGroupItem>("CreateButton")!.IsVisible;
            List<object?> expansionHistory = [];
            EventHandler<AvaloniaPropertyChangedEventArgs> expansionChanged = (_, e) =>
            {
                if (e.Property == ExpandingButtonGroup.ExpandedItemProperty) expansionHistory.Add(actions.ExpandedItem);
            };
            actions.PropertyChanged += expansionChanged;
            refresh.Focus(NavigationMethod.Tab);
            var refreshPoint = refresh.TranslatePoint(new Point(refresh.Bounds.Width / 2, refresh.Bounds.Height / 2), owner)!.Value;
            owner.MouseMove(refreshPoint); Layout();
            owner.MouseDown(refreshPoint, MouseButton.Left); owner.MouseUp(refreshPoint, MouseButton.Left); Layout();
            Check(loading.IsLoading && actions.IsVisible && refresh.IsVisible && refresh.IsFocused && refresh.IsDisabled &&
                  actions.ExpandedItem == refresh && page.FindControl<ExpandingButtonGroupItem>("CreateButton")!.IsVisible == createVisible,
                "Refresh must keep toolbar geometry, focus and expansion instead of disappearing or bouncing to sort.");
            var blocks = choices.GetVisualDescendants().OfType<MorphBlock>().ToArray();
            Check(blocks.Length == 6 && blocks.All(b => !b.IsResolved), "Refreshing must show independent text/status skeletons for every row.");
            var gate = daemon.InstanceListGate; daemon.InstanceListGate = null; gate.SetResult();
            Wait(() => !loading.IsLoading && loading.IsResolving);
            Wait(() => blocks.Any(b => b.IsResolved) && blocks.Any(b => !b.IsResolved));
            Check(loading.Stagger > TimeSpan.Zero, "The native resolve sequence must stagger blocks rather than reveal the whole list together.");
            Wait(() => !loading.IsResolving && !model.Busy);
            Check(refresh.IsFocused && actions.ExpandedItem == refresh && !refresh.IsDisabled && expansionHistory.All(item => ReferenceEquals(item, refresh)),
                "Refresh must not transiently expand sort or bounce away from the focused refresh action.");
            actions.PropertyChanged -= expansionChanged;
            Invoke(OpenMenu(first), "启动实例");
            Check(owner.FindControl<Border>("DialogOverlay")!.IsVisible, "Start must preserve existing confirmation.");
            DialogButton(owner, "取消"); Wait(() => Rows().All(r => r.CanEdit));
            Check(shell.StartupInstanceId == second, "A cancelled contextual start must not replace startup selection.");
            Invoke(OpenMenu(first), "重命名实例");
            var inputs = owner.FindControl<ContentControl>("DialogContent")!.GetVisualDescendants().OfType<Input>().ToArray();
            Check(inputs.Length == 1, "Rename must only show name."); inputs[0].Text = "Alpha renamed";
            DialogButton(owner, "保存"); Wait(() => Rows().Any(r => r.Name == "Alpha renamed") && Rows().All(r => r.CanEdit));
            Toolbar("CreateButton");
            inputs = owner.FindControl<ContentControl>("DialogContent")!.GetVisualDescendants().OfType<Input>().ToArray();
            inputs[0].Text = "Gamma"; inputs[1].Text = "27017"; DialogButton(owner, "保存");
            Wait(() => Rows().Length == 3 && Rows().All(r => r.CanEdit));
            Check(shell.StartupInstanceId == second, "Creating another instance must preserve explicit startup selection.");
            var created = Rows().Single(r => r.Name == "Gamma").Item.Entry.Definition!;
            ArcMotion.SetLevel(choices, ArcMotionLevel.Full);
            var createdRow = choices.ItemsSource!.Cast<InstanceListRow>().Single(r => r.Id == created.Id);
            var exitContainer = choices.ContainerFromItem(createdRow);
            search.Text = "Practice"; Layout();
            Toolbar("SortButton"); Invoke(Menu("sortMenu"), "名称升序");
            Check(ReferenceEquals(exitContainer, choices.ContainerFromItem(createdRow)),
                "Sorting while another row exits must not rebuild its presence container.");
            Wait(() => choices.Items.Count == 2);
            search.Text = ""; Layout();
            var catalogWithCreated = daemon.Catalog;
            daemon.InstanceListGate = new TaskCompletionSource();
            Toolbar("RefreshButton");
            daemon.Catalog = new(daemon.Catalog.Instances.Where(i => i.Id != created.Id).ToArray());
            var externalRefreshGate = daemon.InstanceListGate;
            daemon.InstanceListGate = null; externalRefreshGate.SetResult();
            Wait(() => !loading.IsLoading && Rows().Length == 2);
            Check(choices.Items.Count == 3 && !createdRow.IsPresent,
                "Rows removed by a toolbar refresh must also remain mounted for their exit, not disappear during loading.");
            Wait(() => choices.Items.Count == 2 && !loading.IsResolving);
            daemon.Catalog = catalogWithCreated;
            Pump(model.RefreshInstancesAsync()); Layout();
            createdRow = choices.ItemsSource!.Cast<InstanceListRow>().Single(r => r.Id == created.Id);
            Pump(model.DeleteInstanceAsync(created.Id, created.Revision)); Layout();
            Check(choices.Items.Count == 3 && !createdRow.IsPresent, "Backend deletion must retain the row until its exit completes.");
            Wait(() => choices.Items.Count == 2);

            OpenDelete(first, pointerMenu: true); var writes = daemon.InstanceWrites.Count;
            var deleteSurface = popover.Popup!.Child as PopoverSurface ?? popover.Popup.Child!.GetVisualDescendants().OfType<PopoverSurface>().Single();
            Check(deleteSurface.Background is ISolidColorBrush { Color.A: 0 } && deleteSurface.Padding == default && hold.Opacity == 1,
                "Non-glass delete-popover background must be translucent without fading confirmation text.");
            hold.Focus(); SendKey(owner, Key.Space);
            Check(daemon.InstanceWrites.Count == writes, "Early release must not send deletion.");
            daemon.RejectInstanceDeletes = true;
            hold.Focus(); owner.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.None, "");
            Wait(() => daemon.InstanceWrites.Count > writes);
            owner.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.None, ""); Layout();
            Wait(() => !model.Busy && hold.IsEnabled);
            Check(daemon.Catalog.Instances.Any(i => i.Id == first) && !hold.IsConfirmed && popover.IsOpen &&
                  owner.FindControl<ToastStack>("InstanceNotifications")!.VisibleToasts > 0, "Delete failure must retain row/popover, reset hold and show toast.");
            daemon.RejectInstanceDeletes = false;
            hold.RaiseEvent(new RoutedEventArgs(HoldToConfirm.ConfirmedEvent)); Layout();
            Wait(() => Rows().Length == 1 && !popover.IsOpen);
            Check(daemon.InstanceWrites.Last().InstanceId == first && shell.StartupInstanceId == second, "Deletion must target captured row, not selected startup.");
            Check(owner.FindControl<ToastStack>("InstanceNotifications")!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "实例已删除:"),
                "The deletion notification title must end with a colon.");
            OpenDelete(second); writes = daemon.InstanceWrites.Count;
            daemon.Catalog = new(daemon.Catalog.Instances.Select(i => i with { Definition = i.Definition! with { Revision = i.Definition.Revision + 1 } }).ToArray());
            Pump(model.RefreshInstancesAsync()); Layout();
            hold.RaiseEvent(new RoutedEventArgs(HoldToConfirm.ConfirmedEvent)); Layout();
            Check(daemon.InstanceWrites.Count == writes && !popover.IsOpen, "Revision changes must invalidate pending deletion.");
            daemon.IdleForPresetTests = false; Pump(server.RefreshAsync()); Layout();
            Check(Rows().All(r => !r.CanStart && !r.CanEdit && r.StatusText == "状态未知") &&
                  !page.FindControl<ExpandingButtonGroupItem>("CreateButton")!.IsVisible, "Unattributed run must remain unknown and hide unavailable creation.");
            Toolbar("SortButton");
            Check(Menu("sortMenu").Items.OfType<GlassMenuItem>().Where(i => i.Content!.ToString()!.StartsWith("运行状态")).All(i => !i.IsEnabled), "Unavailable status sort choices must be disabled.");
            Menu("sortMenu").Close();
            daemon.CfgRunIdentity = true; Pump(server.RefreshAsync()); Layout();
            Check(Rows().Single().StatusText == owner.FindControl<Badge>("ServerStatusBadge")!.Content?.ToString(), "Known row must reuse title bar badge mapping.");
            Invoke(OpenMenu(second), "查看日志");
            var logWindow = owner.OwnedWindows.OfType<ServerLogWindow>().Single(); Check(logWindow.IsVisible, "Logs must open existing owned log window.");
            logWindow.Close(); Layout();
            daemon.IdleForPresetTests = true;
            // The catalog is about to become empty: a concurrent fake status poll must not resolve a now-missing run instance with Single().
            daemon.CfgRunIdentity = false;
            Pump(server.RefreshAsync());
            Pump(model.DeleteInstanceAsync(second, daemon.Catalog.Instances.Single().Definition!.Revision)); Layout();
            Wait(() => page.FindControl<EmptyState>("Empty")!.IsVisible);
            Check(page.FindControl<EmptyState>("Empty") is { IsVisible: true, Title: "暂无实例" }, "Empty catalog must differ from no search matches.");
            Console.WriteLine("PASS gui.instances.exit-lifecycle-selection-crossfade-stable-press-and-menu-anchored-delete");
        }
        finally
        {
            daemon.InstanceListGate?.TrySetResult(); daemon.InstanceListGate = null;
            daemon.AllowInstanceWrites = daemon.RejectInstanceDeletes = false;
            daemon.Catalog = originalCatalog; daemon.IdleForPresetTests = originalIdle; daemon.CfgRunIdentity = originalIdentity; daemon.ListRunInstanceId = originalRunInstance;
            page.ClearValue(ArcMotion.LevelProperty); choices.ClearValue(ArcMotion.LevelProperty); loading.ClearValue(ArcMotion.LevelProperty); actions.ClearValue(ArcMotion.LevelProperty);
            search.Text = ""; popover.IsOpen = properties.IsOpen = false;
            Pump(server.RefreshAsync()); Pump(model.RefreshInstancesAsync());
            shell.NavigateCommand.Execute("home"); Layout();
        }
    }

    private static void SendKey(MainWindow owner, Key key)
    {
        owner.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, "");
        owner.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, ""); Layout();
    }
    private static void DialogButton(MainWindow owner, string text)
    {
        var button = owner.FindControl<ContentControl>("DialogContent")!.GetVisualDescendants().OfType<ArcButton>()
            .Single(b => b.IsEffectivelyVisible && Equals(b.Content, text));
        button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); Layout();
    }
    private static void Layout() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static void Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Instance list UI did not settle.");
            Layout(); Thread.Sleep(5);
        }
        Layout();
    }
    private static void Pump(Task task) { Wait(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
