using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace RCL.Core;

public sealed class RobloxVersion
{
    public string Directory { get; init; } = "";
    public string FolderName { get; init; } = "";
    public string ExePath { get; init; } = "";
    public string Version { get; init; } = "";
    public DateTime Modified { get; init; }
    public string Source { get; init; } = "";
    public bool IsLatest { get; set; }
    public bool IsRunning { get; set; }

    public string ShortTag => FolderName.StartsWith("version-", StringComparison.OrdinalIgnoreCase)
        ? FolderName[8..]
        : FolderName;

    public string DisplayName => string.IsNullOrWhiteSpace(Version) ? ShortTag : Version;

    public string Detail => $"{FolderName} · {Modified:yyyy-MM-dd}" +
                            (string.IsNullOrWhiteSpace(Source) ? "" : $" · {Source}");
}

public static class RobloxInstall
{
    public static List<RobloxVersion> ScanPlayerVersions()
    {
        var list = new List<RobloxVersion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var running = RunningDirectories();

        try
        {
            foreach (var dir in EnumerateVersionDirectories())
            {
                var exe = Path.Combine(dir, "RobloxPlayerBeta.exe");
                if (!File.Exists(exe) || !seen.Add(Path.GetFullPath(dir)))
                    continue;

                string version = "";
                try
                {
                    version = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "";
                }
                catch
                {
                    // Some builds ship without usable version resources.
                }

                list.Add(new RobloxVersion
                {
                    Directory = dir,
                    FolderName = Path.GetFileName(dir),
                    ExePath = exe,
                    Version = version,
                    Modified = Directory.GetLastWriteTime(dir),
                    Source = SourceOf(dir),
                    IsRunning = running.Contains(dir.TrimEnd(Path.DirectorySeparatorChar))
                });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("扫描 Roblox 版本失败：" + ex.Message);
        }

        return list.OrderByDescending(v => v.Modified).ToList();
    }

    private static IEnumerable<string> EnumerateVersionDirectories()
    {
        foreach (var root in VersionRoots())
        {
            if (!Directory.Exists(root))
                continue;
            if (File.Exists(Path.Combine(root, "RobloxPlayerBeta.exe")))
            {
                yield return root;
                continue;
            }
            foreach (var dir in Directory.GetDirectories(root, "version-*"))
                yield return dir;
        }
    }

    /// <summary>
    /// Standard installs live under %LOCALAPPDATA%\Roblox\Versions, but third party
    /// bootstrappers (Fishstrap, Bloxstrap portable, ...) often keep a custom root. The
    /// roblox:// protocol handler points at that bootstrapper, so its folder is the strongest
    /// available hint; a running client also reveals its own version directory.
    /// </summary>
    private static IEnumerable<string> VersionRoots()
    {
        var roots = new List<string> { AppPaths.RobloxVersions };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            }
            catch
            {
                return;
            }
            if (seen.Add(path))
                roots.Add(path);
        }

        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var name in new[] { "Bloxstrap", "Voidstrap", "Froststrap", "Fishstrap" })
            {
                Add(Path.Combine(local, name, "Versions"));
                Add(Path.Combine(roaming, name, "Versions"));
            }
        }
        catch
        {
        }

        foreach (var protocol in new[] { @"Software\Classes\roblox-player\shell\open\command", @"Software\Classes\roblox\shell\open\command" })
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(protocol);
                if (key?.GetValue(null) is not string command)
                    continue;
                var exe = ExtractExecutable(command);
                if (exe is null)
                    continue;
                var dir = Path.GetDirectoryName(exe);
                if (dir is null)
                    continue;
                Add(Path.Combine(dir, "Versions"));
                Add(dir);
            }
            catch
            {
            }
        }

        try
        {
            foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path))
                        {
                            var dir = Path.GetDirectoryName(path);
                            Add(dir);
                            if (dir is not null)
                                Add(Path.Combine(Path.GetDirectoryName(dir) ?? dir, "Versions"));
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch
        {
        }

        return roots;
    }

    private static string? ExtractExecutable(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex > 0 ? command[..(exeIndex + 4)] : null;
    }

    private static string SourceOf(string versionDirectory)
    {
        try
        {
            var parent = Directory.GetParent(versionDirectory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (parent is null)
                return "";
            return parent.Name.Equals("Versions", StringComparison.OrdinalIgnoreCase)
                ? parent.Parent?.Name ?? ""
                : parent.Name;
        }
        catch
        {
            return "";
        }
    }

    public static RobloxVersion? Latest() => ScanPlayerVersions().FirstOrDefault();

    public static RobloxVersion? Resolve(string? versionPath)
    {
        if (string.IsNullOrWhiteSpace(versionPath))
            return Latest();

        var exe = Path.Combine(versionPath, "RobloxPlayerBeta.exe");
        if (!File.Exists(exe))
            return null;

        string version = "";
        try
        {
            version = FileVersionInfo.GetVersionInfo(exe).ProductVersion ?? "";
        }
        catch
        {
        }

        return new RobloxVersion
        {
            Directory = versionPath,
            FolderName = Path.GetFileName(versionPath.TrimEnd(Path.DirectorySeparatorChar)),
            ExePath = exe,
            Version = version,
            Modified = Directory.GetLastWriteTime(versionPath),
            Source = SourceOf(versionPath)
        };
    }

    public static bool IsInstalled => Latest() is not null;

    public static int RunningCount()
    {
        try
        {
            return Process.GetProcessesByName("RobloxPlayerBeta").Length;
        }
        catch
        {
            return 0;
        }
    }

    public static IReadOnlySet<string> RunningDirectories()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var process in Process.GetProcessesByName("RobloxPlayerBeta"))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path))
                            set.Add(Path.GetDirectoryName(path)!);
                    }
                    catch
                    {
                        // Access denied for processes we did not start as this user.
                    }
                }
            }
        }
        catch
        {
        }
        return set;
    }
}
