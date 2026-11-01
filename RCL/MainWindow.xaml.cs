using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using RCL.Core;
using RCL.Dialogs;
using RCL.Models;
using RCL.Pages;

namespace RCL;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.2) };
    private readonly LaunchPage _launchPage = new();
    private readonly InstancesPage _instancesPage = new();
    private readonly DownloadPage _downloadPage = new();
    private readonly QuickLaunchPage _quickLaunchPage = new();
    private readonly ToolsPage _toolsPage = new();
    private readonly SettingsPage _settingsPage = new();
    private int _currentTab = -1;

    public MainWindow()
    {
        InitializeComponent();
        Width = AppSettings.Current.WindowWidth;
        Height = AppSettings.Current.WindowHeight;

        _toastTimer.Tick += (_, _) => HideToast();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => InstanceStore.SaveAll();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var s = AppSettings.Current;
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
        if (bounds.Width >= 1000 && bounds.Height >= 640)
        {
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
        }
        s.WindowMaximized = false;
        AppSettings.Save();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyAppearance();
        UpdateShellClip();
        NavigateTo(0);
        if (AppSettings.Current.CheckUpdatesOnStartup &&
            !string.IsNullOrWhiteSpace(AppSettings.Current.UpdateManifestUrl))
            _ = CheckUpdatesOnStartupAsync();
    }

    private async Task CheckUpdatesOnStartupAsync()
    {
        try
        {
            var info = await UpdateService.CheckAsync(AppSettings.Current.UpdateManifestUrl);
            if (info is not null)
                ShowToast($"发现 RCL {info.Version}，可在「设置 → 数据与关于」更新");
        }
        catch (Exception ex)
        {
            Log.Warn("启动更新检查失败：" + ex.Message);
        }
    }

    public void ApplyAppearance()
    {
        ThemeManager.Apply(AppSettings.Current.AccentHex);
        var path = AppSettings.Current.BackgroundImage;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();
                Wallpaper.Source = image;
                Wallpaper.Opacity = AppSettings.Current.BackgroundOpacity;
                Wallpaper.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                Log.Warn("背景图加载失败：" + ex.Message);
                ClearWallpaper();
            }
        }
        else
        {
            ClearWallpaper();
        }
    }

    private void ClearWallpaper()
    {
        Wallpaper.Source = null;
        Wallpaper.Visibility = Visibility.Collapsed;
    }

    public void ShowToast(string message, bool error = false)
    {
        ToastText.Text = message;
        ToastIcon.Text = error ? "\uE783" : "\uE73E";
        ToastIcon.Foreground = (Brush)FindResource(error ? "BrushDanger" : "BrushAccent");
        ToastBorder.Visibility = Visibility.Visible;
        ToastBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        _toastTimer.Stop();
        _toastTimer.Start();
        Log.Info((error ? "[提示] " : "[通知] ") + message);
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => ToastBorder.Visibility = Visibility.Collapsed;
        ToastBorder.BeginAnimation(OpacityProperty, fade);
    }

    public void NavigateTo(int index)
    {
        index = Math.Clamp(index, 0, 5);
        _currentTab = index;
        var tab = index switch
        {
            0 => TabLaunch,
            1 => TabInstances,
            2 => TabDownload,
            3 => TabQuickLaunch,
            4 => TabTools,
            _ => TabSettings
        };
        if (tab.IsChecked != true)
            tab.IsChecked = true;

        PageHost.Content = index switch
        {
            0 => _launchPage,
            1 => _instancesPage,
            2 => _downloadPage,
            3 => _quickLaunchPage,
            4 => _toolsPage,
            _ => _settingsPage
        };

        switch (index)
        {
            case 0: _launchPage.OnActivated(); break;
            case 1: _instancesPage.OnActivated(); break;
            case 2: _downloadPage.OnActivated(); break;
            case 3: _quickLaunchPage.OnActivated(); break;
            case 4: _toolsPage.OnActivated(); break;
            case 5: _settingsPage.OnActivated(); break;
        }
    }

    public void OpenInstanceEditor(RclInstance instance)
    {
        NavigateTo(1);
        _instancesPage.SelectInstance(instance);
    }

    public void OpenAccounts()
    {
        NavigateTo(5);
        _settingsPage.ShowAccounts();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton { IsChecked: true } tab)
            return;
        int index = tab == TabLaunch ? 0
            : tab == TabInstances ? 1
            : tab == TabDownload ? 2
            : tab == TabQuickLaunch ? 3
            : tab == TabTools ? 4
            : 5;
        if (index != _currentTab)
            NavigateTo(index);
    }

    public void LaunchFromCommandLine(CommandLineOptions options)
    {
        var inst = options.LaunchId is { } id
            ? InstanceStore.Find(id)
            : InstanceStore.Find(Guid.TryParseExact(AppSettings.Current.LastInstanceId, "N", out var last)
                ? last
                : Guid.Empty) ?? InstanceStore.All.FirstOrDefault();
        if (inst is null)
        {
            DialogService.Error(options.IsProtocolLaunch
                ? "没有可以处理 Roblox 链接的实例。请先在 RCL 里创建一个实例。"
                : "找不到快捷方式指向的实例，可能已被删除。", "无法启动");
            return;
        }
        if (options.IsProtocolLaunch)
            Log.Info($"协议链接交给实例「{inst.Name}」处理");
        NavigateTo(0);
        _ = LaunchFromShortcutAsync(inst, options);
    }

    private async Task LaunchFromShortcutAsync(RclInstance inst, CommandLineOptions options)
    {
        try
        {
            ShowToast($"正在启动 {inst.Name}…");
            var game = inst.LastGame;
            long? placeId = options.IsProtocolLaunch
                ? options.PlaceId
                : options.PlaceId is > 0 ? options.PlaceId : game?.PlaceId;
            string? jobId = options.IsProtocolLaunch ? options.JobId : options.JobId ?? game?.JobId;
            var result = await LaunchService.LaunchAsync(
                inst,
                placeId,
                jobId,
                options.IsProtocolLaunch ? null : game?.Title,
                inst.MultiInstance);

            ShowToast($"{inst.Name} 已启动（PID {result.Session.Pid}）");
            if (AppSettings.Current.CloseAfterLaunch)
                Close();
        }
        catch (Exception ex)
        {
            Log.Error("快捷方式启动失败", ex);
            DialogService.Error(ex is LaunchException ? ex.Message : "启动失败：\n" + ex.Message, "启动失败");
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateShellClip();

    private void UpdateShellClip()
    {
        if (Shell.ActualWidth <= 0 || Shell.ActualHeight <= 0)
            return;
        Shell.Clip = new RectangleGeometry(new Rect(0, 0, Shell.ActualWidth, Shell.ActualHeight), 6, 6);
    }

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_SIZE = 0xF000;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private void ResizeWindow(int direction)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SendMessage(handle, WM_SYSCOMMAND, new IntPtr(SC_SIZE + direction), IntPtr.Zero);
    }

    private void ResizeLeft(object sender, MouseButtonEventArgs e) => ResizeWindow(1);
    private void ResizeRight(object sender, MouseButtonEventArgs e) => ResizeWindow(2);
    private void ResizeTop(object sender, MouseButtonEventArgs e) => ResizeWindow(3);
    private void ResizeTopLeft(object sender, MouseButtonEventArgs e) => ResizeWindow(4);
    private void ResizeTopRight(object sender, MouseButtonEventArgs e) => ResizeWindow(5);
    private void ResizeBottom(object sender, MouseButtonEventArgs e) => ResizeWindow(6);
    private void ResizeBottomLeft(object sender, MouseButtonEventArgs e) => ResizeWindow(7);
    private void ResizeBottomRight(object sender, MouseButtonEventArgs e) => ResizeWindow(8);
}
