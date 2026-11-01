using System.Diagnostics;
using System.IO;
using System.Threading;

namespace RCL.Core;

/// <summary>
/// Holds the Roblox singleton mutex/events for the lifetime of the launcher and gives every
/// concurrent client its own working directory through directory junctions, which is the same
/// approach Voidstrap used before removing its multi-instance feature.
/// </summary>
public static class MultiInstanceLock
{
    private const string RobloxMutexName = "ROBLOX_singletonMutex";
    private const string RobloxEventName = "ROBLOX_singletonEvent";

    private static readonly object Gate = new();
    private static readonly List<IDisposable> Handles = new();
    private static bool _held;

    public static bool IsHeld
    {
        get { lock (Gate) return _held; }
    }

    public static bool Acquire()
    {
        lock (Gate)
        {
            if (_held)
                return true;

            try
            {
                var mutex = new Mutex(initiallyOwned: true, RobloxMutexName, out _);
                Handles.Add(mutex);
                try { mutex.ReleaseMutex(); }
                catch (ApplicationException) { /* another process owns it, the handle still counts */ }
                _held = true;
                Log.Info("已持有 Roblox 单例互斥体（多开已就绪）");
            }
            catch (Exception ex)
            {
                Log.Warn("无法创建 Roblox 单例互斥体：" + ex.Message);
            }

            foreach (var name in new[]
                     {
                         RobloxEventName,
                         @"Global\" + RobloxEventName,
                         @"Global\" + RobloxMutexName
                     })
            {
                try
                {
                    var handle = new EventWaitHandle(false, EventResetMode.ManualReset, name, out _);
                    Handles.Add(handle);
                    Log.Info("已持有单例事件 " + name);
                }
                catch (Exception ex)
                {
                    Log.Warn($"无法持有单例事件 {name}：{ex.Message}");
                }
            }

            return _held;
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            foreach (var handle in Handles)
            {
                try { handle.Dispose(); }
                catch { }
            }
            Handles.Clear();
            _held = false;
        }
    }

    /// <summary>
    /// Picks a free slot and creates (or reuses) a junction from the slot to the Roblox version
    /// folder. Returns null when no slot can be prepared; the caller then launches in place.
    /// </summary>
    public static string? PrepareSlot(string versionDirectory, MachineBusyPaths busy, int maxSlots)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.MultiInstance);
            versionDirectory = Path.GetFullPath(versionDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            for (int slot = 1; slot <= Math.Clamp(maxSlots, 1, 16); slot++)
            {
                var candidate = Path.Combine(AppPaths.MultiInstance, "slot" + slot);
                if (busy.Paths.Contains(candidate.TrimEnd(Path.DirectorySeparatorChar)))
                    continue;

                if (!EnsureJunction(candidate, versionDirectory))
                    continue;

                Log.Info($"多开槽位 {slot} 已就绪：{candidate}");
                return candidate;
            }

            Log.Warn("所有多开槽位都已被占用，本次将从原目录启动");
        }
        catch (Exception ex)
        {
            Log.Error("准备多开槽位失败", ex);
        }
        return null;
    }

    public static void CleanupUnused()
    {
        try
        {
            if (!Directory.Exists(AppPaths.MultiInstance))
                return;
            var busy = MachineBusyPaths.Scan();
            foreach (var dir in Directory.GetDirectories(AppPaths.MultiInstance, "slot*"))
            {
                if (busy.Paths.Contains(dir.TrimEnd(Path.DirectorySeparatorChar)))
                    continue;
                try
                {
                    Directory.Delete(dir);
                    Log.Info("已清理多开槽位 " + dir);
                }
                catch (Exception ex)
                {
                    Log.Warn($"清理槽位 {dir} 失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("清理多开槽位失败：" + ex.Message);
        }
    }

    private static bool EnsureJunction(string link, string target)
    {
        var info = new DirectoryInfo(link);
        if (info.Exists)
        {
            var current = info.LinkTarget;
            if (!string.IsNullOrEmpty(current))
            {
                if (string.Equals(
                        Path.GetFullPath(current).TrimEnd(Path.DirectorySeparatorChar),
                        target, StringComparison.OrdinalIgnoreCase))
                    return true;
                try { Directory.Delete(link); }
                catch (Exception ex)
                {
                    Log.Warn($"旧槽位 {link} 无法删除：{ex.Message}");
                    return false;
                }
            }
            else
            {
                // A real folder in the slot position is not ours to destroy.
                Log.Warn("槽位 " + link + " 不是目录联接，已跳过");
                return false;
            }
        }

        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var process = Process.Start(psi);
            process?.WaitForExit(5000);
            return Directory.Exists(link) &&
                   !string.IsNullOrEmpty(new DirectoryInfo(link).LinkTarget);
        }
        catch (Exception ex)
        {
            Log.Warn("创建目录联接失败：" + ex.Message);
            return false;
        }
    }
}

public sealed class MachineBusyPaths
{
    public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static MachineBusyPaths Scan()
    {
        var result = new MachineBusyPaths();
        try
        {
            foreach (var name in new[] { "RobloxPlayerBeta", "RobloxStudioBeta" })
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    using (process)
                    {
                        try
                        {
                            var path = process.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(path))
                                result.Paths.Add(Path.GetDirectoryName(path)!
                                    .TrimEnd(Path.DirectorySeparatorChar));
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }
        catch
        {
        }
        return result;
    }
}
