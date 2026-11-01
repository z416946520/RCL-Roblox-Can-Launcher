using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace RCL.Core;

/// <summary>
/// Collects what a bug report needs (logs, settings, instance metadata, environment) into one zip.
/// Account tokens and the Roblox cookie store are deliberately left out.
/// </summary>
public static class SupportBundle
{
    public static string Create(string? destination = null)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        destination ??= Path.Combine(AppPaths.Base, "diagnostics", $"RCL-diagnostics-{stamp}.zip");
        var staging = Path.Combine(AppPaths.Temp, "diagnostics-" + stamp);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.CreateDirectory(staging);
            CopyLogs(staging);
            WriteEnvironment(staging);
            CopyInstanceMetadata(staging);
            CopySettings(staging);

            TryDelete(destination);
            ZipFile.CreateFromDirectory(staging, destination, CompressionLevel.Optimal, false);
        }
        finally
        {
            try { Directory.Delete(staging, true); } catch { }
        }

        Log.Info("已导出诊断包：" + destination);
        return destination;
    }

    private static void CopyLogs(string staging)
    {
        var target = Path.Combine(staging, "logs");
        Directory.CreateDirectory(target);
        try
        {
            if (!Directory.Exists(AppPaths.Logs))
                return;

            var files = Directory.GetFiles(AppPaths.Logs, "rcl*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(5);
            foreach (var file in files)
            {
                try { File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true); }
                catch { }
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(target, "copy-error.txt"), ex.ToString());
        }
    }

    private static void CopySettings(string staging)
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                File.Copy(AppPaths.SettingsFile, Path.Combine(staging, "settings.json"), true);
        }
        catch { }
    }

    private static void CopyInstanceMetadata(string staging)
    {
        var target = Path.Combine(staging, "instances");
        Directory.CreateDirectory(target);
        foreach (var instance in InstanceStore.All)
        {
            try
            {
                if (File.Exists(instance.MetaFile))
                    File.Copy(instance.MetaFile, Path.Combine(target, instance.Id.ToString("N") + ".json"), true);
            }
            catch { }
        }
    }

    private static void WriteEnvironment(string staging)
    {
        var text = new StringBuilder();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        text.AppendLine("RCL " + version);
        text.AppendLine("OS      : " + Environment.OSVersion.VersionString + " (" + Environment.OSVersion.Platform + ")");
        text.AppendLine("64-bit  : " + Environment.Is64BitOperatingSystem + " / process " + Environment.Is64BitProcess);
        text.AppendLine("CPU     : " + Environment.ProcessorCount + " logical cores");
        text.AppendLine(".NET    : " + Environment.Version);
        text.AppendLine("Culture : " + System.Globalization.CultureInfo.CurrentCulture.Name);
        text.AppendLine();
        text.AppendLine("Base     : " + AppPaths.Base);
        text.AppendLine("Roblox   : " + AppPaths.RobloxRoot);
        text.AppendLine("Logs     : " + AppPaths.Logs);
        text.AppendLine();
        text.AppendLine("Update mode: " + AppSettings.Current.RobloxUpdateMode);
        text.AppendLine("Multi-instance default: " + AppSettings.Current.DefaultMultiInstance);
        text.AppendLine();
        text.AppendLine("Installed Roblox versions:");

        foreach (var entry in RobloxInstall.ScanPlayerVersions())
        {
            text.AppendLine($"  {entry.FolderName}  version={entry.Version}  source={entry.Source}  " +
                            $"modified={entry.Modified:yyyy-MM-dd HH:mm}");
        }

        text.AppendLine();
        text.AppendLine("Instances:");
        foreach (var instance in InstanceStore.All)
        {
            text.AppendLine($"  {instance.Name}  id={instance.Id:N}  bound={instance.VersionPath}  " +
                            $"multi={instance.MultiInstance}  mods={instance.ApplyMods}  " +
                            $"flags={instance.EnabledFlagCount}/{instance.FastFlags.Count}  " +
                            $"executor={instance.FollowExecutor}");
        }

        File.WriteAllText(Path.Combine(staging, "environment.txt"), text.ToString(), new UTF8Encoding(false));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
