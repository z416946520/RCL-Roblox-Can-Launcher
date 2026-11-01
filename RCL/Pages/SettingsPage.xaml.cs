using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RCL.Core;
using RCL.Dialogs;
using RCL.Models;

namespace RCL.Pages;

public partial class SettingsPage : UserControl
{
    private static readonly double[] OpacityValues = { 0.10, 0.22, 0.35, 0.50 };
    private bool _loading = true;
    private bool _accountsBusy;
    private bool _avatarsLoading;

    public SettingsPage()
    {
        InitializeComponent();
        OpacityCombo.ItemsSource = new[] { "10%", "22%", "35%", "50%" };
        AccountService.Changed += OnAccountsChanged;
        DiscordRpcService.StateChanged += OnDiscordStateChanged;
        Loaded += (_, _) =>
        {
            BuildAccentPalette();
            LoadIntoControls();
            _loading = false;
            RefreshAccountsUi();
        };
    }

    public void OnActivated()
    {
        LoadIntoControls();
        RefreshMultiStatus();
        RefreshAccountsUi();
        RefreshHandlerUi();
        RefreshDiscordUi();
        RefreshUpdateUi();
    }

    public void ShowAccounts()
    {
        if (NavList is null)
            return;
        NavList.SelectedIndex = 1;
        RefreshAccountsUi();
        _ = LoadAvatarsAsync();
    }

    private void LoadIntoControls()
    {
        _loading = true;
        var s = AppSettings.Current;
        ConfirmCheck.IsChecked = s.ConfirmLaunch;
        CloseAfterCheck.IsChecked = s.CloseAfterLaunch;
        PlaytimeCheck.IsChecked = s.TrackPlaytime;
        DefaultMultiCheck.IsChecked = s.DefaultMultiInstance;

        DefaultInstanceCombo.ItemsSource = null;
        DefaultInstanceCombo.ItemsSource = InstanceStore.All;
        DefaultInstanceCombo.SelectedItem = InstanceStore.Find(
            Guid.TryParseExact(s.LastInstanceId, "N", out var id) ? id : Guid.Empty)
            ?? InstanceStore.All.FirstOrDefault();

        AccentHexBox.Text = s.AccentHex;
        AccentStatus.Text = "当前主题色：" + s.AccentHex;
        BackgroundPathText.Text = string.IsNullOrWhiteSpace(s.BackgroundImage)
            ? "未选择背景图"
            : s.BackgroundImage;
        OpacityCombo.SelectedIndex = NearestOpacityIndex(s.BackgroundOpacity);

        MaxSlotsBox.Text = s.MaxSlots.ToString();
        DataPathText.Text = AppPaths.Base;
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        AboutVersion.Text = "RCL " + version + " · PCL 风格 Roblox 启动器";

        DiscordEnabledCheck.IsChecked = s.DiscordRpcEnabled;
        DiscordClientIdBox.Text = s.DiscordClientId;
        UpdateCurrentText.Text = "当前版本：RCL " + UpdateService.CurrentVersion;
        UpdateUrlBox.Text = s.UpdateManifestUrl;
        UpdateStartupCheck.IsChecked = s.CheckUpdatesOnStartup;

        RefreshMultiStatus();
        RefreshAccountsUi();
        RefreshHandlerUi();
        _loading = false;
    }

    private static int NearestOpacityIndex(double value)
    {
        int best = 1;
        double distance = double.MaxValue;
        for (int i = 0; i < OpacityValues.Length; i++)
        {
            var d = Math.Abs(OpacityValues[i] - value);
            if (d < distance)
            {
                distance = d;
                best = i;
            }
        }
        return best;
    }

