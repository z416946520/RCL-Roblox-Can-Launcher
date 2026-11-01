using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RCL.Core;
using RCL.Dialogs;
using RCL.Models;

namespace RCL.Pages;

public partial class LaunchPage : UserControl
{
    private RclInstance? _current;
    private bool _busy;
    private bool _logStarted;
    private readonly List<string> _logLines = new();

    public LaunchPage()
    {
        InitializeComponent();
        SessionsList.ItemsSource = SessionManager.Sessions;
        InstanceStore.Changed += OnInstancesChanged;
        SessionManager.Changed += OnSessionsChanged;
        AccountService.Changed += OnAccountsChanged;
        Loaded += (_, _) =>
        {
            RefreshInstances();
            RefreshSessions();
            RefreshAccountCard();
            _ = EnsureActiveAvatarAsync();
            if (!_logStarted)
            {
                _logStarted = true;
                AppendLog("RCL 已就绪，选择实例后点击「启动 Roblox」。");
            }
        };
    }

    public RclInstance? Current => _current;

    public void OnActivated()
    {
        RefreshInstances();
        RefreshSessions();
        RefreshAccountCard();
        _ = EnsureActiveAvatarAsync();
    }

    /// <summary>
    /// Fills the homepage avatar when the cache is empty or old. Runs in the background and relies
    /// on AccountService.Changed to repaint, so a slow thumbnail never delays the page.
    /// </summary>
    private async Task EnsureActiveAvatarAsync()
    {
        if (AccountService.ActiveAccount is not { } account)
            return;
        try
        {
            await AccountService.RefreshAvatarAsync(account);
        }
        catch (Exception ex)
        {
            Log.Warn("主页头像加载失败：" + ex.Message);
        }
    }

    private bool _accountRefreshing;

