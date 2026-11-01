using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace RCL.Core;

public sealed class ProtocolHandlerInfo
{
    public string Protocol { get; init; } = "";
    public string Command { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Executable { get; init; } = "";

    public string LauncherName
    {
        get
        {
            var name = Path.GetFileNameWithoutExtension(Executable);
            if (string.IsNullOrWhiteSpace(name))
                return "未设置";
            return name;
        }
    }

    public bool IsRcl => Executable.EndsWith("RCL.exe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Opt-in takeover of the roblox:// and roblox-player: protocol handlers. The previous
/// handler is saved so the user can put another launcher (or Roblox itself) back.
/// </summary>
public static class LauncherIntegrationService
{
    public const string PlayerProtocol = "roblox-player";
    public const string AppProtocol = "roblox";
    private static readonly string[] Protocols = { PlayerProtocol, AppProtocol };
    private static readonly string BackupFile = Path.Combine(AppPaths.Base, "protocol-backup.json");

    public static bool HasBackup
    {
        get
        {
            try
            {
                return File.Exists(BackupFile);
            }
            catch
            {
                return false;
            }
        }
    }

    public static ProtocolHandlerInfo GetHandler(string protocol)
    {
        var command = "";
        var icon = "";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\{protocol}\shell\open\command");
            command = key?.GetValue(null) as string ?? "";
            using var iconKey = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{protocol}\DefaultIcon");
            icon = iconKey?.GetValue(null) as string ?? "";
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 {protocol} 协议处理程序失败：{ex.Message}");
        }

        return new ProtocolHandlerInfo
        {
            Protocol = protocol,
            Command = command,
            Icon = icon,
            Executable = ExtractExecutable(command)
        };
    }

    public static string DescribeCurrentLauncher()
    {
        var player = GetHandler(PlayerProtocol);
        if (player.IsRcl)
            return "RCL";
        if (!string.IsNullOrWhiteSpace(player.LauncherName) && player.LauncherName != "未设置")
            return player.LauncherName;
        var app = GetHandler(AppProtocol);
        return string.IsNullOrWhiteSpace(app.LauncherName) ? "未设置" : app.LauncherName;
    }

    public static void TakeOver()
    {
        var exe = RclExecutable();
        if (exe is null)
            throw new InvalidOperationException(
                "找不到 RCL.exe。请从发布版（dist\\RCL\\RCL.exe）运行后再接管协议。");

        var backup = new ProtocolBackup
        {
            CreatedAt = DateTime.Now,
            Handlers = Protocols.ToDictionary(p => p, p => GetHandler(p))
        };
        WriteBackup(backup);

        foreach (var protocol in Protocols)
        {
            using var root = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{protocol}");
            root.SetValue("URL Protocol", "");
            using var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{protocol}\DefaultIcon");
            iconKey.SetValue(null, $"\"{exe}\",0");
            using var commandKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{protocol}\shell\open\command");
            commandKey.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        Log.Info("已将 roblox:// 与 roblox-player: 协议接管到 RCL");
    }

    public static bool Restore()
    {
        if (!HasBackup)
            return false;
        try
        {
            var backup = JsonSerializer.Deserialize<ProtocolBackup>(
                File.ReadAllText(BackupFile), JsonOpts.Read);
            if (backup?.Handlers is null)
                return false;

            foreach (var protocol in Protocols)
            {
                if (!backup.Handlers.TryGetValue(protocol, out var handler))
                    continue;
                using var iconKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{protocol}\DefaultIcon");
                SetOrDelete(iconKey, handler.Icon);
                using var commandKey = Registry.CurrentUser.CreateSubKey(
                    $@"Software\Classes\{protocol}\shell\open\command");
                SetOrDelete(commandKey, handler.Command);
            }
            Log.Info("已恢复接管前的 Roblox 协议处理程序");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("恢复 Roblox 协议失败", ex);
            return false;
        }
    }

    private static void SetOrDelete(RegistryKey key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            key.DeleteValue(null!, false);
        else
            key.SetValue(null, value);
    }

    private static void WriteBackup(ProtocolBackup backup)
    {
        try
        {
            File.WriteAllText(BackupFile,
                JsonSerializer.Serialize(backup, JsonOpts.Write), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Error("保存协议处理程序备份失败", ex);
            throw;
        }
    }

    private static string? RclExecutable()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(path) ? path : null;
    }

    private static string ExtractExecutable(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : "";
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command[..(exe + 4)] : "";
    }

    public sealed class ProtocolBackup
    {
        public DateTime CreatedAt { get; set; }
        public Dictionary<string, ProtocolHandlerInfo> Handlers { get; set; } = new();
    }
}
