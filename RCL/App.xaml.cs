using System.Windows;
using System.Windows.Threading;
using RCL.Core;
using RCL.Dialogs;

namespace RCL;

public partial class App : Application
{
    public static App Instance => (App)Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppPaths.Ensure();
        Log.Info("==== RCL 1.3.0 启动 ====");
        AppSettings.Load();
        ThemeManager.Apply(AppSettings.Current.AccentHex);
        InstanceStore.Load();
        AccountService.Load();
        QuickLaunchService.Load();
        if (InstanceStore.All.Count == 0)
            InstanceStore.Create("默认实例");
        SessionManager.Start();
        SessionManager.Changed += DiscordRpcService.RefreshFromSessions;
        DiscordRpcService.ApplySettings();

        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("未处理的异常：" + args.ExceptionObject);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        ExecutorFollowService.Notify += (message, error) =>
            window.Dispatcher.BeginInvoke(new Action(() => window.ShowToast(message, error)));
        ExecutorFollowService.Start();

        var cli = CommandLineOptions.Parse(e.Args);
        if (cli.LaunchId is not null || cli.IsProtocolLaunch)
            window.Dispatcher.BeginInvoke(new Action(() => window.LaunchFromCommandLine(cli)), DispatcherPriority.Loaded);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            ExecutorFollowService.Stop();
            DiscordRpcService.Stop();
            SessionManager.Shutdown();
            InstanceStore.SaveAll();
            QuickLaunchService.Save();
            AppSettings.Save();
            MultiInstanceLock.Release();
            Log.Info("==== RCL 退出 ====");
        }
        catch
        {
        }
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("界面线程异常", e.Exception);
        e.Handled = true;
        try
        {
            DialogService.Error("发生了未处理的错误：\n" + e.Exception.Message + "\n\n详细信息已写入日志。", "RCL 错误");
        }
        catch
        {
        }
    }
}

public sealed class CommandLineOptions
{
    public Guid? LaunchId { get; private set; }
    public long? PlaceId { get; private set; }
    public string? JobId { get; private set; }
    public bool IsProtocolLaunch { get; private set; }

    public static CommandLineOptions Parse(string[] args)
    {
        var result = new CommandLineOptions();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--launch" when i + 1 < args.Length:
                    if (Guid.TryParseExact(args[++i], "N", out var guid) || Guid.TryParse(args[i], out guid))
                        result.LaunchId = guid;
                    break;
                case "--place" when i + 1 < args.Length:
                    result.PlaceId = RobloxApi.ParsePlaceId(args[++i]);
                    break;
                case "--job" when i + 1 < args.Length:
                    result.JobId = args[++i];
                    break;
                default:
                    ParseProtocolArgument(args[i], result);
                    break;
            }
        }
        return result;
    }

    private static void ParseProtocolArgument(string raw, CommandLineOptions result)
    {
        var arg = raw.Trim().Trim('"');
        if (!arg.StartsWith("roblox-player:", StringComparison.OrdinalIgnoreCase) &&
            !arg.StartsWith("roblox:", StringComparison.OrdinalIgnoreCase))
            return;

        result.IsProtocolLaunch = true;
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(arg);
        }
        catch
        {
            decoded = arg;
        }

        var place = RobloxApi.ParsePlaceId(decoded);
        if (place > 0)
            result.PlaceId = place;

        var job = ExtractQueryValue(decoded, "gameInstanceId") ?? ExtractQueryValue(decoded, "gameId");
        if (!string.IsNullOrWhiteSpace(job))
            result.JobId = job;

        var launcherIndex = decoded.IndexOf("placelauncherurl:", StringComparison.OrdinalIgnoreCase);
        if (launcherIndex >= 0)
        {
            var url = decoded[(launcherIndex + "placelauncherurl:".Length)..];
            var end = url.IndexOf('+');
            if (end >= 0)
                url = url[..end];
            try
            {
                url = Uri.UnescapeDataString(url);
            }
            catch
            {
            }
            var urlPlace = RobloxApi.ParsePlaceId(url);
            if (urlPlace > 0)
                result.PlaceId = urlPlace;
            var urlJob = ExtractQueryValue(url, "gameInstanceId") ?? ExtractQueryValue(url, "gameId");
            if (!string.IsNullOrWhiteSpace(urlJob))
                result.JobId = urlJob;
        }

        Log.Info($"收到 Roblox 协议链接：place={result.PlaceId?.ToString() ?? "无"} job={result.JobId ?? "无"}");
    }

    private static string? ExtractQueryValue(string text, string key)
    {
        var index = text.IndexOf(key + "=", StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return null;
        var start = index + key.Length + 1;
        var end = text.IndexOfAny(new[] { '&', '+', '\r', '\n' }, start);
        var value = end < 0 ? text[start..] : text[start..end];
        try
        {
            value = Uri.UnescapeDataString(value);
        }
        catch
        {
        }
        value = value.Trim();
        return value.Length == 0 ? null : value;
    }
}
