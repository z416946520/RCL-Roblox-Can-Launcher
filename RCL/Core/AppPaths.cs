using System.IO;

namespace RCL.Core;

public static class AppPaths
{
    public static string Base { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RCL");

    public static string Instances { get; } = Path.Combine(Base, "instances");
    public static string Logs { get; } = Path.Combine(Base, "logs");
    public static string Temp { get; } = Path.Combine(Base, "temp");
    public static string MultiInstance { get; } = Path.Combine(Base, "multi-instance");
    public static string Backups { get; } = Path.Combine(Base, "backups");
    public static string WebViewData { get; } = Path.Combine(Base, "webview");
    public static string Avatars { get; } = Path.Combine(Base, "avatars");
    public static string SettingsFile { get; } = Path.Combine(Base, "settings.json");
    public static string AccountsFile { get; } = Path.Combine(Base, "accounts.json");
    public static string QuickLaunchFile { get; } = Path.Combine(Base, "quicklaunch.json");
    public static string LogFile { get; } = Path.Combine(Logs, "rcl.log");

    public static string RobloxCookieFile { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Roblox", "LocalStorage", "RobloxCookies.dat");

    public static string RobloxRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox");

    public static string RobloxVersions { get; } = Path.Combine(RobloxRoot, "Versions");
    public static string RobloxLogs { get; } = Path.Combine(RobloxRoot, "logs");

    public static void Ensure()
    {
        Directory.CreateDirectory(Base);
        Directory.CreateDirectory(Instances);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Temp);
        Directory.CreateDirectory(MultiInstance);
        Directory.CreateDirectory(Backups);
        Directory.CreateDirectory(WebViewData);
        Directory.CreateDirectory(Avatars);
    }
}
