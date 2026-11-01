using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using RCL.Models;

namespace RCL.Core;

public sealed class ModEntry
{
    public string Name { get; init; } = "";
    public bool IsDirectory { get; init; }
    public bool Enabled { get; init; }
    public int FileCount { get; init; }
    public long Bytes { get; init; }

    public string Kind => IsDirectory ? "文件夹" : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant() + " 文件";
    public string SizeDisplay => FormatSize(Bytes);
    public string CountDisplay => IsDirectory ? $"{FileCount} 个文件" : SizeDisplay;

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}

public static class ModsService
{
    private const string DisabledSuffix = ".rcl-disabled";

    public static void Ensure(RclInstance inst)
    {
        Directory.CreateDirectory(inst.ModsFolder);
    }

    public static List<ModEntry> List(RclInstance inst)
    {
        Ensure(inst);
        var result = new List<ModEntry>();
        foreach (var path in Directory.GetFileSystemEntries(inst.ModsFolder))
        {
            var raw = Path.GetFileName(path);
            if (raw.EndsWith(".rcl-disabled", StringComparison.OrdinalIgnoreCase))
            {
                var display = raw[..^DisabledSuffix.Length];
                result.Add(Describe(path, display, false));
                continue;
            }
            if (raw.StartsWith(".rcl-", StringComparison.OrdinalIgnoreCase))
                continue;
            result.Add(Describe(path, raw, true));
        }
        return result.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static ModEntry Describe(string path, string name, bool enabled)
    {
        bool isDir = Directory.Exists(path);
        int count = 0;
        long bytes = 0;
        try
        {
            if (isDir)
            {
                var info = new DirectoryInfo(path);
                var files = info.GetFiles("*", SearchOption.AllDirectories);
                count = files.Length;
                bytes = files.Sum(f => f.Length);
            }
            else
            {
                var info = new FileInfo(path);
                count = 1;
                bytes = info.Length;
            }
        }
        catch
        {
        }
        return new ModEntry
        {
            Name = name,
            IsDirectory = isDir,
            Enabled = enabled,
            FileCount = count,
            Bytes = bytes
        };
    }

    public static string Import(RclInstance inst, string sourcePath)
    {
        Ensure(inst);
        if (Directory.Exists(sourcePath))
        {
            var target = UniquePath(Path.Combine(inst.ModsFolder, Path.GetFileName(sourcePath)));
            CopyDirectory(sourcePath, target);
            Log.Info($"导入模组文件夹：{sourcePath} -> {target}");
            return Path.GetFileName(target);
        }

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到要导入的文件", sourcePath);

        if (Path.GetExtension(sourcePath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(sourcePath, inst.ModsFolder, true);
            Log.Info("导入模组压缩包：" + sourcePath);
            return Path.GetFileName(sourcePath);
        }

        var fileName = UniquePath(Path.Combine(inst.ModsFolder, Path.GetFileName(sourcePath)));
        File.Copy(sourcePath, fileName, false);
        Log.Info($"导入模组文件：{sourcePath} -> {fileName}");
        return Path.GetFileName(fileName);
    }

    public static void Toggle(RclInstance inst, ModEntry entry)
    {
        var enabledPath = Path.Combine(inst.ModsFolder, entry.Name);
        var disabledPath = enabledPath + DisabledSuffix;
        if (entry.Enabled)
        {
            if (Directory.Exists(enabledPath))
                Directory.Move(enabledPath, disabledPath);
            else if (File.Exists(enabledPath))
                File.Move(enabledPath, disabledPath, true);
        }
        else
        {
            if (Directory.Exists(disabledPath))
                Directory.Move(disabledPath, enabledPath);
            else if (File.Exists(disabledPath))
                File.Move(disabledPath, enabledPath, true);
        }
        Log.Info($"{(entry.Enabled ? "禁用" : "启用")}模组：{entry.Name}");
    }

    public static void Delete(RclInstance inst, ModEntry entry)
    {
        var path = Path.Combine(inst.ModsFolder, entry.Name);
        var disabled = path + DisabledSuffix;
        if (Directory.Exists(path))
            Directory.Delete(path, true);
        else if (File.Exists(path))
            File.Delete(path);
        if (Directory.Exists(disabled))
            Directory.Delete(disabled, true);
        else if (File.Exists(disabled))
            File.Delete(disabled);
        Log.Info("删除模组：" + entry.Name);
    }

    /// <summary>
    /// Overlays enabled mod entries onto the Roblox version folder. Every file that would be
    /// replaced is backed up first, so the "还原" button can put the installation back.
    /// </summary>
    public static int Apply(RclInstance inst, string versionDir)
    {
        Ensure(inst);
        var manifest = LoadManifest(inst);
        int applied = 0;

        foreach (var raw in Directory.GetFileSystemEntries(inst.ModsFolder))
        {
            var name = Path.GetFileName(raw);
            if (name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith(".rcl-", StringComparison.OrdinalIgnoreCase))
                continue;

            if (Directory.Exists(raw))
                applied += ApplyDirectory(inst, versionDir, raw, Path.GetFileName(raw), manifest);
            else
                applied += ApplyFile(inst, versionDir, raw, name, manifest);
        }

        SaveManifest(inst, manifest);
        Log.Info($"已应用 {applied} 个模组文件到 {versionDir}");
        return applied;
    }

    public static int Restore(RclInstance inst, string versionDir)
    {
        var manifest = LoadManifest(inst);
        int restored = 0;

        foreach (var relative in manifest.BackedUp)
        {
            var backup = Path.Combine(inst.BackupFolder, relative);
            var target = Path.Combine(versionDir, relative);
            if (!File.Exists(backup))
                continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(backup, target, true);
                restored++;
            }
            catch (Exception ex)
            {
                Log.Warn($"还原 {relative} 失败：{ex.Message}");
            }
        }

        foreach (var relative in manifest.Created)
        {
            var target = Path.Combine(versionDir, relative);
            try
            {
                if (File.Exists(target))
                {
                    File.Delete(target);
                    restored++;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"删除模组文件 {relative} 失败：{ex.Message}");
            }
        }

        manifest.BackedUp.Clear();
        manifest.Created.Clear();
        SaveManifest(inst, manifest);
        Log.Info($"已还原 {restored} 个文件");
        return restored;
    }

    private static int ApplyDirectory(RclInstance inst, string versionDir, string sourceDir,
        string relativeDir, ModManifest manifest)
    {
        int count = 0;
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var rel = Path.Combine(relativeDir, Path.GetFileName(dir));
            count += ApplyDirectory(inst, versionDir, dir, rel, manifest);
        }
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var rel = Path.Combine(relativeDir, Path.GetFileName(file));
            count += ApplyFile(inst, versionDir, file, rel, manifest);
        }
        return count;
    }

    private static int ApplyFile(RclInstance inst, string versionDir, string source, string relative,
        ModManifest manifest)
    {
        try
        {
            var target = Path.Combine(versionDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                if (!manifest.BackedUp.Contains(relative))
                {
                    var backup = Path.Combine(inst.BackupFolder, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, true);
                    manifest.BackedUp.Add(relative);
                }
            }
            else if (!manifest.Created.Contains(relative))
            {
                manifest.Created.Add(relative);
            }
            File.Copy(source, target, true);
            return 1;
        }
        catch (Exception ex)
        {
            Log.Warn($"复制模组文件 {relative} 失败：{ex.Message}");
            return 0;
        }
    }

    private static ModManifest LoadManifest(RclInstance inst)
    {
        var path = Path.Combine(inst.Folder, "mods-applied.json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(path), JsonOpts.Read) ?? new ModManifest();
        }
        catch
        {
        }
        return new ModManifest();
    }

    private static void SaveManifest(RclInstance inst, ModManifest manifest)
    {
        try
        {
            var path = Path.Combine(inst.Folder, "mods-applied.json");
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOpts.Write), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log.Warn("保存模组清单失败：" + ex.Message);
        }
    }

    private static string UniquePath(string desired)
    {
        if (!File.Exists(desired) && !Directory.Exists(desired))
            return desired;
        var dir = Path.GetDirectoryName(desired)!;
        var name = Path.GetFileNameWithoutExtension(desired);
        var ext = Path.GetExtension(desired);
        int n = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{name} ({n++}){ext}");
        } while (File.Exists(candidate) || Directory.Exists(candidate));
        return candidate;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private sealed class ModManifest
    {
        public List<string> BackedUp { get; set; } = new();
        public List<string> Created { get; set; } = new();
    }
}
