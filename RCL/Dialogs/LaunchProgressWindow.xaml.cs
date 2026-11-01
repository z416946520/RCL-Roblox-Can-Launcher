using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using RCL.Core;

namespace RCL.Dialogs;

/// <summary>
/// The bootstrapper-style window shown while a client is being prepared and started. It mirrors
/// what Voidstrap/Fishstrap show: channel, version, status line, progress and a cancel button.
/// </summary>
public partial class LaunchProgressWindow : Window
{
    private static readonly Regex PercentPattern = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);

    private readonly CancellationTokenSource _cts = new();
    private bool _closed;

    public CancellationToken Token => _cts.Token;

    public bool Canceled => _cts.IsCancellationRequested;

    public LaunchProgressWindow(RobloxVersion? version)
    {
        InitializeComponent();
        ChannelText.Text = "Channel: production";
        VersionText.Text = "Version: " + (version?.DisplayName ?? "\u2014");
        LogoImage.Source = LoadLogo();
        Report(Loc.T("正在与 Roblox 建立连接…"));
    }

    /// <summary>
    /// WPF picks the first (16x16) frame when an .ico is used as an ImageSource, which looks
    /// blurry scaled up. Decode the icon and take the largest frame instead.
    /// </summary>
    private static System.Windows.Media.ImageSource? LoadLogo()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/RCL;component/Assets/RCL.ico");
            var resource = Application.GetResourceStream(uri);
            if (resource?.Stream is null)
                return null;
            using var stream = resource.Stream;
            var decoder = new IconBitmapDecoder(stream,
                BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).FirstOrDefault();
            frame?.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            Log.Warn("加载启动窗图标失败：" + ex.Message);
            return null;
        }
    }

    public void Report(string message)
    {
        if (_closed)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Report(message)));
            return;
        }
        if (Canceled)
            return;

        StatusText.Text = message;
        var match = PercentPattern.Match(message);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Clamp(percent, 0, 100);
        }
        else if (!Progress.IsIndeterminate && !message.Contains('%'))
        {
            Progress.IsIndeterminate = true;
        }
    }

    public void CloseSafely()
    {
        _closed = true;
        try { Close(); } catch { }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (Canceled)
            return;
        _cts.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = Loc.T("正在取消…");
        Progress.IsIndeterminate = true;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _cts.Cancel();
        base.OnClosed(e);
    }
}
