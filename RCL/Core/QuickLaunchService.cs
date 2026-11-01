using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using RCL.Models;

namespace RCL.Core;

public static class QuickLaunchService
{
    private static readonly Dictionary<Guid, int> TrackedPids = new();

    public static ObservableCollection<QuickLaunchEntry> Entries { get; } = new();
    public static event Action? Changed;

    public static void Load()
    {
        Entries.Clear();
        try
        {
            if (File.Exists(AppPaths.QuickLaunchFile))
            {
                var list = JsonSerializer.Deserialize<List<QuickLaunchEntry>>(
                    File.ReadAllText(AppPaths.QuickLaunchFile), JsonOpts.Read);
                if (list is not null)
                {
                    foreach (var entry in list)
                        Entries.Add(entry);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取快捷启动配置失败", ex);
        }
        RefreshRunningStates();
        Changed?.Invoke();
    }

    public static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Entries.ToList(), JsonOpts.Write);
            var temp = AppPaths.QuickLaunchFile + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, AppPaths.QuickLaunchFile, true);
        }
        catch (Exception ex)
        {
            Log.Error("保存快捷启动配置失败", ex);
        }
    }

    public static QuickLaunchEntry CreateFromPath(string path, string? displayName = null)
    {
        var kind = InferKind(path);
        var entry = new QuickLaunchEntry
        {
            Name = UniqueName(displayName ?? Path.GetFileNameWithoutExtension(path)),
            TargetPath = path,
            Kind = kind,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            ProcessName = DefaultProcessName(kind, path)
        };
        Entries.Add(entry);
        Save();
        Changed?.Invoke();
        Log.Info("添加快捷启动：" + entry.Name + " -> " + path);
        return entry;
    }

    public static QuickLaunchEntry CreateEmpty()
    {
        var entry = new QuickLaunchEntry
        {
            Name = UniqueName("新快捷启动"),
            Kind = QuickLaunchKind.Other
        };
        Entries.Add(entry);
        Save();
        Changed?.Invoke();
        return entry;
    }

    public static void Remove(QuickLaunchEntry entry)
    {
        Entries.Remove(entry);
        TrackedPids.Remove(entry.Id);
        Save();
        Changed?.Invoke();
        Log.Info("删除快捷启动：" + entry.Name);
    }

    public static void ApplyPath(QuickLaunchEntry entry, string path, bool keepName = false)
    {
        entry.TargetPath = path;
        entry.Kind = InferKind(path);
        if (!keepName || string.IsNullOrWhiteSpace(entry.Name))
            entry.Name = UniqueName(Path.GetFileNameWithoutExtension(path), entry);
        if (string.IsNullOrWhiteSpace(entry.WorkingDirectory))
            entry.WorkingDirectory = Path.GetDirectoryName(path) ?? "";
        if (string.IsNullOrWhiteSpace(entry.ProcessName))
            entry.ProcessName = DefaultProcessName(entry.Kind, path);
        Save();
    }

    public static void Launch(QuickLaunchEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.TargetPath) || !File.Exists(entry.TargetPath))
            throw new FileNotFoundException("目标文件不存在，请检查路径。", entry.TargetPath);

        var working = string.IsNullOrWhiteSpace(entry.WorkingDirectory)
            ? Path.GetDirectoryName(entry.TargetPath) ?? ""
            : entry.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(working) && !Directory.Exists(working))
            working = Path.GetDirectoryName(entry.TargetPath) ?? "";

        ProcessStartInfo info;
        switch (entry.Kind)
        {
            case QuickLaunchKind.Cmd:
            case QuickLaunchKind.Bat:
                info = new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    Arguments = BuildScriptArguments(entry.TargetPath, entry.Arguments, true),
                    WorkingDirectory = working,
                    UseShellExecute = entry.RunAsAdmin
                };
                break;
            case QuickLaunchKind.Ps1:
                info = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{entry.TargetPath}\" {entry.Arguments}",
                    WorkingDirectory = working,
                    UseShellExecute = entry.RunAsAdmin
                };
                break;
            default:
                info = new ProcessStartInfo
                {
                    FileName = entry.TargetPath,
                    Arguments = entry.Arguments,
                    WorkingDirectory = working,
                    UseShellExecute = true
                };
                break;
        }

        if (entry.RunAsAdmin)
        {
            info.UseShellExecute = true;
            info.Verb = "runas";
        }

        Process? process = null;
        try
        {
            process = Process.Start(info);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("已取消管理员授权，应用没有启动。", ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("启动失败：" + ex.Message, ex);
        }

        if (process is not null)
        {
            try
            {
                if (!process.HasExited)
                    TrackedPids[entry.Id] = process.Id;
            }
            catch
            {
            }
        }

        entry.LastUsed = DateTime.Now;
        Save();
        RefreshRunningStates();
        Log.Info($"快捷启动：{entry.Name}（{entry.TypeLabel}）" +
                 (process is null ? "" : $"，PID {process.Id}"));
    }

    public static void Restart(QuickLaunchEntry entry)
    {
        Kill(entry);
        Thread.Sleep(220);
        if (IsRunning(entry))
            throw new InvalidOperationException(
                "旧进程无法结束（可能受保护或需要管理员权限）。请手动关闭后再启动。");
        Launch(entry);
    }

    public static int Kill(QuickLaunchEntry entry)
    {
        int count = 0;
        foreach (var process in FindRunning(entry))
        {
            try
            {
                process.Kill(true);
                count++;
            }
            catch (Exception ex)
            {
                Log.Warn($"结束进程 {process.Id} 失败：{ex.Message}");
            }
            finally
            {
                process.Dispose();
            }
        }
        TrackedPids.Remove(entry.Id);
        RefreshRunningStates();
        if (count > 0)
            Log.Info($"已结束 {entry.Name} 的 {count} 个进程");
        return count;
    }

    public static bool IsRunning(QuickLaunchEntry entry)
    {
        var processes = FindRunning(entry);
        var running = processes.Count > 0;
        foreach (var process in processes)
            process.Dispose();
        return running;
    }

    public static List<Process> FindRunning(QuickLaunchEntry entry)
    {
        var result = new List<Process>();
        var seen = new HashSet<int>();

        void Add(Process process)
        {
            if (seen.Add(process.Id))
                result.Add(process);
            else
                process.Dispose();
        }

        if (TrackedPids.TryGetValue(entry.Id, out var pid))
        {
            try
            {
                var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                    Add(process);
            }
            catch
            {
                TrackedPids.Remove(entry.Id);
            }
        }

        if (!string.IsNullOrWhiteSpace(entry.ProcessName))
        {
            var name = entry.ProcessName.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];
            foreach (var process in Process.GetProcessesByName(name))
                Add(process);
        }

        var target = entry.TargetPath;
        if (entry.Kind == QuickLaunchKind.Link)
            target = ResolveLink(target) ?? target;

        if (entry.Kind is QuickLaunchKind.Exe or QuickLaunchKind.Other or QuickLaunchKind.Link &&
            !string.IsNullOrWhiteSpace(target))
        {
            var name = Path.GetFileNameWithoutExtension(target);
            if (!string.IsNullOrWhiteSpace(name))
            {
                foreach (var process in Process.GetProcessesByName(name))
                {
                    bool samePath = false;
                    bool canRead = false;
                    try
                    {
                        var module = process.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(module))
                        {
                            canRead = true;
                            samePath = string.Equals(Path.GetFullPath(module),
                                Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    catch
                    {
                    }

                    if (samePath || !canRead)
                        Add(process);
                    else
                        process.Dispose();
                }
            }
        }

        return result;
    }

    public static void RefreshRunningStates()
    {
        foreach (var entry in Entries)
        {
            try
            {
                entry.IsRunning = IsRunning(entry);
            }
            catch
            {
                entry.IsRunning = false;
            }
        }
    }

    public static string? ResolveLink(string linkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            string target = link.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    public static string? TryLocatePotassium()
    {
        foreach (var process in Process.GetProcessesByName("Potassium"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        return path;
                }
                catch
                {
                }
            }
        }

        var fromRegistry = FindPotassiumInUninstall();
        if (fromRegistry is not null)
            return fromRegistry;

        foreach (var startRoot in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         @"Microsoft\Windows\Start Menu\Programs"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                         @"Microsoft\Windows\Start Menu\Programs")
                 })
        {
            try
            {
                if (!Directory.Exists(startRoot))
                    continue;
                foreach (var link in Directory.GetFiles(startRoot, "*.lnk", SearchOption.AllDirectories))
                {
                    if (!Path.GetFileName(link).Contains("potassium", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var target = ResolveLink(link);
                    if (!string.IsNullOrWhiteSpace(target) && File.Exists(target) &&
                        Path.GetFileName(target).Contains("potassium", StringComparison.OrdinalIgnoreCase))
                        return target;
                }
            }
            catch
            {
            }
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Potassium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Potassium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Potassium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Potassium"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        foreach (var dir in candidates)
        {
            try
            {
                if (!Directory.Exists(dir))
                    continue;
                foreach (var file in Directory.GetFiles(dir, "Potassium*.exe", SearchOption.AllDirectories))
                    return file;
            }
            catch
            {
            }
        }

        return null;
    }

    private static string? FindPotassiumInUninstall()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null)
                        continue;
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        using var sub = uninstall.OpenSubKey(name);
                        var display = sub?.GetValue("DisplayName") as string ?? "";
                        if (!display.Contains("potassium", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var location = sub?.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(location))
                        {
                            var exe = Path.Combine(location, "Potassium.exe");
                            if (File.Exists(exe))
                                return exe;
                        }
                        var icon = sub?.GetValue("DisplayIcon") as string;
                        if (!string.IsNullOrWhiteSpace(icon))
                        {
                            var exe = icon.Trim().Trim('"');
                            var comma = exe.LastIndexOf(',');
                            if (comma > 0)
                                exe = exe[..comma];
                            if (File.Exists(exe) &&
                                Path.GetFileName(exe).Contains("potassium", StringComparison.OrdinalIgnoreCase))
                                return exe;
                        }
                    }
                }
                catch
                {
                }
            }
        }
        return null;
    }

    private static string BuildScriptArguments(string path, string arguments, bool cmd) =>
        cmd
            ? $"/c \"\"{path}\"\" {arguments}"
            : arguments;

    private static QuickLaunchKind InferKind(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".lnk" => QuickLaunchKind.Link,
            ".cmd" => QuickLaunchKind.Cmd,
            ".bat" => QuickLaunchKind.Bat,
            ".ps1" => QuickLaunchKind.Ps1,
            ".exe" => QuickLaunchKind.Exe,
            _ => QuickLaunchKind.Other
        };
    }

    private static string DefaultProcessName(QuickLaunchKind kind, string path)
    {
        try
        {
            if (kind == QuickLaunchKind.Exe)
                return Path.GetFileNameWithoutExtension(path);
            if (kind == QuickLaunchKind.Link)
            {
                var target = ResolveLink(path);
                if (!string.IsNullOrWhiteSpace(target) &&
                    Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileNameWithoutExtension(target);
            }
        }
        catch
        {
        }
        return "";
    }

    private static string UniqueName(string desired, QuickLaunchEntry? except = null)
    {
        desired = string.IsNullOrWhiteSpace(desired) ? "快捷启动" : desired.Trim();
        var name = desired;
        int n = 2;
        while (Entries.Any(e => !ReferenceEquals(e, except) &&
                                string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{desired} ({n++})";
        return name;
    }
}
