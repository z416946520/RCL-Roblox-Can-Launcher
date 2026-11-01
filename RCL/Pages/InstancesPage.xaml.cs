using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RCL.Core;
using RCL.Dialogs;
using RCL.Models;

namespace RCL.Pages;

public partial class InstancesPage : UserControl
{
    private sealed record VersionChoice(string Display, string Path)
    {
        public override string ToString() => Display;
    }

    private RclInstance? _current;
    private bool _refreshing;
    private bool _executorListLoading;
    private List<ExecutorChoice>? _executorChoices;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };

    public InstancesPage()
    {
        InitializeComponent();
        _saveTimer.Tick += (_, _) => FlushSave();
        InstanceStore.Changed += OnStoreChanged;
        PresetCombo.ItemsSource = FastFlags.Presets;
        Loaded += (_, _) =>
        {
            BuildPalette();
            RefreshInstances();
        };
    }

    public void OnActivated()
    {
        RefreshInstances();
        RefreshVersions();
        _ = EnsureExecutorListAsync();
    }

    public void SelectInstance(RclInstance instance)
    {
        _current = instance;
        AppSettings.Current.LastInstanceId = instance.Id.ToString("N");
        RefreshInstances();
        SelectTab(0);
    }

    private void OnStoreChanged()
    {
        if (!IsLoaded)
            return;
        RefreshInstances();
    }

    private void BuildPalette()
    {
        if (ColorPalette.Children.Count > 0)
            return;

        foreach (var hex in ThemeManager.Palette)
        {
            var swatch = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 7, 0),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!),
                ToolTip = hex,
                Tag = hex
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                if (_current is null)
                    return;
                _current.IconColor = hex;
                InstanceStore.Save(_current);
                UpdateHeader();
                RefreshInstances();
            };
            ColorPalette.Children.Add(swatch);
        }

        var custom = new Button
        {
            Content = "自定义…",
            Style = (Style)FindResource("GhostButton"),
            FontSize = 11,
            Height = 22,
            Padding = new Thickness(6, 0, 6, 0),
            Margin = new Thickness(3, 0, 0, 0)
        };
        custom.Click += (_, _) =>
        {
            if (_current is null)
                return;
            var hex = DialogService.Prompt("实例颜色", "十六进制颜色", _current.IconColor, "例如 #E74C3C");
            if (hex is null)
                return;
            if (!ThemeManager.TryParse(hex, out var color))
            {
                DialogService.Error("颜色格式不正确，请使用 #RRGGBB 或 #AARRGGBB。");
                return;
            }
            _current.IconColor = ThemeManager.ToHex(color);
            InstanceStore.Save(_current);
            UpdateHeader();
        };
        ColorPalette.Children.Add(custom);
    }

    private void RefreshInstances()
    {
        if (SearchBox is null)
            return;

        _refreshing = true;
        var query = SearchBox.Text.Trim();
        var list = InstanceStore.All
            .Where(i => query.Length == 0 || i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        InstancesList.ItemsSource = list;
        InstanceCountText.Text = InstanceStore.All.Count.ToString();

        var preferredId = _current?.Id.ToString("N") ?? AppSettings.Current.LastInstanceId;
        var selected = list.FirstOrDefault(i => i.Id.ToString("N") == preferredId) ?? list.FirstOrDefault();
        InstancesList.SelectedItem = selected;
        _current = selected;
        _refreshing = false;

        if (selected is not null)
            AppSettings.Current.LastInstanceId = selected.Id.ToString("N");

        BindCurrent();
    }

    private void BindCurrent()
    {
        bool has = _current is not null;
        EmptyPanel.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (_current is not { } inst)
            return;

        _refreshing = true;
        NameBox.Text = inst.Name;
        NotesBox.Text = inst.Notes;
        ArgsBox.Text = inst.LaunchArgs;
        MultiCheck.IsChecked = inst.MultiInstance;
        ApplyModsCheck.IsChecked = inst.ApplyMods;
        _refreshing = false;

        PathInstance.Text = inst.Folder;
        PathFlags.Text = inst.FlagsFile;
        PathMods.Text = inst.ModsFolder;

        DataPlaytime.Text = "游玩时长：" + inst.PlaytimeDisplay;
        DataLastPlayed.Text = "最近启动：" + inst.LastPlayedDisplay;
        DataLastGame.Text = "上次游戏：" + inst.LastGameDisplay;

        UpdateHeader();
        RefreshVersions();
        RefreshFlags();
        RefreshMods();
        UpdateLaunchStatus();
        RefreshExecutorUi(inst);
    }

    private void UpdateHeader()
    {
        if (_current is not { } inst)
            return;
        HeaderName.Text = inst.Name;
        HeaderMeta.Text = $"实例 {inst.Id.ToString("N")[..8]} · 已游玩 {inst.PlaytimeDisplay}";
        HeaderInitial.Text = string.IsNullOrWhiteSpace(inst.Name) ? "R" : inst.Name.Trim()[..1].ToUpperInvariant();
        if (ThemeManager.TryParse(inst.IconColor, out var color))
            HeaderIcon.Background = new SolidColorBrush(color);
    }

    private void RefreshVersions()
    {
        if (_current is not { } inst)
            return;

        var versions = RobloxInstall.ScanPlayerVersions();
        var choices = new List<VersionChoice>
        {
            new("自动（最新安装版本）", "")
        };
        choices.AddRange(versions.Select(v => new VersionChoice(
            $"{v.DisplayName} · {v.ShortTag}" + (string.IsNullOrWhiteSpace(v.Source) ? "" : $" · {v.Source}"),
            v.Directory)));
        choices.Add(new VersionChoice("浏览目录…", "__browse__"));

        bool missing = !string.IsNullOrWhiteSpace(inst.VersionPath) &&
                       !choices.Any(c => string.Equals(c.Path, inst.VersionPath, StringComparison.OrdinalIgnoreCase));
        if (missing)
            choices.Insert(1, new VersionChoice($"已丢失：{Path.GetFileName(inst.VersionPath)}", inst.VersionPath));

        _refreshing = true;
        VersionCombo.ItemsSource = choices;
        VersionCombo.SelectedItem = choices.FirstOrDefault(c =>
            string.Equals(c.Path, inst.VersionPath, StringComparison.OrdinalIgnoreCase)) ?? choices[0];
        _refreshing = false;

        var resolved = RobloxInstall.Resolve(inst.VersionPath);
        VersionPathBox.Text = resolved?.Directory ?? "（没有可用的 Roblox 版本目录）";
        CloneVersionHint.Text = resolved is null
            ? "未检测到可复制的版本。"
            : $"独立副本会把 {resolved.Version} 的全部文件复制到实例目录，占用约 360 MB，之后这个实例完全使用自己的客户端文件。";
    }

    private void UpdateLaunchStatus()
    {
        if (_current is not { } inst)
            return;
        var resolved = RobloxInstall.Resolve(inst.VersionPath);
        LaunchStatus.Text = resolved is null
            ? "未检测到 Roblox，无法启动"
            : $"将使用 {resolved.DisplayName} · {(inst.MultiInstance ? "多开模式" : "单实例")}";
    }

    private void SelectTab(int index)
    {
        _refreshing = true;
        TabOverview.IsChecked = index == 0;
        TabFlags.IsChecked = index == 1;
        TabMods.IsChecked = index == 2;
        TabLaunch.IsChecked = index == 3;
        _refreshing = false;

        PanelOverview.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelFlags.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelMods.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelLaunch.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Tabs_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _refreshing || sender is not RadioButton { IsChecked: true } tab)
            return;
        int index = tab == TabOverview ? 0 : tab == TabFlags ? 1 : tab == TabMods ? 2 : 3;
        SelectTab(index);
        if (index == 1)
            RefreshFlags();
        if (index == 2)
            RefreshMods();
    }

    // ---------- Selection & editing ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is null)
            return;
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshInstances();
    }

    private void InstancesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing)
            return;
        FlushSave();
        _current = InstancesList.SelectedItem as RclInstance;
        if (_current is not null)
        {
            AppSettings.Current.LastInstanceId = _current.Id.ToString("N");
            AppSettings.Save();
        }
        BindCurrent();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshing || _current is null)
            return;
        _current.Name = NameBox.Text;
        UpdateHeader();
        QueueSave();
    }

    private void NotesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshing || _current is null)
            return;
        _current.Notes = NotesBox.Text;
        QueueSave();
    }

    private void ArgsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshing || _current is null)
            return;
        _current.LaunchArgs = ArgsBox.Text;
        QueueSave();
    }

    private void Behavior_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _current is null)
            return;
        _current.MultiInstance = MultiCheck.IsChecked == true;
        _current.ApplyMods = ApplyModsCheck.IsChecked == true;
        InstanceStore.Save(_current);
        UpdateLaunchStatus();
        RefreshInstances();
    }

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushSave()
    {
        _saveTimer.Stop();
        if (_current is null)
            return;
        if (string.IsNullOrWhiteSpace(_current.Name))
            _current.Name = "未命名实例";
        InstanceStore.Save(_current);
        UpdateHeader();
    }

    private void VersionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _current is null || VersionCombo.SelectedItem is not VersionChoice choice)
            return;

        if (choice.Path == "__browse__")
        {
            var folder = DialogService.PickFolder("选择包含 RobloxPlayerBeta.exe 的版本目录");
            if (folder is null || RobloxInstall.Resolve(folder) is null)
            {
                if (folder is not null)
                    DialogService.Error("这个目录里没有 RobloxPlayerBeta.exe，无法作为版本目录。", "无效的版本目录");
                RefreshVersions();
                return;
            }
            _current.VersionPath = folder;
        }
        else
        {
            _current.VersionPath = choice.Path;
        }

        InstanceStore.Save(_current);
        RefreshVersions();
        UpdateLaunchStatus();
        MainWindowHost?.ShowToast("已更新实例版本绑定");
    }

    private void RefreshVersions_Click(object sender, RoutedEventArgs e)
    {
        RefreshVersions();
        MainWindowHost?.ShowToast("版本列表已刷新");
    }

    private void OpenVersionFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = RobloxInstall.Resolve(_current?.VersionPath)?.Directory;
        if (dir is null)
        {
            DialogService.Info("实例还没有绑定可用的 Roblox 版本。", "没有版本目录");
            return;
        }
        OpenPath(dir);
    }

    private async void CloneVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        var version = RobloxInstall.Resolve(inst.VersionPath);
        if (version is null)
        {
            DialogService.Error("没有可复制的 Roblox 版本。", "无法创建副本");
            return;
        }

        var target = Path.Combine(inst.Folder, "Version");
        if (Directory.Exists(target) && File.Exists(Path.Combine(target, "RobloxPlayerBeta.exe")))
        {
            if (!DialogService.Confirm("实例目录里已经有一份独立副本，要重新复制并覆盖吗？", "独立副本已存在"))
                return;
        }

        if (!DialogService.Confirm(
                $"将把 {version.DisplayName} 复制到：\n{target}\n\n这会占用约 400 MB 磁盘空间，过程可能需要几十秒。继续吗？",
                "创建独立副本", "开始复制", "取消"))
            return;

        CloneVersionHint.Text = "正在复制版本文件…";
        try
        {
            await Task.Run(() => CopyDirectoryWithRetry(version.Directory, target));
            inst.VersionPath = target;
            InstanceStore.Save(inst);
            RefreshVersions();
            UpdateLaunchStatus();
            MainWindowHost?.ShowToast("独立副本已创建，实例已绑定到自己的版本目录");
        }
        catch (Exception ex)
        {
            Log.Error("复制版本目录失败", ex);
            CloneVersionHint.Text = "复制失败：" + ex.Message;
            DialogService.Error("复制失败：\n" + ex.Message + "\n\n如果 Roblox 正在运行，请先关闭后重试。", "创建副本失败");
        }
    }

    private static void CopyDirectoryWithRetry(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(target, rel));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current is { } inst)
        {
            Directory.CreateDirectory(inst.Folder);
            OpenPath(inst.Folder);
        }
    }

    private void OpenFlagsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        Directory.CreateDirectory(inst.ClientSettingsFolder);
        OpenPath(inst.ClientSettingsFolder);
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        Directory.CreateDirectory(inst.ModsFolder);
        OpenPath(inst.ModsFolder);
    }

    private void Shortcut_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        try
        {
            var path = ShortcutService.CreateDesktopShortcut(inst);
            MainWindowHost?.ShowToast("桌面快捷方式已创建：" + path);
        }
        catch (Exception ex)
        {
            DialogService.Error("创建快捷方式失败：\n" + ex.Message);
        }
    }

    private void ResetPlaytime_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        if (!DialogService.Confirm($"把「{inst.Name}」的游玩时长清零？", "重置游玩时长"))
            return;
        inst.PlaytimeSeconds = 0;
        InstanceStore.Save(inst);
        BindCurrent();
    }

    // ---------- FastFlags ----------

    private void RefreshFlags()
    {
        if (_current is not { } inst)
            return;
        var query = FlagSearch.Text.Trim();
        var items = inst.FastFlags
            .Where(f => query.Length == 0 ||
                        f.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        f.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Enabled)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FlagsList.ItemsSource = items;
        FlagsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FlagCountText.Text = $"{inst.FastFlags.Count(f => f.Enabled)} / {inst.FastFlags.Count} 条";

        var unknown = inst.FastFlags.Where(f => f.Enabled && !f.IsKnown).Select(f => f.Name).ToList();
        FlagsWarning.Visibility = unknown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (unknown.Count > 0)
        {
            FlagsWarningTitle.Text = $"有 {unknown.Count} 条 Flag 不在 RCL 的常见列表中";
            FlagsWarningText.Text = string.Join("、", unknown.Take(4)) +
                                    (unknown.Count > 4 ? " …" : "") +
                                    "。请确认来源可靠；启动前会照常写入。";
        }
    }

    private void FlagSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FlagSearchHint is null)
            return;
        FlagSearchHint.Visibility = string.IsNullOrEmpty(FlagSearch.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshFlags();
    }

    private void AddFlag_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;

        var dialog = new FastFlagDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Entries.Count == 0)
            return;

        int added = 0;
        int updated = 0;
        foreach (var (rawName, rawValue) in dialog.Entries)
        {
            var name = rawName.Trim();
            if (name.Length == 0)
                continue;
            var value = rawValue.Trim();
            var existing = inst.FastFlags.FirstOrDefault(f =>
                string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                existing.Value = value;
                existing.Enabled = true;
                updated++;
            }
            else
            {
                inst.FastFlags.Add(new FastFlagEntry { Name = name, Value = value });
                added++;
            }
        }

        PersistFlags();
        RefreshFlags();
        MainWindowHost?.ShowToast(
            $"FastFlags：新增 {added} 条" + (updated > 0 ? $"，更新 {updated} 条" : ""));
    }

    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst || PresetCombo.SelectedItem is not FlagPreset preset)
        {
            DialogService.Info("先从右侧下拉框选择一个预设。", "选择预设");
            return;
        }

        foreach (var (key, value) in preset.Flags)
        {
            var entry = inst.FastFlags.FirstOrDefault(f =>
                string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                inst.FastFlags.Add(new FastFlagEntry { Name = key, Value = value, Enabled = true });
            }
            else
            {
                entry.Value = value;
                entry.Enabled = true;
            }
        }

        PersistFlags();
        RefreshFlags();
        MainWindowHost?.ShowToast("已应用预设：" + preset.Name);
    }

    private void ImportFlags_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        var path = DialogService.PickFile("JSON 文件|*.json|所有文件|*.*", "导入 FastFlags");
        if (path is null)
            return;
        try
        {
            var count = FastFlags.ImportFromFile(inst, path);
            PersistFlags();
            RefreshFlags();
            MainWindowHost?.ShowToast($"已导入 {count} 条 FastFlags");
        }
        catch (Exception ex)
        {
            DialogService.Error("导入失败：\n" + ex.Message);
        }
    }

    private void ImportOtherFlags_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        var found = FastFlags.DiscoverImports();
        if (found.Count == 0)
        {
            DialogService.Info("没有在 Bloxstrap / Voidstrap / Froststrap / Fishstrap 的默认目录里找到 FastFlags 文件。\n\n可以改用「从文件导入」。", "没有找到可导入的配置");
            return;
        }

        if (!DialogService.Confirm("找到以下启动器配置：\n\n" + string.Join("\n", found) +
                                   "\n\n导入的 Flag 会合并到当前实例（同名覆盖）。继续吗？", "从其他启动器导入"))
            return;

        int total = 0;
        foreach (var path in found)
        {
            try
            {
                total += FastFlags.ImportFromFile(inst, path);
            }
            catch (Exception ex)
            {
                Log.Warn($"导入 {path} 失败：{ex.Message}");
            }
        }
        PersistFlags();
        RefreshFlags();
        MainWindowHost?.ShowToast($"从其他启动器导入了 {total} 条 Flag");
    }

    private void ExportFlags_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        var path = DialogService.PickSaveFile("JSON 文件|*.json", $"{inst.Name}-FastFlags.json", "导出 FastFlags");
        if (path is null)
            return;
        try
        {
            FastFlags.ExportToFile(inst, path);
            MainWindowHost?.ShowToast("FastFlags 已导出");
        }
        catch (Exception ex)
        {
            DialogService.Error("导出失败：\n" + ex.Message);
        }
    }

    private void WriteFlags_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        var version = RobloxInstall.Resolve(inst.VersionPath);
        if (version is null)
        {
            DialogService.Error("没有可用的 Roblox 版本目录，无法写入。", "写入失败");
            return;
        }
        try
        {
            var target = FastFlags.DeployToVersion(inst, version.Directory);
            FlagStatus.Text = target is null ? "没有启用的 Flag" : "已写入 " + Path.GetFileName(version.Directory);
            MainWindowHost?.ShowToast(target is null ? "当前没有启用的 Flag" : "FastFlags 已写入版本目录");
        }
        catch (Exception ex)
        {
            DialogService.Error("写入失败：\n" + ex.Message);
        }
    }

    private void FlagEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
            return;
        if (sender is CheckBox { DataContext: FastFlagEntry entry } box)
            entry.Enabled = box.IsChecked == true;
        PersistFlags();
        Dispatcher.BeginInvoke(() =>
        {
            RefreshFlags();
            FlagCountText.Text = $"{_current.FastFlags.Count(f => f.Enabled)} / {_current.FastFlags.Count} 条";
        }, DispatcherPriority.Background);
    }

    private void FlagValue_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_current is not { } inst || sender is not TextBox { DataContext: FastFlagEntry entry } box)
            return;
        var error = FastFlags.ValidateValue(box.Text);
        if (error is not null)
        {
            DialogService.Error($"Flag「{entry.Name}」的值不合法：{error}", "值无效");
            return;
        }
        entry.Value = box.Text.Trim();
        PersistFlags();
    }

    private void DeleteFlag_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst || sender is not Button { Tag: FastFlagEntry entry })
            return;
        if (!DialogService.Confirm($"删除 Flag「{entry.Name}」？", "删除 Flag"))
            return;
        inst.FastFlags.Remove(entry);
        PersistFlags();
        RefreshFlags();
    }

    private void PersistFlags()
    {
        if (_current is not { } inst)
            return;
        InstanceStore.Save(inst);
        try
        {
            FastFlags.SaveToInstance(inst);
        }
        catch (Exception ex)
        {
            Log.Warn("保存 FastFlags 文件失败：" + ex.Message);
        }
    }

    // ---------- Mods ----------

    private void RefreshMods()
    {
        if (_current is not { } inst)
            return;
        var mods = ModsService.List(inst);
        ModsList.ItemsSource = mods;
        ModsEmpty.Visibility = mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModCountText.Text = $"{mods.Count(m => m.Enabled)} / {mods.Count} 项";

        var version = RobloxInstall.Resolve(inst.VersionPath);
        ModStatus.Text = version is null
            ? "没有可用的 Roblox 版本目录；启动时会跳过模组覆盖。"
            : $"目标版本：{version.DisplayName}（{version.Directory}）";
        ModApplied.Text = "备份目录：" + inst.BackupFolder;
    }

    private void ImportModFile_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        var path = DialogService.PickFile(
            "模组压缩包|*.zip|所有文件|*.*", "导入模组文件");
        if (path is null)
            return;
        try
        {
            ModsService.Import(inst, path);
            RefreshMods();
            MainWindowHost?.ShowToast("模组已导入");
        }
        catch (Exception ex)
        {
            DialogService.Error("导入模组失败：\n" + ex.Message);
        }
    }

    private void ImportModFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        var path = DialogService.PickFolder("选择模组文件夹（应包含 content 等子目录）");
        if (path is null)
            return;
        try
        {
            ModsService.Import(inst, path);
            RefreshMods();
            MainWindowHost?.ShowToast("模组文件夹已导入");
        }
        catch (Exception ex)
        {
            DialogService.Error("导入模组失败：\n" + ex.Message);
        }
    }

    private void ModToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst || sender is not CheckBox { Tag: ModEntry entry })
            return;
        try
        {
            ModsService.Toggle(inst, entry);
            RefreshMods();
        }
        catch (Exception ex)
        {
            DialogService.Error("切换模组状态失败：\n" + ex.Message);
            RefreshMods();
        }
    }

    private void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst || sender is not Button { Tag: ModEntry entry })
            return;
        if (!DialogService.Confirm($"删除模组「{entry.Name}」？\n如果已经覆盖到 Roblox，可以用「还原安装」恢复备份。", "删除模组"))
            return;
        try
        {
            ModsService.Delete(inst, entry);
            RefreshMods();
        }
        catch (Exception ex)
        {
            DialogService.Error("删除模组失败：\n" + ex.Message);
        }
    }

    private void RestoreMods_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        var version = RobloxInstall.Resolve(inst.VersionPath);
        if (version is null)
        {
            DialogService.Error("没有可用的 Roblox 版本目录。", "无法还原");
            return;
        }
        if (!DialogService.Confirm(
                $"把「{inst.Name}」覆盖过的文件从备份还原，并删除由模组创建的副本文件？\n目标：{version.Directory}",
                "还原安装", "还原", "取消"))
            return;

        try
        {
            var count = ModsService.Restore(inst, version.Directory);
            RefreshMods();
            MainWindowHost?.ShowToast($"已还原 {count} 个文件");
        }
        catch (Exception ex)
        {
            DialogService.Error("还原失败：\n" + ex.Message);
        }
    }

    // ---------- Launch & lifecycle ----------

    private async void Launch_Click(object sender, RoutedEventArgs e) => await LaunchCurrentAsync(false);

    private async void LaunchMulti_Click(object sender, RoutedEventArgs e) => await LaunchCurrentAsync(true);

    private async Task LaunchCurrentAsync(bool forceMulti)
    {
        if (_current is not { } inst)
            return;

        bool multi = forceMulti || inst.MultiInstance;
        if (!multi && RobloxInstall.RunningCount() > 0 && AppSettings.Current.ConfirmLaunch)
        {
            if (!DialogService.Confirm("Roblox 已经在运行，继续启动会尝试再打开一个客户端。\n\n仍然启动吗？", "Roblox 正在运行"))
                return;
        }

        LaunchStatus.Text = "准备启动…";
        try
        {
            var result = await LaunchService.LaunchAsync(inst, null, null, null, multi, s => LaunchStatus.Text = s);
            LaunchStatus.Text = $"已启动 · PID {result.Session.Pid}";
            MainWindowHost?.ShowToast($"{inst.Name} 已启动（PID {result.Session.Pid}）");
            if (AppSettings.Current.CloseAfterLaunch)
                Application.Current.MainWindow?.Close();
        }
        catch (Exception ex)
        {
            LaunchStatus.Text = "启动失败";
            Log.Error("实例页启动失败", ex);
            DialogService.Error(ex is LaunchException ? ex.Message : "启动失败：\n" + ex.Message, "启动失败");
        }
    }

    private void NewInstance_Click(object sender, RoutedEventArgs e)
    {
        var name = DialogService.Prompt("新建实例", "实例名称", "新实例",
            "例如：主账号、测试服、低配模式");
        if (name is null)
            return;
        var inst = InstanceStore.Create(name);
        _current = inst;
        RefreshInstances();
        MainWindowHost?.ShowToast("已创建实例：" + inst.Name);
    }

    private void DuplicateInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
            return;
        var copy = InstanceStore.Duplicate(_current);
        _current = copy;
        RefreshInstances();
        MainWindowHost?.ShowToast("已复制实例：" + copy.Name);
    }

    private void DeleteInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } inst)
            return;
        if (!DialogService.Confirm(
                $"确定删除实例「{inst.Name}」吗？\n\n模组、FastFlags、备份和游玩记录都会被删除，操作不可撤销。",
                "删除实例", "删除", "取消"))
            return;

        try
        {
            InstanceStore.Delete(inst);
            _current = null;
            RefreshInstances();
            MainWindowHost?.ShowToast("已删除实例：" + inst.Name);
        }
        catch (Exception ex)
        {
            DialogService.Error("删除失败：\n" + ex.Message);
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DialogService.Error("无法打开目录：\n" + ex.Message);
        }
    }

    // ---------- WEAO executor following ----------

    private sealed record ExecutorChoice(string Title, string Display)
    {
        public override string ToString() => Display;
    }

    private bool _executorCheckBusy;

    private async Task EnsureExecutorListAsync()
    {
        if (_executorListLoading)
            return;
        if (_executorChoices is not null)
        {
            RefreshExecutorUi(_current);
            return;
        }

        _executorListLoading = true;
        try
        {
            var snapshot = await WeaoService.GetSnapshotAsync(false);
            var choices = new List<ExecutorChoice>
            {
                new("", "自动（最多执行器支持）")
            };
            choices.AddRange(snapshot.WindowsExecutors.Select(e =>
                new ExecutorChoice(e.Title, $"{e.Title} · {e.SupportedVersionShort}")));
            _executorChoices = choices;
            _refreshing = true;
            FollowExecutorCombo.ItemsSource = choices;
            _executorChoices = choices;
            _refreshing = false;
            RefreshExecutorUi(_current);
        }
        catch (Exception ex)
        {
            Log.Warn("加载执行器列表失败：" + ex.Message);
            FollowHint.Text = "无法加载 WEAO 执行器列表；仍可使用「自动」模式。";
        }
        finally
        {
            _executorListLoading = false;
        }
    }

    private void RefreshExecutorUi(RclInstance? instance)
    {
        if (FollowExecutorCheck is null || instance is null)
            return;

        _refreshing = true;
        FollowExecutorCheck.IsChecked = instance.FollowExecutorVersion;
        AutoDownloadCheck.IsChecked = instance.AutoDownloadExecutorVersion;
        AutoDownloadCheck.IsEnabled = true;
        FollowExecutorCombo.IsEnabled = true;
        FollowOptionsPanel.Visibility = instance.FollowExecutorVersion
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (_executorChoices is not null)
        {
            FollowExecutorCombo.ItemsSource = _executorChoices;
            FollowExecutorCombo.SelectedItem = _executorChoices.FirstOrDefault(c =>
                string.Equals(c.Title, instance.FollowExecutor, StringComparison.OrdinalIgnoreCase))
                ?? _executorChoices[0];
        }
        _refreshing = false;

        if (instance.FollowExecutorVersion)
        {
            var label = string.IsNullOrWhiteSpace(instance.FollowExecutor)
                ? "自动（最多执行器支持）"
                : instance.FollowExecutor;
            FollowHint.Text = $"跟随：{label}。启动器会按 WEAO 的支持版本自动下载并锁定。";
        }
        else
        {
            FollowHint.Text = "开启后会按 WEAO 中执行器支持的 Roblox 版本自动锁定实例；下载新版本约 230 MB。";
        }
    }

    private async void FollowExecutor_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _current is not { } inst)
            return;

        if (FollowExecutorCheck.IsChecked == true)
        {
            if (!DialogService.Confirm(
                    "启用后，RCL 会在 WEAO 显示执行器支持新版本时自动下载（约 230 MB）并把实例锁定到该版本。\n\n继续吗？",
                    "跟随执行器版本", "启用", "取消"))
            {
                _refreshing = true;
                FollowExecutorCheck.IsChecked = false;
                _refreshing = false;
                return;
            }

            inst.FollowExecutorVersion = true;
            inst.AutoDownloadExecutorVersion = true;
            await EnsureExecutorListAsync();
        }
        else
        {
            inst.FollowExecutorVersion = false;
        }

        InstanceStore.Save(inst);
        RefreshExecutorUi(inst);
        if (inst.FollowExecutorVersion)
            await RunExecutorCheckAsync(inst);
    }

    private void AutoDownload_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _current is null)
            return;
        _current.AutoDownloadExecutorVersion = AutoDownloadCheck.IsChecked == true;
        InstanceStore.Save(_current);
    }

    private async void FollowExecutor_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing || _current is not { } inst ||
            FollowExecutorCombo.SelectedItem is not ExecutorChoice choice)
            return;
        inst.FollowExecutor = choice.Title;
        InstanceStore.Save(inst);
        if (inst.FollowExecutorVersion)
            await RunExecutorCheckAsync(inst);
    }

    private async void CheckExecutorNow_Click(object sender, RoutedEventArgs e)
    {
        if (_current is { } inst)
            await RunExecutorCheckAsync(inst);
    }

    private async Task RunExecutorCheckAsync(RclInstance inst)
    {
        if (_executorCheckBusy)
            return;
        _executorCheckBusy = true;
        FollowHint.Text = "正在检查执行器支持版本（下载时可能需要几分钟）…";
        try
        {
            var result = await ExecutorFollowService.CheckSingleAsync(inst, true);
            FollowHint.Text = result;
            if (_current?.Id == inst.Id)
            {
                RefreshVersions();
                UpdateLaunchStatus();
            }
            MainWindowHost?.ShowToast(result);
        }
        catch (Exception ex)
        {
            FollowHint.Text = "检查失败：" + ex.Message;
            Log.Warn("检查执行器版本失败：" + ex.Message);
        }
        finally
        {
            _executorCheckBusy = false;
        }
    }

    private MainWindow? MainWindowHost => Window.GetWindow(this) as MainWindow;
}
