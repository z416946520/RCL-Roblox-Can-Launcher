using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RCL.Core;
using RCL.Dialogs;
using RCL.Models;

namespace RCL.Pages;

public partial class ToolsPage : UserControl
{
    private sealed record ShortcutItem(string Name, string FullName, string Path);
    private sealed record SlotRow(string Name, string Target, string State);
    private sealed record ProcRow(int Pid, string Display, string Detail, string Memory);

    private static readonly string[] Headings = { "服务器浏览器", "快捷方式", "多开与进程", "运行日志", "配置导入" };

    private static readonly string[] Subheadings =
    {
        "查看公开服务器并直接加入；加入操作使用当前选中的实例。",
        "为实例创建带参数的桌面快捷方式，双击即可启动。",
        "查看单例锁、目录联接槽位与正在运行的 Roblox 进程。",
        "查看 RCL 的运行日志，排查启动失败与安装问题。",
        "把其他启动器的 FastFlags 合并到 RCL 实例。"
    };

    private readonly DispatcherTimer _logTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private long _placeId;
    private string _gameName = "";
    private List<RobloxServerInfo> _servers = new();

    public ToolsPage()
    {
        InitializeComponent();
        TargetInstanceCombo.ItemsSource = InstanceStore.All;
        Loaded += (_, _) =>
        {
            SelectTargetInstance();
            RefreshShortcuts();
            RefreshSlots();
            RefreshProcesses();
            RefreshImports();
            RefreshLog();
        };
        _logTimer.Tick += (_, _) =>
        {
            if (PanelLogs.IsVisible)
                RefreshLog();
        };
        _logTimer.Start();
    }

    public void OnActivated()
    {
        SelectTargetInstance();
        RefreshShortcuts();
        RefreshSlots();
        RefreshProcesses();
        RefreshImports();
    }

    private void SelectTargetInstance()
    {
        var current = TargetInstanceCombo.SelectedItem as RclInstance;
        var target = InstanceStore.Find(Guid.TryParseExact(AppSettings.Current.LastInstanceId, "N", out var id)
            ? id
            : Guid.Empty) ?? current ?? InstanceStore.All.FirstOrDefault();
        TargetInstanceCombo.SelectedItem = target;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PanelServers is null || NavList.SelectedIndex < 0)
            return;
        int index = NavList.SelectedIndex;
        PageHeading.Text = Headings[index];
        PageSubheading.Text = Subheadings[index];

        PanelServers.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelShortcuts.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelProcesses.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelLogs.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PanelImport.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