    private void OnAccountsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshAccountCard);
            return;
        }
        RefreshAccountCard();
    }

    private sealed record AccountOption(RobloxAccount Account, string Label)
    {
        // The PclComboBox template binds ContentTemplate on the selection box, which wins over
        // DisplayMemberPath, so the label has to come from ToString().
        public override string ToString() => Label;
    }

    private static string MaskedLabel(RobloxAccount account)
    {
        var id = account.UserId.ToString();
        var tail = id.Length >= 4 ? id[^4..] : id;
        return tail.Length == 0 ? "\u2022\u2022\u2022" : "\u2022\u2022\u2022" + tail;
    }

    private void RefreshAccountCard()
    {
        if (AccountCombo is null)
            return;
        _accountRefreshing = true;

        var anonymous = AppSettings.Current.AnonymousAccount;
        var options = AccountService.Accounts
            .Select(account => new AccountOption(account, anonymous ? MaskedLabel(account) : account.Display))
            .ToList();
        var active = AccountService.ActiveAccount;

        AccountCombo.ItemsSource = options;
        AccountCombo.SelectedItem = options.FirstOrDefault(option => ReferenceEquals(option.Account, active));
        AccountCombo.Visibility = options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PrivacyButton.Visibility = options.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AccountAddButton.Visibility = options.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshPrivacyButton(anonymous);

        if (active is not null)
        {
            AccountName.Text = anonymous ? Loc.T("账号已隐藏") : active.Display;
            AccountSub.Text = anonymous
                ? "\u2022\u2022\u2022\u2022\u2022\u2022"
                : active.Subtitle + " \u00b7 " + Loc.T("启动实例时自动使用");
            AccountInitial.Text = anonymous ? "?" : active.Initial;
            if (active.HasAvatar && !anonymous)
            {
                try
                {
                    AccountAvatar.Background = AvatarBrush.Load(active.AvatarCachePath)
                                              ?? (Brush)FindResource("BrushAccentSoft");
                    AccountInitial.Visibility = Visibility.Collapsed;
                }
                catch
                {
                    AccountInitial.Visibility = Visibility.Visible;
                    AccountAvatar.Background = (Brush)FindResource("BrushAccentSoft");
                }
            }
            else
            {
                AccountInitial.Visibility = Visibility.Visible;
                AccountAvatar.Background = (Brush)FindResource("BrushAccentSoft");
            }
        }
        else
        {
            AccountName.Text = AccountService.Accounts.Count > 0 ? "未选择账号" : "未登录 Roblox";
            AccountSub.Text = AccountService.Accounts.Count > 0
                ? "选择一个已保存账号后再启动"
                : "登录后所有实例都会使用这个账号";
            AccountInitial.Text = "R";
            AccountInitial.Visibility = Visibility.Visible;
            AccountAvatar.Background = (Brush)FindResource("BrushAccentSoft");
        }
        _accountRefreshing = false;
    }

    private void RefreshPrivacyButton(bool anonymous)
    {
        PrivacyIcon.Foreground = (Brush)FindResource(anonymous ? "BrushAccent" : "BrushTextSecondary");
        PrivacyButton.ToolTip = Loc.T(anonymous ? "显示账号信息" : "匿名显示账号");
    }

    private void ToggleAnonymous_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.AnonymousAccount = !AppSettings.Current.AnonymousAccount;
        AppSettings.Save();
        RefreshAccountCard();
        MainWindowHost?.ShowToast(AppSettings.Current.AnonymousAccount
            ? Loc.T("已开启账号匿名显示")
            : Loc.T("已显示账号信息"));
    }

    private void AccountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_accountRefreshing || AccountCombo.SelectedItem is not AccountOption option)
            return;
        var account = option.Account;
        if (ReferenceEquals(account, AccountService.ActiveAccount))
            return;
        try
        {
            AccountService.Switch(account);
            MainWindowHost?.ShowToast($"已切换账号：{option.Label}（新启动的客户端生效）");
        }
        catch (Exception ex)
        {
            DialogService.Error("切换账号失败：\n" + ex.Message, "切换账号");
            RefreshAccountCard();
        }
    }

    private void ManageAccounts_Click(object sender, RoutedEventArgs e) => MainWindowHost?.OpenAccounts();

    public void SelectInstance(RclInstance instance)
    {
        _current = instance;
        AppSettings.Current.LastInstanceId = instance.Id.ToString("N");
        UpdateDetail();
    }

    private void OnInstancesChanged()
    {
        if (!IsLoaded)
            return;
        RefreshInstances();
    }

    private void OnSessionsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnSessionsChanged);
            return;
        }
        RefreshSessions();
        UpdateDetail();
    }

    private void RefreshInstances()
    {
        if (!IsLoaded && !IsInitialized)
            return;

        var preferredId = AppSettings.Current.LastInstanceId;
        if (string.IsNullOrWhiteSpace(preferredId) && _current is not null)
            preferredId = _current.Id.ToString("N");
        var selected = InstanceStore.All.FirstOrDefault(i => i.Id.ToString("N") == preferredId)
                       ?? InstanceStore.All.FirstOrDefault();
        _current = selected;
        if (selected is not null)
            AppSettings.Current.LastInstanceId = selected.Id.ToString("N");

        UpdateDetail();
    }

    private void UpdateDetail()
    {
        bool hasInstance = _current is not null;
        EmptyPanel.Visibility = hasInstance ? Visibility.Collapsed : Visibility.Visible;
        RightScroll.Visibility = hasInstance ? Visibility.Visible : Visibility.Collapsed;
        LaunchButton.IsEnabled = hasInstance && !_busy;

        if (_current is not { } inst)
        {
            CurrentInstanceText.Text = "还没有实例";
            VersionInfoText.Text = "在「实例」页新建一个实例";
            ModeTag.Visibility = Visibility.Collapsed;
            MultiCheck.IsChecked = false;
            MultiCheck.IsEnabled = false;
            if (!_busy)
            {
                StatusText.Text = "未选择实例";
                StatusText.Foreground = (Brush)FindResource("BrushTextMuted");
            }
            return;
        }

        MultiCheck.IsEnabled = true;
        ModeTag.Visibility = Visibility.Visible;
        CurrentInstanceText.Text = inst.Name;

        var version = RobloxInstall.Resolve(inst.VersionPath);
        if (version is null)
        {
            VersionInfoText.Text = string.IsNullOrWhiteSpace(inst.VersionPath)
                ? "未检测到 Roblox 客户端"
                : "绑定的版本目录已丢失";
        }
        else
        {
            var source = string.IsNullOrWhiteSpace(inst.VersionPath) ? "自动选择" : "已锁定";
            VersionInfoText.Text = $"Roblox {version.DisplayName} · {source}";
        }

        ModeTagText.Text = inst.MultiInstance ? "多开" : "单实例";
        ModeTag.Background = (Brush)FindResource(inst.MultiInstance ? "BrushSuccessSoft" : "BrushAccentSoft");
        ModeTagText.Foreground = (Brush)FindResource(inst.MultiInstance ? "BrushSuccess" : "BrushAccentDark");

        MissingRobloxCard.Visibility = RobloxInstall.IsInstalled ? Visibility.Collapsed : Visibility.Visible;

        MultiCheck.IsChecked = inst.MultiInstance;
        RejoinButton.IsEnabled = inst.HasLastGame;
        LastGameButton.IsEnabled = inst.HasLastGame;
        LastGameName.Text = inst.LastGameDisplay;
        LastGameMeta.Text = inst.HasLastGame
            ? $"Place {inst.LastGame!.PlaceId} · {inst.LastGame.JoinedAt:yyyy-MM-dd HH:mm}"
            : "启动一次游戏后，这里会出现快速重连入口。";

        if (!_busy)
        {
            StatusText.Text = "就绪";
            StatusText.Foreground = (Brush)FindResource("BrushTextMuted");
        }

        RefreshSessions();
    }

    private void RefreshSessions()
    {
        var count = SessionManager.Sessions.Count;
        SessionCountText.Text = count.ToString();
        SessionsCard.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppendLog(string message)
    {
        if (LaunchLogText is null)
            return;
        _logLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        if (_logLines.Count > 200)
            _logLines.RemoveRange(0, _logLines.Count - 200);
        LaunchLogText.Text = string.Join(Environment.NewLine, _logLines);
        LogScroll?.ScrollToEnd();
    }

    private void MultiCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
            return;
        _current.MultiInstance = MultiCheck.IsChecked == true;
        InstanceStore.Save(_current);
        UpdateDetail();
        MainWindowHost?.ShowToast(_current.MultiInstance ? "已开启多开" : "已关闭多开");
        AppendLog(_current.MultiInstance ? "已开启多开" : "已关闭多开");
    }

    private async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
            return;

        bool multi = _current.MultiInstance;
        if (!multi && RobloxInstall.RunningCount() > 0 && AppSettings.Current.ConfirmLaunch)
        {
            if (!DialogService.Confirm(
                    "检测到 Roblox 已经在运行。\n继续启动会再打开一个客户端；如果不需要多开，请先关闭正在运行的游戏。\n\n仍然启动吗？",
                    "Roblox 正在运行"))
                return;
        }

        await LaunchTargetAsync(null, null, null);
    }

    private async Task LaunchTargetAsync(long? placeId, string? jobId, string? title)
    {
        if (_current is not { } inst)
            return;

        bool multi = inst.MultiInstance;
        var progressWindow = new LaunchProgressWindow(RobloxInstall.Resolve(inst.VersionPath));
        try { progressWindow.Owner = Window.GetWindow(this); } catch { }
        progressWindow.Show();
        SetBusy(true);
        try
        {
            var result = await LaunchService.LaunchAsync(inst, placeId, jobId, title, multi, status =>
            {
                StatusText.Text = status;
                StatusText.Foreground = (Brush)FindResource("BrushAccentDark");
                AppendLog(status);
                progressWindow.Report(status);
            }, progressWindow.Token);

            var detail = result.SafeFallback
                ? "安全启动"
                : result.UsedSlot ? "多开槽位" : "原始目录";
            var extras = new List<string>();
            if (result.ModsApplied > 0)
                extras.Add($"{result.ModsApplied} 个模组文件");
            if (result.FlagsApplied > 0)
                extras.Add($"{result.FlagsApplied} 条 Flags");
            var suffix = extras.Count > 0 ? "，" + string.Join("，", extras) : "";

            StatusText.Text = $"已启动 · PID {result.Session.Pid}";
            MainWindowHost?.ShowToast($"{inst.Name} 已启动（{detail}{suffix}）");
            Log.Info($"启动完成：{inst.Name}，PID {result.Session.Pid}，{detail}{suffix}");
            AppendLog($"{inst.Name} 已启动（PID {result.Session.Pid}，{detail}{suffix}）");

            if (AppSettings.Current.CloseAfterLaunch)
                Application.Current.MainWindow?.Close();
        }
        catch (OperationCanceledException)
        {
            Log.Info("用户取消了启动：" + inst.Name);
            StatusText.Text = "已取消";
            StatusText.Foreground = (Brush)FindResource("BrushTextMuted");
            AppendLog("启动已取消");
        }
        catch (Exception ex)
        {
            Log.Error("启动失败", ex);
            StatusText.Text = "启动失败";
            StatusText.Foreground = (Brush)FindResource("BrushDanger");
            AppendLog("启动失败：" + ex.Message);
            DialogService.Error(ex is LaunchException ? ex.Message : "启动失败：\n" + ex.Message, "启动失败");
        }
        finally
        {
            progressWindow.CloseSafely();
            SetBusy(false);
            UpdateDetail();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        LaunchButton.IsEnabled = !busy && _current is not null;
        JoinButton.IsEnabled = !busy;
        RejoinButton.IsEnabled = !busy && _current?.HasLastGame == true;
        if (busy)
        {
            StatusText.Text = "准备启动…";
            StatusText.Foreground = (Brush)FindResource("BrushAccentDark");
        }
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
            return;

        var input = DialogService.PromptTwo(
            "加入游戏",
            "游戏链接或 Place ID",
            "",
            "例如 https://www.roblox.com/games/1818 或 1818",
            "服务器 Job ID（可选）",
            "",
            "留空则由 Roblox 自动匹配服务器");
        if (input is null)
            return;

        var placeId = RobloxApi.ParsePlaceId(input.Value.First);
        if (placeId <= 0)
        {
            DialogService.Error("没有从输入中解析出 Place ID，请检查链接或数字。", "无法加入");
            return;
        }

        SetBusy(true);
        StatusText.Text = "正在查询游戏信息…";
        AppendLog($"正在查询游戏信息（Place {placeId}）…");
        string title = "";
        try
        {
            var info = await RobloxApi.GetGameInfoAsync(placeId);
            title = info.Name;
            StatusText.Text = $"准备加入：{info.Name}";
            AppendLog($"准备加入：{info.Name}");
        }
        catch (Exception ex)
        {
            Log.Warn("查询游戏信息失败：" + ex.Message);
            StatusText.Text = "游戏信息查询失败，将尝试直接加入";
            AppendLog("游戏信息查询失败，将尝试直接加入");
        }
        finally
        {
            SetBusy(false);
        }

        await LaunchTargetAsync(placeId, string.IsNullOrWhiteSpace(input.Value.Second) ? null : input.Value.Second, title);
    }

    private async void Rejoin_Click(object sender, RoutedEventArgs e)
    {
        if (_current?.LastGame is not { } game)
        {
            DialogService.Info("这个实例还没有加入过游戏。先用「加入游戏」启动一次吧。", "没有可重连的游戏");
            return;
        }
        await LaunchTargetAsync(game.PlaceId, game.JobId, game.Title);
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
        AppendLog("已创建实例：" + inst.Name);
    }

    private void SelectInstance_Click(object sender, RoutedEventArgs e) => MainWindowHost?.NavigateTo(1);

    private void EditInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_current is { } inst)
            MainWindowHost?.OpenInstanceEditor(inst);
    }

    private void GoDownload_Click(object sender, RoutedEventArgs e) => MainWindowHost?.NavigateTo(2);

    private void KillSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RunningSession session })
        {
            if (DialogService.Confirm($"结束「{session.Title}」（PID {session.Pid}）？\n未保存的游戏进度会丢失。", "结束进程"))
                SessionManager.Kill(session);
        }
    }

    private void KillAll_Click(object sender, RoutedEventArgs e)
    {
        if (SessionManager.Sessions.Count == 0)
            return;
        if (!DialogService.Confirm($"结束全部 {SessionManager.Sessions.Count} 个由 RCL 启动的 Roblox 进程？", "结束全部"))
            return;
        SessionManager.KillAll();
    }

    private MainWindow? MainWindowHost => Window.GetWindow(this) as MainWindow;
}
