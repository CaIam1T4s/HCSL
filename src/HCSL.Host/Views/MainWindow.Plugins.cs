using Avalonia.Controls;
using Avalonia.Platform.Storage;
using HCSL.Core;
using HCSL.Frontend;
using TKCode.Arc;
namespace HCSL.Host.Views;

public partial class MainWindow
{
    internal async Task<PluginImportResult?> ImportPluginToLibraryAsync(HostServerController model,
        InstanceDefinition instance, PresetDefinition preset)
    {
        while (true)
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择插件包",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("插件包") { Patterns = ["*.zip", "*.tar.gz", "*.tgz", "*.tar", "*.smx"] }]
            });
            if (files.Count == 0) return null;
            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                ShowInstanceNotification("无法读取插件包", "请选择本地文件。");
                return null;
            }

            PluginImportPreview preview;
            try { preview = await model.ProbePluginImportAsync(path); }
            catch (Exception ex) when (ex is IOException or InstanceException)
            {
                ShowInstanceNotification("无法读取插件包", ex.Message);
                return null;
            }

            var body = new StackPanel { Spacing = 8 };
            var id = Metadata("ID", preview.Id);
            var name = Metadata("名称", preview.Name);
            var version = Metadata("版本", preview.Version);
            Select? framework = null;
            if (preview.TargetRoot is not null && preview.ManagedFramework is ManagedPluginFramework.Unknown or ManagedPluginFramework.Ambiguous)
            {
                framework = new Select { ItemsSource = new[] { "请选择框架", "SwiftlyS2", "ModSharp" }, SelectedIndex = 0 };
                AddRow("所需框架", framework);
            }
            else
            {
                var required = preview.Framework ?? (preview.Kind == "file" && path.EndsWith(".smx", StringComparison.OrdinalIgnoreCase) ? "source2mod" : null);
                var dependency = required is null ? null : FrameworkRules.Find(required)?.Requires;
                var description = required is null ? "未识别" : PluginPresentation.FrameworkLabel(required);
                if (dependency is not null) description += "、" + PluginPresentation.FrameworkLabel(dependency);
                AddRow("所需框架", Value(description));
            }

            while (true)
            {
                var actions = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8 };
                actions.Classes.Add("dialog-actions");
                AddAction("取消", 0, false);
                AddAction("导入", 2, true);
                var dialog = new Dialog
                {
                    Title = "导入插件",
                    Description = Path.GetFileName(path),
                    Size = ControlSize.Small,
                    Content = new ScrollViewer { Content = body, MaxHeight = 360 },
                    Actions = actions
                };
                var choice = await dialog.ShowAsync(this);
                ((ScrollViewer)dialog.Content).Content = null;
                if (choice is not 2) return null;

                void AddAction(string label, int result, bool primary)
                {
                    var button = new TKCode.Arc.Button { Content = label, Variant = primary ? ButtonVariant.Primary : ButtonVariant.Secondary };
                    if (primary) button.Classes.Add("dialog-primary");
                    Dialog.SetResult(button, result);
                    actions.Children.Add(button);
                }
                var frameworkId = framework is null ? preview.Framework : framework.SelectedIndex switch { 1 => "swiftlys2", 2 => "modsharp", _ => null };
                if (framework is not null && frameworkId is null || new[] { id(), name(), version() }.Any(string.IsNullOrWhiteSpace))
                {
                    ShowInstanceNotification("无法导入插件", "请补全插件信息并选择所需框架。");
                    return null;
                }
                try
                {
                    var archive = preview.Kind == "archive" ? path : null;
                    var imported = await model.ImportPluginAsync(new PluginImportCommand(instance.Id, preset.Id,
                        archive is null ? path : "", id(), name(), version(), preview.MetaPlugin, false, false,
                        instance.Revision, archive, CfgIntoPreset: false, Framework: frameworkId));
                    ShowInstanceNotification("已导入插件", $"{imported.Id} {imported.Version} 已加入插件库，未启用。");
                    return imported;
                }
                catch (Exception ex) when (ex is IOException or InstanceException)
                {
                    ShowInstanceNotification("导入失败", ex.Message);
                    return null;
                }
            }

            Func<string> Metadata(string label, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    AddRow(label, Value(value));
                    return () => value;
                }
                var input = new Input { Size = ControlSize.Small, Width = 136,
                    TextAlignment = Avalonia.Media.TextAlignment.Right };
                Avalonia.Automation.AutomationProperties.SetName(input, label);
                AddRow(label, input);
                return () => (input.Text ?? "").Trim();
            }

            static TextBlock Value(string value) => new() { Text = value, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                TextAlignment = Avalonia.Media.TextAlignment.Right };

            void AddRow(string label, Control value)
            {
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
                row.Children.Add(new TextBlock { Text = label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
                value.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
                Grid.SetColumn(value, 1);
                row.Children.Add(value);
                body.Children.Add(row);
            }
        }
    }

    public async Task EditPluginsAsync(HostServerController model, InstanceDefinition instance, PresetDefinition preset,
        string[]? targets = null, bool? enabled = null)
    {
        var draft = new PluginDraft();
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock { Text = "插件 · " + instance.Name + " / " + preset.Name, FontSize = 20 });
        content.Children.Add(new TextBlock
        {
            Text = "从内容库选择插件版本和加载顺序，保存后在下次部署时生效。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        var smxButton = new TKCode.Arc.Button { Content = "导入 .smx 子插件（需要 source2mod + metamod）", Variant = ButtonVariant.Secondary, IsEnabled = false };
        content.Children.Add(smxButton);
        var list = new Select(); content.Children.Add(list);
        var status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap }; content.Children.Add(status);
        int[] visible = [];
        string Label(PluginSelection p)
        {
            var info = PackageFor(p);
            var category = info?.Category ?? PluginCategory.Other;
            var name = info?.Name ?? p.Id;
            return $"[{PluginPresentation.CategoryLabel(category)}] {(p.Enabled ? "[启用]" : "[禁用]")} {name} ({p.Id}) {p.Version}";
        }
        PluginPackageInfo? PackageFor(PluginSelection p) =>
            draft.Loaded?.Available.FirstOrDefault(a => a.Id == p.Id && a.Version == p.Version);
        string? CurrentFramework()
        {
            foreach (var p in draft.Plugins.Where(x => x.Enabled))
            {
                var package = PackageFor(p);
                if (package?.Framework is { } f && package.FrameworkPayload
                    && FrameworkRules.Find(f)?.Kind == FrameworkKind.ScriptFramework && p.Id == f) return f;
            }
            return null;
        }
        bool Visible(PluginSelection p)
        {
            if (FrameworkRules.Find(p.Id) is not null) return false;
            var package = PackageFor(p);
            if (package is { Category: PluginCategory.Framework } or { FrameworkPayload: true }) return false;
            if (package?.Framework is null or "metamod") return true;
            return string.Equals(package.Framework, CurrentFramework(), StringComparison.Ordinal);
        }
        void Refresh(int draftIndex = 0)
        {
            var current = CurrentFramework();
            var baseSelection = draft.Plugins.FirstOrDefault(p => p.Id == "metamod");
            smxButton.IsEnabled = !model.Busy && current == "source2mod" && baseSelection?.Enabled == true;
            var indices = Enumerable.Range(0, draft.Plugins.Count).Where(i => Visible(draft.Plugins[i]))
                .OrderBy(i => Array.IndexOf(PluginPresentation.CategoryOrder, PackageFor(draft.Plugins[i])?.Category ?? PluginCategory.Other))
                .ThenBy(i => i).ToArray();
            visible = indices;
            list.ItemsSource = indices.Select(i => Label(draft.Plugins[i])).ToArray();
            var wanted = Array.IndexOf(indices, draftIndex);
            list.SelectedIndex = indices.Length == 0 ? -1 : Math.Max(0, wanted >= 0 ? wanted : Math.Min(list.SelectedIndex, indices.Length - 1));
        }
        int SelectedDraftIndex()
        {
            var at = list.SelectedIndex;
            return at >= 0 && at < visible.Length ? visible[at] : -1;
        }
        static string RejectionText(PluginResult result) => result.Rejected is { Length: > 0 } rejected
            ? " " + rejected.Length + " 个包被拒绝：" + string.Join("；", rejected.Take(3).Select(x => $"{x.Id}/{x.Version} {x.Code}"))
            : "";
        async Task Load()
        {
            var result = await model.PluginsAsync(new(PluginAction.List, instance.Id, preset.Id));
            if (windowClosed) return;
            draft.Load(result);
            var indices = Enumerable.Range(0, draft.Plugins.Count).Where(i =>
                targets?.Contains(draft.Plugins[i].Id, StringComparer.Ordinal) == true && Visible(draft.Plugins[i])).ToArray();
            var changed = 0;
            if (enabled is { } desired && model.CanEditPlugins)
                foreach (var index in indices)
                    if (draft.Plugins[index].Enabled != desired) { draft.Toggle(index); changed++; }
            enabled = null; // Reload discards the draft rather than reapplying the initial action.
            Refresh(indices.FirstOrDefault());
            status.Text = (changed > 0 ? $"已修改 {changed} 个插件的草稿状态，保存清单后生效。" : "已读取插件清单；未部署。") + RejectionText(result);
        }

        async Task<bool> Discard() => !draft.Dirty || await ChooseAsync("未保存的插件清单", "丢弃未保存的插件选择？", "继续编辑", "丢弃草稿") == 1;
        async Task AddPlugin()
        {
            var current = CurrentFramework();
            var available = draft.Loaded?.Available.Where(a => draft.Plugins.All(p => p.Id != a.Id)
                && a.Category != PluginCategory.Framework && !a.FrameworkPayload && FrameworkRules.Find(a.Id) is null
                && (a.Framework is null or "metamod" || a.Framework == current))
                .OrderBy(a => Array.IndexOf(PluginPresentation.CategoryOrder, a.Category))
                .ThenBy(a => a.Id, StringComparer.Ordinal).ToArray() ?? [];
            if (available.Length == 0)
            {
                await ChooseAsync("没有可添加的插件", "内容库 plugin-packages 中没有未选择的包版本。请先把包放到 <library>/plugin-packages/<id>/<version>/plugin.json + files/。", "返回");
                return;
            }
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(new TextBlock { Text = "添加插件包", FontSize = 20 });
            var select = new Select { ItemsSource = available.Select(a => PluginPresentation.PackageLabel(a)).ToArray(), SelectedIndex = 0 };
            body.Children.Add(select);
            body.Children.Add(new TextBlock { Text = "同一插件只能选择一个版本；数组顺序就是部署顺序。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
            if (await ShowOverlayAsync(body, ["取消", "添加"]) == 1)
            {
                var package = available[Math.Clamp(select.SelectedIndex, 0, available.Length - 1)];
                try { draft.Add(package.Id, package.Version); Refresh(draft.Plugins.Count - 1); status.Text = "已加入草稿；保存后仍不会自动部署。"; }
                catch (InstanceException e) { status.Text = e.Code + ": " + e.Message; }
            }
        }
        async Task ImportPlugin(string? direct = null)
        {
            string? path = direct, archive = null;
            if (direct is null)
            {
                var source = await ChooseAsync("导入插件包", "选择来源：压缩包（.zip / .tar.gz / .tgz / .tar）或已解压的发行目录（含 addons/、csgo/ 或 files/csgo/）。单文件 .smx 使用上方专用按钮。", "取消", "压缩包", "目录");
                if (source == 1)
                {
                    var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                    {
                        Title = "选择插件压缩包",
                        AllowMultiple = false,
                        FileTypeFilter = [new FilePickerFileType("插件压缩包") { Patterns = ["*.zip", "*.tar.gz", "*.tgz", "*.tar"] }]
                    });
                    path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
                    archive = path is not null && !path.EndsWith(".smx", StringComparison.OrdinalIgnoreCase) ? path : null;
                }
                else if (source == 2)
                {
                    var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    {
                        Title = "选择插件发行目录（含 addons/、csgo/ 或 files/csgo/）",
                        AllowMultiple = false
                    });
                    path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
                }
            }
            if (string.IsNullOrWhiteSpace(path)) return;

            PluginImportPreview preview;
            try { preview = await model.ProbePluginImportAsync(path); }
            catch (Exception e) when (e is IOException or InstanceException)
            {
                await ChooseAsync("无法读取插件包", e is InstanceException failure ? failure.Code + ": " + failure.Message : e.Message, "返回");
                return;
            }

            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock { Text = "导入插件包", FontSize = 20 });
            var managed = preview.TargetRoot is not null;
            var frameworkText = !managed ? "非受管插件"
                : preview.Framework is null ? "待选择" : PluginPresentation.FrameworkLabel(preview.Framework);
            body.Children.Add(new TextBlock
            {
                Text = (preview.Kind == "archive" ? "压缩包：" : preview.Kind == "file" ? "单文件：" : "目录：") + path
                    + "\n识别：Id=" + preview.Id + "，名称=" + preview.Name + "，版本=" + preview.Version
                    + "\n规模：" + preview.Files + " 文件，" + preview.Bytes / 1024 + " KiB"
                    + (managed ? "\n框架：" + frameworkText + (preview.Framework is null ? "" : "，目标：" + preview.TargetRoot) : "")
                    + (preview.ManagedFramework == ManagedPluginFramework.Ambiguous ? "（deps.json 同时包含两种依赖）" : ""),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            Select? importFramework = null;
            if (managed && preview.ManagedFramework is ManagedPluginFramework.Unknown or ManagedPluginFramework.Ambiguous)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "该 DLL 的 deps.json 无法唯一确定框架，请选择导入目标：",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                });
                importFramework = new Select { ItemsSource = new[] { "请选择框架", "SwiftlyS2", "ModSharp" }, SelectedIndex = 0 };
                body.Children.Add(importFramework);
            }
            var importId = new Input { Text = preview.Id };
            var importName = new Input { Text = preview.Name };
            var importVersion = new Input { Text = preview.Version };
            var importEnable = new TKCode.Arc.Checkbox { Content = "导入后加入当前预设并启用（需要停服）", IsChecked = preview.Kind == "file" };
            var importForce = new TKCode.Arc.Checkbox { Content = "覆盖同名版本", IsChecked = false };
            var importCfg = new TKCode.Arc.Checkbox { Content = "导入包内 cfg 到当前预设（不覆盖已有文件）", IsChecked = false };
            body.Children.Add(new TextBlock { Text = "Id" }); body.Children.Add(importId);
            body.Children.Add(new TextBlock { Text = "名称" }); body.Children.Add(importName);
            body.Children.Add(new TextBlock { Text = "版本" }); body.Children.Add(importVersion);
            body.Children.Add(importEnable); body.Children.Add(importForce); body.Children.Add(importCfg);
            body.Children.Add(new TextBlock
            {
                Text = preview.MetaPlugin is null
                    ? "未检测到需要显式加载的 Metamod 子插件；Source2Mod 依赖自带 .vdf 自动加载。"
                    : "将生成 addons/metamod/metaplugins.ini：" + preview.MetaPlugin + "（清单中需排在 Metamod:Source 之后）。",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            });
            if (preview.Warnings.Length > 0)
                body.Children.Add(new TextBlock
                {
                    Text = "提示：" + string.Join("；", preview.Warnings.Take(4)),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                });
            if (await ShowOverlayAsync(body, ["取消", "导入"]) != 1) return;
            var importFrameworkId = preview.Framework;
            if (importFramework is not null)
            {
                importFrameworkId = importFramework.SelectedIndex switch { 1 => "swiftlys2", 2 => "modsharp", _ => null };
                if (importFrameworkId is null)
                {
                    await ChooseAsync("需要选择框架", "该 managed 插件的 deps.json 无法唯一确定框架，请重新导入并选择 SwiftlyS2 或 ModSharp。", "返回");
                    return;
                }
            }

            PluginImportResult result;
            try
            {
                result = await model.ImportPluginAsync(new PluginImportCommand(instance.Id, preset.Id,
                    archive is null ? path : "", importId.Text ?? "", importName.Text ?? "", importVersion.Text ?? "",
                    preview.MetaPlugin, importEnable.IsChecked == true, importForce.IsChecked == true, instance.Revision, archive,
                    CfgIntoPreset: importCfg.IsChecked == true, Framework: importFrameworkId));
            }
            catch (Exception e) when (e is IOException or InstanceException)
            {
                await ChooseAsync("导入失败", e is InstanceException failure ? failure.Code + ": " + failure.Message : e.Message, "返回");
                return;
            }
            await Load();
            if (!result.Enabled && draft.Plugins.All(p => p.Id != result.Id))
            {
                try { draft.Add(result.Id, result.Version); Refresh(draft.Plugins.Count - 1); }
                catch (InstanceException) { }
            }
            status.Text = $"已导入 {result.Id} {result.Version}（{result.Files} 文件，{result.Bytes / 1024} KiB）；"
                + (result.Enabled || draft.Plugins.Any(p => p.Id == result.Id) ? "已加入草稿/启用。" : "未启用。")
                + (result.ImportedCfg > 0 || result.KeptCfg > 0 ? $" cfg：导入 {result.ImportedCfg}，保留 {result.KeptCfg}。" : "")
                + (result.Warnings.Length == 0 ? "" : " 警告：" + string.Join("；", result.Warnings.Take(3)))
                + " 保存清单并部署后生效。";
        }
        smxButton.Click += async (_, _) =>
        {
            if (!smxButton.IsEnabled) return;
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 Source2Mod 子插件（.smx）",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Source2Mod 子插件") { Patterns = ["*.smx"] }]
            });
            var selected = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            if (!string.IsNullOrWhiteSpace(selected)) await ImportPlugin(selected);
        };
        try
        {
            status.Text = "正在读取插件清单…";
            var loading = true;
            async Task LoadInitial()
            {
                try { await Load(); }
                catch (Exception e) { if (!windowClosed) status.Text = e.Message; }
                finally { loading = false; }
            }
            _ = LoadInitial();
            while (!windowClosed)
            {
                var choice = await ShowOverlayAsync(content, ["关闭", "添加插件", "导入插件包", "移除", "启用/禁用", "上移", "下移", "保存清单", "重新读取"]);
                if (choice <= 0) { if (windowClosed || await Discard()) break; else continue; }
                if (loading) { status.Text = "正在读取插件清单…请稍候"; continue; }
                try
                {
                    if (choice == 8) { if (await Discard()) await Load(); continue; }
                    // Importing into the library is allowed while running; enabling is gated by the controller.
                    if (!model.CanEditPlugins && choice != 2)
                        throw new IOException("HCSL-PLUGIN-BUSY: 请先停服并完成恢复/工具任务。");
                    var index = SelectedDraftIndex();
                    switch (choice)
                    {
                        case 1: await AddPlugin(); break;
                        case 2: await ImportPlugin(); break;
                        case 3: draft.Remove(index); Refresh(index); status.Text = "已从草稿移除；保存后生效。"; break;
                        case 4: draft.Toggle(index); Refresh(index); status.Text = "已切换启用状态草稿；保存后生效。"; break;
                        case 5: Refresh(draft.Move(index, -1)); status.Text = "已上移草稿；数组顺序即部署顺序。"; break;
                        case 6: Refresh(draft.Move(index, 1)); status.Text = "已下移草稿；数组顺序即部署顺序。"; break;
                        case 7:
                        {
                            var before = draft.Loaded!.Selected.Where(s => s.Enabled)
                                .Select(s => new PluginSelection(s.Id, s.Version, true)).ToArray();
                            var after = draft.Plugins.Where(p => p.Enabled).ToArray();
                            var enabledNew = after.Where(a => !before.Any(b => b.Id == a.Id && b.Version == a.Version)).ToArray();
                            var disabled = before.Where(b => !after.Any(a => a.Id == b.Id && a.Version == b.Version)).ToArray();
                            var overwrite = new List<string>();
                            var applyCfg = false;
                            if (enabledNew.Length > 0)
                            {
                                var preview = await model.PreviewPluginCfgAsync(instance.Id, preset.Id, instance.Revision, enabledNew, disabled);
                                if (preview.Import.Length > 0 || preview.Conflicts.Length > 0)
                                {
                                    var body = new StackPanel { Spacing = 6 };
                                    body.Children.Add(new TextBlock { Text = "插件 cfg 同步", FontSize = 18 });
                                    body.Children.Add(new TextBlock { Text = $"将导入 {preview.Import.Length} 个新文件；冲突 {preview.Conflicts.Length} 个（勾选表示用插件版本覆盖）；相同跳过 {preview.Identical.Length} 个。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                                    var checks = new List<(PluginCfgEntry Entry, TKCode.Arc.Checkbox Box)>();
                                    foreach (var entry in preview.Conflicts.Take(30))
                                    {
                                        var box = new TKCode.Arc.Checkbox { Content = $"{entry.Path}（现有 {entry.ExistingHash?[..8]} → 插件 {entry.SourceHash[..8]}）", IsChecked = false };
                                        checks.Add((entry, box)); body.Children.Add(box);
                                    }
                                    if (preview.Truncated) body.Children.Add(new TextBlock { Text = "文件过多，仅显示前 30 个冲突。" });
                                    var choice2 = await ShowOverlayAsync(body, ["取消", "仅保存清单", "应用"]);
                                    if (choice2 == 0) break;
                                    if (choice2 == 2)
                                    {
                                        applyCfg = true;
                                        overwrite.AddRange(checks.Where(c => c.Box.IsChecked == true).Select(c => c.Entry.Path));
                                    }
                                }
                            }
                            draft.Load(await model.PluginsAsync(draft.SaveCommand(instance.Id, preset.Id, instance.Revision)));
                            Refresh(index);
                            var saved = "插件清单已保存；下次部署/启动会应用，未自动部署。";
                            if (applyCfg && enabledNew.Length > 0)
                            {
                                var applied = await model.ApplyPluginCfgAsync(instance.Id, preset.Id, instance.Revision, enabledNew, [.. overwrite]);
                                saved += $" cfg：导入 {applied.Imported}，覆盖 {applied.Overwritten}。";
                            }
                            if (disabled.Length > 0)
                            {
                                var cleanup = await model.PreviewPluginCfgCleanupAsync(instance.Id, preset.Id, instance.Revision, disabled);
                                if (cleanup.Removable.Length > 0 || cleanup.Modified.Length > 0)
                                {
                                    var body = new StackPanel { Spacing = 6 };
                                    body.Children.Add(new TextBlock { Text = "禁用插件的 cfg 清理", FontSize = 18 });
                                    body.Children.Add(new TextBlock { Text = "以下文件由该插件导入且未被修改；默认勾选删除，取消勾选则保留。已修改的文件永不自动删除。", TextWrapping = Avalonia.Media.TextWrapping.Wrap });
                                    var checks = new List<(PluginCfgEntry Entry, TKCode.Arc.Checkbox Box)>();
                                    foreach (var entry in cleanup.Removable)
                                    {
                                        var box = new TKCode.Arc.Checkbox { Content = entry.Path, IsChecked = true };
                                        checks.Add((entry, box)); body.Children.Add(box);
                                    }
                                    foreach (var entry in cleanup.Modified) body.Children.Add(new TextBlock { Text = "保留（已修改）：" + entry.Path });
                                    if (await ShowOverlayAsync(body, ["跳过", "删除选中"]) == 1 && checks.Any(c => c.Box.IsChecked == true))
                                    {
                                        var removed = await model.CleanupPluginCfgAsync(instance.Id, preset.Id, instance.Revision, disabled,
                                            checks.Where(c => c.Box.IsChecked == true).Select(c => c.Entry.Path).ToArray());
                                        saved += $" cfg：删除 {removed.Deleted}。";
                                    }
                                }
                            }
                            status.Text = saved;
                            break;
                        }
                    }
                }
                catch (Exception e) when (e is IOException or InstanceException) { status.Text = e is InstanceException i ? i.Code + ": " + i.Message : e.Message; }
            }
        }
        finally { list.ItemsSource = null; }
    }
}