    private void BuildAccentPalette()
    {
        if (AccentPalette.Children.Count > 0)
            return;

        foreach (var hex in ThemeManager.Palette)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Margin = new Thickness(0, 0, 9, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!),
                ToolTip = hex,
                Tag = hex
            };
            swatch.MouseLeftButtonUp += (_, _) => ApplyAccent(hex);
            AccentPalette.Children.Add(swatch);
        }
    }

    private void ApplyAccent_Click(object sender, RoutedEventArgs e)
    {
        var hex = AccentHexBox.Text.Trim();
        if (!ThemeManager.TryParse(hex, out var color))
        {
            DialogService.Error("颜色格式不正确，请使用 #RRGGBB 或 #AARRGGBB。", "无效颜色");
            return;
        }
        ApplyAccent(ThemeManager.ToHex(color));
    }

    private void ApplyAccent(string hex)
    {
        AppSettings.Current.AccentHex = hex;
        AppSettings.Save();
        ThemeManager.Apply(hex);
        MainWindowHost?.ApplyAppearance();
        AccentHexBox.Text = hex;
        AccentStatus.Text = "当前主题色：" + hex;
        MainWindowHost?.ShowToast("主题色已更新");
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var s = AppSettings.Current;
        s.ConfirmLaunch = ConfirmCheck.IsChecked == true;
        s.CloseAfterLaunch = CloseAfterCheck.IsChecked == true;
        s.TrackPlaytime = PlaytimeCheck.IsChecked == true;
        s.DefaultMultiInstance = DefaultMultiCheck.IsChecked == true;
        AppSettings.Save();
    }

    private void DefaultInstance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DefaultInstanceCombo.SelectedItem is not Models.RclInstance inst)
            return;
        AppSettings.Current.LastInstanceId = inst.Id.ToString("N");
        AppSettings.Save();
    }

    private void PickBackground_Click(object sender, RoutedEventArgs e)
    {
        var path = DialogService.PickFile("图片|*.png;*.jpg;*.jpeg;*.bmp;*.webp|所有文件|*.*", "选择背景图");
        if (path is null)
            return;
        AppSettings.Current.BackgroundImage = path;
        AppSettings.Save();
        BackgroundPathText.Text = path;
        MainWindowHost?.ApplyAppearance();
        MainWindowHost?.ShowToast("背景图已应用");
    }

    private void ClearBackground_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.BackgroundImage = "";
        AppSettings.Save();
        BackgroundPathText.Text = "未选择背景图";
        MainWindowHost?.ApplyAppearance();
        MainWindowHost?.ShowToast("已清除背景图");
    }

    private void Opacity_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || OpacityCombo.SelectedIndex < 0)
            return;
        AppSettings.Current.BackgroundOpacity = OpacityValues[OpacityCombo.SelectedIndex];
        AppSettings.Save();
        MainWindowHost?.ApplyAppearance();
    }

    private void SaveSlots_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MaxSlotsBox.Text.Trim(), out var slots) || slots < 1 || slots > 16)
        {
            DialogService.Error("槽位数量需要在 1 到 16 之间。", "无效数量");
            return;
        }
        AppSettings.Current.MaxSlots = slots;
        AppSettings.Save();
        RefreshMultiStatus();
        MainWindowHost?.ShowToast("多开槽位上限已设为 " + slots);
    }

    private void RefreshMultiStatus()
    {
        MultiStatusText.Text = MultiInstanceLock.IsHeld
            ? "单例锁已持有。多开启动时会从空闲槽位分配独立工作目录。"
            : "单例锁尚未持有；第一次以多开方式启动时会自动获取并一直保持到 RCL 退出。";
        SessionText.Text = SessionManager.Sessions.Count == 0
            ? "当前没有由 RCL 启动的会话。"
            : $"当前有 {SessionManager.Sessions.Count} 个由 RCL 启动的会话正在运行。";
    }

    private void CleanupSlots_Click(object sender, RoutedEventArgs e)
    {
        MultiInstanceLock.CleanupUnused();
        RefreshMultiStatus();
        MainWindowHost?.ShowToast("已清理空闲多开槽位");
    }

    private void OpenSlots_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.MultiInstance);
        OpenPath(AppPaths.MultiInstance);
    }

    private void KillAll_Click(object sender, RoutedEventArgs e)
    {
        if (SessionManager.Sessions.Count == 0)
        {
            DialogService.Info("当前没有 RCL 会话。", "没有会话");
            return;
        }
        if (!DialogService.Confirm($"结束全部 {SessionManager.Sessions.Count} 个会话？未保存的进度会丢失。", "结束全部"))
            return;
        SessionManager.KillAll();
        RefreshMultiStatus();
        MainWindowHost?.ShowToast("已结束全部 RCL 会话");
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Base);
        OpenPath(AppPaths.Base);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e) => Log.Open();

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        var path = DialogService.PickSaveFile("ZIP 压缩包|*.zip",
            $"RCL-backup-{DateTime.Now:yyyyMMdd-HHmm}.zip", "备份 RCL 配置");
        if (path is null)
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);

            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            int count = 0;
            foreach (var file in Directory.GetFiles(AppPaths.Base, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(AppPaths.Base, file);
                if (relative.StartsWith("logs" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("temp" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("multi-instance" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                zip.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
                count++;
            }

            var size = new FileInfo(path).Length / 1024.0 / 1024.0;
            MainWindowHost?.ShowToast($"配置已备份（{count} 个文件，{size:0.0} MB）");
        }
        catch (Exception ex)
        {
            Log.Error("备份配置失败", ex);
            DialogService.Error("备份失败：\n" + ex.Message, "备份失败");
        }
    }

    private void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogService.Confirm(
                "恢复默认的启动设置、主题色与背景图？\n\n实例、模组与 FastFlags 不会被删除。",
                "重置设置", "重置", "取消"))
            return;

        AppSettings.ResetToDefaults();
        ThemeManager.Apply(AppSettings.Current.AccentHex);
        MainWindowHost?.ApplyAppearance();
        LoadIntoControls();
        MainWindowHost?.ShowToast("设置已重置");
    }

    private void OpenPcl_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/PCL-Community/PCL-CE");

    private void OpenVoidstrap_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/KloBraticc/Voidstrap");

    private void OpenFroststrap_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://github.com/Froststrap/Froststrap");

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DialogService.Error("无法打开链接：\n" + ex.Message);
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

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PanelLaunch is null || NavList.SelectedIndex < 0)
            return;
        int index = NavList.SelectedIndex;
        PanelLaunch.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelAccounts.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelAppearance.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelIntegrations.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PanelMulti.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
        if (index == 1)
        {
            RefreshAccountsUi();
            _ = LoadAvatarsAsync();
        }
        if (index == 3)
            RefreshDiscordUi();
        if (index == 4)
            RefreshMultiStatus();
        if (index == 5)
            RefreshUpdateUi();
    }

    // ---------- Roblox accounts ----------

    private void OnAccountsChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshAccountsUi);
            return;
        }
        RefreshAccountsUi();
    }

    private void RefreshAccountsUi()
    {
        if (AccountsList is null || CurrentAccountName is null)
            return;

        var active = AccountService.ActiveAccount;
        CurrentAccountName.Text = active?.Display ?? "未登录 Roblox";
        CurrentAccountSub.Text = active?.Subtitle ?? "添加账号后，启动的实例会使用它";
        CurrentAccountId.Text = active is null ? "" : $"User ID {active.UserId}";
        SetAvatar(CurrentAccountAvatar, CurrentAccountInitial, active);

        AccountsList.ItemsSource = AccountService.Accounts;
        AccountCountText.Text = $"{AccountService.Accounts.Count} 个";
        AccountsEmpty.Visibility = AccountService.Accounts.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RestoreCookieButton.IsEnabled = !_accountsBusy && RobloxCookieStore.HasOriginalBackup;
        AddAccountWebButton.IsEnabled = !_accountsBusy && WebView2Available();
        AddAccountWebButton.ToolTip = WebView2Available()
            ? "使用 Roblox 官方登录页登录"
            : "未检测到 WebView2 运行时，请改用「手动 Cookie 添加」";
        AddAccountCookieButton.IsEnabled = !_accountsBusy;
        RefreshAvatarsButton.IsEnabled = !_accountsBusy && AccountService.Accounts.Count > 0;
    }

    private static void SetAvatar(Border avatar, TextBlock initial, RobloxAccount? account)
    {
        if (account is not null && account.HasAvatar)
        {
            var brush = AvatarBrush.Load(account.AvatarCachePath);
            if (brush is not null)
            {
                avatar.Background = brush;
                initial.Visibility = Visibility.Collapsed;
                initial.Text = account.Initial;
                return;
            }
        }

        avatar.Background = new SolidColorBrush(Color.FromRgb(0xEA, 0xF2, 0xFE));
        initial.Text = account?.Initial ?? "R";
        initial.Visibility = Visibility.Visible;
    }

    private static bool WebView2Available()
    {
        try
        {
            _ = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task LoadAvatarsAsync()
    {
        if (_avatarsLoading || AccountService.Accounts.Count == 0)
            return;
        _avatarsLoading = true;
        try
        {
            await AccountService.EnsureAvatarsAsync();
            if (Dispatcher.CheckAccess())
                RefreshAccountsUi();
            else
                _ = Dispatcher.BeginInvoke(RefreshAccountsUi);
        }
        catch (Exception ex)
        {
            Log.Warn("加载账号头像失败：" + ex.Message);
        }
        finally
        {
            _avatarsLoading = false;
        }
    }

    private async void AddAccountWeb_Click(object sender, RoutedEventArgs e)
    {
        if (_accountsBusy)
            return;
        Log.Info("点击网页登录添加账号");
        AccountStatus.Text = "正在打开 Roblox 登录页面…";
        try
        {
            var window = new AccountLoginWindow { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() != true || string.IsNullOrWhiteSpace(window.Cookie))
            {
                AccountStatus.Text = "没有获取到登录 Cookie。可以重试，或改用「手动 Cookie 添加」。";
                return;
            }

            SetAccountsBusy(true);
            AccountStatus.Text = "正在验证 Cookie 并保存账号…";
            var (account, error) = await AccountService.AddFromCookieAsync(
                window.Cookie, window.User, allowUnverified: true);
            if (account is null)
            {
                AccountStatus.Text = "添加失败：" + error;
                DialogService.Error(error ?? "无法添加账号。", "添加账号失败");
            }
            else if (error is not null)
            {
                AccountStatus.Text = $"账号「{account.Display}」已保存，但切换失败：{error}";
                DialogService.Error(error, "账号已保存，但切换失败");
            }
            else
            {
                AccountStatus.Text = account.IsVerified
                    ? $"已添加并切换到 {account.Display}。"
                    : "已添加并切换账号（账号信息暂时无法验证，但登录 Cookie 已保存）。";
                MainWindowHost?.ShowToast("已添加 Roblox 账号：" + account.Display);
            }
        }
        catch (Exception ex)
        {
            Log.Error("网页登录添加账号失败", ex);
            AccountStatus.Text = "添加失败：" + ex.Message;
            DialogService.Error("添加账号失败：\n" + ex.Message);
        }
        finally
        {
            SetAccountsBusy(false);
            RefreshAccountsUi();
        }
    }

    private async void AddAccountCookie_Click(object sender, RoutedEventArgs e)
    {
        if (_accountsBusy)
            return;
        if (!DialogService.Confirm(
                "手动添加需要粘贴 .ROBLOSECURITY。它等同于账号密码，只应粘贴到你自己的电脑上。\n\n继续吗？",
                "手动添加账号", "继续", "取消"))
            return;

        var cookie = DialogService.Prompt(
            "手动添加 Roblox 账号",
            ".ROBLOSECURITY",
            "",
            "粘贴完整的 Cookie；它只会保存在本机并用 Windows DPAPI 加密");
        if (string.IsNullOrWhiteSpace(cookie))
            return;

        SetAccountsBusy(true);
        AccountStatus.Text = "正在验证 Cookie…";
        try
        {
            var (account, error) = await AccountService.AddFromCookieAsync(cookie);
            if (account is null && error is not null &&
                DialogService.Confirm(
                    "无法验证这个 Cookie：\n" + error +
                    "\n\n可能是当前网络限制了 Roblox 的验证接口。仍然保存并切换吗？",
                    "无法验证 Cookie", "仍然保存", "取消"))
            {
                (account, error) = await AccountService.AddFromCookieAsync(
                    cookie, null, allowUnverified: true);
            }

            if (account is null)
            {
                AccountStatus.Text = "添加失败：" + error;
                DialogService.Error(error ?? "无法添加账号。", "添加账号失败");
            }
            else if (error is not null)
            {
                AccountStatus.Text = $"账号已保存，但切换失败：{error}";
                DialogService.Error(error, "账号已保存，但切换失败");
            }
            else
            {
                AccountStatus.Text = account.IsVerified
                    ? $"已添加并切换到 {account.Display}。"
                    : "已添加并切换账号（暂未验证账号信息）。";
                MainWindowHost?.ShowToast("已添加 Roblox 账号：" + account.Display);
            }
        }
        catch (Exception ex)
        {
            Log.Error("手动添加账号失败", ex);
            AccountStatus.Text = "添加失败：" + ex.Message;
            DialogService.Error("添加账号失败：\n" + ex.Message);
        }
        finally
        {
            SetAccountsBusy(false);
            RefreshAccountsUi();
        }
    }

    private async void RefreshAvatars_Click(object sender, RoutedEventArgs e)
    {
        AccountStatus.Text = "正在获取账号头像…";
        await LoadAvatarsAsync();
        AccountStatus.Text = "头像已更新。";
    }

    private void SwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RobloxAccount account })
            return;
        try
        {
            AccountService.Switch(account);
            AccountStatus.Text = $"已切换到 {account.Display}；新启动的客户端会使用这个账号。";
            MainWindowHost?.ShowToast("已切换账号：" + account.Display);
        }
        catch (Exception ex)
        {
            AccountStatus.Text = "切换失败：" + ex.Message;
            DialogService.Error("切换账号失败：\n" + ex.Message, "切换账号");
        }
        RefreshAccountsUi();
    }

    private void RemoveAccount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RobloxAccount account })
            return;
        if (!DialogService.Confirm(
                $"从 RCL 移除账号「{account.Display}」？\n\n这不会注销 Roblox 客户端当前的登录；如需恢复接管前的登录，请使用「恢复启动前登录」。",
                "移除账号", "移除", "取消"))
            return;
        AccountService.Remove(account);
        AccountStatus.Text = "已移除账号：" + account.Display;
        RefreshAccountsUi();
    }

    private void RestoreCookie_Click(object sender, RoutedEventArgs e)
    {
        if (!RobloxCookieStore.HasOriginalBackup)
        {
            DialogService.Info("还没有 RCL 接管前的 Cookie 备份。", "没有备份");
            return;
        }
        if (!DialogService.Confirm(
                "把 Roblox Cookie 文件恢复为 RCL 第一次切换账号之前的内容？\n\n当前选中的 RCL 账号会被取消。",
                "恢复启动前登录", "恢复", "取消"))
            return;

        if (AccountService.RestoreOriginalCookie())
        {
            AccountStatus.Text = "已恢复 RCL 接管前的 Roblox 登录。";
            MainWindowHost?.ShowToast("已恢复启动前的 Roblox 登录");
        }
        else
        {
            DialogService.Error("恢复失败，Cookie 文件可能被 Roblox 占用。请先关闭 Roblox。", "恢复失败");
        }
        RefreshAccountsUi();
    }

    private void SetAccountsBusy(bool busy)
    {
        _accountsBusy = busy;
        RefreshAccountsUi();
    }

    // ---------- Roblox protocol takeover ----------

    private void RefreshHandlerUi()
    {
        if (HandlerCurrentText is null)
            return;

        var current = LauncherIntegrationService.DescribeCurrentLauncher();
        var handler = LauncherIntegrationService.GetHandler(LauncherIntegrationService.PlayerProtocol);
        HandlerCurrentText.Text = "当前处理程序：" + current;
        HandlerStatusText.Text = handler.IsRcl
            ? "RCL 已接管 roblox:// 与 roblox-player: 链接；点击链接会由 RCL 使用当前账号启动。"
            : $"链接现在由「{current}」处理。接管后由 RCL 打开，并使用当前选择的 Roblox 账号。";
        HandlerStatusText.Foreground = (System.Windows.Media.Brush)FindResource(
            handler.IsRcl ? "BrushSuccess" : "BrushTextMuted");
        TakeoverButton.IsEnabled = !handler.IsRcl;
        RestoreHandlerButton.IsEnabled = LauncherIntegrationService.HasBackup;
    }

    private void Takeover_Click(object sender, RoutedEventArgs e)
    {
        if (!DialogService.Confirm(
                "把 roblox:// 和 roblox-player: 链接交给 RCL 处理？\n\n" +
                "这会写入当前用户的协议注册表项；原来的处理程序会先备份，可随时恢复。",
                "接管 Roblox 链接", "接管", "取消"))
            return;

        try
        {
            LauncherIntegrationService.TakeOver();
            MainWindowHost?.ShowToast("已接管 Roblox 链接");
        }
        catch (Exception ex)
        {
            Log.Error("接管 Roblox 链接失败", ex);
            DialogService.Error("接管失败：\n" + ex.Message, "接管失败");
        }
        RefreshHandlerUi();
    }

    private void RestoreHandler_Click(object sender, RoutedEventArgs e)
    {
        if (!LauncherIntegrationService.HasBackup)
        {
            DialogService.Info("还没有接管前的协议备份。", "没有备份");
            return;
        }
        if (!DialogService.Confirm(
                "恢复 RCL 接管前的 Roblox 链接处理程序？\n\n之后网页链接会重新交给原来的启动器。",
                "恢复原启动器", "恢复", "取消"))
            return;

        if (LauncherIntegrationService.Restore())
            MainWindowHost?.ShowToast("已恢复接管前的 Roblox 链接处理程序");
        else
            DialogService.Error("恢复失败，请检查注册表权限。", "恢复失败");
        RefreshHandlerUi();
    }

    // ---------- integrations ----------

    private void OnDiscordStateChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateDiscordStatus);
            return;
        }
        UpdateDiscordStatus();
    }

    private void RefreshDiscordUi()
    {
        if (DiscordEnabledCheck is null)
            return;
        var s = AppSettings.Current;
        DiscordEnabledCheck.IsChecked = s.DiscordRpcEnabled;
        DiscordClientIdBox.Text = s.DiscordClientId;
        UpdateDiscordStatus();
    }

    private void UpdateDiscordStatus()
    {
        if (DiscordStatusText is null)
            return;
        var s = AppSettings.Current;
        if (!s.DiscordRpcEnabled)
            DiscordStatusText.Text = "未启用。填写 Application ID 并勾选启用后，启动游戏时才会显示状态。";
        else if (!DiscordRpcService.IsValidClientId(s.DiscordClientId))
            DiscordStatusText.Text = "Application ID 无效：需要 17 位以上的数字。";
        else if (DiscordRpcService.IsConnected)
            DiscordStatusText.Text = "已连接到 Discord。启动游戏后会显示当前实例与游戏名。";
        else
            DiscordStatusText.Text = "等待 Discord…请确认 Discord 正在运行并已登录。";
    }

    private void DiscordEnabled_Click(object sender, RoutedEventArgs e)
    {
        AppSettings.Current.DiscordRpcEnabled = DiscordEnabledCheck.IsChecked == true;
        AppSettings.Save();
        DiscordRpcService.ApplySettings();
        UpdateDiscordStatus();
    }

    private void DiscordApply_Click(object sender, RoutedEventArgs e)
    {
        var id = DiscordClientIdBox.Text.Trim();
        if (id.Length > 0 && !DiscordRpcService.IsValidClientId(id))
        {
            DialogService.Error("Application ID 需要是 17 位以上的数字。", "Discord 状态");
            return;
        }
        AppSettings.Current.DiscordClientId = id;
        AppSettings.Current.DiscordRpcEnabled = DiscordEnabledCheck.IsChecked == true;
        AppSettings.Save();
        DiscordRpcService.ApplySettings();
        UpdateDiscordStatus();
        MainWindowHost?.ShowToast("Discord 设置已保存");
    }

    private async void DiscordTest_Click(object sender, RoutedEventArgs e)
    {
        DiscordStatusText.Text = "正在连接 Discord…";
        DiscordStatusText.Text = await DiscordRpcService.TestAsync();
    }

    private void DiscordPortal_Click(object sender, RoutedEventArgs e) =>
        OpenUrl("https://discord.com/developers/applications");

    // ---------- updates ----------

    private void RefreshUpdateUi()
    {
        if (UpdateStatusText is null)
            return;
        var s = AppSettings.Current;
        UpdateCurrentText.Text = "当前版本：RCL " + UpdateService.CurrentVersion;
        UpdateUrlBox.Text = s.UpdateManifestUrl;
        UpdateStartupCheck.IsChecked = s.CheckUpdatesOnStartup;

        if (UpdateService.LastFound is { } info)
        {
            UpdateStatusText.Text = $"发现新版本 {info.Version}，当前 {UpdateService.CurrentVersion}。" +
                                    (string.IsNullOrWhiteSpace(info.Notes) ? "" : "\n" + info.Notes);
            UpdateInstallButton.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateInstallButton.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = string.IsNullOrWhiteSpace(s.UpdateManifestUrl)
                ? "还没有配置更新源。"
                : "点「保存并检查」获取最新版本。";
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var url = UpdateUrlBox.Text.Trim();
        AppSettings.Current.UpdateManifestUrl = url;
        AppSettings.Current.CheckUpdatesOnStartup = UpdateStartupCheck.IsChecked == true;
        AppSettings.Save();

        UpdateInstallButton.Visibility = Visibility.Collapsed;
        if (url.Length == 0)
        {
            UpdateStatusText.Text = "还没有配置更新源。";
            return;
        }

        UpdateCheckButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            var info = await UpdateService.CheckAsync(url);
            if (info is null)
            {
                UpdateStatusText.Text = $"当前已经是最新版本（RCL {UpdateService.CurrentVersion}）。";
                return;
            }
            UpdateStatusText.Text = $"发现新版本 {info.Version}，当前 {UpdateService.CurrentVersion}。" +
                                    (string.IsNullOrWhiteSpace(info.Notes) ? "" : "\n" + info.Notes);
            UpdateInstallButton.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "检查失败：" + ex.Message;
            Log.Warn("检查更新失败：" + ex.Message);
        }
        finally
        {
            UpdateCheckButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.LastFound is not { } info)
            return;
        if (!DialogService.Confirm(
                $"下载并安装 RCL {info.Version}？\n\nRCL 会退出，替换当前 exe 后自动重新打开。",
                "安装更新", "下载并安装", "取消"))
            return;

        UpdateCheckButton.IsEnabled = false;
        UpdateInstallButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        UpdateProgress.Value = 0;
        UpdateStatusText.Text = "正在下载更新…";
        try
        {
            var progress = new Progress<double>(value =>
            {
                if (value >= 0)
                    UpdateProgress.Value = value * 100;
            });
            var path = await UpdateService.DownloadAsync(info, progress);
            UpdateStatusText.Text = "下载完成，正在替换 RCL…";
            UpdateService.ApplyAndRestart(path);
        }
        catch (Exception ex)
        {
            UpdateInstallButton.IsEnabled = true;
            UpdateCheckButton.IsEnabled = true;
            UpdateProgress.Visibility = Visibility.Collapsed;
            UpdateStatusText.Text = "更新失败：" + ex.Message;
            Log.Error("安装更新失败", ex);
            DialogService.Error("更新失败：\n" + ex.Message, "更新失败");
        }
    }

    private void RestoreClient_Click(object sender, RoutedEventArgs e)
    {
        if (RobloxInstall.RunningCount() > 0)
        {
            DialogService.Info("请先关闭所有 Roblox 客户端，再还原客户端修改。", "客户端安全");
            return;
        }

        if (!DialogService.Confirm(
                "将还原 RCL 写入的 FastFlags、恢复被模组覆盖的文件，并清理空闲的多开槽位。\n\n" +
                "不会删除你的模组源文件，也不会改动其他启动器的文件。继续吗？",
                "还原客户端修改", "还原", "取消"))
            return;

        try
        {
            var result = ClientCleanupService.RestoreAll();
            var text = result.Summary;
            if (result.Warnings.Count > 0)
                text += "\n\n" + string.Join("\n", result.Warnings.Take(4)) +
                        (result.Warnings.Count > 4 ? "\n…" : "");
            ClientSafetyStatus.Text = text;
            if (result.ChangedAnything || result.Warnings.Count > 0)
                DialogService.Info(text, "客户端安全");
            else
                MainWindowHost?.ShowToast("未发现 RCL 写入的客户端修改");
        }
        catch (Exception ex)
        {
            Log.Error("还原客户端修改失败", ex);
            DialogService.Error("还原失败：\n" + ex.Message, "客户端安全");
        }
    }

    private void OpenRepair_Click(object sender, RoutedEventArgs e)
    {
        MainWindowHost?.NavigateTo(2);
        MainWindowHost?.ShowToast("在「已安装版本」选择版本，点「校验并修复」");
    }

    private MainWindow? MainWindowHost => Window.GetWindow(this) as MainWindow;
}
