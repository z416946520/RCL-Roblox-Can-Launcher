using System.IO;

namespace RCL.Core;

public sealed class ClientCleanupResult
{
    public int FlagFilesRestored { get; set; }
    public int ModFilesRestored { get; set; }
    public int SlotsRemoved { get; set; }
    public List<string> Warnings { get; } = new();

    public bool ChangedAnything =>
        FlagFilesRestored > 0 || ModFilesRestored > 0 || SlotsRemoved > 0;

    public string Summary
    {
        get
        {
            if (!ChangedAnything)
                return "未发现 RCL 写入的客户端修改。";
            return $"已还原 {FlagFilesRestored} 个版本的客户端配置，恢复 {ModFilesRestored} 个模组文件，清理 {SlotsRemoved} 个多开槽位。";
        }
    }
}

/// <summary>
/// Reverts the client changes RCL is responsible for: FastFlags it wrote, mod files it overlaid,
/// and idle multi-instance junctions. It never touches files owned by another bootstrapper.
/// </summary>
public static class ClientCleanupService
{
    public static ClientCleanupResult RestoreAll()
    {
        var result = new ClientCleanupResult();
        if (RobloxInstall.RunningCount() > 0)
        {
            result.Warnings.Add("检测到 Roblox 正在运行。请先关闭所有客户端，再还原客户端修改。");
            return result;
        }

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var instance in InstanceStore.All)
        {
            var version = RobloxInstall.Resolve(instance.VersionPath);
            if (version is null)
                continue;

            versions.Add(version.Directory);
            result.ModFilesRestored += ModsService.Restore(instance, version.Directory);
        }

        // Catch versions that still carry an RCL marker even if the instance was removed.
        foreach (var version in RobloxInstall.ScanPlayerVersions())
            versions.Add(version.Directory);

        foreach (var versionDir in versions)
        {
            if (FastFlags.RestoreFromVersion(versionDir))
                result.FlagFilesRestored++;
        }

        result.SlotsRemoved = MultiInstanceLock.CleanupUnused();

        foreach (var versionDir in versions)
        {
            var leftover = Path.Combine(versionDir, "ClientSettings", "ClientAppSettings.json");
            if (File.Exists(leftover))
            {
                result.Warnings.Add(
                    $"{Path.GetFileName(versionDir)} 仍存在 ClientAppSettings.json。它可能来自其他启动器，" +
                    "RCL 不会擅自删除；请用官方安装器或「下载 → 校验并修复」恢复。");
            }
        }

        Log.Info("客户端安全清理：" + result.Summary);
        return result;
    }
}
