using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using RCL.Models;

namespace RCL.Core;

public static class SessionManager
{
    public static ObservableCollection<RunningSession> Sessions { get; } = new();

    public static event Action? Changed;
    public static event Action<RunningSession>? SessionEnded;

    private static DispatcherTimer? _timer;
    private static bool _running;

    public static void Start()
    {
        if (_running)
            return;
        _running = true;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Log.Info("进程监视器已启动");
    }

    public static RunningSession Track(RclInstance instance, Process process, string placeName, string launchDirectory, long? placeId = null)
    {
        var session = new RunningSession
        {
            InstanceId = instance.Id,
            InstanceName = instance.Name,
            Pid = process.Id,
            StartedAt = DateTime.Now,
            PlaceName = placeName,
            PlaceId = placeId,
            Process = process
        };
        Sessions.Add(session);
        Changed?.Invoke();
        Log.Info($"跟踪进程 PID {session.Pid}（{instance.Name}），启动目录 {launchDirectory}");
        return session;
    }

    public static void Kill(RunningSession session)
    {
        try
        {
            if (session.Process is { HasExited: false })
            {
                session.Process.Kill(true);
                Log.Info("已结束进程 PID " + session.Pid);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"结束 PID {session.Pid} 失败：{ex.Message}");
        }
    }

    public static void KillAll()
    {
        foreach (var session in Sessions.ToList())
            Kill(session);
    }

    /// <summary>
    /// Records elapsed time for sessions still running when the launcher closes. The Roblox
    /// clients keep running, but RCL cannot keep counting once its process exits.
    /// </summary>
    public static void Shutdown()
    {
        foreach (var session in Sessions.ToList())
        {
            try
            {
                if (session.IsExternal)
                    continue;
                var inst = InstanceStore.Find(session.InstanceId);
                if (inst is null)
                    continue;
                var elapsed = DateTime.Now - session.StartedAt;
                if (AppSettings.Current.TrackPlaytime && elapsed.TotalSeconds > 1)
                {
                    inst.PlaytimeSeconds += (long)elapsed.TotalSeconds;
                    inst.LastPlayed = DateTime.Now;
                    Log.Info($"退出时结算会话 PID {session.Pid}，时长 {elapsed:hh\\:mm\\:ss}");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("退出时结算会话失败：" + ex.Message);
            }
        }
        Sessions.Clear();
        _timer?.Stop();
        _running = false;
    }

    private static void Tick()
    {
        foreach (var session in Sessions.ToList())
        {
            var elapsed = DateTime.Now - session.StartedAt;
            session.Elapsed = elapsed.TotalHours >= 1
                ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
                : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";

            bool exited;
            try
            {
                exited = session.Process is null || session.Process.HasExited;
            }
            catch
            {
                exited = true;
            }

            if (!exited)
                continue;

            Sessions.Remove(session);
            Finalize(session);
            Changed?.Invoke();
            SessionEnded?.Invoke(session);
        }
    }

    private static void Finalize(RunningSession session)
    {
        try
        {
            if (session.IsExternal)
                return;
            var inst = InstanceStore.Find(session.InstanceId);
            if (inst is null)
                return;

            var elapsed = DateTime.Now - session.StartedAt;
            if (AppSettings.Current.TrackPlaytime && elapsed.TotalSeconds > 1)
            {
                inst.PlaytimeSeconds += (long)elapsed.TotalSeconds;
                inst.LastPlayed = DateTime.Now;
                InstanceStore.Save(inst);
            }
            Log.Info($"进程 PID {session.Pid} 已退出，会话时长 {elapsed:hh\\:mm\\:ss}");
        }
        catch (Exception ex)
        {
            Log.Warn("结算会话失败：" + ex.Message);
        }
    }
}

public sealed class LaunchException : Exception
{
    public LaunchException(string message) : base(message)
    {
    }
}

public sealed class LaunchResult
{
    public RunningSession Session { get; init; } = null!;
    public string VersionDirectory { get; init; } = "";
    public string LaunchDirectory { get; init; } = "";
    public string Executable { get; init; } = "";
    public bool MultiInstance { get; init; }
    public bool UsedSlot { get; init; }
    public bool SafeFallback { get; init; }
    public int ModsApplied { get; init; }
    public int FlagsApplied { get; init; }
    public string Arguments { get; init; } = "";
}

public static class LaunchService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<LaunchResult> LaunchAsync(
        RclInstance instance,
        long? placeId,
        string? jobId,
        string? placeName,
        bool multiInstance,
        Action<string>? status = null,
        CancellationToken cancellationToken = default,
        string? deeplink = null)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            status?.Invoke("检查 Roblox 版本…");
            if (string.IsNullOrWhiteSpace(instance.VersionPath) && RobloxUpdateService.ShouldCheck())
                await RobloxUpdateService.EnsureLatestAsync(status, cancellationToken);

