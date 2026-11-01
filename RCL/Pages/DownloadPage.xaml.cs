using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RCL.Core;
using RCL.Dialogs;
using RCL.Models;

namespace RCL.Pages;

public partial class DownloadPage : UserControl
{
    private sealed record ChannelChoice(string Name, string Id)
    {
        public override string ToString() => Name;
    }

    private static readonly ChannelChoice[] Channels =
    {
        new("默认渠道", "default"),
        new("正式渠道 (live)", "live"),
        new("自定义渠道…", "__custom__")
    };

    private string _channel = "default";
    private readonly System.Collections.ObjectModel.ObservableCollection<ChannelChoice> _channelChoices =
        new(Channels);
    private RobloxDeploymentInfo? _deployment;
    private CancellationTokenSource? _cts;
    private bool _refreshingChannel;
    private bool _busy;
    private bool _executorBusy;
    private bool _weaoLoaded;
    private CancellationTokenSource? _executorCts;
    private WeaoSnapshot? _weaoSnapshot;

    public DownloadPage()
    {
        InitializeComponent();
        ChannelCombo.ItemsSource = _channelChoices;
        ChannelCombo.SelectedIndex = 0;
        TargetInstanceCombo.ItemsSource = InstanceStore.All;
        Loaded += (_, _) =>
        {
            RefreshVersions();
            SelectTargetInstance();
            if (_deployment is null)
                _ = LoadDeploymentAsync();
        };
        InstanceStore.Changed += () =>
        {
            if (IsLoaded)
                Dispatcher.BeginInvoke(SelectTargetInstance);
        };
    }

    public void OnActivated()
    {
        RefreshVersions();
        SelectTargetInstance();
        if (_deployment is null)
            _ = LoadDeploymentAsync();
        if (NavList.SelectedIndex == 2)
            _ = LoadExecutorsAsync(false);
    }

