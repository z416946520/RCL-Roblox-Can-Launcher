using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using RCL.Core;
using RCL.Dialogs;
using RCL.Models;

namespace RCL.Pages;

public partial class QuickLaunchPage : UserControl
{
    private QuickLaunchEntry? _current;
    private bool _refreshing;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    public QuickLaunchPage()
    {
        InitializeComponent();
        _saveTimer.Tick += (_, _) => FlushSave();
        _stateTimer.Tick += (_, _) => RefreshRunningState();
        QuickLaunchService.Changed += OnServiceChanged;
        Loaded += (_, _) =>
        {
            RefreshEntries();
            _stateTimer.Start();
        };
    }

    public void OnActivated()
    {
        RefreshEntries();
        RefreshRunningState();
    }

    private void OnServiceChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshEntries);
            return;
        }
        RefreshEntries();
    }

    private void RefreshEntries()
    {
        if (EntriesList is null)
            return;

        _refreshing = true;
        var query = SearchBox.Text.Trim();
        var items = QuickLaunchService.Entries
            .Where(e => query.Length == 0 ||
                        e.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        e.TargetPath.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        EntriesList.ItemsSource = items;
        EntryCountText.Text = QuickLaunchService.Entries.Count.ToString();
        var selected = items.FirstOrDefault(e => e.Id == _current?.Id) ?? items.FirstOrDefault();
        EntriesList.SelectedItem = selected;
        _current = selected;
        _refreshing = false;

        EmptyPanel.Visibility = QuickLaunchService.Entries.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        DetailScroll.Visibility = QuickLaunchService.Entries.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
        BindCurrent();
    }

    private void BindCurrent()
    {
        if (_current is not { } entry)
            return;

        _refreshing = true;
        NameBox.Text = entry.Name;
        PathBox.Text = entry.TargetPath;
        ArgsBox.Text = entry.Arguments;
        WorkDirBox.Text = entry.WorkingDirectory;
        ProcessBox.Text = entry.ProcessName;
        AdminCheck.IsChecked = entry.RunAsAdmin;
        _refreshing = false;

        HeaderName.Text = entry.Name;
        HeaderGlyph.Text = entry.IconGlyph;
        HeaderMeta.Text = entry.TypeLabel + " · " + entry.PathDisplay;
        UpdateHeaderIcon(entry);
        PathStatusText.Text = string.IsNullOrWhiteSpace(entry.TargetPath)
            ? "还没有设置目标路径"
            : entry.PathExists
                ? "路径有效"
                : "⚠ 目标文件不存在";
        PathStatusText.Foreground = (entry.PathExists || string.IsNullOrWhiteSpace(entry.TargetPath))
            ? (System.Windows.Media.Brush)FindResource("BrushTextMuted")
            : (System.Windows.Media.Brush)FindResource("BrushDanger");
        LastUsedText.Text = "上次启动：" + entry.LastUsedDisplay;
        SaveStatus.Text = "自动保存";
        RefreshRunningState();
    }

    private void RefreshRunningState()
    {
        if (_current is not { } entry)
            return;

        try
        {
            QuickLaunchService.RefreshRunningStates();
            var running = QuickLaunchService.FindRunning(entry);
            StopButton.IsEnabled = running.Count > 0;
            RunningInfoText.Text = running.Count == 0
                ? "未运行"
                : $"运行中 · {running.Count} 个进程 · PID " +
                  string.Join(", ", running.Select(p => p.Id));
            foreach (var process in running)
                process.Dispose();
        }
        catch (Exception ex)
        {
            RunningInfoText.Text = "无法读取运行状态：" + ex.Message;
            StopButton.IsEnabled = false;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is null)
            return;
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshEntries();
    }

    private void EntriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing)
            return;
        FlushSave();
        _current = EntriesList.SelectedItem as QuickLaunchEntry;
        BindCurrent();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshing || _current is null)
            return;
        _current.Name = NameBox.Text;
        HeaderName.Text = string.IsNullOrWhiteSpace(NameBox.Text) ? "未命名" : NameBox.Text;
        QueueSave();
    }

    private void PathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshing || _current is null)
            return;
        _current.TargetPath = PathBox.Text;
        PathStatusText.Text = string.IsNullOrWhiteSpace(PathBox.Text)
            ? "还没有设置目标路径"
            : File.Exists(PathBox.Text)
                ? "路径有效"
                : "⚠ 目标文件不存在";
        QueueSave();
    }

    private void PathBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_refreshing || _current is null || !File.Exists(PathBox.Text))
            return;

        _refreshing = true;
        QuickLaunchService.ApplyPath(_current, PathBox.Text, keepName: true);
        NameBox.Text = _current.Name;
        PathBox.Text = _current.TargetPath;
        WorkDirBox.Text = _current.WorkingDirectory;
        ProcessBox.Text = _current.ProcessName;
        HeaderGlyph.Text = _current.IconGlyph;
        HeaderMeta.Text = _current.TypeLabel + " · " + _current.PathDisplay;
        _refreshing = false;
        UpdateHeaderIcon(_current);
        FlushSave();
    }

    private void UpdateHeaderIcon(QuickLaunchEntry? entry)
    {
        if (HeaderIconImage is null)
            return;
        var icon = AppIconService.GetIcon(entry?.TargetPath);
        HeaderIconImage.Source = icon;
        HeaderGlyph.Visibility = icon is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ArgsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshing || _current is null)
            return;
        _current.Arguments = ArgsBox.Text;
        QueueSave();
    }

    private void WorkDirBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshing || _current is null)
            return;
        _current.WorkingDirectory = WorkDirBox.Text;
        QueueSave();
    }

    private void ProcessBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshing || _current is null)
            return;
        _current.ProcessName = ProcessBox.Text;
        QueueSave();
    }

    private void AdminCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
            return;
        _current.RunAsAdmin = AdminCheck.IsChecked == true;
        FlushSave();
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
            _current.Name = "未命名快捷启动";
        QuickLaunchService.Save();
        SaveStatus.Text = "已保存 " + DateTime.Now.ToString("HH:mm:ss");
    }

    private void AddApplication_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "添加快捷启动",
            Filter = "应用与脚本|*.exe;*.lnk;*.cmd;*.bat;*.ps1|可执行程序|*.exe|快捷方式|*.lnk|脚本|*.cmd;*.bat;*.ps1|所有文件|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        AddPaths(dialog.FileNames);
    }

    private async void AddPotassium_Click(object sender, RoutedEventArgs e)
    {
        var existing = QuickLaunchService.Entries.FirstOrDefault(x =>
            x.Name.Contains("Potassium", StringComparison.OrdinalIgnoreCase) ||
            x.TargetPath.Contains("Potassium", StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _current = existing;
            RefreshEntries();
            MainWindowHost?.ShowToast("已选择 Potassium；点击「启动」即可运行");
            return;
        }

        string? path = null;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            path = await Task.Run(QuickLaunchService.TryLocatePotassium);
        }
        catch (Exception ex)
        {
            Log.Warn("自动定位 Potassium 失败：" + ex.Message);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择 Potassium.exe（受保护程序可能无法自动定位）",
                Filter = "Potassium|Potassium*.exe|可执行程序|*.exe|所有文件|*.*"
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;
            path = dialog.FileName;
        }

        var entry = QuickLaunchService.CreateFromPath(path, "Potassium");
        entry.ProcessName = "Potassium";
        QuickLaunchService.Save();
        _current = entry;
        RefreshEntries();
        MainWindowHost?.ShowToast("已添加 Potassium，点击「启动」即可运行");
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        QuickLaunchEntry? last = null;
        int added = 0;
        foreach (var path in paths)
        {
            if (!File.Exists(path))
                continue;
            last = QuickLaunchService.CreateFromPath(path);
            added++;
        }
        if (last is not null)
        {
            _current = last;
            RefreshEntries();
            MainWindowHost?.ShowToast($"已添加 {added} 个快捷启动");
        }
    }

    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
            return;
        var dialog = new OpenFileDialog
        {
            Title = "选择目标",
            Filter = "应用与脚本|*.exe;*.lnk;*.cmd;*.bat;*.ps1|所有文件|*.*"
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        QuickLaunchService.ApplyPath(_current, dialog.FileName, keepName: false);
        BindCurrent();
        FlushSave();
    }

    private void BrowseWorkDir_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
            return;
        var dialog = new OpenFolderDialog { Title = "选择工作目录" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        _current.WorkingDirectory = dialog.FolderName;
        WorkDirBox.Text = dialog.FolderName;
        FlushSave();
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } entry)
            return;
        if (!entry.PathExists)
        {
            DialogService.Error("目标文件不存在，请先设置路径。", "无法启动");
            return;
        }

        try
        {
            if (QuickLaunchService.IsRunning(entry))
            {
                if (!DialogService.Confirm(
                        $"「{entry.Name}」已经在运行。\n\n是否结束旧进程并启动一个新的？",
                        "应用已在运行", "结束并重启", "取消"))
                    return;
                QuickLaunchService.Restart(entry);
                MainWindowHost?.ShowToast("已重启：" + entry.Name);
            }
            else
            {
                QuickLaunchService.Launch(entry);
                MainWindowHost?.ShowToast("已启动：" + entry.Name);
            }
        }
        catch (Exception ex)
        {
            Log.Error("快捷启动失败", ex);
            DialogService.Error("启动失败：\n" + ex.Message, "快捷启动");
        }
        RefreshRunningState();
        LastUsedText.Text = "上次启动：" + entry.LastUsedDisplay;
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } entry)
            return;
        if (!DialogService.Confirm($"结束「{entry.Name}」的所有进程？", "结束进程", "结束", "取消"))
            return;
        var count = QuickLaunchService.Kill(entry);
        RefreshRunningState();
        if (count == 0 && QuickLaunchService.IsRunning(entry))
            DialogService.Error("无法结束进程。它可能受保护或需要管理员权限，请手动关闭。", "结束失败");
        else
            MainWindowHost?.ShowToast(count == 0 ? "没有找到运行中的进程" : $"已结束 {count} 个进程");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not { } entry)
            return;
        if (!DialogService.Confirm($"删除快捷启动「{entry.Name}」？", "删除快捷启动", "删除", "取消"))
            return;
        QuickLaunchService.Remove(entry);
        _current = null;
        RefreshEntries();
        MainWindowHost?.ShowToast("已删除快捷启动");
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;
        AddPaths(files);
        e.Handled = true;
    }

    private MainWindow? MainWindowHost => Window.GetWindow(this) as MainWindow;
}