            var version = RobloxInstall.Resolve(instance.VersionPath);
            if (version is null)
            {
                if (!string.IsNullOrWhiteSpace(instance.VersionPath))
                    throw new LaunchException("实例绑定的 Roblox 版本目录不存在：\n" + instance.VersionPath);
                throw new LaunchException("没有检测到 Roblox 客户端。请先在「下载」页安装，或在实例里绑定一个已有版本。");
            }

            Log.Info($"启动 {instance.Name}，版本 {version.FolderName}，多开={multiInstance}");

            int flagsApplied = 0;
            int modsApplied = 0;
            status?.Invoke("应用 FastFlags…");
            FastFlags.DeployToVersion(instance, version.Directory);
            flagsApplied = instance.FastFlags.Count(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Name));

            if (instance.ApplyMods)
            {
                status?.Invoke("应用模组…");
                modsApplied = ModsService.Apply(instance, version.Directory);
            }

            string exe = version.ExePath;
            string launchDir = version.Directory;
            bool usedSlot = false;

            if (multiInstance)
            {
                status?.Invoke("准备多开槽位…");
                MultiInstanceLock.Acquire();
                var busy = MachineBusyPaths.Scan();
                var slot = MultiInstanceLock.PrepareSlot(version.Directory, busy, AppSettings.Current.MaxSlots);
                if (slot is not null)
                {
                    exe = Path.Combine(slot, "RobloxPlayerBeta.exe");
                    launchDir = slot;
                    usedSlot = true;
                }
            }

            var argumentList = BuildArguments(placeId, jobId, instance.LaunchArgs, deeplink);
            try
            {
                AccountService.PrepareLaunch();
            }
            catch (Exception ex)
            {
                throw new LaunchException(ex.Message);
            }

            var attempt = await StartAsync(exe, launchDir, argumentList, status);
            bool safeFallback = false;

            if (attempt.Watch.Health == LaunchHealth.Crashed)
            {
                Log.Warn($"Roblox 启动即崩溃（退出码 {FormatExitCode(attempt.Watch.ExitCode)}），按原样重试一次");
                status?.Invoke("客户端启动崩溃，按原样重试…");
                attempt = await StartAsync(exe, launchDir, argumentList, status);
            }

            if (attempt.Watch.Health == LaunchHealth.Crashed)
            {
                Log.Warn("Roblox 连续两次启动崩溃，改用安全启动（跳过 FastFlags、模组与多开）");
                status?.Invoke("仍然崩溃，改用安全启动…");
                FastFlags.RestoreFromVersion(version.Directory);
                ModsService.Restore(instance, version.Directory);
                flagsApplied = 0;
                modsApplied = 0;
                attempt = await StartAsync(version.ExePath, version.Directory, argumentList, status);
                if (attempt.Watch.Health != LaunchHealth.Crashed)
                {
                    exe = version.ExePath;
                    launchDir = version.Directory;
                    usedSlot = false;
                    safeFallback = true;
                }
            }

            if (attempt.Watch.Health == LaunchHealth.Crashed)
            {
                // Last resort before giving up: Roblox's own asset cache can be the thing that
                // keeps crashing the client. Moving it aside keeps login and settings intact.
                Log.Warn("安全启动仍然崩溃，重置 Roblox 本地缓存后重试");
                status?.Invoke("仍然崩溃，重置 Roblox 本地缓存后重试…");
                var cacheBackup = RobloxCacheReset.MoveAside();
                attempt = await StartAsync(version.ExePath, version.Directory, argumentList, status);
                if (attempt.Watch.Health != LaunchHealth.Crashed)
                {
                    exe = version.ExePath;
                    launchDir = version.Directory;
                    usedSlot = false;
                    safeFallback = true;
                    Log.Info(cacheBackup is null
                        ? "缓存重置后启动成功（没有找到可移动的缓存）"
                        : "缓存重置后启动成功，旧缓存保留在 " + cacheBackup);
                    if (cacheBackup is not null)
                        status?.Invoke("缓存重置后启动成功");
                }
            }

            if (attempt.Watch.Health == LaunchHealth.Crashed)
            {
                var disabledMods = instance.ApplyMods
                    ? ModCrashGuard.Quarantine(instance)
                    : (IReadOnlyList<string>)Array.Empty<string>();
                throw new LaunchException(BuildCrashMessage(attempt.Watch, disabledMods));
            }

            var process = attempt.Process;

            // Process tuning is exactly what a safe launch exists to rule out, so skip it there.
            if (!safeFallback)
                RobloxProcessOptimizer.Apply(process);

            if (instance.ApplyMods)
            {
                if (safeFallback)
                {
                    var disabled = ModCrashGuard.Quarantine(instance);
                    if (disabled.Count > 0)
                    {
                        Log.Warn("安全启动成功后已停用疑似模组：" + string.Join("、", disabled));
                        status?.Invoke("已停用疑似崩溃的模组：" + string.Join("、", disabled));
                    }
                }
                else
                {
                    ModCrashGuard.MarkHealthy(instance);
                }
            }

            var displayName = string.IsNullOrWhiteSpace(placeName) ? "" : placeName!;
            var session = SessionManager.Track(instance, process, displayName, launchDir, placeId);

            instance.LastPlayed = DateTime.Now;
            if (placeId is > 0)
            {
                instance.LastGame = new GameRef
                {
                    PlaceId = placeId.Value,
                    JobId = jobId,
                    Title = placeName
                };
            }
            InstanceStore.Save(instance);
            instance.NotifyAll();

            Log.Info($"Roblox 已启动，PID {process.Id}，参数：{(argumentList.Count == 0 ? "（无）" : string.Join(' ', argumentList))}");
            return new LaunchResult
            {
                Session = session,
                VersionDirectory = version.Directory,
                LaunchDirectory = launchDir,
                Executable = exe,
                MultiInstance = multiInstance,
                UsedSlot = usedSlot,
                SafeFallback = safeFallback,
                ModsApplied = modsApplied,
                FlagsApplied = flagsApplied,
                Arguments = string.Join(' ', argumentList)
            };
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<LaunchAttempt> StartAsync(
        string exe,
        string launchDir,
        List<string> argumentList,
        Action<string>? status)
    {
        // Snapshot first: a log written after this point belongs to the launch below.
        var existingLogs = StartupCrashGuard.SnapshotLogs();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = launchDir,
            UseShellExecute = false
        };
        foreach (var arg in argumentList)
            psi.ArgumentList.Add(arg);

        status?.Invoke("启动 Roblox…");
        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new LaunchException("Roblox 启动失败：\n" + ex.Message);
        }

        if (process is null)
            throw new LaunchException("Roblox 没有返回进程句柄，启动失败。");

        status?.Invoke("等待客户端就绪…");
        var watch = await StartupCrashGuard.WaitAsync(process, existingLogs, DateTime.UtcNow);
        return new LaunchAttempt(process, watch);
    }

    private static string BuildCrashMessage(StartupWatchResult watch, IReadOnlyList<string>? disabledMods = null)
    {
        var log = string.IsNullOrEmpty(watch.LogFile) ? "" : "\n日志：" + watch.LogFile;
        var mods = disabledMods is { Count: > 0 }
            ? "\n已停用疑似导致崩溃的模组：" + string.Join("、", disabledMods) + "（可在「模组」页重新启用）。"
            : "";
        return "Roblox 在启动阶段连续崩溃（退出码 " + FormatExitCode(watch.ExitCode) +
               "），还原修改、停用可疑模组、重置本地缓存都没能拉起。\n" +
               "这通常来自客户端本体、显卡驱动或注入游戏的其他程序，而不是 RCL 写入的设置。" +
               "可以试试：更新显卡驱动、关闭覆盖层与杀毒注入，或重装一次客户端。" + mods + log;
    }

    private static string FormatExitCode(int? code) =>
        code is { } value ? $"0x{(uint)value:X8}" : "未知";

    private sealed record LaunchAttempt(Process Process, StartupWatchResult Watch);

    public static List<string> BuildArguments(long? placeId, string? jobId, string? extra, string? deeplink = null)
    {
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(deeplink))
        {
            // Only a validated link reaches the client; the raw string is never forwarded.
            if (!RobloxDeeplink.TryExtract(deeplink, out var parsed) || parsed is null ||
                !RobloxDeeplink.IsPlayerLink(parsed))
                throw new LaunchException("启动链接无效，已拒绝。");
            args.Add(parsed.OriginalString);
        }
        else if (placeId is > 0)
        {
            args.Add(RobloxDeeplink.BuildPlayerLink(placeId.Value, jobId));
        }

        if (!string.IsNullOrWhiteSpace(extra))
        {
            if (extra.Any(char.IsControl))
                throw new LaunchException("启动参数包含不可见控制字符，已拒绝。");
            args.AddRange(SplitArguments(extra));
        }

        // Without a link the client only spins up its tray host and exits; -app is what brings
        // up the Roblox window.
        if (args.Count == 0)
            args.Add("-app");

        return args;
    }

    public static IEnumerable<string> SplitArguments(string commandLine)
    {
        var current = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }
}
