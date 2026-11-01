using System.IO;
using System.Text.Json;
using RCL.Models;

namespace RCL.Core;

/// <summary>
/// Remembers the mod set that last launched successfully. When Roblox keeps crashing before its
/// renderer appears, only the mods added since that good run are treated as suspects and turned
/// off, so nobody has to bisect a mod folder by hand.
/// </summary>
public static class ModCrashGuard
{
    private sealed class State
    {
        public List<string> LastGood { get; set; } = new();
        public DateTime? LastGoodAt { get; set; }
    }

    private static string StateFile(RclInstance instance) => Path.Combine(instance.Folder, "mod-guard.json");

    /// <summary>Enabled mods that did not take part in the last successful launch.</summary>
    public static IReadOnlyList<string> Suspects(RclInstance instance)
    {
        var enabled = ModsService.List(instance)
            .Where(m => m.Enabled)
            .Select(m => m.Name)
            .ToList();
        if (enabled.Count == 0)
            return Array.Empty<string>();

        var state = Load(instance);
        if (state.LastGood.Count == 0)
            return enabled;

        var good = new HashSet<string>(state.LastGood, StringComparer.OrdinalIgnoreCase);
        return enabled.Where(name => !good.Contains(name)).ToList();
    }

    /// <summary>Called after a launch reached the renderer with the current mod set.</summary>
    public static void MarkHealthy(RclInstance instance)
    {
        try
        {
            var enabled = ModsService.List(instance)
                .Where(m => m.Enabled)
                .Select(m => m.Name)
                .ToList();
            Save(instance, new State { LastGood = enabled, LastGoodAt = DateTime.Now });
        }
        catch (Exception ex)
        {
            Log.Warn("记录模组健康状态失败：" + ex.Message);
        }
    }

    /// <summary>Turns off the suspects and returns what was disabled.</summary>
    public static IReadOnlyList<string> Quarantine(RclInstance instance)
    {
        var disabled = new List<string>();
        foreach (var name in Suspects(instance))
        {
            if (ModsService.Disable(instance, name))
                disabled.Add(name);
        }

        if (disabled.Count > 0)
            Log.Warn("已停用疑似导致启动崩溃的模组：" + string.Join("、", disabled));
        return disabled;
    }

    private static State Load(RclInstance instance)
    {
        try
        {
            var path = StateFile(instance);
            if (!File.Exists(path))
                return new State();
            return JsonSerializer.Deserialize<State>(File.ReadAllText(path), JsonOpts.Read) ?? new State();
        }
        catch
        {
            return new State();
        }
    }

    private static void Save(RclInstance instance, State state)
    {
        try
        {
            Directory.CreateDirectory(instance.Folder);
            File.WriteAllText(StateFile(instance), JsonSerializer.Serialize(state, JsonOpts.Write));
        }
        catch (Exception ex)
        {
            Log.Warn("保存模组健康状态失败：" + ex.Message);
        }
    }
}
