using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using RCL.Core;

namespace RCL.Dialogs;

public partial class AccountLoginWindow : Window
{
    private readonly string _userDataFolder =
        Path.Combine(AppPaths.WebViewData, "login-" + Guid.NewGuid().ToString("N"));
    private bool _ready;

    public string? Cookie { get; private set; }
    public AuthenticatedRobloxUser? User { get; private set; }

    public AccountLoginWindow()
    {
        InitializeComponent();
        Log.Info("打开 Roblox 账号登录窗口");
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => Cleanup();
    }

    private async Task InitializeAsync()
    {
        try
        {
            Directory.CreateDirectory(_userDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(null, _userDataFolder);
            await WebView.EnsureCoreWebView2Async(environment);
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = true;
            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            WebView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                StatusText.Text = "登录完成后点「完成添加」。";
            };
            WebView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                try
                {
                    StatusText.Text = "正在打开：" + new Uri(args.Uri).Host;
                }
                catch
                {
                }
            };
            _ready = true;
            WebView.CoreWebView2.Navigate("https://www.roblox.com/login");
            Log.Info("WebView2 登录页面开始加载");
        }
        catch (Exception ex)
        {
            LoadingText.Text = "无法启动内置浏览器（WebView2）：\n" + ex.Message +
                               "\n\n可以关闭本窗口，改用「手动 Cookie」添加账号。";
            FinishButton.IsEnabled = false;
            StatusText.Text = "WebView2 不可用，请改用手动 Cookie。";
            Log.Warn("WebView2 登录窗口初始化失败：" + ex.Message);
        }
    }

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || WebView.CoreWebView2 is null)
            return;

        FinishButton.IsEnabled = false;
        StatusText.Text = "正在读取 Roblox 登录 Cookie…";
        try
        {
            var cookies = await WebView.CoreWebView2.CookieManager
                .GetCookiesAsync("https://www.roblox.com");
            var security = cookies
                .Where(c => c.Name.Equals(".ROBLOSECURITY", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Value?.Length ?? 0)
                .FirstOrDefault();
            if (security is null || string.IsNullOrWhiteSpace(security.Value))
            {
                StatusText.Text = "没有检测到登录 Cookie。请先在页面完成登录（或处理验证码）后再点一次。";
                FinishButton.IsEnabled = true;
                return;
            }

            Cookie = RobloxAccountApi.SanitizeCookie(security.Value);
            if (!RobloxAccountApi.LooksPlausible(Cookie))
            {
                StatusText.Text = "读取到的 Cookie 格式不完整，请重新登录后再试。";
                FinishButton.IsEnabled = true;
                return;
            }

            StatusText.Text = "已读取登录 Cookie，正在读取账号信息…";
            User = await ReadUserViaBrowserAsync();
            StatusText.Text = User is null
                ? "账号信息暂时无法读取，将先保存登录 Cookie。"
                : $"已识别账号：{User.DisplayName}";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取 Cookie 失败：" + ex.Message;
            FinishButton.IsEnabled = true;
            Log.Warn("读取 WebView2 Cookie 失败：" + ex.Message);
        }
    }

    private async Task<AuthenticatedRobloxUser?> ReadUserViaBrowserAsync()
    {
        try
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args) =>
                completion.TrySetResult(args.IsSuccess);

            WebView.CoreWebView2.NavigationCompleted += OnCompleted;
            try
            {
                WebView.CoreWebView2.Navigate("https://users.roblox.com/v1/users/authenticated");
                var finished = await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(6)));
                if (finished != completion.Task || !completion.Task.Result)
                    return null;

                var raw = await WebView.CoreWebView2.ExecuteScriptAsync("document.body.innerText");
                var text = JsonSerializer.Deserialize<string>(raw) ?? raw;
                if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{'))
                    return null;

                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) || id <= 0)
                    return null;
                var name = root.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString() ?? ""
                    : "";
                var display = root.TryGetProperty("displayName", out var displayElement)
                    ? displayElement.GetString() ?? name
                    : name;
                if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(display))
                    return null;
                Log.Info("WebView2 已读取登录账号信息");
                return new AuthenticatedRobloxUser(id, name, display);
            }
            finally
            {
                WebView.CoreWebView2.NavigationCompleted -= OnCompleted;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("通过 WebView2 读取账号信息失败：" + ex.Message);
            return null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Title_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Cleanup()
    {
        try
        {
            WebView.Dispose();
        }
        catch
        {
        }
        try
        {
            if (Directory.Exists(_userDataFolder))
                Directory.Delete(_userDataFolder, true);
        }
        catch
        {
            // The browser process can still hold the folder for a moment.
        }
    }
}
