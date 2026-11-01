using System.Windows;
using System.Windows.Threading;
using RCL.Models;

namespace RCL.Core;

/// <summary>
/// Keeps instances that opted in pinned to the Roblox version their chosen executor supports.
/// WEAO is polled every half hour; when the supported version changes RCL downloads it (or
/// reuses an installed copy) and rebinds the instance.
/// </summary>
public static class ExecutorFollowService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Notified = new(StringComparer.OrdinalIgnoreCase);
    private static DispatcherTimer? _timer;
    private static bool _firstTick = true;
    private static bool _running;

    public static event Action<string, bool>? Notify;

    public static void Start()
    {
        if (_timer is not null)
            return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _timer.Tick += async (_, _) =>
        {
            if (_firstTick)
            {
                _firstTick = false;
                _timer!.Interval = TimeSpan.FromMinutes(30);
            }
            await CheckAllAsync(false);
        };
        _timer.Start();
        Log.Info("执行器版本跟随服务已启动（每 30 分钟检查一次 WEAO）");
    }

    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    public static async Task CheckAllAsync(bool force)
    {
        if (_running)
            return;
        var followers = InstanceStore.All.Where(i => i.FollowExecutorVersion).ToList();
        if (followers.Count == 0)
            return;

        _running = true;
        try
        {
            var snapshot = await WeaoService.GetSnapshotAsync(force);
            if (snapshot.IsEmpty)
            {
                if (force)
                    Notify?.Invoke("WEAO 暂时没有返回执行器数据，请稍后重试。", true);
                return;
            }

            foreach (var instance in followers)
            {
                if (!InstanceStore.All.Contains(instance))
                    continue;

                var target = ExecutorVersionService.ResolveTargetVersion(snapshot, instance.FollowExecutor);
                if (string.IsNullOrWhiteSpace(target))
                {
                    Log.Warn($"无法为实例「{instance.Name}」解析执行器支持版本");
                    continue;
                }

                var current = RobloxInstall.Resolve(instance.VersionPath)?.FolderName;
                if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                {
                    Notified.Remove(instance.Id + ":" + target);
                    continue;
                }

                var directory = ExecutorVersionService.FindInstalledDirectory(target);
                if (directory is null)
                {
                    if (!instance.AutoDownloadExecutorVersion)
                    {
                        if (Notified.Add(instance.Id + ":" + target))
                            Notify?.Invoke(
                                $"执行器支持版本已变为 {WeaoExecutor.ShortenVersion(target)}；" +
                                $"「{instance.Name}」没有自动下载。可在下载页手动安装。", false);
                        continue;
                    }

                    Notify?.Invoke(
                        $"检测到执行器支持版本 {WeaoExecutor.ShortenVersion(target)}，" +
                        $"正在为「{instance.Name}」下载…", false);
                    try
                    {
                        directory = await ExecutorVersionService.EnsureInstalledAsync(target);
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"为实例「{instance.Name}」下载执行器版本失败", ex);
                        Notify?.Invoke(
                            $"下载执行器版本失败：{ex.Message}", true);
                        continue;
                    }
                }

                instance.VersionPath = directory;
                InstanceStore.Save(instance);
                instance.NotifyAll();
                Notified.Remove(instance.Id + ":" + target);
                Log.Info($"实例「{instance.Name}」已锁定到执行器支持版本 {target}");
                Notify?.Invoke(
                    $"「{instance.Name}」已锁定到执行器支持版本 {WeaoExecutor.ShortenVersion(target)}。", false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("检查执行器版本失败", ex);
            if (force)
                Notify?.Invoke("检查执行器版本失败：" + ex.Message, true);
        }
        finally
        {
            _running = false;
        }
    }

    public static async Task<string> CheckSingleAsync(RclInstance instance, bool force)
    {
        var snapshot = await WeaoService.GetSnapshotAsync(force);
        if (snapshot.IsEmpty)
            return "WEAO 没有返回执行器数据。";
        var target = ExecutorVersionService.ResolveTargetVersion(snapshot, instance.FollowExecutor);
        if (string.IsNullOrWhiteSpace(target))
            return "找不到所选执行器的支持版本。";
        var current = RobloxInstall.Resolve(instance.VersionPath)?.FolderName;
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            return "已经是最新的执行器支持版本。";

        var dir = ExecutorVersionService.FindInstalledDirectory(target);
        if (dir is null)
        {
            if (!instance.AutoDownloadExecutorVersion)
                return $"新版本是 {WeaoExecutor.ShortenVersion(target)}，尚未安装。";
            try
            {
                dir = await ExecutorVersionService.EnsureInstalledAsync(target);
            }
            catch (Exception ex)
            {
                return "下载失败：" + ex.Message;
            }
        }

        instance.VersionPath = dir;
        InstanceStore.Save(instance);
        instance.NotifyAll();
        return $"已切换并锁定到 {WeaoExecutor.ShortenVersion(target)}。";
    }
}
