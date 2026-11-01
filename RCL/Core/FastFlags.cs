using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RCL.Models;

namespace RCL.Core;

public sealed record FlagPreset(string Name, string Description, IReadOnlyList<(string Key, string Value)> Flags)
{
    public override string ToString() => Name;
}

public static class FastFlags
{
    public static readonly HashSet<string> KnownFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "DFIntTaskSchedulerTargetFps",
        "FFlagDebugDisableTelemetryEphemeralCounter",
        "FFlagDebugDisableTelemetryPersistentCounter",
        "FFlagDebugDisableTelemetrySampledCounter",
        "FFlagDebugDisableTelemetryEphemeralStat",
        "FFlagDebugDisableTelemetryPersistentStat",
        "FFlagDebugDisableTelemetrySampledStat",
        "FFlagDebugDisableTelemetryEvents",
        "DFIntDebugFRMQualityLevelOverride",
        "FIntRenderShadowIntensity",
        "DFFlagTextureQualityOverrideEnabled",
        "DFIntTextureQualityOverride",
        "DFIntMaxFrameBufferSize",
        "FFlagHandleAltEnterFullscreenManually",
        "FFlagDebugSkyGray",
        "FIntFRMMinGrassDistance",
        "FIntFRMMaxGrassDistance",
        "FLogNetwork"
    };

    public static readonly IReadOnlyList<FlagPreset> Presets = new List<FlagPreset>
    {
        new("解锁帧率", "把渲染上限抬高到 240 FPS（能否生效取决于显示器与客户端限制）。",
            new[] { ("DFIntTaskSchedulerTargetFps", "240") }),
        new("关闭遥测", "关闭 Roblox 客户端的多类统计上报，降低后台占用。",
            new[]
            {
                ("FFlagDebugDisableTelemetryEphemeralCounter", "true"),
                ("FFlagDebugDisableTelemetryPersistentCounter", "true"),
                ("FFlagDebugDisableTelemetrySampledCounter", "true"),
                ("FFlagDebugDisableTelemetryEphemeralStat", "true"),
                ("FFlagDebugDisableTelemetryPersistentStat", "true"),
                ("FFlagDebugDisableTelemetrySampledStat", "true")
            }),
        new("性能优先画质", "降低画质分级，适合核显与老机器。",
            new[] { ("DFIntDebugFRMQualityLevelOverride", "3") }),
        new("关闭阴影", "彻底关闭实时阴影，帧数提升明显。",
            new[] { ("FIntRenderShadowIntensity", "0") }),
        new("高质量阴影", "提高阴影强度，观感更好，代价是性能。",
            new[] { ("FIntRenderShadowIntensity", "75") }),
        new("低配材质", "强制使用低分辨率纹理，显存占用更低。",
            new[]
            {
                ("DFFlagTextureQualityOverrideEnabled", "true"),
                ("DFIntTextureQualityOverride", "1")
            }),
        new("限制帧缓冲", "限制最大帧缓冲尺寸，降低高分屏压力。",
            new[] { ("DFIntMaxFrameBufferSize", "4") })
    };

    public static string? ValidateValue(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
            return "值不能为空";
        try
        {
            using var _ = JsonDocument.Parse(value);
            return null;
        }
        catch (JsonException)
        {
            return "不是合法的 JSON 值（数字 / true / false / \"文本\"）";
        }
    }

    public static string BuildJson(RclInstance instance)
    {
        var obj = new JsonObject();
        foreach (var entry in instance.FastFlags.Where(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Name)))
        {
            try
            {
                obj[entry.Name.Trim()] = JsonNode.Parse(entry.Value.Trim());
            }
            catch (JsonException ex)
            {
                Log.Warn($"Flag {entry.Name} 的值无效，已跳过：{ex.Message}");
            }
        }
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static string SaveToInstance(RclInstance instance)
    {
        Directory.CreateDirectory(instance.ClientSettingsFolder);
        var json = BuildJson(instance);
        WriteAtomic(instance.FlagsFile, json);
        return json;
    }

    /// <summary>
    /// Writes this instance's flags into the Roblox version folder, keeping a one-time backup of
    /// whatever Roblox (or another bootstrapper) had there before.
    /// </summary>
    public static string? DeployToVersion(RclInstance instance, string versionDir)
    {
        var settingsDir = Path.Combine(versionDir, "ClientSettings");
        var target = Path.Combine(settingsDir, "ClientAppSettings.json");
        var enabled = instance.FastFlags.Count(f => f.Enabled && !string.IsNullOrWhiteSpace(f.Name));
        var backup = BackupPath(versionDir);
        var backupDir = Path.GetDirectoryName(backup)!;
        var marker = Path.Combine(backupDir, "rcl-written.marker");

        if (enabled == 0)
        {
            // The instance has no active flags: undo whatever RCL wrote last time instead of
            // leaving stale flags behind for this client.
            try
            {
                if (File.Exists(backup) && File.Exists(target))
                    File.Copy(backup, target, true);
                else if (File.Exists(marker) && File.Exists(target))
                    File.Delete(target);
                if (File.Exists(backup))
                    File.Delete(backup);
                if (File.Exists(marker))
                    File.Delete(marker);
            }
            catch (Exception ex)
            {
                Log.Warn("清理旧 FastFlags 失败：" + ex.Message);
            }
            return null;
        }

        Directory.CreateDirectory(settingsDir);
        Directory.CreateDirectory(backupDir);
        if (File.Exists(target) && !File.Exists(backup))
        {
            File.Copy(target, backup, true);
            Log.Info("已备份 Roblox 原有 ClientAppSettings.json：" + backup);
        }

        var json = BuildJson(instance);
        WriteAtomic(target, json);
        File.WriteAllText(marker, DateTime.Now.ToString("O"));
        return target;
    }

    private static string BackupPath(string versionDir) =>
        Path.Combine(AppPaths.Backups, Path.GetFileName(versionDir.TrimEnd(Path.DirectorySeparatorChar)),
            "ClientAppSettings.json");

    public static int ImportFromFile(RclInstance instance, string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到 FastFlags 文件", path);

        var node = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new InvalidDataException("文件内容不是 JSON 对象");
        int count = 0;
        foreach (var pair in node)
        {
            var existing = instance.FastFlags.FirstOrDefault(f =>
                string.Equals(f.Name, pair.Key, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new FastFlagEntry { Name = pair.Key };
                instance.FastFlags.Add(existing);
            }
            existing.Value = pair.Value?.ToJsonString() ?? "null";
            existing.Enabled = true;
            count++;
        }
        Log.Info($"从 {path} 导入 {count} 条 FastFlags");
        return count;
    }

    public static IReadOnlyList<string> DiscoverImports()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] bootstrappers = { "Bloxstrap", "Voidstrap", "Froststrap", "Fishstrap" };
        var found = new List<string>();
        foreach (var name in bootstrappers)
        {
            var path = Path.Combine(local, name, "Mods", "ClientSettings", "ClientAppSettings.json");
            if (File.Exists(path))
                found.Add(path);
        }
        return found;
    }

    public static void ExportToFile(RclInstance instance, string path)
    {
        File.WriteAllText(path, BuildJson(instance), new UTF8Encoding(false));
    }

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
}