        if (index == 2)
        {
            RefreshSlots();
            RefreshProcesses();
        }
        else if (index == 3)
        {
            RefreshLog();
        }
        else if (index == 4)
        {
            RefreshImports();
        }
    }

    // ---------- Servers ----------

    private async void Query_Click(object sender, RoutedEventArgs e) => await QueryAsync();

    private async void PlaceBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await QueryAsync();
        }
    }

    private async Task QueryAsync()
    {
        var placeId = RobloxApi.ParsePlaceId(PlaceBox.Text);
        if (placeId <= 0)
        {
            DialogService.Error("没有解析出 Place ID。可以粘贴完整游戏链接，或直接输入数字。", "查询失败");
            return;
        }

        _placeId = placeId;
        QueryStatus.Text = "正在查询游戏信息…";
        QueryStatus.Foreground = (Brush)FindResource("BrushTextMuted");
        GameInfoPanel.Visibility = Visibility.Collapsed;
        ServersList.ItemsSource = null;
        _servers.Clear();
        ServerCountText.Text = "0 个";
        ServersEmpty.Visibility = Visibility.Visible;

        await LoadGameAsync(placeId);
        await LoadServersAsync(placeId);
    }

    private async Task LoadGameAsync(long placeId)
    {
        try
        {
            var info = await RobloxApi.GetGameInfoAsync(placeId);
            _gameName = info.Name;
            GameName.Text = info.Name;
            GameMeta.Text = string.IsNullOrWhiteSpace(info.Creator)
                ? $"Place {placeId} · 在线 {info.Playing}"
                : $"{info.Creator} · 在线 {info.Playing} · Place {placeId}";
            GameInfoPanel.Visibility = Visibility.Visible;
            QueryStatus.Text = "游戏信息已更新";
            if (!string.IsNullOrWhiteSpace(info.ThumbnailUrl))
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.UriSource = new Uri(info.ThumbnailUrl);
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    GameThumb.Source = image;
                }
                catch (Exception ex)
                {
                    Log.Warn("缩略图加载失败：" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            QueryStatus.Text = "游戏信息查询失败：" + ex.Message;
            QueryStatus.Foreground = (Brush)FindResource("BrushDanger");
            GameInfoPanel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task LoadServersAsync(long placeId)
    {
        try
        {
            QueryStatus.Text = "正在获取公开服务器…";
            _servers = await RobloxApi.GetPublicServersAsync(placeId);
            ServersList.ItemsSource = _servers;
            ServerCountText.Text = $"{_servers.Count} 个";
            ServersEmpty.Visibility = _servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            QueryStatus.Text = _servers.Count == 0
                ? "没有公开服务器（游戏可能限制查询，或暂时没有在线公开服）。"
                : $"已获取 {_servers.Count} 个公开服务器";
            QueryStatus.Foreground = (Brush)FindResource("BrushTextMuted");
        }
        catch (Exception ex)
        {
            QueryStatus.Text = "服务器列表获取失败：" + ex.Message;
            QueryStatus.Foreground = (Brush)FindResource("BrushDanger");
            Log.Warn("获取服务器列表失败：" + ex.Message);
        }
    }

    private async void RefreshServers_Click(object sender, RoutedEventArgs e)
    {
        if (_placeId == 0)
        {
            DialogService.Info("先输入一个游戏并查询。", "没有选择游戏");
            return;
        }
        await LoadServersAsync(_placeId);
    }

    private async void JoinServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RobloxServerInfo server })
            return;
        if (TargetInstanceCombo.SelectedItem is not RclInstance inst)
        {
            DialogService.Info("先选择一个目标实例。", "没有实例");
            return;
        }
        if (_placeId == 0)
            return;

        bool multi = inst.MultiInstance;
        if (!multi && RobloxInstall.RunningCount() > 0 &&
            !DialogService.Confirm("Roblox 已经在运行。继续会尝试再打开一个客户端。\n\n仍然加入吗？", "Roblox 正在运行"))
            return;

        var progressWindow = new LaunchProgressWindow(RobloxInstall.Resolve(inst.VersionPath));
        try { progressWindow.Owner = Window.GetWindow(this); } catch { }
        progressWindow.Show();
        try
        {
            QueryStatus.Text = $"正在加入 {server.Id[..Math.Min(8, server.Id.Length)]}…";
            var result = await LaunchService.LaunchAsync(inst, _placeId, server.Id, _gameName, multi,
                progressWindow.Report, progressWindow.Token);
            QueryStatus.Text = $"已加入（PID {result.Session.Pid}）";
            MainWindowHost?.ShowToast($"{inst.Name} 已加入服务器 {server.PlayersDisplay}");
        }
        catch (OperationCanceledException)
        {
            QueryStatus.Text = "已取消";
            Log.Info("用户取消了加入：" + inst.Name);
        }
        catch (Exception ex)
        {
            Log.Error("加入服务器失败", ex);
            DialogService.Error(ex is LaunchException ? ex.Message : "加入失败：\n" + ex.Message, "加入失败");
        }
        finally
        {
            progressWindow.CloseSafely();
        }
    }

    // ---------- Shortcuts ----------

    private void RefreshShortcuts()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var items = new List<ShortcutItem>();
        try
        {
            foreach (var file in Directory.GetFiles(desktop, "RCL - *.lnk"))
            {
                items.Add(new ShortcutItem(Path.GetFileNameWithoutExtension(file), file, file));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("扫描快捷方式失败：" + ex.Message);
        }

        ShortcutsList.ItemsSource = items;
        ShortcutCount.Text = $"{items.Count} 个";
        ShortcutsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CreateShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (TargetInstanceCombo.SelectedItem is not RclInstance inst)
        {
            DialogService.Info("先选择一个目标实例。", "没有实例");
            return;
        }
        try
        {
            var path = ShortcutService.CreateDesktopShortcut(inst);
            ShortcutStatus.Text = "已创建：" + path;
            MainWindowHost?.ShowToast("桌面快捷方式已创建");
            RefreshShortcuts();
        }
        catch (Exception ex)
        {
            ShortcutStatus.Text = "创建失败：" + ex.Message;
            DialogService.Error("创建快捷方式失败：\n" + ex.Message);
        }
    }

    private void OpenDesktop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe",
                $"\"{Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DialogService.Error("无法打开桌面目录：\n" + ex.Message);
        }
    }

    private void DeleteShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ShortcutItem item })
            return;
        if (!DialogService.Confirm($"删除快捷方式「{item.Name}」？", "删除快捷方式"))
            return;
        try
        {
            File.Delete(item.Path);
            RefreshShortcuts();
            MainWindowHost?.ShowToast("快捷方式已删除");
        }
        catch (Exception ex)
        {
            DialogService.Error("删除失败：\n" + ex.Message);
        }
    }

    // ---------- Multi instance & processes ----------

    private void RefreshSlots()
    {
        var rows = new List<SlotRow>();
        try
        {
            if (Directory.Exists(AppPaths.MultiInstance))
            {
                var busy = MachineBusyPaths.Scan();
                foreach (var dir in Directory.GetDirectories(AppPaths.MultiInstance, "slot*")
                             .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string target = "";
                    try
                    {
                        target = new DirectoryInfo(dir).LinkTarget ?? "(不是目录联接)";
                    }
                    catch
                    {
                    }
                    bool used = busy.Paths.Contains(dir.TrimEnd(Path.DirectorySeparatorChar));
                    rows.Add(new SlotRow(Path.GetFileName(dir), target, used ? "占用中" : "空闲"));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取多开槽位失败：" + ex.Message);
        }

        SlotsList.ItemsSource = rows;
        SlotsEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MultiStatus.Text = MultiInstanceLock.IsHeld
            ? $"单例锁已持有 · 已用 {rows.Count(r => r.State == "占用中")} 个槽位 / 上限 {AppSettings.Current.MaxSlots}"
            : $"单例锁尚未持有（首次多开启动时自动获取）· 槽位上限 {AppSettings.Current.MaxSlots}";
    }

    private void CleanupSlots_Click(object sender, RoutedEventArgs e)
    {
        MultiInstanceLock.CleanupUnused();
        RefreshSlots();
        MainWindowHost?.ShowToast("已清理空闲槽位");
    }

    private void RefreshProcesses()
    {
        var rows = new List<ProcRow>();
        var tracked = new HashSet<int>();

        foreach (var session in SessionManager.Sessions)
        {
            tracked.Add(session.Pid);
            rows.Add(new ProcRow(
                session.Pid,
                $"RCL 会话 · {session.InstanceName}",
                $"PID {session.Pid} · {session.Title} · 已运行 {session.Elapsed}",
                ""));
        }

        try
        {
            foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                using (process)
                {
                    if (tracked.Contains(process.Id))
                        continue;

                    string detail;
                    string memory = "";
                    try
                    {
                        var path = process.MainModule?.FileName ?? "RobloxPlayerBeta.exe";
                        var started = "";
                        try { started = process.StartTime.ToString("HH:mm:ss"); } catch { }
                        detail = $"PID {process.Id} · {path}" + (started.Length > 0 ? $" · 启动于 {started}" : "");
                        memory = $"{process.WorkingSet64 / 1048576.0:0} MB";
                    }
                    catch
                    {
                        detail = $"PID {process.Id} · 路径不可访问（可能由其他用户启动）";
                    }

                    rows.Add(new ProcRow(process.Id, "Roblox 客户端（非 RCL 会话）", detail, memory));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("枚举 Roblox 进程失败：" + ex.Message);
        }

        ProcessesList.ItemsSource = rows;
        ProcessesEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        RefreshProcesses();
        RefreshSlots();
    }

    private void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProcRow row })
            return;
        if (!DialogService.Confirm(
                $"结束进程 PID {row.Pid}？\n\n{row.Display}\n{row.Detail}\n\n未保存的游戏进度会丢失。",
                "结束进程", "结束", "取消"))
            return;

        try
        {
            using var process = Process.GetProcessById(row.Pid);
            process.Kill(true);
            Log.Info("已结束进程 PID " + row.Pid);
            Dispatcher.BeginInvoke(RefreshProcesses, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            DialogService.Error("结束进程失败：\n" + ex.Message);
        }
    }

    // ---------- Logs ----------

    private void RefreshLog()
    {
        if (LogBox is null)
            return;
        LogBox.Text = Log.Tail(500);
        LogBox.ScrollToEnd();
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => RefreshLog();

    private void OpenLogFile_Click(object sender, RoutedEventArgs e) => Log.Open();

    private void OpenRobloxLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RobloxLogs);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.RobloxLogs}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DialogService.Error("无法打开 Roblox 日志目录：\n" + ex.Message);
        }
    }

    // ---------- Import ----------

    private void RefreshImports()
    {
        var found = FastFlags.DiscoverImports();
        ImportsList.ItemsSource = found;
        ImportsEmpty.Visibility = found.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ImportStatus.Text = found.Count == 0
            ? "没有找到可导入的配置。"
            : $"找到 {found.Count} 个配置来源。";
    }

    private void ImportOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path })
            return;
        ImportPaths(new[] { path });
    }

    private void ImportAll_Click(object sender, RoutedEventArgs e)
    {
        var found = FastFlags.DiscoverImports();
        if (found.Count == 0)
        {
            DialogService.Info("没有找到其他启动器的 FastFlags 文件。", "没有可导入的配置");
            return;
        }
        ImportPaths(found);
    }

    private void ImportPaths(IReadOnlyList<string> paths)
    {
        if (TargetInstanceCombo.SelectedItem is not RclInstance inst)
        {
            DialogService.Info("先选择一个目标实例。", "没有实例");
            return;
        }

        int total = 0;
        var errors = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                total += FastFlags.ImportFromFile(inst, path);
            }
            catch (Exception ex)
            {
                errors.Add(Path.GetFileName(path) + "：" + ex.Message);
                Log.Warn($"导入 {path} 失败：{ex.Message}");
            }
        }

        InstanceStore.Save(inst);
        try
        {
            FastFlags.SaveToInstance(inst);
        }
        catch (Exception ex)
        {
            Log.Warn("写入实例 FastFlags 文件失败：" + ex.Message);
        }

        ImportStatus.Text = $"已导入 {total} 条到「{inst.Name}」" +
                            (errors.Count > 0 ? "；失败：" + string.Join("；", errors) : "");
        MainWindowHost?.ShowToast($"已导入 {total} 条 FastFlags");
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Base}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DialogService.Error("无法打开数据目录：\n" + ex.Message);
        }
    }

    private MainWindow? MainWindowHost => Window.GetWindow(this) as MainWindow;
}
