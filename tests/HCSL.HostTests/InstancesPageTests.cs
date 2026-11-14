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
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
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
        InstanceListRow[] Rows() => choices.ItemsSource!.Cast<InstanceListRow>().ToArray();
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
        void OpenDelete(Guid id)
        {
            Invoke(OpenMenu(id), "删除实例"); Layout();
            Check(popover.IsOpen, "Delete must open a hold popover without sending a request.");
        }
        void Toolbar(string name) { Pump(actions.ActivateAsync(page.FindControl<ExpandingButtonGroupItem>(name)!)); Layout(); }
        try
        {
            Wait(() => Rows().Length == 2 && choices.GetVisualDescendants().OfType<RadioGroupItem>().Count() == 2 && model.CanCreate);
            Check(Rows()[0].Id == second, "Initial order must be name descending.");
            Check(!page.GetVisualDescendants().OfType<Table>().Any() && !page.GetVisualDescendants().OfType<RadioButton>().Any(), "No table/header/standalone radio should remain.");
            Check(choices.GetVisualDescendants().OfType<Panel>().Where(p => p.Name == "PART_Mark").All(p => !p.IsVisible), "The native radio marks must be hidden.");
            Check(choices.GetVisualDescendants().OfType<TKCode.Arc.Primitives.GlidingIndicator>().Any(), "RadioGroup's native gliding highlight must be retained.");
            Check(Rows().All(r => r.CreatedAt == "暂无记录" && r.LastRunAt == "暂无记录" && r.StatusText == "已停止"), "Unavailable dates must not be invented.");
            var row = Container(first);
            var badge = row.GetVisualDescendants().OfType<Badge>().Single();
            var badgeOrigin = badge.TranslatePoint(default, row)!.Value;
            Check(badge.HorizontalAlignment == HorizontalAlignment.Right && badge.VerticalAlignment == VerticalAlignment.Center &&
                  Math.Abs(badgeOrigin.Y + badge.Bounds.Height / 2 - row.Bounds.Height / 2) < 2 &&
                  row.Bounds.Width - badgeOrigin.X - badge.Bounds.Width < 30, "The badge must be aligned to the right and vertically centered in the row.");
            Check(search.TranslatePoint(default, page)!.Value.X == choices.TranslatePoint(default, page)!.Value.X,
                "Search must start at the list's left edge, not be centered in its toolbar lane.");
            Check(page.FindControl<ExpandingButtonGroupItem>("CreateButton")!.Icon!.ToString() == Lucide.Geometry(LucideIconKind.Plus).ToString(), "Create must use Lucide plus.");

            // Native keyboard selection, not a synthetic SelectionChanged event.
            row.Focus(); SendKey(owner, Key.Space);
            Check(shell.StartupInstanceId == first && model.Selected?.Entry.Id == first && shell.IsInstances, "Selecting a row must choose startup without navigating to management.");
            var secondRow = Container(second); secondRow.Focus(); SendKey(owner, Key.Space);
            var menu = OpenMenu(first);
            Check(shell.StartupInstanceId == second && choices.SelectedItem is InstanceListRow { Id: var selected } && selected == second,
                "Right-click must not change startup selection.");
            Check(menu.Items.OfType<GlassMenuItem>().Select(i => i.Content?.ToString()).SequenceEqual(new[]
                { "启动实例", "查看日志", "实例管理", "重命名实例", "属性", "删除实例" }), "Context actions must replace the operations column and include properties.");
            Invoke(menu, "属性");
            var card = page.FindControl<GlassCard>("PropertiesCard")!;
            Check(properties.IsOpen && card.DataContext is InstanceListRow { Id: var propertyId } && propertyId == first &&
                  shell.StartupInstanceId == second, "Properties must bind the clicked row, not the startup row.");
            Check(card.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "27015") &&
                  card.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == "暂无记录") == 2 &&
                  card.GetVisualDescendants().OfType<Badge>().Single().Content?.ToString() == "已停止", "Property values and status badge must be present.");
            Check(card.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("instance-property-value"))
                .All(t => t.HorizontalAlignment == HorizontalAlignment.Right && t.TextAlignment == Avalonia.Media.TextAlignment.Right),
                "Property values must align right, separate from the left-aligned field labels.");
            properties.IsOpen = false; Layout();
            var pointer = Container(first).TranslatePoint(new Point(24, Container(first).Bounds.Height / 2), owner)!.Value;
            owner.MouseMove(pointer);
            owner.MouseDown(pointer, MouseButton.Right); owner.MouseUp(pointer, MouseButton.Right); Layout();
            Check(Menu("rowMenu").IsOpen && shell.StartupInstanceId == second,
                "Native right-click must open the context menu without changing startup selection.");
            Menu("rowMenu").Close(); Layout();
            var poolReads = daemon.PoolReads;
            Invoke(OpenMenu(first), "实例管理"); Layout();
            Check(shell.IsInstance && model.Selected?.Entry.Id == first && shell.StartupInstanceId == second && daemon.PoolReads == poolReads,
                "Only explicit management action may navigate, without changing startup or reading hidden home pools.");
            shell.NavigateCommand.Execute("home"); Layout();
            Check(model.Selected?.Entry.Id == second, "Home must restore startup choice.");
            shell.NavigateCommand.Execute("instances"); Layout();
            search.Text = "27016"; Layout();
            Check(Rows().Length == 1 && Rows()[0].Id == second, "Search must match port.");
            search.Text = "Practice"; Layout(); Check(Rows().Length == 2, "Search must match preset.");
            search.Text = "missing"; Layout();
            Check(Rows().Length == 0 && page.FindControl<EmptyState>("Empty")!.IsVisible && shell.StartupInstanceId == second, "Filtering must not clear startup identity.");
            search.Text = ""; Layout();
            Toolbar("SortButton"); Invoke(Menu("sortMenu"), "名称升序");
            Check(Rows()[0].Id == first && page.FindControl<ExpandingButtonGroupItem>("SortButton")!.Label == "名称升序" && shell.StartupInstanceId == second,
                "Sort menu must change order and label, not startup selection.");

            daemon.IdleForPresetTests = false; daemon.CfgRunIdentity = true; daemon.ListRunInstanceId = second;
            Pump(server.RefreshAsync()); Layout();
            Toolbar("SortButton"); Invoke(Menu("sortMenu"), "运行状态升序");
            Check(Rows()[0].Id == first && Rows()[1].StatusText == "运行中", "Status ascending must sort by state rather than name or global run status.");
            Toolbar("SortButton"); Invoke(Menu("sortMenu"), "运行状态降序");
            Check(Rows()[0].Id == second && shell.StartupInstanceId == second, "Status descending must reorder without changing selection.");
            daemon.IdleForPresetTests = true; daemon.CfgRunIdentity = originalIdentity; daemon.ListRunInstanceId = originalRunInstance;
            Pump(server.RefreshAsync()); Layout();
            Check(daemon.InstanceWrites.Count == originalWriteCount, "Selection, browsing, properties, filtering and sorting must not write metadata.");
            daemon.InstanceListGate = new TaskCompletionSource();
            Toolbar("RefreshButton");
            Check(loading.IsLoading && !actions.IsVisible && !page.FindControl<ExpandingButtonGroupItem>("RefreshButton")!.IsVisible &&
                  !page.FindControl<ExpandingButtonGroupItem>("CreateButton")!.IsVisible, "Pending actions must disappear from the expanding group while SkeletonMorph is loading.");
            var gate = daemon.InstanceListGate; daemon.InstanceListGate = null; gate.SetResult();
            Wait(() => !loading.IsLoading && !model.Busy);
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
            Pump(model.DeleteInstanceAsync(created.Id, created.Revision)); Layout();

            OpenDelete(first); var writes = daemon.InstanceWrites.Count;
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
            daemon.IdleForPresetTests = true; Pump(server.RefreshAsync());
            Pump(model.DeleteInstanceAsync(second, daemon.Catalog.Instances.Single().Definition!.Revision)); Layout();
            Check(page.FindControl<EmptyState>("Empty") is { IsVisible: true, Title: "暂无实例" }, "Empty catalog must differ from no search matches.");
            Console.WriteLine("PASS gui.instances.radio-list-native-highlight-right-centered-badge-sort-context-properties-and-safe-hold-delete");
        }
        finally
        {
            daemon.InstanceListGate?.TrySetResult(); daemon.InstanceListGate = null;
            daemon.AllowInstanceWrites = daemon.RejectInstanceDeletes = false;
            daemon.Catalog = originalCatalog; daemon.IdleForPresetTests = originalIdle; daemon.CfgRunIdentity = originalIdentity; daemon.ListRunInstanceId = originalRunInstance;
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
