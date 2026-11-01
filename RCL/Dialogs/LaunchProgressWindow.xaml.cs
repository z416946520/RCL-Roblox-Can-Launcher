using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        Loaded += (_, _) =>
        {
            if (_indeterminate)
                StartSweep();
        };
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
            ShowPercent(Math.Clamp(percent, 0, 100));
        else
            ShowIndeterminate();
    }

    /// <summary>Rounded capsule sweep used while there is no real percentage to show.</summary>
    private bool _indeterminate = true;

    private void ShowIndeterminate()
    {
        if (_indeterminate && BarGlow.Visibility == Visibility.Visible)
            return;
        _indeterminate = true;
        BarFill.Visibility = Visibility.Collapsed;
        BarGlow.Visibility = Visibility.Visible;
        StartSweep();
    }

    private void StartSweep()
    {
        var width = BarTrack.ActualWidth > 0 ? BarTrack.ActualWidth : 480;
        var animation = new DoubleAnimation(-BarGlow.Width, width + BarGlow.Width,
            TimeSpan.FromSeconds(1.5))
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        BarOffset.BeginAnimation(TranslateTransform.XProperty, animation);
    }

    private void ShowPercent(double percent)
    {
        _indeterminate = false;
        BarOffset.BeginAnimation(TranslateTransform.XProperty, null);
        BarGlow.Visibility = Visibility.Collapsed;
        BarFill.Visibility = Visibility.Visible;
        var width = BarTrack.ActualWidth > 0 ? BarTrack.ActualWidth : 480;
        BarFill.Width = Math.Max(0, width * percent / 100.0);
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
        ShowIndeterminate();
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
