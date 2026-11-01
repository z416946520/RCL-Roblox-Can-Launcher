using System.IO;
using System.Text.Json;

namespace RCL.Core;

/// <summary>
/// Keeps the installed client current before a launch, the way Voidstrap's bootstrapper does.
/// The delivery mode decides when a freshly published version is installed:
/// Auto installs right away, Delay waits a day (the version it first saw is remembered),
/// Manual never touches the installation.
/// </summary>
public static class RobloxUpdateService
{
    private const string AutoMode = "Auto";
    private const string DelayMode = "Delay";
    private const string ManualMode = "Manual";

    private static readonly TimeSpan DelayWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(10);

    private static readonly object Gate = new();
    private static DateTime _lastCheckUtc = DateTime.MinValue;

    private sealed class UpdateState
    {
        public string VersionGuid { get; set; } = "";
        public DateTime FirstSeenUtc { get; set; }
    }

    private static string StateFile => Path.Combine(AppPaths.Base, "cache", "update-state.json");

    public static string Mode
    {
        get
        {
            var mode = AppSettings.Current.RobloxUpdateMode;
            return mode is AutoMode or DelayMode or ManualMode ? mode : AutoMode;
        }
    }

    /// <summary>Returns true when a new client version was installed.</summary>
    public static async Task<bool> EnsureLatestAsync(
        Action<string>? status = null,
        CancellationToken cancellationToken = default)
    {
        if (Mode == ManualMode)
            return false;

        var installed = RobloxInstall.Latest();
        if (RobloxInstall.RunningCount() > 0)
        {
            Log.Info("Roblox 正在运行，跳过本次客户端更新检查");
            return false;
        }

        RobloxDeploymentInfo deployment;
        try
        {
            deployment = await RobloxApi.GetDeploymentAsync("default", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("检查 Roblox 更新失败，继续使用已安装版本：" + ex.Message);
            return false;
        }

        if (installed is not null && IsUpToDate(installed, deployment))
        {
            ClearState(deployment.ClientVersionUpload);
            return false;
        }

        var target = deployment.ClientVersionUpload;
        if (installed is not null && string.Equals(installed.FolderName, target, StringComparison.OrdinalIgnoreCase))
            return false;

        if (Mode == DelayMode && !DelayElapsed(target))
        {
            Log.Info($"发现新版本 {target}，按「延迟更新」策略等满 {DelayWindow.TotalHours:0} 小时后再安装");
            status?.Invoke($"发现新版本，按设置稍后更新");
            return false;
        }

        status?.Invoke($"发现新版本 {deployment.Version}，正在更新…");
        Log.Info($"开始自动更新 Roblox：{installed?.FolderName ?? "未安装"} → {target}");

        var lastPercent = -1;
        var progress = new Progress<InstallProgress>(p =>
        {
            var percent = (int)p.Percent;
            if (percent == lastPercent)
                return;
            lastPercent = percent;
            status?.Invoke($"更新 Roblox {percent}% · {p.Message}");
        });

        try
        {
            await RobloxDeployment.InstallAsync(deployment, progress, cancellationToken).ConfigureAwait(false);
            ClearState(target);
            _lastCheckUtc = DateTime.UtcNow;
            Log.Info($"Roblox 已更新到 {deployment.Version}（{target}）");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("自动更新失败，继续使用已安装版本：" + ex.Message);
            status?.Invoke("自动更新失败，继续使用已安装版本");
            return false;
        }
    }

    private static bool IsUpToDate(RobloxVersion installed, RobloxDeploymentInfo deployment)
    {
        if (Version.TryParse(installed.Version, out var have) &&
            Version.TryParse(deployment.Version, out var latest))
            return have >= latest;
        return string.Equals(installed.FolderName, deployment.ClientVersionUpload,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool DelayElapsed(string versionGuid)
    {
        var state = LoadState();
        if (state is { VersionGuid: var seen } && seen == versionGuid &&
            state.FirstSeenUtc != default)
            return DateTime.UtcNow - state.FirstSeenUtc >= DelayWindow;

        SaveState(new UpdateState { VersionGuid = versionGuid, FirstSeenUtc = DateTime.UtcNow });
        return false;
    }

    private static UpdateState? LoadState()
    {
        try
        {
            return File.Exists(StateFile)
                ? JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(StateFile), JsonOpts.Read)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void SaveState(UpdateState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(state, JsonOpts.Write));
        }
        catch (Exception ex)
        {
            Log.Warn("写入更新状态失败：" + ex.Message);
        }
    }

    private static void ClearState(string currentVersionGuid)
    {
        var state = LoadState();
        if (state is null || state.VersionGuid == currentVersionGuid)
        {
            try { File.Delete(StateFile); } catch { }
        }
    }

    /// <summary>
    /// Cheap guard for callers that run the check often; a real check happens at most once per
    /// <see cref="CheckInterval"/> unless the version changed.
    /// </summary>
    public static bool ShouldCheck()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _lastCheckUtc < CheckInterval)
                return false;
            _lastCheckUtc = DateTime.UtcNow;
            return true;
        }
    }
}