    private void SelectTargetInstance()
    {
        var current = TargetInstanceCombo.SelectedItem as RclInstance;
        var target = InstanceStore.Find(Guid.TryParseExact(AppSettings.Current.LastInstanceId, "N", out var id)
            ? id
            : Guid.Empty) ?? current ?? InstanceStore.All.FirstOrDefault();
        TargetInstanceCombo.SelectedItem = target;
        ExecutorTargetCombo.ItemsSource = InstanceStore.All;
        ExecutorTargetCombo.SelectedItem = target;
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InstallPanel is null)
            return;
        int index = NavList.SelectedIndex;
        InstallPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        VersionsPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelExecutors.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (index == 1)
            RefreshVersions();
        if (index == 2)
            _ = LoadExecutorsAsync(false);
    }

    private async Task LoadDeploymentAsync()
    {
        VersionValue.Text = "正在查询…";
        VersionGuidValue.Text = "";
        InstallStateTag.Background = (System.Windows.Media.Brush)FindResource("BrushHover");
        InstallStateText.Text = "查询中";
        InstallButton.IsEnabled = false;
        RepairButton.IsEnabled = false;
        _deployment = null;

        try
        {
            _deployment = await RobloxApi.GetDeploymentAsync(_channel);
            VersionValue.Text = _deployment.Version;
            VersionGuidValue.Text = _deployment.ClientVersionUpload;
            UpdateInstallState();
            Log.Info($"渠道 {_channel} 当前版本：{_deployment.Version} ({_deployment.ClientVersionUpload})");
        }
        catch (Exception ex)
        {
            VersionValue.Text = "无法获取版本信息";
            VersionGuidValue.Text = ex.Message;
            InstallStateText.Text = "查询失败";
            InstallStateTag.Background = (System.Windows.Media.Brush)FindResource("BrushDangerSoft");
            InstallButton.IsEnabled = false;
            RepairButton.IsEnabled = false;
            Log.Warn("查询部署信息失败：" + ex.Message);
        }
    }

    private void UpdateInstallState()
    {
        if (_deployment is null)
            return;

        bool installed = RobloxDeployment.IsInstalled(_deployment.ClientVersionUpload);
        InstallStateTag.Background = (System.Windows.Media.Brush)FindResource(installed ? "BrushSuccessSoft" : "BrushHover");
        InstallStateText.Foreground = (System.Windows.Media.Brush)FindResource(installed ? "BrushSuccess" : "BrushTextSecondary");
        InstallStateText.Text = installed ? "已安装" : "未安装";
        InstallButton.Content = installed ? "重新安装" : "下载并安装";
        InstallButton.IsEnabled = !_busy;
        RepairButton.IsEnabled = !_busy && installed;

        var dir = RobloxDeployment.GetInstallDirectory(_deployment.ClientVersionUpload);
        InstallPathText.Text =
            $"目标目录：{dir}\n" +
            "下载内容来自 setup.rbxcdn.com，安装后可直接在实例里绑定这个版本。已有版本不会被删除，方便做版本隔离。";
        ChannelHint.Text = _channel == "default"
            ? "默认渠道与 Roblox 官方更新保持一致。"
            : $"当前使用渠道：{_channel}（非默认渠道可能随时变化或需要 Roblox 授权）。";
    }

    private async void ChannelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingChannel || ChannelCombo.SelectedItem is not ChannelChoice choice)
            return;

        if (choice.Id == "__custom__")
        {
            var custom = DialogService.Prompt("自定义渠道", "渠道名称", "",
                "例如 zcanary；大多数非公开渠道会返回 401");
            if (string.IsNullOrWhiteSpace(custom))
            {
                _refreshingChannel = true;
                ChannelCombo.SelectedItem = _channelChoices.FirstOrDefault(c => c.Id == _channel) ?? _channelChoices[0];
                _refreshingChannel = false;
                return;
            }

            custom = custom.Trim();
            var existing = _channelChoices.FirstOrDefault(c =>
                string.Equals(c.Id, custom, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new ChannelChoice("自定义：" + custom, custom);
                _channelChoices.Add(existing);
            }
            _channel = custom;
            _refreshingChannel = true;
            ChannelCombo.SelectedItem = existing;
            _refreshingChannel = false;
        }
        else
        {
            _channel = choice.Id;
        }

        await LoadDeploymentAsync();
    }

    private async void RefreshChannel_Click(object sender, RoutedEventArgs e)
    {
        await LoadDeploymentAsync();
        if (_deployment is not null)
            MainWindowHost?.ShowToast("版本信息已刷新");
    }

    private async void Install_Click(object sender, RoutedEventArgs e) => await InstallAsync(false);

    private async void Repair_Click(object sender, RoutedEventArgs e) => await InstallAsync(true);

    private async Task InstallAsync(bool repair)
    {
        if (_busy)
            return;
        if (_deployment is null)
        {
            await LoadDeploymentAsync();
            if (_deployment is null)
            {
                DialogService.Error("无法获取 Roblox 部署信息，请先检查网络。", "无法安装");
                return;
            }
        }

        var running = RobloxInstall.RunningCount();
        var warning = running > 0
            ? $"\n\n检测到 {running} 个 Roblox 进程正在运行，覆盖被占用的文件可能失败，建议先关闭游戏。"
            : "";
        if (!DialogService.Confirm(
                $"将下载 Roblox {_deployment.Version}（约 230 MB，解压后约 355 MB）。\n" +
                $"目标：{RobloxDeployment.GetInstallDirectory(_deployment.ClientVersionUpload)}{warning}\n\n继续吗？",
                repair ? "修复 Roblox 客户端" : "安装 Roblox 客户端", "开始", "取消"))
            return;

        SetBusy(true);
        _cts = new CancellationTokenSource();
        InstallProgressPanel.Visibility = Visibility.Visible;
        InstallProgressBar.Value = 0;
        InstallStatusText.Text = "准备下载…";

        var progress = new Progress<InstallProgress>(p =>
        {
            InstallProgressBar.Value = p.Percent;
            var received = p.TotalBytes > 0
                ? $" · {p.BytesReceived / 1048576.0:0.0}/{p.TotalBytes / 1048576.0:0.0} MB"
                : "";
            InstallStatusText.Text = $"{p.Message} · {p.Percent:0}%{received}";
        });

        try
        {
            var dir = await RobloxDeployment.InstallAsync(_deployment, progress, _cts.Token);
            InstallProgressBar.Value = 100;
            InstallStatusText.Text = "安装完成：" + dir;
            MainWindowHost?.ShowToast("Roblox 安装完成");
            RefreshVersions();
            UpdateInstallState();
        }
        catch (OperationCanceledException)
        {
            InstallStatusText.Text = "已取消安装";
            Log.Info("用户取消了 Roblox 安装");
        }
        catch (Exception ex)
        {
            InstallStatusText.Text = "安装失败：" + ex.Message;
            Log.Error("安装 Roblox 失败", ex);
            DialogService.Error("安装失败：\n" + ex.Message, "安装失败");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InstallButton.IsEnabled = !busy && _deployment is not null;
        RepairButton.IsEnabled = !busy && _deployment is not null &&
                                 RobloxDeployment.IsInstalled(_deployment.ClientVersionUpload);
        CancelInstallButton.IsEnabled = busy;
        ChannelCombo.IsEnabled = !busy;
        if (!busy)
            UpdateInstallState();
    }

    private void CancelInstall_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        InstallStatusText.Text = "正在取消…";
    }

    private async void TestNetwork_Click(object sender, RoutedEventArgs e)
    {
        NetworkStatus.Text = "测试中…";
        bool ok = await RobloxApi.CheckConnectivityAsync();
        NetworkStatus.Text = ok ? "clientsettingscdn.roblox.com 可访问" : "连接失败，请检查网络或代理";
        NetworkStatus.Foreground = (System.Windows.Media.Brush)FindResource(ok ? "BrushSuccess" : "BrushDanger");
    }

    private void RefreshVersions_Click(object sender, RoutedEventArgs e) => RefreshVersions();

    private void RefreshVersions()
    {
        var versions = RobloxInstall.ScanPlayerVersions();
        for (int i = 0; i < versions.Count; i++)
            versions[i].IsLatest = i == 0;

        InstalledVersionsList.ItemsSource = versions;
        VersionCountText.Text = versions.Count == 0 ? "没有检测到版本" : $"共 {versions.Count} 个版本";
        VersionsEmpty.Visibility = versions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenVersionsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.RobloxVersions);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.RobloxVersions}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DialogService.Error("无法打开目录：\n" + ex.Message);
        }
    }

    private void OpenVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RobloxVersion version })
            return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{version.Directory}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            DialogService.Error("无法打开目录：\n" + ex.Message);
        }
    }

    private void BindVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RobloxVersion version })
            return;

        var target = TargetInstanceCombo.SelectedItem as RclInstance;
        if (target is null)
        {
            DialogService.Info("先选择一个要绑定的实例。", "没有实例");
            return;
        }

        target.VersionPath = version.Directory;
        InstanceStore.Save(target);
        MainWindowHost?.ShowToast($"已把「{target.Name}」绑定到 {version.DisplayName}");
        Log.Info($"实例 {target.Name} 绑定版本 {version.Directory}");
    }

    // ---------- WEAO executor versions ----------

    private async Task LoadExecutorsAsync(bool force)
    {
        if (_executorBusy)
            return;
        if (_weaoLoaded && !force && _weaoSnapshot is not null)
        {
            RefreshExecutorGroups();
            return;
        }

        WeaoStatusText.Text = "正在连接 WEAO…";
        WeaoStatusText.Foreground = (System.Windows.Media.Brush)FindResource("BrushTextMuted");
        try
        {
            _weaoSnapshot = await WeaoService.GetSnapshotAsync(force);
            RefreshExecutorGroups();
            WeaoStatusText.Text = _weaoSnapshot.Error ??
                                  (_weaoSnapshot.IsEmpty
                                      ? "WEAO 没有返回执行器数据。"
                                      : "已从 WEAO 获取执行器数据。");
            WeaoStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
                _weaoSnapshot.IsEmpty ? "BrushDanger" : "BrushTextMuted");
            WeaoUpdatedText.Text =
                $"更新于 {_weaoSnapshot.FetchedAt:HH:mm:ss}" +
                (string.IsNullOrWhiteSpace(_weaoSnapshot.CurrentWindowsVersion)
                    ? ""
                    : $" · 当前 Windows 版本 {WeaoExecutor.ShortenVersion(_weaoSnapshot.CurrentWindowsVersion)}");
            _weaoLoaded = !_weaoSnapshot.IsEmpty;
        }
        catch (Exception ex)
        {
            WeaoStatusText.Text = "WEAO 查询失败：" + ex.Message;
            WeaoStatusText.Foreground = (System.Windows.Media.Brush)FindResource("BrushDanger");
            Log.Warn("查询 WEAO 失败：" + ex.Message);
        }
    }

    private void RefreshExecutorGroups()
    {
        if (_weaoSnapshot is null)
            return;
        var groups = ExecutorVersionService.BuildGroups(_weaoSnapshot);
        ExecutorVersionsList.ItemsSource = groups;
        ExecutorList.ItemsSource = _weaoSnapshot.WindowsExecutors;
        ExecutorVersionCount.Text = $"{groups.Count} 个版本";
        ExecutorCountText.Text = $"{_weaoSnapshot.WindowsExecutors.Count} 个 Windows 执行器";
        ExecutorsEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshExecutors_Click(object sender, RoutedEventArgs e)
    {
        await LoadExecutorsAsync(true);
        MainWindowHost?.ShowToast("已刷新 WEAO 数据");
    }

    private async void DownloadExecutorVersion_Click(object sender, RoutedEventArgs e)
    {
        if (_executorBusy || sender is not Button { Tag: ExecutorVersionGroup group })
            return;

        var instance = ExecutorTargetCombo.SelectedItem as RclInstance
                       ?? TargetInstanceCombo.SelectedItem as RclInstance;
        if (instance is null)
        {
            DialogService.Info("先选择一个要绑定的实例。", "没有实例");
            return;
        }

        if (group.IsInstalled && group.InstallDirectory is { } installed)
        {
            BindExecutorVersion(instance, group.Version, installed);
            return;
        }

        if (!DialogService.Confirm(
                $"将下载 Roblox {group.ShortVersion}\n" +
                $"执行器支持：{group.ExecutorsDisplay}\n\n" +
                "安装约需要 230 MB 下载空间，完成后会把目标实例锁定到这个版本。\n\n继续吗？",
                "下载执行器支持版本", "开始下载", "取消"))
            return;

        _executorBusy = true;
        _executorCts = new CancellationTokenSource();
        ExecutorProgressPanel.Visibility = Visibility.Visible;
        ExecutorProgressBar.Value = 0;
        ExecutorStatusText.Text = "准备下载…";

        var progress = new Progress<InstallProgress>(p =>
        {
            ExecutorProgressBar.Value = p.Percent;
            ExecutorStatusText.Text =
                $"{p.Message} · {p.Percent:0}%" +
                (p.TotalBytes > 0
                    ? $" · {p.BytesReceived / 1048576.0:0.0}/{p.TotalBytes / 1048576.0:0.0} MB"
                    : "");
        });

        try
        {
            var directory = await ExecutorVersionService.EnsureInstalledAsync(
                group.Version, progress, _executorCts.Token);
            BindExecutorVersion(instance, group.Version, directory);
            RefreshExecutorGroups();
        }
        catch (OperationCanceledException)
        {
            ExecutorStatusText.Text = "已取消下载";
        }
        catch (Exception ex)
        {
            ExecutorStatusText.Text = "下载失败：" + ex.Message;
            Log.Error("下载执行器版本失败", ex);
            DialogService.Error("下载执行器版本失败：\n" + ex.Message, "下载失败");
        }
        finally
        {
            _executorCts?.Dispose();
            _executorCts = null;
            _executorBusy = false;
        }
    }

    private void BindExecutorVersion(RclInstance instance, string version, string directory)
    {
        instance.VersionPath = directory;
        InstanceStore.Save(instance);
        instance.NotifyAll();
        ExecutorStatusText.Text = $"已把「{instance.Name}」锁定到 {WeaoExecutor.ShortenVersion(version)}";
        MainWindowHost?.ShowToast($"已锁定执行器版本：{WeaoExecutor.ShortenVersion(version)}");
        Log.Info($"实例 {instance.Name} 绑定执行器支持版本 {version}（{directory}）");
    }

    private void CancelExecutor_Click(object sender, RoutedEventArgs e)
    {
        _executorCts?.Cancel();
        ExecutorStatusText.Text = "正在取消…";
    }

    private void OpenWeao_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://weao.xyz/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DialogService.Error("无法打开 WEAO：" + ex.Message);
        }
    }

    private MainWindow? MainWindowHost => Window.GetWindow(this) as MainWindow;
}
