using System.IO;

namespace RCL.Core;

/// <summary>
/// Voidstrap's cache reset: when the client still crashes with every launcher change reverted,
/// the next suspect is Roblox's own local asset cache. It is moved aside (a rename, so it is
/// instant and reversible) while login data and graphics settings stay untouched.
/// </summary>
public static class RobloxCacheReset
{
    private const string CachePattern = "rbx-storage*";

    /// <summary>
    /// Moves the local asset cache out of the Roblox folder. Returns the backup folder, or null
    /// when there was nothing to move. Pass a root to exercise this against a temp directory.
    /// </summary>
    public static string? MoveAside(string? robloxRoot = null, string? backupRoot = null)
    {
        var root = robloxRoot ?? AppPaths.RobloxRoot;
        if (!Directory.Exists(root))
            return null;

        backupRoot ??= Path.Combine(AppPaths.Base, "cache-reset",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        var moved = new List<string>();
        try
        {
            Directory.CreateDirectory(backupRoot);
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, CachePattern))
            {
                var name = Path.GetFileName(entry);
                try
                {
                    var target = Path.Combine(backupRoot, name);
                    if (Directory.Exists(entry))
                        Directory.Move(entry, target);
                    else
                        File.Move(entry, target, true);
                    moved.Add(name);
                }
                catch (Exception ex)
                {
                    Log.Warn($"移动缓存 {name} 失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("重置 Roblox 缓存失败：" + ex.Message);
            return null;
        }

        if (moved.Count == 0)
        {
            try { Directory.Delete(backupRoot); } catch { }
            return null;
        }

        Log.Info($"已把 Roblox 本地缓存移到 {backupRoot}（{string.Join("、", moved)}）");
        return backupRoot;
    }
}
